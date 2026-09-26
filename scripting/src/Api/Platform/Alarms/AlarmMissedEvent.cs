namespace SEED.Platform;

/// <summary>
/// 目覚ましが鳴らなかったイベント "platform.alarm.missed" の中身（不変値型。W1-3）。
///
/// <para>
/// 再起動・強制停止・正確なアラームの許可の取り消しなどで、予約が OS から消えていた間に予定時刻を過ぎた予約を、
/// 張り直すとき（:seed_platform の BootReceiver）に見つけて記録したもの。鳴らさずに控えから消している
/// （受信機から直接鳴らさない）。アプリは「鳴らなかった朝」をどう扱うかをここで決める。
/// </para>
/// </summary>
public readonly struct AlarmMissedEvent
{
    /// <summary>イベントの名前（SEED.Events でもこの名前で届く）。</summary>
    public const string Name = "platform.alarm.missed";

    /// <summary>理由の文字列: 電源断・強制停止・更新など。</summary>
    public const string ReasonDeviceOff = "device_off";

    /// <summary>理由の文字列: 正確なアラームの許可の取り消し。</summary>
    public const string ReasonPermissionRevoked = "permission_revoked";

    /// <summary>予約の ID。</summary>
    public string Id { get; }

    /// <summary>鳴るはずだった時刻（UTC の epoch ミリ秒）。</summary>
    public long ScheduledAtUtcMs { get; }

    /// <summary>理由。</summary>
    public AlarmMissedReason Reason { get; }

    /// <summary>理由の元の文字列（"device_off" など）。</summary>
    public string ReasonName { get; }

    /// <summary>予約に渡した任意の JSON。</summary>
    public string PayloadJson { get; }

    /// <summary>値を作る。</summary>
    internal AlarmMissedEvent(string id, long scheduledAtUtcMs, string reasonName, string payloadJson)
    {
        Id = id;
        ScheduledAtUtcMs = scheduledAtUtcMs;
        ReasonName = reasonName;
        Reason = reasonName switch
        {
            ReasonDeviceOff => AlarmMissedReason.DeviceOff,
            ReasonPermissionRevoked => AlarmMissedReason.PermissionRevoked,
            _ => AlarmMissedReason.Unknown,
        };
        PayloadJson = payloadJson;
    }

    /// <summary>
    /// イベントの JSON（SEED.Events の引数）を読む。
    /// </summary>
    /// <param name="json">イベントの JSON 全体。</param>
    /// <param name="value">読めた値（読めなければ既定値）。</param>
    /// <returns>名前が <see cref="Name"/> で、中身が読めたら true。</returns>
    public static bool TryParse(string json, out AlarmMissedEvent value) =>
        AlarmJson.TryReadEvent(json, Name, data => new AlarmMissedEvent(
            AlarmJson.GetString(data, AlarmJson.KeyId),
            AlarmJson.GetLong(data, AlarmJson.KeyScheduledAtUtcMs),
            AlarmJson.GetString(data, AlarmJson.KeyReason),
            AlarmJson.GetString(data, AlarmJson.KeyPayloadJson)), out value);

    /// <summary>ログ向けの 1 行。</summary>
    public override string ToString() => $"{Id} 予定 {ScheduledAtUtcMs}（{ReasonName}）";
}
