// ============================================================
//  CheckCommand.cs — check（配布物とプロジェクトの設定を Google Play の要件で確かめる。ビルドはしない。段階D。docs/android.md §24）
//
//  中身は中核の Release/AndroidRequirementsCheckRunner（パッケージ化ウィンドウの「要件を確認」と共通）。
//  確かめる配布物は --artifact、無ければ Gradle の出力の前回の配布用ビルド（--format の形式）。
//  署名の鍵を確かめるためにパスワードが要る（環境変数、無ければ対話で聞く。聞けなければ「署名」を不合格として出す）。
//  不合格があれば終了コード 6。
// ============================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Release;
using SEEDEditor.Android.Signing;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Tools.SeedAndroid.Commands;

/// <summary>check。</summary>
public static class CheckCommand
{
    /// <summary>
    /// 要件を確かめて一覧を書く。
    /// </summary>
    /// <param name="toolchain">道具の場所。</param>
    /// <param name="line">コマンドラインの指定。</param>
    /// <param name="config">設定 JSON の値（無ければ null）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
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
        request = request with { SigningSecrets = ConsoleSecretPrompt.AskIfNeeded(request.KeystorePath) };
        var result = await AndroidRequirementsCheckRunner.RunAsync(
            engine, toolchain, request, line.ArtifactPath, message => Console.Out.WriteLine(message), cancellationToken);

        Console.Out.WriteLine();
        Console.Out.WriteLine($"Google Play の要件（{result.Report.Summary()}）{(result.ArtifactPath is null ? string.Empty : $"  配布物 {result.ArtifactPath}")}:");
        foreach (var item in result.Report.Items) Write(item);
        return result.Report.HasFailures ? SeedAndroidExitCodes.RequirementsNotMet : SeedAndroidExitCodes.Success;
    }

    /// <summary>判定 1 件を書く（不合格は赤・注意は黄）。</summary>
    /// <param name="item">判定。</param>
    public static void Write(AndroidRequirementItem item)
    {
        var previous = Console.ForegroundColor;
        var color = item.Severity switch
        {
            AndroidRequirementSeverity.Failure => ConsoleColor.Red,
            AndroidRequirementSeverity.Warning => ConsoleColor.Yellow,
            _ => previous,
        };
        if (!Console.IsOutputRedirected) Console.ForegroundColor = color;
        Console.Out.WriteLine("  " + item.Describe());
        if (!Console.IsOutputRedirected) Console.ForegroundColor = previous;
    }
}
