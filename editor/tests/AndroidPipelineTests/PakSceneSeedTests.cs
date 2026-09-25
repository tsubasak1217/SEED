using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Steps;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Tools.SeedAndroid;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>
/// 段階C-4: シーンマネージャに未登録の起動シーンを APK の pak の収録の起点に足す。
/// 足すかの判断（Project/AndroidPakSceneSeeds）・登録シーンの読み取り・pak の指紋への反映・SeedPak の引数（--extra-scene）・
/// pak に入らなかったときの警告の文言、と SeedAndroid の --scene から判断までの経路。
/// </summary>
public static class PakSceneSeedTests
{
    /// <summary>テストのアセットルート（純粋な処理の判断に使う。ファイルは作らない）。</summary>
    private static readonly string AssetsRoot = Path.Combine(Path.GetTempPath(), "SeedC4Seeds", "Game", "assets");

    /// <summary>登録シーン（start_scene と scenes[].path。書き方の違う 3 つ）。</summary>
    private static readonly string[] Registered =
    {
        "assets://scenes/Main.scene",
        "scenes\\Second.scene",
        Path.Combine(AssetsRoot, "シーン", "森 の 2.scene"),
    };

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("pak の起点: 未登録は足し、登録済み（assets://・\\・絶対パス・大小文字違い）・無いシーン・指定なしは足さない", DecidesFromRegistration);
        harness.Add("pak の起点: project_settings.json の登録シーンを読み、開発用（--assets-dir）・プロジェクト無しは足さない", DecidesFromProject);
        harness.Add("pak の指紋: 足すシーンが変われば変わり、同じなら同じ・足さなければ従来と同じ（出力の同一性は変えない）", FingerprintIncludesExtraScenes);
        harness.Add("SeedPak の引数: --project … --out … --scripts に、足すシーンを --extra-scene で 1 つずつ（空白・日本語もそのまま）", BuildsSeedPakArguments);
        harness.Add("SeedAndroid の --scene（assets://・絶対パス）から、未登録なら足すシーンが決まる", CliSceneReachesDecision);
        harness.Add("起動の警告: pak に無いときの理由は 収録の失敗／--skip-gradle／push で分ける", SceneNotInPakMessages);
    }

    /// <summary>登録の有無と書き方の違い。</summary>
    private static void DecidesFromRegistration()
    {
        AndroidPakSceneSeedDecision D(string? scene, bool exists = true) =>
            AndroidPakSceneSeeds.Decide(scene, Registered, AssetsRoot, _ => exists);

        var unregistered = D("scenes/Third.scene");
        Check.Equal(AndroidPakSceneSeedStatus.AddedAsSeed, unregistered.Status, "未登録は足す");
        Check.Equal("scenes/Third.scene", string.Join("|", unregistered.ExtraScenes), "足すのはアセットルートからの相対パス 1 つ");
        Check.Equal("scenes/Third.scene", string.Join("|", D("assets://scenes/Third.scene").ExtraScenes), "assets:// の指定も相対パスにして足す");
        Check.Equal("scenes/Third.scene", string.Join("|", D(Path.Combine(AssetsRoot, "scenes", "Third.scene")).ExtraScenes),
            "アセットルート内の絶対パスも相対パスにして足す");

        Check.Equal(AndroidPakSceneSeedStatus.Registered, D("scenes/Main.scene").Status, "assets:// で登録したシーン");
        Check.Equal(AndroidPakSceneSeedStatus.Registered, D("SCENES/main.SCENE").Status, "大小文字の違いは同じシーン（収録の照合・端末の pak と同じ）");
        Check.Equal(AndroidPakSceneSeedStatus.Registered, D("scenes/Second.scene").Status, "\\ 区切りで登録したシーン");
        Check.Equal(AndroidPakSceneSeedStatus.Registered, D("シーン/森 の 2.scene").Status, "絶対パスで登録したシーン（日本語・空白）");
        Check.Equal(0, D("scenes/Main.scene").ExtraScenes.Count, "登録済みは足さない");

        Check.Equal(AndroidPakSceneSeedStatus.NotOnDisk, D("scenes/Third.scene", exists: false).Status, "アセットルートに無いシーンは足さない");
        Check.Equal(0, D("scenes/Third.scene", exists: false).ExtraScenes.Count, "無いシーンは足さない（端末が警告して開始シーン）");
        Check.Equal(AndroidPakSceneSeedStatus.NoLaunchScene, D(null).Status, "指定なし（開始シーン）");
        Check.Equal(AndroidPakSceneSeedStatus.NoLaunchScene, D("   ").Status, "空白だけも指定なし");
        Check.Equal(AndroidPakSceneSeedStatus.NoLaunchScene, D("../x.scene").Status, "受け付けない形（準備で弾き済み）は足さない");

        Check.True(!AndroidPakSceneSeeds.IsRegistered("scenes/Main.scene", Array.Empty<string>(), AssetsRoot), "登録が空なら未登録");
        Check.True(!AndroidPakSceneSeeds.IsRegistered("scenes/Main.scene", new[] { "scenes/Main.scene.bak" }, AssetsRoot), "前方一致だけでは登録済みにしない");
    }

    /// <summary>プロジェクトの設定から決める。</summary>
    private static void DecidesFromProject()
    {
        using var temp = new TempDir();
        temp.WriteFile("Game/assets/project_settings.json",
            "{ \"start_scene\": \"assets://scenes/Main.scene\", \"scenes\": [ { \"name\": \"Main\", \"path\": \"assets://scenes/Main.scene\" }," +
            " { \"name\": \"壊れた\", \"path\": 3 }, { \"name\": \"パスなし\" } ] }");
        temp.WriteFile("Game/assets/scenes/Main.scene", "{}");
        temp.WriteFile("Game/assets/scenes/Second.scene", "{}");

        var project = AndroidProjectResolver.Resolve(temp.Combine("Game"), null)!;
        Check.Equal("assets://scenes/Main.scene|assets://scenes/Main.scene", string.Join("|", project.Settings.RegisteredScenes),
            "start_scene と scenes[].path を書かれたまま・書かれた順に読む（文字列でない・無い path は読み飛ばす）");

        var second = AndroidPakSceneSeeds.Decide("scenes/Second.scene", project);
        Check.Equal(AndroidPakSceneSeedStatus.AddedAsSeed, second.Status, "未登録の Second は足す");
        Check.Equal("scenes/Second.scene", string.Join("|", second.ExtraScenes), "足すシーン");
        Check.Equal(AndroidPakSceneSeedStatus.Registered, AndroidPakSceneSeeds.Decide("scenes/Main.scene", project).Status, "登録済みの Main は足さない");
        Check.Equal(AndroidPakSceneSeedStatus.NotOnDisk, AndroidPakSceneSeeds.Decide("scenes/None.scene", project).Status, "ディスクに無いシーン");
        Check.Equal(AndroidPakSceneSeedStatus.NoLaunchScene, AndroidPakSceneSeeds.Decide(null, project).Status, "開始シーン");

        var development = AndroidProjectResolver.Resolve(null, temp.Combine("Game/assets"))!;
        Check.Equal(AndroidPakSceneSeedStatus.NotPackaged, AndroidPakSceneSeeds.Decide("scenes/Second.scene", development).Status,
            "開発用（--assets-dir）の APK は pak を入れないので足さない");
        Check.Equal(AndroidPakSceneSeedStatus.NotPackaged, AndroidPakSceneSeeds.Decide("scenes/Second.scene", null).Status, "プロジェクト無し");

        temp.WriteFile("Bare/assets/project_settings.json", "{ \"game_name\": \"x\" }");
        var bare = AndroidProjectResolver.Resolve(temp.Combine("Bare"), null)!;
        Check.Equal(0, bare.Settings.RegisteredScenes.Count, "登録が無い設定は空");
    }

    /// <summary>pak の指紋。</summary>
    private static void FingerprintIncludesExtraScenes()
    {
        using var temp = new TempDir();
        temp.WriteFile("Game/assets/project_settings.json", "{ \"start_scene\": \"assets://scenes/Main.scene\" }");
        temp.WriteFile("Game/assets/scenes/Main.scene", "{}");
        temp.WriteFile("Game/assets/scenes/Second.scene", "{}");
        temp.WriteFile("Game/assets/scenes/Third.scene", "{}");
        var engine = new AndroidEnginePaths(temp.Combine("repo"));
        var project = AndroidProjectResolver.Resolve(temp.Combine("Game"), null)!;

        var none = AndroidStepFingerprints.PackageContent(engine, project);
        var empty = AndroidStepFingerprints.PackageContent(engine, project, Array.Empty<string>());
        var second = AndroidStepFingerprints.PackageContent(engine, project, new[] { "scenes/Second.scene" });
        var secondAgain = AndroidStepFingerprints.PackageContent(engine, project, new[] { "scenes/Second.scene" });
        var third = AndroidStepFingerprints.PackageContent(engine, project, new[] { "scenes/Third.scene" });

        Check.Equal(none.Inputs, empty.Inputs, "足さないときは従来の式のまま（空を渡しても同じ）");
        Check.True(second.Inputs != none.Inputs, "未登録のシーンへ切り替えると pak を作り直す（指紋が変わる）");
        Check.Equal(second.Inputs, secondAgain.Inputs, "同じシーンのままなら同じ指紋（2 回目は飛ばす）");
        Check.True(third.Inputs != second.Inputs, "別の未登録のシーンへ切り替えても作り直す");
        Check.Equal(none.Output, second.Output, "出力の同一性（置き場の中身）は足すシーンに依らない");

        var development = AndroidProjectResolver.Resolve(null, temp.Combine("Game/assets"))!;
        Check.Equal(AndroidStepFingerprints.PackageContent(engine, development).Inputs,
            AndroidStepFingerprints.PackageContent(engine, development, new[] { "scenes/Second.scene" }).Inputs,
            "pak を入れない開発用の APK では足すシーンを材料にしない（置き場を空にするだけ）");
    }

    /// <summary>SeedPak の引数。</summary>
    private static void BuildsSeedPakArguments()
    {
        const string project = @"D:\Game Project";
        const string output = @"C:\repo\runtime\android\app\src\main\assets\seed";
        Check.Equal($"--project|{project}|--out|{output}|--scripts",
            string.Join("|", PackageContentStep.SeedPakArguments(project, output, Array.Empty<string>())),
            "足すシーンが無ければ従来どおり");
        Check.Equal($"--project|{project}|--out|{output}|--scripts|--extra-scene|scenes/日本語 シーン.scene|--extra-scene|scenes/B.scene",
            string.Join("|", PackageContentStep.SeedPakArguments(project, output, new[] { "scenes/日本語 シーン.scene", "scenes/B.scene" })),
            "--extra-scene と値を 1 引数ずつ（空白・日本語を含む値も 1 引数のまま。引用は子プロセスの起動が行う）");
        Check.Equal("--extra-scene", SeedPakProcess.ExtraSceneOption, "SeedPak の引数名（editor/tools/SeedPak/SeedPakArguments.cs と一致）");
    }

    /// <summary>SeedAndroid の --scene から判断まで。</summary>
    private static void CliSceneReachesDecision()
    {
        using var temp = new TempDir();
        temp.WriteFile("Game/assets/project_settings.json",
            "{ \"start_scene\": \"assets://scenes/Main.scene\", \"scenes\": [ { \"path\": \"assets://scenes/Main.scene\" } ] }");
        temp.WriteFile("Game/assets/scenes/Main.scene", "{}");
        temp.WriteFile("Game/assets/scenes/Second.scene", "{}");
        var project = AndroidProjectResolver.Resolve(temp.Combine("Game"), null)!;

        foreach (var spec in new[] { "assets://scenes/Second.scene", temp.Combine("Game/assets/scenes/Second.scene"), "scenes\\Second.scene" })
        {
            var parsed = SeedAndroidArguments.Parse(new[] { "run", "--project", temp.Combine("Game"), "--scene", spec });
            Check.True(parsed.Error is null, $"解釈できる: {parsed.Error}");
            var request = SeedAndroidArguments.ToRequest(parsed.CommandLine!, null);
            var decision = AndroidPakSceneSeeds.Decide(request.ScenePath, project);
            Check.Equal("scenes/Second.scene", string.Join("|", decision.ExtraScenes), $"--scene {spec} → 足すシーン");
        }

        var registered = SeedAndroidArguments.ToRequest(
            SeedAndroidArguments.Parse(new[] { "run", "--project", temp.Combine("Game"), "--scene", "scenes/Main.scene" }).CommandLine!, null);
        Check.Equal(AndroidPakSceneSeedStatus.Registered, AndroidPakSceneSeeds.Decide(registered.ScenePath, project).Status, "登録済みは足さない");
    }

    /// <summary>pak に無いときの警告。</summary>
    private static void SceneNotInPakMessages()
    {
        const string scene = "scenes/Second.scene";
        var run = LaunchStep.SceneNotInPakMessage(scene, new AndroidRunRequest { Goal = AndroidRunGoal.Run });
        Check.True(run.StartsWith($"シーン {scene} が APK の pak に入っていません（", StringComparison.Ordinal), $"書き出し: {run}");
        Check.True(run.Contains("収録に失敗しました") && run.Contains("--extra-scene"), $"通常は収録の失敗（起点には入れている）: {run}");
        Check.True(run.EndsWith("端末は開始シーンで起動します。", StringComparison.Ordinal), $"端末の振る舞い: {run}");
        Check.True(!run.Contains("シーンマネージャに登録してから"), "登録を促す C-3 の文言は使わない（未登録でも足すため）");

        var skipGradle = LaunchStep.SceneNotInPakMessage(scene, new AndroidRunRequest { Goal = AndroidRunGoal.Run, SkipGradle = true });
        Check.True(skipGradle.Contains("--skip-gradle") && skipGradle.Contains("前回の pak"), $"APK の作成を飛ばした: {skipGradle}");

        var push = LaunchStep.SceneNotInPakMessage(scene, new AndroidRunRequest { Goal = AndroidRunGoal.Push });
        Check.True(push.Contains("push") && push.Contains("作り直しません"), $"push は pak を作り直さない: {push}");
    }
}
