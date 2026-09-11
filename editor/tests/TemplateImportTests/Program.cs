// ============================================================
//  Program.cs — テンプレートライブラリ / インポートの単体テスト
//
//  実行: dotnet run --project editor/tests/TemplateImportTests
//
//  【検証範囲】
//   1. TemplateLibrary            : カテゴリとエントリの列挙（粒度・表示名・サイズ・隠し要素）
//   2. AssetCollector.CollectFrom : 指定した起点だけからの閉包（既定の起点を積まないこと）
//   3. TemplateImporter           : 計画（衝突検出）と実行（スキップ / 上書き）
//   4. TemplateLibraryLocator     : 環境変数による置き場所の上書き
//   5. ByteSizeText               : 表示用のバイト数整形
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SEEDEditor.Packaging.Collect;
using SEEDEditor.Templates;
using SpriteRigTests;

namespace SEEDEditor.Tests.TemplateImport;

/// <summary>テストの登録と実行を行うエントリポイント。</summary>
public static class Program
{
    /// <summary>ドライランで一覧表示するエントリの最大件数（カテゴリごと）。</summary>
    private const int DryRunEntryListLimit = 8;

    /// <summary>ドライランで一覧表示する衝突・欠落の最大件数。</summary>
    private const int DryRunIssueListLimit = 10;

    /// <summary>
    /// テストを登録して実行する。
    ///
    /// 引数に「ライブラリルート [コピー先アセットルート] [エントリ相対パス...]」を渡すと、
    /// テストの代わりに実ライブラリへのドライラン
    /// （カテゴリ一覧、指定エントリのコピー計画の要約）を行う。ファイルは書き込まない。
    /// </summary>
    /// <param name="args">コマンドライン引数。</param>
    /// <returns>プロセス終了コード（全成功なら 0）。</returns>
    public static int Main(string[] args)
    {
        if (args.Length > 0) return DryRun(args);

        Console.WriteLine("TemplateImportTests");
        var h = new TestHarness();

        RegisterLibraryTests(h);
        RegisterCollectFromTests(h);
        RegisterImporterTests(h);
        RegisterLocatorTests(h);
        RegisterFormatTests(h);

        return h.Run();
    }

    // ============================================================
    //  0. ドライラン（実ライブラリの中身と計画を確認する）
    // ============================================================

    /// <summary>
    /// 実ライブラリを走査して一覧を表示し、エントリを指定した場合はコピー計画の要約を出す。
    /// コピーは一切行わないので、実データに対して安全に実行できる。
    /// </summary>
    /// <param name="args">
    /// args[0] = ライブラリルート、args[1] = コピー先アセットルート（省略可）、
    /// args[2..] = 計画を作るエントリの相対パス。
    /// </param>
    /// <returns>プロセス終了コード。</returns>
    private static int DryRun(string[] args)
    {
        var libraryRoot = args[0];
        if (!Directory.Exists(libraryRoot))
        {
            Console.WriteLine($"ライブラリが見つかりません: {libraryRoot}");
            return 1;
        }

        // ── 走査して一覧を出す ─────────────────────────────────
        var watch   = System.Diagnostics.Stopwatch.StartNew();
        var library = TemplateLibrary.Load(libraryRoot);
        watch.Stop();

        Console.WriteLine($"ライブラリ: {library.Root}");
        Console.WriteLine($"走査時間: {watch.Elapsed.TotalSeconds:F2} 秒");
        Console.WriteLine();
        foreach (var category in library.Categories)
        {
            Console.WriteLine(
                $"[{category.DisplayName}] {category.FolderName} — " +
                $"{category.Entries.Count} 件 / {category.TotalFileCount} ファイル / " +
                $"{ByteSizeText.Format(category.TotalSizeBytes)}");
            foreach (var entry in category.Entries.Take(DryRunEntryListLimit))
                Console.WriteLine(
                    $"    {(entry.IsFolder ? "[F]" : "   ")} {entry.RelPath}  " +
                    $"({entry.FileCount} ファイル / {ByteSizeText.Format(entry.SizeBytes)})");
            if (category.Entries.Count > DryRunEntryListLimit)
                Console.WriteLine($"    …ほか {category.Entries.Count - DryRunEntryListLimit} 件");
        }

        if (args.Length < 3) return 0;

        // ── 指定エントリのコピー計画を出す ─────────────────────
        var assetsRoot = args[1];
        var entries    = args.Skip(2).ToArray();

        Console.WriteLine();
        Console.WriteLine($"コピー先: {assetsRoot}");
        Console.WriteLine($"選択エントリ: {string.Join(" , ", entries)}");
        Console.WriteLine();

        var plan = TemplateImporter.CreatePlan(libraryRoot, assetsRoot, entries, Console.WriteLine);

        Console.WriteLine();
        Console.WriteLine($"コピー対象: {plan.FileCount} ファイル / {ByteSizeText.Format(plan.TotalBytes)}");
        foreach (var item in plan.Items.Take(DryRunIssueListLimit))
            Console.WriteLine($"    {item.RelPath} ({ByteSizeText.Format(item.SizeBytes)})" +
                              (item.ConflictsWithExisting ? "  ← 既存あり" : ""));
        if (plan.FileCount > DryRunIssueListLimit)
            Console.WriteLine($"    …ほか {plan.FileCount - DryRunIssueListLimit} 件");

        Console.WriteLine();
        Console.WriteLine($"既存ファイルと重複: {plan.ConflictCount} 件");
        foreach (var p in plan.ConflictPaths.Take(DryRunIssueListLimit)) Console.WriteLine($"    {p}");

        Console.WriteLine();
        Console.WriteLine($"解決できなかった参照: {plan.MissingReferences.Count} 件");
        foreach (var m in plan.MissingReferences.Take(DryRunIssueListLimit))
            Console.WriteLine($"    {m.ReferencePath}  ← {m.SourceRelPath}");

        return 0;
    }

    // ============================================================
    //  1. ライブラリの走査
    // ============================================================

    /// <summary>TemplateLibrary のテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterLibraryTests(TestHarness h)
    {
        h.Add("カテゴリはトップレベルフォルダで、表示名の対応表の順に並ぶ", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var library  = TemplateLibrary.Load(fx.LibraryRoot);

            var folders = library.Categories.Select(c => c.FolderName).ToList();
            Check.True(folders.Contains("scenes"), "scenes カテゴリが無い");
            Check.True(folders.Contains("actors"), "actors カテゴリが無い");
            Check.True(folders.Contains("fonts"),  "fonts カテゴリが無い");

            // 対応表の並び（scenes → actors → textures → fonts）が保たれていること
            Check.True(folders.IndexOf("scenes") < folders.IndexOf("actors"),
                       "scenes は actors より前に出る");
            Check.True(folders.IndexOf("textures") < folders.IndexOf("fonts"),
                       "textures は fonts より前に出る");
            // 未知カテゴリ（対応表に無いフォルダ）は既知カテゴリすべての後ろへ回る
            Check.Equal(TemplateLibraryFixture.UnknownCategoryFolder, folders[^1],
                        "未知カテゴリは末尾に出る");

            var scenes = library.Categories.First(c => c.FolderName == "scenes");
            Check.Equal("シーン", scenes.DisplayName, "既知カテゴリの表示名");
        });

        h.Add("未知カテゴリはフォルダ名がそのまま表示名になる", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var library  = TemplateLibrary.Load(fx.LibraryRoot);

            var unknown = library.Categories
                .First(c => c.FolderName == TemplateLibraryFixture.UnknownCategoryFolder);
            Check.Equal(TemplateLibraryFixture.UnknownCategoryFolder, unknown.DisplayName,
                        "未知カテゴリの表示名はフォルダ名そのもの");
        });

        h.Add("エントリの粒度はカテゴリ直下の子（ファイルとフォルダ）", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var library  = TemplateLibrary.Load(fx.LibraryRoot);

            var scenes = library.Categories.First(c => c.FolderName == "scenes");
            var paths  = scenes.Entries.Select(e => e.RelPath).ToList();
            Check.True(paths.Contains(TemplateLibraryFixture.SceneEntry), "シーンのファイルエントリが無い");
            Check.True(paths.Contains(TemplateLibraryFixture.EmptySceneEntry), "2 つ目のシーンが無い");

            var fonts = library.Categories.First(c => c.FolderName == "fonts");
            var digital = fonts.Entries.First(e => e.RelPath == TemplateLibraryFixture.FontFolderEntry);
            Check.True(digital.IsFolder, "fonts/Digital はフォルダエントリ");
            Check.Equal(TemplateLibraryFixture.FontFolderFileCount, digital.FileCount,
                        "フォルダエントリのファイル数は配下の合計");
            Check.True(digital.SizeBytes > 0, "フォルダエントリのサイズは配下の合計");

            // 配下のファイルが単独のエントリとして現れてはいけない（粒度は直下の子まで）
            Check.True(fonts.Entries.All(e => e.RelPath != TemplateLibraryFixture.FontFilePath),
                       "フォルダ配下のファイルは個別エントリにしない");
        });

        h.Add("隠しフォルダ・隠しファイルは一覧に出ない", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var library  = TemplateLibrary.Load(fx.LibraryRoot);

            Check.True(library.Categories.All(c => c.FolderName != ".backup"),
                       "隠しフォルダがカテゴリとして出ている");

            var scenes = library.Categories.First(c => c.FolderName == "scenes");
            Check.True(scenes.Entries.All(e => !e.DisplayName.StartsWith('.')),
                       "隠しファイルがエントリとして出ている");
        });

        h.Add("存在しないライブラリは空の一覧になる（例外にしない）", () =>
        {
            var library = TemplateLibrary.Load(
                Path.Combine(Path.GetTempPath(), "seed_no_such_library_" + Guid.NewGuid().ToString("N")));
            Check.Equal(0, library.Categories.Count, "カテゴリ数");
            Check.True(library.IsEmpty, "空判定");
        });

        h.Add("相対パスからエントリを引ける", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var library  = TemplateLibrary.Load(fx.LibraryRoot);

            Check.True(library.TryGetEntry(TemplateLibraryFixture.SceneEntry, out var entry),
                       "登録済みの相対パスを引けない");
            Check.Equal("demo.scene", entry.DisplayName, "エントリの表示名");
            Check.True(!library.TryGetEntry("scenes/no_such.scene", out _),
                       "存在しない相対パスで true を返している");
        });
    }

    // ============================================================
    //  2. 指定した起点からの閉包
    // ============================================================

    /// <summary>AssetCollector.CollectFrom のテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterCollectFromTests(TestHarness h)
    {
        h.Add("指定した起点から参照を多段で辿る", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var included = CollectFrom(fx, [TemplateLibraryFixture.SceneEntry]);

            Check.True(included.Contains(TemplateLibraryFixture.SceneEntry),  "起点のシーンが入っていない");
            Check.True(included.Contains(TemplateLibraryFixture.ActorPath),   "1 段目（アクタ）が入っていない");
            Check.True(included.Contains(TemplateLibraryFixture.RockTexturePath), "1 段目（テクスチャ）が入っていない");
            Check.True(included.Contains(TemplateLibraryFixture.HeroTexturePath), "2 段目（アクタ経由）が入っていない");
        });

        h.Add("既定の起点（未参照ファイル・他のシーン）は積まない", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var included = CollectFrom(fx, [TemplateLibraryFixture.SceneEntry]);

            Check.True(!included.Contains(TemplateLibraryFixture.UnreferencedPath),
                       "未参照ファイルまで収録している（Collect の起点が混ざっている）");
            Check.True(!included.Contains(TemplateLibraryFixture.EmptySceneEntry),
                       "指定していないシーンまで収録している");
            Check.True(!included.Contains(TemplateLibraryFixture.FontFilePath),
                       "指定していないフォルダの中身まで収録している");
        });

        h.Add("フォルダを起点に渡すと配下を丸ごと取り込む", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var included = CollectFrom(fx, [TemplateLibraryFixture.FontFolderEntry]);

            Check.True(included.Contains(TemplateLibraryFixture.FontFilePath),
                       "フォルダ直下のファイルが入っていない");
            Check.True(included.Contains(TemplateLibraryFixture.FontNestedFilePath),
                       "フォルダの入れ子まで辿れていない");
            Check.Equal(TemplateLibraryFixture.FontFolderFileCount, included.Count,
                        "フォルダ起点の収録件数");
        });

        h.Add("起点が空なら収録も空になる", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var included = CollectFrom(fx, []);
            Check.Equal(0, included.Count, "収録件数");
        });

        h.Add("実在しない起点は欠落として報告する", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var collector = new AssetCollector(fx.LibraryRoot, new AssetPackagingSettings());
            var result    = collector.CollectFrom(["scenes/no_such.scene"]);

            Check.Equal(0, result.Included.Count, "収録件数");
            Check.Equal(1, result.MissingReferences.Count, "欠落件数");
            Check.Equal(AssetCollector.SeedSourceLabel, result.MissingReferences[0].SourceRelPath,
                        "欠落の参照元ラベル");
        });

        h.Add("ライブラリ内で解決できない参照を欠落として報告する", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var collector = new AssetCollector(fx.LibraryRoot, new AssetPackagingSettings());
            var result    = collector.CollectFrom([TemplateLibraryFixture.SceneEntry]);

            var missing = result.MissingReferences
                .FirstOrDefault(m => m.ReferencePath == TemplateLibraryFixture.MissingTexturePath);
            Check.Equal(TemplateLibraryFixture.MissingTexturePath, missing.ReferencePath,
                        "参照切れが報告されていない");
            Check.Equal(TemplateLibraryFixture.SceneEntry, missing.SourceRelPath, "欠落の参照元");
        });
    }

    /// <summary>指定した起点から閉包を取り、収録された相対パスの集合を返す。</summary>
    /// <param name="fx">フィクスチャ。</param>
    /// <param name="seeds">起点の相対パス。</param>
    /// <returns>収録された相対パス。</returns>
    private static List<string> CollectFrom(TemplateLibraryFixture fx, string[] seeds)
    {
        var collector = new AssetCollector(fx.LibraryRoot, new AssetPackagingSettings());
        return collector.CollectFrom(seeds).Included.Select(a => a.RelPath).ToList();
    }

    // ============================================================
    //  3. インポートの計画と実行
    // ============================================================

    /// <summary>TemplateImporter のテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterImporterTests(TestHarness h)
    {
        h.Add("計画は依存を含み、コピー先は templates 接頭辞を付けない", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var plan = TemplateImporter.CreatePlan(
                fx.LibraryRoot, fx.AssetsRoot, [TemplateLibraryFixture.SceneEntry]);

            var paths = plan.Items.Select(i => i.RelPath).ToList();
            Check.True(paths.Contains(TemplateLibraryFixture.HeroTexturePath), "依存が計画に入っていない");
            Check.True(paths.All(p => !p.StartsWith("templates/", StringComparison.OrdinalIgnoreCase)),
                       "コピー先の相対パスに templates/ が付いている");
            Check.Equal(0, plan.ConflictCount, "まっさらなコピー先なら衝突は 0");
            Check.True(plan.TotalBytes > 0, "合計サイズが 0 になっている");
        });

        h.Add("計画は既存ファイルとの衝突を検出する", () =>
        {
            using var fx = new TemplateLibraryFixture();
            fx.WriteAssetText(TemplateLibraryFixture.RockTexturePath, "既存の中身");

            var plan = TemplateImporter.CreatePlan(
                fx.LibraryRoot, fx.AssetsRoot, [TemplateLibraryFixture.SceneEntry]);

            Check.Equal(1, plan.ConflictCount, "衝突件数");
            Check.Equal(TemplateLibraryFixture.RockTexturePath, plan.ConflictPaths[0], "衝突したパス");
        });

        h.Add("実行すると依存ごとコピーされ、入れ子フォルダも作られる", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var plan = TemplateImporter.CreatePlan(
                fx.LibraryRoot, fx.AssetsRoot,
                [TemplateLibraryFixture.SceneEntry, TemplateLibraryFixture.FontFolderEntry]);
            var result = TemplateImporter.Execute(plan, TemplateImportConflictPolicy.Skip);

            Check.Equal(plan.FileCount, result.CopiedCount, "コピー件数は計画どおり");
            Check.Equal(0, result.Failures.Count, "コピー失敗が出ている");
            Check.True(fx.AssetExists(TemplateLibraryFixture.SceneEntry),      "シーンがコピーされていない");
            Check.True(fx.AssetExists(TemplateLibraryFixture.HeroTexturePath), "依存がコピーされていない");
            Check.True(fx.AssetExists(TemplateLibraryFixture.FontNestedFilePath),
                       "入れ子フォルダのファイルがコピーされていない");
            Check.True(!fx.AssetExists(TemplateLibraryFixture.UnreferencedPath),
                       "選んでいないファイルまでコピーされている");
        });

        h.Add("スキップ方針は既存ファイルを残す", () =>
        {
            using var fx = new TemplateLibraryFixture();
            const string existingText = "既存の中身";
            fx.WriteAssetText(TemplateLibraryFixture.RockTexturePath, existingText);

            var plan   = TemplateImporter.CreatePlan(
                fx.LibraryRoot, fx.AssetsRoot, [TemplateLibraryFixture.SceneEntry]);
            var result = TemplateImporter.Execute(plan, TemplateImportConflictPolicy.Skip);

            Check.Equal(1, result.SkippedCount, "スキップ件数");
            Check.Equal(0, result.OverwrittenCount, "上書き件数");
            Check.Equal(plan.FileCount - 1, result.CopiedCount, "コピー件数");
            Check.Equal(existingText, fx.ReadAssetText(TemplateLibraryFixture.RockTexturePath),
                        "既存ファイルが書き換えられている");
        });

        h.Add("上書き方針は既存ファイルを置き換える", () =>
        {
            using var fx = new TemplateLibraryFixture();
            fx.WriteAssetText(TemplateLibraryFixture.RockTexturePath, "既存の中身");

            var plan   = TemplateImporter.CreatePlan(
                fx.LibraryRoot, fx.AssetsRoot, [TemplateLibraryFixture.SceneEntry]);
            var result = TemplateImporter.Execute(plan, TemplateImportConflictPolicy.Overwrite);

            Check.Equal(0, result.SkippedCount, "スキップ件数");
            Check.Equal(1, result.OverwrittenCount, "上書き件数");
            Check.Equal(plan.FileCount, result.CopiedCount, "コピー件数");

            var libraryBytes = File.ReadAllBytes(Path.Combine(
                fx.LibraryRoot,
                TemplateLibraryFixture.RockTexturePath.Replace('/', Path.DirectorySeparatorChar)));
            var assetBytes = File.ReadAllBytes(Path.Combine(
                fx.AssetsRoot,
                TemplateLibraryFixture.RockTexturePath.Replace('/', Path.DirectorySeparatorChar)));
            Check.True(libraryBytes.SequenceEqual(assetBytes), "上書き後の中身がライブラリと一致しない");
        });

        h.Add("計画は欠落参照を引き継ぎ、結果にも残る", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var plan = TemplateImporter.CreatePlan(
                fx.LibraryRoot, fx.AssetsRoot, [TemplateLibraryFixture.SceneEntry]);
            var result = TemplateImporter.Execute(plan, TemplateImportConflictPolicy.Skip);

            Check.True(plan.MissingReferences.Count > 0, "計画に欠落参照が無い");
            Check.Equal(plan.MissingReferences.Count, result.MissingReferences.Count,
                        "結果に欠落参照が引き継がれていない");
        });

        h.Add("コピー元とコピー先が同じなら何もしない", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var plan = TemplateImporter.CreatePlan(
                fx.LibraryRoot, fx.LibraryRoot, [TemplateLibraryFixture.SceneEntry]);
            var result = TemplateImporter.Execute(plan, TemplateImportConflictPolicy.Overwrite);

            Check.Equal(0, result.CopiedCount, "自分自身へコピーしている");
            Check.Equal(0, result.Failures.Count, "失敗として扱われている");
        });
    }

    // ============================================================
    //  4. 置き場所の解決
    // ============================================================

    /// <summary>TemplateLibraryLocator のテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterLocatorTests(TestHarness h)
    {
        h.Add("環境変数でライブラリの場所を上書きできる", () =>
        {
            using var fx = new TemplateLibraryFixture();
            var saved = Environment.GetEnvironmentVariable(TemplateLibraryLocator.OverrideEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(
                    TemplateLibraryLocator.OverrideEnvVar, fx.LibraryRoot);
                Check.Equal(Path.GetFullPath(fx.LibraryRoot), TemplateLibraryLocator.Resolve(),
                            "環境変数の指定が使われていない");

                // 実在しないパスを指定したら、環境変数は無視して次の候補へ進む
                Environment.SetEnvironmentVariable(
                    TemplateLibraryLocator.OverrideEnvVar,
                    Path.Combine(fx.LibraryRoot, "no_such_dir"));
                Check.True(TemplateLibraryLocator.Resolve() != Path.Combine(fx.LibraryRoot, "no_such_dir"),
                           "実在しない指定を採用している");
            }
            finally
            {
                Environment.SetEnvironmentVariable(TemplateLibraryLocator.OverrideEnvVar, saved);
            }
        });
    }

    // ============================================================
    //  5. 表示整形
    // ============================================================

    /// <summary>ByteSizeText のテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterFormatTests(TestHarness h)
    {
        h.Add("バイト数は単位付きで整形される", () =>
        {
            Check.Equal("0 B",     ByteSizeText.Format(0),              "0 バイト");
            Check.Equal("512 B",   ByteSizeText.Format(512),            "1 KB 未満");
            Check.Equal("1.0 KB",  ByteSizeText.Format(1024),           "ちょうど 1 KB");
            Check.Equal("1.5 MB",  ByteSizeText.Format(1024 * 1536),    "MB 表記");
            Check.Equal("2.0 GB",  ByteSizeText.Format(2L * 1024 * 1024 * 1024), "GB 表記");
        });
    }
}
