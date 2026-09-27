namespace SEED.Platform;

/// <summary>
/// 起動理由（<see cref="App"/>・<see cref="LaunchInfo"/>）の JSON の名前（内部用。W1-4a・W1-6 でディープリンクの uri）。
///
/// 名前の正典は Rust 側 runtime/src/engine/platform/bridge/wire.rs の <c>wire::launch</c>（Java 側は PlatformContract.java の
/// <c>*LAUNCH*</c>）。値を変えるときは 3 か所を必ず揃える。id・scheduled_at_utc_ms・fired_at_utc_ms・payload_json は目覚ましと同じ名前
/// （<see cref="AlarmJson"/> の Key*）。
/// </summary>
internal static class LaunchJson
{
    /// <summary>起動理由のモジュール（基盤そのもの）。</summary>
    internal const string ModulePlatform = "platform";

    /// <summary>この起動の理由を返す。</summary>
    internal const string MethodLaunchReason = "launch_reason";

    /// <summary>launch_reason の返答: 起動理由のオブジェクト。</summary>
    internal const string KeyLaunch = "launch";

    /// <summary>起動理由: 種類。</summary>
    internal const string KeyKind = "kind";

    /// <summary>起動理由: 通知の操作の ID。</summary>
    internal const string KeyActionId = "action_id";

    /// <summary>起動理由: ディープリンクの URI（W1-6）。</summary>
    internal const string KeyUri = "uri";

    // ── 種類の文字列（LaunchKind と対応）──
    internal const string KindLauncher = "launcher";
    internal const string KindAlarm = "alarm";
    internal const string KindNotificationTap = "notification_tap";
    internal const string KindNotificationAction = "notification_action";
    internal const string KindAlarmClockInfo = "alarm_clock_info";
    internal const string KindOther = "other";
    internal const string KindDeepLink = "deep_link";

    /// <summary>
    /// 種類の文字列を列挙にする（知らない文字列は <see cref="LaunchKind.Other"/>）。
    /// </summary>
    /// <param name="kind">種類の文字列。</param>
    /// <returns>種類。</returns>
    internal static LaunchKind ParseKind(string kind) => kind switch
    {
        KindLauncher => LaunchKind.Launcher,
        KindAlarm => LaunchKind.Alarm,
        KindNotificationTap => LaunchKind.NotificationTap,
        KindNotificationAction => LaunchKind.NotificationAction,
        KindAlarmClockInfo => LaunchKind.AlarmClockInfo,
        KindDeepLink => LaunchKind.DeepLink,
        _ => LaunchKind.Other,
    };
}
