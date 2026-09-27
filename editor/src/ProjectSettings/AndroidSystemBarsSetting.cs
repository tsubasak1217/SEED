// ============================================================
//  AndroidSystemBarsSetting.cs — project_settings.json の android.system_bars の値と表示名（W1-2）
//
//  【役割】
//  Android の APK を起動したときのシステムバー（ステータスバー・ナビゲーションバー）の既定の出し方。
//    hidden  … 隠す（既定。従来のゲームの振る舞い。端からのスワイプで一時的に出せる）
//    visible … 出したまま（時刻・電池が見えるべきアプリ向け。バーの分は安全領域 Screen.SafeArea で避ける）
//  SeedAndroid（editor/src/Android/Platform/）がビルドのたびに app/src/seedFeatures/res/values/seed_platform.xml の
//  bool seed_system_bars_visible へ書き、MainActivity（SystemBarsController.java）が起動時に読む。
//  スクリプトから実行中に切り替える API は Window.SetSystemBarsVisible（W1-6。docs/android.md §25.15.3）。デスクトップ（Windows）の実行には影響しない。
//  値の表（ここ）と読み方は ScreenOrientationSetting と同じ形。docs/android.md §25.10。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;

namespace SEEDEditor.ProjectSettings;

/// <summary>
/// プロジェクト設定「システムバー」（project_settings.json の <c>android.system_bars</c>）の値と表示名。
/// </summary>
public static class AndroidSystemBarsSetting
{
    /// <summary>隠す（既定。ゲーム向け）。</summary>
    public const string Hidden = "hidden";

    /// <summary>出したまま（アプリ向け）。</summary>
    public const string Visible = "visible";

    /// <summary>既定値（キーが無い・不明な値のとき）。生成物が無いときの APK の既定（res の false）と同じ。</summary>
    public const string Default = Hidden;

    /// <summary>
    /// 選べる値と表示名（プロジェクト設定ウィンドウのコンボボックスの項目順）。
    /// </summary>
    public static readonly (string Value, string Label)[] Choices =
    {
        (Hidden,  "隠す（ゲーム向け。端からのスワイプで一時的に出せる）"),
        (Visible, "出したまま（アプリ向け。時刻・電池が見える）"),
    };

    /// <summary>
    /// 設定値を正規化する（前後の空白を落として小文字へ。知らない値・空・null は既定値）。
    /// </summary>
    /// <param name="value">project_settings.json から読んだ値。</param>
    /// <returns>Choices のいずれかの値。</returns>
    public static string Normalize(string? value) => IsKnown(value) ? value!.Trim().ToLowerInvariant() : Default;

    /// <summary>
    /// 知っている値か（前後の空白・大文字小文字は区別しない。空・null は「知らない」ではなく未設定なので false）。
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

    /// <summary>APK に焼き込む bool（出したままなら true）。</summary>
    /// <param name="value">設定値（正規化前でよい）。</param>
    /// <returns>seed_system_bars_visible の値。</returns>
    public static bool IsVisible(string? value) => Normalize(value) == Visible;
}
