// ============================================================
//  ImageResampler.cs — 画像の縮小・拡大（ランチャーのアイコンを各密度の大きさにする。段階D）
//
//  縦と横を別々に処理する（分離可能なフィルタ）:
//    縮小 … 面積平均（出力の 1 画素が覆う元の範囲を、覆う割合で重み付けして平均する。細部がちらつかない）
//    拡大 … 双線形（画素の中心を合わせて隣り合う 2 画素を距離で混ぜる）
//  アルファを乗算した値で混ぜてから戻す（透明な部分の色が縁に滲んで黒ずむのを防ぐ）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.Android.Icons;

/// <summary>画像の縮小・拡大。</summary>
public static class ImageResampler
{
    /// <summary>画素の中心の位置（左端から半画素）。</summary>
    private const double PixelCenter = 0.5;

    /// <summary>重みを捨てる下限（浮動小数の誤差で生じるごく小さな重み）。</summary>
    private const double NegligibleWeight = 1e-9;

    /// <summary>1 成分の最大値。</summary>
    private const float MaxChannel = byte.MaxValue;

    /// <summary>
    /// 画像を指定の大きさにする（同じ大きさなら写しを返す）。
    /// </summary>
    /// <param name="source">元の画像。</param>
    /// <param name="width">幅（1 以上）。</param>
    /// <param name="height">高さ（1 以上）。</param>
    /// <returns>新しい画像。</returns>
    public static RgbaImage Resize(RgbaImage source, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException($"大きさが不正です（{width}x{height}）。");
        if (width == source.Width && height == source.Height)
        {
            return new RgbaImage(width, height, (byte[])source.Pixels.Clone());
        }

        // ── 乗算済みの浮動小数へ ──
        var premultiplied = new float[source.Pixels.Length];
        for (var i = 0; i < source.Pixels.Length; i += RgbaImage.BytesPerPixel)
        {
            var alpha = source.Pixels[i + RgbaImage.AlphaOffset] / MaxChannel;
            for (var c = 0; c < RgbaImage.ColorChannels; c++) premultiplied[i + c] = source.Pixels[i + c] * alpha;
            premultiplied[i + RgbaImage.AlphaOffset] = source.Pixels[i + RgbaImage.AlphaOffset];
        }

        // ── 横 → 縦 ──
        var horizontal = ResampleRows(premultiplied, source.Width, source.Height, AxisWeights(source.Width, width), width);
        var vertical = ResampleColumns(horizontal, width, source.Height, AxisWeights(source.Height, height), height);

        // ── 乗算を戻して 8 bit へ ──
        var result = new RgbaImage(width, height);
        for (var i = 0; i < vertical.Length; i += RgbaImage.BytesPerPixel)
        {
            var alpha = vertical[i + RgbaImage.AlphaOffset];
            var scale = alpha > 0f ? MaxChannel / alpha : 0f;
            for (var c = 0; c < RgbaImage.ColorChannels; c++) result.Pixels[i + c] = RgbaImage.ToByte(vertical[i + c] * scale);
            result.Pixels[i + RgbaImage.AlphaOffset] = RgbaImage.ToByte(alpha);
        }
        return result;
    }

    /// <summary>
    /// 箱の中に縦横比を保って収まる大きさ（どちらかの辺が箱に一致する。各辺 1 画素以上）。
    /// </summary>
    /// <param name="width">元の幅。</param>
    /// <param name="height">元の高さ。</param>
    /// <param name="box">箱の一辺。</param>
    /// <returns>収まる大きさ。</returns>
    public static (int Width, int Height) FitInside(int width, int height, int box)
    {
        var scale = Math.Min((double)box / width, (double)box / height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>1 つの出力画素の重み（元の画素の開始位置と、並んだ重み。和は 1）。</summary>
    /// <param name="Start">最初の元の画素。</param>
    /// <param name="Weights">重み。</param>
    public sealed record Taps(int Start, IReadOnlyList<double> Weights);

    /// <summary>
    /// 1 つの軸の重みを作る（縮小は面積平均、拡大は双線形）。
    /// </summary>
    /// <param name="sourceSize">元の長さ。</param>
    /// <param name="targetSize">出力の長さ。</param>
    /// <returns>出力の画素ごとの重み。</returns>
    public static IReadOnlyList<Taps> AxisWeights(int sourceSize, int targetSize)
    {
        var taps = new List<Taps>(targetSize);
        var ratio = (double)sourceSize / targetSize;
        for (var i = 0; i < targetSize; i++)
        {
            taps.Add(targetSize < sourceSize ? AreaTaps(i, ratio, sourceSize) : LinearTaps(i, ratio, sourceSize));
        }
        return taps;
    }

    /// <summary>面積平均の重み（出力の画素 i が覆う元の範囲 [i×比, (i+1)×比) との重なりの長さ）。</summary>
    private static Taps AreaTaps(int index, double ratio, int sourceSize)
    {
        var begin = index * ratio;
        var end = Math.Min(sourceSize, (index + 1) * ratio);
        var first = (int)Math.Floor(begin);
        var last = Math.Min(sourceSize - 1, (int)Math.Ceiling(end) - 1);
        var weights = new List<double>();
        for (var s = first; s <= last; s++)
        {
            var overlap = Math.Min(end, s + 1) - Math.Max(begin, s);
            weights.Add(overlap > NegligibleWeight ? overlap / (end - begin) : 0.0);
        }
        return new Taps(first, weights);
    }

    /// <summary>双線形の重み（画素の中心を合わせ、両隣の 2 画素を距離で混ぜる。端は端の画素）。</summary>
    private static Taps LinearTaps(int index, double ratio, int sourceSize)
    {
        var position = (index + PixelCenter) * ratio - PixelCenter;
        var clamped = Math.Clamp(position, 0.0, sourceSize - 1);
        var first = (int)Math.Floor(clamped);
        var fraction = clamped - first;
        return first + 1 < sourceSize && fraction > NegligibleWeight
            ? new Taps(first, new[] { 1.0 - fraction, fraction })
            : new Taps(first, new[] { 1.0 });
    }

    /// <summary>横方向に並べ直す（行ごと）。</summary>
    private static float[] ResampleRows(float[] source, int sourceWidth, int height, IReadOnlyList<Taps> taps, int targetWidth)
    {
        const int channels = RgbaImage.BytesPerPixel;
        var result = new float[targetWidth * height * channels];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = y * sourceWidth * channels;
            var targetRow = y * targetWidth * channels;
            for (var x = 0; x < targetWidth; x++)
            {
                var tap = taps[x];
                for (var k = 0; k < tap.Weights.Count; k++)
                {
                    var weight = (float)tap.Weights[k];
                    if (weight == 0f) continue;
                    var s = sourceRow + (tap.Start + k) * channels;
                    var t = targetRow + x * channels;
                    for (var c = 0; c < channels; c++) result[t + c] += source[s + c] * weight;
                }
            }
        }
        return result;
    }

    /// <summary>縦方向に並べ直す（列ごと）。</summary>
    private static float[] ResampleColumns(float[] source, int width, int sourceHeight, IReadOnlyList<Taps> taps, int targetHeight)
    {
        const int channels = RgbaImage.BytesPerPixel;
        var result = new float[width * targetHeight * channels];
        for (var y = 0; y < targetHeight; y++)
        {
            var tap = taps[y];
            for (var k = 0; k < tap.Weights.Count; k++)
            {
                var weight = (float)tap.Weights[k];
                if (weight == 0f) continue;
                var sourceRow = (tap.Start + k) * width * channels;
                var targetRow = y * width * channels;
                for (var i = 0; i < width * channels; i++) result[targetRow + i] += source[sourceRow + i] * weight;
            }
        }
        return result;
    }
}
