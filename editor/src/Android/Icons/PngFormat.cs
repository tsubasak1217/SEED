// ============================================================
//  PngFormat.cs — PNG の書式の決まり（署名・チャンクの名前・色の種類・CRC-32）。PngDecoder / PngEncoder が共有する
//
//  仕様は W3C の PNG（Portable Network Graphics）Specification。ここにある数値はその仕様で決まった値。
//  CRC-32 は System.IO.Hashing（NuGet）を足さずに、仕様の付録の表引きで計算する。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Buffers.Binary;
using System.IO;

namespace SEEDEditor.Android.Icons;

/// <summary>PNG として読めないときの例外（説明は利用者向け）。</summary>
public sealed class PngFormatException : Exception
{
    /// <summary>説明を指定して作る。</summary>
    /// <param name="message">説明。</param>
    public PngFormatException(string message) : base(message)
    {
    }
}

/// <summary>PNG の書式の決まり。</summary>
public static class PngFormat
{
    /// <summary>ファイルの先頭の署名（8 バイト）。</summary>
    public static ReadOnlySpan<byte> Signature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>画像の見出しのチャンク。</summary>
    public const string HeaderChunk = "IHDR";

    /// <summary>パレットのチャンク。</summary>
    public const string PaletteChunk = "PLTE";

    /// <summary>透明度のチャンク。</summary>
    public const string TransparencyChunk = "tRNS";

    /// <summary>画像のデータのチャンク（zlib で圧縮した行の並び。複数に分かれ得る）。</summary>
    public const string DataChunk = "IDAT";

    /// <summary>終わりのチャンク。</summary>
    public const string EndChunk = "IEND";

    /// <summary>チャンクの長さ・名前・CRC のそれぞれのバイト数。</summary>
    public const int ChunkFieldLength = 4;

    /// <summary>IHDR の中身の長さ。</summary>
    public const int HeaderLength = 13;

    /// <summary>IHDR の中のビットの深さの位置（幅 4・高さ 4 の後ろ）。</summary>
    public const int HeaderBitDepthOffset = 8;

    /// <summary>IHDR の中の色の種類の位置。</summary>
    public const int HeaderColorTypeOffset = 9;

    /// <summary>IHDR の中の圧縮の方式の位置（常に 0）。</summary>
    public const int HeaderCompressionOffset = 10;

    /// <summary>IHDR の中のフィルタの方式の位置（常に 0）。</summary>
    public const int HeaderFilterMethodOffset = 11;

    /// <summary>IHDR の中のインターレースの方式の位置。</summary>
    public const int HeaderInterlaceOffset = 12;

    /// <summary>色の種類: 灰色。</summary>
    public const byte ColorTypeGray = 0;

    /// <summary>色の種類: RGB。</summary>
    public const byte ColorTypeRgb = 2;

    /// <summary>色の種類: パレット。</summary>
    public const byte ColorTypePalette = 3;

    /// <summary>色の種類: 灰色 ＋ アルファ。</summary>
    public const byte ColorTypeGrayAlpha = 4;

    /// <summary>色の種類: RGBA。</summary>
    public const byte ColorTypeRgba = 6;

    /// <summary>インターレース無し。</summary>
    public const byte InterlaceNone = 0;

    /// <summary>Adam7 インターレース。</summary>
    public const byte InterlaceAdam7 = 1;

    /// <summary>行のフィルタ: なし。</summary>
    public const byte FilterNone = 0;

    /// <summary>行のフィルタ: 左との差。</summary>
    public const byte FilterSub = 1;

    /// <summary>行のフィルタ: 上との差。</summary>
    public const byte FilterUp = 2;

    /// <summary>行のフィルタ: 左と上の平均との差。</summary>
    public const byte FilterAverage = 3;

    /// <summary>行のフィルタ: Paeth 予測との差。</summary>
    public const byte FilterPaeth = 4;

    /// <summary>CRC-32 の多項式（反転表現）。</summary>
    private const uint CrcPolynomial = 0xEDB88320u;

    /// <summary>CRC-32 の表のバイトの数。</summary>
    private const int CrcTableSize = 256;

    /// <summary>1 バイトのビット数。</summary>
    private const int BitsPerByte = 8;

    /// <summary>CRC-32 の表（仕様の付録と同じ作り方）。</summary>
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// チャンクの CRC-32（名前と中身の上で計算する）。
    /// </summary>
    /// <param name="type">チャンクの名前の 4 バイト。</param>
    /// <param name="data">中身。</param>
    /// <returns>CRC。</returns>
    public static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        crc = Update(crc, type);
        crc = Update(crc, data);
        return crc ^ uint.MaxValue;
    }

    /// <summary>
    /// Paeth の予測（左 a・上 b・左上 c のうち、a + b - c に最も近いもの）。
    /// </summary>
    /// <param name="a">左。</param>
    /// <param name="b">上。</param>
    /// <param name="c">左上。</param>
    /// <returns>予測。</returns>
    public static byte Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    /// <summary>ビッグエンディアンの 32 bit を書く。</summary>
    /// <param name="stream">書き先。</param>
    /// <param name="value">値。</param>
    public static void WriteUInt32BigEndian(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[ChunkFieldLength];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    /// <summary>CRC を 1 区間ぶん進める。</summary>
    private static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            crc = CrcTable[(crc ^ b) & byte.MaxValue] ^ (crc >> BitsPerByte);
        }
        return crc;
    }

    /// <summary>CRC-32 の表を作る。</summary>
    private static uint[] BuildCrcTable()
    {
        var table = new uint[CrcTableSize];
        for (uint n = 0; n < CrcTableSize; n++)
        {
            var c = n;
            for (var k = 0; k < BitsPerByte; k++)
            {
                c = (c & 1) != 0 ? CrcPolynomial ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
