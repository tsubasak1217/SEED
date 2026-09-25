// ============================================================
//  NativeBuildStep.cs — cargo ndk で libSEED.so を作り、app/src/main/jniLibs/<ABI>/ へ置く
//
//  cargo ndk -t <ABI>... -P <最低 API> -o <jniLibs> build [--release]（作業フォルダ runtime/android/native）。
//  計画が「古い」と判断した ABI だけを作る。ANDROID_NDK_HOME には道具の解決で決めた表記をそのまま渡す
//  （表記が変わると cc 系の依存が作り直されるため。Toolchain/AndroidToolchain.cs）。
//  cargo ndk を呼ぶ前に、作る ABI の jniLibs の .so を消す（RemovePreviousLibraries。cargo-ndk は
//  コピー先の方が新しいと写さないため、debug ⇔ release の切り替えで別のプロファイルの .so が APK に残るのを防ぐ）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
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

        // 前のビルドの .so を消してから作る（cargo-ndk が今回のプロファイルの成果物を必ず写すように。理由は RemovePreviousLibraries）
        RemovePreviousLibraries(context, decision, log);

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
        // --release の指定か、配布用（release）のビルド（段階D。配布物は常に最適化した .so）
        if (context.Request.OptimizesNative) arguments.Add("--release");

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

    /// <summary>
    /// これから作る ABI の jniLibs の .so（前のビルドで写したもの）を消す。
    /// cargo-ndk（4.1.2 で確認）は「コピー先の更新時刻がビルドの成果物と同じか新しければ写さない」（is_fresh）。
    /// そのため debug ⇔ release を切り替えると、Rust に変更が無く今回のプロファイルの成果物が前のコピーより古いとき、
    /// cargo ndk は成功しても写さず、もう一方のプロファイルの .so が APK に入ってしまう
    /// （2026-09-26 に実機で確認: 配布用のビルドの後の開発用の run で、配布用の .so が開発用の APK に入った。
    /// 逆向きでは配布用の APK / AAB に開発用の .so が入りうる）。消しておけば cargo-ndk は必ず写す。
    /// 消した後に cargo ndk が失敗しても、この工程の記録は更新しないので次の実行で作り直す。
    /// </summary>
    /// <param name="context">工程の文脈（jniLibs の置き場）。</param>
    /// <param name="decision">この工程の判断（作る ABI）。</param>
    /// <param name="log">工程のログ（消したことを出す）。</param>
    private static void RemovePreviousLibraries(AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log)
    {
        foreach (var abi in decision.Abis)
        {
            var library = context.Engine.NativeLibraryPath(abi);
            if (!File.Exists(library)) continue;
            try
            {
                File.Delete(library);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AndroidPipelineException(AndroidFailureKind.Build,
                    $"前のビルドの {library} を消せませんでした（{ex.Message}）。ほかのプログラムが開いていないか確認してください。");
            }
            log.Info($"前のビルドの .so を消しました（{abi.Name}。cargo-ndk がこのプロファイルの .so を必ず写すように）");
        }
    }
}
