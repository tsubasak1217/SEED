using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SEEDEditor.Android;
using SEEDEditor.Android.Gradle;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Platform;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.ProjectSettings;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>
/// アプリのプラットフォーム機能の opt-in（W1-2。docs/android.md §25.10）: 機能の表（runtime/android/platform_features.json）、
/// プロジェクト設定 → 機能（AndroidPlatformFeatureResolver）、マニフェストの断片と values の生成（AndroidPlatformManifestWriter）、
/// 置き場（AndroidPlatformFeatureStager）、Gradle のプロパティと指紋、Gradle・Java・main のマニフェストとの取り決めの一致。
/// </summary>
public static class PlatformFeatureTests
{
    /// <summary>Android の XML の名前空間（XDocument で属性を引く）。</summary>
    private static readonly XNamespace AndroidNs = AndroidPlatformManifestWriter.AndroidNamespace;

    /// <summary>目覚ましの発火の受信機（W1-3。runtime/android/app/src/main/java の service/alarm/AlarmReceiver.java）。</summary>
    private const string AlarmReceiverClass = "com.seedengine.runtime.platform.service.alarm.AlarmReceiver";

    /// <summary>目覚ましの張り直しの受信機（W1-3）。</summary>
    private const string BootReceiverClass = "com.seedengine.runtime.platform.service.alarm.BootReceiver";

    /// <summary>目覚ましの鳴動の前景サービス（W1-4a。service/alarm/ring/RingService.java）。</summary>
    private const string RingServiceClass = "com.seedengine.runtime.platform.service.alarm.ring.RingService";

    /// <summary>鳴動の見張りの予約の受信機（W1-7。service/alarm/ring/RingWatchdogReceiver.java。殺されたら鳴動を戻す）。</summary>
    private const string RingWatchdogReceiverClass = "com.seedengine.runtime.platform.service.alarm.ring.RingWatchdogReceiver";

    /// <summary>信頼できる起動の入口（W1-4a。main の AndroidManifest.xml の activity-alias。Java の PlatformContract.PLATFORM_ENTRY_ALIAS）。</summary>
    private const string PlatformEntryAlias = "com.seedengine.runtime.platform.PlatformEntry";

    /// <summary>BootReceiver の intent-filter の中身（表の順。"要素名:android:name"）。</summary>
    private static readonly string[] BootReceiverActions =
    {
        "action:android.intent.action.LOCKED_BOOT_COMPLETED",
        "action:android.intent.action.BOOT_COMPLETED",
        "action:android.intent.action.MY_PACKAGE_REPLACED",
        "action:android.intent.action.TIME_SET",
        "action:android.intent.action.TIMEZONE_CHANGED",
        "action:android.app.action.SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED",
    };

    /// <summary>
    /// 機能なし（android 節の無いプロジェクト。WarashibeFishing など）の生成物の指紋。予測型の戻る（W2 の手直し P1-3）を足す前の
    /// 書き手で 2026-09-29 に控えた値。false・未設定のプロジェクトの生成物を 1 バイトも変えないことの見張り
    /// （書き手の形を意図して変えたときだけ、ここを新しい値に直す）。
    /// </summary>
    private const string EmptyFilesDigestBeforePredictiveBack = "d732e6c95d08a127427c11cf37e7ac90f5dab4cae43b71390843e182bbdac458";

    /// <summary>Wake or Pay と同じ機能の設定（予測型の戻るだけを変えて比べる）。</summary>
    private static AndroidAppSettings AppLikeSettings(bool? predictiveBack) => new()
    {
        Features = new() { "alarm", "notifications" },
        SystemBars = "visible",
        AppCategory = "productivity",
        PredictiveBack = predictiveBack,
    };

    /// <summary>機能の表の要素の属性の値（無ければ null）。</summary>
    private static string? Attr(AndroidManifestElement element, string name) =>
        element.Attributes.Where(a => a.Key == name).Select(a => a.Value).FirstOrDefault();

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("機能の表: 埋め込みの表が読め、alarm / notifications / deep_links の権限が仕様どおり", BuiltInCatalogMatchesSpec);
        harness.Add("機能の表: 宣言した com.seedengine.runtime.* の部品の Java のソースがある（無いクラスを宣言すると起動時に落ちる）", CatalogComponentsHaveJavaSources);
        harness.Add("機能の表: 版・重なり・属性名・権限の名前の誤りは InvalidDataException", CatalogRejectsMalformedTables);
        harness.Add("機能: 設定なし・空は機能なし（hidden・game）", ResolveEmpty);
        harness.Add("機能: 大文字小文字・空白・重なりをそろえ、表の順・権限は重ならない・知らない名前は注意して無視", ResolveFeaturesAndUnknown);
        harness.Add("機能: 同じ権限の maxSdkVersion は広いほう（無制限が勝つ・両方あれば大きいほう）", MergePermissionsWidensMaxSdk);
        harness.Add("ディープリンク: 機能があるときだけ・形の誤り（大文字・scheme なし・host なしの path・/ 無し・ポート）・注意・重なり", ResolveDeepLinks);
        harness.Add("システムバー・分類: 正規化と知らない値の注意", ResolveSystemBarsAndCategory);
        harness.Add("予測型の戻る: true のときだけ使う・使わないときはログも従来のまま（W2 の手直し P1-3）", ResolvePredictiveBack);
        harness.Add("断片: 機能なしは中身の無い <manifest>・values は false（予測型の戻るを足す前と同じ指紋）", WriterEmpty);
        harness.Add("断片: 予測型の戻るは true のときだけ values に seed_predictive_back（マニフェストは変えない・false は従来と同じバイト列）", WriterPredictiveBack);
        harness.Add("断片: alarm と notifications の権限（SCHEDULE_EXACT_ALARM は maxSdkVersion 32）・system_bars visible は true", WriterAlarmAndNotifications);
        harness.Add("断片: ディープリンクは MainActivity へ 1 件 1 つの intent-filter（autoVerify は true のときだけ・値はエスケープ）", WriterDeepLinks);
        harness.Add("断片: 同じ設定からは同じバイト列（features の順によらない）・設定が変われば指紋も変わる", WriterIsDeterministic);
        harness.Add("置き場: 機能が空でも書く・同じ中身は書かない・古いファイルは消す", StagerWritesAndCleans);
        harness.Add("Gradle: seed.appCategory は既定の game 以外だけ渡す・seed.predictiveBack は使うときだけ（使わなければ引数も指紋も従来どおり）・断片の中身は APK の指紋に入る", GradlePropertyAndFingerprint);
        harness.Add("取り決め: build.gradle.kts の語彙・置き場・main のマニフェスト（PlatformProvider・PlatformEntry・VIBRATE）・Java のリソース名が中核と一致", ContractsMatchRepository);
    }

    // ── 機能の表 ────────────────────────────────────────────

    /// <summary>埋め込みの表の中身。</summary>
    private static void BuiltInCatalogMatchesSpec()
    {
        var catalog = AndroidPlatformFeatureCatalog.BuiltIn;
        Check.Equal("alarm,notifications,deep_links", string.Join(",", catalog.Names), "機能の名前と順");

        var alarm = catalog.Find("alarm")!;
        var expected = new[]
        {
            "android.permission.USE_EXACT_ALARM", "android.permission.SCHEDULE_EXACT_ALARM", "android.permission.RECEIVE_BOOT_COMPLETED",
            "android.permission.WAKE_LOCK", "android.permission.USE_FULL_SCREEN_INTENT",
            "android.permission.FOREGROUND_SERVICE", "android.permission.FOREGROUND_SERVICE_MEDIA_PLAYBACK", "android.permission.POST_NOTIFICATIONS",
        };
        Check.Equal(string.Join(",", expected), string.Join(",", alarm.Permissions.Select(p => p.Name)), "alarm の権限");
        Check.Equal(32, alarm.Permissions.Single(p => p.Name.EndsWith("SCHEDULE_EXACT_ALARM")).MaxSdkVersion, "SCHEDULE_EXACT_ALARM の maxSdkVersion");
        Check.True(alarm.Permissions.Where(p => !p.Name.EndsWith("SCHEDULE_EXACT_ALARM")).All(p => p.MaxSdkVersion is null), "ほかは maxSdkVersion なし");
        // W1-3: 予約の受信機 2 つ（発火・張り直し）、W1-4a: 鳴動の前景サービス、W1-7: 鳴動の見張りの受信機。
        // どれも :seed_platform・exported=false・directBootAware
        Check.Equal("receiver,receiver,service,receiver", string.Join(",", alarm.ApplicationElements.Select(e => e.Tag)),
            "alarm の部品は受信機 2 つと鳴動のサービスと見張りの受信機");
        Check.Equal(AlarmReceiverClass + "," + BootReceiverClass + "," + RingServiceClass + "," + RingWatchdogReceiverClass,
            string.Join(",", alarm.ApplicationElements.Select(e => Attr(e, "android:name"))), "部品の名前（完全修飾）");
        foreach (var component in alarm.ApplicationElements)
        {
            Check.Equal(":seed_platform", Attr(component, "android:process"), $"{Attr(component, "android:name")} は :seed_platform");
            Check.Equal("false", Attr(component, "android:exported"), $"{Attr(component, "android:name")} は exported=false");
            Check.Equal("true", Attr(component, "android:directBootAware"), $"{Attr(component, "android:name")} は directBootAware");
        }
        Check.Equal(0, alarm.ApplicationElements[0].Children.Count, "AlarmReceiver は intent-filter を持たない（アプリの PendingIntent だけが届く）");
        var bootFilter = alarm.ApplicationElements[1].Children.Single();
        Check.Equal("intent-filter", bootFilter.Tag, "BootReceiver の intent-filter");
        Check.Equal(string.Join(",", BootReceiverActions),
            string.Join(",", bootFilter.Children.Select(a => $"{a.Tag}:{Attr(a, "android:name")}")), "BootReceiver が受ける放送");
        // 鳴動の前景サービスの種類は mediaPlayback（E-03。FOREGROUND_SERVICE_MEDIA_PLAYBACK の権限と対）。intent-filter は持たない
        var ringService = alarm.ApplicationElements[2];
        Check.Equal("mediaPlayback", Attr(ringService, "android:foregroundServiceType"), "RingService の前景サービスの種類");
        Check.Equal(0, ringService.Children.Count, "RingService は intent-filter を持たない（アプリの中から起こすだけ）");
        Check.True(alarm.Permissions.Any(p => p.Name == "android.permission.FOREGROUND_SERVICE_MEDIA_PLAYBACK"), "種類の権限も alarm にある");
        // 見張りの受信機はアプリの PendingIntent（setExactAndAllowWhileIdle）だけが届けばよい
        Check.Equal(0, alarm.ApplicationElements[3].Children.Count, "RingWatchdogReceiver は intent-filter を持たない");

        Check.Equal("android.permission.POST_NOTIFICATIONS", catalog.Find("notifications")!.Permissions.Single().Name, "notifications の権限");
        // W1-5: 通知は main に常設の PlatformProvider（:seed_platform）の NotificationModule が出すので、機能の部品は要らない
        // （Java は POST_NOTIFICATIONS の宣言の有無で機能を判定する。platform/DeclaredPermissions）
        Check.Equal(0, catalog.Find("notifications")!.ApplicationElements.Count, "notifications は権限だけ（部品なし）");
        var deepLinks = catalog.Find(" Deep_Links ")!;
        Check.True(deepLinks.DeepLinkFilters && deepLinks.Permissions.Count == 0, "deep_links は intent-filter だけ（名前は大文字小文字・空白を問わず引ける）");
        Check.Equal("alarm,notifications", string.Join(",", catalog.FeaturesRequesting("android.permission.POST_NOTIFICATIONS")), "POST_NOTIFICATIONS を要る機能");
        Check.Equal(0, catalog.FeaturesRequesting("android.permission.INTERNET").Count, "INTERNET はどの機能のものでもない");
        // W1-6: VIBRATE（normal 権限。触感はゲームにも使う）は機能ではなく main のマニフェストに常設（ContractsMatchRepository で確かめる）
        Check.Equal(0, catalog.FeaturesRequesting("android.permission.VIBRATE").Count, "VIBRATE はどの機能のものでもない（main に常設）");
    }

    /// <summary>部品の Java のソース。</summary>
    private static void CatalogComponentsHaveJavaSources()
    {
        var engine = AndroidEnginePaths.Locate(System.AppContext.BaseDirectory, System.Environment.CurrentDirectory)!;
        var javaRoot = Path.Combine(engine.AndroidDir, "app", "src", "main", "java");
        var classAttributes = new[] { "android:name", "android:targetActivity" };
        var checkedCount = 0;
        foreach (var element in AndroidPlatformFeatureCatalog.BuiltIn.Features.SelectMany(f => Flatten(f.ApplicationElements)))
        {
            foreach (var (name, value) in element.Attributes.Where(a => classAttributes.Contains(a.Key)))
            {
                if (!value.StartsWith("com.seedengine.runtime.", System.StringComparison.Ordinal)) continue;
                var source = Path.Combine(javaRoot, value.Replace('.', Path.DirectorySeparatorChar) + ".java");
                Check.True(File.Exists(source), $"{element.Tag} の {name}={value} の Java のソースがありません: {source}");
                checkedCount++;
            }
        }
        // W1-3 で alarm の受信機 2 つ（AlarmReceiver・BootReceiver）、W1-4a で RingService。行を足しても自動で確かめられる
        Check.True(checkedCount >= 3, $"確かめた数 {checkedCount}");

        // 確かめ方そのものが働くこと（main に常設の PlatformProvider のソースは見つかる）
        var provider = Path.Combine(javaRoot, "com", "seedengine", "runtime", "platform", "service", "PlatformProvider.java");
        Check.True(File.Exists(provider), "Java のソースの置き場の求め方が正しい");
    }

    /// <summary>要素の木を平らにする。</summary>
    private static System.Collections.Generic.IEnumerable<AndroidManifestElement> Flatten(System.Collections.Generic.IEnumerable<AndroidManifestElement> elements) =>
        elements.SelectMany(e => new[] { e }.Concat(Flatten(e.Children)));

    /// <summary>壊れた表。</summary>
    private static void CatalogRejectsMalformedTables()
    {
        static void Rejects(string json, string what)
        {
            try
            {
                AndroidPlatformFeatureCatalog.Parse(json);
                throw new AssertionException($"{what}: 例外が出ない");
            }
            catch (InvalidDataException)
            {
                // 期待どおり
            }
        }
        Rejects("{ \"format_version\": 2, \"features\": [] }", "版が違う");
        Rejects("{ \"format_version\": 1 }", "features が無い");
        Rejects("{ \"format_version\": 1, \"features\": [ { \"name\": \"a\", \"label\": \"A\" }, { \"name\": \"a\", \"label\": \"B\" } ] }", "名前の重なり");
        Rejects("{ \"format_version\": 1, \"features\": [ { \"name\": \"Alarm\", \"label\": \"A\" } ] }", "名前の大文字");
        Rejects("{ \"format_version\": 1, \"features\": [ { \"name\": \"a\", \"label\": \"A\", \"permissions\": [ { \"name\": \"WAKE LOCK\" } ] } ] }", "権限の名前の形");
        Rejects("{ \"format_version\": 1, \"features\": [ { \"name\": \"a\", \"label\": \"A\", \"permissions\": [ { \"name\": \"a.b\", \"max_sdk_version\": 0 } ] } ] }", "maxSdkVersion 0");
        Rejects("{ \"format_version\": 1, \"features\": [ { \"name\": \"a\", \"label\": \"A\", \"application_elements\": [ { \"tag\": \"receiver\", \"attributes\": { \"tools:node\": \"merge\" } } ] } ] }", "android: 以外の前置き");
        Rejects("not json", "JSON でない");

        // 正しい表（コメント・末尾のカンマは許す。属性の真偽・数値は文字列にする）
        var catalog = AndroidPlatformFeatureCatalog.Parse(
            "// comment\n{ \"format_version\": 1, \"features\": [ { \"name\": \"x\", \"label\": \"X\", \"application_elements\": [ " +
            "{ \"tag\": \"receiver\", \"attributes\": { \"android:name\": \"a.B\", \"android:exported\": false, \"android:priority\": 5 }, " +
            "\"children\": [ { \"tag\": \"intent-filter\" } ] } ], }, ] }");
        var receiver = catalog.Features.Single().ApplicationElements.Single();
        Check.Equal("false", receiver.Attributes.Single(a => a.Key == "android:exported").Value, "真偽は false");
        Check.Equal("5", receiver.Attributes.Single(a => a.Key == "android:priority").Value, "数値");
        Check.Equal("intent-filter", receiver.Children.Single().Tag, "子の要素");
    }

    // ── プロジェクト設定 → 機能 ─────────────────────────────

    /// <summary>設定なし。</summary>
    private static void ResolveEmpty()
    {
        var catalog = AndroidPlatformFeatureCatalog.BuiltIn;
        foreach (var settings in new[] { null, new AndroidAppSettings(), new AndroidAppSettings { Features = new() } })
        {
            var set = AndroidPlatformFeatureResolver.Resolve(settings, catalog);
            Check.True(set.Features.Count == 0 && set.Permissions.Count == 0 && set.DeepLinks.Count == 0, "機能なし");
            Check.Equal(AndroidSystemBarsSetting.Hidden, set.SystemBars, "システムバーの既定");
            Check.Equal(AndroidAppCategorySetting.Game, set.AppCategory, "分類の既定");
            Check.True(!set.PredictiveBack, "予測型の戻るは既定で使わない（W2 の手直し P1-3）");
            Check.True(set.Warnings.Count == 0 && set.Errors.Count == 0, "注意も誤りもない");
        }
    }

    /// <summary>予測型の戻る（W2 の手直し P1-3）: true のときだけ使う（false・未設定は使わない）。ログの 1 行は使うときだけ変わる。</summary>
    private static void ResolvePredictiveBack()
    {
        var catalog = AndroidPlatformFeatureCatalog.BuiltIn;
        var on = AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings { PredictiveBack = true }, catalog);
        Check.True(on.PredictiveBack, "true は使う");
        Check.True(on.Describe().EndsWith("・予測型の戻る"), $"ログに出る: {on.Describe()}");
        Check.True(on.Warnings.Count == 0 && on.Errors.Count == 0, "注意も誤りもない");
        var off = AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings { PredictiveBack = false, SystemBars = "visible" }, catalog);
        Check.True(!off.PredictiveBack, "false は使わない");
        var unset = AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings { SystemBars = "visible" }, catalog);
        Check.Equal(unset.Describe(), off.Describe(), "false と未設定のログは同じ（従来の 1 行）");
        Check.True(!off.Describe().Contains("予測型の戻る"), "使わないときはログを変えない");
    }

    /// <summary>features のそろえ方と知らない名前。</summary>
    private static void ResolveFeaturesAndUnknown()
    {
        var settings = new AndroidAppSettings { Features = new() { "Notifications", " alarm ", "alarm", "future_x", "FUTURE_X", "" } };
        var set = AndroidPlatformFeatureResolver.Resolve(settings, AndroidPlatformFeatureCatalog.BuiltIn);
        Check.Equal("alarm,notifications", string.Join(",", set.FeatureNames), "表の順（書いた順によらない）・重なりなし");
        // alarm の 8（W1-6 で VIBRATE を main の常設へ移した）＋ notifications の POST_NOTIFICATIONS（alarm と重なるので 1 つ）
        Check.Equal(8, set.Permissions.Count, "POST_NOTIFICATIONS は 1 つ");
        Check.Equal(1, set.Permissions.Count(p => p.Name == "android.permission.POST_NOTIFICATIONS"), "重なりなし");
        Check.Equal("future_x", string.Join(",", set.UnknownFeatures), "知らない名前（大文字小文字違いは 1 つ）");
        Check.True(set.Warnings.Single().Contains("future_x") && set.Warnings.Single().Contains("alarm / notifications / deep_links"), $"注意に使える機能: {set.Warnings.Single()}");
        Check.Equal(0, set.Errors.Count, "知らない名前は誤りにしない");
        Check.True(set.Describe().Contains("alarm, notifications"), set.Describe());
    }

    /// <summary>maxSdkVersion の寄せ方。</summary>
    private static void MergePermissionsWidensMaxSdk()
    {
        static AndroidPlatformFeature Feature(string name, params AndroidManifestPermission[] permissions) =>
            new(name, name, string.Empty, permissions, System.Array.Empty<AndroidManifestElement>(), false);
        var merged = AndroidPlatformFeatureResolver.MergePermissions(new[]
        {
            Feature("a", new AndroidManifestPermission("p.A", 30), new AndroidManifestPermission("p.B", 28)),
            Feature("b", new AndroidManifestPermission("p.A", null), new AndroidManifestPermission("p.B", 32), new AndroidManifestPermission("p.C", 29)),
        });
        Check.Equal("p.A,p.B,p.C", string.Join(",", merged.Select(p => p.Name)), "最初に出た順");
        Check.Equal((int?)null, merged[0].MaxSdkVersion, "無制限が勝つ");
        Check.Equal(32, merged[1].MaxSdkVersion, "大きいほう");
        Check.Equal(29, merged[2].MaxSdkVersion, "1 つだけならそのまま");
    }

    /// <summary>ディープリンク。</summary>
    private static void ResolveDeepLinks()
    {
        var catalog = AndroidPlatformFeatureCatalog.BuiltIn;
        AndroidDeepLinkSetting Link(string? scheme, string? host = null, string? path = null, bool verify = false) =>
            new() { Scheme = scheme, Host = host, PathPrefix = path, AutoVerify = verify };

        // 機能が無いのに一覧がある → 入れない・注意
        var off = AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings { DeepLinks = new() { Link("https", "example.com") } }, catalog);
        Check.True(off.DeepLinks.Count == 0 && off.Warnings.Single().Contains("deep_links"), "機能なしは入れない");

        // 機能があるのに一覧が空 → 注意（空の行は数えない）
        var empty = AndroidPlatformFeatureResolver.Resolve(
            new AndroidAppSettings { Features = new() { "deep_links" }, DeepLinks = new() { new AndroidDeepLinkSetting() } }, catalog);
        Check.True(empty.DeepLinks.Count == 0 && empty.Warnings.Single().Contains("1 件もありません"), "一覧が空");

        // 正しいもの・注意・重なり
        var ok = AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings
        {
            Features = new() { "deep_links" },
            DeepLinks = new()
            {
                Link("https", "example.com", "/wake", verify: true),
                Link("wakeorpay", "open_alarm"),
                Link("wakeorpay", verify: true),        // 独自のスキームの autoVerify → 注意
                Link(" https ", " example.com ", " /wake ", verify: true), // 前後の空白は落として同じ → まとめる
                Link("myapp", "*.example.com"),
            },
        }, catalog);
        Check.Equal(0, ok.Errors.Count, $"誤りなし: {string.Join(" / ", ok.Errors)}");
        Check.Equal(4, ok.DeepLinks.Count, "重なりを除いた件数");
        Check.True(ok.Warnings.Any(w => w.Contains("auto_verify")), "独自のスキームの autoVerify は注意");
        Check.True(ok.Warnings.Any(w => w.Contains("まとめます")), "重なりは注意");

        // 形の誤り（1 件ずつ誤りになり、その件は入れない）
        var bad = AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings
        {
            Features = new() { "deep_links" },
            DeepLinks = new()
            {
                Link("HTTPS", "example.com"),            // 大文字の scheme
                Link(null, "example.com"),               // scheme なし
                Link("https", "Example.com"),            // 大文字の host
                Link("https", "example.com:8080"),       // ポート
                Link("wakeorpay", null, "/alarm"),       // host なしの path
                Link("https", "example.com", "wake"),    // / で始まらない
                Link("https", "example.com", "/a b?c"),  // 空白・?
                Link("https", "example.com", "/x${y}"),  // マニフェストの置き換えの記法
                Link("https", "example.com", "/ok"),     // 正しい
            },
        }, catalog);
        Check.Equal(8, bad.Errors.Count, $"誤り 8 件: {string.Join(" / ", bad.Errors)}");
        Check.True(bad.Errors[0].Contains("小文字"), "大文字の scheme は小文字の案内");
        Check.True(bad.Errors.Any(e => e.Contains("scheme がありません")), "scheme なし");
        Check.True(bad.Errors.Any(e => e.Contains("host も書いて")), "host なしの path");
        Check.Equal(1, bad.DeepLinks.Count, "正しい件だけ入る");
        Check.Equal("/ok", bad.DeepLinks.Single().PathPrefix, "正しい件");
    }

    /// <summary>システムバーと分類。</summary>
    private static void ResolveSystemBarsAndCategory()
    {
        var catalog = AndroidPlatformFeatureCatalog.BuiltIn;
        var visible = AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings { SystemBars = " Visible ", AppCategory = "PRODUCTIVITY" }, catalog);
        Check.True(visible.SystemBarsVisible, "visible");
        Check.Equal("productivity", visible.AppCategory, "分類は小文字へ");
        Check.Equal(0, visible.Warnings.Count, "知っている値は注意なし");

        var unknown = AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings { SystemBars = "immersive", AppCategory = "tools" }, catalog);
        Check.True(!unknown.SystemBarsVisible && unknown.AppCategory == "game", "知らない値は既定値");
        Check.Equal(2, unknown.Warnings.Count, "2 つとも注意");
        Check.True(unknown.Warnings[1].Contains("productivity"), "使える値を示す");
    }

    // ── 生成 ────────────────────────────────────────────────

    /// <summary>機能なしの生成物。</summary>
    private static void WriterEmpty()
    {
        var files = AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureSet.Empty);
        var manifest = XDocument.Parse(files.ManifestText);
        Check.Equal("manifest", manifest.Root!.Name.LocalName, "ルートは manifest");
        Check.Equal(0, manifest.Root.Elements().Count(), "中身の無い manifest");
        var values = XDocument.Parse(files.ValuesText);
        var boolean = values.Root!.Element("bool")!;
        Check.Equal(AndroidPlatformManifestWriter.SystemBarsVisibleResourceName, (string?)boolean.Attribute("name"), "リソース名");
        Check.Equal("false", boolean.Value, "隠す");
        Check.Equal(1, values.Root.Elements("bool").Count(), "予測型の戻るの印は書かない（W2 の手直し P1-3）");
        Check.True(files.ManifestText.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"), "UTF-8 の宣言と LF");
        Check.True(!files.Files.Values.Any(bytes => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "BOM なし");
        Check.True(!files.ManifestText.Contains('\r'), "CR なし");
        Check.Equal(EmptyFilesDigestBeforePredictiveBack, files.Digest,
            "android 節の無いプロジェクト（WarashibeFishing など）の生成物は予測型の戻るを足す前とバイト単位で同じ");
    }

    /// <summary>予測型の戻る（W2 の手直し P1-3）: values の印は true のときだけ。マニフェストの断片は変えない。</summary>
    private static void WriterPredictiveBack()
    {
        var catalog = AndroidPlatformFeatureCatalog.BuiltIn;
        var unset = AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureResolver.Resolve(AppLikeSettings(null), catalog));
        var off = AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureResolver.Resolve(AppLikeSettings(false), catalog));
        Check.Equal(unset.Digest, off.Digest, "false と未設定は同じバイト列");
        Check.True(unset.Files.All(pair => pair.Value.SequenceEqual(off.Files[pair.Key])), "バイト列も同じ");
        Check.Equal(1, XDocument.Parse(off.ValuesText).Root!.Elements("bool").Count(), "使わないときは seed_system_bars_visible だけ");
        Check.True(!off.ValuesText.Contains(AndroidPlatformManifestWriter.PredictiveBackResourceName)
                   && !off.ValuesText.Contains(AndroidAppSettings.PredictiveBackKey), "頭のコメントにも書かない");

        var on = AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureResolver.Resolve(AppLikeSettings(true), catalog));
        Check.Equal(off.ManifestText, on.ManifestText, "マニフェストの断片は変えない（enableOnBackInvokedCallback は main のマニフェストのプレースホルダ）");
        var bools = XDocument.Parse(on.ValuesText).Root!.Elements("bool").ToList();
        Check.Equal(2, bools.Count, "システムバーと予測型の戻るの 2 つ");
        Check.Equal(AndroidPlatformManifestWriter.SystemBarsVisibleResourceName, (string?)bools[0].Attribute("name"), "システムバーが先（従来の並び）");
        Check.Equal("true", bools[0].Value, "visible のまま");
        Check.Equal(AndroidPlatformManifestWriter.PredictiveBackResourceName, (string?)bools[1].Attribute("name"), "予測型の戻るの印");
        Check.Equal("true", bools[1].Value, "true");
        Check.True(on.ValuesText.Contains($"{AndroidAppSettings.PredictiveBackKey}: true"), "頭のコメントに設定を書く");
        Check.True(on.Digest != off.Digest, "指紋が変わる（APK を作り直す）");
        Check.True(!on.ValuesText.Contains('\r'), "CR なし");
    }

    /// <summary>alarm と notifications。</summary>
    private static void WriterAlarmAndNotifications()
    {
        var set = AndroidPlatformFeatureResolver.Resolve(
            new AndroidAppSettings { Features = new() { "alarm", "notifications" }, SystemBars = "visible" }, AndroidPlatformFeatureCatalog.BuiltIn);
        var files = AndroidPlatformManifestWriter.Render(set);
        var manifest = XDocument.Parse(files.ManifestText).Root!;
        var permissions = manifest.Elements("uses-permission").ToList();
        Check.Equal(8, permissions.Count, "権限 8（W1-6 で VIBRATE は main の常設へ移した）");
        var schedule = permissions.Single(p => (string?)p.Attribute(AndroidNs + "name") == "android.permission.SCHEDULE_EXACT_ALARM");
        Check.Equal("32", (string?)schedule.Attribute(AndroidNs + "maxSdkVersion"), "maxSdkVersion");
        // alarm の受信機 2 つ（W1-3）と鳴動の前景サービス（W1-4a）と見張りの受信機（W1-7）。ディープリンクが無いので MainActivity の要素は書かない
        var application = manifest.Element("application")!;
        Check.True(application.Element("activity") is null, "ディープリンクが無ければ MainActivity を書かない");
        var receivers = application.Elements("receiver").ToList();
        Check.Equal(AlarmReceiverClass + "," + BootReceiverClass + "," + RingWatchdogReceiverClass,
            string.Join(",", receivers.Select(r => (string?)r.Attribute(AndroidNs + "name"))), "受信機（表の順）");
        Check.True(receivers.All(r => (string?)r.Attribute(AndroidNs + "process") == ":seed_platform"
                                      && (string?)r.Attribute(AndroidNs + "exported") == "false"
                                      && (string?)r.Attribute(AndroidNs + "directBootAware") == "true"), "受信機の属性");
        var actions = receivers[1].Element("intent-filter")!.Elements("action").Select(a => "action:" + (string?)a.Attribute(AndroidNs + "name"));
        Check.Equal(string.Join(",", BootReceiverActions), string.Join(",", actions), "BootReceiver の intent-filter");
        var service = application.Elements("service").Single();
        Check.Equal(RingServiceClass, (string?)service.Attribute(AndroidNs + "name"), "鳴動のサービス");
        Check.True((string?)service.Attribute(AndroidNs + "process") == ":seed_platform"
                   && (string?)service.Attribute(AndroidNs + "exported") == "false"
                   && (string?)service.Attribute(AndroidNs + "directBootAware") == "true"
                   && (string?)service.Attribute(AndroidNs + "foregroundServiceType") == "mediaPlayback", "鳴動のサービスの属性");
        Check.True(files.ManifestText.Contains("機能: alarm, notifications"), "頭のコメントに機能");
        Check.Equal("true", XDocument.Parse(files.ValuesText).Root!.Element("bool")!.Value, "visible は true");
    }

    /// <summary>ディープリンクの intent-filter。</summary>
    private static void WriterDeepLinks()
    {
        var set = AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings
        {
            Features = new() { "deep_links" },
            DeepLinks = new()
            {
                new AndroidDeepLinkSetting { Scheme = "https", Host = "example.com", PathPrefix = "/wake&go", AutoVerify = true },
                new AndroidDeepLinkSetting { Scheme = "wakeorpay" },
            },
        }, AndroidPlatformFeatureCatalog.BuiltIn);
        Check.Equal(0, set.Errors.Count, string.Join(" / ", set.Errors));
        var manifest = XDocument.Parse(AndroidPlatformManifestWriter.Render(set).ManifestText).Root!;
        var activity = manifest.Element("application")!.Element("activity")!;
        Check.Equal(AndroidRuntimeContract.LaunchActivityClassName, (string?)activity.Attribute(AndroidNs + "name"), "MainActivity（完全修飾）");
        var filters = activity.Elements("intent-filter").ToList();
        Check.Equal(2, filters.Count, "1 件 1 つ（data が掛け合わされないように）");
        Check.Equal("true", (string?)filters[0].Attribute(AndroidNs + "autoVerify"), "autoVerify");
        Check.True(filters[1].Attribute(AndroidNs + "autoVerify") is null, "false なら書かない");
        foreach (var filter in filters)
        {
            Check.Equal(AndroidPlatformManifestWriter.ViewAction, (string?)filter.Element("action")!.Attribute(AndroidNs + "name"), "VIEW");
            var categories = filter.Elements("category").Select(c => (string?)c.Attribute(AndroidNs + "name")).ToList();
            Check.True(categories.Contains(AndroidPlatformManifestWriter.DefaultCategory) && categories.Contains(AndroidPlatformManifestWriter.BrowsableCategory), "DEFAULT と BROWSABLE");
        }
        var data = filters[0].Element("data")!;
        Check.Equal("https", (string?)data.Attribute(AndroidNs + "scheme"), "scheme");
        Check.Equal("example.com", (string?)data.Attribute(AndroidNs + "host"), "host");
        Check.Equal("/wake&go", (string?)data.Attribute(AndroidNs + "pathPrefix"), "& はエスケープされ、読み直すと元の値");
        var custom = filters[1].Element("data")!;
        Check.True(custom.Attribute(AndroidNs + "host") is null && custom.Attribute(AndroidNs + "pathPrefix") is null, "無い欄は書かない");
    }

    /// <summary>同じ入力 → 同じバイト列。</summary>
    private static void WriterIsDeterministic()
    {
        var catalog = AndroidPlatformFeatureCatalog.BuiltIn;
        var a = AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings { Features = new() { "notifications", "alarm" } }, catalog));
        var b = AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings { Features = new() { "alarm", "notifications" } }, catalog));
        Check.Equal(a.Digest, b.Digest, "features の順によらない");
        Check.True(a.Files.Keys.SequenceEqual(b.Files.Keys) && a.Files.All(pair => pair.Value.SequenceEqual(b.Files[pair.Key])), "バイト列も同じ");
        var visible = AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureResolver.Resolve(
            new AndroidAppSettings { Features = new() { "alarm", "notifications" }, SystemBars = "visible" }, catalog));
        Check.True(visible.Digest != a.Digest, "システムバーが変われば指紋も変わる");
        Check.True(AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureSet.Empty).Digest != a.Digest, "機能が変われば指紋も変わる");
        Check.Equal(64, a.Digest.Length, "SHA-256 の 16 進");
    }

    /// <summary>置き場。</summary>
    private static void StagerWritesAndCleans()
    {
        using var temp = new TempDir();
        var staging = temp.Combine("app/src/seedFeatures");
        var alarm = AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureResolver.Resolve(
            new AndroidAppSettings { Features = new() { "alarm" } }, AndroidPlatformFeatureCatalog.BuiltIn));

        var first = AndroidPlatformFeatureStager.Stage(staging, alarm);
        Check.Equal(2, first.Written, "マニフェストと values を書く");
        var manifestPath = Path.Combine(staging, "AndroidManifest.xml");
        var stamp = File.GetLastWriteTimeUtc(manifestPath);

        var second = AndroidPlatformFeatureStager.Stage(staging, alarm);
        Check.True(second.Written == 0 && second.Unchanged == 2, "同じ中身は書かない");
        Check.Equal(stamp, File.GetLastWriteTimeUtc(manifestPath), "更新時刻を変えない");

        temp.WriteFile("app/src/seedFeatures/res/values/old.xml", "<resources/>");
        temp.WriteFile("app/src/seedFeatures/res/drawable/stale.xml", "<x/>");
        var empty = AndroidPlatformFeatureStager.Stage(staging, AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureSet.Empty));
        Check.Equal(1, empty.Written, "機能が空でも中身の無いマニフェストを書く（前の権限を消す）");
        Check.Equal(1, empty.Unchanged, "values は同じ中身（どちらも hidden）なので書かない");
        Check.Equal(2, empty.Removed, "一覧に無いファイルは消す");
        Check.True(!Directory.Exists(Path.Combine(staging, "res", "drawable")), "空になったフォルダも消す");
        Check.True(!File.ReadAllText(manifestPath).Contains("uses-permission"), "前の権限が残らない");
    }

    // ── Gradle ──────────────────────────────────────────────

    /// <summary>seed.appCategory と指紋。</summary>
    private static void GradlePropertyAndFingerprint()
    {
        var identity = AndroidAppIdentityResolver.Resolve(null, "MyGame", "My Game", hasProjectContext: true);
        var plain = new GradleBuildParameters(new[] { AndroidAbis.Arm64 }, "both", identity, null);
        Check.True(!GradleInvocation.Build(plain).Arguments.Any(a => a.StartsWith("-Pseed.appCategory")), "未指定は渡さない");
        Check.True(!GradleInvocation.Build(plain with { AppCategory = "game" }).Arguments.Any(a => a.StartsWith("-Pseed.appCategory")), "既定の game は渡さない");
        Check.True(GradleInvocation.Build(plain with { AppCategory = "productivity" }).Arguments.Contains("-Pseed.appCategory=productivity"), "それ以外は渡す");

        using var temp = new TempDir();
        var engine = new AndroidEnginePaths(temp.Combine("repo"));
        var catalog = AndroidPlatformFeatureCatalog.BuiltIn;
        var none = AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureSet.Empty);
        var alarm = AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings { Features = new() { "alarm" } }, catalog));
        var withNone = AndroidStepFingerprints.Gradle(engine, plain, null, none);
        Check.True(withNone.Inputs != AndroidStepFingerprints.Gradle(engine, plain, null, alarm).Inputs, "機能を変えれば APK を作り直す");
        Check.Equal(withNone.Inputs, AndroidStepFingerprints.Gradle(engine, plain, null, AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureSet.Empty)).Inputs, "同じなら同じ指紋");
        Check.True(withNone.Inputs != AndroidStepFingerprints.Gradle(engine, plain with { AppCategory = "productivity" }, null, none).Inputs, "分類を変えても作り直す");

        // 置き場を消したら（中身が同じでも）作り直す
        AndroidPlatformFeatureStager.Stage(engine.PlatformFeaturesStagingDir, none);
        var staged = AndroidStepFingerprints.Gradle(engine, plain, null, none);
        Directory.Delete(engine.PlatformFeaturesStagingDir, recursive: true);
        Check.True(staged.Inputs != AndroidStepFingerprints.Gradle(engine, plain, null, none).Inputs, "置き場が無くなれば作り直す");

        // 予測型の戻る（W2 の手直し P1-3）: 使うときだけ -Pseed.predictiveBack=true。使わないプロジェクトの引数は予測型の戻るを足す前と同じ並び
        Check.Equal(
            "assembleDebug -Pseed.abis=arm64-v8a -Pseed.orientation=both -Pseed.applicationId=com.seedengine.mygame -Pseed.appName=My Game " +
            "-Pseed.versionCode=1 -Pseed.versionName=1.0 --console=plain",
            string.Join(" ", GradleInvocation.Build(plain).Arguments), "使わないプロジェクトの引数は従来と同じ");
        Check.True(!GradleInvocation.Build(plain).Properties.Any(p => p.Name == GradleInvocation.PredictiveBackProperty), "使わなければ一覧（指紋の材料）にも無い");
        var predictive = plain with { PredictiveBack = true };
        Check.True(GradleInvocation.Build(predictive).Arguments.Contains("-Pseed.predictiveBack=true"), "使うときだけ渡す");
        Check.Equal(
            string.Join(" ", GradleInvocation.Build(plain with { AppCategory = "productivity" }).Arguments).Replace(" --console=plain", string.Empty) +
            " -Pseed.predictiveBack=true --console=plain",
            string.Join(" ", GradleInvocation.Build(plain with { AppCategory = "productivity", PredictiveBack = true }).Arguments),
            "並びは分類の後（署名の前）");
        var predictiveFiles = AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureResolver.Resolve(new AndroidAppSettings { PredictiveBack = true }, catalog));
        var plainNow = AndroidStepFingerprints.Gradle(engine, plain, null, none).Inputs;
        Check.Equal(plainNow, AndroidStepFingerprints.Gradle(engine, plain with { PredictiveBack = false }, null, none).Inputs, "false は指紋を変えない");
        Check.True(plainNow != AndroidStepFingerprints.Gradle(engine, predictive, null, none).Inputs, "-P が変われば作り直す");
        Check.True(plainNow != AndroidStepFingerprints.Gradle(engine, plain, null, predictiveFiles).Inputs, "values の印が変われば作り直す");
    }

    // ── リポジトリとの取り決め ──────────────────────────────

    /// <summary>Gradle・main のマニフェスト・Java・.gitignore と中核の値が一致する。</summary>
    private static void ContractsMatchRepository()
    {
        var engine = AndroidEnginePaths.Locate(System.AppContext.BaseDirectory, System.Environment.CurrentDirectory)!;
        var gradle = File.ReadAllText(Path.Combine(engine.AndroidDir, "app", "build.gradle.kts"));

        // appCategory の語彙（build.gradle.kts の appCategoryValues ＝ エディタの AndroidAppCategorySetting.Choices）
        var match = Regex.Match(gradle, @"val appCategoryValues = listOf\((?<values>[^)]*)\)");
        Check.True(match.Success, "build.gradle.kts に appCategoryValues がある");
        var gradleValues = Regex.Matches(match.Groups["values"].Value, "\"([a-z]+)\"").Select(m => m.Groups[1].Value).OrderBy(v => v).ToList();
        var editorValues = AndroidAppCategorySetting.Choices.Select(c => c.Value).OrderBy(v => v).ToList();
        Check.Equal(string.Join(",", editorValues), string.Join(",", gradleValues), "appCategory の語彙が一致");
        Check.True(gradle.Contains($"val defaultAppCategory = \"{AndroidAppCategorySetting.Default}\""), "既定値が一致");
        Check.True(gradle.Contains($"seedProperty(\"{GradleInvocation.AppCategoryProperty["seed.".Length..]}\")"), "プロパティ名が一致");

        // 置き場（build.gradle.kts の seedFeaturesDir ＝ AndroidEnginePaths.PlatformFeaturesStagingDir）
        Check.True(gradle.Contains("val seedFeaturesDir = \"src/seedFeatures\""), "build.gradle.kts の置き場");
        Check.Equal(Path.Combine(engine.AndroidDir, "app", "src", "seedFeatures"), engine.PlatformFeaturesStagingDir, "中核の置き場");
        Check.True(gradle.Contains($"\"$seedFeaturesDir/{AndroidPlatformFeatureFiles.ManifestRelativePath}\""), "マニフェストの名前が一致");

        // main のマニフェストは appCategory を置き換える・PlatformProvider は常設
        var manifest = File.ReadAllText(Path.Combine(engine.AndroidDir, "app", "src", "main", "AndroidManifest.xml"));
        Check.True(manifest.Contains("android:appCategory=\"${seedAppCategory}\""), "main のマニフェストの appCategory");
        Check.True(manifest.Contains("android:name=\".platform.service.PlatformProvider\""), "PlatformProvider は main に常設");

        // W1-6: VIBRATE は main に常設（SEED.Platform の Haptics と鳴動の振動。features が空の APK にも入る）
        var mainPermissions = XDocument.Parse(manifest).Root!.Elements("uses-permission")
            .Select(p => (string?)p.Attribute(AndroidNs + "name")).ToList();
        Check.True(mainPermissions.Contains("android.permission.VIBRATE"), "VIBRATE は main のマニフェストに常設");

        // 信頼できる起動の入口 PlatformEntry（W1-4a）: main に常設の activity-alias・exported=false・対象は MainActivity・
        // GameActivity が起動の部品の ActivityInfo から読む lib_name を別名にも持つ・Java の定数と同じ名前
        var mainDoc = XDocument.Parse(manifest);
        var alias = mainDoc.Root!.Element("application")!.Elements("activity-alias").Single();
        var namespaceName = "com.seedengine.runtime";
        var aliasName = (string?)alias.Attribute(AndroidNs + "name");
        Check.Equal(PlatformEntryAlias, aliasName!.StartsWith('.') ? namespaceName + aliasName : aliasName, "PlatformEntry の名前");
        Check.Equal("false", (string?)alias.Attribute(AndroidNs + "exported"), "PlatformEntry は exported=false");
        Check.Equal(".MainActivity", (string?)alias.Attribute(AndroidNs + "targetActivity"), "PlatformEntry の対象は MainActivity");
        Check.True(!alias.Elements("intent-filter").Any(), "PlatformEntry は intent-filter を持たない");
        var libName = alias.Elements("meta-data").SingleOrDefault(m => (string?)m.Attribute(AndroidNs + "name") == "android.app.lib_name");
        var mainLibName = mainDoc.Root.Element("application")!.Element("activity")!.Elements("meta-data")
            .Single(m => (string?)m.Attribute(AndroidNs + "name") == "android.app.lib_name");
        Check.Equal((string?)mainLibName.Attribute(AndroidNs + "value"), (string?)libName?.Attribute(AndroidNs + "value"),
            "PlatformEntry にも MainActivity と同じ lib_name（無いと GameActivity が libmain.so を探して落ちる）");
        var contract = File.ReadAllText(Path.Combine(engine.AndroidDir, "app", "src", "main", "java", "com", "seedengine", "runtime", "platform", "PlatformContract.java"));
        Check.True(contract.Contains($"PLATFORM_ENTRY_ALIAS = \"{PlatformEntryAlias}\""), "Java の PlatformContract.PLATFORM_ENTRY_ALIAS と一致");

        // bool のリソース名（main の既定値・Java・生成物）
        var defaults = XDocument.Load(Path.Combine(engine.AndroidDir, "app", "src", "main", "res", "values", "seed_platform_defaults.xml"));
        var defaultBool = defaults.Root!.Elements("bool").Single(e => (string?)e.Attribute("name") == AndroidPlatformManifestWriter.SystemBarsVisibleResourceName);
        Check.Equal("false", defaultBool.Value, "main の既定値は false（従来どおり隠す）");
        var java = File.ReadAllText(Path.Combine(engine.AndroidDir, "app", "src", "main", "java", "com", "seedengine", "runtime", "SystemBarsController.java"));
        Check.True(java.Contains($"R.bool.{AndroidPlatformManifestWriter.SystemBarsVisibleResourceName}"), "Java が同じ名前を読む");

        // 予測型の戻る（W2 の手直し P1-3）: main のマニフェストのプレースホルダ・Gradle のプロパティ名と既定値 "false"・
        // res の既定値 false・Java の R.bool の名前・Gradle の食い違いの確かめの行が書き手の出力と一致する
        Check.True(manifest.Contains("android:enableOnBackInvokedCallback=\"${seedEnableOnBackInvokedCallback}\""),
            "main のマニフェストの enableOnBackInvokedCallback はプレースホルダ");
        Check.True(gradle.Contains("manifestPlaceholders[\"seedEnableOnBackInvokedCallback\"] = seedEnableOnBackInvokedCallback"),
            "build.gradle.kts がプレースホルダを置き換える");
        Check.True(gradle.Contains($"seedProperty(\"{GradleInvocation.PredictiveBackProperty["seed.".Length..]}\")"), "予測型の戻るのプロパティ名が一致");
        Check.True(gradle.Contains($"val predictiveBackEnabledValue = \"{GradleInvocation.PredictiveBackEnabledValue}\""), "「使う」の値が一致");
        Check.True(gradle.Contains("val predictiveBackDisabledValue = \"false\""), "Gradle の既定値（渡されないとき）は \"false\"");
        var predictiveDefault = defaults.Root.Elements("bool").Single(e => (string?)e.Attribute("name") == AndroidPlatformManifestWriter.PredictiveBackResourceName);
        Check.Equal("false", predictiveDefault.Value, "main の既定値は false（従来の戻るキー）");
        var backController = File.ReadAllText(Path.Combine(engine.AndroidDir, "app", "src", "main", "java", "com", "seedengine", "runtime", "back", "BackCallbackController.java"));
        Check.True(backController.Contains($"R.bool.{AndroidPlatformManifestWriter.PredictiveBackResourceName}"), "Java が同じ名前の印を読む");
        var predictiveValues = AndroidPlatformManifestWriter.Render(AndroidPlatformFeatureResolver.Resolve(
            new AndroidAppSettings { PredictiveBack = true }, AndroidPlatformFeatureCatalog.BuiltIn)).ValuesText;
        var resourceLine = Regex.Match(gradle, "val predictiveBackResourceLine = \"(?<line>(?:[^\"\\\\]|\\\\.)*)\"");
        Check.True(resourceLine.Success, "build.gradle.kts に predictiveBackResourceLine がある");
        Check.True(predictiveValues.Contains(resourceLine.Groups["line"].Value.Replace("\\\"", "\"")),
            "Gradle の食い違いの確かめの行が書き手の出力に含まれる");

        // 生成物は追跡しない
        var gitignore = File.ReadAllText(Path.Combine(engine.AndroidDir, ".gitignore"));
        Check.True(gitignore.Split('\n').Any(line => line.Trim() == "app/src/seedFeatures/"), ".gitignore に app/src/seedFeatures/");
    }
}
