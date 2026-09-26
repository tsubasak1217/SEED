// ============================================================
//  AndroidPlatformFeatureSet.cs — プロジェクト設定から決まった「このビルドのプラットフォーム機能」（W1-2）
//
//  【役割】
//  AndroidPlatformFeatureResolver がプロジェクト設定（android.features / deep_links / system_bars / app_category）と
//  機能の表から作る結果。マニフェストの断片の組み立て（AndroidPlatformManifestWriter）・Gradle のプロパティ
//  （seed.appCategory）・ログ・Google Play の要件チェック（Release/AndroidPlatformFeatureChecks）が同じこれを見る。
//  並びは機能の表の順（features に書いた順によらず、生成物が同じバイト列になるように）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.ProjectSettings;

namespace SEEDEditor.Android.Platform;

/// <summary>このビルドのプラットフォーム機能。</summary>
public sealed record AndroidPlatformFeatureSet
{
    /// <summary>有効な機能（表にある名前だけ・表の順）。</summary>
    public IReadOnlyList<AndroidPlatformFeature> Features { get; init; } = Array.Empty<AndroidPlatformFeature>();

    /// <summary>features に書かれたが表に無い名前（書かれた表記のまま。無視する）。</summary>
    public IReadOnlyList<string> UnknownFeatures { get; init; } = Array.Empty<string>();

    /// <summary>マニフェストに入れる権限（重なりを除いた・機能の表の順）。</summary>
    public IReadOnlyList<AndroidManifestPermission> Permissions { get; init; } = Array.Empty<AndroidManifestPermission>();

    /// <summary>&lt;application&gt; の下に入れる要素（機能の表の順）。</summary>
    public IReadOnlyList<AndroidManifestElement> ApplicationElements { get; init; } = Array.Empty<AndroidManifestElement>();

    /// <summary>MainActivity の intent-filter にするディープリンク（検査を通ったもの・重なりを除いたもの）。</summary>
    public IReadOnlyList<AndroidDeepLinkSetting> DeepLinks { get; init; } = Array.Empty<AndroidDeepLinkSetting>();

    /// <summary>システムバーの既定の出し方（正規化済み。AndroidSystemBarsSetting の値）。</summary>
    public string SystemBars { get; init; } = AndroidSystemBarsSetting.Default;

    /// <summary>android:appCategory（正規化済み。AndroidAppCategorySetting の値）。</summary>
    public string AppCategory { get; init; } = AndroidAppCategorySetting.Default;

    /// <summary>注意（ログ・設定ウィンドウに出す。ビルドは続ける）。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>誤り（ビルドを止め、設定ウィンドウの保存も止める）。</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>機能なし・既定（プロジェクトの無い開発用の APK）。</summary>
    public static AndroidPlatformFeatureSet Empty { get; } = new();

    /// <summary>システムバーを出したままにするか（APK の seed_system_bars_visible）。</summary>
    public bool SystemBarsVisible => SystemBars == AndroidSystemBarsSetting.Visible;

    /// <summary>有効な機能の名前（表の順）。</summary>
    public IReadOnlyList<string> FeatureNames => Features.Select(f => f.Name).ToList();

    /// <summary>
    /// マニフェストに入れる権限の名前の集合（Google Play の要件チェックで配布物と比べる）。
    /// </summary>
    public IReadOnlySet<string> PermissionNames => Permissions.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>ログ用の一行（例 機能 alarm, notifications・権限 9・ディープリンク 0・システムバー visible・appCategory productivity）。</summary>
    /// <returns>説明。</returns>
    public string Describe()
    {
        var features = Features.Count == 0 ? "なし" : string.Join(", ", FeatureNames);
        return $"機能 {features}・権限 {Permissions.Count}・部品 {ApplicationElements.Count}・ディープリンク {DeepLinks.Count}・" +
               $"システムバー {SystemBars}・appCategory {AppCategory}";
    }
}
