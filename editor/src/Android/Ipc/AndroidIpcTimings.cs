// ============================================================
//  AndroidIpcTimings.cs — Android の IPC の接続・応答の時間の決まり（データ。段階D-1）
//
//  AndroidIpcConnector / AndroidIpcSession が使う。単体テストは短い値に差し替えて素早く確かめる。
//
//  WPF に依存しない（SeedAndroid・単体テストからリンクされる）。
// ============================================================

using System;

namespace SEEDEditor.Android.Ipc;

/// <summary>Android の IPC の時間の決まり。</summary>
/// <param name="ConnectTimeout">つながるまで試し続ける上限（アプリの起動直後はまだ待ち受けていないのでやり直す）。</param>
/// <param name="RetryInterval">つながらなかったときに次を試すまでの間隔。</param>
/// <param name="GreetingTimeout">1 回の試しで挨拶（READY:）を待つ上限（別の接続がつながっていると届かない）。</param>
/// <param name="ReplyTimeout">命令の応答（スクリーンショット等）を待つ上限。</param>
/// <param name="CloseTimeout">切り離し（DETACH）の後、相手が閉じるのを待つ上限（送った行を確実に届けるため）。</param>
public sealed record AndroidIpcTimings(
    TimeSpan ConnectTimeout, TimeSpan RetryInterval, TimeSpan GreetingTimeout, TimeSpan ReplyTimeout, TimeSpan CloseTimeout)
{
    /// <summary>
    /// 既定の接続の上限（秒）。ランタイムは起動の最初（同梱 .NET の展開の直後。初回でも 1 秒前後）に待ち受けを始めるので、
    /// 古い APK（待ち受けない）を「使えない」と判断するまでの時間でもある。
    /// </summary>
    public const double DefaultConnectTimeoutSeconds = 20.0;

    /// <summary>既定のやり直しの間隔（ミリ秒）。</summary>
    public const double DefaultRetryIntervalMilliseconds = 500.0;

    /// <summary>既定の挨拶の待ち（秒）。挨拶は受け付けた直後に書かれるので、ふつうは数ミリ秒で届く。</summary>
    public const double DefaultGreetingTimeoutSeconds = 3.0;

    /// <summary>既定の応答の待ち（秒）。スクリーンショットは次に描いたフレームを PNG にするまで。</summary>
    public const double DefaultReplyTimeoutSeconds = 20.0;

    /// <summary>既定の切り離しの後の待ち（秒）。</summary>
    public const double DefaultCloseTimeoutSeconds = 2.0;

    /// <summary>既定値（エディタ・SeedAndroid が使う）。</summary>
    public static readonly AndroidIpcTimings Default = new(
        TimeSpan.FromSeconds(DefaultConnectTimeoutSeconds),
        TimeSpan.FromMilliseconds(DefaultRetryIntervalMilliseconds),
        TimeSpan.FromSeconds(DefaultGreetingTimeoutSeconds),
        TimeSpan.FromSeconds(DefaultReplyTimeoutSeconds),
        TimeSpan.FromSeconds(DefaultCloseTimeoutSeconds));
}
