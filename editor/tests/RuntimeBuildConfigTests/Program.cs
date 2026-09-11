using System;
using System.IO;
using System.Linq;
using SEEDEditor.Runtime.BuildConfig;
using SpriteRigTests;   // TestHarness / Check（テストランナーは SpriteRigTests と共用）

namespace RuntimeBuildConfigTests;

/// <summary>
/// ランタイムのビルド構成（docs/runtime_build_configs.md）の単体テスト。
///
/// 検証の柱:
///   1. カタログ JSON の読み込み（既定・一覧・未知 id・破損時のフォールバック）
///   2. リポジトリ同梱の runtime_build_configs.json が組み込み既定と食い違っていないこと
///   3. RuntimeExeLocator のパス解決（環境変数 &gt; exe の隣 &gt; target/&lt;構成&gt;）
///   4. CargoBuildCommand の引数組み立て
///   5. RuntimeSourceDirLocator（cargo の作業ディレクトリ）
/// </summary>
public static class Program
{
    // ── テストで使う固定値（マジックナンバー / マジックストリングの一元化）──

    /// <summary>組み込み既定に必ず含まれる構成 id。</summary>
    private const string IdDebug   = "debug";
    private const string IdDevelop = "develop";
    private const string IdRelease = "release";

    /// <summary>組み込み既定の構成数。</summary>
    private const int BuiltInConfigCount = 3;

    /// <summary>カタログのファイル名（テスト用の一時 JSON もこの名前で置く）。</summary>
    private const string CatalogFileName = RuntimeBuildConfigCatalog.FileName;

    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();

        // ── カタログ: 正常系 ────────────────────────────────
        harness.Add("JSON から構成一覧と既定を読める",                  CatalogLoadsConfigsAndDefault);
        harness.Add("リポジトリ同梱のカタログは組み込み既定と一致する",  ShippedCatalogMatchesBuiltIn);
        harness.Add("format_version が新しくても読める（警告のみ）",     CatalogFutureFormatVersionStillLoads);

        // ── カタログ: 異常系（必ず組み込み既定へ落ちる）──────
        harness.Add("ファイルが無ければ組み込み既定へフォールバック",    CatalogMissingFileFallsBack);
        harness.Add("壊れた JSON なら組み込み既定へフォールバック",      CatalogBrokenJsonFallsBack);
        harness.Add("configs が空なら組み込み既定へフォールバック",      CatalogEmptyConfigsFallsBack);
        harness.Add("必須項目が欠けた構成は捨てて残りを使う",            CatalogInvalidEntryIsSkipped);
        harness.Add("id が重複したら後の方を無視する",                   CatalogDuplicateIdIsSkipped);
        harness.Add("未知の既定 id なら先頭を既定にする",                CatalogUnknownDefaultUsesFirst);
        harness.Add("構成フォルダが null なら組み込み既定",              CatalogNullDirFallsBack);

        // ── 選択 id の解決 ─────────────────────────────────
        harness.Add("Resolve は既知の id をそのまま返す",                ResolveKnownId);
        harness.Add("Resolve は未知の id を既定へ丸める",                ResolveUnknownIdFallsBackToDefault);
        harness.Add("Resolve は null / 空文字を既定へ丸める",            ResolveNullIdFallsBackToDefault);
        harness.Add("id の大文字小文字は区別しない",                     ResolveIdIsCaseInsensitive);

        // ── exe パスの解決 ─────────────────────────────────
        harness.Add("構成の target_dir が exe パスへ反映される",         LocatorUsesTargetDir);
        harness.Add("exe が未ビルドでもパスは返る",                      LocatorReturnsPathEvenIfMissing);
        harness.Add("環境変数が実在すれば最優先で採用される",            LocatorEnvOverrideWins);
        harness.Add("環境変数が実在しなければ無視される",                LocatorMissingEnvOverrideIsIgnored);
        harness.Add("エディタ exe の隣の SEED.exe が構成より優先される", LocatorSameDirWinsOverTarget);
        harness.Add("カレントからもリポジトリを探せる",                  LocatorFindsRepoFromCurrentDir);
        harness.Add("リポジトリが無ければ従来の 4 階層上へ落ちる",       LocatorFallsBackToLegacyDepth);
        harness.Add("NeedsBuild は exe の有無で判定する",                NeedsBuildChecksExistence);

        // ── cargo 引数 ─────────────────────────────────────
        harness.Add("cargo 引数は build --profile <プロファイル>",       CargoArgumentsUseProfile);
        harness.Add("dev も --profile dev と明示する",                   CargoArgumentsForDevAreExplicit);
        harness.Add("cargo_profile が空なら例外",                        CargoArgumentsRejectEmptyProfile);

        // ── cargo の作業ディレクトリ ───────────────────────
        harness.Add("target/<構成>/SEED.exe から runtime/ を解決する",   SourceDirFromTargetLayout);
        harness.Add("Cargo.toml が無ければ null",                        SourceDirWithoutManifestIsNull);

        return harness.Run();
    }

    // ============================================================
    //  カタログ: 正常系
    // ============================================================

    /// <summary>JSON に書いた構成一覧と既定 id がそのまま読めること。</summary>
    private static void CatalogLoadsConfigsAndDefault()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteFile(CatalogFileName, """
        {
          "format_version": 1,
          "default": "release",
          "configs": [
            { "id": "debug",   "label": "Debug",   "cargo_profile": "dev",     "target_dir": "debug",   "description": "D" },
            { "id": "release", "label": "Release", "cargo_profile": "release", "target_dir": "release", "description": "R" }
          ]
        }
        """);

        var catalog = RuntimeBuildConfigCatalog.Load(path);

        Check.Equal(0, catalog.Warnings.Count, "警告件数");
        Check.Equal(2, catalog.Configs.Count, "構成件数");
        Check.Equal(IdRelease, catalog.Default.Id, "既定 id");
        Check.Equal("dev", catalog.Configs[0].CargoProfile, "先頭のプロファイル");
        Check.Equal("release", catalog.Configs[1].TargetDir, "2 件目の target_dir");
        Check.Equal(Path.GetFullPath(path), catalog.SourcePath, "読み込み元パス");
    }

    /// <summary>
    /// リポジトリに入っている実物の runtime_build_configs.json が、
    /// 組み込み既定（フォールバック）と同じ内容であること。
    /// どちらか片方だけ直す事故（JSON を消したら挙動が変わる）を検知する。
    /// </summary>
    private static void ShippedCatalogMatchesBuiltIn()
    {
        // csproj で出力へコピーしている実ファイル
        var shippedPath = Path.Combine(AppContext.BaseDirectory, CatalogFileName);
        Check.True(File.Exists(shippedPath), $"同梱カタログが出力に無い: {shippedPath}");

        var shipped = RuntimeBuildConfigCatalog.Load(shippedPath);
        var builtIn = RuntimeBuildConfigCatalog.BuiltIn();

        Check.Equal(0, shipped.Warnings.Count, "同梱カタログの警告件数");
        Check.Equal(BuiltInConfigCount, shipped.Configs.Count, "同梱カタログの構成件数");
        Check.Equal(builtIn.Default.Id, shipped.Default.Id, "既定 id");

        for (var i = 0; i < builtIn.Configs.Count; i++)
        {
            var b = builtIn.Configs[i];
            var s = shipped.Configs[i];
            Check.Equal(b.Id,           s.Id,           $"[{i}] id");
            Check.Equal(b.Label,        s.Label,        $"[{i}] label");
            Check.Equal(b.CargoProfile, s.CargoProfile, $"[{i}] cargo_profile");
            Check.Equal(b.TargetDir,    s.TargetDir,    $"[{i}] target_dir");
            Check.Equal(b.Description,  s.Description,  $"[{i}] description");
        }
    }

    /// <summary>未知の format_version でも、読める範囲は読んで警告だけ出すこと。</summary>
    private static void CatalogFutureFormatVersionStillLoads()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteFile(CatalogFileName, """
        {
          "format_version": 999,
          "default": "debug",
          "configs": [
            { "id": "debug", "label": "Debug", "cargo_profile": "dev", "target_dir": "debug", "description": "D" }
          ]
        }
        """);

        var catalog = RuntimeBuildConfigCatalog.Load(path);

        Check.Equal(1, catalog.Configs.Count, "構成件数");
        Check.Equal(IdDebug, catalog.Default.Id, "既定 id");
        Check.True(catalog.Warnings.Count > 0, "format_version の警告が出ること");
    }

    // ============================================================
    //  カタログ: 異常系
    // ============================================================

    /// <summary>ファイルが無ければ組み込み既定になること。</summary>
    private static void CatalogMissingFileFallsBack()
    {
        using var tmp = new TempDir();
        var catalog = RuntimeBuildConfigCatalog.Load(tmp.Combine("no_such_file.json"));
        AssertIsBuiltIn(catalog);
    }

    /// <summary>JSON が壊れていても例外を投げず組み込み既定になること。</summary>
    private static void CatalogBrokenJsonFallsBack()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteFile(CatalogFileName, "{ これは JSON ではない ][ ");
        var catalog = RuntimeBuildConfigCatalog.Load(path);
        AssertIsBuiltIn(catalog);
    }

    /// <summary>configs が空配列なら組み込み既定になること（選択肢ゼロの UI を作らない）。</summary>
    private static void CatalogEmptyConfigsFallsBack()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteFile(CatalogFileName,
            """{ "format_version": 1, "default": "develop", "configs": [] }""");
        var catalog = RuntimeBuildConfigCatalog.Load(path);
        AssertIsBuiltIn(catalog);
    }

    /// <summary>必須項目が欠けた要素だけを捨て、他は生かすこと。</summary>
    private static void CatalogInvalidEntryIsSkipped()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteFile(CatalogFileName, """
        {
          "format_version": 1,
          "default": "develop",
          "configs": [
            { "id": "broken",  "label": "Broken",  "cargo_profile": "",        "target_dir": "broken"  },
            { "id": "develop", "label": "Develop", "cargo_profile": "develop", "target_dir": "develop" }
          ]
        }
        """);

        var catalog = RuntimeBuildConfigCatalog.Load(path);

        Check.Equal(1, catalog.Configs.Count, "有効な構成だけ残ること");
        Check.Equal(IdDevelop, catalog.Configs[0].Id, "残った構成");
        Check.True(catalog.Warnings.Count > 0, "捨てた理由が警告に残ること");
    }

    /// <summary>id が重複したら後の方を無視すること。</summary>
    private static void CatalogDuplicateIdIsSkipped()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteFile(CatalogFileName, """
        {
          "format_version": 1,
          "default": "develop",
          "configs": [
            { "id": "develop", "label": "Develop",  "cargo_profile": "develop", "target_dir": "develop" },
            { "id": "develop", "label": "Develop2", "cargo_profile": "dev",     "target_dir": "debug"   }
          ]
        }
        """);

        var catalog = RuntimeBuildConfigCatalog.Load(path);

        Check.Equal(1, catalog.Configs.Count, "構成件数");
        Check.Equal("Develop", catalog.Configs[0].Label, "先に書いた方が残ること");
        Check.True(catalog.Warnings.Count > 0, "重複の警告が出ること");
    }

    /// <summary>既定 id が configs に無ければ先頭を既定にすること。</summary>
    private static void CatalogUnknownDefaultUsesFirst()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteFile(CatalogFileName, """
        {
          "format_version": 1,
          "default": "no_such_config",
          "configs": [
            { "id": "debug",   "label": "Debug",   "cargo_profile": "dev",     "target_dir": "debug"   },
            { "id": "develop", "label": "Develop", "cargo_profile": "develop", "target_dir": "develop" }
          ]
        }
        """);

        var catalog = RuntimeBuildConfigCatalog.Load(path);

        Check.Equal(IdDebug, catalog.Default.Id, "先頭が既定になること");
        Check.True(catalog.Warnings.Count > 0, "未知の既定 id の警告が出ること");
    }

    /// <summary>構成フォルダが解決できない（null）なら組み込み既定になること。</summary>
    private static void CatalogNullDirFallsBack()
    {
        var catalog = RuntimeBuildConfigCatalog.LoadFromDir(null);
        AssertIsBuiltIn(catalog);
    }

    // ============================================================
    //  選択 id の解決
    // ============================================================

    /// <summary>既知の id はそのまま引けること。</summary>
    private static void ResolveKnownId()
    {
        var catalog = RuntimeBuildConfigCatalog.BuiltIn();
        Check.Equal(IdRelease, catalog.Resolve(IdRelease).Id, "解決結果");
        Check.True(catalog.Contains(IdDebug), "Contains(debug)");
    }

    /// <summary>
    /// カタログから消えた id が環境設定に残っていても、既定へ丸めて起動できること
    /// （カタログの編集・ダウングレードで起動不能にならないこと）。
    /// </summary>
    private static void ResolveUnknownIdFallsBackToDefault()
    {
        var catalog = RuntimeBuildConfigCatalog.BuiltIn();
        Check.Equal(catalog.Default.Id, catalog.Resolve("no_such_config").Id, "未知 id の解決結果");
        Check.Equal(IdDevelop, catalog.Default.Id, "組み込み既定は develop");
        Check.True(!catalog.Contains("no_such_config"), "Contains(未知)");
    }

    /// <summary>未選択（null / 空文字）なら既定になること。</summary>
    private static void ResolveNullIdFallsBackToDefault()
    {
        var catalog = RuntimeBuildConfigCatalog.BuiltIn();
        Check.Equal(catalog.Default.Id, catalog.Resolve(null).Id,  "null の解決結果");
        Check.Equal(catalog.Default.Id, catalog.Resolve("").Id,    "空文字の解決結果");
        Check.Equal(catalog.Default.Id, catalog.Resolve("   ").Id, "空白のみの解決結果");
    }

    /// <summary>id の大文字小文字が違っても同じ構成として引けること。</summary>
    private static void ResolveIdIsCaseInsensitive()
    {
        var catalog = RuntimeBuildConfigCatalog.BuiltIn();
        Check.Equal(IdDevelop, catalog.Resolve("DEVELOP").Id, "大文字 id の解決結果");
    }

    // ============================================================
    //  exe パスの解決
    // ============================================================

    /// <summary>構成の target_dir が exe パスに反映されること。</summary>
    private static void LocatorUsesTargetDir()
    {
        using var tmp = new TempDir();
        var repo      = CreateFakeRepo(tmp);
        var editorDir = tmp.CreateDirectory(@"repo\editor\bin\Debug\net9.0-windows");

        var catalog = RuntimeBuildConfigCatalog.BuiltIn();
        foreach (var cfg in catalog.Configs)
        {
            var path = RuntimeExeLocator.Resolve(cfg, editorDir, currentDir: null, envOverride: null);
            var expected = Path.GetFullPath(Path.Combine(
                repo, "runtime", "target", cfg.TargetDir, RuntimeExeLocator.RuntimeExeFileName));
            Check.Equal(expected, path, $"構成 {cfg.Id} の exe パス");
        }
    }

    /// <summary>未ビルド（exe が存在しない）でもパスは返ること。</summary>
    private static void LocatorReturnsPathEvenIfMissing()
    {
        using var tmp = new TempDir();
        CreateFakeRepo(tmp);
        var editorDir = tmp.CreateDirectory(@"repo\editor\bin\Debug\net9.0-windows");
        var cfg       = RuntimeBuildConfigCatalog.BuiltIn().Resolve(IdDevelop);

        var path = RuntimeExeLocator.Resolve(cfg, editorDir, currentDir: null, envOverride: null);

        Check.True(!File.Exists(path), "テスト前提: exe はまだ存在しない");
        Check.True(path.EndsWith(Path.Combine("develop", "SEED.exe"), StringComparison.OrdinalIgnoreCase),
                   $"未ビルドでも develop のパスが返ること: {path}");
        Check.True(RuntimeExeLocator.NeedsBuild(path), "NeedsBuild=true");
    }

    /// <summary>環境変数が実在するファイルを指していれば、構成より優先されること。</summary>
    private static void LocatorEnvOverrideWins()
    {
        using var tmp = new TempDir();
        CreateFakeRepo(tmp);
        var editorDir = tmp.CreateDirectory(@"repo\editor\bin\Debug\net9.0-windows");
        var overrideExe = tmp.WriteFile(@"measure\SEED.exe", "dummy");
        var cfg = RuntimeBuildConfigCatalog.BuiltIn().Resolve(IdDevelop);

        var path = RuntimeExeLocator.Resolve(cfg, editorDir, currentDir: null, envOverride: overrideExe);

        Check.Equal(Path.GetFullPath(overrideExe), path, "環境変数で指定した exe");
    }

    /// <summary>環境変数が実在しないパスなら無視され、構成のパスへ落ちること。</summary>
    private static void LocatorMissingEnvOverrideIsIgnored()
    {
        using var tmp = new TempDir();
        var repo      = CreateFakeRepo(tmp);
        var editorDir = tmp.CreateDirectory(@"repo\editor\bin\Debug\net9.0-windows");
        var cfg       = RuntimeBuildConfigCatalog.BuiltIn().Resolve(IdDevelop);

        var path = RuntimeExeLocator.Resolve(
            cfg, editorDir, currentDir: null, envOverride: tmp.Combine(@"missing\SEED.exe"));

        var expected = Path.GetFullPath(Path.Combine(repo, "runtime", "target", "develop", "SEED.exe"));
        Check.Equal(expected, path, "構成のパスへ落ちること");
    }

    /// <summary>
    /// 配布形態（エディタ exe の隣に SEED.exe がある）では、そちらが構成より優先されること。
    /// 配布物には構成別の target/ が無いため。
    /// </summary>
    private static void LocatorSameDirWinsOverTarget()
    {
        using var tmp = new TempDir();
        CreateFakeRepo(tmp);
        var editorDir = tmp.CreateDirectory(@"repo\editor\bin\Debug\net9.0-windows");
        var sideBySide = tmp.WriteFile(
            @"repo\editor\bin\Debug\net9.0-windows\SEED.exe", "dummy");
        var cfg = RuntimeBuildConfigCatalog.BuiltIn().Resolve(IdDevelop);

        var path = RuntimeExeLocator.Resolve(cfg, editorDir, currentDir: null, envOverride: null);

        Check.Equal(Path.GetFullPath(sideBySide), path, "exe の隣の SEED.exe");
    }

    /// <summary>
    /// エディタ exe が一時フォルダ（-p:OutputPath=... でのビルド）にあってリポジトリを
    /// 辿れない場合でも、カレントディレクトリからリポジトリを見つけられること。
    /// </summary>
    private static void LocatorFindsRepoFromCurrentDir()
    {
        using var tmp = new TempDir();
        var repo      = CreateFakeRepo(tmp);
        var strayDir  = tmp.CreateDirectory(@"elsewhere\out");   // リポジトリ外
        var cfg       = RuntimeBuildConfigCatalog.BuiltIn().Resolve(IdDevelop);

        var path = RuntimeExeLocator.Resolve(cfg, strayDir, currentDir: repo, envOverride: null);

        var expected = Path.GetFullPath(Path.Combine(repo, "runtime", "target", "develop", "SEED.exe"));
        Check.Equal(expected, path, "カレントから見つけたリポジトリの exe パス");
    }

    /// <summary>
    /// exe 側・カレント側のどちらからもリポジトリが見つからない場合は、
    /// 従来どおり exe から 4 階層上をリポジトリとみなすこと。
    /// </summary>
    private static void LocatorFallsBackToLegacyDepth()
    {
        using var tmp = new TempDir();
        // リポジトリ（runtime/Cargo.toml）を作らない
        var editorDir = tmp.CreateDirectory(@"a\b\c\d");
        var cfg       = RuntimeBuildConfigCatalog.BuiltIn().Resolve(IdDevelop);

        var path = RuntimeExeLocator.Resolve(cfg, editorDir, currentDir: null, envOverride: null);

        var expected = Path.GetFullPath(Path.Combine(
            tmp.Path, "runtime", "target", "develop", "SEED.exe"));
        Check.Equal(expected, path, "4 階層上をリポジトリとみなすこと");
    }

    /// <summary>NeedsBuild が exe の実在で判定すること。</summary>
    private static void NeedsBuildChecksExistence()
    {
        using var tmp = new TempDir();
        var exists = tmp.WriteFile(@"built\SEED.exe", "dummy");

        Check.True(!RuntimeExeLocator.NeedsBuild(exists),               "存在する exe は要ビルドでない");
        Check.True(RuntimeExeLocator.NeedsBuild(tmp.Combine("x.exe")),  "存在しない exe は要ビルド");
        Check.True(RuntimeExeLocator.NeedsBuild(""),                    "空文字は要ビルド");
    }

    // ============================================================
    //  cargo 引数
    // ============================================================

    /// <summary>構成のプロファイルが --profile へ渡ること。</summary>
    private static void CargoArgumentsUseProfile()
    {
        var catalog = RuntimeBuildConfigCatalog.BuiltIn();
        Check.Equal("build --profile develop", CargoBuildCommand.BuildArguments(catalog.Resolve(IdDevelop)), "develop");
        Check.Equal("build --profile release", CargoBuildCommand.BuildArguments(catalog.Resolve(IdRelease)), "release");
        Check.Equal("cargo", CargoBuildCommand.Executable, "実行ファイル名");
    }

    /// <summary>
    /// dev も「引数なし」ではなく --profile dev と明示すること
    /// （どの構成でビルドしたかがログに残る）。
    /// </summary>
    private static void CargoArgumentsForDevAreExplicit()
    {
        var cfg = RuntimeBuildConfigCatalog.BuiltIn().Resolve(IdDebug);
        Check.Equal("dev", cfg.CargoProfile, "debug 構成のプロファイル名");
        Check.Equal("build --profile dev", CargoBuildCommand.BuildArguments(cfg), "debug の引数");
    }

    /// <summary>cargo_profile が空の構成は引数を組み立てられず例外になること。</summary>
    private static void CargoArgumentsRejectEmptyProfile()
    {
        var broken = RuntimeBuildConfig.Create("x", "X", "", "x", "");
        var threw = false;
        try { CargoBuildCommand.BuildArguments(broken); }
        catch (ArgumentException) { threw = true; }
        Check.True(threw, "ArgumentException が投げられること");
    }

    // ============================================================
    //  cargo の作業ディレクトリ
    // ============================================================

    /// <summary>runtime/target/&lt;構成&gt;/SEED.exe から runtime/ を解決できること。</summary>
    private static void SourceDirFromTargetLayout()
    {
        using var tmp = new TempDir();
        var repo = CreateFakeRepo(tmp);
        var exe  = Path.Combine(repo, "runtime", "target", "develop", "SEED.exe");

        var dir = RuntimeSourceDirLocator.FromExePath(exe);

        Check.Equal(Path.GetFullPath(Path.Combine(repo, "runtime")), dir, "runtime/ のパス");
    }

    /// <summary>Cargo.toml が無い配置（配布形態）では null になること。</summary>
    private static void SourceDirWithoutManifestIsNull()
    {
        using var tmp = new TempDir();
        var exe = tmp.WriteFile(@"dist\SEED.exe", "dummy");

        Check.True(RuntimeSourceDirLocator.FromExePath(exe) is null, "配布形態では null");
        Check.True(RuntimeSourceDirLocator.FromExePath(null) is null, "null 入力は null");
        Check.True(RuntimeSourceDirLocator.FromExePath("") is null,   "空文字は null");
    }

    // ============================================================
    //  ヘルパー
    // ============================================================

    /// <summary>
    /// 一時フォルダ配下に「runtime/Cargo.toml を持つリポジトリ」を作り、そのルートを返す。
    /// </summary>
    /// <param name="tmp">一時フォルダ。</param>
    private static string CreateFakeRepo(TempDir tmp)
    {
        tmp.WriteFile(@"repo\runtime\Cargo.toml", "[package]\nname = \"SEED\"\n");
        return Path.GetFullPath(tmp.Combine("repo"));
    }

    /// <summary>カタログが組み込み既定（debug / develop / release、既定 develop）であることを表明する。</summary>
    /// <param name="catalog">検査するカタログ。</param>
    private static void AssertIsBuiltIn(RuntimeBuildConfigCatalog catalog)
    {
        Check.Equal(BuiltInConfigCount, catalog.Configs.Count, "組み込み既定の構成件数");
        Check.Equal(IdDevelop, catalog.Default.Id, "組み込み既定の既定 id");
        Check.True(catalog.SourcePath is null, "組み込み既定では SourcePath が null");
        Check.True(catalog.Warnings.Count > 0, "フォールバックの理由が警告に残ること");
        Check.True(catalog.Configs.Any(c => c.Id == IdDebug),   "debug が含まれる");
        Check.True(catalog.Configs.Any(c => c.Id == IdRelease), "release が含まれる");
    }
}
