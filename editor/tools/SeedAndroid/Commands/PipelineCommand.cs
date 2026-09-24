// ============================================================
//  PipelineCommand.cs — build / install / run / push（中核の AndroidRunPipeline を動かす）
// ============================================================

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Tools.SeedAndroid.Commands;

/// <summary>build / install / run / push。</summary>
public static class PipelineCommand
{
    /// <summary>
    /// 中核のビルド・配置・起動を動かし、結果の要約を書く。
    /// </summary>
    /// <param name="toolchain">道具の場所。</param>
    /// <param name="line">コマンドラインの指定。</param>
    /// <param name="config">設定 JSON の値（無ければ null）。</param>
    /// <param name="cancellationToken">中断の合図（Ctrl+C）。</param>
    /// <returns>終了コード。</returns>
    public static async Task<int> RunAsync(
        AndroidToolchain toolchain, SeedAndroidCommandLine line, AndroidRunRequest? config, CancellationToken cancellationToken)
    {
        var engine = AndroidEnginePaths.Locate(AppContext.BaseDirectory, Environment.CurrentDirectory);
        if (engine is null)
        {
            Console.Error.WriteLine("エラー: SEED のリポジトリ（runtime/Cargo.toml と runtime/android/gradlew.bat）が見つかりません。リポジトリの中で実行してください。");
            return SeedAndroidExitCodes.Toolchain;
        }

        var request = SeedAndroidArguments.ToRequest(line, config);
        var pipeline = new AndroidRunPipeline(engine, toolchain);
        var result = await pipeline.RunAsync(request, new ConsoleEventPrinter(), cancellationToken);

        // ── 要約 ──
        Console.Out.WriteLine();
        var ran = result.Steps.Count(step => step.Phase != AndroidPipelinePhase.Prepare && step.Outcome == AndroidPhaseOutcome.Succeeded);
        var skipped = result.Steps.Count(step => step.Outcome == AndroidPhaseOutcome.Skipped);
        var head = result.Succeeded ? "完了" : result.Canceled ? "中断" : "失敗";
        Console.Out.WriteLine($"{head}: {ran} 工程を行い {skipped} 工程を飛ばしました（{result.Elapsed.TotalSeconds:F1} 秒）" +
                              (result.Identity is null ? string.Empty : $"  アプリ {result.Identity.ApplicationId}") +
                              (result.Device is null ? string.Empty : $"  端末 {result.Device.DisplayName}"));
        foreach (var step in result.Steps.Where(step => step.Phase != AndroidPipelinePhase.Prepare))
        {
            Console.Out.WriteLine($"  {AndroidPipelinePhaseNames.Title(step.Phase),-36} {Label(step.Outcome),-6} {step.Elapsed.TotalSeconds,6:F1} 秒  {step.Summary}");
        }

        if (result.Succeeded) return SeedAndroidExitCodes.Success;
        if (result.Canceled) return SeedAndroidExitCodes.Canceled;
        return SeedAndroidExitCodes.For(result.FailureKind ?? AndroidFailureKind.Build);
    }

    /// <summary>終わり方の表示名。</summary>
    private static string Label(AndroidPhaseOutcome outcome) => outcome switch
    {
        AndroidPhaseOutcome.Succeeded => "行った",
        AndroidPhaseOutcome.Skipped   => "飛ばした",
        AndroidPhaseOutcome.Canceled  => "中断",
        _                             => "失敗",
    };
}
