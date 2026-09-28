using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ChartTicks.cs — 目盛りの選び方（切りの良い刻み。W2-8。docs/ui_charts.md §4）
//
//  【2 通りの選び方】
//    - 数（NiceStep）: 1・2・5 × 10^n の刻みのうち、幅を maxIntervals 以下の区間に分ける最小のもの
//      （d3 の ticks・Excel の既定の目盛りと同じ考え方）。例: 幅 0〜1,340 を 4 区間以下 → 500（0・500・1,000）
//    - 候補の列（StepFromCandidates）: 候補（昇順）のうち、幅 ÷ 刻み ≤ maxIntervals になる最初のもの。
//      時刻の軸は 15・30・60・120・180・360・720 分（Wake or Pay の Flutter 版 wake_time_chart.dart の _clockInterval と同じ:
//      「37 分のような刻みにしない」）、日付の軸は 1・2・3・7・14・30・61・91・182・365 日。
//      どの候補でも区間が多すぎるときは、最後の候補の切りの良い倍数（×1・2・5・10…）
//  【目盛りの位置】origin + k × 刻み のうち範囲の中のもの（Generate）。日付の軸は origin をデータの最初の日にするので、
//  パン（横のドラッグ）で見える範囲が動いても目盛りは同じ日に付いたまま（跳ねない）。
// ============================================================

/// <summary>目盛りの刻みと位置の計算（純粋な計算）。</summary>
public static class ChartTicks
{
    /// <summary>区間の数・刻みの比べる許容量（割り算の丸めの誤差で候補を飛ばさない）。</summary>
    private const double RoundingEpsilon = 1e-9;
    /// <summary>切りの良い刻みの仮数（1・2・5・10）。</summary>
    private static readonly double[] NiceMantissas = { 1.0, 2.0, 5.0, 10.0 };
    /// <summary>10 進の底。</summary>
    private const double DecimalBase = 10.0;
    /// <summary>目盛りの数の既定の上限（壊れた刻みで無限に並べない安全弁）。</summary>
    public const int DefaultMaxTicks = 64;

    /// <summary>時刻の軸の刻みの候補（分。Flutter 版の _clockInterval と同じ並び＋半日）。</summary>
    public static readonly double[] TimeOfDaySteps = { 15, 30, 60, 120, 180, 360, 720 };

    /// <summary>日付の軸の刻みの候補（日。1 日・2 日・3 日・1 週・2 週・約 1 か月・2 か月・3 か月・半年・1 年）。</summary>
    public static readonly double[] DateSteps = { 1, 2, 3, 7, 14, 30, 61, 91, 182, 365 };

    /// <summary>
    /// 切りの良い刻み（1・2・5 × 10^n）: 幅 <paramref name="span"/> を <paramref name="maxIntervals"/> 以下の区間に分ける最小のもの。
    /// 幅が 0 以下・非数、区間の数が 1 未満なら 1。
    /// </summary>
    public static double NiceStep(double span, int maxIntervals)
    {
        if (!(span > 0) || !double.IsFinite(span) || maxIntervals < 1) return 1.0;
        double raw = span / maxIntervals;
        double magnitude = Math.Pow(DecimalBase, Math.Floor(Math.Log10(raw)));
        foreach (var m in NiceMantissas)
        {
            double step = m * magnitude;
            if (step * (1.0 + RoundingEpsilon) >= raw) return step;
        }
        return DecimalBase * magnitude;
    }

    /// <summary>
    /// 候補（昇順）から刻みを選ぶ: 幅 ÷ 刻み ≤ <paramref name="maxIntervals"/> になる最初の候補。
    /// どれでも区間が多すぎるときは、最後の候補の切りの良い倍数（NiceStep で 1・2・5・10… 倍）。候補が空なら NiceStep。
    /// </summary>
    public static double StepFromCandidates(double span, int maxIntervals, IReadOnlyList<double> candidates)
    {
        if (candidates == null || candidates.Count == 0) return NiceStep(span, maxIntervals);
        if (!(span > 0) || !double.IsFinite(span) || maxIntervals < 1) return candidates[0] > 0 ? candidates[0] : 1.0;
        foreach (var c in candidates)
        {
            if (c > 0 && span / c <= maxIntervals + RoundingEpsilon) return c;
        }
        double last = candidates[^1] > 0 ? candidates[^1] : 1.0;
        return last * Math.Max(1.0, NiceStep(span / last, maxIntervals));
    }

    /// <summary>
    /// 目盛りの位置（<paramref name="origin"/> + k × <paramref name="step"/> のうち範囲の中のもの。両端を含む）を
    /// <paramref name="into"/> へ入れる（中身は置き換える）。刻みが正でない・範囲が有限でないときは空。
    /// </summary>
    /// <returns>入れた数。</returns>
    public static int Generate(ChartRange range, double step, double origin, List<double> into, int maxCount = DefaultMaxTicks)
    {
        into.Clear();
        if (!(step > 0) || !double.IsFinite(step) || !range.IsFinite || !double.IsFinite(origin)) return 0;
        double first = Math.Ceiling((range.Min - origin) / step - RoundingEpsilon);
        double tolerance = step * RoundingEpsilon;
        for (double k = first; into.Count < maxCount; k++)
        {
            double v = origin + k * step;
            if (v > range.Max + tolerance) break;
            // 丸めの誤差（0.30000000000000004 など）は刻みの倍数へ寄せる
            into.Add(origin + Math.Round((v - origin) / step) * step);
        }
        return into.Count;
    }

    /// <summary>範囲を刻みの倍数へ外向きに丸める（下は切り下げ・上は切り上げ。origin からの倍数）。</summary>
    public static ChartRange NiceBounds(ChartRange range, double step, double origin = 0.0)
    {
        if (!(step > 0) || !range.IsFinite) return range;
        double lo = origin + Math.Floor((range.Min - origin) / step + RoundingEpsilon) * step;
        double hi = origin + Math.Ceiling((range.Max - origin) / step - RoundingEpsilon) * step;
        if (hi <= lo) hi = lo + step;
        return new ChartRange(lo, hi);
    }

    /// <summary>
    /// 軸の長さ（dp）と文字の最小の間隔（dp）から、置ける区間の数（少なくとも 1）。
    /// 例: 横の軸 300 dp・文字の間隔 48 dp → 6 区間。
    /// </summary>
    public static int IntervalsForLength(float lengthDp, float minSpacingDp)
    {
        if (!(lengthDp > 0) || !(minSpacingDp > 0)) return 1;
        return Math.Max(1, (int)Math.Floor(lengthDp / minSpacingDp));
    }
}
