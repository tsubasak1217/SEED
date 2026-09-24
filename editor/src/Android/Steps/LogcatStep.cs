// ============================================================
//  LogcatStep.cs — 起動したアプリの logcat を流す（止められるまで、または指定の秒数）
//
//  起点は起動の直前に控えた端末の時刻（起動しなかったときは今）。タグは SEED（エンジン）・DOTNET（C# スクリプトの
//  SEED.Debug.Log）ほか（AndroidRuntimeContract.LogcatFilters）。止める合図は正常な終わり（この工程は成功）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;

namespace SEEDEditor.Android.Steps;

/// <summary>logcat。</summary>
public sealed class LogcatStep : IAndroidPipelineStep
{
    /// <inheritdoc />
    public AndroidPipelinePhase Phase => AndroidPipelinePhase.Logcat;

    /// <inheritdoc />
    public async Task<string> RunAsync(
        AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var adb = context.RequireAdb();
        var device = context.RequireDevice();
        var since = context.LogcatSince ?? await adb.GetLogcatSinceAsync(device.Serial, cancellationToken).ConfigureAwait(false);
        var seconds = context.Request.LogcatSeconds;
        var duration = seconds > 0 ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        log.Info(duration is null
            ? $"logcat を流します（止めるまで。起点 {since}）"
            : $"logcat を {seconds} 秒流します（起点 {since}）");
        if (!string.IsNullOrWhiteSpace(context.Request.LogFile)) log.Info($"保存先: {context.Request.LogFile}（UTF-8）");

        var (lines, exitCode) = await AndroidLogcatSession.RunAsync(
            adb, device.Serial, since, AndroidRuntimeContract.LogcatFilters, duration, context.Request.LogFile, log.Logcat, cancellationToken)
            .ConfigureAwait(false);
        if (exitCode is int code && code != 0) log.Warn($"adb logcat が終了コード {code} で終わりました（端末が外れた等）。");
        return $"{lines} 行";
    }
}
