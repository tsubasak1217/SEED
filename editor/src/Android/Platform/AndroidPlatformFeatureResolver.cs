// ============================================================
//  AndroidPlatformFeatureResolver.cs — プロジェクト設定 → このビルドのプラットフォーム機能（純粋な処理。W1-2）
//
//  【決まり】（docs/android.md §25.10・docs/app_platform_roadmap.md §2.5）
//    features     … 表にある名前だけを有効にする（大文字小文字・前後の空白は問わない。重なりは 1 つに）。
//                   表に無い名前は注意を出して無視する（誤りにしない。新しいエディタで足した機能を古いエディタで壊さない）
//    権限          … 有効な機能の権限を表の順に集め、重なりを除く。同じ権限の maxSdkVersion が機能で違えば広いほう
//                   （どちらかが無制限なら無制限、両方あれば大きいほう）
//    deep_links   … 表で deep_link_filters の機能（deep_links）が有効なときだけ、1 件ずつ検査（AndroidDeepLinkRules）して
//                   intent-filter にする。機能が無いのに一覧がある・機能があるのに一覧が空は注意。誤りのある件は入れない
//    system_bars / app_category … 正規化（知らない値は注意を出して既定値。screen_orientation と同じ方針）
//    predictive_back … true のときだけ使う（W2 の手直し P1-3。型違いは設定の読み取りで未設定になっている＝使わない）
//  誤り（Errors）が 1 つでもあればビルドは何も作らずに止め、プロジェクト設定ウィンドウは保存しない（同じ関数で判定する）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.ProjectSettings;

namespace SEEDEditor.Android.Platform;

/// <summary>プロジェクト設定 → このビルドのプラットフォーム機能。</summary>
public static class AndroidPlatformFeatureResolver
{
    /// <summary>
    /// プロジェクト設定と機能の表から、このビルドのプラットフォーム機能を決める。
    /// </summary>
    /// <param name="settings">project_settings.json の android 節（無ければ null＝機能なし・既定）。</param>
    /// <param name="catalog">機能の表。</param>
    /// <returns>結果（誤り・注意を含む）。</returns>
    public static AndroidPlatformFeatureSet Resolve(AndroidAppSettings? settings, AndroidPlatformFeatureCatalog catalog)
    {
        if (settings is null) return AndroidPlatformFeatureSet.Empty;
        var warnings = new List<string>();
        var errors = new List<string>();

        // ── features（表にある名前だけ・表の順）──
        var (features, unknown) = SelectFeatures(settings.Features, catalog);
        if (unknown.Count > 0)
        {
            warnings.Add($"{AndroidAppSettings.FeaturesKey} の {string.Join(", ", unknown.Select(name => $"\"{name}\""))} は知らない機能です" +
                         $"（使える機能: {string.Join(" / ", catalog.Names)}）。無視します。");
        }

        // ── ディープリンク（intent-filter を作る機能が有効なときだけ）──
        var deepLinks = ResolveDeepLinks(settings.DeepLinks, features, catalog, errors, warnings);

        // ── システムバー・アプリの分類（知らない値は注意を出して既定値）──
        var systemBars = AndroidSystemBarsSetting.Normalize(settings.SystemBars);
        if (!string.IsNullOrWhiteSpace(settings.SystemBars) && !AndroidSystemBarsSetting.IsKnown(settings.SystemBars))
        {
            warnings.Add($"{AndroidAppSettings.SystemBarsKey}=\"{settings.SystemBars}\" は知らない値です" +
                         $"（使える値: {DescribeChoices(AndroidSystemBarsSetting.Choices)}）。\"{systemBars}\" として扱います。");
        }
        var appCategory = AndroidAppCategorySetting.Normalize(settings.AppCategory);
        if (!string.IsNullOrWhiteSpace(settings.AppCategory) && !AndroidAppCategorySetting.IsKnown(settings.AppCategory))
        {
            warnings.Add($"{AndroidAppSettings.AppCategoryKey}=\"{settings.AppCategory}\" は知らない値です" +
                         $"（使える値: {DescribeChoices(AndroidAppCategorySetting.Choices)}）。\"{appCategory}\" として扱います。");
        }

        return new AndroidPlatformFeatureSet
        {
            Features = features,
            UnknownFeatures = unknown,
            Permissions = MergePermissions(features),
            ApplicationElements = features.SelectMany(f => f.ApplicationElements).ToList(),
            DeepLinks = deepLinks,
            SystemBars = systemBars,
            AppCategory = appCategory,
            PredictiveBack = settings.PredictiveBack == true,
            Warnings = warnings,
            Errors = errors,
        };
    }

    /// <summary>
    /// features の名前を表と照らし、有効な機能（表の順）と知らない名前（書かれた表記・書かれた順・重なりなし）に分ける。
    /// </summary>
    private static (List<AndroidPlatformFeature> Features, List<string> Unknown) SelectFeatures(
        IReadOnlyList<string>? requested, AndroidPlatformFeatureCatalog catalog)
    {
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new List<string>();
        foreach (var raw in requested ?? Array.Empty<string>())
        {
            var name = AndroidPlatformFeatureCatalog.NormalizeName(raw);
            if (name is null) continue;
            if (catalog.Find(name) is not null)
            {
                enabled.Add(name);
            }
            else if (!unknown.Contains(raw.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                unknown.Add(raw.Trim());
            }
        }
        return (catalog.Features.Where(f => enabled.Contains(f.Name)).ToList(), unknown);
    }

    /// <summary>
    /// ディープリンクを決める（intent-filter を作る機能が有効なときだけ。検査に通った件だけ・重なりを除く）。
    /// </summary>
    private static List<AndroidDeepLinkSetting> ResolveDeepLinks(
        IReadOnlyList<AndroidDeepLinkSetting>? configured, IReadOnlyList<AndroidPlatformFeature> features,
        AndroidPlatformFeatureCatalog catalog, List<string> errors, List<string> warnings)
    {
        var links = configured?.Where(link => !link.IsBlank).ToList() ?? new List<AndroidDeepLinkSetting>();
        var filterFeature = catalog.Features.FirstOrDefault(f => f.DeepLinkFilters)?.Name;
        var result = new List<AndroidDeepLinkSetting>();
        if (!features.Any(f => f.DeepLinkFilters))
        {
            if (links.Count > 0)
            {
                warnings.Add($"{AndroidAppSettings.DeepLinksKey} に {links.Count} 件ありますが、{AndroidAppSettings.FeaturesKey} に " +
                             $"{filterFeature ?? "ディープリンクの機能"} が無いため intent-filter を入れません。");
            }
            return result;
        }
        if (links.Count == 0)
        {
            warnings.Add($"{AndroidAppSettings.FeaturesKey} に {filterFeature} がありますが、{AndroidAppSettings.DeepLinksKey} が 1 件もありません" +
                         "（intent-filter は入りません）。");
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < links.Count; index++)
        {
            var link = links[index];
            var errorsBefore = errors.Count;
            AndroidDeepLinkRules.Check(link, index + 1, errors, warnings);
            if (errors.Count > errorsBefore) continue;
            if (!seen.Add(AndroidDeepLinkRules.IdentityKey(link)))
            {
                warnings.Add($"ディープリンク {index + 1} 件目（{link.Describe()}）は前の件と同じなので 1 つにまとめます。");
                continue;
            }
            result.Add(link);
        }
        return result;
    }

    /// <summary>
    /// 有効な機能の権限を表の順に集め、重なりを除く（同じ権限の maxSdkVersion は広いほうへ寄せる）。
    /// </summary>
    /// <param name="features">有効な機能（表の順）。</param>
    /// <returns>権限。</returns>
    public static IReadOnlyList<AndroidManifestPermission> MergePermissions(IEnumerable<AndroidPlatformFeature> features)
    {
        var merged = new List<AndroidManifestPermission>();
        foreach (var permission in features.SelectMany(f => f.Permissions))
        {
            var index = merged.FindIndex(p => string.Equals(p.Name, permission.Name, StringComparison.Ordinal));
            if (index < 0)
            {
                merged.Add(permission);
                continue;
            }
            // どちらかが無制限（maxSdkVersion なし）なら無制限、両方あれば大きいほう（要る機能がある API レベルでは必ず要求する）
            var existing = merged[index];
            var widened = existing.MaxSdkVersion is int a && permission.MaxSdkVersion is int b ? Math.Max(a, b) : (int?)null;
            merged[index] = existing with { MaxSdkVersion = widened };
        }
        return merged;
    }

    /// <summary>選べる値の説明（a / b / c）。</summary>
    private static string DescribeChoices((string Value, string Label)[] choices) =>
        string.Join(" / ", choices.Select(c => c.Value));
}
