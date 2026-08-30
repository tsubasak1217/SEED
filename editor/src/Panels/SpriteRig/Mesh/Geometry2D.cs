using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.SpriteRig.Mesh;

/// <summary>
/// 2D 幾何の共通述語（面積・内外判定・距離・交差）。
///
/// 輪郭抽出・簡略化・三角分割のいずれもここの述語だけを使い、判定規則を 1 箇所に集約する。
/// 座標系は「+X 右・+Y 下」なので、符号付き面積が正 = 画面上では時計回りである点に注意。
/// 本プロジェクトでは <b>外周輪郭 = 正の符号付き面積</b>、<b>穴 = 負</b>を不変条件とする
/// （既存 <c>.sprite_mesh</c> の矩形サンプル [0,0]→[w,0]→[w,h]→[0,h] と同じ向き）。
/// </summary>
public static class Geometry2D
{
    /// <summary>座標比較に使う既定の許容誤差（ピクセル）。</summary>
    public const double Epsilon = 1.0e-9;

    /// <summary>面積比較に使う許容誤差（ピクセル^2）。</summary>
    public const double AreaEpsilon = 1.0e-12;

    /// <summary>
    /// 閉多角形の符号付き面積を返す。正 = 外周向き（画面上の時計回り）、負 = 穴向き。
    /// </summary>
    /// <param name="polygon">閉じた頂点列（末尾と先頭を暗黙に結ぶ。3 点未満なら 0）。</param>
    public static double SignedArea(IReadOnlyList<Vec2> polygon)
    {
        int n = polygon.Count;
        if (n < 3) return 0.0;

        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            Vec2 a = polygon[i];
            Vec2 b = polygon[(i + 1) % n];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum * 0.5;
    }

    /// <summary>
    /// 3 点が作る三角形の符号付き面積の 2 倍。向き判定に使う（正 = 外周向き）。
    /// </summary>
    public static double Cross3(Vec2 a, Vec2 b, Vec2 c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    /// <summary>
    /// 点が閉多角形の内部にあるかを判定する（レイキャスティング法・境界上は不定）。
    /// 穴を含む領域の判定は <see cref="IsInsideRegion"/> を使う。
    /// </summary>
    /// <param name="polygon">閉じた頂点列。</param>
    /// <param name="p">判定する点。</param>
    public static bool PointInPolygon(IReadOnlyList<Vec2> polygon, Vec2 p)
    {
        int n = polygon.Count;
        if (n < 3) return false;

        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vec2 a = polygon[i];
            Vec2 b = polygon[j];
            // 点の Y 高さを跨ぐ辺だけを対象に、交点の X が点より右かどうかで反転させる
            bool straddles = (a.Y > p.Y) != (b.Y > p.Y);
            if (!straddles) continue;

            double t = (p.Y - a.Y) / (b.Y - a.Y);
            double xCross = a.X + t * (b.X - a.X);
            if (p.X < xCross) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// 「外周 1 本 + 穴 0 本以上」で表される領域の内部かどうかを判定する。
    /// </summary>
    /// <param name="outer">外周輪郭。</param>
    /// <param name="holes">穴輪郭の列（null 可）。</param>
    /// <param name="p">判定する点。</param>
    public static bool IsInsideRegion(
        IReadOnlyList<Vec2> outer,
        IReadOnlyList<IReadOnlyList<Vec2>>? holes,
        Vec2 p)
    {
        if (!PointInPolygon(outer, p)) return false;
        if (holes == null) return true;

        foreach (var hole in holes)
        {
            if (PointInPolygon(hole, p)) return false;
        }
        return true;
    }

    /// <summary>
    /// 点が三角形の内部にあるかを判定する（既定は境界を含まない厳密内部）。
    /// 耳の切り出し判定で「他頂点が耳の中に入っていないか」を見るのに使う。
    /// </summary>
    /// <param name="a">三角形の頂点 1。</param>
    /// <param name="b">三角形の頂点 2。</param>
    /// <param name="c">三角形の頂点 3。</param>
    /// <param name="p">判定する点。</param>
    /// <param name="tolerance">境界とみなす許容幅（面積の 2 倍スケール）。</param>
    public static bool PointInTriangle(Vec2 a, Vec2 b, Vec2 c, Vec2 p, double tolerance = Epsilon)
    {
        double d1 = Cross3(a, b, p);
        double d2 = Cross3(b, c, p);
        double d3 = Cross3(c, a, p);

        bool anyNegative = d1 < -tolerance || d2 < -tolerance || d3 < -tolerance;
        bool anyPositive = d1 > tolerance || d2 > tolerance || d3 > tolerance;
        // すべて同符号（＝どの辺から見ても同じ側）なら内部
        return !(anyNegative && anyPositive);
    }

    /// <summary>
    /// 線分 ab と点 p の最短距離。内部点の間引き（輪郭から離す）に使う。
    /// </summary>
    public static double DistancePointSegment(Vec2 a, Vec2 b, Vec2 p)
    {
        Vec2 ab = b - a;
        double lenSq = ab.LengthSquared;
        if (lenSq < Epsilon) return Vec2.Distance(a, p);

        // 線分上への射影パラメータを [0,1] へクランプしてから距離を測る
        double t = Vec2.Dot(p - a, ab) / lenSq;
        t = Math.Clamp(t, 0.0, 1.0);
        Vec2 proj = a + ab * t;
        return Vec2.Distance(proj, p);
    }

    /// <summary>
    /// 閉多角形の全辺に対する点の最短距離。
    /// </summary>
    public static double DistanceToPolygonEdges(IReadOnlyList<Vec2> polygon, Vec2 p)
    {
        int n = polygon.Count;
        if (n == 0) return double.PositiveInfinity;
        if (n == 1) return Vec2.Distance(polygon[0], p);

        double best = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            double d = DistancePointSegment(polygon[i], polygon[(i + 1) % n], p);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>
    /// 多角形の向きを指定符号へ揃える（必要なら頂点列を反転した新しいリストを返す）。
    /// </summary>
    /// <param name="polygon">対象の頂点列。</param>
    /// <param name="wantPositiveArea">true = 外周向き（正の面積）にする。false = 穴向き。</param>
    public static List<Vec2> EnsureOrientation(IReadOnlyList<Vec2> polygon, bool wantPositiveArea)
    {
        var result = new List<Vec2>(polygon);
        double area = SignedArea(result);
        bool isPositive = area > 0.0;
        if (isPositive != wantPositiveArea) result.Reverse();
        return result;
    }

    /// <summary>
    /// 2 つの線分が交差するか（端点の共有は交差とみなさない）。
    /// 穴のブリッジ辺が既存の辺を跨がないかの検査に使う。
    /// </summary>
    public static bool SegmentsProperlyIntersect(Vec2 p1, Vec2 p2, Vec2 q1, Vec2 q2)
    {
        double d1 = Cross3(q1, q2, p1);
        double d2 = Cross3(q1, q2, p2);
        double d3 = Cross3(p1, p2, q1);
        double d4 = Cross3(p1, p2, q2);

        // 厳密に互いを跨いでいる場合のみ true（端点接触・共線は false）
        bool straddle1 = (d1 > Epsilon && d2 < -Epsilon) || (d1 < -Epsilon && d2 > Epsilon);
        bool straddle2 = (d3 > Epsilon && d4 < -Epsilon) || (d3 < -Epsilon && d4 > Epsilon);
        return straddle1 && straddle2;
    }
}
