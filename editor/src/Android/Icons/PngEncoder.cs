// ============================================================
//  PngEncoder.cs — 8 bit の RGBA の画像を PNG にする（ランチャーのアイコンの書き出し。段階D）
//
//  色の種類は RGBA（6）・8 bit・インターレース無し。行ごとのフィルタは 5 種類を試し、差の絶対値の和が最小のもの
//  （libpng と同じ目安）を選ぶ。圧縮は .NET 標準の ZLibStream（最小の大きさ）。
//  同じ画像からは常に同じバイト列になる（生成物を「中身が変わったときだけ書く」ための前提。Icons/LauncherIconStager）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SEEDEditor.Android.Icons;

/// <summary>PNG の書き出し。</summary>
public static class PngEncoder
{
    /// <summary>ビットの深さ。</summary>
    private const byte BitDepth = 8;

    /// <summary>試すフィルタ（仕様の 5 種類）。</summary>
    private static readonly byte[] Filters =
    {
        PngFormat.FilterNone, PngFormat.FilterSub, PngFormat.FilterUp, PngFormat.FilterAverage, PngFormat.FilterPaeth,
    };

    /// <summary>
    /// 画像を PNG のバイト列にする。
    /// </summary>
    /// <param name="image">画像。</param>
    /// <returns>PNG のファイルの中身。</returns>
    public static byte[] Encode(RgbaImage image)
    {
        using var output = new MemoryStream();
        output.Write(PngFormat.Signature);

        // ── IHDR ──
        var header = new byte[PngFormat.HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(PngFormat.ChunkFieldLength), (uint)image.Height);
        header[PngFormat.HeaderBitDepthOffset] = BitDepth;
        header[PngFormat.HeaderColorTypeOffset] = PngFormat.ColorTypeRgba;
        header[PngFormat.HeaderCompressionOffset] = 0;
        header[PngFormat.HeaderFilterMethodOffset] = 0;
        header[PngFormat.HeaderInterlaceOffset] = PngFormat.InterlaceNone;
        WriteChunk(output, PngFormat.HeaderChunk, header);

        // ── IDAT（行ごとにフィルタを選んで zlib で圧縮）──
        WriteChunk(output, PngFormat.DataChunk, CompressRows(image));

        // ── IEND ──
        WriteChunk(output, PngFormat.EndChunk, Array.Empty<byte>());
        return output.ToArray();
    }

    /// <summary>行ごとにフィルタを選んで並べ、zlib で圧縮する。</summary>
    private static byte[] CompressRows(RgbaImage image)
    {
        var rowBytes = image.Width * RgbaImage.BytesPerPixel;
        var previous = new byte[rowBytes];
        var candidate = new byte[rowBytes];
        var best = new byte[rowBytes];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            for (var y = 0; y < image.Height; y++)
            {
                var row = image.Pixels.AsSpan(y * rowBytes, rowBytes);
                var bestFilter = PngFormat.FilterNone;
                var bestScore = long.MaxValue;
                foreach (var filter in Filters)
                {
                    ApplyFilter(filter, row, previous, candidate);
                    var score = Score(candidate);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestFilter = filter;
                        candidate.CopyTo(best, 0);
                    }
                }
                zlib.WriteByte(bestFilter);
                zlib.Write(best, 0, rowBytes);
                row.CopyTo(previous);
            }
        }
        return compressed.ToArray();
    }

    /// <summary>1 行にフィルタを掛ける（左の距離は 1 画素 = 4 バイト）。</summary>
    private static void ApplyFilter(byte filter, ReadOnlySpan<byte> row, byte[] previous, byte[] output)
    {
        const int unit = RgbaImage.BytesPerPixel;
        for (var i = 0; i < row.Length; i++)
        {
            var left = i >= unit ? row[i - unit] : (byte)0;
            var up = previous[i];
            var upLeft = i >= unit ? previous[i - unit] : (byte)0;
            output[i] = filter switch
            {
                PngFormat.FilterSub => (byte)(row[i] - left),
                PngFormat.FilterUp => (byte)(row[i] - up),
                PngFormat.FilterAverage => (byte)(row[i] - ((left + up) >> 1)),
                PngFormat.FilterPaeth => (byte)(row[i] - PngFormat.Paeth(left, up, upLeft)),
                _ => row[i],
            };
        }
    }

    /// <summary>フィルタの良さの目安（符号付きとみなした値の絶対値の和。小さいほど圧縮しやすい）。</summary>
    private static long Score(byte[] filtered)
    {
        long sum = 0;
        // int へ広げてから絶対値（sbyte の -128 の絶対値は sbyte に入らないため）
        foreach (var b in filtered) sum += Math.Abs((int)(sbyte)b);
        return sum;
    }

    /// <summary>チャンク（長さ・名前・中身・CRC）を書く。</summary>
    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        PngFormat.WriteUInt32BigEndian(output, (uint)data.Length);
        output.Write(typeBytes);
        output.Write(data);
        PngFormat.WriteUInt32BigEndian(output, PngFormat.Crc(typeBytes, data));
    }
}
