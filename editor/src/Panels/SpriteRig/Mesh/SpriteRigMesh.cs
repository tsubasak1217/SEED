using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.SpriteRig.Mesh;

/// <summary>
/// スプライトリグの編集対象メッシュ（UI 非依存のデータモデル）。
///
/// 【編集モデルの要】
/// ユーザーが直接触るのは <b>輪郭ポリゴン（<see cref="Polygons"/>）と内部点
/// （<see cref="InteriorPoints"/>）だけ</b>で、三角形（<see cref="Triangles"/>）と
/// 頂点配列（<see cref="Vertices"/>）はそこから <see cref="Rebuild"/> で毎回作り直す
/// <b>派生データ</b>である。
///
/// この形にしているのは、どの編集操作（ポリゴン追加・頂点追加・移動・削除）も
/// 「入力データを変えて作り直す」1 本の道に揃えられるためで、
/// 三角形リストを直接いじる場合に必ず出てくる「辺の整合が壊れる」問題が起きない。
///
/// 穴かどうかはユーザーに指定させず、<b>他ポリゴンに何重に囲まれているか（偶奇）</b>で
/// 自動判定する（偶数 = 外周・奇数 = 穴）。
///
/// ボーンとウェイトは Phase B1b（ボーン配置・ウェイトペイント）で本格的に使う。
/// B1a では「ルート 1 本・全頂点がルートへ 1.0」を保つだけで、
/// 保存した <c>.sprite_mesh</c> がランタイムの検証を通ることを保証する。
/// </summary>
public sealed class SpriteRigMesh
{
    /// <summary>ルートボーンの既定名（B1a が自動生成する唯一のボーン）。</summary>
    public const string RootBoneName = "root";

    /// <summary>多角形として成立する最小頂点数。</summary>
    public const int MinPolygonVertices = 3;

    /// <summary>輪郭ポリゴン（外周・穴の区別は <see cref="Rebuild"/> が付ける）。</summary>
    public List<SpriteRigPolygon> Polygons { get; private set; } = new();

    /// <summary>輪郭に属さない内部点（三角形密度を上げるための点）。</summary>
    public List<Vec2> InteriorPoints { get; private set; } = new();

    /// <summary>三角分割後の頂点（派生データ。直接編集しない）。</summary>
    public List<Vec2> Vertices { get; private set; } = new();

    /// <summary>三角形インデックス（派生データ。3 個 1 組）。</summary>
    public List<int> Triangles { get; private set; } = new();

    /// <summary>
    /// ボーン宣言（B1a ではルート 1 本）。
    /// 自動メッシュ生成で作り直したメッシュへ既存ボーンを引き継げるよう、外から差し替え可能にしてある。
    /// </summary>
    public List<SpriteRigBone> Bones { get; set; } = new();

    /// <summary>頂点ごとのボーン影響（<see cref="Vertices"/> と同数）。</summary>
    public List<List<SpriteRigInfluence>> Weights { get; private set; } = new();

    /// <summary>三角形の個数。</summary>
    public int TriangleCount => Triangles.Count / Triangulation.IndicesPerTriangle;

    /// <summary>編集可能な点（輪郭点 + 内部点）が 1 つでもあるか。</summary>
    public bool HasGeometry => Polygons.Count > 0 || InteriorPoints.Count > 0;

    /// <summary>
    /// 輪郭ポリゴンと内部点から三角形を作り直す。
    ///
    /// 穴の判定（偶奇の入れ子）→ 領域ごとの制約付き三角分割 →
    /// ウェイトの張り直し、の順に行う。
    /// </summary>
    /// <param name="refineDelaunay">true なら辺フリップで三角形の形を整える。</param>
    public void Rebuild(bool refineDelaunay = true)
    {
        ClassifyHoles();

        var regions = BuildRegions();
        var result = Triangulation.Triangulate(regions, InteriorPoints, refineDelaunay);

        Vertices = result.Vertices;
        Triangles = result.Triangles;

        EnsureRootBone();
        ResetWeightsToRoot();
    }

    /// <summary>
    /// 各ポリゴンについて「他のポリゴンに何重に囲まれているか」を数え、
    /// 奇数なら穴、偶数なら外周として <see cref="SpriteRigPolygon.IsHole"/> を更新する。
    /// </summary>
    private void ClassifyHoles()
    {
        foreach (var polygon in Polygons)
        {
            if (polygon.Points.Count < MinPolygonVertices)
            {
                polygon.IsHole = false;
                continue;
            }

            // 代表点（重心ではなく先頭頂点の少し内側でなく、単純に先頭頂点）で包含を数える。
            // 輪郭同士は交差しない前提なので、どの点で数えても結果は同じになる。
            Vec2 sample = polygon.Points[0];
            int enclosingCount = 0;
            foreach (var other in Polygons)
            {
                if (ReferenceEquals(other, polygon)) continue;
                if (other.Points.Count < MinPolygonVertices) continue;
                if (Geometry2D.PointInPolygon(other.Points, sample)) enclosingCount++;
            }
            polygon.IsHole = (enclosingCount % 2) == 1;
        }
    }

    /// <summary>
    /// 外周ポリゴンごとに、その内側にある穴をまとめて三角分割用の領域を作る。
    /// </summary>
    private List<Triangulation.Region> BuildRegions()
    {
        var regions = new List<Triangulation.Region>();
        var outers = new List<SpriteRigPolygon>();
        var holes = new List<SpriteRigPolygon>();

        foreach (var polygon in Polygons)
        {
            if (polygon.Points.Count < MinPolygonVertices) continue;
            if (polygon.IsHole) holes.Add(polygon);
            else outers.Add(polygon);
        }

        foreach (var outer in outers)
        {
            var owned = new List<List<Vec2>>();
            foreach (var hole in holes)
            {
                // 穴は「自分を囲む最小面積の外周」に属させる（入れ子が深くても正しく割り当たる）
                if (FindSmallestEnclosingOuter(outers, hole) == outer)
                    owned.Add(new List<Vec2>(hole.Points));
            }
            regions.Add(new Triangulation.Region(new List<Vec2>(outer.Points), owned));
        }
        return regions;
    }

    /// <summary>穴を囲む外周のうち、面積が最小のものを返す（見つからなければ null）。</summary>
    private static SpriteRigPolygon? FindSmallestEnclosingOuter(
        List<SpriteRigPolygon> outers, SpriteRigPolygon hole)
    {
        SpriteRigPolygon? best = null;
        double bestArea = double.PositiveInfinity;
        Vec2 sample = hole.Points[0];

        foreach (var outer in outers)
        {
            if (!Geometry2D.PointInPolygon(outer.Points, sample)) continue;
            double area = Math.Abs(Geometry2D.SignedArea(outer.Points));
            if (area < bestArea)
            {
                bestArea = area;
                best = outer;
            }
        }
        return best;
    }

    /// <summary>
    /// ルートボーンが 1 本も無ければ作る（<c>.sprite_mesh</c> は bones 空を許さない）。
    /// </summary>
    public void EnsureRootBone()
    {
        if (Bones.Count > 0) return;
        Bones.Add(SpriteRigBone.CreateRoot(RootBoneName));
    }

    /// <summary>
    /// 全頂点のウェイトを「ルートボーンへ 1.0」に張り直す。
    ///
    /// B1a ではウェイト編集 UI を持たないため、頂点数が変わるたびにこれで作り直す。
    /// B1b でウェイトペイントを入れる際は、ここを「既存ウェイトを座標で引き継ぐ」実装へ置き換える。
    /// </summary>
    public void ResetWeightsToRoot()
    {
        EnsureRootBone();
        Weights = new List<List<SpriteRigInfluence>>(Vertices.Count);
        for (int i = 0; i < Vertices.Count; i++)
        {
            Weights.Add(new List<SpriteRigInfluence> { new(0, 1.0) });
        }
    }

    /// <summary>
    /// 深いコピーを作る（Undo/Redo のスナップショット用）。
    /// </summary>
    public SpriteRigMesh Clone()
    {
        var clone = new SpriteRigMesh
        {
            Polygons = new List<SpriteRigPolygon>(Polygons.Count),
            InteriorPoints = new List<Vec2>(InteriorPoints),
            Vertices = new List<Vec2>(Vertices),
            Triangles = new List<int>(Triangles),
            Bones = new List<SpriteRigBone>(Bones.Count),
            Weights = new List<List<SpriteRigInfluence>>(Weights.Count),
        };
        foreach (var polygon in Polygons) clone.Polygons.Add(polygon.Clone());
        foreach (var bone in Bones) clone.Bones.Add(bone.Clone());
        foreach (var influences in Weights) clone.Weights.Add(new List<SpriteRigInfluence>(influences));
        return clone;
    }

    /// <summary>
    /// 全ジオメトリを捨てる（自動メッシュ再生成の前処理）。ボーンは残す。
    /// </summary>
    public void ClearGeometry()
    {
        Polygons.Clear();
        InteriorPoints.Clear();
        Vertices.Clear();
        Triangles.Clear();
        Weights.Clear();
    }
}

/// <summary>
/// 輪郭ポリゴン 1 本。外周か穴かは入れ子の偶奇から自動判定される。
/// </summary>
public sealed class SpriteRigPolygon
{
    /// <summary>閉じた頂点列（末尾と先頭が暗黙に繋がる）。</summary>
    public List<Vec2> Points { get; set; } = new();

    /// <summary>true = 穴。<see cref="SpriteRigMesh.Rebuild"/> が毎回計算し直す表示用フラグ。</summary>
    public bool IsHole { get; set; }

    /// <summary>空のポリゴンを作る。</summary>
    public SpriteRigPolygon() { }

    /// <summary>頂点列から作る。</summary>
    public SpriteRigPolygon(IEnumerable<Vec2> points) => Points = new List<Vec2>(points);

    /// <summary>深いコピー。</summary>
    public SpriteRigPolygon Clone() => new(Points) { IsHole = IsHole };
}

/// <summary>
/// バインドポーズのボーン宣言。<c>.sprite_mesh</c> の <c>bones</c> 要素と 1:1 対応する。
/// </summary>
public sealed class SpriteRigBone
{
    /// <summary>ボーン名（シーン上の子アクター名と突き合わせるキー・重複不可）。</summary>
    public string Name { get; set; } = SpriteRigMesh.RootBoneName;

    /// <summary>親ボーン名（空 = ルート）。</summary>
    public string Parent { get; set; } = string.Empty;

    /// <summary>バインドポーズのローカル位置（キャンバスピクセル）。</summary>
    public Vec2 Position { get; set; } = Vec2.Zero;

    /// <summary>バインドポーズのローカル回転（度・Z 軸まわり）。</summary>
    public double Rotation { get; set; }

    /// <summary>バインドポーズのローカルスケール。</summary>
    public Vec2 Scale { get; set; } = new(1.0, 1.0);

    /// <summary>無変形のルートボーンを作る。</summary>
    /// <param name="name">ボーン名。</param>
    public static SpriteRigBone CreateRoot(string name) => new() { Name = name };

    /// <summary>深いコピー。</summary>
    public SpriteRigBone Clone() => new()
    {
        Name = Name,
        Parent = Parent,
        Position = Position,
        Rotation = Rotation,
        Scale = Scale,
    };
}

/// <summary>
/// 1 頂点に対する 1 本ぶんのボーン影響。<c>.sprite_mesh</c> の <c>weights</c> 要素と対応する。
/// </summary>
/// <param name="BoneIndex">影響するボーンの添字（<see cref="SpriteRigMesh.Bones"/> の位置）。</param>
/// <param name="Weight">影響度（保存時に合計 1.0 へ正規化される）。</param>
public readonly record struct SpriteRigInfluence(int BoneIndex, double Weight);
