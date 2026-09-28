using System;

namespace SEED.UI;

// ============================================================
//  ChartFling.cs — 横に払った後の慣性（摩擦で減速。W2-8。docs/ui_charts.md §5）
//
//  Flutter の FrictionSimulation と同じ式: 位置 x(t) = x0 + v0 (drag^t − 1) / ln(drag)、速度 v(t) = v0 · drag^t。
//  drag は 1 秒で速度が何倍になるか（0 と 1 の間。既定は BouncingScrollSimulation と同じ 0.135 ＝ テーマの ratio.chart_fling_drag）。
//  止まる時刻は速度が stopSpeed まで落ちた時: t = ln(stopSpeed / |v0|) / ln(drag)。止まるまでの距離は v0 (drag^T − 1) / ln(drag)。
//  時刻からの閉じた式なのでフレームの刻みに依らない（60 fps でも 30 fps でも同じ位置で止まる）。
// ============================================================

/// <summary>摩擦で減速する慣性（純粋な計算）。</summary>
public readonly struct ChartFling
{
    /// <summary>始めた位置（値の単位）。</summary>
    public readonly double Origin;
    /// <summary>始めの速度（値の単位/秒）。</summary>
    public readonly double Velocity;
    /// <summary>1 秒で速度が何倍になるか（0 と 1 の間）。</summary>
    public readonly double Drag;
    /// <summary>止まる時刻（秒）。</summary>
    public readonly double Duration;

    /// <summary>drag の下限・上限（0 と 1 そのものは式が壊れるので避ける）。</summary>
    private const double MinDrag = 1e-6;
    private const double MaxDrag = 1.0 - 1e-6;

    /// <summary>慣性を始める。</summary>
    /// <param name="origin">始めた位置（値の単位）。</param>
    /// <param name="velocity">始めの速度（値の単位/秒）。</param>
    /// <param name="drag">1 秒で速度が何倍になるか（0 と 1 の間へ収める）。</param>
    /// <param name="stopSpeed">これより遅くなったら止まる（値の単位/秒）。</param>
    public ChartFling(double origin, double velocity, double drag, double stopSpeed)
    {
        Origin = origin;
        Velocity = double.IsFinite(velocity) ? velocity : 0;
        Drag = Math.Clamp(double.IsFinite(drag) ? drag : MinDrag, MinDrag, MaxDrag);
        Duration = StopTime(Velocity, Drag, stopSpeed);
    }

    /// <summary>時刻 t（秒）の位置。</summary>
    public double PositionAt(double t) => Origin + Offset(Velocity, Drag, Math.Clamp(t, 0, Duration));

    /// <summary>時刻 t（秒）の速度（止まった後は 0）。</summary>
    public double VelocityAt(double t) => t >= Duration ? 0 : Velocity * Math.Pow(Drag, Math.Max(0, t));

    /// <summary>止まったか。</summary>
    public bool IsDone(double t) => t >= Duration;

    /// <summary>始めから時刻 t までに進む距離（FrictionSimulation の x(t) − x0）。</summary>
    public static double Offset(double v0, double drag, double t) => v0 * (Math.Pow(drag, t) - 1.0) / Math.Log(drag);

    /// <summary>速度が stopSpeed まで落ちる時刻（始めから遅ければ 0）。</summary>
    public static double StopTime(double v0, double drag, double stopSpeed)
    {
        double speed = Math.Abs(v0);
        if (!(stopSpeed > 0) || speed <= stopSpeed) return 0;
        return Math.Log(stopSpeed / speed) / Math.Log(drag);
    }
}
