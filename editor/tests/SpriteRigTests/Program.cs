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

    /// <summary>Rust 側テストへ渡す、多ボーン + 自動ウェイトのフィクスチャ相対パス。</summary>
    private const string RiggedFixtureRelativePath =
        "runtime/tests/fixtures/generated_rigged.sprite_mesh";

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
        harness.Add("ボーンの 根元/先端 表現と TRS が 3 階層で往復する", BoneHeadTipTrsRoundTrip);
        harness.Add("親の付け替えでワールド姿勢が変わらない", ReparentPreservesWorldPose);
        harness.Add("先端ドラッグで先端に生えた子が追従する", TipDragCarriesAttachedChild);
        harness.Add("2 ボーンの棒で中間頂点が両方から約 50% を受ける", AutoWeightSplitsEvenlyAtMidpoint);
        harness.Add("自動ウェイトは最大 4 本・合計 1.0 を守る", AutoWeightRespectsInfluenceLimit);
        harness.Add("5 本目を塗ると最弱の 1 本が追い出される", PaintEvictsWeakestInfluence);
        harness.Add("ブラシは半径外の頂点を書き換えない", BrushLeavesVerticesOutsideRadius);
        harness.Add("メッシュ再生成でウェイトが座標で引き継がれる", WeightsSurviveRetriangulation);
        harness.Add("ボーン削除で影響が除かれ再正規化される", DeletingBoneRenormalizesWeights);
        harness.Add("bones / weights を含む .sprite_mesh が往復する", RiggedMeshRoundTrip);
        harness.Add("Rust テスト用フィクスチャを書き出す", WriteGeneratedFixture);
        harness.Add("Rust テスト用の多ボーンフィクスチャを書き出す", WriteRiggedFixture);

        Console.WriteLine("スプライトリグ（Phase B1a / B1b）テスト");
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
    //  Phase B1b: ボーンの編集表現 ⇔ TRS
    // ============================================================

    /// <summary>
    /// 「根元 + 先端」で指定したボーン 3 階層が、親ローカル TRS へ変換されても
    /// まったく同じ位置へ復元されることを確認する（B1b の変換規約の要）。
    /// </summary>
    private static void BoneHeadTipTrsRoundTrip()
    {
        var bones = new List<SpriteRigBone>();

        // 3 階層の鎖（それぞれ向きが違うので、回転の合成がずれていれば検出できる）
        var heads = new[] { new Vec2(10.0, 20.0), new Vec2(60.0, 20.0), new Vec2(100.0, 60.0) };
        var tips = new[] { new Vec2(60.0, 20.0), new Vec2(100.0, 60.0), new Vec2(140.0, 40.0) };

        for (int i = 0; i < heads.Length; i++)
            SpriteRigSkeleton.AddBone(bones, i - 1, heads[i], tips[i]);

        Check.Equal(3, bones.Count, "ボーン数");
        Check.Equal(string.Empty, bones[0].Parent, "先頭ボーンはルート");
        Check.Equal(bones[0].Name, bones[1].Parent, "2 本目の親");
        Check.Equal(bones[1].Name, bones[2].Parent, "3 本目の親");

        var globals = SpriteRigSkeleton.ComputeGlobals(bones);
        for (int i = 0; i < heads.Length; i++)
        {
            AssertVec2Close(heads[i], SpriteRigSkeleton.HeadOf(globals, i), 1.0e-9, $"ボーン {i} の根元");
            AssertVec2Close(tips[i], SpriteRigSkeleton.TipOf(bones, globals, i), 1.0e-9, $"ボーン {i} の先端");
            Check.Close(Vec2.Distance(heads[i], tips[i]), bones[i].Length, 1.0e-9, $"ボーン {i} の長さ");
        }

        // 末端ボーンの長さは子から復元できないので、length フィールドが唯一の情報源になる
        Check.True(bones[2].Length > 0.0, "末端ボーンにも長さが入る");
    }

    /// <summary>親を付け替えてもワールド上の根元・先端が動かないことを確認する。</summary>
    private static void ReparentPreservesWorldPose()
    {
        var bones = new List<SpriteRigBone>();
        SpriteRigSkeleton.AddBone(bones, -1, new Vec2(0.0, 0.0), new Vec2(50.0, 0.0));
        SpriteRigSkeleton.AddBone(bones, 0, new Vec2(50.0, 0.0), new Vec2(50.0, 40.0));
        SpriteRigSkeleton.AddBone(bones, 1, new Vec2(50.0, 40.0), new Vec2(90.0, 70.0));

        var before = SpriteRigSkeleton.ComputeGlobals(bones);
        Vec2 head = SpriteRigSkeleton.HeadOf(before, 2);
        Vec2 tip = SpriteRigSkeleton.TipOf(bones, before, 2);

        Check.True(SpriteRigSkeleton.Reparent(bones, 2, 0), "ルート直下の子へ付け替えられる");

        var after = SpriteRigSkeleton.ComputeGlobals(bones);
        AssertVec2Close(head, SpriteRigSkeleton.HeadOf(after, 2), 1.0e-9, "付け替え後の根元");
        AssertVec2Close(tip, SpriteRigSkeleton.TipOf(bones, after, 2), 1.0e-9, "付け替え後の先端");

        // 自分の子孫を親にしようとすると循環するので拒否される
        Check.True(!SpriteRigSkeleton.Reparent(bones, 0, 1), "子孫を親にはできない");
    }

    /// <summary>先端を動かしたとき、先端に生えている子ボーンが追従することを確認する。</summary>
    private static void TipDragCarriesAttachedChild()
    {
        var bones = new List<SpriteRigBone>();
        SpriteRigSkeleton.AddBone(bones, -1, new Vec2(0.0, 0.0), new Vec2(50.0, 0.0));
        SpriteRigSkeleton.AddBone(bones, 0, new Vec2(50.0, 0.0), new Vec2(90.0, 0.0));

        SpriteRigSkeleton.MoveTip(bones, 0, new Vec2(70.0, 0.0));

        var globals = SpriteRigSkeleton.ComputeGlobals(bones);
        AssertVec2Close(new Vec2(0.0, 0.0), SpriteRigSkeleton.HeadOf(globals, 0), 1.0e-9, "親の根元は動かない");
        AssertVec2Close(new Vec2(70.0, 0.0), SpriteRigSkeleton.TipOf(bones, globals, 0), 1.0e-9, "親の先端");
        AssertVec2Close(new Vec2(70.0, 0.0), SpriteRigSkeleton.HeadOf(globals, 1), 1.0e-9, "子の根元が先端へ追従する");
    }

    // ============================================================
    //  Phase B1b: 自動ウェイト
    // ============================================================

    /// <summary>
    /// 2 本のボーンが一直線に並んだ「棒」で、継ぎ目の頂点が両方から
    /// ほぼ半々の影響を受けることを確認する（距離カーネルの基本性質）。
    /// </summary>
    private static void AutoWeightSplitsEvenlyAtMidpoint()
    {
        var mesh = BuildBarMesh(out int midpointVertex);

        int applied = AutoWeights.Apply(mesh, new AutoWeights.Options());
        Check.Equal(mesh.Vertices.Count, applied, "適用された頂点数");

        double toFirst = WeightPaint.GetWeight(mesh.Weights[midpointVertex], 0);
        double toSecond = WeightPaint.GetWeight(mesh.Weights[midpointVertex], 1);
        Check.Close(0.5, toFirst, 1.0e-6, "継ぎ目から 1 本目への影響");
        Check.Close(0.5, toSecond, 1.0e-6, "継ぎ目から 2 本目への影響");

        // 端の頂点は、乗っているボーンが支配的になる
        Check.True(WeightPaint.GetWeight(mesh.Weights[0], 0) > 0.9, "1 本目の根元は 1 本目が支配的");
        Check.True(WeightPaint.GetWeight(mesh.Weights[2], 1) > 0.9, "2 本目の先端は 2 本目が支配的");
    }

    /// <summary>ボーンが 5 本以上あっても、影響は 4 本以内・合計 1.0 に収まることを確認する。</summary>
    private static void AutoWeightRespectsInfluenceLimit()
    {
        var mesh = BuildBarMesh(out _);
        // 似た距離のボーンを増やして、上限の切り詰めが効くかを見る
        for (int i = 0; i < 4; i++)
        {
            SpriteRigSkeleton.AddBone(mesh.Bones, 0,
                new Vec2(20.0 + i * 30.0, 5.0 + i), new Vec2(60.0 + i * 30.0, 5.0 + i));
        }
        Check.Equal(6, mesh.Bones.Count, "ボーン数");

        AutoWeights.Apply(mesh, new AutoWeights.Options());
        foreach (var influences in mesh.Weights) AssertInfluencesValid(influences, mesh.Bones.Count);
    }

    // ============================================================
    //  Phase B1b: ウェイトペイント
    // ============================================================

    /// <summary>
    /// 既に 4 本の影響を持つ頂点へ 5 本目を塗ると、
    /// <b>塗ったボーン以外で最も弱い 1 本</b>が追い出されることを確認する。
    /// </summary>
    private static void PaintEvictsWeakestInfluence()
    {
        var influences = new List<SpriteRigInfluence>
        {
            new(0, 0.4), new(1, 0.3), new(2, 0.2), new(3, 0.1),
        };

        var painted = WeightPaint.SetBoneWeight(influences, boneIndex: 4, target: 0.5);

        Check.Equal(WeightPaint.MaxInfluences, painted.Count, "影響本数は上限のまま");
        Check.Close(0.5, WeightPaint.GetWeight(painted, 4), 1.0e-9, "塗ったボーンのウェイト");
        Check.Close(0.0, WeightPaint.GetWeight(painted, 3), 1.0e-9, "最弱だったボーン 3 が追い出される");
        // 残った 3 本は元の比率のまま 0.5 を按分する（0.4 : 0.3 : 0.2 → 0.9 で割って 0.5 倍）
        Check.Close(0.5 * 0.4 / 0.9, WeightPaint.GetWeight(painted, 0), 1.0e-9, "ボーン 0 の按分後ウェイト");
        AssertInfluencesValid(painted, boneCount: 5);

        // ウェイトを 0 まで下げると、その影響自体が消えて残りが正規化される
        var erased = WeightPaint.SetBoneWeight(painted, boneIndex: 4, target: 0.0);
        Check.Close(0.0, WeightPaint.GetWeight(erased, 4), 1.0e-9, "0 にした影響は消える");
        AssertInfluencesValid(erased, boneCount: 5);
    }

    /// <summary>ブラシが半径の外の頂点へ影響しないことを確認する。</summary>
    private static void BrushLeavesVerticesOutsideRadius()
    {
        var mesh = BuildBarMesh(out _);
        AutoWeights.Apply(mesh, new AutoWeights.Options());

        double before = WeightPaint.GetWeight(mesh.Weights[2], 0);
        var brush = new WeightPaint.BrushOptions { Radius = 10.0, Strength = 1.0, Mode = WeightBrushMode.Add };

        // 1 本目の根元 (0,0) を中心に塗る。頂点 2 は (200,0) なので半径外
        Check.True(WeightPaint.ApplyBrush(mesh.Vertices, mesh.Weights, mesh.Triangles, 0, Vec2.Zero, brush),
            "半径内の頂点が塗られる");
        Check.Close(before, WeightPaint.GetWeight(mesh.Weights[2], 0), 1.0e-12, "半径外の頂点は変わらない");
        foreach (var influences in mesh.Weights) AssertInfluencesValid(influences, mesh.Bones.Count);
    }

    // ============================================================
    //  Phase B1b: ウェイトの引き継ぎ・ボーン削除
    // ============================================================

    /// <summary>
    /// 三角分割をやり直しても、既存頂点のウェイトが座標を手がかりに引き継がれることを確認する。
    /// （B1a では毎回ルート 1.0 へ張り直されていた箇所）
    /// </summary>
    private static void WeightsSurviveRetriangulation()
    {
        var image = SpriteImageData.CreateOpaque(200, 100);
        var document = new SpriteRigDocument(@"C:\assets\bar.png", image);

        document.AddPendingPolygonPoint(new Vec2(10.0, 10.0));
        document.AddPendingPolygonPoint(new Vec2(190.0, 10.0));
        document.AddPendingPolygonPoint(new Vec2(190.0, 90.0));
        document.AddPendingPolygonPoint(new Vec2(10.0, 90.0));
        Check.True(document.CommitPendingPolygon(), "輪郭を確定できる");

        // 2 本のボーンを置いて自動ウェイトを掛ける
        SpriteRigSkeleton.AddBone(document.Mesh.Bones, 0, new Vec2(10.0, 50.0), new Vec2(100.0, 50.0));
        SpriteRigSkeleton.AddBone(document.Mesh.Bones, 1, new Vec2(100.0, 50.0), new Vec2(190.0, 50.0));
        Check.True(document.ApplyAutoWeights() > 0, "自動ウェイトが適用される");

        var probe = new Vec2(10.0, 10.0);
        int beforeIndex = document.HitTestVertex(probe);
        Check.True(beforeIndex >= 0, "輪郭頂点が派生頂点として存在する");
        double beforeWeight = document.GetInfluenceWeight(beforeIndex, 1);
        Check.True(beforeWeight > 0.0, "引き継ぎを検出できるだけのウェイトがある");

        // 内部点を足すと三角分割がやり直される
        Check.True(document.AddVertexAt(new Vec2(100.0, 70.0)), "内部点を追加できる");

        int afterIndex = document.HitTestVertex(probe);
        Check.True(afterIndex >= 0, "再構築後も同じ座標に頂点がある");
        Check.Close(beforeWeight, document.GetInfluenceWeight(afterIndex, 1), 1.0e-12,
            "再三角分割後もウェイトが保たれる");

        foreach (var influences in document.Mesh.Weights)
            AssertInfluencesValid(influences, document.Mesh.Bones.Count);
    }

    /// <summary>ボーンを削除すると、その影響が消えて残りが再正規化されることを確認する。</summary>
    private static void DeletingBoneRenormalizesWeights()
    {
        var image = SpriteImageData.CreateOpaque(100, 100);
        var document = new SpriteRigDocument(@"C:\assets\del.png", image);

        document.AddPendingPolygonPoint(new Vec2(10.0, 10.0));
        document.AddPendingPolygonPoint(new Vec2(90.0, 10.0));
        document.AddPendingPolygonPoint(new Vec2(90.0, 90.0));
        Check.True(document.CommitPendingPolygon(), "輪郭を確定できる");

        SpriteRigSkeleton.AddBone(document.Mesh.Bones, 0, new Vec2(10.0, 10.0), new Vec2(50.0, 10.0));
        SpriteRigSkeleton.AddBone(document.Mesh.Bones, 1, new Vec2(50.0, 10.0), new Vec2(90.0, 10.0));
        Check.Equal(3, document.Mesh.Bones.Count, "ボーン数");

        // 検証しやすいよう、先頭頂点のウェイトを手で作る
        document.Mesh.Weights[0] = WeightPaint.Normalize(new List<SpriteRigInfluence>
        {
            new(0, 0.5), new(1, 0.3), new(2, 0.2),
        });

        Check.True(document.DeleteBone(1), "中間のボーンを削除できる");
        Check.Equal(2, document.Mesh.Bones.Count, "削除後のボーン数");

        var influences = document.Mesh.Weights[0];
        AssertInfluencesValid(influences, document.Mesh.Bones.Count);
        // 消えたボーン 1 の 0.3 が抜け、0.5 : 0.2 を 0.7 で割った比率になる
        Check.Close(0.5 / 0.7, WeightPaint.GetWeight(influences, 0), 1.0e-9, "ボーン 0 の再正規化後ウェイト");
        Check.Close(0.2 / 0.7, WeightPaint.GetWeight(influences, 1), 1.0e-9, "旧ボーン 2 が添字 1 へ詰められる");

        // 最後の 1 本は消せない
        Check.True(document.DeleteBone(1), "2 本目も消せる");
        Check.True(!document.DeleteBone(0), "最後の 1 本は消せない");
    }

    /// <summary>ボーン階層・長さ・ウェイトを含む .sprite_mesh が往復することを確認する。</summary>
    private static void RiggedMeshRoundTrip()
    {
        var mesh = BuildBarMesh(out _);
        AutoWeights.Apply(mesh, new AutoWeights.Options());

        string json = SpriteMeshFile.Serialize(mesh, 200, 40, null, "bar", "");
        var loaded = SpriteMeshFile.Deserialize(json, baseDirectory: null).Mesh;

        Check.Equal(mesh.Bones.Count, loaded.Bones.Count, "ボーン数");
        for (int i = 0; i < mesh.Bones.Count; i++)
        {
            Check.Equal(mesh.Bones[i].Name, loaded.Bones[i].Name, $"ボーン {i} の名前");
            Check.Equal(mesh.Bones[i].Parent, loaded.Bones[i].Parent, $"ボーン {i} の親");
            Check.Close(mesh.Bones[i].Rotation, loaded.Bones[i].Rotation, 1.0e-9, $"ボーン {i} の回転");
            Check.Close(mesh.Bones[i].Length, loaded.Bones[i].Length, 1.0e-9, $"ボーン {i} の長さ");
            AssertVec2Close(mesh.Bones[i].Position, loaded.Bones[i].Position, 1.0e-9, $"ボーン {i} の位置");
        }

        // 根元・先端が往復後も一致する（＝編集表現が完全に復元される）
        var before = SpriteRigSkeleton.ComputeGlobals(mesh.Bones);
        var after = SpriteRigSkeleton.ComputeGlobals(loaded.Bones);
        for (int i = 0; i < mesh.Bones.Count; i++)
        {
            AssertVec2Close(SpriteRigSkeleton.HeadOf(before, i), SpriteRigSkeleton.HeadOf(after, i),
                1.0e-9, $"ボーン {i} の根元");
            AssertVec2Close(SpriteRigSkeleton.TipOf(mesh.Bones, before, i),
                SpriteRigSkeleton.TipOf(loaded.Bones, after, i), 1.0e-9, $"ボーン {i} の先端");
        }

        Check.Equal(mesh.Weights.Count, loaded.Weights.Count, "ウェイト数");
        for (int v = 0; v < mesh.Weights.Count; v++)
        {
            AssertInfluencesValid(loaded.Weights[v], loaded.Bones.Count);
            Check.Close(WeightPaint.GetWeight(mesh.Weights[v], 0),
                WeightPaint.GetWeight(loaded.Weights[v], 0), 1.0e-6, $"頂点 {v} のボーン 0 ウェイト");
        }

        // 旧ファイル（length 無し）も読める＝後方互換
        string legacy = json.Replace("\"length\":", "\"legacy_ignored\":");
        var legacyMesh = SpriteMeshFile.Deserialize(legacy, baseDirectory: null).Mesh;
        Check.Equal(mesh.Bones.Count, legacyMesh.Bones.Count, "length 無しでも読める");
        Check.Close(0.0, legacyMesh.Bones[0].Length, 1.0e-12, "length 省略時は 0");
    }

    // ============================================================
    //  Phase B1b のテスト用ヘルパー
    // ============================================================

    /// <summary>
    /// 「2 本のボーンが一直線に並んだ棒」を手で組み立てる。
    ///
    /// 自動ウェイトの性質だけを見たいので、輪郭ポリゴンは持たせず
    /// （＝輪郭またぎのペナルティが働かない）頂点と三角形だけを直接置く。
    /// </summary>
    /// <param name="midpointVertex">2 本のボーンの継ぎ目にある頂点の添字。</param>
    private static SpriteRigMesh BuildBarMesh(out int midpointVertex)
    {
        var mesh = new SpriteRigMesh();
        mesh.Vertices.Add(new Vec2(0.0, 0.0));
        mesh.Vertices.Add(new Vec2(100.0, 0.0));
        mesh.Vertices.Add(new Vec2(200.0, 0.0));
        mesh.Vertices.Add(new Vec2(100.0, 20.0));
        mesh.Triangles.AddRange(new[] { 0, 1, 3, 1, 2, 3 });

        mesh.Bones.Clear();
        SpriteRigSkeleton.AddBone(mesh.Bones, -1, new Vec2(0.0, 0.0), new Vec2(100.0, 0.0));
        SpriteRigSkeleton.AddBone(mesh.Bones, 0, new Vec2(100.0, 0.0), new Vec2(200.0, 0.0));

        midpointVertex = 1;
        return mesh;
    }

    /// <summary>影響一覧がランタイムの受理条件（1〜4 本・範囲内・合計 1.0）を満たすことを表明する。</summary>
    /// <param name="influences">検査する影響一覧。</param>
    /// <param name="boneCount">ボーン総数。</param>
    private static void AssertInfluencesValid(IReadOnlyList<SpriteRigInfluence> influences, int boneCount)
    {
        Check.True(influences.Count >= 1 && influences.Count <= WeightPaint.MaxInfluences,
            $"影響は 1〜{WeightPaint.MaxInfluences} 本（実際 {influences.Count} 本）");

        var seen = new HashSet<int>();
        double sum = 0.0;
        foreach (var influence in influences)
        {
            Check.True(influence.BoneIndex >= 0 && influence.BoneIndex < boneCount, "ボーン添字が範囲内");
            Check.True(seen.Add(influence.BoneIndex), "同じボーンが重複しない");
            Check.True(influence.Weight > 0.0 && double.IsFinite(influence.Weight), "ウェイトが正の有限値");
            sum += influence.Weight;
        }
        Check.Close(1.0, sum, 1.0e-9, "ウェイト合計");
    }

    /// <summary>2 点がほぼ一致することを表明する。</summary>
    /// <param name="expected">期待する点。</param>
    /// <param name="actual">実際の点。</param>
    /// <param name="tolerance">許容誤差。</param>
    /// <param name="what">対象の説明。</param>
    private static void AssertVec2Close(Vec2 expected, Vec2 actual, double tolerance, string what)
    {
        Check.Close(expected.X, actual.X, tolerance, what + " (X)");
        Check.Close(expected.Y, actual.Y, tolerance, what + " (Y)");
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
    /// ボーン階層とウェイトを持つメッシュを <c>runtime/tests/fixtures/</c> へ書き出す。
    /// Rust 側の <c>editor_generated_rigged_mesh_is_accepted</c> がこれを読み、
    /// 「B1b が吐く多ボーン + 自動ウェイトの JSON をランタイムがそのまま受理する」ことを確認する。
    /// </summary>
    private static void WriteRiggedFixture()
    {
        string root = FindRepositoryRoot();
        string fixturePath = Path.Combine(root,
            RiggedFixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));

        var image = SyntheticImages.Circle(96, 36.0);
        var mesh = AutoMesh.Build(image, new AutoMesh.Options
        {
            SimplifyTolerance = 1.5,
            InteriorSpacing = 14.0,
        });

        // 画像を横断する 3 本の鎖を置き、距離ベースの自動ウェイトを掛ける
        mesh.Bones.Clear();
        SpriteRigSkeleton.AddBone(mesh.Bones, -1, new Vec2(16.0, 48.0), new Vec2(48.0, 48.0), "root");
        SpriteRigSkeleton.AddBone(mesh.Bones, 0, new Vec2(48.0, 48.0), new Vec2(72.0, 48.0), "mid");
        SpriteRigSkeleton.AddBone(mesh.Bones, 1, new Vec2(72.0, 48.0), new Vec2(88.0, 48.0), "tip");
        int applied = AutoWeights.Apply(mesh, new AutoWeights.Options());
        Check.Equal(mesh.Vertices.Count, applied, "全頂点へ自動ウェイトが適用される");

        foreach (var influences in mesh.Weights) AssertInfluencesValid(influences, mesh.Bones.Count);

        string json = SpriteMeshFile.Serialize(
            mesh, image.Width, image.Height,
            relativeTexturePath: "generated_rigged.png",
            name: "generated_rigged",
            comment: "editor/tests/SpriteRigTests が自動生成（手で編集しない）");

        Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
        File.WriteAllText(fixturePath, json);
        Console.WriteLine($"         フィクスチャを書き出しました: {fixturePath}");
        Console.WriteLine($"         頂点 {mesh.Vertices.Count} / 三角形 {mesh.TriangleCount} / ボーン {mesh.Bones.Count}");
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
