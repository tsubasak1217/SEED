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
            EditorLog.Write($"[CliAgent][Dispose] バックグラウンド Kill 開始 name={Name}");
            _ = Task.Run(() =>
            {
                var swBg = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    processToKill.Kill(entireProcessTree: true);
                    EditorLog.Write($"[CliAgent][Dispose] Kill 完了 ({swBg.ElapsedMilliseconds}ms)");
                }
                catch (Exception ex) { EditorLog.Write($"[CliAgent][Dispose] Kill 例外: {ex.Message}"); }
                try { processToKill.Dispose(); } catch { }
            });
        }

        try { stdinToClose?.Dispose(); } catch { }
        try { _warmLock.Dispose(); } catch { }
        try { _chatLock.Dispose(); } catch { }

        EditorLog.Write($"[CliAgent][Dispose] 完了（Kill はバックグラウンド）name={Name}");
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
        var swW = System.Diagnostics.Stopwatch.StartNew();
        EditorLog.Write($"[CliAgent][WarmUp] 開始 name={Name} model={_model}");
        try
        {
            // ResolveCommandPath は where/which コマンドをブロック実行するため
            // スレッドプールで実行し UI スレッドをブロックしない。
            // ConfigureAwait(false) で以降の continuation もスレッドプールで実行する
            // （WPF Dispatcher に戻さないことで _warmLock.Wait() とのデッドロックを防ぐ）。
            EditorLog.Write($"[CliAgent][WarmUp] ResolveCommandPath 開始 ({swW.ElapsedMilliseconds}ms)");
            var path = await Task.Run(() => ResolveCommandPath(_command)).ConfigureAwait(false);
            EditorLog.Write($"[CliAgent][WarmUp] ResolveCommandPath 完了 path={path} ({swW.ElapsedMilliseconds}ms)");
            if (_disposed) { EditorLog.Write("[CliAgent][WarmUp] Dispose 済みにつき中断"); return; }

            var psi     = BuildGeminiStartInfo(path);
            var process = new Process { StartInfo = psi };
            EditorLog.Write($"[CliAgent][WarmUp] Process.Start 開始 ({swW.ElapsedMilliseconds}ms)");
            if (!process.Start())
            {
                process.Dispose();
                EditorLog.Write("[CliAgent] プリウォーム: プロセス起動失敗。");
                return;
            }
            EditorLog.Write($"[CliAgent][WarmUp] Process.Start 完了 PID={process.Id} ({swW.ElapsedMilliseconds}ms)");

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
                EditorLog.Write("[CliAgent][WarmUp] Dispose 済み（プロセス起動後）につき中断");
                return;
            }

            EditorLog.Write($"[CliAgent][WarmUp] _warmLock.WaitAsync 開始 ({swW.ElapsedMilliseconds}ms)");
            try { await _warmLock.WaitAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { EditorLog.Write("[CliAgent][WarmUp] _warmLock Disposed につき中断"); return; }
            EditorLog.Write($"[CliAgent][WarmUp] _warmLock 取得 ({swW.ElapsedMilliseconds}ms)");

            try
            {
                if (_disposed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    process.Dispose();
                    EditorLog.Write("[CliAgent][WarmUp] Dispose 済み（ロック取得後）につき中断");
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

            EditorLog.Write($"[CliAgent][WarmUp] 完了（stdin 待機中）PID={process.Id} ({swW.ElapsedMilliseconds}ms)");
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

    /// <summary>メッセージ履歴とツール定義から CLI 向けのプロンプト文字列を組み立てる。</summary>
    private string BuildPrompt(List<ChatMessage> messages, List<ToolDefinition> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("あなたは SEED ゲームエンジンのエディタアシスタントです。以下の指示に従ってください。");
        sb.AppendLine("- 出力は日本語で行ってください。");
        sb.AppendLine("- コアコードを直接編集せず、提供されたツールを使用して操作してください。");
        sb.AppendLine("- ツールを使用する場合は、以下の JSON 形式のみをコードブロック ```json ... ``` で出力してください。");
        sb.AppendLine("  { \"tool\": \"関数名\", \"parameters\": { \"引数名\": \"値\" } }");
        sb.AppendLine();

        // システムメッセージを先頭にまとめて出力する
        var systemMsgs = messages.Where(m => m.Role == "system").ToList();
        if (systemMsgs.Count > 0)
        {
            sb.AppendLine("## システム指示:");
            foreach (var sm in systemMsgs)
                sb.AppendLine(sm.Content);
            sb.AppendLine();
        }

        sb.AppendLine("## 利用可能なツール:");
        foreach (var t in tools)
            sb.AppendLine($"- {t.Name}: {t.Description}");

        // システムメッセージ以外の会話履歴をロールラベル付きで出力する
        sb.AppendLine("\n## 会話履歴:");
        foreach (var msg in messages.Where(m => m.Role != "system"))
        {
            var roleName = msg.Role switch
            {
                "user"      => "ユーザー",
                "assistant" => "アシスタント",
                "tool"      => "ツール結果",
                _           => "その他",
            };
            sb.AppendLine($"{roleName}: {msg.Content}");
        }
        sb.AppendLine("\nアシスタント: ");
        return sb.ToString();
    }

    /// <summary>Gemini CLI 起動用の ProcessStartInfo を構築する。</summary>
    private ProcessStartInfo BuildGeminiStartInfo(string geminiPath)
    {
        var modelFlag = string.IsNullOrWhiteSpace(_model) ? "" : $" -m {_model}";
        // プロジェクトルート（SEED/）を作業ディレクトリにする。
        // AppDomain.CurrentDomain.BaseDirectory は editor\bin\Debug\net9.0-windows\ を指すため
        // 4階層上がるとプロジェクトルートになる。
        // WorkingDirectory を設定しないと Gemini CLI がバイナリ出力ディレクトリを
        // ワークスペースと認識してそれ以外のパスへのアクセスを拒否する。
        var workDir = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\"));
        var psi = new ProcessStartInfo
        {
            FileName               = "cmd.exe",
            // -p - : stdin からプロンプトを読み取るプリントモード（EOF まで読んで 1 回応答して終了）
            Arguments              = $"/c \"\"{geminiPath}\"{modelFlag} -p - --skip-trust --approval-mode plan\"",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
            WorkingDirectory       = workDir,
        };
        psi.EnvironmentVariables["CI"]                     = "true";
        psi.EnvironmentVariables["NON_INTERACTIVE"]        = "true";
        psi.EnvironmentVariables["GEMINI_SKIP_TRUST"]      = "true";
        psi.EnvironmentVariables["GEMINI_WORKSPACE_TRUST"] = "true";
        return psi;
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
