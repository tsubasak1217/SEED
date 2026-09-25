// ============================================================
//  GradleBuildStep.cs — gradlew で APK / AAB を作る（開発用 assembleDebug・配布用 assembleRelease / bundleRelease）
//
//  ABI・画面の向き・アプリの識別情報は Gradle のプロジェクトプロパティ（-Pseed.*。cmd.exe が解釈する文字を含む値は
//  環境変数 ORG_GRADLE_PROJECT_seed.*）で渡し、runtime/android/app/build.gradle.kts がマニフェスト・applicationId 等へ反映する
//  （変換表は build.gradle.kts の 1 か所。Gradle/GradleInvocation.cs）。JAVA_HOME は道具の解決で決めた JDK。
//
//  【段階D で足したこと】
//    - ランチャーのアイコン: Gradle の前に、プロジェクト設定 android.icon から app/src/seedIcon/res/ へ生成物を置く
//      （Icons/LauncherIconStager。中身の違うファイルだけを書く）。生成したら seed.launcherIcon=generated を渡す
//    - 配布用（release）: 署名のキーストア・別名・パスワードを環境変数 ORG_GRADLE_PROJECT_seed.signing.* で渡す（パスワードは
//      ログ・コマンドラインに出さない）。できた配布物を記録し、配布用ビルドの記録（versionCode）を書く
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Gradle;
using SEEDEditor.Android.Icons;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Processes;
using SEEDEditor.Android.Release;
using SEEDEditor.Android.State;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Steps;

/// <summary>APK / AAB の作成。</summary>
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
        new(context.Abis, context.ScreenOrientation, context.Identity, ndkPath)
        {
            Variant = context.Request.Variant,
            Format = context.Request.Format,
            Signing = context.Signing,
            LauncherIcon = context.LauncherIcon is not null,
        };

    /// <inheritdoc />
    public async Task<string> RunAsync(
        AndroidPipelineContext context, AndroidStepDecision decision, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        var javaHome = context.Toolchain.RequireJavaHome();
        var sdk = context.Toolchain.RequireSdk();
        var ndk = context.Toolchain.RequireNdk();

        // ── ランチャーのアイコン（Gradle が res として読む生成物。設定が無ければ前の生成物を消す）──
        var icon = LauncherIconStager.Stage(context.Engine.LauncherIconResDir, context.LauncherIcon);
        log.Info(icon.Describe());
        foreach (var warning in icon.Warnings) log.Warn(warning);

        var parameters = Parameters(context, ndk);
        var command = GradleInvocation.Build(parameters);
        foreach (var property in command.Properties)
        {
            log.Info(property.IsSecret
                ? $"  {property.Name}={property.Value}（秘密。環境変数 {GradleInvocation.EnvironmentPropertyPrefix}{property.Name} で渡す）"
                : property.ViaEnvironment
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
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"gradlew {command.Task} が失敗しました（終了コード {exitCode}。上の出力を確認してください）。");
        }

        var artifact = context.ArtifactPath;
        if (!File.Exists(artifact))
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build, $"{command.Task} の後に配布物が見つかりません: {artifact}");
        }

        // 記録: 入力は上流の工程を作り直した後の値で計算し直す（.so・pak・同梱 .NET・アイコンの今の出力を含むため）
        var identity = AndroidOutputIdentity.OfFile(artifact);
        var sha256 = ApkFile.ComputeSha256(artifact);
        context.Stamps.Steps[context.GradleKey] = AndroidStepFingerprints.Gradle(context.Engine, parameters, context.LauncherIcon);
        context.Stamps.SetArtifact(context.GradleKey,
            new AndroidApkStamp(identity, sha256, context.Abis.Select(abi => abi.Name).ToArray(), context.Identity.ApplicationId));
        if (context.Request.Variant == AndroidBuildVariant.Release) RecordRelease(context, sha256, log);
        return $"{Path.GetFileName(artifact)} {new FileInfo(artifact).Length / BytesPerMegabyte:F1} MB（{AndroidAbis.Describe(context.Abis)}・{context.Identity.ApplicationId}）";
    }

    /// <summary>
    /// 配布用ビルドの記録（versionCode の単調増加の材料）を書く。書けなくても工程は成功のまま（次回「記録なし」と出るだけ）。
    /// </summary>
    private static void RecordRelease(AndroidPipelineContext context, string sha256, AndroidPhaseLog log)
    {
        if (context.Identity.VersionCode.Value is not int versionCode) return;
        try
        {
            var history = AndroidReleaseHistory.Load(context.ReleaseHistoryPath);
            history.Record(context.Identity.ApplicationId, context.Request.Format, new AndroidReleaseRecord
            {
                VersionCode = versionCode,
                VersionName = context.Identity.VersionName.Value,
                BuiltAt = DateTimeOffset.Now,
                Sha256 = sha256,
            });
            history.Save(context.ReleaseHistoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"配布用ビルドの記録を書けません（次回の versionCode の比べは「記録なし」になります）: {context.ReleaseHistoryPath}（{ex.Message}）");
        }
    }
}
