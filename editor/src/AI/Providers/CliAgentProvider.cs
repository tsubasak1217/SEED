// ============================================================
//  CliAgentProvider.cs — 外部 CLI ツール（Gemini CLI / Claude Code）呼び出しプロバイダー
//
//  【Gemini CLI】プリウォーム（事前起動）方式
//    コンストラクタ生成時点でバックグラウンドに gemini プロセスを起動し、
//    stdin の書き込み待ち状態にしておく。
//    メッセージ送信時はすでに初期化済みのプロセスへ stdin を書き込んで EOF を通知
//    するだけなので、Node.js / モジュールロードのオーバーヘッドが 2 回目以降ゼロになる。
//    レスポンス受信後は次のプロセスをバックグラウンドでウォームアップする。
//
//  【Claude Code】-p フラグによる一発起動方式
//    -p は scripting 用の print モードとして設計されているため現状維持とする。
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.AI.Models;

namespace SEEDEditor.AI.Providers;

/// <summary>
/// 外部の CLI AI ツール（Gemini CLI, Claude Code 等）を呼び出すプロバイダー。
/// Gemini CLI はプリウォームプロセス方式で起動コストを 1 回に抑える。
/// </summary>
public class CliAgentProvider : IAIProvider, IDisposable
{
    private readonly string _command;
    private readonly string _guideUrl;
    /// <summary>使用するモデル名。空の場合は CLI ツールのデフォルトモデルを使用する。</summary>
    private readonly string _model;

    // ── プリウォームプロセス管理（Gemini CLI 用） ─────────────────────────
    /// <summary>stdin 書き込み待ちの事前起動済みプロセス。null の場合は未準備。</summary>
    private Process? _warmProcess;
    /// <summary>プリウォームプロセスへの stdin ライター。</summary>
    private StreamWriter? _warmStdin;
    /// <summary>プリウォームプロセスへのアクセスを排他制御するロック。</summary>
    private readonly SemaphoreSlim _warmLock = new(1, 1);
    /// <summary>WarmUpAsync が現在実行中かどうか（二重起動防止フラグ）。</summary>
    private volatile bool _isWarming;
    /// <summary>このプロバイダーが破棄済みかどうか。</summary>
    private volatile bool _disposed;
    /// <summary>チャット同時実行防止ロック（プリウォームプロセスの多重使用を防ぐ）。</summary>
    private readonly SemaphoreSlim _chatLock = new(1, 1);

    /// <summary>レスポンス全体のタイムアウト時間。</summary>
    private static readonly TimeSpan CHAT_TIMEOUT = TimeSpan.FromMinutes(5);

    public string Name { get; }
    public bool IsConfigured => true;

    /// <summary>
    /// コンストラクタ。
    /// Gemini CLI の場合はバックグラウンドでプリウォームを即座に開始する。
    /// </summary>
    public CliAgentProvider(string name, string command, string guideUrl, string model = "")
    {
        Name      = name;
        _command  = command;
        _guideUrl = guideUrl;
        _model    = model;

        // Gemini CLI のみプリウォームを行う（Claude Code は -p 一発起動のため不要）
        // IsCommandAvailable は内部で where/which コマンドをブロック実行するため
        // コンストラクタ（UI スレッド）では呼ばない。WarmUpAsync 内でパス解決する。
        if (IsGemini)
            _ = WarmUpAsync();
    }

    /// <summary>Gemini CLI コマンドかどうかを返す。</summary>
    private bool IsGemini => _command.Contains("gemini");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // プリウォームプロセスへの参照をローカルに退避してフィールドを即座に null にする。
        // Kill(entireProcessTree: true) は Node.js プロセスツリーの列挙に数十秒かかることがあるため
        // UI スレッドをブロックしないようバックグラウンドスレッドで実行する。
        var processToKill = _warmProcess;
        var stdinToClose  = _warmStdin;
        _warmProcess = null;
        _warmStdin   = null;

        if (processToKill != null)
        {
            _ = Task.Run(async () =>
            {
                // stdin を閉じて EOF を送り、gemini CLI のクリーン終了を促す。
                // (-p - モードは EOF を受け取ると自然終了する)
                try { stdinToClose?.Dispose(); } catch { }

                // 500ms 待って自然終了していれば Kill 不要（全プロセス列挙のオーバーヘッドを回避）。
                // Kill(entireProcessTree:true) は全プロセス列挙を伴い数百ms かかることがあるため
                // なるべくスキップする。
                await Task.Delay(500).ConfigureAwait(false);

                try
                {
                    if (!processToKill.HasExited)
                        processToKill.Kill(entireProcessTree: true);
                }
                catch (Exception ex) { EditorLog.Write($"[CliAgent] Kill 例外: {ex.Message}"); }
                try { processToKill.Dispose(); } catch { }
            });
        }
        try { _warmLock.Dispose(); } catch { }
        try { _chatLock.Dispose(); } catch { }
    }

    // ── プリウォーム管理 ──────────────────────────────────────────────────

    /// <summary>
    /// バックグラウンドで新しいプリウォームプロセスを起動する。
    /// 起動直後に stdin への書き込み待ち状態になるため、次のメッセージ送信が即座に開始できる。
    /// </summary>
    private async Task WarmUpAsync()
    {
        // 既にウォームアップ中・破棄済みなら何もしない
        if (_isWarming || _disposed) return;
        _isWarming = true;
        try
        {
            // 設定ポップアップを閉じた直後の描画が落ち着いてから Node.js を起動するため、
            // 1500ms 待機する。Node.js 起動時のディスク I/O がエディタの
            // レンダリングや他の I/O 操作と競合するのを緩和する。
            await Task.Delay(1500).ConfigureAwait(false);
            if (_disposed) return;

            // ResolveCommandPath は where/which コマンドをブロック実行するため
            // スレッドプールで実行し UI スレッドをブロックしない。
            // ConfigureAwait(false) で以降の continuation もスレッドプールで実行する
            // （WPF Dispatcher に戻さないことで _warmLock.Wait() とのデッドロックを防ぐ）。
            var path = await Task.Run(() => ResolveCommandPath(_command)).ConfigureAwait(false);
            if (_disposed) return;

            var psi     = BuildGeminiStartInfo(path);
            var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                process.Dispose();
                EditorLog.Write("[CliAgent] プリウォーム: プロセス起動失敗。");
                return;
            }

            // Node.js 起動中の CPU 負荷がエディタに影響しないよう優先度を下げる。
            // 実際にメッセージを送信する際（SendToGeminiAsync）はプロセスを引き継ぐだけなので
            // 優先度が低くてもレスポンス速度への影響は小さい。
            try { process.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }

            // 強制終了時に子プロセスも終了させるためジョブオブジェクトに登録する
            ChildProcessGuard.Track(process);

            // stdin ライターを生成する（BOM なし UTF-8）
            var writer = new StreamWriter(
                process.StandardInput.BaseStream,
                new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true);

            if (_disposed)
            {
                // Dispose がすでに呼ばれていた場合はプロセスを即終了させる
                try { process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
                return;
            }

            try { await _warmLock.WaitAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { return; }

            try
            {
                if (_disposed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    process.Dispose();
                    return;
                }
                // 古いプリウォームプロセスが残っていれば破棄する
                KillWarmProcessUnsafe();
                _warmProcess = process;
                _warmStdin   = writer;
            }
            finally
            {
                try { _warmLock.Release(); } catch (ObjectDisposedException) { }
            }
        }
        catch (Exception ex)
        {
            EditorLog.Write($"[CliAgent] プリウォームエラー: {ex.Message}");
        }
        finally
        {
            _isWarming = false;
        }
    }

    /// <summary>
    /// プリウォームプロセスを強制終了・破棄する。
    /// <c>_warmLock</c> を取得済みの状態で呼ぶこと。
    /// </summary>
    private void KillWarmProcessUnsafe()
    {
        try { _warmProcess?.Kill(entireProcessTree: true); } catch { }
        _warmProcess?.Dispose();
        _warmProcess = null;
        _warmStdin?.Dispose();
        _warmStdin = null;
    }

    /// <summary>プリウォームプロセスを安全に終了・破棄する。</summary>
    private void KillWarmProcess()
    {
        _warmLock.Wait();
        try { KillWarmProcessUnsafe(); }
        finally { _warmLock.Release(); }
    }

    // ── ChatAsync ─────────────────────────────────────────────────────────

    public async Task<AIResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition> tools)
    {
        // IsCommandAvailable は where/which をブロック実行するためスレッドプールで確認する
        bool available = await Task.Run(() => IsCommandAvailable(_command));
        if (!available)
            throw new Exception($"{Name} (コマンド: '{_command}') が見つかりません。\n導入案内: {_guideUrl}");

        var lastUserMsg = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        if (string.IsNullOrWhiteSpace(lastUserMsg))
            return new AIResponse { TextContent = "メッセージが空です。" };

        var fullPrompt = BuildPrompt(messages, tools);

        // Claude Code は -p 一発起動モード
        if (!IsGemini)
            return await RunOneShotClaudeAsync(fullPrompt);

        // Gemini CLI はプリウォームプロセスを使用（同時実行はロックで防ぐ）
        await _chatLock.WaitAsync();
        try
        {
            return await SendToGeminiAsync(fullPrompt);
        }
        finally
        {
            _chatLock.Release();
        }
    }

    // ── Gemini CLI プリウォーム送信 ───────────────────────────────────────

    /// <summary>
    /// プリウォームプロセスを取得してプロンプトを送信する。
    /// 取得直後に次のプリウォームをバックグラウンドで開始する。
    /// プリウォームが間に合っていない場合はコールドスタートにフォールバックする。
    /// </summary>
    private async Task<AIResponse> SendToGeminiAsync(string fullPrompt)
    {
        // プリウォームプロセスを引き取る
        Process process;
        StreamWriter stdin;

        await _warmLock.WaitAsync();
        try
        {
            if (_warmProcess == null || _warmProcess.HasExited)
            {
                // プリウォームが間に合わなかった場合はコールドスタートする
                EditorLog.Write("[CliAgent] プリウォームなし → コールドスタート。");
                var path = await Task.Run(() => ResolveCommandPath(_command));
                process  = new Process { StartInfo = BuildGeminiStartInfo(path) };
                if (!process.Start())
                    return new AIResponse { TextContent = "[エラー] Gemini CLI プロセスの起動に失敗しました。" };
                ChildProcessGuard.Track(process);
                stdin = new StreamWriter(
                    process.StandardInput.BaseStream,
                    new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true);
            }
            else
            {
                // プリウォーム済みプロセスを取得してフィールドから切り離す
                process      = _warmProcess;
                stdin        = _warmStdin!;
                _warmProcess = null;
                _warmStdin   = null;
            }
        }
        finally
        {
            _warmLock.Release();
        }

        // 取得したプロセスを使っている間に、次のプリウォームをバックグラウンドで開始する
        _ = WarmUpAsync();

        // プロンプトを送信してレスポンスを受信する
        return await RunGeminiRequestAsync(process, stdin, fullPrompt);
    }

    /// <summary>
    /// 指定プロセスへプロンプトを stdin で送り、stdout を受信して AIResponse を返す。
    /// </summary>
    private async Task<AIResponse> RunGeminiRequestAsync(Process process, StreamWriter stdin, string fullPrompt)
    {
        // stdin は using に入れない。
        // process.StandardInput.Close() でベースストリームが閉じられた後に
        // using(stdin) の Dispose → Flush が "Cannot access a closed file" を投げるため。
        using (process)
        {
            var outSb = new StringBuilder();
            var errSb = new StringBuilder();
            using var cts = new CancellationTokenSource(CHAT_TIMEOUT);

            // stdout / stderr の非同期読み取りを開始する
            var outTask = Task.Run(async () =>
            {
                try
                {
                    while (!process.StandardOutput.EndOfStream)
                    {
                        var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                        if (line != null)
                        {
                            outSb.AppendLine(line);
                            if (outSb.Length < 5000)
                                EditorLog.Write($"[CliAgent STDOUT] {line}");
                        }
                    }
                }
                catch { }
            });

            var errTask = Task.Run(async () =>
            {
                try
                {
                    while (!process.StandardError.EndOfStream)
                    {
                        var line = await process.StandardError.ReadLineAsync(cts.Token);
                        if (line != null)
                        {
                            errSb.AppendLine(line);
                            EditorLog.Write($"[CliAgent STDERR] {line}");
                        }
                    }
                }
                catch { }
            });

            // stdin にプロンプトを書き込んで EOF を通知する
            try
            {
                await stdin.WriteAsync(fullPrompt);
                await stdin.FlushAsync();
                // StandardInput を閉じて EOF を送信する（stdin を閉じると Gemini CLI がプロンプトを処理し始める）
                process.StandardInput.Close();
            }
            catch (Exception ex)
            {
                EditorLog.Write($"[CliAgent] stdin 書き込みエラー: {ex.Message}");
            }

            // プロセス終了を待機する
            try
            {
                await process.WaitForExitAsync(cts.Token);
                await Task.WhenAll(outTask, errTask);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new Exception($"{Name} の実行がタイムアウトしました (5分)。");
            }

            var output = StripAnsiCodes(outSb.ToString());
            var error  = StripAnsiCodes(errSb.ToString());

            EditorLog.Write($"[CliAgent] Gemini 完了: exit={process.ExitCode} outputLen={output.Length}");

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                return new AIResponse { TextContent = $"[エラー] {Name} が終了コード {process.ExitCode} で終了しました。\n{error}" };

            return ParseResponse(output);
        }
    }

    // ── Claude Code 一発起動 ──────────────────────────────────────────────

    /// <summary>Claude Code を -p フラグで一発起動してレスポンスを取得する。</summary>
    private async Task<AIResponse> RunOneShotClaudeAsync(string fullPrompt)
    {
        var fullCommandPath = await Task.Run(() => ResolveCommandPath(_command));
        var workDir = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\"));

        var isCmdFile = fullCommandPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            FileName               = isCmdFile ? "cmd.exe" : fullCommandPath,
            Arguments              = isCmdFile
                ? $"/c \"\"{fullCommandPath}\" -p --dangerously-skip-permissions --tools \"\"\""
                : "-p --dangerously-skip-permissions --tools \"\"",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            StandardOutputEncoding = Encoding.UTF8,
            // stderr はフック（PowerShell 等）がシステム ANSI エンコードで出力するため
            // Encoding.Default（日本語環境では Shift-JIS）を使用して文字化けを防ぐ
            StandardErrorEncoding  = Encoding.Default,
            WorkingDirectory       = workDir,
        };
        psi.EnvironmentVariables["CI"]                          = "true";
        psi.EnvironmentVariables["NON_INTERACTIVE"]             = "true";
        psi.EnvironmentVariables["CLAUDE_CODE_NON_INTERACTIVE"] = "true";

        EditorLog.Write($"[CliAgent] Claude Code 起動: {psi.FileName} {psi.Arguments}");

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start()) throw new Exception("プロセスの起動に失敗しました。");
            ChildProcessGuard.Track(process);

            var outSb = new StringBuilder();
            var errSb = new StringBuilder();
            using var cts = new CancellationTokenSource(CHAT_TIMEOUT);

            var outTask = Task.Run(async () =>
            {
                try
                {
                    while (!process.StandardOutput.EndOfStream)
                    {
                        var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                        if (line != null)
                        {
                            outSb.AppendLine(line);
                            if (outSb.Length < 5000)
                                EditorLog.Write($"[CliAgent STDOUT] {line}");
                        }
                    }
                }
                catch { }
            });

            var errTask = Task.Run(async () =>
            {
                try
                {
                    while (!process.StandardError.EndOfStream)
                    {
                        var line = await process.StandardError.ReadLineAsync(cts.Token);
                        if (line != null)
                        {
                            errSb.AppendLine(line);
                            EditorLog.Write($"[CliAgent STDERR] {line}");
                        }
                    }
                }
                catch { }
            });

            // stdin にプロンプトを書き込んで閉じる
            try
            {
                using var writer = new StreamWriter(
                    process.StandardInput.BaseStream,
                    new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true);
                await writer.WriteAsync(fullPrompt);
                await writer.FlushAsync();
                process.StandardInput.Close();
            }
            catch (Exception ex) { EditorLog.Write($"[CliAgent] stdin エラー: {ex.Message}"); }

            try
            {
                await process.WaitForExitAsync(cts.Token);
                await Task.WhenAll(outTask, errTask);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new Exception($"{Name} の実行がタイムアウトしました (5分)。");
            }

            var output = StripAnsiCodes(outSb.ToString());
            var error  = StripAnsiCodes(errSb.ToString());

            EditorLog.Write($"[CliAgent] Claude Code 完了: exit={process.ExitCode} outputLen={output.Length}");

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                return new AIResponse { TextContent = $"[エラー] {Name} が終了コード {process.ExitCode} で終了しました。\n{error}" };

            return ParseResponse(output);
        }
        catch (Exception ex)
        {
            throw new Exception($"{Name} の実行中に例外が発生しました: {ex.Message}", ex);
        }
    }

    // ── 共通ユーティリティ ────────────────────────────────────────────────

    /// <summary>
    /// メッセージ履歴とツール定義から CLI 向けのプロンプト文字列を組み立てる。
    ///
    /// 【Gemini CLI 向け設計指針】
    /// Gemini CLI の強みは「実行→観察→再調整」の自律ループにある。
    /// このループを活かすため、シェルコマンド（run_shell_command）経由で
    /// SeedAIBridge HTTP API（http://localhost:7234/seed-ai/）を呼ばせる。
    ///
    /// 旧方式（JSON ブロック出力→C# パース）の問題:
    ///  ・エージェントループが機能せず 1 ターンで終わる
    ///  ・「実行結果を観察して次の行動を決める」ができない
    ///  ・ファイルツールを禁止するだけでは Gemini の能力を活かせない
    ///
    /// 新方式（HTTP API 呼び出し）の利点:
    ///  ・Invoke-RestMethod の出力を Gemini 自身が観察して次の操作を決める
    ///  ・シーン情報取得 → アクター追加 → 結果確認 の流れを自律実行できる
    ///  ・ファイルツールは settings.json で除外済み → ソース改変は不可能
    /// </summary>
    private string BuildPrompt(List<ChatMessage> messages, List<ToolDefinition> tools)
    {
        var sb = new StringBuilder();

        // ── Gemini CLI 向け: MCP ツール説明 ─────────────────────────────
        // シェルコマンドはすべて無効化済み。Gemini が使えるのは MCP ツールのみ。
        // seed_query（読み取り）と seed_batch（一括変更）の 2 つだけを説明する。
        sb.AppendLine("=== SEED EDITOR MCP MODE ===");
        sb.AppendLine("You are a SEED game engine SCENE EDITOR.");
        sb.AppendLine("You have exactly 2 MCP tools. Use them EFFICIENTLY — minimize total tool calls.");
        sb.AppendLine();
        sb.AppendLine("## Tool 1: seed_query");
        sb.AppendLine("Read scene state or asset file list.");
        sb.AppendLine("  seed_query(type:\"scene\")              → current actors, dfs_id, components, slot_idx");
        sb.AppendLine("  seed_query(type:\"assets\",dir:\"models\")  → model file absolute paths");
        sb.AppendLine("  seed_query(type:\"assets\",dir:\"scripts\") → script file absolute paths");
        sb.AppendLine();
        sb.AppendLine("## Tool 2: seed_batch");
        sb.AppendLine("Execute scene modifications. ALL operations go into ONE single call as an array.");
        sb.AppendLine("Operations execute in order. DFS IDs are assigned sequentially as actors are added (0, 1, 2...).");
        sb.AppendLine("You can predict DFS IDs in advance: first add_actor gets id=N, next gets id=N+1, etc.");
        sb.AppendLine();
        sb.AppendLine("  Commands:");
        sb.AppendLine("    {\"cmd\":\"add_actor\",     \"name\":\"Name\", \"x\":0, \"y\":0, \"z\":0}");
        sb.AppendLine("    {\"cmd\":\"add_component\", \"actor_dfs_id\":N, \"component_type\":\"Model\"}");
        sb.AppendLine("    {\"cmd\":\"set_value\",     \"actor_dfs_id\":N, \"slot_idx\":0, \"key\":\"model_path\", \"value\":\"C:/path/to.glb\"}");
        sb.AppendLine("    {\"cmd\":\"move_actor\",    \"actor_dfs_id\":N, \"x\":0, \"y\":0, \"z\":0}");
        sb.AppendLine("    {\"cmd\":\"remove_actor\",  \"actor_dfs_id\":N}");
        sb.AppendLine();
        sb.AppendLine("## EXAMPLE — correct way to add 3 actors with Model (all in ONE seed_batch call):");
        sb.AppendLine("  // Suppose current scene is empty (next dfs_id = 0)");
        sb.AppendLine("  // and model path = \"C:/assets/models/Cube.glb\" from seed_query");
        sb.AppendLine("  seed_batch(operations: [");
        sb.AppendLine("    {\"cmd\":\"add_actor\",     \"name\":\"A\", \"x\":0, \"y\":0, \"z\":0},   // → dfs_id 0");
        sb.AppendLine("    {\"cmd\":\"add_component\", \"actor_dfs_id\":0, \"component_type\":\"Model\"},");
        sb.AppendLine("    {\"cmd\":\"set_value\",     \"actor_dfs_id\":0, \"slot_idx\":0, \"key\":\"model_path\", \"value\":\"C:/assets/models/Cube.glb\"},");
        sb.AppendLine("    {\"cmd\":\"add_actor\",     \"name\":\"B\", \"x\":2, \"y\":0, \"z\":0},   // → dfs_id 1");
        sb.AppendLine("    {\"cmd\":\"add_component\", \"actor_dfs_id\":1, \"component_type\":\"Model\"},");
        sb.AppendLine("    {\"cmd\":\"set_value\",     \"actor_dfs_id\":1, \"slot_idx\":0, \"key\":\"model_path\", \"value\":\"C:/assets/models/Cube.glb\"},");
        sb.AppendLine("    {\"cmd\":\"add_actor\",     \"name\":\"C\", \"x\":4, \"y\":0, \"z\":0},   // → dfs_id 2");
        sb.AppendLine("    {\"cmd\":\"add_component\", \"actor_dfs_id\":2, \"component_type\":\"Model\"},");
        sb.AppendLine("    {\"cmd\":\"set_value\",     \"actor_dfs_id\":2, \"slot_idx\":0, \"key\":\"model_path\", \"value\":\"C:/assets/models/Cube.glb\"}");
        sb.AppendLine("  ])");
        sb.AppendLine("  // For 25 actors, put ALL 75 operations (add_actor+add_component+set_value × 25) into ONE call.");
        sb.AppendLine();
        sb.AppendLine("## !! SYSTEM-ENFORCED HARD LIMIT: EXACTLY 4 TURNS TOTAL !!");
        sb.AppendLine("The agentic loop is terminated after 4 turns by the system. This is NOT optional.");
        sb.AppendLine();
        sb.AppendLine("  Turn 1: seed_query(type:\"scene\")              — read current state");
        sb.AppendLine("  Turn 2: seed_query(type:\"assets\", dir:\"...\")  — read asset paths (skip if not needed)");
        sb.AppendLine("  Turn 3: seed_batch(operations:[...ALL...])    — execute ALL operations in ONE call");
        sb.AppendLine("  Turn 4: Japanese summary. SESSION ENDS.");
        sb.AppendLine();
        sb.AppendLine("!! CRITICAL: If you call seed_batch more than once (e.g., once per actor),");
        sb.AppendLine("   the session terminates after turn 4 with the task INCOMPLETE.");
        sb.AppendLine("   Adding 25 actors one-by-one = 25 seed_batch calls = FAILS after 2 actors.");
        sb.AppendLine("   The ONLY way to add 25 actors: put ALL 75 ops in the single Turn 3 seed_batch.");
        sb.AppendLine();
        sb.AppendLine("## Workflow:");
        sb.AppendLine("1. seed_query(type:\"scene\") — ONCE.");
        sb.AppendLine("2. seed_query(type:\"assets\") — ONCE, only if file paths are needed.");
        sb.AppendLine("3. Plan ALL operations. Predict DFS IDs (sequential from current count).");
        sb.AppendLine("4. seed_batch — ONE call with ALL operations. No intermediate checks.");
        sb.AppendLine("5. Summarize in Japanese. STOP.");
        sb.AppendLine();
        sb.AppendLine("## Additional Rules:");
        sb.AppendLine("- set_value values are always strings: numbers=\"45.0\", bool=\"true\"/\"false\", color=\"r,g,b,a\".");
        sb.AppendLine("- NEVER guess file paths — use seed_query to get real absolute paths first.");
        sb.AppendLine("- After summarizing in Japanese, STOP. Do NOT call any more tools.");
        sb.AppendLine("=================================");
        sb.AppendLine();

        // ── システムメッセージは CLI モードでは使わない ─────────────────────────────
        // API モード向けの SYSTEM_PROMPT（個別ツール呼び出しルール等）は MCP 指示と矛盾するため除外する。
        // 例: "Call get_scene_info after every add_actor" は seed_batch の一括実行方針に反する。

        // システムメッセージ以外の会話履歴をロールラベル付きで出力する。
        // 最後のユーザーメッセージにバッチ指示を付加する。
        // ・ユーザー発言として付加することでシステムプリアンブルより高い優先度でモデルに届く
        // ・プロンプト末尾（"Assistant:" 直前）に置くことで再帰性バイアスを最大活用する
        // ・"Undo可能" と伝えることで失敗を恐れた確認ループを抑制する（GPT review ⑤）
        sb.AppendLine("## Conversation History:");
        var nonSystemMsgs = messages.Where(m => m.Role != "system").ToList();
        var lastUserMsg   = nonSystemMsgs.LastOrDefault(m => m.Role == "user");
        foreach (var msg in nonSystemMsgs)
        {
            var roleName = msg.Role switch
            {
                "user"      => "User",
                "assistant" => "Assistant",
                "tool"      => "Tool Result",
                _           => "Other",
            };
            var content = msg.Content;

            // 最後のユーザーメッセージの末尾にバッチ強制の注記を付加する。
            // 失敗を恐れず一括実行できるよう "Undo可能" の旨も添える。
            if (ReferenceEquals(msg, lastUserMsg))
                content +=
                    "\n(注意: すべての操作を必ず1回のseed_batchコールにまとめること。" +
                    "アクターやコンポーネントごとに個別に呼ばないこと。" +
                    "操作はすべてUndo可能なので失敗を恐れず一括実行すること。)";

            sb.AppendLine($"{roleName}: {content}");
        }
        sb.AppendLine("\nAssistant: ");
        return sb.ToString();
    }

    /// <summary>
    /// Gemini CLI 起動用の ProcessStartInfo を構築する。
    /// 作業ディレクトリには空のサンドボックスを使用し、プロジェクトのソースファイルを
    /// Gemini に見せない。これにより Gemini のファイル編集ツールが誤動作するのを防ぐ。
    /// </summary>
    private ProcessStartInfo BuildGeminiStartInfo(string geminiPath)
    {
        var modelFlag = string.IsNullOrWhiteSpace(_model) ? "" : $" -m {_model}";
        // 空のサンドボックスディレクトリを使用する。
        // プロジェクトルートを指定すると Gemini CLI がコーディングエージェントとして
        // Rust ソースファイルを直接編集してしまうため、意図的に何もないディレクトリを渡す。
        // SEED のシーン操作は JSON ツール呼び出し（IPC 経由）で行うため
        // ファイルシステムアクセスは不要。
        var workDir = GetOrCreateGeminiSandbox();
        var psi = new ProcessStartInfo
        {
            FileName               = "cmd.exe",
            // -p - : stdin からプロンプトを読み取るプリントモード（EOF まで読んで 1 回応答して終了）
            // --approval-mode yolo : MCP ツール（seed_query / seed_batch）を自動承認する。
            //   coreTools:[] で危険なネイティブツールは封じてあるため安全。
            //   有効値: "default" | "auto_edit" | "yolo" | "plan"
            Arguments              = $"/c \"\"{geminiPath}\"{modelFlag} -p - --skip-trust --approval-mode yolo\"",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
            WorkingDirectory       = workDir,
        };
        psi.EnvironmentVariables["CI"]                        = "true";
        psi.EnvironmentVariables["NON_INTERACTIVE"]            = "true";
        // GEMINI_CLI_TRUST_WORKSPACE=true: ワークスペース設定（.gemini/settings.json）を信頼済みとしてロードする。
        // 未設定だと Gemini CLI のフォルダトラスト機構により workspace 設定が無視され、
        // mcpServers や coreTools が適用されない（デフォルトは untrusted = workspace 設定を空扱い）。
        psi.EnvironmentVariables["GEMINI_CLI_TRUST_WORKSPACE"] = "true";
        return psi;
    }

    /// <summary>
    /// Gemini CLI 専用の空サンドボックスディレクトリを作成して返す。
    ///
    /// .gemini/settings.json に MCP サーバー（SeedMcpServer.exe）を登録し、
    /// coreTools を空にすることでシェル実行・ファイル編集などすべてのネイティブツールを無効化する。
    ///
    /// これにより Gemini は SeedMcpServer が公開する 2 つのツールのみ使用可能になる:
    ///   seed_query  → シーン情報 / アセット一覧の取得
    ///   seed_batch  → シーン操作の一括実行（変更の唯一の手段）
    ///
    /// "変更できる手段が seed_batch の 1 回のみ" という仕組みで
    /// Gemini が全操作をまとめるよう強制される（プロンプト指示でなく設計で強制）。
    /// </summary>
    private static string GetOrCreateGeminiSandbox()
    {
        var sandboxDir = Path.Combine(Path.GetTempPath(), "seed_gemini_sandbox");
        var dotGemini  = Path.Combine(sandboxDir, ".gemini");
        Directory.CreateDirectory(dotGemini);

        // SeedMcpServer.exe のパスを SEEDEditor.exe と同じディレクトリから解決する。
        var editorDir     = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
        var mcpServerPath = Path.Combine(editorDir, "SeedMcpServer.exe")
            .Replace('\\', '/');  // JSON 文字列中のパス区切りはスラッシュに統一

        // coreTools: [] → すべてのネイティブツール（run_shell_command / read_file 等）を無効化。
        // mcpServers.seed → SeedMcpServer.exe を MCP プロバイダーとして登録。
        // maxSessionTurns は設定しない。
        //   値が小さすぎるとタスクが途中で打ち切られエラーの原因になる。
        //   バッチ強制はプロンプト指示とユーザーメッセージへの付加で行う。
        var settingsJson = $$"""
            {
              "coreTools": [],
              "mcpServers": {
                "seed": {
                  "command": "{{mcpServerPath}}"
                }
              }
            }
            """;
        File.WriteAllText(
            Path.Combine(dotGemini, "settings.json"),
            settingsJson,
            new UTF8Encoding(false));

        return sandboxDir;
    }

    /// <summary>stdout テキストを解析して AIResponse を生成する。</summary>
    private static AIResponse ParseResponse(string output)
    {
        var response = new AIResponse { TextContent = output };
        var matches  = Regex.Matches(output, @"```json\s*({.*?})\s*```", RegexOptions.Singleline);
        foreach (Match m in matches)
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                var root = doc.RootElement;
                if (root.TryGetProperty("tool", out var nameEl))
                {
                    response.ToolCalls.Add(new ToolCall
                    {
                        Id            = Guid.NewGuid().ToString("N"),
                        FunctionName  = nameEl.GetString() ?? "",
                        ArgumentsJson = root.TryGetProperty("parameters", out var p) ? p.GetRawText() : "{}",
                    });
                }
            }
            catch { }
        }
        return response;
    }

    /// <summary>コマンドの実行ファイルパスを解決する。npm グローバルパスを優先する。</summary>
    private string ResolveCommandPath(string command)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmPath = Path.Combine(appData, "npm", command + ".cmd");
        if (File.Exists(npmPath)) return npmPath;

        try
        {
            var checkCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            var psi = new ProcessStartInfo
            {
                FileName               = checkCmd,
                Arguments              = command,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                var output = p.StandardOutput.ReadToEnd().Split('\n', '\r')[0].Trim();
                p.WaitForExit();
                if (p.ExitCode == 0 && !string.IsNullOrEmpty(output)) return output;
            }
        }
        catch { }
        return command;
    }

    private bool IsCommandAvailable(string command)
        => ResolveCommandPath(command) != command || CanRunCommand(command);

    /// <summary>CLI 出力に含まれる ANSI エスケープシーケンスを除去する。</summary>
    private static string StripAnsiCodes(string text)
        => Regex.Replace(text, @"\x1b(\[[0-9;]*[a-zA-Z]|\][^\x07]*\x07|.)", "");

    private bool CanRunCommand(string command)
    {
        try
        {
            var checkCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            var psi = new ProcessStartInfo
            {
                FileName               = checkCmd,
                Arguments              = command,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
