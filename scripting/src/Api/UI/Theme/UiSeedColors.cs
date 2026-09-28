using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  UiSeedColors.cs — 種の色（seed_color）から主の色と選択の色を作る規則（W2-9。docs/ui_theme.md §6。純粋な計算）
//
//  Wake or Pay のテーマ（themes.json）は「種の色 seedColor ＋ 明暗 brightness」だけで決まる（Flutter の ColorScheme.fromSeed）。
//  SEED のテーマの JSON に "seed_color" と "brightness" を書くと、面・文字の色は既定のテーマのその明暗の配色のまま、
//  種の色に依る 6 つのトークンをここの規則で作る（同じテーマに明示したトークンがあればそちらが勝つ）:
//    color.primary          … 種の色。面（color.surface）とのコントラストが 3:1 に足りなければ、暗い方では白へ・明るい方では黒へ寄せる
//    color.on_primary       … 白か黒（白で 4.5:1 に届けば白、届かず黒で届けば黒、どちらも届かなければ比の大きい方）
//    color.selected         … 主の色を面へ混ぜた薄い色（暗い方は面へ 50%、明るい方は面へ 80%）
//    color.on_selected      … 暗い方は白に主の色を 15%、明るい方は主の色を黒へ 50%。選択の色と 4.5:1 に足りなければさらに白・黒へ寄せる
//    color.chart_series_1   … 主の色
//    color.chart_highlight  … 主の色の 12%（棒グラフの選んだ列の背景）
//  混ぜ方は sRGB（CSS の color-mix(in srgb)）。比は WCAG 2 のコントラスト比（UiColorMath.Contrast）。
//  Material 3 の fromSeed（HCT の色調の段）とは違う簡単な規則（色相を保ったまま明るさだけを動かす近似）。
// ============================================================

/// <summary>種の色から主の色と選択の色を作る規則。</summary>
public static class UiSeedColors
{
    /// <summary>主の色と面の最小のコントラスト比（WCAG 2 の大きな文字・図形の 3:1）。</summary>
    public const float MinPrimaryContrast = 3.0f;
    /// <summary>文字と下地の最小のコントラスト比（WCAG 2 の AA の 4.5:1）。</summary>
    public const float MinTextContrast = 4.5f;
    /// <summary>コントラストが足りないとき 1 歩で白・黒へ寄せる割合。</summary>
    private const float AdjustStep = 0.05f;
    /// <summary>寄せる最大の歩数（20 歩 = 白・黒そのもの）。</summary>
    private const int MaxAdjustSteps = 20;
    /// <summary>暗い方の選択の色: 主の色を面へ混ぜる割合。</summary>
    private const float SelectedToSurfaceDark = 0.5f;
    /// <summary>明るい方の選択の色: 主の色を面へ混ぜる割合。</summary>
    private const float SelectedToSurfaceLight = 0.8f;
    /// <summary>暗い方の選択の文字: 白に混ぜる主の色の割合。</summary>
    private const float OnSelectedTintDark = 0.15f;
    /// <summary>明るい方の選択の文字: 主の色を黒へ混ぜる割合。</summary>
    private const float OnSelectedShadeLight = 0.5f;
    /// <summary>選んだ列の背景の濃さ（主の色のアルファ）。</summary>
    private const float HighlightAlpha = 0.12f;

    /// <summary>白。</summary>
    private static readonly Color White = new(1f, 1f, 1f, 1f);
    /// <summary>黒。</summary>
    private static readonly Color Black = new(0f, 0f, 0f, 1f);

    /// <summary>作るトークン（docs とテストの一覧）。</summary>
    public static readonly string[] DerivedTokens =
    {
        UiTokens.ColorPrimary, UiTokens.ColorOnPrimary, UiTokens.ColorSelected, UiTokens.ColorOnSelected,
        ChartTokens.ColorSeries1, ChartTokens.ColorHighlight,
    };

    /// <summary>面の色が引けないときの面（暗い方は黒・明るい方は白）。</summary>
    public static Color DefaultSurface(UiBrightness brightness) => brightness == UiBrightness.Light ? White : Black;

    /// <summary>
    /// 種の色からトークンを作る。
    /// </summary>
    /// <param name="seed">種の色（線形）。</param>
    /// <param name="brightness">作る明暗。</param>
    /// <param name="surface">面の色（線形。基のテーマのその明暗の color.surface）。</param>
    public static Dictionary<string, UiThemeValue> Derive(Color seed, UiBrightness brightness, Color surface)
    {
        bool light = brightness == UiBrightness.Light;
        var opaqueSeed = new Color(seed.r, seed.g, seed.b, 1f);
        // 主の色: 面とのコントラストが足りなければ、面と反対の側（暗い面なら白・明るい面なら黒）へ寄せる
        var primary = EnsureContrast(opaqueSeed, surface, MinPrimaryContrast, light ? Black : White);
        var onPrimary = PickOnColor(primary);
        var selected = UiColorMath.MixSrgb(primary, surface, light ? SelectedToSurfaceLight : SelectedToSurfaceDark);
        var onSelectedStart = light ? UiColorMath.MixSrgb(primary, Black, OnSelectedShadeLight) : UiColorMath.MixSrgb(White, primary, OnSelectedTintDark);
        var onSelected = EnsureContrast(onSelectedStart, selected, MinTextContrast, light ? Black : White);
        return new Dictionary<string, UiThemeValue>
        {
            [UiTokens.ColorPrimary] = UiThemeValue.FromColor(primary),
            [UiTokens.ColorOnPrimary] = UiThemeValue.FromColor(onPrimary),
            [UiTokens.ColorSelected] = UiThemeValue.FromColor(selected),
            [UiTokens.ColorOnSelected] = UiThemeValue.FromColor(onSelected),
            [ChartTokens.ColorSeries1] = UiThemeValue.FromColor(primary),
            [ChartTokens.ColorHighlight] = UiThemeValue.FromColor(UiColorMath.WithAlpha(primary, HighlightAlpha)),
        };
    }

    /// <summary>
    /// <paramref name="color"/> を <paramref name="toward"/> へ少しずつ寄せ、<paramref name="against"/> とのコントラスト比が
    /// <paramref name="minContrast"/> に届いたところで止める（届かなければ寄せきった色）。
    /// </summary>
    public static Color EnsureContrast(Color color, Color against, float minContrast, Color toward)
    {
        var current = color;
        for (int step = 1; step <= MaxAdjustSteps && UiColorMath.Contrast(current, against) < minContrast; step++)
            current = UiColorMath.MixSrgb(color, toward, step * AdjustStep);
        return current;
    }

    /// <summary>下地の上の文字の色（白で届けば白・黒で届けば黒・どちらも届かなければ比の大きい方）。</summary>
    public static Color PickOnColor(Color background)
    {
        float white = UiColorMath.Contrast(White, background);
        if (white >= MinTextContrast) return White;
        float black = UiColorMath.Contrast(Black, background);
        if (black >= MinTextContrast) return Black;
        return white >= black ? White : Black;
    }
}
