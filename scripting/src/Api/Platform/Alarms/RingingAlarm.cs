using System.Globalization;
using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// 鳴動中の目覚まし（<see cref="Alarms.GetRinging"/> の結果。不変値型。W1-4a）。
///
/// <para>
/// Android では別プロセス :seed_platform の鳴動の前景サービス（RingService）が鳴らしている予約。デスクトップの模擬では、
/// 発火してから止める（<see cref="Alarms.StopRinging"/>）か安全弁（MaxRingMinutes）までの間（音は鳴らない）。
/// </para>
/// </summary>
public readonly struct RingingAlarm
{
    /// <summary>予約の ID。</summary>
    public string Id { get; }

    /// <summary>鳴るはずだった時刻（予約の TriggerAtUtcMs。UTC の epoch ミリ秒）。</summary>
    public long ScheduledAtUtcMs { get; }

    /// <summary>鳴り始めた時刻（UTC の epoch ミリ秒。待ち行列から繰り上がったときはその時刻）。</summary>
    public long StartedAtUtcMs { get; }

    /// <summary>予約に渡した任意の JSON。</summary>
    public string PayloadJson { get; }

    /// <summary>デスクトップの模擬が作ったか。</summary>
    public bool Simulated { get; }

    /// <summary>値を作る。</summary>
    internal RingingAlarm(string id, long scheduledAtUtcMs, long startedAtUtcMs, string payloadJson, bool simulated)
    {
        Id = id;
        ScheduledAtUtcMs = scheduledAtUtcMs;
        StartedAtUtcMs = startedAtUtcMs;
        PayloadJson = payloadJson;
        Simulated = simulated;
    }

    /// <summary>get_ringing の返答の ringing から作る。</summary>
    internal static RingingAlarm FromJson(JsonElement ringing) => new RingingAlarm(
        AlarmJson.GetString(ringing, AlarmJson.KeyId),
        AlarmJson.GetLong(ringing, AlarmJson.KeyScheduledAtUtcMs),
        AlarmJson.GetLong(ringing, AlarmJson.KeyStartedAtUtcMs),
        AlarmJson.GetString(ringing, AlarmJson.KeyPayloadJson),
        AlarmJson.GetBool(ringing, AlarmJson.KeySimulated));

    /// <summary>ログ向けの 1 行（例 "morning 予定 1790000000000 → 鳴り始め 1790000000012"）。</summary>
    public override string ToString() =>
        $"{Id} 予定 {ScheduledAtUtcMs.ToString(CultureInfo.InvariantCulture)} → 鳴り始め {StartedAtUtcMs.ToString(CultureInfo.InvariantCulture)}";
}
