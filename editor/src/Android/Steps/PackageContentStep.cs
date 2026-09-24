// ============================================================
//  PackageContentStep.cs — APK に入れる配布物（app/src/main/assets/seed/）を作り直す
//
//    --project あり … SeedPak --scripts で assets.pak とスクリプトの bin/ を作って置く（パッケージ実行の APK）
//    それ以外       … 置き場を空にする（pak の無い開発用の APK。アセットは run-as で送る）
//  置き場に前回の pak が残ったままだと、端末はそれで起動して run-as で送ったアセットを読まなくなるため、
//  Gradle を回す前に必ず今回の指定どおりの状態へ作り直す（従来の build_and_run.ps1 の Update-ApkPackage と同じ）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Project;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Steps;

/// <summary>APK に入れる配布物の作成。</summary>
public sealed class PackageContentStep : IAndroidPipelineStep
{
    /// <summary>1 MiB（大きさの表示用）。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <inheritdoc />
    public AndroidPipelinePhase Phase => AndroidPipelinePhase.PackageContent;

    /// <inheritdoc />
    public async Task<string> RunAsync(
        AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var packageDir = context.Engine.ApkPackageDir;
        if (Directory.Exists(packageDir)) Directory.Delete(packageDir, recursive: true);

        string summary;
        if (context.Project is { Mode: AndroidProjectMode.Packaged } project)
        {
            await SeedPakProcess.RunAsync(
                context, new[] { "--project", project.SourceArgument, "--out", packageDir, "--scripts" }, log, cancellationToken)
                .ConfigureAwait(false);
            var pak = Path.Combine(packageDir, PackageLayout.PakFileName);
            if (!File.Exists(pak))
            {
                throw new AndroidPipelineException(AndroidFailureKind.Build, $"SeedPak の出力に {PackageLayout.PakFileName} がありません: {pak}");
            }
            var binFiles = Directory.Exists(Path.Combine(packageDir, PackageLayout.BinDirName))
                ? Directory.GetFiles(Path.Combine(packageDir, PackageLayout.BinDirName)).Select(path => new FileInfo(path)).ToList()
                : new System.Collections.Generic.List<FileInfo>();
            summary = $"{PackageLayout.PakFileName} {new FileInfo(pak).Length / BytesPerMegabyte:F1} MB・" +
                      $"{PackageLayout.BinDirName}/ {binFiles.Count} ファイル {binFiles.Sum(f => f.Length) / BytesPerMegabyte:F1} MB";
        }
        else
        {
            summary = "APK に pak とスクリプトを入れません（開発用。アセットは --assets-dir、スクリプトは --push-scripts で送る）";
        }

        context.Stamps.Steps[AndroidStepKeys.PackageContent] = new AndroidStepFingerprint(
            context.CurrentFingerprints[AndroidStepKeys.PackageContent].Inputs, AndroidOutputIdentity.OfDirectory(packageDir));
        return summary;
    }
}
