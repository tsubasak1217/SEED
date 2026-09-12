using System;
using System.IO;
using ProjectSystemTests;   // TempDir（一時フォルダは ProjectSystemTests のものを共用）
using SEEDEditor.Assets;
using SpriteRigTests;       // TestHarness / Check（テストランナーも共用）

namespace ProjectPanelLogicTests;

/// <summary>
/// プロジェクトパネルの純ロジック単体テスト（docs/editor_project_panel.md）。
///
/// 検証の柱:
///   1. assets:// 仮想パスへの相対化（<see cref="AssetUriPath"/>）
///      ＝ 右クリック「パスをコピー」が貼り付け可能な文字列を作れること
///   2. 拡張子 -> プレビュー種別の対応表（<see cref="AssetPreviewKinds"/>）
///   3. プレビューのキャッシュキー（<see cref="AssetPreviewCacheKey"/>）
///      ＝ ファイルを差し替えたら古いプレビューを使い回さないこと
///   4. ピクセル寸法の表示書式（<see cref="ImagePixelSize"/>）
/// </summary>
public static class Program
{
    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();

        // ── assets:// 相対化 ────────────────────────────────
        harness.Add("直下のファイルを assets:// へ変換できる",            AssetUriDirectChild);
        harness.Add("入れ子のファイルはスラッシュ区切りになる",            AssetUriNested);
        harness.Add("アセットルート自身は assets:// になる",               AssetUriRootItself);
        harness.Add("ルート末尾の区切り文字は結果を変えない",              AssetUriRootTrailingSeparator);
        harness.Add("ルートの大文字小文字が違っても変換できる",            AssetUriRootCaseInsensitive);
        harness.Add("アセットルート外は null になる",                      AssetUriOutsideRoot);
        harness.Add("名前が前方一致する別フォルダを取り違えない",          AssetUriSiblingPrefixTrap);
        harness.Add("フォルダの末尾スラッシュは落ちる",                    AssetUriFolderTrailingSlash);
        harness.Add("空文字・null は null になる",                         AssetUriEmptyInput);
        harness.Add("複数パスは改行区切りになり変換不能分は落ちる",        AssetUriJoinLines);

        // ── プレビュー種別の対応表 ──────────────────────────
        harness.Add("画像拡張子は画像サムネイル対象",                      PreviewKindImage);
        harness.Add("フォント拡張子はフォントサムネイル対象",              PreviewKindFont);
        harness.Add("対象外の拡張子はプレビュー無し",                      PreviewKindNone);
        harness.Add("拡張子の大文字小文字は区別しない",                    PreviewKindCaseInsensitive);
        harness.Add("寸法取得の対象は画像だけ",                            PixelSizeTargets);
        harness.Add("パスからでもプレビュー種別を引ける",                  PreviewKindFromPath);

        // ── キャッシュキー ──────────────────────────────────
        harness.Add("同じ入力からは同じキーができる",                      CacheKeyStable);
        harness.Add("更新時刻が違えば別キーになる",                        CacheKeyTimestamp);
        harness.Add("サイズが違えば別キーになる",                          CacheKeyLength);
        harness.Add("派生条件（サムネイル一辺）が違えば別キーになる",      CacheKeyVariant);
        harness.Add("パスの大文字小文字は同一視される",                    CacheKeyPathCase);
        harness.Add("実ファイルからのキーは書き換えで変わる",              CacheKeyFromFile);
        harness.Add("存在しないファイルでもキーは作れる",                  CacheKeyMissingFile);

        // ── モデルサムネイルのキャッシュキー（ランタイムと共有の規則）──
        harness.Add("モデル拡張子はモデルサムネイル対象",                  PreviewKindModel);
        harness.Add("FNV-1a が仕様どおりの値を出す",                       ModelKeyFnvReferenceVectors);
        harness.Add("パス正規化が区切り・大小・スキームを吸収する",        ModelKeyNormalization);
        harness.Add("固定入力から Rust と同じファイル名が出る",            ModelKeyMatchesRuntimeFixture);
        harness.Add("材料が 1 つ違えばファイル名が変わる",                 ModelKeyIngredientsMatter);
        harness.Add("キャッシュパスは cache/thumbnails 配下になる",        ModelKeyCachePath);
        harness.Add("アセットルートからキャッシュ場所を導ける",            ModelKeyCacheDirFromAssetsRoot);
        harness.Add("実ファイルから作ったパスは更新で変わる",              ModelKeyPathForFile);
        harness.Add("既定サイズはランタイムの受理範囲に収まる",            ModelKeyDefaultSizeInRange);

        // ── 寸法の表示書式 ──────────────────────────────────
        harness.Add("キャプションは 1024×512 形式",                        PixelSizeCaption);
        harness.Add("ツールチップには px が付く",                          PixelSizeTooltip);
        harness.Add("0 以下の寸法は無効と判定される",                      PixelSizeValidity);

        // ── 非表示ルール（隠しファイル）──────────────────────
        harness.Add("作業フォルダ（.backup / __MACOSX）は隠れる",          HiddenWorkFolders);
        harness.Add("先頭がドットのフォルダ・ファイルは隠れる",            HiddenDotPrefixed);
        harness.Add("作業ファイルの拡張子は隠れる",                        HiddenWorkExtensions);
        harness.Add("地形の中間データ（tvox/tscatter/tcover）は隠れる",    HiddenTerrainIntermediates);
        harness.Add("OS が作るファイル（Thumbs.db 等）は隠れる",           HiddenOsFiles);
        harness.Add("macOS のリソースフォーク（._*）は隠れる",             HiddenResourceForks);
        harness.Add("Blender の世代バックアップは隠れる",                  HiddenBlenderBackups);
        harness.Add("制作物（.scene/.cs/.png/terrain_meta.json）は隠れない", VisibleAuthoredAssets);
        harness.Add("フォルダ用ルールはファイルに適用されない",            HiddenFolderRuleDoesNotLeak);
        harness.Add("拡張子の大文字小文字は区別しない（非表示判定）",      HiddenCaseInsensitive);
        harness.Add("絶対パス・末尾区切り・空文字でも落ちない",            HiddenPathForms);
        harness.Add("表示トグルが ON なら何も隠れない",                    HiddenShowAllToggle);
        harness.Add("JSON でルールを差し替えられる",                       HiddenRulesFromJson);
        harness.Add("空配列を書けばそのルールを無効にできる",              HiddenRulesEmptyList);
        harness.Add("壊れた JSON は組み込み既定へフォールバックする",      HiddenRulesBrokenJson);
        harness.Add("ファイルが無ければ組み込み既定へフォールバックする",  HiddenRulesMissingFile);
        harness.Add("同梱の project_panel_rules.json が既定と一致する",    HiddenRulesShippedFile);

        return harness.Run();
    }

    // ── assets:// 相対化 ────────────────────────────────────────

    /// <summary>テスト用のアセットルート（実在しなくてよい。純粋なパス計算のため）。</summary>
    private const string Root = @"C:\projects\Warashibe\assets";

    private static void AssetUriDirectChild()
    {
        Check.Equal("assets://credits.txt",
                    AssetUriPath.ToAssetUri(Root, Root + @"\credits.txt"), "直下ファイル");
    }

    private static void AssetUriNested()
    {
        Check.Equal("assets://mainGame/textures/ui/white.png",
                    AssetUriPath.ToAssetUri(Root, Root + @"\mainGame\textures\ui\white.png"), "入れ子");
        Check.Equal("mainGame/textures/ui/white.png",
                    AssetUriPath.ToRelative(Root, Root + @"\mainGame\textures\ui\white.png"), "スキーム無し");
    }

    private static void AssetUriRootItself()
    {
        Check.Equal("assets://", AssetUriPath.ToAssetUri(Root, Root), "ルート自身");
        Check.Equal("",          AssetUriPath.ToRelative(Root, Root), "ルート自身（相対）");
    }

    private static void AssetUriRootTrailingSeparator()
    {
        Check.Equal("assets://mainGame/fonts",
                    AssetUriPath.ToAssetUri(Root + @"\", Root + @"\mainGame\fonts"), "末尾区切り付きルート");
    }

    private static void AssetUriRootCaseInsensitive()
    {
        // Windows のファイルシステムは大文字小文字を区別しない。
        // パネルの _assetsRoot とタイルの Tag で綴りが揺れても同じ結果になること。
        Check.Equal("assets://mainGame/fonts/x.otf",
                    AssetUriPath.ToAssetUri(Root.ToUpperInvariant(), Root + @"\mainGame\fonts\x.otf"),
                    "大文字ルート");
    }

    private static void AssetUriOutsideRoot()
    {
        Check.Equal(null, AssetUriPath.ToAssetUri(Root, @"D:\elsewhere\x.png"), "別ドライブ");
        Check.Equal(null, AssetUriPath.ToAssetUri(Root, @"C:\projects\Warashibe\readme.md"), "ルートの親");
    }

    private static void AssetUriSiblingPrefixTrap()
    {
        // "assets" と "assetsBackup" は文字列前方一致では区別できない。
        // ここが null にならないと、プロジェクト外のファイルへ assets:// を振ってしまう。
        Check.Equal(null,
                    AssetUriPath.ToAssetUri(Root, @"C:\projects\Warashibe\assetsBackup\x.png"),
                    "接頭辞が一致する別フォルダ");
    }

    private static void AssetUriFolderTrailingSlash()
    {
        Check.Equal("assets://mainGame/fonts",
                    AssetUriPath.ToAssetUri(Root, Root + @"\mainGame\fonts\"), "末尾スラッシュ付きフォルダ");
    }

    private static void AssetUriEmptyInput()
    {
        Check.Equal(null, AssetUriPath.ToAssetUri(Root, ""),   "空パス");
        Check.Equal(null, AssetUriPath.ToAssetUri("",   Root), "空ルート");
        Check.Equal(null, AssetUriPath.ToAssetUri(null, null), "null");
    }

    private static void AssetUriJoinLines()
    {
        var joined = AssetUriPath.JoinLines(new[] { "assets://a.png", null, "assets://b.png" });
        Check.Equal("assets://a.png" + Environment.NewLine + "assets://b.png", joined, "改行区切り");
        Check.Equal("", AssetUriPath.JoinLines(new string?[] { null, "" }), "全部変換不能なら空");
    }

    // ── プレビュー種別の対応表 ──────────────────────────────────

    private static void PreviewKindImage()
    {
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tga", ".hdr", ".exr", ".webp" })
        {
            Check.Equal(AssetPreviewKind.Image, AssetPreviewKinds.Of(ext), ext);
            Check.True(AssetPreviewKinds.SupportsImageThumbnail(ext), ext + " は画像サムネイル対象");
            Check.True(!AssetPreviewKinds.SupportsFontThumbnail(ext), ext + " はフォント対象ではない");
        }
    }

    private static void PreviewKindFont()
    {
        foreach (var ext in new[] { ".ttf", ".otf", ".ttc" })
        {
            Check.Equal(AssetPreviewKind.Font, AssetPreviewKinds.Of(ext), ext);
            Check.True(AssetPreviewKinds.SupportsFontThumbnail(ext), ext + " はフォントサムネイル対象");
            Check.True(!AssetPreviewKinds.SupportsImageThumbnail(ext), ext + " は画像対象ではない");
        }
    }

    private static void PreviewKindNone()
    {
        foreach (var ext in new[] { ".cs", ".scene", ".wav", ".txt", ".unknown", "", ".fontx" })
            Check.Equal(AssetPreviewKind.None, AssetPreviewKinds.Of(ext), "'" + ext + "'");
        Check.Equal(AssetPreviewKind.None, AssetPreviewKinds.Of(null), "null");
    }

    private static void PreviewKindCaseInsensitive()
    {
        Check.Equal(AssetPreviewKind.Image, AssetPreviewKinds.Of(".PNG"), "大文字 .PNG");
        Check.Equal(AssetPreviewKind.Font,  AssetPreviewKinds.Of(".OTF"), "大文字 .OTF");
    }

    private static void PixelSizeTargets()
    {
        Check.True(AssetPreviewKinds.SupportsPixelSize(".png"), "png は寸法取得対象");
        Check.True(AssetPreviewKinds.SupportsPixelSize(".dds"), "dds も候補に入れる（読めるかは取得側が判断）");
        Check.True(!AssetPreviewKinds.SupportsPixelSize(".otf"), "フォントは寸法取得しない");
        Check.True(!AssetPreviewKinds.SupportsPixelSize(".cs"),  "スクリプトは寸法取得しない");
        Check.True(!AssetPreviewKinds.SupportsPixelSize(null),   "null は対象外");
    }

    private static void PreviewKindFromPath()
    {
        Check.Equal(AssetPreviewKind.Image, AssetPreviewKinds.OfPath(@"C:\a\b\white.PNG"), "パス指定（画像）");
        Check.Equal(AssetPreviewKind.Font,  AssetPreviewKinds.OfPath(@"C:\a\ゆず ポップ.ttf"), "パス指定（フォント）");
        Check.True(AssetPreviewKinds.SupportsPixelSizePath(@"C:\a\b.png"), "パス指定（寸法取得）");
        Check.True(!AssetPreviewKinds.SupportsPixelSizePath(""), "空パスは対象外");
    }

    // ── キャッシュキー ──────────────────────────────────────────

    /// <summary>キー生成に使う基準時刻（値が変わったことだけを見るので任意の固定値でよい）。</summary>
    private static readonly DateTime BaseTime = new(2026, 9, 12, 1, 2, 3, DateTimeKind.Utc);

    /// <summary>キー生成に使う基準サイズ（バイト）。</summary>
    private const long BaseLength = 2006468;

    private static void CacheKeyStable()
    {
        var a = AssetPreviewCacheKey.Build(@"C:\a\b.png", BaseTime, BaseLength);
        var b = AssetPreviewCacheKey.Build(@"C:\a\b.png", BaseTime, BaseLength);
        Check.Equal(a, b, "同じ入力");
        Check.True(a.Contains("b.png"), "キーにパスが含まれる");
    }

    private static void CacheKeyTimestamp()
    {
        var a = AssetPreviewCacheKey.Build(@"C:\a\b.png", BaseTime, BaseLength);
        var b = AssetPreviewCacheKey.Build(@"C:\a\b.png", BaseTime.AddSeconds(1), BaseLength);
        Check.True(a != b, "更新時刻違いは別キー");
    }

    private static void CacheKeyLength()
    {
        // 更新時刻の分解能で拾えない差し替え（同じ秒に別サイズのファイルへ置換）を検出する。
        var a = AssetPreviewCacheKey.Build(@"C:\a\b.png", BaseTime, BaseLength);
        var b = AssetPreviewCacheKey.Build(@"C:\a\b.png", BaseTime, BaseLength + 1);
        Check.True(a != b, "サイズ違いは別キー");
    }

    private static void CacheKeyVariant()
    {
        var small = AssetPreviewCacheKey.Build(@"C:\a\b.otf", BaseTime, BaseLength, "96");
        var large = AssetPreviewCacheKey.Build(@"C:\a\b.otf", BaseTime, BaseLength, "192");
        Check.True(small != large, "サムネイル一辺違いは別キー");
    }

    private static void CacheKeyPathCase()
    {
        var lower = AssetPreviewCacheKey.Build(@"c:\a\b.png", BaseTime, BaseLength);
        var upper = AssetPreviewCacheKey.Build(@"C:\A\B.PNG", BaseTime, BaseLength);
        Check.Equal(lower, upper, "大文字小文字違いは同じキー");
    }

    private static void CacheKeyFromFile()
    {
        using var temp = new TempDir();
        var path = temp.Combine("sample.png");
        File.WriteAllText(path, "first");

        var first = AssetPreviewCacheKey.BuildFromFile(path);
        Check.Equal(first, AssetPreviewCacheKey.BuildFromFile(path), "同じファイルなら同じキー");

        // 内容を変えて更新時刻もずらす（FAT 系の 2 秒分解能でもサイズ差で確実に別キーになる）
        File.WriteAllText(path, "second-longer-content");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Check.True(first != AssetPreviewCacheKey.BuildFromFile(path), "書き換え後は別キー");
    }

    private static void CacheKeyMissingFile()
    {
        using var temp = new TempDir();
        var key = AssetPreviewCacheKey.BuildFromFile(temp.Combine("no_such_file.png"));
        Check.True(!string.IsNullOrEmpty(key), "存在しなくても例外にせずキーを返す");
    }

    // ── モデルサムネイルのキャッシュキー ────────────────────────
    //
    //  ここはランタイム（Rust）との**契約**のテストである。
    //  プロジェクトパネルは「キャッシュ PNG があるか」をこの規則で探し、
    //  ランタイムは同じ規則で PNG を書く。1 文字でもずれると
    //  エディタは永久にキャッシュを見つけられず、毎回描き直しになる。
    //
    //  対になる Rust 側テスト:
    //    runtime/src/engine/core/renderer/thumbnail/cache_key.rs の
    //      - fnv1a_matches_reference_vectors
    //      - normalization_unifies_separators_and_case
    //      - fixed_input_produces_the_agreed_file_name
    //      - every_ingredient_changes_the_file_name
    //      - cache_path_is_under_the_thumbnails_subdirectory
    //  **両者は同じ入力・同じ期待値を書くこと。**

    /// <summary>両言語のテストで共有する固定入力: アセットパス。</summary>
    private const string FixtureAssetPath = "assets://mainGame/models/Yasi.glb";

    /// <summary>両言語のテストで共有する固定入力: 最終更新時刻（Unix 秒）。</summary>
    private const long FixtureModifiedUnixSeconds = 1_700_000_000L;

    /// <summary>両言語のテストで共有する固定入力: ファイルサイズ（バイト）。</summary>
    private const long FixtureFileSizeBytes = 123_456L;

    /// <summary>両言語のテストで共有する固定入力: サムネイル 1 辺（px）。</summary>
    private const int FixtureSizePx = 128;

    /// <summary>固定入力から出るべきキー文字列（Rust 側と同一）。</summary>
    private const string FixtureKeyString = "maingame/models/yasi.glb|1700000000|123456|128";

    /// <summary>固定入力から出るべきファイル名（Rust 側と同一）。</summary>
    private const string FixtureFileName = "63cf730ec7b8e0eb.png";

    /// <summary>固定入力のファイル名を求めるヘルパ。</summary>
    private static string FixtureName()
        => ModelThumbnailCacheKey.BuildFileName(
            FixtureAssetPath, FixtureModifiedUnixSeconds, FixtureFileSizeBytes, FixtureSizePx);

    private static void PreviewKindModel()
    {
        foreach (var ext in new[] { ".glb", ".gltf", ".obj" })
        {
            Check.Equal(AssetPreviewKind.Model, AssetPreviewKinds.Of(ext), ext);
            Check.True(AssetPreviewKinds.SupportsModelThumbnail(ext), ext + " はモデルサムネイル対象");
        }
        Check.Equal(AssetPreviewKind.Model, AssetPreviewKinds.Of(".GLB"), "大文字 .GLB");
        // .fbx はランタイムのローダが非対応。対象に入れると必ず失敗応答が返る。
        Check.Equal(AssetPreviewKind.None, AssetPreviewKinds.Of(".fbx"), ".fbx は対象外");
        // .blend も 3D だが読めないので対象外（Blender アイコンを出すだけ）
        Check.Equal(AssetPreviewKind.None, AssetPreviewKinds.Of(".blend"), ".blend は対象外");
    }

    private static void ModelKeyFnvReferenceVectors()
    {
        // FNV 公式のテストベクタ（Rust 側と同じ 3 本）
        Check.Equal(0xcbf29ce484222325UL,
            ModelThumbnailCacheKey.Fnv1a64(Array.Empty<byte>()), "空文字列");
        Check.Equal(0xaf63dc4c8601ec8cUL,
            ModelThumbnailCacheKey.Fnv1a64(System.Text.Encoding.UTF8.GetBytes("a")), "a");
        Check.Equal(0x85944171f73967e8UL,
            ModelThumbnailCacheKey.Fnv1a64(System.Text.Encoding.UTF8.GetBytes("foobar")), "foobar");
    }

    private static void ModelKeyNormalization()
    {
        Check.Equal("maingame/models/yasi.glb",
            ModelThumbnailCacheKey.NormalizeAssetPath(@"assets://mainGame\Models\Yasi.GLB"),
            "スキーム・区切り・大小の吸収");
        Check.Equal("foo/bar.obj",
            ModelThumbnailCacheKey.NormalizeAssetPath("/Foo/Bar.obj"), "先頭スラッシュは落ちる");
        // 非 ASCII はそのまま（大小の区別が無いので変換不要。Rust の to_ascii_lowercase と一致）
        Check.Equal("モデル/魚.glb",
            ModelThumbnailCacheKey.NormalizeAssetPath("assets://モデル/魚.glb"), "非 ASCII は不変");
        Check.Equal("", ModelThumbnailCacheKey.NormalizeAssetPath(null), "null は空文字");
    }

    private static void ModelKeyMatchesRuntimeFixture()
    {
        Check.Equal(FixtureKeyString,
            ModelThumbnailCacheKey.BuildKeyString(
                FixtureAssetPath, FixtureModifiedUnixSeconds, FixtureFileSizeBytes, FixtureSizePx),
            "キー文字列が Rust 側と一致すること");
        Check.Equal(FixtureFileName, FixtureName(), "ファイル名が Rust 側と一致すること");

        // 表記ゆれのあるパスでも同じキャッシュファイルへ落ちる
        Check.Equal(FixtureFileName,
            ModelThumbnailCacheKey.BuildFileName(
                @"mainGame\models\YASI.glb",
                FixtureModifiedUnixSeconds, FixtureFileSizeBytes, FixtureSizePx),
            "表記ゆれでも同じファイル名");
    }

    private static void ModelKeyIngredientsMatter()
    {
        var baseName = FixtureName();

        Check.True(ModelThumbnailCacheKey.BuildFileName(
            FixtureAssetPath, FixtureModifiedUnixSeconds + 1, FixtureFileSizeBytes, FixtureSizePx)
            != baseName, "更新時刻が効いていない");

        Check.True(ModelThumbnailCacheKey.BuildFileName(
            FixtureAssetPath, FixtureModifiedUnixSeconds, FixtureFileSizeBytes + 1, FixtureSizePx)
            != baseName, "ファイルサイズが効いていない");

        Check.True(ModelThumbnailCacheKey.BuildFileName(
            FixtureAssetPath, FixtureModifiedUnixSeconds, FixtureFileSizeBytes, 256)
            != baseName, "要求ピクセル数が効いていない");

        Check.True(ModelThumbnailCacheKey.BuildFileName(
            "mainGame/models/hut.glb",
            FixtureModifiedUnixSeconds, FixtureFileSizeBytes, FixtureSizePx)
            != baseName, "パスが効いていない");
    }

    private static void ModelKeyCachePath()
    {
        var path = ModelThumbnailCacheKey.BuildPath(
            @"C:\proj\cache", FixtureAssetPath,
            FixtureModifiedUnixSeconds, FixtureFileSizeBytes, FixtureSizePx);
        Check.Equal(Path.Combine(@"C:\proj\cache", "thumbnails", FixtureFileName), path,
            "cache/thumbnails/<ハッシュ>.png になること");

        Check.Equal(null, ModelThumbnailCacheKey.BuildPath(
            null, FixtureAssetPath,
            FixtureModifiedUnixSeconds, FixtureFileSizeBytes, FixtureSizePx),
            "キャッシュ場所が無ければ null");
    }

    private static void ModelKeyCacheDirFromAssetsRoot()
    {
        // ランタイムは --assets-root の親から cache を導出する（asset_cache.rs の cache_dir()）
        Check.Equal(Path.Combine(@"C:\projects\Warashibe", "cache"),
            ModelThumbnailCacheKey.CacheDirForAssetsRoot(@"C:\projects\Warashibe\assets"),
            "アセットルートの親の cache/");
        // 末尾区切りがあっても同じ場所になる
        Check.Equal(Path.Combine(@"C:\projects\Warashibe", "cache"),
            ModelThumbnailCacheKey.CacheDirForAssetsRoot(@"C:\projects\Warashibe\assets\"),
            "末尾区切りは結果を変えない");
        Check.Equal(null, ModelThumbnailCacheKey.CacheDirForAssetsRoot(""), "空文字は null");
        Check.Equal(null, ModelThumbnailCacheKey.CacheDirForAssetsRoot(null), "null は null");
    }

    private static void ModelKeyPathForFile()
    {
        using var temp = new TempDir();
        var model = Path.Combine(temp.Path, "a.glb");
        File.WriteAllText(model, "first");

        var first = ModelThumbnailCacheKey.BuildPathForFile(
            @"C:\proj\cache", "models/a.glb", model, ModelThumbnailCacheKey.DefaultSizePx);
        Check.True(first != null, "実ファイルからパスを作れること");

        // 内容とサイズを変える（更新時刻が同じ秒でもサイズで区別できること）
        File.WriteAllText(model, "second-and-longer");
        var second = ModelThumbnailCacheKey.BuildPathForFile(
            @"C:\proj\cache", "models/a.glb", model, ModelThumbnailCacheKey.DefaultSizePx);
        Check.True(first != second, "書き換えたら別のキャッシュパスになること");

        // 存在しないファイルは「サムネイルを出せない」として null
        Check.Equal(null, ModelThumbnailCacheKey.BuildPathForFile(
            @"C:\proj\cache", "models/none.glb",
            Path.Combine(temp.Path, "none.glb"), ModelThumbnailCacheKey.DefaultSizePx),
            "存在しないファイルは null");
    }

    private static void ModelKeyDefaultSizeInRange()
    {
        // 既定値の所有者はエディタ側。ランタイムの受理範囲から外れると
        // タイルごとに必ず失敗応答が返るので、上下限との整合をここで見張る。
        Check.True(ModelThumbnailCacheKey.DefaultSizePx >= ModelThumbnailCacheKey.MinSizePx,
            "既定サイズが下限を下回っている");
        Check.True(ModelThumbnailCacheKey.DefaultSizePx <= ModelThumbnailCacheKey.MaxSizePx,
            "既定サイズが上限を超えている");
    }

    // ── 寸法の表示書式 ──────────────────────────────────────────

    private static void PixelSizeCaption()
    {
        Check.Equal("1024×512", new ImagePixelSize(1024, 512).ToCaption(), "キャプション");
        Check.Equal("1×1",      new ImagePixelSize(1, 1).ToCaption(),      "最小");
    }

    private static void PixelSizeTooltip()
    {
        Check.Equal("1024×512 px", new ImagePixelSize(1024, 512).ToTooltipText(), "ツールチップ");
    }

    private static void PixelSizeValidity()
    {
        Check.True(new ImagePixelSize(1, 1).IsValid,     "正の寸法は有効");
        Check.True(!new ImagePixelSize(0, 16).IsValid,   "幅 0 は無効");
        Check.True(!new ImagePixelSize(16, -1).IsValid,  "負の高さは無効");
    }

    // ── 非表示ルール（隠しファイル）──────────────────────────────
    //
    //  「パネルに出さないもの」の判定。ここが緩むと作業ファイルで一覧が埋まり、
    //  きつすぎると制作物（.scene / 画像 / スクリプト）が消えて作業できなくなる。
    //  後者のほうが重大なので、"隠れないこと" のテストも必ず置く。

    /// <summary>組み込み既定のルール（JSON を読まない）。</summary>
    private static ProjectPanelVisibilityRules Rules() => ProjectPanelVisibilityRules.BuiltIn();

    /// <summary>ファイル名が隠れることを表明する。</summary>
    private static void AssertHiddenFile(string name)
        => Check.True(Rules().IsHiddenFileName(name), $"隠れるはず: {name}");

    /// <summary>ファイル名が隠れないことを表明する。</summary>
    private static void AssertVisibleFile(string name)
        => Check.True(!Rules().IsHiddenFileName(name), $"見えるはず: {name}");

    private static void HiddenWorkFolders()
    {
        var rules = Rules();
        Check.True(rules.IsHiddenFolderName(".backup"),   ".backup（シーンの世代バックアップ）");
        Check.True(rules.IsHiddenFolderName("__MACOSX"),  "__MACOSX（zip 展開の残骸）");
        Check.True(rules.IsHiddenFolderName("__macosx"),  "大文字小文字は無視");
        Check.True(!rules.IsHiddenFolderName("textures"), "普通のフォルダは見える");
    }

    private static void HiddenDotPrefixed()
    {
        var rules = Rules();
        Check.True(rules.IsHiddenFolderName(".git"),     ".git");
        Check.True(rules.IsHiddenFolderName(".vscode"),  ".vscode");
        Check.True(rules.IsHiddenFileName(".gitignore"), ".gitignore");
        Check.True(rules.IsHiddenFileName(".DS_Store"),  ".DS_Store");
    }

    private static void HiddenWorkExtensions()
    {
        AssertHiddenFile("scene.lock");     // エディタのロック
        AssertHiddenFile("work.tmp");       // 一時ファイル
        AssertHiddenFile("player.cs.bak");  // 手動バックアップ
        AssertHiddenFile("tree.blend1");    // Blender の世代バックアップ
    }

    private static void HiddenTerrainIntermediates()
    {
        // いずれもエンジンが生成する独自バイナリ（magic TVOX / TSCT / TCOV）。
        // エディタの地形ツールが読み書きするだけで、人が開く意味は無い。
        AssertHiddenFile("chunk_0_0_0.tvox");
        AssertHiddenFile("chunk_0_0_0.tscatter");
        AssertHiddenFile("chunk_0_0_0.tcover");
    }

    private static void HiddenOsFiles()
    {
        AssertHiddenFile("Thumbs.db");
        AssertHiddenFile("thumbs.db");     // 大文字小文字は無視
        AssertHiddenFile("desktop.ini");
    }

    private static void HiddenResourceForks()
    {
        AssertHiddenFile("._character.png");   // AppleDouble
        AssertVisibleFile("_character.png");   // 先頭が "_" だけなら普通のファイル
    }

    private static void HiddenBlenderBackups()
    {
        AssertHiddenFile("stage.blend1");
        AssertHiddenFile("stage.blend9");      // パターン "*.blend?" で 2 世代目以降も拾う
        AssertVisibleFile("stage.blend");      // 本体は当然見える
    }

    private static void VisibleAuthoredAssets()
    {
        AssertVisibleFile("title.scene");
        AssertVisibleFile("Player.cs");
        AssertVisibleFile("hero.png");
        AssertVisibleFile("bgm.wav");
        AssertVisibleFile("ui.icons");
        // 地形フォルダの設定値は平文 JSON で、内容に意味があるので隠さない
        AssertVisibleFile("terrain_meta.json");
    }

    private static void HiddenFolderRuleDoesNotLeak()
    {
        var rules = Rules();
        // "__MACOSX" はフォルダ名のルール。同名のファイルは（ドットでもないので）見える。
        Check.True(!rules.IsHiddenFileName("__MACOSX"),   "フォルダ名ルールはファイルに効かない");
        // 逆に Thumbs.db はファイル名のルール。同名フォルダは見える。
        Check.True(!rules.IsHiddenFolderName("Thumbs.db"), "ファイル名ルールはフォルダに効かない");
    }

    private static void HiddenCaseInsensitive()
    {
        AssertHiddenFile("WORK.TMP");
        AssertHiddenFile("CHUNK_0_0_0.TVOX");
    }

    private static void HiddenPathForms()
    {
        var rules = Rules();
        Check.True(rules.IsHiddenPath(@"C:\proj\assets\.backup", isDirectory: true),  "絶対パス（フォルダ）");
        Check.True(rules.IsHiddenPath(@"C:\proj\assets\.backup\", isDirectory: true), "末尾区切りがあっても同じ");
        Check.True(rules.IsHiddenPath(@"C:\proj\assets\a\b.tmp", isDirectory: false), "絶対パス（ファイル）");
        Check.True(!rules.IsHiddenPath("", isDirectory: false),   "空文字は隠さない（例外にしない）");
        Check.True(!rules.IsHiddenPath(null, isDirectory: false), "null は隠さない（例外にしない）");
    }

    private static void HiddenShowAllToggle()
    {
        var rules = Rules();
        Check.True(!rules.ShouldShowPath("work.tmp", false, showHidden: false), "OFF なら隠れる");
        Check.True(rules.ShouldShowPath("work.tmp", false, showHidden: true),   "ON なら見える");
        Check.True(rules.ShouldShowPath("hero.png", false, showHidden: false),  "対象外は常に見える");
    }

    private static void HiddenRulesFromJson()
    {
        using var temp = new TempDir();
        var path = temp.Combine(ProjectPanelVisibilityRules.FileName);
        File.WriteAllText(path, """
        {
          "format_version": 1,
          "hide_dot_prefixed": false,
          "hidden_folder_names": ["Intermediate"],
          "hidden_file_names": ["notes.md"],
          "hidden_file_extensions": ["foo"],
          "hidden_file_patterns": ["draft_*"]
        }
        """);

        var rules = ProjectPanelVisibilityRules.Load(path);
        Check.Equal(0, rules.Warnings.Count, "警告なしで読める");
        Check.True(rules.SourcePath != null, "読み込み元が記録される");

        Check.True(rules.IsHiddenFolderName("Intermediate"), "JSON のフォルダ名が効く");
        Check.True(rules.IsHiddenFileName("notes.md"),       "JSON のファイル名が効く");
        Check.True(rules.IsHiddenFileName("data.foo"),       "ドット無しで書いた拡張子も効く");
        Check.True(rules.IsHiddenFileName("draft_01.png"),   "JSON のパターンが効く");
        Check.True(!rules.IsHiddenFolderName(".git"),        "hide_dot_prefixed=false が効く");
        Check.True(!rules.IsHiddenFileName("work.tmp"),      "JSON で差し替えたので組み込み既定は効かない");
    }

    private static void HiddenRulesEmptyList()
    {
        using var temp = new TempDir();
        var path = temp.Combine(ProjectPanelVisibilityRules.FileName);
        // 「拡張子では何も隠さない」を空配列で明示できること（未指定＝既定、と区別する）
        File.WriteAllText(path, """
        { "format_version": 1, "hidden_file_extensions": [] }
        """);

        var rules = ProjectPanelVisibilityRules.Load(path);
        Check.True(!rules.IsHiddenFileName("work.tmp"),    "空配列なら拡張子では隠れない");
        Check.True(rules.IsHiddenFolderName(".backup"),    "未指定のルールは組み込み既定のまま");
    }

    private static void HiddenRulesBrokenJson()
    {
        using var temp = new TempDir();
        var path = temp.Combine(ProjectPanelVisibilityRules.FileName);
        File.WriteAllText(path, "{ これは JSON ではない");

        var rules = ProjectPanelVisibilityRules.Load(path);
        Check.True(rules.Warnings.Count > 0,            "理由が警告に残る");
        Check.Equal(null, rules.SourcePath,             "組み込み既定なので読み込み元は無い");
        Check.True(rules.IsHiddenFileName("work.tmp"),  "組み込み既定で動き続ける");
    }

    private static void HiddenRulesMissingFile()
    {
        using var temp = new TempDir();
        var rules = ProjectPanelVisibilityRules.LoadFromDir(temp.Path);
        Check.True(rules.Warnings.Count > 0,           "理由が警告に残る");
        Check.True(rules.IsHiddenFolderName(".backup"), "組み込み既定で動き続ける");
    }

    /// <summary>
    /// リポジトリに入っている実物（editor/config/project_panel_rules.json）を読む。
    ///
    /// 組み込み既定は「JSON を消しても動く」ための写しなので、両者がずれると
    /// 「開発環境と配布環境で隠れ方が違う」という気付きにくい差になる。
    /// 実ファイルを読んで、警告ゼロ・既定と同じ判定になることを確かめる。
    /// </summary>
    private static void HiddenRulesShippedFile()
    {
        var configDir = FindEditorConfigDir();
        var rules     = ProjectPanelVisibilityRules.LoadFromDir(configDir);

        Check.True(rules.SourcePath != null, "同梱ファイルを実際に読めている");
        Check.Equal(0, rules.Warnings.Count, "同梱ファイルは警告なしで読めること");

        // 組み込み既定と同じ結果になること（代表例で突き合わせる）
        var builtIn = ProjectPanelVisibilityRules.BuiltIn();
        string[] samples =
        {
            ".backup", "__MACOSX", ".git", "Thumbs.db", "desktop.ini", ".DS_Store",
            "a.lock", "a.tmp", "a.bak", "a.blend1", "a.blend2", "._a.png",
            "a.tvox", "a.tscatter", "a.tcover",
            "a.scene", "a.cs", "a.png", "terrain_meta.json",
        };
        foreach (var name in samples)
        {
            Check.Equal(builtIn.IsHiddenFileName(name),   rules.IsHiddenFileName(name),   $"ファイル: {name}");
            Check.Equal(builtIn.IsHiddenFolderName(name), rules.IsHiddenFolderName(name), $"フォルダ: {name}");
        }
    }

    /// <summary>
    /// editor/config フォルダを探す。テストの出力先（editor/tests/&lt;Proj&gt;/bin/&lt;Cfg&gt;/&lt;tfm&gt;）から
    /// 上へ辿り、SEEDEditor.csproj のあるフォルダ（＝ editor）を見つけて config を返す。
    /// </summary>
    /// <returns>editor/config の絶対パス。</returns>
    private static string FindEditorConfigDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SEEDEditor.csproj")))
                return Path.Combine(dir.FullName, "config");
            dir = dir.Parent;
        }
        throw new AssertionException(
            $"editor フォルダが見つかりません（起点: {AppContext.BaseDirectory}）");
    }
}
