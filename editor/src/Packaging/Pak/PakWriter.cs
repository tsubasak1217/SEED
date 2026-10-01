// ============================================================
//  PakWriter.cs — assets.pak の書き出し
//
//  【バイナリ形式】（runtime/src/engine/pak/mod.rs の PakReader と 1 対 1。変更禁止）
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
//
//  【生成エントリ】
//  収録ファイルのほかに、メモリ上で作った中身のエントリ（PakGeneratedEntry。エンジンが書く予約のエントリ。
//  例: 開発用のビルドの印 PakBuildManifest）を足せる。エントリ表とデータ部では収録ファイルの後ろに並べる
//  （バイナリ形式は変わらない。生成エントリが無ければ従来と 1 バイトも違わない）。
//  生成エントリの名前は収録ファイルと重なってはいけない（重なれば呼び出し側の誤りとして例外。予約の名前の
//  利用者のファイルは AssetPakBuilder が先に外す）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SEEDEditor.Packaging.Collect;

namespace SEEDEditor.Packaging.Pak;

/// <summary>
/// 収録ファイルではなく、メモリ上で作った中身を持つ pak のエントリ（エンジンが書く予約のエントリ。ビルドの印など）。
/// </summary>
/// <param name="RelPath">エントリ名（'/' 区切りの相対パス。収録ファイルと同じ名前空間）。</param>
/// <param name="Content">中身。</param>
public readonly record struct PakGeneratedEntry(string RelPath, byte[] Content);

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

    /// <summary>PAK フォーマットのバージョン番号（pak/mod.rs の VERSION と一致させる）。</summary>
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
    /// <param name="generated">
    /// 収録ファイルの後ろに足す、メモリ上の中身のエントリ（省略・空なら従来どおり収録ファイルだけ）。
    /// 名前は収録ファイル・ほかの生成エントリと重なってはいけない。
    /// </param>
    /// <returns>書き出し結果の統計（エントリ数・バイト数は生成エントリを含む）。</returns>
    /// <exception cref="ArgumentException">生成エントリの名前が収録ファイルかほかの生成エントリと重なっている。</exception>
    public static PakWriteStats Write(
        string pakPath,
        string assetsRoot,
        IReadOnlyList<CollectedAsset> assets,
        Action<string>? log = null,
        Action<PakWriteProgress>? progress = null,
        IReadOnlyList<PakGeneratedEntry>? generated = null)
    {
        // ── フェーズ 0: 生成エントリの名前が重ならないことを確かめる（書き始める前に。重なれば呼び出し側の誤り）──
        generated ??= Array.Empty<PakGeneratedEntry>();
        EnsureUniqueGeneratedPaths(assets, generated);
        int totalFiles = assets.Count + generated.Count;

        // ── フェーズ 1: 書き換え対象を変換してサイズを確定させる ──
        //   書き換えるとバイト数が変わるので、エントリ表を書く前に確定が要る。
        var rewritten = new Dictionary<string, byte[]>(AssetPathUtil.PathComparer);
        var sizes     = new long[assets.Count];

        for (int i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];

            // 書き換えの要否と手順は PakEntryContent が正典（Android の差し替えで送る中身もこれと同じにするため）
            if (!PakEntryContent.NeedsRewrite(asset.RelPath))
            {
                sizes[i] = asset.SizeBytes;
                continue;
            }

            try
            {
                var bytes = PakEntryContent.ReadRewritten(assetsRoot, asset.RelPath);
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
        //   データ部の開始位置 = ヘッダー + エントリ表の合計サイズ。
        //   エントリの並びは「収録ファイル（引数の順）→ 生成エントリ（引数の順）」。
        var pathBytes  = new byte[totalFiles][];
        var entrySizes = new long[totalFiles];
        long tableSize = 0;
        for (int i = 0; i < totalFiles; i++)
        {
            bool isAsset = i < assets.Count;
            pathBytes[i]  = Encoding.UTF8.GetBytes(isAsset ? assets[i].RelPath : generated[i - assets.Count].RelPath);
            entrySizes[i] = isAsset ? sizes[i] : generated[i - assets.Count].Content.Length;
            tableSize    += EntryFixedBytes + pathBytes[i].Length;
        }
        long dataOffset = HeaderBytes + tableSize;

        long totalDataBytes = 0;
        foreach (var s in entrySizes) totalDataBytes += s;

        // ── フェーズ 3: ヘッダーとエントリ表を書く ──────────────
        Directory.CreateDirectory(Path.GetDirectoryName(pakPath) ?? ".");
        using var fs = new FileStream(
            pakPath, FileMode.Create, FileAccess.Write, FileShare.None,
            CopyBufferBytes, FileOptions.SequentialScan);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

        bw.Write((byte)'S'); bw.Write((byte)'E'); bw.Write((byte)'E'); bw.Write((byte)'D');
        bw.Write(FormatVersion);
        bw.Write((uint)totalFiles);

        long cursor = dataOffset;
        for (int i = 0; i < totalFiles; i++)
        {
            bw.Write((uint)pathBytes[i].Length);
            bw.Write(pathBytes[i]);
            bw.Write((ulong)cursor);
            bw.Write((ulong)entrySizes[i]);
            cursor += entrySizes[i];
        }
        bw.Flush();

        // ── フェーズ 4: データ部をストリームで書く ──────────────
        var  buffer            = new byte[CopyBufferBytes];
        long bytesWritten      = 0;
        long lastProgressBytes = 0;
        int  mismatchCount     = 0;

        // 進捗の通知（ファイル数・バイト数の間隔ごとと、最後の 1 件のあと）。収録ファイルと生成エントリで共有する
        void ReportProgressIfDue(int filesWritten)
        {
            bool byFile  = filesWritten % ProgressIntervalFiles == 0;
            bool byBytes = bytesWritten - lastProgressBytes >= ProgressIntervalBytes;
            if (byFile || byBytes || filesWritten == totalFiles)
            {
                lastProgressBytes = bytesWritten;
                progress?.Invoke(new PakWriteProgress(filesWritten, totalFiles, bytesWritten, totalDataBytes));
            }
        }

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

            ReportProgressIfDue(i + 1);
        }

        // 生成エントリ（メモリ上の中身をそのまま。エントリ表に書いた大きさと必ず同じ）
        for (int g = 0; g < generated.Count; g++)
        {
            var content = generated[g].Content;
            fs.Write(content, 0, content.Length);
            bytesWritten += content.Length;
            ReportProgressIfDue(assets.Count + g + 1);
        }

        return new PakWriteStats(totalFiles, totalDataBytes, rewritten.Count, mismatchCount);
    }

    // ============================================================
    //  ヘルパー
    // ============================================================

    /// <summary>
    /// 生成エントリの名前が、収録ファイル・ほかの生成エントリと重ならないことを確かめる。
    /// 照合はランタイムの pak の引き方（区切り \ と / を同じに・大文字小文字を問わない）と同じ。
    /// 重なるとランタイムはどちらか一方しか読めない（どちらになるかは並び次第）ので、書き始める前に止める。
    /// </summary>
    /// <param name="assets">収録ファイル。</param>
    /// <param name="generated">生成エントリ。</param>
    /// <exception cref="ArgumentException">重なっている。</exception>
    private static void EnsureUniqueGeneratedPaths(IReadOnlyList<CollectedAsset> assets, IReadOnlyList<PakGeneratedEntry> generated)
    {
        if (generated.Count == 0) return;

        var used = new HashSet<string>(assets.Select(asset => NormalizeEntryPath(asset.RelPath)), AssetPathUtil.PathComparer);
        foreach (var entry in generated)
        {
            if (!used.Add(NormalizeEntryPath(entry.RelPath)))
            {
                throw new ArgumentException(
                    $"生成エントリ {entry.RelPath} が収録ファイルかほかの生成エントリと同じ名前です" +
                    "（予約の名前の利用者のファイルは呼び出し側で外すこと）。", nameof(generated));
            }
        }
    }

    /// <summary>エントリ名の照合用の形（区切りを / にそろえる。大文字小文字は比べる側が無視する）。</summary>
    /// <param name="relPath">エントリ名。</param>
    /// <returns>照合用の形。</returns>
    private static string NormalizeEntryPath(string relPath) => relPath.Replace('\\', '/');

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
