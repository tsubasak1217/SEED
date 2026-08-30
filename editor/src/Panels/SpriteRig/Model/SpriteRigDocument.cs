using System;
using System.Collections.Generic;
using System.IO;
using SEEDEditor.Panels.SpriteRig.IO;
using SEEDEditor.Panels.SpriteRig.Mesh;

namespace SEEDEditor.Panels.SpriteRig.Model;

/// <summary>
/// スプライトリグパネルの「1 タブぶん」の編集状態（UI 非依存）。
///
/// 1 枚の画像 + 1 本の <c>.sprite_mesh</c> に対する編集作業をすべてここに閉じ込める。
/// WPF の型を一切持たないので、タブ管理・編集操作・保存はすべて単体テストから直接叩ける
/// （キャンバスの描画と入力だけが <c>SpriteRigCanvas</c> 側の責務）。
///
/// 編集操作は例外なく次の 3 段階で進む:
///   1. <see cref="History"/> へ操作前スナップショットを積む
///   2. 輪郭ポリゴン／内部点（＝入力データ）を書き換える
///   3. <see cref="SpriteRigMesh.Rebuild"/> で三角形を作り直し、<see cref="IsDirty"/> を立てる
/// </summary>
public sealed class SpriteRigDocument
{
    /// <summary>頂点ハンドルのヒット判定半径の既定値（画像ピクセル）。</summary>
    public const double DefaultHitRadius = 6.0;

    /// <summary>「辺の上をクリックした」とみなす距離の既定値（画像ピクセル）。</summary>
    public const double DefaultEdgeHitDistance = 6.0;

    /// <summary>ズーム倍率の下限。</summary>
    public const double MinZoom = 0.05;

    /// <summary>ズーム倍率の上限。</summary>
    public const double MaxZoom = 64.0;

    /// <summary>編集対象画像の絶対パス。</summary>
    public string ImagePath { get; private set; }

    /// <summary>編集対象画像のピクセルデータ。</summary>
    public SpriteImageData Image { get; private set; }

    /// <summary>保存先の <c>.sprite_mesh</c> 絶対パス（未保存なら null）。</summary>
    public string? MeshPath { get; private set; }

    /// <summary>編集中のメッシュ。</summary>
    public SpriteRigMesh Mesh { get; private set; } = new();

    /// <summary>このタブ専用の Undo/Redo 履歴。</summary>
    public MeshHistory History { get; } = new();

    /// <summary>自動メッシュ化のパラメータ（タブごとに独立して覚える）。</summary>
    public AutoMesh.Options AutoMeshOptions { get; } = new();

    /// <summary>未保存の変更があるか。</summary>
    public bool IsDirty { get; private set; }

    /// <summary>現在の編集モード（メッシュ / ボーン / ウェイト）。</summary>
    public SpriteRigEditMode EditMode { get; set; } = SpriteRigEditMode.Mesh;

    /// <summary>メッシュ編集モードでの現在のツール。</summary>
    public SpriteRigMeshTool Tool { get; set; } = SpriteRigMeshTool.Select;

    /// <summary>作図中のポリゴン（まだ閉じていない頂点列）。</summary>
    public List<Vec2> PendingPolygon { get; } = new();

    /// <summary>現在選択中の点（無ければ null）。</summary>
    public SpriteRigPointRef? SelectedPoint { get; set; }

    // ── ビュー状態（タブを切り替えても保つ） ──

    /// <summary>表示倍率。</summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>表示原点のオフセット X（画面ピクセル）。</summary>
    public double OffsetX { get; set; }

    /// <summary>表示原点のオフセット Y（画面ピクセル）。</summary>
    public double OffsetY { get; set; }

    /// <summary>ピクセルグリッドを描くか。</summary>
    public bool ShowPixelGrid { get; set; }

    /// <summary>ドラッグ中に位置を追従させる派生頂点の添字（未ドラッグなら -1）。</summary>
    private int _dragVertexIndex = -1;

    /// <summary>ドラッグ中の点への参照（未ドラッグなら null）。</summary>
    private SpriteRigPointRef? _dragPoint;

    /// <summary>タブ見出しに出す名前（拡張子なし）。</summary>
    public string DisplayName => Path.GetFileNameWithoutExtension(MeshPath ?? ImagePath);

    /// <summary>タブ見出し文字列（未保存なら末尾に * を付ける）。</summary>
    public string TabTitle => IsDirty ? DisplayName + " *" : DisplayName;

    /// <summary>
    /// 画像を対象に新しい編集ドキュメントを作る。
    /// </summary>
    /// <param name="imagePath">画像の絶対パス。</param>
    /// <param name="image">読み込み済みのピクセルデータ。</param>
    /// <param name="meshPath">既存 <c>.sprite_mesh</c> のパス（新規なら null）。</param>
    public SpriteRigDocument(string imagePath, SpriteImageData image, string? meshPath = null)
    {
        ImagePath = imagePath;
        Image = image;
        MeshPath = meshPath;
        Mesh.EnsureRootBone();
    }

    /// <summary>
    /// 既存メッシュを読み込んだ状態のドキュメントを作る。
    /// </summary>
    /// <param name="imagePath">画像の絶対パス。</param>
    /// <param name="image">読み込み済みのピクセルデータ。</param>
    /// <param name="meshPath">読み込んだ <c>.sprite_mesh</c> の絶対パス。</param>
    /// <param name="mesh">復元済みのメッシュ。</param>
    public static SpriteRigDocument FromExistingMesh(
        string imagePath, SpriteImageData image, string meshPath, SpriteRigMesh mesh)
    {
        var document = new SpriteRigDocument(imagePath, image, meshPath);
        document.Mesh = mesh;
        document.Mesh.EnsureRootBone();
        return document;
    }

    /// <summary>
    /// 編集対象の画像だけを差し替える（同じメッシュを別解像度の画像に付け替える用途）。
    /// </summary>
    /// <param name="imagePath">新しい画像の絶対パス。</param>
    /// <param name="image">新しいピクセルデータ。</param>
    public void ReplaceImage(string imagePath, SpriteImageData image)
    {
        ImagePath = imagePath;
        Image = image;
        IsDirty = true;
    }

    // ============================================================
    //  編集操作
    // ============================================================

    /// <summary>
    /// アルファから自動メッシュを生成し、既存のジオメトリを置き換える。
    /// </summary>
    public void ApplyAutoMesh()
    {
        History.Push("自動メッシュ生成", Mesh);

        var generated = AutoMesh.Build(Image, AutoMeshOptions);
        // ボーンは自動生成の対象外なので既存のものを引き継ぐ
        generated.Bones = Mesh.Bones;
        generated.Rebuild(AutoMeshOptions.RefineDelaunay);

        Mesh = generated;
        PendingPolygon.Clear();
        SelectedPoint = null;
        MarkDirty();
    }

    /// <summary>ジオメトリを全消去する（ボーンは残す）。</summary>
    public void ClearGeometry()
    {
        History.Push("メッシュを消去", Mesh);
        Mesh.ClearGeometry();
        Mesh.Rebuild(AutoMeshOptions.RefineDelaunay);
        PendingPolygon.Clear();
        SelectedPoint = null;
        MarkDirty();
    }

    /// <summary>現在の輪郭・内部点から三角形を作り直す。</summary>
    public void Retriangulate()
    {
        History.Push("再三角分割", Mesh);
        Mesh.Rebuild(AutoMeshOptions.RefineDelaunay);
        MarkDirty();
    }

    /// <summary>
    /// 作図中ポリゴンへ頂点を 1 つ足す（画像範囲へクランプされる）。
    /// </summary>
    /// <param name="position">追加する位置（画像ピクセル）。</param>
    public void AddPendingPolygonPoint(Vec2 position)
    {
        PendingPolygon.Add(ClampToImage(position));
    }

    /// <summary>作図中ポリゴンを破棄する。</summary>
    public void CancelPendingPolygon() => PendingPolygon.Clear();

    /// <summary>
    /// 作図中ポリゴンを閉じて輪郭として確定する。
    /// </summary>
    /// <returns>確定できた場合 true（3 頂点未満なら false で破棄）。</returns>
    public bool CommitPendingPolygon()
    {
        if (PendingPolygon.Count < SpriteRigMesh.MinPolygonVertices)
        {
            PendingPolygon.Clear();
            return false;
        }

        History.Push("ポリゴンを追加", Mesh);
        Mesh.Polygons.Add(new SpriteRigPolygon(PendingPolygon));
        PendingPolygon.Clear();
        Mesh.Rebuild(AutoMeshOptions.RefineDelaunay);
        MarkDirty();
        return true;
    }

    /// <summary>
    /// クリック位置に応じて頂点を足す。
    /// 輪郭辺の近くなら<b>その辺を分割</b>し、そうでなければ<b>内部点</b>として追加する。
    /// </summary>
    /// <param name="position">クリック位置（画像ピクセル）。</param>
    /// <param name="edgeHitDistance">辺上とみなす距離（画像ピクセル）。</param>
    /// <returns>追加できた場合 true。</returns>
    public bool AddVertexAt(Vec2 position, double edgeHitDistance = DefaultEdgeHitDistance)
    {
        Vec2 clamped = ClampToImage(position);

        // ── まず輪郭辺の分割を試す ──
        if (FindNearestPolygonEdge(clamped, edgeHitDistance, out int polygonIndex, out int edgeIndex))
        {
            History.Push("辺を分割", Mesh);
            Mesh.Polygons[polygonIndex].Points.Insert(edgeIndex + 1, clamped);
            Mesh.Rebuild(AutoMeshOptions.RefineDelaunay);
            MarkDirty();
            return true;
        }

        // ── 領域の内側なら内部点として追加する ──
        if (!IsInsideMeshRegion(clamped)) return false;

        History.Push("内部点を追加", Mesh);
        Mesh.InteriorPoints.Add(clamped);
        Mesh.Rebuild(AutoMeshOptions.RefineDelaunay);
        MarkDirty();
        return true;
    }

    /// <summary>
    /// 指定した点を削除する。輪郭頂点を消してポリゴンが 3 頂点未満になる場合はポリゴンごと消える。
    /// </summary>
    /// <param name="point">削除する点への参照。</param>
    /// <returns>削除できた場合 true。</returns>
    public bool DeletePoint(SpriteRigPointRef point)
    {
        if (!IsValidPoint(point)) return false;

        History.Push("頂点を削除", Mesh);
        if (point.IsInterior)
        {
            Mesh.InteriorPoints.RemoveAt(point.PointIndex);
        }
        else
        {
            var polygon = Mesh.Polygons[point.PolygonIndex];
            polygon.Points.RemoveAt(point.PointIndex);
            if (polygon.Points.Count < SpriteRigMesh.MinPolygonVertices)
                Mesh.Polygons.RemoveAt(point.PolygonIndex);
        }

        SelectedPoint = null;
        Mesh.Rebuild(AutoMeshOptions.RefineDelaunay);
        MarkDirty();
        return true;
    }

    // ── 頂点ドラッグ（開始・更新・終了の 3 段階） ──

    /// <summary>
    /// 頂点ドラッグを開始する。Undo スナップショットはここで 1 回だけ積む。
    /// </summary>
    /// <param name="point">動かす点。</param>
    /// <returns>開始できた場合 true。</returns>
    public bool BeginPointDrag(SpriteRigPointRef point)
    {
        if (!IsValidPoint(point)) return false;

        History.Push("頂点を移動", Mesh);
        _dragPoint = point;
        // ドラッグ中は三角形を組み直さず、対応する派生頂点の座標だけ追従させる
        _dragVertexIndex = FindVertexIndexAt(GetPointPosition(point));
        return true;
    }

    /// <summary>
    /// ドラッグ中の点を移動する（三角形の再構築はしない）。
    /// </summary>
    /// <param name="position">新しい位置（画像ピクセル）。</param>
    public void UpdatePointDrag(Vec2 position)
    {
        if (_dragPoint is not { } point) return;

        Vec2 clamped = ClampToImage(position);
        SetPointPosition(point, clamped);
        if (_dragVertexIndex >= 0 && _dragVertexIndex < Mesh.Vertices.Count)
            Mesh.Vertices[_dragVertexIndex] = clamped;
    }

    /// <summary>
    /// ドラッグを終了し、三角形を作り直す。
    /// </summary>
    public void EndPointDrag()
    {
        if (_dragPoint is null) return;
        _dragPoint = null;
        _dragVertexIndex = -1;
        Mesh.Rebuild(AutoMeshOptions.RefineDelaunay);
        MarkDirty();
    }

    /// <summary>ドラッグ中かどうか。</summary>
    public bool IsDraggingPoint => _dragPoint is not null;

    // ── Undo / Redo ──

    /// <summary>直前の操作を取り消す。</summary>
    /// <returns>取り消した場合 true。</returns>
    public bool Undo()
    {
        var restored = History.Undo(Mesh);
        if (restored is null) return false;
        Mesh = restored;
        PendingPolygon.Clear();
        SelectedPoint = null;
        MarkDirty();
        return true;
    }

    /// <summary>取り消した操作をやり直す。</summary>
    /// <returns>やり直した場合 true。</returns>
    public bool Redo()
    {
        var restored = History.Redo(Mesh);
        if (restored is null) return false;
        Mesh = restored;
        PendingPolygon.Clear();
        SelectedPoint = null;
        MarkDirty();
        return true;
    }

    // ============================================================
    //  ヒットテスト・座標ヘルパー
    // ============================================================

    /// <summary>
    /// 指定位置に最も近い編集点を探す。
    /// </summary>
    /// <param name="position">探索位置（画像ピクセル）。</param>
    /// <param name="radius">ヒット半径（画像ピクセル）。</param>
    /// <returns>見つかった点への参照。見つからなければ null。</returns>
    public SpriteRigPointRef? HitTestPoint(Vec2 position, double radius = DefaultHitRadius)
    {
        SpriteRigPointRef? best = null;
        double bestDistance = radius;

        for (int p = 0; p < Mesh.Polygons.Count; p++)
        {
            var points = Mesh.Polygons[p].Points;
            for (int i = 0; i < points.Count; i++)
            {
                double d = Vec2.Distance(points[i], position);
                if (d > bestDistance) continue;
                bestDistance = d;
                best = new SpriteRigPointRef(p, i);
            }
        }

        for (int i = 0; i < Mesh.InteriorPoints.Count; i++)
        {
            double d = Vec2.Distance(Mesh.InteriorPoints[i], position);
            if (d > bestDistance) continue;
            bestDistance = d;
            best = SpriteRigPointRef.Interior(i);
        }

        return best;
    }

    /// <summary>参照が現在のメッシュに対して有効かを判定する。</summary>
    /// <param name="point">検査する参照。</param>
    public bool IsValidPoint(SpriteRigPointRef point)
    {
        if (point.IsInterior)
            return point.PointIndex >= 0 && point.PointIndex < Mesh.InteriorPoints.Count;
        if (point.PolygonIndex < 0 || point.PolygonIndex >= Mesh.Polygons.Count) return false;
        return point.PointIndex >= 0 && point.PointIndex < Mesh.Polygons[point.PolygonIndex].Points.Count;
    }

    /// <summary>参照が指す点の座標を返す。</summary>
    /// <param name="point">対象の参照（有効であること）。</param>
    public Vec2 GetPointPosition(SpriteRigPointRef point)
        => point.IsInterior
            ? Mesh.InteriorPoints[point.PointIndex]
            : Mesh.Polygons[point.PolygonIndex].Points[point.PointIndex];

    /// <summary>参照が指す点の座標を書き換える。</summary>
    /// <param name="point">対象の参照（有効であること）。</param>
    /// <param name="position">新しい座標。</param>
    private void SetPointPosition(SpriteRigPointRef point, Vec2 position)
    {
        if (point.IsInterior) Mesh.InteriorPoints[point.PointIndex] = position;
        else Mesh.Polygons[point.PolygonIndex].Points[point.PointIndex] = position;
    }

    /// <summary>座標が一致する派生頂点の添字を探す（無ければ -1）。</summary>
    private int FindVertexIndexAt(Vec2 position)
    {
        for (int i = 0; i < Mesh.Vertices.Count; i++)
        {
            if (Vec2.DistanceSquared(Mesh.Vertices[i], position) < Geometry2D.Epsilon) return i;
        }
        return -1;
    }

    /// <summary>
    /// 指定位置に最も近い輪郭辺を探す。
    /// </summary>
    /// <param name="position">探索位置。</param>
    /// <param name="maxDistance">辺上とみなす最大距離。</param>
    /// <param name="polygonIndex">見つかったポリゴンの添字。</param>
    /// <param name="edgeIndex">見つかった辺の始点添字。</param>
    /// <returns>見つかった場合 true。</returns>
    public bool FindNearestPolygonEdge(
        Vec2 position, double maxDistance, out int polygonIndex, out int edgeIndex)
    {
        polygonIndex = -1;
        edgeIndex = -1;
        double bestDistance = maxDistance;

        for (int p = 0; p < Mesh.Polygons.Count; p++)
        {
            var points = Mesh.Polygons[p].Points;
            for (int i = 0; i < points.Count; i++)
            {
                Vec2 a = points[i];
                Vec2 b = points[(i + 1) % points.Count];
                double d = Geometry2D.DistancePointSegment(a, b, position);
                if (d > bestDistance) continue;
                bestDistance = d;
                polygonIndex = p;
                edgeIndex = i;
            }
        }
        return polygonIndex >= 0;
    }

    /// <summary>位置がメッシュ領域（外周の内側かつ穴の外側）にあるか。</summary>
    /// <param name="position">判定する位置。</param>
    public bool IsInsideMeshRegion(Vec2 position)
    {
        bool inside = false;
        foreach (var polygon in Mesh.Polygons)
        {
            if (polygon.Points.Count < SpriteRigMesh.MinPolygonVertices) continue;
            // 入れ子の偶奇で内外を決める（穴の中は「外」になる）
            if (Geometry2D.PointInPolygon(polygon.Points, position)) inside = !inside;
        }
        return inside;
    }

    /// <summary>座標を画像の範囲内へ丸める。</summary>
    /// <param name="position">元の座標。</param>
    public Vec2 ClampToImage(Vec2 position)
        => new(Math.Clamp(position.X, 0.0, Image.Width), Math.Clamp(position.Y, 0.0, Image.Height));

    // ============================================================
    //  保存
    // ============================================================

    /// <summary>
    /// 既定の保存先（画像と同じフォルダに同名の <c>.sprite_mesh</c>）。
    /// </summary>
    public string DefaultMeshPath
        => MeshPath ?? Path.ChangeExtension(ImagePath, SpriteMeshFile.Extension);

    /// <summary>
    /// <c>.sprite_mesh</c> として保存する。
    /// </summary>
    /// <param name="path">保存先（null なら <see cref="DefaultMeshPath"/>）。</param>
    /// <returns>実際に保存したパス。</returns>
    public string Save(string? path = null)
    {
        string target = path ?? DefaultMeshPath;
        SpriteMeshFile.Save(target, Mesh, Image.Width, Image.Height, ImagePath, DisplayNameFor(target));
        MeshPath = target;
        IsDirty = false;
        return target;
    }

    /// <summary>保存先パスから <c>name</c> フィールドに書く名前を作る。</summary>
    private static string DisplayNameFor(string path) => Path.GetFileNameWithoutExtension(path);

    /// <summary>未保存フラグを立てる。</summary>
    private void MarkDirty() => IsDirty = true;
}
