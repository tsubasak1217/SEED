// ============================================================
//  RunningAppConnector.cs — 動いている端末のアプリへ IPC でつなぐ（pause / resume / screenshot / reload で共有）
//
//  【流れ】（段階D-1 の AppControlCommand の前半をそのまま切り出した。reload〈docs/android.md §23〉も同じつなぎ方）
//    1. アプリ ID を決める（--app-id か --project の設定。TargetApplication）・端末を決める（--serial か使える 1 台）
//    2. 起動の記録（run / push の起動の工程がプロジェクトの cache/android/run_state.json の ipc_launches へ書いた、
//       その端末・そのアプリの IPC のポートと接続トークン）を読む。無ければ「run / push で起動し直して」と伝えて終わる
//    3. adb forward tcp:0 tcp:<端末のポート>（--ipc-port があればそれ、無ければ記録のポート）→ TCP でつなぎ、
//       最初の行で HELLO:<接続トークン> を送って挨拶（READY:）を待つ（トークンが違えば端末が断る＝IPC_DENIED:）
//  閉じるのは呼び出し側（DETACH を送ってから閉じる＝端末の一時停止を据え置く）。
// ============================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Ipc;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.State;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Tools.SeedAndroid.Commands;

/// <summary>つながった端末のアプリ。</summary>
/// <param name="Session">IPC の通信路（呼び出し側が閉じる）。</param>
/// <param name="ApplicationId">アプリ ID。</param>
/// <param name="Device">端末。</param>
/// <param name="Engine">エンジン側の置き場。</param>
/// <param name="Request">設定 JSON と重ねた指定（プロジェクト）。</param>
/// <param name="DevicePort">端末側のポート。</param>
public sealed record RunningAppConnection(
    AndroidIpcSession Session, string ApplicationId, AdbDevice Device, AndroidEnginePaths Engine, AndroidRunRequest Request, int DevicePort);

/// <summary>動いている端末のアプリへつなぐ。</summary>
public static class RunningAppConnector
{
    /// <summary>
    /// 動いている端末のアプリへつなぐ（つながらなければ理由を標準エラーへ書いて終了コードを返す）。
    /// </summary>
    /// <param name="toolchain">道具の場所（adb）。</param>
    /// <param name="line">コマンドラインの指定。</param>
    /// <param name="config">設定 JSON の値（無ければ null）。</param>
    /// <param name="cancellationToken">中断の合図（Ctrl+C）。</param>
    /// <returns>つながったアプリ（つながらなければ null）と、つながらなかったときの終了コード。</returns>
    public static async Task<(RunningAppConnection? Connection, int ExitCode)> ConnectAsync(
        AndroidToolchain toolchain, SeedAndroidCommandLine line, AndroidRunRequest? config, CancellationToken cancellationToken)
    {
        var request = SeedAndroidArguments.ToRequest(line, config);
        if (!TargetApplication.TryResolve(line, request, out var applicationId, out var error))
        {
            Console.Error.WriteLine($"エラー: {error}");
            return (null, SeedAndroidExitCodes.InvalidRequest);
        }
        if (request.IpcPort == AndroidIpcSettings.DisabledPort)
        {
            Console.Error.WriteLine($"エラー: {SeedAndroidArguments.IpcPortOption} {AndroidIpcSettings.DisabledPort} では端末のアプリへ命令を送れません。");
            return (null, SeedAndroidExitCodes.InvalidRequest);
        }
        var engine = AndroidEnginePaths.Locate(AppContext.BaseDirectory, Environment.CurrentDirectory);
        if (engine is null)
        {
            Console.Error.WriteLine("エラー: SEED のリポジトリ（runtime/Cargo.toml と runtime/android/gradlew.bat）が見つかりません。リポジトリの中で実行してください。");
            return (null, SeedAndroidExitCodes.Toolchain);
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
            return (null, SeedAndroidExitCodes.DeviceOperation);
        }
        var devicePort = request.IpcPort ?? launch.IpcPort;

        try
        {
            var session = await actions.ConnectIpcAsync(device.Serial, devicePort, launch.IpcToken, null, cancellationToken);
            Console.Error.WriteLine($"端末のアプリとつながりました（{session.Serial}・端末のポート {devicePort}・PC 側のポート {session.LocalPort}）。");
            return (new RunningAppConnection(session, applicationId, device, engine, request, devicePort), SeedAndroidExitCodes.Success);
        }
        catch (AndroidIpcException ex)
        {
            Console.Error.WriteLine($"エラー: 端末のアプリ（{applicationId}）とつながりません: {ex.Message}");
            return (null, SeedAndroidExitCodes.DeviceOperation);
        }
    }
}
