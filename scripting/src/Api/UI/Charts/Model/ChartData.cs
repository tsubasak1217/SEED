using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ChartData.cs — グラフに渡すデータの形（W2-8。docs/ui_charts.md §2）
//
//  【値の単位】X・Y は軸の書式（ChartValueFormat）に合わせた数:
//    - 日付の軸（Date）… 日の番号（DateOnly.DayNumber。ChartPoint.Day で作れる）
//    - 時刻の軸（TimeOfDay）… 0 時からの分（7:30 = 450。ChartPoint.Minutes で作れる）
//    - 数の軸（Number）… その数
//  【欠けた値】ChartPoint.Y が null の点は「記録が無い」。折れ線は点を打たず、前後の点を線でつなぐ（既定。GapMode.Connect）か、
//  線を切る（GapMode.Break）。Wake or Pay の起床時間は「記録の無い日は点を打たずに線をつなぐ」。
// ============================================================

/// <summary>折れ線の点（X と Y。Y が null なら欠けた値）。</summary>
public readonly struct ChartPoint
{
    /// <summary>X（軸の単位）。</summary>
    public readonly double X;
    /// <summary>Y（軸の単位。null = 欠けた値）。</summary>
    public readonly double? Y;

    /// <summary>点を作る。</summary>
    public ChartPoint(double x, double? y)
    {
        X = x;
        Y = y;
    }

    /// <summary>値があるか（null でなく有限の数）。</summary>
    public bool HasValue => Y.HasValue && double.IsFinite(Y.Value);

    /// <summary>日付と値の点（X = 日の番号）。</summary>
    public static ChartPoint Day(DateOnly day, double? y) => new(day.DayNumber, y);

    /// <summary>日付と時刻の点（X = 日の番号・Y = 0 時からの分。時刻の軸の起床時間など）。</summary>
    public static ChartPoint Minutes(DateOnly day, TimeSpan? timeOfDay) => new(day.DayNumber, timeOfDay?.TotalMinutes);

    /// <summary>デバッグ表示。</summary>
    public override string ToString() => HasValue ? $"({X:0.###}, {Y!.Value:0.###})" : $"({X:0.###}, -)";
}

/// <summary>棒の 1 本（X と、下から積む値の列。値が 1 つなら積み上げなし）。</summary>
public readonly struct BarDatum
{
    /// <summary>X（軸の単位。日付の軸なら日の番号・順番なら 0, 1, 2…）。</summary>
    public readonly double X;
    /// <summary>下から積む値（系列の順。負・NaN は 0 とみなす）。</summary>
    public readonly IReadOnlyList<double> Values;

    /// <summary>棒を作る。</summary>
    public BarDatum(double x, params double[] values)
    {
        X = x;
        Values = values ?? Array.Empty<double>();
    }

    /// <summary>積んだ合計（負・NaN は 0 とみなす）。</summary>
    public double Total
    {
        get
        {
            double t = 0;
            foreach (var v in Values) t += BarGeometry.Positive(v);
            return t;
        }
    }
}

/// <summary>折れ線の欠けた値の扱い。</summary>
public enum GapMode
{
    /// <summary>前後の点を線でつなぐ（点は打たない。既定）。</summary>
    Connect = 0,
    /// <summary>線を切る。</summary>
    Break = 1,
}

/// <summary>軸の値の書式（目盛りの文字・吹き出し）。</summary>
public enum ChartValueFormat
{
    /// <summary>数（3 桁区切り。小数は刻みに合わせる）。</summary>
    Number = 0,
    /// <summary>時刻（0 時からの分 → H:mm）。</summary>
    TimeOfDay = 1,
    /// <summary>日付（日の番号 → M/d）。</summary>
    Date = 2,
}

/// <summary>棒の向き。</summary>
public enum BarOrientation
{
    /// <summary>縦の棒（X が横・値が縦。既定）。</summary>
    Vertical = 0,
    /// <summary>横の棒（X が縦〈上から下〉・値が横）。</summary>
    Horizontal = 1,
}
