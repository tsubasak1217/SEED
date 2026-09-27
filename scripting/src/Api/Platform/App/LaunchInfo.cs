using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// 起動理由（<see cref="App.LaunchReason"/> とイベント "platform.launch" の中身。不変値型。W1-4a）。
///
/// <para>
/// Android では、プラットフォーム層が自分で作った PendingIntent（exported=false の入口 PlatformEntry を通るもの）の起動だけを
/// <see cref="LaunchKind.Alarm"/> などとして信用する（リリース版でも取れ、他のアプリの Intent では偽造できない）。
/// それ以外の起動は <see cref="LaunchKind.Launcher"/>（MAIN）か <see cref="LaunchKind.Other"/>。最近のタスクからの開き直しは
/// <see cref="LaunchKind.Launcher"/>（止めた後の目覚ましで鳴動画面を出し直さないため）。デスクトップは常に <see cref="LaunchKind.Launcher"/>。
/// </para>
/// </summary>
public readonly struct LaunchInfo
{
    /// <summary>起動した後に届いた Intent（Android の onNewIntent）のイベントの名前（SEED.Events でもこの名前で届く）。</summary>
    public const string EventName = "platform.launch";

    /// <summary>通知の操作の ID: 鳴動の通知の「開く」（<see cref="LaunchKind.NotificationAction"/> のとき）。</summary>
    public const string ActionOpen = "open";

    /// <summary>種類。</summary>
    public LaunchKind Kind { get; }

    /// <summary>種類の元の文字列（"alarm" など）。</summary>
    public string KindName { get; }

    /// <summary>予約・通知の ID（無ければ空文字）。</summary>
    public string Id { get; }

    /// <summary>通知の操作の ID（<see cref="LaunchKind.NotificationAction"/> のとき。無ければ空文字）。</summary>
    public string ActionId { get; }

    /// <summary>鳴るはずだった時刻（UTC の epoch ミリ秒。無ければ 0）。</summary>
    public long ScheduledAtUtcMs { get; }

    /// <summary>目覚ましの配信を受けた時刻（UTC の epoch ミリ秒。無ければ 0。鳴動画面が出るまでの遅れを測る起点）。</summary>
    public long FiredAtUtcMs { get; }

    /// <summary>予約・通知に渡した任意の JSON（無ければ空文字）。</summary>
    public string PayloadJson { get; }

    /// <summary>デスクトップの模擬が作ったか。</summary>
    public bool Simulated { get; }

    /// <summary>ランチャーの起動（理由が取れなかったときの既定）。</summary>
    internal static LaunchInfo Launcher => new LaunchInfo(LaunchJson.KindLauncher, string.Empty, string.Empty, 0, 0, string.Empty, false);

    /// <summary>値を作る。</summary>
    internal LaunchInfo(string kindName, string id, string actionId, long scheduledAtUtcMs, long firedAtUtcMs, string payloadJson, bool simulated)
    {
        KindName = kindName;
        Kind = LaunchJson.ParseKind(kindName);
        Id = id;
        ActionId = actionId;
        ScheduledAtUtcMs = scheduledAtUtcMs;
        FiredAtUtcMs = firedAtUtcMs;
        PayloadJson = payloadJson;
        Simulated = simulated;
    }

    /// <summary>起動理由のオブジェクト（launch_reason の launch・イベントの data）から作る。</summary>
    internal static LaunchInfo FromJson(JsonElement launch) => new LaunchInfo(
        AlarmJson.GetString(launch, LaunchJson.KeyKind),
        AlarmJson.GetString(launch, AlarmJson.KeyId),
        AlarmJson.GetString(launch, LaunchJson.KeyActionId),
        AlarmJson.GetLong(launch, AlarmJson.KeyScheduledAtUtcMs),
        AlarmJson.GetLong(launch, AlarmJson.KeyFiredAtUtcMs),
        AlarmJson.GetString(launch, AlarmJson.KeyPayloadJson),
        AlarmJson.GetBool(launch, AlarmJson.KeySimulated));

    /// <summary>
    /// イベント "platform.launch" の JSON（SEED.Events の引数）を読む。
    /// </summary>
    /// <param name="json">イベントの JSON 全体。</param>
    /// <param name="value">読めた値（読めなければ既定値）。</param>
    /// <returns>名前が <see cref="EventName"/> で、中身が読めたら true。</returns>
    public static bool TryParseEvent(string json, out LaunchInfo value) =>
        AlarmJson.TryReadEvent(json, EventName, FromJson, out value);

    /// <summary>ログ向けの 1 行（例 "alarm morning" / "notification_action morning/open" / "launcher"）。</summary>
    public override string ToString() =>
        KindName + (string.IsNullOrEmpty(Id) ? string.Empty : " " + Id) + (string.IsNullOrEmpty(ActionId) ? string.Empty : "/" + ActionId);
}
