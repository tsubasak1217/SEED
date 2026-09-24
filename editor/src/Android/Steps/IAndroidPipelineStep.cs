// ============================================================
//  IAndroidPipelineStep.cs — 工程 1 つの実装の形
//
//  各工程は「計画で行うと決まったとき」だけ呼ばれる（飛ばす判断は Plan/AndroidBuildPlan.cs が済ませている）。
//  失敗は AndroidPipelineException（種類付き）で知らせ、成功したら結果の一行説明を返す。
//  中断（CancellationToken）では子プロセスを止めて OperationCanceledException を投げる。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;

namespace SEEDEditor.Android.Steps;

/// <summary>工程 1 つ。</summary>
public interface IAndroidPipelineStep
{
    /// <summary>この実装が受け持つ工程。</summary>
    AndroidPipelinePhase Phase { get; }

    /// <summary>
    /// 工程を行う。
    /// </summary>
    /// <param name="context">共有の値。</param>
    /// <param name="decision">計画の判断（対象の ABI・理由）。</param>
    /// <param name="log">この工程のログ。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>結果の一行説明。</returns>
    Task<string> RunAsync(
        AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log, CancellationToken cancellationToken);
}
