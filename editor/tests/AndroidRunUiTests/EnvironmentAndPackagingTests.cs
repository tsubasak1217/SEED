using SEEDEditor.Android;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.AndroidRun;
using SEEDEditor.Packaging;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>Android の実行先を使えるか・道具の一覧・パッケージ化ウィンドウの Android 出力・中核への指定。</summary>
public static class EnvironmentAndPackagingTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("使えるか: プロジェクト無し → リポジトリ無し → adb 無し の順に理由（エラーにしない）", JudgesAvailability);
        harness.Add("道具の一覧: 見つかった場所と、見つからない理由と対処", ReportsToolchain);
        harness.Add("パッケージ化: ABI の選択肢と表示名の往復・ABI の名前", ArchChoices);
        harness.Add("パッケージ化: APK の名前（{ゲーム名}-{ABI}-debug.apk）と置き場（出力フォルダ/ゲーム名/）", ApkNames);
        harness.Add("中核への指定: 実行ボタンは Run（端末・止めるまで logcat）、パッケージ化は Build（ABI・Release・端末なし）", EditorRequests);
    }

    /// <summary>道具の場所を偽の環境変数で作る。</summary>
    private static AndroidToolchain Toolchain(AndroidPipelineTests.TempDir temp, bool withAdb)
    {
        temp.WriteFile("sdk/platform-tools/.keep", "");
        if (withAdb) temp.WriteFile("sdk/platform-tools/adb.exe", "");
        temp.WriteFile("jdk/bin/java.exe", "");
        var env = new Dictionary<string, string?>
        {
            ["ANDROID_HOME"] = temp.Combine("sdk"),
            ["JAVA_HOME"] = temp.Combine("jdk"),
            ["PATH"] = string.Empty,
        };
        return AndroidToolchain.Detect(name => env.GetValueOrDefault(name));
    }

    /// <summary>使えるか。</summary>
    private static void JudgesAvailability()
    {
        using var temp = new AndroidPipelineTests.TempDir();
        temp.WriteFile("repo/runtime/Cargo.toml", "");
        temp.WriteFile("repo/runtime/android/gradlew.bat", "");
        var engine = AndroidEnginePaths.Locate(temp.Combine("repo/runtime/android/app"));
        Check.True(engine is not null, "リポジトリを上へ辿って見つける");
        var withAdb = Toolchain(temp, withAdb: true);

        var noProject = AndroidRunEnvironment.Judge(string.Empty, engine, withAdb);
        Check.True(!noProject.IsAvailable && noProject.Reason == AndroidRunEnvironment.NoProjectReason, "プロジェクトが無い");
        var noEngine = AndroidRunEnvironment.Judge(temp.Path, null, withAdb);
        Check.True(!noEngine.IsAvailable && noEngine.Reason == AndroidRunEnvironment.NoEngineReason, "リポジトリが無い");
        var ok = AndroidRunEnvironment.Judge(temp.Path, engine, withAdb);
        Check.True(ok.IsAvailable && ok.Reason is null, "使える");

        using var noAdbTemp = new AndroidPipelineTests.TempDir();
        var noAdb = AndroidRunEnvironment.Judge(temp.Path, engine, Toolchain(noAdbTemp, withAdb: false));
        Check.True(!noAdb.IsAvailable, "adb が無ければ使えない");
        Check.True(noAdb.Reason!.Contains("adb が見つかりません") && noAdb.Reason.Contains("Platform-Tools"), $"対処を示す: {noAdb.Reason}");

        var detected = AndroidRunEnvironment.Detect(temp.Path, withAdb, null, temp.Combine("repo/runtime"));
        Check.Equal(engine!.RepositoryRoot, detected.Engine?.RepositoryRoot, "探し始めの null は飛ばす");
        Check.True(detected.Availability.IsAvailable, "Detect も同じ判断");
    }

    /// <summary>道具の一覧。</summary>
    private static void ReportsToolchain()
    {
        using var temp = new AndroidPipelineTests.TempDir();
        var statuses = AndroidToolchainReport.Build(Toolchain(temp, withAdb: true));
        Check.Equal("Android SDK,Android NDK,adb,JDK,cargo,dotnet", string.Join(",", statuses.Select(s => s.Name)), "道具の並び");
        var sdk = statuses.Single(s => s.Name == "Android SDK");
        Check.True(sdk.Found && sdk.Detail == temp.Combine("sdk"), "SDK の場所");
        var ndk = statuses.Single(s => s.Name == "Android NDK");
        Check.True(!ndk.Found && ndk.Detail.Contains("ANDROID_NDK_HOME"), $"NDK が無い理由: {ndk.Detail}");
        Check.True(!AndroidToolchainReport.AllFound(statuses), "すべては見つかっていない");
        var text = AndroidToolchainReport.Describe(statuses);
        Check.True(text.Contains("見つかった    adb: ") && text.Contains("見つからない  Android NDK: "), $"複数行の説明:\n{text}");
        Check.Equal(statuses.Count, text.Split('\n').Length, "1 道具 1 行");
    }

    /// <summary>ABI の選択肢。</summary>
    private static void ArchChoices()
    {
        Check.Equal(3, AndroidApkOutput.ArchChoices.Count, "arm64-v8a・x86_64・両方");
        Check.Equal(AndroidArch.Arm64V8a, AndroidApkOutput.ArchChoices[0].Arch, "既定は arm64-v8a（配布用）");
        foreach (var choice in AndroidApkOutput.ArchChoices)
        {
            Check.Equal(choice.Arch, AndroidApkOutput.ArchFor(AndroidApkOutput.LabelFor(choice.Arch)), $"{choice.Arch}: 表示名の往復");
        }
        Check.Equal(AndroidArch.Arm64V8a, AndroidApkOutput.ArchFor("知らない表示名"), "知らない表示名は既定");
        Check.Equal("arm64-v8a", string.Join(",", AndroidApkOutput.AbisFor(AndroidArch.Arm64V8a)), "arm64");
        Check.Equal("x86_64", string.Join(",", AndroidApkOutput.AbisFor(AndroidArch.X86_64)), "x86_64");
        Check.Equal(AndroidAbis.Describe(AndroidAbis.Supported), string.Join(",", AndroidApkOutput.AbisFor(AndroidArch.Both)), "両方は中核の表の順");
        foreach (var abi in AndroidApkOutput.AbisFor(AndroidArch.Both))
        {
            Check.True(AndroidAbis.Find(abi) is not null, $"{abi} は中核が知っている ABI");
        }
    }

    /// <summary>APK の名前と置き場。</summary>
    private static void ApkNames()
    {
        Check.Equal("WarashibeFishing-arm64-v8a-debug.apk", AndroidApkOutput.FileName("WarashibeFishing", new[] { "arm64-v8a" }), "1 ABI");
        Check.Equal("Game-arm64-v8a+x86_64-debug.apk", AndroidApkOutput.FileName("Game", AndroidApkOutput.AbisFor(AndroidArch.Both)), "両方");
        var destination = AndroidApkOutput.DestinationPath(Path.Combine("D:", "out"), "Game", new[] { "x86_64" });
        Check.Equal(Path.Combine("D:", "out", "Game", "Game-x86_64-debug.apk"), destination, "出力フォルダ/ゲーム名/");
    }

    /// <summary>中核への指定。</summary>
    private static void EditorRequests()
    {
        var play = AndroidEditorRunRequests.ForPlay("D:/proj", Fixtures.PhoneSerial);
        Check.Equal(AndroidRunGoal.Run, play.Goal, "実行ボタンは Run");
        Check.Equal("D:/proj", play.ProjectDir, "プロジェクト（APK に pak とスクリプトを入れる）");
        Check.Equal(Fixtures.PhoneSerial, play.Serial, "選んだ端末");
        Check.True(play.Abis is null, "ABI は端末から決める");
        Check.Equal(0, play.LogcatSeconds, "logcat は止めるまで");
        Check.True(!play.Release && !play.Rebuild && !play.PushScripts && play.AssetsDir is null, "debug・自動で飛ばす・開発用の転送なし");

        var package = AndroidEditorRunRequests.ForPackage("D:/proj", new[] { "arm64-v8a" }, release: true);
        Check.Equal(AndroidRunGoal.Build, package.Goal, "パッケージ化は Build（端末を使わない）");
        Check.Equal("arm64-v8a", string.Join(",", package.Abis!), "ABI の指定");
        Check.True(package.Release, "Rust の最適化");
        Check.True(package.Serial is null, "端末なし");
    }
}
