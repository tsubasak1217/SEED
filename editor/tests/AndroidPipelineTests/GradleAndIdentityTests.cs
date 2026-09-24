using System.Linq;
using System.Text.Json;
using SEEDEditor.Android;
using SEEDEditor.Android.Gradle;
using SEEDEditor.Android.Project;
using SEEDEditor.ProjectSettings;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>Gradle の引数の組み立てと、アプリの識別情報（既定値・検査・"android" 節の読み取り）。</summary>
public static class GradleAndIdentityTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("Gradle: ABI・向き・NDK・識別情報を -P で渡す（assembleDebug と --console=plain）", GradleArgumentsForProject);
        harness.Add("Gradle: Gradle の既定値に任せる識別情報は渡さない", GradleOmitsGradleDefaults);
        harness.Add("Gradle: cmd.exe が解釈する文字を含む値は環境変数 ORG_GRADLE_PROJECT_ で渡す", GradleUsesEnvironmentForUnsafeValues);
        harness.Add("既定のアプリ ID: .seedproj の名前を英数字化して com.seedengine. の後ろへ", DefaultApplicationIds);
        harness.Add("既定のアプリ ID: 作った ID は常に Android の規則を満たす", DefaultApplicationIdsAreValid);
        harness.Add("識別情報: 書かれた値 → プロジェクト名 → 既定値の順に決まる", ResolvePrecedence);
        harness.Add("識別情報: プロジェクトが無ければ Gradle の既定値（ID は com.seedengine.runtime）", ResolveWithoutProject);
        harness.Add("識別情報の検査: ID の形式・名前の先頭の @ ?・版の範囲・制御文字", ValidatesExplicitValues);
        harness.Add("\"android\" 節: 型の違う値は未設定・数字の文字列は整数・知らないキーは保つ", AndroidSectionIsLenient);
    }

    /// <summary>プロジェクトの識別情報を作る。</summary>
    private static AndroidAppIdentity ProjectIdentity(string name = "My Game") =>
        AndroidAppIdentityResolver.Resolve(null, "MyGame", name, hasProjectContext: true);

    /// <summary>プロジェクトのビルドの引数。</summary>
    private static void GradleArgumentsForProject()
    {
        var command = GradleInvocation.Build(new GradleBuildParameters(
            new[] { AndroidAbis.Arm64, AndroidAbis.X86_64 }, "portrait", ProjectIdentity(), @"C:\Sdk\ndk\28.2.13676358"));
        var args = command.Arguments;
        Check.Equal("assembleDebug", args[0], "タスク");
        Check.Equal("--console=plain", args[^1], "素のテキスト出力");
        Check.True(args.Contains(@"-Pseed.ndkPath=C:\Sdk\ndk\28.2.13676358"), "NDK");
        Check.True(args.Contains("-Pseed.abis=arm64-v8a,x86_64"), "ABI");
        Check.True(args.Contains("-Pseed.orientation=portrait"), "向き");
        Check.True(args.Contains("-Pseed.applicationId=com.seedengine.mygame"), "アプリ ID");
        Check.True(args.Contains("-Pseed.appName=My Game"), "名前（空白は引数の引用で渡る）");
        Check.True(args.Contains("-Pseed.versionCode=1"), "整数の版");
        Check.True(args.Contains("-Pseed.versionName=1.0"), "版の文字列");
        Check.Equal(0, command.Environment.Count, "環境変数では渡さない");
    }

    /// <summary>Gradle の既定値。</summary>
    private static void GradleOmitsGradleDefaults()
    {
        var identity = AndroidAppIdentityResolver.Resolve(null, null, null, hasProjectContext: false);
        var command = GradleInvocation.Build(new GradleBuildParameters(new[] { AndroidAbis.X86_64 }, "both", identity, null));
        Check.True(!command.Arguments.Any(a => a.StartsWith("-Pseed.applicationId")), "ID は渡さない（Gradle の既定 com.seedengine.runtime）");
        Check.True(!command.Arguments.Any(a => a.StartsWith("-Pseed.appName")), "名前は渡さない");
        Check.True(!command.Arguments.Any(a => a.StartsWith("-Pseed.version")), "版は渡さない");
        Check.True(!command.Arguments.Any(a => a.StartsWith("-Pseed.ndkPath")), "NDK が分からなければ渡さない");
        Check.True(command.Arguments.Contains("-Pseed.abis=x86_64"), "ABI は渡す");
    }

    /// <summary>危ない文字の値。</summary>
    private static void GradleUsesEnvironmentForUnsafeValues()
    {
        var settings = new AndroidAppSettings { AppName = "Tom & Jerry \"100%\"", VersionName = "1.0 (beta)" };
        var identity = AndroidAppIdentityResolver.Resolve(settings, "TomJerry", null, hasProjectContext: true);
        var command = GradleInvocation.Build(new GradleBuildParameters(new[] { AndroidAbis.X86_64 }, "both", identity, null));
        Check.True(!command.Arguments.Any(a => a.Contains("Tom")), "名前はコマンドラインに載せない");
        Check.Equal("Tom & Jerry \"100%\"", command.Environment["ORG_GRADLE_PROJECT_seed.appName"], "名前は環境変数");
        Check.Equal("1.0 (beta)", command.Environment["ORG_GRADLE_PROJECT_seed.versionName"], "括弧も環境変数");
        Check.True(command.Properties.Single(p => p.Name == "seed.appName").ViaEnvironment, "ログ用の一覧にも環境変数と出る");
        Check.True(GradleInvocation.IsSafeForCommandLine("ゲーム 1"), "日本語と空白は安全");
        Check.True(!GradleInvocation.IsSafeForCommandLine("a\nb"), "改行は安全でない");
    }

    /// <summary>既定のアプリ ID。</summary>
    private static void DefaultApplicationIds()
    {
        Check.Equal("com.seedengine.warashibefishing", AndroidAppIdentityResolver.DefaultApplicationIdFor("WarashibeFishing"), "英字");
        Check.Equal("com.seedengine.mygame2", AndroidAppIdentityResolver.DefaultApplicationIdFor("My Game-2!"), "空白・記号は落とす");
        Check.Equal("com.seedengine.app3dgame", AndroidAppIdentityResolver.DefaultApplicationIdFor("3DGame"), "数字で始まれば app を前に");
        Check.Equal("com.seedengine.mygame", AndroidAppIdentityResolver.DefaultApplicationIdFor("ＭｙＧａｍｅ"), "全角は半角へ");
        var japanese = AndroidAppIdentityResolver.DefaultApplicationIdFor("釣りゲーム");
        Check.True(japanese.StartsWith("com.seedengine.app") && japanese.Length == "com.seedengine.app".Length + 8, $"何も残らなければ app ＋ ハッシュ 8 桁: {japanese}");
        Check.True(japanese != AndroidAppIdentityResolver.DefaultApplicationIdFor("釣りゲーム２"), "日本語名どうしでぶつからない");
        Check.Equal(japanese, AndroidAppIdentityResolver.DefaultApplicationIdFor("釣りゲーム"), "同じ名前なら同じ ID（毎回変わらない）");
    }

    /// <summary>作った ID の形式。</summary>
    private static void DefaultApplicationIdsAreValid()
    {
        foreach (var name in new[] { "WarashibeFishing", "My Game-2!", "3DGame", "釣りゲーム", "_", "ＭｙＧａｍｅ", "a" })
        {
            var id = AndroidAppIdentityResolver.DefaultApplicationIdFor(name);
            Check.True(AndroidAppIdentityResolver.IsValidApplicationId(id), $"{name} → {id}");
        }
    }

    /// <summary>決まる順。</summary>
    private static void ResolvePrecedence()
    {
        var settings = new AndroidAppSettings { ApplicationId = " com.example.game ", VersionCode = 7 };
        var identity = AndroidAppIdentityResolver.Resolve(settings, "MyGame", "私のゲーム", hasProjectContext: true);
        Check.Equal("com.example.game", identity.ApplicationId, "書かれた ID（前後の空白を落とす）");
        Check.Equal(AndroidIdentitySource.ProjectSettings, identity.ApplicationIdSource, "ID の出どころ");
        Check.Equal("私のゲーム", identity.AppName.Value, "名前はプロジェクトの表示名");
        Check.Equal(AndroidIdentitySource.ProjectDefault, identity.AppName.Source, "名前の出どころ");
        Check.Equal(7, identity.VersionCode.Value, "書かれた版");
        Check.Equal("1.0", identity.VersionName.Value, "版の文字列の既定値");
        Check.Equal(AndroidIdentitySource.FixedDefault, identity.VersionName.Source, "版の文字列の出どころ");

        var fromName = AndroidAppIdentityResolver.Resolve(null, "MyGame", null, hasProjectContext: true);
        Check.Equal("com.seedengine.mygame", fromName.ApplicationId, "ID はプロジェクト名から");
        Check.Equal("MyGame", fromName.AppName.Value, "表示名が無ければ name");
    }

    /// <summary>プロジェクトが無い。</summary>
    private static void ResolveWithoutProject()
    {
        var identity = AndroidAppIdentityResolver.Resolve(null, null, null, hasProjectContext: false);
        Check.Equal(AndroidRuntimeContract.DefaultApplicationId, identity.ApplicationId, "既定の ID");
        Check.Equal(AndroidIdentitySource.GradleDefault, identity.ApplicationIdSource, "出どころ");
        Check.True(identity.AppName.Value is null && identity.VersionCode.Value is null && identity.VersionName.Value is null, "他は Gradle の既定値");

        // アセットだけ（.seedproj 無し）のプロジェクトは ID と名前が Gradle の既定値、版は 1 / 1.0
        var assetsOnly = AndroidAppIdentityResolver.Resolve(null, null, null, hasProjectContext: true);
        Check.Equal(AndroidRuntimeContract.DefaultApplicationId, assetsOnly.ApplicationId, ".seedproj が無ければ com.seedengine.runtime");
        Check.Equal(1, assetsOnly.VersionCode.Value, "版");
    }

    /// <summary>検査。</summary>
    private static void ValidatesExplicitValues()
    {
        Check.Equal(0, AndroidAppIdentityResolver.Validate(null).Count, "何も無ければ違反なし");
        Check.Equal(0, AndroidAppIdentityResolver.Validate(new AndroidAppSettings { ApplicationId = "com.example.my_game2", AppName = "ゲーム", VersionCode = 2100000000, VersionName = "1.0.3" }).Count, "正しい値");
        foreach (var bad in new[] { "game", "com.example.", "1com.example", "com.2example", "com.example.my-game", "com..example" })
        {
            Check.Equal(1, AndroidAppIdentityResolver.Validate(new AndroidAppSettings { ApplicationId = bad }).Count, $"ID {bad}");
        }
        Check.Equal(1, AndroidAppIdentityResolver.Validate(new AndroidAppSettings { AppName = "@string/x" }).Count, "名前の先頭 @");
        Check.Equal(1, AndroidAppIdentityResolver.Validate(new AndroidAppSettings { AppName = " ?attr" }).Count, "空白の後の ?");
        Check.Equal(1, AndroidAppIdentityResolver.Validate(new AndroidAppSettings { AppName = "a\nb" }).Count, "名前の改行");
        Check.Equal(1, AndroidAppIdentityResolver.Validate(new AndroidAppSettings { VersionCode = 0 }).Count, "版 0");
        Check.Equal(1, AndroidAppIdentityResolver.Validate(new AndroidAppSettings { VersionCode = 2100000001 }).Count, "版の上限超え");
        Check.Equal(1, AndroidAppIdentityResolver.Validate(new AndroidAppSettings { VersionName = "1.0\t" + "\u0001" }).Count, "版の文字列の制御文字");
    }

    /// <summary>"android" 節の寛容な読み取りと往復。</summary>
    private static void AndroidSectionIsLenient()
    {
        var settings = JsonSerializer.Deserialize<AndroidAppSettings>(
            "{\"application_id\":\"com.x.y\",\"version_code\":\"2\",\"version_name\":3,\"app_name\":true,\"future_key\":{\"a\":1}}")!;
        Check.Equal("com.x.y", settings.ApplicationId, "ID");
        Check.Equal(2, settings.VersionCode, "数字の文字列は整数");
        Check.Equal("3", settings.VersionName, "数値は文字列として");
        Check.True(settings.AppName is null, "真偽値は未設定");
        Check.True(settings.ExtraData.ContainsKey("future_key"), "知らないキーを保つ");

        var written = JsonSerializer.Serialize(settings);
        using var document = JsonDocument.Parse(written);
        Check.Equal(2, document.RootElement.GetProperty("version_code").GetInt32(), "整数で書く");
        Check.True(!document.RootElement.TryGetProperty("app_name", out _), "未設定は書かない");
        Check.Equal(1, document.RootElement.GetProperty("future_key").GetProperty("a").GetInt32(), "知らないキーを書き戻す");

        var broken = JsonSerializer.Deserialize<AndroidAppSettings>("{\"version_code\":\"abc\"}")!;
        Check.True(broken.VersionCode is null, "読めない版は未設定");
        Check.True(new AndroidAppSettings().IsEmpty, "空の節");
        Check.True(JsonSerializer.Deserialize<AndroidAppSettings>("[1,2]")!.IsEmpty, "オブジェクトでなければ空");
    }
}
