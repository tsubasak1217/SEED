// ============================================================
//  InstallStep.cs — APK を端末へ入れる（adb install -r。アプリのデータは残す）
//
//  入れた後に pm path（base.apk の場所。インストールのたびに変わる）と APK の SHA-256 をプロジェクトの実行状態へ
//  記録する。次回、同じ APK がその場所のまま入っていればインストールを飛ばす（Plan/AndroidBuildPlan.cs）。
//
//  【上書きの解除】adb install -r はアプリのデータを残すので、push で置いた DLL（files/bin/）と差し替えで送ったアセット
//  （files/assets/）は入れ直しても残り、新しい APK の中身より優先される。入れ直したら、run の起動の前と同じ規則で消す
//  （あったときだけ Output に 1 行。PushedOverrides の AfterInstall。docs/android.md §17.7・§23.4）。インストールを飛ばした
//  （同じ APK が入っている）ときは消さない。
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

    /// <summary>署名の違う同じアプリ ID を上書きしようとしたときの失敗の印（段階D）。</summary>
    private const string UpdateIncompatibleCode = "INSTALL_FAILED_UPDATE_INCOMPATIBLE";

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
        // 開発用はデバッグ版の APK、配布用（release）はアップロード鍵で署名した APK（AAB は入れられない。指定の検査で弾いてある。段階D）
        var apk = context.ArtifactPath;
        if (!File.Exists(apk))
        {
            throw new AndroidPipelineException(AndroidFailureKind.DeviceOperation,
                $"APK がありません: {apk}（APK の作成を飛ばさずに実行してください）");
        }
        var sha256 = ApkFile.Sha256(apk, context.Stamps.ArtifactFor(context.GradleKey))!;

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
            // 署名の違う同じ ID のアプリ（開発用とデバッグ用の鍵・配布用とアップロード鍵）は上書きできない。データが消えるので
            // 勝手にアンインストールはしない（段階D）
            var signatureMismatch = snapshot.Any(line => line.Contains(UpdateIncompatibleCode, StringComparison.Ordinal));
            throw new AndroidPipelineException(AndroidFailureKind.DeviceOperation,
                $"adb install が失敗しました（終了コード {exitCode}）: {string.Join(" / ", snapshot)}" +
                (signatureMismatch
                    ? $"。端末に署名の違う {context.Identity.ApplicationId} が入っています（開発用＝デバッグ用の鍵と配布用＝アップロード鍵は共存できません）。" +
                      $"入れ替えるなら、端末のデータが消えてよいことを確かめてから adb uninstall {context.Identity.ApplicationId} してください。"
                    : string.Empty));
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

        // 入れ直した APK の中身を正とし、push・差し替えで置いた上書きを消す（アプリのデータは install -r で残るため）
        await PushedOverrides.ClearAsync(
            PushedOverrideScope.For(context, device.Serial, PushedOverrideMoment.AfterInstall),
            PushedOverrides.RunAs(adb, device.Serial, context.Identity.ApplicationId), log, cancellationToken).ConfigureAwait(false);
        return $"{context.Identity.ApplicationId} を {device.DisplayName} へ入れました";
    }
}
