using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Debugger;

/// <summary>停止イベントの情報（reason 例: "breakpoint" / "step" / "pause"）。</summary>
public sealed record StoppedInfo(string Reason, int ThreadId);

/// <summary>1 スタックフレーム（現在行の表示・変数取得に使う）。</summary>
public sealed record StackFrameInfo(int Id, string Name, string? SourcePath, int Line);

/// <summary>
/// netcoredbg（DAP）を使ったスクリプトのデバッグセッション。
///
/// SEED.exe（実行中のランタイム）へアタッチし、ブレークポイントを設定して
/// 停止・継続・ステップ・スタック/変数取得を行う。プロトコルの土台は
/// <see cref="DapClient"/>、デバッガの起動と DAP の手続きをここで組み立てる。
///
/// スレッド注意: イベントは DAP 読み取りスレッドから発火する。UI から購読する側で
/// Dispatcher へマーシャリングすること。
/// </summary>
public sealed class ScriptDebugSession : IDisposable
{
    private Process?   _adapter;   // netcoredbg プロセス
    private DapClient? _client;

    // "initialized" イベント待ち（アタッチ手順の同期に使う）
    private readonly TaskCompletionSource<bool> _initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>直近に停止したスレッド ID（継続・ステップの対象）。</summary>
    public int CurrentThreadId { get; private set; }

    /// <summary>セッションがアタッチ済みで操作可能か。</summary>
    public bool IsActive => _client is not null && _adapter is { HasExited: false };

    // ── イベント（UI へ通知）─────────────────────────────────
    public event Action<StoppedInfo>? Stopped;
    public event Action?              Continued;
    public event Action?              Terminated;
    public event Action<string>?      Output;   // デバッギの標準出力など
    public event Action<string>?      Log;      // セッション・プロトコルのログ

    /// <summary>
    /// netcoredbg を起動し、指定 PID のプロセスへアタッチしてブレークポイントを設定する。
    /// netcoredbg が見つからない場合や起動失敗時は <see cref="InvalidOperationException"/>。
    /// </summary>
    public async Task StartAsync(
        int processId,
        IReadOnlyDictionary<string, IReadOnlyList<int>> breakpoints,
        CancellationToken ct = default)
    {
        var exe = NetcoredbgLocator.Locate()
            ?? throw new InvalidOperationException(
                "netcoredbg が見つかりません。SEED_NETCOREDBG 環境変数、または tools\\netcoredbg\\netcoredbg.exe に配置してください（docs/scripting_debugger.md 参照）。");

        // netcoredbg を DAP(vscode) モードで起動し、標準入出力で通信する
        _adapter = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = exe,
                Arguments              = "--interpreter=vscode",
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            },
            EnableRaisingEvents = true,
        };
        _adapter.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log?.Invoke($"[netcoredbg] {e.Data}"); };
        _adapter.Exited            += (_, _) => Terminated?.Invoke();

        _adapter.Start();
        _adapter.BeginErrorReadLine();

        _client = new DapClient(_adapter.StandardOutput.BaseStream, _adapter.StandardInput.BaseStream);
        _client.Log           += m => Log?.Invoke(m);
        _client.EventReceived += OnEvent;

        // ── DAP アタッチ手順 ──
        // 1) initialize（機能ネゴシエーション）
        await _client.SendRequestAsync("initialize", new JsonObject
        {
            ["clientID"]                     = "seed-editor",
            ["clientName"]                   = "SEED Editor",
            ["adapterID"]                    = "coreclr",
            ["locale"]                       = "en",
            ["linesStartAt1"]                = true,
            ["columnsStartAt1"]              = true,
            ["pathFormat"]                   = "path",
            ["supportsRunInTerminalRequest"] = false,
        }, ct).ConfigureAwait(false);

        // 2) attach（processId 指定）。レスポンスは configurationDone 後に返るため、
        //    ここでは待たずに投げる（先に await すると相手待ちでデッドロックする）。
        //
        //    justMyCode=false は必須。ユーザースクリプトは Roslyn がメモリ上
        //    （LoadFromStream の動的アセンブリ）に生成するため、netcoredbg の
        //    Just My Code 判定では「ユーザーコードでない」と分類されがちで、
        //    有効だとブレークポイントがスキップされて停止しない。無効化して
        //    動的アセンブリでも確実に停止させる。
        var attachTask = _client.SendRequestAsync("attach", new JsonObject
        {
            ["processId"] = processId,
            ["justMyCode"] = false,
        }, ct);

        // 3) "initialized" イベントを待ってからブレークポイントを設定する
        await _initialized.Task.WaitAsync(ct).ConfigureAwait(false);

        foreach (var (path, lines) in breakpoints)
            await SetBreakpointsAsync(path, lines, ct).ConfigureAwait(false);

        // 4) configurationDone → これで attach が完了する
        await _client.SendRequestAsync("configurationDone", new JsonObject(), ct).ConfigureAwait(false);
        await attachTask.ConfigureAwait(false);

        Log?.Invoke($"[デバッグ] PID={processId} へアタッチしました。");
    }

    /// <summary>指定ソースファイルのブレークポイント行を設定する。</summary>
    public async Task SetBreakpointsAsync(string filePath, IReadOnlyList<int> lines, CancellationToken ct = default)
    {
        if (_client is null) return;
        var arr = new JsonArray();
        foreach (var line in lines) arr.Add(new JsonObject { ["line"] = line });

        var body = await _client.SendRequestAsync("setBreakpoints", new JsonObject
        {
            ["source"]      = new JsonObject { ["path"] = filePath, ["name"] = Path.GetFileName(filePath) },
            ["breakpoints"] = arr,
        }, ct).ConfigureAwait(false);

        // 応答の verified を確認してログに出す。
        // verified=false の場合、netcoredbg がそのソース行をロード済みモジュールの
        // シーケンスポイントへ対応づけられていない（＝停止しない）。
        // 原因の切り分け（ソースパス不一致・PDB 未読込・JMC など）に必須の情報。
        var fileName = Path.GetFileName(filePath);
        if (body?["breakpoints"] is JsonArray results)
        {
            int i = 0;
            foreach (var bp in results)
            {
                var line     = bp?["line"]?.GetValue<int>() ?? (i < lines.Count ? lines[i] : 0);
                var verified = bp?["verified"]?.GetValue<bool>() ?? false;
                var message  = bp?["message"]?.GetValue<string>();
                Log?.Invoke(verified
                    ? $"[デバッグ] BP設定OK {fileName}:{line}"
                    : $"[デバッグ] BP未解決 {fileName}:{line}  {message ?? "(netcoredbg が行をバインドできませんでした)"}");
                i++;
            }
        }
        else
        {
            Log?.Invoke($"[デバッグ] setBreakpoints 応答に breakpoints がありません（{fileName}）。");
        }
    }

    // ── 実行制御 ────────────────────────────────────────────
    public Task ContinueAsync(CancellationToken ct = default) => ControlAsync("continue", ct);
    public Task StepOverAsync(CancellationToken ct = default) => ControlAsync("next", ct);
    public Task StepInAsync(CancellationToken ct = default)   => ControlAsync("stepIn", ct);
    public Task StepOutAsync(CancellationToken ct = default)  => ControlAsync("stepOut", ct);
    public Task PauseAsync(CancellationToken ct = default)    => ControlAsync("pause", ct);

    private async Task ControlAsync(string command, CancellationToken ct)
    {
        if (_client is null) return;
        await _client.SendRequestAsync(command, new JsonObject { ["threadId"] = CurrentThreadId }, ct)
                     .ConfigureAwait(false);
    }

    /// <summary>現在停止中スレッドのコールスタック上位フレームを取得する。</summary>
    public async Task<IReadOnlyList<StackFrameInfo>> GetStackTraceAsync(int levels = 20, CancellationToken ct = default)
    {
        var result = new List<StackFrameInfo>();
        if (_client is null) return result;

        var body = await _client.SendRequestAsync("stackTrace", new JsonObject
        {
            ["threadId"]   = CurrentThreadId,
            ["startFrame"] = 0,
            ["levels"]     = levels,
        }, ct).ConfigureAwait(false);

        if (body?["stackFrames"] is JsonArray frames)
        {
            foreach (var f in frames)
            {
                if (f is not JsonObject fo) continue;
                result.Add(new StackFrameInfo(
                    Id:         fo["id"]?.GetValue<int>() ?? 0,
                    Name:       fo["name"]?.GetValue<string>() ?? "",
                    SourcePath: fo["source"]?["path"]?.GetValue<string>(),
                    Line:       fo["line"]?.GetValue<int>() ?? 0));
            }
        }
        return result;
    }

    /// <summary>アタッチを解除する（デバッギ＝ランタイムは終了させない）。</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            if (_client is not null)
                await _client.SendRequestAsync("disconnect", new JsonObject
                {
                    ["terminateDebuggee"] = false,
                }, ct).ConfigureAwait(false);
        }
        catch { /* 接続が既に切れていても無視 */ }
        Dispose();
    }

    // ── イベント処理 ─────────────────────────────────────────
    private void OnEvent(string ev, JsonNode? body)
    {
        switch (ev)
        {
            case "initialized":
                _initialized.TrySetResult(true);
                break;

            case "stopped":
            {
                int threadId = body?["threadId"]?.GetValue<int>() ?? 0;
                var reason   = body?["reason"]?.GetValue<string>() ?? "";
                CurrentThreadId = threadId;
                Stopped?.Invoke(new StoppedInfo(reason, threadId));
                break;
            }

            case "continued":
                Continued?.Invoke();
                break;

            case "output":
            {
                var text = body?["output"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(text)) Output?.Invoke(text!);
                break;
            }

            case "terminated":
            case "exited":
                Terminated?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        try
        {
            if (_adapter is { HasExited: false }) _adapter.Kill();
        }
        catch { /* 終了処理のエラーは無視 */ }
        _adapter?.Dispose();
        _adapter = null;
    }
}
