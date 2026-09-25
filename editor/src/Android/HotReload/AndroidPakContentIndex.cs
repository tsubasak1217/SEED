// ============================================================
//  AndroidPakContentIndex.cs — APK に入れた assets.pak の中身の指紋（差し替えの差分を選ぶ土台。docs/android.md §23）
//
//  【何に使うか】
//  端末は APK の pak を読んでいる（上書き層 files/assets に無いアセットは pak から読む）。エディタが「変わったアセット」を
//  送るとき、pak に入っている中身と同じなら送らない（送っても同じ）。そのために、pak のエントリの中身の SHA-256 を
//  必要になったものだけ計算して覚える（pak 全体は読まない）。
//
//  【形式】正典は runtime/src/engine/pak/mod.rs（書き手は editor/src/Packaging/Pak/PakWriter.cs。読み取りの先例は
//  Project/PakEntryIndex.cs）: "SEED"・版 u32（1）・エントリ数 u32・［パスの長さ u32・パス・位置 u64・大きさ u64］× エントリ数・データ。
//  照合は端末と同じく区切り（\ と /）と大文字小文字を問わない。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SEEDEditor.Android.HotReload;

/// <summary>assets.pak のエントリの中身の指紋。</summary>
public sealed class AndroidPakContentIndex
{
    /// <summary>形式の目印（"SEED"）。</summary>
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SEED");

    /// <summary>読める形式の版。</summary>
    private const uint SupportedVersion = 1;

    /// <summary>読み取りの区切りのバッファの大きさ（ハッシュの計算に使う）。</summary>
    private const int CopyBufferBytes = 1 << 16;

    /// <summary>pak のパス。</summary>
    private readonly string _pakPath;

    /// <summary>照合のキー → （位置, 大きさ）。</summary>
    private readonly Dictionary<string, (long Offset, long Size)> _entries;

    /// <summary>計算済みの指紋（照合のキー → SHA-256 の 16 進）。</summary>
    private readonly Dictionary<string, string> _digests = new(StringComparer.Ordinal);

    /// <summary>エントリ表を読んで作る。</summary>
    /// <param name="pakPath">pak のパス。</param>
    /// <param name="entries">照合のキー → （位置, 大きさ）。</param>
    private AndroidPakContentIndex(string pakPath, Dictionary<string, (long Offset, long Size)> entries)
    {
        _pakPath = pakPath;
        _entries = entries;
    }

    /// <summary>エントリの数。</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// pak のエントリ表を読む（中身は読まない）。
    /// </summary>
    /// <param name="pakPath">assets.pak のパス。</param>
    /// <returns>指紋の表。</returns>
    /// <exception cref="InvalidDataException">形式が違う・途中で切れている。</exception>
    /// <exception cref="IOException">読めない。</exception>
    public static AndroidPakContentIndex Open(string pakPath)
    {
        using var stream = File.OpenRead(pakPath);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        try
        {
            if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException($"pak の目印が SEED ではありません: {pakPath}");
            var version = reader.ReadUInt32();
            if (version != SupportedVersion) throw new InvalidDataException($"知らない pak の版です（{version}）: {pakPath}");
            var count = reader.ReadUInt32();
            var entries = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
            for (var i = 0u; i < count; i++)
            {
                var length = checked((int)reader.ReadUInt32());
                var pathBytes = reader.ReadBytes(length);
                if (pathBytes.Length != length) throw new EndOfStreamException();
                var offset = checked((long)reader.ReadUInt64());
                var size = checked((long)reader.ReadUInt64());
                entries[KeyOf(Encoding.UTF8.GetString(pathBytes))] = (offset, size);
            }
            return new AndroidPakContentIndex(pakPath, entries);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException($"pak のエントリ表が途中で切れています: {pakPath}", ex);
        }
    }

    /// <summary>
    /// 相対パスのエントリの中身の SHA-256（16 進・大文字）。pak に無ければ null。
    /// </summary>
    /// <param name="relative">アセットルートからの相対パス。</param>
    /// <returns>指紋（無ければ null）。</returns>
    /// <exception cref="IOException">pak を読めない。</exception>
    public string? DigestOf(string relative)
    {
        var key = KeyOf(relative);
        if (_digests.TryGetValue(key, out var cached)) return cached;
        if (!_entries.TryGetValue(key, out var entry)) return null;

        using var stream = File.OpenRead(_pakPath);
        stream.Seek(entry.Offset, SeekOrigin.Begin);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];
        var remaining = entry.Size;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new InvalidDataException($"pak のエントリ {relative} が途中で切れています: {_pakPath}");
            sha.AppendData(buffer, 0, read);
            remaining -= read;
        }
        var digest = Convert.ToHexString(sha.GetHashAndReset());
        _digests[key] = digest;
        return digest;
    }

    /// <summary>照合のキー（\ を / に・小文字。端末の pak の引き方と同じ）。</summary>
    /// <param name="path">相対パス。</param>
    /// <returns>キー。</returns>
    public static string KeyOf(string path) => path.Replace('\\', '/').ToLowerInvariant();
}
