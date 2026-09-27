using System;
using System.Globalization;

namespace SEED.UI;

// ============================================================
//  ValueMath.cs — スライダ・数値欄・進捗の値の計算（W2-4。純粋な計算）
//
//  スライダ: 値 ↔ 溝の上の割合、段階（step > 0 なら min + k × step へ寄せる）、指の位置 → 値。
//  数値欄: 増減（段階ぶん・範囲へ収める）、文字の解釈（数でない入力は捨てる・範囲の外は収める。roadmap §3.3）、
//          長押しの連続の間隔（motion.repeat_* のトークン。押し続けるほど速くなり、最短の間隔で止まる）、表示の文字。
//  進捗: 0..1 へ収めた割合・棒の塗りの幅・輪の角度（度）。
// ============================================================

/// <summary>スライダ・数値欄・進捗の値の計算。</summary>
public static class ValueMath
{
    /// <summary>一周（度）。</summary>
    public const float FullTurnDegrees = 360f;
    /// <summary>段階の丸めの許容（浮動小数の誤差で 1 段ずれないように）。</summary>
    private const float StepEpsilon = 1e-4f;

    /// <summary>値を範囲へ収める（min &gt; max なら入れ替える）。</summary>
    public static float Clamp(float value, float min, float max)
    {
        if (min > max) (min, max) = (max, min);
        return float.IsNaN(value) ? min : Math.Clamp(value, min, max);
    }

    /// <summary>
    /// 段階へ寄せて範囲へ収める（step ≤ 0 なら範囲へ収めるだけ）。段階は min から数え、max も止まれる位置にする
    /// （範囲が段階で割り切れなくても端まで動かせる。例 5〜30・段階 10 なら 5, 15, 25, 30）。
    /// </summary>
    public static float Snap(float value, float min, float max, float step)
    {
        float v = Clamp(value, min, max);
        if (step <= 0f) return v;
        float lo = Math.Min(min, max);
        float hi = Math.Max(min, max);
        float k = MathF.Round((v - lo) / step);
        float snapped = Clamp(lo + k * step, min, max);
        // 最後の段階より端（max）の方が近ければ端へ
        return MathF.Abs(v - hi) < MathF.Abs(v - snapped) ? hi : snapped;
    }

    /// <summary>値 → 溝の上の割合（0..1）。</summary>
    public static float Fraction(float value, float min, float max)
    {
        float span = max - min;
        return MathF.Abs(span) < float.Epsilon ? 0f : Math.Clamp((value - min) / span, 0f, 1f);
    }

    /// <summary>
    /// 指の位置（部品のローカルの x）→ 値（段階へ寄せる）。
    /// </summary>
    /// <param name="localX">指の位置（部品の左端が 0。GestureEvent.LocalPosition.x）。</param>
    /// <param name="trackStart">溝の左端の x。</param>
    /// <param name="trackLength">溝の長さ。</param>
    /// <param name="min">最小値。</param>
    /// <param name="max">最大値。</param>
    /// <param name="step">段階（0 = 連続）。</param>
    public static float ValueAt(float localX, float trackStart, float trackLength, float min, float max, float step)
    {
        float t = trackLength > 0f ? Math.Clamp((localX - trackStart) / trackLength, 0f, 1f) : 0f;
        return Snap(min + (max - min) * t, min, max, step);
    }

    /// <summary>増減（段階 × 向き。範囲へ収める）。</summary>
    /// <param name="value">今の値。</param>
    /// <param name="direction">+1 で増やす・−1 で減らす。</param>
    /// <param name="min">最小値。</param>
    /// <param name="max">最大値。</param>
    /// <param name="step">段階（0 以下なら 1）。</param>
    public static float Step(float value, int direction, float min, float max, float step)
    {
        float s = step > 0f ? step : 1f;
        return Snap(value + Math.Sign(direction) * s, min, max, step > 0f ? step : 0f);
    }

    /// <summary>
    /// 文字を数として読む（数でない・無限・NaN は false＝入力を捨てる。範囲の外は収め、段階へ寄せる）。
    /// </summary>
    public static bool TryParse(string? text, float min, float max, float step, out float value)
    {
        value = min;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            && !float.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
            return false;
        if (!float.IsFinite(v)) return false;
        value = Snap(v, min, max, step);
        return true;
    }

    /// <summary>
    /// 長押しの連続の間隔（秒）。押し続けた時間 held が長いほど短くなり、最短の間隔で止まる。
    /// </summary>
    /// <param name="held">連続が始まってからの時間（秒）。</param>
    /// <param name="first">最初の間隔（motion.repeat_interval）。</param>
    /// <param name="shortest">最短の間隔（motion.repeat_min_interval）。</param>
    /// <param name="accelPerSecond">1 秒ごとに間隔が何倍になるか（motion.repeat_accel。1 未満で速くなる）。</param>
    public static float RepeatInterval(float held, float first, float shortest, float accelPerSecond)
    {
        float k = accelPerSecond > 0f ? accelPerSecond : 1f;
        return MathF.Max(shortest, first * MathF.Pow(k, MathF.Max(0f, held)));
    }

    /// <summary>
    /// 押し続けた時間 held の間に、長押しの連続で何回増減したか（始まりの待ち delay を過ぎてから数える）。
    /// </summary>
    public static int RepeatCount(float held, float delay, float first, float shortest, float accelPerSecond)
    {
        int count = 0;
        float t = delay;
        // 回数は時間に比例する程度なので、単純に積み上げて数える（最短の間隔で上限がある）
        while (t <= held)
        {
            count++;
            t += RepeatInterval(t - delay, first, shortest, accelPerSecond);
        }
        return count;
    }

    /// <summary>表示の文字（書式はインバリアント。書式が壊れていれば一般の書式）。</summary>
    public static string Format(float value, string? format, string? suffix)
    {
        string body;
        try
        {
            body = value.ToString(string.IsNullOrEmpty(format) ? "0.##" : format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            body = value.ToString(CultureInfo.InvariantCulture);
        }
        return body + (suffix ?? string.Empty);
    }

    /// <summary>進捗の割合（0..1 へ収める。NaN は 0）。</summary>
    public static float Progress(float value) => float.IsNaN(value) ? 0f : Math.Clamp(value, 0f, 1f);

    /// <summary>進捗の棒の塗りの幅。</summary>
    public static float BarFillWidth(float progress, float trackWidth) => Progress(progress) * MathF.Max(0f, trackWidth);

    /// <summary>進捗の輪の角度（度。0..360）。</summary>
    public static float RingSweepDegrees(float progress) => Progress(progress) * FullTurnDegrees;

    /// <summary>段階の丸めの誤差を除いて 2 つの値が等しいか。</summary>
    public static bool NearlyEqual(float a, float b) => MathF.Abs(a - b) <= StepEpsilon;
}
