using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.SpriteRig.Mesh;

/// <summary>
/// ウェイトペイントのブラシ動作（頂点 1 点に対する重みの書き換え規則）。
/// </summary>
public enum WeightBrushMode
{
    /// <summary>加算（対象ボーンの影響を強める）。</summary>
    Add,

    /// <summary>減算（対象ボーンの影響を弱める）。</summary>
    Subtract,

    /// <summary>置換（対象ボーンの影響を「強さ」の値へ寄せる）。</summary>
    Replace,

    /// <summary>スムーズ（隣接頂点の平均へ寄せる。段差をならす）。</summary>
    Smooth,
}

/// <summary>
/// 手動ウェイトペイントと、ウェイト列の正規化・最大本数制約の実装。
///
/// <para>【不変条件】</para>
/// <para>
/// このクラスを通した結果のウェイト列は必ず次を満たす（＝ランタイムのパーサが必ず受理する）:
/// </para>
/// <list type="bullet">
///   <item>影響本数は 1 本以上 <see cref="MaxInfluences"/> 本以下</item>
///   <item>全ウェイトが正の有限値で、合計がちょうど 1.0</item>
///   <item>同じボーンが 2 回現れない</item>
/// </list>
///
/// <para>【最大 4 本の維持 ―― 「最弱を追い出す」規則】</para>
/// <para>
/// 5 本目のボーンを塗ると本数が上限を超える。このとき捨てるのは
/// <b>いま塗っているボーンを除いた中で最も弱い 1 本</b>である。
/// 「塗った本人を残す」ことで、ユーザーの操作が必ず画面へ反映される
/// （最弱を無条件に捨てると、弱く塗った直後にその影響自身が消えて操作が効かなく見える）。
/// </para>
/// </summary>
public static class WeightPaint
{
    /// <summary>1 頂点が持てる影響の最大本数（ランタイムの <c>MAX_BONE_INFLUENCES</c> と一致）。</summary>
    public const int MaxInfluences = 4;

    /// <summary>これ未満のウェイトは 0 とみなして捨てる。</summary>
    public const double MinWeight = 1.0e-6;

    /// <summary>ブラシ半径の下限（ピクセル）。0 除算を避ける。</summary>
    public const double MinBrushRadius = 1.0e-3;

    // ============================================================
    //  ウェイト列の基本操作
    // ============================================================

    /// <summary>
    /// 指定ボーンの現在のウェイトを返す（影響が無ければ 0）。
    /// </summary>
    /// <param name="influences">頂点の影響一覧。</param>
    /// <param name="boneIndex">調べるボーンの添字。</param>
    public static double GetWeight(IReadOnlyList<SpriteRigInfluence> influences, int boneIndex)
    {
        foreach (var influence in influences)
        {
            if (influence.BoneIndex == boneIndex) return influence.Weight;
        }
        return 0.0;
    }

    /// <summary>
    /// 影響一覧を「1〜4 本・正の値・合計 1.0」の形へ整える。
    ///
    /// 同じボーンの重複はまとめ、微小・非有限・非正の値は捨て、
    /// 5 本以上なら大きい順に 4 本へ切り詰めてから正規化する。
    /// すべて捨てられた場合はルート（添字 0）へ 1.0 を割り当てる
    /// （<c>.sprite_mesh</c> は影響 0 本の頂点を許さないため）。
    /// </summary>
    /// <param name="influences">元の影響一覧。</param>
    public static List<SpriteRigInfluence> Normalize(IReadOnlyList<SpriteRigInfluence> influences)
    {
        // ── 重複をまとめつつ、有効な値だけ拾う ──
        var merged = new List<SpriteRigInfluence>(influences.Count);
        foreach (var influence in influences)
        {
            if (influence.BoneIndex < 0) continue;
            if (!double.IsFinite(influence.Weight) || influence.Weight <= MinWeight) continue;

            int existing = IndexOfBone(merged, influence.BoneIndex);
            if (existing >= 0)
            {
                merged[existing] = new SpriteRigInfluence(
                    influence.BoneIndex, merged[existing].Weight + influence.Weight);
            }
            else
            {
                merged.Add(influence);
            }
        }

        // ── 上限本数まで（大きい順に）絞る ──
        if (merged.Count > MaxInfluences)
        {
            merged.Sort(static (a, b) => b.Weight.CompareTo(a.Weight));
            merged.RemoveRange(MaxInfluences, merged.Count - MaxInfluences);
        }

        if (merged.Count == 0) return new List<SpriteRigInfluence> { new(0, 1.0) };

        // ── 合計 1.0 へ正規化 ──
        double sum = 0.0;
        foreach (var influence in merged) sum += influence.Weight;
        if (sum <= MinWeight) return new List<SpriteRigInfluence> { new(merged[0].BoneIndex, 1.0) };

        var result = new List<SpriteRigInfluence>(merged.Count);
        foreach (var influence in merged)
            result.Add(new SpriteRigInfluence(influence.BoneIndex, influence.Weight / sum));
        return result;
    }

    /// <summary>
    /// 1 頂点の「指定ボーンのウェイト」を目標値へ設定し、残りを合計 1.0 になるよう按分する。
    ///
    /// <para>
    /// 本数が上限を超える場合は、<b>塗っているボーン以外で最も弱い 1 本</b>を追い出す
    /// （クラスコメントの「最弱を追い出す」規則）。
    /// </para>
    /// </summary>
    /// <param name="influences">元の影響一覧。</param>
    /// <param name="boneIndex">目標値を設定するボーンの添字。</param>
    /// <param name="target">設定するウェイト（0〜1 へクランプされる）。</param>
    /// <returns>正規化済みの新しい影響一覧。</returns>
    public static List<SpriteRigInfluence> SetBoneWeight(
        IReadOnlyList<SpriteRigInfluence> influences, int boneIndex, double target)
    {
        if (boneIndex < 0) return Normalize(influences);
        target = Math.Clamp(double.IsFinite(target) ? target : 0.0, 0.0, 1.0);

        // ── 対象ボーン以外を集める ──
        var others = new List<SpriteRigInfluence>(influences.Count);
        foreach (var influence in influences)
        {
            if (influence.BoneIndex == boneIndex) continue;
            if (!double.IsFinite(influence.Weight) || influence.Weight <= MinWeight) continue;
            others.Add(influence);
        }

        // ── ウェイト 0 は「その影響を消す」＝他だけを正規化する ──
        if (target <= MinWeight)
        {
            if (others.Count == 0) return new List<SpriteRigInfluence> { new(boneIndex, 1.0) };
            return Normalize(others);
        }

        // ── 上限本数を守る（対象ボーンぶんの枠を空ける）──
        // 対象ボーンは必ず残すので、他は MaxInfluences - 1 本まで
        if (others.Count > MaxInfluences - 1)
        {
            others.Sort(static (a, b) => b.Weight.CompareTo(a.Weight));
            others.RemoveRange(MaxInfluences - 1, others.Count - (MaxInfluences - 1));
        }

        double otherSum = 0.0;
        foreach (var influence in others) otherSum += influence.Weight;

        // 他に影響が無い／目標が 1.0 なら、対象ボーン 1 本だけになる
        if (others.Count == 0 || target >= 1.0 - MinWeight || otherSum <= MinWeight)
            return new List<SpriteRigInfluence> { new(boneIndex, 1.0) };

        // 残り (1 - target) を、他の影響の比率のまま按分する
        double scale = (1.0 - target) / otherSum;
        var result = new List<SpriteRigInfluence>(others.Count + 1) { new(boneIndex, target) };
        foreach (var influence in others)
            result.Add(new SpriteRigInfluence(influence.BoneIndex, influence.Weight * scale));
        return result;
    }

    // ============================================================
    //  ブラシ
    // ============================================================

    /// <summary>ブラシ 1 ストロークぶんのパラメータ。</summary>
    public sealed class BrushOptions
    {
        /// <summary>ブラシ半径の既定値（キャンバスピクセル）。</summary>
        public const double DefaultRadius = 24.0;

        /// <summary>ブラシ強さの既定値（0〜1）。</summary>
        public const double DefaultStrength = 0.5;

        /// <summary>影響半径（キャンバスピクセル）。</summary>
        public double Radius { get; set; } = DefaultRadius;

        /// <summary>1 回の適用で動かす量（0〜1）。</summary>
        public double Strength { get; set; } = DefaultStrength;

        /// <summary>ブラシの動作。</summary>
        public WeightBrushMode Mode { get; set; } = WeightBrushMode.Add;
    }

    /// <summary>
    /// ブラシを 1 回適用する。
    /// </summary>
    /// <param name="vertices">頂点位置（キャンバスピクセル）。</param>
    /// <param name="weights">頂点ごとの影響一覧（その場で書き換える）。</param>
    /// <param name="triangles">三角形インデックス（スムーズの隣接計算に使う。null 可）。</param>
    /// <param name="boneIndex">塗る対象のボーン添字。</param>
    /// <param name="center">ブラシ中心（キャンバスピクセル）。</param>
    /// <param name="options">ブラシパラメータ。</param>
    /// <returns>1 頂点でも書き換えた場合 true。</returns>
    public static bool ApplyBrush(
        IReadOnlyList<Vec2> vertices,
        List<List<SpriteRigInfluence>> weights,
        IReadOnlyList<int>? triangles,
        int boneIndex,
        Vec2 center,
        BrushOptions options)
    {
        if (boneIndex < 0 || vertices.Count == 0) return false;

        double radius = Math.Max(options.Radius, MinBrushRadius);
        double radiusSquared = radius * radius;

        // スムーズは隣接頂点の平均を要るので、必要なときだけ隣接表を作る
        List<int>[]? adjacency = options.Mode == WeightBrushMode.Smooth && triangles != null
            ? BuildAdjacency(vertices.Count, triangles)
            : null;

        bool changed = false;
        for (int v = 0; v < vertices.Count && v < weights.Count; v++)
        {
            double distanceSquared = Vec2.DistanceSquared(vertices[v], center);
            if (distanceSquared > radiusSquared) continue;

            double falloff = Falloff(Math.Sqrt(distanceSquared) / radius);
            if (falloff <= 0.0) continue;

            double current = GetWeight(weights[v], boneIndex);
            double amount = options.Strength * falloff;
            double target = options.Mode switch
            {
                WeightBrushMode.Add => current + amount,
                WeightBrushMode.Subtract => current - amount,
                WeightBrushMode.Replace => Lerp(current, options.Strength, falloff),
                WeightBrushMode.Smooth => Lerp(current, NeighborAverage(weights, adjacency, v, boneIndex, current), amount),
                _ => current,
            };

            weights[v] = SetBoneWeight(weights[v], boneIndex, target);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// ブラシの減衰カーブ。中心で 1・縁で 0 になる滑らかな山（smoothstep）。
    /// </summary>
    /// <param name="normalizedDistance">中心からの距離 ÷ 半径（0〜1）。</param>
    public static double Falloff(double normalizedDistance)
    {
        double t = 1.0 - Math.Clamp(normalizedDistance, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>線形補間。</summary>
    private static double Lerp(double from, double to, double t) => from + (to - from) * Math.Clamp(t, 0.0, 1.0);

    /// <summary>
    /// 隣接頂点における対象ボーンのウェイト平均（スムーズ用）。隣接が無ければ現在値を返す。
    /// </summary>
    private static double NeighborAverage(
        List<List<SpriteRigInfluence>> weights, List<int>[]? adjacency,
        int vertexIndex, int boneIndex, double fallback)
    {
        if (adjacency == null) return fallback;
        var neighbors = adjacency[vertexIndex];
        if (neighbors.Count == 0) return fallback;

        double sum = 0.0;
        foreach (int n in neighbors) sum += GetWeight(weights[n], boneIndex);
        return sum / neighbors.Count;
    }

    /// <summary>
    /// 三角形インデックスから頂点の隣接表を作る（辺で繋がっている頂点の一覧）。
    /// </summary>
    /// <param name="vertexCount">頂点数。</param>
    /// <param name="triangles">三角形インデックス（3 個 1 組）。</param>
    public static List<int>[] BuildAdjacency(int vertexCount, IReadOnlyList<int> triangles)
    {
        var adjacency = new List<int>[vertexCount];
        for (int i = 0; i < vertexCount; i++) adjacency[i] = new List<int>();

        for (int t = 0; t + Triangulation.IndicesPerTriangle <= triangles.Count;
             t += Triangulation.IndicesPerTriangle)
        {
            int a = triangles[t];
            int b = triangles[t + 1];
            int c = triangles[t + 2];
            if (!InRange(a, vertexCount) || !InRange(b, vertexCount) || !InRange(c, vertexCount)) continue;

            AddOnce(adjacency[a], b); AddOnce(adjacency[a], c);
            AddOnce(adjacency[b], a); AddOnce(adjacency[b], c);
            AddOnce(adjacency[c], a); AddOnce(adjacency[c], b);
        }
        return adjacency;

        static bool InRange(int index, int count) => index >= 0 && index < count;
        static void AddOnce(List<int> list, int value)
        {
            if (!list.Contains(value)) list.Add(value);
        }
    }

    /// <summary>影響一覧の中で指定ボーンが何番目にあるかを返す（無ければ -1）。</summary>
    private static int IndexOfBone(IReadOnlyList<SpriteRigInfluence> influences, int boneIndex)
    {
        for (int i = 0; i < influences.Count; i++)
        {
            if (influences[i].BoneIndex == boneIndex) return i;
        }
        return -1;
    }
}
