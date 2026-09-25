// ============================================================
//  ReleaseCheckStep.cs — 配布用（release）の APK / AAB を Google Play の要件で確かめる（段階D。docs/android.md §24）
//
//  できた（または変更が無く前回のままの）配布物を道具で読み直し（Release/AndroidArtifactInspector）、判定して
//  （Release/AndroidArtifactChecks）、準備で出したビルドの前の判定の一覧の同じ項目を上書きする。一覧はログへ出し、
//  中核の結果（AndroidPipelineResult.RequirementReport）でエディタ・SeedAndroid へ渡す。
//  不合格があっても工程は成功にする（配布物はできている。手元で試すことがあるため）。不合格の扱いは呼び出し側
//  （SeedAndroid は終了コード 6、パッケージ化ウィンドウは赤）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Release;

namespace SEEDEditor.Android.Steps;

/// <summary>Google Play の要件の確認。</summary>
public sealed class ReleaseCheckStep : IAndroidPipelineStep
{
    /// <inheritdoc />
    public AndroidPipelinePhase Phase => AndroidPipelinePhase.ReleaseCheck;

    /// <inheritdoc />
    public async Task<string> RunAsync(
        AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var report = context.RequirementReport ?? new AndroidRequirementReport();
        if (context.PlayRequirements is not { } requirements)
        {
            // 表が読めなければ、準備で「判定できない」の不合格を入れてある
            return $"要件の表を読めないため判定できません（{report.Summary()}）";
        }

        log.Info($"{context.ArtifactPath} を読み直して確かめます（aapt2・zipalign・apksigner / keytool・.so の ELF）");
        var facts = await AndroidArtifactInspector.InspectAsync(
            context.Toolchain, context.ArtifactPath, context.Request.Format, requirements, cancellationToken).ConfigureAwait(false);
        var expectation = new AndroidArtifactExpectation(
            context.Identity, SignerCertificateParser.NormalizeFingerprint(context.SigningCertificate?.Sha256));
        foreach (var item in AndroidArtifactChecks.Evaluate(requirements, facts, expectation)) report.Put(item);

        log.Info($"Google Play の要件（{report.Summary()}）:");
        foreach (var item in report.Items) AndroidRunPipeline.LogRequirement(log, item);
        return report.HasFailures
            ? $"不合格があります（{report.Summary()}）。上の一覧の直し方を確かめてください"
            : $"不合格なし（{report.Summary()}）";
    }
}
