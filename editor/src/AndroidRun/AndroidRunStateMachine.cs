// ============================================================
//  AndroidRunStateMachine.cs — エディタからの Android の実行の状態遷移（純粋な処理。スレッド安全ではない）
//
//  【遷移】
//    Idle     ──Start──────────────────────────────▶ Building
//    Building ──起動（Launch）の工程が成功─────────▶ Running（アプリの見張りと IPC の接続を始める合図を返す）
//    Building ──RequestStop(User)──────────────────▶ Stopping
//    Running  ──RequestPause（IPC がつながっている）▶ Paused（段階D-1）
//    Paused   ──RequestResume──────────────────────▶ Running
//    Paused   ──IpcLost（通信路が切れた）───────────▶ Running（端末のランタイムは切断で一時停止を解く）
//    Running / Paused ──RequestStop(User / AppExited)▶ Stopping
//    Building / Running / Paused / Stopping ──Complete▶ Idle（起動後に停止ボタンで止めたときだけ、アプリを止め終えるまで Stopping）
//    Stopping ──FinishStopApp──────────────────────▶ Idle
//  Idle で届いたイベント（パイプラインが戻った後に遅れて届いた行など）は無視する。
//
//  【IPC の状態（AndroidIpcStatus。段階D-1）】Running / Paused の間だけ意味を持つ
//    Off ──BeginIpcConnect──▶ Connecting ──IpcConnected──▶ Connected ──IpcLost──▶ Connecting（つなぎ直す）
//                                   └──IpcFailed（理由）──▶ Unavailable
//    Off のまま（DisableIpc・ポート 0 の指定）なら一時停止は使えない。Start・Complete で Off に戻す。
//
//  【一時停止中の端末のシーンの写し（AndroidPauseSnapshotStatus。docs/android.md §20.17）】Paused の間だけ意味を持つ
//    None ──BeginPauseSnapshot（Paused のとき。回数を進める）──▶ Fetching ──PauseSnapshotFetched──▶ Ready
//                                                                      └──PauseSnapshotFailed──▶ Failed
//    Paused を出る（RequestResume・IpcLost・RequestStop・Complete・Start）と None へ戻す。
//    結果は回数（Generation）で照合し、再開した後に遅れて届いた古い結果は捨てる。
//
//  【終わり方（AndroidRunOutcome）の決め方】止める理由を優先し、無ければパイプラインの結果で決める。
//    停止ボタン: 起動の工程に入っていれば StoppedByUser（アプリも止める）、入る前なら BuildCanceled（アプリに触らない）
//    アプリの終了: AppExited ／ エディタを閉じる: Canceled
//    止めていない: 成功（logcat が自分で終わった）＝ LogcatEnded、中断 ＝ Canceled、それ以外 ＝ Failed
//
//  呼び出し側（AndroidRunController）がロックの中で使う。子プロセス・端末には触らない。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.SceneSnapshot;

namespace SEEDEditor.AndroidRun;

/// <summary>パイプラインが戻ったときの判断。</summary>
/// <param name="Outcome">終わり方。</param>
/// <param name="NeedsStopApp">続けて端末のアプリを止めるか（起動した後に停止ボタンで止めたとき）。</param>
/// <param name="Result">パイプラインの結果。</param>
public sealed record AndroidRunCompletion(AndroidRunOutcome Outcome, bool NeedsStopApp, AndroidPipelineResult Result);

/// <summary>Android の実行の状態遷移。</summary>
public sealed class AndroidRunStateMachine
{
    /// <summary>進み具合の下限。</summary>
    private const double MinFraction = 0.0;

    /// <summary>進み具合の上限。</summary>
    private const double MaxFraction = 1.0;

    /// <summary>状態。</summary>
    public AndroidRunPhase Phase { get; private set; } = AndroidRunPhase.Idle;

    /// <summary>止める理由。</summary>
    public AndroidRunStopReason StopReason { get; private set; }

    /// <summary>起動（Launch）の工程に入ったか（停止のときにアプリも止めるかの判断に使う）。</summary>
    public bool LaunchStarted { get; private set; }

    /// <summary>実行先の表示名。</summary>
    public string? TargetText { get; private set; }

    /// <summary>端末のシリアル（指定、または準備で決まったもの）。</summary>
    public string? Serial { get; private set; }

    /// <summary>アプリ ID（準備で決まる）。</summary>
    public string? ApplicationId { get; private set; }

    /// <summary>いまの工程の表示名。</summary>
    public string? StepTitle { get; private set; }

    /// <summary>いまの工程が何番目か。</summary>
    public int StepIndex { get; private set; }

    /// <summary>計画に載った工程の数。</summary>
    public int StepCount { get; private set; }

    /// <summary>全体の進み具合（0〜1）。</summary>
    public double Fraction { get; private set; }

    /// <summary>準備の途中の詳細（エミュレータの起動待ち等）。</summary>
    public string? PrepareDetail { get; private set; }

    /// <summary>端末のアプリとの IPC の状態（段階D-1）。</summary>
    public AndroidIpcStatus Ipc { get; private set; } = AndroidIpcStatus.Off;

    /// <summary>IPC の状態の補足（つながらなかった理由・使わない理由）。</summary>
    public string? IpcNote { get; private set; }

    /// <summary>一時停止中の端末のシーンの写しの状態（docs/android.md §20.17）。</summary>
    public AndroidPauseSnapshotState PauseSnapshot { get; private set; } = AndroidPauseSnapshotState.None;

    /// <summary>写しを取り出した回数（一時停止のたびに進む。実行をまたいでも戻さない＝古い結果と取り違えない）。</summary>
    private int _pauseSnapshotGeneration;

    /// <summary>一時停止できるか（実行中で IPC がつながっている）。</summary>
    public bool CanPause => Phase == AndroidRunPhase.Running && Ipc == AndroidIpcStatus.Connected;

    /// <summary>いまの写し。</summary>
    public AndroidRunSnapshot Snapshot => new()
    {
        Phase = Phase,
        TargetText = TargetText,
        Serial = Serial,
        ApplicationId = ApplicationId,
        StepTitle = StepTitle,
        StepIndex = StepIndex,
        StepCount = StepCount,
        Fraction = Fraction,
        StopReason = StopReason,
        PrepareDetail = PrepareDetail,
        Ipc = Ipc,
        IpcNote = IpcNote,
        PauseSnapshot = PauseSnapshot,
    };

    /// <summary>
    /// 実行を始める（Idle のときだけ）。
    /// </summary>
    /// <param name="targetText">実行先の表示名。</param>
    /// <param name="serial">端末のシリアル（Android（自動）のようにまだ決まっていなければ null）。</param>
    /// <returns>始めたら true（動いている途中なら false）。</returns>
    public bool Start(string targetText, string? serial)
    {
        if (Phase != AndroidRunPhase.Idle) return false;
        Phase = AndroidRunPhase.Building;
        StopReason = AndroidRunStopReason.None;
        LaunchStarted = false;
        TargetText = targetText;
        Serial = serial;
        ApplicationId = null;
        StepTitle = null;
        StepIndex = 0;
        StepCount = 0;
        Fraction = MinFraction;
        PrepareDetail = null;
        Ipc = AndroidIpcStatus.Off;
        IpcNote = null;
        PauseSnapshot = AndroidPauseSnapshotState.None;
        return true;
    }

    /// <summary>
    /// パイプラインのイベントを反映する。
    /// </summary>
    /// <param name="pipelineEvent">イベント。</param>
    /// <returns>この反映で Running に入ったら true（呼び出し側はアプリの見張りを始める）。</returns>
    public bool Apply(AndroidPipelineEvent pipelineEvent)
    {
        if (Phase == AndroidRunPhase.Idle) return false;
        switch (pipelineEvent)
        {
            case AndroidPrepared prepared:
                if (prepared.Device is { } device)
                {
                    // 実行を始めてから決まった端末（Android（自動）・選んだ端末が見えずエミュレータへ切り替えた）は、
                    // 進捗・Output の実行先の表示もその端末にする（段階C-3）
                    if (!string.Equals(device.Serial, Serial, StringComparison.Ordinal))
                    {
                        TargetText = RunTargetCatalogBuilder.DeviceText(device);
                    }
                    Serial = device.Serial;
                }
                ApplicationId = prepared.Identity.ApplicationId;
                return false;

            case AndroidPhaseStarted started:
                StepTitle = started.Title;
                StepIndex = started.Index;
                if (started.Count > 0) StepCount = started.Count;
                if (started.Phase == AndroidPipelinePhase.Launch) LaunchStarted = true;
                return false;

            case AndroidProgressChanged progress:
                Fraction = Math.Clamp(progress.Fraction, MinFraction, MaxFraction);
                // 準備の途中の知らせ（エミュレータの起動待ち等）は、工程が始まる前の進捗の文言に使う
                if (progress.Phase == AndroidPipelinePhase.Prepare) PrepareDetail = progress.Message;
                return false;

            case AndroidPhaseFinished finished:
                if (finished.Count > 0) StepCount = finished.Count;
                if (finished.Phase == AndroidPipelinePhase.Launch
                    && finished.Outcome == AndroidPhaseOutcome.Succeeded
                    && Phase == AndroidRunPhase.Building)
                {
                    Phase = AndroidRunPhase.Running;
                    return true;
                }
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// 止める（Building / Running / Paused のときだけ。アプリの終了は Running / Paused のときだけ）。
    /// </summary>
    /// <param name="reason">理由。</param>
    /// <returns>Stopping に入ったら true（呼び出し側は中断の合図を送る）。</returns>
    public bool RequestStop(AndroidRunStopReason reason)
    {
        if (reason == AndroidRunStopReason.None) return false;
        if (Phase is not (AndroidRunPhase.Building or AndroidRunPhase.Running or AndroidRunPhase.Paused)) return false;
        if (reason == AndroidRunStopReason.AppExited && Phase is not (AndroidRunPhase.Running or AndroidRunPhase.Paused)) return false;
        StopReason = reason;
        Phase = AndroidRunPhase.Stopping;
        // 一時停止を出るので写しは使わない（エディタは編集中のシーンへ戻す）
        PauseSnapshot = AndroidPauseSnapshotState.None;
        return true;
    }

    // ── 端末のアプリとの IPC（段階D-1）────────────────────────────

    /// <summary>
    /// IPC の接続を始める（Running / Paused のときだけ。起動の直後・切れた後のつなぎ直し）。
    /// </summary>
    /// <returns>Connecting にしたら true。</returns>
    public bool BeginIpcConnect()
    {
        if (Phase is not (AndroidRunPhase.Running or AndroidRunPhase.Paused)) return false;
        Ipc = AndroidIpcStatus.Connecting;
        IpcNote = null;
        return true;
    }

    /// <summary>
    /// IPC を使わない（ポート 0 の指定で起動オプションを渡していない）。
    /// </summary>
    /// <param name="reason">使わない理由（実行ボタンのツールチップに出す）。</param>
    public void DisableIpc(string reason)
    {
        Ipc = AndroidIpcStatus.Off;
        IpcNote = reason;
    }

    /// <summary>
    /// IPC がつながった（Running のときだけ受け付ける。止めている途中・止めた後に遅れて届いた接続は捨てさせる）。
    /// </summary>
    /// <returns>受け付けたら true（false なら呼び出し側は接続を閉じる）。</returns>
    public bool IpcConnected()
    {
        if (Phase != AndroidRunPhase.Running || Ipc != AndroidIpcStatus.Connecting) return false;
        Ipc = AndroidIpcStatus.Connected;
        IpcNote = null;
        return true;
    }

    /// <summary>
    /// IPC がつながらなかった（Running / Paused のときだけ）。
    /// </summary>
    /// <param name="reason">理由（実行ボタンのツールチップに出す）。</param>
    /// <returns>反映したら true。</returns>
    public bool IpcFailed(string reason)
    {
        if (Phase is not (AndroidRunPhase.Running or AndroidRunPhase.Paused) || Ipc != AndroidIpcStatus.Connecting) return false;
        Ipc = AndroidIpcStatus.Unavailable;
        IpcNote = reason;
        return true;
    }

    /// <summary>
    /// つながっていた IPC が切れた（Running / Paused のときだけ）。端末のランタイムは黙って切れると一時停止を解くので
    /// Paused なら Running へ戻す。呼び出し側はアプリが動いているかを確かめ、動いていればつなぎ直す（Connecting のまま）。
    /// </summary>
    /// <returns>反映したら true（一時停止していたかは戻り値の前に Phase を見る）。</returns>
    public bool IpcLost()
    {
        if (Phase is not (AndroidRunPhase.Running or AndroidRunPhase.Paused) || Ipc != AndroidIpcStatus.Connected) return false;
        Phase = AndroidRunPhase.Running;
        Ipc = AndroidIpcStatus.Connecting;
        IpcNote = null;
        // 端末は切断で一時停止を解いたので、写しはもう「いまの端末の状態」ではない
        PauseSnapshot = AndroidPauseSnapshotState.None;
        return true;
    }

    /// <summary>
    /// 一時停止にする（Running で IPC がつながっているときだけ。呼び出し側は PAUSE を送り、送れなければ <see cref="RequestResume"/> で戻す）。
    /// </summary>
    /// <returns>Paused にしたら true。</returns>
    public bool RequestPause()
    {
        if (!CanPause) return false;
        Phase = AndroidRunPhase.Paused;
        return true;
    }

    /// <summary>
    /// 一時停止を解く（Paused のときだけ。呼び出し側は RESUME を送る）。
    /// </summary>
    /// <returns>Running にしたら true。</returns>
    public bool RequestResume()
    {
        if (Phase != AndroidRunPhase.Paused) return false;
        Phase = AndroidRunPhase.Running;
        // 再開したら写しは使わない（エディタは編集中のシーンへ戻す）
        PauseSnapshot = AndroidPauseSnapshotState.None;
        return true;
    }

    // ── 一時停止中の端末のシーンの写し（docs/android.md §20.17）──────────────

    /// <summary>
    /// 写しの取り出しを始める（Paused のときだけ。回数を進めて Fetching にする）。
    /// </summary>
    /// <returns>この取り出しの回数（Paused でなければ null）。</returns>
    public int? BeginPauseSnapshot()
    {
        if (Phase != AndroidRunPhase.Paused) return null;
        var generation = ++_pauseSnapshotGeneration;
        PauseSnapshot = new AndroidPauseSnapshotState(AndroidPauseSnapshotStatus.Fetching, generation, null, null, null);
        return generation;
    }

    /// <summary>
    /// 写しを取り出せた（同じ回数の取り出しの途中で、まだ Paused のときだけ受け付ける）。
    /// </summary>
    /// <param name="generation">取り出しの回数（<see cref="BeginPauseSnapshot"/> の戻り値）。</param>
    /// <param name="localPath">PC に写したファイル。</param>
    /// <param name="camera">端末のメインカメラの位置と向き（無ければ null）。</param>
    /// <returns>反映したら true（再開した後に遅れて届いた等は false）。</returns>
    public bool PauseSnapshotFetched(int generation, string localPath, SceneSnapshotCameraPose? camera)
    {
        if (!IsFetching(generation)) return false;
        PauseSnapshot = new AndroidPauseSnapshotState(AndroidPauseSnapshotStatus.Ready, generation, localPath, camera, null);
        return true;
    }

    /// <summary>
    /// 写しを取り出せなかった（同じ回数の取り出しの途中で、まだ Paused のときだけ受け付ける）。一時停止は続ける。
    /// </summary>
    /// <param name="generation">取り出しの回数。</param>
    /// <param name="reason">理由（Output に出す）。</param>
    /// <returns>反映したら true。</returns>
    public bool PauseSnapshotFailed(int generation, string reason)
    {
        if (!IsFetching(generation)) return false;
        PauseSnapshot = new AndroidPauseSnapshotState(AndroidPauseSnapshotStatus.Failed, generation, null, null, reason);
        return true;
    }

    /// <summary>その回数の取り出しの途中で、まだ一時停止しているか。</summary>
    private bool IsFetching(int generation) =>
        Phase == AndroidRunPhase.Paused
        && PauseSnapshot.Status == AndroidPauseSnapshotStatus.Fetching
        && PauseSnapshot.Generation == generation;

    /// <summary>
    /// パイプラインが戻ったことを反映し、終わり方と「アプリも止めるか」を決める。
    /// アプリを止めるなら Stopping のまま（<see cref="FinishStopApp"/> で Idle）、それ以外は Idle。
    /// </summary>
    /// <param name="result">パイプラインの結果。</param>
    /// <returns>判断。</returns>
    public AndroidRunCompletion Complete(AndroidPipelineResult result)
    {
        var outcome = DecideOutcome(result);
        var needsStopApp = outcome == AndroidRunOutcome.StoppedByUser && Serial is not null && ApplicationId is not null;
        Phase = needsStopApp ? AndroidRunPhase.Stopping : AndroidRunPhase.Idle;
        // 実行が終わったら IPC は使わない（呼び出し側が通信路を閉じ、forward を外す）
        Ipc = AndroidIpcStatus.Off;
        IpcNote = null;
        PauseSnapshot = AndroidPauseSnapshotState.None;
        return new AndroidRunCompletion(outcome, needsStopApp, result);
    }

    /// <summary>アプリを止め終えた（成功・失敗どちらでも Idle へ）。</summary>
    public void FinishStopApp()
    {
        if (Phase == AndroidRunPhase.Stopping) Phase = AndroidRunPhase.Idle;
    }

    /// <summary>終わり方を決める（止める理由を優先し、無ければパイプラインの結果）。</summary>
    /// <param name="result">パイプラインの結果。</param>
    /// <returns>終わり方。</returns>
    private AndroidRunOutcome DecideOutcome(AndroidPipelineResult result) => StopReason switch
    {
        AndroidRunStopReason.User      => LaunchStarted ? AndroidRunOutcome.StoppedByUser : AndroidRunOutcome.BuildCanceled,
        AndroidRunStopReason.AppExited => AndroidRunOutcome.AppExited,
        AndroidRunStopReason.Shutdown  => AndroidRunOutcome.Canceled,
        _ => result.Succeeded ? AndroidRunOutcome.LogcatEnded
            : result.Canceled ? AndroidRunOutcome.Canceled
            : AndroidRunOutcome.Failed,
    };
}
