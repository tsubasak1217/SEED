using SEEDEditor.Android;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Steps;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>
/// 段階C-4: push で置いた DLL の上書き（端末の内部 files/bin/）を、run（APK の内容を正とする）の起動の前に消す。
/// 消すかの判断（Run では消す・Push / run --push-scripts / 開発用の --assets-dir では消さない）、run-as の引数
/// （自分のアプリのデータフォルダの相対パスだけ）、端末の出力の読み方、Output の文言。
/// </summary>
public static class PushOverrideTests
{
    /// <summary>テストのアプリ ID。</summary>
    private const string AppId = "com.seedengine.runtime";

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("push の上書きの解除: Run（--project）では消し、Push・run --push-scripts・--assets-dir・プロジェクト無し・Build/Install では消さない", DecidesWhenToClear);
        harness.Add("push の上書きの解除: run-as の引数は自分のアプリのデータフォルダの相対パスだけ（絶対パス・.. は受け付けない）", BuildsRemoveArguments);
        harness.Add("push の上書きの解除: 端末の出力（removed＝消した・空＝無かった・それ以外＝失敗）と Output の文言", ParsesRemoveOutput);
    }

    /// <summary>消すかの判断。</summary>
    private static void DecidesWhenToClear()
    {
        using var temp = new TempDir();
        temp.WriteFile("Game/assets/project_settings.json", "{ \"start_scene\": \"assets://scenes/Main.scene\" }");
        var packaged = AndroidProjectResolver.Resolve(temp.Combine("Game"), null)!;
        var development = AndroidProjectResolver.Resolve(null, temp.Combine("Game/assets"))!;

        Check.True(LaunchStep.ClearsPushedScripts(new AndroidRunRequest { Goal = AndroidRunGoal.Run }, packaged),
            "run（--project）は APK の内容を正とするので消す");
        Check.True(!LaunchStep.ClearsPushedScripts(new AndroidRunRequest { Goal = AndroidRunGoal.Push }, packaged),
            "push はこれから files/bin/ に置くので消さない");
        Check.True(!LaunchStep.ClearsPushedScripts(new AndroidRunRequest { Goal = AndroidRunGoal.Run, PushScripts = true }, packaged),
            "run --push-scripts は起動の前に置いたものを使うので消さない");
        Check.True(!LaunchStep.ClearsPushedScripts(new AndroidRunRequest { Goal = AndroidRunGoal.Run }, development),
            "開発用の --assets-dir は APK に bin/ が無く files/bin/ が唯一の置き場なので消さない");
        Check.True(!LaunchStep.ClearsPushedScripts(new AndroidRunRequest { Goal = AndroidRunGoal.Run }, null), "プロジェクト無し");
        foreach (var goal in new[] { AndroidRunGoal.Build, AndroidRunGoal.Install })
        {
            Check.True(!LaunchStep.ClearsPushedScripts(new AndroidRunRequest { Goal = goal }, packaged), $"{goal} は起動しないので消さない");
        }
        Check.Equal("files/bin", AndroidRuntimeContract.RemoteScriptsDir, "消すのは push の置き場と同じ files/bin（端末の script_sources.rs と一致）");
    }

    /// <summary>run-as の引数。</summary>
    private static void BuildsRemoveArguments()
    {
        Check.Equal($"exec-out|run-as|{AppId}|sh|-c|if [ -e files/bin ]; then rm -rf files/bin && echo removed; fi",
            string.Join("|", AdbClient.RunAsRemoveArguments(AppId, AndroidRuntimeContract.RemoteScriptsDir)),
            "exec-out で run-as（アプリの権限・作業フォルダはそのアプリのデータフォルダ）。スクリプトは 1 引数のまま");

        foreach (var bad in new[] { "/data/local/tmp", "../other", "files/../../x", "", "  " })
        {
            try
            {
                AdbClient.RunAsRemoveArguments(AppId, bad);
                throw new AssertionException($"受け付けてしまった: '{bad}'");
            }
            catch (ArgumentException)
            {
                // アプリのデータフォルダの外を指す・空の指定は受け付けない
            }
        }
    }

    /// <summary>端末の出力の読み方と Output の文言。</summary>
    private static void ParsesRemoveOutput()
    {
        Check.Equal(true, AdbClient.ParseRunAsRemoveOutput("removed\r\n"), "removed は消した");
        Check.Equal(true, AdbClient.ParseRunAsRemoveOutput("run-as: warning\nremoved\n"), "端末の注意の行が混ざっても removed があれば消した（rm が成功したときだけ出る）");
        Check.Equal(false, AdbClient.ParseRunAsRemoveOutput(""), "空はもともと無かった");
        Check.Equal(false, AdbClient.ParseRunAsRemoveOutput("  \r\n"), "空白だけも無かった");
        Check.Equal(null, AdbClient.ParseRunAsRemoveOutput("run-as: package not debuggable: com.x"), "run-as のエラーは失敗");
        Check.Equal(null, AdbClient.ParseRunAsRemoveOutput("rm: files/bin/x.dll: Permission denied"), "rm のエラーは失敗");

        Check.True(LaunchStep.PushedScriptsClearedMessage.StartsWith("push した DLL の上書きを解除しました", StringComparison.Ordinal),
            $"Output の 1 行: {LaunchStep.PushedScriptsClearedMessage}");
        Check.True(LaunchStep.PushedScriptsClearedMessage.Contains("files/bin/") && LaunchStep.PushedScriptsClearedMessage.Contains("APK の bin/"),
            "何を消し、どれで起動するか");
    }
}
