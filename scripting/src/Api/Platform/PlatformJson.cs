using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// SEED.Platform の JSON の読み書き（内部用）。
///
/// 形の正典は Rust 側 runtime/src/engine/platform/bridge/wire.rs（Java 側は PlatformContract.java）。
///   返答: {"ok": true, ...} / {"ok": false, "error": "理由"}
///   イベント: {"name": "platform.…", "seq": 番号, "time_ms": UTC の epoch ミリ秒, "data": {...}}
/// 読み取りは System.Text.Json の JsonDocument、書き込みは Utf8JsonWriter（リフレクションを使わない）。
/// </summary>
internal static class PlatformJson
{
    /// <summary>返答: 成功したか。</summary>
    internal const string KeyOk = "ok";

    /// <summary>返答: 失敗の理由の名前。</summary>
    internal const string KeyError = "error";

    /// <summary>イベント: 名前。</summary>
    internal const string KeyName = "name";

    /// <summary>
    /// 返答の JSON から ok と error を読む。
    /// </summary>
    /// <param name="reply">返答の JSON。</param>
    /// <param name="ok">成功したか（読めなければ false）。</param>
    /// <param name="error">失敗の理由（無ければ空文字）。</param>
    /// <returns>オブジェクトで ok（bool）を持っていれば true。</returns>
    internal static bool TryReadReply(string reply, out bool ok, out string error)
    {
        ok = false;
        error = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(reply);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty(KeyOk, out JsonElement okElement)) return false;
            if (okElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            ok = okElement.GetBoolean();
            if (root.TryGetProperty(KeyError, out JsonElement errorElement) && errorElement.ValueKind == JsonValueKind.String)
            {
                error = errorElement.GetString() ?? string.Empty;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// イベントの JSON から名前を読む。
    /// </summary>
    /// <param name="eventJson">イベントの JSON。</param>
    /// <param name="name">名前（読めなければ空文字）。</param>
    /// <returns>オブジェクトで name（文字列）を持っていれば true。</returns>
    internal static bool TryReadEventName(string eventJson, out string name)
    {
        name = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(eventJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty(KeyName, out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String) return false;
            name = nameElement.GetString() ?? string.Empty;
            return name.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 真偽 1 つだけのオブジェクトの JSON を作る（例 {"on":true}。W1-4a の Window の内部の関数を W1-6 で共通化）。
    /// </summary>
    /// <param name="key">キー。</param>
    /// <param name="value">値。</param>
    /// <returns>JSON の文字列。</returns>
    internal static string BoolObject(string key, bool value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean(key, value);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// 整数 1 つだけのオブジェクトの JSON を作る（例 {"ms":40}。W1-6 の Haptics.Vibrate）。
    /// </summary>
    /// <param name="key">キー。</param>
    /// <param name="value">値。</param>
    /// <returns>JSON の文字列。</returns>
    internal static string Int32Object(string key, int value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber(key, value);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// 文字列の値だけを持つオブジェクトの JSON を作る（例 {"nonce":"ping-1"}）。
    /// </summary>
    /// <param name="fields">キーと値の組。</param>
    /// <returns>JSON の文字列。</returns>
    internal static string StringObject(params (string Key, string Value)[] fields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach ((string key, string value) in fields)
            {
                writer.WriteString(key, value);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
