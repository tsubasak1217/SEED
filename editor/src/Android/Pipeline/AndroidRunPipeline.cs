// ============================================================
//  AndroidRunPipeline.cs — Android のビルド・配置・起動の本体（段階C。従来の runtime/android/build_and_run.ps1 の中身）
//
//  【流れ】
//    準備: 指定の検査 → プロジェクト（アセットルート・画面の向き・アプリの識別情報）→ 起動するシーン
//          （シーンマネージャに未登録なら pak の収録の起点に足す。段階C-4）→
//          端末（「自動」等なら要ればエミュレータを起動して待つ。段階C-3）と ABI →
//          各工程の今の指紋と前回の記録 → 実行計画（Plan/AndroidBuildPlan.cs。飛ばす工程と理由）
//    工程: libSEED.so（cargo ndk）→ pak とスクリプト（SeedPak）→ 同梱 .NET → APK / AAB（Gradle。アイコンの生成も）→
//          （配布用だけ）Google Play の要件の確認 → インストール → 開発用の転送（アセット・スクリプトの DLL）→ 起動 → logcat
//          （Steps/ の各クラス）
//    配布用（release。段階D）: 準備で署名の鍵を決めて keytool で開けるかを確かめ（無ければ・開けなければビルドを始めない）、
//          ビルドの前の要件の判定を出す（Release/AndroidRequirementChecks）。
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
using SEEDEditor.Android.Icons;
using SEEDEditor.Android.Ipc;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Processes;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Release;
using SEEDEditor.Android.Signing;
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
            new NativeBuildStep(), new PackageContentStep(), new DotnetBundleStep(), new GradleBuildStep(), new ReleaseCheckStep(),
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
        var builds = context.Request.Goal is AndroidRunGoal.Build or AndroidRunGoal.Install or AndroidRunGoal.Run;
        result = result with
        {
            Elapsed = total.Elapsed, Plan = plan, Device = context.Device, Identity = context.Identity,
            ArtifactPath = builds ? context.ArtifactPath : null,
            RequirementReport = context.RequirementReport,
        };
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

        var runStatePath = AndroidRunState.PathFor(project, _engine);
        var runState = AndroidRunState.Load(runStatePath);
        var buildScope = request.Goal is AndroidRunGoal.Build or AndroidRunGoal.Install or AndroidRunGoal.Run;

        // ── ランチャーのアイコン（APK を作るときだけ。設定の誤りは何もしないうちに弾く。段階D）──
        var launcherIcon = buildScope ? ResolveLauncherIcon(project, log) : null;

        // ── 配布用（release）の署名と、ビルドの前の Google Play の要件の判定（段階D）──
        var release = request.Variant == AndroidBuildVariant.Release
            ? await PrepareReleaseAsync(request, project, log, cancellationToken).ConfigureAwait(false)
            : null;

        // ── 起動するシーン（起動の工程があるときだけ。端末を用意する前に指定の誤りを弾く）──
        var launchScene = ResolveLaunchScene(request, project, log);
        if (release is not null && launchScene is not null)
        {
            log.Warn("配布用（release）の APK は起動オプション（起動するシーン・IPC）を受け取りません（MainActivity は debuggable のときだけ渡す）。開始シーンで起動します。");
        }
        // ── 起動するシーンがシーンマネージャに未登録なら、pak の収録の起点に足す（段階C-4）──
        var pakExtraScenes = DecidePakExtraScenes(request, launchScene, project, log);

        // ── 端末と ABI ──
        var (device, adb) = await ResolveDeviceAsync(request, runState, log, cancellationToken).ConfigureAwait(false);
        var abis = await ResolveAbisAsync(request, device, adb, log, cancellationToken).ConfigureAwait(false);

        // ── 今の指紋と前回の記録 ──
        var stamps = AndroidStepStamps.Load(_engine.StepStampsPath);
        // 配布用の APK は起動オプション（MainActivity が debuggable のときだけネイティブへ渡す）を受け取らないので IPC のポートを渡さない
        var ipcDevicePort = request.Variant == AndroidBuildVariant.Release ? null : AndroidIpcSettings.ResolveDevicePort(request.IpcPort);
        var context = new AndroidPipelineContext
        {
            Request = request, Engine = _engine, Toolchain = _toolchain, Project = project, Identity = identity,
            ScreenOrientation = orientation, Abis = abis, Device = device, Adb = adb,
            Stamps = stamps, RunState = runState, RunStatePath = runStatePath, LaunchScene = launchScene,
            PakExtraScenes = pakExtraScenes,
            // エディタとの IPC のポートと接続トークン（起動の工程が am start の extra seed.ipc_port・seed.ipc_token で渡す。段階D-1）。
            // トークンは指定（エディタ）が無ければここで作る（起動ごとの使い捨て）
            IpcDevicePort = ipcDevicePort,
            IpcToken = ipcDevicePort is null ? null : request.IpcToken ?? AndroidIpcToken.Create(),
            LauncherIcon = launcherIcon,
            Signing = release?.Signing,
            SigningCertificate = release?.Certificate,
            PlayRequirements = release?.Requirements,
            RequirementReport = release?.Report,
            ReleaseHistoryPath = AndroidReleaseHistory.PathFor(project, _engine),
        };
        if (buildScope)
        {
            var ndkPath = TryGetNdk();
            foreach (var abi in abis)
            {
                context.CurrentFingerprints[AndroidStepKeys.Native(abi)] = AndroidStepFingerprints.Native(_engine, abi, request.OptimizesNative, ndkPath);
            }
            context.CurrentFingerprints[AndroidStepKeys.PackageContent] = AndroidStepFingerprints.PackageContent(_engine, project, pakExtraScenes);
            context.CurrentFingerprints[AndroidStepKeys.DotnetBundle] = AndroidStepFingerprints.DotnetBundle(_engine, abis);
            context.CurrentFingerprints[context.GradleKey] =
                AndroidStepFingerprints.Gradle(_engine, GradleBuildStep.Parameters(context, ndkPath), launcherIcon);
        }

        // ── ビルドの前の要件の判定（ABI が決まってから。配布用だけ）──
        if (release is not null) EvaluatePreBuildRequirements(context, release, log);

        // ── インストールの判断の材料（端末に入っている APK の場所と、前回自分が入れた記録）──
        AndroidInstallFacts? install = null;
        var installScope = request.Goal is AndroidRunGoal.Install or AndroidRunGoal.Run;
        if (installScope && device is not null && adb is not null && !request.NoInstall)
        {
            var gradleMaySkip = request.SkipGradle
                || (stamps.Steps.TryGetValue(context.GradleKey, out var recordedGradle)
                    && context.CurrentFingerprints.TryGetValue(context.GradleKey, out var currentGradle)
                    && recordedGradle == currentGradle);
            // APK を作り直すならどのみち入れ直すので、APK の SHA-256（60〜110 MB を読む）は計算しない
            var localSha = gradleMaySkip ? ApkFile.Sha256(context.ArtifactPath, stamps.ArtifactFor(context.GradleKey)) : null;
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
            RecordedApkAbis = stamps.ArtifactFor(context.GradleKey)?.Abis,
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
        if (AndroidIpcSettings.Validate(request.IpcPort) is { } ipcPortError)
        {
            throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest, ipcPortError);
        }
        if (AndroidIpcToken.Validate(request.IpcToken) is { } ipcTokenError)
        {
            throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest, ipcTokenError);
        }
        if (ValidateVariant(request) is { } variantError)
        {
            throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest, variantError);
        }
    }

    /// <summary>
    /// ビルドの種類・形式・署名の指定の食い違い（段階D。純粋な処理。問題が無ければ null）。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <returns>問題の説明。</returns>
    public static string? ValidateVariant(AndroidRunRequest request)
    {
        var release = request.Variant == AndroidBuildVariant.Release;
        if (request.Format == AndroidPackageFormat.Aab && !release)
        {
            return "AAB は配布用（release）のビルドだけで作ります（SeedAndroid は --variant release、パッケージ化ウィンドウは「ビルドの種類」を配布用に）。";
        }
        if (request.Format == AndroidPackageFormat.Aab && request.Goal != AndroidRunGoal.Build)
        {
            return "AAB は端末へ直接入れられません。build で作り、Google Play へ出す（端末で試すなら bundletool で APKs にしてから入れる。docs/android.md §24）か、" +
                   "形式を APK にしてください。";
        }
        if (release && (request.Goal == AndroidRunGoal.Push || request.PushScripts || !string.IsNullOrWhiteSpace(request.AssetsDir)))
        {
            return "配布用（release）の APK は debuggable でないため run-as が使えず、開発用の転送（push・--push-scripts・--assets-dir）はできません。";
        }
        if (!release && (!string.IsNullOrWhiteSpace(request.KeystorePath) || !string.IsNullOrWhiteSpace(request.KeyAlias) || request.SigningSecrets is not null))
        {
            return "署名の指定（キーストア・別名・パスワード）は配布用（release）のビルドで使います（開発用はデバッグ用の鍵で署名します）。";
        }
        return null;
    }

    /// <summary>ランチャーのアイコンの元を決めてログへ出す（設定の誤りは例外。段階D）。</summary>
    private static LauncherIconSource? ResolveLauncherIcon(AndroidProjectInfo? project, AndroidPhaseLog log)
    {
        var icon = LauncherIconSettings.Resolve(project?.Settings.Android, project?.Folder.AssetsRoot);
        log.Info(icon is null
            ? $"アイコン: 設定なし（システムの既定のアイコン。プロジェクト設定 {AndroidAppSettings.SectionKey}.{AndroidAppSettings.IconKey}）"
            : $"アイコン: {icon.IconPath}（背景色 {icon.Background.ToAndroidHex()}。各密度の mipmap とアダプティブアイコンを生成）");
        return icon;
    }

    /// <summary>配布用ビルドの準備で決まったもの（段階D）。</summary>
    /// <param name="Signing">署名。</param>
    /// <param name="Certificate">鍵の証明書の要点。</param>
    /// <param name="Requirements">要件の表（読めなければ null）。</param>
    /// <param name="RequirementsError">要件の表を読めなかった理由。</param>
    /// <param name="Report">要件チェックの結果（ビルドの前の判定を後で入れる）。</param>
    private sealed record ReleasePreparation(
        AndroidSigningConfig Signing, AndroidKeystoreCertificate Certificate, PlayRequirements? Requirements, string? RequirementsError,
        AndroidRequirementReport Report);

    /// <summary>
    /// 配布用（release）の準備: 署名の鍵を決め、keytool で開けるか（パスワード・別名）を確かめ、要件の表を読む（段階D）。
    /// 鍵が無い・開けないときは、数分かかるビルドを始める前に止める（デバッグ署名にはしない）。
    /// </summary>
    private async Task<ReleasePreparation> PrepareReleaseAsync(
        AndroidRunRequest request, AndroidProjectInfo? project, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        log.Info($"ビルドの種類: 配布用（release・{request.Format.ToString().ToUpperInvariant()}。debuggable にしない・INTERNET なし・" +
                 "アップロード鍵で署名・Rust は --release）");
        var signing = AndroidSigningResolver.Resolve(SigningInputs(request, project));
        foreach (var warning in signing.Warnings) log.Warn(warning);
        log.Info($"署名: {signing.Describe()}");
        var certificate = await AndroidKeystoreTool.ReadAsync(
            _toolchain.RequireKeytool(), signing.KeystorePath, signing.KeyAlias, signing.Secrets, cancellationToken).ConfigureAwait(false);
        log.Info($"  {certificate.Describe()}");

        var requirements = PlayRequirements.Load(_engine.PlayRequirementsPath, out var requirementsError);
        return new ReleasePreparation(signing, certificate, requirements, requirementsError, new AndroidRequirementReport());
    }

    /// <summary>署名の鍵を決める材料（指定 → プロジェクトの packaging_settings.json の android.signing）。</summary>
    /// <param name="request">指定。</param>
    /// <param name="project">プロジェクト（無ければ null）。</param>
    /// <returns>材料。</returns>
    public static AndroidSigningInputs SigningInputs(AndroidRunRequest request, AndroidProjectInfo? project)
    {
        AndroidSigningSettings? settings = null;
        if (project is not null)
        {
            settings = PackagingData.LoadFrom(Path.Combine(project.Folder.AssetsRoot, PackagingData.SettingsFileName)).Android.Signing;
        }
        return new AndroidSigningInputs(
            request.KeystorePath, request.KeyAlias, request.SigningSecrets, settings, project?.Folder.ProjectRoot, project?.Folder.AssetsRoot);
    }

    /// <summary>ビルドの前の Google Play の要件の判定を行い、一覧をログへ出す（段階D）。</summary>
    private static void EvaluatePreBuildRequirements(AndroidPipelineContext context, ReleasePreparation release, AndroidPhaseLog log)
    {
        var report = release.Report;
        if (release.Requirements is null)
        {
            report.Put(AndroidRequirementChecks.TableMissing(release.RequirementsError ?? string.Empty));
        }
        else
        {
            var history = AndroidReleaseHistory.Load(context.ReleaseHistoryPath);
            var facts = new AndroidPreBuildFacts
            {
                Format = context.Request.Format,
                TargetApiLevel = AndroidRuntimeContract.TargetApiLevel,
                Abis = context.Abis,
                Identity = context.Identity,
                PreviousRelease = history.Last(context.Identity.ApplicationId, context.Request.Format),
                Signing = release.Signing,
                LauncherIcon = context.LauncherIcon is not null,
            };
            foreach (var item in AndroidRequirementChecks.Evaluate(release.Requirements, facts)) report.Put(item);
        }
        log.Info($"Google Play の要件（ビルドの前。{report.Summary()}）:");
        foreach (var item in report.Items) LogRequirement(log, item);
    }

    /// <summary>要件の判定 1 件をログへ出す（不合格・注意は警告の色）。</summary>
    /// <param name="log">ログ。</param>
    /// <param name="item">判定。</param>
    public static void LogRequirement(AndroidPhaseLog log, AndroidRequirementItem item)
    {
        var line = "  " + item.Describe();
        if (item.Severity is AndroidRequirementSeverity.Failure or AndroidRequirementSeverity.Warning) log.Warn(line);
        else log.Info(line);
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
    /// 起動するシーンを決める（起動の工程が無ければ見ない）。指定の形の誤り（アセットフォルダの外・..）は何もしないうちに弾き、
    /// プロジェクトに無いシーンは警告だけ出してそのまま渡す（端末が警告を出して開始シーンで起動する。pak に入らなかった
    /// シーンも端末側で同じ扱いになるので、判断は端末に 1 本化する）。シーンマネージャに未登録のシーンを pak に入れるのは
    /// 次の <see cref="DecidePakExtraScenes"/>（段階C-4）。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <param name="project">プロジェクト（無ければ null）。</param>
    /// <param name="log">準備のログ。</param>
    /// <returns>アセットルートからの相対パス（開始シーンなら null）。</returns>
    private static string? ResolveLaunchScene(AndroidRunRequest request, AndroidProjectInfo? project, AndroidPhaseLog log)
    {
        var launches = (request.Goal is AndroidRunGoal.Run or AndroidRunGoal.Push) && !request.NoLaunch;
        if (!launches) return null;

        var assetsRoot = project?.Folder.AssetsRoot;
        var scene = AndroidScenePath.Normalize(request.ScenePath, assetsRoot);
        if (scene.Error is not null) throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest, scene.Error);
        if (scene.Relative is null)
        {
            log.Info("起動するシーン: 開始シーン（project_settings.json の start_scene）");
            return null;
        }

        log.Info($"起動するシーン: {scene.Relative}（am start の extra {AndroidRuntimeContract.SceneExtraName} で渡す）");
        if (assetsRoot is null || !AndroidScenePath.ExistsUnder(assetsRoot, scene.Relative))
        {
            log.Warn($"シーン {scene.Relative} がプロジェクトのアセット（{assetsRoot ?? "指定なし"}）にありません。端末は警告を出して開始シーンで起動します。");
        }
        return scene.Relative;
    }

    /// <summary>
    /// 起動するシーンがシーンマネージャに未登録なら、APK の pak の収録の起点に足すシーンとして返す（段階C-4。判断は
    /// Project/AndroidPakSceneSeeds）。pak を作る目的（Build・Install・Run）のときだけ見る（Push は pak を作らない）。
    /// 足したシーンは pak の指紋に入るので、未登録のシーンへ切り替えた最初の実行で pak・APK・インストールをやり直す。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <param name="launchScene">起動するシーン（アセットルートからの相対パス。開始シーンなら null）。</param>
    /// <param name="project">プロジェクト（無ければ null）。</param>
    /// <param name="log">準備のログ。</param>
    /// <returns>pak の収録の起点に足すシーン（無ければ空）。</returns>
    private static IReadOnlyList<string> DecidePakExtraScenes(
        AndroidRunRequest request, string? launchScene, AndroidProjectInfo? project, AndroidPhaseLog log)
    {
        var buildsPak = request.Goal is AndroidRunGoal.Build or AndroidRunGoal.Install or AndroidRunGoal.Run;
        if (!buildsPak) return Array.Empty<string>();

        var decision = AndroidPakSceneSeeds.Decide(launchScene, project);
        if (decision.Status == AndroidPakSceneSeedStatus.AddedAsSeed)
        {
            log.Info(request.SkipGradle
                ? $"シーン {launchScene} はシーンマネージャに未登録です（APK の作成を飛ばす指定のため pak は作り直しません。前回の pak に無ければ端末は開始シーンで起動します）。"
                : $"シーン {launchScene} はシーンマネージャに未登録のため、pak の収録の起点に足します（SeedPak {SeedPakProcess.ExtraSceneOption}。" +
                  "未登録のシーンへ切り替えた最初の実行は pak・APK・インストールをやり直します）。");
        }
        return decision.ExtraScenes;
    }

    /// <summary>
    /// 端末を決める。端末の工程を行うなら必須。ビルドだけでも、ABI を決めるために 1 台に決まる端末があれば使う。
    /// 「自動」「選んだ端末が見えなければエミュレータ」（段階C-3）で端末の工程を行うなら、要ればエミュレータを起動して待つ
    /// （AndroidDeviceActions.EnsureDeviceAsync）。
    /// </summary>
    private async Task<(AdbDevice? Device, AdbClient? Adb)> ResolveDeviceAsync(
        AndroidRunRequest request, AndroidRunState runState, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var needsDevice = NeedsDevice(request);
        var target = AndroidDeviceTarget.From(request.Serial, request.EmulatorFallback);

        // ── 自動・エミュレータへの切り替え（端末の工程を行うときだけ。ビルドだけのためにエミュレータを起動しない）──
        if (needsDevice && target.MayLaunchEmulator)
        {
            var provisioningAdb = new AdbClient(_toolchain.RequireAdb());
            var provisioned = await new AndroidDeviceActions(_toolchain)
                .EnsureDeviceAsync(target, runState.LastTarget?.Serial, request.Avd, log, cancellationToken).ConfigureAwait(false);
            log.Info($"端末: {provisioned.Serial}（{provisioned.KindLabel}・{provisioned.Model ?? "機種不明"}）");
            return (provisioned, provisioningAdb);
        }

        // 「自動」でも端末の工程が無ければ、指定なしと同じ（ABI を決める端末が 1 台に決まれば使う）
        var serial = target.Mode == AndroidDeviceTargetMode.Auto ? null : target.Serial;
        // 配布用のビルドは、たまたまつながっている端末で ABI を変えない（エミュレータの x86_64 だけの配布物を作らないため。段階D）
        var wantsAbiFromDevice = request.Abis is null && request.Goal == AndroidRunGoal.Build && request.Variant == AndroidBuildVariant.Debug;
        if (!needsDevice && !wantsAbiFromDevice && string.IsNullOrWhiteSpace(serial)) return (null, null);

        AdbClient adb;
        try
        {
            adb = new AdbClient(_toolchain.RequireAdb());
        }
        catch (AndroidPipelineException) when (!needsDevice && string.IsNullOrWhiteSpace(serial))
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
            if (!needsDevice && string.IsNullOrWhiteSpace(serial)) return (null, null);
            throw new AndroidPipelineException(AndroidFailureKind.Device, $"端末の一覧を取れません: {ex.Message}", ex);
        }

        var selection = AndroidDeviceSelector.Select(devices, serial, runState.LastTarget?.Serial);
        if (selection.Device is null)
        {
            if (!needsDevice && string.IsNullOrWhiteSpace(serial))
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
        else if (request.Variant == AndroidBuildVariant.Release)
        {
            // 配布は実機向けの arm64-v8a だけ（docs/android.md §2。x86_64 も入れると同梱 .NET の BCL が両方の分配られる。段階D）
            abis = new[] { AndroidAbis.Arm64 };
            log.Info($"ABI: {AndroidAbis.Arm64.Name}（配布用の既定。変えるなら --abi）");
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
