// ============================================================
//  AndroidBuildPlan.cs — 「どの工程を行い、どれを飛ばすか」を決める（純粋な処理）
//
//  【判断の材料（入出力はすべて呼び出し側が先に集める。ここはファイルも端末も触らない）】
//    - 指定（目的・明示の Skip* / No*・Rebuild・開発用の転送）
//    - 各ビルド工程の「今の入力の指紋・今の出力の同一性」と「前回その出力を作ったときの記録」
//    - インストール: 今の APK の SHA-256・端末に入っている APK の場所（pm path）・前回自分が入れたときの記録
//
//  【決まり】
//    1. 目的の範囲外の工程は計画に載せない（Build では端末の工程が無い 等）
//    2. 明示の Skip* / No* は常に飛ばす（従来の build_and_run.ps1 の -SkipRustBuild 等と同じ）
//    3. Rebuild なら自動では飛ばさない
//    4. ビルド工程は「記録が無い・入力が違う・出力が無い・出力が記録と違う（外で作り直された）」なら行う。同じなら飛ばす
//       libSEED.so は ABI ごとに見て、古い ABI だけを作る
//    5. Gradle は上流（.so・pak・同梱 .NET）のどれかを作り直すなら必ず行う
//    6. インストールは APK を作り直すなら行う。作り直さないなら「前回自分が入れた APK（SHA-256）が、
//       そのときの場所のまま端末に入っている」ときだけ飛ばす（場所はインストールのたびに変わるので、
//       他の人・他のプロジェクトが入れ直していれば分かる）
//    7. 開発用の転送・起動・logcat は指定どおり（変更の有無では飛ばさない）
//
//  WPF に依存しない（単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using SEEDEditor.Android.Pipeline;

namespace SEEDEditor.Android.Plan;

/// <summary>工程の記録・比較のキー。</summary>
public static class AndroidStepKeys
{
    /// <summary>pak とスクリプト（SeedPak）。</summary>
    public const string PackageContent = "package_content";

    /// <summary>同梱 .NET。</summary>
    public const string DotnetBundle = "dotnet_bundle";

    /// <summary>APK（Gradle）。</summary>
    public const string Gradle = "gradle";

    /// <summary>libSEED.so（ABI ごと）。</summary>
    /// <param name="abi">ABI。</param>
    /// <returns>キー。</returns>
    public static string Native(AndroidAbi abi) => $"native_build/{abi.Name}";
}

/// <summary>工程の入力の指紋と出力の同一性（今の値、または前回の記録）。</summary>
/// <param name="Inputs">入力の指紋。</param>
/// <param name="Output">出力の同一性（無ければ AndroidFingerprintBuilder.MissingMarker / 空のフォルダは AndroidOutputIdentity.Empty）。</param>
public sealed record AndroidStepFingerprint(
    [property: JsonPropertyName("inputs")] string Inputs,
    [property: JsonPropertyName("output")] string Output);

/// <summary>前回自分が端末へ入れた APK の記録。</summary>
/// <param name="ApplicationId">アプリ ID。</param>
/// <param name="ApkSha256">入れた APK の SHA-256。</param>
/// <param name="InstalledPath">入れた直後の pm path（base.apk の場所）。</param>
public sealed record AndroidInstallRecord(string ApplicationId, string ApkSha256, string InstalledPath);

/// <summary>インストールの判断の材料。</summary>
/// <param name="ApplicationId">今回のアプリ ID。</param>
/// <param name="LocalApkSha256">今の APK の SHA-256（APK が無ければ null）。</param>
/// <param name="DeviceInstalledPath">端末に入っている APK の場所（入っていなければ null）。</param>
/// <param name="Recorded">前回自分がこの端末へ入れたときの記録（無ければ null）。</param>
public sealed record AndroidInstallFacts(
    string ApplicationId, string? LocalApkSha256, string? DeviceInstalledPath, AndroidInstallRecord? Recorded);

/// <summary>計画の材料。</summary>
public sealed record AndroidPlanInput
{
    /// <summary>指定。</summary>
    public required AndroidRunRequest Request { get; init; }

    /// <summary>今回の ABI。</summary>
    public required IReadOnlyList<AndroidAbi> Abis { get; init; }

    /// <summary>ビルド工程の今の指紋（キーは <see cref="AndroidStepKeys"/>）。</summary>
    public IReadOnlyDictionary<string, AndroidStepFingerprint> Current { get; init; } = new Dictionary<string, AndroidStepFingerprint>();

    /// <summary>ビルド工程の前回の記録（キーは <see cref="AndroidStepKeys"/>）。</summary>
    public IReadOnlyDictionary<string, AndroidStepFingerprint> Recorded { get; init; } = new Dictionary<string, AndroidStepFingerprint>();

    /// <summary>インストールの判断の材料（端末の工程が無いときは null）。</summary>
    public AndroidInstallFacts? Install { get; init; }

    /// <summary>前回作った APK に詰めた ABI（記録が無ければ null）。</summary>
    public IReadOnlyList<string>? RecordedApkAbis { get; init; }

    /// <summary>APK に入れる配布物の置き場に pak が残っているか（-SkipGradle と開発用の転送の組み合わせの警告用）。</summary>
    public bool StagedPakPresent { get; init; }
}

/// <summary>工程を行うか飛ばすか。</summary>
public enum AndroidStepAction
{
    /// <summary>行う。</summary>
    Run,

    /// <summary>飛ばす。</summary>
    Skip,
}

/// <summary>1 つの工程の判断。</summary>
/// <param name="Phase">工程。</param>
/// <param name="Action">行うか飛ばすか。</param>
/// <param name="Reason">理由（ログ・エディタの表示用）。</param>
/// <param name="Abis">対象の ABI（libSEED.so のビルドでは作り直す ABI だけ。他の工程は今回の ABI 全部）。</param>
public sealed record AndroidStepDecision(
    AndroidPipelinePhase Phase, AndroidStepAction Action, string Reason, IReadOnlyList<AndroidAbi> Abis)
{
    /// <summary>行うか。</summary>
    public bool Runs => Action == AndroidStepAction.Run;
}

/// <summary>実行計画（工程の順に並んだ判断と、先に知らせる警告）。</summary>
/// <param name="Steps">工程の判断（実行の順）。</param>
/// <param name="Warnings">警告。</param>
public sealed record AndroidBuildPlan(IReadOnlyList<AndroidStepDecision> Steps, IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// 入力も出力も前回の記録と同じで飛ばすときの理由（エディタの Output パネルはこの理由の工程を「変更なし」の 1 行で出す）。
    /// </summary>
    public const string UnchangedReason = "変更なし";

    /// <summary>その工程の判断（計画に無ければ null）。</summary>
    /// <param name="phase">工程。</param>
    /// <returns>判断。</returns>
    public AndroidStepDecision? Find(AndroidPipelinePhase phase) => Steps.FirstOrDefault(step => step.Phase == phase);

    /// <summary>その工程を行うか（計画に無ければ false）。</summary>
    /// <param name="phase">工程。</param>
    /// <returns>行うなら true。</returns>
    public bool Runs(AndroidPipelinePhase phase) => Find(phase)?.Runs ?? false;

    /// <summary>端末が要るか（端末の工程を 1 つでも行う）。</summary>
    public bool NeedsDevice => Steps.Any(step => step.Runs && IsDevicePhase(step.Phase));

    /// <summary>端末に触る工程か。</summary>
    /// <param name="phase">工程。</param>
    /// <returns>端末の工程なら true。</returns>
    public static bool IsDevicePhase(AndroidPipelinePhase phase) => phase is
        AndroidPipelinePhase.Install or AndroidPipelinePhase.PushAssets or AndroidPipelinePhase.PushScripts or
        AndroidPipelinePhase.Launch or AndroidPipelinePhase.Logcat;

    /// <summary>
    /// 計画を立てる。
    /// </summary>
    /// <param name="input">計画の材料。</param>
    /// <returns>実行計画。</returns>
    public static AndroidBuildPlan Create(AndroidPlanInput input)
    {
        var request = input.Request;
        var goal = request.Goal;
        var steps = new List<AndroidStepDecision>();
        var warnings = new List<string>();
        var buildScope = goal is AndroidRunGoal.Build or AndroidRunGoal.Install or AndroidRunGoal.Run;
        var installScope = goal is AndroidRunGoal.Install or AndroidRunGoal.Run;
        var launchScope = goal is AndroidRunGoal.Run or AndroidRunGoal.Push;

        // ── ビルド工程 ──
        if (buildScope)
        {
            var native = DecideNative(input, warnings);
            var package = request.SkipGradle
                ? Skip(AndroidPipelinePhase.PackageContent, "指定で飛ばします（APK を作り直さない）", input.Abis)
                : Compare(AndroidPipelinePhase.PackageContent, AndroidStepKeys.PackageContent, input);
            var dotnet = request.SkipGradle
                ? Skip(AndroidPipelinePhase.DotnetBundle, "指定で飛ばします（APK を作り直さない）", input.Abis)
                : Compare(AndroidPipelinePhase.DotnetBundle, AndroidStepKeys.DotnetBundle, input);
            AndroidStepDecision gradle;
            if (request.SkipGradle)
            {
                gradle = Skip(AndroidPipelinePhase.Gradle, "指定で飛ばします（前回の APK をそのまま使う）", input.Abis);
                WarnAboutReusedApk(input, installScope, warnings);
            }
            else if (native.Runs || package.Runs || dotnet.Runs)
            {
                gradle = Run(AndroidPipelinePhase.Gradle, "上流の工程（.so・pak・同梱 .NET）を作り直すため", input.Abis);
            }
            else
            {
                gradle = Compare(AndroidPipelinePhase.Gradle, AndroidStepKeys.Gradle, input);
            }
            steps.AddRange(new[] { native, package, dotnet, gradle });

            if (installScope) steps.Add(DecideInstall(input, gradle));
        }

        // ── 開発用の転送・起動・logcat（指定どおり）──
        var pushScope = goal is AndroidRunGoal.Install or AndroidRunGoal.Run or AndroidRunGoal.Push;
        if (pushScope && !string.IsNullOrWhiteSpace(request.AssetsDir))
        {
            steps.Add(Run(AndroidPipelinePhase.PushAssets, "開発用のアセットの転送を指定", input.Abis));
        }
        if (goal == AndroidRunGoal.Push || (pushScope && request.PushScripts))
        {
            steps.Add(Run(AndroidPipelinePhase.PushScripts, "スクリプトの DLL の転送を指定", input.Abis));
        }
        if (launchScope)
        {
            steps.Add(request.NoLaunch
                ? Skip(AndroidPipelinePhase.Launch, "指定で飛ばします", input.Abis)
                : Run(AndroidPipelinePhase.Launch, "止めてから起動し直す", input.Abis));
            steps.Add(request.NoLogcat
                ? Skip(AndroidPipelinePhase.Logcat, "指定で飛ばします", input.Abis)
                : Run(AndroidPipelinePhase.Logcat, request.LogcatSeconds > 0 ? $"{request.LogcatSeconds} 秒流す" : "止めるまで流す", input.Abis));
        }

        return new AndroidBuildPlan(steps, warnings);
    }

    /// <summary>libSEED.so のビルドの判断（ABI ごとに古いものだけを作る）。</summary>
    private static AndroidStepDecision DecideNative(AndroidPlanInput input, List<string> warnings)
    {
        if (input.Request.SkipNativeBuild)
        {
            foreach (var abi in input.Abis)
            {
                if (input.Current.TryGetValue(AndroidStepKeys.Native(abi), out var current)
                    && current.Output == AndroidFingerprintBuilder.MissingMarker)
                {
                    warnings.Add($"{abi.Name} の {AndroidRuntimeContract.NativeLibraryFileName} がありません（libSEED.so のビルドを飛ばす指定のため作りません）。APK にエンジンが入りません。");
                }
            }
            return Skip(AndroidPipelinePhase.NativeBuild, "指定で飛ばします", input.Abis);
        }

        var stale = new List<AndroidAbi>();
        string? firstReason = null;
        foreach (var abi in input.Abis)
        {
            var reason = StaleReason(AndroidStepKeys.Native(abi), input);
            if (reason is null) continue;
            stale.Add(abi);
            firstReason ??= $"{abi.Name}: {reason}";
        }
        return stale.Count == 0
            ? Skip(AndroidPipelinePhase.NativeBuild, UnchangedReason, input.Abis)
            : Run(AndroidPipelinePhase.NativeBuild, firstReason!, stale);
    }

    /// <summary>記録と今を比べて、行うか飛ばすかを決める。</summary>
    private static AndroidStepDecision Compare(AndroidPipelinePhase phase, string key, AndroidPlanInput input)
    {
        var reason = StaleReason(key, input);
        return reason is null ? Skip(phase, UnchangedReason, input.Abis) : Run(phase, reason, input.Abis);
    }

    /// <summary>
    /// 作り直す理由（作り直さなくてよければ null）。
    /// </summary>
    private static string? StaleReason(string key, AndroidPlanInput input)
    {
        if (input.Request.Rebuild) return "作り直しを指定";
        if (!input.Current.TryGetValue(key, out var current)) return "今の状態が分からない";
        if (current.Output == AndroidFingerprintBuilder.MissingMarker) return "出力がまだ無い";
        if (!input.Recorded.TryGetValue(key, out var recorded)) return "前回の記録が無い";
        if (recorded.Inputs != current.Inputs) return "入力が変わった";
        if (recorded.Output != current.Output) return "出力が前回の記録と違う（外で作り直された・消された）";
        return null;
    }

    /// <summary>インストールの判断。</summary>
    private static AndroidStepDecision DecideInstall(AndroidPlanInput input, AndroidStepDecision gradle)
    {
        var request = input.Request;
        if (request.NoInstall) return Skip(AndroidPipelinePhase.Install, "指定で飛ばします", input.Abis);
        if (request.Rebuild) return Run(AndroidPipelinePhase.Install, "入れ直しを指定", input.Abis);
        if (gradle.Runs) return Run(AndroidPipelinePhase.Install, "APK を作り直すため", input.Abis);

        var facts = input.Install;
        if (facts is null) return Run(AndroidPipelinePhase.Install, "端末の状態が分からない", input.Abis);
        if (facts.LocalApkSha256 is null) return Run(AndroidPipelinePhase.Install, "APK がありません", input.Abis);
        if (facts.DeviceInstalledPath is null) return Run(AndroidPipelinePhase.Install, "端末に入っていない", input.Abis);

        var recorded = facts.Recorded;
        if (recorded is null) return Run(AndroidPipelinePhase.Install, "この端末へ入れた記録が無い", input.Abis);
        if (!string.Equals(recorded.ApplicationId, facts.ApplicationId, StringComparison.Ordinal) ||
            !string.Equals(recorded.ApkSha256, facts.LocalApkSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Run(AndroidPipelinePhase.Install, "前回この端末へ入れた APK と違う", input.Abis);
        }
        if (!string.Equals(recorded.InstalledPath, facts.DeviceInstalledPath, StringComparison.Ordinal))
        {
            return Run(AndroidPipelinePhase.Install, "端末のアプリが前回自分で入れた後に入れ直されている", input.Abis);
        }
        return Skip(AndroidPipelinePhase.Install, "端末に同じ APK が入っている", input.Abis);
    }

    /// <summary>APK を作り直さないときの警告（APK が無い・ABI が合わない・pak が残っている）。</summary>
    private static void WarnAboutReusedApk(AndroidPlanInput input, bool installScope, List<string> warnings)
    {
        var request = input.Request;
        var installsApk = installScope && !request.NoInstall;
        if (installsApk && input.Install is { LocalApkSha256: null })
        {
            warnings.Add("APK がありません（APK の作成を飛ばす指定のため作りません）。インストールは失敗します。");
        }
        // ABI の食い違いは、その APK を端末へ入れるときだけ意味がある
        if (installsApk && input.RecordedApkAbis is { } apkAbis)
        {
            var missing = input.Abis.Where(abi => !apkAbis.Contains(abi.Name)).Select(abi => abi.Name).ToList();
            if (missing.Count > 0)
            {
                warnings.Add($"前回の APK には {string.Join(", ", missing)} が入っていません（入っている ABI: {string.Join(", ", apkAbis)}）。端末で起動できないことがあります。");
            }
        }
        if (!string.IsNullOrWhiteSpace(request.AssetsDir) && input.StagedPakPresent)
        {
            warnings.Add("前回のビルドで APK に assets.pak を入れています。そのままの APK ならパッケージ実行が優先され、送ったアセットは PAK に無いものしか使われません（APK を作り直すと pak 無しになります）。");
        }
    }

    /// <summary>行う判断を作る。</summary>
    private static AndroidStepDecision Run(AndroidPipelinePhase phase, string reason, IReadOnlyList<AndroidAbi> abis) =>
        new(phase, AndroidStepAction.Run, reason, abis);

    /// <summary>飛ばす判断を作る。</summary>
    private static AndroidStepDecision Skip(AndroidPipelinePhase phase, string reason, IReadOnlyList<AndroidAbi> abis) =>
        new(phase, AndroidStepAction.Skip, reason, abis);
}
