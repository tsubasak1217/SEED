// ============================================================
//  AndroidRunController.cs — エディタからの Android の実行（ビルド → インストール → 起動 → logcat → 停止）の段取り
//
//  【役割】
//    - 実行: 中核の RunAsync（Goal = Run）をスレッドプールで動かし、イベントを状態機械（AndroidRunStateMachine）と
//            Output パネルの行（AndroidRunOutputFormatter）へ流す
//    - 見張り: 起動に成功したら（Running）、数秒おきに pidof でアプリが動いているかを確かめ、続けて見つからなければ
//              「アプリが終わった」として中断の合図を送る（logcat は止めても成功扱いなので、パイプラインはすぐ戻る）
//    - 停止: 中断の合図を送り（ビルド中なら子プロセスとその子孫の終了を中核が待つ）、パイプラインが戻るのを待ってから、
//            起動の工程に入っていればアプリを止める（am force-stop。実行の合図とは別の新しい合図・時間切れ付き）
//    - 終了: パイプラインが戻ったら終わり方を 1 行出して Idle へ
//
//  【スレッド】
//    状態はロック（_gate）の中だけで変える。イベント（StateChanged / OutputWritten）は任意のスレッドから、ロックの外で
//    発火する（受け手: MainWindow が Dispatcher へ回す／EditorLog.Write はスレッド安全）。パイプラインのイベントは
//    同期の IProgress で受ける（WPF の Progress<T> を使わない。単体テストでも順序と状態の反映が変わらないように）。
//
//  【PC の実行との排他】はここでは見ない（プレイバーの判断 PlayBarPolicy が、PC の実行中は Android の実行を始めさせない）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests が偽の中核で遷移を確かめる）。
// ============================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;

namespace SEEDEditor.AndroidRun;

/// <summary>エディタからの Android の実行。</summary>
public sealed class AndroidRunController : IDisposable
{
    /// <summary>中核の入口。</summary>
    private readonly IAndroidRunBackend _backend;

    /// <summary>見張り・停止の時間の決まり。</summary>
    private readonly AndroidRunTimings _timings;

    /// <summary>状態の排他。</summary>
    private readonly object _gate = new();

    /// <summary>状態遷移。</summary>
    private readonly AndroidRunStateMachine _machine = new();

    /// <summary>いまの実行の中断の合図（動いていなければ null）。</summary>
    private CancellationTokenSource? _runCancellation;

    /// <summary>アプリの見張りの中断の合図（見張っていなければ null）。</summary>
    private CancellationTokenSource? _monitorCancellation;

    /// <summary>いまの実行が終わる（Idle に戻る）と完了するタスク。</summary>
    private Task _completion = Task.CompletedTask;

    /// <summary>破棄済みか（エディタを閉じた）。</summary>
    private bool _disposed;

    /// <summary>中核の入口と時間の決まりを指定して作る。</summary>
    /// <param name="backend">中核の入口。</param>
    /// <param name="timings">見張り・停止の時間の決まり。</param>
    public AndroidRunController(IAndroidRunBackend backend, AndroidRunTimings timings)
    {
        _backend = backend;
        _timings = timings;
    }

    /// <summary>状態・進み具合が変わった（任意のスレッドから。受け手は <see cref="Snapshot"/> を読み直す）。</summary>
    public event Action? StateChanged;

    /// <summary>Output パネルへ出す行（任意のスレッドから・複数のスレッドから同時に届き得る）。</summary>
    public event Action<AndroidRunOutputLine>? OutputWritten;

    /// <summary>いまの写し。</summary>
    public AndroidRunSnapshot Snapshot
    {
        get
        {
            lock (_gate) return _machine.Snapshot;
        }
    }

    /// <summary>いまの実行が終わる（Idle に戻る）と完了するタスク（動いていなければ完了済み）。</summary>
    public Task Completion
    {
        get
        {
            lock (_gate) return _completion;
        }
    }

    /// <summary>
    /// 実行を始める（動いていないときだけ）。すぐ戻り、実行はスレッドプールで進む。
    /// </summary>
    /// <param name="request">指定（エディタは <see cref="AndroidEditorRunRequests.ForPlay"/>）。</param>
    /// <param name="targetText">実行先の表示名（Output パネル・進捗の表示用）。</param>
    /// <returns>始めたら true。</returns>
    public bool TryStart(AndroidRunRequest request, string targetText)
    {
        CancellationTokenSource cancellation;
        TaskCompletionSource done;
        lock (_gate)
        {
            if (_disposed || !_machine.Start(targetText, request.Serial)) return false;
            cancellation = new CancellationTokenSource();
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _runCancellation = cancellation;
            _completion = done.Task;
        }
        RaiseStateChanged();
        Emit(AndroidRunOutputFormatter.Started(targetText, request.ProjectDir));
        _ = Task.Run(() => RunLoopAsync(request, cancellation, done));
        return true;
    }

    /// <summary>
    /// 停止ボタン: 中断の合図を送る。戻り値は実行が終わる（アプリを止め終える）と完了する。
    /// ビルド中なら子プロセスとその子孫の終了を中核が待ってから戻る。
    /// </summary>
    /// <returns>実行が終わると完了するタスク。</returns>
    public Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task completion;
        lock (_gate)
        {
            completion = _completion;
            if (!_machine.RequestStop(AndroidRunStopReason.User)) return completion;
            cancellation = _runCancellation;
        }
        RaiseStateChanged();
        Emit(AndroidRunOutputFormatter.Stopping(AndroidRunStopReason.User));
        // 中断の合図は子プロセスを止める処理（プロセスツリーの終了）を同期に呼ぶので、UI スレッドを止めないよう別スレッドで送る
        _ = Task.Run(() => TryCancel(cancellation));
        return completion;
    }

    /// <summary>
    /// エディタを閉じる: 実行と見張りに中断の合図を送る（待たない）。子プロセスとその子孫はこの場で止まる。
    /// 端末のアプリは止めない（閉じる操作を adb で待たせないため。docs/android.md §20）。
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? run;
        CancellationTokenSource? monitor;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _machine.RequestStop(AndroidRunStopReason.Shutdown);
            run = _runCancellation;
            monitor = _monitorCancellation;
        }
        TryCancel(monitor);
        TryCancel(run);
    }

    // ── 実行の本体（スレッドプール）─────────────────────────────

    /// <summary>パイプラインを動かし、戻ったら終わり方を決めて（要ればアプリを止めて）Idle へ戻す。</summary>
    private async Task RunLoopAsync(AndroidRunRequest request, CancellationTokenSource cancellation, TaskCompletionSource done)
    {
        try
        {
            AndroidPipelineResult result;
            try
            {
                result = await _backend.RunAsync(request, new InlineProgress(OnPipelineEvent), cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 中核は例外を投げない約束だが、入口の不具合でも「動いたまま」にしない
                result = ex is OperationCanceledException && cancellation.IsCancellationRequested
                    ? new AndroidPipelineResult { Canceled = true }
                    : new AndroidPipelineResult { FailureKind = AndroidFailureKind.Build, FailureMessage = $"予期しないエラー（{ex.GetType().Name}）: {ex.Message}" };
            }
            StopMonitor();

            AndroidRunCompletion completion;
            string? serial;
            string? applicationId;
            lock (_gate)
            {
                completion = _machine.Complete(result);
                serial = _machine.Serial;
                applicationId = _machine.ApplicationId;
                _runCancellation = null;
            }
            RaiseStateChanged();

            string? stopAppError = null;
            if (completion.NeedsStopApp && serial is not null && applicationId is not null)
            {
                stopAppError = await StopAppAsync(serial, applicationId).ConfigureAwait(false);
                lock (_gate) _machine.FinishStopApp();
                RaiseStateChanged();
            }
            foreach (var line in AndroidRunOutputFormatter.Completed(completion, stopAppError)) Emit(line);
        }
        finally
        {
            done.TrySetResult();
        }
    }

    /// <summary>
    /// 端末のアプリを止める（実行の合図とは別の、時間切れ付きの新しい合図で）。
    /// </summary>
    /// <returns>止められなかった理由（止めたら null）。</returns>
    private async Task<string?> StopAppAsync(string serial, string applicationId)
    {
        using var timeout = new CancellationTokenSource(_timings.StopAppTimeout);
        try
        {
            await _backend.StopAppAsync(serial, applicationId, timeout.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            return $"am force-stop が {_timings.StopAppTimeout.TotalSeconds:F0} 秒以内に終わりませんでした";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>パイプラインのイベント（パイプラインのスレッド・子プロセスの出力を読むスレッドから同期に届く）。</summary>
    private void OnPipelineEvent(AndroidPipelineEvent pipelineEvent)
    {
        bool enteredRunning;
        string? targetText;
        lock (_gate)
        {
            enteredRunning = _machine.Apply(pipelineEvent);
            targetText = _machine.TargetText;
        }
        foreach (var line in AndroidRunOutputFormatter.Format(pipelineEvent)) Emit(line);
        if (enteredRunning)
        {
            Emit(AndroidRunOutputFormatter.Launched(targetText));
            StartMonitor();
        }
        // ログの行は状態を変えない（数が多いので UI へ知らせない）
        if (pipelineEvent is not AndroidLogLine) RaiseStateChanged();
    }

    // ── アプリの見張り（pidof）─────────────────────────────────

    /// <summary>アプリの見張りを始める（Running に入ったとき。アプリ ID と端末が分かっているときだけ）。</summary>
    private void StartMonitor()
    {
        string serial;
        string applicationId;
        CancellationTokenSource monitor;
        lock (_gate)
        {
            if (_machine.Phase != AndroidRunPhase.Running || _machine.Serial is null || _machine.ApplicationId is null) return;
            serial = _machine.Serial;
            applicationId = _machine.ApplicationId;
            monitor = new CancellationTokenSource();
            _monitorCancellation = monitor;
        }
        _ = Task.Run(() => MonitorAsync(serial, applicationId, monitor.Token));
    }

    /// <summary>アプリの見張りを止める。</summary>
    private void StopMonitor()
    {
        CancellationTokenSource? monitor;
        lock (_gate)
        {
            monitor = _monitorCancellation;
            _monitorCancellation = null;
        }
        TryCancel(monitor);
    }

    /// <summary>
    /// 数秒おきにアプリが動いているかを確かめ、続けて見つからなければ「アプリが終わった」として実行を止める。
    /// adb の失敗（端末が外れた等）は数えない（そのときは logcat も終わってパイプラインが戻る）。
    /// </summary>
    private async Task MonitorAsync(string serial, string applicationId, CancellationToken cancellationToken)
    {
        var misses = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_timings.AppPollInterval, cancellationToken).ConfigureAwait(false);
                bool running;
                try
                {
                    running = await _backend.IsAppRunningAsync(serial, applicationId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // 分からない（adb の失敗）。数えずに次の確認へ
                    continue;
                }

                misses = running ? 0 : misses + 1;
                if (misses >= _timings.AppExitConfirmations)
                {
                    OnAppExited();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 見張りの終わり（実行が戻った・止めた）
        }
    }

    /// <summary>アプリが終わった: 中断の合図を送る（logcat が止まり、パイプラインが戻る）。</summary>
    private void OnAppExited()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (!_machine.RequestStop(AndroidRunStopReason.AppExited)) return;
            cancellation = _runCancellation;
        }
        RaiseStateChanged();
        Emit(AndroidRunOutputFormatter.Stopping(AndroidRunStopReason.AppExited));
        TryCancel(cancellation);
    }

    // ── 部品 ───────────────────────────────────────────

    /// <summary>中断の合図を送る（破棄済み・送り済みでも失敗させない）。</summary>
    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 既に終わった実行の合図
        }
        catch (AggregateException)
        {
            // 合図に登録された処理（子プロセスの終了）の失敗。中核が終了を確かめる
        }
    }

    /// <summary>状態の変化を知らせる（受け手の例外で段取りを止めない）。</summary>
    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception)
        {
            // 受け手（UI）の不具合で実行の段取りを止めない
        }
    }

    /// <summary>1 行を出す（受け手の例外で段取りを止めない）。</summary>
    private void Emit(AndroidRunOutputLine line)
    {
        try
        {
            OutputWritten?.Invoke(line);
        }
        catch (Exception)
        {
            // 受け手（UI・ログ）の不具合で実行の段取りを止めない
        }
    }

    /// <summary>その場で呼ぶ IProgress（WPF の Progress&lt;T&gt; と違い、同期コンテキストへ送らない）。</summary>
    /// <param name="handler">受け手。</param>
    private sealed class InlineProgress(Action<AndroidPipelineEvent> handler) : IProgress<AndroidPipelineEvent>
    {
        /// <inheritdoc />
        public void Report(AndroidPipelineEvent value) => handler(value);
    }
}
