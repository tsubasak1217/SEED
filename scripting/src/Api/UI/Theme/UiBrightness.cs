namespace SEED.UI;

// ============================================================
//  UiBrightness.cs — 明暗（ライト・ダーク）と、どちらを使うかの選び方（W2-9。docs/ui_theme.md §4。純粋な計算）
//
//  テーマは自分の明暗（brightness）を持ち、light / dark の節があればもう一方の明暗にも対応する（UiThemeDefinition.Supports）。
//  どちらで表示するかは選び方（UiBrightnessMode）で決める:
//    Theme  … テーマの明暗のまま（既定。Wake or Pay のように明暗がテーマごとに決まっているアプリ）
//    System … 端末の設定に従う（Android の Configuration.uiMode・PC は OS の「アプリのモード」。取れなければテーマの明暗）
//    Light / Dark … スクリプトから強制する
//  テーマが対応していない明暗を選んだときは、テーマの明暗のまま（色の組が無いので作れない。UiTheme が 1 度警告する）。
// ============================================================

/// <summary>明暗。</summary>
public enum UiBrightness
{
    /// <summary>暗い（暗い面に明るい文字）。</summary>
    Dark = 0,
    /// <summary>明るい（明るい面に暗い文字）。</summary>
    Light = 1,
}

/// <summary>明暗の選び方。</summary>
public enum UiBrightnessMode
{
    /// <summary>テーマの明暗のまま（既定）。</summary>
    Theme = 0,
    /// <summary>端末の設定に従う（取れなければテーマの明暗）。</summary>
    System = 1,
    /// <summary>明るい方を強制する（テーマが対応していればの話）。</summary>
    Light = 2,
    /// <summary>暗い方を強制する（テーマが対応していればの話）。</summary>
    Dark = 3,
}

/// <summary>明暗の選び方の規則（純粋な計算）。</summary>
public static class UiBrightnessRules
{
    /// <summary>
    /// 選び方が望む明暗（テーマが対応しているかは見ない）。
    /// </summary>
    /// <param name="mode">選び方。</param>
    /// <param name="themeOwn">テーマの明暗。</param>
    /// <param name="system">端末の明暗（取れなければ null）。</param>
    public static UiBrightness Desired(UiBrightnessMode mode, UiBrightness themeOwn, UiBrightness? system) => mode switch
    {
        UiBrightnessMode.System => system ?? themeOwn,
        UiBrightnessMode.Light => UiBrightness.Light,
        UiBrightnessMode.Dark => UiBrightness.Dark,
        _ => themeOwn,
    };

    /// <summary>
    /// 表示する明暗（望む明暗にテーマが対応していなければテーマの明暗）。
    /// </summary>
    /// <param name="mode">選び方。</param>
    /// <param name="themeOwn">テーマの明暗。</param>
    /// <param name="supportsLight">テーマが明るい方に対応しているか。</param>
    /// <param name="supportsDark">テーマが暗い方に対応しているか。</param>
    /// <param name="system">端末の明暗（取れなければ null）。</param>
    public static UiBrightness Resolve(UiBrightnessMode mode, UiBrightness themeOwn, bool supportsLight, bool supportsDark, UiBrightness? system)
    {
        var desired = Desired(mode, themeOwn, system);
        bool supported = desired == UiBrightness.Light ? supportsLight : supportsDark;
        return supported ? desired : themeOwn;
    }

    /// <summary>明暗の JSON の語（"dark" / "light"）。</summary>
    public static string ToWord(UiBrightness brightness) => brightness == UiBrightness.Light ? LightWord : DarkWord;

    /// <summary>JSON の語を明暗にする（知らない語は false）。</summary>
    public static bool TryParse(string? word, out UiBrightness brightness)
    {
        switch (word)
        {
            case DarkWord:
                brightness = UiBrightness.Dark;
                return true;
            case LightWord:
                brightness = UiBrightness.Light;
                return true;
            default:
                brightness = UiBrightness.Dark;
                return false;
        }
    }

    /// <summary>暗い方の語（テーマの JSON の brightness・節の名前）。</summary>
    public const string DarkWord = "dark";

    /// <summary>明るい方の語。</summary>
    public const string LightWord = "light";
}
