// ============================================================
//  AndroidRunStateMachine.cs — エディタからの Android の実行の状態遷移（純粋な処理。スレッド安全ではない）
//
//  【遷移】
//    Idle     ──Start──────────────────────────────▶ Building
//    Building ──起動（Launch）の工程が成功─────────▶ Running（アプリの見張りを始める合図を返す）
//    Building ──RequestStop(User)──────────────────▶ Stopping
//    Running  ──RequestStop(User / AppExited)──────▶ Stopping
//    Building / Running / Stopping ──Complete─────▶ Idle（起動後に停止ボタンで止めたときだけ、アプリを止め終えるまで Stopping）
//    Stopping ──FinishStopApp──────────────────────▶ Idle
//  Idle で届いたイベント（パイプラインが戻った後に遅れて届いた行など）は無視する。
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
    };

    /// <summary>
    /// 実行を始める（Idle のときだけ）。
    /// </summary>
    /// <param name="targetText">実行先の表示名。</param>
    /// <param name="serial">端末のシリアル。</param>
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
                Serial = prepared.Device?.Serial ?? Serial;
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
    /// 止める（Building / Running のときだけ。アプリの終了は Running のときだけ）。
    /// </summary>
    /// <param name="reason">理由。</param>
    /// <returns>Stopping に入ったら true（呼び出し側は中断の合図を送る）。</returns>
    public bool RequestStop(AndroidRunStopReason reason)
    {
        if (reason == AndroidRunStopReason.None) return false;
        if (Phase is not (AndroidRunPhase.Building or AndroidRunPhase.Running)) return false;
        if (reason == AndroidRunStopReason.AppExited && Phase != AndroidRunPhase.Running) return false;
        StopReason = reason;
        Phase = AndroidRunPhase.Stopping;
        return true;
    }

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
