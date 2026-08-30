using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.SpriteRig.Mesh;

/// <summary>
/// ボーン線分への距離から自動的にスキンウェイトを割り当てる。
///
/// <para>【アルゴリズム】</para>
/// <list type="number">
///   <item>
///     各ボーンを「根元 → 先端」の<b>線分</b>とみなす
///     （長さが 0 のボーンは根元の 1 点として扱う）。
///   </item>
///   <item>
///     頂点ごとに、全ボーン線分への最短距離 <c>d</c> を測る。
///   </item>
///   <item>
///     <b>輪郭をまたぐ影響の抑制</b>（<see cref="Options.SuppressAcrossContour"/>）:
///     頂点からボーン上の最近点へ引いた直線がメッシュ領域の外を通る場合、
///     その距離を <see cref="AcrossContourPenalty"/> 倍して「遠い」ことにする。
///     腕と胴が近接していても、間の隙間を跨ぐ影響が弱まる（簡易ジオデシック補正）。
///   </item>
///   <item>
///     距離の逆数系カーネル <c>w = 1 / (d + ε)^p</c>（<c>p</c> = <see cref="Options.Falloff"/>）で
///     重みに変換し、<b>大きい順に最大 4 本</b>だけ残して合計 1.0 へ正規化する。
///   </item>
/// </list>
///
/// <para>
/// 距離の逆数を使うのは、ボーンに乗っている頂点（<c>d ≈ 0</c>）でそのボーンの重みが
/// 支配的になり、2 本のボーンから等距離の頂点でちょうど半々になる、という
/// 直感どおりの分布が式ひとつで得られるためである。
/// <c>p</c> を大きくするほど「一番近いボーン 1 本」に寄り、小さくするほど滑らかに混ざる。
/// </para>
/// </summary>
public static class AutoWeights
{
    /// <summary>距離 0 での発散を防ぐための下駄（ピクセル）。</summary>
    public const double DistanceEpsilon = 1.0e-2;

    /// <summary>領域外を通る影響に掛ける距離の倍率（大きいほど強く抑制される）。</summary>
    public const double AcrossContourPenalty = 8.0;

    /// <summary>領域外判定のために「頂点 → ボーン最近点」の直線を分割する数。</summary>
    public const int ContourSampleCount = 8;

    /// <summary>自動ウェイトのパラメータ。</summary>
    public sealed class Options
    {
        /// <summary>減衰指数の既定値。</summary>
        public const double DefaultFalloff = 2.0;

        /// <summary>減衰指数の下限（小さいほど広く混ざる）。</summary>
        public const double MinFalloff = 0.5;

        /// <summary>減衰指数の上限（大きいほど一番近いボーンへ寄る）。</summary>
        public const double MaxFalloff = 8.0;

        /// <summary>距離カーネルの指数 <c>p</c>。</summary>
        public double Falloff { get; set; } = DefaultFalloff;

        /// <summary>true なら輪郭をまたぐ影響にペナルティを掛ける（簡易ジオデシック補正）。</summary>
        public bool SuppressAcrossContour { get; set; } = true;
    }

    /// <summary>
    /// メッシュ全体（または指定した頂点だけ）へ自動ウェイトを割り当てる。
    /// </summary>
    /// <param name="mesh">対象メッシュ（<see cref="SpriteRigMesh.Weights"/> を書き換える）。</param>
    /// <param name="options">パラメータ。</param>
    /// <param name="targetVertices">
    /// 対象頂点の添字集合。null なら全頂点へ適用する。
    /// </param>
    /// <returns>実際に割り当てた頂点数。</returns>
    public static int Apply(SpriteRigMesh mesh, Options options, IReadOnlyCollection<int>? targetVertices = null)
    {
        if (mesh.Vertices.Count == 0 || mesh.Bones.Count == 0) return 0;

        // 頂点数とウェイト数が食い違っていたら先に器を揃える（壊れた状態からの復帰）
        while (mesh.Weights.Count < mesh.Vertices.Count)
            mesh.Weights.Add(new List<SpriteRigInfluence> { new(0, 1.0) });

        var segments = BuildBoneSegments(mesh.Bones);
        var active = BuildActiveBoneMask(segments);
        double falloff = Math.Clamp(options.Falloff, Options.MinFalloff, Options.MaxFalloff);
        bool suppress = options.SuppressAcrossContour && mesh.Polygons.Count > 0;

        int applied = 0;
        for (int v = 0; v < mesh.Vertices.Count; v++)
        {
            if (targetVertices != null && !targetVertices.Contains(v)) continue;
            mesh.Weights[v] = ComputeForVertex(mesh, mesh.Vertices[v], segments, active, falloff, suppress);
            applied++;
        }
        return applied;
    }

    /// <summary>
    /// 自動ウェイトの候補に含めるボーンを決める。
    ///
    /// 長さのあるボーンが 1 本でもあるなら、<b>長さ 0 のボーンは候補から外す</b>。
    /// 長さ 0 の骨（＝まだ形を与えていない既定のルートなど）を 1 点として扱うと、
    /// その点の周りの頂点だけが理由もなく強く引っ張られ、結果が直感に反するためである。
    /// すべてが長さ 0 の場合は、影響 0 本の頂点を作らないよう全ボーンを候補にする。
    /// </summary>
    /// <param name="segments">ボーン線分の一覧。</param>
    public static bool[] BuildActiveBoneMask(List<(Vec2 Head, Vec2 Tip)> segments)
    {
        var active = new bool[segments.Count];
        bool anySized = false;
        for (int i = 0; i < segments.Count; i++)
        {
            active[i] = Vec2.Distance(segments[i].Head, segments[i].Tip) >= SpriteRigSkeleton.MinBoneLength;
            if (active[i]) anySized = true;
        }
        if (!anySized) Array.Fill(active, true);
        return active;
    }

    /// <summary>
    /// ボーンを「根元 → 先端」の線分に落とす。長さ 0 のボーンは根元だけの退化線分になる。
    /// </summary>
    /// <param name="bones">ボーン一覧。</param>
    public static List<(Vec2 Head, Vec2 Tip)> BuildBoneSegments(IReadOnlyList<SpriteRigBone> bones)
    {
        var globals = SpriteRigSkeleton.ComputeGlobals(bones);
        var segments = new List<(Vec2, Vec2)>(bones.Count);
        for (int i = 0; i < bones.Count; i++)
        {
            Vec2 head = SpriteRigSkeleton.HeadOf(globals, i);
            // 表示用のスタブ長ではなく「実際に記録されている長さ」を使う。
            // 長さの無いボーンを勝手に伸ばすと、その方向の頂点が理由なく強く引っ張られるため。
            double length = bones[i].Length;
            Vec2 tip = length < SpriteRigSkeleton.MinBoneLength
                ? head
                : globals[i].Transform(new Vec2(length, 0.0));
            segments.Add((head, tip));
        }
        return segments;
    }

    /// <summary>
    /// 頂点 1 点ぶんの影響を計算する。
    /// </summary>
    /// <param name="mesh">領域判定に使うメッシュ。</param>
    /// <param name="vertex">頂点位置。</param>
    /// <param name="segments">ボーン線分の一覧。</param>
    /// <param name="active">ボーンごとの「候補に含めるか」。</param>
    /// <param name="falloff">距離カーネルの指数。</param>
    /// <param name="suppressAcrossContour">輪郭をまたぐ影響を抑制するか。</param>
    private static List<SpriteRigInfluence> ComputeForVertex(
        SpriteRigMesh mesh, Vec2 vertex, List<(Vec2 Head, Vec2 Tip)> segments, bool[] active,
        double falloff, bool suppressAcrossContour)
    {
        var candidates = new List<SpriteRigInfluence>(segments.Count);
        for (int b = 0; b < segments.Count; b++)
        {
            if (!active[b]) continue;

            var (head, tip) = segments[b];
            double distance = Geometry2D.DistancePointSegment(head, tip, vertex);

            if (suppressAcrossContour && CrossesOutsideRegion(mesh, vertex, ClosestPointOnSegment(head, tip, vertex)))
                distance *= AcrossContourPenalty;

            double weight = Math.Pow(1.0 / (distance + DistanceEpsilon), falloff);
            if (!double.IsFinite(weight) || weight <= 0.0) continue;
            candidates.Add(new SpriteRigInfluence(b, weight));
        }

        // 大きい順に最大 4 本へ絞って正規化する（Normalize が両方まとめて行う）
        return WeightPaint.Normalize(candidates);
    }

    /// <summary>線分 ab 上で点 p に最も近い点を返す。</summary>
    /// <param name="a">線分の始点。</param>
    /// <param name="b">線分の終点。</param>
    /// <param name="p">対象の点。</param>
    public static Vec2 ClosestPointOnSegment(Vec2 a, Vec2 b, Vec2 p)
    {
        Vec2 ab = b - a;
        double lengthSquared = ab.LengthSquared;
        if (lengthSquared < Geometry2D.Epsilon) return a;

        double t = Math.Clamp(Vec2.Dot(p - a, ab) / lengthSquared, 0.0, 1.0);
        return a + ab * t;
    }

    /// <summary>
    /// 2 点を結ぶ直線がメッシュ領域の外を通るかを、等間隔サンプルで簡易判定する。
    ///
    /// 厳密な測地距離（メッシュ上の最短経路）は高価なので、
    /// 「途中が領域外なら遠いことにする」だけの近似で済ませている。
    /// </summary>
    /// <param name="mesh">領域を定義するメッシュ。</param>
    /// <param name="from">始点（頂点）。</param>
    /// <param name="to">終点（ボーン上の最近点）。</param>
    private static bool CrossesOutsideRegion(SpriteRigMesh mesh, Vec2 from, Vec2 to)
    {
        for (int i = 1; i < ContourSampleCount; i++)
        {
            double t = (double)i / ContourSampleCount;
            Vec2 sample = from + (to - from) * t;
            if (!IsInsideRegion(mesh, sample)) return true;
        }
        return false;
    }

    /// <summary>入れ子の偶奇で「領域の内側か」を判定する（穴の中は外）。</summary>
    /// <param name="mesh">対象メッシュ。</param>
    /// <param name="point">判定する点。</param>
    private static bool IsInsideRegion(SpriteRigMesh mesh, Vec2 point)
    {
        bool inside = false;
        foreach (var polygon in mesh.Polygons)
        {
            if (polygon.Points.Count < SpriteRigMesh.MinPolygonVertices) continue;
            if (Geometry2D.PointInPolygon(polygon.Points, point)) inside = !inside;
        }
        return inside;
    }
}
