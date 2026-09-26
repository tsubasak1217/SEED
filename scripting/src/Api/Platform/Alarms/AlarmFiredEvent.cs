namespace SEED.Platform;

/// <summary>
/// 目覚ましが鳴った（予定時刻に配信された）イベント "platform.alarm.fired" の中身（不変値型。W1-3）。
///
/// <para>
/// 受け方: <c>this.On(AlarmFiredEvent.Name, (string json) =&gt; { if (AlarmFiredEvent.TryParse(json, out var e)) … });</c>
/// （SEED.Events の引数はイベントの JSON 全体）。Android では :seed_platform が記録し、アプリが動いていれば次のフレームで、
/// 動いていなければ次に SEED.Platform へつないだとき（最初の呼び出しの後の platform.connected の頃）に届く。
/// 鳴った予約は控えから消える（一回限り）。W1-3 では音は鳴らない（鳴動は W1-4）。
/// </para>
/// </summary>
public readonly struct AlarmFiredEvent
{
    /// <summary>イベントの名前（SEED.Events でもこの名前で届く）。</summary>
    public const string Name = "platform.alarm.fired";

    /// <summary>予約の ID。</summary>
    public string Id { get; }

    /// <summary>鳴るはずだった時刻（予約の TriggerAtUtcMs。UTC の epoch ミリ秒）。</summary>
    public long ScheduledAtUtcMs { get; }

    /// <summary>配信を受けた時刻（UTC の epoch ミリ秒。予定との差が遅れ）。</summary>
    public long FiredAtUtcMs { get; }

    /// <summary>予約に渡した任意の JSON。</summary>
    public string PayloadJson { get; }

    /// <summary>デスクトップの模擬が作ったか。</summary>
    public bool Simulated { get; }

    /// <summary>値を作る。</summary>
    internal AlarmFiredEvent(string id, long scheduledAtUtcMs, long firedAtUtcMs, string payloadJson, bool simulated)
    {
        Id = id;
        ScheduledAtUtcMs = scheduledAtUtcMs;
        FiredAtUtcMs = firedAtUtcMs;
        PayloadJson = payloadJson;
        Simulated = simulated;
    }

    /// <summary>
    /// イベントの JSON（SEED.Events の引数）を読む。
    /// </summary>
    /// <param name="json">イベントの JSON 全体。</param>
    /// <param name="value">読めた値（読めなければ既定値）。</param>
    /// <returns>名前が <see cref="Name"/> で、中身が読めたら true。</returns>
    public static bool TryParse(string json, out AlarmFiredEvent value) =>
        AlarmJson.TryReadEvent(json, Name, data => new AlarmFiredEvent(
            AlarmJson.GetString(data, AlarmJson.KeyId),
            AlarmJson.GetLong(data, AlarmJson.KeyScheduledAtUtcMs),
            AlarmJson.GetLong(data, AlarmJson.KeyFiredAtUtcMs),
            AlarmJson.GetString(data, AlarmJson.KeyPayloadJson),
            AlarmJson.GetBool(data, AlarmJson.KeySimulated)), out value);

    /// <summary>ログ向けの 1 行。</summary>
    public override string ToString() => $"{Id} 予定 {ScheduledAtUtcMs} → 配信 {FiredAtUtcMs}（{FiredAtUtcMs - ScheduledAtUtcMs} ms 遅れ）";
}
