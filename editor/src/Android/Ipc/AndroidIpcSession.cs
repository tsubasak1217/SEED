// ============================================================
//  AndroidIpcSession.cs — 端末のアプリとつながった IPC の通信路（adb forward ＋ TCP。段階D-1）
//
//  AndroidIpcConnector が挨拶（READY:）まで確かめてから作る。行の送受信は PC の名前付きパイプと同じ
//  IpcLineChannel（editor/src/Ipc/）で行う（プロトコルの文字列も同じ。RuntimeIpcCommands）。
//
//  【閉じ方】
//    CloseAsync(detach: false) … 黙って閉じる → 端末のアプリは「切れた」とみなし、一時停止中なら再開する
//                                 （エディタの実行を止めた・エディタを閉じた）
//    CloseAsync(detach: true)  … DETACH を送り、送った行が確実に届くよう送り側だけ閉じて（FIN）相手が閉じるのを少し待つ
//                                 → 端末のアプリは一時停止を据え置く（SeedAndroid の pause / resume）
//  どちらも最後に自分が張った adb forward を外す（adb forward --remove tcp:<PC 側のポート>。他の forward には触らない）。
//
//  WPF に依存しない（SeedAndroid・単体テストからリンクされる）。
// ============================================================

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Ipc;

namespace SEEDEditor.Android.Ipc;

/// <summary>端末のアプリとつながった IPC の通信路。</summary>
public sealed class AndroidIpcSession : IAndroidIpcLink, IAsyncDisposable
{
    /// <summary>forward を外す adb の操作の時間切れ（秒）。adb forward --remove はふつう 0.1 秒かからない。</summary>
    private const double RemoveForwardTimeoutSeconds = 10.0;

    /// <summary>adb（forward を外す）。</summary>
    private readonly AdbClient _adb;

    /// <summary>TCP の接続（PC 側の 127.0.0.1:<see cref="LocalPort"/> へ。adb が端末へ中継する）。</summary>
    private readonly TcpClient _client;

    /// <summary>行の送受信。</summary>
    private readonly IpcLineChannel _channel;

    /// <summary>時間の決まり。</summary>
    private readonly AndroidIpcTimings _timings;

    /// <summary>閉じたか（0 / 1）。</summary>
    private int _closing;

    /// <summary>
    /// つながった接続から作る（AndroidIpcConnector が挨拶を確かめてから呼ぶ）。
    /// </summary>
    /// <param name="adb">adb。</param>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="localPort">PC 側のポート（adb forward が割り当てたもの）。</param>
    /// <param name="devicePort">端末側のポート。</param>
    /// <param name="client">TCP の接続。</param>
    /// <param name="channel">受信を始めた行の送受信。</param>
    /// <param name="timings">時間の決まり。</param>
    internal AndroidIpcSession(
        AdbClient adb, string serial, int localPort, int devicePort, TcpClient client, IpcLineChannel channel, AndroidIpcTimings timings)
    {
        _adb = adb;
        Serial = serial;
        LocalPort = localPort;
        DevicePort = devicePort;
        _client = client;
        _channel = channel;
        _timings = timings;
        _channel.MessageReceived += line => MessageReceived?.Invoke(line);
    }

    /// <summary>端末のシリアル。</summary>
    public string Serial { get; }

    /// <summary>PC 側のポート（adb forward tcp:&lt;これ&gt; tcp:&lt;端末側&gt;）。</summary>
    public int LocalPort { get; }

    /// <summary>端末側のポート（ランタイムが待ち受けているもの）。</summary>
    public int DevicePort { get; }

    /// <summary>この通信路の端末への adb（スクリーンショットの取り出し等、同じ端末への操作に使う）。</summary>
    public AdbClient Adb => _adb;

    /// <summary>端末のアプリから 1 行届いた（受信のスレッドから。FPS: 等の通知も届く）。</summary>
    public event Action<string>? MessageReceived;

    /// <inheritdoc />
    public Task Closed => _channel.Closed;

    /// <inheritdoc />
    public bool Send(string command) => Volatile.Read(ref _closing) == 0 && _channel.TrySend(command);

    /// <summary>
    /// 命令を送り、応答の行を待つ（スクリーンショット等）。
    /// </summary>
    /// <param name="command">命令。</param>
    /// <param name="isReply">応答の行か（例 RuntimeIpcCommands.IsScreenshotReply）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>応答の行。</returns>
    /// <exception cref="AndroidIpcException">送れない・応答の前に切れた・時間内に応答が無い。</exception>
    public async Task<string> RequestAsync(string command, Func<string, bool> isReply, CancellationToken cancellationToken)
    {
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(string line)
        {
            if (isReply(line)) reply.TrySetResult(line);
        }

        // 応答を取りこぼさないよう、送る前に受け手を付ける
        MessageReceived += OnLine;
        try
        {
            if (!Send(command))
            {
                throw new AndroidIpcException(AndroidIpcFailureKind.Disconnected, $"端末のアプリへ命令を送れません（通信路が切れています）: {command}");
            }
            var winner = await Task.WhenAny(reply.Task, Closed, Task.Delay(_timings.ReplyTimeout, cancellationToken)).ConfigureAwait(false);
            if (winner == reply.Task) return await reply.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw winner == Closed
                ? new AndroidIpcException(AndroidIpcFailureKind.Disconnected, "応答の前に端末のアプリとの通信路が切れました（アプリが終わった等）。")
                : new AndroidIpcException(AndroidIpcFailureKind.NoReply,
                    $"端末のアプリが {_timings.ReplyTimeout.TotalSeconds:F0} 秒以内に応答しませんでした（アプリが背面にある・止まっている等）。");
        }
        finally
        {
            MessageReceived -= OnLine;
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(bool detach)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        if (detach && _channel.TrySend(RuntimeIpcCommands.Detach))
        {
            // 送った DETACH を確実に届けるため、送り側だけ閉じて（FIN）相手が閉じるのを少し待つ
            // （いきなり閉じると、読んでいない行が残った接続は RST になり、中継する adb が DETACH を捨てることがある）
            try
            {
                _client.Client.Shutdown(SocketShutdown.Send);
                await Task.WhenAny(_channel.Closed, Task.Delay(_timings.CloseTimeout)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // 既に切れていた
            }
        }
        _channel.Dispose();
        _client.Dispose();
        await RemoveForwardQuietlyAsync(_adb, Serial, LocalPort).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await CloseAsync(detach: false).ConfigureAwait(false);

    /// <summary>
    /// 自分が張った adb forward を外す（端末が外れた等で外せなくても例外にしない。adb のサーバーが止まれば消える）。
    /// </summary>
    /// <param name="adb">adb。</param>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="localPort">PC 側のポート。</param>
    internal static async Task RemoveForwardQuietlyAsync(AdbClient adb, string serial, int localPort)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(RemoveForwardTimeoutSeconds));
        try
        {
            await adb.RemoveForwardAsync(serial, localPort, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 外せなくても続ける（端末が外れていれば adb が forward ごと消している）
        }
    }
}
