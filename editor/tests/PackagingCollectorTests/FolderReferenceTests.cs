// ============================================================
//  FolderReferenceTests.cs — 末尾が '/' のフォルダ参照（"assets://common/data/"）の収録
//  （docs/backlog.md「W3: Wake or Pay の移植で見つかった…」の「パッケージの収録が末尾 / のフォルダ参照を拾わない」）
//
//  【起きていたこと】
//  Wake or Pay のスクリプトの定数 "assets://common/data/"・"assets://common/themes/" のフォルダが pak に入らず、
//  Android（APK の pak から読む）でデータの JSON が 1 つも読めなかった。PC の Play はディスクから直接読むので気づけない。
//  原因: AssetPathUtil.NormalizeRelative が末尾の '/' を落とさず "common/data/" のまま照合し、
//  AssetCollector のフォルダの索引（末尾 '/' なしで登録）と一致しなかった。拡張子も無いので欠落の報告にもならず、黙って落ちた。
//
//  【検証範囲】
//   - NormalizeRelative は末尾の '/'（と '\'）を落とし、ファイルの参照の正規形は変えない
//   - 走査が拾う候補・Collect の閉包・CollectFrom の起点・追加同梱フォルダの設定のどれでも、末尾 '/' のフォルダが解決する
// ============================================================

using System;
using System.Linq;
using SEEDEditor.Packaging.Collect;
using SpriteRigTests;

namespace SEEDEditor.Tests.PackagingCollector;

/// <summary>末尾が '/' のフォルダ参照のテスト。</summary>
public static class FolderReferenceTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("末尾 /: NormalizeRelative は末尾の / を落とし、ファイルの参照の正規形は変えない", NormalizeDropsTrailingSlashOnly);
        h.Add("末尾 /: 走査が拾う assets://common/data/ の候補は末尾 / なし", ScannerCandidateHasNoTrailingSlash);
        h.Add("末尾 /: スクリプトの \"assets://common/data/\" のフォルダの中身が収録される（欠落にもならない）", CollectIncludesTrailingSlashFolder);
        h.Add("末尾 /: CollectFrom の起点・追加同梱フォルダの設定に末尾 / のフォルダを書いても解決する", SeedsAndAdditionalFoldersAcceptTrailingSlash);
    }

    /// <summary>NormalizeRelative の正規形。</summary>
    private static void NormalizeDropsTrailingSlashOnly()
    {
        // フォルダ（末尾の区切りを落とす。重なり・'\'・先頭の "./" と '/' も今までどおり落とす）
        Check.Equal("common/data", AssetPathUtil.NormalizeRelative("common/data/"), "末尾の /");
        Check.Equal("common/data", AssetPathUtil.NormalizeRelative("common/data//"), "末尾の / が重なる");
        Check.Equal("common/data", AssetPathUtil.NormalizeRelative(@"common\data\"), "末尾の \\");
        Check.Equal("common/data", AssetPathUtil.NormalizeRelative("/common/data/"), "先頭と末尾の /");
        Check.Equal("common/data", AssetPathUtil.NormalizeRelative("./common/data/"), "先頭の ./ と末尾の /");
        Check.Equal("common", AssetPathUtil.NormalizeRelative("common/data/../"), ".. で畳んだ後の末尾");
        Check.Equal("", AssetPathUtil.NormalizeRelative("/"), "/ だけは空");
        Check.Equal("", AssetPathUtil.NormalizeRelative(""), "空は空");

        // ファイル（正規形は今までと同じ）
        Check.Equal("ui/logo.png", AssetPathUtil.NormalizeRelative("ui/logo.png"), "ファイルはそのまま");
        Check.Equal("ui/logo.png", AssetPathUtil.NormalizeRelative("./ui/logo.png"), "先頭の ./");
        Check.Equal("ui/logo.png", AssetPathUtil.NormalizeRelative(@"\ui\logo.png"), "先頭の \\ と区切りの \\");
        Check.Equal("ui/logo.png", AssetPathUtil.NormalizeRelative("textures/../ui/logo.png"), ".. の畳み");
        Check.Equal("models/tex a.png", AssetPathUtil.NormalizeRelative(" models/tex a.png "), "前後の空白だけ落とし、中の空白は残す");
        Check.Equal("画面/タイトル.scene", AssetPathUtil.NormalizeRelative("画面/タイトル.scene"), "日本語のパス");
        Check.Equal("terrain/Scene1", AssetPathUtil.NormalizeRelative("terrain/Scene1"), "末尾 / の無いフォルダはそのまま");
    }

    /// <summary>走査の候補。</summary>
    private static void ScannerCandidateHasNoTrailingSlash()
    {
        const string text = "public static class GameData { public const string DataRoot = \"assets://common/data/\"; }";
        var refs = AssetReferenceScanner.Scan(text, "scripts/GameData.cs", @"C:\proj\assets");
        var folder = refs.FirstOrDefault(r => r.Raw == "assets://common/data/");
        Check.True(folder.Candidates is not null, "assets://common/data/ を拾っていない");
        Check.Equal("common/data", folder.Candidates![0], "候補は末尾 / なし（フォルダの索引と同じ形）");
        Check.True(folder.IsExplicit, "assets:// は確実な参照");
    }

    /// <summary>Collect の閉包で末尾 / のフォルダが入る。</summary>
    private static void CollectIncludesTrailingSlashFolder()
    {
        using var fx = new AssetFixture();
        // Wake or Pay と同じ形: どのシーンからも辿られないスクリプトの定数（型参照対策の走査で拾われる）
        fx.WriteText("scripts/GameData.cs", """
        public static class GameData
        {
            public const string DataRoot = "assets://common/data/";
            public const string ThemeFolder = "assets://common/themes/";
        }
        """);
        fx.WriteText("common/data/alarms.json", "{ \"alarms\": [] }");
        fx.WriteText("common/data/catalog/items.json", "{ \"items\": [] }");
        fx.WriteText("common/themes/midnight.json", "{ \"extends\": \"assets://common/themes/base.json\" }");
        fx.WriteText("common/themes/base.json", "{ }");
        fx.WriteText("common/unrelated/skip.json", "{ }");

        var result = new AssetCollector(fx.Root, new AssetPackagingSettings()).Collect();
        var included = result.Included.Select(a => a.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in new[]
                 { "common/data/alarms.json", "common/data/catalog/items.json", "common/themes/midnight.json", "common/themes/base.json" })
            Check.True(included.Contains(expected), $"末尾 / のフォルダ参照の中身が入っていない: {expected}");
        Check.True(!included.Contains("common/unrelated/skip.json"), "参照されていないフォルダまで入った");
        Check.True(!result.MissingReferences.Any(m => m.ReferencePath.StartsWith("common/", StringComparison.OrdinalIgnoreCase)),
            "フォルダ参照を欠落として報告した: " + string.Join(", ", result.MissingReferences.Select(m => m.ReferencePath)));
    }

    /// <summary>CollectFrom の起点と追加同梱フォルダ。</summary>
    private static void SeedsAndAdditionalFoldersAcceptTrailingSlash()
    {
        using var fx = new AssetFixture();

        // CollectFrom の起点（テンプレートのインポートの入口と同じ）
        var fromSeeds = new AssetCollector(fx.Root, new AssetPackagingSettings()).CollectFrom(["ui/"]);
        var seeded = fromSeeds.Included.Select(a => a.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check.True(seeded.Contains("ui/logo.png") && seeded.Contains("ui/frame.png"), "起点の \"ui/\" をフォルダとして解決していない");
        Check.True(!fromSeeds.MissingReferences.Any(), "起点の \"ui/\" を欠落にした: " +
            string.Join(", ", fromSeeds.MissingReferences.Select(m => m.ReferencePath)));

        // 追加同梱フォルダの設定（packaging_settings.json の additional_folders）
        var settings = new AssetPackagingSettings();
        settings.AdditionalFolders.Add("extra/");
        var withFolder = new AssetCollector(fx.Root, settings).Collect();
        Check.True(withFolder.Included.Any(a => string.Equals(a.RelPath, "extra/extra.bin", StringComparison.OrdinalIgnoreCase)),
            "追加同梱フォルダ \"extra/\" の中身が入っていない");
    }
}
