// ============================================================
//  Program.cs — パッケージ化のアセット収集 / PAK 書き出しの単体テスト
//
//  実行: dotnet run --project editor/tests/PackagingCollectorTests
//
//  【検証範囲】
//   1. AssetReferenceScanner : 参照抽出の 4 系統
//   2. AssetCollector        : 閉包 / 同伴ファイル / 除外と参照優先 / 欠落検出
//   3. PakWriter             : PAK バイナリの往復（pak.rs と同じ読み方で確認）
//   4. AssetPathRewriter     : 絶対パス 4 形式の書き換え
//   5. DotnetRuntimeBundler  : 同梱する .NET の選択と出力先レイアウト
//   6. PackageLayout         : 配布物のフォルダ構成と旧レイアウトの後始末の判定
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SEEDEditor.Packaging;
using SEEDEditor.Packaging.Collect;
using SEEDEditor.Packaging.Pak;
using SEEDEditor.Packaging.Runtime;
using SpriteRigTests;

namespace SEEDEditor.Tests.PackagingCollector;

/// <summary>テストの登録と実行を行うエントリポイント。</summary>
public static class Program
{
    /// <summary>テスト用に使う架空のアセットルート（ファイルを触らない走査テスト用）。</summary>
    private const string FakeRoot = @"C:\proj\runtime\assets";

    /// <summary>ドライラン時に一覧表示する欠落参照の最大件数。</summary>
    private const int DryRunMissingListLimit = 40;

    /// <summary>ドライラン時に一覧表示する収録上位フォルダの最大件数。</summary>
    private const int DryRunFolderListLimit = 15;

    /// <summary>バイト数を MB へ直すための除数。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <summary>
    /// テストを登録して実行する。
    ///
    /// 引数に「アセットルート [runtime/src] [--write-pak &lt;出力先&gt;]」を渡すと、
    /// テストの代わりに実プロジェクトへのドライラン
    /// （収録件数・サイズ・欠落参照の表示。--write-pak 指定時は PAK も書き出す）を行う。
    /// </summary>
    /// <param name="args">コマンドライン引数。</param>
    /// <returns>プロセス終了コード（全成功なら 0）。</returns>
    public static int Main(string[] args)
    {
        // .NET ランタイムの同梱だけを行うモード（アセットルートを必要としないので先に判定する）
        if (args.Length > 0 && args[0] == BundleDotnetOption) return BundleDotnet(args);

        if (args.Length > 0) return DryRun(args);

        Console.WriteLine("PackagingCollectorTests");
        var h = new TestHarness();

        RegisterScannerTests(h);
        RegisterCollectorTests(h);
        RegisterPakTests(h);
        RegisterRuleTests(h);
        RegisterDotnetBundlerTests(h);

        return h.Run();
    }

    // ============================================================
    //  0-b. .NET ランタイムの同梱（UI を使わない実行）
    // ============================================================

    /// <summary>.NET ランタイムの同梱だけを実行するオプション名。</summary>
    private const string BundleDotnetOption = "--bundle-dotnet";

    /// <summary>
    /// パッケージ出力フォルダに対して .NET ランタイムの同梱だけを実行する。
    ///
    /// <para>
    /// エディタ UI を起動せずにパッケージ相当の配置を作るための入口で、
    /// <c>--write-pak</c> と同じくパッケージ版の実機確認に使う。
    /// 必要な .NET のバージョンは出力フォルダの
    /// <c>bin/SEEDScripting.runtimeconfig.json</c> から読むので、
    /// 先にスクリプト同梱（ScriptPrecompileTests）を済ませておくこと。
    /// 引数はゲーム出力フォルダ（exe と同じ場所）で、同梱先は
    /// その下の <c>bin/dotnet/</c> になる。
    /// </para>
    /// </summary>
    /// <param name="args">コマンドライン引数。args[1] が出力フォルダ。</param>
    /// <returns>プロセス終了コード。</returns>
    private static int BundleDotnet(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine($"{BundleDotnetOption} には出力フォルダが必要です");
            return 1;
        }

        var outDir = args[1];
        if (!Directory.Exists(outDir))
        {
            Console.WriteLine($"出力フォルダが見つかりません: {outDir}");
            return 1;
        }

        Console.WriteLine($".NET ランタイムの同梱: {outDir}");
        var result = DotnetRuntimeBundler.Run(outDir, Console.WriteLine);

        if (result.Bundled) return 0;

        Console.WriteLine($"⚠ {result.SkipReason}");
        return 1;
    }

    // ============================================================
    //  0. ドライラン（実プロジェクトの収録内容を確認する）
    // ============================================================

    /// <summary>PAK の書き出し先を指定するオプション名。</summary>
    private const string WritePakOption = "--write-pak";

    /// <summary>収録ファイル一覧の書き出し先を指定するオプション名。</summary>
    private const string ListIncludedOption = "--list-included";

    /// <summary>
    /// 実プロジェクトのアセットルートに対して収集を行い、結果を表示する。
    /// 既定では PAK を書かないので、パッケージ化の前に
    /// 「何が入って何が落ちるか」だけを確認できる。
    ///
    /// <para>
    /// <c>--write-pak &lt;出力先&gt;</c> を付けると、収集した内容で本物の assets.pak を書き出す。
    /// エディタ UI を起動せずにパッケージ相当のアセットを用意するための入口で、
    /// パッケージ版の動作確認（実機確認）に使う。
    /// </para>
    /// <para>
    /// <c>--list-included &lt;出力ファイル&gt;</c> を付けると、収録が決まったファイルの
    /// アセットルート相対パスを 1 行 1 件で書き出す。既存ゲームをプロジェクト形式へ移すとき、
    /// 「templates/ 配下のうち実際に参照されている物はどれか」を機械的に洗い出すのに使う
    /// （grep で絞り込める形にしてある。docs/template_library.md を参照）。
    /// </para>
    /// </summary>
    /// <param name="args">
    /// コマンドライン引数。args[0] がアセットルート。
    /// 続く位置引数は runtime/src（省略可）、
    /// "--write-pak &lt;パス&gt;" と "--list-included &lt;パス&gt;" は任意の位置に置ける。
    /// </param>
    /// <returns>プロセス終了コード。</returns>
    private static int DryRun(string[] args)
    {
        var assetsRoot = args[0];

        // 位置引数（runtime/src）とオプション（--write-pak / --list-included）を分けて読む
        string? runtimeSourceRoot = null;
        string? pakOutputPath     = null;
        string? listOutputPath    = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == WritePakOption)
            {
                if (i + 1 >= args.Length)
                {
                    Console.WriteLine($"{WritePakOption} には出力先パスが必要です");
                    return 1;
                }
                pakOutputPath = args[++i];
                continue;
            }
            if (args[i] == ListIncludedOption)
            {
                if (i + 1 >= args.Length)
                {
                    Console.WriteLine($"{ListIncludedOption} には出力先パスが必要です");
                    return 1;
                }
                listOutputPath = args[++i];
                continue;
            }
            runtimeSourceRoot ??= args[i];
        }

        if (!Directory.Exists(assetsRoot))
        {
            Console.WriteLine($"アセットルートが見つかりません: {assetsRoot}");
            return 1;
        }

        Console.WriteLine($"ドライラン: {assetsRoot}");
        var watch     = System.Diagnostics.Stopwatch.StartNew();
        var collector = new AssetCollector(assetsRoot, new AssetPackagingSettings(),
                                           runtimeSourceRoot, Console.WriteLine);
        var result    = collector.Collect();
        watch.Stop();

        Console.WriteLine();
        Console.WriteLine($"所要時間: {watch.Elapsed.TotalSeconds:F1} 秒");
        Console.WriteLine($"収録: {result.Included.Count} ファイル / {result.IncludedBytes / BytesPerMegabyte:F1} MB");
        Console.WriteLine($"除外: {result.ExcludedFileCount} ファイル / {result.ExcludedBytes / BytesPerMegabyte:F1} MB");
        Console.WriteLine($"全体: {result.TotalFileCount} ファイル / {result.TotalBytes / BytesPerMegabyte:F1} MB");

        // 収録サイズの内訳（先頭フォルダ単位）
        Console.WriteLine();
        Console.WriteLine("収録サイズの内訳（先頭フォルダ）:");
        var byTop = result.Included
            .GroupBy(a => a.RelPath.Contains('/') ? a.RelPath[..a.RelPath.IndexOf('/')] : "(ルート)")
            .Select(g => (Folder: g.Key, Count: g.Count(), Bytes: g.Sum(x => x.SizeBytes)))
            .OrderByDescending(x => x.Bytes)
            .Take(DryRunFolderListLimit);
        foreach (var (folder, count, bytes) in byTop)
            Console.WriteLine($"  {folder,-20} {count,6} ファイル {bytes / BytesPerMegabyte,10:F1} MB");

        // 収録サイズの内訳（拡張子単位）
        Console.WriteLine();
        Console.WriteLine("収録サイズの内訳（拡張子）:");
        var byExt = result.Included
            .GroupBy(a => AssetPathUtil.GetExtensionLower(a.RelPath))
            .Select(g => (Ext: g.Key.Length == 0 ? "(なし)" : g.Key, Count: g.Count(), Bytes: g.Sum(x => x.SizeBytes)))
            .OrderByDescending(x => x.Bytes);   // 拡張子の種類は少ないので全件出す
        foreach (var (ext, count, bytes) in byExt)
            Console.WriteLine($"  {ext,-20} {count,6} ファイル {bytes / BytesPerMegabyte,10:F1} MB");

        // 欠落参照
        Console.WriteLine();
        Console.WriteLine($"参照先が見つからないパス: {result.MissingReferences.Count} 件");
        foreach (var m in result.MissingReferences.Take(DryRunMissingListLimit))
            Console.WriteLine($"  {m.ReferencePath}  <- {m.SourceRelPath}");
        if (result.MissingReferences.Count > DryRunMissingListLimit)
            Console.WriteLine($"  ...ほか {result.MissingReferences.Count - DryRunMissingListLimit} 件");

        // 実体の無い登録シーン
        Console.WriteLine();
        Console.WriteLine($"実体の無い登録シーン: {result.MissingScenes.Count} 件");
        foreach (var s in result.MissingScenes) Console.WriteLine($"  {s}");

        // 除外ルールに当たったまま同梱したもの
        Console.WriteLine();
        Console.WriteLine($"除外ルールに一致するが参照されたため同梱: {result.IncludedDespiteExclusion.Count} 件");
        foreach (var p in result.IncludedDespiteExclusion.Take(DryRunFolderListLimit))
            Console.WriteLine($"  {p}");
        if (result.IncludedDespiteExclusion.Count > DryRunFolderListLimit)
            Console.WriteLine($"  ...ほか {result.IncludedDespiteExclusion.Count - DryRunFolderListLimit} 件");

        // 収録ファイル一覧の書き出し（指定時のみ）
        if (listOutputPath is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"収録ファイル一覧を書き出し: {listOutputPath}");
            try
            {
                var listDir = Path.GetDirectoryName(Path.GetFullPath(listOutputPath));
                if (!string.IsNullOrEmpty(listDir)) Directory.CreateDirectory(listDir);
                // 1 行 1 件。並びは AssetCollectionResult.Included（相対パス昇順）のまま。
                File.WriteAllLines(listOutputPath, result.Included.Select(a => a.RelPath));
                Console.WriteLine($"完了: {result.Included.Count} 行");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ 書き出しに失敗: {ex.Message}");
                return 1;
            }
        }

        // PAK の書き出し（指定時のみ）
        if (pakOutputPath is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"PAK を書き出し: {pakOutputPath}");
            var stats = PakWriter.Write(pakOutputPath, assetsRoot, result.Included);
            Console.WriteLine(
                $"完了: {stats.EntryCount} ファイル / {stats.TotalBytes / BytesPerMegabyte:F1} MB");
            if (stats.SizeMismatchCount > 0)
                Console.WriteLine($"警告: 収集後にサイズが変わったファイル {stats.SizeMismatchCount} 件");
        }

        return 0;
    }

    // ============================================================
    //  1. 参照抽出（ファイルシステムに触らない）
    // ============================================================

    /// <summary>AssetReferenceScanner のテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterScannerTests(TestHarness h)
    {
        h.Add("assets:// 形式を終端文字まで正しく切り出す", () =>
        {
            var text = "{ \"a\": \"assets://ui/logo.png\", \"b\": <assets://fx/spark.png> }";
            var refs = AssetReferenceScanner.Scan(text, "scenes/main.scene", FakeRoot);
            var got  = ExplicitPaths(refs);
            Check.True(got.Contains("ui/logo.png"), "assets:// の基本形を拾えていない");
            Check.True(got.Contains("fx/spark.png"), "山括弧で終端する形を拾えていない");
        });

        h.Add("アセットルート絶対パスの 4 形式すべてを拾う", () =>
        {
            var slash    = FakeRoot.Replace('\\', '/');
            var back     = FakeRoot;
            var escBack  = back.Replace("\\", "\\\\");
            var escSlash = slash.Replace("/", "\\/");

            var text =
                $"\"{slash}/models/box.glb\" " +
                $"\"{back}\\audio\\bgm.mp3\" " +
                $"\"{escBack}\\\\ui\\\\logo.png\" " +
                $"\"{escSlash}\\/ui\\/frame.png\"";

            var got = ExplicitPaths(AssetReferenceScanner.Scan(text, "scenes/main.scene", FakeRoot));
            Check.True(got.Contains("models/box.glb"),  "スラッシュ区切りの絶対パスを拾えていない");
            Check.True(got.Contains("audio/bgm.mp3"),   "バックスラッシュ区切りの絶対パスを拾えていない");
            Check.True(got.Contains("ui/logo.png"),     "エスケープ済みバックスラッシュを拾えていない");
            Check.True(got.Contains("ui/frame.png"),    "エスケープ済みスラッシュを拾えていない");
        });

        h.Add("glTF の uri を参照元からの相対として拾い URL デコードする", () =>
        {
            var text = "{ \"buffers\": [ { \"uri\": \"man.bin\" } ], " +
                       "  \"images\":  [ { \"uri\": \"tex%20a.png\" } ] }";
            var refs = AssetReferenceScanner.Scan(text, "models/man/man.gltf", FakeRoot);

            var bin = refs.FirstOrDefault(r => r.Raw == "man.bin");
            Check.Equal("models/man/man.bin", bin.Candidates[0], "uri の第 1 候補は glTF からの相対");

            var tex = refs.FirstOrDefault(r => r.Raw == "tex%20a.png");
            Check.Equal("models/man/tex a.png", tex.Candidates[0], "uri の %20 がデコードされていない");
        });

        h.Add("ルート相対の参照は 参照元相対 と ルート相対 の 2 候補を持つ", () =>
        {
            var text = "{ \"base_color_texture\": \"textures/rock.png\" }";
            var refs = AssetReferenceScanner.Scan(text, "terrain/layers.json", FakeRoot);
            var rock = refs.FirstOrDefault(r => r.Raw == "textures/rock.png");

            Check.Equal(2, rock.Candidates.Count, "候補は 2 つ（参照元相対 / ルート相対）");
            Check.Equal("terrain/textures/rock.png", rock.Candidates[0], "第 1 候補は参照元フォルダ基準");
            Check.Equal("textures/rock.png",         rock.Candidates[1], "第 2 候補はアセットルート基準");
            Check.True(!rock.IsExplicit, "ルート相対の推測は確実な参照として扱わない");
        });

        h.Add("C# の文字列リテラル内の assets:// を拾う", () =>
        {
            var text = "public class A { private const string P = \"assets://fx/spark.png\"; }";
            var got  = ExplicitPaths(AssetReferenceScanner.Scan(text, "scripts/A.cs", FakeRoot));
            Check.True(got.Contains("fx/spark.png"), "スクリプトの文字列リテラルを拾えていない");
        });

        h.Add("拡張子を持たない文字列は推測の参照にしない", () =>
        {
            var text = "{ \"name\": \"Player\", \"tag\": \"enemy\" }";
            var refs = AssetReferenceScanner.Scan(text, "actors/a.actor", FakeRoot);
            Check.Equal(0, refs.Count, "拡張子なしの文字列を参照として拾ってはいけない");
        });
    }

    // ============================================================
    //  2. 収集（実ファイルのフィクスチャ）
    // ============================================================

    /// <summary>AssetCollector のテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterCollectorTests(TestHarness h)
    {
        h.Add("参照グラフの閉包で到達するファイルだけを収録する", () =>
        {
            using var fx = new AssetFixture();
            var result = Collect(fx);
            var got    = IncludedSet(result);

            // 到達するはずのファイル（多段の閉包・4 形式の絶対パス・glTF・ルート相対を含む）
            string[] expected =
            [
                "project_settings.json",
                "scenes/main.scene",
                "actors/hero.actor",
                "scripts/Hero.cs",
                "scripts/Notes.cs",
                "fx/spark.png",
                "notes/memo.png",
                "models/man.gltf",
                "models/man.bin",
                "models/tex a.png",
                "models/box.glb",
                "audio/bgm.mp3",
                "ui/logo.png",
                "ui/frame.png",
                "terrain/layers.json",
                "textures/rock.png",
                "world/chunk_0_0_0.tvox",
                "world/chunk_0_0_0.tscatter",
                "world/chunk_0_0_0.tcover",
                "world/terrain_meta.json",
                "templates/fonts/f.ttf",
            ];
            foreach (var e in expected)
                Check.True(got.Contains(e), $"収録されるべきファイルが無い: {e}（収録 {got.Count} 件）");

            Check.Equal(expected.Length, result.Included.Count,
                $"収録件数が想定と違う: {string.Join(", ", got.Except(expected, StringComparer.OrdinalIgnoreCase))}");
        });

        h.Add("未参照ファイルと除外対象は収録しない", () =>
        {
            using var fx = new AssetFixture();
            var got = IncludedSet(Collect(fx));

            Check.True(!got.Contains("unused/unused.png"),  "未参照ファイルが入っている");
            Check.True(!got.Contains("junk/scratch.tmp"),   "除外拡張子が入っている");
            Check.True(!got.Contains(".backup/old.scene"),  "除外フォルダが入っている");
            Check.True(!got.Contains("extra/extra.bin"),    "未指定の追加フォルダが入っている");
        });

        h.Add("除外フォルダでも参照されていれば同梱し警告に載せる", () =>
        {
            using var fx = new AssetFixture();
            var result = Collect(fx);

            Check.True(IncludedSet(result).Contains("templates/fonts/f.ttf"),
                "参照されている除外フォルダのファイルが落ちている（参照は除外より優先）");
            Check.True(result.IncludedDespiteExclusion.Contains("templates/fonts/f.ttf"),
                "除外ルールに一致したまま同梱したことが報告されていない");
        });

        h.Add("地形の同伴ファイル（.tscatter / .tcover / terrain_meta.json）を足す", () =>
        {
            using var fx = new AssetFixture();
            var got = IncludedSet(Collect(fx));

            // .scene には .tvox しか書かれていないが、ランタイムは拡張子を差し替えて隣を読む
            Check.True(got.Contains("world/chunk_0_0_0.tscatter"), "散布データが落ちている");
            Check.True(got.Contains("world/chunk_0_0_0.tcover"),   "カバーデータが落ちている");
            Check.True(got.Contains("world/terrain_meta.json"),    "地形メタデータが落ちている");
        });

        h.Add("実体の無い参照を参照元付きで報告する", () =>
        {
            using var fx = new AssetFixture();
            var result = Collect(fx);

            var missing = result.MissingReferences
                .FirstOrDefault(m => m.ReferencePath == "scenes/no_such_texture.png");
            Check.True(missing.ReferencePath is not null, "欠落参照が報告されていない");
            Check.Equal("scenes/main.scene", missing.SourceRelPath, "欠落参照の参照元が違う");
        });

        h.Add("コメント中の参照は拡張子の切れ目まで詰めて解決し、文章断片は欠落にしない", () =>
        {
            using var fx = new AssetFixture();
            var result = Collect(fx);

            // "assets://notes/memo.png）から読む。" のように文章が続いていても解決できること
            Check.True(IncludedSet(result).Contains("notes/memo.png"),
                "コメント中の参照が解決できていない");

            // "<c>assets://...</c>" のような書式説明は欠落として報告しないこと
            Check.True(!result.MissingReferences.Any(m => m.SourceRelPath == "scripts/Notes.cs"),
                "文章断片が欠落参照として報告されている: " +
                string.Join(", ", result.MissingReferences
                    .Where(m => m.SourceRelPath == "scripts/Notes.cs")
                    .Select(m => m.ReferencePath)));
        });

        h.Add("実体の無い登録シーンを報告してスキップする", () =>
        {
            using var fx = new AssetFixture();
            var result = Collect(fx);
            Check.True(result.MissingScenes.Any(s => s.Contains("missing.scene")),
                "実体の無い登録シーンが報告されていない");
        });

        h.Add("エディタ専用設定は参照されていても同梱しない", () =>
        {
            using var fx = new AssetFixture();
            var got = IncludedSet(Collect(fx));
            Check.True(!got.Contains("packaging_settings.json"),
                "packaging_settings.json は配布物に入れてはいけない");
        });

        h.Add("常時同梱拡張子の既定は空（.cs は DLL へ事前コンパイルして配るため）", () =>
        {
            using var fx = new AssetFixture();
            var got = IncludedSet(Collect(fx));

            // 既定設定では「未参照の .cs」は入らない。
            // （スクリプトは SEEDUserScripts.dll へ事前コンパイルして同梱するので、
            //   ソースをアセットとして配る必要が無い。ScriptPackager 参照）
            Check.True(!got.Contains("unused/Unused.cs"),
                "既定で未参照の .cs が同梱されている（常時同梱の既定が空になっていない）");
            Check.Equal(0, PackagingRules.DefaultAlwaysIncludedExtensions.Count,
                "常時同梱拡張子の既定");
        });

        // ── 型参照対策（走査専用の起点。AssetCollector.AddScriptScanSeeds） ──
        //
        //  実プロジェクトの FishCatalog.cs（Zukan.cs から型 FishCatalog として使われる）
        //  や MoveMission.cs（MissionFactory.cs から new MoveMission() で使われる）のように、
        //  C# の型名だけで参照されるスクリプトはパスの参照グラフに一切現れない。
        //  この 3 件でそのスクリプトを「走査だけして同梱はしない」挙動を検証する。

        h.Add("どのシーンからも辿られない .cs 内の assets:// 参照が収録される（型参照対策）", () =>
        {
            using var fx = new AssetFixture();
            AddTypeOnlyScriptFixture(fx);

            var got = IncludedSet(Collect(fx));
            Check.True(got.Contains("icons/type_only.png"),
                "型名でしか参照されないスクリプト（走査専用の起点）内の assets:// 参照が拾えていない");
        });

        h.Add("除外フォルダ配下の .cs は走査専用の起点にしない（フォルダの資産を丸ごと引き込まない）", () =>
        {
            using var fx = new AssetFixture();
            // templates は既定の除外フォルダ。ここに置いた .cs はどこからも参照されないので、
            // 走査専用の起点として拾ってしまうと templates 配下の資産が丸ごと収録されてしまう。
            fx.WriteText("templates/scripts/SampleOnly.cs", """
            public static class SampleOnly
            {
                public const string SampleIcon = "assets://templates/icons/should_not_appear.png";
            }
            """);
            fx.WriteBinary("templates/icons/should_not_appear.png", 8);

            var got = IncludedSet(Collect(fx));
            Check.True(!got.Contains("templates/icons/should_not_appear.png"),
                "除外フォルダ内の .cs まで走査してしまい、参照先が同梱されている");
            Check.True(!got.Contains("templates/scripts/SampleOnly.cs"),
                "除外フォルダ内の .cs 自身が同梱されている");
        });

        h.Add("型参照対策で走査した .cs 自身は同梱しない（走査と同梱は別の判断）", () =>
        {
            using var fx = new AssetFixture();
            AddTypeOnlyScriptFixture(fx);

            var result = Collect(fx);
            var got    = IncludedSet(result);

            // 参照先（icons/type_only.png）は入るのに、走査元の .cs 自身は入らないこと。
            // （前段のテストと合わせて確認することで、「そもそも走査されていない」ケースと
            //   「走査はしたが同梱していない」ケースを区別する）
            Check.True(got.Contains("icons/type_only.png"),
                "前提が崩れている: 走査自体が行われていない");
            Check.True(!got.Contains("scripts/TypeOnly.cs"),
                "走査専用のはずの .cs がそのまま PAK の収録集合に入っている");
        });

        h.Add("設定で指定した拡張子は参照が無くても入る（除外フォルダ内は除く）", () =>
        {
            using var fx = new AssetFixture();
            var settings = new AssetPackagingSettings();
            settings.AlwaysIncludedExtensions.Add(".cs");

            var got = IncludedSet(new AssetCollector(fx.Root, settings).Collect());
            Check.True(got.Contains("unused/Unused.cs"), "常時同梱指定の未参照スクリプトが入っていない");
            Check.True(!got.Contains(".backup/Old.cs"), "除外フォルダ内のスクリプトが入っている");
        });

        h.Add("追加同梱フォルダは参照が無くても丸ごと入る", () =>
        {
            using var fx = new AssetFixture();
            var settings = new AssetPackagingSettings();
            settings.AdditionalFolders.Add("extra");

            var got = IncludedSet(new AssetCollector(fx.Root, settings).Collect());
            Check.True(got.Contains("extra/extra.bin"), "追加同梱フォルダのファイルが入っていない");
        });

        h.Add("全ファイル同梱トグルで従来どおり全部入る", () =>
        {
            using var fx = new AssetFixture();
            var settings = new AssetPackagingSettings { IncludeAllFiles = true };
            var result   = new AssetCollector(fx.Root, settings).Collect();

            Check.Equal(result.TotalFileCount, result.Included.Count, "全ファイル同梱で件数が一致しない");
            Check.True(IncludedSet(result).Contains("unused/unused.png"), "未参照ファイルも入るべき");
        });
    }

    // ============================================================
    //  3. PAK 書き出し
    // ============================================================

    /// <summary>PakWriter のテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterPakTests(TestHarness h)
    {
        h.Add("PAK を書いて読み返せる（件数・オフセット・バイト一致）", () =>
        {
            using var fx = new AssetFixture();
            var result  = Collect(fx);
            var pakPath = Path.Combine(fx.Root, "out", "assets.pak");

            var stats = PakWriter.Write(pakPath, fx.Root, result.Included);
            var pak   = new PakTestReader(pakPath);

            Check.Equal(result.Included.Count, pak.EntryCount, "エントリ数が一致しない");
            Check.Equal(pak.FileLength, pak.DataEnd(), "データ部の終端がファイル末尾と一致しない");
            Check.Equal(result.Included.Count, stats.EntryCount, "統計のエントリ数が一致しない");

            // バイナリは 1 バイトも変えずに格納されること
            var expected = File.ReadAllBytes(Path.Combine(fx.Root, "models", "box.glb"));
            var actual   = pak.Read("models/box.glb");
            Check.True(actual is not null, "バイナリエントリが読めない");
            Check.True(expected.SequenceEqual(actual!), "バイナリの中身が変わっている");
        });

        h.Add("PAK 格納時にアセット絶対パスが仮想パスへ書き換わる", () =>
        {
            using var fx = new AssetFixture();
            var result  = Collect(fx);
            var pakPath = Path.Combine(fx.Root, "out", "assets.pak");
            PakWriter.Write(pakPath, fx.Root, result.Included);

            var scene = new PakTestReader(pakPath).ReadText("scenes/main.scene");
            Check.True(scene is not null, "シーンが読めない");

            var slash = fx.Root.Replace('\\', '/');
            Check.True(!scene!.Contains(slash), "スラッシュ区切りの絶対パスが残っている");
            Check.True(!scene.Contains(fx.Root), "バックスラッシュ区切りの絶対パスが残っている");
            Check.True(!scene.Contains(fx.Root.Replace("\\", "\\\\")), "エスケープ済み絶対パスが残っている");
            Check.True(scene.Contains("assets://models/box.glb"), "仮想パスへ書き換わっていない");
        });
    }

    // ============================================================
    //  4. 規則ヘルパー
    // ============================================================

    /// <summary>PackagingRules / AssetPathRewriter のテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterRuleTests(TestHarness h)
    {
        h.Add("絶対パス書き換えは 4 形式すべてに効く", () =>
        {
            var root     = @"C:\proj\runtime\assets";
            var slash    = root.Replace('\\', '/');
            var escBack  = root.Replace("\\", "\\\\");
            var escSlash = slash.Replace("/", "\\/");

            var text = $"{slash}/a.png|{root}\\b.png|{escBack}\\\\c.png|{escSlash}\\/d.png";
            var got  = AssetPathRewriter.ToVirtual(text, root);

            Check.True(!got.Contains("C:"), $"絶対パスが残っている: {got}");
            Check.Equal(4, got.Split("assets://").Length - 1, "仮想パスへの置換数が違う");
        });

        h.Add("ワイルドカード照合が前方一致パターンに効く", () =>
        {
            Check.True(PackagingRules.WildcardMatch("assets_realdir_backup_*", "assets_realdir_backup_20260903"),
                "前方一致 + * が一致しない");
            Check.True(PackagingRules.WildcardMatch("._*", "._craftmincho.otf"), "AppleDouble パターンが一致しない");
            Check.True(!PackagingRules.WildcardMatch("._*", "craftmincho.otf"),  "無関係な名前に一致してしまう");
            Check.True(PackagingRules.WildcardMatch("templates", "TEMPLATES"),   "大文字小文字を無視していない");
            Check.True(!PackagingRules.WildcardMatch("templates", "templates2"), "部分一致してしまっている");
        });

        h.Add("拡張子はドットの有無・大文字小文字を問わず同じ意味になる", () =>
        {
            Check.Equal(".cs", PackagingRules.NormalizeExtension("cs"),   "ドット無しの入力を補えていない");
            Check.Equal(".cs", PackagingRules.NormalizeExtension(" .CS "), "空白と大文字を吸収できていない");
            Check.Equal("",    PackagingRules.NormalizeExtension("  "),    "空入力は空文字であるべき");
        });

        h.Add("除外拡張子はドット無しで書いても効く", () =>
        {
            using var fx = new AssetFixture();
            // 既定の除外拡張子を「ドット無し」に置き換えても同じ結果になること
            var settings = new AssetPackagingSettings { ExcludedExtensions = ["tmp", "BLEND", "blend1", "zip", "psd", "lock", "bak"] };
            var got = IncludedSet(new AssetCollector(fx.Root, settings).Collect());
            Check.True(!got.Contains("junk/scratch.tmp"), "ドット無しの除外拡張子が効いていない");
        });
    }

    // ============================================================
    //  5. .NET ランタイムの同梱（DotnetRuntimeBundler）
    // ============================================================

    /// <summary>DotnetRuntimeBundler の純関数層（検出・選択・パス組み立て）のテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterDotnetBundlerTests(TestHarness h)
    {
        h.Add("--list-runtimes の出力から .NET ルートを割り出せる", () =>
        {
            // 実際の出力形式（フレームワークが 3 種類並ぶ）をそのまま与える
            const string output =
                "Microsoft.AspNetCore.App 9.0.20 [C:\\Program Files\\dotnet\\shared\\Microsoft.AspNetCore.App]\r\n" +
                "Microsoft.NETCore.App 9.0.19 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]\r\n" +
                "Microsoft.NETCore.App 9.0.20 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]\r\n" +
                "Microsoft.WindowsDesktop.App 9.0.20 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]\r\n";

            var listed = DotnetRuntimeBundler.ParseListedRuntimes(output);
            Check.Equal(4, listed.Count, "解析できた行数が違う");
            Check.Equal("9.0.19", listed[1].Version, "バージョンの読み取りが違う");

            var roots = DotnetRuntimeBundler.DotnetRootsFrom(listed);
            Check.Equal(1, roots.Count, "同じルートが重複している");
            Check.Equal("C:\\Program Files\\dotnet", roots[0], ".NET ルートの割り出しが違う");
        });

        h.Add("--list-runtimes の壊れた行は読み飛ばす", () =>
        {
            const string output =
                "\r\n" +
                "何かの警告メッセージ\r\n" +
                "Microsoft.NETCore.App 9.0.20\r\n" +                       // パスが無い
                "Microsoft.NETCore.App [C:\\dotnet\\shared\\Microsoft.NETCore.App]\r\n" + // バージョンが無い
                "Microsoft.NETCore.App 9.0.20 [C:\\dotnet\\shared\\Microsoft.NETCore.App]\r\n";

            var listed = DotnetRuntimeBundler.ParseListedRuntimes(output);
            Check.Equal(1, listed.Count, "壊れた行を拾ってしまっている");
            Check.Equal("C:\\dotnet", DotnetRuntimeBundler.DotnetRootsFrom(listed)[0], "ルートの割り出しが違う");
        });

        h.Add("要求 major.minor の最新パッチを選ぶ", () =>
        {
            DotnetRuntimeBundler.TryParseRequiredFrameworkVersion(
                """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"9.0.0"}}}""",
                out var required);

            string[] installed = ["8.0.11", "9.0.3", "9.0.19", "9.0.20", "10.0.0"];
            Check.Equal("9.0.20", DotnetRuntimeBundler.SelectLatestPatch(installed, required),
                "最新パッチを選べていない");
        });

        h.Add("major.minor が違うものは選ばない", () =>
        {
            var required = new DotnetVersion(9, 0, 0, "");

            // 9.0.x が 1 つも無ければ、より新しい 10.0 があっても選ばない
            Check.Equal(null, DotnetRuntimeBundler.SelectLatestPatch(["8.0.20", "10.0.5"], required),
                "major.minor 不一致を採用してしまっている");
            // 9.1 も別バンドルなので不可
            Check.Equal(null, DotnetRuntimeBundler.SelectLatestPatch(["9.1.0"], required),
                "minor 違いを採用してしまっている");
            // バージョンとして読めない名前は無視する
            Check.Equal(null, DotnetRuntimeBundler.SelectLatestPatch(["9.0", "current", ""], required),
                "バージョンでない名前を採用してしまっている");
        });

        h.Add("プレビュー版は同じ数値の正式版より下に順位付けされる", () =>
        {
            var required = new DotnetVersion(10, 0, 0, "");
            Check.Equal("10.0.0", DotnetRuntimeBundler.SelectLatestPatch(["10.0.0-preview.5.1", "10.0.0"], required),
                "プレビュー版を正式版より優先してしまっている");
            Check.Equal("10.0.0-preview.5.1",
                DotnetRuntimeBundler.SelectLatestPatch(["10.0.0-preview.5.1"], required),
                "プレビュー版しか無いときに選べていない");
        });

        h.Add("hostfxr は同じバージョン優先・無ければそれ以上の最新", () =>
        {
            Check.Equal("9.0.20", DotnetRuntimeBundler.SelectHostFxrVersion(["9.0.19", "9.0.20", "10.0.0"], "9.0.20"),
                "完全一致を優先できていない");
            Check.Equal("10.0.0", DotnetRuntimeBundler.SelectHostFxrVersion(["9.0.19", "10.0.0"], "9.0.20"),
                "CLR 以上の最新を選べていない");
            Check.Equal(null, DotnetRuntimeBundler.SelectHostFxrVersion(["9.0.3", "9.0.19"], "9.0.20"),
                "CLR より古い hostfxr を選んでしまっている");
        });

        h.Add("runtimeconfig の framework は単一・配列の両形式を読める", () =>
        {
            var single = DotnetRuntimeBundler.TryParseRequiredFrameworkVersion(
                """{"runtimeOptions":{"tfm":"net9.0","framework":{"name":"Microsoft.NETCore.App","version":"9.0.0"}}}""",
                out var v1);
            Check.True(single, "単一形式を読めていない");
            Check.Equal(9, v1.Major, "major が違う");

            var array = DotnetRuntimeBundler.TryParseRequiredFrameworkVersion(
                """
                {"runtimeOptions":{"frameworks":[
                    {"name":"Microsoft.WindowsDesktop.App","version":"9.0.0"},
                    {"name":"Microsoft.NETCore.App","version":"10.0.0"}]}}
                """,
                out var v2);
            Check.True(array, "配列形式を読めていない");
            Check.Equal(10, v2.Major, "配列から Microsoft.NETCore.App を選べていない");

            Check.True(!DotnetRuntimeBundler.TryParseRequiredFrameworkVersion("{}", out _),
                "空の JSON を読めたことにしてしまっている");
            Check.True(!DotnetRuntimeBundler.TryParseRequiredFrameworkVersion("壊れた JSON", out _),
                "壊れた JSON で例外が漏れている");
        });

        h.Add("出力レイアウトがランタイム側の規約と一致する", () =>
        {
            const string outDir = @"D:\Out\MyGame";

            // 同梱先は出力フォルダ直下ではなく bin/ の下（package_layout.rs と同じ構成）
            Check.Equal(Path.Combine(outDir, "bin", "dotnet"),
                DotnetRuntimeBundler.BundledRootDirectory(outDir), ".NET ルートの出力先が規約と違う");
            Check.Equal(Path.Combine(outDir, "bin", "dotnet", "shared", "Microsoft.NETCore.App", "9.0.20"),
                DotnetRuntimeBundler.BundledFrameworkDirectory(outDir, "9.0.20"), "CLR の出力先が規約と違う");
            Check.Equal(Path.Combine(outDir, "bin", "dotnet", "host", "fxr", "9.0.20", "hostfxr.dll"),
                DotnetRuntimeBundler.BundledHostFxrPath(outDir, "9.0.20"), "hostfxr の出力先が規約と違う");

            // ランタイム側 scripting/mod.rs の BUNDLED_DOTNET_ROOT_DIR と同じ名前であること
            Check.Equal("dotnet", DotnetRuntimeBundler.BundledRootDirName, "同梱フォルダ名が規約と違う");
        });

        RegisterPackageLayoutTests(h);
    }

    // ============================================================
    //  6. 配布物のフォルダ構成（PackageLayout）
    // ============================================================

    /// <summary>PackageLayout（フォルダ名の規約と旧レイアウトの後始末）のテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterPackageLayoutTests(TestHarness h)
    {
        h.Add("フォルダ名がランタイム側 package_layout.rs の規約と一致する", () =>
        {
            // 名前がずれるとビルドは通るのに配布物だけが壊れるため、文字列で固定する
            Check.Equal("bin",    PackageLayout.BinDirName,    "bin の名前が規約と違う");
            Check.Equal("caches", PackageLayout.CachesDirName, "caches の名前が規約と違う");
            Check.Equal("logs",   PackageLayout.LogsDirName,   "logs の名前が規約と違う");
            Check.Equal("saved",  PackageLayout.SavedDirName,  "saved の名前が規約と違う");

            Check.Equal(Path.Combine(@"D:\Out\MyGame", "bin"),
                PackageLayout.BinDirectory(@"D:\Out\MyGame"), "bin フォルダのパス組み立てが違う");
        });

        h.Add("旧レイアウトの残骸だけを削除対象に選ぶ", () =>
        {
            string[] files =
            [
                "MyGame.exe",                       // 実行ファイル（残す）
                "assets.pak",                       // アセット（残す）
                "SEEDScripting.dll",                // 旧配置の残骸
                "SEEDUserScripts.dll",              // 旧配置の残骸
                "Microsoft.CodeAnalysis.CSharp.dll",// 旧配置の残骸
                "SEEDScripting.runtimeconfig.json", // 旧配置の残骸
                "SEEDScripting.deps.json",          // 旧配置の残骸
                "pipeline_cache.bin",               // 実行時生成（残す）
                "readme.txt",                       // 利用者が置いたもの（残す）
                "settings.json",                    // 無関係な JSON（残す）
            ];
            string[] dirs = ["dotnet", "bin", "caches", "logs", "saved", "save"];

            var targets = PackageLayout.SelectLegacyLeftovers(files, dirs);

            Check.Equal(6, targets.Count, "削除対象の件数が違う: " + string.Join(" / ", targets));
            foreach (var expected in new[]
                     {
                         "SEEDScripting.dll", "SEEDUserScripts.dll", "Microsoft.CodeAnalysis.CSharp.dll",
                         "SEEDScripting.runtimeconfig.json", "SEEDScripting.deps.json", "dotnet",
                     })
            {
                Check.True(targets.Contains(expected), $"{expected} が削除対象に入っていない");
            }
        });

        h.Add("実行時生成フォルダと利用者データは削除対象にしない", () =>
        {
            // caches / logs / saved は利用者のキャッシュ・ログ・セーブ。
            // 旧レイアウトの save/ も同じくセーブなので消さない。
            foreach (var name in new[] { "caches", "logs", "saved", "save", "bin" })
                Check.True(!PackageLayout.IsLegacyLeftoverDirectory(name), $"{name} を消そうとしている");

            foreach (var name in new[] { "MyGame.exe", "assets.pak", "pipeline_cache.bin", "save.json" })
                Check.True(!PackageLayout.IsLegacyLeftoverFile(name), $"{name} を消そうとしている");

            // 実行時生成フォルダの一覧が 3 つ揃っていること（後始末の除外リストの正典）
            Check.Equal(3, PackageLayout.RuntimeGeneratedDirNames.Count, "実行時生成フォルダの件数が違う");
        });
    }

    // ============================================================
    //  ヘルパー
    // ============================================================

    /// <summary>既定設定でフィクスチャを収集する。</summary>
    /// <param name="fx">対象フィクスチャ。</param>
    /// <returns>収集結果。</returns>
    private static AssetCollectionResult Collect(AssetFixture fx) =>
        new AssetCollector(fx.Root, new AssetPackagingSettings()).Collect();

    /// <summary>
    /// 「C# の型名だけで参照されるスクリプト」を模したファイル一式をフィクスチャへ書き足す。
    ///
    /// <para>
    /// 実プロジェクトの <c>FishCatalog.cs</c>（<c>Zukan.cs</c> から型 <c>FishCatalog</c> として
    /// 使われるだけで、パスとしては一切参照されない）と同じ形。<c>scripts/TypeOnly.cs</c> は
    /// どのシーン・アクタからも辿られないが、中に <c>assets://icons/type_only.png</c> を持つ。
    /// </para>
    /// </summary>
    /// <param name="fx">書き足す対象のフィクスチャ。</param>
    private static void AddTypeOnlyScriptFixture(AssetFixture fx)
    {
        fx.WriteText("scripts/TypeOnly.cs", """
        public static class TypeOnly
        {
            // 型名だけで参照されるスクリプトの assets:// 参照（生成された画像パスなどの想定）。
            public const string IconPath = "assets://icons/type_only.png";
        }
        """);
        fx.WriteBinary("icons/type_only.png", 8);
    }

    /// <summary>収録された相対パスの集合を作る（大文字小文字は無視）。</summary>
    /// <param name="result">収集結果。</param>
    /// <returns>相対パスの集合。</returns>
    private static HashSet<string> IncludedSet(AssetCollectionResult result) =>
        new(result.Included.Select(a => a.RelPath), StringComparer.OrdinalIgnoreCase);

    /// <summary>確実な参照（IsExplicit）の第 1 候補パスを集合で返す。</summary>
    /// <param name="refs">抽出された参照候補。</param>
    /// <returns>第 1 候補パスの集合。</returns>
    private static HashSet<string> ExplicitPaths(IReadOnlyList<AssetReferenceCandidate> refs) =>
        new(refs.Where(r => r.IsExplicit).Select(r => r.Candidates[0]), StringComparer.OrdinalIgnoreCase);
}
