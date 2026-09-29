// ============================================================
//  AndroidPlatformFeatureChecks.cs — プラットフォーム機能（android.features）の権限と Google Play の方針の判定（純粋な処理。W1-2）
//
//  【項目】（Google Play の要件チェックの一覧に入る。表現はほかの項目と同じ 合格／知らせ／注意／不合格）
//    platform_features      … ビルドの前: features から決まる権限の一覧（知らない機能は注意）。
//                             ビルドの後: 配布物の権限と比べる
//                               ・features の機能の権限が配布物に無い → 不合格（機能が端末で動かない。断片が Gradle に渡っていない・古い配布物）
//                               ・features に無い機能の権限がある     → 注意（Google Play の審査・申告の対象になりうる。W1-P4）
//    play_policy/<権限>      … 権限ごとの Google Play の方針（play_requirements.json の permission_policies）。
//                             ビルドの前は features から決まる権限、ビルドの後は配布物の権限について出す
//                             （例: USE_EXACT_ALARM は目覚まし・カレンダーのアプリとしての申告、USE_FULL_SCREEN_INTENT は
//                              通話・目覚ましが中核でないと取り上げられる、FOREGROUND_SERVICE_MEDIA_PLAYBACK は前景サービスの申告）
//  ビルドの後の項目は、同じ名前のビルドの前の項目を上書きする（AndroidRequirementReport.Put）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.Android.Platform;
using SEEDEditor.ProjectSettings;

namespace SEEDEditor.Android.Release;

/// <summary>プラットフォーム機能の権限と Google Play の方針の判定。</summary>
public static class AndroidPlatformFeatureChecks
{
    /// <summary>platform_features の項目の見出し。</summary>
    public const string Title = "プラットフォーム機能の権限";

    /// <summary>権限の名前の区切り（見出しに最後の区切りだけを出す）。</summary>
    private const char PermissionNameSeparator = '.';

    /// <summary>
    /// ビルドの前の判定（プロジェクト設定から決まった機能と、その権限の Google Play の方針）。
    /// </summary>
    /// <param name="requirements">要件の表。</param>
    /// <param name="features">このビルドのプラットフォーム機能。</param>
    /// <returns>判定の一覧。</returns>
    public static IReadOnlyList<AndroidRequirementItem> BeforeBuild(PlayRequirements requirements, AndroidPlatformFeatureSet features)
    {
        var items = new List<AndroidRequirementItem> { DeclaredFeatures(features) };
        items.AddRange(Policies(requirements, features.Permissions.Select(p => p.Name)));
        return items;
    }

    /// <summary>
    /// ビルドの後の判定（配布物の権限と features を比べる。方針の注意は配布物の権限について出す）。
    /// </summary>
    /// <param name="requirements">要件の表。</param>
    /// <param name="features">このビルドのプラットフォーム機能。</param>
    /// <param name="catalog">機能の表（どの権限がどの機能のものか）。</param>
    /// <param name="artifactPermissions">配布物の権限（aapt2 dump badging の uses-permission）。</param>
    /// <returns>判定の一覧。</returns>
    public static IReadOnlyList<AndroidRequirementItem> AfterBuild(
        PlayRequirements requirements, AndroidPlatformFeatureSet features, AndroidPlatformFeatureCatalog catalog,
        IReadOnlyList<string> artifactPermissions)
    {
        var items = new List<AndroidRequirementItem> { CompareWithArtifact(features, catalog, artifactPermissions) };
        items.AddRange(Policies(requirements, artifactPermissions));
        return items;
    }

    /// <summary>ビルドの前: features から決まる権限の一覧（知らない機能は注意）。</summary>
    /// <param name="features">このビルドのプラットフォーム機能。</param>
    /// <returns>判定。</returns>
    public static AndroidRequirementItem DeclaredFeatures(AndroidPlatformFeatureSet features)
    {
        var unknown = features.UnknownFeatures.Count == 0
            ? string.Empty
            : $" 知らない機能 {string.Join(", ", features.UnknownFeatures)} は無視しました（プロジェクト設定の {AndroidAppSettings.SectionKey}.{AndroidAppSettings.FeaturesKey}）。";
        var severity = features.UnknownFeatures.Count == 0 ? AndroidRequirementSeverity.Pass : AndroidRequirementSeverity.Warning;
        if (features.Features.Count == 0)
        {
            return new(AndroidRequirementIds.PlatformFeatures, Title, severity,
                $"{AndroidAppSettings.FeaturesKey} なし（機能ごとの権限・部品を入れない）。{unknown}".TrimEnd());
        }
        var permissions = features.Permissions.Count == 0 ? "権限なし" : $"権限 {features.Permissions.Count}（{string.Join(", ", features.Permissions.Select(p => ShortName(p.Name)))}）";
        return new(AndroidRequirementIds.PlatformFeatures, Title, severity,
            $"{string.Join(", ", features.FeatureNames)} → {permissions}。{unknown}".TrimEnd());
    }

    /// <summary>
    /// ビルドの後: 配布物の権限と features を比べる（足りない＝不合格・余計＝注意）。
    /// </summary>
    /// <param name="features">このビルドのプラットフォーム機能。</param>
    /// <param name="catalog">機能の表。</param>
    /// <param name="artifactPermissions">配布物の権限。</param>
    /// <returns>判定。</returns>
    public static AndroidRequirementItem CompareWithArtifact(
        AndroidPlatformFeatureSet features, AndroidPlatformFeatureCatalog catalog, IReadOnlyList<string> artifactPermissions)
    {
        var actual = artifactPermissions.ToHashSet(StringComparer.Ordinal);
        var expected = features.PermissionNames;

        // features の機能の権限が配布物に無い（断片が Gradle に渡っていない・古い配布物を見ている）
        var missing = features.Permissions.Where(p => !actual.Contains(p.Name)).Select(p => p.Name).ToList();
        if (missing.Count > 0)
        {
            return new(AndroidRequirementIds.PlatformFeatures, Title, AndroidRequirementSeverity.Failure,
                $"{AndroidAppSettings.FeaturesKey}（{string.Join(", ", features.FeatureNames)}）の権限が配布物にありません: {string.Join(", ", missing)}。" +
                "機能が端末で動きません。マニフェストの断片（runtime/android/app/src/seedFeatures/）が Gradle に渡ったか・古い配布物を見ていないかを確かめてください。");
        }

        // features に無い機能の権限が配布物にある（機能の表のどれかの機能の権限だけを見る。機能の表に無い権限〈INTERNET・androidx の
        // DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION 等〉は対象外。ライブラリが機能と同じ権限を足した場合は区別できないので注意どまり）
        var unexpected = artifactPermissions
            .Where(permission => !expected.Contains(permission))
            .Select(permission => (Permission: permission, Owners: catalog.FeaturesRequesting(permission)))
            .Where(entry => entry.Owners.Count > 0)
            .ToList();
        if (unexpected.Count > 0)
        {
            var listed = string.Join(", ", unexpected.Select(entry => $"{entry.Permission}（機能 {string.Join(" / ", entry.Owners)}）"));
            return new(AndroidRequirementIds.PlatformFeatures, Title, AndroidRequirementSeverity.Warning,
                $"{AndroidAppSettings.FeaturesKey} に無い機能の権限が入っています: {listed}。Google Play の審査・申告の対象になることがあります" +
                "（使わない機能の権限は入れない。古い配布物・手で書いたマニフェストが残っていないか、依存ライブラリが同じ権限を足していないかを確かめてください）。");
        }

        var summary = features.Features.Count == 0
            ? $"{AndroidAppSettings.FeaturesKey} なし・機能の権限なし"
            : $"{string.Join(", ", features.FeatureNames)} の権限 {features.Permissions.Count} がそろっている・ほかの機能の権限なし";
        return new(AndroidRequirementIds.PlatformFeatures, Title, AndroidRequirementSeverity.Pass, summary);
    }

    /// <summary>
    /// 権限ごとの Google Play の方針の注意（表にある権限だけ・表の順）。
    /// </summary>
    /// <param name="requirements">要件の表。</param>
    /// <param name="permissions">権限の名前。</param>
    /// <returns>判定の一覧。</returns>
    public static IReadOnlyList<AndroidRequirementItem> Policies(PlayRequirements requirements, IEnumerable<string> permissions)
    {
        var present = permissions.ToHashSet(StringComparer.Ordinal);
        return requirements.PermissionPolicies
            .Where(policy => present.Contains(policy.Permission))
            .Select(policy => new AndroidRequirementItem(
                AndroidRequirementIds.PlayPolicy(policy.Permission),
                policy.Title ?? $"Google Play の方針（{ShortName(policy.Permission)}）",
                SeverityOf(policy),
                policy.Source is null ? policy.Note : $"{policy.Note}（出どころ: {policy.Source}）"))
            .ToList();
    }

    /// <summary>表の判定の表記を判定にする（info は知らせ、それ以外は注意）。</summary>
    private static AndroidRequirementSeverity SeverityOf(PlayPermissionPolicy policy) =>
        string.Equals(policy.Severity, PlayPermissionPolicy.InfoSeverity, StringComparison.OrdinalIgnoreCase)
            ? AndroidRequirementSeverity.Info
            : AndroidRequirementSeverity.Warning;

    /// <summary>権限の名前の最後の区切り（android.permission.USE_EXACT_ALARM → USE_EXACT_ALARM）。</summary>
    private static string ShortName(string permission)
    {
        var index = permission.LastIndexOf(PermissionNameSeparator);
        return index >= 0 ? permission[(index + 1)..] : permission;
    }
}
