// ============================================================
//  ElfAlignmentReader.cs — 共有ライブラリ（.so）の ELF の見出しから、LOAD セグメントの整列を読む（純粋な処理。段階D）
//
//  Google Play の 16 KB ページの要件（Android 15 以上を対象とするアプリ）は、.so のすべての LOAD セグメントの
//  p_align が 16 KB（0x4000）以上であること（NDK r28 の既定。llvm-readelf -l の LOAD 行の Align と同じ値）。
//  外部の llvm-readelf を使わず、APK / AAB の zip から .so の先頭（ELF の見出しとプログラムヘッダ）だけを読んで調べる
//  （数十 MB の .so を展開し切らない。単体テストで合成した ELF でも確かめられる）。
//
//  ELF の書式は System V ABI / ELF の仕様（ここの位置と値はその仕様で決まった値）。32 bit・64 bit、リトルエンディアンだけを読む
//  （Android の ABI はすべてリトルエンディアン）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Buffers.Binary;
using System.IO;

namespace SEEDEditor.Android.Release;

/// <summary>1 つの .so の ELF の要点。</summary>
/// <param name="Is64Bit">64 bit の ELF か。</param>
/// <param name="Machine">e_machine（AArch64 = 183・x86-64 = 62 等）。</param>
/// <param name="LoadSegments">LOAD セグメントの数。</param>
/// <param name="MinLoadAlignment">LOAD セグメントの p_align の最小値（LOAD が無ければ 0）。</param>
public sealed record ElfLoadAlignment(bool Is64Bit, int Machine, int LoadSegments, ulong MinLoadAlignment);

/// <summary>ELF の LOAD セグメントの整列を読む。</summary>
public static class ElfAlignmentReader
{
    /// <summary>ELF の見出しの目印（0x7F 'E' 'L' 'F'）。</summary>
    private static ReadOnlySpan<byte> Magic => new byte[] { 0x7F, 0x45, 0x4C, 0x46 };

    /// <summary>e_ident の中の class の位置。</summary>
    private const int ClassOffset = 4;

    /// <summary>e_ident の中のエンディアンの位置。</summary>
    private const int DataOffset = 5;

    /// <summary>class: 32 bit。</summary>
    private const byte Class32 = 1;

    /// <summary>class: 64 bit。</summary>
    private const byte Class64 = 2;

    /// <summary>エンディアン: リトル。</summary>
    private const byte DataLittleEndian = 1;

    /// <summary>e_machine の位置（32 / 64 bit 共通）。</summary>
    private const int MachineOffset = 18;

    /// <summary>64 bit の見出しの長さ。</summary>
    private const int Header64Length = 64;

    /// <summary>32 bit の見出しの長さ。</summary>
    private const int Header32Length = 52;

    /// <summary>64 bit: e_phoff の位置。</summary>
    private const int PhOff64 = 32;

    /// <summary>64 bit: e_phentsize の位置。</summary>
    private const int PhEntSize64 = 54;

    /// <summary>64 bit: e_phnum の位置。</summary>
    private const int PhNum64 = 56;

    /// <summary>32 bit: e_phoff の位置。</summary>
    private const int PhOff32 = 28;

    /// <summary>32 bit: e_phentsize の位置。</summary>
    private const int PhEntSize32 = 42;

    /// <summary>32 bit: e_phnum の位置。</summary>
    private const int PhNum32 = 44;

    /// <summary>プログラムヘッダの種類: LOAD。</summary>
    private const uint PtLoad = 1;

    /// <summary>64 bit のプログラムヘッダの中の p_align の位置。</summary>
    private const int Align64 = 48;

    /// <summary>32 bit のプログラムヘッダの中の p_align の位置。</summary>
    private const int Align32 = 28;

    /// <summary>64 bit のプログラムヘッダ 1 つの長さ（p_align まで読むのに要る長さ）。</summary>
    private const int Program64MinLength = 56;

    /// <summary>32 bit のプログラムヘッダ 1 つの長さ。</summary>
    private const int Program32MinLength = 32;

    /// <summary>プログラムヘッダの数の上限（壊れたファイルで巨大な読み込みをしない）。</summary>
    private const int MaxProgramHeaders = 4096;

    /// <summary>
    /// ストリームの先頭から ELF を読み、LOAD セグメントの整列を返す（ストリームは先頭から順にだけ読む。zip の展開ストリームでよい）。
    /// </summary>
    /// <param name="stream">.so の中身（先頭から）。</param>
    /// <returns>要点。</returns>
    /// <exception cref="InvalidDataException">ELF として読めないとき。</exception>
    public static ElfLoadAlignment Read(Stream stream)
    {
        var header = ReadExactly(stream, 0, Header64Length, allowShort: true);
        if (header.Length < Header32Length || !header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("ELF の見出しがありません（共有ライブラリではありません）。");
        }
        if (header[DataOffset] != DataLittleEndian) throw new InvalidDataException("ビッグエンディアンの ELF は読みません。");
        var is64 = header[ClassOffset] switch
        {
            Class64 => true,
            Class32 => false,
            _ => throw new InvalidDataException($"ELF の class {header[ClassOffset]} は仕様にありません。"),
        };
        if (is64 && header.Length < Header64Length) throw new InvalidDataException("64 bit の ELF の見出しが途中で切れています。");

        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(MachineOffset));
        var phoff = is64 ? BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(PhOff64)) : BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(PhOff32));
        var entSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(is64 ? PhEntSize64 : PhEntSize32));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(is64 ? PhNum64 : PhNum32));
        var minEntry = is64 ? Program64MinLength : Program32MinLength;
        if (count > MaxProgramHeaders || (count > 0 && entSize < minEntry))
        {
            throw new InvalidDataException($"ELF のプログラムヘッダの表が不正です（数 {count}・大きさ {entSize}）。");
        }

        // 見出しの後ろからプログラムヘッダの表の終わりまで順に読み、先頭からの 1 つの並びにする（多くは見出しの直後にある）
        var headerLength = is64 ? Header64Length : Header32Length;
        if (count > 0 && phoff < (ulong)headerLength) throw new InvalidDataException("ELF のプログラムヘッダの位置が見出しと重なっています。");
        var tableEnd = checked((long)phoff + (long)entSize * count);
        var rest = ReadExactly(stream, header.Length, tableEnd, allowShort: false);
        var image = new byte[header.Length + rest.Length];
        header.CopyTo(image, 0);
        rest.CopyTo(image, header.Length);
        var loads = 0;
        var minAlign = ulong.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var entry = image.AsSpan((int)((long)phoff + (long)i * entSize), entSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != PtLoad) continue;
            loads++;
            var align = is64 ? BinaryPrimitives.ReadUInt64LittleEndian(entry[Align64..]) : BinaryPrimitives.ReadUInt32LittleEndian(entry[Align32..]);
            minAlign = Math.Min(minAlign, align);
        }
        return new ElfLoadAlignment(is64, machine, loads, loads == 0 ? 0 : minAlign);
    }

    /// <summary>
    /// 今の位置（alreadyRead）から end までを読む（end が今の位置以下なら空）。
    /// </summary>
    private static byte[] ReadExactly(Stream stream, long alreadyRead, long end, bool allowShort)
    {
        var length = end - alreadyRead;
        if (length <= 0) return Array.Empty<byte>();
        if (length > int.MaxValue) throw new InvalidDataException("ELF のプログラムヘッダの表が大きすぎます。");
        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        if (read < buffer.Length)
        {
            if (!allowShort) throw new InvalidDataException("ELF が途中で切れています。");
            Array.Resize(ref buffer, read);
        }
        return buffer;
    }
}
