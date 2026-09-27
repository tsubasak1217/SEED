namespace SEED.Platform;

/// <summary>
/// 目覚ましの鳴動が終わったイベント "platform.alarm.ring_stopped" の中身（不変値型。W1-4a）。
///
/// <para>
/// 止めた（<see cref="Alarms.StopRinging"/>）・安全弁（MaxRingMinutes）・鳴らし続けられなかった、のどれでも届く。
/// 待ち行列に次の予約があれば、続けてそれが鳴り始める（<see cref="Alarms.GetRinging"/> で分かる）。
/// Android ではアプリが動いていなければ、次に SEED.Platform へつないだときに届く（安全弁で止まった朝のあとに開いたとき等）。
/// </para>
/// </summary>
public readonly struct AlarmRingStoppedEvent
{
    /// <summary>イベントの名前（SEED.Events でもこの名前で届く）。</summary>
    public const string Name = "platform.alarm.ring_stopped";

    /// <summary>理由の文字列: アプリが止めた。</summary>
    public const string ReasonStopped = "stopped";

    /// <summary>理由の文字列: 安全弁で止まった。</summary>
    public const string ReasonTimeout = "timeout";

    /// <summary>理由の文字列: 鳴らし続けられなかった。</summary>
    public const string ReasonError = "error";

    /// <summary>予約の ID。</summary>
    public string Id { get; }

    /// <summary>理由。</summary>
    public AlarmRingStopReason Reason { get; }

    /// <summary>理由の元の文字列（"stopped" など）。</summary>
    public string ReasonName { get; }

    /// <summary>鳴るはずだった時刻（UTC の epoch ミリ秒）。</summary>
    public long ScheduledAtUtcMs { get; }

    /// <summary>予約に渡した任意の JSON。</summary>
    public string PayloadJson { get; }

    /// <summary>デスクトップの模擬が作ったか。</summary>
    public bool Simulated { get; }

    /// <summary>値を作る。</summary>
    internal AlarmRingStoppedEvent(string id, string reasonName, long scheduledAtUtcMs, string payloadJson, bool simulated)
    {
        Id = id;
        ReasonName = reasonName;
        Reason = reasonName switch
        {
            ReasonStopped => AlarmRingStopReason.Stopped,
            ReasonTimeout => AlarmRingStopReason.Timeout,
            ReasonError => AlarmRingStopReason.Error,
            _ => AlarmRingStopReason.Unknown,
        };
        ScheduledAtUtcMs = scheduledAtUtcMs;
        PayloadJson = payloadJson;
        Simulated = simulated;
    }

    /// <summary>
    /// イベントの JSON（SEED.Events の引数）を読む。
    /// </summary>
    /// <param name="json">イベントの JSON 全体。</param>
    /// <param name="value">読めた値（読めなければ既定値）。</param>
    /// <returns>名前が <see cref="Name"/> で、中身が読めたら true。</returns>
    public static bool TryParse(string json, out AlarmRingStoppedEvent value) =>
        AlarmJson.TryReadEvent(json, Name, data => new AlarmRingStoppedEvent(
            AlarmJson.GetString(data, AlarmJson.KeyId),
            AlarmJson.GetString(data, AlarmJson.KeyReason),
            AlarmJson.GetLong(data, AlarmJson.KeyScheduledAtUtcMs),
            AlarmJson.GetString(data, AlarmJson.KeyPayloadJson),
            AlarmJson.GetBool(data, AlarmJson.KeySimulated)), out value);

    /// <summary>ログ向けの 1 行。</summary>
    public override string ToString() => $"{Id}（{ReasonName}）";
}
