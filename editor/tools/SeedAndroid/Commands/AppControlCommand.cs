// ============================================================
//  AppControlCommand.cs — pause / resume / screenshot（動いている端末のアプリへ IPC で 1 命令送る。段階D-1）
//
//  【流れ】（エディタの実行バーと同じ中核 editor/src/Android/Ipc/ を使う）
//    1. アプリ ID を決める（--app-id か --project の設定。TargetApplication）・端末を決める（--serial か使える 1 台）
//    2. 起動の記録（run / push の起動の工程がプロジェクトの cache/android/run_state.json の ipc_launches へ書いた、
//       その端末・そのアプリの IPC のポートと接続トークン）を読む。無ければ「run / push で起動し直して」と伝えて終わる
//    3. adb forward tcp:0 tcp:<端末のポート>（--ipc-port があればそれ、無ければ記録のポート）→ TCP でつなぎ、
//       最初の行で HELLO:<接続トークン> を送って挨拶（READY:）を待つ（トークンが違えば端末が断る＝IPC_DENIED:）
//    4. 命令を 1 つ送る
//         pause      … PAUSE（端末のゲームの時間・物理・スクリプトを止める。画面はゲームのまま）
//         resume     … RESUME
//         screenshot … SCREENSHOT:game,<端末のパス> → 応答を待って run-as で PNG を取り出す（--out。省略時はカレントに日時の名前）
//    5. DETACH（意図した切り離し）を送ってから閉じ、forward を外す
//       → 端末のランタイムは一時停止を据え置く（pause の効果が切断で消えない。黙って切れた場合だけ再開する）
//
//  【前提】端末のアプリが run / push（エディタの実行を含む）で起動したもので、その起動の記録が同じ run_state.json にあること。
//  エディタの実行中はエディタがつながっているので使えない（1 本だけ受け付ける。時間切れで理由を出す）。
// ============================================================

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Ipc;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.State;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Ipc;

namespace SEEDEditor.Tools.SeedAndroid.Commands;

/// <summary>pause / resume / screenshot。</summary>
public static class AppControlCommand
{
    /// <summary>--out を省いたときのスクリーンショットのファイル名の書式（{0}=日時）。</summary>
    private const string DefaultScreenshotNameFormat = "android_screenshot_{0}.png";

    /// <summary>既定のファイル名に入れる日時の書式。</summary>
    private const string ScreenshotTimestampFormat = "yyyyMMdd_HHmmss";

    /// <summary>
    /// 動いている端末のアプリへ命令を 1 つ送る。
    /// </summary>
    /// <param name="toolchain">道具の場所（adb）。</param>
    /// <param name="line">コマンドラインの指定。</param>
    /// <param name="config">設定 JSON の値（無ければ null）。</param>
    /// <param name="cancellationToken">中断の合図（Ctrl+C）。</param>
    /// <returns>終了コード。</returns>
    public static async Task<int> RunAsync(
        AndroidToolchain toolchain, SeedAndroidCommandLine line, AndroidRunRequest? config, CancellationToken cancellationToken)
    {
        var request = SeedAndroidArguments.ToRequest(line, config);
        if (!TargetApplication.TryResolve(line, request, out var applicationId, out var error))
        {
            Console.Error.WriteLine($"エラー: {error}");
            return SeedAndroidExitCodes.InvalidRequest;
        }
        if (request.IpcPort == AndroidIpcSettings.DisabledPort)
        {
            Console.Error.WriteLine($"エラー: {SeedAndroidArguments.IpcPortOption} {AndroidIpcSettings.DisabledPort} では端末のアプリへ命令を送れません。");
            return SeedAndroidExitCodes.InvalidRequest;
        }
        var engine = AndroidEnginePaths.Locate(AppContext.BaseDirectory, Environment.CurrentDirectory);
        if (engine is null)
        {
            Console.Error.WriteLine("エラー: SEED のリポジトリ（runtime/Cargo.toml と runtime/android/gradlew.bat）が見つかりません。リポジトリの中で実行してください。");
            return SeedAndroidExitCodes.Toolchain;
        }

        // ── 端末と、その端末で最後に起動したアプリの IPC の記録（接続トークン）──
        var actions = new AndroidDeviceActions(toolchain);
        var device = await actions.ResolveDeviceAsync(request.Serial, cancellationToken);
        var runStatePath = AndroidRunState.PathFor(AndroidProjectResolver.Resolve(request.ProjectDir, request.AssetsDir), engine);
        var launch = AndroidRunState.Load(runStatePath).IpcLaunchFor(device.Serial, applicationId);
        if (launch is null)
        {
            Console.Error.WriteLine(
                $"エラー: {device.DisplayName} で {applicationId} を起動した記録（接続トークン）がありません（{runStatePath}）。" +
                "同じ --project で run / push を行ってアプリを起動してから使ってください（接続トークンは起動のたびに変わります）。");
            return SeedAndroidExitCodes.DeviceOperation;
        }
        var devicePort = request.IpcPort ?? launch.IpcPort;

        AndroidIpcSession session;
        try
        {
            session = await actions.ConnectIpcAsync(device.Serial, devicePort, launch.IpcToken, null, cancellationToken);
        }
        catch (AndroidIpcException ex)
        {
            Console.Error.WriteLine($"エラー: 端末のアプリ（{applicationId}）とつながりません: {ex.Message}");
            return SeedAndroidExitCodes.DeviceOperation;
        }

        try
        {
            Console.Error.WriteLine($"端末のアプリとつながりました（{session.Serial}・端末のポート {devicePort}・PC 側のポート {session.LocalPort}）。");
            return line.Command switch
            {
                SeedAndroidCommand.Pause => SendOne(session, RuntimeIpcCommands.Pause,
                    $"{applicationId} を一時停止しました（{session.Serial}）。切り離しても一時停止のままです（再開は resume）。"),
                SeedAndroidCommand.Resume => SendOne(session, RuntimeIpcCommands.Resume,
                    $"{applicationId} の一時停止を解きました（{session.Serial}）。"),
                _ => await ScreenshotAsync(session, applicationId, line.OutputPath, cancellationToken),
            };
        }
        finally
        {
            // DETACH を送ってから閉じる（端末は一時停止を据え置く）。forward も外す
            await session.CloseAsync(detach: true);
        }
    }

    /// <summary>命令を 1 つ送る。</summary>
    /// <param name="session">通信路。</param>
    /// <param name="command">命令。</param>
    /// <param name="doneMessage">送れたときに出す 1 行。</param>
    /// <returns>終了コード。</returns>
    private static int SendOne(AndroidIpcSession session, string command, string doneMessage)
    {
        if (!session.Send(command))
        {
            Console.Error.WriteLine($"エラー: {command} を送れませんでした（通信路が切れました）。");
            return SeedAndroidExitCodes.DeviceOperation;
        }
        Console.Out.WriteLine(doneMessage);
        return SeedAndroidExitCodes.Success;
    }

    /// <summary>スクリーンショットを撮って PC のファイルへ書く。</summary>
    /// <param name="session">通信路。</param>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="outputPath">書き先（null なら カレントに日時の名前）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>終了コード。</returns>
    private static async Task<int> ScreenshotAsync(
        AndroidIpcSession session, string applicationId, string? outputPath, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(Environment.CurrentDirectory, string.Format(CultureInfo.InvariantCulture, DefaultScreenshotNameFormat,
                DateTime.Now.ToString(ScreenshotTimestampFormat, CultureInfo.InvariantCulture)))
            : outputPath;
        try
        {
            var result = await AndroidIpcScreenshot.CaptureAsync(session, applicationId, path, cancellationToken);
            Console.Out.WriteLine($"スクリーンショットを保存しました: {result.LocalPath}（{result.Width}x{result.Height}・{result.Bytes} バイト）");
            return SeedAndroidExitCodes.Success;
        }
        catch (AndroidIpcException ex)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return SeedAndroidExitCodes.DeviceOperation;
        }
        catch (Exception ex) when (ex is SEEDEditor.Android.Adb.AdbCommandException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"エラー: スクリーンショットを取り出せませんでした: {ex.Message}");
            return SeedAndroidExitCodes.DeviceOperation;
        }
    }
}
