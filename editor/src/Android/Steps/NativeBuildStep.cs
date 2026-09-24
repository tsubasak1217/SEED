// ============================================================
//  NativeBuildStep.cs — cargo ndk で libSEED.so を作り、app/src/main/jniLibs/<ABI>/ へ置く
//
//  cargo ndk -t <ABI>... -P <最低 API> -o <jniLibs> build [--release]（作業フォルダ runtime/android/native）。
//  計画が「古い」と判断した ABI だけを作る。ANDROID_NDK_HOME には道具の解決で決めた表記をそのまま渡す
//  （表記が変わると cc 系の依存が作り直されるため。Toolchain/AndroidToolchain.cs）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Processes;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Android.Steps;

/// <summary>libSEED.so のビルド。</summary>
public sealed class NativeBuildStep : IAndroidPipelineStep
{
    /// <summary>1 MiB（大きさの表示用）。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <inheritdoc />
    public AndroidPipelinePhase Phase => AndroidPipelinePhase.NativeBuild;

    /// <inheritdoc />
    public async Task<string> RunAsync(
        AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var cargo = context.Toolchain.RequireCargo();
        var ndk = context.Toolchain.RequireNdk();

        // cargo-ndk が入っているか（入っていなければ入れ方を案内する）
        var check = await ChildProcessRunner.CaptureAsync(
            new ChildProcessSpec { FileName = cargo, Arguments = new[] { "ndk", "--version" } }, cancellationToken).ConfigureAwait(false);
        if (check.ExitCode != 0)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Toolchain,
                "cargo-ndk が見つかりません。`cargo install cargo-ndk` と `rustup target add aarch64-linux-android x86_64-linux-android` を実行してください。");
        }
        log.Info($"{check.StandardOutput.FirstOrDefault()?.Trim()}  NDK: {ndk}");

        var arguments = new List<string> { "ndk" };
        foreach (var abi in decision.Abis)
        {
            arguments.Add("-t");
            arguments.Add(abi.Name);
        }
        arguments.AddRange(new[]
        {
            "-P", AndroidRuntimeContract.MinApiLevel.ToString(CultureInfo.InvariantCulture), "-o", context.Engine.JniLibsDir, "build",
        });
        if (context.Request.Release) arguments.Add("--release");

        var spec = new ChildProcessSpec
        {
            FileName         = cargo,
            Arguments        = arguments,
            WorkingDirectory = context.Engine.NativeCrateDir,
            Environment      = new Dictionary<string, string?> { [AndroidToolchain.NdkHomeVariable] = ndk },
        };
        log.Info(spec.Describe());
        var exitCode = await ChildProcessRunner.RunAsync(spec, log.Process, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"cargo ndk が失敗しました（終了コード {exitCode}。上の出力を確認してください）。");
        }

        // 作った .so を確かめて記録する（次回、入力が同じならこの ABI のビルドを飛ばす）
        var sizes = new List<string>();
        foreach (var abi in decision.Abis)
        {
            var library = context.Engine.NativeLibraryPath(abi);
            if (!File.Exists(library))
            {
                throw new AndroidPipelineException(AndroidFailureKind.Build, $"cargo ndk の後に {library} がありません。");
            }
            var key = AndroidStepKeys.Native(abi);
            context.Stamps.Steps[key] = new AndroidStepFingerprint(context.CurrentFingerprints[key].Inputs, AndroidOutputIdentity.OfFile(library));
            sizes.Add($"{abi.Name} {new FileInfo(library).Length / BytesPerMegabyte:F1} MB");
        }
        return string.Join("・", sizes);
    }
}
