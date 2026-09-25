// ============================================================
//  LaunchStep.cs — アプリを止めてから起動し直す（am force-stop → am start -W）
//
//  Activity は singleTask なので、動いたままだと am start は前面へ出すだけで、送り直したアセット・入れ直した .so を
//  読まない。止めるのは自分のアプリ（アプリ ID）だけ。起動の直前に端末の時刻を控え、logcat はそれ以降だけを読む
//  （共用の端末で logcat -c をしない）。
//
//  【起動オプション（段階C-3）】
//  起動するシーン（準備で決めたアセットルートからの相対パス）を am start の extra（--es seed.scene '<パス>'）で渡す。
//  MainActivity が「seed.」で始まる文字列の extra を JSON にまとめてネイティブへ渡し、runtime/android/native の
//  launch.rs が LaunchArgs.scene_path に入れる（pak に無ければ logcat に警告を出して開始シーンで起動する）。
//  毎回アプリを止めてから起動するので、extra は必ず新しいプロセスの onCreate に届く。
//  APK の pak はプロジェクト設定の開始シーン・シーン一覧から参照をたどって作るので、ディスクにあってもどこからも
//  参照されていないシーンは pak に入らない。起動の直前に置き場の pak（今の APK の中身と同じ）を引き、入っていなければ
//  理由と直し方を警告の 1 行で出す（端末も警告して開始シーンで起動する）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Project;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Steps;

/// <summary>起動。</summary>
public sealed class LaunchStep : IAndroidPipelineStep
{
    /// <summary>am start -W の出力のうち結果の要約に使う行の頭（起動の種類と所要時間）。</summary>
    private static readonly string[] SummaryLinePrefixes = { "LaunchState:", "TotalTime:" };

    /// <summary>起動オプションが無いときの extra（空）。</summary>
    private static readonly IReadOnlyList<AdbIntentExtra> NoExtras = Array.Empty<AdbIntentExtra>();

    /// <summary>起動するシーンが pak に入っていないときの警告の書式（{0}=シーン）。</summary>
    private const string SceneNotInPakFormat =
        "シーン {0} は APK の pak に入っていません（pak はプロジェクト設定の開始シーン・シーン一覧から参照をたどって作るため、" +
        "どちらにも無く参照もされていないシーンは入りません）。端末は開始シーンで起動します。" +
        "このシーンから起動するには、プロジェクト設定のシーンマネージャに登録してから実行してください。";

    /// <inheritdoc />
    public AndroidPipelinePhase Phase => AndroidPipelinePhase.Launch;

    /// <inheritdoc />
    public async Task<string> RunAsync(
        AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var adb = context.RequireAdb();
        var device = context.RequireDevice();
        var extras = LaunchExtras(context.LaunchScene);
        WarnIfSceneNotInPak(context, log);
        try
        {
            context.LogcatSince = await adb.GetLogcatSinceAsync(device.Serial, cancellationToken).ConfigureAwait(false);
            await adb.ForceStopAsync(device.Serial, context.Identity.ApplicationId, cancellationToken).ConfigureAwait(false);
            var shown = string.Join(" ", AdbClient.AmStartArguments(context.LaunchComponent, extras).Skip(1));
            log.Info($"{shown}（logcat はこの時刻から: {context.LogcatSince}）");
            var lines = await adb.StartActivityAsync(device.Serial, context.LaunchComponent, extras, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 起動するシーンがディスクにはあるのに APK の pak に入っていなければ、理由と直し方を警告する
    /// （ディスクに無いシーンは準備で警告済み。開発用の --assets-dir はフォルダごと送るので見ない）。
    /// pak を読めなければ何も言わない（判断は端末に任せる）。
    /// </summary>
    /// <param name="context">共有の値。</param>
    /// <param name="log">起動の工程のログ。</param>
    private static void WarnIfSceneNotInPak(AndroidPipelineContext context, AndroidPhaseLog log)
    {
        if (context.LaunchScene is not { } scene || context.Project is not { Mode: AndroidProjectMode.Packaged } project) return;
        if (!AndroidScenePath.ExistsUnder(project.Folder.AssetsRoot, scene)) return;
        var pak = Path.Combine(context.Engine.ApkPackageDir, PackageLayout.PakFileName);
        try
        {
            if (File.Exists(pak) && !PakEntryIndex.Contains(PakEntryIndex.ReadEntryPaths(pak), scene))
            {
                log.Warn(string.Format(SceneNotInPakFormat, scene));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // 確かめられないだけ（端末が確かめて、無ければ警告して開始シーンで起動する）
        }
    }

    /// <summary>
    /// 起動オプションを am start の extra にする（純粋な処理）。今は「起動するシーン」だけ（seed.scene）。
    /// </summary>
    /// <param name="launchScene">起動するシーン（アセットルートからの相対パス。null なら開始シーン＝extra なし）。</param>
    /// <returns>extra の並び。</returns>
    public static IReadOnlyList<AdbIntentExtra> LaunchExtras(string? launchScene) =>
        string.IsNullOrWhiteSpace(launchScene)
            ? NoExtras
            : new[] { new AdbIntentExtra(AndroidRuntimeContract.SceneExtraName, launchScene) };
}
