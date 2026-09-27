using System;
using System.Globalization;

namespace SEED.UI;

// ============================================================
//  UiColorMath.cs — 部品の色の計算（W2-4。純粋な計算）
//
//  SEED の色（Sprite.Color・Text.Color）は線形の値（エディタの色の選び方も線形 ↔ sRGB で表示する）。
//  テーマの JSON の色は sRGB の #RRGGBB なので、読み込むときに線形へ直す（IEC 61966-2-1 の式）。
//  押下の見た目は「状態の重ね色」（Material の state layer）を上から重ねる（アルファの重ね合わせ）。
// ============================================================

/// <summary>部品の色の計算。</summary>
public static class UiColorMath
{
    /// <summary>sRGB → 線形の折れ目（これ以下は直線）。</summary>
    private const float SrgbLinearThreshold = 0.04045f;
    /// <summary>sRGB の直線部の傾き。</summary>
    private const float SrgbLinearSlope = 12.92f;
    /// <summary>sRGB の曲線部のずれ。</summary>
    private const float SrgbOffset = 0.055f;
    /// <summary>sRGB の曲線部のずれ込みの割り算（1 + ずれ）。</summary>
    private const float SrgbScale = 1.055f;
    /// <summary>sRGB の曲線部の指数。</summary>
    private const float SrgbGamma = 2.4f;
    /// <summary>1 チャンネルの最大値（8 bit）。</summary>
    private const float ByteMax = 255f;
    /// <summary>#RRGGBB の長さ（# を除く）。</summary>
    private const int HexRgbLength = 6;
    /// <summary>#RRGGBBAA の長さ（# を除く）。</summary>
    private const int HexRgbaLength = 8;
    /// <summary>1 チャンネルの 16 進の桁数。</summary>
    private const int HexChannelDigits = 2;
    /// <summary>0 とみなすアルファ。</summary>
    private const float AlphaEpsilon = 1e-6f;

    /// <summary>sRGB の 1 チャンネル（0..1）を線形へ。</summary>
    public static float SrgbToLinear(float c)
        => c <= SrgbLinearThreshold ? c / SrgbLinearSlope : MathF.Pow((c + SrgbOffset) / SrgbScale, SrgbGamma);

    /// <summary>
    /// "#RRGGBB" / "#RRGGBBAA"（sRGB）を線形の色にする（アルファはそのまま）。読めなければ false。
    /// </summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = Color.White;
        if (string.IsNullOrEmpty(text) || text[0] != '#') return false;
        var hex = text.AsSpan(1);
        if (hex.Length != HexRgbLength && hex.Length != HexRgbaLength) return false;
        Span<float> ch = stackalloc float[4];
        ch[3] = 1f;
        for (int i = 0; i < hex.Length / HexChannelDigits; i++)
        {
            if (!byte.TryParse(hex.Slice(i * HexChannelDigits, HexChannelDigits), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var b)) return false;
            ch[i] = b / ByteMax;
        }
        color = new Color(SrgbToLinear(ch[0]), SrgbToLinear(ch[1]), SrgbToLinear(ch[2]), ch[3]);
        return true;
    }

    /// <summary>アルファだけを置き換える。</summary>
    public static Color WithAlpha(Color c, float alpha) => new(c.r, c.g, c.b, alpha);

    /// <summary>アルファに掛ける（無効の部品を薄くする）。</summary>
    public static Color FadeAlpha(Color c, float factor) => new(c.r, c.g, c.b, c.a * factor);

    /// <summary>
    /// 下の色の上へ、上の色を濃さ <paramref name="opacity"/> で重ねる（アルファの重ね合わせ。下が透明でも上の色が出る）。
    /// </summary>
    /// <param name="bottom">下の色（部品の塗り）。</param>
    /// <param name="top">重ねる色（状態の重ね色）。</param>
    /// <param name="opacity">重ねる濃さ（0..1）。</param>
    public static Color Over(Color bottom, Color top, float opacity)
    {
        float ta = Math.Clamp(opacity, 0f, 1f) * top.a;
        float outA = ta + bottom.a * (1f - ta);
        if (outA <= AlphaEpsilon) return new Color(0f, 0f, 0f, 0f);
        float Mix(float t, float b) => (t * ta + b * bottom.a * (1f - ta)) / outA;
        return new Color(Mix(top.r, bottom.r), Mix(top.g, bottom.g), Mix(top.b, bottom.b), outA);
    }

    /// <summary>2 色の補間（t は 0..1 に収める）。</summary>
    public static Color Lerp(Color a, Color b, float t) => Color.Lerp(a, b, Math.Clamp(t, 0f, 1f));

    /// <summary>透明（黒のアルファ 0）。</summary>
    public static Color Transparent => new(0f, 0f, 0f, 0f);
}
