// ============================================================
//  AndroidAppCategorySetting.cs — project_settings.json の android.app_category の値と表示名（W1-2）
//
//  【役割】
//  Android のアプリの分類（マニフェストの android:appCategory）。語彙は SDK の
//  platforms/android-36/data/res/values/attrs_manifest.xml の enum appCategory と同じ 9 つ（2026-09-27 に確かめた）。
//  既定は game（従来の固定値。Android 16 の「大画面で向き・サイズ変更の制限を無視する」変更の対象外になる）。
//  SeedAndroid が正規化して Gradle へ -Pseed.appCategory=<値> で渡し（既定の game は渡さない）、
//  runtime/android/app/build.gradle.kts の appCategoryValues（同じ語彙。単体テストが突き合わせる）が
//  manifestPlaceholders の seedAppCategory へ入れる。デスクトップ（Windows）の実行には影響しない。docs/android.md §25.10。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;

namespace SEEDEditor.ProjectSettings;

/// <summary>
/// プロジェクト設定「アプリの分類」（project_settings.json の <c>android.app_category</c>）の値と表示名。
/// </summary>
public static class AndroidAppCategorySetting
{
    /// <summary>ゲーム（既定）。</summary>
    public const string Game = "game";

    /// <summary>既定値（キーが無い・不明な値のとき。build.gradle.kts の defaultAppCategory と同じ）。</summary>
    public const string Default = Game;

    /// <summary>
    /// 選べる値と表示名（プロジェクト設定ウィンドウのコンボボックスの項目順。値は attrs_manifest.xml の enum の名前）。
    /// 値を足すときは build.gradle.kts の appCategoryValues にも同じ値を足すこと（単体テストが突き合わせる）。
    /// </summary>
    public static readonly (string Value, string Label)[] Choices =
    {
        (Game,            "ゲーム（game。既定）"),
        ("productivity",  "仕事効率化（productivity。目覚まし・カレンダー・ツール等）"),
        ("audio",         "音声・音楽（audio）"),
        ("video",         "動画（video）"),
        ("image",         "画像・写真（image）"),
        ("social",        "SNS・コミュニケーション（social）"),
        ("news",          "ニュース・雑誌（news）"),
        ("maps",          "地図・ナビ（maps）"),
        ("accessibility", "ユーザー補助（accessibility）"),
    };

    /// <summary>
    /// 設定値を正規化する（前後の空白を落として小文字へ。知らない値・空・null は既定値）。
    /// </summary>
    /// <param name="value">project_settings.json から読んだ値。</param>
    /// <returns>Choices のいずれかの値。</returns>
    public static string Normalize(string? value) => IsKnown(value) ? value!.Trim().ToLowerInvariant() : Default;

    /// <summary>
    /// 知っている値か（前後の空白・大文字小文字は区別しない。空・null は未設定なので false）。
    /// </summary>
    /// <param name="value">設定値。</param>
    /// <returns>Choices のどれかなら true。</returns>
    public static bool IsKnown(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return Array.Exists(Choices, c => string.Equals(c.Value, normalized, StringComparison.Ordinal));
    }

    /// <summary>
    /// 値に対応する Choices の位置（コンボボックスの初期選択用）。知らない値は既定値の位置。
    /// </summary>
    /// <param name="value">設定値（正規化前でよい）。</param>
    /// <returns>Choices の添字。</returns>
    public static int IndexOf(string? value)
    {
        var normalized = Normalize(value);
        return Array.FindIndex(Choices, c => c.Value == normalized);
    }
}
