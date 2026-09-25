// ============================================================
//  PushScriptsStep.cs — 開発用: スクリプトの DLL だけを作り直して端末の files/bin/ へ送る（APK は作り直さない）
//
//  プロジェクト（--project、無ければ --assets-dir のアセット）の .cs を SeedPak --scripts-only で事前コンパイルし
//  （APK に入れるものと同じ bin/）、DLL と runtimeconfig を run-as で files/bin/ へ送る（前回分は消して置き直す）。
//  端末は files/bin/ を APK の bin/ より優先して読む（runtime/android/native/src/dotnet_runtime/script_sources.rs）。
//  消せば APK の中のものへ戻る: adb exec-out run-as <アプリ ID> rm -rf files/bin
//  送った後の再起動（force-stop → am start）は起動の工程が行う。
//  作り直しと転送の中身は ScriptBinaryPusher（実行中の差し替え〈RELOAD_SCRIPTS〉と共有。docs/android.md §23）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Steps;

/// <summary>スクリプトの DLL の転送。</summary>
public sealed class PushScriptsStep : IAndroidPipelineStep
{
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
        var files = await ScriptBinaryPusher.BuildAsync(
            context.Toolchain.RequireDotnet(), context.Engine, project, log.Info, log.Process, cancellationToken).ConfigureAwait(false);

        var binDir = Path.Combine(context.Engine.PushScriptsStagingDir, PackageLayout.BinDirName);
        log.Info($"{binDir} -> (run-as {context.Identity.ApplicationId}) {AndroidRuntimeContract.RemoteScriptsDir}");
        return await ScriptBinaryPusher.PushAsync(adb, device.Serial, context.Identity.ApplicationId, files, cancellationToken)
            .ConfigureAwait(false);
    }
}
