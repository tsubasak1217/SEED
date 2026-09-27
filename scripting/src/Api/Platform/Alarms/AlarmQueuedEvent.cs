namespace SEED.Platform;

/// <summary>
/// 別の目覚ましの鳴動中に時刻が来たので待たせたイベント "platform.alarm.queued" の中身（不変値型。W1-4a）。
///
/// <para>
/// 捨てずに待ち行列へ入れ、今の鳴動が止まったら（<see cref="Alarms.StopRinging"/>・安全弁）続けて鳴らす
/// （Flutter 版は後の予約を黙って捨てていた穴を塞いだもの）。待たせた予約も <see cref="AlarmFiredEvent"/> は先に届いている。
/// 待ち行列の予約を鳴らさずに外すには、その ID で <see cref="Alarms.StopRinging"/> を呼ぶ。
/// </para>
/// </summary>
public readonly struct AlarmQueuedEvent
{
    /// <summary>イベントの名前（SEED.Events でもこの名前で届く）。</summary>
    public const string Name = "platform.alarm.queued";

    /// <summary>待たせた予約の ID。</summary>
    public string Id { get; }

    /// <summary>待たせた予約の鳴るはずだった時刻（UTC の epoch ミリ秒）。</summary>
    public long ScheduledAtUtcMs { get; }

    /// <summary>今鳴っていて、止まるのを待っている予約の ID。</summary>
    public string WaitingFor { get; }

    /// <summary>待たせた予約に渡した任意の JSON。</summary>
    public string PayloadJson { get; }

    /// <summary>デスクトップの模擬が作ったか。</summary>
    public bool Simulated { get; }

    /// <summary>値を作る。</summary>
    internal AlarmQueuedEvent(string id, long scheduledAtUtcMs, string waitingFor, string payloadJson, bool simulated)
    {
        Id = id;
        ScheduledAtUtcMs = scheduledAtUtcMs;
        WaitingFor = waitingFor;
        PayloadJson = payloadJson;
        Simulated = simulated;
    }

    /// <summary>
    /// イベントの JSON（SEED.Events の引数）を読む。
    /// </summary>
    /// <param name="json">イベントの JSON 全体。</param>
    /// <param name="value">読めた値（読めなければ既定値）。</param>
    /// <returns>名前が <see cref="Name"/> で、中身が読めたら true。</returns>
    public static bool TryParse(string json, out AlarmQueuedEvent value) =>
        AlarmJson.TryReadEvent(json, Name, data => new AlarmQueuedEvent(
            AlarmJson.GetString(data, AlarmJson.KeyId),
            AlarmJson.GetLong(data, AlarmJson.KeyScheduledAtUtcMs),
            AlarmJson.GetString(data, AlarmJson.KeyWaitingFor),
            AlarmJson.GetString(data, AlarmJson.KeyPayloadJson),
            AlarmJson.GetBool(data, AlarmJson.KeySimulated)), out value);

    /// <summary>ログ向けの 1 行。</summary>
    public override string ToString() => $"{Id}（{WaitingFor} の鳴動が終わるまで待つ）";
}
