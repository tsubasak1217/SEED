// ============================================================
//  RgbaImage.cs — 8 bit RGBA の画像（ランチャーのアイコンの生成に使う最小限の画像の入れ物。段階D）
//
//  画素は行ごとに左上から R, G, B, A の順（アルファは乗算していない値）。
//  PNG の読み書き（PngDecoder / PngEncoder）・縮小と拡大（ImageResampler）・重ね合わせ（DrawOver）だけを持つ。
//  WPF の BitmapDecoder や System.Drawing（Windows 専用の NuGet）を使わないのは、SeedAndroid（net10.0 のコンソール）と
//  単体テストからも同じコードを使うため（docs/android.md §24）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Globalization;

namespace SEEDEditor.Android.Icons;

/// <summary>8 bit の RGBA の色（アルファは乗算していない）。</summary>
/// <param name="R">赤。</param>
/// <param name="G">緑。</param>
/// <param name="B">青。</param>
/// <param name="A">不透明度。</param>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A)
{
    /// <summary>不透明の白（アイコンの背景色の既定値）。</summary>
    public static readonly RgbaColor White = new(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);

    /// <summary>透明。</summary>
    public static readonly RgbaColor Transparent = new(0, 0, 0, 0);

    /// <summary>#RRGGBB の 16 進の桁数。</summary>
    private const int RgbHexLength = 6;

    /// <summary>#AARRGGBB の 16 進の桁数（Android の色の書式）。</summary>
    private const int ArgbHexLength = 8;

    /// <summary>1 成分の 16 進の桁数。</summary>
    private const int ComponentHexLength = 2;

    /// <summary>色の書式の先頭の文字。</summary>
    private const char HexPrefix = '#';

    /// <summary>
    /// Android の色の書式（#RRGGBB か #AARRGGBB）を読む。
    /// </summary>
    /// <param name="text">文字列（前後の空白は無視）。</param>
    /// <param name="color">読めた色。</param>
    /// <returns>読めたら true。</returns>
    public static bool TryParse(string? text, out RgbaColor color)
    {
        color = default;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != HexPrefix) return false;
        var hex = trimmed[1..];
        if (hex.Length != RgbHexLength && hex.Length != ArgbHexLength) return false;
        if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)) return false;

        var offset = 0;
        var a = byte.MaxValue;
        if (hex.Length == ArgbHexLength)
        {
            a = Component(hex, offset);
            offset += ComponentHexLength;
        }
        var r = Component(hex, offset);
        var g = Component(hex, offset + ComponentHexLength);
        var b = Component(hex, offset + ComponentHexLength * 2);
        color = new RgbaColor(r, g, b, a);
        return true;
    }

    /// <summary>Android の色の書式（不透明なら #RRGGBB、そうでなければ #AARRGGBB。大文字）。</summary>
    /// <returns>文字列。</returns>
    public string ToAndroidHex() => A == byte.MaxValue
        ? $"{HexPrefix}{R:X2}{G:X2}{B:X2}"
        : $"{HexPrefix}{A:X2}{R:X2}{G:X2}{B:X2}";

    /// <summary>16 進 2 桁を 1 成分として読む。</summary>
    private static byte Component(string hex, int offset) =>
        byte.Parse(hex.AsSpan(offset, ComponentHexLength), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
}

/// <summary>8 bit の RGBA の画像。</summary>
public sealed class RgbaImage
{
    /// <summary>1 画素のバイト数（R, G, B, A）。</summary>
    public const int BytesPerPixel = 4;

    /// <summary>色の成分の数（R, G, B。アルファは別に扱う）。</summary>
    public const int ColorChannels = 3;

    /// <summary>画素の中のアルファの位置（R, G, B の後ろ）。</summary>
    public const int AlphaOffset = 3;

    /// <summary>幅（画素）。</summary>
    public int Width { get; }

    /// <summary>高さ（画素）。</summary>
    public int Height { get; }

    /// <summary>画素（行ごとに左上から RGBA。長さは Width × Height × 4）。</summary>
    public byte[] Pixels { get; }

    /// <summary>
    /// 透明の画像を作る。
    /// </summary>
    /// <param name="width">幅（1 以上）。</param>
    /// <param name="height">高さ（1 以上）。</param>
    public RgbaImage(int width, int height)
        : this(width, height, new byte[checked(width * height * BytesPerPixel)])
    {
    }

    /// <summary>
    /// 画素を指定して作る。
    /// </summary>
    /// <param name="width">幅（1 以上）。</param>
    /// <param name="height">高さ（1 以上）。</param>
    /// <param name="pixels">画素（長さは width × height × 4）。</param>
    /// <exception cref="ArgumentException">大きさと画素の長さが合わないとき。</exception>
    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException($"画像の大きさが不正です（{width}x{height}）。");
        if (pixels.Length != (long)width * height * BytesPerPixel)
        {
            throw new ArgumentException($"画素の長さ {pixels.Length} が {width}x{height} の RGBA と合いません。");
        }
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>
    /// 1 色で塗りつぶした画像を作る。
    /// </summary>
    /// <param name="width">幅。</param>
    /// <param name="height">高さ。</param>
    /// <param name="color">色。</param>
    /// <returns>画像。</returns>
    public static RgbaImage Filled(int width, int height, RgbaColor color)
    {
        var image = new RgbaImage(width, height);
        for (var i = 0; i < image.Pixels.Length; i += BytesPerPixel)
        {
            image.Pixels[i] = color.R;
            image.Pixels[i + 1] = color.G;
            image.Pixels[i + 2] = color.B;
            image.Pixels[i + 3] = color.A;
        }
        return image;
    }

    /// <summary>画素 1 つを読む。</summary>
    /// <param name="x">横の位置。</param>
    /// <param name="y">縦の位置。</param>
    /// <returns>色。</returns>
    public RgbaColor GetPixel(int x, int y)
    {
        var i = (y * Width + x) * BytesPerPixel;
        return new RgbaColor(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    /// <summary>
    /// 別の画像を (left, top) の位置へ重ねる（アルファによる通常の重ね合わせ。はみ出した部分は捨てる）。
    /// </summary>
    /// <param name="source">重ねる画像。</param>
    /// <param name="left">左の位置。</param>
    /// <param name="top">上の位置。</param>
    public void DrawOver(RgbaImage source, int left, int top)
    {
        const float MaxChannel = byte.MaxValue;
        for (var sy = 0; sy < source.Height; sy++)
        {
            var dy = top + sy;
            if (dy < 0 || dy >= Height) continue;
            for (var sx = 0; sx < source.Width; sx++)
            {
                var dx = left + sx;
                if (dx < 0 || dx >= Width) continue;
                var si = (sy * source.Width + sx) * BytesPerPixel;
                var di = (dy * Width + dx) * BytesPerPixel;
                var sa = source.Pixels[si + AlphaOffset] / MaxChannel;
                if (sa <= 0f) continue;
                var da = Pixels[di + AlphaOffset] / MaxChannel;
                var outA = sa + da * (1f - sa);
                for (var c = 0; c < ColorChannels; c++)
                {
                    // 乗算済みで重ねてから戻す（半透明の縁が黒ずまないように）
                    var premultiplied = source.Pixels[si + c] * sa + Pixels[di + c] * da * (1f - sa);
                    Pixels[di + c] = ToByte(outA > 0f ? premultiplied / outA : 0f);
                }
                Pixels[di + AlphaOffset] = ToByte(outA * MaxChannel);
            }
        }
    }

    /// <summary>0〜255 に丸めて収める。</summary>
    /// <param name="value">値。</param>
    /// <returns>バイト。</returns>
    internal static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), byte.MinValue, byte.MaxValue);
}
