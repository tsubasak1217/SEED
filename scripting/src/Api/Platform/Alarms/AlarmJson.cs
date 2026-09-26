using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// 目覚まし（<see cref="Alarms"/>）の JSON の名前と読み書き（内部用。W1-3）。
///
/// 名前の正典は Rust 側 runtime/src/engine/platform/bridge/wire.rs の <c>wire::alarm</c>（Java 側は PlatformContract.java の
/// <c>*ALARM*</c>）。値を変えるときは 3 か所を必ず揃える。読み取りは JsonDocument、書き込みは Utf8JsonWriter（リフレクションなし）。
/// </summary>
internal static class AlarmJson
{
    /// <summary>目覚ましのモジュール。</summary>
    internal const string Module = "alarm";

    /// <summary>予約する（同じ ID は置き換え）。</summary>
    internal const string MethodSchedule = "schedule";

    /// <summary>1 つ取り消す。</summary>
    internal const string MethodCancel = "cancel";

    /// <summary>全部取り消す。</summary>
    internal const string MethodCancelAll = "cancel_all";

    /// <summary>控えの一覧。</summary>
    internal const string MethodList = "list";

    /// <summary>正確なアラームを張れるか。</summary>
    internal const string MethodCanScheduleExact = "can_schedule_exact";

    // ── 欄の名前 ──
    internal const string KeyId = "id";
    internal const string KeyTriggerAtUtcMs = "trigger_at_utc_ms";
    internal const string KeySoundAsset = "sound_asset";
    internal const string KeySoundPath = "sound_path";
    internal const string KeyVibrate = "vibrate";
    internal const string KeyForceVolume = "force_volume";
    internal const string KeyKeepVolume = "keep_volume";
    internal const string KeyFadeInSeconds = "fade_in_seconds";
    internal const string KeyMaxRingMinutes = "max_ring_minutes";
    internal const string KeyTitle = "title";
    internal const string KeyBody = "body";
    internal const string KeyPayloadJson = "payload_json";
    internal const string KeyCreatedAtUtcMs = "created_at_utc_ms";
    internal const string KeyAlarms = "alarms";
    internal const string KeyCount = "count";
    internal const string KeyCanScheduleExact = "can_schedule_exact";
    internal const string KeyScheduledAtUtcMs = "scheduled_at_utc_ms";
    internal const string KeyFiredAtUtcMs = "fired_at_utc_ms";
    internal const string KeyReason = "reason";
    internal const string KeyMissed = "missed";
    internal const string KeyFailed = "failed";
    internal const string KeySimulated = "simulated";

    /// <summary>イベント: 中身。</summary>
    internal const string KeyData = "data";

    /// <summary>イベント: 名前。</summary>
    internal const string KeyName = "name";

    /// <summary>
    /// 予約の引数の JSON を作る。有限でない数（NaN・無限大）は既定値にする（JSON に書けないため）。
    /// </summary>
    /// <param name="request">予約（null でないこと）。</param>
    /// <returns>JSON の文字列。</returns>
    internal static string WriteRequest(AlarmRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(KeyId, request.Id ?? string.Empty);
            writer.WriteNumber(KeyTriggerAtUtcMs, request.TriggerAtUtcMs);
            if (!string.IsNullOrEmpty(request.SoundAsset))
            {
                writer.WriteString(KeySoundAsset, request.SoundAsset);
            }
            writer.WriteBoolean(KeyVibrate, request.Vibrate);
            writer.WriteNumber(KeyForceVolume, Finite(request.ForceVolume, AlarmRequest.VolumeUnchanged));
            writer.WriteBoolean(KeyKeepVolume, request.KeepVolume);
            writer.WriteNumber(KeyFadeInSeconds, Finite(request.FadeInSeconds, AlarmRequest.DefaultFadeInSeconds));
            writer.WriteNumber(KeyMaxRingMinutes, request.MaxRingMinutes);
            writer.WriteString(KeyTitle, request.Title ?? string.Empty);
            writer.WriteString(KeyBody, request.Body ?? string.Empty);
            writer.WriteString(KeyPayloadJson, request.PayloadJson ?? string.Empty);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>ID だけのオブジェクト（cancel の引数）を作る。</summary>
    internal static string IdObject(string id) => PlatformJson.StringObject((KeyId, id));

    /// <summary>有限の数ならそのまま、そうでなければ既定値。</summary>
    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;

    /// <summary>オブジェクトの文字列の欄（無い・文字列でなければ空文字）。</summary>
    internal static string GetString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>オブジェクトの整数の欄（無い・整数でなければ 0）。</summary>
    internal static long GetLong(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number
            : 0;

    /// <summary>オブジェクトの真偽の欄（無い・真偽でなければ false）。</summary>
    internal static bool GetBool(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    /// <summary>返答の JSON の真偽の欄（読めなければ false）。</summary>
    internal static bool ReadReplyBool(string reply, string key)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(reply);
            return GetBool(document.RootElement, key);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// イベントの JSON を読み、名前が <paramref name="expectedName"/> なら中身（data）を <paramref name="read"/> に渡して値にする。
    /// </summary>
    /// <typeparam name="T">イベントの型。</typeparam>
    /// <param name="eventJson">イベントの JSON 全体（SEED.Events の引数）。</param>
    /// <param name="expectedName">期待する名前。</param>
    /// <param name="read">data から値を作る関数。</param>
    /// <param name="value">読めた値（読めなければ既定値）。</param>
    /// <returns>名前が合い、data がオブジェクトなら true。</returns>
    internal static bool TryReadEvent<T>(string eventJson, string expectedName, System.Func<JsonElement, T> read, out T value)
        where T : struct
    {
        value = default;
        if (string.IsNullOrEmpty(eventJson)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(eventJson);
            JsonElement root = document.RootElement;
            if (GetString(root, KeyName) != expectedName) return false;
            if (!root.TryGetProperty(KeyData, out JsonElement data) || data.ValueKind != JsonValueKind.Object) return false;
            value = read(data);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
