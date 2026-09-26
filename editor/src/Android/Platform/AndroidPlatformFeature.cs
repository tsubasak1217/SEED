// ============================================================
//  AndroidPlatformFeature.cs — 機能の表（runtime/android/platform_features.json）の 1 行とその中身の型（W1-2）
//
//  【役割】
//  アプリのプラットフォーム機能（alarm・notifications・deep_links …）1 つが、マニフェストの断片へ何を足すかを表す。
//    AndroidManifestPermission … <uses-permission android:name android:maxSdkVersion>
//    AndroidManifestElement    … <application> の下に置く要素の木（受信機・サービス・activity-alias。W1-3・W1-4 で使う）
//    AndroidPlatformFeature    … 機能 1 つ（名前・表示名・説明・権限・要素・ディープリンクの intent-filter を作るか）
//  表の読み込みは AndroidPlatformFeatureCatalog、断片の組み立ては AndroidPlatformManifestWriter。docs/android.md §25.10。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;

namespace SEEDEditor.Android.Platform;

/// <summary>権限 1 つ（マニフェストの &lt;uses-permission&gt;）。</summary>
/// <param name="Name">権限の名前（例 android.permission.USE_EXACT_ALARM）。</param>
/// <param name="MaxSdkVersion">android:maxSdkVersion（この API レベルまでだけ要求する。null なら付けない）。</param>
public sealed record AndroidManifestPermission(string Name, int? MaxSdkVersion);

/// <summary>
/// マニフェストの要素 1 つ（子を持てる木）。属性は書かれた順に書き出す（生成物を毎回同じバイト列にするため）。
/// 属性名は <c>android:xxx</c>（Android の名前空間）か前置きなしの名前だけ（表の読み込みで確かめる）。
/// </summary>
/// <param name="Tag">要素名（例 receiver）。</param>
/// <param name="Attributes">属性（名前, 値）の並び。</param>
/// <param name="Children">子の要素。</param>
public sealed record AndroidManifestElement(
    string Tag,
    IReadOnlyList<KeyValuePair<string, string>> Attributes,
    IReadOnlyList<AndroidManifestElement> Children);

/// <summary>機能 1 つ（機能の表の 1 行）。</summary>
/// <param name="Name">features に書く名前（小文字英数字と _）。</param>
/// <param name="Label">表示名（プロジェクト設定ウィンドウのチェックボックス）。</param>
/// <param name="Description">説明（チェックボックスの下の文）。</param>
/// <param name="Permissions">足す権限。</param>
/// <param name="ApplicationElements">&lt;application&gt; の下に足す要素。</param>
/// <param name="DeepLinkFilters">true なら android.deep_links を 1 件ずつ MainActivity の intent-filter にする。</param>
public sealed record AndroidPlatformFeature(
    string Name,
    string Label,
    string Description,
    IReadOnlyList<AndroidManifestPermission> Permissions,
    IReadOnlyList<AndroidManifestElement> ApplicationElements,
    bool DeepLinkFilters);
