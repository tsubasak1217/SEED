using System.Collections.Generic;
using System.Linq;
using SEEDEditor.Android;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>実行計画（どの工程を行い、どれを飛ばすか）。</summary>
public static class PlanTests
{
    /// <summary>今回のアプリ ID。</summary>
    private const string AppId = "com.seedengine.runtime";

    /// <summary>今の APK の SHA-256（テスト用の値）。</summary>
    private const string ApkSha = "aaaa";

    /// <summary>端末に入っている APK の場所（テスト用の値）。</summary>
    private const string InstalledPath = "/data/app/~~x==/com.seedengine.runtime-y==/base.apk";

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("計画: 記録が無ければビルドの全工程とインストールを行う", FirstRunRunsEverything);
        harness.Add("計画: 入力も出力も記録と同じならビルドを飛ばし、同じ APK が入っていればインストールも飛ばす", UnchangedSkipsEverything);
        harness.Add("計画: .so の入力が変わった ABI だけを作り、Gradle とインストールを行う", NativeChangeRebuildsOnlyThatAbi);
        harness.Add("計画: 出力が記録と違えば（外で作り直された）作り直す", ExternallyChangedOutputRebuilds);
        harness.Add("計画: 出力が無ければ作る", MissingOutputRebuilds);
        harness.Add("計画: 明示の Skip / No は自動判定より強い（無い .so は警告）", ExplicitSkipsWin);
        harness.Add("計画: Rebuild は自動で飛ばさない（インストールも入れ直す）", RebuildRunsEverything);
        harness.Add("計画: インストールの判断（未インストール・別の APK・入れ直され・別のアプリ ID）", InstallDecisions);
        harness.Add("計画: Build では端末の工程が無い", BuildGoalHasNoDeviceSteps);
        harness.Add("計画: Push はスクリプト（とアセット）の転送・起動・logcat だけ", PushGoalSteps);
        harness.Add("計画: NoLaunch / NoLogcat は飛ばす判断として載る", NoLaunchNoLogcat);
        harness.Add("計画: APK を作り直さないときの警告（pak が残っている・ABI が合わない・APK が無い）", ReusedApkWarnings);
    }

    /// <summary>今と記録が同じ材料（x86_64 だけ）を作る。</summary>
    private static AndroidPlanInput UnchangedInput(AndroidRunRequest request)
    {
        var current = new Dictionary<string, AndroidStepFingerprint>
        {
            [AndroidStepKeys.Native(AndroidAbis.X86_64)] = new("n-in", "n-out"),
            [AndroidStepKeys.PackageContent] = new("p-in", "p-out"),
            [AndroidStepKeys.DotnetBundle] = new("d-in", "d-out"),
            [AndroidStepKeys.Gradle] = new("g-in", "g-out"),
        };
        return new AndroidPlanInput
        {
            Request = request,
            Abis = new[] { AndroidAbis.X86_64 },
            Current = current,
            Recorded = new Dictionary<string, AndroidStepFingerprint>(current),
            Install = new AndroidInstallFacts(AppId, ApkSha, InstalledPath, new AndroidInstallRecord(AppId, ApkSha, InstalledPath)),
            RecordedApkAbis = new[] { "x86_64" },
        };
    }

    /// <summary>判断を引く（無ければ失敗）。</summary>
    private static AndroidStepDecision Decision(AndroidBuildPlan plan, AndroidPipelinePhase phase) =>
        plan.Find(phase) ?? throw new AssertionException($"{phase} が計画に無い");

    /// <summary>記録が無い初回。</summary>
    private static void FirstRunRunsEverything()
    {
        var input = UnchangedInput(new AndroidRunRequest()) with
        {
            Recorded = new Dictionary<string, AndroidStepFingerprint>(),
            Install = new AndroidInstallFacts(AppId, null, null, null),
        };
        var plan = AndroidBuildPlan.Create(input);
        foreach (var phase in new[] { AndroidPipelinePhase.NativeBuild, AndroidPipelinePhase.PackageContent, AndroidPipelinePhase.DotnetBundle, AndroidPipelinePhase.Gradle, AndroidPipelinePhase.Install, AndroidPipelinePhase.Launch, AndroidPipelinePhase.Logcat })
        {
            Check.True(Decision(plan, phase).Runs, $"{phase} を行う");
        }
        Check.True(Decision(plan, AndroidPipelinePhase.NativeBuild).Reason.Contains("記録が無い"), "理由");
        Check.True(plan.NeedsDevice, "端末が要る");
    }

    /// <summary>何も変わっていない 2 回目。</summary>
    private static void UnchangedSkipsEverything()
    {
        var plan = AndroidBuildPlan.Create(UnchangedInput(new AndroidRunRequest()));
        foreach (var phase in new[] { AndroidPipelinePhase.NativeBuild, AndroidPipelinePhase.PackageContent, AndroidPipelinePhase.DotnetBundle, AndroidPipelinePhase.Gradle, AndroidPipelinePhase.Install })
        {
            var decision = Decision(plan, phase);
            Check.True(!decision.Runs, $"{phase} を飛ばす（{decision.Reason}）");
        }
        Check.True(Decision(plan, AndroidPipelinePhase.Install).Reason.Contains("同じ APK"), "インストールを飛ばす理由");
        Check.True(Decision(plan, AndroidPipelinePhase.Launch).Runs, "起動はいつも行う");
        Check.Equal(0, plan.Warnings.Count, "警告なし");
    }

    /// <summary>1 つの ABI の入力だけが変わった。</summary>
    private static void NativeChangeRebuildsOnlyThatAbi()
    {
        var baseInput = UnchangedInput(new AndroidRunRequest());
        var current = new Dictionary<string, AndroidStepFingerprint>(baseInput.Current)
        {
            [AndroidStepKeys.Native(AndroidAbis.Arm64)] = new("a-in-new", "a-out"),
        };
        var recorded = new Dictionary<string, AndroidStepFingerprint>(baseInput.Recorded)
        {
            [AndroidStepKeys.Native(AndroidAbis.Arm64)] = new("a-in-old", "a-out"),
        };
        var plan = AndroidBuildPlan.Create(baseInput with
        {
            Abis = new[] { AndroidAbis.Arm64, AndroidAbis.X86_64 }, Current = current, Recorded = recorded,
        });
        var native = Decision(plan, AndroidPipelinePhase.NativeBuild);
        Check.True(native.Runs, "作る");
        Check.Equal("arm64-v8a", AndroidAbis.Describe(native.Abis), "変わった ABI だけ");
        Check.True(native.Reason.Contains("入力が変わった"), $"理由: {native.Reason}");
        Check.True(!Decision(plan, AndroidPipelinePhase.PackageContent).Runs, "pak は飛ばす");
        Check.True(Decision(plan, AndroidPipelinePhase.Gradle).Runs, "上流を作り直すので Gradle を行う");
        Check.True(Decision(plan, AndroidPipelinePhase.Install).Runs, "APK を作り直すので入れ直す");
    }

    /// <summary>出力が外で変わった。</summary>
    private static void ExternallyChangedOutputRebuilds()
    {
        var baseInput = UnchangedInput(new AndroidRunRequest());
        var current = new Dictionary<string, AndroidStepFingerprint>(baseInput.Current)
        {
            [AndroidStepKeys.PackageContent] = new("p-in", "p-out-touched"),
        };
        var plan = AndroidBuildPlan.Create(baseInput with { Current = current });
        var package = Decision(plan, AndroidPipelinePhase.PackageContent);
        Check.True(package.Runs, "作り直す");
        Check.True(package.Reason.Contains("外で"), $"理由: {package.Reason}");
    }

    /// <summary>出力が無い。</summary>
    private static void MissingOutputRebuilds()
    {
        var baseInput = UnchangedInput(new AndroidRunRequest());
        var current = new Dictionary<string, AndroidStepFingerprint>(baseInput.Current)
        {
            [AndroidStepKeys.Gradle] = new("g-in", AndroidFingerprintBuilder.MissingMarker),
        };
        var plan = AndroidBuildPlan.Create(baseInput with { Current = current });
        Check.True(Decision(plan, AndroidPipelinePhase.Gradle).Runs, "APK が無ければ作る");
        Check.True(Decision(plan, AndroidPipelinePhase.Install).Runs, "作り直すので入れる");
    }

    /// <summary>明示の Skip / No。</summary>
    private static void ExplicitSkipsWin()
    {
        var request = new AndroidRunRequest { SkipNativeBuild = true, SkipGradle = true, NoInstall = true };
        var baseInput = UnchangedInput(request);
        var current = new Dictionary<string, AndroidStepFingerprint>(baseInput.Current)
        {
            [AndroidStepKeys.Native(AndroidAbis.X86_64)] = new("changed", AndroidFingerprintBuilder.MissingMarker),
            [AndroidStepKeys.PackageContent] = new("changed", "p-out"),
        };
        var plan = AndroidBuildPlan.Create(baseInput with { Current = current });
        foreach (var phase in new[] { AndroidPipelinePhase.NativeBuild, AndroidPipelinePhase.PackageContent, AndroidPipelinePhase.DotnetBundle, AndroidPipelinePhase.Gradle, AndroidPipelinePhase.Install })
        {
            var decision = Decision(plan, phase);
            Check.True(!decision.Runs, $"{phase} は指定で飛ばす");
            Check.True(decision.Reason.Contains("指定"), $"{phase} の理由: {decision.Reason}");
        }
        Check.True(plan.Warnings.Any(w => w.Contains("libSEED.so")), "無い .so を警告する");
    }

    /// <summary>Rebuild。</summary>
    private static void RebuildRunsEverything()
    {
        var plan = AndroidBuildPlan.Create(UnchangedInput(new AndroidRunRequest { Rebuild = true }));
        foreach (var phase in new[] { AndroidPipelinePhase.NativeBuild, AndroidPipelinePhase.PackageContent, AndroidPipelinePhase.DotnetBundle, AndroidPipelinePhase.Gradle, AndroidPipelinePhase.Install })
        {
            Check.True(Decision(plan, phase).Runs, $"{phase} を行う");
        }
        // 明示の Skip は Rebuild より強い
        var skip = AndroidBuildPlan.Create(UnchangedInput(new AndroidRunRequest { Rebuild = true, SkipNativeBuild = true }));
        Check.True(!Decision(skip, AndroidPipelinePhase.NativeBuild).Runs, "Rebuild でも --skip-rust は飛ばす");
    }

    /// <summary>インストールの判断。</summary>
    private static void InstallDecisions()
    {
        var baseInput = UnchangedInput(new AndroidRunRequest());
        (AndroidInstallFacts Facts, string Expect)[] cases =
        {
            (new AndroidInstallFacts(AppId, ApkSha, null, new AndroidInstallRecord(AppId, ApkSha, InstalledPath)), "入っていない"),
            (new AndroidInstallFacts(AppId, "bbbb", InstalledPath, new AndroidInstallRecord(AppId, ApkSha, InstalledPath)), "APK と違う"),
            (new AndroidInstallFacts(AppId, ApkSha, "/data/app/~~other==/base.apk", new AndroidInstallRecord(AppId, ApkSha, InstalledPath)), "入れ直されている"),
            (new AndroidInstallFacts(AppId, ApkSha, InstalledPath, new AndroidInstallRecord("com.example.other", ApkSha, InstalledPath)), "APK と違う"),
            (new AndroidInstallFacts(AppId, ApkSha, InstalledPath, null), "記録が無い"),
            (new AndroidInstallFacts(AppId, null, InstalledPath, new AndroidInstallRecord(AppId, ApkSha, InstalledPath)), "APK がありません"),
        };
        foreach (var (facts, expect) in cases)
        {
            var install = Decision(AndroidBuildPlan.Create(baseInput with { Install = facts }), AndroidPipelinePhase.Install);
            Check.True(install.Runs, $"入れる（{expect}）");
            Check.True(install.Reason.Contains(expect), $"理由 {install.Reason} に「{expect}」");
        }
    }

    /// <summary>Build の範囲。</summary>
    private static void BuildGoalHasNoDeviceSteps()
    {
        var plan = AndroidBuildPlan.Create(UnchangedInput(new AndroidRunRequest { Goal = AndroidRunGoal.Build, AssetsDir = "x", PushScripts = true }));
        Check.Equal(4, plan.Steps.Count, "ビルドの 4 工程だけ");
        Check.True(plan.Steps.All(step => !AndroidBuildPlan.IsDevicePhase(step.Phase)), "端末の工程は無い");
        Check.True(!plan.NeedsDevice, "端末は要らない");
    }

    /// <summary>Push の範囲。</summary>
    private static void PushGoalSteps()
    {
        var plan = AndroidBuildPlan.Create(UnchangedInput(new AndroidRunRequest { Goal = AndroidRunGoal.Push, AssetsDir = "assets" }));
        var phases = plan.Steps.Select(step => step.Phase).ToList();
        Check.True(phases.SequenceEqual(new[]
        {
            AndroidPipelinePhase.PushAssets, AndroidPipelinePhase.PushScripts, AndroidPipelinePhase.Launch, AndroidPipelinePhase.Logcat,
        }), $"工程: {string.Join(", ", phases)}");
        Check.True(plan.Steps.All(step => step.Runs), "すべて行う");
    }

    /// <summary>起動・logcat を飛ばす指定。</summary>
    private static void NoLaunchNoLogcat()
    {
        var plan = AndroidBuildPlan.Create(UnchangedInput(new AndroidRunRequest { NoLaunch = true, NoLogcat = true, NoInstall = true }));
        Check.True(!Decision(plan, AndroidPipelinePhase.Launch).Runs, "起動を飛ばす");
        Check.True(!Decision(plan, AndroidPipelinePhase.Logcat).Runs, "logcat を飛ばす");
        Check.True(!plan.NeedsDevice, "端末の工程をどれも行わないなら端末は要らない");
        var withSeconds = AndroidBuildPlan.Create(UnchangedInput(new AndroidRunRequest { LogcatSeconds = 20 }));
        Check.True(Decision(withSeconds, AndroidPipelinePhase.Logcat).Reason.Contains("20 秒"), "秒数を理由に出す");
    }

    /// <summary>APK を作り直さないときの警告。</summary>
    private static void ReusedApkWarnings()
    {
        var request = new AndroidRunRequest { SkipGradle = true, AssetsDir = "assets" };
        var input = UnchangedInput(request) with
        {
            Abis = new[] { AndroidAbis.Arm64 },
            RecordedApkAbis = new[] { "x86_64" },
            StagedPakPresent = true,
            Install = new AndroidInstallFacts(AppId, null, null, null),
        };
        var warnings = AndroidBuildPlan.Create(input).Warnings;
        Check.True(warnings.Any(w => w.Contains("assets.pak")), "pak が残っている");
        Check.True(warnings.Any(w => w.Contains("arm64-v8a")), "ABI が合わない");
        Check.True(warnings.Any(w => w.Contains("APK がありません")), "APK が無い");

        // 入れないなら APK の ABI・有無は関係ない（警告しない）
        var notInstalling = AndroidBuildPlan.Create(input with { Request = request with { NoInstall = true } }).Warnings;
        Check.True(!notInstalling.Any(w => w.Contains("arm64-v8a") || w.Contains("APK がありません")), $"入れないなら警告しない: {string.Join(" / ", notInstalling)}");
    }
}
