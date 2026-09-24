// ============================================================
//  GradleInvocation.cs — gradlew へ渡す引数と Gradle のプロジェクトプロパティ（-P）の組み立て（純粋な処理）
//
//  【渡すプロパティ（runtime/android/app/build.gradle.kts が providers.gradleProperty で読む）】
//    seed.ndkPath       … NDK の場所（APK へ詰める前に .so のシンボルを削るのに使う）
//    seed.abis          … APK に詰める ABI（a,b）
//    seed.orientation   … 画面の向き（both / portrait / landscape。マニフェストの値への変換表は build.gradle.kts）
//    seed.applicationId … アプリ ID（applicationId）
//    seed.appName       … ランチャーに出る名前（マニフェストの android:label）
//    seed.versionCode   … 整数の版（versionCode）
//    seed.versionName   … 版の文字列（versionName）
//  アプリの識別情報を渡さなければ build.gradle.kts の既定値（com.seedengine.runtime・SEED Runtime 等）になる。
//
//  【なぜ一部を環境変数で渡すのか】
//  gradlew.bat はバッチファイルなので、Windows は cmd.exe を通して起動する。cmd.exe は引数の中の
//  " % ! ^ & | < > ( ) を解釈するため、アプリ名（プロジェクトのデータ）に & 等があると、引数が壊れたり
//  別のコマンドとして実行されたりし得る。そこで、そうした文字を含む値は -P ではなく
//  環境変数 ORG_GRADLE_PROJECT_<名前>（Gradle の仕様で -P と同じプロジェクトプロパティになる）で渡す。
//  安全な値は再現しやすいよう -P でコマンドラインに載せる。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SEEDEditor.Android.Project;

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
    string? NdkPath);

/// <summary>Gradle のプロジェクトプロパティ 1 つ。</summary>
/// <param name="Name">名前（seed.abis 等）。</param>
/// <param name="Value">値。</param>
/// <param name="ViaEnvironment">環境変数で渡すか（コマンドラインに載せると cmd.exe が解釈する文字を含む）。</param>
public sealed record GradleProjectProperty(string Name, string Value, bool ViaEnvironment);

/// <summary>組み立てた gradlew の呼び出し。</summary>
/// <param name="Arguments">gradlew の引数。</param>
/// <param name="Environment">足す環境変数（ORG_GRADLE_PROJECT_*）。</param>
/// <param name="Properties">渡すプロジェクトプロパティ（ログ用）。</param>
public sealed record GradleCommand(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<GradleProjectProperty> Properties);

/// <summary>gradlew の呼び出しの組み立て。</summary>
public static class GradleInvocation
{
    /// <summary>デバッグ版 APK を作るタスク。</summary>
    public const string AssembleDebugTask = "assembleDebug";

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

    /// <summary>cmd.exe がコマンドラインで解釈する文字（これを含む値は環境変数で渡す）。</summary>
    private static readonly char[] CommandLineSpecialChars = { '"', '%', '!', '^', '&', '|', '<', '>', '(', ')' };

    /// <summary>
    /// gradlew の呼び出しを組み立てる。
    /// </summary>
    /// <param name="parameters">ビルドの入力。</param>
    /// <returns>引数・環境変数・プロパティの一覧。</returns>
    public static GradleCommand Build(GradleBuildParameters parameters)
    {
        var properties = new List<(string Name, string Value)>();
        if (!string.IsNullOrEmpty(parameters.NdkPath)) properties.Add((NdkPathProperty, parameters.NdkPath));
        properties.Add((AbisProperty, AndroidAbis.Describe(parameters.Abis)));
        properties.Add((OrientationProperty, parameters.ScreenOrientation));

        var identity = parameters.Identity;
        if (identity is not null)
        {
            // Gradle の既定値に任せる値（出どころ GradleDefault）は渡さない
            if (identity.ApplicationIdSource != AndroidIdentitySource.GradleDefault)
            {
                properties.Add((ApplicationIdProperty, identity.ApplicationId));
            }
            if (identity.AppName.Value is { } appName) properties.Add((AppNameProperty, appName));
            if (identity.VersionCode.Value is int versionCode)
            {
                properties.Add((VersionCodeProperty, versionCode.ToString(CultureInfo.InvariantCulture)));
            }
            if (identity.VersionName.Value is { } versionName) properties.Add((VersionNameProperty, versionName));
        }

        var described = properties
            .Select(p => new GradleProjectProperty(p.Name, p.Value, ViaEnvironment: !IsSafeForCommandLine(p.Value)))
            .ToList();
        var arguments = new List<string> { AssembleDebugTask };
        arguments.AddRange(described.Where(p => !p.ViaEnvironment).Select(p => $"{CommandLinePropertyPrefix}{p.Name}={p.Value}"));
        arguments.Add(PlainConsoleArgument);
        var environment = described
            .Where(p => p.ViaEnvironment)
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
