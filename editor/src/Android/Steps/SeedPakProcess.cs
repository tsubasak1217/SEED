// ============================================================
//  SeedPakProcess.cs — SeedPak（editor/tools/SeedPak）を子プロセスで呼ぶ
//
//  pak の収録規則・パス書き換え・スクリプトの事前コンパイルは、パッケージ化ウィンドウと同じコードを持つ SeedPak に任せる
//  （従来の build_and_run.ps1 と同じ呼び方: dotnet run --project editor/tools/SeedPak -- …）。
//  dotnet run は SeedPak と scripting/ のビルドを確かめてから動かすので、同梱するスクリプトホストが常に最新になる。
//  エディタに組み込む段階C-2 では、同じプロセスの ScriptPackager を直接呼ぶ形に替える余地がある（docs/backlog.md）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Processes;

namespace SEEDEditor.Android.Steps;

/// <summary>SeedPak の呼び出し。</summary>
public static class SeedPakProcess
{
    /// <summary>SeedPak の終了コード: スクリプトのコンパイル・同梱の失敗（editor/tools/SeedPak/Program.cs）。</summary>
    private const int ScriptsFailedExitCode = 4;

    /// <summary>dotnet の初回の案内（ようこそ表示）を出さない環境変数。</summary>
    private const string NoLogoVariable = "DOTNET_NOLOGO";

    /// <summary>
    /// SeedPak を動かす。
    /// </summary>
    /// <param name="context">共有の値（dotnet と SeedPak の場所）。</param>
    /// <param name="arguments">SeedPak の引数（--project … --out … --scripts 等）。</param>
    /// <param name="log">この工程のログ。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <exception cref="AndroidPipelineException">失敗したとき。</exception>
    public static async Task RunAsync(
        AndroidPipelineContext context, IReadOnlyList<string> arguments, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var dotnet = context.Toolchain.RequireDotnet();
        var all = new List<string> { "run", "--project", context.Engine.SeedPakProjectDir, "--" };
        all.AddRange(arguments);
        var spec = new ChildProcessSpec
        {
            FileName         = dotnet,
            Arguments        = all,
            WorkingDirectory = context.Engine.RepositoryRoot,
            Environment      = new Dictionary<string, string?> { [NoLogoVariable] = "1" },
        };
        log.Info("SeedPak " + string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        var exitCode = await ChildProcessRunner.RunAsync(spec, log.Process, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            var detail = exitCode == ScriptsFailedExitCode ? "スクリプトのコンパイル・同梱に失敗しました" : "SeedPak が失敗しました";
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"{detail}（SeedPak の終了コード {exitCode}。上の出力を確認してください）。");
        }
    }
}
