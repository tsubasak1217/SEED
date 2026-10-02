// ============================================================
//  LocaleFolderSnapshot.cs — 多言語の置き場の *.json の中身の控え（外部変更の判定。純粋な計算）
//
//  【なぜ控えを比べるか】
//  ファイル監視のイベントは、時刻だけの更新・同じ書き込みの重複・パネル自身の保存（一時ファイル → 置き換え）でも届く。
//  そこで置き場の *.json の中身の SHA-256（Reload/FileContentHash。シーン・プレハブの自動再読込と同じ）を控え、
//  イベントが静まった後に取り直して比べる。違うファイルがあれば外部の変更。
//  パネル自身の保存の後は控えを取り直すので、自分の書き込みは変更に数えない（時間の窓は要らない）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SEED.Localization;

namespace SEEDEditor.Localization.IO;

/// <summary>多言語の置き場の *.json の中身の控え。</summary>
public sealed class LocaleFolderSnapshot
{
    /// <summary>ファイル名 → 中身のハッシュ（読めなければ null）。名前は大文字小文字を区別しない（Windows）。</summary>
    private readonly Dictionary<string, string?> _hashes;

    /// <summary>控えを作る（Capture・Empty から）。</summary>
    private LocaleFolderSnapshot(Dictionary<string, string?> hashes)
    {
        _hashes = hashes;
    }

    /// <summary>空の控え（置き場が無いとき）。</summary>
    public static LocaleFolderSnapshot Empty { get; } = new(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

    /// <summary>読めないファイル（書き込み中など）があったか（あれば少し待って取り直す）。</summary>
    public bool HasUnreadable => _hashes.Values.Any(h => h is null);

    /// <summary>控えたファイル名。</summary>
    public IReadOnlyCollection<string> FileNames => _hashes.Keys;

    /// <summary>
    /// 置き場の *.json（直下だけ）の中身のハッシュを取る。
    /// </summary>
    /// <param name="folder">置き場の絶対パス。</param>
    /// <param name="computeHash">ファイルの中身のハッシュを返す関数（読めなければ null）。</param>
    /// <returns>控え（置き場が無ければ空）。</returns>
    public static LocaleFolderSnapshot Capture(string folder, Func<string, string?> computeHash)
    {
        var hashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder)) return new LocaleFolderSnapshot(hashes);
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                // 拡張子は自分で確かめる（一時ファイル en.json.tmp などを入れない）
                if (!string.Equals(Path.GetExtension(path), LocalePaths.TableExtension, StringComparison.OrdinalIgnoreCase)) continue;
                hashes[Path.GetFileName(path)] = computeHash(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 置き場ごと消えた・見えなくなった途中。取れた分だけで比べる（消えたファイルは変更として出る）
        }
        return new LocaleFolderSnapshot(hashes);
    }

    /// <summary>
    /// 前の控えと比べて、足された・消えた・中身の変わったファイル名を返す（名前の順）。
    /// </summary>
    /// <param name="previous">前の控え。</param>
    /// <returns>変わったファイル名（同じなら空）。</returns>
    public IReadOnlyList<string> ChangedSince(LocaleFolderSnapshot previous)
    {
        var changed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, hash) in _hashes)
        {
            if (!previous._hashes.TryGetValue(name, out var before) || !string.Equals(before, hash, StringComparison.Ordinal))
                changed.Add(name);
        }
        foreach (var name in previous._hashes.Keys)
        {
            if (!_hashes.ContainsKey(name)) changed.Add(name);
        }
        return changed.ToList();
    }
}
