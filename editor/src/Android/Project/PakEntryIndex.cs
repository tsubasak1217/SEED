// ============================================================
//  PakEntryIndex.cs — assets.pak のエントリ名だけを読む（中身は読まない。段階C-3）
//
//  【何に使うか】
//  起動するシーン（開いているシーン）が APK に入る pak に本当に入っているかを、起動の直前に PC 側で確かめる
//  （Steps/LaunchStep）。pak はプロジェクト設定の開始シーン・シーン一覧から参照をたどって作る
//  （editor/src/Packaging/Collect/AssetCollector.cs）ため、どこからも参照されていないシーンはディスクにあっても入らない。
//  そのときは端末が警告して開始シーンで起動するので、理由と直し方を Output にも出す。
//
//  【形式】正典は runtime/src/engine/pak/mod.rs（書き手は editor/src/Packaging/Pak/PakWriter.cs）:
//    "SEED"（4 バイト）・版 u32 LE（1）・エントリ数 u32 LE ・［パスの長さ u32・パス（UTF-8）・位置 u64・大きさ u64］× エントリ数
//  エントリの照合は端末と同じく区切り（\ と /）と大文字小文字を問わない（pak/mod.rs の normalize_key）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SEEDEditor.Android.Project;

/// <summary>assets.pak のエントリ名の読み取り。</summary>
public static class PakEntryIndex
{
    /// <summary>形式の目印（"SEED"）。</summary>
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SEED");

    /// <summary>読める形式の版。</summary>
    private const uint SupportedVersion = 1;

    /// <summary>エントリの位置と大きさ（u64 × 2）のバイト数（読み飛ばす）。</summary>
    private const int OffsetAndSizeBytes = sizeof(ulong) * 2;

    /// <summary>
    /// pak のエントリ名を読む（表だけ。中身は読まない）。
    /// </summary>
    /// <param name="pakPath">assets.pak のパス。</param>
    /// <returns>エントリ名（pak に書かれた表記のまま）。</returns>
    /// <exception cref="InvalidDataException">形式が違う・途中で切れている。</exception>
    /// <exception cref="IOException">読めない。</exception>
    public static IReadOnlyList<string> ReadEntryPaths(string pakPath)
    {
        using var stream = File.OpenRead(pakPath);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        try
        {
            if (!ReadExactly(reader, Magic.Length, pakPath).SequenceEqual(Magic)) throw new InvalidDataException($"pak の目印が SEED ではありません: {pakPath}");
            var version = reader.ReadUInt32();
            if (version != SupportedVersion) throw new InvalidDataException($"知らない pak の版です（{version}）: {pakPath}");
            var count = reader.ReadUInt32();
            var paths = new List<string>();
            for (var i = 0u; i < count; i++)
            {
                var length = reader.ReadUInt32();
                paths.Add(Encoding.UTF8.GetString(ReadExactly(reader, checked((int)length), pakPath)));
                ReadExactly(reader, OffsetAndSizeBytes, pakPath);
            }
            return paths;
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException($"pak のエントリ表が途中で切れています: {pakPath}", ex);
        }
    }

    /// <summary>ちょうど <paramref name="count"/> バイトを読む（BinaryReader.ReadBytes は足りなくても例外にしないため）。</summary>
    private static byte[] ReadExactly(BinaryReader reader, int count, string pakPath)
    {
        var bytes = reader.ReadBytes(count);
        return bytes.Length == count ? bytes : throw new InvalidDataException($"pak のエントリ表が途中で切れています: {pakPath}");
    }

    /// <summary>
    /// エントリ名の中に相対パスがあるか（区切りと大文字小文字を問わない。端末の pak の引き方と同じ。純粋な処理）。
    /// </summary>
    /// <param name="entryPaths">エントリ名。</param>
    /// <param name="relative">アセットルートからの相対パス。</param>
    /// <returns>あれば true。</returns>
    public static bool Contains(IEnumerable<string> entryPaths, string relative)
    {
        var key = NormalizeKey(relative);
        return entryPaths.Any(path => NormalizeKey(path) == key);
    }

    /// <summary>照合のキー（\ を / に・小文字。runtime の normalize_key と同じ）。</summary>
    private static string NormalizeKey(string path) => path.Replace('\\', '/').ToLowerInvariant();
}
