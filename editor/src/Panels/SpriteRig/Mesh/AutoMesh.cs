using System;
using System.Collections.Generic;
using SEEDEditor.Panels.SpriteRig.Model;

namespace SEEDEditor.Panels.SpriteRig.Mesh;

/// <summary>
/// 画像のアルファから輪郭付きメッシュを自動生成するパイプライン。
///
/// 【手順】
///   1. <b>二値化</b>: アルファが閾値以上のピクセルを不透明とみなす。
///   2. <b>輪郭抽出</b>: <see cref="ContourTracer"/> でピクセル境界を辿り、外周・穴・複数島を得る。
///   3. <b>簡略化</b>: <see cref="PolylineSimplifier"/>（Douglas-Peucker）で階段状の頂点を間引く。
///   4. <b>内部点配置</b>: 格子状に候補を撒き、領域内かつ輪郭から十分離れた点だけ残す。
///   5. <b>三角分割</b>: <see cref="Triangulation"/> の制約付き三角分割にかける。
///
/// 全透明・全不透明のどちらでも破綻しない:
/// 不透明な画素が 1 つも無ければ画像全体の矩形を輪郭として使い、
/// 全画素が不透明なら手順 2 が画像枠そのものを返す（手順 3 で 4 隅へ簡略化される）。
/// </summary>
public static class AutoMesh
{
    /// <summary>
    /// 自動メッシュ化のパラメータ（UI のスライダーと 1:1 対応）。
    /// </summary>
    public sealed class Options
    {
        /// <summary>アルファ閾値の既定値（この値以上を不透明とみなす）。</summary>
        public const int DefaultAlphaThreshold = 128;

        /// <summary>輪郭簡略化の許容誤差の既定値（ピクセル）。</summary>
        public const double DefaultSimplifyTolerance = 2.0;

        /// <summary>内部点の目標間隔の既定値（ピクセル）。0 なら内部点を置かない。</summary>
        public const double DefaultInteriorSpacing = 24.0;

        /// <summary>ノイズとして捨てる島の最小面積の既定値（ピクセル^2）。</summary>
        public const double DefaultMinIslandArea = 16.0;

        /// <summary>
        /// 内部点を輪郭からどれだけ離すかの係数（間隔に対する比率）。
        /// 輪郭のすぐ内側に点を置くと極端に細長い三角形ができるため、
        /// 「間隔 × この比率」より輪郭に近い候補は捨てる。
        /// </summary>
        public const double InteriorEdgeClearanceRatio = 0.6;

        /// <summary>
        /// 内部点の行を半ピッチずらす量（六方格子にして三角形を正三角形へ近づける）。
        /// </summary>
        public const double StaggerRatio = 0.5;

        /// <summary>アルファ閾値（0〜255）。この値以上を不透明とみなす。</summary>
        public int AlphaThreshold { get; set; } = DefaultAlphaThreshold;

        /// <summary>輪郭簡略化の許容誤差（ピクセル）。大きいほど頂点が減る。</summary>
        public double SimplifyTolerance { get; set; } = DefaultSimplifyTolerance;

        /// <summary>内部点の目標間隔（ピクセル）。0 以下なら内部点を置かない。</summary>
        public double InteriorSpacing { get; set; } = DefaultInteriorSpacing;

        /// <summary>この面積未満の輪郭はノイズとして捨てる（ピクセル^2）。</summary>
        public double MinIslandArea { get; set; } = DefaultMinIslandArea;

        /// <summary>辺フリップによる Delaunay 化を行うか。</summary>
        public bool RefineDelaunay { get; set; } = true;
    }

    /// <summary>
    /// 画像から輪郭ポリゴンと内部点を生成し、三角分割済みのメッシュを返す。
    /// </summary>
    /// <param name="image">対象画像（アルファのみ見る）。</param>
    /// <param name="options">生成パラメータ。</param>
    /// <returns>新しく構築されたメッシュ（既存メッシュは破棄して差し替える想定）。</returns>
    public static SpriteRigMesh Build(SpriteImageData image, Options options)
    {
        var mesh = new SpriteRigMesh();

        // ── 1〜3. 二値化 → 輪郭抽出 → 簡略化 ──
        foreach (var polygon in BuildContourPolygons(image, options))
        {
            mesh.Polygons.Add(polygon);
        }

        // ── 4. 内部点の配置 ──
        // 穴の判定は Rebuild が行うが、内部点の可否判定にも必要なので先に一度だけ走らせる。
        mesh.Rebuild(options.RefineDelaunay);
        foreach (var point in GenerateInteriorPoints(mesh, image, options))
        {
            mesh.InteriorPoints.Add(point);
        }

        // ── 5. 内部点込みで最終的な三角分割を行う ──
        mesh.Rebuild(options.RefineDelaunay);
        return mesh;
    }

    /// <summary>
    /// 画像のアルファから輪郭ポリゴン（簡略化済み）を作る。
    /// 不透明画素が 1 つも無い場合は画像全体の矩形を 1 本だけ返す。
    /// </summary>
    /// <param name="image">対象画像。</param>
    /// <param name="options">生成パラメータ。</param>
    public static List<SpriteRigPolygon> BuildContourPolygons(SpriteImageData image, Options options)
    {
        var solid = image.BuildSolidMask(options.AlphaThreshold);
        var contours = ContourTracer.Trace(solid, image.Width, image.Height, options.MinIslandArea);

        var polygons = new List<SpriteRigPolygon>(contours.Count);
        foreach (var contour in contours)
        {
            var simplified = PolylineSimplifier.SimplifyClosed(contour.Points, options.SimplifyTolerance);
            if (simplified.Count < SpriteRigMesh.MinPolygonVertices) continue;
            polygons.Add(new SpriteRigPolygon(simplified) { IsHole = contour.IsHole });
        }

        // 完全透明画像などで輪郭が 1 本も取れなかった場合は画像枠を使う
        // （従来スプライトと同じ矩形メッシュになり、編集の出発点として破綻しない）。
        if (polygons.Count == 0) polygons.Add(CreateImageRectangle(image));

        return polygons;
    }

    /// <summary>
    /// 画像全体を覆う矩形ポリゴン（外周向き = 正の符号付き面積）を作る。
    /// </summary>
    /// <param name="image">対象画像。</param>
    public static SpriteRigPolygon CreateImageRectangle(SpriteImageData image)
    {
        double w = image.Width;
        double h = image.Height;
        return new SpriteRigPolygon(new[]
        {
            new Vec2(0.0, 0.0),
            new Vec2(w, 0.0),
            new Vec2(w, h),
            new Vec2(0.0, h),
        });
    }

    /// <summary>
    /// 領域の内部に、指定間隔の六方格子で点を撒く。
    /// 輪郭から <see cref="Options.InteriorEdgeClearanceRatio"/> 倍の距離より近い候補は捨てる。
    /// </summary>
    /// <param name="mesh">輪郭が入っており、穴判定済み（Rebuild 済み）のメッシュ。</param>
    /// <param name="image">画像（配置範囲の上限に使う）。</param>
    /// <param name="options">生成パラメータ。</param>
    public static List<Vec2> GenerateInteriorPoints(
        SpriteRigMesh mesh, SpriteImageData image, Options options)
    {
        var points = new List<Vec2>();
        double spacing = options.InteriorSpacing;
        if (spacing <= 0.0) return points;

        double clearance = spacing * Options.InteriorEdgeClearanceRatio;
        double rowStep = spacing * Math.Sqrt(3.0) * 0.5;   // 正三角形格子の行間

        // 外周ポリゴンごとに、その外接矩形の中だけを走査する
        foreach (var outer in mesh.Polygons)
        {
            if (outer.IsHole || outer.Points.Count < SpriteRigMesh.MinPolygonVertices) continue;

            GetBounds(outer.Points, out double minX, out double minY, out double maxX, out double maxY);
            minX = Math.Max(minX, 0.0);
            minY = Math.Max(minY, 0.0);
            maxX = Math.Min(maxX, image.Width);
            maxY = Math.Min(maxY, image.Height);

            int row = 0;
            for (double y = minY + rowStep * Options.StaggerRatio; y < maxY; y += rowStep, row++)
            {
                // 奇数行を半ピッチずらして六方格子にする
                double offset = (row % 2 == 0) ? 0.0 : spacing * Options.StaggerRatio;
                for (double x = minX + offset; x < maxX; x += spacing)
                {
                    var candidate = new Vec2(x, y);
                    if (!Geometry2D.PointInPolygon(outer.Points, candidate)) continue;
                    if (Geometry2D.DistanceToPolygonEdges(outer.Points, candidate) < clearance) continue;
                    if (IsTooCloseToHole(mesh, candidate, clearance)) continue;

                    points.Add(candidate);
                }
            }
        }
        return points;
    }

    /// <summary>
    /// 候補点が、いずれかの穴の内部にあるか、穴の輪郭に近すぎるかを判定する。
    /// </summary>
    private static bool IsTooCloseToHole(SpriteRigMesh mesh, Vec2 candidate, double clearance)
    {
        foreach (var hole in mesh.Polygons)
        {
            if (!hole.IsHole || hole.Points.Count < SpriteRigMesh.MinPolygonVertices) continue;
            if (Geometry2D.PointInPolygon(hole.Points, candidate)) return true;
            if (Geometry2D.DistanceToPolygonEdges(hole.Points, candidate) < clearance) return true;
        }
        return false;
    }

    /// <summary>頂点列の外接矩形を求める。</summary>
    private static void GetBounds(
        IReadOnlyList<Vec2> points,
        out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = double.PositiveInfinity;
        minY = double.PositiveInfinity;
        maxX = double.NegativeInfinity;
        maxY = double.NegativeInfinity;
        foreach (var p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
    }
}
