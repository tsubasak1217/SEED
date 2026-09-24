// ============================================================
//  AndroidAppIdentityResolver.cs — アプリの識別情報の既定値の作り方と、値の検査（純粋な処理）
//
//  【既定値】（project_settings.json の "android" 節に値が無いとき）
//    application_id … .seedproj の名前から com.seedengine.<英数字化した名前>
//                      （英数字化: 全角を半角へ → 小文字 → a-z0-9 だけ残す。数字で始まれば app を前に付ける。
//                        何も残らなければ app ＋ 名前のハッシュ 8 桁。日本語名のプロジェクト同士で ID がぶつからないように）
//                      .seedproj が無ければ Gradle の既定値 com.seedengine.runtime
//    app_name       … プロジェクトの表示名（.seedproj の display_name、空なら name）。無ければ Gradle の既定値（SEED Runtime）
//    version_code   … 1
//    version_name   … "1.0"
//  プロジェクトの名前を変えると既定の ID も変わる（端末は別のアプリとして扱い、セーブを引き継がない）。
//  配布するゲームは ID を明示しておくこと（プロジェクト設定ウィンドウの案内にも書いている）。
//
//  【検査】（書かれた値だけ。違反はビルドの前に止める）
//    application_id … 2 つ以上の区切り。各区切りは英字で始まり英数字と _ だけ（Android の applicationId の規則）
//    app_name       … 先頭（空白を除く）が @ / ? でない（マニフェストでリソースの参照と解釈されるため）・制御文字なし
//    version_code   … 1〜2100000000（Google Play の上限）
//    version_name   … 制御文字なし
//
//  WPF に依存しない（エディタのプロジェクト設定ウィンドウ・コンソールツール・単体テストから使う）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SEEDEditor.ProjectSettings;

namespace SEEDEditor.Android.Project;

/// <summary>アプリの識別情報の既定値と検査。</summary>
public static class AndroidAppIdentityResolver
{
    /// <summary>既定のアプリ ID の前半（後ろにプロジェクト名を英数字化したものが付く）。</summary>
    public const string DefaultApplicationIdPrefix = "com.seedengine.";

    /// <summary>整数の版の既定値。</summary>
    public const int DefaultVersionCode = 1;

    /// <summary>版の文字列の既定値。</summary>
    public const string DefaultVersionName = "1.0";

    /// <summary>整数の版の下限。</summary>
    public const int MinVersionCode = 1;

    /// <summary>整数の版の上限（Google Play が受け付ける最大値）。</summary>
    public const int MaxVersionCode = 2_100_000_000;

    /// <summary>英数字化で何も残らない・数字で始まるときに前へ付ける語。</summary>
    private const string FallbackSegmentPrefix = "app";

    /// <summary>何も残らないときに付けるハッシュの桁数（16 進）。</summary>
    private const int FallbackHashLength = 8;

    /// <summary>アプリ ID の規則（Android の applicationId: 2 区切り以上・各区切りは英字で始まり英数字と _）。</summary>
    private static readonly Regex ApplicationIdPattern =
        new(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant);

    /// <summary>アプリ名の先頭に置けない文字（マニフェストの属性値でリソース参照・テーマ属性参照と解釈される）。</summary>
    private static readonly char[] ForbiddenAppNameLeadingChars = { '@', '?' };

    /// <summary>
    /// 識別情報を決める（書かれた値 → プロジェクトからの既定値 → 決まった既定値 / Gradle の既定値）。
    /// 書かれた値の検査は <see cref="Validate"/> で先に済ませておくこと。
    /// </summary>
    /// <param name="settings">project_settings.json の "android" 節（無ければ null）。</param>
    /// <param name="projectName">.seedproj の name（無ければ null）。</param>
    /// <param name="projectDisplayName">.seedproj の表示名（無ければ null）。</param>
    /// <param name="hasProjectContext">プロジェクト（アセットルート）があるか。無ければ版も Gradle の既定値に任せる。</param>
    /// <returns>決まった識別情報。</returns>
    public static AndroidAppIdentity Resolve(
        AndroidAppSettings? settings, string? projectName, string? projectDisplayName, bool hasProjectContext)
    {
        // ── アプリ ID ──
        var (applicationId, idSource) =
            AndroidAppSettings.NormalizeText(settings?.ApplicationId) is { } explicitId ? (explicitId, AndroidIdentitySource.ProjectSettings)
            : !string.IsNullOrWhiteSpace(projectName) ? (DefaultApplicationIdFor(projectName), AndroidIdentitySource.ProjectDefault)
            : (AndroidRuntimeContract.DefaultApplicationId, AndroidIdentitySource.GradleDefault);

        // ── アプリ名 ──
        var displayName = AndroidAppSettings.NormalizeText(projectDisplayName) ?? AndroidAppSettings.NormalizeText(projectName);
        var appName =
            AndroidAppSettings.NormalizeText(settings?.AppName) is { } explicitName ? new AndroidIdentityValue<string>(explicitName, AndroidIdentitySource.ProjectSettings)
            : displayName is not null ? new AndroidIdentityValue<string>(displayName, AndroidIdentitySource.ProjectDefault)
            : new AndroidIdentityValue<string>(null, AndroidIdentitySource.GradleDefault);

        // ── 版 ──
        var versionCode =
            settings?.VersionCode is int explicitCode ? new AndroidIdentityValue<int?>(explicitCode, AndroidIdentitySource.ProjectSettings)
            : hasProjectContext ? new AndroidIdentityValue<int?>(DefaultVersionCode, AndroidIdentitySource.FixedDefault)
            : new AndroidIdentityValue<int?>(null, AndroidIdentitySource.GradleDefault);
        var versionName =
            AndroidAppSettings.NormalizeText(settings?.VersionName) is { } explicitVersion ? new AndroidIdentityValue<string>(explicitVersion, AndroidIdentitySource.ProjectSettings)
            : hasProjectContext ? new AndroidIdentityValue<string>(DefaultVersionName, AndroidIdentitySource.FixedDefault)
            : new AndroidIdentityValue<string>(null, AndroidIdentitySource.GradleDefault);

        return new AndroidAppIdentity(applicationId, idSource, appName, versionCode, versionName);
    }

    /// <summary>
    /// プロジェクト名から既定のアプリ ID を作る（com.seedengine.&lt;英数字化した名前&gt;）。
    /// </summary>
    /// <param name="projectName">.seedproj の name。</param>
    /// <returns>アプリ ID。</returns>
    public static string DefaultApplicationIdFor(string projectName) =>
        DefaultApplicationIdPrefix + SanitizeSegment(projectName);

    /// <summary>
    /// 名前をアプリ ID の 1 区切りにする（英数字化）。
    /// </summary>
    /// <param name="name">元の名前。</param>
    /// <returns>英字で始まり英数字だけの区切り。</returns>
    public static string SanitizeSegment(string name)
    {
        // 全角英数字を半角へ（ＭｙＧａｍｅ → MyGame）してから、小文字の a-z0-9 だけを残す
        var normalized = name.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var kept = new string(normalized.Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());
        if (kept.Length == 0) return FallbackSegmentPrefix + ShortHash(name);
        return char.IsAsciiDigit(kept[0]) ? FallbackSegmentPrefix + kept : kept;
    }

    /// <summary>
    /// 書かれた値を検査する（書かれていない値は検査しない）。
    /// </summary>
    /// <param name="settings">"android" 節（null なら何も書かれていない）。</param>
    /// <returns>違反の説明（無ければ空）。</returns>
    public static IReadOnlyList<string> Validate(AndroidAppSettings? settings)
    {
        var errors = new List<string>();
        if (settings is null) return errors;

        if (AndroidAppSettings.NormalizeText(settings.ApplicationId) is { } id && !IsValidApplicationId(id))
        {
            errors.Add($"アプリ ID「{id}」の形式が正しくありません。英字で始まる英数字と _ の語を . で 2 つ以上つないでください（例: com.example.mygame）。");
        }
        if (AndroidAppSettings.NormalizeText(settings.AppName) is { } name)
        {
            if (Array.IndexOf(ForbiddenAppNameLeadingChars, name[0]) >= 0)
            {
                errors.Add($"アプリ名「{name}」の先頭に {string.Join(" / ", ForbiddenAppNameLeadingChars)} は使えません（Android がリソースの参照と解釈するため）。");
            }
            if (name.Any(char.IsControl))
            {
                errors.Add("アプリ名に改行・制御文字は使えません。");
            }
        }
        if (settings.VersionCode is int code && code is < MinVersionCode or > MaxVersionCode)
        {
            errors.Add($"バージョン番号（version_code）は {MinVersionCode}〜{MaxVersionCode} の整数にしてください（今の値: {code}）。");
        }
        if (AndroidAppSettings.NormalizeText(settings.VersionName) is { } versionName && versionName.Any(char.IsControl))
        {
            errors.Add("バージョン名（version_name）に改行・制御文字は使えません。");
        }
        return errors;
    }

    /// <summary>アプリ ID の形式が正しいか。</summary>
    /// <param name="applicationId">アプリ ID。</param>
    /// <returns>正しければ true。</returns>
    public static bool IsValidApplicationId(string applicationId) => ApplicationIdPattern.IsMatch(applicationId);

    /// <summary>名前のハッシュの先頭（英数字化で何も残らない名前の区別用）。</summary>
    /// <param name="name">元の名前。</param>
    /// <returns>16 進の小文字 <see cref="FallbackHashLength"/> 桁。</returns>
    private static string ShortHash(string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant()[..FallbackHashLength];
}
