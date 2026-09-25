// ============================================================
//  GradleInvocation.cs — gradlew へ渡す引数と Gradle のプロジェクトプロパティ（-P）の組み立て（純粋な処理）
//
//  【タスク（ビルドの種類 × 形式。段階D で配布用を足した）】
//    debug   × apk … assembleDebug（開発用。デバッグ用の鍵で署名）
//    release × apk … assembleRelease（配布用の APK。アップロード鍵で署名）
//    release × aab … bundleRelease（Google Play へ出す AAB。アップロード鍵で署名）
//    （debug の AAB は作らない。中核の指定の検査が先に弾く）
//
//  【渡すプロパティ（runtime/android/app/build.gradle.kts が providers.gradleProperty で読む）】
//    seed.ndkPath       … NDK の場所（APK へ詰める前に .so のシンボルを削るのに使う）
//    seed.abis          … APK に詰める ABI（a,b）
//    seed.orientation   … 画面の向き（both / portrait / landscape。マニフェストの値への変換表は build.gradle.kts）
//    seed.applicationId … アプリ ID（applicationId）
//    seed.appName       … ランチャーに出る名前（マニフェストの android:label）
//    seed.versionCode   … 整数の版（versionCode）
//    seed.versionName   … 版の文字列（versionName）
//    seed.launcherIcon  … generated（Icons/ が src/seedIcon/res へアイコンを生成したとき。段階D）
//    seed.signing.storeFile / seed.signing.keyAlias          … 配布用の署名のキーストアと別名（release だけ。段階D）
//    seed.signing.storePassword / seed.signing.keyPassword   … 同じくパスワード（必ず環境変数。一覧には伏せ字で載る）
//  アプリの識別情報を渡さなければ build.gradle.kts の既定値（com.seedengine.runtime・SEED Runtime 等）になる。
//
//  【なぜ一部を環境変数で渡すのか】
//  gradlew.bat はバッチファイルなので、Windows は cmd.exe を通して起動する。cmd.exe は引数の中の
//  " % ! ^ & | < > ( ) を解釈するため、アプリ名（プロジェクトのデータ）に & 等があると、引数が壊れたり
//  別のコマンドとして実行されたりし得る。そこで、そうした文字を含む値は -P ではなく
//  環境変数 ORG_GRADLE_PROJECT_<名前>（Gradle の仕様で -P と同じプロジェクトプロパティになる）で渡す。
//  安全な値は再現しやすいよう -P でコマンドラインに載せる。
//  パスワードは文字に関わらず必ず環境変数で渡す（コマンドラインはプロセスの一覧・ログから見えるため）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Signing;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Gradle;

/// <summary>Gradle のビルドの入力。</summary>
/// <param name="Abis">APK に詰める ABI。</param>
/// <param name="ScreenOrientation">画面の向き（正規化済みの設定値）。</param>
/// <param name="Identity">アプリの識別情報（null なら渡さず Gradle の既定値）。</param>
/// <param name="NdkPath">NDK の場所（null なら渡さない）。</param>
public sealed record GradleBuildParameters(
    IReadOnlyList<AndroidAbi> Abis,
    string ScreenOrientation,
    AndroidAppIdentity? Identity,
    string? NdkPath)
{
    /// <summary>ビルドの種類（段階D。既定は開発用）。</summary>
    public AndroidBuildVariant Variant { get; init; } = AndroidBuildVariant.Debug;

    /// <summary>形式（段階D。既定は APK）。</summary>
    public AndroidPackageFormat Format { get; init; } = AndroidPackageFormat.Apk;

    /// <summary>配布用の署名（release のときだけ。パスワードは ToString で伏せ字）。</summary>
    public AndroidSigningConfig? Signing { get; init; }

    /// <summary>ランチャーのアイコンを生成したか（src/seedIcon/res。段階D）。</summary>
    public bool LauncherIcon { get; init; }
}

/// <summary>Gradle のプロジェクトプロパティ 1 つ。</summary>
/// <param name="Name">名前（seed.abis 等）。</param>
/// <param name="Value">値（<see cref="IsSecret"/> なら伏せ字。本当の値は <see cref="GradleCommand.Environment"/> にだけある）。</param>
/// <param name="ViaEnvironment">環境変数で渡すか（コマンドラインに載せると cmd.exe が解釈する文字を含む・秘密）。</param>
/// <param name="IsSecret">パスワード等の秘密か（常に環境変数で渡し、一覧・ログ・指紋には伏せ字で載せる）。</param>
public sealed record GradleProjectProperty(string Name, string Value, bool ViaEnvironment, bool IsSecret = false);

/// <summary>組み立てた gradlew の呼び出し。</summary>
/// <param name="Arguments">gradlew の引数。</param>
/// <param name="Environment">足す環境変数（ORG_GRADLE_PROJECT_*。秘密の本当の値を含むのでログに出さない）。</param>
/// <param name="Properties">渡すプロジェクトプロパティ（ログ用。秘密は伏せ字）。</param>
public sealed record GradleCommand(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<GradleProjectProperty> Properties)
{
    /// <summary>行うタスク（引数の先頭）。</summary>
    public string Task => Arguments[0];
}

/// <summary>gradlew の呼び出しの組み立て。</summary>
public static class GradleInvocation
{
    /// <summary>デバッグ版 APK を作るタスク。</summary>
    public const string AssembleDebugTask = "assembleDebug";

    /// <summary>配布用の APK を作るタスク（段階D）。</summary>
    public const string AssembleReleaseTask = "assembleRelease";

    /// <summary>配布用の AAB を作るタスク（段階D）。</summary>
    public const string BundleReleaseTask = "bundleRelease";

    /// <summary>Gradle の進捗表示を素のテキストにする（出力を行ごとに流すため）。</summary>
    public const string PlainConsoleArgument = "--console=plain";

    /// <summary>コマンドラインでプロジェクトプロパティを渡す接頭辞。</summary>
    public const string CommandLinePropertyPrefix = "-P";

    /// <summary>環境変数でプロジェクトプロパティを渡す接頭辞（Gradle の仕様）。</summary>
    public const string EnvironmentPropertyPrefix = "ORG_GRADLE_PROJECT_";

    /// <summary>NDK の場所。</summary>
    public const string NdkPathProperty = "seed.ndkPath";

    /// <summary>APK に詰める ABI。</summary>
    public const string AbisProperty = "seed.abis";

    /// <summary>画面の向き。</summary>
    public const string OrientationProperty = "seed.orientation";

    /// <summary>アプリ ID。</summary>
    public const string ApplicationIdProperty = "seed.applicationId";

    /// <summary>ランチャーに出る名前。</summary>
    public const string AppNameProperty = "seed.appName";

    /// <summary>整数の版。</summary>
    public const string VersionCodeProperty = "seed.versionCode";

    /// <summary>版の文字列。</summary>
    public const string VersionNameProperty = "seed.versionName";

    /// <summary>ランチャーのアイコン（段階D）。</summary>
    public const string LauncherIconProperty = "seed.launcherIcon";

    /// <summary>生成したアイコンを使うときの値（build.gradle.kts の generatedLauncherIconMarker と同じ）。</summary>
    public const string LauncherIconGeneratedValue = "generated";

    /// <summary>配布用の署名のキーストア（段階D）。</summary>
    public const string SigningStoreFileProperty = "seed.signing.storeFile";

    /// <summary>配布用の署名のキーの別名（段階D）。</summary>
    public const string SigningKeyAliasProperty = "seed.signing.keyAlias";

    /// <summary>配布用の署名のキーストアのパスワード（段階D。必ず環境変数）。</summary>
    public const string SigningStorePasswordProperty = "seed.signing.storePassword";

    /// <summary>配布用の署名のキーのパスワード（段階D。必ず環境変数）。</summary>
    public const string SigningKeyPasswordProperty = "seed.signing.keyPassword";

    /// <summary>cmd.exe がコマンドラインで解釈する文字（これを含む値は環境変数で渡す）。</summary>
    private static readonly char[] CommandLineSpecialChars = { '"', '%', '!', '^', '&', '|', '<', '>', '(', ')' };

    /// <summary>
    /// ビルドの種類と形式 → タスク（debug の AAB は作らないので debug は常に APK のタスク）。
    /// </summary>
    /// <param name="variant">ビルドの種類。</param>
    /// <param name="format">形式。</param>
    /// <returns>タスク名。</returns>
    public static string TaskFor(AndroidBuildVariant variant, AndroidPackageFormat format) => variant switch
    {
        AndroidBuildVariant.Release => format == AndroidPackageFormat.Aab ? BundleReleaseTask : AssembleReleaseTask,
        _ => AssembleDebugTask,
    };

    /// <summary>
    /// gradlew の呼び出しを組み立てる。
    /// </summary>
    /// <param name="parameters">ビルドの入力。</param>
    /// <returns>引数・環境変数・プロパティの一覧。</returns>
    public static GradleCommand Build(GradleBuildParameters parameters)
    {
        var properties = new List<(string Name, string Value, bool Secret)>();
        void Add(string name, string value) => properties.Add((name, value, false));

        if (!string.IsNullOrEmpty(parameters.NdkPath)) Add(NdkPathProperty, parameters.NdkPath);
        Add(AbisProperty, AndroidAbis.Describe(parameters.Abis));
        Add(OrientationProperty, parameters.ScreenOrientation);

        var identity = parameters.Identity;
        if (identity is not null)
        {
            // Gradle の既定値に任せる値（出どころ GradleDefault）は渡さない
            if (identity.ApplicationIdSource != AndroidIdentitySource.GradleDefault)
            {
                Add(ApplicationIdProperty, identity.ApplicationId);
            }
            if (identity.AppName.Value is { } appName) Add(AppNameProperty, appName);
            if (identity.VersionCode.Value is int versionCode)
            {
                Add(VersionCodeProperty, versionCode.ToString(CultureInfo.InvariantCulture));
            }
            if (identity.VersionName.Value is { } versionName) Add(VersionNameProperty, versionName);
        }

        // 生成したアイコン（渡さなければシステムの既定のアイコン。従来のビルドの指紋を変えないよう、生成したときだけ足す）
        if (parameters.LauncherIcon) Add(LauncherIconProperty, LauncherIconGeneratedValue);

        // 配布用の署名（release だけ。パスワードは秘密として必ず環境変数）
        if (parameters.Variant == AndroidBuildVariant.Release && parameters.Signing is { } signing)
        {
            Add(SigningStoreFileProperty, signing.KeystorePath);
            Add(SigningKeyAliasProperty, signing.KeyAlias);
            properties.Add((SigningStorePasswordProperty, signing.Secrets.KeystorePassword, true));
            properties.Add((SigningKeyPasswordProperty, signing.Secrets.KeyPassword, true));
        }

        // 一覧（ログ・指紋用）には秘密を伏せ字で載せ、本当の値は環境変数の表にだけ入れる
        var described = properties
            .Select(p => new GradleProjectProperty(
                p.Name,
                p.Secret ? AndroidSigningSecrets.Mask : p.Value,
                ViaEnvironment: p.Secret || !IsSafeForCommandLine(p.Value),
                IsSecret: p.Secret))
            .ToList();
        var arguments = new List<string> { TaskFor(parameters.Variant, parameters.Format) };
        arguments.AddRange(described.Where(p => !p.ViaEnvironment).Select(p => $"{CommandLinePropertyPrefix}{p.Name}={p.Value}"));
        arguments.Add(PlainConsoleArgument);
        var environment = properties
            .Where(p => p.Secret || !IsSafeForCommandLine(p.Value))
            .ToDictionary(p => EnvironmentPropertyPrefix + p.Name, p => p.Value, StringComparer.Ordinal);
        return new GradleCommand(arguments, environment, described);
    }

    /// <summary>
    /// 値をコマンドライン（cmd.exe を通る）にそのまま載せてよいか。
    /// </summary>
    /// <param name="value">値。</param>
    /// <returns>cmd.exe が解釈する文字・制御文字を含まなければ true。</returns>
    public static bool IsSafeForCommandLine(string value) =>
        value.IndexOfAny(CommandLineSpecialChars) < 0 && !value.Any(char.IsControl);
}
