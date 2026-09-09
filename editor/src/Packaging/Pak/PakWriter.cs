// ============================================================
//  PakWriter.cs — assets.pak の書き出し
//
//  【バイナリ形式】（runtime/src/engine/pak.rs の PakReader と 1 対 1。変更禁止）
//  [Header - 12 bytes]
//    magic:       "SEED" (4 bytes)
//    version:     1      (u32 LE)
//    entry_count: N      (u32 LE)
//
//  [Entry Table - N entries]
//    path_len: u32 LE
//    path:     UTF-8 bytes（アセットルートからの相対パス、'/' 区切り）
//    offset:   u64 LE（ファイル先頭からのバイト位置）
//    size:     u64 LE
//
//  [Data Section]
//    各ファイルの生バイトを連結
//
//  【設計方針: 全ファイルをメモリに載せない】
//  従来は全ファイルを byte[] にして List へ貯めてから書いていたため、
//  1 GB 近いメモリを使っていた。ここでは
//    ① 先に (相対パス, サイズ) の表を作ってヘッダーとエントリ表を確定させ、
//    ② データ部はファイルを 1 本ずつストリームコピーする
//  という 2 段構えにして、常駐メモリを「書き換え対象のテキストだけ」に抑える。
//  書き換え対象（.scene / .json など）はサイズが変わるため、
//  ①の時点でメモリ上に変換結果を持つ必要がある（合計でも数 MB）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SEEDEditor.Packaging.Collect;

namespace SEEDEditor.Packaging.Pak;

/// <summary>
/// PAK 書き出しの進行状況。
/// </summary>
/// <param name="FilesWritten">書き終えたファイル数。</param>
/// <param name="TotalFiles">書き出す総ファイル数。</param>
/// <param name="BytesWritten">書き終えたバイト数。</param>
/// <param name="TotalBytes">書き出す総バイト数。</param>
public readonly record struct PakWriteProgress(
    int FilesWritten,
    int TotalFiles,
    long BytesWritten,
    long TotalBytes);

/// <summary>
/// PAK 書き出しの結果。
/// </summary>
/// <param name="EntryCount">格納したエントリ数。</param>
/// <param name="TotalBytes">データ部の合計バイト数。</param>
/// <param name="RewrittenCount">パス書き換えを行ったファイル数。</param>
/// <param name="SizeMismatchCount">
/// 収集時とサイズが食い違ったファイル数（書き出し中に外部から変更された場合）。
/// </param>
public readonly record struct PakWriteStats(
    int EntryCount,
    long TotalBytes,
    int RewrittenCount,
    int SizeMismatchCount);

/// <summary>assets.pak を書き出す。UI に依存しない（ログと進捗はコールバック経由）。</summary>
public static class PakWriter
{
    // ── フォーマット定数 ─────────────────────────────────────

    /// <summary>ヘッダーのバイト数（magic 4 + version 4 + entry_count 4）。</summary>
    private const int HeaderBytes = 12;

    /// <summary>エントリ 1 件あたりの固定バイト数（path_len 4 + offset 8 + size 8）。</summary>
    private const int EntryFixedBytes = 20;

    /// <summary>PAK フォーマットのバージョン番号（pak.rs の VERSION と一致させる）。</summary>
    private const uint FormatVersion = 1;

    // ── 動作パラメータ ───────────────────────────────────────

    /// <summary>データ部のストリームコピーに使うバッファサイズ（バイト）。</summary>
    private const int CopyBufferBytes = 1 << 20;   // 1 MB

    /// <summary>進捗を通知するファイル数の間隔。</summary>
    private const int ProgressIntervalFiles = 50;

    /// <summary>進捗を通知するバイト数の間隔。</summary>
    private const long ProgressIntervalBytes = 32L * 1024 * 1024;   // 32 MB

    // ============================================================
    //  書き出し
    // ============================================================

    /// <summary>
    /// 収録対象のアセットを assets.pak として書き出す。
    /// </summary>
    /// <param name="pakPath">出力する .pak のパス。</param>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="assets">収録するファイル（アセットルート相対パスとサイズ）。</param>
    /// <param name="log">ログ出力先（省略可）。</param>
    /// <param name="progress">進捗通知先（省略可）。</param>
    /// <returns>書き出し結果の統計。</returns>
    public static PakWriteStats Write(
        string pakPath,
        string assetsRoot,
        IReadOnlyList<CollectedAsset> assets,
        Action<string>? log = null,
        Action<PakWriteProgress>? progress = null)
    {
        // ── フェーズ 1: 書き換え対象を変換してサイズを確定させる ──
        //   書き換えるとバイト数が変わるので、エントリ表を書く前に確定が要る。
        var rewritten = new Dictionary<string, byte[]>(AssetPathUtil.PathComparer);
        var sizes     = new long[assets.Count];

        for (int i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];
            var ext   = AssetPathUtil.GetExtensionLower(asset.RelPath);

            if (!PackagingRules.PathRewriteExtensions.Contains(ext))
            {
                sizes[i] = asset.SizeBytes;
                continue;
            }

            var abs = AssetPathUtil.ToAbsolute(assetsRoot, asset.RelPath);
            try
            {
                var text  = File.ReadAllText(abs, Encoding.UTF8);
                var bytes = Encoding.UTF8.GetBytes(AssetPathRewriter.ToVirtual(text, assetsRoot));
                rewritten[asset.RelPath] = bytes;
                sizes[i] = bytes.Length;
            }
            catch (Exception ex)
            {
                // 読めない場合は書き換えを諦め、生バイトをそのまま格納する
                log?.Invoke($"⚠ パス書き換えに失敗（生データで格納）: {asset.RelPath} — {ex.Message}");
                sizes[i] = asset.SizeBytes;
            }
        }
        log?.Invoke($"  パス書き換え: {rewritten.Count} ファイル");

        // ── フェーズ 2: オフセットを計算する ────────────────────
        //   データ部の開始位置 = ヘッダー + エントリ表の合計サイズ
        var pathBytes  = new byte[assets.Count][];
        long tableSize = 0;
        for (int i = 0; i < assets.Count; i++)
        {
            pathBytes[i] = Encoding.UTF8.GetBytes(assets[i].RelPath);
            tableSize   += EntryFixedBytes + pathBytes[i].Length;
        }
        long dataOffset = HeaderBytes + tableSize;

        long totalDataBytes = 0;
        foreach (var s in sizes) totalDataBytes += s;

        // ── フェーズ 3: ヘッダーとエントリ表を書く ──────────────
        Directory.CreateDirectory(Path.GetDirectoryName(pakPath) ?? ".");
        using var fs = new FileStream(
            pakPath, FileMode.Create, FileAccess.Write, FileShare.None,
            CopyBufferBytes, FileOptions.SequentialScan);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

        bw.Write((byte)'S'); bw.Write((byte)'E'); bw.Write((byte)'E'); bw.Write((byte)'D');
        bw.Write(FormatVersion);
        bw.Write((uint)assets.Count);

        long cursor = dataOffset;
        for (int i = 0; i < assets.Count; i++)
        {
            bw.Write((uint)pathBytes[i].Length);
            bw.Write(pathBytes[i]);
            bw.Write((ulong)cursor);
            bw.Write((ulong)sizes[i]);
            cursor += sizes[i];
        }
        bw.Flush();

        // ── フェーズ 4: データ部をストリームで書く ──────────────
        var  buffer            = new byte[CopyBufferBytes];
        long bytesWritten      = 0;
        long lastProgressBytes = 0;
        int  mismatchCount     = 0;

        for (int i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];

            if (rewritten.TryGetValue(asset.RelPath, out var data))
            {
                fs.Write(data, 0, data.Length);
                bytesWritten += data.Length;
            }
            else
            {
                // エントリ表に書いたサイズと必ず一致させる。
                // 書き出し中に外部からファイルが変更されても PAK が壊れないよう、
                // 足りなければ 0 埋め、多ければ切り捨てる（食い違いは件数で報告する）。
                var written = CopyExact(
                    AssetPathUtil.ToAbsolute(assetsRoot, asset.RelPath), fs, sizes[i], buffer, log);
                if (written != sizes[i]) mismatchCount++;
                bytesWritten += sizes[i];
            }

            bool byFile  = (i + 1) % ProgressIntervalFiles == 0;
            bool byBytes = bytesWritten - lastProgressBytes >= ProgressIntervalBytes;
            if (byFile || byBytes || i == assets.Count - 1)
            {
                lastProgressBytes = bytesWritten;
                progress?.Invoke(new PakWriteProgress(i + 1, assets.Count, bytesWritten, totalDataBytes));
            }
        }

        return new PakWriteStats(assets.Count, totalDataBytes, rewritten.Count, mismatchCount);
    }

    // ============================================================
    //  ヘルパー
    // ============================================================

    /// <summary>
    /// ファイルの中身を出力ストリームへ「ちょうど expectedSize バイト」書き込む。
    /// </summary>
    /// <param name="sourcePath">コピー元ファイル。</param>
    /// <param name="dest">出力先ストリーム。</param>
    /// <param name="expectedSize">エントリ表に記録済みのサイズ。</param>
    /// <param name="buffer">再利用するコピーバッファ。</param>
    /// <param name="log">ログ出力先。</param>
    /// <returns>実際にコピー元から読めたバイト数（不足分は 0 埋めされる）。</returns>
    private static long CopyExact(
        string sourcePath, Stream dest, long expectedSize, byte[] buffer, Action<string>? log)
    {
        long copied = 0;
        try
        {
            using var src = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                buffer.Length, FileOptions.SequentialScan);

            while (copied < expectedSize)
            {
                int want = (int)Math.Min(buffer.Length, expectedSize - copied);
                int read = src.Read(buffer, 0, want);
                if (read <= 0) break;
                dest.Write(buffer, 0, read);
                copied += read;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"⚠ 読み込み失敗（0 埋めで格納）: {sourcePath} — {ex.Message}");
        }

        // 不足分を 0 で埋めて、エントリ表のサイズと必ず一致させる
        if (copied < expectedSize)
        {
            log?.Invoke($"⚠ サイズが収集時と違います（0 埋め）: {sourcePath} " +
                        $"期待 {expectedSize} / 実際 {copied}");
            Array.Clear(buffer, 0, buffer.Length);
            long remain = expectedSize - copied;
            while (remain > 0)
            {
                int chunk = (int)Math.Min(buffer.Length, remain);
                dest.Write(buffer, 0, chunk);
                remain -= chunk;
            }
        }
        return copied;
    }
}
