using System;
using System.Globalization;

namespace SEED.UI;

// ============================================================
//  ChartFormat.cs — 目盛りと吹き出しの文字の書式（W2-8。docs/ui_charts.md §4）
//
//  - 時刻（TimeOfDay）: 0 時からの分 → "H:mm"（7:05。Wake or Pay の縦軸）。時をゼロ詰めする "HH:mm" も選べる（吹き出しの 07:05）。
//    範囲は 0〜1440 分（1440 は "24:00"。Flutter 版は 23:59 に丸めていたが、目盛りの 24:00 が 23:59 と出るのを避ける）。
//  - 日付（Date）: 日の番号（DateOnly.DayNumber）→ "M/d"（9/28）。
//  - 数（Number）: 3 桁区切り。小数の桁は刻みに合わせる（刻み 0.25 → 2 桁、刻み 5 → 0 桁）。-0 は 0。
//  文化に依らない（InvariantCulture）。
// ============================================================

/// <summary>グラフの文字の書式（純粋な計算）。</summary>
public static class ChartFormat
{
    /// <summary>1 時間の分。</summary>
    public const int MinutesPerHour = 60;
    /// <summary>1 日の分。</summary>
    public const int MinutesPerDay = 24 * MinutesPerHour;
    /// <summary>小数の桁の上限（刻みがとても細かくても長い文字にしない）。</summary>
    private const int MaxDecimals = 6;
    /// <summary>0 とみなす大きさ（刻みに対する割合。-0.000001 を "-0" にしない）。</summary>
    private const double ZeroFraction = 1e-9;

    /// <summary>0 時からの分 → "H:mm"（<paramref name="padHour"/> なら "HH:mm"）。0〜1440 分へ収める（1440 は 24:00）。</summary>
    public static string TimeOfDay(double minutes, bool padHour = false)
    {
        if (!double.IsFinite(minutes)) return "-";
        int m = (int)Math.Round(Math.Clamp(minutes, 0, MinutesPerDay));
        int h = m / MinutesPerHour, mm = m % MinutesPerHour;
        return padHour
            ? $"{h.ToString("00", CultureInfo.InvariantCulture)}:{mm.ToString("00", CultureInfo.InvariantCulture)}"
            : $"{h.ToString(CultureInfo.InvariantCulture)}:{mm.ToString("00", CultureInfo.InvariantCulture)}";
    }

    /// <summary>日の番号（DateOnly.DayNumber）→ "M/d"。範囲の外は "-"。</summary>
    public static string Date(double dayNumber)
    {
        if (!TryDay(dayNumber, out var day)) return "-";
        return $"{day.Month.ToString(CultureInfo.InvariantCulture)}/{day.Day.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>日の番号 → DateOnly（範囲の外・非数は false）。</summary>
    public static bool TryDay(double dayNumber, out DateOnly day)
    {
        day = default;
        if (!double.IsFinite(dayNumber)) return false;
        double n = Math.Round(dayNumber);
        if (n < DateOnly.MinValue.DayNumber || n > DateOnly.MaxValue.DayNumber) return false;
        day = DateOnly.FromDayNumber((int)n);
        return true;
    }

    /// <summary>数 → 3 桁区切り（小数の桁は刻み <paramref name="step"/> に合わせる。刻み ≤ 0 なら 2 桁まで）。</summary>
    public static string Number(double value, double step)
    {
        if (!double.IsFinite(value)) return "-";
        int decimals = DecimalsFor(step);
        double zero = (step > 0 ? step : 1.0) * ZeroFraction;
        if (Math.Abs(value) < zero) value = 0;
        string pattern = decimals == 0 ? "#,0" : "#,0." + new string('0', decimals);
        return value.ToString(pattern, CultureInfo.InvariantCulture);
    }

    /// <summary>刻みに合う小数の桁（刻み 1 以上 → 0、0.5 → 1、0.25 → 2。刻みが無ければ 2）。</summary>
    public static int DecimalsFor(double step)
    {
        if (!(step > 0) || !double.IsFinite(step)) return NoStepDecimals;
        int d = 0;
        double s = step;
        // 刻みが整数になるまで 10 倍する（0.25 → 2.5 → 25 で 2 桁。浮動小数の誤差は IntegerTolerance まで許す）
        while (d < MaxDecimals && Math.Abs(s - Math.Round(s)) > IntegerTolerance * Math.Max(1.0, Math.Abs(s)))
        {
            s *= DecimalScale;
            d++;
        }
        return d;
    }

    /// <summary>刻みが無いときの小数の桁。</summary>
    private const int NoStepDecimals = 2;
    /// <summary>整数とみなす誤差（相対）。</summary>
    private const double IntegerTolerance = 1e-6;
    /// <summary>1 桁ずらす倍率。</summary>
    private const double DecimalScale = 10.0;

    /// <summary>書式に合わせて値を文字にする（目盛りの文字）。</summary>
    public static string Format(ChartValueFormat format, double value, double step) => format switch
    {
        ChartValueFormat.TimeOfDay => TimeOfDay(value),
        ChartValueFormat.Date => Date(value),
        _ => Number(value, step),
    };
}
