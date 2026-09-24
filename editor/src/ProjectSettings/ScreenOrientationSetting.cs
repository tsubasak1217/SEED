using System;

namespace SEEDEditor.ProjectSettings;

/// <summary>
/// プロジェクト設定「画面の向き」（project_settings.json の <c>screen_orientation</c>）の値と表示名。
///
/// <para>
/// この値はモバイル（Android）の APK を作るときに使われる。SeedAndroid（editor/src/Android/Project/AndroidProjectSettingsReader.cs）が
/// project_settings.json から読んで正規化し、Gradle へ <c>-Pseed.orientation=&lt;値&gt;</c> で渡し、
/// runtime/android/app/build.gradle.kts の変換表がマニフェストの screenOrientation
/// （both=fullSensor / portrait=sensorPortrait / landscape=sensorLandscape）へ差し込む。
/// マニフェストの値への変換表はそちらだけに置き、ここは設定値とエディタの表示名だけを持つ。
/// デスクトップ（Windows）の実行には影響しない（ウィンドウの大きさは解像度設定で決まる）。
/// </para>
/// WPF に依存しない（単体テストのプロジェクトからもリンクされる）。
/// </summary>
public static class ScreenOrientationSetting
{
    /// <summary>縦横どちらも（端末の向きに追従。4 方向）。</summary>
    public const string Both = "both";

    /// <summary>縦に固定（正立・逆さはセンサーに従う）。</summary>
    public const string Portrait = "portrait";

    /// <summary>横に固定（左右どちら向きの横もセンサーに従う）。</summary>
    public const string Landscape = "landscape";

    /// <summary>既定値（キーが無い・不明な値のとき）。Gradle 側の既定値と同じ。</summary>
    public const string Default = Both;

    /// <summary>
    /// 選べる値と表示名（プロジェクト設定ウィンドウのコンボボックスの項目順）。
    /// 値を足すときは build.gradle.kts の変換表にも同じ値を足すこと。
    /// </summary>
    public static readonly (string Value, string Label)[] Choices =
    {
        (Both,      "縦横どちらも（端末の向きに追従）"),
        (Portrait,  "縦に固定"),
        (Landscape, "横に固定"),
    };

    /// <summary>
    /// 設定値を正規化する（前後の空白を落として小文字へ。知らない値・空・null は既定値）。
    /// SeedAndroid・build.gradle.kts と同じ読み方。
    /// </summary>
    /// <param name="value">project_settings.json から読んだ値。</param>
    /// <returns>Choices のいずれかの値。</returns>
    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        foreach (var (choice, _) in Choices)
        {
            if (string.Equals(choice, normalized, StringComparison.Ordinal)) return choice;
        }
        return Default;
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
