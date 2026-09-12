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

        // ── 寸法の表示書式 ──────────────────────────────────
        harness.Add("キャプションは 1024×512 形式",                        PixelSizeCaption);
        harness.Add("ツールチップには px が付く",                          PixelSizeTooltip);
        harness.Add("0 以下の寸法は無効と判定される",                      PixelSizeValidity);

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
}
