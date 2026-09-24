// ============================================================
//  LaunchStep.cs — アプリを止めてから起動し直す（am force-stop → am start -W）
//
//  Activity は singleTask なので、動いたままだと am start は前面へ出すだけで、送り直したアセット・入れ直した .so を
//  読まない。止めるのは自分のアプリ（アプリ ID）だけ。起動の直前に端末の時刻を控え、logcat はそれ以降だけを読む
//  （共用の端末で logcat -c をしない）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;

namespace SEEDEditor.Android.Steps;

/// <summary>起動。</summary>
public sealed class LaunchStep : IAndroidPipelineStep
{
    /// <summary>am start -W の出力のうち結果の要約に使う行の頭（起動の種類と所要時間）。</summary>
    private static readonly string[] SummaryLinePrefixes = { "LaunchState:", "TotalTime:" };

    /// <inheritdoc />
    public AndroidPipelinePhase Phase => AndroidPipelinePhase.Launch;

    /// <inheritdoc />
    public async Task<string> RunAsync(
        AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var adb = context.RequireAdb();
        var device = context.RequireDevice();
        try
        {
            context.LogcatSince = await adb.GetLogcatSinceAsync(device.Serial, cancellationToken).ConfigureAwait(false);
            await adb.ForceStopAsync(device.Serial, context.Identity.ApplicationId, cancellationToken).ConfigureAwait(false);
            log.Info($"am start -W -n {context.LaunchComponent}（logcat はこの時刻から: {context.LogcatSince}）");
            var lines = await adb.StartActivityAsync(device.Serial, context.LaunchComponent, cancellationToken).ConfigureAwait(false);
            foreach (var line in lines) log.Info("  " + line);
            var summary = string.Join(" ", lines
                .Select(line => line.Trim())
                .Where(line => SummaryLinePrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal))));
            return summary.Length > 0 ? summary : "起動しました";
        }
        catch (AdbCommandException ex)
        {
            throw new AndroidPipelineException(AndroidFailureKind.DeviceOperation, $"起動できませんでした: {ex.Message}", ex);
        }
    }
}
