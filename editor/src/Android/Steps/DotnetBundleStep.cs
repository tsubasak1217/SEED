// ============================================================
//  DotnetBundleStep.cs — APK に同梱する .NET を今回の ABI ぶん組み立てる（app/src/seedDotnet/）
//
//  runtime/android/dotnet_runtime.json の版・パックを NuGet から取り寄せ（キャッシュに無いものだけ）、
//  ABI ごとに dotnet-root 形式へ組み立てる（Dotnet/DotnetRuntimeBundle.cs）。今回の ABI に無い前回分は消す
//  （APK の assets は ABI で絞られないため）。ABI ごとの content_id が同じなら組み立てを省く。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Dotnet;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;

namespace SEEDEditor.Android.Steps;

/// <summary>同梱 .NET の組み立て。</summary>
public sealed class DotnetBundleStep : IAndroidPipelineStep
{
    /// <summary>1 MiB（大きさの表示用）。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <inheritdoc />
    public AndroidPipelinePhase Phase => AndroidPipelinePhase.DotnetBundle;

    /// <inheritdoc />
    public async Task<string> RunAsync(
        AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var staging = context.Engine.DotnetStagingDir;
        var settings = DotnetRuntimeSettings.Load(context.Engine.DotnetSettingsPath);
        var abis = context.Abis;
        log.Info($"{settings.Kind} {settings.Version}（{AndroidAbis.Describe(abis)}。設定 {context.Engine.DotnetSettingsPath}）");

        DotnetRuntimeBundle.RemoveOtherAbis(staging, abis, log.Info);

        // どの ABI も前回と同じ中身で、.jar も揃っていれば NuGet に問い合わせずに済ませる
        var libsDir = DotnetRuntimeBundle.JavaLibsDir(staging);
        var allUnchanged = abis.All(abi =>
            DotnetRuntimeBundle.ReadStagedContentId(staging, abi) == DotnetRuntimeBundle.ContentId(settings, abi)
            && Directory.Exists(Path.Combine(DotnetRuntimeBundle.JniLibsParentDir(staging), abi.Name)));
        var jarsReady = settings.Runtime.JavaLibraries.Count == 0
            ? !Directory.Exists(libsDir)
            : settings.Runtime.JavaLibraries.All(jar => File.Exists(Path.Combine(libsDir, jar)));

        string summary;
        if (allUnchanged && jarsReady)
        {
            summary = "変更なし（" + string.Join("・", abis.Select(abi => $"{abi.Name} content_id={DotnetRuntimeBundle.ContentId(settings, abi)}")) + "）";
            log.Info(summary);
        }
        else
        {
            var dotnet = context.Toolchain.RequireDotnet();
            var packIds = abis.SelectMany(abi => new[] { settings.RuntimePackId(abi), settings.HostPackId(abi) }).Distinct().ToList();
            var folders = await NuGetRuntimePackRestorer.RestoreAsync(
                dotnet, context.Engine.DotnetRestoreDir, packIds, settings.Version, settings.TargetFramework,
                log.Info, log.Process, cancellationToken).ConfigureAwait(false);

            var parts = new System.Collections.Generic.List<string>();
            foreach (var abi in abis)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = DotnetRuntimeBundle.AssembleAbi(settings, abi, folders, staging);
                var line = result.Unchanged
                    ? $"{abi.Name}: 変更なし（content_id={result.ContentId}）"
                    : $"{abi.Name}: asset {result.AssetFileCount} ファイル {result.AssetBytes / BytesPerMegabyte:F1} MB・" +
                      $".so {result.NativeFileCount} 個 {result.NativeBytes / BytesPerMegabyte:F1} MB（content_id={result.ContentId}）";
                log.Info(line);
                parts.Add(line);
            }
            foreach (var jar in DotnetRuntimeBundle.UpdateJavaLibraries(settings, abis[0], folders, staging))
            {
                log.Info($"Java: {Path.GetFileName(jar)} -> {libsDir}");
            }
            summary = string.Join(" / ", parts);
        }

        context.Stamps.Steps[AndroidStepKeys.DotnetBundle] = new AndroidStepFingerprint(
            context.CurrentFingerprints[AndroidStepKeys.DotnetBundle].Inputs, AndroidOutputIdentity.OfDirectory(staging));
        return summary;
    }
}
