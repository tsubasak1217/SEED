using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// センサー（<see cref="Sensors"/>）の JSON の名前と読み書き（内部用。W1-8）。
///
/// 名前の正典は Rust 側 runtime/src/engine/platform/bridge/wire.rs の <c>wire::sensor</c>（Java 側は PlatformContract.java の
/// <c>*SENSOR*</c>）。値を変えるときは 3 か所を必ず揃える。
/// </summary>
internal static class SensorJson
{
    /// <summary>センサーのモジュール。</summary>
    internal const string Module = "sensor";

    /// <summary>受け取りを始める。</summary>
    internal const string MethodStart = "start";

    /// <summary>受け取りを止める。</summary>
    internal const string MethodStop = "stop";

    /// <summary>最新の標本と、前回の read からの最大の大きさ・標本の数。</summary>
    internal const string MethodRead = "read";

    /// <summary>標本を 1 つ入れる（デスクトップの模擬だけ）。</summary>
    internal const string MethodSimInject = "sim_inject";

    // ── 欄の名前 ──
    internal const string KeyKind = "kind";
    internal const string KeyRateHz = "rate_hz";
    internal const string KeySource = "source";
    internal const string KeyX = "x";
    internal const string KeyY = "y";
    internal const string KeyZ = "z";
    internal const string KeyTimestampMs = "timestamp_ms";
    internal const string KeyPeakMagnitude = "peak_magnitude";
    internal const string KeySampleCount = "sample_count";

    // ── 種類の文字列（SensorKind と対応）──
    internal const string KindLinearAcceleration = "linear_acceleration";

    /// <summary>種類を wire の名前にする（知らない値は空文字＝送らずに invalid_argument）。</summary>
    internal static string KindName(SensorKind kind) => kind switch
    {
        SensorKind.LinearAcceleration => KindLinearAcceleration,
        _ => string.Empty,
    };

    /// <summary>種類だけのオブジェクト（stop・read の引数）を作る。</summary>
    internal static string KindObject(SensorKind kind) => PlatformJson.StringObject((KeyKind, KindName(kind)));

    /// <summary>start の引数 {"kind":…,"rate_hz":…} を作る。</summary>
    internal static string StartObject(SensorKind kind, int rateHz)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(KeyKind, KindName(kind));
            writer.WriteNumber(KeyRateHz, rateHz);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>sim_inject の引数 {"kind":…,"x":…,"y":…,"z":…} を作る。</summary>
    internal static string InjectObject(SensorKind kind, Vector3 acceleration)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(KeyKind, KindName(kind));
            writer.WriteNumber(KeyX, acceleration.x);
            writer.WriteNumber(KeyY, acceleration.y);
            writer.WriteNumber(KeyZ, acceleration.z);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>オブジェクトの数の欄を float で（無い・数でない・float に収まらなければ 0）。</summary>
    internal static float GetFloat(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number))
        {
            return 0f;
        }
        float single = (float)number;
        return float.IsFinite(single) ? single : 0f;
    }
}
