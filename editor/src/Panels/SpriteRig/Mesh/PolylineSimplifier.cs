using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.SpriteRig.Mesh;

/// <summary>
/// Douglas-Peucker 法による折れ線・閉多角形の簡略化。
///
/// 輪郭抽出が返す階段状ポリゴン（1 ピクセルごとに頂点が立つ）をそのまま三角分割すると
/// 頂点が数千個になるため、許容誤差 <c>tolerance</c>（ピクセル）以内で頂点を間引く。
///
/// 閉多角形は「始点をどこに置くか」で結果が変わるので、
/// <b>重心から最も遠い点</b>と<b>そこから最も遠い点</b>の 2 点をアンカーに固定し、
/// 2 本の開いた折れ線に分けてから簡略化する（始点位置に依存しない安定した結果になる）。
///
/// 簡略化は必ず「安全側フォールバック」を持つ:
/// 結果が 3 点未満になる／面積の符号が反転する場合は、元の輪郭をそのまま返す。
/// これにより、透過が無い画像（＝輪郭が画像枠の矩形）でも破綻しない。
/// </summary>
public static class PolylineSimplifier
{
    /// <summary>多角形として成立する最小頂点数。</summary>
    private const int MinPolygonVertices = 3;

    /// <summary>
    /// 閉多角形を簡略化する。
    /// </summary>
    /// <param name="polygon">閉じた頂点列。</param>
    /// <param name="tolerance">許容誤差（ピクセル）。0 以下なら簡略化しない。</param>
    /// <returns>簡略化後の頂点列（元と同じ向き）。</returns>
    public static List<Vec2> SimplifyClosed(IReadOnlyList<Vec2> polygon, double tolerance)
    {
        int n = polygon.Count;
        if (tolerance <= 0.0 || n <= MinPolygonVertices) return new List<Vec2>(polygon);

        // ── アンカー 1: 重心から最も遠い点 ──
        Vec2 centroid = Vec2.Zero;
        for (int i = 0; i < n; i++) centroid += polygon[i];
        centroid /= n;
        int anchorA = FarthestIndexFrom(polygon, centroid);

        // ── アンカー 2: アンカー 1 から最も遠い点 ──
        int anchorB = FarthestIndexFrom(polygon, polygon[anchorA]);
        if (anchorA == anchorB) return new List<Vec2>(polygon);

        // ── リングをアンカーで 2 本の開いた折れ線に割る ──
        var firstHalf = ExtractRingSegment(polygon, anchorA, anchorB);
        var secondHalf = ExtractRingSegment(polygon, anchorB, anchorA);

        var simplifiedFirst = SimplifyOpen(firstHalf, tolerance);
        var simplifiedSecond = SimplifyOpen(secondHalf, tolerance);

        // 連結時にアンカーが重複しないよう、各半分の末尾（次の半分の先頭）を落とす
        var result = new List<Vec2>(simplifiedFirst.Count + simplifiedSecond.Count);
        for (int i = 0; i < simplifiedFirst.Count - 1; i++) result.Add(simplifiedFirst[i]);
        for (int i = 0; i < simplifiedSecond.Count - 1; i++) result.Add(simplifiedSecond[i]);

        // ── 安全側フォールバック ──
        if (result.Count < MinPolygonVertices) return new List<Vec2>(polygon);
        double originalArea = Geometry2D.SignedArea(polygon);
        double simplifiedArea = Geometry2D.SignedArea(result);
        bool sameSign = Math.Sign(originalArea) == Math.Sign(simplifiedArea);
        if (!sameSign || Math.Abs(simplifiedArea) < Geometry2D.AreaEpsilon)
            return new List<Vec2>(polygon);

        return result;
    }

    /// <summary>
    /// 開いた折れ線を Douglas-Peucker で簡略化する（両端は必ず残る）。
    /// </summary>
    /// <param name="points">折れ線の頂点列。</param>
    /// <param name="tolerance">許容誤差（ピクセル）。</param>
    public static List<Vec2> SimplifyOpen(IReadOnlyList<Vec2> points, double tolerance)
    {
        int n = points.Count;
        if (n <= 2 || tolerance <= 0.0) return new List<Vec2>(points);

        var keep = new bool[n];
        keep[0] = true;
        keep[n - 1] = true;

        // 再帰の代わりに明示スタックを使う（数千点の輪郭でもスタック溢れしない）
        var stack = new Stack<(int Start, int End)>();
        stack.Push((0, n - 1));

        while (stack.Count > 0)
        {
            var (start, end) = stack.Pop();
            if (end - start < 2) continue;

            // 区間の両端を結ぶ線分から最も離れた点を探す
            double maxDistance = -1.0;
            int maxIndex = -1;
            for (int i = start + 1; i < end; i++)
            {
                double d = Geometry2D.DistancePointSegment(points[start], points[end], points[i]);
                if (d > maxDistance)
                {
                    maxDistance = d;
                    maxIndex = i;
                }
            }

            // 許容誤差を超える点があれば残し、その点で区間を割ってさらに調べる
            if (maxIndex >= 0 && maxDistance > tolerance)
            {
                keep[maxIndex] = true;
                stack.Push((start, maxIndex));
                stack.Push((maxIndex, end));
            }
        }

        var result = new List<Vec2>();
        for (int i = 0; i < n; i++)
        {
            if (keep[i]) result.Add(points[i]);
        }
        return result;
    }

    /// <summary>指定点から最も遠い頂点の添字を返す。</summary>
    private static int FarthestIndexFrom(IReadOnlyList<Vec2> polygon, Vec2 origin)
    {
        int best = 0;
        double bestDistance = -1.0;
        for (int i = 0; i < polygon.Count; i++)
        {
            double d = Vec2.DistanceSquared(polygon[i], origin);
            if (d > bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// リング上を <paramref name="from"/> から <paramref name="to"/> まで
    /// 前進方向に辿った開いた頂点列を切り出す（両端を含む）。
    /// </summary>
    private static List<Vec2> ExtractRingSegment(IReadOnlyList<Vec2> ring, int from, int to)
    {
        int n = ring.Count;
        var segment = new List<Vec2>();
        int i = from;
        while (true)
        {
            segment.Add(ring[i]);
            if (i == to) break;
            i = (i + 1) % n;
        }
        return segment;
    }
}
