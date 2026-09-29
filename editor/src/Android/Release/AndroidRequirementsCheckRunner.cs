// ============================================================
//  AndroidRequirementsCheckRunner.cs — ビルドをせずに Google Play の要件を確かめる（SeedAndroid の check・パッケージ化ウィンドウの
//                                     「要件を確認」。段階D。docs/android.md §24）
//
//  【流れ】
//    1. プロジェクト・アプリの識別情報・アイコン・プラットフォーム機能（W1-2）の設定を読む（中核のビルドの準備と同じ関数）
//    2. 署名の鍵を決めて keytool で開けるかを確かめる（決まらない・開けないことも「署名」の不合格として一覧に出す。例外にしない）
//    3. ビルドの前の判定（AndroidRequirementChecks）
//    4. 配布物があれば（--artifact か、Gradle の出力の前回の配布用ビルド）道具で読み直して判定（AndroidArtifactChecks）で上書き
//  ビルドのパイプライン（AndroidRunPipeline）は鍵が無いと準備で止まるが、こちらは「何が足りないか」を全部並べるのが目的。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Icons;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Platform;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Signing;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Release;

/// <summary>ビルドをせずに要件を確かめた結果。</summary>
/// <param name="Report">判定の一覧。</param>
/// <param name="ArtifactPath">確かめた配布物（無ければ null）。</param>
public sealed record AndroidRequirementsCheckResult(AndroidRequirementReport Report, string? ArtifactPath);

/// <summary>ビルドをせずに要件を確かめる。</summary>
public static class AndroidRequirementsCheckRunner
{
    /// <summary>
    /// 要件を確かめる。
    /// </summary>
    /// <param name="engine">エンジン側の置き場。</param>
    /// <param name="toolchain">道具の場所。</param>
    /// <param name="request">指定（プロジェクト・形式・ABI・署名の指定。ビルドの種類は配布用として扱う）。</param>
    /// <param name="artifactPath">確かめる配布物（null なら Gradle の出力の前回の配布用ビルド。無ければ配布物の判定はしない）。</param>
    /// <param name="log">経過の 1 行（null なら出さない）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>結果。</returns>
    /// <exception cref="AndroidPipelineException">プロジェクト・アプリの識別情報の指定の誤り。</exception>
    public static async Task<AndroidRequirementsCheckResult> RunAsync(
        AndroidEnginePaths engine, AndroidToolchain toolchain, AndroidRunRequest request, string? artifactPath,
        Action<string>? log, CancellationToken cancellationToken)
    {
        var report = new AndroidRequirementReport();
        var requirements = PlayRequirements.Load(engine.PlayRequirementsPath, out var tableError);
        if (requirements is null)
        {
            report.Put(AndroidRequirementChecks.TableMissing(tableError ?? string.Empty));
            return new AndroidRequirementsCheckResult(report, null);
        }

        // ── プロジェクトと識別情報（ビルドと同じ関数。誤りは例外＝指定の誤り）──
        var project = AndroidProjectResolver.Resolve(request.ProjectDir, request.AssetsDir);
        var identity = AndroidProjectResolver.ResolveIdentity(project);
        log?.Invoke($"アプリ: {identity.Describe()}");

        // ── アイコン（設定の誤りは「アイコン」の不合格として出す）──
        var iconErrors = LauncherIconSettings.Validate(project?.Settings.Android, project?.Folder.AssetsRoot);
        var iconConfigured = AndroidAppSettingsHasIcon(project);

        // ── プラットフォーム機能（W1-2。設定の誤り・機能の表が読めないことは「プラットフォーム機能の権限」の不合格として出す）──
        var (platformFeatures, featureCatalog, featureProblem) = ResolvePlatformFeatures(project, log);

        // ── 署名（決まらない・開けないことも一覧に出す）──
        AndroidSigningConfig? signing = null;
        AndroidKeystoreCertificate? certificate = null;
        string? signingProblem = null;
        try
        {
            signing = AndroidSigningResolver.Resolve(AndroidRunPipeline.SigningInputs(request, project));
            certificate = await AndroidKeystoreTool.ReadAsync(
                toolchain.RequireKeytool(), signing.KeystorePath, signing.KeyAlias, signing.Secrets, cancellationToken).ConfigureAwait(false);
            log?.Invoke($"署名: {signing.Describe()}・{certificate.Describe()}");
        }
        catch (AndroidPipelineException ex)
        {
            signing = null;
            signingProblem = ex.Message;
        }

        // ── ビルドの前の判定 ──
        var abis = ResolveAbis(request);
        var history = AndroidReleaseHistory.Load(AndroidReleaseHistory.PathFor(project, engine));
        var facts = new AndroidPreBuildFacts
        {
            Format = request.Format,
            TargetApiLevel = AndroidRuntimeContract.TargetApiLevel,
            Abis = abis,
            Identity = identity,
            PreviousRelease = history.Last(identity.ApplicationId, request.Format),
            Signing = signing,
            SigningProblem = signingProblem,
            LauncherIcon = iconConfigured && iconErrors.Count == 0,
            PlatformFeatures = platformFeatures,
        };
        foreach (var item in AndroidRequirementChecks.Evaluate(requirements, facts)) report.Put(item);
        if (iconErrors.Count > 0)
        {
            report.Put(new AndroidRequirementItem(AndroidRequirementIds.Icon, "アイコン", AndroidRequirementSeverity.Failure, string.Join(" ", iconErrors)));
        }
        if (featureProblem is not null)
        {
            report.Put(new AndroidRequirementItem(
                AndroidRequirementIds.PlatformFeatures, AndroidPlatformFeatureChecks.Title, AndroidRequirementSeverity.Failure, featureProblem));
        }

        // ── 配布物（あれば）──
        var artifact = artifactPath is not null
            ? Path.GetFullPath(artifactPath)
            : engine.ArtifactPath(AndroidBuildVariant.Release, request.Format);
        if (!File.Exists(artifact))
        {
            report.Put(new AndroidRequirementItem(AndroidRequirementIds.ArtifactIdentity, "配布物", AndroidRequirementSeverity.Info,
                $"配布物がまだありません（{artifact}）。build で作ってから確かめると、中身（debuggable・権限・16 KB・署名）も判定します。"));
            return new AndroidRequirementsCheckResult(report, null);
        }
        var format = FormatOf(artifact, request.Format);
        log?.Invoke($"配布物を読み直します: {artifact}");
        var artifactFacts = await AndroidArtifactInspector.InspectAsync(toolchain, artifact, format, requirements, cancellationToken).ConfigureAwait(false);
        var expectation = new AndroidArtifactExpectation(identity, SignerCertificateParser.NormalizeFingerprint(certificate?.Sha256))
        {
            // 機能の設定に誤りがあれば配布物との比べはしない（ビルドの前の不合格を残す）
            PlatformFeatures = platformFeatures ?? AndroidPlatformFeatureSet.Empty,
            FeatureCatalog = featureProblem is null ? featureCatalog : null,
        };
        foreach (var item in AndroidArtifactChecks.Evaluate(requirements, artifactFacts, expectation)) report.Put(item);
        return new AndroidRequirementsCheckResult(report, artifact);
    }

    /// <summary>
    /// プラットフォーム機能を決める（W1-2。注意はログへ。設定の誤り・機能の表が読めないことは例外にせず理由として返す）。
    /// </summary>
    /// <param name="project">プロジェクト（無ければ null）。</param>
    /// <param name="log">経過の 1 行（null なら出さない）。</param>
    /// <returns>機能（誤りがあれば null）・機能の表（読めなければ null）・誤りの説明（無ければ null）。</returns>
    private static (AndroidPlatformFeatureSet? Features, AndroidPlatformFeatureCatalog? Catalog, string? Problem) ResolvePlatformFeatures(
        AndroidProjectInfo? project, Action<string>? log)
    {
        AndroidPlatformFeatureCatalog catalog;
        try
        {
            catalog = AndroidPlatformFeatureBuildInput.LoadCatalog();
        }
        catch (AndroidPipelineException ex)
        {
            return (null, null, ex.Message);
        }
        var features = AndroidPlatformFeatureResolver.Resolve(project?.Settings.Android, catalog);
        foreach (var warning in features.Warnings) log?.Invoke($"注意: {warning}");
        log?.Invoke($"プラットフォーム機能: {features.Describe()}");
        return features.Errors.Count > 0
            ? (null, catalog, "プロジェクト設定のプラットフォーム機能に誤りがあります: " + string.Join(" ", features.Errors))
            : (features, catalog, null);
    }

    /// <summary>ABI（指定 → 配布用の既定 arm64-v8a）。</summary>
    private static IReadOnlyList<AndroidAbi> ResolveAbis(AndroidRunRequest request)
    {
        if (request.Abis is null) return new[] { AndroidAbis.Arm64 };
        var abis = AndroidAbis.ParseList(string.Join(AndroidAbis.ListSeparator, request.Abis), out var error);
        if (error is not null) throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest, error);
        return abis;
    }

    /// <summary>配布物の形式（拡張子 .aab なら AAB、.apk なら APK、それ以外は指定のまま）。</summary>
    /// <param name="path">配布物。</param>
    /// <param name="requested">指定の形式。</param>
    /// <returns>形式。</returns>
    public static AndroidPackageFormat FormatOf(string path, AndroidPackageFormat requested) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".aab" => AndroidPackageFormat.Aab,
            ".apk" => AndroidPackageFormat.Apk,
            _ => requested,
        };

    /// <summary>プロジェクト設定にアイコンが書かれているか。</summary>
    private static bool AndroidAppSettingsHasIcon(AndroidProjectInfo? project) =>
        !string.IsNullOrWhiteSpace(project?.Settings.Android?.Icon);
}
