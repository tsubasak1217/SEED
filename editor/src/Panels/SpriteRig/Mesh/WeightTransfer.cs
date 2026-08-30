using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.SpriteRig.Mesh;

/// <summary>
/// メッシュを作り直したときに、<b>旧頂点のウェイトを新頂点へ座標で引き継ぐ</b>処理。
///
/// <para>
/// 編集モデルの都合上、輪郭や内部点をいじるたびに三角分割をやり直すため、
/// 頂点配列は毎回まるごと作り直される（頂点の添字は保存されない）。
/// そこで「同じ位置にある頂点は同じ頂点である」とみなして、
/// <b>最近傍の旧頂点</b>からウェイトをコピーする。
/// </para>
///
/// <para>
/// 実際、輪郭点と内部点はそのまま新しい頂点配列に現れるので、
/// 移動していない点については距離 0 の完全一致で引き継がれる。
/// 新しく増えた点（辺の分割で生まれた点など）は、いちばん近い既存点の重みを受け継ぐ
/// ―― 隣り合う点はウェイトも近い、という前提での近似である。
/// </para>
///
/// <para>
/// 旧ウェイトが 1 つも無い（＝初回生成）場合は、全頂点をルート（添字 0）へ 1.0 で張る。
/// これは <c>.sprite_mesh</c> が「影響 0 本の頂点」を許さないための最低保証でもある。
/// </para>
/// </summary>
public static class WeightTransfer
{
    /// <summary>
    /// 旧頂点のウェイトを新頂点へ引き継ぐ。
    /// </summary>
    /// <param name="previousVertices">作り直す前の頂点位置。</param>
    /// <param name="previousWeights">作り直す前の頂点ごとの影響（頂点数と同数でなくてもよい）。</param>
    /// <param name="newVertices">作り直した後の頂点位置。</param>
    /// <param name="boneCount">現在のボーン本数（範囲外の影響を捨てるため）。</param>
    /// <returns>新頂点数ぶんの影響一覧（各要素は正規化済み）。</returns>
    public static List<List<SpriteRigInfluence>> Transfer(
        IReadOnlyList<Vec2> previousVertices,
        IReadOnlyList<List<SpriteRigInfluence>> previousWeights,
        IReadOnlyList<Vec2> newVertices,
        int boneCount)
    {
        var result = new List<List<SpriteRigInfluence>>(newVertices.Count);

        // 引き継げる旧データが無ければ、ルート 1.0 で埋める
        int usable = Math.Min(previousVertices.Count, previousWeights.Count);
        if (usable == 0 || boneCount <= 0)
        {
            for (int i = 0; i < newVertices.Count; i++)
                result.Add(new List<SpriteRigInfluence> { new(0, 1.0) });
            return result;
        }

        foreach (var vertex in newVertices)
        {
            int nearest = FindNearest(previousVertices, usable, vertex);
            result.Add(SanitizeForBoneCount(previousWeights[nearest], boneCount));
        }
        return result;
    }

    /// <summary>
    /// 指定位置に最も近い旧頂点の添字を返す。
    /// </summary>
    /// <param name="vertices">旧頂点の一覧。</param>
    /// <param name="count">走査する範囲（先頭からこの個数まで）。</param>
    /// <param name="position">探索位置。</param>
    private static int FindNearest(IReadOnlyList<Vec2> vertices, int count, Vec2 position)
    {
        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            double distance = Vec2.DistanceSquared(vertices[i], position);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
            if (distance <= Geometry2D.Epsilon) break;   // 完全一致より近い点は無い
        }
        return best;
    }

    /// <summary>
    /// 影響一覧から現在のボーン数の範囲外を除き、正規化して返す。
    /// </summary>
    /// <param name="influences">元の影響一覧。</param>
    /// <param name="boneCount">現在のボーン本数。</param>
    private static List<SpriteRigInfluence> SanitizeForBoneCount(
        IReadOnlyList<SpriteRigInfluence> influences, int boneCount)
    {
        var valid = new List<SpriteRigInfluence>(influences.Count);
        foreach (var influence in influences)
        {
            if (influence.BoneIndex < 0 || influence.BoneIndex >= boneCount) continue;
            valid.Add(influence);
        }
        return WeightPaint.Normalize(valid);
    }
}
