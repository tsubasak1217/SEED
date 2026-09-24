// ============================================================
//  PushScriptsStep.cs — 開発用: スクリプトの DLL だけを作り直して端末の files/bin/ へ送る（APK は作り直さない）
//
//  プロジェクト（--project、無ければ --assets-dir のアセット）の .cs を SeedPak --scripts-only で事前コンパイルし
//  （APK に入れるものと同じ bin/）、DLL と runtimeconfig を run-as で files/bin/ へ送る（前回分は消して置き直す）。
//  端末は files/bin/ を APK の bin/ より優先して読む（runtime/android/native/src/dotnet_runtime/script_sources.rs）。
//  消せば APK の中のものへ戻る: adb exec-out run-as <アプリ ID> rm -rf files/bin
//  送った後の再起動（force-stop → am start）は起動の工程が行う。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Project;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Steps;

/// <summary>スクリプトの DLL の転送。</summary>
public sealed class PushScriptsStep : IAndroidPipelineStep
{
    /// <summary>1 MiB（大きさの表示用）。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <inheritdoc />
    public AndroidPipelinePhase Phase => AndroidPipelinePhase.PushScripts;

    /// <inheritdoc />
    public async Task<string> RunAsync(
        AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var adb = context.RequireAdb();
        var device = context.RequireDevice();
        var project = context.Project
                      ?? throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest,
                          "スクリプトの転送には、スクリプト（.cs）の出どころとしてプロジェクトかアセットフォルダを指定してください。");

        // SeedPak --scripts-only で bin/ だけを作る（出力先は毎回作り直す）
        var staging = context.Engine.PushScriptsStagingDir;
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        var source = project.Mode == AndroidProjectMode.Packaged
            ? new[] { "--project", project.SourceArgument }
            : new[] { "--assets", project.Folder.AssetsRoot };
        await SeedPakProcess.RunAsync(context, source.Concat(new[] { "--out", staging, "--scripts-only" }).ToArray(), log, cancellationToken)
            .ConfigureAwait(false);

        var binDir = Path.Combine(staging, PackageLayout.BinDirName);
        var files = AndroidRuntimeContract.ScriptBinaryPatterns
            .SelectMany(pattern => Directory.Exists(binDir) ? new DirectoryInfo(binDir).GetFiles(pattern) : System.Array.Empty<FileInfo>())
            .GroupBy(file => file.Name).Select(group => group.First())
            .OrderBy(file => file.Name, System.StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"SeedPak の出力に送るファイルがありません: {binDir}");
        }

        log.Info($"{binDir} -> (run-as {context.Identity.ApplicationId}) {AndroidRuntimeContract.RemoteScriptsDir}");
        try
        {
            await adb.RunAsExtractTarAsync(
                device.Serial, context.Identity.ApplicationId, AndroidRuntimeContract.RemoteScriptsDir, AndroidRuntimeContract.ScriptsCheckFileName,
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
