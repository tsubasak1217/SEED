// ============================================================
//  PushAssetsStep.cs — 開発用: アセットフォルダを端末のアプリの内部フォルダ（files/assets）へ送る
//
//  run-as でアプリの権限になり、tar のストリームを展開する（前回分は消して置き直す。Adb/RunAsTarArchive.cs）。
//  APK を作り直さずにアセットだけを差し替える高速経路（デバッグ版の APK だけ）。端末の launch.rs は、APK に pak が
//  無ければこの置き場から読む（APK に pak があれば pak が優先され、ここは「pak に無いもの」だけに使われる）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;

namespace SEEDEditor.Android.Steps;

/// <summary>開発用のアセットの転送。</summary>
public sealed class PushAssetsStep : IAndroidPipelineStep
{
    /// <summary>1 MiB（大きさの表示用）。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <inheritdoc />
    public AndroidPipelinePhase Phase => AndroidPipelinePhase.PushAssets;

    /// <inheritdoc />
    public async Task<string> RunAsync(
        AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var adb = context.RequireAdb();
        var device = context.RequireDevice();
        var assetsDir = context.Project?.Folder.AssetsRoot
                        ?? throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest, "送るアセットフォルダが決まっていません。");
        if (!File.Exists(Path.Combine(assetsDir, AndroidRuntimeContract.AssetsCheckFileName)))
        {
            throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest,
                $"アセットフォルダにはプロジェクトの assets/（{AndroidRuntimeContract.AssetsCheckFileName} を含むフォルダ）を指定してください: {assetsDir}");
        }

        log.Info($"{assetsDir} -> (run-as {context.Identity.ApplicationId}) {AndroidRuntimeContract.RemoteAssetsDir}");
        var written = (Files: 0, Bytes: 0L);
        try
        {
            await adb.RunAsExtractTarAsync(
                device.Serial, context.Identity.ApplicationId, AndroidRuntimeContract.RemoteAssetsDir, AndroidRuntimeContract.AssetsCheckFileName,
                async (stream, token) => written = await RunAsTarArchive.WriteDirectoryAsync(assetsDir, stream, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (AdbCommandException ex)
        {
            throw new AndroidPipelineException(AndroidFailureKind.DeviceOperation, $"アセットの転送が失敗しました: {ex.Message}", ex);
        }
        return $"{written.Files} ファイル・{written.Bytes / BytesPerMegabyte:F1} MB を送りました";
    }
}
