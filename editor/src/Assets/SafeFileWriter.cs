using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SEEDEditor.Assets;

// ============================================================
//  SafeFileWriter.cs — アセットファイルの「壊さない」書き込み
//
//  ランタイム側の runtime/src/engine/core/app_base/safe_write.rs と同じ規約を
//  エディタ側にも用意したもの。AI（write_asset_file）やエディタのファイル生成が
//  既存の .scene / .actor / .anim を上書きするとき、
//    1. 旧版を <assets>/.backup/<相対パス>/<名前>.<yyyyMMdd-HHmmss><拡張子> へ退避
//    2. <file>.tmp へ書き切ってから置換
//  の 2 段構えで書く。世代は 1 ファイルにつき最新 BackupKeep 件だけ残す。
//
//  【復旧手順】
//   <assets>/.backup/ 配下から目的のファイル名 + 時刻のものを探し、
//   タイムスタンプ部分を取り除いた名前で元の場所へコピーし直す。
// ============================================================

/// <summary>
/// バックアップ付き・原子的なファイル書き込み。状態を持たない静的クラス。
/// </summary>
public static class SafeFileWriter
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>同一ファイルにつき保持するバックアップ世代数。</summary>
    public const int BackupKeep = 10;

    /// <summary>バックアップ置き場のディレクトリ名（アセットルート直下）。</summary>
    public const string BackupDirName = ".backup";

    /// <summary>原子的置換に使う一時ファイルの拡張子。</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>バックアップ名へ差し込むタイムスタンプの書式。</summary>
    public const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>同一秒内の連続保存で名前が衝突したときに付ける連番の上限。</summary>
    private const int DedupLimit = 1000;

    // ── 書き込み ─────────────────────────────────────────────────

    /// <summary>
    /// バックアップを取ってから原子的にテキストファイルを書き込む。
    /// </summary>
    /// <param name="path">書き込み先の絶対パス。</param>
    /// <param name="content">書き込む内容。</param>
    /// <param name="assetsRoot">
    /// アセットルート。<paramref name="path"/> がこの配下にあれば
    /// バックアップを <c>&lt;assetsRoot&gt;/.backup/&lt;相対ディレクトリ&gt;</c> へ置く。
    /// null / 配下でない場合はファイルと同じディレクトリの <c>.backup</c> を使う。
    /// </param>
    /// <param name="encoding">エンコーディング。null なら BOM なし UTF-8。</param>
    /// <returns>作成したバックアップのパス（新規ファイルなら null）。</returns>
    public static string? WriteAllTextAtomic(
        string path, string content, string? assetsRoot = null, Encoding? encoding = null)
    {
        encoding ??= new UTF8Encoding(false);

        var backup = BackupExisting(path, assetsRoot);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + TempSuffix;
        File.WriteAllText(temp, content, encoding);
        // File.Move(overwrite:true) は同一ボリュームでは置換 API を使うため、
        // 「書きかけの内容が見える」瞬間が無い。
        File.Move(temp, path, overwrite: true);
        return backup;
    }

    // ── バックアップ ─────────────────────────────────────────────

    /// <summary>
    /// 既存ファイルをバックアップ置き場へ複製し、世代数を <see cref="BackupKeep"/> に切り詰める。
    /// 元ファイルが無ければ何もしない。
    /// </summary>
    /// <returns>作成したバックアップのパス（作らなかった場合は null）。</returns>
    public static string? BackupExisting(string path, string? assetsRoot)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var dir = BackupDirFor(path, assetsRoot);
            Directory.CreateDirectory(dir);

            var stem = Path.GetFileNameWithoutExtension(path);
            var ext  = Path.GetExtension(path);
            var dest = UniqueBackupPath(dir, stem, ext, DateTime.Now.ToString(TimestampFormat));

            File.Copy(path, dest, overwrite: false);
            RotateBackups(dir, stem, ext, BackupKeep);
            return dest;
        }
        catch
        {
            // バックアップの失敗で保存自体を止めない（保存できない方が損害が大きい）。
            return null;
        }
    }

    /// <summary>あるファイルのバックアップ置き場（ディレクトリ）を返す。</summary>
    public static string BackupDirFor(string path, string? assetsRoot)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        if (!string.IsNullOrEmpty(assetsRoot))
        {
            var root = Path.GetFullPath(assetsRoot);
            if (parent.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var rel = Path.GetRelativePath(root, parent);
                if (rel == ".") rel = "";
                return Path.Combine(root, BackupDirName, rel);
            }
        }
        return Path.Combine(parent, BackupDirName);
    }

    /// <summary>
    /// バックアップの世代を <paramref name="keep"/> 件へ切り詰める（古いものから削除）。
    /// 対象は <c>&lt;stem&gt;.&lt;タイムスタンプ&gt;&lt;ext&gt;</c> 形式のファイルだけ。
    /// </summary>
    /// <returns>削除した件数。</returns>
    public static int RotateBackups(string dir, string stem, string ext, int keep)
    {
        if (!Directory.Exists(dir)) return 0;

        var names = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (IsBackupName(name, stem, ext)) names.Add(name);
        }
        if (names.Count <= keep) return 0;

        // タイムスタンプは固定長なので、名前の昇順＝古い順になる。
        names.Sort(StringComparer.Ordinal);
        int removeCount = names.Count - keep;
        int removed = 0;
        for (int i = 0; i < removeCount; i++)
        {
            try { File.Delete(Path.Combine(dir, names[i])); removed++; }
            catch { /* 消せない世代は次回に回す */ }
        }
        return removed;
    }

    /// <summary>
    /// ファイル名が <c>&lt;stem&gt;.&lt;タイムスタンプ&gt;&lt;ext&gt;</c> 形式のバックアップかどうか。
    ///
    /// 前方後方一致だけでは <c>MainGame.scene</c> の整理が <c>MainGameOld.scene</c> の
    /// バックアップまで巻き込むため、stem と ext の間がタイムスタンプ文字だけかも確認する。
    /// </summary>
    public static bool IsBackupName(string name, string stem, string ext)
    {
        var prefix = stem + ".";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (!name.EndsWith(ext, StringComparison.Ordinal)) return false;
        if (name.Length <= prefix.Length + ext.Length) return false;

        var middle = name.Substring(prefix.Length, name.Length - prefix.Length - ext.Length);
        foreach (var c in middle)
        {
            // タイムスタンプ（数字・ハイフン）と重複回避の連番（'_' + 数字）だけを許す。
            if (!char.IsAsciiDigit(c) && c != '-' && c != '_') return false;
        }
        return true;
    }

    /// <summary>同一秒内の連続保存でも上書きしないよう、空いている名前を探す。</summary>
    private static string UniqueBackupPath(string dir, string stem, string ext, string stamp)
    {
        var first = Path.Combine(dir, $"{stem}.{stamp}{ext}");
        if (!File.Exists(first)) return first;

        for (int i = 1; i < DedupLimit; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}.{stamp}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return first;
    }
}
