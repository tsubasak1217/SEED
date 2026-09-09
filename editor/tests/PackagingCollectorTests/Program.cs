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
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SEEDEditor.Packaging.Collect;
using SEEDEditor.Packaging.Pak;
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
        if (args.Length > 0) return DryRun(args);

        Console.WriteLine("PackagingCollectorTests");
        var h = new TestHarness();

        RegisterScannerTests(h);
        RegisterCollectorTests(h);
        RegisterPakTests(h);
        RegisterRuleTests(h);

        return h.Run();
    }

    // ============================================================
    //  0. ドライラン（実プロジェクトの収録内容を確認する）
    // ============================================================

    /// <summary>PAK の書き出し先を指定するオプション名。</summary>
    private const string WritePakOption = "--write-pak";

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
    /// </summary>
    /// <param name="args">
    /// コマンドライン引数。args[0] がアセットルート。
    /// 続く位置引数は runtime/src（省略可）、"--write-pak &lt;パス&gt;" は任意の位置に置ける。
    /// </param>
    /// <returns>プロセス終了コード。</returns>
    private static int DryRun(string[] args)
    {
        var assetsRoot = args[0];

        // 位置引数（runtime/src）とオプション（--write-pak）を分けて読む
        string? runtimeSourceRoot = null;
        string? pakOutputPath     = null;
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
    //  ヘルパー
    // ============================================================

    /// <summary>既定設定でフィクスチャを収集する。</summary>
    /// <param name="fx">対象フィクスチャ。</param>
    /// <returns>収集結果。</returns>
    private static AssetCollectionResult Collect(AssetFixture fx) =>
        new AssetCollector(fx.Root, new AssetPackagingSettings()).Collect();

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
