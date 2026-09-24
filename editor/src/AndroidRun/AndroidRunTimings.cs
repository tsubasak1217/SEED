// ============================================================
//  AndroidRunTimings.cs — エディタの Android の実行の「見張り・停止」の時間の決まり（データ）
//
//  AndroidRunController が使う。単体テストは短い値に差し替えて、アプリの終了の検知・停止の段取りを素早く確かめる。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;

namespace SEEDEditor.AndroidRun;

/// <summary>見張り・停止の時間の決まり。</summary>
/// <param name="AppPollInterval">アプリが動いているか（pidof）を確かめる間隔。</param>
/// <param name="AppExitConfirmations">何回続けて見つからなければ「終わった」とみなすか（一時的な取りこぼしで止めないため）。</param>
/// <param name="StopAppTimeout">アプリを止める操作（am force-stop）の時間切れ。</param>
public sealed record AndroidRunTimings(TimeSpan AppPollInterval, int AppExitConfirmations, TimeSpan StopAppTimeout)
{
    /// <summary>既定の確認の間隔（秒）。adb の子プロセスを起こすので短くしすぎない。</summary>
    public const double DefaultAppPollIntervalSeconds = 2.0;

    /// <summary>既定の「続けて見つからない」回数（間隔 2 秒 × 2 回 ＝ 終了から 2〜4 秒で気付く）。</summary>
    public const int DefaultAppExitConfirmations = 2;

    /// <summary>既定のアプリの停止の時間切れ（秒）。am force-stop はふつう 1 秒かからない。</summary>
    public const double DefaultStopAppTimeoutSeconds = 15.0;

    /// <summary>既定値（エディタが使う）。</summary>
    public static readonly AndroidRunTimings Default = new(
        TimeSpan.FromSeconds(DefaultAppPollIntervalSeconds), DefaultAppExitConfirmations, TimeSpan.FromSeconds(DefaultStopAppTimeoutSeconds));
}
