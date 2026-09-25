// ============================================================
//  AndroidIpcSnapshot.cs — 端末のシーンの「いまの状態」を IPC で書き出させて PC へ取り出す（写し。docs/android.md §20.17）
//
//  【流れ】（スクリーンショット〈AndroidIpcScreenshot〉と同じ段取り）
//    1. IPC: SNAPSHOT_SCENE:/data/user/0/<アプリ ID>/cache/seed_ipc_snapshot.scene
//       → 端末のランタイムがいまの世界（スクリプトが生成したアクタを含む全アクタ・現在の Transform とコンポーネントの値）を
//         既存のシーン形式で書く（runtime/.../app/scene_snapshot_ops.rs）
//       → SNAPSHOT_DONE:<パス>|actors=…|skipped=…|ms=…|cam=… ／ SNAPSHOT_FAILED:<パス>|<理由>（書式は SceneSnapshotWire）
//    2. adb exec-out run-as <アプリ ID> cat cache/seed_ipc_snapshot.scene → PC のファイル
//       （アプリの内部データフォルダは adb の shell から読めないので run-as で読む。デバッグ版の APK だけ）
//    3. 端末のファイルを消す（run-as。自分のアプリのキャッシュだけ）
//  使う側: エディタの実行バーの一時停止（AndroidRunController → AndroidDeviceActions）と SeedAndroid の snapshot。
//  応答の待ち合わせは IAndroidIpcLink（エディタの実行がつないでいる通信路）の行の受信で行う（AndroidIpcSession 専用にしない）。
//
//  WPF に依存しない（SeedAndroid・単体テストからリンクされる）。
// ============================================================

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.SceneSnapshot;

namespace SEEDEditor.Android.Ipc;

/// <summary>取り出した写し。</summary>
/// <param name="LocalPath">PC に書いたファイル（絶対パス）。</param>
/// <param name="Reply">端末のランタイムの応答（アクタ数・飛ばした数・端末での所要時間・メインカメラ）。</param>
/// <param name="Bytes">ファイルの大きさ（バイト）。</param>
/// <param name="Elapsed">命令を送ってから PC に書き終えるまでの時間。</param>
public sealed record AndroidSceneSnapshotResult(string LocalPath, SceneSnapshotReply Reply, long Bytes, TimeSpan Elapsed);

/// <summary>端末のシーンの写しを IPC で書き出させて PC へ取り出す。</summary>
public static class AndroidIpcSnapshot
{
    /// <summary>
    /// 既定の応答待ち（秒）。端末のランタイムはシーン全体を直列化して書くので、スクリーンショット（20 秒）より長めに待つ
    /// （開発用の debug の .so・大きなシーンでも収まる長さ）。
    /// </summary>
    public const double DefaultReplyTimeoutSeconds = 60.0;

    /// <summary>既定の応答待ち。</summary>
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(DefaultReplyTimeoutSeconds);

    /// <summary>送れなかったときの理由。</summary>
    private const string NotSentReason = "端末のアプリへ命令を送れません（通信路が切れています）";

    /// <summary>応答の前に切れたときの理由。</summary>
    private const string ClosedReason = "応答の前に端末のアプリとの通信路が切れました（アプリが終わった等）";

    /// <summary>応答が来ないときの理由の書式（{0}=秒数）。</summary>
    private const string NoReplyReasonFormat = "端末のアプリが {0:F0} 秒以内に写しを書き出しませんでした（アプリが背面にある・画面が消えている等）";

    /// <summary>
    /// 写しを書き出させて PC のファイルへ取り出す。
    /// </summary>
    /// <param name="adb">端末への adb。</param>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="applicationId">アプリ ID（検査済みの値）。</param>
    /// <param name="link">端末のアプリとつながった通信路。</param>
    /// <param name="localPath">PC の書き先（フォルダが無ければ作る。あれば上書き）。</param>
    /// <param name="replyTimeout">応答待ちの上限。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>取り出した写し。</returns>
    /// <exception cref="AndroidIpcException">命令が届かない・応答が無い・端末が書き出せなかった。</exception>
    /// <exception cref="AdbCommandException">端末から取り出せなかった。</exception>
    public static async Task<AndroidSceneSnapshotResult> FetchAsync(
        AdbClient adb, string serial, string applicationId, IAndroidIpcLink link, string localPath,
        TimeSpan replyTimeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var line = await RequestAsync(link, SceneSnapshotWire.SnapshotScene(DevicePath(applicationId)), replyTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!SceneSnapshotReply.TryParse(line, out var reply, out var failure))
        {
            throw new AndroidIpcException(AndroidIpcFailureKind.NoReply, $"端末のアプリが写しを書き出せませんでした: {failure}");
        }

        var bytes = await adb.RunAsReadFileAsync(serial, applicationId, AndroidRuntimeContract.RemoteSnapshotPath, cancellationToken)
            .ConfigureAwait(false);
        var fullPath = Path.GetFullPath(localPath);
        var folder = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken).ConfigureAwait(false);

        // 端末に残さない（消せなくても取り出しは成功。次の写しで上書きされる）
        try
        {
            await adb.RunAsRemoveDirectoryAsync(serial, applicationId, AndroidRuntimeContract.RemoteSnapshotPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AdbCommandException)
        {
            // 消せなかった
        }
        stopwatch.Stop();
        return new AndroidSceneSnapshotResult(fullPath, reply!, bytes.LongLength, stopwatch.Elapsed);
    }

    /// <summary>ランタイムへ渡す、端末の書き先の絶対パス（純粋な処理）。</summary>
    /// <param name="applicationId">アプリ ID。</param>
    /// <returns>絶対パス（/data/user/0/&lt;ID&gt;/cache/seed_ipc_snapshot.scene）。</returns>
    public static string DevicePath(string applicationId) =>
        string.Format(CultureInfo.InvariantCulture, AndroidRuntimeContract.AppDataDirFormat, applicationId)
        + "/" + AndroidRuntimeContract.RemoteSnapshotPath;

    /// <summary>
    /// 命令を送り、写しの応答（SNAPSHOT_DONE / SNAPSHOT_FAILED）を待つ。応答を取りこぼさないよう、送る前に受け手を付ける。
    /// </summary>
    /// <exception cref="AndroidIpcException">送れない・応答の前に切れた・時間内に応答が無い。</exception>
    private static async Task<string> RequestAsync(
        IAndroidIpcLink link, string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(string line)
        {
            if (SceneSnapshotWire.IsSnapshotReply(line)) reply.TrySetResult(line);
        }

        link.MessageReceived += OnLine;
        try
        {
            if (!link.Send(command)) throw new AndroidIpcException(AndroidIpcFailureKind.Disconnected, NotSentReason);
            var winner = await Task.WhenAny(reply.Task, link.Closed, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            // 応答の直後に閉じられたときは、どちらの続きが先に走るかがスレッドの都合で入れ替わるので、応答が届いているかを先に見る
            if (reply.Task.IsCompletedSuccessfully) return reply.Task.Result;
            cancellationToken.ThrowIfCancellationRequested();
            throw winner == link.Closed
                ? new AndroidIpcException(AndroidIpcFailureKind.Disconnected, ClosedReason)
                : new AndroidIpcException(AndroidIpcFailureKind.NoReply,
                    string.Format(CultureInfo.InvariantCulture, NoReplyReasonFormat, timeout.TotalSeconds));
        }
        finally
        {
            link.MessageReceived -= OnLine;
        }
    }
}
