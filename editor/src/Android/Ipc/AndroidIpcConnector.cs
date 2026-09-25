// ============================================================
//  AndroidIpcConnector.cs — 端末のアプリの IPC（TCP）へ adb forward 越しにつなぐ（段階D-1）
//
//  【手順】
//    1. adb -s <シリアル> forward tcp:0 tcp:<端末のポート>（PC 側の空きポートは adb が選んで返す）
//    2. 127.0.0.1:<PC 側のポート> へ TCP でつなぎ、最初の 1 行（挨拶 READY:）を待つ
//       - 挨拶が届いた                 → つながった（AndroidIpcSession を返す）
//       - 挨拶の前に閉じられた         → 端末でまだ待ち受けていない（アプリの起動の途中・古い APK）。少し待ってやり直す
//       - 挨拶が来ないまま時間が過ぎた → 別の接続（エディタ・SeedAndroid）がつながっている等。やり直す
//    3. 上限（AndroidIpcTimings.ConnectTimeout）まで 2 をやり直し、つながらなければ理由付きの例外（forward は外す）
//
//  【なぜ挨拶を待つのか】adb forward は端末で誰も待ち受けていなくても PC 側の接続をいったん受け付け、その後に閉じる。
//  そのため「TCP でつながった」だけでは端末のアプリとつながったか分からない（ランタイムの ipc_transport/tcp.rs）。
//
//  WPF に依存しない（SeedAndroid・単体テストからリンクされる）。
// ============================================================

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Processes;
using SEEDEditor.Ipc;

namespace SEEDEditor.Android.Ipc;

/// <summary>1 回の試しの結果。</summary>
internal enum AndroidIpcAttemptOutcome
{
    /// <summary>挨拶が届いた。</summary>
    Connected,

    /// <summary>挨拶の前に閉じられた（端末でまだ待ち受けていない）。</summary>
    ClosedBeforeGreeting,

    /// <summary>挨拶が来ないまま時間が過ぎた（別の接続がつながっている等）。</summary>
    NoGreeting,

    /// <summary>PC 側のポートへつながらなかった（forward が外れた等）。</summary>
    Refused,
}

/// <summary>端末のアプリの IPC へつなぐ。</summary>
public static class AndroidIpcConnector
{
    /// <summary>Debug 出力に付ける名前。</summary>
    private const string ChannelName = "AndroidIpc";

    /// <summary>
    /// adb forward を張って端末のアプリの IPC へつなぐ（挨拶まで確かめる）。
    /// </summary>
    /// <param name="adb">adb。</param>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="devicePort">端末でランタイムが待ち受けているポート。</param>
    /// <param name="timings">時間の決まり。</param>
    /// <param name="cancellationToken">中断の合図（forward は外してから中断を投げる）。</param>
    /// <returns>つながった通信路。</returns>
    /// <exception cref="AndroidIpcException">forward を張れない・時間内につながらない。</exception>
    public static async Task<AndroidIpcSession> ConnectAsync(
        AdbClient adb, string serial, int devicePort, AndroidIpcTimings timings, CancellationToken cancellationToken)
    {
        int localPort;
        try
        {
            localPort = await adb.ForwardTcpAsync(serial, devicePort, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AdbCommandException or ChildProcessStartException)
        {
            throw new AndroidIpcException(AndroidIpcFailureKind.Forward, $"adb forward を張れません: {ex.Message}", ex);
        }

        try
        {
            var (client, channel) = await HandshakeWithRetryAsync(localPort, devicePort, timings, cancellationToken).ConfigureAwait(false);
            return new AndroidIpcSession(adb, serial, localPort, devicePort, client, channel, timings);
        }
        catch
        {
            // つながらなかった・中断した: 自分が張った forward を外す
            await AndroidIpcSession.RemoveForwardQuietlyAsync(adb, serial, localPort).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// forward 済みの PC 側のポートへ、挨拶が届くまでやり直してつなぐ（adb を使わない部分。単体テストはループバックの
    /// 偽のランタイムで確かめる）。
    /// </summary>
    /// <param name="localPort">PC 側のポート。</param>
    /// <param name="devicePort">端末側のポート（失敗の説明用）。</param>
    /// <param name="timings">時間の決まり。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>つながった接続と、受信を始めた行の送受信。</returns>
    /// <exception cref="AndroidIpcException">時間内につながらない。</exception>
    internal static async Task<(TcpClient Client, IpcLineChannel Channel)> HandshakeWithRetryAsync(
        int localPort, int devicePort, AndroidIpcTimings timings, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (outcome, client, channel) = await TryHandshakeAsync(localPort, timings.GreetingTimeout, cancellationToken).ConfigureAwait(false);
            if (outcome == AndroidIpcAttemptOutcome.Connected && client is not null && channel is not null)
            {
                return (client, channel);
            }
            if (watch.Elapsed + timings.RetryInterval > timings.ConnectTimeout)
            {
                throw new AndroidIpcException(KindOf(outcome), DescribeFailure(outcome, devicePort, timings.ConnectTimeout));
            }
            await Task.Delay(timings.RetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 1 回つないで挨拶を待つ。
    /// </summary>
    /// <param name="localPort">PC 側のポート。</param>
    /// <param name="greetingTimeout">挨拶を待つ上限。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>結果（つながったときだけ接続と行の送受信を返す。それ以外は閉じてある）。</returns>
    private static async Task<(AndroidIpcAttemptOutcome Outcome, TcpClient? Client, IpcLineChannel? Channel)> TryHandshakeAsync(
        int localPort, TimeSpan greetingTimeout, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, localPort, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            client.Dispose();
            return (AndroidIpcAttemptOutcome.Refused, null, null);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var channel = new IpcLineChannel(client.GetStream(), ChannelName);
        var greeting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(string line)
        {
            if (RuntimeIpcCommands.IsReady(line)) greeting.TrySetResult(line);
        }

        channel.MessageReceived += OnLine;
        channel.Start();
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var winner = await Task.WhenAny(greeting.Task, channel.Closed, Task.Delay(greetingTimeout, delayCancellation.Token)).ConfigureAwait(false);
        delayCancellation.Cancel();
        channel.MessageReceived -= OnLine;
        if (winner == greeting.Task) return (AndroidIpcAttemptOutcome.Connected, client, channel);

        channel.Dispose();
        client.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        return (winner == channel.Closed ? AndroidIpcAttemptOutcome.ClosedBeforeGreeting : AndroidIpcAttemptOutcome.NoGreeting, null, null);
    }

    /// <summary>最後の試しの結果 → 理由の種類（純粋な処理）。</summary>
    /// <param name="last">最後の試しの結果。</param>
    /// <returns>理由の種類。</returns>
    internal static AndroidIpcFailureKind KindOf(AndroidIpcAttemptOutcome last) => last switch
    {
        AndroidIpcAttemptOutcome.NoGreeting => AndroidIpcFailureKind.NoGreeting,
        AndroidIpcAttemptOutcome.Refused => AndroidIpcFailureKind.Forward,
        _ => AndroidIpcFailureKind.NotListening,
    };

    /// <summary>
    /// つながらなかった理由と、何を確かめればよいか（純粋な処理）。
    /// </summary>
    /// <param name="last">最後の試しの結果。</param>
    /// <param name="devicePort">端末側のポート。</param>
    /// <param name="timeout">試し続けた時間。</param>
    /// <returns>説明。</returns>
    internal static string DescribeFailure(AndroidIpcAttemptOutcome last, int devicePort, TimeSpan timeout)
    {
        var seconds = timeout.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        return last switch
        {
            AndroidIpcAttemptOutcome.NoGreeting =>
                $"端末のアプリ（ポート {devicePort}）は待ち受けていますが {seconds} 秒以内に応答しませんでした。" +
                "別の接続（エディタの実行・SeedAndroid）がつながっている間はつなげません（1 本だけ受け付けます）。",
            AndroidIpcAttemptOutcome.Refused =>
                $"adb forward の PC 側のポートへつながりませんでした（{seconds} 秒）。adb のサーバーが再起動された等。",
            _ =>
                $"端末のアプリがポート {devicePort} で待ち受けていません（{seconds} 秒待ちました）。アプリが起動しているか、" +
                "段階D-1 より前の APK（run で入れ直す）・INTERNET 権限の無い APK でないかを確かめてください。",
        };
    }
}
