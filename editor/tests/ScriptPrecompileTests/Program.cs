// ============================================================
//  Program.cs — ユーザースクリプト事前コンパイル経路の単体テスト
//
//  実行: dotnet run --project editor/tests/ScriptPrecompileTests
//
//  【検証範囲】
//   1. CompileToFile    : DLL の出力と型マップ（マニフェストリソース）の埋め込み
//   2. LoadPrecompiled  : 型マップからの解決テーブル復元・ファイルをロックしないこと
//   3. Resolve          : assets:// 相対パスでの型解決
//                         （別フォルダの同名 .cs を取り違えないこと）
//   4. エラー処理        : コンパイルエラーが結果に載り、壊れた DLL を残さないこと
//
//  【なぜこれを固定するのか】
//  パッケージ版はエディタ上で再現しない。ここが壊れると
//  「配布物だけスクリプトが全部動かない」という、出荷後にしか気付けない
//  形の不具合になるため、経路そのものをテストで押さえておく。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SEEDEditor.Packaging;
using SEEDEditor.Packaging.Scripts;
using SEEDEditor.Scripting;
using SEEDEditor.Scripting.Compilation;
using SpriteRigTests;

namespace SEEDEditor.Tests.ScriptPrecompile;

/// <summary>テストの登録と実行を行うエントリポイント。</summary>
public static class Program
{
    /// <summary>AppContext から参照アセンブリ一覧を取り出すキー（ScriptPackager と同じ）。</summary>
    private const string TrustedAssembliesKey = "TRUSTED_PLATFORM_ASSEMBLIES";

    /// <summary>TRUSTED_PLATFORM_ASSEMBLIES の区切り文字（Windows）。</summary>
    private const char TrustedAssembliesSeparator = ';';

    /// <summary>標準構成に含まれるスクリプト型の数（Alpha.Foo / Beta.Foo / Solo）。</summary>
    private const int StandardTreeScriptTypeCount = 3;

    /// <summary>標準構成に含まれる .cs の数（スクリプト 3 本 + 普通のクラス 1 本）。</summary>
    private const int StandardTreeSourceFileCount = 4;

    /// <summary>スクリプトホスト本体の DLL 名（同梱先の確認に使う）。</summary>
    private const string HostAssemblyFileName = "SEEDScripting.dll";

    /// <summary>スクリプトホストの runtimeconfig 名（.NET 同梱フェーズの読み取り元）。</summary>
    private const string HostRuntimeConfigFileName = "SEEDScripting.runtimeconfig.json";

    /// <summary>スクリプトホストのデバッグシンボル名（同梱されないことの確認に使う）。</summary>
    private const string HostSymbolFileName = "SEEDScripting.pdb";

    /// <summary>
    /// テストを登録して実行する。
    ///
    /// 引数に「runtime フォルダ アセットルート 出力フォルダ」を渡すと、
    /// テストの代わりに実プロジェクトのスクリプトパッケージ化
    /// （<see cref="ScriptPackager.Run"/>）を行う。エディタ UI を起動せずに
    /// パッケージ相当のスクリプト一式を用意するための入口で、
    /// パッケージ版の動作確認（実機確認）に使う。
    /// </summary>
    /// <param name="args">コマンドライン引数。</param>
    /// <returns>プロセス終了コード（全成功なら 0）。</returns>
    public static int Main(string[] args)
    {
        if (args.Length > 0) return PackageRealProject(args);

        Console.WriteLine("ScriptPrecompileTests");
        var h = new TestHarness();

        RegisterCompileTests(h);
        RegisterResolveTests(h);
        RegisterPackagerTests(h);
        RegisterFailureTests(h);

        return h.Run();
    }

    /// <summary>
    /// 実プロジェクトに対してスクリプトのパッケージ化を実行する（UI を使わない入口）。
    /// </summary>
    /// <param name="args">runtime フォルダ / アセットルート / 出力フォルダ。</param>
    /// <returns>プロセス終了コード。</returns>
    private static int PackageRealProject(string[] args)
    {
        const int RequiredArgumentCount = 3;
        if (args.Length < RequiredArgumentCount)
        {
            Console.WriteLine("引数: <runtime フォルダ> <アセットルート> <出力フォルダ>");
            return 1;
        }

        var (runtimePath, assetsPath, outputDir) = (args[0], args[1], args[2]);
        Directory.CreateDirectory(outputDir);

        var result = ScriptPackager.Run(runtimePath, assetsPath, outputDir, Console.WriteLine);
        if (!result.Success)
        {
            ScriptPackager.LogErrors(result, Console.WriteLine);
            return 1;
        }

        Console.WriteLine($"完了: {result.ScriptTypeCount} 型 / {result.CopiedFileCount} ファイル同梱");
        return 0;
    }

    // ============================================================
    //  1. 事前コンパイル（DLL 出力と型マップ）
    // ============================================================

    /// <summary>CompileToFile / LoadPrecompiled のテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterCompileTests(TestHarness h)
    {
        h.Add("事前コンパイルで DLL が出力され型マップが埋め込まれる", () =>
        {
            using var fx = new ScriptFixture();
            fx.BuildStandardTree();

            var dll    = fx.OutputPath(PrecompiledScriptArtifact.AssemblyFileName);
            var result = ScriptAssemblyManager.CompileToFile(fx.Root, dll, BuildReferences());

            Check.True(result.Success, "コンパイルに失敗した: " + string.Join(" / ", result.Errors));
            Check.True(File.Exists(dll), "DLL が出力されていない");
            Check.Equal(StandardTreeSourceFileCount, result.SourceFileCount, "対象ソース数");
            Check.Equal(StandardTreeScriptTypeCount, result.ScriptTypeCount, "スクリプト型数");

            // マニフェストリソースとして型マップが焼き込まれていること
            Check.True(ManifestResourceNames(dll).Contains(PrecompiledScriptArtifact.TypeMapResourceName),
                "型マップのマニフェストリソースが DLL に無い");
        });

        h.Add("事前コンパイル DLL をロードしても DLL ファイルをロックしない", () =>
        {
            using var fx = new ScriptFixture();
            fx.BuildStandardTree();

            var dll = fx.OutputPath(PrecompiledScriptArtifact.AssemblyFileName);
            Check.True(ScriptAssemblyManager.CompileToFile(fx.Root, dll, BuildReferences()).Success,
                "前提のコンパイルに失敗した");

            var count = ScriptAssemblyManager.LoadPrecompiled(dll);
            Check.Equal(StandardTreeScriptTypeCount, count, "ロードできたスクリプト型数");

            // バイト列で読んでからロードしているので、ロード後も削除できるはず
            // （ここでロックしていると、配布物の上書き更新ができなくなる）
            File.Delete(dll);
            Check.True(!File.Exists(dll), "ロード後に DLL を削除できない（ファイルをロックしている）");
        });

        h.Add("スクリプトではない型は型マップに載らない", () =>
        {
            using var fx = new ScriptFixture();
            fx.BuildStandardTree();

            var dll = fx.OutputPath(PrecompiledScriptArtifact.AssemblyFileName);
            ScriptAssemblyManager.CompileToFile(fx.Root, dll, BuildReferences());
            ScriptAssemblyManager.LoadPrecompiled(dll);

            // d/Plain.cs は SEEDScript を継承していないのでパス解決の対象外
            Check.True(ScriptAssemblyManager.Resolve("assets://d/Plain.cs") is null,
                "スクリプトではない型がパスで解決できてしまっている");
        });
    }

    // ============================================================
    //  2. 型解決（この経路の一番の危険地帯）
    // ============================================================

    /// <summary>Resolve のテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterResolveTests(TestHarness h)
    {
        h.Add("別フォルダの同名 .cs をそれぞれ正しい型へ解決する", () =>
        {
            using var fx = LoadStandardTree();

            var a = ScriptAssemblyManager.Resolve("assets://a/Foo.cs");
            var b = ScriptAssemblyManager.Resolve("assets://b/Foo.cs");

            Check.True(a is not null, "assets://a/Foo.cs が解決できない");
            Check.True(b is not null, "assets://b/Foo.cs が解決できない");
            Check.Equal("Alpha.Foo", a!.FullName, "a/Foo.cs の解決先");
            Check.Equal("Beta.Foo",  b!.FullName, "b/Foo.cs の解決先");
        });

        h.Add("区切り文字と大文字小文字の表記ゆれを吸収する", () =>
        {
            using var fx = LoadStandardTree();

            // シーンの保存形式が変わっても壊れないこと（'\' 区切り・大小文字違い）
            Check.Equal("Alpha.Foo", ScriptAssemblyManager.Resolve(@"assets://A\FOO.CS")?.FullName,
                "表記ゆれのある参照が解決できない");
        });

        h.Add("ファイル名だけ・型名だけでも解決できる", () =>
        {
            using var fx = LoadStandardTree();

            Check.Equal("Solo", ScriptAssemblyManager.Resolve("Solo.cs")?.FullName,
                "ファイル名だけの参照が解決できない");
            Check.Equal("Solo", ScriptAssemblyManager.Resolve("Solo")?.FullName,
                "型名だけの参照が解決できない");
        });

        h.Add("存在しないスクリプトは null を返す（例外を投げない）", () =>
        {
            using var fx = LoadStandardTree();
            Check.True(ScriptAssemblyManager.Resolve("assets://a/NoSuch.cs") is null,
                "存在しない .cs が解決できてしまっている");
        });

        h.Add("その場コンパイル（エディタ経路）でも絶対パス・相対キーの両方で解決できる", () =>
        {
            using var fx = new ScriptFixture();
            fx.BuildStandardTree();

            // エディタ / Play の経路。事前コンパイルと同じ型マップ実装を通ることの確認。
            var count = ScriptAssemblyManager.CompileAndLoad(fx.Root);
            Check.Equal(StandardTreeScriptTypeCount, count, "その場コンパイルのスクリプト型数");

            // エディタが .scene に保存する絶対パス形式
            Check.Equal("Alpha.Foo",
                ScriptAssemblyManager.Resolve(Path.Combine(fx.Root, "a", "Foo.cs"))?.FullName,
                "絶対パスでの解決先");
            Check.Equal("Beta.Foo",
                ScriptAssemblyManager.Resolve(Path.Combine(fx.Root, "b", "Foo.cs"))?.FullName,
                "絶対パスでの解決先（同名ファイル）");

            // PAK 化後の assets:// 形式も同じ型へ解決できること（形式が混在しても壊れない）
            Check.Equal("Alpha.Foo", ScriptAssemblyManager.Resolve("assets://a/Foo.cs")?.FullName,
                "仮想パスでの解決先");
        });
    }

    // ============================================================
    //  3. パッケージ化の入口
    // ============================================================

    /// <summary>ScriptPackager のテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterPackagerTests(TestHarness h)
    {
        h.Add("スクリプトホストが未ビルドなら理由付きで失敗する", () =>
        {
            using var fx = new ScriptFixture();
            fx.BuildStandardTree();

            // ホスト出力の無い場所を runtime フォルダとして渡す
            var result = ScriptPackager.Run(fx.OutputDir, fx.Root, fx.OutputDir, _ => { });

            Check.True(!result.Success, "ホスト不在でも成功してしまっている");
            Check.True(result.FailureSummary.Contains("SEEDScripting.dll", StringComparison.Ordinal),
                "失敗理由に不足しているファイル名が入っていない: " + result.FailureSummary);
        });

        h.Add("成果物とホスト一式は出力フォルダ直下ではなく bin/ へ置かれる", () =>
        {
            using var fx = new ScriptFixture();
            fx.BuildStandardTree();
            var runtimeDir = fx.BuildHostBuildOutput();

            var result = ScriptPackager.Run(runtimeDir, fx.Root, fx.OutputDir, _ => { });
            Check.True(result.Success, "パッケージ化に失敗した: " + result.FailureSummary
                + string.Join(" / ", result.Errors));

            var binDir = PackageLayout.BinDirectory(fx.OutputDir);

            // bin/ に揃っていること（ランタイムはここしか見ない）
            Check.True(File.Exists(Path.Combine(binDir, PrecompiledScriptArtifact.AssemblyFileName)),
                "事前コンパイル DLL が bin/ に無い");
            Check.True(File.Exists(Path.Combine(binDir, HostAssemblyFileName)),
                "スクリプトホストが bin/ に無い");
            Check.True(File.Exists(Path.Combine(binDir, HostRuntimeConfigFileName)),
                "runtimeconfig が bin/ に無い（.NET 同梱フェーズがバージョンを読めなくなる）");

            // 直下には 1 つも置かないこと（「exe の隣に DLL が散らかる」状態の回帰防止）
            Check.True(!File.Exists(Path.Combine(fx.OutputDir, PrecompiledScriptArtifact.AssemblyFileName)),
                "事前コンパイル DLL が出力フォルダ直下に残っている");
            Check.True(!File.Exists(Path.Combine(fx.OutputDir, HostAssemblyFileName)),
                "スクリプトホストが出力フォルダ直下に残っている");
            Check.True(!File.Exists(Path.Combine(fx.OutputDir, HostRuntimeConfigFileName)),
                "runtimeconfig が出力フォルダ直下に残っている");

            // デバッグシンボルは同梱しない（配布物に開発機のソースパスを載せないため）
            Check.True(!File.Exists(Path.Combine(binDir, HostSymbolFileName)),
                "デバッグシンボル（.pdb）を同梱してしまっている");
        });
    }

    // ============================================================
    //  4. 失敗時の振る舞い
    // ============================================================

    /// <summary>コンパイル失敗まわりのテストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    private static void RegisterFailureTests(TestHarness h)
    {
        h.Add("コンパイルエラーは結果に載り、壊れた DLL を残さない", () =>
        {
            using var fx = new ScriptFixture();
            fx.BuildStandardTree();
            fx.WriteText("a/Broken.cs", "public class Broken : SEEDScript { this is not C# }");

            var dll    = fx.OutputPath(PrecompiledScriptArtifact.AssemblyFileName);
            var result = ScriptAssemblyManager.CompileToFile(fx.Root, dll, BuildReferences());

            Check.True(!result.Success, "壊れたソースでコンパイルが成功してしまっている");
            Check.True(result.Errors.Count > 0, "エラーが 1 件も報告されていない");
            Check.True(result.Errors.Any(e => e.Contains("Broken.cs", StringComparison.OrdinalIgnoreCase)),
                "エラーメッセージに該当ファイル名が含まれていない: " + string.Join(" / ", result.Errors));
            Check.True(!File.Exists(dll), "失敗したのに DLL が残っている（古い成果物が配られる原因になる）");
        });

        h.Add("参照に SEEDScripting が無いとエラーとして扱う", () =>
        {
            using var fx = new ScriptFixture();
            fx.BuildStandardTree();

            // 基本ライブラリだけ（SEEDScripting.dll を除いた参照）でコンパイルする
            var references = BuildReferences()
                .Where(p => !string.Equals(Path.GetFileName(p), "SEEDScripting.dll",
                                           StringComparison.OrdinalIgnoreCase))
                .ToList();

            var dll    = fx.OutputPath("NoHost.dll");
            var result = ScriptAssemblyManager.CompileToFile(fx.Root, dll, references);

            Check.True(!result.Success, "SEEDScripting 抜きでコンパイルが成功してしまっている");
            Check.True(result.Errors.Count > 0, "エラーが 1 件も報告されていない");
        });

        h.Add("存在しない DLL のロードは -1 を返す（例外を投げない）", () =>
        {
            using var fx = new ScriptFixture();
            Check.Equal(-1, ScriptAssemblyManager.LoadPrecompiled(fx.OutputPath("NotThere.dll")),
                "存在しない DLL のロード結果");
        });
    }

    // ============================================================
    //  ヘルパー
    // ============================================================

    /// <summary>
    /// 標準構成を作り、事前コンパイルしてロードした状態のフィクスチャを返す。
    /// </summary>
    /// <returns>使用後に破棄するフィクスチャ。</returns>
    private static ScriptFixture LoadStandardTree()
    {
        var fx = new ScriptFixture();
        fx.BuildStandardTree();

        var dll = fx.OutputPath(PrecompiledScriptArtifact.AssemblyFileName);
        var compiled = ScriptAssemblyManager.CompileToFile(fx.Root, dll, BuildReferences());
        if (!compiled.Success)
        {
            fx.Dispose();
            throw new AssertionException("前提のコンパイルに失敗した: " + string.Join(" / ", compiled.Errors));
        }

        ScriptAssemblyManager.LoadPrecompiled(dll);
        return fx;
    }

    /// <summary>
    /// 参照アセンブリ一覧を作る（ScriptPackager.BuildReferences と同じ考え方）。
    /// </summary>
    /// <returns>参照アセンブリの絶対パス一覧。</returns>
    private static List<string> BuildReferences()
    {
        var references = new List<string>();

        if (AppContext.GetData(TrustedAssembliesKey) is string trusted)
        {
            references.AddRange(trusted
                .Split(TrustedAssembliesSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        // ランタイムが実際に読む側の SEEDScripting.dll を末尾に足す（後勝ちで畳まれる）
        var hostPath = typeof(SEEDScript).Assembly.Location;
        if (!string.IsNullOrEmpty(hostPath)) references.Add(hostPath);

        return references;
    }

    /// <summary>
    /// DLL をロードせずにマニフェストリソース名を列挙する。
    /// （ロードするとファイルを掴んでしまい、後片付けができなくなる）
    /// </summary>
    /// <param name="dllPath">対象 DLL のパス。</param>
    /// <returns>マニフェストリソース名の一覧。</returns>
    private static List<string> ManifestResourceNames(string dllPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe     = new PEReader(stream);
        var metadata     = pe.GetMetadataReader();

        return metadata.ManifestResources
            .Select(handle => metadata.GetString(metadata.GetManifestResource(handle).Name))
            .ToList();
    }
}
