using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SEEDEditor.Headless;
using SEEDEditor.Project;
using SEEDEditor.Startup;
using SpriteRigTests;   // TestHarness / Check（テストランナーは SpriteRigTests と共用）

namespace ProjectSystemTests;

/// <summary>
/// プロジェクト概念（docs/project_system.md）の単体テスト。
///
/// 検証の柱:
///   1. .seedproj の往復（保存 → 読み込みで値が保たれる／欠けたキーが補完される）
///   2. ProjectPaths の導出（assets / plugins / cache / save / logs / build）
///      ＝ ランタイムが「assets の親」から導く位置と一致していること
///   3. 起動引数の解析（--project / --project= / 位置引数 / 環境変数の優先順位）
///   4. 起動時のプロジェクト解決（明示指定・ヘッドレス・スタート画面の振り分け）
///   5. RecentProjectsStore の往復と、旧 recent_projects.json（.scene 配列）の移行
///   6. FileAssociation のレジストリ値組み立て
///   7. ProjectCreator が一時フォルダに一式を生成すること
/// </summary>
public static class Program
{
    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();

        // ── .seedproj ────────────────────────────────────────
        harness.Add(".seedproj は保存 → 読み込みで内容が保たれる",       SeedProjectRoundTrip);
        harness.Add(".seedproj の欠けたキーは既定値で補完される",         SeedProjectMissingKeysAreFilled);
        harness.Add(".seedproj の未知キーは保存で失われない",             SeedProjectUnknownKeysSurvive);
        harness.Add("存在しない .seedproj の読み込みは例外になる",        SeedProjectMissingFileThrows);
        harness.Add("壊れた .seedproj の読み込みは例外になる",            SeedProjectBrokenJsonThrows);
        harness.Add("新しい format_version は拒否される",                 SeedProjectFutureVersionThrows);
        harness.Add("フォルダから .seedproj を探せる",                    SeedProjectFindInDirectory);

        // ── ProjectPaths ────────────────────────────────────
        harness.Add("ProjectPaths が各フォルダを導出する",                ProjectPathsDerivation);
        harness.Add("ProjectPaths はランタイムの導出（assets の親）と一致する",
                                                                          ProjectPathsMatchRuntimeLayout);
        harness.Add("EnsureDirectories は assets と plugins だけ作る",    EnsureDirectoriesCreatesOnlyTwo);
        harness.Add("assets_dir の指定がパスへ反映される",                ProjectPathsCustomAssetsDir);

        // ── 起動引数 ────────────────────────────────────────
        harness.Add("--project <path> を解析できる",                      ArgsProjectSpaceForm);
        harness.Add("--project=<path> を解析できる",                      ArgsProjectInlineForm);
        harness.Add("位置引数の .seedproj を解析できる",                  ArgsProjectPositional);
        harness.Add("環境変数 SEED_PROJECT を解析できる",                 ArgsProjectEnv);
        harness.Add("プロジェクト指定の優先順位は --project > 位置引数 > 環境変数",
                                                                          ArgsProjectPriority);
        harness.Add("--project にフォルダを渡すと中の .seedproj を指す",  ArgsProjectFolderForm);
        harness.Add("--project は他のオプションと併用できる",             ArgsProjectWithOtherOptions);
        harness.Add("プロジェクト未指定なら ProjectFilePath は null",     ArgsProjectAbsent);

        // ── 起動時の解決 ────────────────────────────────────
        harness.Add("実在する指定プロジェクトはそのまま開く",             ResolveOpensRequestedProject);
        harness.Add("指定が見つからない場合はスタート画面＋エラー",       ResolveMissingRequestedShowsStart);
        harness.Add("ヘッドレスで指定が見つからない場合は終了",           ResolveMissingRequestedHeadlessFails);
        harness.Add("ヘッドレス無指定は最近の先頭で実在するものを使う",   ResolveHeadlessUsesRecent);
        harness.Add("ヘッドレス無指定で候補ゼロなら終了",                 ResolveHeadlessNoCandidateFails);
        harness.Add("通常起動で無指定ならスタート画面",                   ResolveNoRequestShowsStart);

        // ── 最近のプロジェクト ──────────────────────────────
        harness.Add("RecentProjectsStore は保存 → 読み込みで往復する",    RecentStoreRoundTrip);
        harness.Add("同じプロジェクトの再追加は先頭へ移動する",           RecentStoreReAddMovesToTop);
        harness.Add("Remove で 1 件だけ外れる",                           RecentStoreRemove);
        harness.Add("最大件数を超えたら古いものが捨てられる",             RecentStoreTrimsToMax);
        harness.Add("旧 recent_projects.json（.scene 配列）は移行される", RecentStoreMigratesLegacy);
        harness.Add("新形式の recent_projects.json は移行されない",       RecentStoreDoesNotMigrateNewFormat);

        // ── 関連付けの値 ────────────────────────────────────
        harness.Add("FileAssociation の値が HKCU 配下で組み立てられる",   AssociationValues);
        harness.Add("空白を含む exe パスは引用符で囲まれる",              AssociationValuesQuotesSpaces);

        // ── プロジェクト生成 ────────────────────────────────
        harness.Add("ProjectCreator が一式を生成する",                    CreatorGeneratesFullSet);
        harness.Add("生成した .seedproj はそのまま開ける",                CreatorOutputIsLoadable);
        harness.Add("生成後フックが呼ばれる",                             CreatorInvokesHook);
        harness.Add("不正な名前・空でない作成先は拒否される",             CreatorRejectsInvalidInput);

        return harness.Run();
    }

    // ============================================================
    //  .seedproj
    // ============================================================

    /// <summary>保存した内容がそのまま読み戻せる。</summary>
    private static void SeedProjectRoundTrip()
    {
        using var temp = new TempDir();
        var path = temp.Combine("Sample" + SeedProjectFile.EXTENSION);

        var created = SeedProjectFile.Create(
            "Sample", "サンプル ゲーム", engineVersion: "1.2.3",
            createdAt: new DateTimeOffset(2026, 9, 11, 1, 2, 3, TimeSpan.Zero));
        created.Save(path);

        var loaded = SeedProjectFile.Load(path);
        Check.Equal(SeedProjectFile.CURRENT_FORMAT_VERSION, loaded.FormatVersion, "format_version");
        Check.Equal("Sample",          loaded.Name,          "name");
        Check.Equal("サンプル ゲーム", loaded.DisplayName,   "display_name");
        Check.Equal("1.2.3",           loaded.EngineVersion, "engine_version");
        Check.Equal("assets",          loaded.AssetsDir,     "assets_dir");
        Check.Equal("plugins",         loaded.PluginsDir,    "plugins_dir");
        Check.True(loaded.CreatedAt.StartsWith("2026-09-11"), "created_at が ISO 8601 で記録される");
        Check.Equal("サンプル ゲーム", loaded.EffectiveDisplayName, "EffectiveDisplayName");
    }

    /// <summary>手書きの最小 JSON でも既定値で補完されて読める。</summary>
    private static void SeedProjectMissingKeysAreFilled()
    {
        using var temp = new TempDir();
        var path = temp.Combine("Minimal" + SeedProjectFile.EXTENSION);
        File.WriteAllText(path, "{ \"format_version\": 1 }");

        var loaded = SeedProjectFile.Load(path);
        Check.Equal("Minimal", loaded.Name,       "name はファイル名で補完される");
        Check.Equal("assets",  loaded.AssetsDir,  "assets_dir の既定値");
        Check.Equal("plugins", loaded.PluginsDir, "plugins_dir の既定値");
        Check.Equal("Minimal", loaded.EffectiveDisplayName, "display_name が空なら name を使う");
    }

    /// <summary>新しいエディタが足したキーを、古い側が保存で消さない。</summary>
    private static void SeedProjectUnknownKeysSurvive()
    {
        using var temp = new TempDir();
        var path = temp.Combine("Extra" + SeedProjectFile.EXTENSION);
        File.WriteAllText(path,
            "{ \"format_version\": 1, \"name\": \"Extra\", \"future_key\": 42 }");

        var loaded = SeedProjectFile.Load(path);
        loaded.Save(path);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Check.True(doc.RootElement.TryGetProperty("future_key", out var el), "未知キーが残っている");
        Check.Equal(42, el.GetInt32(), "未知キーの値");
    }

    /// <summary>存在しないファイルは例外。</summary>
    private static void SeedProjectMissingFileThrows()
    {
        using var temp = new TempDir();
        ExpectThrows<SeedProjectFileException>(
            () => SeedProjectFile.Load(temp.Combine("nope" + SeedProjectFile.EXTENSION)),
            "存在しない .seedproj");
    }

    /// <summary>JSON として壊れていれば例外。</summary>
    private static void SeedProjectBrokenJsonThrows()
    {
        using var temp = new TempDir();
        var path = temp.Combine("Broken" + SeedProjectFile.EXTENSION);
        File.WriteAllText(path, "{ this is not json");

        ExpectThrows<SeedProjectFileException>(
            () => SeedProjectFile.Load(path), "壊れた .seedproj");

        // TryLoad は投げずに理由を返す。
        Check.True(!SeedProjectFile.TryLoad(path, out var file, out var error), "TryLoad は false");
        Check.Equal(null, file, "失敗時の内容は null");
        Check.True(!string.IsNullOrEmpty(error), "失敗理由が入る");
    }

    /// <summary>このエディタより新しい形式は拒否する（黙って壊さない）。</summary>
    private static void SeedProjectFutureVersionThrows()
    {
        using var temp = new TempDir();
        var path = temp.Combine("Future" + SeedProjectFile.EXTENSION);
        File.WriteAllText(path,
            $"{{ \"format_version\": {SeedProjectFile.CURRENT_FORMAT_VERSION + 1}, \"name\": \"Future\" }}");

        ExpectThrows<SeedProjectFileException>(
            () => SeedProjectFile.Load(path), "新しい format_version");
    }

    /// <summary>フォルダ名と同じ stem を優先して .seedproj を見つける。</summary>
    private static void SeedProjectFindInDirectory()
    {
        using var temp = new TempDir();
        var dir = temp.CreateSubDirectory("MyGame");
        File.WriteAllText(Path.Combine(dir, "Other"  + SeedProjectFile.EXTENSION), "{}");
        File.WriteAllText(Path.Combine(dir, "MyGame" + SeedProjectFile.EXTENSION), "{}");

        var found = SeedProjectFile.FindInDirectory(dir);
        Check.Equal("MyGame" + SeedProjectFile.EXTENSION,
                    Path.GetFileName(found ?? ""), "フォルダ名と同じ stem が優先される");

        Check.Equal(null, SeedProjectFile.FindInDirectory(temp.Combine("no_such_dir")),
                    "存在しないフォルダは null");
    }

    // ============================================================
    //  ProjectPaths
    // ============================================================

    /// <summary>各フォルダがプロジェクトルート直下に導出される。</summary>
    private static void ProjectPathsDerivation()
    {
        using var temp = new TempDir();
        var root = temp.CreateSubDirectory("Game");
        var file = SeedProjectFile.Create("Game");
        var paths = ProjectPaths.FromProjectFile(
            Path.Combine(root, "Game" + SeedProjectFile.EXTENSION), file);

        Check.Equal(root,                              paths.RootDir,    "RootDir");
        Check.Equal(Path.Combine(root, "assets"),      paths.AssetsDir,  "AssetsDir");
        Check.Equal(Path.Combine(root, "plugins"),     paths.PluginsDir, "PluginsDir");
        Check.Equal(Path.Combine(root, "cache"),       paths.CacheDir,   "CacheDir");
        Check.Equal(Path.Combine(root, "save"),        paths.SaveDir,    "SaveDir");
        Check.Equal(Path.Combine(root, "logs"),        paths.LogsDir,    "LogsDir");
        Check.Equal(Path.Combine(root, "build"),       paths.BuildDir,   "BuildDir");
        Check.Equal(Path.Combine(root, "build", "windows"),
                    paths.BuildDirFor("windows"), "BuildDirFor");
        Check.Equal("Game", paths.Name,        "Name");
        Check.Equal("Game", paths.DisplayName, "DisplayName（display_name 未設定なら name）");
    }

    /// <summary>
    /// ランタイムは「アセットルートの親」から cache / save / plugins を導く
    /// （asset_cache.rs / save/path.rs / app_init.rs）。その導出と一致することを固定する。
    /// </summary>
    private static void ProjectPathsMatchRuntimeLayout()
    {
        using var temp = new TempDir();
        var root = temp.CreateSubDirectory("Game");
        var paths = ProjectPaths.FromProjectFile(
            Path.Combine(root, "Game" + SeedProjectFile.EXTENSION), SeedProjectFile.Create("Game"));

        // ランタイム側の導出を再現する: assets の親 + 固定名
        var assetsParent = Path.GetDirectoryName(paths.AssetsDir)!;
        Check.Equal(Path.Combine(assetsParent, "cache"),   paths.CacheDir,   "cache はアセットの親直下");
        Check.Equal(Path.Combine(assetsParent, "save"),    paths.SaveDir,    "save はアセットの親直下");
        Check.Equal(Path.Combine(assetsParent, "plugins"), paths.PluginsDir, "plugins はアセットの親直下");
    }

    /// <summary>EnsureDirectories は assets と plugins だけ作る（生成物は作らない）。</summary>
    private static void EnsureDirectoriesCreatesOnlyTwo()
    {
        using var temp = new TempDir();
        var root  = temp.Combine("New");
        var paths = ProjectPaths.FromProjectFile(
            Path.Combine(root, "New" + SeedProjectFile.EXTENSION), SeedProjectFile.Create("New"));

        paths.EnsureDirectories();

        Check.True(Directory.Exists(paths.AssetsDir),   "assets が作られる");
        Check.True(Directory.Exists(paths.PluginsDir),  "plugins が作られる");
        Check.True(!Directory.Exists(paths.CacheDir),   "cache は作らない");
        Check.True(!Directory.Exists(paths.SaveDir),    "save は作らない");
        Check.True(!Directory.Exists(paths.LogsDir),    "logs は作らない");
        Check.True(!Directory.Exists(paths.BuildDir),   "build は作らない");
    }

    /// <summary>assets_dir を変えるとアセットルートだけが動く（生成物はルート直下のまま）。</summary>
    private static void ProjectPathsCustomAssetsDir()
    {
        using var temp = new TempDir();
        var root = temp.CreateSubDirectory("Custom");
        var file = SeedProjectFile.Create("Custom");
        file.AssetsDir = "content";

        var paths = ProjectPaths.FromProjectFile(
            Path.Combine(root, "Custom" + SeedProjectFile.EXTENSION), file);

        Check.Equal(Path.Combine(root, "content"), paths.AssetsDir, "assets_dir が反映される");
        Check.Equal(Path.Combine(root, "cache"),   paths.CacheDir,  "cache はルート直下のまま");
    }

    // ============================================================
    //  起動引数の解析
    // ============================================================

    /// <summary><c>--project &lt;path&gt;</c> 形式。</summary>
    private static void ArgsProjectSpaceForm()
    {
        var expected = Path.GetFullPath(@"C:\games\A\A" + SeedProjectFile.EXTENSION);
        var result = EditorStartupOptions.ParseArgs(
            new[] { "--project", @"C:\games\A\A" + SeedProjectFile.EXTENSION });
        Check.Equal(expected, result.ProjectFilePath, "--project <path>");
    }

    /// <summary><c>--project=&lt;path&gt;</c> 形式。</summary>
    private static void ArgsProjectInlineForm()
    {
        var expected = Path.GetFullPath(@"C:\games\B\B" + SeedProjectFile.EXTENSION);
        var result = EditorStartupOptions.ParseArgs(
            new[] { "--project=" + @"C:\games\B\B" + SeedProjectFile.EXTENSION });
        Check.Equal(expected, result.ProjectFilePath, "--project=<path>");
    }

    /// <summary>位置引数（エクスプローラーのダブルクリック経路）。</summary>
    private static void ArgsProjectPositional()
    {
        var expected = Path.GetFullPath(@"C:\games\C\C" + SeedProjectFile.EXTENSION);
        var result = EditorStartupOptions.ParseArgs(
            new[] { @"C:\games\C\C" + SeedProjectFile.EXTENSION });
        Check.Equal(expected, result.ProjectFilePath, "位置引数");
    }

    /// <summary>環境変数 SEED_PROJECT。</summary>
    private static void ArgsProjectEnv()
    {
        var expected = Path.GetFullPath(@"C:\games\D\D" + SeedProjectFile.EXTENSION);
        var result = EditorStartupOptions.ParseArgs(
            Array.Empty<string>(),
            name => name == "SEED_PROJECT" ? @"C:\games\D\D" + SeedProjectFile.EXTENSION : null);
        Check.Equal(expected, result.ProjectFilePath, "環境変数");
    }

    /// <summary>--project > 位置引数 > 環境変数 の優先順位。</summary>
    private static void ArgsProjectPriority()
    {
        var explicitPath   = @"C:\games\explicit\explicit" + SeedProjectFile.EXTENSION;
        var positionalPath = @"C:\games\positional\positional" + SeedProjectFile.EXTENSION;
        var envPath        = @"C:\games\env\env" + SeedProjectFile.EXTENSION;

        Func<string, string?> env = name => name == "SEED_PROJECT" ? envPath : null;

        // 3 つとも指定 → --project が勝つ
        var all = EditorStartupOptions.ParseArgs(
            new[] { positionalPath, "--project", explicitPath }, env);
        Check.Equal(Path.GetFullPath(explicitPath), all.ProjectFilePath, "--project が最優先");

        // 位置引数と環境変数 → 位置引数が勝つ
        var positionalAndEnv = EditorStartupOptions.ParseArgs(new[] { positionalPath }, env);
        Check.Equal(Path.GetFullPath(positionalPath), positionalAndEnv.ProjectFilePath,
                    "位置引数が環境変数より優先");

        // 環境変数だけ
        var envOnly = EditorStartupOptions.ParseArgs(Array.Empty<string>(), env);
        Check.Equal(Path.GetFullPath(envPath), envOnly.ProjectFilePath, "環境変数が最後の砦");
    }

    /// <summary>フォルダを渡したら中の .seedproj を指す。</summary>
    private static void ArgsProjectFolderForm()
    {
        using var temp = new TempDir();
        var dir  = temp.CreateSubDirectory("FolderGame");
        var file = Path.Combine(dir, "FolderGame" + SeedProjectFile.EXTENSION);
        File.WriteAllText(file, "{}");

        var found = EditorStartupOptions.ParseArgs(new[] { "--project", dir });
        Check.Equal(file, found.ProjectFilePath, "フォルダ内の .seedproj を指す");

        // .seedproj が無いフォルダでも「そのフォルダのプロジェクト」を候補として返す
        // （呼び出し側が「見つかりません」と出せるようにするため）。
        var emptyDir = temp.CreateSubDirectory("EmptyGame");
        var guessed  = EditorStartupOptions.ParseArgs(new[] { "--project", emptyDir });
        Check.Equal(Path.Combine(emptyDir, "EmptyGame" + SeedProjectFile.EXTENSION),
                    guessed.ProjectFilePath, "候補パスを返す");
    }

    /// <summary>他のオプションと混ざっても取りこぼさない。</summary>
    private static void ArgsProjectWithOtherOptions()
    {
        var projectPath = @"C:\games\Mixed\Mixed" + SeedProjectFile.EXTENSION;
        var result = EditorStartupOptions.ParseArgs(new[]
        {
            "--headless", "--ai-port", "7391", "--project", projectPath, "--ai-token", "abc",
        });

        Check.Equal(Path.GetFullPath(projectPath), result.ProjectFilePath, "プロジェクト");
        Check.True(result.IsHeadless, "--headless");
        Check.Equal(7391,  result.AiPort ?? 0, "--ai-port");
        Check.Equal("abc", result.AiToken,     "--ai-token");
    }

    /// <summary>指定が無ければ null（黙ってどこかを開かない）。</summary>
    private static void ArgsProjectAbsent()
    {
        var result = EditorStartupOptions.ParseArgs(new[] { "--headless" });
        Check.Equal(null, result.ProjectFilePath, "指定なしは null");
        Check.True(result.IsHeadless, "--headless は効いている");
    }

    // ============================================================
    //  起動時のプロジェクト解決
    // ============================================================

    /// <summary>実在する指定プロジェクトはそのまま開く。</summary>
    private static void ResolveOpensRequestedProject()
    {
        using var temp = new TempDir();
        var path = temp.Combine("A" + SeedProjectFile.EXTENSION);
        File.WriteAllText(path, "{}");

        var result = ProjectStartupResolver.Resolve(path, isHeadless: false, Array.Empty<string>());
        Check.Equal(ProjectStartupKind.OpenProject, result.Kind, "種別");
        Check.Equal(path, result.ProjectFilePath, "開くパス");
        Check.Equal(null, result.ErrorMessage, "エラーなし");
    }

    /// <summary>指定が見つからない場合はスタート画面へ（エラー文つき）。</summary>
    private static void ResolveMissingRequestedShowsStart()
    {
        using var temp = new TempDir();
        var missing = temp.Combine("missing" + SeedProjectFile.EXTENSION);
        var present = temp.Combine("present" + SeedProjectFile.EXTENSION);
        File.WriteAllText(present, "{}");

        var result = ProjectStartupResolver.Resolve(missing, isHeadless: false, new[] { present });
        Check.Equal(ProjectStartupKind.ShowStartWindow, result.Kind, "種別");
        Check.Equal(null, result.ProjectFilePath, "最近の一覧へ勝手に倒さない");
        Check.True(!string.IsNullOrEmpty(result.ErrorMessage), "理由が入る");
    }

    /// <summary>ヘッドレスで指定が見つからない場合は終了（別プロジェクトを開かない）。</summary>
    private static void ResolveMissingRequestedHeadlessFails()
    {
        using var temp = new TempDir();
        var missing = temp.Combine("missing" + SeedProjectFile.EXTENSION);
        var present = temp.Combine("present" + SeedProjectFile.EXTENSION);
        File.WriteAllText(present, "{}");

        var result = ProjectStartupResolver.Resolve(missing, isHeadless: true, new[] { present });
        Check.Equal(ProjectStartupKind.FailAndExit, result.Kind, "種別");
        Check.Equal(null, result.ProjectFilePath, "最近の一覧へ勝手に倒さない");
    }

    /// <summary>ヘッドレス無指定は「最近の先頭で実在するもの」を使う。</summary>
    private static void ResolveHeadlessUsesRecent()
    {
        using var temp = new TempDir();
        var gone    = temp.Combine("gone"    + SeedProjectFile.EXTENSION);
        var present = temp.Combine("present" + SeedProjectFile.EXTENSION);
        File.WriteAllText(present, "{}");

        var result = ProjectStartupResolver.Resolve(null, isHeadless: true, new[] { gone, present });
        Check.Equal(ProjectStartupKind.OpenProject, result.Kind, "種別");
        Check.Equal(present, result.ProjectFilePath, "実在する先頭を使う");
    }

    /// <summary>ヘッドレス無指定で候補が無ければ終了。</summary>
    private static void ResolveHeadlessNoCandidateFails()
    {
        var result = ProjectStartupResolver.Resolve(null, isHeadless: true, Array.Empty<string>());
        Check.Equal(ProjectStartupKind.FailAndExit, result.Kind, "種別");
        Check.True(!string.IsNullOrEmpty(result.ErrorMessage), "理由が入る");
    }

    /// <summary>通常起動で無指定ならスタート画面（最近があっても勝手に開かない）。</summary>
    private static void ResolveNoRequestShowsStart()
    {
        using var temp = new TempDir();
        var present = temp.Combine("present" + SeedProjectFile.EXTENSION);
        File.WriteAllText(present, "{}");

        var result = ProjectStartupResolver.Resolve(null, isHeadless: false, new[] { present });
        Check.Equal(ProjectStartupKind.ShowStartWindow, result.Kind, "種別");
    }

    // ============================================================
    //  最近のプロジェクト
    // ============================================================

    /// <summary>追加した内容がそのまま読み戻せる（新しい順）。</summary>
    private static void RecentStoreRoundTrip()
    {
        using var temp = new TempDir();
        var store = new RecentProjectsStore(temp.Path);

        var at = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        store.Add(temp.Combine("A" + SeedProjectFile.EXTENSION), "ゲーム A", at);
        store.Add(temp.Combine("B" + SeedProjectFile.EXTENSION), "ゲーム B", at.AddMinutes(1));

        var entries = store.Load();
        Check.Equal(2, entries.Count, "件数");
        Check.Equal("ゲーム B", entries[0].Name, "最後に追加したものが先頭");
        Check.Equal("ゲーム A", entries[1].Name, "2 番目");
        Check.Equal(temp.Combine("B" + SeedProjectFile.EXTENSION), entries[0].Path, "パス");
        Check.True(entries[0].LastOpenedAt is not null, "last_opened が日時として読める");

        // 保存形式（format_version + entries）を固定する。
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(temp.Path, RecentProjectsStore.FILE_NAME)));
        Check.Equal(RecentProjectsStore.CURRENT_FORMAT_VERSION,
                    doc.RootElement.GetProperty("format_version").GetInt32(), "format_version");
        Check.Equal(2, doc.RootElement.GetProperty("entries").GetArrayLength(), "entries の件数");
    }

    /// <summary>同じプロジェクトを開き直したら先頭へ移動し、重複しない。</summary>
    private static void RecentStoreReAddMovesToTop()
    {
        using var temp = new TempDir();
        var store = new RecentProjectsStore(temp.Path);
        var a = temp.Combine("A" + SeedProjectFile.EXTENSION);
        var b = temp.Combine("B" + SeedProjectFile.EXTENSION);

        store.Add(a, "A");
        store.Add(b, "B");
        store.Add(a, "A");

        var entries = store.Load();
        Check.Equal(2, entries.Count, "重複しない");
        Check.Equal(a, entries[0].Path, "開き直したものが先頭");
    }

    /// <summary>Remove は指定の 1 件だけ外す。</summary>
    private static void RecentStoreRemove()
    {
        using var temp = new TempDir();
        var store = new RecentProjectsStore(temp.Path);
        var a = temp.Combine("A" + SeedProjectFile.EXTENSION);
        var b = temp.Combine("B" + SeedProjectFile.EXTENSION);

        store.Add(a, "A");
        store.Add(b, "B");
        store.Remove(a);

        var entries = store.Load();
        Check.Equal(1, entries.Count, "件数");
        Check.Equal(b, entries[0].Path, "残ったもの");
    }

    /// <summary>上限を超えたら古いものから捨てる。</summary>
    private static void RecentStoreTrimsToMax()
    {
        using var temp = new TempDir();
        var store = new RecentProjectsStore(temp.Path);

        for (int i = 0; i <= RecentProjectsStore.MAX_ENTRIES; i++)
            store.Add(temp.Combine($"P{i}{SeedProjectFile.EXTENSION}"), $"P{i}");

        var entries = store.Load();
        Check.Equal(RecentProjectsStore.MAX_ENTRIES, entries.Count, "上限で頭打ち");
        Check.Equal(temp.Combine($"P{RecentProjectsStore.MAX_ENTRIES}{SeedProjectFile.EXTENSION}"),
                    entries[0].Path, "最後に追加したものが先頭");
    }

    /// <summary>
    /// 旧 recent_projects.json（.scene パスの文字列配列）は
    /// recent_scenes.json へ移され、新形式で作り直される。
    /// </summary>
    private static void RecentStoreMigratesLegacy()
    {
        using var temp = new TempDir();
        var legacyPath = Path.Combine(temp.Path, RecentProjectsStore.FILE_NAME);
        var scenesPath = Path.Combine(temp.Path, RecentProjectsStore.LEGACY_SCENES_FILE_NAME);
        var legacyJson = "[\"C:\\\\a\\\\x.scene\",\"C:\\\\a\\\\y.scene\"]";
        File.WriteAllText(legacyPath, legacyJson);

        var store   = new RecentProjectsStore(temp.Path);
        var entries = store.Load();

        Check.Equal(0, entries.Count, "旧一覧はプロジェクトとしては 0 件");
        Check.True(File.Exists(scenesPath), "recent_scenes.json へ移されている");
        Check.True(!File.Exists(legacyPath), "旧 recent_projects.json は消えている");

        using var doc = JsonDocument.Parse(File.ReadAllText(scenesPath));
        Check.Equal(JsonValueKind.Array, doc.RootElement.ValueKind, "移行先は .scene の配列");
        Check.Equal(2, doc.RootElement.GetArrayLength(), "移行された件数");

        // 移行後に追加したら新形式で書かれる。
        store.Add(temp.Combine("A" + SeedProjectFile.EXTENSION), "A");
        using var newDoc = JsonDocument.Parse(File.ReadAllText(legacyPath));
        Check.Equal(JsonValueKind.Object, newDoc.RootElement.ValueKind, "新形式はオブジェクト");
    }

    /// <summary>新形式のファイルは移行対象にならない（誤検知しない）。</summary>
    private static void RecentStoreDoesNotMigrateNewFormat()
    {
        using var temp = new TempDir();
        var store = new RecentProjectsStore(temp.Path);
        store.Add(temp.Combine("A" + SeedProjectFile.EXTENSION), "A");

        // 別インスタンスから読み直しても（＝移行判定を再び通っても）中身は残る。
        var reopened = new RecentProjectsStore(temp.Path).Load();
        Check.Equal(1, reopened.Count, "新形式は移行されず残る");
        Check.True(!File.Exists(Path.Combine(temp.Path, RecentProjectsStore.LEGACY_SCENES_FILE_NAME)),
                   "recent_scenes.json は作られない");
    }

    // ============================================================
    //  関連付けの値
    // ============================================================

    /// <summary>HKCU 配下のキーと値が仕様どおりに組み立てられる。</summary>
    private static void AssociationValues()
    {
        var values = FileAssociationValues.Build(@"C:\Tools\SEEDEditor.exe");

        Check.Equal(SeedProjectFile.EXTENSION, values.Extension, "拡張子");
        Check.Equal("SEED.Project", values.ProgId, "ProgID");
        Check.Equal(@"Software\Classes\.seedproj", values.ExtensionKeyPath, "拡張子キー");
        Check.Equal(@"Software\Classes\SEED.Project", values.ProgIdKeyPath, "ProgID キー");
        Check.Equal(@"Software\Classes\SEED.Project\DefaultIcon",
                    values.DefaultIconKeyPath, "DefaultIcon キー");
        Check.Equal(@"Software\Classes\SEED.Project\shell\open\command",
                    values.OpenCommandKeyPath, "open コマンドキー");
        Check.Equal("\"C:\\Tools\\SEEDEditor.exe\",0", values.DefaultIconValue, "DefaultIcon 値");
        Check.Equal("\"C:\\Tools\\SEEDEditor.exe\" \"%1\"", values.OpenCommandValue, "open コマンド値");

        ExpectThrows<ArgumentException>(
            () => FileAssociationValues.Build(""), "空の exe パス");
    }

    /// <summary>空白を含むパスでも 1 引数として解釈される形になる。</summary>
    private static void AssociationValuesQuotesSpaces()
    {
        var values = FileAssociationValues.Build(@"C:\Program Files\SEED\SEEDEditor.exe");
        Check.Equal("\"C:\\Program Files\\SEED\\SEEDEditor.exe\" \"%1\"",
                    values.OpenCommandValue, "空白入りパスの引用");
    }

    // ============================================================
    //  プロジェクト生成
    // ============================================================

    /// <summary>フォルダ・.seedproj・設定・初期シーンが一式生成される。</summary>
    private static void CreatorGeneratesFullSet()
    {
        using var temp = new TempDir();
        var paths = ProjectCreator.Create(temp.Path, "NewGame", "新しいゲーム");

        Check.Equal(Path.Combine(temp.Path, "NewGame"), paths.RootDir, "プロジェクトルート");
        Check.True(File.Exists(paths.ProjectFilePath), ".seedproj が生成される");
        Check.True(Directory.Exists(paths.AssetsDir),  "assets が生成される");
        Check.True(Directory.Exists(paths.PluginsDir), "plugins が生成される");

        var settingsPath = Path.Combine(paths.AssetsDir, ProjectCreator.PROJECT_SETTINGS_FILE_NAME);
        var packagePath  = Path.Combine(paths.AssetsDir, ProjectCreator.PACKAGING_SETTINGS_FILE_NAME);
        var scenePath    = Path.Combine(paths.AssetsDir, ProjectCreator.SCENES_DIR_NAME,
                                        ProjectCreator.INITIAL_SCENE_NAME + ProjectCreator.SCENE_EXTENSION);

        Check.True(File.Exists(settingsPath), "project_settings.json が生成される");
        Check.True(File.Exists(packagePath),  "packaging_settings.json が生成される");
        Check.True(File.Exists(scenePath),    "初期シーンが生成される");

        // project_settings.json の中身（開始シーン・シーン一覧・解像度）を固定する。
        using var settingsDoc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var root = settingsDoc.RootElement;
        Check.Equal("新しいゲーム", root.GetProperty("game_name").GetString(), "game_name");
        Check.Equal("assets://scenes/Main.scene",
                    root.GetProperty("start_scene").GetString(), "start_scene");
        Check.Equal(ProjectCreator.DEFAULT_WINDOW_WIDTH,
                    root.GetProperty("window_width").GetInt32(), "window_width");
        Check.Equal(ProjectCreator.DEFAULT_WINDOW_HEIGHT,
                    root.GetProperty("window_height").GetInt32(), "window_height");
        Check.Equal(1, root.GetProperty("scenes").GetArrayLength(), "scenes の件数");
        Check.Equal(0, root.GetProperty("plugins").GetArrayLength(), "plugins は空");

        // 初期シーンはランタイムの必須キー（name / actors）を満たす。
        using var sceneDoc = JsonDocument.Parse(File.ReadAllText(scenePath));
        Check.Equal(ProjectCreator.INITIAL_SCENE_NAME,
                    sceneDoc.RootElement.GetProperty("name").GetString(), "シーン名");
        Check.Equal(0, sceneDoc.RootElement.GetProperty("actors").GetArrayLength(), "actors は空");

        // 生成物フォルダ（cache/save/logs/build）は先回りして作らない。
        Check.True(!Directory.Exists(paths.CacheDir), "cache は作らない");
        Check.True(!Directory.Exists(paths.BuildDir), "build は作らない");
    }

    /// <summary>生成した .seedproj をそのまま読み戻せる。</summary>
    private static void CreatorOutputIsLoadable()
    {
        using var temp = new TempDir();
        var paths = ProjectCreator.Create(temp.Path, "Loadable");

        var loaded = SeedProjectFile.Load(paths.ProjectFilePath);
        Check.Equal("Loadable", loaded.Name, "name");
        Check.Equal("Loadable", loaded.EffectiveDisplayName, "display_name（未指定なら name）");
        Check.True(!string.IsNullOrEmpty(loaded.EngineVersion), "engine_version が記録される");
        Check.True(!string.IsNullOrEmpty(loaded.CreatedAt),     "created_at が記録される");

        // 読み直した内容から導いたパスが、生成時のパスと一致する。
        var reDerived = ProjectPaths.FromProjectFile(paths.ProjectFilePath, loaded);
        Check.Equal(paths.AssetsDir, reDerived.AssetsDir, "アセットルートが一致する");
    }

    /// <summary>テンプレート投入用の生成後フックが呼ばれる。</summary>
    private static void CreatorInvokesHook()
    {
        using var temp = new TempDir();

        ProjectPaths? seen = null;
        void Hook(ProjectPaths p) => seen = p;

        ProjectCreator.ProjectCreated += Hook;
        try
        {
            var paths = ProjectCreator.Create(temp.Path, "Hooked");
            Check.True(seen is not null, "フックが呼ばれる");
            Check.Equal(paths.RootDir, seen!.RootDir, "フックに渡るパス");
        }
        finally
        {
            ProjectCreator.ProjectCreated -= Hook;
        }
    }

    /// <summary>不正な入力は生成前に弾かれる（途中まで作って壊さない）。</summary>
    private static void CreatorRejectsInvalidInput()
    {
        using var temp = new TempDir();

        Check.True(ProjectCreator.ValidateName("")        is not null, "空の名前");
        Check.True(ProjectCreator.ValidateName("a/b")     is not null, "区切り文字入りの名前");
        Check.True(ProjectCreator.ValidateName(" pad ")   is not null, "前後に空白のある名前");
        Check.True(ProjectCreator.ValidateName("ok_name") is null,     "使える名前");

        // 空でない既存フォルダは拒否する。
        var occupied = temp.CreateSubDirectory("Occupied");
        File.WriteAllText(Path.Combine(occupied, "something.txt"), "x");
        Check.True(ProjectCreator.ValidateDestination(temp.Path, "Occupied") is not null,
                   "空でない既存フォルダ");
        ExpectThrows<SeedProjectFileException>(
            () => ProjectCreator.Create(temp.Path, "Occupied"), "空でない既存フォルダへの生成");

        // 空の既存フォルダは許可する（先に作ってから作成する使い方を壊さない）。
        temp.CreateSubDirectory("EmptyOk");
        Check.True(ProjectCreator.ValidateDestination(temp.Path, "EmptyOk") is null,
                   "空の既存フォルダは使える");
    }

    // ============================================================
    //  補助
    // ============================================================

    /// <summary>指定した例外が投げられることを表明する。</summary>
    /// <typeparam name="TException">期待する例外の型。</typeparam>
    /// <param name="action">実行する処理。</param>
    /// <param name="what">対象の説明（失敗時に表示される）。</param>
    private static void ExpectThrows<TException>(Action action, string what)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new AssertionException(
                $"{what}: {typeof(TException).Name} を期待したが {ex.GetType().Name} が投げられた");
        }
        throw new AssertionException($"{what}: {typeof(TException).Name} が投げられなかった");
    }
}
