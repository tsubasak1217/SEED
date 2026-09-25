using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SEEDEditor.Headless;
using SEEDEditor.Project;
using SEEDEditor.ProjectSettings;
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
///   8. project_settings.json の screen_orientation（画面の向き）の既定値・往復・正規化
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

        // ── engine_version の判定（EngineVersionCheck） ──────
        harness.Add("engine_version が一致すれば Same",                   EngineVersionSameIsDetected);
        harness.Add("プロジェクトが古ければ ProjectOlder",                 EngineVersionProjectOlderIsDetected);
        harness.Add("プロジェクトが新しければ ProjectNewer",               EngineVersionProjectNewerIsDetected);
        harness.Add("ビルドメタデータ (+xxx) は比較前に落ちる",            EngineVersionBuildMetadataIsIgnored);
        harness.Add("プレリリース識別子 (-beta) は比較前に落ちる",         EngineVersionPrereleaseIsIgnored);
        harness.Add("プロジェクト側が空・null なら Unknown",               EngineVersionEmptyProjectVersionIsUnknown);
        harness.Add("解釈できない文字列は Unknown",                        EngineVersionInvalidVersionIsUnknown);
        harness.Add("4 要素以上のバージョンも要素数の差を 0 補完して比較する",
                                                                          EngineVersionFourComponentIsComparedWithPadding);

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

        // ── タスクバーのジャンプリスト ─────────────────────
        harness.Add("ジャンプリストは実在する .seedproj だけを順序通りに並べる", JumpListKeepsOnlyExistingInOrder);
        harness.Add("ジャンプリストは重複パスを除き最大件数で打ち切る",        JumpListDedupesAndCaps);

        // ── 関連付けの値 ────────────────────────────────────
        harness.Add("FileAssociation の値が HKCU 配下で組み立てられる",   AssociationValues);
        harness.Add("空白を含む exe パスは引用符で囲まれる",              AssociationValuesQuotesSpaces);

        // ── プロジェクト生成 ────────────────────────────────
        harness.Add("ProjectCreator が一式を生成する",                    CreatorGeneratesFullSet);
        harness.Add("生成した .seedproj はそのまま開ける",                CreatorOutputIsLoadable);
        harness.Add("生成後フックが呼ばれる",                             CreatorInvokesHook);
        harness.Add("不正な名前・空でない作成先は拒否される",             CreatorRejectsInvalidInput);

        // ── 画面の向き（project_settings.json の screen_orientation）──
        harness.Add("screen_orientation の既定値は both",                 ScreenOrientationDefaultsToBoth);
        harness.Add("screen_orientation は保存 → 読み込みで往復する",     ScreenOrientationRoundTrip);
        harness.Add("screen_orientation が無い旧ファイルは both で読む",  ScreenOrientationMissingKeyIsBoth);
        harness.Add("screen_orientation は空白・大文字・未知の値を正規化する", ScreenOrientationNormalizes);

        // ── Android アプリ情報（project_settings.json の android 節）──
        harness.Add("android 節は保存 → 読み込みで往復し、知らないキーも保つ",   AndroidSectionRoundTrip);
        harness.Add("android 節は空なら保存しない（既存のファイルに空の節を増やさない）", AndroidSectionOmittedWhenEmpty);
        harness.Add("android 節の型の違う値でも他の設定は読める",               AndroidSectionWrongTypesDoNotBreakLoading);

        // ── プロジェクトフォルダ → アセットルート（SeedPak / SeedAndroid 共通の規則）──
        harness.Add("プロジェクトフォルダの解決: .seedproj → assets/ → フォルダ自体", ProjectFolderResolverRules);

        // ── 描画品質（project_settings.json の render_quality 節。Android 段階D-2）──
        harness.Add("render_quality 節は保存 → 読み込みで往復し、画面に出さないつまみ・知らない節も保つ", RenderQualitySectionRoundTrip);
        harness.Add("render_quality 節は空なら保存しない",                          RenderQualitySectionOmittedWhenEmpty);
        harness.Add("render_quality 節の型の違う値でも他の設定は読める",           RenderQualityWrongTypesDoNotBreakLoading);
        harness.Add("描画スケールは 0.5〜1.0 に収める（NaN は未設定）",            RenderQualityScaleIsClamped);
        harness.Add("埋め込みのプリセット一覧に各プラットフォームの既定がある",    RenderQualityCatalogHasPlatformDefaults);
        harness.Add("プリセット定義の書式違いは空の一覧（重複・名前なしは飛ばす）", RenderQualityCatalogRejectsBrokenDefinitions);

        return harness.Run();
    }

    // ============================================================
    //  描画品質（project_settings.json の render_quality 節。Android 段階D-2）
    // ============================================================

    /// <summary>書いた値が render_quality 節に入り、読み戻せる。画面に出さないつまみ（gi 等）・知らない節も保つ。</summary>
    private static void RenderQualitySectionRoundTrip()
    {
        using var temp = new TempDir();
        var path = temp.Combine("project_settings.json");
        File.WriteAllText(path,
            "{ \"game_name\": \"G\", \"render_quality\": { \"android\": { \"preset\": \"mobile\", \"gi\": \"ssgi\" }, \"ios\": { \"preset\": \"mobile\" } } }");

        var data = ProjectSettingsData.LoadFrom(path);
        Check.Equal("mobile", data.RenderQuality?.Android?.Preset, "preset を読む");
        Check.True(data.RenderQuality!.Android!.ExtraData.ContainsKey("gi"), "画面に出さないつまみを読む");
        Check.True(data.RenderQuality.ExtraData.ContainsKey("ios"), "知らない節を読む");
        data.RenderQuality.Android.RenderScale = 0.75;
        data.RenderQuality.Android.Shadows = false;
        data.RenderQuality.Set(RenderQualitySettings.DesktopKey, new RenderQualityPlatformSettings { Preset = "mobile_high" });
        data.SaveTo(path);

        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var section = doc.RootElement.GetProperty(RenderQualitySettings.SectionKey);
            var android = section.GetProperty(RenderQualitySettings.AndroidKey);
            Check.Equal("mobile", android.GetProperty("preset").GetString(), "preset");
            Check.Equal(0.75, android.GetProperty("render_scale").GetDouble(), "render_scale は数");
            Check.Equal(false, android.GetProperty("shadows").GetBoolean(), "shadows は真偽値");
            Check.Equal("ssgi", android.GetProperty("gi").GetString(), "画面に出さないつまみを書き戻す");
            Check.Equal("mobile_high", section.GetProperty(RenderQualitySettings.DesktopKey).GetProperty("preset").GetString(), "desktop");
            Check.True(section.TryGetProperty("ios", out _), "知らない節を書き戻す");
        }
        var loaded = ProjectSettingsData.LoadFrom(path);
        Check.Equal(0.75, loaded.RenderQuality?.Android?.RenderScale, "render_scale を読み戻す");
        Check.Equal(false, loaded.RenderQuality?.Android?.Shadows, "shadows を読み戻す");
        Check.Equal("G", loaded.GameName, "他の設定");
    }

    /// <summary>何も設定していなければ render_quality 節を書かない（既定＝プラットフォームの既定のプリセット）。</summary>
    private static void RenderQualitySectionOmittedWhenEmpty()
    {
        using var temp = new TempDir();
        var path = temp.Combine("project_settings.json");
        var settings = new RenderQualitySettings();
        settings.Set(RenderQualitySettings.AndroidKey, new RenderQualityPlatformSettings());
        new ProjectSettingsData { RenderQuality = settings }.SaveTo(path);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Check.True(!doc.RootElement.TryGetProperty(RenderQualitySettings.SectionKey, out _), "空の節は書かない");
        Check.True(ProjectSettingsData.LoadFrom(path).RenderQuality is null, "読み戻すと null（既定）");
    }

    /// <summary>手で書いた型違いの値で ProjectSettingsData 全体の読み込みが失敗しない。</summary>
    private static void RenderQualityWrongTypesDoNotBreakLoading()
    {
        using var temp = new TempDir();
        var path = temp.Combine("project_settings.json");
        File.WriteAllText(path,
            "{ \"game_name\": \"Keep\", \"render_quality\": { \"android\": { \"preset\": 3, \"render_scale\": \"0.6\", \"shadows\": \"no\" }, \"desktop\": \"mobile\" } }");
        var loaded = ProjectSettingsData.LoadFrom(path);
        Check.Equal("Keep", loaded.GameName, "他の設定は読める");
        Check.True(loaded.RenderQuality?.Android?.Preset is null, "読めない preset は未設定");
        Check.Equal(0.6, loaded.RenderQuality?.Android?.RenderScale, "数として読める文字列は読む");
        Check.True(loaded.RenderQuality?.Android?.Shadows is null, "読めない shadows は未設定");
        Check.True(loaded.RenderQuality?.Desktop is null, "型の違う節は型付きでは読まない");
        Check.True(loaded.RenderQuality?.ExtraData.ContainsKey("desktop") == true, "型の違う節は保つ（ランタイムは警告して無視する）");
        // 文字列が空・無しのコンボ（画面を開かない保存）でもそのまま往復する
        loaded.SaveTo(path);
        Check.Equal("Keep", ProjectSettingsData.LoadFrom(path).GameName, "保存し直しても読める");

        // 型の違う節を画面で設定し直すと、型付きの節だけが 1 回書かれる（同じキーを 2 回書かない）
        var reloaded = ProjectSettingsData.LoadFrom(path);
        reloaded.RenderQuality!.Set(RenderQualitySettings.DesktopKey, new RenderQualityPlatformSettings { Preset = "mobile" });
        reloaded.SaveTo(path);
        var text = File.ReadAllText(path);
        Check.Equal(1, text.Split("\"desktop\"").Length - 1, "desktop のキーは 1 回だけ");
        Check.Equal("mobile", ProjectSettingsData.LoadFrom(path).RenderQuality?.Desktop?.Preset, "型付きの節として読める");
    }

    /// <summary>描画スケールはランタイムの値域（0.5〜1.0）へ収める。</summary>
    private static void RenderQualityScaleIsClamped()
    {
        Check.Equal(0.5, RenderQualityPlatformSettings.ClampRenderScale(0.1), "下限");
        Check.Equal(1.0, RenderQualityPlatformSettings.ClampRenderScale(3.0), "上限");
        Check.Equal(0.75, RenderQualityPlatformSettings.ClampRenderScale(0.75), "範囲内はそのまま");
        Check.True(RenderQualityPlatformSettings.ClampRenderScale(double.NaN) is null, "NaN は未設定");
        Check.True(RenderQualityPlatformSettings.ClampRenderScale(null) is null, "null は未設定");
        Check.True(RenderQualityPlatformSettings.NormalizePreset("  ") is null, "空白だけのプリセット名は既定");
        Check.Equal("mobile", RenderQualityPlatformSettings.NormalizePreset(" mobile "), "前後の空白を落とす");
    }

    /// <summary>
    /// 埋め込みの runtime/config/render_presets.json（ランタイムと同じファイル）に、各プラットフォームの既定のプリセットがある。
    /// デスクトップの既定は何も下げない（空）、Android の既定は描画スケールを下げる。
    /// </summary>
    private static void RenderQualityCatalogHasPlatformDefaults()
    {
        var presets = RenderQualityPresetCatalog.Presets;
        Check.True(presets.Count >= 2, $"プリセットがある（{presets.Count} 件）");
        var desktop = RenderQualityPresetCatalog.Find(RenderQualitySettings.DefaultPresetFor(RenderQualitySettings.DesktopKey));
        var mobile = RenderQualityPresetCatalog.Find(RenderQualitySettings.DefaultPresetFor(RenderQualitySettings.AndroidKey));
        Check.True(desktop is not null && desktop.Knobs.Count == 0, "desktop は何も下げない");
        Check.True(mobile is not null && mobile.Knobs.TryGetValue(RenderQualityPlatformSettings.RenderScaleKey, out var scale)
                   && scale.GetDouble() < RenderQualityPlatformSettings.MaxRenderScale, "mobile は描画スケールを下げる");
        foreach (var preset in presets)
        {
            Check.True(!string.IsNullOrWhiteSpace(preset.Label) && !string.IsNullOrWhiteSpace(preset.Description),
                $"{preset.Name} に表示名と説明がある");
            Check.True(RenderQualityPresetCatalog.DescribeKnobs(preset.Knobs).Length > 0, $"{preset.Name} の中身を説明できる");
        }
        Check.True(RenderQualityPresetCatalog.Find(" MOBILE ") is not null, "名前は大文字小文字・空白を区別しない");
    }

    /// <summary>書式違い（版・配列なし・JSON の誤り）は空の一覧。重複した名前・名前なしは飛ばす。</summary>
    private static void RenderQualityCatalogRejectsBrokenDefinitions()
    {
        Check.Equal(0, RenderQualityPresetCatalog.Parse("not json").Count, "JSON の誤り");
        Check.Equal(0, RenderQualityPresetCatalog.Parse("{\"format_version\": 99, \"presets\": []}").Count, "版の違い");
        Check.Equal(0, RenderQualityPresetCatalog.Parse("{\"format_version\": 1}").Count, "配列なし");
        var partial = RenderQualityPresetCatalog.Parse(
            "{\"format_version\": 1, \"presets\": [ {\"name\": \"a\", \"knobs\": {\"bloom\": false}}, {\"name\": \"A\"}, {\"label\": \"x\"} ]}");
        Check.Equal(1, partial.Count, "重複・名前なしは飛ばす");
        Check.Equal("ブルームなし", RenderQualityPresetCatalog.DescribeKnobs(partial[0].Knobs), "つまみの説明");
    }

    // ============================================================
    //  Android アプリ情報（project_settings.json の android 節）
    // ============================================================

    /// <summary>書いた値が android 節に入り、読み戻せる。知らないキーも保つ。</summary>
    private static void AndroidSectionRoundTrip()
    {
        using var temp = new TempDir();
        var path = temp.Combine("project_settings.json");
        File.WriteAllText(path, "{ \"game_name\": \"G\", \"android\": { \"future_key\": 5 } }");

        var data = ProjectSettingsData.LoadFrom(path);
        Check.True(data.Android is not null && data.Android.ExtraData.ContainsKey("future_key"), "知らないキーを読む");
        data.Android!.ApplicationId = "com.example.game";
        data.Android.AppName = "ゲーム";
        data.Android.VersionCode = 4;
        data.Android.VersionName = "1.2";
        data.SaveTo(path);

        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var android = doc.RootElement.GetProperty(AndroidAppSettings.SectionKey);
            Check.Equal("com.example.game", android.GetProperty("application_id").GetString(), "application_id");
            Check.Equal(4, android.GetProperty("version_code").GetInt32(), "version_code は整数");
            Check.Equal(5, android.GetProperty("future_key").GetInt32(), "知らないキーを書き戻す");
        }
        var loaded = ProjectSettingsData.LoadFrom(path);
        Check.Equal("ゲーム", loaded.Android?.AppName, "app_name");
        Check.Equal("1.2", loaded.Android?.VersionName, "version_name");
        Check.Equal("G", loaded.GameName, "他の設定");
    }

    /// <summary>何も設定していなければ android 節を書かない。</summary>
    private static void AndroidSectionOmittedWhenEmpty()
    {
        using var temp = new TempDir();
        var path = temp.Combine("project_settings.json");
        new ProjectSettingsData { Android = new AndroidAppSettings() }.SaveTo(path);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Check.True(!doc.RootElement.TryGetProperty(AndroidAppSettings.SectionKey, out _), "空の節は書かない");
        Check.True(ProjectSettingsData.LoadFrom(path).Android is null, "読み戻すと null（既定値）");
    }

    /// <summary>手で書いた型違いの値で ProjectSettingsData 全体の読み込みが失敗しない（既定値で上書き保存される事故を防ぐ）。</summary>
    private static void AndroidSectionWrongTypesDoNotBreakLoading()
    {
        using var temp = new TempDir();
        var path = temp.Combine("project_settings.json");
        File.WriteAllText(path, "{ \"game_name\": \"Keep\", \"android\": { \"version_code\": \"x\", \"app_name\": 12 } }");
        var loaded = ProjectSettingsData.LoadFrom(path);
        Check.Equal("Keep", loaded.GameName, "他の設定は読める");
        Check.True(loaded.Android?.VersionCode is null, "読めない版は未設定");
        Check.Equal("12", loaded.Android?.AppName, "数値の名前は文字列として");
    }

    /// <summary>プロジェクトフォルダの解決の規則（SeedPak の --project・SeedAndroid の --project と同じ）。</summary>
    private static void ProjectFolderResolverRules()
    {
        using var temp = new TempDir();
        // 1. .seedproj の assets_dir（Content）
        var withProject = temp.CreateSubDirectory("WithProject");
        var file = SeedProjectFile.Create("WithProject", "表示名");
        file.AssetsDir = "Content";
        file.Save(Path.Combine(withProject, "WithProject.seedproj"));
        Directory.CreateDirectory(Path.Combine(withProject, "Content"));
        var resolved = ProjectFolderResolver.Resolve(withProject, out var error)!;
        Check.True(error is null, $"エラーなし: {error}");
        Check.Equal(Path.Combine(withProject, "Content"), resolved.AssetsRoot, "assets_dir");
        Check.Equal("WithProject", resolved.ProjectName, "name");
        Check.Equal("表示名", resolved.ProjectDisplayName, "display_name");

        // 2. .seedproj が無ければ <フォルダ>/assets
        var plain = temp.CreateSubDirectory("Plain");
        Directory.CreateDirectory(Path.Combine(plain, "assets"));
        var plainResolved = ProjectFolderResolver.Resolve(plain, out _)!;
        Check.Equal(Path.Combine(plain, "assets"), plainResolved.AssetsRoot, "assets/");
        Check.True(plainResolved.ProjectName is null, ".seedproj が無ければ名前は無い");

        // 3. フォルダ自体がアセットルート
        var root = temp.CreateSubDirectory("RootAssets");
        File.WriteAllText(Path.Combine(root, "project_settings.json"), "{}");
        var rootResolved = ProjectFolderResolver.Resolve(root, out _)!;
        Check.Equal(root, rootResolved.AssetsRoot, "フォルダ自体");
        Check.Equal(temp.Path, rootResolved.ProjectRoot, "プロジェクトルートは親（ランタイムと同じ）");

        // 決められない
        Check.True(ProjectFolderResolver.Resolve(temp.CreateSubDirectory("Empty"), out var emptyError) is null && emptyError is not null, "空のフォルダ");
        Check.True(ProjectFolderResolver.Resolve(temp.Combine("Missing"), out var missingError) is null && missingError!.Contains("見つかりません"), "無いフォルダ");
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
    //  engine_version の判定（EngineVersionCheck）
    // ============================================================

    /// <summary>完全一致は Same。</summary>
    private static void EngineVersionSameIsDetected()
    {
        var result = EngineVersionCheck.Compare("0.1.0", "0.1.0");
        Check.Equal(EngineVersionComparison.Same, result.Comparison, "0.1.0 vs 0.1.0");
    }

    /// <summary>プロジェクト側が数値として小さければ ProjectOlder（エディタの方が新しい）。</summary>
    private static void EngineVersionProjectOlderIsDetected()
    {
        var result = EngineVersionCheck.Compare("0.1.0", "0.2.0");
        Check.Equal(EngineVersionComparison.ProjectOlder, result.Comparison, "0.1.0 vs 0.2.0");
    }

    /// <summary>プロジェクト側が数値として大きければ ProjectNewer（プロジェクトの方が新しい）。</summary>
    private static void EngineVersionProjectNewerIsDetected()
    {
        var result = EngineVersionCheck.Compare("0.3.0", "0.2.0");
        Check.Equal(EngineVersionComparison.ProjectNewer, result.Comparison, "0.3.0 vs 0.2.0");
    }

    /// <summary>"+abc" のビルドメタデータは比較前に落ちるため "0.1.0+abc" と "0.1.0" は同値。</summary>
    private static void EngineVersionBuildMetadataIsIgnored()
    {
        var result = EngineVersionCheck.Compare("0.1.0+abc", "0.1.0");
        Check.Equal(EngineVersionComparison.Same, result.Comparison, "0.1.0+abc vs 0.1.0");
        Check.Equal("0.1.0", result.ProjectVersionNormalized, "正規化後の文字列からメタデータが落ちている");
    }

    /// <summary>"-beta" のようなプレリリース識別子も比較前に落ちる。</summary>
    private static void EngineVersionPrereleaseIsIgnored()
    {
        var result = EngineVersionCheck.Compare("1.0.0-beta", "1.0.0");
        Check.Equal(EngineVersionComparison.Same, result.Comparison, "1.0.0-beta vs 1.0.0");
    }

    /// <summary>.seedproj の engine_version が空・null（＝旧プロジェクト）なら Unknown。</summary>
    private static void EngineVersionEmptyProjectVersionIsUnknown()
    {
        var result = EngineVersionCheck.Compare("", "0.1.0");
        Check.Equal(EngineVersionComparison.Unknown, result.Comparison, "空文字 vs 0.1.0");

        var resultNull = EngineVersionCheck.Compare(null, "0.1.0");
        Check.Equal(EngineVersionComparison.Unknown, resultNull.Comparison, "null vs 0.1.0");
    }

    /// <summary>数値として解釈できない文字列は Unknown。</summary>
    private static void EngineVersionInvalidVersionIsUnknown()
    {
        var result = EngineVersionCheck.Compare("not-a-version", "0.1.0");
        Check.Equal(EngineVersionComparison.Unknown, result.Comparison, "不正な文字列 vs 0.1.0");
    }

    /// <summary>4 要素以上のバージョンも、足りない要素を 0 として比較する。</summary>
    private static void EngineVersionFourComponentIsComparedWithPadding()
    {
        var same = EngineVersionCheck.Compare("0.1.0.0", "0.1.0");
        Check.Equal(EngineVersionComparison.Same, same.Comparison, "0.1.0.0 vs 0.1.0（末尾 0 は同値）");

        var newer = EngineVersionCheck.Compare("0.1.0.1", "0.1.0");
        Check.Equal(EngineVersionComparison.ProjectNewer, newer.Comparison, "0.1.0.1 vs 0.1.0（末尾が非 0 なら新しい）");
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
    //  画面の向き（project_settings.json の screen_orientation）
    // ============================================================

    /// <summary>project_settings.json の中の画面の向きのキー（SeedAndroid・build.gradle.kts と同じ）。</summary>
    private const string ScreenOrientationKey = "screen_orientation";

    /// <summary>新しい設定の既定値は both（縦横どちらも）。Gradle 側の既定値と同じ。</summary>
    private static void ScreenOrientationDefaultsToBoth()
    {
        Check.Equal("both", new ProjectSettingsData().ScreenOrientation, "既定値");
        Check.Equal(ScreenOrientationSetting.Both, ScreenOrientationSetting.Default, "Default 定数");
    }

    /// <summary>保存した値が JSON の screen_orientation に書かれ、読み戻せる。</summary>
    private static void ScreenOrientationRoundTrip()
    {
        using var temp = new TempDir();
        var path = temp.Combine("project_settings.json");

        var data = new ProjectSettingsData { ScreenOrientation = ScreenOrientationSetting.Portrait };
        data.SaveTo(path);

        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Check.Equal("portrait", doc.RootElement.GetProperty(ScreenOrientationKey).GetString(),
                        "JSON のキーと値（ランタイム側のスクリプトが読む形）");
        }
        Check.Equal("portrait", ProjectSettingsData.LoadFrom(path).ScreenOrientation, "読み戻した値");
    }

    /// <summary>キーの無い旧ファイルは既定値（both）で読む（既存プロジェクトの挙動は変わらない）。</summary>
    private static void ScreenOrientationMissingKeyIsBoth()
    {
        using var temp = new TempDir();
        var path = temp.Combine("project_settings.json");
        File.WriteAllText(path, "{ \"game_name\": \"Old\", \"window_width\": 1280, \"window_height\": 720 }");

        var loaded = ProjectSettingsData.LoadFrom(path);
        Check.Equal("Old", loaded.GameName, "ほかのキーは読める");
        Check.Equal("both", loaded.ScreenOrientation, "screen_orientation が無ければ both");
    }

    /// <summary>前後の空白・大文字小文字を吸収し、未知の値・空は既定値へ倒す（ps1・Gradle と同じ読み方）。</summary>
    private static void ScreenOrientationNormalizes()
    {
        Check.Equal("landscape", ScreenOrientationSetting.Normalize("  Landscape "), "空白と大文字");
        Check.Equal("portrait",  ScreenOrientationSetting.Normalize("PORTRAIT"),     "大文字");
        Check.Equal("both",      ScreenOrientationSetting.Normalize("sideways"),     "未知の値");
        Check.Equal("both",      ScreenOrientationSetting.Normalize(""),             "空");
        Check.Equal("both",      ScreenOrientationSetting.Normalize(null),           "null");
        Check.Equal(1, ScreenOrientationSetting.IndexOf("portrait"),  "コンボボックスの位置（縦）");
        Check.Equal(0, ScreenOrientationSetting.IndexOf("unknown"),   "未知の値は既定値の位置");
        Check.Equal(3, ScreenOrientationSetting.Choices.Length,       "選べる値は 3 つ（build.gradle.kts の変換表と同じ数）");
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

    // ── タスクバーのジャンプリスト ────────────────────────

    /// <summary>実在するものだけを、最近の順序を保って並べ、表示名が空ならファイル名を使う。</summary>
    private static void JumpListKeepsOnlyExistingInOrder()
    {
        var entries = new List<RecentProjectEntry>
        {
            new() { Path = @"C:\p\A.seedproj",       Name = "ゲーム A" },
            new() { Path = @"C:\p\Missing.seedproj", Name = "消えた" },
            new() { Path = @"C:\p\B.seedproj",       Name = "" },
        };
        var items = ProjectJumpListBuilder.Build(entries, path => !path.Contains("Missing"));
        Check.Equal(2, items.Count, "実在する 2 件だけ");
        Check.Equal("ゲーム A", items[0].Title, "順序 1");
        Check.Equal("B", items[1].Title, "表示名が空ならファイル名");
        Check.Equal(@"C:\p\B.seedproj", items[1].ProjectFilePath, "開くパス");
    }

    /// <summary>同じパス（大文字小文字違い含む）は 1 回だけ、最大件数で打ち切る。</summary>
    private static void JumpListDedupesAndCaps()
    {
        var entries = new List<RecentProjectEntry>();
        for (int i = 0; i < 15; i++)
        {
            var stem = "G" + (i % 12);
            // 偶数番は小文字パスにして、大文字小文字違いの重複も 1 件に畳まれることを見る
            var path = i % 2 == 0 ? @"c:\p\" + stem + ".seedproj" : @"C:\P\" + stem + ".seedproj";
            entries.Add(new RecentProjectEntry { Path = path, Name = stem });
        }
        var items = ProjectJumpListBuilder.Build(entries, _ => true);
        Check.Equal(ProjectJumpListBuilder.MAX_RECENT_ITEMS, items.Count, "最大件数で打ち切り");
        var distinct = items.Select(i => i.ProjectFilePath.ToLowerInvariant()).Distinct().Count();
        Check.Equal(items.Count, distinct, "重複なし");
    }


}
