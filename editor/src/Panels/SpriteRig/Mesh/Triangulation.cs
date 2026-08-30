using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.SpriteRig.Mesh;

/// <summary>
/// 輪郭（外周 + 穴）と内部点から、制約付き三角メッシュを作る。
///
/// 【方式】
///   1. <b>穴のブリッジ結合</b>: 各穴を外周へ 2 重辺（ブリッジ）で繋ぎ、穴なしの単純多角形にする。
///   2. <b>耳の切り出し（ear clipping）</b>: 単純多角形を三角形へ分解する。
///      多角形の内部しか三角形にならないので、<b>三角形が輪郭の外に出ないことが構造的に保証される</b>。
///   3. <b>内部点の挿入</b>: 点を含む三角形を 3 つに割る。
///   4. <b>Delaunay 化のための辺フリップ</b>: 輪郭辺（制約辺）以外を、外接円条件を満たすまで反転する。
///      形状は変えずに、細長い三角形だけを潰して変形品質を上げる。
///
/// Constrained Delaunay Triangulation の厳密実装ではないが、
/// 「制約辺を保持したまま内部だけを Delaunay 化する」という同じ結果に収束する。
/// </summary>
public static class Triangulation
{
    /// <summary>三角形 1 個あたりのインデックス数。</summary>
    public const int IndicesPerTriangle = 3;

    /// <summary>辺フリップを繰り返す最大パス数（発振したときの打ち切り）。</summary>
    private const int MaxFlipPasses = 64;

    /// <summary>面積がこの値未満の三角形は退化とみなして捨てる（ピクセル^2 の 2 倍値）。</summary>
    private const double DegenerateAreaThreshold = 1.0e-7;

    /// <summary>耳の判定で「頂点が一致している」とみなす距離（ピクセル）。</summary>
    private const double VertexMergeDistance = 1.0e-6;

    /// <summary>耳が 1 つも見つからない異常時に、1 周のパスを何回まで許すか。</summary>
    private const int MaxEarClipStalls = 2;

    /// <summary>
    /// 三角分割の対象となる 1 つの領域（外周 1 本 + 穴 0 本以上）。
    /// </summary>
    /// <param name="Outer">外周輪郭（正の符号付き面積へ正規化される）。</param>
    /// <param name="Holes">穴輪郭の一覧（負の符号付き面積へ正規化される）。</param>
    public sealed record Region(List<Vec2> Outer, List<List<Vec2>> Holes);

    /// <summary>
    /// 三角分割の結果。
    /// </summary>
    public sealed class TriangulationResult
    {
        /// <summary>頂点（座標重複は統合済み）。</summary>
        public List<Vec2> Vertices { get; } = new();

        /// <summary>三角形インデックス（3 個 1 組・外周と同じ向き）。</summary>
        public List<int> Triangles { get; } = new();

        /// <summary>輪郭由来の制約辺（フリップ禁止）。キーは <see cref="MakeEdgeKey"/> の値。</summary>
        public HashSet<long> ConstrainedEdges { get; } = new();
    }

    /// <summary>
    /// 領域群と内部点から三角メッシュを構築する。
    /// </summary>
    /// <param name="regions">外周 + 穴の領域一覧。</param>
    /// <param name="interiorPoints">内部に追加したい点（領域内に無い点は無視される）。</param>
    /// <param name="refineDelaunay">true なら辺フリップによる Delaunay 化を行う。</param>
    public static TriangulationResult Triangulate(
        IReadOnlyList<Region> regions,
        IReadOnlyList<Vec2>? interiorPoints = null,
        bool refineDelaunay = true)
    {
        var result = new TriangulationResult();
        // 座標 -> 頂点番号（ブリッジで重複した頂点をここで 1 本に畳む）
        var vertexIndexByPosition = new Dictionary<Vec2, int>();

        // ローカル関数: 座標を頂点として登録し、番号を返す（既出なら既存番号）
        int RegisterVertex(Vec2 p)
        {
            if (vertexIndexByPosition.TryGetValue(p, out int existing)) return existing;
            int index = result.Vertices.Count;
            result.Vertices.Add(p);
            vertexIndexByPosition[p] = index;
            return index;
        }

        foreach (var region in regions)
        {
            // ── 向きを規約へ正規化（外周 = 正・穴 = 負）──
            var outer = Geometry2D.EnsureOrientation(region.Outer, wantPositiveArea: true);
            if (outer.Count < IndicesPerTriangle) continue;

            var holes = new List<List<Vec2>>();
            foreach (var hole in region.Holes)
            {
                if (hole.Count < IndicesPerTriangle) continue;
                holes.Add(Geometry2D.EnsureOrientation(hole, wantPositiveArea: false));
            }

            // ── 制約辺（輪郭の辺）を登録する ──
            RegisterConstrainedRing(outer, RegisterVertex, result.ConstrainedEdges);
            foreach (var hole in holes) RegisterConstrainedRing(hole, RegisterVertex, result.ConstrainedEdges);

            // ── 穴をブリッジで繋いで単純多角形にし、耳切りで三角化する ──
            var merged = MergeHoles(outer, holes);
            var localTriangles = EarClip(merged);

            foreach (var tri in localTriangles)
            {
                Vec2 a = merged[tri.A];
                Vec2 b = merged[tri.B];
                Vec2 c = merged[tri.C];
                if (Math.Abs(Geometry2D.Cross3(a, b, c)) < DegenerateAreaThreshold) continue;

                result.Triangles.Add(RegisterVertex(a));
                result.Triangles.Add(RegisterVertex(b));
                result.Triangles.Add(RegisterVertex(c));
            }
        }

        // ── 内部点を挿入する ──
        if (interiorPoints != null)
        {
            foreach (var p in interiorPoints) InsertInteriorPoint(result, RegisterVertex, p);
        }

        // ── Delaunay 化（制約辺は動かさない）──
        if (refineDelaunay) RefineByEdgeFlips(result);

        // 退化三角形の最終掃除（挿入・フリップ後にも念のため）
        RemoveDegenerateTriangles(result);
        return result;
    }

    /// <summary>
    /// 無向辺のキー（頂点番号の組を順序非依存の 1 つの long に畳む）。
    /// </summary>
    public static long MakeEdgeKey(int a, int b)
    {
        int lo = Math.Min(a, b);
        int hi = Math.Max(a, b);
        return ((long)lo << 32) | (uint)hi;
    }

    // ============================================================
    //  制約辺の登録
    // ============================================================

    /// <summary>閉じた輪郭の全辺を制約辺として登録する。</summary>
    private static void RegisterConstrainedRing(
        List<Vec2> ring,
        Func<Vec2, int> registerVertex,
        HashSet<long> constrained)
    {
        int n = ring.Count;
        for (int i = 0; i < n; i++)
        {
            int a = registerVertex(ring[i]);
            int b = registerVertex(ring[(i + 1) % n]);
            if (a != b) constrained.Add(MakeEdgeKey(a, b));
        }
    }

    // ============================================================
    //  穴のブリッジ結合
    // ============================================================

    /// <summary>
    /// 穴を外周へブリッジで繋ぎ、穴の無い単純多角形へ畳む。
    ///
    /// 各穴について「最も右にある頂点 M」から +X 方向へレイを飛ばし、
    /// 最初に当たる外周辺の端点（可視な頂点）とを 2 重辺で結ぶ（Eberly の方式）。
    /// 右にある穴から順に処理することで、既に結合済みの形へ次の穴を繋げられる。
    /// </summary>
    private static List<Vec2> MergeHoles(List<Vec2> outer, List<List<Vec2>> holes)
    {
        if (holes.Count == 0) return new List<Vec2>(outer);

        // 最も右にある穴から処理する（外周へ到達するレイが他の穴に邪魔されにくい）
        var ordered = new List<List<Vec2>>(holes);
        ordered.Sort((x, y) => MaxX(y).CompareTo(MaxX(x)));

        var merged = new List<Vec2>(outer);
        foreach (var hole in ordered)
        {
            int holeStart = IndexOfMaxX(hole);
            int bridgeIndex = FindVisibleOuterVertex(merged, hole[holeStart]);
            if (bridgeIndex < 0) continue;   // 到達不能な穴（想定外）はブリッジせず捨てる

            merged = SpliceHole(merged, bridgeIndex, hole, holeStart);
        }
        return merged;
    }

    /// <summary>頂点列の最大 X。</summary>
    private static double MaxX(List<Vec2> points)
    {
        double best = double.NegativeInfinity;
        foreach (var p in points)
        {
            if (p.X > best) best = p.X;
        }
        return best;
    }

    /// <summary>最大 X を持つ頂点の添字（同値なら Y が大きい方）。</summary>
    private static int IndexOfMaxX(List<Vec2> points)
    {
        int best = 0;
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].X > points[best].X ||
                (points[i].X == points[best].X && points[i].Y > points[best].Y))
            {
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// 穴の頂点 <paramref name="m"/> から +X 方向に見て、最初にぶつかる外周辺の
    /// 可視な端点の添字を返す。見つからなければ -1。
    /// </summary>
    private static int FindVisibleOuterVertex(List<Vec2> outer, Vec2 m)
    {
        int n = outer.Count;
        double bestX = double.PositiveInfinity;
        int bestEdge = -1;
        Vec2 hit = Vec2.Zero;

        // ── レイ (m -> +X) と交差する外周辺のうち、最も近いものを探す ──
        for (int i = 0; i < n; i++)
        {
            Vec2 a = outer[i];
            Vec2 b = outer[(i + 1) % n];
            bool straddles = (a.Y > m.Y) != (b.Y > m.Y);
            if (!straddles) continue;

            double t = (m.Y - a.Y) / (b.Y - a.Y);
            double x = a.X + t * (b.X - a.X);
            if (x < m.X - Geometry2D.Epsilon) continue;   // 左側の交点は無視
            if (x < bestX)
            {
                bestX = x;
                bestEdge = i;
                hit = new Vec2(x, m.Y);
            }
        }
        if (bestEdge < 0) return -1;

        // ── 当たった辺の端点のうち X が大きい方を初期候補にする ──
        int i0 = bestEdge;
        int i1 = (bestEdge + 1) % n;
        int candidate = outer[i0].X >= outer[i1].X ? i0 : i1;

        // ── 三角形 (M, 交点, 候補) の内側に凹頂点があれば、そちらを優先する ──
        // 角度が最も小さい（= レイに最も近い）凹頂点が「実際に見える」頂点になる。
        double bestAngle = double.PositiveInfinity;
        double bestDistance = double.PositiveInfinity;
        int refined = candidate;
        for (int i = 0; i < n; i++)
        {
            if (i == candidate) continue;
            Vec2 prev = outer[(i - 1 + n) % n];
            Vec2 cur = outer[i];
            Vec2 next = outer[(i + 1) % n];
            // 外周は正の向きなので、外積が負の頂点が凹頂点
            if (Geometry2D.Cross3(prev, cur, next) >= 0.0) continue;
            if (!Geometry2D.PointInTriangle(m, hit, outer[candidate], cur)) continue;

            Vec2 d = cur - m;
            double length = d.Length;
            if (length < Geometry2D.Epsilon) continue;
            double angle = Math.Abs(d.Y) / length;   // +X 軸となす角の単調な指標
            if (angle < bestAngle || (angle == bestAngle && length < bestDistance))
            {
                bestAngle = angle;
                bestDistance = length;
                refined = i;
            }
        }
        return refined;
    }

    /// <summary>
    /// 外周の <paramref name="outerIndex"/> と穴の <paramref name="holeStart"/> を
    /// 2 重辺で結び、1 本の多角形に繋ぎ直す。
    /// </summary>
    private static List<Vec2> SpliceHole(
        List<Vec2> outer, int outerIndex, List<Vec2> hole, int holeStart)
    {
        var merged = new List<Vec2>(outer.Count + hole.Count + 2);

        // 外周の先頭 〜 ブリッジ点（含む）
        for (int i = 0; i <= outerIndex; i++) merged.Add(outer[i]);
        // 穴を holeStart から 1 周ぶん（holeStart を最後にもう一度置いて閉じる）
        for (int k = 0; k < hole.Count; k++) merged.Add(hole[(holeStart + k) % hole.Count]);
        merged.Add(hole[holeStart]);
        // 戻りのブリッジ点と外周の残り
        merged.Add(outer[outerIndex]);
        for (int i = outerIndex + 1; i < outer.Count; i++) merged.Add(outer[i]);

        return merged;
    }

    // ============================================================
    //  耳の切り出し（ear clipping）
    // ============================================================

    /// <summary>耳切りが出力する三角形（多角形頂点列への添字）。</summary>
    private readonly record struct LocalTriangle(int A, int B, int C);

    /// <summary>
    /// 正の向きの単純多角形を三角形へ分解する。
    /// </summary>
    private static List<LocalTriangle> EarClip(List<Vec2> polygon)
    {
        var triangles = new List<LocalTriangle>();
        int n = polygon.Count;
        if (n < IndicesPerTriangle) return triangles;

        // 残り頂点の巡回リスト（添字で持つ）
        var remaining = new List<int>(n);
        for (int i = 0; i < n; i++) remaining.Add(i);

        int stalls = 0;
        while (remaining.Count > IndicesPerTriangle)
        {
            bool clipped = false;
            for (int k = 0; k < remaining.Count; k++)
            {
                int prev = remaining[(k - 1 + remaining.Count) % remaining.Count];
                int cur = remaining[k];
                int next = remaining[(k + 1) % remaining.Count];

                if (!IsEar(polygon, remaining, prev, cur, next)) continue;

                triangles.Add(new LocalTriangle(prev, cur, next));
                remaining.RemoveAt(k);
                clipped = true;
                break;
            }

            if (clipped) continue;

            // 耳が見つからない（自己交差など想定外の入力）。
            // 1 度だけ、退化頂点を落として再挑戦し、それでも駄目なら諦める。
            stalls++;
            if (stalls > MaxEarClipStalls) return triangles;
            if (!RemoveOneDegenerateVertex(polygon, remaining)) return triangles;
        }

        if (remaining.Count == IndicesPerTriangle)
            triangles.Add(new LocalTriangle(remaining[0], remaining[1], remaining[2]));

        return triangles;
    }

    /// <summary>
    /// (prev, cur, next) が耳（切り落として良い三角形）かを判定する。
    /// 凸であり、かつ他のどの残り頂点も三角形の内部に無いことが条件。
    /// </summary>
    private static bool IsEar(List<Vec2> polygon, List<int> remaining, int prev, int cur, int next)
    {
        Vec2 a = polygon[prev];
        Vec2 b = polygon[cur];
        Vec2 c = polygon[next];

        // 凸判定（多角形は正の向きなので、外積が正なら凸）
        if (Geometry2D.Cross3(a, b, c) <= DegenerateAreaThreshold) return false;

        foreach (int index in remaining)
        {
            if (index == prev || index == cur || index == next) continue;
            Vec2 p = polygon[index];
            // ブリッジで座標が重複した頂点は「同じ点」なので内部判定から除く
            if (Vec2.Distance(p, a) < VertexMergeDistance) continue;
            if (Vec2.Distance(p, b) < VertexMergeDistance) continue;
            if (Vec2.Distance(p, c) < VertexMergeDistance) continue;

            if (Geometry2D.PointInTriangle(a, b, c, p)) return false;
        }
        return true;
    }

    /// <summary>
    /// 残り頂点から退化した（前後の頂点と一直線・または重複した）頂点を 1 つ取り除く。
    /// 耳が見つからない膠着状態を抜けるための救済処理。
    /// </summary>
    private static bool RemoveOneDegenerateVertex(List<Vec2> polygon, List<int> remaining)
    {
        for (int k = 0; k < remaining.Count; k++)
        {
            int prev = remaining[(k - 1 + remaining.Count) % remaining.Count];
            int cur = remaining[k];
            int next = remaining[(k + 1) % remaining.Count];
            double area = Math.Abs(Geometry2D.Cross3(polygon[prev], polygon[cur], polygon[next]));
            if (area < DegenerateAreaThreshold)
            {
                remaining.RemoveAt(k);
                return true;
            }
        }
        return false;
    }

    // ============================================================
    //  内部点の挿入
    // ============================================================

    /// <summary>
    /// 内部点を、それを含む三角形へ挿入して 3 分割する。
    /// どの三角形にも入らない点（＝領域外・辺上）は黙って無視する。
    /// </summary>
    private static void InsertInteriorPoint(
        TriangulationResult mesh, Func<Vec2, int> registerVertex, Vec2 p)
    {
        var tris = mesh.Triangles;
        for (int t = 0; t + IndicesPerTriangle <= tris.Count; t += IndicesPerTriangle)
        {
            int i0 = tris[t];
            int i1 = tris[t + 1];
            int i2 = tris[t + 2];
            Vec2 a = mesh.Vertices[i0];
            Vec2 b = mesh.Vertices[i1];
            Vec2 c = mesh.Vertices[i2];
            if (!Geometry2D.PointInTriangle(a, b, c, p)) continue;

            // 3 つの小三角形が全て有効な面積を持つときだけ採用する（辺上の点を弾く）。
            // 判定を頂点登録より先に行うのは、採用しない点で孤立頂点を作らないため。
            if (Math.Abs(Geometry2D.Cross3(a, b, p)) < DegenerateAreaThreshold) return;
            if (Math.Abs(Geometry2D.Cross3(b, c, p)) < DegenerateAreaThreshold) return;
            if (Math.Abs(Geometry2D.Cross3(c, a, p)) < DegenerateAreaThreshold) return;

            int pi = registerVertex(p);
            if (pi == i0 || pi == i1 || pi == i2) return;

            // 元の三角形を (a, b, p) に潰し、残り 2 つを末尾へ足す
            tris[t] = i0;
            tris[t + 1] = i1;
            tris[t + 2] = pi;
            tris.Add(i1); tris.Add(i2); tris.Add(pi);
            tris.Add(i2); tris.Add(i0); tris.Add(pi);
            return;
        }
    }

    // ============================================================
    //  Delaunay 化（辺フリップ）
    // ============================================================

    /// <summary>
    /// 制約辺以外の内部辺を、外接円条件を満たすまで反転して三角形の形を整える。
    /// 形状（占有領域）は変わらず、細長い三角形だけが解消される。
    /// </summary>
    private static void RefineByEdgeFlips(TriangulationResult mesh)
    {
        for (int pass = 0; pass < MaxFlipPasses; pass++)
        {
            // 辺 -> それを共有する三角形番号（最大 2 個）を作り直す
            var trianglesByEdge = new Dictionary<long, List<int>>();
            int triangleCount = mesh.Triangles.Count / IndicesPerTriangle;
            for (int t = 0; t < triangleCount; t++)
            {
                int baseIndex = t * IndicesPerTriangle;
                for (int e = 0; e < IndicesPerTriangle; e++)
                {
                    int u = mesh.Triangles[baseIndex + e];
                    int v = mesh.Triangles[baseIndex + (e + 1) % IndicesPerTriangle];
                    long key = MakeEdgeKey(u, v);
                    if (!trianglesByEdge.TryGetValue(key, out var list))
                    {
                        list = new List<int>(2);
                        trianglesByEdge[key] = list;
                    }
                    list.Add(t);
                }
            }

            // 1 パス内で同じ三角形を二重に触らないための印
            var touched = new bool[triangleCount];
            int flips = 0;

            foreach (var (key, owners) in trianglesByEdge)
            {
                if (owners.Count != 2) continue;                 // 境界辺（片側のみ）は対象外
                if (mesh.ConstrainedEdges.Contains(key)) continue; // 輪郭辺は動かさない
                int t0 = owners[0];
                int t1 = owners[1];
                if (touched[t0] || touched[t1]) continue;

                if (!TryFlipEdge(mesh, t0, t1, key)) continue;
                touched[t0] = true;
                touched[t1] = true;
                flips++;
            }

            if (flips == 0) break;
        }
    }

    /// <summary>
    /// 2 つの三角形が共有する辺を、Delaunay 条件を満たすなら反転する。
    /// </summary>
    /// <returns>反転した場合 true。</returns>
    private static bool TryFlipEdge(TriangulationResult mesh, int t0, int t1, long sharedEdgeKey)
    {
        int u = (int)(sharedEdgeKey >> 32);
        int v = (int)(sharedEdgeKey & 0xFFFFFFFF);

        int w0 = OppositeVertex(mesh, t0, u, v);
        int w1 = OppositeVertex(mesh, t1, u, v);
        if (w0 < 0 || w1 < 0 || w0 == w1) return false;

        Vec2 pu = mesh.Vertices[u];
        Vec2 pv = mesh.Vertices[v];
        Vec2 p0 = mesh.Vertices[w0];
        Vec2 p1 = mesh.Vertices[w1];

        // 四角形 (u, w0, v, w1) が凸でなければ反転すると裏返るので不可
        bool convex = Geometry2D.Cross3(pu, p0, p1) * Geometry2D.Cross3(pv, p0, p1) < 0.0
                   && Geometry2D.Cross3(p0, pu, pv) * Geometry2D.Cross3(p1, pu, pv) < 0.0;
        if (!convex) return false;

        // Delaunay 条件: w1 が三角形 (u, v, w0) の外接円の内側なら反転すべき
        if (!IsInsideCircumcircle(pu, pv, p0, p1)) return false;

        // 反転後の 2 三角形が退化しないことを確認する
        if (Math.Abs(Geometry2D.Cross3(p0, p1, pv)) < DegenerateAreaThreshold) return false;
        if (Math.Abs(Geometry2D.Cross3(p1, p0, pu)) < DegenerateAreaThreshold) return false;

        WriteTriangle(mesh, t0, p0, p1, pv, w0, w1, v);
        WriteTriangle(mesh, t1, p1, p0, pu, w1, w0, u);
        return true;
    }

    /// <summary>
    /// 三角形スロットへ、向きを正に揃えて 3 頂点を書き込む。
    /// </summary>
    private static void WriteTriangle(
        TriangulationResult mesh, int slot, Vec2 a, Vec2 b, Vec2 c, int ia, int ib, int ic)
    {
        int baseIndex = slot * IndicesPerTriangle;
        // 出力は外周と同じ向き（正の符号付き面積）で統一する
        if (Geometry2D.Cross3(a, b, c) < 0.0) (ib, ic) = (ic, ib);
        mesh.Triangles[baseIndex] = ia;
        mesh.Triangles[baseIndex + 1] = ib;
        mesh.Triangles[baseIndex + 2] = ic;
    }

    /// <summary>三角形のうち、辺 (u, v) に含まれない残り 1 頂点を返す（無ければ -1）。</summary>
    private static int OppositeVertex(TriangulationResult mesh, int triangle, int u, int v)
    {
        int baseIndex = triangle * IndicesPerTriangle;
        for (int e = 0; e < IndicesPerTriangle; e++)
        {
            int index = mesh.Triangles[baseIndex + e];
            if (index != u && index != v) return index;
        }
        return -1;
    }

    /// <summary>
    /// 点 d が三角形 (a, b, c) の外接円の内側にあるかを判定する（InCircle 述語）。
    /// 三角形は正の向き（<see cref="Geometry2D.Cross3"/> が正）である必要がある。
    /// </summary>
    private static bool IsInsideCircumcircle(Vec2 a, Vec2 b, Vec2 c, Vec2 d)
    {
        // 向きが負なら 2 頂点を入れ替えて正にしてから判定する
        if (Geometry2D.Cross3(a, b, c) < 0.0) (b, c) = (c, b);

        double ax = a.X - d.X, ay = a.Y - d.Y;
        double bx = b.X - d.X, by = b.Y - d.Y;
        double cx = c.X - d.X, cy = c.Y - d.Y;

        double det =
            (ax * ax + ay * ay) * (bx * cy - cx * by)
          - (bx * bx + by * by) * (ax * cy - cx * ay)
          + (cx * cx + cy * cy) * (ax * by - bx * ay);

        return det > DegenerateAreaThreshold;
    }

    /// <summary>面積が実質 0 の三角形を取り除く。</summary>
    private static void RemoveDegenerateTriangles(TriangulationResult mesh)
    {
        var kept = new List<int>(mesh.Triangles.Count);
        for (int t = 0; t + IndicesPerTriangle <= mesh.Triangles.Count; t += IndicesPerTriangle)
        {
            int i0 = mesh.Triangles[t];
            int i1 = mesh.Triangles[t + 1];
            int i2 = mesh.Triangles[t + 2];
            if (i0 == i1 || i1 == i2 || i2 == i0) continue;

            double area = Geometry2D.Cross3(mesh.Vertices[i0], mesh.Vertices[i1], mesh.Vertices[i2]);
            if (Math.Abs(area) < DegenerateAreaThreshold) continue;

            kept.Add(i0);
            kept.Add(i1);
            kept.Add(i2);
        }
        mesh.Triangles.Clear();
        mesh.Triangles.AddRange(kept);
    }
}
