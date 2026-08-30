using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.SpriteRig.Mesh;

/// <summary>
/// 三角形リストから輪郭（境界ループ）を復元するユーティリティ。
///
/// 既存の <c>.sprite_mesh</c> を開き直すとき、ファイルには頂点と三角形しか無く、
/// 編集モデルが必要とする「輪郭ポリゴン + 内部点」が入っていない。
/// そこで <b>片側にしか三角形が無い辺（＝境界辺）</b>を繋いで輪郭を復元し、
/// どの輪郭にも乗っていない頂点を内部点として拾う。
///
/// 三角形の向き（正の符号付き面積）を保ったまま繋ぐので、
/// 外周ループは正・穴ループは負の面積になり、そのまま編集モデルの規約と一致する。
/// </summary>
public static class MeshTopology
{
    /// <summary>
    /// 境界ループの復元結果。
    /// </summary>
    /// <param name="Polygons">復元された輪郭ポリゴン。</param>
    /// <param name="InteriorPoints">どの輪郭にも属さない頂点（内部点）。</param>
    public sealed record BoundaryResult(List<SpriteRigPolygon> Polygons, List<Vec2> InteriorPoints);

    /// <summary>
    /// 頂点と三角形から輪郭ポリゴンと内部点を復元する。
    /// </summary>
    /// <param name="vertices">頂点座標。</param>
    /// <param name="triangles">三角形インデックス（3 個 1 組）。</param>
    public static BoundaryResult ExtractBoundary(IReadOnlyList<Vec2> vertices, IReadOnlyList<int> triangles)
    {
        // ── 有向辺を全部数え、逆向きが存在しないものだけを境界辺として残す ──
        var directedEdges = new HashSet<long>();
        for (int t = 0; t + Triangulation.IndicesPerTriangle <= triangles.Count;
             t += Triangulation.IndicesPerTriangle)
        {
            for (int e = 0; e < Triangulation.IndicesPerTriangle; e++)
            {
                int u = triangles[t + e];
                int v = triangles[t + (e + 1) % Triangulation.IndicesPerTriangle];
                if (u != v) directedEdges.Add(MakeDirectedKey(u, v));
            }
        }

        // 出発頂点 -> 到達頂点の一覧（境界辺のみ）
        var boundaryNext = new Dictionary<int, List<int>>();
        int boundaryEdgeCount = 0;
        foreach (long key in directedEdges)
        {
            int u = (int)(key >> 32);
            int v = (int)(key & 0xFFFFFFFF);
            if (directedEdges.Contains(MakeDirectedKey(v, u))) continue;   // 内部辺（両側に三角形）

            if (!boundaryNext.TryGetValue(u, out var list))
            {
                list = new List<int>(1);
                boundaryNext[u] = list;
            }
            list.Add(v);
            boundaryEdgeCount++;
        }

        // ── 境界辺を繋いでループにする ──
        var polygons = new List<SpriteRigPolygon>();
        var usedVertices = new HashSet<int>();
        var consumed = new HashSet<long>();

        foreach (var (start, _) in boundaryNext)
        {
            foreach (int firstTarget in boundaryNext[start])
            {
                long firstKey = MakeDirectedKey(start, firstTarget);
                if (consumed.Contains(firstKey)) continue;

                var loop = new List<Vec2>();
                int current = start;
                int next = firstTarget;
                // 無限ループ保険: 境界辺の総数を超えたら打ち切る
                for (int step = 0; step <= boundaryEdgeCount; step++)
                {
                    long key = MakeDirectedKey(current, next);
                    if (consumed.Contains(key)) break;
                    consumed.Add(key);
                    loop.Add(vertices[current]);
                    usedVertices.Add(current);

                    current = next;
                    if (!boundaryNext.TryGetValue(current, out var outs)) break;

                    int picked = -1;
                    foreach (int candidate in outs)
                    {
                        if (consumed.Contains(MakeDirectedKey(current, candidate))) continue;
                        picked = candidate;
                        break;
                    }
                    if (picked < 0) break;
                    next = picked;
                }

                if (loop.Count >= SpriteRigMesh.MinPolygonVertices)
                {
                    polygons.Add(new SpriteRigPolygon(loop)
                    {
                        IsHole = Geometry2D.SignedArea(loop) < 0.0,
                    });
                }
            }
        }

        // ── 輪郭に乗らなかった頂点を内部点として拾う ──
        var interior = new List<Vec2>();
        for (int i = 0; i < vertices.Count; i++)
        {
            if (!usedVertices.Contains(i)) interior.Add(vertices[i]);
        }

        return new BoundaryResult(polygons, interior);
    }

    /// <summary>有向辺のキー（順序を保った 2 頂点の組を 1 つの long に畳む）。</summary>
    private static long MakeDirectedKey(int from, int to) => ((long)from << 32) | (uint)to;
}
