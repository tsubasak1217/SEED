// ============================================================
//  AndroidRunPipeline.cs — Android のビルド・配置・起動の本体（段階C。従来の runtime/android/build_and_run.ps1 の中身）
//
//  【流れ】
//    準備: 指定の検査 → プロジェクト（アセットルート・画面の向き・アプリの識別情報）→ 端末と ABI →
//          各工程の今の指紋と前回の記録 → 実行計画（Plan/AndroidBuildPlan.cs。飛ばす工程と理由）
//    工程: libSEED.so（cargo ndk）→ pak とスクリプト（SeedPak）→ 同梱 .NET → APK（Gradle）→ インストール →
//          開発用の転送（アセット・スクリプトの DLL）→ 起動 → logcat（Steps/ の各クラス）
//    後始末: 置き場の記録（工程ごと。エンジン側）とプロジェクトの実行状態（前回の実行先・入れた APK・結果）を保存
//
//  【呼び出し側への約束】
//    - 進み具合・ログ・エラーは IProgress&lt;AndroidPipelineEvent&gt; へ流す（Pipeline/AndroidPipelineEvent.cs）。
//    - CancellationToken で中断できる（子プロセスとその子孫を終了させる）。logcat の工程での中断は「止めた」＝成功。
//    - 失敗しても例外は投げず、結果（AndroidPipelineResult）に種類と説明を入れて返す。
//    - 全体をスレッドプールで動かす（エディタの UI スレッドから await しても、ファイルの走査・複製で UI を止めない）。
//  エディタ（段階C-2 の実行先セレクタ）とコンソールツール（editor/tools/SeedAndroid）が同じこのクラスを使う。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Processes;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.State;
using SEEDEditor.Android.Steps;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Packaging;
using SEEDEditor.Project;
using SEEDEditor.ProjectSettings;

namespace SEEDEditor.Android.Pipeline;

/// <summary>Android のビルド・配置・起動。</summary>
public sealed class AndroidRunPipeline
{
    /// <summary>エンジン側の置き場。</summary>
    private readonly AndroidEnginePaths _engine;

    /// <summary>道具の場所。</summary>
    private readonly AndroidToolchain _toolchain;

    /// <summary>工程 → 実装。</summary>
    private readonly IReadOnlyDictionary<AndroidPipelinePhase, IAndroidPipelineStep> _steps;

    /// <summary>置き場とツールを指定して作る。</summary>
    /// <param name="engine">エンジン側の置き場（AndroidEnginePaths.Locate で探す）。</param>
    /// <param name="toolchain">道具の場所（AndroidToolchain.Detect で探す）。</param>
    public AndroidRunPipeline(AndroidEnginePaths engine, AndroidToolchain toolchain)
    {
        _engine = engine;
        _toolchain = toolchain;
        var steps = new IAndroidPipelineStep[]
        {
            new NativeBuildStep(), new PackageContentStep(), new DotnetBundleStep(), new GradleBuildStep(),
            new InstallStep(), new PushAssetsStep(), new PushScriptsStep(), new LaunchStep(), new LogcatStep(),
        };
        _steps = steps.ToDictionary(step => step.Phase);
    }

    /// <summary>
    /// ビルド・配置・起動を行う（失敗しても例外は投げず、結果に入れて返す）。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <param name="progress">進み具合・ログの送り先（複数のスレッドから呼ばれる）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>結果。</returns>
    public Task<AndroidPipelineResult> RunAsync(
        AndroidRunRequest request, IProgress<AndroidPipelineEvent>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => RunCoreAsync(request, progress, cancellationToken), CancellationToken.None);

    /// <summary>本体（スレッドプールで動く）。</summary>
    private async Task<AndroidPipelineResult> RunCoreAsync(
        AndroidRunRequest request, IProgress<AndroidPipelineEvent>? progress, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.Now;
        var outcomes = new List<AndroidStepOutcome>();
        var prepareLog = new AndroidPhaseLog(AndroidPipelinePhase.Prepare, progress);
        AndroidPipelineContext? context = null;
        AndroidBuildPlan? plan = null;
        AndroidPipelineResult result;

        // ── 準備 ──
        var prepareWatch = Stopwatch.StartNew();
        var prepareTitle = AndroidPipelinePhaseNames.Title(AndroidPipelinePhase.Prepare);
        prepareLog.Report(new AndroidPhaseStarted(AndroidPipelinePhase.Prepare, 0, 0, prepareTitle, request.Goal.ToString()));
        try
        {
            (context, plan) = await PrepareAsync(request, prepareLog, cancellationToken).ConfigureAwait(false);
            // 決まった端末・アプリ・ABI を先に知らせる（エディタはアプリの終了の見張りと停止に使う）
            prepareLog.Report(new AndroidPrepared(context.Device, context.Identity, context.Abis));
            prepareLog.Report(new AndroidPhaseFinished(
                AndroidPipelinePhase.Prepare, 0, plan.Steps.Count, prepareTitle, AndroidPhaseOutcome.Succeeded, prepareWatch.Elapsed,
                $"{plan.Steps.Count(s => s.Runs)} 工程を行い、{plan.Steps.Count(s => !s.Runs)} 工程を飛ばします"));
        }
        catch (Exception ex)
        {
            var (outcome, failure) = Classify(ex, AndroidPipelinePhase.Prepare, cancellationToken);
            prepareLog.Report(new AndroidPhaseFinished(AndroidPipelinePhase.Prepare, 0, 0, prepareTitle, outcome, prepareWatch.Elapsed, failure?.Message ?? "中断しました"));
            outcomes.Add(new AndroidStepOutcome(AndroidPipelinePhase.Prepare, outcome, failure?.Message ?? "中断しました", prepareWatch.Elapsed));
            if (failure is not null) prepareLog.Report(new AndroidPipelineError(AndroidPipelinePhase.Prepare, failure.Kind, failure.Message));
            return new AndroidPipelineResult
            {
                Canceled = failure is null, FailureKind = failure?.Kind, FailureMessage = failure?.Message,
                Steps = outcomes, Elapsed = total.Elapsed,
            };
        }

        // ── 工程 ──
        result = await ExecuteAsync(context, plan, outcomes, progress, cancellationToken).ConfigureAwait(false);
        result = result with { Elapsed = total.Elapsed, Plan = plan, Device = context.Device, Identity = context.Identity };
        SaveRunState(context, result, startedAt, prepareLog);
        return result;
    }

    // ── 準備 ──────────────────────────────────────────────

    /// <summary>準備: 指定の検査・プロジェクト・端末と ABI・指紋・実行計画。</summary>
    private async Task<(AndroidPipelineContext Context, AndroidBuildPlan Plan)> PrepareAsync(
        AndroidRunRequest request, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        foreach (var note in _toolchain.Notes) log.Info(note);

        // ── プロジェクト（アセットルート・画面の向き・アプリの識別情報）──
        var project = AndroidProjectResolver.Resolve(request.ProjectDir, request.AssetsDir);
        DescribeProject(project, log);
        var identity = AndroidProjectResolver.ResolveIdentity(project);
        var orientation = project?.Settings.ScreenOrientation ?? ScreenOrientationSetting.Default;
        log.Info($"アプリ: {identity.Describe()}");
        log.Info($"画面の向き（{AndroidProjectSettingsReader.ScreenOrientationKey}）: {orientation}" +
                 (project is { Settings.Found: true } ? $"  ← {project.Settings.SettingsPath}" : "  ← 既定値"));

        var runStatePath = project is not null ? AndroidRunState.PathForProject(project.Folder.ProjectRoot) : _engine.FallbackRunStatePath;
        var runState = AndroidRunState.Load(runStatePath);

        // ── 端末と ABI ──
        var (device, adb) = await ResolveDeviceAsync(request, runState, log, cancellationToken).ConfigureAwait(false);
        var abis = await ResolveAbisAsync(request, device, adb, log, cancellationToken).ConfigureAwait(false);

        // ── 今の指紋と前回の記録 ──
        var stamps = AndroidStepStamps.Load(_engine.StepStampsPath);
        var context = new AndroidPipelineContext
        {
            Request = request, Engine = _engine, Toolchain = _toolchain, Project = project, Identity = identity,
            ScreenOrientation = orientation, Abis = abis, Device = device, Adb = adb,
            Stamps = stamps, RunState = runState, RunStatePath = runStatePath,
        };
        var buildScope = request.Goal is AndroidRunGoal.Build or AndroidRunGoal.Install or AndroidRunGoal.Run;
        if (buildScope)
        {
            var ndkPath = TryGetNdk();
            foreach (var abi in abis)
            {
                context.CurrentFingerprints[AndroidStepKeys.Native(abi)] = AndroidStepFingerprints.Native(_engine, abi, request.Release, ndkPath);
            }
            context.CurrentFingerprints[AndroidStepKeys.PackageContent] = AndroidStepFingerprints.PackageContent(_engine, project);
            context.CurrentFingerprints[AndroidStepKeys.DotnetBundle] = AndroidStepFingerprints.DotnetBundle(_engine, abis);
            context.CurrentFingerprints[AndroidStepKeys.Gradle] = AndroidStepFingerprints.Gradle(_engine, GradleBuildStep.Parameters(context, ndkPath));
        }

        // ── インストールの判断の材料（端末に入っている APK の場所と、前回自分が入れた記録）──
        AndroidInstallFacts? install = null;
        var installScope = request.Goal is AndroidRunGoal.Install or AndroidRunGoal.Run;
        if (installScope && device is not null && adb is not null && !request.NoInstall)
        {
            var gradleMaySkip = request.SkipGradle
                || (stamps.Steps.TryGetValue(AndroidStepKeys.Gradle, out var recordedGradle)
                    && context.CurrentFingerprints.TryGetValue(AndroidStepKeys.Gradle, out var currentGradle)
                    && recordedGradle == currentGradle);
            // APK を作り直すならどのみち入れ直すので、APK の SHA-256（60〜110 MB を読む）は計算しない
            var localSha = gradleMaySkip ? ApkFile.Sha256(_engine.DebugApkPath, stamps.Apk) : null;
            var installedPath = await adb.GetInstalledApkPathAsync(device.Serial, identity.ApplicationId, cancellationToken).ConfigureAwait(false);
            install = new AndroidInstallFacts(identity.ApplicationId, localSha, installedPath, runState.InstallRecordFor(device.Serial));
        }

        var plan = AndroidBuildPlan.Create(new AndroidPlanInput
        {
            Request = request,
            Abis = abis,
            Current = context.CurrentFingerprints,
            Recorded = stamps.Steps,
            Install = install,
            RecordedApkAbis = stamps.Apk?.Abis,
            StagedPakPresent = File.Exists(Path.Combine(_engine.ApkPackageDir, PackageLayout.PakFileName)),
        });
        foreach (var step in plan.Steps)
        {
            log.Info($"  {(step.Runs ? "行う  " : "飛ばす")} {AndroidPipelinePhaseNames.Title(step.Phase)} — {step.Reason}");
        }
        foreach (var warning in plan.Warnings) log.Warn(warning);
        return (context, plan);
    }

    /// <summary>指定の食い違いを、何もしないうちに弾く（従来の build_and_run.ps1 と同じ検査）。</summary>
    private static void ValidateRequest(AndroidRunRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ProjectDir) && !string.IsNullOrWhiteSpace(request.AssetsDir))
        {
            throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest,
                "プロジェクト（APK 内の pak で起動）と開発用のアセットフォルダ（run-as で送ったアセットで起動）は同時に指定できません。端末は APK に pak があればそちらを優先します。");
        }
        var needsScriptSource = request.Goal == AndroidRunGoal.Push || request.PushScripts;
        if (needsScriptSource && string.IsNullOrWhiteSpace(request.ProjectDir) && string.IsNullOrWhiteSpace(request.AssetsDir))
        {
            throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest,
                "スクリプトの転送には、スクリプト（.cs）の出どころとしてプロジェクトかアセットフォルダを指定してください。");
        }
        if (request.LogcatSeconds < 0)
        {
            throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest, "logcat の秒数は 0 以上にしてください（0 は止めるまで）。");
        }
    }

    /// <summary>決まったプロジェクトをログへ出す（設定の読み取りの警告も）。</summary>
    private static void DescribeProject(AndroidProjectInfo? project, AndroidPhaseLog log)
    {
        if (project is null)
        {
            log.Info("プロジェクトの指定がありません（pak とスクリプトの無い開発用の APK を作ります）。");
            return;
        }
        foreach (var warning in project.Settings.Warnings) log.Warn(warning);
        var folder = project.Folder;
        log.Info(project.Mode == AndroidProjectMode.Packaged
            ? $"プロジェクト: {folder.AssetsRoot}（{folder.Origin}）→ APK に pak とスクリプトを入れる"
            : $"開発用のアセット: {folder.AssetsRoot} → run-as で端末の {AndroidRuntimeContract.RemoteAssetsDir} へ送る");
    }

    /// <summary>
    /// 端末を決める。端末の工程を行うなら必須。ビルドだけでも、ABI を決めるために 1 台に決まる端末があれば使う。
    /// </summary>
    private async Task<(AdbDevice? Device, AdbClient? Adb)> ResolveDeviceAsync(
        AndroidRunRequest request, AndroidRunState runState, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var needsDevice = NeedsDevice(request);
        var wantsAbiFromDevice = request.Abis is null && request.Goal == AndroidRunGoal.Build;
        if (!needsDevice && !wantsAbiFromDevice && string.IsNullOrWhiteSpace(request.Serial)) return (null, null);

        AdbClient adb;
        try
        {
            adb = new AdbClient(_toolchain.RequireAdb());
        }
        catch (AndroidPipelineException) when (!needsDevice && string.IsNullOrWhiteSpace(request.Serial))
        {
            // ビルドだけなら adb が無くても続ける（ABI は既定の両方）
            return (null, null);
        }

        IReadOnlyList<AdbDevice> devices;
        try
        {
            devices = await adb.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AdbCommandException or ChildProcessStartException)
        {
            if (!needsDevice && string.IsNullOrWhiteSpace(request.Serial)) return (null, null);
            throw new AndroidPipelineException(AndroidFailureKind.Device, $"端末の一覧を取れません: {ex.Message}", ex);
        }

        var selection = AndroidDeviceSelector.Select(devices, request.Serial, runState.LastTarget?.Serial);
        if (selection.Device is null)
        {
            if (!needsDevice && string.IsNullOrWhiteSpace(request.Serial))
            {
                log.Info("ABI を決める端末が 1 台に決まりません（端末が無い・2 台以上）。");
                return (null, null);
            }
            throw new AndroidPipelineException(AndroidFailureKind.Device, selection.Error ?? "端末を決められません。");
        }
        var device = selection.Device;
        log.Info($"端末: {device.Serial}（{device.KindLabel}・{device.Model ?? "機種不明"}）");
        return (device, adb);
    }

    /// <summary>端末の工程を 1 つでも行う指定か（従来の build_and_run.ps1 の $needsDevice と同じ考え方）。</summary>
    private static bool NeedsDevice(AndroidRunRequest request) => request.Goal switch
    {
        AndroidRunGoal.Build   => false,
        AndroidRunGoal.Install => !request.NoInstall || !string.IsNullOrWhiteSpace(request.AssetsDir) || request.PushScripts,
        AndroidRunGoal.Run     => !request.NoInstall || !string.IsNullOrWhiteSpace(request.AssetsDir) || request.PushScripts
                                  || !request.NoLaunch || !request.NoLogcat,
        _                      => true,
    };

    /// <summary>ABI を決める（指定 → 端末の ro.product.cpu.abilist → 両方）。</summary>
    private static async Task<IReadOnlyList<AndroidAbi>> ResolveAbisAsync(
        AndroidRunRequest request, AdbDevice? device, AdbClient? adb, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        AndroidAbi? deviceAbi = null;
        if (device is not null && adb is not null)
        {
            var abiList = await adb.GetPropertyAsync(device.Serial, AdbClient.AbiListProperty, cancellationToken).ConfigureAwait(false);
            deviceAbi = AndroidAbis.ChooseForDevice(abiList);
            if (deviceAbi is null && request.Abis is null)
            {
                throw new AndroidPipelineException(AndroidFailureKind.Device,
                    $"端末 {device.Serial} の ABI（{abiList}）に、ビルドできる ABI（{AndroidAbis.Describe(AndroidAbis.Supported)}）がありません。");
            }
        }

        IReadOnlyList<AndroidAbi> abis;
        if (request.Abis is not null)
        {
            abis = AndroidAbis.ParseList(string.Join(AndroidAbis.ListSeparator, request.Abis), out var error);
            if (error is not null) throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest, error);
            if (deviceAbi is not null && !abis.Contains(deviceAbi))
            {
                log.Warn($"指定の ABI（{AndroidAbis.Describe(abis)}）に端末の ABI {deviceAbi.Name} がありません。端末で起動できないことがあります。");
            }
            log.Info($"ABI: {AndroidAbis.Describe(abis)}（指定）");
        }
        else if (deviceAbi is not null)
        {
            abis = new[] { deviceAbi };
            log.Info($"ABI: {deviceAbi.Name}（端末から判定）");
        }
        else
        {
            abis = AndroidAbis.Default;
            log.Info($"ABI: {AndroidAbis.Describe(abis)}（端末から決められないため両方）");
        }
        return abis;
    }

    /// <summary>NDK の場所（無ければ null。指紋の材料で、無いことは NDK を使う工程で知らせる）。</summary>
    private string? TryGetNdk()
    {
        try
        {
            return _toolchain.RequireNdk();
        }
        catch (AndroidPipelineException)
        {
            return null;
        }
    }

    // ── 工程の実行 ────────────────────────────────────────

    /// <summary>計画の順に工程を行う。</summary>
    private async Task<AndroidPipelineResult> ExecuteAsync(
        AndroidPipelineContext context, AndroidBuildPlan plan, List<AndroidStepOutcome> outcomes,
        IProgress<AndroidPipelineEvent>? progress, CancellationToken cancellationToken)
    {
        var count = plan.Steps.Count;
        for (var i = 0; i < count; i++)
        {
            var decision = plan.Steps[i];
            var index = i + 1;
            var title = AndroidPipelinePhaseNames.Title(decision.Phase);
            var log = new AndroidPhaseLog(decision.Phase, progress);

            if (!decision.Runs)
            {
                log.Report(new AndroidPhaseFinished(decision.Phase, index, count, title, AndroidPhaseOutcome.Skipped, TimeSpan.Zero, decision.Reason));
                outcomes.Add(new AndroidStepOutcome(decision.Phase, AndroidPhaseOutcome.Skipped, decision.Reason, TimeSpan.Zero));
                log.Report(new AndroidProgressChanged(decision.Phase, (double)index / count, title));
                continue;
            }

            log.Report(new AndroidPhaseStarted(decision.Phase, index, count, title, decision.Reason));
            var watch = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var summary = await _steps[decision.Phase].RunAsync(context, decision, log, cancellationToken).ConfigureAwait(false);
                if (IsBuildPhase(decision.Phase)) SaveStamps(context, log);
                log.Report(new AndroidPhaseFinished(decision.Phase, index, count, title, AndroidPhaseOutcome.Succeeded, watch.Elapsed, summary));
                outcomes.Add(new AndroidStepOutcome(decision.Phase, AndroidPhaseOutcome.Succeeded, summary, watch.Elapsed));
                log.Report(new AndroidProgressChanged(decision.Phase, (double)index / count, title));
            }
            catch (Exception ex)
            {
                var (outcome, failure) = Classify(ex, decision.Phase, cancellationToken);
                var message = failure?.Message ?? "中断しました";
                log.Report(new AndroidPhaseFinished(decision.Phase, index, count, title, outcome, watch.Elapsed, message));
                outcomes.Add(new AndroidStepOutcome(decision.Phase, outcome, message, watch.Elapsed));
                if (failure is not null) log.Report(new AndroidPipelineError(decision.Phase, failure.Kind, failure.Message));
                return new AndroidPipelineResult
                {
                    Canceled = failure is null, FailureKind = failure?.Kind, FailureMessage = failure?.Message, Steps = outcomes,
                };
            }
        }
        return new AndroidPipelineResult { Steps = outcomes };
    }

    /// <summary>ビルドの工程か（終えたら置き場の記録を書く）。</summary>
    private static bool IsBuildPhase(AndroidPipelinePhase phase) => phase is
        AndroidPipelinePhase.NativeBuild or AndroidPipelinePhase.PackageContent or AndroidPipelinePhase.DotnetBundle or AndroidPipelinePhase.Gradle;

    /// <summary>
    /// 例外を「中断」か「種類付きの失敗」に分ける（中断なら失敗は null）。
    /// </summary>
    private static (AndroidPhaseOutcome Outcome, AndroidPipelineException? Failure) Classify(
        Exception exception, AndroidPipelinePhase phase, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return (AndroidPhaseOutcome.Canceled, null);
        }
        var devicePhase = AndroidBuildPlan.IsDevicePhase(phase);
        var failure = exception switch
        {
            AndroidPipelineException pipeline => pipeline,
            ChildProcessStartException start => new AndroidPipelineException(AndroidFailureKind.Toolchain, start.Message, start),
            AdbCommandException adb => new AndroidPipelineException(
                phase == AndroidPipelinePhase.Prepare ? AndroidFailureKind.Device : AndroidFailureKind.DeviceOperation, adb.Message, adb),
            IOException or UnauthorizedAccessException or JsonException => new AndroidPipelineException(
                devicePhase ? AndroidFailureKind.DeviceOperation : AndroidFailureKind.Build, exception.Message, exception),
            _ => new AndroidPipelineException(
                devicePhase ? AndroidFailureKind.DeviceOperation : AndroidFailureKind.Build,
                $"予期しないエラー（{exception.GetType().Name}）: {exception.Message}", exception),
        };
        return (AndroidPhaseOutcome.Failed, failure);
    }

    // ── 記録 ──────────────────────────────────────────────

    /// <summary>置き場の記録を書く（失敗しても工程は成功のまま。次回作り直すだけ）。</summary>
    private void SaveStamps(AndroidPipelineContext context, AndroidPhaseLog log)
    {
        try
        {
            context.Stamps.Save(_engine.StepStampsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"置き場の記録を書けません（次回は作り直します）: {ex.Message}");
        }
    }

    /// <summary>プロジェクトの実行状態（前回の実行先・結果）を書く。</summary>
    private static void SaveRunState(AndroidPipelineContext context, AndroidPipelineResult result, DateTimeOffset startedAt, AndroidPhaseLog log)
    {
        var state = context.RunState;
        if (context.Device is { } device)
        {
            state.LastTarget = new AndroidTargetRecord
            {
                Serial = device.Serial,
                Kind = device.Kind == AdbDeviceKind.Emulator ? "emulator" : "physical",
                Model = device.Model,
                Abi = context.Abis.FirstOrDefault()?.Name,
                ApplicationId = context.Identity.ApplicationId,
                UsedAt = DateTimeOffset.Now,
            };
        }
        state.LastRun = new AndroidLastRunRecord
        {
            Goal = context.Request.Goal.ToString().ToLowerInvariant(),
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now,
            Result = result.Succeeded ? "succeeded" : result.Canceled ? "canceled" : "failed",
            Message = result.FailureMessage,
            Steps = result.Steps.Select(step => new AndroidRunStepRecord
            {
                Phase = step.Phase.ToString(),
                Outcome = step.Outcome.ToString().ToLowerInvariant(),
                Reason = step.Summary,
                Seconds = Math.Round(step.Elapsed.TotalSeconds, 3),
            }).ToList(),
            Fingerprints = new Dictionary<string, AndroidStepFingerprint>(context.CurrentFingerprints),
        };
        try
        {
            state.Save(context.RunStatePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"実行状態を書けません: {context.RunStatePath}（{ex.Message}）");
        }
    }
}
