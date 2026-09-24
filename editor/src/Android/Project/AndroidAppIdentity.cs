// ============================================================
//  AndroidAppIdentity.cs — APK に焼き込むアプリの識別情報（決まった値と、その出どころ）
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

namespace SEEDEditor.Android.Project;

/// <summary>値の出どころ。</summary>
public enum AndroidIdentitySource
{
    /// <summary>project_settings.json の "android" 節に書かれた値。</summary>
    ProjectSettings,

    /// <summary>プロジェクト（.seedproj の名前・表示名）から作った既定値。</summary>
    ProjectDefault,

    /// <summary>決まった既定値（版の 1 / "1.0"）。</summary>
    FixedDefault,

    /// <summary>Gradle の既定値に任せる（プロジェクトが無いとき。ID は com.seedengine.runtime、名前は SEED Runtime）。</summary>
    GradleDefault,
}

/// <summary>決まった値 1 つとその出どころ。</summary>
/// <typeparam name="T">値の型。</typeparam>
/// <param name="Value">値（Gradle の既定値に任せるときは null）。</param>
/// <param name="Source">出どころ。</param>
public sealed record AndroidIdentityValue<T>(T? Value, AndroidIdentitySource Source);

/// <summary>
/// APK に焼き込むアプリの識別情報。<see cref="ApplicationId"/> は常に決まっている（端末の操作に使う）。
/// ほかは Gradle の既定値に任せるときに null。
/// </summary>
/// <param name="ApplicationId">アプリ ID（端末でのパッケージ名。adb の操作に使う）。</param>
/// <param name="ApplicationIdSource">アプリ ID の出どころ。</param>
/// <param name="AppName">ランチャーに出る名前（null なら Gradle の既定値）。</param>
/// <param name="VersionCode">整数の版（null なら Gradle の既定値）。</param>
/// <param name="VersionName">版の文字列（null なら Gradle の既定値）。</param>
public sealed record AndroidAppIdentity(
    string ApplicationId,
    AndroidIdentitySource ApplicationIdSource,
    AndroidIdentityValue<string> AppName,
    AndroidIdentityValue<int?> VersionCode,
    AndroidIdentityValue<string> VersionName)
{
    /// <summary>ログ用の一行説明。</summary>
    /// <returns>説明。</returns>
    public string Describe() =>
        $"ID={ApplicationId}（{Label(ApplicationIdSource)}）・名前={AppName.Value ?? "（Gradle の既定）"}（{Label(AppName.Source)}）・" +
        $"版={VersionCode.Value?.ToString() ?? "（Gradle の既定）"} / {VersionName.Value ?? "（Gradle の既定）"}";

    /// <summary>出どころの表示名。</summary>
    /// <param name="source">出どころ。</param>
    /// <returns>表示名。</returns>
    public static string Label(AndroidIdentitySource source) => source switch
    {
        AndroidIdentitySource.ProjectSettings => "プロジェクト設定",
        AndroidIdentitySource.ProjectDefault  => "プロジェクト名からの既定値",
        AndroidIdentitySource.FixedDefault    => "既定値",
        _                                     => "Gradle の既定値",
    };
}
