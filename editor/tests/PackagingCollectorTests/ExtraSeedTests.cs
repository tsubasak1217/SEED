// ============================================================
//  ExtraSeedTests.cs — 収録の「追加の起点」（段階C-4）の単体テスト
//
//  【検証範囲】
//   - AssetCollector.Collect(extraSeeds) … 既定の起点（project_settings.json・登録シーンほか）に足した起点から参照をたどる。
//                                          既定の結果は 1 つも減らさない・空なら従来と同じ・無い起点は欠落として報告
//   - AssetPakBuilder.Collect の extraSeeds … SeedPak の --extra-scene の経路（パッケージ化ウィンドウは渡さない）
//   - SeedPakArguments の --extra-scene   … 繰り返し・値の要否・--scripts-only との併用の誤り
//  Android の実行で、シーンマネージャに登録していない開いているシーンを APK の pak に入れるために足した口
//  （docs/android.md §20.10・docs/packaging.md §10.2）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SEEDEditor.Packaging;
using SEEDEditor.Packaging.Collect;
using SEEDEditor.Packaging.Pak;
using SEEDEditor.Tools.SeedPak;
using SpriteRigTests;

namespace SEEDEditor.Tests.PackagingCollector;

/// <summary>追加の起点のテスト。</summary>
public static class ExtraSeedTests
{
    /// <summary>登録もされず、どこからも参照されないシーン（追加の起点にする）。</summary>
    private const string ExtraScene = "scenes/extra.scene";

    /// <summary>追加の起点のシーンからだけ参照される画像。</summary>
    private const string ExtraOnlyTexture = "fx/extra_only.png";

    /// <summary>追加の起点のシーンが参照する、既定の起点からも辿れるモデル（増えないことの確認用）。</summary>
    private const string SharedModel = "models/box.glb";

    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("追加の起点: 未登録のシーンとその参照先が入り、既定の起点の結果は 1 つも減らない", AddsUnregisteredSceneAndReferences);
        h.Add("追加の起点: assets://・\\ 区切り・./・アセットルート内の絶対パス・大小文字違いも同じシーン（収録パスはディスクの表記）", AcceptsSceneSpellings);
        h.Add("追加の起点: 空・登録済みのシーンなら Collect() と同じ PAK（パッケージ化ウィンドウの挙動は不変）", EmptyOrRegisteredKeepsPak);
        h.Add("追加の起点: 無いシーン・アセットルートの外は欠落（参照元 = 指定された起点）として報告して飛ばす", ReportsMissingExtraSeeds);
        h.Add("SeedPak の引数: --extra-scene は繰り返せる・値が要る・--scripts-only とは併用できない・既定は空", ParsesExtraSceneOption);
    }

    // ============================================================
    //  テスト本体
    // ============================================================

    /// <summary>未登録のシーンと参照先が入る。</summary>
    private static void AddsUnregisteredSceneAndReferences()
    {
        using var fx = NewFixture();
        var settings = new AssetPackagingSettings();
        var baseline = new AssetCollector(fx.Root, settings).Collect();
        var log = new List<string>();
        var viaBuilder = AssetPakBuilder.Collect(fx.Root, settings, runtimeSourceRoot: null, log.Add, new[] { ExtraScene });
        var direct = new AssetCollector(fx.Root, settings).Collect(new[] { ExtraScene });

        var basePaths = PathSet(baseline);
        var extraPaths = PathSet(viaBuilder);
        Check.True(!basePaths.Contains(ExtraScene) && !basePaths.Contains(ExtraOnlyTexture), "既定の起点からは辿れない（未登録・未参照）");
        Check.True(basePaths.Contains(SharedModel), "共有のモデルは既定の起点からも入る");
        Check.True(extraPaths.Contains(ExtraScene), "追加したシーン自身が入る");
        Check.True(extraPaths.Contains(ExtraOnlyTexture), "そのシーンから参照をたどったものも入る");
        Check.True(basePaths.IsSubsetOf(extraPaths), "既定の起点の結果は 1 つも減らない");
        Check.Equal(baseline.Included.Count + 2, viaBuilder.Included.Count, "増えるのはシーンと画像の 2 ファイルだけ（共有のモデルは重複しない）");
        Check.True(PathSet(direct).SetEquals(extraPaths), "AssetPakBuilder の extraSeeds は AssetCollector.Collect(extraSeeds) と同じ");
        Check.True(log.Contains($"追加の起点: {ExtraScene}"), $"ログに追加の起点の行: {string.Join(" / ", log.Where(l => l.Contains("起点")))}");
        Check.True(!viaBuilder.MissingReferences.Any(m => m.SourceRelPath == AssetCollector.SeedSourceLabel), "実在する起点は欠落にしない");
    }

    /// <summary>書き方の違い。</summary>
    private static void AcceptsSceneSpellings()
    {
        using var fx = NewFixture();
        var settings = new AssetPackagingSettings();
        var spellings = new[]
        {
            "assets://scenes/extra.scene",
            "scenes\\extra.scene",
            "./scenes/extra.scene",
            Path.Combine(fx.Root, "scenes", "extra.scene"),
            "Scenes/EXTRA.scene",
        };
        foreach (var spelling in spellings)
        {
            var paths = new AssetCollector(fx.Root, settings).Collect(new[] { spelling }).Included.Select(a => a.RelPath).ToList();
            Check.True(paths.Contains(ExtraScene, StringComparer.Ordinal), $"{spelling} → 収録パスはディスクの表記 {ExtraScene}");
            Check.True(paths.Contains(ExtraOnlyTexture, StringComparer.Ordinal), $"{spelling} → 参照先も入る");
        }
    }

    /// <summary>空・登録済みなら従来と同じ PAK。</summary>
    private static void EmptyOrRegisteredKeepsPak()
    {
        using var fx = NewFixture();
        var settings = new AssetPackagingSettings();

        // 収集を先に全部済ませる（書き出した PAK がアセットルートの索引に混ざらないように）
        var plain = AssetPakBuilder.Collect(fx.Root, settings, null, null);
        var empty = AssetPakBuilder.Collect(fx.Root, settings, null, null, Array.Empty<string>());
        var registered = AssetPakBuilder.Collect(fx.Root, settings, null, null, new[] { "assets://scenes/main.scene" });

        var outRoot = Path.Combine(Path.GetTempPath(), "seed_pak_extra_" + Guid.NewGuid().ToString("N"));
        try
        {
            var plainBytes = WritePak(outRoot, "plain", fx.Root, plain);
            Check.True(plainBytes.SequenceEqual(WritePak(outRoot, "empty", fx.Root, empty)), "空の追加は Collect() と 1 バイトも違わない");
            Check.True(plainBytes.SequenceEqual(WritePak(outRoot, "registered", fx.Root, registered)), "登録済みのシーンを足しても同じ PAK");
        }
        finally
        {
            try { Directory.Delete(outRoot, recursive: true); }
            catch { /* 掃除に失敗してもテスト結果には影響させない */ }
        }
    }

    /// <summary>無い起点・アセットルートの外。</summary>
    private static void ReportsMissingExtraSeeds()
    {
        using var fx = NewFixture();
        var settings = new AssetPackagingSettings();
        var baseline = new AssetCollector(fx.Root, settings).Collect();
        var outside = Path.Combine(Path.GetTempPath(), "seed_elsewhere_" + Guid.NewGuid().ToString("N"), "x.scene");
        var log = new List<string>();
        var result = new AssetCollector(fx.Root, settings, null, log.Add).Collect(new[] { "scenes/nope.scene", outside, "   " });

        var missing = result.MissingReferences.Where(m => m.SourceRelPath == AssetCollector.SeedSourceLabel).ToList();
        Check.True(missing.Any(m => m.ReferencePath == "scenes/nope.scene"), "無いシーンは欠落として報告");
        Check.True(missing.Any(m => m.RawText == outside), "アセットルートの外も欠落として報告（生の指定のまま）");
        Check.Equal(2, missing.Count, "空白だけの指定は無視する");
        Check.True(log.Any(l => l.StartsWith("⚠ 追加の起点の実体がありません", StringComparison.Ordinal)), "ログに警告");
        Check.True(PathSet(result).SetEquals(PathSet(baseline)), "収録は既定の起点の結果のまま（飛ばすだけ）");
    }

    /// <summary>SeedPak の引数。</summary>
    private static void ParsesExtraSceneOption()
    {
        var none = SeedPakArguments.Parse(new[] { "--project", "P", "--out", "O" });
        Check.True(none.Error is null && none.Options!.ExtraScenes.Count == 0, "既定は空（従来どおり）");

        var two = SeedPakArguments.Parse(new[]
        {
            "--project", "P", "--out", "O", "--scripts", "--extra-scene", "scenes/日本語 シーン.scene", "--extra-scene", "assets://scenes/B.scene",
        });
        Check.True(two.Error is null, $"解釈できる: {two.Error}");
        Check.Equal("scenes/日本語 シーン.scene|assets://scenes/B.scene", string.Join("|", two.Options!.ExtraScenes), "繰り返し指定を順に積む");
        Check.Equal(SeedPakContent.PakAndScripts, two.Options.Content, "--scripts と併用できる");

        var noValue = SeedPakArguments.Parse(new[] { "--project", "P", "--out", "O", "--extra-scene" });
        Check.True(noValue.Error is not null && noValue.Error.Contains("--extra-scene"), $"値が要る: {noValue.Error}");

        var scriptsOnly = SeedPakArguments.Parse(new[] { "--project", "P", "--out", "O", "--scripts-only", "--extra-scene", "scenes/A.scene" });
        Check.True(scriptsOnly.Error is not null && scriptsOnly.Error.Contains("--scripts-only"), $"PAK を作らない指定とは併用できない: {scriptsOnly.Error}");
        Check.Equal("--extra-scene", SeedPakArguments.ExtraSceneOption, "引数名（SeedAndroid の SeedPakProcess.ExtraSceneOption と一致させる）");
    }

    // ============================================================
    //  ヘルパー
    // ============================================================

    /// <summary>既定のフィクスチャに、登録されていないシーンとその参照先を書き足したものを作る。</summary>
    /// <returns>フィクスチャ。</returns>
    private static AssetFixture NewFixture()
    {
        var fx = new AssetFixture();
        // どこからも参照されない（登録もされていない）シーン。既定の起点からは辿れない画像と、既に収録されるモデルを参照する
        fx.WriteText(ExtraScene, """
        {
          "texture": "assets://fx/extra_only.png",
          "model":   "assets://models/box.glb"
        }
        """);
        fx.WriteBinary(ExtraOnlyTexture, 8);
        return fx;
    }

    /// <summary>収録された相対パスの集合（大文字小文字は無視）。</summary>
    /// <param name="result">収集結果。</param>
    /// <returns>相対パスの集合。</returns>
    private static HashSet<string> PathSet(AssetCollectionResult result) =>
        new(result.Included.Select(a => a.RelPath), StringComparer.OrdinalIgnoreCase);

    /// <summary>PAK を一時フォルダへ書き出し、バイト列を返す。</summary>
    /// <param name="outRoot">出力の親フォルダ。</param>
    /// <param name="name">出力フォルダ名。</param>
    /// <param name="assetsRoot">アセットルート。</param>
    /// <param name="result">収集結果。</param>
    /// <returns>PAK のバイト列。</returns>
    private static byte[] WritePak(string outRoot, string name, string assetsRoot, AssetCollectionResult result)
    {
        var pakPath = PackageLayout.PakPath(Path.Combine(outRoot, name));
        AssetPakBuilder.Write(pakPath, assetsRoot, result, log: null, progress: null);
        return File.ReadAllBytes(pakPath);
    }
}
