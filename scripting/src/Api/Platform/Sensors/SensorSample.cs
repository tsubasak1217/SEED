using System.Globalization;
using System.Text.Json;

namespace SEED.Platform;

/// <summary>
/// センサーを 1 回読んだ結果（<see cref="Sensors.Read"/>。不変値型。W1-8）。
///
/// <para>
/// <see cref="Acceleration"/> と <see cref="TimestampMs"/> は最新の標本、<see cref="PeakMagnitude"/> と <see cref="SampleCount"/> は
/// <b>前回の <see cref="Sensors.Read"/> からの</b>最大の大きさと標本の数です（読むたびに 0 から数え直す）。センサーはフレームより細かく
/// （既定 50 Hz）標本を出すので、最新の値だけを見るとフレームの間の振りの頂点を見落とします。振りの判定には <see cref="PeakMagnitude"/> を使います。
/// </para>
/// </summary>
public readonly struct SensorSample
{
    /// <summary>最新の標本（m/s²。重力を除いた加速度。端末の座標系）。まだ標本が無ければ <see cref="Vector3.Zero"/>。</summary>
    public Vector3 Acceleration { get; }

    /// <summary>最新の標本の時刻（UTC の epoch ミリ秒。まだ標本が無ければ 0）。</summary>
    public long TimestampMs { get; }

    /// <summary>前回の <see cref="Sensors.Read"/> からの標本の大きさ √(x²+y²+z²) の最大（m/s²。標本が無ければ 0）。</summary>
    public float PeakMagnitude { get; }

    /// <summary>前回の <see cref="Sensors.Read"/> からの標本の数（前面から外れている間・模擬で標本を入れていなければ 0）。</summary>
    public int SampleCount { get; }

    /// <summary>値を作る。</summary>
    internal SensorSample(Vector3 acceleration, long timestampMs, float peakMagnitude, int sampleCount)
    {
        Acceleration = acceleration;
        TimestampMs = timestampMs;
        PeakMagnitude = peakMagnitude;
        SampleCount = sampleCount;
    }

    /// <summary>read の返答から作る（欄が無い・数でなければ 0。標本の数は int に収める）。</summary>
    internal static SensorSample FromJson(JsonElement reply) => new SensorSample(
        new Vector3(SensorJson.GetFloat(reply, SensorJson.KeyX), SensorJson.GetFloat(reply, SensorJson.KeyY), SensorJson.GetFloat(reply, SensorJson.KeyZ)),
        AlarmJson.GetLong(reply, SensorJson.KeyTimestampMs),
        SensorJson.GetFloat(reply, SensorJson.KeyPeakMagnitude),
        (int)System.Math.Clamp(AlarmJson.GetLong(reply, SensorJson.KeySampleCount), 0L, int.MaxValue));

    /// <summary>ログ向けの 1 行（例 "peak 13.20 m/s²・12 個・最新 (0.10, -0.20, 0.05) @1790000000000"）。</summary>
    public override string ToString()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        return $"peak {PeakMagnitude.ToString("0.00", c)} m/s²・{SampleCount} 個・最新 ({Acceleration.x.ToString("0.00", c)}, "
            + $"{Acceleration.y.ToString("0.00", c)}, {Acceleration.z.ToString("0.00", c)}) @{TimestampMs.ToString(c)}";
    }
}
