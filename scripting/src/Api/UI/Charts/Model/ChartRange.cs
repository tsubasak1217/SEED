using System;

namespace SEED.UI;

// ============================================================
//  ChartRange.cs — グラフの軸の値の範囲（W2-8。docs/ui_charts.md §3）
//
//  【役割】軸の最小と最大（値の単位: 日付の軸なら日の番号、時刻の軸なら 0 時からの分、数の軸ならその数）。
//  割合への変換（Fraction）とその逆（Lerp）、範囲へ収める（Clamp）を 1 か所にまとめる。幅 0 の範囲で割らないよう、
//  割り算には SafeSpan（幅が 0 なら 1）を使う。エンジンに触れない純粋な値（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>軸の値の範囲（Min ≤ Max）。</summary>
public readonly struct ChartRange : IEquatable<ChartRange>
{
    /// <summary>幅を 0 とみなす大きさ（これ以下の幅では割らない）。</summary>
    private const double SpanEpsilon = 1e-12;

    /// <summary>最小。</summary>
    public readonly double Min;
    /// <summary>最大。</summary>
    public readonly double Max;

    /// <summary>範囲を作る（逆順なら入れ替える）。</summary>
    public ChartRange(double min, double max)
    {
        if (max < min) (min, max) = (max, min);
        Min = min;
        Max = max;
    }

    /// <summary>幅（Max − Min）。</summary>
    public double Span => Max - Min;

    /// <summary>割り算に使う幅（幅が 0 なら 1。0 で割らない）。</summary>
    public double SafeSpan => Span > SpanEpsilon ? Span : 1.0;

    /// <summary>両端が有限の数か。</summary>
    public bool IsFinite => double.IsFinite(Min) && double.IsFinite(Max);

    /// <summary>値が範囲の中か（両端を含む）。</summary>
    public bool Contains(double v) => v >= Min && v <= Max;

    /// <summary>値を範囲へ収める。</summary>
    public double Clamp(double v) => v < Min ? Min : (v > Max ? Max : v);

    /// <summary>値の範囲の中の割合（Min で 0・Max で 1。外は 0..1 の外）。</summary>
    public double Fraction(double v) => (v - Min) / SafeSpan;

    /// <summary>割合 → 値（Fraction の逆）。</summary>
    public double Lerp(double t) => Min + t * Span;

    /// <summary>両側へ <paramref name="amount"/> だけ広げた範囲。</summary>
    public ChartRange Expand(double amount) => new(Min - amount, Max + amount);

    /// <inheritdoc />
    public bool Equals(ChartRange other) => Min.Equals(other.Min) && Max.Equals(other.Max);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ChartRange r && Equals(r);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Min, Max);

    /// <summary>等しいか。</summary>
    public static bool operator ==(ChartRange a, ChartRange b) => a.Equals(b);

    /// <summary>等しくないか。</summary>
    public static bool operator !=(ChartRange a, ChartRange b) => !a.Equals(b);

    /// <summary>デバッグ表示（例: [420, 600]）。</summary>
    public override string ToString() => $"[{Min:0.###}, {Max:0.###}]";
}
