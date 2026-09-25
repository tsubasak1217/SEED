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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Processes;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Android.Steps;

/// <summary>SeedPak の呼び出し。</summary>
public static class SeedPakProcess
{
    // ── SeedPak の引数の名前（正典は editor/tools/SeedPak/SeedPakArguments.cs。SeedAndroid はそのファイルをリンクしないので写す）──

    /// <summary>プロジェクトフォルダ。</summary>
    public const string ProjectOption = "--project";

    /// <summary>出力フォルダ。</summary>
    public const string OutOption = "--out";

    /// <summary>assets.pak に加えて bin/（スクリプト）も作る。</summary>
    public const string ScriptsOption = "--scripts";

    /// <summary>登録シーンに加えて収録の起点にするシーン（段階C-4。繰り返し指定できる）。</summary>
    public const string ExtraSceneOption = "--extra-scene";

    /// <summary>bin/（スクリプトの DLL）だけを作る（pak は作らない。push・実行中の差し替え）。</summary>
    public const string ScriptsOnlyOption = "--scripts-only";

    /// <summary>アセットフォルダ（プロジェクトの代わりにアセットルートを直接指定する）。</summary>
    public const string AssetsOption = "--assets";

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
    public static Task RunAsync(
        AndroidPipelineContext context, IReadOnlyList<string> arguments, AndroidPhaseLog log, CancellationToken cancellationToken) =>
        RunAsync(context.Toolchain.RequireDotnet(), context.Engine, arguments, log.Info, log.Process, cancellationToken);

    /// <summary>
    /// SeedPak を動かす（パイプラインの外から使う形。実行中の差し替えでスクリプトの DLL を作り直す。docs/android.md §23）。
    /// </summary>
    /// <param name="dotnet">dotnet の実行ファイル。</param>
    /// <param name="engine">エンジン側の置き場（SeedPak の場所・作業フォルダ）。</param>
    /// <param name="arguments">SeedPak の引数。</param>
    /// <param name="info">説明の 1 行の出し先（呼び出したコマンド）。</param>
    /// <param name="onLine">子プロセスの出力の 1 行ごとの出し先。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <exception cref="AndroidPipelineException">失敗したとき。</exception>
    public static async Task RunAsync(
        string dotnet,
        AndroidEnginePaths engine,
        IReadOnlyList<string> arguments,
        Action<string> info,
        Action<ChildProcessStream, string> onLine,
        CancellationToken cancellationToken)
    {
        var all = new List<string> { "run", "--project", engine.SeedPakProjectDir, "--" };
        all.AddRange(arguments);
        var spec = new ChildProcessSpec
        {
            FileName         = dotnet,
            Arguments        = all,
            WorkingDirectory = engine.RepositoryRoot,
            Environment      = new Dictionary<string, string?> { [NoLogoVariable] = "1" },
        };
        info("SeedPak " + string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        var exitCode = await ChildProcessRunner.RunAsync(spec, onLine, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            var detail = exitCode == ScriptsFailedExitCode ? "スクリプトのコンパイル・同梱に失敗しました" : "SeedPak が失敗しました";
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"{detail}（SeedPak の終了コード {exitCode}。上の出力を確認してください）。");
        }
    }
}
