// ============================================================
//  PngDecoder.cs — PNG を 8 bit の RGBA の画像にする（ランチャーのアイコンの元の画像を読む。段階D）
//
//  PNG の仕様の全部の形を読む: 色の種類（灰色・RGB・パレット・灰色＋アルファ・RGBA）× ビットの深さ（1/2/4/8/16）、
//  tRNS（パレットのアルファ・灰色 / RGB の透明色）、Adam7 インターレース。16 bit は上位 8 bit にする。
//  色の管理（gAMA・iCCP・sRGB 等）は読み飛ばす（アイコンの縮小には要らない）。
//  圧縮は .NET 標準の ZLibStream（System.IO.Compression）で解く。NuGet は足さない。
//
//  【守り】大きすぎる画像（辺 MaxDimension 超・画素 MaxPixels 超）は読まない（アイコンの元に数億画素は要らず、
//  展開でメモリを使い切らないように）。壊れたファイルは PngFormatException（何が悪いかを書いた説明）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SEEDEditor.Android.Icons;

/// <summary>PNG の読み取り。</summary>
public static class PngDecoder
{
    /// <summary>読む画像の辺の上限（画素）。</summary>
    public const int MaxDimension = 16384;

    /// <summary>読む画像の画素数の上限（RGBA で 256 MiB）。</summary>
    public const long MaxPixels = 64L * 1024 * 1024;

    /// <summary>1 バイトのビット数。</summary>
    private const int BitsPerByte = 8;

    /// <summary>16 bit の標本のバイト数。</summary>
    private const int SixteenBitSampleBytes = 2;

    /// <summary>16 bit の深さ。</summary>
    private const int SixteenBitDepth = 16;

    /// <summary>8 bit の深さ。</summary>
    private const int EightBitDepth = 8;

    /// <summary>パレット 1 項目のバイト数（R, G, B）。</summary>
    private const int PaletteEntryBytes = 3;

    /// <summary>Adam7 の 7 回の走査（開始 x・開始 y・x の間隔・y の間隔）。仕様の表。</summary>
    private static readonly (int X, int Y, int StepX, int StepY)[] Adam7Passes =
    {
        (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2),
    };

    /// <summary>
    /// PNG のバイト列を読む。
    /// </summary>
    /// <param name="data">PNG のファイルの中身。</param>
    /// <returns>8 bit の RGBA の画像。</returns>
    /// <exception cref="PngFormatException">PNG として読めないとき。</exception>
    public static RgbaImage Decode(byte[] data)
    {
        var header = ReadChunks(data, out var palette, out var transparency, out var compressed);
        var raw = Inflate(compressed, ExpectedRawLength(header));
        var image = new RgbaImage(header.Width, header.Height);
        var context = new SampleContext(header, palette, transparency);

        if (header.Interlace == PngFormat.InterlaceNone)
        {
            DecodePass(raw, 0, header.Width, header.Height, context, (x, y) => (x, y), image);
        }
        else
        {
            var offset = 0;
            foreach (var pass in Adam7Passes)
            {
                var (passWidth, passHeight) = PassSize(header.Width, header.Height, pass);
                if (passWidth == 0 || passHeight == 0) continue;
                offset = DecodePass(raw, offset, passWidth, passHeight, context,
                    (x, y) => (pass.X + x * pass.StepX, pass.Y + y * pass.StepY), image);
            }
        }
        return image;
    }

    // ── チャンク ──────────────────────────────────────────

    /// <summary>IHDR の中身。</summary>
    private sealed record Header(int Width, int Height, int BitDepth, byte ColorType, byte Interlace)
    {
        /// <summary>1 画素の標本の数。</summary>
        public int Channels => ColorType switch
        {
            PngFormat.ColorTypeRgb => 3,
            PngFormat.ColorTypeGrayAlpha => 2,
            PngFormat.ColorTypeRgba => 4,
            _ => 1,
        };

        /// <summary>1 画素のビット数。</summary>
        public int BitsPerPixel => Channels * BitDepth;

        /// <summary>フィルタの「左」の距離（バイト。1 バイト未満の画素は 1）。</summary>
        public int FilterUnit => Math.Max(1, BitsPerPixel / BitsPerByte);

        /// <summary>幅 w の 1 行のバイト数（フィルタの 1 バイトを除く）。</summary>
        public int RowBytes(int w) => (int)(((long)w * BitsPerPixel + BitsPerByte - 1) / BitsPerByte);
    }

    /// <summary>チャンクを順に読み、見出し・パレット・透明度・圧縮データを集める。</summary>
    private static Header ReadChunks(byte[] data, out byte[]? palette, out byte[]? transparency, out byte[] compressed)
    {
        var signature = PngFormat.Signature;
        if (data.Length < signature.Length || !data.AsSpan(0, signature.Length).SequenceEqual(signature))
        {
            throw new PngFormatException("PNG の署名がありません（PNG ではないファイルです）。");
        }

        Header? header = null;
        palette = null;
        transparency = null;
        using var idat = new MemoryStream();
        var position = signature.Length;
        var sawEnd = false;
        while (position + PngFormat.ChunkFieldLength * 2 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
            var type = Encoding.ASCII.GetString(data, position + PngFormat.ChunkFieldLength, PngFormat.ChunkFieldLength);
            var bodyStart = position + PngFormat.ChunkFieldLength * 2;
            if (length > int.MaxValue || bodyStart + (long)length + PngFormat.ChunkFieldLength > data.Length)
            {
                throw new PngFormatException($"PNG のチャンク {type} が途中で切れています（ファイルが壊れています）。");
            }
            var body = data.AsSpan(bodyStart, (int)length);
            switch (type)
            {
                case PngFormat.HeaderChunk:
                    header = ParseHeader(body);
                    break;
                case PngFormat.PaletteChunk:
                    palette = body.ToArray();
                    break;
                case PngFormat.TransparencyChunk:
                    transparency = body.ToArray();
                    break;
                case PngFormat.DataChunk:
                    idat.Write(body);
                    break;
                case PngFormat.EndChunk:
                    sawEnd = true;
                    break;
            }
            if (sawEnd) break;
            position = bodyStart + (int)length + PngFormat.ChunkFieldLength;
        }

        if (header is null) throw new PngFormatException("PNG の見出し（IHDR）がありません。");
        if (idat.Length == 0) throw new PngFormatException("PNG に画像のデータ（IDAT）がありません。");
        if (header.ColorType == PngFormat.ColorTypePalette && (palette is null || palette.Length < PaletteEntryBytes))
        {
            throw new PngFormatException("パレットの PNG にパレット（PLTE）がありません。");
        }
        compressed = idat.ToArray();
        return header;
    }

    /// <summary>IHDR を読んで検査する。</summary>
    private static Header ParseHeader(ReadOnlySpan<byte> body)
    {
        if (body.Length < PngFormat.HeaderLength) throw new PngFormatException("PNG の見出し（IHDR）が短すぎます。");
        var width = BinaryPrimitives.ReadUInt32BigEndian(body);
        var height = BinaryPrimitives.ReadUInt32BigEndian(body[PngFormat.ChunkFieldLength..]);
        var bitDepth = body[PngFormat.HeaderBitDepthOffset];
        var colorType = body[PngFormat.HeaderColorTypeOffset];
        var interlace = body[PngFormat.HeaderInterlaceOffset];
        if (width == 0 || height == 0 || width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
        {
            throw new PngFormatException($"PNG の大きさ {width}x{height} は扱えません（辺 {MaxDimension} 画素・{MaxPixels} 画素まで）。");
        }
        var validDepth = colorType switch
        {
            PngFormat.ColorTypeGray => bitDepth is 1 or 2 or 4 or 8 or 16,
            PngFormat.ColorTypePalette => bitDepth is 1 or 2 or 4 or 8,
            PngFormat.ColorTypeRgb or PngFormat.ColorTypeGrayAlpha or PngFormat.ColorTypeRgba => bitDepth is 8 or 16,
            _ => false,
        };
        if (!validDepth) throw new PngFormatException($"PNG の色の種類 {colorType}・ビットの深さ {bitDepth} の組み合わせは仕様にありません。");
        if (interlace != PngFormat.InterlaceNone && interlace != PngFormat.InterlaceAdam7)
        {
            throw new PngFormatException($"PNG のインターレースの方式 {interlace} は仕様にありません。");
        }
        return new Header((int)width, (int)height, bitDepth, colorType, interlace);
    }

    // ── 展開とフィルタ ────────────────────────────────────

    /// <summary>フィルタの 1 バイトを含む、展開後のデータの長さ（インターレースの走査ごとの合計）。</summary>
    private static long ExpectedRawLength(Header header)
    {
        if (header.Interlace == PngFormat.InterlaceNone)
        {
            return (long)header.Height * (1 + header.RowBytes(header.Width));
        }
        long total = 0;
        foreach (var pass in Adam7Passes)
        {
            var (w, h) = PassSize(header.Width, header.Height, pass);
            if (w > 0 && h > 0) total += (long)h * (1 + header.RowBytes(w));
        }
        return total;
    }

    /// <summary>zlib を解く（期待の長さより短ければ壊れている）。</summary>
    private static byte[] Inflate(byte[] compressed, long expectedLength)
    {
        var raw = new byte[expectedLength];
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            var read = 0;
            while (read < raw.Length)
            {
                var n = zlib.Read(raw, read, raw.Length - read);
                if (n == 0) break;
                read += n;
            }
            if (read < raw.Length) throw new PngFormatException("PNG の画像のデータが途中で切れています（ファイルが壊れています）。");
        }
        catch (InvalidDataException ex)
        {
            throw new PngFormatException($"PNG の画像のデータを展開できません（ファイルが壊れています）: {ex.Message}");
        }
        return raw;
    }

    /// <summary>Adam7 の 1 回の走査の大きさ。</summary>
    private static (int Width, int Height) PassSize(int width, int height, (int X, int Y, int StepX, int StepY) pass) =>
        (width > pass.X ? (width - pass.X + pass.StepX - 1) / pass.StepX : 0,
         height > pass.Y ? (height - pass.Y + pass.StepY - 1) / pass.StepY : 0);

    /// <summary>
    /// 1 回の走査（インターレース無しなら画像全体）のフィルタを戻して画素へ置く。
    /// </summary>
    /// <returns>次の走査のデータの位置。</returns>
    private static int DecodePass(
        byte[] raw, int offset, int width, int height, SampleContext context, Func<int, int, (int X, int Y)> place, RgbaImage image)
    {
        var header = context.Header;
        var rowBytes = header.RowBytes(width);
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[offset++];
            Array.Copy(raw, offset, current, 0, rowBytes);
            offset += rowBytes;
            Unfilter(filter, current, previous, header.FilterUnit);
            for (var x = 0; x < width; x++)
            {
                var (ix, iy) = place(x, y);
                var color = context.Pixel(current, x);
                var i = (iy * image.Width + ix) * RgbaImage.BytesPerPixel;
                image.Pixels[i] = color.R;
                image.Pixels[i + 1] = color.G;
                image.Pixels[i + 2] = color.B;
                image.Pixels[i + 3] = color.A;
            }
            (previous, current) = (current, previous);
        }
        return offset;
    }

    /// <summary>1 行のフィルタを戻す（仕様の 5 種類）。</summary>
    private static void Unfilter(byte filter, byte[] row, byte[] previous, int unit)
    {
        switch (filter)
        {
            case PngFormat.FilterNone:
                return;
            case PngFormat.FilterSub:
                for (var i = unit; i < row.Length; i++) row[i] = (byte)(row[i] + row[i - unit]);
                return;
            case PngFormat.FilterUp:
                for (var i = 0; i < row.Length; i++) row[i] = (byte)(row[i] + previous[i]);
                return;
            case PngFormat.FilterAverage:
                for (var i = 0; i < row.Length; i++)
                {
                    var left = i >= unit ? row[i - unit] : 0;
                    row[i] = (byte)(row[i] + ((left + previous[i]) >> 1));
                }
                return;
            case PngFormat.FilterPaeth:
                for (var i = 0; i < row.Length; i++)
                {
                    var left = i >= unit ? row[i - unit] : (byte)0;
                    var upLeft = i >= unit ? previous[i - unit] : (byte)0;
                    row[i] = (byte)(row[i] + PngFormat.Paeth(left, previous[i], upLeft));
                }
                return;
            default:
                throw new PngFormatException($"PNG の行のフィルタ {filter} は仕様にありません（ファイルが壊れています）。");
        }
    }

    // ── 標本 → RGBA ───────────────────────────────────────

    /// <summary>行から画素を読むための材料（見出し・パレット・透明度）。</summary>
    private sealed class SampleContext
    {
        public Header Header { get; }
        private readonly byte[]? _palette;
        private readonly byte[]? _transparency;
        private readonly int _maxSample;

        public SampleContext(Header header, byte[]? palette, byte[]? transparency)
        {
            Header = header;
            _palette = palette;
            _transparency = transparency;
            _maxSample = (1 << header.BitDepth) - 1;
        }

        /// <summary>行の x 番目の画素を RGBA にする。</summary>
        public RgbaColor Pixel(byte[] row, int x)
        {
            switch (Header.ColorType)
            {
                case PngFormat.ColorTypePalette:
                {
                    var index = Sample(row, x, 0);
                    var p = index * PaletteEntryBytes;
                    if (p + PaletteEntryBytes > _palette!.Length) return RgbaColor.Transparent;
                    var alpha = _transparency is not null && index < _transparency.Length ? _transparency[index] : byte.MaxValue;
                    return new RgbaColor(_palette[p], _palette[p + 1], _palette[p + 2], alpha);
                }
                case PngFormat.ColorTypeGray:
                {
                    var gray = Sample(row, x, 0);
                    var level = ToEight(gray);
                    var alpha = _transparency is { Length: >= SixteenBitSampleBytes } && gray == BinaryPrimitives.ReadUInt16BigEndian(_transparency)
                        ? (byte)0 : byte.MaxValue;
                    return new RgbaColor(level, level, level, alpha);
                }
                case PngFormat.ColorTypeRgb:
                {
                    int r = Sample(row, x, 0), g = Sample(row, x, 1), b = Sample(row, x, 2);
                    var transparent = _transparency is { Length: >= SixteenBitSampleBytes * 3 }
                        && r == BinaryPrimitives.ReadUInt16BigEndian(_transparency)
                        && g == BinaryPrimitives.ReadUInt16BigEndian(_transparency.AsSpan(SixteenBitSampleBytes))
                        && b == BinaryPrimitives.ReadUInt16BigEndian(_transparency.AsSpan(SixteenBitSampleBytes * 2));
                    return new RgbaColor(ToEight(r), ToEight(g), ToEight(b), transparent ? (byte)0 : byte.MaxValue);
                }
                case PngFormat.ColorTypeGrayAlpha:
                {
                    var level = ToEight(Sample(row, x, 0));
                    return new RgbaColor(level, level, level, ToEight(Sample(row, x, 1)));
                }
                default:
                    return new RgbaColor(ToEight(Sample(row, x, 0)), ToEight(Sample(row, x, 1)), ToEight(Sample(row, x, 2)), ToEight(Sample(row, x, 3)));
            }
        }

        /// <summary>行の x 番目の画素の channel 番目の標本（元のビットの深さのままの値）。</summary>
        private int Sample(byte[] row, int x, int channel)
        {
            var depth = Header.BitDepth;
            if (depth == SixteenBitDepth)
            {
                var i = (x * Header.Channels + channel) * SixteenBitSampleBytes;
                return (row[i] << BitsPerByte) | row[i + 1];
            }
            if (depth == EightBitDepth) return row[x * Header.Channels + channel];

            // 1 バイト未満（灰色・パレットだけ。標本は 1 つ）: 上位のビットから詰まっている
            var bit = x * depth;
            var shift = BitsPerByte - depth - bit % BitsPerByte;
            return (row[bit / BitsPerByte] >> shift) & _maxSample;
        }

        /// <summary>元のビットの深さの値を 8 bit にする（16 bit は上位 8 bit、8 bit 未満は 0〜255 へ伸ばす）。</summary>
        private byte ToEight(int sample) => Header.BitDepth switch
        {
            SixteenBitDepth => (byte)(sample >> BitsPerByte),
            EightBitDepth => (byte)sample,
            _ => (byte)(sample * byte.MaxValue / _maxSample),
        };
    }
}
