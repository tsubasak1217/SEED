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
//  APK の pak はプロジェクト設定の開始シーン・シーン一覧から参照をたどって作り、シーンマネージャに未登録の起動シーンは
//  準備で収録の起点に足す（段階C-4。Project/AndroidPakSceneSeeds → SeedPak --extra-scene）。それでも入っていないのは
//  収録に失敗したか、pak を作り直さなかった（push・--skip-gradle）とき。その保険として、起動の直前に置き場の pak
//  （今の APK の中身と同じ）を引き、入っていなければ理由と直し方を警告の 1 行で出す（端末も警告して開始シーンで起動する）。
//
//  【push した DLL の上書きの解除（段階C-4）】
//  端末はスクリプトの DLL を「内部の files/bin/ → APK の bin/」の順に探すので、push で置いた files/bin/ が残っていると、
//  その後の run で APK を作り直しても古い push の DLL で動く。run（APK の内容を正とする）では、止めた後・起動の前に
//  自分のアプリの files/bin/ を run-as で消す（あったときだけ Output に 1 行）。push と run --push-scripts（これから置く・
//  置いた）、開発用の --assets-dir（APK に bin/ が無く、files/bin/ が唯一の置き場）は消さない（ClearsPushedScripts）。
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

    /// <summary>起動するシーンが pak に入っていないときの警告の書式（{0}=シーン・{1}=理由と直し方）。</summary>
    private const string SceneNotInPakFormat = "シーン {0} が APK の pak に入っていません（{1}）。端末は開始シーンで起動します。";

    /// <summary>理由: 収録に失敗した（起動するシーンは収録の起点に入れている。段階C-4 からの通常の経路）。</summary>
    private const string ReasonCollectionFailed =
        "起動するシーンは pak の収録の起点に入れています〈登録シーン、未登録なら SeedPak の --extra-scene〉が、収録に失敗しました。" +
        "pak とスクリプトの工程のログ（SeedPak の「追加の起点」・欠落の報告）を確かめてください";

    /// <summary>理由: APK の作成を飛ばす指定で、前回の pak のまま。</summary>
    private const string ReasonPakNotRebuilt =
        "APK の作成を飛ばす指定（--skip-gradle）のため前回の pak のままです。指定を外して実行すると、このシーンを収録の起点に足して作り直します";

    /// <summary>理由: push は APK（pak）を作り直さない。</summary>
    private const string ReasonPushKeepsApk =
        "push はスクリプトの DLL だけを送り、APK の pak は作り直しません。run で実行すると、このシーンを収録の起点に足して作り直します";

    /// <summary>push した DLL の上書き（端末の files/bin/）を消したときの Output の 1 行。</summary>
    public const string PushedScriptsClearedMessage =
        "push した DLL の上書きを解除しました（端末の " + AndroidRuntimeContract.RemoteScriptsDir + "/ を消し、APK の bin/ のスクリプトで起動します）。";

    /// <summary>push した DLL の上書きを消せなかったときの警告の書式（{0}=理由）。</summary>
    private const string PushedScriptsNotClearedFormat =
        "push した DLL の上書きを解除できませんでした（端末の " + AndroidRuntimeContract.RemoteScriptsDir + "/ の DLL が APK の bin/ より優先されます）: {0}";

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
            // 止めた後・起動の前に、push で置いた DLL の上書きを消す（run は APK の内容を正とする）
            await ClearPushedScriptsAsync(context, adb, device.Serial, log, cancellationToken).ConfigureAwait(false);
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
    /// run の起動の前に、push で置いた DLL の上書き（自分のアプリの内部 files/bin/）を消す（<see cref="ClearsPushedScripts"/> が
    /// 真のときだけ）。あって消したときだけ Output に 1 行出す。消せなくても起動は続ける（警告を出す。上書きの DLL で動く）。
    /// </summary>
    /// <param name="context">共有の値（指定・プロジェクト・アプリ ID）。</param>
    /// <param name="adb">adb。</param>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="log">起動の工程のログ。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    private static async Task ClearPushedScriptsAsync(
        AndroidPipelineContext context, AdbClient adb, string serial, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        if (!ClearsPushedScripts(context.Request, context.Project)) return;
        try
        {
            var removed = await adb.RunAsRemoveDirectoryAsync(
                serial, context.Identity.ApplicationId, AndroidRuntimeContract.RemoteScriptsDir, cancellationToken).ConfigureAwait(false);
            if (removed) log.Info(PushedScriptsClearedMessage);
        }
        catch (AdbCommandException ex)
        {
            log.Warn(string.Format(PushedScriptsNotClearedFormat, ex.Message));
        }
    }

    /// <summary>
    /// 起動の前に push した DLL の上書き（端末の files/bin/）を消すか（純粋な処理）。run（APK の内容を正とする）で、
    /// APK にスクリプトを入れる（--project）ときだけ消す。push と run --push-scripts はこれから files/bin/ に置く（置いた）ので、
    /// 開発用の --assets-dir は APK に bin/ が無く files/bin/ が唯一のスクリプトの置き場なので、消さない。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <param name="project">プロジェクト（無ければ null）。</param>
    /// <returns>消すなら true。</returns>
    public static bool ClearsPushedScripts(AndroidRunRequest request, AndroidProjectInfo? project) =>
        request.Goal == AndroidRunGoal.Run && !request.PushScripts && project is { Mode: AndroidProjectMode.Packaged };

    /// <summary>
    /// 起動するシーンがディスクにはあるのに APK の pak に入っていなければ、理由と直し方を警告する（段階C-4 からは
    /// 未登録のシーンも収録の起点に足すので、ここに来るのは保険の場面だけ。<see cref="SceneNotInPakReason"/>）。
    /// ディスクに無いシーンは準備で警告済み。開発用の --assets-dir はフォルダごと送るので見ない。
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
                log.Warn(SceneNotInPakMessage(scene, context.Request));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // 確かめられないだけ（端末が確かめて、無ければ警告して開始シーンで起動する）
        }
    }

    /// <summary>
    /// 起動するシーンが pak に入っていないときの警告の文（純粋な処理）。
    /// </summary>
    /// <param name="scene">起動するシーン（アセットルートからの相対パス）。</param>
    /// <param name="request">指定（pak を作り直さない指定かを見る）。</param>
    /// <returns>警告の 1 行。</returns>
    public static string SceneNotInPakMessage(string scene, AndroidRunRequest request) =>
        string.Format(SceneNotInPakFormat, scene, SceneNotInPakReason(request));

    /// <summary>
    /// 起動するシーンが pak に入っていない理由と直し方（push・--skip-gradle は pak を作り直さない。それ以外は収録の失敗）。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <returns>理由と直し方。</returns>
    private static string SceneNotInPakReason(AndroidRunRequest request) =>
        request.Goal == AndroidRunGoal.Push ? ReasonPushKeepsApk
        : request.SkipGradle ? ReasonPakNotRebuilt
        : ReasonCollectionFailed;

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
