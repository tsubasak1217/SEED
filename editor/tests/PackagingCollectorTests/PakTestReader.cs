// ============================================================
//  PakTestReader.cs — テスト用の PAK 読み込み
//
//  【役割】
//  runtime/src/engine/pak.rs の PakReader と**同じ手順**で assets.pak を読む。
//  PakWriter が書いたバイナリを実際に読み返すことで、
//  ヘッダー・エントリ表・オフセットの整合を検証する。
//
//  【なぜ Rust 側の実装を写すのか】
//  「書けたこと」ではなく「ランタイムが読めること」を確かめたいため。
//  読み方を独立に実装しておくと、書き手側の思い込みでは通らない。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SEEDEditor.Tests.PackagingCollector;

/// <summary>PAK ファイルを読み、エントリ名 → バイト列を取り出す（テスト専用）。</summary>
public sealed class PakTestReader
{
    /// <summary>相対パス → (オフセット, サイズ)。</summary>
    private readonly Dictionary<string, (long Offset, long Size)> _entries = new(StringComparer.Ordinal);

    /// <summary>PAK ファイルの全バイト。</summary>
    private readonly byte[] _bytes;

    /// <summary>格納されているエントリ数。</summary>
    public int EntryCount => _entries.Count;

    /// <summary>PAK ファイル全体のバイト数。</summary>
    public long FileLength => _bytes.LongLength;

    /// <summary>エントリ名の一覧。</summary>
    public IEnumerable<string> EntryPaths => _entries.Keys;

    /// <summary>PAK を開いてエントリ表を読む。</summary>
    /// <param name="pakPath">読み込む .pak のパス。</param>
    public PakTestReader(string pakPath)
    {
        _bytes = File.ReadAllBytes(pakPath);
        using var ms = new MemoryStream(_bytes, writable: false);
        using var br = new BinaryReader(ms, Encoding.UTF8);

        // ── ヘッダー ───────────────────────────────────────────
        var magic = br.ReadBytes(4);
        if (magic[0] != (byte)'S' || magic[1] != (byte)'E' ||
            magic[2] != (byte)'E' || magic[3] != (byte)'D')
            throw new InvalidDataException("PAK マジックが 'SEED' ではありません");

        var version = br.ReadUInt32();
        if (version != 1) throw new InvalidDataException($"未対応の PAK バージョン: {version}");

        var count = br.ReadUInt32();

        // ── エントリ表 ─────────────────────────────────────────
        for (uint i = 0; i < count; i++)
        {
            var pathLen = br.ReadUInt32();
            var path    = Encoding.UTF8.GetString(br.ReadBytes((int)pathLen));
            var offset  = (long)br.ReadUInt64();
            var size    = (long)br.ReadUInt64();
            _entries[path] = (offset, size);
        }
    }

    /// <summary>エントリのバイト列を取り出す（存在しなければ null）。</summary>
    /// <param name="relativePath">'/' 区切りの相対パス。</param>
    /// <returns>バイト列。</returns>
    public byte[]? Read(string relativePath)
    {
        var key = relativePath.Replace('\\', '/');
        if (!_entries.TryGetValue(key, out var e)) return null;
        var buf = new byte[e.Size];
        Array.Copy(_bytes, e.Offset, buf, 0, e.Size);
        return buf;
    }

    /// <summary>エントリの中身を UTF-8 テキストとして取り出す。</summary>
    /// <param name="relativePath">'/' 区切りの相対パス。</param>
    /// <returns>テキスト（存在しなければ null）。</returns>
    public string? ReadText(string relativePath)
    {
        var bytes = Read(relativePath);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>データ部の終端がファイル末尾と一致するかを返す（オフセット計算の検証用）。</summary>
    /// <returns>最終エントリの終端位置。</returns>
    public long DataEnd()
    {
        long end = 0;
        foreach (var (offset, size) in _entries.Values)
            end = Math.Max(end, offset + size);
        return end;
    }
}
