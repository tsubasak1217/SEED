// ============================================================
//  AndroidPhaseLog.cs — 1 つの工程のログを IProgress へ流す小さな窓口
//
//  工程の中の処理は「どの工程のログか」を毎回書かずに済むよう、工程に結び付いたこの窓口へ書く。
//  子プロセスの出力は 2 本の読み取りスレッドから届くが、IProgress.Report を呼ぶだけなので状態は持たない
//  （受け手のスレッド安全性は AndroidPipelineEvent.cs のとおり）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using SEEDEditor.Android.Processes;

namespace SEEDEditor.Android.Pipeline;

/// <summary>1 つの工程のログの窓口。</summary>
public sealed class AndroidPhaseLog
{
    /// <summary>送り先（null なら捨てる）。</summary>
    private readonly IProgress<AndroidPipelineEvent>? _progress;

    /// <summary>工程。</summary>
    public AndroidPipelinePhase Phase { get; }

    /// <summary>工程と送り先を指定して作る。</summary>
    /// <param name="phase">工程。</param>
    /// <param name="progress">送り先。</param>
    public AndroidPhaseLog(AndroidPipelinePhase phase, IProgress<AndroidPipelineEvent>? progress)
    {
        Phase = phase;
        _progress = progress;
    }

    /// <summary>説明を 1 行出す。</summary>
    /// <param name="text">本文。</param>
    public void Info(string text) => Write(AndroidLogLevel.Info, text);

    /// <summary>警告を 1 行出す。</summary>
    /// <param name="text">本文。</param>
    public void Warn(string text) => Write(AndroidLogLevel.Warning, text);

    /// <summary>エラーを 1 行出す。</summary>
    /// <param name="text">本文。</param>
    public void Error(string text) => Write(AndroidLogLevel.Error, text);

    /// <summary>logcat の 1 行を出す。</summary>
    /// <param name="text">本文。</param>
    public void Logcat(string text) => Write(AndroidLogLevel.Logcat, text);

    /// <summary>子プロセスの出力の 1 行を出す（ChildProcessRunner のコールバックにそのまま渡せる形）。</summary>
    /// <param name="stream">標準出力か標準エラーか。</param>
    /// <param name="text">本文。</param>
    public void Process(ChildProcessStream stream, string text) =>
        Write(stream == ChildProcessStream.StandardOutput ? AndroidLogLevel.ProcessOutput : AndroidLogLevel.ProcessError, text);

    /// <summary>任意のイベントをそのまま送る。</summary>
    /// <param name="pipelineEvent">イベント。</param>
    public void Report(AndroidPipelineEvent pipelineEvent) => _progress?.Report(pipelineEvent);

    /// <summary>1 行を送る。</summary>
    /// <param name="level">種類。</param>
    /// <param name="text">本文。</param>
    private void Write(AndroidLogLevel level, string text) => _progress?.Report(new AndroidLogLine(Phase, level, text));
}
