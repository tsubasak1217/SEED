// ============================================================
//  AppControlCommand.cs — pause / resume / screenshot（動いている端末のアプリへ IPC で 1 命令送る。段階D-1）
//
//  【流れ】（エディタの実行バーと同じ中核 editor/src/Android/Ipc/ を使う。1〜3 は reload と共有の RunningAppConnector）
//    1. アプリ ID を決める（--app-id か --project の設定。TargetApplication）・端末を決める（--serial か使える 1 台）
//    2. 起動の記録（run / push の起動の工程がプロジェクトの cache/android/run_state.json の ipc_launches へ書いた、
//       その端末・そのアプリの IPC のポートと接続トークン）を読む。無ければ「run / push で起動し直して」と伝えて終わる
//    3. adb forward tcp:0 tcp:<端末のポート>（--ipc-port があればそれ、無ければ記録のポート）→ TCP でつなぎ、
//       最初の行で HELLO:<接続トークン> を送って挨拶（READY:）を待つ（トークンが違えば端末が断る＝IPC_DENIED:）
//    4. 命令を 1 つ送る
//         pause      … PAUSE（端末のゲームの時間・物理・スクリプトを止める。画面はゲームのまま）
//         resume     … RESUME
//         screenshot … SCREENSHOT:game,<端末のパス> → 応答を待って run-as で PNG を取り出す（--out。省略時はカレントに日時の名前）
//         snapshot   … SNAPSHOT_SCENE:<端末のパス> → 応答を待って run-as で .scene を取り出す（シーンのいまの状態の写し。
//                      --out。省略時はカレントに日時の名前。エディタの一時停止と同じ中核 Android/Ipc/AndroidIpcSnapshot。§20.17）
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
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Ipc;

namespace SEEDEditor.Tools.SeedAndroid.Commands;

/// <summary>pause / resume / screenshot / snapshot。</summary>
public static class AppControlCommand
{
    /// <summary>--out を省いたときのスクリーンショットのファイル名の書式（{0}=日時）。</summary>
    private const string DefaultScreenshotNameFormat = "android_screenshot_{0}.png";

    /// <summary>--out を省いたときの写しのファイル名の書式（{0}=日時）。</summary>
    private const string DefaultSnapshotNameFormat = "android_snapshot_{0}.scene";

    /// <summary>端末での所要ミリ秒の表示の書式（小数 1 桁）。</summary>
    private const string MillisecondsFormat = "F1";

    /// <summary>全体の所要秒の表示の書式（小数 2 桁）。</summary>
    private const string SecondsFormat = "F2";

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
        // ── 1〜3. アプリ ID・端末・起動の記録（接続トークン）を決めてつなぐ（reload と共有。RunningAppConnector）──
        var (connection, exitCode) = await RunningAppConnector.ConnectAsync(toolchain, line, config, cancellationToken);
        if (connection is null) return exitCode;
        var session = connection.Session;
        var applicationId = connection.ApplicationId;

        try
        {
            return line.Command switch
            {
                SeedAndroidCommand.Pause => SendOne(session, RuntimeIpcCommands.Pause,
                    $"{applicationId} を一時停止しました（{session.Serial}）。切り離しても一時停止のままです（再開は resume）。"),
                SeedAndroidCommand.Resume => SendOne(session, RuntimeIpcCommands.Resume,
                    $"{applicationId} の一時停止を解きました（{session.Serial}）。"),
                SeedAndroidCommand.Snapshot => await SnapshotAsync(session, applicationId, line.OutputPath, cancellationToken),
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

    /// <summary>
    /// シーンのいまの状態の写しを書き出させて PC のファイルへ取り出す（docs/android.md §20.17）。
    /// 所要時間（端末での書き出し・全体）とアクタの数・飛ばした数・メインカメラの位置と向きを出す。
    /// </summary>
    /// <param name="session">通信路。</param>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="outputPath">書き先（null なら カレントに日時の名前）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>終了コード。</returns>
    private static async Task<int> SnapshotAsync(
        AndroidIpcSession session, string applicationId, string? outputPath, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(Environment.CurrentDirectory, string.Format(CultureInfo.InvariantCulture, DefaultSnapshotNameFormat,
                DateTime.Now.ToString(ScreenshotTimestampFormat, CultureInfo.InvariantCulture)))
            : outputPath;
        try
        {
            var result = await AndroidIpcSnapshot.FetchAsync(
                session.Adb, session.Serial, applicationId, session, path, AndroidIpcSnapshot.DefaultReplyTimeout, cancellationToken);
            var reply = result.Reply;
            var camera = reply.Camera is { } pose ? $"メインカメラ {pose.Describe()}" : "メインカメラなし";
            Console.Out.WriteLine(
                $"写しを保存しました: {result.LocalPath}（アクタ {reply.Actors}・飛ばした {reply.Skipped}・{result.Bytes} バイト・" +
                $"端末 {reply.RuntimeMilliseconds.ToString(MillisecondsFormat, CultureInfo.InvariantCulture)} ms・" +
                $"合計 {result.Elapsed.TotalSeconds.ToString(SecondsFormat, CultureInfo.InvariantCulture)} 秒・{camera}）");
            return SeedAndroidExitCodes.Success;
        }
        catch (AndroidIpcException ex)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return SeedAndroidExitCodes.DeviceOperation;
        }
        catch (Exception ex) when (ex is SEEDEditor.Android.Adb.AdbCommandException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"エラー: 写しを取り出せませんでした: {ex.Message}");
            return SeedAndroidExitCodes.DeviceOperation;
        }
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
