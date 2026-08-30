using System;
using System.Collections.Generic;
using System.IO;
using SEEDEditor.Panels.SpriteRig.IO;
using SEEDEditor.Panels.SpriteRig.Mesh;
using SEEDEditor.Panels.SpriteRig.Model;

namespace SpriteRigTests;

/// <summary>
/// スプライトリグ（Phase B1a）のアルゴリズム単体テスト一式。
///
/// 検証の柱は 4 つ:
///   1. <b>輪郭抽出</b>が外周・穴・複数島を正しい向き（外周 = 正面積 / 穴 = 負面積）で返す
///   2. <b>三角分割</b>が輪郭からはみ出さない（全三角形の面積合計 = 領域面積、重心が領域内）
///   3. <b>書き出した .sprite_mesh</b> がランタイムのパーサの検証条件をすべて満たす
///      （実際に Rust 側で読めることは runtime のテストが
///       `runtime/tests/fixtures/generated_circle.sprite_mesh` を読んで確認する）
///   4. <b>タブ管理</b>が「別画像をインポートしてもタブが増えるだけ」を守る
/// </summary>
public static class Program
{
    /// <summary>Rust 側テストへ渡す、自動生成メッシュのフィクスチャ相対パス。</summary>
    private const string GeneratedFixtureRelativePath =
        "runtime/tests/fixtures/generated_circle.sprite_mesh";

    /// <summary>面積比較の相対許容誤差。</summary>
    private const double AreaRelativeTolerance = 1.0e-6;

    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();

        harness.Add("不透明のみの画像は画像枠そのものの矩形輪郭になる", OpaqueImageBecomesRectangle);
        harness.Add("透過円の輪郭は外周 1 本・正の面積", CircleProducesSingleOuterContour);
        harness.Add("ドーナツは外周 1 本 + 穴 1 本になる", DonutProducesOuterAndHole);
        harness.Add("離れた 2 つの島は輪郭 2 本になる", TwoIslandsProduceTwoContours);
        harness.Add("Douglas-Peucker は矩形を 4 頂点へ簡略化する", SimplifyRectangleToFourCorners);
        harness.Add("透過円の自動メッシュは輪郭からはみ出さない", CircleMeshStaysInsideContour);
        harness.Add("ドーナツの自動メッシュは穴を覆わない", DonutMeshDoesNotCoverHole);
        harness.Add("完全透明画像でも矩形メッシュへフォールバックする", FullyTransparentFallsBackToRectangle);
        harness.Add(".sprite_mesh の書き出しはランタイムの検証条件を満たす", SavedMeshSatisfiesRuntimeRules);
        harness.Add(".sprite_mesh は往復（保存→読込）で形が保たれる", SpriteMeshRoundTrip);
        harness.Add("texture フィールドが相対パスで往復する", TextureHintRoundTrip);
        harness.Add("別画像をインポートしてもタブが増えて既存編集が残る", ImportingAnotherImageAddsTab);
        harness.Add("Undo / Redo が自動メッシュ生成を巻き戻せる", UndoRedoRestoresGeometry);
        harness.Add("手動ポリゴン作図と頂点編集が反映される", ManualPolygonEditing);
        harness.Add("Rust テスト用フィクスチャを書き出す", WriteGeneratedFixture);

        Console.WriteLine("スプライトリグ（Phase B1a）テスト");
        return harness.Run();
    }

    // ============================================================
    //  輪郭抽出
    // ============================================================

    /// <summary>全画素不透明の画像は、画像枠そのものの矩形輪郭になることを確認する。</summary>
    private static void OpaqueImageBecomesRectangle()
    {
        var image = SpriteImageData.CreateOpaque(32, 20);
        var options = new AutoMesh.Options { SimplifyTolerance = 1.0, InteriorSpacing = 0.0 };
        var polygons = AutoMesh.BuildContourPolygons(image, options);

        Check.Equal(1, polygons.Count, "輪郭の本数");
        Check.Equal(4, polygons[0].Points.Count, "矩形輪郭の頂点数");
        Check.Close(32.0 * 20.0, Geometry2D.SignedArea(polygons[0].Points), 1.0e-6, "矩形輪郭の面積");
    }

    /// <summary>透過円は外周 1 本（正の面積）だけになることを確認する。</summary>
    private static void CircleProducesSingleOuterContour()
    {
        var image = SyntheticImages.Circle(64, 24.0);
        var solid = image.BuildSolidMask(AutoMesh.Options.DefaultAlphaThreshold);
        var contours = ContourTracer.Trace(solid, image.Width, image.Height);

        Check.Equal(1, contours.Count, "輪郭の本数");
        Check.True(!contours[0].IsHole, "外周として判定される");
        Check.True(contours[0].SignedArea > 0.0, "外周の符号付き面積が正");
        // 面積は円の面積に近い（ピクセル境界追跡なので数 % の差は出る）
        double expected = Math.PI * 24.0 * 24.0;
        Check.True(Math.Abs(contours[0].SignedArea - expected) / expected < 0.05,
            $"円の面積に近い（期待 {expected:0.0} / 実際 {contours[0].SignedArea:0.0}）");
    }

    /// <summary>ドーナツ形状が外周 1 本と穴 1 本を返すことを確認する。</summary>
    private static void DonutProducesOuterAndHole()
    {
        var image = SyntheticImages.Donut(80, 32.0, 14.0);
        var solid = image.BuildSolidMask(AutoMesh.Options.DefaultAlphaThreshold);
        var contours = ContourTracer.Trace(solid, image.Width, image.Height);

        Check.Equal(2, contours.Count, "輪郭の本数");
        int outerCount = 0;
        int holeCount = 0;
        foreach (var contour in contours)
        {
            if (contour.IsHole) holeCount++;
            else outerCount++;
        }
        Check.Equal(1, outerCount, "外周の本数");
        Check.Equal(1, holeCount, "穴の本数");
    }

    /// <summary>離れた 2 つの円が別々の輪郭になることを確認する。</summary>
    private static void TwoIslandsProduceTwoContours()
    {
        var image = SyntheticImages.TwoIslands(120, 60, 20.0);
        var solid = image.BuildSolidMask(AutoMesh.Options.DefaultAlphaThreshold);
        var contours = ContourTracer.Trace(solid, image.Width, image.Height);

        Check.Equal(2, contours.Count, "輪郭の本数");
        foreach (var contour in contours) Check.True(!contour.IsHole, "どちらも外周として判定される");
    }

    /// <summary>階段状の矩形輪郭が 4 頂点まで簡略化されることを確認する。</summary>
    private static void SimplifyRectangleToFourCorners()
    {
        var image = SpriteImageData.CreateOpaque(50, 30);
        var solid = image.BuildSolidMask(AutoMesh.Options.DefaultAlphaThreshold);
        var contours = ContourTracer.Trace(solid, image.Width, image.Height);
        Check.Equal(1, contours.Count, "輪郭の本数");
        // 簡略化前は画像枠の全ピクセル境界頂点が並んでいる
        Check.Equal(2 * (50 + 30), contours[0].Points.Count, "簡略化前の頂点数");

        var simplified = PolylineSimplifier.SimplifyClosed(contours[0].Points, 0.5);
        Check.Equal(4, simplified.Count, "簡略化後の頂点数");
        Check.Close(50.0 * 30.0, Geometry2D.SignedArea(simplified), 1.0e-6, "簡略化後の面積");
    }

    // ============================================================
    //  三角分割の健全性
    // ============================================================

    /// <summary>透過円の自動メッシュが輪郭からはみ出さないことを確認する。</summary>
    private static void CircleMeshStaysInsideContour()
    {
        var image = SyntheticImages.Circle(96, 36.0);
        var mesh = AutoMesh.Build(image, new AutoMesh.Options
        {
            SimplifyTolerance = 1.5,
            InteriorSpacing = 12.0,
        });

        Check.True(mesh.TriangleCount > 0, "三角形が生成される");
        AssertTrianglesCoverRegionExactly(mesh);
        AssertVerticesWithinImage(mesh, image);
    }

    /// <summary>ドーナツの自動メッシュが穴を覆わないことを確認する。</summary>
    private static void DonutMeshDoesNotCoverHole()
    {
        var image = SyntheticImages.Donut(96, 40.0, 16.0);
        var mesh = AutoMesh.Build(image, new AutoMesh.Options
        {
            SimplifyTolerance = 1.5,
            InteriorSpacing = 10.0,
        });

        Check.True(mesh.TriangleCount > 0, "三角形が生成される");
        AssertTrianglesCoverRegionExactly(mesh);

        // 穴の中心は三角形に覆われていないこと
        var center = new Vec2(image.Width * 0.5, image.Height * 0.5);
        foreach (var (a, b, c) in EnumerateTriangles(mesh))
        {
            Check.True(!Geometry2D.PointInTriangle(a, b, c, center),
                "穴の中心を覆う三角形が無い");
        }
    }

    /// <summary>完全透明画像でも矩形メッシュへフォールバックすることを確認する。</summary>
    private static void FullyTransparentFallsBackToRectangle()
    {
        var image = SyntheticImages.FullyTransparent(24, 16);
        var mesh = AutoMesh.Build(image, new AutoMesh.Options { InteriorSpacing = 0.0 });

        Check.Equal(1, mesh.Polygons.Count, "輪郭の本数");
        Check.Equal(4, mesh.Vertices.Count, "矩形の頂点数");
        Check.Equal(2, mesh.TriangleCount, "矩形は 2 三角形");
        AssertTrianglesCoverRegionExactly(mesh);
    }

    // ============================================================
    //  .sprite_mesh の書き出し・読み込み
    // ============================================================

    /// <summary>書き出した JSON がランタイムのパーサの検証条件を満たすことを確認する。</summary>
    private static void SavedMeshSatisfiesRuntimeRules()
    {
        var image = SyntheticImages.Circle(64, 24.0);
        var mesh = AutoMesh.Build(image, new AutoMesh.Options { InteriorSpacing = 14.0 });

        string json = SpriteMeshFile.Serialize(mesh, image.Width, image.Height, "circle.png", "circle", "");
        var loaded = SpriteMeshFile.Deserialize(json, baseDirectory: null);

        // 頂点数・三角形数・ウェイト数の整合（ランタイムが最初に見る条件）
        Check.Equal(mesh.Vertices.Count, loaded.Mesh.Vertices.Count, "頂点数");
        Check.Equal(mesh.Triangles.Count, loaded.Mesh.Triangles.Count, "三角形インデックス数");
        Check.Equal(mesh.Vertices.Count, loaded.Mesh.Weights.Count, "ウェイト数");
        Check.Equal(0, loaded.Mesh.Triangles.Count % 3, "三角形インデックスは 3 の倍数");
        Check.True(loaded.Mesh.Bones.Count >= 1, "ボーンが 1 本以上");

        foreach (int index in loaded.Mesh.Triangles)
            Check.True(index >= 0 && index < loaded.Mesh.Vertices.Count, "三角形インデックスが範囲内");

        foreach (var influences in loaded.Mesh.Weights)
        {
            Check.True(influences.Count >= 1 && influences.Count <= SpriteMeshFile.MaxBoneInfluences,
                "1 頂点の影響は 1〜4 本");
            double sum = 0.0;
            foreach (var influence in influences)
            {
                Check.True(influence.BoneIndex >= 0 && influence.BoneIndex < loaded.Mesh.Bones.Count,
                    "ボーン添字が範囲内");
                Check.True(influence.Weight >= 0.0, "ウェイトが非負");
                sum += influence.Weight;
            }
            Check.Close(1.0, sum, 1.0e-6, "ウェイト合計");
        }

        // UV は [0,1]^2 に収まる
        foreach (var v in loaded.Mesh.Vertices)
        {
            Check.True(v.X >= 0.0 && v.X <= image.Width, "頂点 X が画像内");
            Check.True(v.Y >= 0.0 && v.Y <= image.Height, "頂点 Y が画像内");
        }
    }

    /// <summary>保存→読込で頂点・三角形が保たれ、輪郭が復元されることを確認する。</summary>
    private static void SpriteMeshRoundTrip()
    {
        var image = SyntheticImages.Donut(72, 28.0, 12.0);
        var mesh = AutoMesh.Build(image, new AutoMesh.Options { InteriorSpacing = 10.0 });

        string json = SpriteMeshFile.Serialize(mesh, image.Width, image.Height, null, "donut", "");
        var loaded = SpriteMeshFile.Deserialize(json, baseDirectory: null);

        Check.Equal(mesh.Vertices.Count, loaded.Mesh.Vertices.Count, "頂点数");
        Check.Equal(mesh.TriangleCount, loaded.Mesh.TriangleCount, "三角形数");
        // 境界ループから輪郭が復元される（ドーナツなので外周 1 + 穴 1）
        Check.Equal(2, loaded.Mesh.Polygons.Count, "復元された輪郭の本数");
    }

    /// <summary>texture フィールドが相対パスとして往復することを確認する。</summary>
    private static void TextureHintRoundTrip()
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(),
            "seed_sprite_rig_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string imagePath = Path.Combine(temporaryDirectory, "hero.png");
            File.WriteAllBytes(imagePath, Array.Empty<byte>());   // 実体は不要（存在確認のみ）
            string meshPath = Path.Combine(temporaryDirectory, "hero.sprite_mesh");

            var image = SyntheticImages.Circle(32, 12.0);
            var mesh = AutoMesh.Build(image, new AutoMesh.Options { InteriorSpacing = 0.0 });
            SpriteMeshFile.Save(meshPath, mesh, image.Width, image.Height, imagePath);

            string json = File.ReadAllText(meshPath);
            Check.True(json.Contains("\"texture\": \"hero.png\""),
                $"texture が相対パスで書かれる（実際の JSON: {Head(json, 400)}）");

            var loaded = SpriteMeshFile.Load(meshPath);
            Check.True(loaded.TextureHint != null, "texture ヒントが解決される");
            Check.Equal(Path.GetFullPath(imagePath), Path.GetFullPath(loaded.TextureHint!), "解決された画像パス");
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    // ============================================================
    //  タブ管理・編集操作
    // ============================================================

    /// <summary>
    /// 編集中に別画像をインポートしても、タブが増えるだけで
    /// 既存ドキュメントの編集内容が保持されることを確認する。
    /// </summary>
    private static void ImportingAnotherImageAddsTab()
    {
        var set = new SpriteRigDocumentSet();

        var firstImage = SyntheticImages.Circle(48, 18.0);
        var first = new SpriteRigDocument(@"C:\assets\first.png", firstImage);
        set.AddOrActivate(first);
        first.ApplyAutoMesh();
        int firstTriangles = first.Mesh.TriangleCount;
        Check.True(firstTriangles > 0, "1 枚目にメッシュが作られる");

        // 2 枚目をインポート
        var secondImage = SyntheticImages.Donut(48, 20.0, 8.0);
        var second = new SpriteRigDocument(@"C:\assets\second.png", secondImage);
        var activated = set.AddOrActivate(second);

        Check.Equal(2, set.Count, "タブ数");
        Check.True(ReferenceEquals(activated, second), "2 枚目がアクティブになる");
        Check.True(ReferenceEquals(set.Documents[0], first), "1 枚目のタブが残っている");
        Check.Equal(firstTriangles, first.Mesh.TriangleCount, "1 枚目の編集内容が保たれる");
        Check.True(set.HasUnsavedChanges, "未保存の変更が検出される");

        // 同じ画像をもう一度インポートしてもタブは増えない
        var duplicate = new SpriteRigDocument(@"C:\assets\first.png", firstImage);
        var reused = set.AddOrActivate(duplicate);
        Check.Equal(2, set.Count, "同じ画像ではタブが増えない");
        Check.True(ReferenceEquals(reused, first), "既存タブが再利用される");

        // 閉じると隣のタブがアクティブになる
        set.Close(first);
        Check.Equal(1, set.Count, "閉じた後のタブ数");
        Check.True(ReferenceEquals(set.Active, second), "残ったタブがアクティブ");
    }

    /// <summary>Undo / Redo が自動メッシュ生成を巻き戻せることを確認する。</summary>
    private static void UndoRedoRestoresGeometry()
    {
        var image = SyntheticImages.Circle(48, 18.0);
        var document = new SpriteRigDocument(@"C:\assets\undo.png", image);

        Check.Equal(0, document.Mesh.TriangleCount, "初期状態は三角形なし");
        document.ApplyAutoMesh();
        int generated = document.Mesh.TriangleCount;
        Check.True(generated > 0, "自動メッシュで三角形ができる");

        Check.True(document.Undo(), "Undo できる");
        Check.Equal(0, document.Mesh.TriangleCount, "Undo で元に戻る");

        Check.True(document.Redo(), "Redo できる");
        Check.Equal(generated, document.Mesh.TriangleCount, "Redo でやり直せる");
    }

    /// <summary>手動でのポリゴン作図・頂点追加・移動・削除が反映されることを確認する。</summary>
    private static void ManualPolygonEditing()
    {
        var image = SpriteImageData.CreateOpaque(100, 100);
        var document = new SpriteRigDocument(@"C:\assets\manual.png", image);

        // 三角形 1 枚ぶんのポリゴンを描いて確定する
        document.AddPendingPolygonPoint(new Vec2(10.0, 10.0));
        document.AddPendingPolygonPoint(new Vec2(90.0, 10.0));
        document.AddPendingPolygonPoint(new Vec2(90.0, 90.0));
        Check.True(document.CommitPendingPolygon(), "ポリゴンを確定できる");
        Check.Equal(1, document.Mesh.Polygons.Count, "輪郭が 1 本");
        Check.Equal(1, document.Mesh.TriangleCount, "三角形 1 枚");

        // 内部点を足すと三角形が増える
        Check.True(document.AddVertexAt(new Vec2(70.0, 40.0)), "内部点を追加できる");
        Check.Equal(1, document.Mesh.InteriorPoints.Count, "内部点の数");
        Check.Equal(3, document.Mesh.TriangleCount, "内部点で 3 分割される");

        // 辺の上をクリックすると輪郭が分割される
        Check.True(document.AddVertexAt(new Vec2(50.0, 10.0)), "辺を分割できる");
        Check.Equal(4, document.Mesh.Polygons[0].Points.Count, "輪郭の頂点が増える");

        // 頂点を掴んで動かす
        var handle = document.HitTestPoint(new Vec2(50.0, 10.0));
        Check.True(handle != null, "追加した頂点をヒットできる");
        Check.True(document.BeginPointDrag(handle!.Value), "ドラッグ開始できる");
        document.UpdatePointDrag(new Vec2(50.0, 30.0));
        document.EndPointDrag();
        Check.Close(30.0, document.GetPointPosition(handle.Value).Y, 1.0e-9, "移動後の Y");

        // 内部点を消すと三角形が減る
        var interior = document.HitTestPoint(new Vec2(70.0, 40.0));
        Check.True(interior != null && interior.Value.IsInterior, "内部点をヒットできる");
        Check.True(document.DeletePoint(interior!.Value), "内部点を削除できる");
        Check.Equal(0, document.Mesh.InteriorPoints.Count, "内部点が消える");
    }

    // ============================================================
    //  Rust 側テスト用フィクスチャの書き出し
    // ============================================================

    /// <summary>
    /// 自動メッシュ生成の結果を <c>runtime/tests/fixtures/</c> へ書き出す。
    /// このファイルは Rust の <c>sprite_mesh.rs</c> のテストが読み込んで
    /// 「エディタ生成のメッシュをランタイムのパーサが受理する」ことを確認する。
    /// </summary>
    private static void WriteGeneratedFixture()
    {
        string root = FindRepositoryRoot();
        string fixturePath = Path.Combine(root, GeneratedFixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));

        var image = SyntheticImages.Circle(64, 24.0);
        var mesh = AutoMesh.Build(image, new AutoMesh.Options
        {
            SimplifyTolerance = 1.5,
            InteriorSpacing = 12.0,
        });
        AssertTrianglesCoverRegionExactly(mesh);

        string json = SpriteMeshFile.Serialize(
            mesh, image.Width, image.Height,
            relativeTexturePath: "generated_circle.png",
            name: "generated_circle",
            comment: "editor/tests/SpriteRigTests が自動生成（手で編集しない）");

        Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
        File.WriteAllText(fixturePath, json);
        Console.WriteLine($"         フィクスチャを書き出しました: {fixturePath}");
        Console.WriteLine($"         頂点 {mesh.Vertices.Count} / 三角形 {mesh.TriangleCount}");
    }

    /// <summary>
    /// 実行ファイルの位置からリポジトリルート（runtime/ と editor/ を持つ階層）を探す。
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "runtime")) &&
                Directory.Exists(Path.Combine(directory.FullName, "editor")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new AssertionException("リポジトリルート（runtime/ と editor/ を含む階層）が見つかりません");
    }

    // ============================================================
    //  共通の検証ヘルパー
    // ============================================================

    /// <summary>
    /// 「三角形が輪郭内に収まり、隙間も重なりも無い」ことを面積の一致で検証する。
    ///
    /// 全三角形の面積合計 = 外周の面積 - 穴の面積 が成り立つのは、
    /// 三角形が領域の外へ出ておらず、かつ互いに重なっていない場合だけである
    /// （はみ出せば合計が増え、重なれば合計が増え、隙間があれば減る）。
    /// あわせて各三角形の重心が領域内にあることも確認する。
    /// </summary>
    /// <param name="mesh">検証するメッシュ。</param>
    private static void AssertTrianglesCoverRegionExactly(SpriteRigMesh mesh)
    {
        double regionArea = 0.0;
        foreach (var polygon in mesh.Polygons)
        {
            if (polygon.Points.Count < SpriteRigMesh.MinPolygonVertices) continue;
            double area = Math.Abs(Geometry2D.SignedArea(polygon.Points));
            regionArea += polygon.IsHole ? -area : area;
        }

        double triangleArea = 0.0;
        foreach (var (a, b, c) in EnumerateTriangles(mesh))
        {
            double signed = Geometry2D.Cross3(a, b, c) * 0.5;
            Check.True(signed > 0.0, "三角形の向きが外周と揃っている（正の面積）");
            triangleArea += signed;

            var centroid = (a + b + c) / 3.0;
            Check.True(IsInsideRegion(mesh, centroid), $"三角形の重心 {centroid} が領域内にある");
        }

        double tolerance = Math.Max(regionArea * AreaRelativeTolerance, 1.0e-6);
        Check.Close(regionArea, triangleArea, tolerance, "三角形の面積合計 = 領域面積");
    }

    /// <summary>全頂点が画像の範囲内にあることを確認する。</summary>
    /// <param name="mesh">検証するメッシュ。</param>
    /// <param name="image">対象画像。</param>
    private static void AssertVerticesWithinImage(SpriteRigMesh mesh, SpriteImageData image)
    {
        foreach (var v in mesh.Vertices)
        {
            Check.True(v.X >= 0.0 && v.X <= image.Width && v.Y >= 0.0 && v.Y <= image.Height,
                $"頂点 {v} が画像 {image.Width}x{image.Height} の内側");
        }
    }

    /// <summary>入れ子の偶奇で「領域の内側か」を判定する（穴の中は外）。</summary>
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

    /// <summary>三角形を (頂点 A, 頂点 B, 頂点 C) の組として列挙する。</summary>
    private static IEnumerable<(Vec2 A, Vec2 B, Vec2 C)> EnumerateTriangles(SpriteRigMesh mesh)
    {
        for (int t = 0; t + Triangulation.IndicesPerTriangle <= mesh.Triangles.Count;
             t += Triangulation.IndicesPerTriangle)
        {
            yield return (
                mesh.Vertices[mesh.Triangles[t]],
                mesh.Vertices[mesh.Triangles[t + 1]],
                mesh.Vertices[mesh.Triangles[t + 2]]);
        }
    }

    /// <summary>文字列の先頭 N 文字を返す（エラーメッセージ用）。</summary>
    private static string Head(string text, int length)
        => text.Length <= length ? text : text.Substring(0, length) + "…";
}
