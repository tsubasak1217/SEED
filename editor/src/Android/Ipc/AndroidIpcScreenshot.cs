// ============================================================
//  AndroidIpcScreenshot.cs — 端末のアプリの画面を IPC で撮って PC へ取り出す（段階D-1）
//
//  【流れ】PC の Play の SCREENSHOT（AI ツールの seed_screenshot）と同じ命令をそのまま使う:
//    1. IPC: SCREENSHOT:game,/data/user/0/<アプリ ID>/cache/seed_ipc_screenshot.png
//       → ランタイムが次に描いたフレームを PNG にして端末に書く（runtime/.../app/screenshot_ops.rs）
//       → SCREENSHOT_DONE:{パス},{幅},{高さ} ／ SCREENSHOT_ERROR:{理由}
//    2. adb exec-out run-as <アプリ ID> cat cache/seed_ipc_screenshot.png → PC のファイル
//       （アプリの内部データフォルダは adb の shell から読めないので run-as で読む。デバッグ版の APK だけ）
//    3. 端末の PNG を消す（run-as。自分のアプリのキャッシュだけ）
//  使う側: SeedAndroid の screenshot。エディタの AI ツールからの Android の撮影は持ち越し（docs/backlog.md）。
//
//  WPF に依存しない（SeedAndroid・単体テストからリンクされる）。
// ============================================================

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Ipc;

namespace SEEDEditor.Android.Ipc;

/// <summary>撮れたスクリーンショット。</summary>
/// <param name="LocalPath">PC に書いたファイル。</param>
/// <param name="Width">幅（ピクセル。ランタイムの応答）。</param>
/// <param name="Height">高さ（ピクセル。ランタイムの応答）。</param>
/// <param name="Bytes">ファイルの大きさ（バイト）。</param>
public sealed record AndroidScreenshotResult(string LocalPath, int Width, int Height, long Bytes);

/// <summary>端末のアプリの画面を IPC で撮って PC へ取り出す。</summary>
public static class AndroidIpcScreenshot
{
    /// <summary>SCREENSHOT_DONE の本文（{パス},{幅},{高さ}）の項目の数。</summary>
    private const int DoneFieldCount = 3;

    /// <summary>
    /// 撮って PC のファイルへ書く。
    /// </summary>
    /// <param name="session">端末のアプリとの通信路（その端末の adb で取り出す・消す）。</param>
    /// <param name="applicationId">アプリ ID（検査済みの値）。</param>
    /// <param name="localPath">PC の書き先（フォルダが無ければ作る）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>撮れたもの。</returns>
    /// <exception cref="AndroidIpcException">命令が届かない・応答が無い・ランタイムが撮れなかった。</exception>
    /// <exception cref="AdbCommandException">端末から取り出せなかった。</exception>
    public static async Task<AndroidScreenshotResult> CaptureAsync(
        AndroidIpcSession session, string applicationId, string localPath, CancellationToken cancellationToken)
    {
        var adb = session.Adb;
        var devicePath = DevicePath(applicationId);
        var reply = await session.RequestAsync(
            RuntimeIpcCommands.Screenshot(RuntimeIpcCommands.ScreenshotGameTarget, devicePath),
            RuntimeIpcCommands.IsScreenshotReply,
            cancellationToken).ConfigureAwait(false);
        var (width, height) = ParseReply(reply);

        var bytes = await adb.RunAsReadFileAsync(session.Serial, applicationId, AndroidRuntimeContract.RemoteScreenshotPath, cancellationToken)
            .ConfigureAwait(false);
        var folder = Path.GetDirectoryName(Path.GetFullPath(localPath));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(localPath, bytes, cancellationToken).ConfigureAwait(false);

        // 端末に残さない（消せなくても撮影は成功。次の撮影で上書きされる）
        try
        {
            await adb.RunAsRemoveDirectoryAsync(session.Serial, applicationId, AndroidRuntimeContract.RemoteScreenshotPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AdbCommandException)
        {
            // 消せなかった
        }
        return new AndroidScreenshotResult(Path.GetFullPath(localPath), width, height, bytes.LongLength);
    }

    /// <summary>ランタイムへ渡す、端末の書き先の絶対パス（純粋な処理）。</summary>
    /// <param name="applicationId">アプリ ID。</param>
    /// <returns>絶対パス（/data/user/0/&lt;ID&gt;/cache/…）。</returns>
    public static string DevicePath(string applicationId) =>
        string.Format(CultureInfo.InvariantCulture, AndroidRuntimeContract.AppDataDirFormat, applicationId)
        + "/" + AndroidRuntimeContract.RemoteScreenshotPath;

    /// <summary>
    /// 応答を読む（純粋な処理）。SCREENSHOT_DONE:{パス},{幅},{高さ} なら幅と高さ、SCREENSHOT_ERROR:{理由} なら例外。
    /// </summary>
    /// <param name="reply">応答の行。</param>
    /// <returns>幅と高さ。</returns>
    /// <exception cref="AndroidIpcException">撮れなかった・応答の書式が違う。</exception>
    public static (int Width, int Height) ParseReply(string reply)
    {
        if (reply.StartsWith(RuntimeIpcCommands.ScreenshotErrorPrefix, StringComparison.Ordinal))
        {
            throw new AndroidIpcException(AndroidIpcFailureKind.NoReply,
                $"端末のアプリがスクリーンショットを撮れませんでした: {reply[RuntimeIpcCommands.ScreenshotErrorPrefix.Length..]}");
        }
        var body = reply.StartsWith(RuntimeIpcCommands.ScreenshotDonePrefix, StringComparison.Ordinal)
            ? reply[RuntimeIpcCommands.ScreenshotDonePrefix.Length..]
            : string.Empty;
        // パスにカンマは入れないが、念のため後ろから 2 つ（幅・高さ）を取る
        var fields = body.Split(RuntimeIpcCommands.ArgumentSeparator);
        if (fields.Length >= DoneFieldCount
            && int.TryParse(fields[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            && int.TryParse(fields[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var height))
        {
            return (width, height);
        }
        throw new AndroidIpcException(AndroidIpcFailureKind.NoReply, $"スクリーンショットの応答を読めません: {reply}");
    }
}
