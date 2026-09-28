using System;

namespace SEED.UI;

// ============================================================
//  ChartHit.cs — グラフの当たり（最寄りの点・棒の列。W2-8。docs/ui_charts.md §6）
//
//  - 最寄りの点（Nearest）: 押した位置から横の距離が maxDx 以内の点のうち、横の距離が最も近いもの（同じなら縦の距離が近いもの）。
//    fl_chart の既定（横の距離で選ぶ）と同じ考え方で、指で押しても点の真上を狙わなくてよい。maxDx は既定で 24 dp（48 dp の半分）
//  - 棒の列（SlotAt）: 列の中心 = 最初の X + i × 間隔、列の幅 = 間隔。押した位置の X がどの列の幅の中か（縦は問わない＝
//    空の高さまで当たり判定。Wake or Pay の「列全体が当たり判定」）。
// ============================================================

/// <summary>グラフの当たりの計算（純粋な計算）。</summary>
public static class ChartHit
{
    /// <summary>半分。</summary>
    private const double Half = 0.5;

    /// <summary>
    /// 最寄りの点の添字（横の距離が <paramref name="maxDx"/> 以内で最も近いもの。同じなら縦の距離が近いもの）。無ければ −1。
    /// </summary>
    /// <param name="points">点（描く位置）。順は問わない。</param>
    /// <param name="at">押した位置（同じ空間）。</param>
    /// <param name="maxDx">横の距離の上限（同じ空間の単位）。</param>
    public static int Nearest(ReadOnlySpan<Vector2> points, Vector2 at, float maxDx)
    {
        int best = -1;
        float bestDx = float.PositiveInfinity, bestDy = float.PositiveInfinity;
        for (int i = 0; i < points.Length; i++)
        {
            float dx = MathF.Abs(points[i].x - at.x);
            if (dx > maxDx) continue;
            float dy = MathF.Abs(points[i].y - at.y);
            if (dx < bestDx || (dx == bestDx && dy < bestDy))
            {
                best = i;
                bestDx = dx;
                bestDy = dy;
            }
        }
        return best;
    }

    /// <summary>
    /// X の値 <paramref name="x"/> がどの列の幅の中か（列 i の中心 = <paramref name="first"/> + i × <paramref name="pitch"/>、幅 = 間隔）。
    /// 範囲の外・間隔が正でないときは −1。
    /// </summary>
    public static int SlotAt(double x, double first, double pitch, int count)
    {
        if (!(pitch > 0) || count <= 0 || !double.IsFinite(x)) return -1;
        double i = Math.Floor((x - first) / pitch + Half);
        return i >= 0 && i < count ? (int)i : -1;
    }

    /// <summary>
    /// 昇順の X の列 <paramref name="xs"/> のうち、<paramref name="x"/> に最も近く、距離が <paramref name="maxDistance"/> 以内のものの添字
    /// （棒の列の当たり: 列の幅の半分以内）。無ければ −1。同じ距離なら小さい添字。
    /// </summary>
    public static int NearestX(ReadOnlySpan<double> xs, double x, double maxDistance)
    {
        if (xs.Length == 0 || !double.IsFinite(x)) return -1;
        // 二分探索で x 以上の最初の位置
        int lo = 0, hi = xs.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (xs[mid] < x) lo = mid + 1;
            else hi = mid;
        }
        int best = -1;
        double bestD = double.PositiveInfinity;
        for (int i = Math.Max(0, lo - 1); i <= Math.Min(xs.Length - 1, lo); i++)
        {
            double d = Math.Abs(xs[i] - x);
            if (d <= maxDistance && d < bestD)
            {
                best = i;
                bestD = d;
            }
        }
        return best;
    }
}
