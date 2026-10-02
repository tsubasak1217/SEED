// ============================================================
//  ThumbnailImage.cs — 撮った画像から見本の画像を作る（読み込み・切り出し・縮小・PNG の書き出し）
//
//  【縮め方】
//  面積の平均（出力の 1 画素が覆う元の画素を、覆う面積の割合で重みづけして平均する）。
//  平均は線形の光の量で行う（sRGB → 線形 → 平均 → sRGB）。暗い地に細い明るい線（文字・枠）が乗る UI を縮めても、
//  線が暗く沈まない。撮るときに 1 dp を 1.5 画素で描いているので、縮めると文字の縁が滑らかになる。
//
//  【書き出し】
//  α の無い 24 ビットの PNG（舞台の背景は不透明なので α は要らない。ファイルも小さくなる）。
//  PNG の読み書きだけに WPF の画像の機能（WIC）を使う。窓は作らない。
// ============================================================

using System.Windows.Media;
using System.Windows.Media.Imaging;
using SEEDEditor.Tools.SeedTemplateThumbnails.Stage;

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Imaging;

/// <summary>BGRA の 8 ビットの画像（読み込んだ元の画像）。</summary>
/// <param name="Width">幅（画素）。</param>
/// <param name="Height">高さ（画素）。</param>
/// <param name="Pixels">画素（B, G, R, A の順・行は詰めて並ぶ）。</param>
public sealed record BgraImage(int Width, int Height, byte[] Pixels);

/// <summary>見本の画像の中身の量（空白・背景だけの見本を見つけるため）。</summary>
/// <param name="ContentRatio">背景の色と違う画素の割合（0..1）。</param>
/// <param name="TouchesEdge">背景と違う画素が縁（外周 1 画素）にあるか（切り出しで部品が切れている疑い）。</param>
public readonly record struct ThumbnailStats(double ContentRatio, bool TouchesEdge);

/// <summary>見本の画像の組み立て。状態を持たない（LUT だけ）。</summary>
public static class ThumbnailImage
{
    /// <summary>BGRA の 1 画素のバイト数。</summary>
    private const int BgraBytes = 4;

    /// <summary>BGR の 1 画素のバイト数（書き出しは α なし）。</summary>
    private const int BgrBytes = 3;

    /// <summary>BGR(A) の画素の中の青・緑・赤のバイトの位置。</summary>
    private const int BlueOffset = 0, GreenOffset = 1, RedOffset = 2;

    /// <summary>sRGB の色の配列（[R, G, B]）の赤・緑・青の位置。</summary>
    private const int SrgbRed = 0, SrgbGreen = 1, SrgbBlue = 2;

    /// <summary>真ん中へ寄せるときの割る数（余りを両側へ半分ずつ）。</summary>
    private const int Halves = 2;

    /// <summary>PNG の解像度（WPF の既定と同じ 96 DPI。画素の数だけが意味を持つ）。</summary>
    private const double Dpi = 96.0;

    /// <summary>8 ビットの最大値。</summary>
    private const double ByteMax = 255.0;

    /// <summary>8 ビットで表せる値の数（変換表の大きさ）。</summary>
    private const int ByteValueCount = 256;

    /// <summary>背景と「違う」とみなす成分の差（0〜255。圧縮・丸めの揺れを無視する）。</summary>
    private const int ContentDifferenceThreshold = 6;

    /// <summary>sRGB → 線形の変換の境・傾き・曲線部の定数（IEC 61966-2-1）。</summary>
    private const double SrgbThreshold = 0.04045, LinearThreshold = 0.0031308, SrgbSlope = 12.92;
    private const double SrgbOffset = 0.055, SrgbScale = 1.055, SrgbGamma = 2.4;

    /// <summary>sRGB の 8 ビット → 線形（0..1）の表。</summary>
    private static readonly double[] ToLinearTable = Enumerable.Range(0, ByteValueCount).Select(v =>
    {
        double c = v / ByteMax;
        return c <= SrgbThreshold ? c / SrgbSlope : Math.Pow((c + SrgbOffset) / SrgbScale, SrgbGamma);
    }).ToArray();

    // ============================================================
    //  読み込み
    // ============================================================

    /// <summary>
    /// PNG を BGRA の 8 ビットで読む。
    /// </summary>
    /// <param name="path">画像のパス。</param>
    /// <returns>画像。</returns>
    public static BgraImage Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32) frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = frame.PixelWidth * BgraBytes;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        return new BgraImage(frame.PixelWidth, frame.PixelHeight, pixels);
    }

    // ============================================================
    //  組み立て
    // ============================================================

    /// <summary>
    /// 撮った画像から一辺 <paramref name="size"/> の見本の画像（BGR）を作る。
    /// </summary>
    /// <param name="source">撮った画像。</param>
    /// <param name="plan">撮る計画（収め方・切り出す正方形・期待した窓の大きさ）。</param>
    /// <param name="size">出力の一辺（画素）。</param>
    /// <param name="letterboxSrgb">contain で余った所を埋める色（sRGB 0〜255）。</param>
    /// <returns>BGR の画素（行は詰めて並ぶ）。</returns>
    public static byte[] Compose(BgraImage source, StagePlan plan, int size, byte[] letterboxSrgb)
    {
        var output = new byte[size * size * BgrBytes];
        // 余りの色で埋める（contain の上下・左右の余白。crop では全面を上書きするので見えない）
        for (int i = 0; i < size * size; i++)
        {
            output[i * BgrBytes + BlueOffset] = letterboxSrgb[SrgbBlue];
            output[i * BgrBytes + GreenOffset] = letterboxSrgb[SrgbGreen];
            output[i * BgrBytes + RedOffset] = letterboxSrgb[SrgbRed];
        }

        if (plan.Fit == ThumbnailFit.Contain)
        {
            // 全体を正方形に収める（長い辺が size になる）
            double scale = Math.Min((double)size / source.Width, (double)size / source.Height);
            int dw = Math.Max(1, (int)Math.Round(source.Width * scale));
            int dh = Math.Max(1, (int)Math.Round(source.Height * scale));
            Resample(source, 0, 0, source.Width, source.Height, output, size, (size - dw) / Halves, (size - dh) / Halves, dw, dh);
        }
        else
        {
            // 切り出す正方形は「期待した窓」の画素で決めてあるので、実際に撮れた大きさとの比で直す
            double rx = (double)source.Width / plan.WindowWidthPx;
            double ry = (double)source.Height / plan.WindowHeightPx;
            double side = Math.Min(plan.Crop.Size * Math.Min(rx, ry), Math.Min(source.Width, source.Height));
            double x = Math.Clamp(plan.Crop.X * rx, 0, source.Width - side);
            double y = Math.Clamp(plan.Crop.Y * ry, 0, source.Height - side);
            Resample(source, x, y, side, side, output, size, 0, 0, size, size);
        }
        return output;
    }

    /// <summary>
    /// 元の矩形（小数の画素）を出力の矩形へ面積の平均で縮める（線形の光の量で平均する）。
    /// </summary>
    private static void Resample(BgraImage src, double sx, double sy, double sw, double sh,
        byte[] dst, int dstStride, int dx, int dy, int dw, int dh)
    {
        var xWeights = AxisWeights(sx, sw, dw, src.Width);
        var yWeights = AxisWeights(sy, sh, dh, src.Height);
        for (int oy = 0; oy < dh; oy++)
        {
            for (int ox = 0; ox < dw; ox++)
            {
                double b = 0, g = 0, r = 0, total = 0;
                foreach (var (iy, wy) in yWeights[oy])
                {
                    int row = iy * src.Width * BgraBytes;
                    foreach (var (ix, wx) in xWeights[ox])
                    {
                        double w = wx * wy;
                        int p = row + ix * BgraBytes;
                        b += ToLinearTable[src.Pixels[p + BlueOffset]] * w;
                        g += ToLinearTable[src.Pixels[p + GreenOffset]] * w;
                        r += ToLinearTable[src.Pixels[p + RedOffset]] * w;
                        total += w;
                    }
                }
                if (total <= 0) continue;
                int o = ((dy + oy) * dstStride + (dx + ox)) * BgrBytes;
                dst[o + BlueOffset] = ToSrgbByte(b / total);
                dst[o + GreenOffset] = ToSrgbByte(g / total);
                dst[o + RedOffset] = ToSrgbByte(r / total);
            }
        }
    }

    /// <summary>
    /// 1 つの軸で、出力の各画素が覆う元の画素と、その覆う長さ（重み）を求める。
    /// </summary>
    /// <param name="start">元の矩形の始まり（小数の画素）。</param>
    /// <param name="length">元の矩形の長さ（小数の画素）。</param>
    /// <param name="count">出力の画素の数。</param>
    /// <param name="limit">元の画像の大きさ（はみ出さない）。</param>
    /// <returns>出力の画素ごとの（元の画素の番号, 重み）の並び。</returns>
    private static List<(int Index, double Weight)>[] AxisWeights(double start, double length, int count, int limit)
    {
        var result = new List<(int, double)>[count];
        double step = length / count;
        for (int i = 0; i < count; i++)
        {
            double a = start + i * step, b = a + step;
            var list = new List<(int, double)>();
            int first = Math.Max(0, (int)Math.Floor(a));
            int last = Math.Min(limit - 1, (int)Math.Ceiling(b) - 1);
            for (int p = first; p <= last; p++)
            {
                double covered = Math.Min(b, p + 1) - Math.Max(a, p);
                if (covered > 0) list.Add((p, covered));
            }
            result[i] = list;
        }
        return result;
    }

    /// <summary>線形（0..1）を sRGB の 8 ビットへ。</summary>
    private static byte ToSrgbByte(double linear)
    {
        double c = Math.Clamp(linear, 0, 1);
        double s = c <= LinearThreshold ? c * SrgbSlope : SrgbScale * Math.Pow(c, 1 / SrgbGamma) - SrgbOffset;
        return (byte)Math.Clamp(Math.Round(s * ByteMax), 0, ByteMax);
    }

    // ============================================================
    //  中身の量
    // ============================================================

    /// <summary>
    /// 見本の画像の中身の量（背景・余白のどちらの色とも違う画素の割合・縁にかかっているか）を数える。
    /// </summary>
    /// <param name="bgr">BGR の画素。</param>
    /// <param name="size">一辺（画素）。</param>
    /// <param name="emptyColorsSrgb">「中身ではない」とみなす色（sRGB 0〜255。舞台の背景と余白の色）。</param>
    /// <returns>中身の量。</returns>
    public static ThumbnailStats Measure(byte[] bgr, int size, params byte[][] emptyColorsSrgb)
    {
        int content = 0;
        bool edge = false;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * BgrBytes;
                if (emptyColorsSrgb.Any(c => Near(bgr, o, c))) continue;
                content++;
                if (x == 0 || y == 0 || x == size - 1 || y == size - 1) edge = true;
            }
        }
        return new ThumbnailStats((double)content / (size * size), edge);
    }

    /// <summary>BGR の 1 画素が sRGB の色とほぼ同じか（圧縮・丸めの揺れは無視する）。</summary>
    private static bool Near(byte[] bgr, int offset, byte[] srgb) =>
        Math.Abs(bgr[offset + RedOffset] - srgb[SrgbRed]) <= ContentDifferenceThreshold
        && Math.Abs(bgr[offset + GreenOffset] - srgb[SrgbGreen]) <= ContentDifferenceThreshold
        && Math.Abs(bgr[offset + BlueOffset] - srgb[SrgbBlue]) <= ContentDifferenceThreshold;

    // ============================================================
    //  書き出し
    // ============================================================

    /// <summary>
    /// BGR の画素を α の無い PNG に書く（フォルダが無ければ作る）。
    /// </summary>
    /// <param name="path">書き出し先。</param>
    /// <param name="bgr">BGR の画素。</param>
    /// <param name="width">幅（画素）。</param>
    /// <param name="height">高さ（画素）。</param>
    public static void SavePng(string path, byte[] bgr, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bitmap = BitmapSource.Create(width, height, Dpi, Dpi, PixelFormats.Bgr24, null, bgr, width * BgrBytes);
        var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        // いったん隣へ書いてから置き換える（書きかけの画像をエディタの窓が読まないように）
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            encoder.Save(stream);
        File.Move(temp, path, overwrite: true);
    }
}
