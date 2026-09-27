namespace SEED.Platform;

/// <summary>起動の種類（<see cref="LaunchInfo.Kind"/>。W1-4a）。</summary>
public enum LaunchKind
{
    /// <summary>"launcher": ランチャー・普通の起動（プラットフォーム層を通らない起動。デスクトップは常にこれ）。</summary>
    Launcher,

    /// <summary>"alarm": 目覚ましの鳴動（フルスクリーン通知・鳴動の通知の本文のタップ）。ロック画面の上に出て画面が点いている。</summary>
    Alarm,

    /// <summary>"notification_tap": 通知の本文のタップ（W1-5 の通知）。</summary>
    NotificationTap,

    /// <summary>"notification_action": 通知の操作（ボタン）。<see cref="LaunchInfo.ActionId"/> に操作の ID（鳴動の通知の「開く」は "open"）。</summary>
    NotificationAction,

    /// <summary>"alarm_clock_info": ステータスバー・ロック画面の「次の目覚まし」の表示を押した。</summary>
    AlarmClockInfo,

    /// <summary>"other": それ以外（ランチャー以外の action で起動された・知らない種類）。</summary>
    Other,
}
