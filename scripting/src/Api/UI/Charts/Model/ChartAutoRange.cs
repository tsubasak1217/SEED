using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ChartAutoRange.cs — 軸の範囲の自動（W2-8。docs/ui_charts.md §4）
//
//  【手順】（値の最小〜最大から）
//    1. 0 を含める（IncludeZero。棒の値の軸）
//    2. 余白を足す: Padding（値の単位）+ 幅 × PaddingFraction。0 を含めた側（0 が端）には足さない（棒は 0 から立つ）
//    3. 最小の幅（MinSpan）より狭ければ中央から広げる（0 が端なら上へだけ）。Wake or Pay の起床時間は「上下 30 分の余白・最小 2 時間の幅」
//       （Flutter 版 wake_time_chart.dart の minuteRange と同じ）で、30 日の朝がほぼ同じ時刻でも山脈のように見えない
//    4. 収める範囲（ClampMin・ClampMax。時刻なら 0〜1440 分）の外へ出たら、幅を保って内側へずらす（それでも入らなければ切る。
//       Flutter 版は切るだけで、0:10 の朝が多いと幅が 2 時間より狭くなっていた）
//    5. 刻みに丸める（NiceBounds。数の軸の棒）は目盛りを選んでから ChartTicks.NiceBounds で行う（呼び出し側）
//  値が 1 つも無ければ Empty（既定の範囲。時刻なら 4:00〜12:00 = Flutter 版と同じ）。
// ============================================================

/// <summary>軸の範囲の自動の決め方。</summary>
public struct AutoRangeOptions
{
    /// <summary>上下に足す余白（値の単位）。</summary>
    public double Padding;
    /// <summary>上下に足す余白（幅に対する割合）。</summary>
    public double PaddingFraction;
    /// <summary>最小の幅（値の単位。0 = なし）。</summary>
    public double MinSpan;
    /// <summary>収める範囲の下限（NaN = なし）。</summary>
    public double ClampMin;
    /// <summary>収める範囲の上限（NaN = なし）。</summary>
    public double ClampMax;
    /// <summary>0 を含める（棒の値の軸）。</summary>
    public bool IncludeZero;
    /// <summary>値が 1 つも無いときの範囲。</summary>
    public ChartRange Empty;

    /// <summary>何もしない決め方（値の最小〜最大そのもの。無ければ 0〜1）。</summary>
    public static AutoRangeOptions Plain => new()
    {
        ClampMin = double.NaN,
        ClampMax = double.NaN,
        Empty = new ChartRange(0, 1),
    };
}

/// <summary>軸の範囲の自動（純粋な計算）。</summary>
public static class ChartAutoRange
{
    /// <summary>幅が 0 になったときに与える幅（値の単位）。</summary>
    private const double DegenerateSpan = 1.0;
    /// <summary>半分。</summary>
    private const double Half = 0.5;

    /// <summary>値の列から軸の範囲を決める（冒頭の手順）。非数・無限の値は読まない。</summary>
    public static ChartRange FromValues(IEnumerable<double> values, in AutoRangeOptions o)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var v in values)
        {
            if (!double.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (min > max) return o.Empty;
        return Shape(min, max, o);
    }

    /// <summary>最小と最大（値の最小〜最大）から軸の範囲を決める（手順 1〜4）。</summary>
    public static ChartRange Shape(double min, double max, in AutoRangeOptions o)
    {
        // 1. 0 を含める
        bool zeroBottom = false, zeroTop = false;
        if (o.IncludeZero)
        {
            if (min >= 0) { min = 0; zeroBottom = true; }
            if (max <= 0) { max = 0; zeroTop = true; }
        }
        // 2. 余白（0 が端の側には足さない）
        double pad = Math.Max(0, o.Padding) + Math.Max(0, o.PaddingFraction) * (max - min);
        if (!zeroBottom) min -= pad;
        if (!zeroTop) max += pad;
        // 3. 最小の幅（0 が端なら反対側へだけ広げる）
        if (o.MinSpan > 0 && max - min < o.MinSpan)
        {
            if (zeroBottom && !zeroTop) max = min + o.MinSpan;
            else if (zeroTop && !zeroBottom) min = max - o.MinSpan;
            else
            {
                double center = (min + max) * Half;
                min = center - o.MinSpan * Half;
                max = center + o.MinSpan * Half;
            }
        }
        // 4. 収める範囲: 幅を保って内側へずらし、それでも入らなければ切る
        if (double.IsFinite(o.ClampMin) && min < o.ClampMin)
        {
            max += o.ClampMin - min;
            min = o.ClampMin;
        }
        if (double.IsFinite(o.ClampMax) && max > o.ClampMax)
        {
            min -= max - o.ClampMax;
            max = o.ClampMax;
        }
        if (double.IsFinite(o.ClampMin) && min < o.ClampMin) min = o.ClampMin;
        if (max <= min) max = min + DegenerateSpan;
        return new ChartRange(min, max);
    }
}
