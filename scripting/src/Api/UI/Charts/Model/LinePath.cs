using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  LinePath.cs — 系列の点 → 描く点の列（欠けた値・見える範囲・塊。W2-8。docs/ui_charts.md §3）
//
//  【Build】X の昇順の点から、見える範囲 ± 1 点（線が面の端まで届くように外側の 1 点を含める）の値のある点を
//  描く位置へ直し、つながった区間（run）ごとに並べる。欠けた値（Y = null）は Connect なら飛ばして前後をつなぎ（点は打たない）、
//  Break なら区間を切る。見える範囲の外の点は作らない（365 点の全期間を 6 倍に拡大しても、描くのは見える約 60 点＋両端）。
//  【塊】SEED.Draw の 1 図形の点の上限（1024）を超える区間は塊に分ける。隣の塊とは 1 点重ねる（折れ目の丸が欠けない）。
// ============================================================

/// <summary>系列の点 → 描く点の列（純粋な計算）。</summary>
public static class LinePath
{
    /// <summary>
    /// 見える範囲の点を描く位置へ直し、区間ごとに <paramref name="points"/> へ入れる（中身は置き換える）。
    /// 区間 r の点は <paramref name="runStarts"/>[r] から次の区間の頭（最後は points の終わり）まで。
    /// <paramref name="sourceIndex"/> には描く点ごとの元の添字（吹き出しの点を引くため）。
    /// </summary>
    /// <param name="series">点（X の昇順）。</param>
    /// <param name="map">値 → 位置（X は見える範囲）。</param>
    /// <param name="gaps">欠けた値の扱い。</param>
    public static void Build(
        IReadOnlyList<ChartPoint> series, in ChartMapping map, GapMode gaps,
        List<Vector2> points, List<int> runStarts, List<int> sourceIndex)
    {
        points.Clear();
        runStarts.Clear();
        sourceIndex.Clear();
        int n = series.Count;
        if (n == 0) return;
        // 見える範囲の最初と最後（外側の 1 点を含める。値のある点で数える）
        int first = FirstIndexAtOrAfter(series, map.X.Min);
        int last = LastIndexAtOrBefore(series, map.X.Max);
        first = StepToValue(series, first - 1, -1, 0);
        last = StepToValue(series, last + 1, +1, n - 1);
        bool inRun = false;
        for (int i = Math.Max(0, first); i <= Math.Min(n - 1, last); i++)
        {
            var p = series[i];
            if (!p.HasValue)
            {
                if (gaps == GapMode.Break) inRun = false;
                continue;
            }
            if (!inRun)
            {
                runStarts.Add(points.Count);
                inRun = true;
            }
            points.Add(map.ToLocal(p.X, p.Y!.Value));
            sourceIndex.Add(i);
        }
    }

    /// <summary>X が <paramref name="x"/> 以上の最初の添字（無ければ Count）。X の昇順を前提に二分探索。</summary>
    public static int FirstIndexAtOrAfter(IReadOnlyList<ChartPoint> series, double x)
    {
        int lo = 0, hi = series.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (series[mid].X < x) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>X が <paramref name="x"/> 以下の最後の添字（無ければ −1）。</summary>
    public static int LastIndexAtOrBefore(IReadOnlyList<ChartPoint> series, double x)
    {
        int lo = 0, hi = series.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (series[mid].X <= x) lo = mid + 1;
            else hi = mid;
        }
        return lo - 1;
    }

    /// <summary><paramref name="start"/> から <paramref name="step"/> の向きへ、値のある最初の点の添字（無ければ端 <paramref name="bound"/>）。</summary>
    private static int StepToValue(IReadOnlyList<ChartPoint> series, int start, int step, int bound)
    {
        for (int i = start; i >= 0 && i < series.Count; i += step)
        {
            if (series[i].HasValue) return i;
        }
        return bound;
    }

    /// <summary>区間 r の点の数。</summary>
    public static int RunLength(List<int> runStarts, int pointCount, int r)
        => (r + 1 < runStarts.Count ? runStarts[r + 1] : pointCount) - runStarts[r];

    /// <summary>点の数 <paramref name="count"/> の区間を上限 <paramref name="maxPoints"/> の塊に分けたときの塊の数（隣と 1 点重ねる）。</summary>
    public static int ChunkCount(int count, int maxPoints)
    {
        if (count <= 0) return 0;
        if (maxPoints < 2 || count <= maxPoints) return 1;
        // 塊ごとに (maxPoints − 1) 点ずつ進む（1 点重ねる）
        return (int)Math.Ceiling((count - 1) / (double)(maxPoints - 1));
    }

    /// <summary>塊 <paramref name="index"/> の (最初の添字, 点の数)。</summary>
    public static (int Start, int Length) Chunk(int count, int maxPoints, int index)
    {
        if (maxPoints < 2 || count <= maxPoints) return (0, count);
        int start = index * (maxPoints - 1);
        return (start, Math.Min(maxPoints, count - start));
    }
}
