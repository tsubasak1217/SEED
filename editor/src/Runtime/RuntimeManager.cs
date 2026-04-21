using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SEEDEditor.Ipc;

namespace SEEDEditor.Runtime;

// ============================================================
//  EditorState
// ============================================================

public enum EditorState { Idle, Building, Edit, Play, Pause }

// ============================================================
//  RuntimeManager
// ============================================================

/// <summary>
/// Runtime プロセスのライフサイクル・IPC・状態遷移を管理する。
///
/// 状態遷移:
///   Idle  ──[StartEdit]──▶ Edit
///   Edit  ──[Play]────────▶ Play
///   Play  ──[最小化検知]──▶ Pause   ← WinEventHook
///   Pause ──[Resume]──────▶ Play
///   Play/Pause ──[Stop]───▶ Edit    ← Kill → Edit 再起動
///   Play/Pause ──[閉じる]─▶ Edit    ← Runtime 自己終了
/// </summary>
public sealed class RuntimeManager : IDisposable
{
    // ── Win32 デリゲート ───────────────────────────────────────
    private delegate void WinEventProc(
        IntPtr hHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time);

    // ── 定数 ──────────────────────────────────────────────────
    private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    private const uint EVENT_SYSTEM_MOVESIZEEND   = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint WINEVENT_OUTOFCONTEXT      = 0x0000;
    private const int  GWL_STYLE                  = -16;
    private const int  GWL_EXSTYLE                = -20;
    private const int  WS_CHILD                   = 0x40000000;
    private const int  WS_POPUP                   = unchecked((int)0x80000000);
    private const int  WS_CAPTION                 = 0x00C00000;
    private const int  WS_THICKFRAME              = 0x00040000;
    private const int  WS_OVERLAPPEDWINDOW        = 0x00CF0000;
    private const int  WS_VISIBLE                 = 0x10000000;
    private const int  WS_EX_APPWINDOW            = 0x00040000;
    private const int  SW_RESTORE                 = 9;
    private const int  SW_SHOWDEFAULT             = 10;
    private const uint SWP_NOMOVE                 = 0x0002;
    private const uint SWP_NOSIZE                 = 0x0001;
    private const uint SWP_NOZORDER               = 0x0004;
    private const uint SWP_FRAMECHANGED           = 0x0020;

    // ── フィールド ─────────────────────────────────────────────
    private readonly string               _runtimeExePath;
    private readonly RuntimeSourceWatcher? _sourceWatcher;
    private Process?                      _process;
    private PipeServer?                   _pipe;
    private IntPtr                        _runtimeHwnd;
    private IntPtr                        _viewportContainerHwnd;
    private EditorState                   _state = EditorState.Idle;
    private Win32.RECT                    _runtimeRectBeforeEmbed;

    // WinEventHook（GC 対策で delegate を保持）
    private WinEventProc? _winEventDelegate;
    private IntPtr        _winEventHook;

    // ── 公開プロパティ・イベント ────────────────────────────────

    public EditorState State => _state;

    /// <summary>ゲームウィンドウ HWND（READY 受信後に確定）。</summary>
    public nint RuntimeHwnd => (nint)_runtimeHwnd;

    /// <summary>状態変化時に発火する。UI スレッドから呼ばれる。</summary>
    public event Action<EditorState>? StateChanged;

    /// <summary>ゲームウィンドウの HWND が確定したときに発火する。</summary>
    public event Action<nint>? RuntimeHwndAvailable;

    /// <summary>ゲームウィンドウのドラッグ / リサイズ開始時に発火する。</summary>
    public event Action? RuntimeMoveStart;

    /// <summary>ゲームウィンドウのドラッグ / リサイズ終了時に発火する。</summary>
    public event Action? RuntimeMoveEnd;

    /// <summary>ヒエラルキーが更新されたときに発火する（JSON 文字列）。</summary>
    public event Action<string>? HierarchyUpdated;

    /// <summary>選択インスタンスが変化したときに発火する（-1 = 選択なし）。</summary>
    public event Action<int>? SelectionChanged;

    /// <summary>ビューポートで複数選択されたときに発火する（インスタンスインデックスのリスト）。</summary>
    public event Action<IReadOnlyList<int>>? SelectionMultiChanged;

    /// <summary>シーン保存完了時に発火する（ok=true: 成功, ok=false: 失敗メッセージ付き）。</summary>
    public event Action<bool, string>? SaveCompleted;

    /// <summary>ビューポート上で短押し右クリックされたときに発火する。</summary>
    public event Action? ViewportContextMenuRequested;

    /// <summary>
    /// ランタイムが最初のフレームを実際に描画したときに発火する。
    /// デバッグビルドのランタイムのみ送信する（FIRST_FRAME メッセージ）。
    /// </summary>
    public event Action? FirstFrameReady;

    // ── コンストラクタ ─────────────────────────────────────────

    public RuntimeManager(string runtimeExePath)
    {
        _runtimeExePath = runtimeExePath;

        var sourceDir = ResolveRuntimeSourceDir(runtimeExePath);
        if (sourceDir is not null)
        {
            _sourceWatcher = new RuntimeSourceWatcher(sourceDir, runtimeExePath);
            EditorLog.Write($"RuntimeSourceWatcher 開始 — dir={sourceDir}  isDirty={_sourceWatcher.IsDirty}");
        }
        else
        {
            EditorLog.Write($"RuntimeSourceWatcher: Cargo.toml が見つからないためスキップ (exe={runtimeExePath})");
        }
    }

    // ── 公開: 状態遷移 ─────────────────────────────────────────

    /// <summary>Edit モードで Runtime を起動する（エディタ起動時に呼ぶ）。</summary>
    public async Task StartEditAsync(IntPtr viewportContainerHwnd)
    {
        _viewportContainerHwnd = viewportContainerHwnd;

        // 開発時のみ: ソースに変更がある場合だけビルドする
        if (_sourceWatcher is not null && _sourceWatcher.IsDirty)
        {
            // ビルド前に旧プロセスを確実に終了（EXE ファイルのロック解除）
            KillStaleRuntimeProcesses();

            ChangeState(EditorState.Building);
            var sourceDir = ResolveRuntimeSourceDir(_runtimeExePath)!;
            var ok = await BuildAsync(sourceDir);
            if (!ok)
            {
                ChangeState(EditorState.Idle);
                throw new InvalidOperationException(
                    "cargo build failed. ログを確認してください:\n" +
                    Path.GetFullPath("logs/SEEDEditor.log"));
            }
            _sourceWatcher.MarkClean();
        }
        else if (_sourceWatcher is not null)
        {
            EditorLog.Write("StartEditAsync — ソース変更なし、ビルドをスキップ");
        }

        await LaunchAsync(editMode: true);
    }

    /// <summary>Play ボタン: Edit Runtime を終了し Play Runtime を起動する。</summary>
    public async Task PlayAsync()
    {
        KillRuntime(sendStop: true);
        await LaunchAsync(editMode: false);
    }

    /// <summary>最小化検知 or Pause ボタン: デバッグカメラに切替えて Viewport に埋め込む。</summary>
    public void Pause()
    {
        if (_state != EditorState.Play) return;
        _pipe?.Send("PAUSE");
        EmbedRuntimeWindow();
        ChangeState(EditorState.Pause);
    }

    /// <summary>Resume ボタン: 通常モードに戻し独立ウィンドウに戻す。</summary>
    public void Resume()
    {
        if (_state != EditorState.Pause) return;
        DetachRuntimeWindow();
        _pipe?.Send("RESUME");
        ChangeState(EditorState.Play);
    }

    /// <summary>Runtime に任意のメッセージを送信する（IPC 経由）。</summary>
    public void SendToRuntime(string message) => _pipe?.Send(message);

    /// <summary>
    /// Runtime ウィンドウをコンテナの現在のクライアントサイズに合わせてリサイズする。
    /// 最大化起動などで初期サイズがズレた場合の補正用。
    /// </summary>
    public void ResizeRuntimeToContainer()
    {
        if (_runtimeHwnd == IntPtr.Zero || _viewportContainerHwnd == IntPtr.Zero) return;
        Win32.GetClientRect(_viewportContainerHwnd, out var rect);
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w > 0 && h > 0)
            Win32.MoveWindow(_runtimeHwnd, 0, 0, w, h, repaint: true);
        EditorLog.Write($"ResizeRuntimeToContainer — {w}x{h}");
    }

    /// <summary>Stop ボタン: Runtime を終了し Edit に戻る。</summary>
    public void Stop()
    {
        if (_state == EditorState.Pause) DetachRuntimeWindow();
        KillRuntime(sendStop: true);
        _ = StartEditAsync(_viewportContainerHwnd);
    }

    // ── プライベート: 残存プロセス終了 ──────────────────────────

    /// <summary>
    /// 同名の Runtime プロセスが残っていれば強制終了し、
    /// EXE ファイルのロックを解放する。
    /// </summary>
    private void KillStaleRuntimeProcesses()
    {
        var exeName = Path.GetFileNameWithoutExtension(_runtimeExePath); // "SEED"
        foreach (var proc in Process.GetProcessesByName(exeName))
        {
            // 自分が管理しているプロセスは KillRuntime で既に終了済みのはず
            if (_process is not null && proc.Id == _process.Id)
            {
                proc.Dispose();
                continue;
            }
            try
            {
                EditorLog.Write($"KillStaleRuntimeProcesses — killing PID={proc.Id}");
                proc.Kill();
                proc.WaitForExit(1000);
            }
            catch (Exception ex)
            {
                EditorLog.Write($"KillStaleRuntimeProcesses — kill failed: {ex.Message}");
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    // ── プライベート: ビルド ───────────────────────────────────

    /// <summary>
    /// exe パスから runtime/ ソースディレクトリを解決する。
    /// target/debug または target/release の 2 階層上が runtime/。
    /// Cargo.toml が存在しない（配布時 etc）場合は null を返す。
    /// </summary>
    private static string? ResolveRuntimeSourceDir(string exePath)
    {
        // runtime/target/debug/SEED.exe
        //          ↑ ↑ ↑ 3 つ上
        var exeDir     = Path.GetDirectoryName(exePath) ?? "";        // target/debug
        var targetDir  = Path.GetDirectoryName(exeDir)  ?? "";        // target
        var runtimeDir = Path.GetDirectoryName(targetDir) ?? "";      // runtime
        var cargoToml  = Path.Combine(runtimeDir, "Cargo.toml");
        return File.Exists(cargoToml) ? runtimeDir : null;
    }

    /// <summary>
    /// 指定ディレクトリで <c>cargo build</c> を実行し、完了を待つ。
    /// 標準エラー出力（cargo の進捗表示）をログに書き出す。
    /// </summary>
    private static async Task<bool> BuildAsync(string workingDir)
    {
        EditorLog.Write($"BuildAsync — cargo build  dir={workingDir}");

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = "cargo",
                Arguments              = "build",
                WorkingDirectory       = workingDir,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            },
            EnableRaisingEvents = true,
        };

        // cargo はほぼすべての出力を stderr に書く
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) EditorLog.Write($"[cargo] {e.Data}"); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) EditorLog.Write($"[cargo] {e.Data}"); };
        proc.Exited             += (_, _) => tcs.TrySetResult(proc.ExitCode);

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            EditorLog.Write($"BuildAsync — cargo の起動に失敗: {ex.Message}  (cargo が PATH に無い?)");
            proc.Dispose();
            return false;
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var exitCode = await tcs.Task;
        proc.Dispose();

        EditorLog.Write($"BuildAsync — cargo build 終了  exitCode={exitCode}");
        return exitCode == 0;
    }

    // ── プライベート: 起動 ─────────────────────────────────────

    private async Task LaunchAsync(bool editMode)
    {
        EditorLog.Write($"LaunchAsync start — editMode={editMode}");

        _pipe = new PipeServer();
        EditorLog.Write($"PipeServer created — name={_pipe.PipeName}");

        var args = editMode
            ? $"--mode=edit --pipe={_pipe.PipeName} --parent-hwnd={_viewportContainerHwnd}"
            : $"--mode=play --pipe={_pipe.PipeName}";

        var workDir = ResolveWorkingDirectory(_runtimeExePath);
        EditorLog.Write($"Process.Start — exe={_runtimeExePath}  args={args}  workDir={workDir}");

        var stderr = new System.Text.StringBuilder();

        _process = Process.Start(new ProcessStartInfo
        {
            FileName               = _runtimeExePath,
            Arguments              = args,
            UseShellExecute        = false,
            WorkingDirectory       = workDir,
            CreateNoWindow         = true,
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("Failed to start runtime.");

        EditorLog.Write($"Process started — PID={_process.Id}");

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderr.AppendLine(e.Data);
                EditorLog.Write($"[STDERR] {e.Data}");
            }
        };
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) EditorLog.Write($"[STDOUT] {e.Data}");
        };
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();

        _process.EnableRaisingEvents = true;
        _process.Exited += OnRuntimeExited;

        _pipe.MessageReceived += OnPipeMessage;

        // 起動直後クラッシュを検知
        EditorLog.Write("Waiting 500ms for crash detection...");
        await Task.Delay(500);
        if (_process.HasExited)
        {
            var msg = stderr.Length > 0 ? stderr.ToString() : "(no stderr output)";
            throw new InvalidOperationException($"Runtime crashed immediately (exit code {_process.ExitCode}):\n{msg}");
        }
        // 正常起動できたのでクラッシュカウントをリセット
        _restartCount    = 0;
        _intentionalStop = false;
        EditorLog.Write("Process still alive — waiting for pipe connection (10s timeout)...");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _pipe.WaitForConnectionAsync(cts.Token);
            EditorLog.Write("Pipe connected");
        }
        catch (Exception ex)
        {
            EditorLog.Write($"Pipe connection timeout/error: {ex.Message}  (continuing without HWND)");
        }

        ChangeState(editMode ? EditorState.Edit : EditorState.Play);
        EditorLog.Write($"State changed to {(editMode ? "Edit" : "Play")}");

        if (!editMode) InstallMinimizeHook();
    }

    // ── プライベート: IPC メッセージ処理 ──────────────────────

    private void OnPipeMessage(string msg)
    {
        if (msg.StartsWith("READY:", StringComparison.Ordinal) &&
            long.TryParse(msg["READY:".Length..], out var hwnd))
        {
            _runtimeHwnd = (IntPtr)hwnd;
            EditorLog.Write($"[Runtime→Editor] READY  hwnd=0x{hwnd:X}");
            RuntimeHwndAvailable?.Invoke((nint)hwnd);
        }
        else if (msg == "FIRST_FRAME")
        {
            EditorLog.Write("[Runtime→Editor] FIRST_FRAME — 最初の実フレーム描画完了");
            FirstFrameReady?.Invoke();
        }
        else if (msg.StartsWith("HIERARCHY:", StringComparison.Ordinal))
        {
            var json = msg["HIERARCHY:".Length..];
            EditorLog.Write($"[Runtime→Editor] HIERARCHY ({json.Length} chars)");
            HierarchyUpdated?.Invoke(json);
        }
        else if (msg.StartsWith("SELECTED:", StringComparison.Ordinal) &&
                 int.TryParse(msg["SELECTED:".Length..], out var selIdx))
        {
            EditorLog.Write($"[Runtime→Editor] SELECTED idx={selIdx}");
            SelectionChanged?.Invoke(selIdx);
        }
        else if (msg.StartsWith("SELECTED_MULTI:", StringComparison.Ordinal))
        {
            var ids = msg["SELECTED_MULTI:".Length..]
                .Split(',')
                .Select(s => int.TryParse(s, out var n) ? (int?)n : null)
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .ToList();
            EditorLog.Write($"[Runtime→Editor] SELECTED_MULTI count={ids.Count}");
            if (ids.Count > 0) SelectionMultiChanged?.Invoke(ids);
        }
        else if (msg == "CONTEXT_MENU")
        {
            EditorLog.Write("[Runtime→Editor] CONTEXT_MENU");
            ViewportContextMenuRequested?.Invoke();
        }
        else if (msg == "SAVE_OK")
        {
            EditorLog.Write("[Runtime→Editor] SAVE_OK");
            SaveCompleted?.Invoke(true, "");
        }
        else if (msg.StartsWith("SAVE_ERROR:", StringComparison.Ordinal))
        {
            EditorLog.Write($"[Runtime→Editor] SAVE_ERROR: {msg["SAVE_ERROR:".Length..]}");
            SaveCompleted?.Invoke(false, msg["SAVE_ERROR:".Length..]);
        }
        else
        {
            EditorLog.Write($"[Runtime→Editor] unknown: {msg[..Math.Min(80, msg.Length)]}");
        }
    }

    // ── プライベート: Runtime 自己終了 ────────────────────────

    private int            _restartCount  = 0;
    private const int      MaxRestarts    = 3;
    // KillRuntime が呼ばれた後に Exited が遅延発火しても
    // クラッシュと誤判定しないためのフラグ。
    private volatile bool  _intentionalStop = false;

    private void OnRuntimeExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;
        EditorLog.Write($"OnRuntimeExited — exitCode={exitCode}  intentional={_intentionalStop}  restartCount={_restartCount}");

        // KillRuntime / Stop による意図的な終了は再起動もクラッシュ計上もしない
        if (_intentionalStop)
        {
            _intentionalStop = false;
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UninstallMinimizeHook();
                _pipe?.Dispose();
                _pipe        = null;
                _runtimeHwnd = IntPtr.Zero;
                ChangeState(EditorState.Idle);
            });
            return;
        }

        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            UninstallMinimizeHook();
            _pipe?.Dispose();
            _pipe         = null;
            _runtimeHwnd  = IntPtr.Zero;
            ChangeState(EditorState.Idle);

            if (_restartCount >= MaxRestarts)
            {
                EditorLog.Write("OnRuntimeExited — restart limit reached, stopping");
                MessageBox.Show(
                    $"Runtime が {MaxRestarts} 回クラッシュしたため再起動を中断しました。\nログを確認してください:\n{System.IO.Path.GetFullPath("logs/SEEDEditor.log")}",
                    "SEED Editor", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _restartCount++;
            EditorLog.Write($"OnRuntimeExited — restarting (attempt {_restartCount})");
            try
            {
                await StartEditAsync(_viewportContainerHwnd);
            }
            catch (Exception ex)
            {
                EditorLog.Write($"OnRuntimeExited — StartEditAsync failed: {ex}");
                MessageBox.Show($"Runtime 再起動失敗:\n{ex}", "SEED Editor",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    // ── プライベート: WinEvent フック（最小化検知）────────────

    private void InstallMinimizeHook()
    {
        _winEventDelegate = OnWinEvent;
        // MOVESIZESTART(0x000A) 〜 MINIMIZESTART(0x0016) を一括フック
        _winEventHook = Win32.SetWinEventHook(
            EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MINIMIZESTART,
            IntPtr.Zero, _winEventDelegate,
            _process is not null ? (uint)_process.Id : 0,
            0, WINEVENT_OUTOFCONTEXT);
    }

    private void UninstallMinimizeHook()
    {
        if (_winEventHook == IntPtr.Zero) return;
        Win32.UnhookWinEvent(_winEventHook);
        _winEventHook     = IntPtr.Zero;
        _winEventDelegate = null;
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != _runtimeHwnd) return;

        if (eventType == EVENT_SYSTEM_MINIMIZESTART)
            Application.Current.Dispatcher.Invoke(Pause);
        else if (eventType == EVENT_SYSTEM_MOVESIZESTART)
            RuntimeMoveStart?.Invoke();
        else if (eventType == EVENT_SYSTEM_MOVESIZEEND)
            RuntimeMoveEnd?.Invoke();
    }

    // ── プライベート: ウィンドウ埋め込み ───────────────────────

    private void EmbedRuntimeWindow()
    {
        if (_runtimeHwnd == IntPtr.Zero || _viewportContainerHwnd == IntPtr.Zero) return;

        // Resume 時に元のサイズ・位置へ戻すために保存
        Win32.GetWindowRect(_runtimeHwnd, out _runtimeRectBeforeEmbed);

        Win32.ShowWindow(_runtimeHwnd, SW_RESTORE);

        // WS_POPUP / タイトルバー / リサイズ枠を除去して WS_CHILD に
        var style = Win32.GetWindowLong(_runtimeHwnd, GWL_STYLE);
        style = (style & ~WS_POPUP & ~WS_CAPTION & ~WS_THICKFRAME) | WS_CHILD;
        Win32.SetWindowLong(_runtimeHwnd, GWL_STYLE, style);

        // タスクバーエントリを除去
        var exStyle = Win32.GetWindowLong(_runtimeHwnd, GWL_EXSTYLE);
        Win32.SetWindowLong(_runtimeHwnd, GWL_EXSTYLE, exStyle & ~WS_EX_APPWINDOW);

        Win32.SetParent(_runtimeHwnd, _viewportContainerHwnd);

        Win32.GetClientRect(_viewportContainerHwnd, out var rect);
        Win32.MoveWindow(_runtimeHwnd, 0, 0,
            rect.Right - rect.Left, rect.Bottom - rect.Top, repaint: true);

        Win32.SetWindowPos(_runtimeHwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    private void DetachRuntimeWindow()
    {
        if (_runtimeHwnd == IntPtr.Zero) return;

        Win32.SetParent(_runtimeHwnd, IntPtr.Zero);

        var style = Win32.GetWindowLong(_runtimeHwnd, GWL_STYLE);
        style = (style & ~WS_CHILD) | WS_OVERLAPPEDWINDOW | WS_VISIBLE;
        Win32.SetWindowLong(_runtimeHwnd, GWL_STYLE, style);

        var exStyle = Win32.GetWindowLong(_runtimeHwnd, GWL_EXSTYLE);
        Win32.SetWindowLong(_runtimeHwnd, GWL_EXSTYLE, exStyle | WS_EX_APPWINDOW);

        Win32.SetWindowPos(_runtimeHwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

        // Pause 前のサイズ・位置を復元
        var r = _runtimeRectBeforeEmbed;
        int w = r.Right  - r.Left;
        int h = r.Bottom - r.Top;
        if (w > 0 && h > 0)
            Win32.MoveWindow(_runtimeHwnd, r.Left, r.Top, w, h, repaint: true);

        Win32.ShowWindow(_runtimeHwnd, SW_SHOWDEFAULT);
    }

    // ── プライベート: プロセス終了 ─────────────────────────────

    private void KillRuntime(bool sendStop = false)
    {
        // Exited が Kill より先に発火する競合があるため、先にフラグを立てる
        _intentionalStop = true;
        UninstallMinimizeHook();
        if (sendStop) _pipe?.Send("STOP");
        _pipe?.Dispose();
        _pipe = null;
        if (_process is not null)
        {
            // Kill 前に購読解除 — Kill 後に Exited が発火して OnRuntimeExited が
            // 新 _pipe を Dispose するレースコンディションを防ぐ。
            _process.Exited -= OnRuntimeExited;
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(500);
            }
            _process.Dispose();
        }
        _process     = null;
        _runtimeHwnd = IntPtr.Zero;
    }

    /// <summary>
    /// Runtime の作業ディレクトリを解決する。
    /// Cargo ビルド出力（target/debug, target/release）の場合は 2 階層上に戻し、
    /// assets/ などが存在するプロジェクトルートを返す。
    /// 配布時（exe 隣に assets/ がある構成）はそのまま exe のディレクトリを返す。
    /// </summary>
    private static string ResolveWorkingDirectory(string exePath)
    {
        var exeDir     = Path.GetDirectoryName(exePath)!;
        var buildType  = Path.GetFileName(exeDir);                    // "debug" / "release"
        var targetDir  = Path.GetFileName(Path.GetDirectoryName(exeDir)!); // "target"

        if ((buildType is "debug" or "release") && targetDir == "target")
            return Path.GetFullPath(Path.Combine(exeDir, @"..\.."));

        return exeDir;
    }

    private void ChangeState(EditorState next)
    {
        _state = next;
        StateChanged?.Invoke(next);
    }

    public void Dispose()
    {
        KillRuntime();
        _sourceWatcher?.Dispose();
    }

    // ============================================================
    //  Win32 P/Invoke（ネストクラス）
    // ============================================================

    private static class Win32
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventProc lpfnWinEventProc,
            uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        internal static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        internal static extern bool MoveWindow(
            IntPtr hWnd, int x, int y, int w, int h, bool repaint);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }
    }
}
