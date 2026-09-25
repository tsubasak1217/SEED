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
//  段階D-1 から、エディタとの IPC を待ち受けるポート（--es seed.ipc_port '<ポート>'。0 の指定なら渡さない）と、
//  起動ごとの使い捨ての接続トークン（--es seed.ipc_token '<トークン>'。表示では伏せる）も渡す。端末のランタイムが
//  127.0.0.1:<ポート> で待ち受け、エディタ／SeedAndroid が adb forward 越しにつないで最初の行でトークンを示す（Ipc/）。
//  起動の直後にポートとトークンを実行状態（run_state.json の ipc_launches）へ書く（SeedAndroid の pause 等が使う）。
//  APK の pak はプロジェクト設定の開始シーン・シーン一覧から参照をたどって作り、シーンマネージャに未登録の起動シーンは
//  準備で収録の起点に足す（段階C-4。Project/AndroidPakSceneSeeds → SeedPak --extra-scene）。それでも入っていないのは
//  収録に失敗したか、pak を作り直さなかった（push・--skip-gradle）とき。その保険として、起動の直前に置き場の pak
//  （今の APK の中身と同じ）を引き、入っていなければ理由と直し方を警告の 1 行で出す（端末も警告して開始シーンで起動する）。
//
//  【端末に置いた上書きの解除（段階C-4・実行中の差し替え。docs/android.md §17.7・§23.4）】
//  push で置いた DLL（files/bin/）と差し替えで送ったアセット（files/assets/）は APK の中身より優先されるので、run（APK の
//  内容を正とする。--project）では止めた後・起動の前に消す（あったときだけ Output に 1 行）。APK を入れ直した直後
//  （インストールの工程）も同じ規則で消す。規則・消し方・上書き層の記録の作り直しは PushedOverrides（BeforeLaunch）。
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
using SEEDEditor.Android.Ipc;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.State;
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

    /// <inheritdoc />
    public AndroidPipelinePhase Phase => AndroidPipelinePhase.Launch;

    /// <inheritdoc />
    public async Task<string> RunAsync(
        AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var adb = context.RequireAdb();
        var device = context.RequireDevice();
        var extras = LaunchExtras(context.LaunchScene, context.IpcDevicePort, context.IpcToken);
        WarnIfSceneNotInPak(context, log);
        try
        {
            context.LogcatSince = await adb.GetLogcatSinceAsync(device.Serial, cancellationToken).ConfigureAwait(false);
            await adb.ForceStopAsync(device.Serial, context.Identity.ApplicationId, cancellationToken).ConfigureAwait(false);
            // 止めた後・起動の前に、push で置いた DLL と差し替えで送ったアセットの上書きを消し、上書き層の記録を今の pak で
            // 作り直す（run は APK の内容を正とする。§17.7・§23.4）
            await PushedOverrides.ClearAsync(
                PushedOverrideScope.For(context, device.Serial, PushedOverrideMoment.BeforeLaunch),
                PushedOverrides.RunAs(adb, device.Serial, context.Identity.ApplicationId), log, cancellationToken).ConfigureAwait(false);
            // 表示では接続トークンの値を伏せる（Output・エディタのログに残さない）
            var shown = string.Join(" ", AdbClient.AmStartArguments(context.LaunchComponent, MaskSecrets(extras)).Skip(1));
            log.Info($"{shown}（logcat はこの時刻から: {context.LogcatSince}）");
            var lines = await adb.StartActivityAsync(device.Serial, context.LaunchComponent, extras, cancellationToken).ConfigureAwait(false);
            foreach (var line in lines) log.Info("  " + line);
            // 起動したアプリの IPC のポートと接続トークンを記録する（SeedAndroid の pause 等が使う。起動の直後に書き、
            // 実行の最後まで待たない＝logcat を流している間に別のターミナルから使える）
            RecordIpcLaunch(context, device.Serial, log);
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
    /// 起動オプションを am start の extra にする（純粋な処理）。「起動するシーン」（seed.scene）と、エディタとの IPC を
    /// 待ち受けるポート（seed.ipc_port。段階D-1。値は 10 進の文字列＝Java は文字列の extra だけを渡す）と接続トークン
    /// （seed.ipc_token。ポートと一緒のときだけ）。
    /// </summary>
    /// <param name="launchScene">起動するシーン（アセットルートからの相対パス。null なら開始シーン＝extra なし）。</param>
    /// <param name="ipcDevicePort">IPC のポート（null なら渡さない＝端末は待ち受けない）。</param>
    /// <param name="ipcToken">IPC の接続トークン（ポートがあるときだけ渡す）。</param>
    /// <returns>extra の並び。</returns>
    public static IReadOnlyList<AdbIntentExtra> LaunchExtras(string? launchScene, int? ipcDevicePort = null, string? ipcToken = null)
    {
        var extras = new List<AdbIntentExtra>();
        if (!string.IsNullOrWhiteSpace(launchScene))
        {
            extras.Add(new AdbIntentExtra(AndroidRuntimeContract.SceneExtraName, launchScene));
        }
        if (ipcDevicePort is { } port)
        {
            extras.Add(new AdbIntentExtra(AndroidRuntimeContract.IpcPortExtraName, port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (!string.IsNullOrEmpty(ipcToken))
            {
                extras.Add(new AdbIntentExtra(AndroidRuntimeContract.IpcTokenExtraName, ipcToken));
            }
        }
        return extras.Count == 0 ? NoExtras : extras;
    }

    /// <summary>
    /// 表示用に、接続トークンの値を伏せた extra の並びを作る（純粋な処理。実際に渡すのは元の並び）。
    /// </summary>
    /// <param name="extras">extra の並び。</param>
    /// <returns>伏せた並び。</returns>
    public static IReadOnlyList<AdbIntentExtra> MaskSecrets(IReadOnlyList<AdbIntentExtra> extras) =>
        extras.Select(extra => extra.Key == AndroidRuntimeContract.IpcTokenExtraName ? extra with { Value = AndroidIpcToken.MaskedValue } : extra)
            .ToArray();

    /// <summary>
    /// 起動したアプリの IPC のポートと接続トークンを実行状態（run_state.json の ipc_launches）へ記録して書く（段階D-1）。
    /// IPC を使わない起動なら、その端末の古い記録を消す（前回のトークンでつなぎに行かないように）。書けなくても起動は続ける。
    /// </summary>
    /// <param name="context">共有の値（ポート・トークン・アプリ ID・実行状態）。</param>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="log">起動の工程のログ。</param>
    private static void RecordIpcLaunch(AndroidPipelineContext context, string serial, AndroidPhaseLog log)
    {
        if (context.IpcDevicePort is { } port && context.IpcToken is { } token)
        {
            context.RunState.IpcLaunches[serial] = new AndroidIpcLaunchRecord
            {
                ApplicationId = context.Identity.ApplicationId,
                IpcPort = port,
                IpcToken = token,
                LaunchedAt = DateTimeOffset.Now,
            };
        }
        else if (!context.RunState.IpcLaunches.Remove(serial))
        {
            return;
        }
        try
        {
            context.RunState.Save(context.RunStatePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"IPC の接続トークンを実行状態へ書けません（SeedAndroid の pause 等でつなげません）: {context.RunStatePath}（{ex.Message}）");
        }
    }
}
