using System.IO;
using System.Linq;
using System.Text.Json;
using SEEDEditor.Android.Platform;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Release;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Packaging;
using SEEDEditor.ProjectSettings;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>
/// アプリのプラットフォーム機能の opt-in（W1-2）のうち、プロジェクト設定の読み書き（"android" 節の新しい 4 キー）、
/// プロジェクト設定ウィンドウの編集の状態（AndroidPlatformSettingsEditor。WPF 非依存）、Google Play の要件チェック
/// （AndroidPlatformFeatureChecks・play_requirements.json の permission_policies）。
/// </summary>
public static class PlatformSettingsTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("\"android\" 節: features・deep_links・system_bars・app_category の往復（知らないキーは保つ・空の行は書かない）", SettingsRoundTrip);
        harness.Add("\"android\" 節: 新しいキーの寛容な読み取り（文字列 1 つの features・型違い・真偽の文字列）と IsEmpty", SettingsAreLenient);
        harness.Add("設定ウィンドウ: 機能の切り替え・知らない機能は保つ・コンボは選び直すまで元の値・既定値はキーを省く", EditorFeaturesAndChoices);
        harness.Add("設定ウィンドウ: ディープリンクの追加・削除・空の行・誤りで保存を止める・注意と誤りの行", EditorDeepLinksAndValidation);
        harness.Add("設定ウィンドウ: 機能の表が読めないときは理由を出し、書かれていた機能を保つ", EditorWithoutCatalog);
        harness.Add("要件の表: permission_policies が読め、どの方針の権限も機能の表のどれかの機能のもの", PolicyTableMatchesCatalog);
        harness.Add("要件（ビルドの前）: features の権限の一覧・知らない機能は注意・alarm は申告の注意 3 件", PreBuildFeatureChecks);
        harness.Add("要件（ビルドの後）: 足りない権限は不合格・features に無い機能の権限は注意・ライブラリの権限は見ない・方針は配布物の権限で", ArtifactFeatureChecks);
    }

    /// <summary>JSON の設定。</summary>
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>往復。</summary>
    private static void SettingsRoundTrip()
    {
        var settings = new AndroidAppSettings
        {
            ApplicationId = "com.wakeorpay.seed",
            Features = new() { "alarm", "notifications", " future_x " },
            DeepLinks = new()
            {
                new AndroidDeepLinkSetting { Scheme = "https", Host = "example.com", PathPrefix = "/wake", AutoVerify = true },
                new AndroidDeepLinkSetting(),   // 空の行は書かない
                new AndroidDeepLinkSetting { Scheme = "wakeorpay" },
            },
            SystemBars = "visible",
            AppCategory = "productivity",
        };
        settings.DeepLinks[2].ExtraData["path_pattern"] = JsonDocument.Parse("\"/a.*\"").RootElement.Clone();
        var json = JsonSerializer.Serialize(settings, Options);
        Check.True(json.Contains("\"features\":[\"alarm\",\"notifications\",\"future_x\"]"), $"features（前後の空白は落とす）: {json}");
        Check.True(json.Contains("\"deep_links\":[{\"scheme\":\"https\",\"host\":\"example.com\",\"path_prefix\":\"/wake\",\"auto_verify\":true},{\"scheme\":\"wakeorpay\",\"path_pattern\":\"/a.*\"}]"),
            $"deep_links（空の行なし・auto_verify は true のときだけ・知らないキーは保つ）: {json}");
        Check.True(json.Contains("\"system_bars\":\"visible\"") && json.Contains("\"app_category\":\"productivity\""), "system_bars・app_category");

        var back = JsonSerializer.Deserialize<AndroidAppSettings>(json)!;
        Check.Equal("alarm,notifications,future_x", string.Join(",", back.Features!), "features の往復");
        Check.Equal(2, back.DeepLinks!.Count, "deep_links の往復");
        Check.True(back.DeepLinks[0].AutoVerify && back.DeepLinks[0].PathPrefix == "/wake", "1 件目");
        Check.True(back.DeepLinks[1].ExtraData.ContainsKey("path_pattern"), "知らないキーを保つ");
        Check.Equal("visible", back.SystemBars, "system_bars");
        Check.Equal("productivity", back.AppCategory, "app_category");
        Check.Equal(0, back.ExtraData.Count, "新しい 4 キーは ExtraData に入らない");

        // 何も無ければ書かない
        var minimal = JsonSerializer.Serialize(new AndroidAppSettings { Features = new(), DeepLinks = new() { new AndroidDeepLinkSetting() } }, Options);
        Check.Equal("{}", minimal, "空の一覧・空の行は書かない");
    }

    /// <summary>寛容な読み取り。</summary>
    private static void SettingsAreLenient()
    {
        var single = JsonSerializer.Deserialize<AndroidAppSettings>("{\"features\":\"alarm\"}")!;
        Check.Equal("alarm", single.Features!.Single(), "文字列 1 つは 1 要素");
        var mixed = JsonSerializer.Deserialize<AndroidAppSettings>("{\"features\":[\"alarm\", 3, null, \" \"],\"deep_links\":[1,{\"scheme\":\"x\",\"auto_verify\":\"TRUE\"},\"y\"]}")!;
        Check.Equal("alarm", string.Join(",", mixed.Features!), "文字列以外・空は読み飛ばす");
        Check.Equal(1, mixed.DeepLinks!.Count, "オブジェクト以外の要素は読み飛ばす");
        Check.True(mixed.DeepLinks[0].AutoVerify, "真偽の文字列");
        var wrong = JsonSerializer.Deserialize<AndroidAppSettings>("{\"features\":{\"a\":1},\"deep_links\":\"x\",\"system_bars\":true,\"app_category\":[1]}")!;
        Check.True(wrong.Features is null && wrong.DeepLinks is null && wrong.SystemBars is null && wrong.AppCategory is null, "型違いは未設定");
        Check.True(wrong.IsEmpty, "型違いだけなら空の節");
        Check.True(!new AndroidAppSettings { SystemBars = "visible" }.IsEmpty, "system_bars だけでも空ではない");
        Check.True(new AndroidAppSettings { DeepLinks = new() { new AndroidDeepLinkSetting() } }.IsEmpty, "空の行だけなら空");
    }

    /// <summary>機能とコンボ。</summary>
    private static void EditorFeaturesAndChoices()
    {
        var original = new AndroidAppSettings { Features = new() { "Alarm", "future_x" }, SystemBars = "immersive", AppCategory = "productivity" };
        var editor = new AndroidPlatformSettingsEditor(original, AndroidPlatformFeatureCatalog.BuiltIn);
        Check.Equal("alarm,notifications,deep_links", string.Join(",", editor.AvailableFeatures.Select(f => f.Name)), "チェックボックスは表の順");
        Check.True(editor.IsFeatureEnabled("alarm") && !editor.IsFeatureEnabled("notifications"), "書かれていた機能にチェック");
        Check.Equal("future_x", editor.UnknownFeatures.Single(), "知らない機能は画面に出さず保つ");
        Check.Equal(AndroidSystemBarsSetting.IndexOf(AndroidSystemBarsSetting.Default), editor.SystemBarsIndex, "知らないシステムバーの値は既定の位置");
        Check.Equal(1, editor.AppCategoryIndex, "productivity は 2 番目");

        editor.SetFeatureEnabled("notifications", true);
        editor.SetFeatureEnabled("alarm", false);
        editor.SetFeatureEnabled("not_in_table", true);
        var applied = new AndroidAppSettings();
        editor.ApplyTo(applied);
        Check.Equal("notifications,future_x", string.Join(",", applied.Features!), "表の順＋知らない機能");
        Check.Equal("immersive", applied.SystemBars, "選び直していない知らない値は消さない");
        Check.Equal("productivity", applied.AppCategory, "選び直していない値はそのまま");

        editor.SelectSystemBars(AndroidSystemBarsSetting.Visible);
        editor.SelectAppCategory(AndroidAppCategorySetting.Game);
        editor.ApplyTo(applied);
        Check.Equal("visible", applied.SystemBars, "選び直した値");
        Check.True(applied.AppCategory is null, "既定値（game）を選んだらキーを省く");
        editor.SelectSystemBars(AndroidSystemBarsSetting.Hidden);
        editor.ApplyTo(applied);
        Check.True(applied.SystemBars is null, "既定値（hidden）を選んだらキーを省く");

        // 何も設定していないプロジェクトは、開いて保存しても android 節を増やさない
        var untouched = new AndroidAppSettings();
        new AndroidPlatformSettingsEditor(null, AndroidPlatformFeatureCatalog.BuiltIn).ApplyTo(untouched);
        Check.True(untouched.IsEmpty, "空のまま");
    }

    /// <summary>ディープリンクと検査。</summary>
    private static void EditorDeepLinksAndValidation()
    {
        var editor = new AndroidPlatformSettingsEditor(new AndroidAppSettings { Features = new() { "deep_links" } }, AndroidPlatformFeatureCatalog.BuiltIn);
        Check.True(editor.DeepLinkFeatureEnabled, "deep_links が有効なら一覧を出す");
        Check.True(editor.DescribeProblems().Single().StartsWith(AndroidPlatformSettingsEditor.WarningPrefix), "一覧が空の注意");
        Check.Equal(0, editor.Validate().Count, "注意だけなら保存できる");

        var first = editor.AddDeepLink();
        first.Scheme = "HTTPS";
        first.Host = "example.com";
        var second = editor.AddDeepLink();   // 何も書かない行
        Check.Equal(2, editor.DeepLinks.Count, "行を足す");
        Check.Equal(1, editor.Validate().Count, "大文字の scheme は誤り（保存を止める）");
        Check.True(editor.DescribeProblems().First().StartsWith(AndroidPlatformSettingsEditor.ErrorPrefix), "誤りが先頭");

        first.Scheme = "https";
        Check.Equal(0, editor.Validate().Count, "直せば保存できる");
        editor.RemoveDeepLinkAt(5);   // 範囲外は何もしない
        editor.RemoveDeepLinkAt(1);
        Check.True(editor.DeepLinks.Single() == first, "行を消す");
        editor.AddDeepLink();
        var applied = new AndroidAppSettings();
        editor.ApplyTo(applied);
        Check.Equal(1, applied.DeepLinks!.Count, "空の行は保存しない");
        Check.True(!ReferenceEquals(applied.DeepLinks[0], first), "保存するのは複製（作業コピーと切り離す）");
        Check.True(second.IsBlank, "消した行は空のまま");

        editor.SetFeatureEnabled("deep_links", false);
        Check.True(!editor.DeepLinkFeatureEnabled, "無効にしたら一覧を隠す");
        Check.True(editor.DescribeProblems().Any(line => line.Contains("intent-filter を入れません")), "一覧があるのに機能が無い注意");
    }

    /// <summary>機能の表が読めない。</summary>
    private static void EditorWithoutCatalog()
    {
        var editor = new AndroidPlatformSettingsEditor(new AndroidAppSettings { Features = new() { "alarm" } }, null, "テスト用の理由");
        Check.Equal(0, editor.AvailableFeatures.Count, "チェックボックスを出さない");
        Check.True(editor.DescribeProblems().Single().Contains("テスト用の理由"), "理由を出す");
        Check.Equal(0, editor.Validate().Count, "表が読めないことは保存を止めない（利用者には直せない）");
        var applied = new AndroidAppSettings();
        editor.ApplyTo(applied);
        Check.Equal("alarm", applied.Features!.Single(), "書かれていた機能は保つ");
    }

    /// <summary>リポジトリの要件の表の permission_policies。</summary>
    private static void PolicyTableMatchesCatalog()
    {
        var engine = AndroidEnginePaths.Locate(System.AppContext.BaseDirectory, System.Environment.CurrentDirectory)!;
        var table = PlayRequirements.Load(engine.PlayRequirementsPath, out var error)!;
        Check.True(error is null, error ?? string.Empty);
        var permissions = table.PermissionPolicies.Select(p => p.Permission).ToList();
        foreach (var expected in new[] { "android.permission.USE_EXACT_ALARM", "android.permission.USE_FULL_SCREEN_INTENT", "android.permission.FOREGROUND_SERVICE_MEDIA_PLAYBACK" })
        {
            Check.True(permissions.Contains(expected), $"{expected} の方針がある");
        }
        var catalog = AndroidPlatformFeatureCatalog.BuiltIn;
        foreach (var policy in table.PermissionPolicies)
        {
            Check.True(catalog.FeaturesRequesting(policy.Permission).Count > 0, $"{policy.Permission} はどれかの機能の権限");
            Check.True(!string.IsNullOrWhiteSpace(policy.Note), $"{policy.Permission} に説明がある");
            Check.True(policy.Severity is PlayPermissionPolicy.InfoSeverity or PlayPermissionPolicy.WarningSeverity, $"{policy.Permission} の判定の表記");
        }
    }

    /// <summary>テスト用の要件の表（方針 2 件）。</summary>
    private static PlayRequirements Table() => new()
    {
        TargetSdk = new PlayTargetSdkRequirement { MinApiLevel = 36 },
        PageSizeBytes = 16384,
        PermissionPolicies = new[]
        {
            new PlayPermissionPolicy { Permission = "android.permission.USE_EXACT_ALARM", Note = "申告が要る", Source = "help" },
            // info の知らせの例（W1-6 で VIBRATE が alarm から main の常設へ移ったので、alarm に残る WAKE_LOCK にした）
            new PlayPermissionPolicy { Permission = "android.permission.WAKE_LOCK", Severity = "info", Note = "知らせ" },
        },
    };

    /// <summary>ビルドの前。</summary>
    private static void PreBuildFeatureChecks()
    {
        var catalog = AndroidPlatformFeatureCatalog.BuiltIn;
        var none = AndroidPlatformFeatureChecks.BeforeBuild(Table(), AndroidPlatformFeatureSet.Empty);
        Check.Equal(1, none.Count, "機能なしは 1 件");
        Check.True(none[0].Id == AndroidRequirementIds.PlatformFeatures && none[0].Severity == AndroidRequirementSeverity.Pass, "合格");

        var alarm = AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings { Features = new() { "alarm", "zzz" } }, catalog);
        var items = AndroidPlatformFeatureChecks.BeforeBuild(Table(), alarm);
        Check.Equal(AndroidRequirementSeverity.Warning, items[0].Severity, "知らない機能は注意");
        Check.True(items[0].Detail.Contains("USE_EXACT_ALARM") && items[0].Detail.Contains("zzz"), items[0].Detail);
        var exact = items.Single(i => i.Id == AndroidRequirementIds.PlayPolicy("android.permission.USE_EXACT_ALARM"));
        Check.True(exact.Severity == AndroidRequirementSeverity.Warning && exact.Detail.Contains("申告が要る") && exact.Detail.Contains("help"), "方針の注意と出どころ");
        Check.Equal(AndroidRequirementSeverity.Info, items.Single(i => i.Id.EndsWith("WAKE_LOCK")).Severity, "info は知らせ");

        // 実物の表では alarm に申告の注意が 3 件（USE_EXACT_ALARM・USE_FULL_SCREEN_INTENT・FOREGROUND_SERVICE_MEDIA_PLAYBACK）
        var engine = AndroidEnginePaths.Locate(System.AppContext.BaseDirectory, System.Environment.CurrentDirectory)!;
        var real = PlayRequirements.Load(engine.PlayRequirementsPath, out _)!;
        var realItems = AndroidPlatformFeatureChecks.BeforeBuild(real, alarm);
        Check.Equal(3, realItems.Count(i => i.Id.StartsWith(AndroidRequirementIds.PlayPolicyPrefix)), "申告の注意 3 件");
        Check.Equal(0, AndroidPlatformFeatureChecks.BeforeBuild(real,
            AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings { Features = new() { "notifications" } }, catalog))
            .Count(i => i.Id.StartsWith(AndroidRequirementIds.PlayPolicyPrefix)), "notifications だけなら申告の注意なし");

        // 中核の一覧に入る（材料があるときだけ）
        var identity = AndroidAppIdentityResolver.Resolve(null, "Game", "Game", hasProjectContext: true);
        var facts = new AndroidPreBuildFacts
        {
            Format = AndroidPackageFormat.Aab, TargetApiLevel = 36, Abis = new[] { SEEDEditor.Android.AndroidAbis.Arm64 }, Identity = identity,
        };
        Check.True(!AndroidRequirementChecks.Evaluate(Table(), facts).Any(i => i.Id == AndroidRequirementIds.PlatformFeatures), "材料が無ければ出さない");
        Check.True(AndroidRequirementChecks.Evaluate(Table(), facts with { PlatformFeatures = alarm }).Any(i => i.Id == AndroidRequirementIds.PlatformFeatures), "材料があれば出す");
    }

    /// <summary>ビルドの後。</summary>
    private static void ArtifactFeatureChecks()
    {
        var catalog = AndroidPlatformFeatureCatalog.BuiltIn;
        var alarm = AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings { Features = new() { "alarm" } }, catalog);
        var library = new[] { "android.permission.INTERNET", "com.wakeorpay.seed.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION" };

        // そろっている（ライブラリ・デバッグの権限は見ない）
        var ok = AndroidPlatformFeatureChecks.AfterBuild(Table(), alarm, catalog, alarm.Permissions.Select(p => p.Name).Concat(library).ToList());
        Check.Equal(AndroidRequirementSeverity.Pass, ok[0].Severity, ok[0].Detail);
        Check.True(ok.Any(i => i.Id == AndroidRequirementIds.PlayPolicy("android.permission.USE_EXACT_ALARM")), "配布物の権限で方針を出す");

        // 足りない → 不合格
        var missing = AndroidPlatformFeatureChecks.AfterBuild(Table(), alarm, catalog, library);
        Check.Equal(AndroidRequirementSeverity.Failure, missing[0].Severity, "足りない権限は不合格");
        Check.True(missing[0].Detail.Contains("USE_EXACT_ALARM") && missing[0].Detail.Contains("seedFeatures"), missing[0].Detail);

        // features に無い機能の権限（古い生成物など）→ 注意＋方針
        var stale = AndroidPlatformFeatureChecks.AfterBuild(Table(), AndroidPlatformFeatureSet.Empty, catalog,
            library.Append("android.permission.USE_EXACT_ALARM").ToList());
        Check.Equal(AndroidRequirementSeverity.Warning, stale[0].Severity, "余計な権限は注意");
        Check.True(stale[0].Detail.Contains("機能 alarm"), stale[0].Detail);
        Check.True(stale.Any(i => i.Id == AndroidRequirementIds.PlayPolicy("android.permission.USE_EXACT_ALARM")), "余計でも方針の注意は出す");

        // 機能なし・権限なしは合格
        var clean = AndroidPlatformFeatureChecks.AfterBuild(Table(), AndroidPlatformFeatureSet.Empty, catalog, library);
        Check.True(clean.Single().Severity == AndroidRequirementSeverity.Pass, "機能なしで合格");

        // 中核の配布物の判定に入る（機能の表があるときだけ）
        var identity = AndroidAppIdentityResolver.Resolve(null, "Game", "Game", hasProjectContext: true);
        var facts = new AndroidArtifactFacts
        {
            ArtifactPath = "app-release.apk",
            Format = AndroidPackageFormat.Apk,
            Manifest = new AaptBadging("com.seedengine.game", 1, "1.0", 29, 36, false, library, new[] { "arm64-v8a" }),
        };
        var withoutCatalog = AndroidArtifactChecks.Evaluate(Table(), facts, new AndroidArtifactExpectation(identity, null) { PlatformFeatures = alarm });
        Check.True(!withoutCatalog.Any(i => i.Id == AndroidRequirementIds.PlatformFeatures), "機能の表が無ければ比べない");
        var withCatalog = AndroidArtifactChecks.Evaluate(Table(), facts, new AndroidArtifactExpectation(identity, null) { PlatformFeatures = alarm, FeatureCatalog = catalog });
        Check.Equal(AndroidRequirementSeverity.Failure, withCatalog.Single(i => i.Id == AndroidRequirementIds.PlatformFeatures).Severity, "配布物に alarm の権限が無い");
    }
}
