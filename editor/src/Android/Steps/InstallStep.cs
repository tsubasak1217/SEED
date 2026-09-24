// ============================================================
//  InstallStep.cs — APK を端末へ入れる（adb install -r。アプリのデータは残す）
//
//  入れた後に pm path（base.apk の場所。インストールのたびに変わる）と APK の SHA-256 をプロジェクトの実行状態へ
//  記録する。次回、同じ APK がその場所のまま入っていればインストールを飛ばす（Plan/AndroidBuildPlan.cs）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.State;

namespace SEEDEditor.Android.Steps;

/// <summary>インストール。</summary>
public sealed class InstallStep : IAndroidPipelineStep
{
    /// <summary>adb install の失敗の行頭（"Failure [INSTALL_FAILED_…]"）。</summary>
    private const string FailurePrefix = "Failure";

    /// <summary>1 MiB（大きさの表示用）。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <inheritdoc />
    public AndroidPipelinePhase Phase => AndroidPipelinePhase.Install;

    /// <inheritdoc />
    public async Task<string> RunAsync(
        AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var adb = context.RequireAdb();
        var device = context.RequireDevice();
        var apk = context.Engine.DebugApkPath;
        if (!File.Exists(apk))
        {
            throw new AndroidPipelineException(AndroidFailureKind.DeviceOperation,
                $"APK がありません: {apk}（APK の作成を飛ばさずに実行してください）");
        }
        var sha256 = ApkFile.Sha256(apk, context.Stamps.Apk)!;

        log.Info($"adb -s {device.Serial} install -r {apk}（{new FileInfo(apk).Length / BytesPerMegabyte:F1} MB）");
        var lines = new List<string>();
        var gate = new object();
        var exitCode = await adb.InstallAsync(device.Serial, apk, (stream, line) =>
        {
            log.Process(stream, line);
            lock (gate) lines.Add(line);
        }, cancellationToken).ConfigureAwait(false);
        List<string> snapshot;
        lock (gate) snapshot = lines.ToList();
        if (exitCode != 0 || snapshot.Any(line => line.TrimStart().StartsWith(FailurePrefix, StringComparison.Ordinal)))
        {
            throw new AndroidPipelineException(AndroidFailureKind.DeviceOperation,
                $"adb install が失敗しました（終了コード {exitCode}）: {string.Join(" / ", snapshot)}");
        }

        // 入れた APK と、その場所を記録する（場所はインストールのたびに変わる＝他の人が入れ直せば分かる）
        var installedPath = await adb.GetInstalledApkPathAsync(device.Serial, context.Identity.ApplicationId, cancellationToken).ConfigureAwait(false);
        if (installedPath is not null)
        {
            context.RunState.Installs[device.Serial] = new AndroidInstallStateRecord
            {
                ApplicationId = context.Identity.ApplicationId,
                ApkSha256     = sha256,
                InstalledPath = installedPath,
                InstalledAt   = DateTimeOffset.Now,
            };
        }
        else
        {
            log.Warn($"インストールの後に pm path {context.Identity.ApplicationId} が場所を返しませんでした（次回もインストールします）。");
        }
        return $"{context.Identity.ApplicationId} を {device.DisplayName} へ入れました";
    }
}
