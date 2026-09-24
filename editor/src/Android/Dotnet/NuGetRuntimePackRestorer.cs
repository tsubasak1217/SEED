// ============================================================
//  NuGetRuntimePackRestorer.cs — 同梱 .NET のランタイムパックを NuGet から取り寄せる
//
//  【やり方（従来の build_and_run.ps1 の Restore-DotnetRuntimePacks と同じ）】
//  NuGet のグローバルパッケージフォルダ（既定 ~/.nuget/packages。NUGET_PACKAGES があればそちら。
//  無ければ `dotnet nuget locals global-packages --list` で聞く）に展開済みか（.nupkg.metadata の有無）を見て、
//  足りないものだけを「PackageDownload だけを持つ一時プロジェクト」の `dotnet restore` で取り寄せる（ビルドはしない）。
//  初回は 1 パック 25〜190 MB。以後はキャッシュを使う。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Processes;

namespace SEEDEditor.Android.Dotnet;

/// <summary>同梱 .NET のランタイムパックの取り寄せ。</summary>
public static class NuGetRuntimePackRestorer
{
    /// <summary>NuGet が展開を終えたパックに置く印（これがあれば取り寄せ済み）。</summary>
    public const string CompletionMarker = ".nupkg.metadata";

    /// <summary>グローバルパッケージフォルダを上書きする環境変数。</summary>
    private const string PackagesRootVariable = "NUGET_PACKAGES";

    /// <summary>`dotnet nuget locals global-packages --list` の出力の区切り（「global-packages: C:\…\」）。</summary>
    private const char LocalsSeparator = ':';

    /// <summary>取り寄せだけを行う一時プロジェクトのファイル名。</summary>
    private const string RestoreProjectFileName = "SeedDotnetRuntimePacks.csproj";

    /// <summary>
    /// NuGet のグローバルパッケージフォルダを調べる。
    /// </summary>
    /// <param name="dotnet">dotnet.exe。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>フォルダの絶対パス。</returns>
    public static async Task<string> FindPackagesRootAsync(string dotnet, CancellationToken cancellationToken)
    {
        var fromEnv = Environment.GetEnvironmentVariable(PackagesRootVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        var capture = await ChildProcessRunner.CaptureAsync(new ChildProcessSpec
        {
            FileName  = dotnet,
            Arguments = new[] { "nuget", "locals", "global-packages", "--list" },
        }, cancellationToken).ConfigureAwait(false);
        var line = capture.StandardOutput.FirstOrDefault(l => l.Contains(LocalsSeparator)) ?? string.Empty;
        if (capture.ExitCode != 0 || line.Length == 0)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build,
                $"NuGet のグローバルパッケージフォルダが分かりません（dotnet nuget locals global-packages --list: {capture.AllOutputText}）。");
        }
        // 「global-packages: C:\Users\...\.nuget\packages\」のキーの後ろがフォルダ（ドライブ名の : より前で切る）
        return Path.TrimEndingDirectorySeparator(line[(line.IndexOf(LocalsSeparator) + 1)..].Trim());
    }

    /// <summary>パック名 → 展開先のフォルダ（&lt;キャッシュ&gt;/&lt;小文字の名前&gt;/&lt;版&gt;）。</summary>
    /// <param name="packagesRoot">グローバルパッケージフォルダ。</param>
    /// <param name="packIds">パック名。</param>
    /// <param name="version">版。</param>
    /// <returns>パック名 → フォルダ。</returns>
    public static IReadOnlyDictionary<string, string> PackFolders(string packagesRoot, IEnumerable<string> packIds, string version) =>
        packIds.Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => Path.Combine(packagesRoot, id.ToLowerInvariant(), version), StringComparer.Ordinal);

    /// <summary>
    /// 取り寄せだけを行う一時プロジェクトの中身を作る（純粋な処理）。
    /// </summary>
    /// <param name="packIds">取り寄せるパック名。</param>
    /// <param name="version">版（ちょうどこの版だけを取る）。</param>
    /// <param name="targetFramework">TFM（net10.0）。</param>
    /// <returns>csproj の中身。</returns>
    public static string CreateRestoreProject(IEnumerable<string> packIds, string version, string targetFramework)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        builder.AppendLine("  <!-- SeedAndroid（editor/src/Android/Dotnet）が生成する。同梱 .NET のランタイムパックを NuGet から取り寄せるだけ（ビルドはしない） -->");
        builder.AppendLine("  <PropertyGroup>");
        builder.AppendLine($"    <TargetFramework>{SecurityElement.Escape(targetFramework)}</TargetFramework>");
        builder.AppendLine("  </PropertyGroup>");
        builder.AppendLine("  <ItemGroup>");
        foreach (var id in packIds.Distinct(StringComparer.Ordinal))
        {
            builder.AppendLine($"    <PackageDownload Include=\"{SecurityElement.Escape(id)}\" Version=\"[{SecurityElement.Escape(version)}]\" />");
        }
        builder.AppendLine("  </ItemGroup>");
        builder.AppendLine("</Project>");
        return builder.ToString();
    }

    /// <summary>
    /// パックを取り寄せる（キャッシュに無いものだけ）。
    /// </summary>
    /// <param name="dotnet">dotnet.exe。</param>
    /// <param name="workDir">一時プロジェクトを置くフォルダ。</param>
    /// <param name="packIds">パック名。</param>
    /// <param name="version">版。</param>
    /// <param name="targetFramework">TFM。</param>
    /// <param name="log">説明の 1 行ずつ。</param>
    /// <param name="onProcessLine">dotnet restore の出力 1 行ずつ。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>パック名 → 展開済みのフォルダ。</returns>
    public static async Task<IReadOnlyDictionary<string, string>> RestoreAsync(
        string dotnet,
        string workDir,
        IReadOnlyCollection<string> packIds,
        string version,
        string targetFramework,
        Action<string> log,
        Action<ChildProcessStream, string>? onProcessLine,
        CancellationToken cancellationToken)
    {
        var root = await FindPackagesRootAsync(dotnet, cancellationToken).ConfigureAwait(false);
        var folders = PackFolders(root, packIds, version);
        var missing = folders.Where(pair => !File.Exists(Path.Combine(pair.Value, CompletionMarker))).Select(pair => pair.Key).ToList();
        if (missing.Count == 0)
        {
            log($"パックは取り寄せ済み（{root}）: {string.Join(", ", folders.Keys)}");
            return folders;
        }

        log($"NuGet から取り寄せます（初回は 1 パック 25〜190 MB）: {string.Join(", ", missing)}");
        Directory.CreateDirectory(workDir);
        var projectPath = Path.Combine(workDir, RestoreProjectFileName);
        await File.WriteAllTextAsync(projectPath, CreateRestoreProject(missing, version, targetFramework), new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
        var exitCode = await ChildProcessRunner.RunAsync(new ChildProcessSpec
        {
            FileName  = dotnet,
            Arguments = new[] { "restore", projectPath, "--nologo" },
        }, onProcessLine, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"同梱 .NET のパックを取り寄せられませんでした（dotnet restore の終了コード {exitCode}）。");
        }
        foreach (var id in missing)
        {
            if (!File.Exists(Path.Combine(folders[id], CompletionMarker)))
            {
                throw new AndroidPipelineException(AndroidFailureKind.Build, $"取り寄せたはずのパックがありません: {folders[id]}");
            }
        }
        return folders;
    }
}
