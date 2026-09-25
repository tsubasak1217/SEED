// ============================================================
//  ScriptBinaryPusher.cs — スクリプトの DLL を作り直して、端末の files/bin/ へ送る（push と実行中の差し替えで共有）
//
//  【作る】SeedPak --scripts-only（プロジェクトか開発用のアセットフォルダの .cs を事前コンパイルし、APK に入れるものと同じ bin/ を作る）
//  【送る】bin/ の DLL と runtimeconfig（AndroidRuntimeContract.ScriptBinaryPatterns）を run-as で files/bin/ へ（前回分は消して置き直す）
//  端末は files/bin/ を APK の bin/ より優先して読む（起動時は runtime/android/native の script_sources.rs、実行中の読み直し＝
//  RELOAD_SCRIPTS はエンジンの scripting/script_reload.rs）。
//  使う側: Steps/PushScriptsStep（push。送った後に起動し直す）・HotReload/AndroidHotReloadApplier（実行中の差し替え。
//  送った後に RELOAD_SCRIPTS）。以前は PushScriptsStep の中にあった処理（振る舞いは同じ）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Processes;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Steps;

/// <summary>スクリプトの DLL の作り直しと転送。</summary>
public static class ScriptBinaryPusher
{
    /// <summary>1 MiB（大きさの表示用）。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <summary>
    /// SeedPak --scripts-only で bin/ を作り直し、送るファイルを返す（出力先は毎回作り直す）。
    /// </summary>
    /// <param name="dotnet">dotnet の実行ファイル。</param>
    /// <param name="engine">エンジン側の置き場（SeedPak・出力先）。</param>
    /// <param name="project">プロジェクト（.cs の出どころ）。</param>
    /// <param name="info">説明の 1 行の出し先。</param>
    /// <param name="onLine">SeedPak の出力の 1 行ごとの出し先。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>送るファイル（名前順）。</returns>
    /// <exception cref="AndroidPipelineException">SeedPak が失敗した・送るファイルが無い。</exception>
    public static async Task<IReadOnlyList<FileInfo>> BuildAsync(
        string dotnet,
        AndroidEnginePaths engine,
        AndroidProjectInfo project,
        Action<string> info,
        Action<ChildProcessStream, string> onLine,
        CancellationToken cancellationToken)
    {
        var staging = engine.PushScriptsStagingDir;
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        var source = project.Mode == AndroidProjectMode.Packaged
            ? new[] { SeedPakProcess.ProjectOption, project.SourceArgument }
            : new[] { SeedPakProcess.AssetsOption, project.Folder.AssetsRoot };
        var arguments = source.Concat(new[] { SeedPakProcess.OutOption, staging, SeedPakProcess.ScriptsOnlyOption }).ToArray();
        await SeedPakProcess.RunAsync(dotnet, engine, arguments, info, onLine, cancellationToken).ConfigureAwait(false);

        var binDir = Path.Combine(staging, PackageLayout.BinDirName);
        var files = AndroidRuntimeContract.ScriptBinaryPatterns
            .SelectMany(pattern => Directory.Exists(binDir) ? new DirectoryInfo(binDir).GetFiles(pattern) : Array.Empty<FileInfo>())
            .GroupBy(file => file.Name).Select(group => group.First())
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"SeedPak の出力に送るファイルがありません: {binDir}");
        }
        return files;
    }

    /// <summary>
    /// ファイルを run-as で端末の files/bin/ へ送る（前回分は消して置き直す）。
    /// </summary>
    /// <param name="adb">adb。</param>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="files">送るファイル（BuildAsync の結果）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>1 行の結果（件数・大きさ・名前）。</returns>
    /// <exception cref="AndroidPipelineException">転送できなかった。</exception>
    public static async Task<string> PushAsync(
        AdbClient adb, string serial, string applicationId, IReadOnlyList<FileInfo> files, CancellationToken cancellationToken)
    {
        try
        {
            await adb.RunAsExtractTarAsync(
                serial, applicationId, AndroidRuntimeContract.RemoteScriptsDir, AndroidRuntimeContract.ScriptsCheckFileName,
                (stream, token) => RunAsTarArchive.WriteFilesAsync(files, stream, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (AdbCommandException ex)
        {
            throw new AndroidPipelineException(AndroidFailureKind.DeviceOperation, $"スクリプトの DLL の転送が失敗しました: {ex.Message}", ex);
        }
        return $"{files.Count} ファイル・{files.Sum(f => f.Length) / BytesPerMegabyte:F1} MB を送りました（{string.Join(", ", files.Select(f => f.Name))}）";
    }
}
