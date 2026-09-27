namespace SEED.Platform;

/// <summary>起動の種類（<see cref="LaunchInfo.Kind"/>。W1-4a・W1-6 で <see cref="DeepLink"/>。値の並びは変えない〈後ろへ足す〉）。</summary>
public enum LaunchKind
{
    /// <summary>"launcher": ランチャー・普通の起動（プラットフォーム層を通らない起動。デスクトップは常にこれ）。</summary>
    Launcher,

    /// <summary>"alarm": 目覚ましの鳴動（フルスクリーン通知・鳴動の通知の本文のタップ）。ロック画面の上に出て画面が点いている。</summary>
    Alarm,

    /// <summary>"notification_tap": 通知（<see cref="Notifications.Show"/>。W1-5）の本文のタップ。<see cref="LaunchInfo.Id"/> に通知の ID。</summary>
    NotificationTap,

    /// <summary>
    /// "notification_action": 通知の操作（ボタン）。<see cref="LaunchInfo.ActionId"/> に操作の ID（鳴動の通知の「開く」は "open"、
    /// <see cref="Notifications.Show"/> の通知は <see cref="NotificationAction.Id"/>）、<see cref="LaunchInfo.Id"/> に通知・予約の ID。
    /// </summary>
    NotificationAction,

    /// <summary>"alarm_clock_info": ステータスバー・ロック画面の「次の目覚まし」の表示を押した。</summary>
    AlarmClockInfo,

    /// <summary>"other": それ以外（ランチャー以外の action で起動された・知らない種類）。</summary>
    Other,

    /// <summary>
    /// "deep_link": URL（ディープリンク）で開かれた（W1-6）。<see cref="LaunchInfo.Uri"/> にその URL。Android では機能 deep_links の
    /// intent-filter に合った URL のほか、他のアプリが明示して送った URL も同じに見えるので、<b>中身を検査してから使う</b>
    /// （お金・データを動かす操作をそのまま行わない）。最近のタスクからの開き直しは <see cref="Launcher"/>（同じ URL を二度処理しない）。
    /// </summary>
    DeepLink,
}
