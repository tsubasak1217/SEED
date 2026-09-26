using System.Globalization;
using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// 控えにある予約 1 件（<see cref="Alarms.GetScheduled"/> の結果。不変値型。W1-3）。
/// </summary>
public readonly struct ScheduledAlarm
{
    /// <summary>予約の ID。</summary>
    public string Id { get; }

    /// <summary>鳴らす時刻（UTC の epoch ミリ秒）。</summary>
    public long TriggerAtUtcMs { get; }

    /// <summary>予約に渡した任意の JSON。</summary>
    public string PayloadJson { get; }

    /// <summary>鳴動の通知の題。</summary>
    public string Title { get; }

    /// <summary>鳴動の通知の本文。</summary>
    public string Body { get; }

    /// <summary>音源（Android: 書き出した端末のファイルの絶対パス。空なら既定の音。デスクトップの模擬: 渡したままの値）。</summary>
    public string Sound { get; }

    /// <summary>予約を受け付けた時刻（UTC の epoch ミリ秒）。</summary>
    public long CreatedAtUtcMs { get; }

    /// <summary>値を作る。</summary>
    internal ScheduledAlarm(string id, long triggerAtUtcMs, string payloadJson, string title, string body, string sound, long createdAtUtcMs)
    {
        Id = id;
        TriggerAtUtcMs = triggerAtUtcMs;
        PayloadJson = payloadJson;
        Title = title;
        Body = body;
        Sound = sound;
        CreatedAtUtcMs = createdAtUtcMs;
    }

    /// <summary>list の返答の 1 行から作る（端末は sound_path、模擬は sound_asset を音源にする）。</summary>
    internal static ScheduledAlarm FromJson(JsonElement row)
    {
        string sound = AlarmJson.GetString(row, AlarmJson.KeySoundPath);
        if (sound.Length == 0) sound = AlarmJson.GetString(row, AlarmJson.KeySoundAsset);
        return new ScheduledAlarm(
            AlarmJson.GetString(row, AlarmJson.KeyId),
            AlarmJson.GetLong(row, AlarmJson.KeyTriggerAtUtcMs),
            AlarmJson.GetString(row, AlarmJson.KeyPayloadJson),
            AlarmJson.GetString(row, AlarmJson.KeyTitle),
            AlarmJson.GetString(row, AlarmJson.KeyBody),
            sound,
            AlarmJson.GetLong(row, AlarmJson.KeyCreatedAtUtcMs));
    }

    /// <summary>ログ向けの 1 行（例 "morning @1790000000000"）。</summary>
    public override string ToString() => $"{Id} @{TriggerAtUtcMs.ToString(CultureInfo.InvariantCulture)}";
}
