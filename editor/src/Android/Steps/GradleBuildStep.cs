// ============================================================
//  GradleBuildStep.cs — gradlew assembleDebug で APK を作る
//
//  ABI・画面の向き・アプリの識別情報は Gradle のプロジェクトプロパティ（-Pseed.*。cmd.exe が解釈する文字を含む値は
//  環境変数 ORG_GRADLE_PROJECT_seed.*）で渡し、runtime/android/app/build.gradle.kts がマニフェスト・applicationId 等へ反映する
//  （変換表は build.gradle.kts の 1 か所。Gradle/GradleInvocation.cs）。JAVA_HOME は道具の解決で決めた JDK。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Gradle;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Processes;
using SEEDEditor.Android.State;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Android.Steps;

/// <summary>APK の作成。</summary>
public sealed class GradleBuildStep : IAndroidPipelineStep
{
    /// <summary>1 MiB（大きさの表示用）。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <inheritdoc />
    public AndroidPipelinePhase Phase => AndroidPipelinePhase.Gradle;

    /// <summary>今回の Gradle へ渡す値（計画の指紋と実行で同じものを使う）。</summary>
    /// <param name="context">共有の値。</param>
    /// <param name="ndkPath">NDK の場所。</param>
    /// <returns>Gradle のビルドの入力。</returns>
    public static GradleBuildParameters Parameters(AndroidPipelineContext context, string? ndkPath) =>
        new(context.Abis, context.ScreenOrientation, context.Identity, ndkPath);

    /// <inheritdoc />
    public async Task<string> RunAsync(
        AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var javaHome = context.Toolchain.RequireJavaHome();
        var sdk = context.Toolchain.RequireSdk();
        var ndk = context.Toolchain.RequireNdk();
        var parameters = Parameters(context, ndk);
        var command = GradleInvocation.Build(parameters);

        foreach (var property in command.Properties)
        {
            log.Info(property.ViaEnvironment
                ? $"  {property.Name}={property.Value}（cmd.exe が解釈する文字を含むため環境変数 {GradleInvocation.EnvironmentPropertyPrefix}{property.Name} で渡す）"
                : $"  {property.Name}={property.Value}");
        }

        // JDK と SDK は道具の解決で見つけたものを渡す（環境変数が無く既定の場所で見つけたときも、gradlew・AGP が同じものを使うように）。
        // AGP は ANDROID_HOME を読む。ANDROID_SDK_ROOT は触らない（設定されていれば SDK はそれから見つけているので同じ値）。
        var environment = new Dictionary<string, string?>
        {
            [AndroidToolchain.JavaHomeVariable] = javaHome,
            [AndroidToolchain.SdkHomeVariable]  = sdk,
        };
        foreach (var (name, value) in command.Environment) environment[name] = value;
        var spec = new ChildProcessSpec
        {
            FileName         = context.Engine.GradleWrapper,
            Arguments        = command.Arguments,
            WorkingDirectory = context.Engine.AndroidDir,
            Environment      = environment,
        };
        log.Info(spec.Describe());
        var exitCode = await ChildProcessRunner.RunAsync(spec, log.Process, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"gradlew {GradleInvocation.AssembleDebugTask} が失敗しました（終了コード {exitCode}。上の出力を確認してください）。");
        }

        var apk = context.Engine.DebugApkPath;
        if (!File.Exists(apk))
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"APK が見つかりません: {apk}");
        }

        // 記録: 入力は上流の工程を作り直した後の値で計算し直す（.so・pak・同梱 .NET の今の出力を含むため）
        var identity = AndroidOutputIdentity.OfFile(apk);
        var sha256 = ApkFile.ComputeSha256(apk);
        context.Stamps.Steps[AndroidStepKeys.Gradle] = AndroidStepFingerprints.Gradle(context.Engine, parameters);
        context.Stamps.Apk = new AndroidApkStamp(identity, sha256, context.Abis.Select(abi => abi.Name).ToArray(), context.Identity.ApplicationId);
        return $"{Path.GetFileName(apk)} {new FileInfo(apk).Length / BytesPerMegabyte:F1} MB（{AndroidAbis.Describe(context.Abis)}・{context.Identity.ApplicationId}）";
    }
}
