using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ChartAxis.cs — 軸 1 本の目盛りを決める（書式 → 刻みの候補 → 目盛りの値と文字。W2-8。docs/ui_charts.md §4）
//
//  軸の書式ごとの刻みの選び方:
//    - 時刻（TimeOfDay）… ChartTicks.TimeOfDaySteps（15・30・60・120・180・360・720 分）から、区間の数の上限以下の最初のもの
//    - 日付（Date）   … ChartTicks.DateSteps（1・2・3・7・14・30・61・91・182・365 日）から同じく
//    - 数（Number）   … 1・2・5 × 10^n（NiceStep）
//  刻みの候補は Steps で差し替えられる（データ駆動。例 "1,7,30"）。区間の数の上限は軸の長さ ÷ 文字の最小の間隔
//  （横軸）か、テーマの count.chart_y_intervals（縦軸）。目盛りの位置の起点（Origin）: 日付の軸はデータの最初の日
//  （パンで目盛りが跳ねない）、ほかは 0。
// ============================================================

/// <summary>軸 1 本の目盛りの決め方（書式・刻みの候補・起点）。</summary>
public sealed class ChartAxis
{
    /// <summary>書式。</summary>
    public ChartValueFormat Format = ChartValueFormat.Number;
    /// <summary>刻みの候補（昇順。null・空なら書式の既定）。</summary>
    public IReadOnlyList<double>? Steps;
    /// <summary>目盛りの位置の起点（origin + k × 刻み）。</summary>
    public double Origin;
    /// <summary>文字を独自に作る（null なら書式の既定。値と刻みを受け取る）。</summary>
    public Func<double, double, string>? Formatter;

    /// <summary>今の刻み（最後に Compute したもの）。</summary>
    public double Step { get; private set; } = 1;

    /// <summary>今の目盛りの値（最後に Compute したもの）。</summary>
    public List<double> Values { get; } = new();

    /// <summary>書式の既定の刻みの候補（数は null = NiceStep）。</summary>
    public static IReadOnlyList<double>? DefaultSteps(ChartValueFormat format) => format switch
    {
        ChartValueFormat.TimeOfDay => ChartTicks.TimeOfDaySteps,
        ChartValueFormat.Date => ChartTicks.DateSteps,
        _ => null,
    };

    /// <summary>範囲 <paramref name="range"/> を <paramref name="maxIntervals"/> 以下の区間に分ける刻みを選び、目盛りの値を並べる。</summary>
    public void Compute(ChartRange range, int maxIntervals)
    {
        var steps = Steps is { Count: > 0 } ? Steps : DefaultSteps(Format);
        Step = steps == null ? ChartTicks.NiceStep(range.Span, maxIntervals) : ChartTicks.StepFromCandidates(range.Span, maxIntervals, steps);
        ChartTicks.Generate(range, Step, Origin, Values);
    }

    /// <summary>値の目盛りの文字。</summary>
    public string Label(double value) => Formatter?.Invoke(value, Step) ?? ChartFormat.Format(Format, value, Step);

    /// <summary>刻みの候補の文字（"15,30,60"）を読む（数でないものは飛ばす。空なら null）。</summary>
    public static IReadOnlyList<double>? ParseSteps(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var list = new List<double>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                && v > 0 && double.IsFinite(v))
                list.Add(v);
        }
        list.Sort();
        return list.Count > 0 ? list : null;
    }
}
