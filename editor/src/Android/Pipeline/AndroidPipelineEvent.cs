// ============================================================
//  AndroidPipelineEvent.cs — ビルド・配置・起動の進み具合を呼び出し側へ知らせるイベント
//
//  【受け手】
//  コンソールツール（SeedAndroid の ConsoleEventPrinter）とエディタの Output パネル（段階C-2）が
//  IProgress&lt;AndroidPipelineEvent&gt; で受け取る。子プロセスの標準出力・標準エラーを読むスレッドからも
//  届くので、受け手はスレッド安全にすること（WPF の Progress&lt;T&gt; は UI スレッドへ順に送るのでそのままでよい）。
//
//  【種類】
//    AndroidPhaseStarted  … 工程を始めた（何番目か・理由）
//    AndroidPhaseFinished … 工程を終えた（成功・飛ばした・失敗・中断と所要時間）。飛ばした工程は Started 無しでこれだけ届く
//    AndroidLogLine       … 1 行のログ（説明・子プロセスの出力・警告・エラー・logcat）
//    AndroidProgressChanged … 全体の進み具合（0〜1。工程の数で割る）
//    AndroidPipelineError … 失敗の種類と説明（最後に 1 回）
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;

namespace SEEDEditor.Android.Pipeline;

/// <summary>イベントの共通部分。</summary>
/// <param name="Phase">どの工程のイベントか。</param>
public abstract record AndroidPipelineEvent(AndroidPipelinePhase Phase)
{
    /// <summary>起きた時刻。</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

/// <summary>工程の終わり方。</summary>
public enum AndroidPhaseOutcome
{
    /// <summary>やり終えた。</summary>
    Succeeded,

    /// <summary>飛ばした（変更なし・指定）。</summary>
    Skipped,

    /// <summary>失敗した。</summary>
    Failed,

    /// <summary>中断された。</summary>
    Canceled,
}

/// <summary>ログの種類。</summary>
public enum AndroidLogLevel
{
    /// <summary>このツールの説明。</summary>
    Info,

    /// <summary>子プロセスの標準出力。</summary>
    ProcessOutput,

    /// <summary>子プロセスの標準エラー（cargo の進捗表示もここに出る。エラーとは限らない）。</summary>
    ProcessError,

    /// <summary>警告。</summary>
    Warning,

    /// <summary>エラー。</summary>
    Error,

    /// <summary>端末の logcat の 1 行。</summary>
    Logcat,
}

/// <summary>工程を始めた。</summary>
/// <param name="Phase">工程。</param>
/// <param name="Index">何番目か（1 始まり。計画に載った工程の中で）。</param>
/// <param name="Count">計画に載った工程の数。</param>
/// <param name="Title">表示名。</param>
/// <param name="Reason">行う理由。</param>
public sealed record AndroidPhaseStarted(AndroidPipelinePhase Phase, int Index, int Count, string Title, string Reason)
    : AndroidPipelineEvent(Phase);

/// <summary>工程を終えた。</summary>
/// <param name="Phase">工程。</param>
/// <param name="Index">何番目か（1 始まり）。</param>
/// <param name="Count">計画に載った工程の数。</param>
/// <param name="Title">表示名。</param>
/// <param name="Outcome">終わり方。</param>
/// <param name="Elapsed">所要時間。</param>
/// <param name="Summary">結果の一行説明（飛ばした理由・作ったものの大きさ等）。</param>
public sealed record AndroidPhaseFinished(
    AndroidPipelinePhase Phase, int Index, int Count, string Title, AndroidPhaseOutcome Outcome, TimeSpan Elapsed, string Summary)
    : AndroidPipelineEvent(Phase);

/// <summary>1 行のログ。</summary>
/// <param name="Phase">工程。</param>
/// <param name="Level">種類。</param>
/// <param name="Text">本文。</param>
public sealed record AndroidLogLine(AndroidPipelinePhase Phase, AndroidLogLevel Level, string Text)
    : AndroidPipelineEvent(Phase);

/// <summary>全体の進み具合。</summary>
/// <param name="Phase">いまの工程。</param>
/// <param name="Fraction">0〜1。</param>
/// <param name="Message">説明。</param>
public sealed record AndroidProgressChanged(AndroidPipelinePhase Phase, double Fraction, string Message)
    : AndroidPipelineEvent(Phase);

/// <summary>失敗した（最後に 1 回）。</summary>
/// <param name="Phase">失敗した工程。</param>
/// <param name="Kind">失敗の種類。</param>
/// <param name="Message">説明。</param>
public sealed record AndroidPipelineError(AndroidPipelinePhase Phase, AndroidFailureKind Kind, string Message)
    : AndroidPipelineEvent(Phase);
