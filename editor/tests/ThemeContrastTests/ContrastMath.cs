// ============================================================
//  ContrastMath.cs — WCAG 2.1 のコントラスト比を計算する
//
//  【役割】
//  「背景色 × 文字色」が読めるかどうかを数値で判定する。
//  規格の定義（相対輝度とコントラスト比）をそのまま実装しただけの純関数。
//
//  【なぜ製品コード側に置かないのか】
//  エディタ本体はこの計算を実行時に使わない。使うのは検査だけなので
//  テストプロジェクト側に置き、製品コードに死んだコードを残さない。
// ============================================================

using SEEDEditor.Theme;

namespace SEEDEditor.Tests.ThemeContrast;

/// <summary>
/// WCAG 2.1 のコントラスト比計算。
/// </summary>
public static class ContrastMath
{
    /// <summary>相対輝度の計算で、線形化の分岐に使うしきい値（WCAG 2.1 の定義値）。</summary>
    private const double SRGB_LINEAR_THRESHOLD = 0.03928;

    /// <summary>しきい値以下の区間で使う除数（WCAG 2.1 の定義値）。</summary>
    private const double SRGB_LOW_DIVISOR = 12.92;

    /// <summary>しきい値超の区間で使うオフセット（WCAG 2.1 の定義値）。</summary>
    private const double SRGB_HIGH_OFFSET = 0.055;

    /// <summary>しきい値超の区間で使う除数（WCAG 2.1 の定義値）。</summary>
    private const double SRGB_HIGH_DIVISOR = 1.055;

    /// <summary>しきい値超の区間で使う指数（WCAG 2.1 の定義値）。</summary>
    private const double SRGB_HIGH_EXPONENT = 2.4;

    /// <summary>相対輝度における赤の重み（WCAG 2.1 の定義値）。</summary>
    private const double LUMINANCE_WEIGHT_R = 0.2126;

    /// <summary>相対輝度における緑の重み（WCAG 2.1 の定義値）。</summary>
    private const double LUMINANCE_WEIGHT_G = 0.7152;

    /// <summary>相対輝度における青の重み（WCAG 2.1 の定義値）。</summary>
    private const double LUMINANCE_WEIGHT_B = 0.0722;

    /// <summary>コントラスト比の式に足す定数（WCAG 2.1 の定義値）。</summary>
    private const double CONTRAST_OFFSET = 0.05;

    /// <summary>1 チャンネルの最大値（8 bit）。</summary>
    private const double CHANNEL_MAX = 255.0;

    /// <summary>
    /// 色の相対輝度を求める。
    /// </summary>
    /// <param name="hex">"#RRGGBB"（アルファ付きも可。アルファは無視する）。</param>
    /// <returns>0.0（黒）～1.0（白）の相対輝度。</returns>
    public static double RelativeLuminance(string hex)
    {
        var (_, r, g, b) = SeedColorTable.Parse(hex);
        return LUMINANCE_WEIGHT_R * Linearize(r)
             + LUMINANCE_WEIGHT_G * Linearize(g)
             + LUMINANCE_WEIGHT_B * Linearize(b);
    }

    /// <summary>1 チャンネルを sRGB から線形へ戻す。</summary>
    /// <param name="channel">0-255 のチャンネル値。</param>
    private static double Linearize(byte channel)
    {
        var c = channel / CHANNEL_MAX;
        return c <= SRGB_LINEAR_THRESHOLD
            ? c / SRGB_LOW_DIVISOR
            : Math.Pow((c + SRGB_HIGH_OFFSET) / SRGB_HIGH_DIVISOR, SRGB_HIGH_EXPONENT);
    }

    /// <summary>
    /// 2 色のコントラスト比を求める。
    /// </summary>
    /// <param name="hexA">片方の色。</param>
    /// <param name="hexB">もう片方の色。</param>
    /// <returns>1.0（同じ色）～21.0（黒と白）の比。</returns>
    public static double Ratio(string hexA, string hexB)
    {
        var la = RelativeLuminance(hexA);
        var lb = RelativeLuminance(hexB);
        var hi = Math.Max(la, lb);
        var lo = Math.Min(la, lb);
        return (hi + CONTRAST_OFFSET) / (lo + CONTRAST_OFFSET);
    }
}
