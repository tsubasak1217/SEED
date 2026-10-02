using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SEEDEditor.Reload;

/// <summary>
/// 監視の開始時に覚える既存のプレハブ 1 本（絶対パスと内容のハッシュ）。
/// </summary>
/// <param name="Path">プレハブの絶対パス。</param>
/// <param name="Hash">内容のハッシュ（<see cref="FileContentHash.TryCompute"/> と同じ形）。</param>
public readonly record struct PrefabKnownHash(string Path, string Hash);

/// <summary>
/// プレハブの外部変更の監視を始めるときに、既存の .actor / .actor2d の内容のハッシュを読む（背景スレッドで呼ぶ）。
///
/// <para>
/// 【なぜ要るか（2026-10-03 の 2 回目のレビュー #15）】
/// 追跡器（<see cref="PrefabExternalChangeTracker"/>）は起動直後に「知っている内容」を持たないので、まだ一度もイベントの
/// 来ていない .actor へ同じ内容の書き込み・コピー・touch が来ると Changed と判定し、再展開・当て直しを走らせていた
/// （docs/editor_auto_reload.md §7.1 の規則 3 と食い違い）。開始時に読んだ内容を覚えさせて、規則どおり Unchanged にする。
/// </para>
/// <para>
/// 【覚えてよいファイル】覚え込みの開始より前に書かれ、読む間に変わらなかったものだけ（読む前後の更新時刻が同じで、
/// 開始の時刻より前）。覚え込みの最中に書かれたファイルを覚えると、その書き込みのイベントが「知っている内容と同じ」で
/// Unchanged になり、外部の変更を取りこぼすため。読めない・更新時刻が分からないファイルも覚えない（従来どおり Changed）。
/// </para>
/// <para>
/// ファイルシステムへの依存は関数で受ける（<see cref="Collect"/>。テストは差し替える）。数が多くても UI を止めないよう、
/// 呼び出し側（<see cref="PrefabAutoReloader"/>）が背景スレッドで呼び、結果だけを UI スレッドで追跡器へ渡す。
/// </para>
/// </summary>
public static class PrefabKnownHashSeeder
{
    /// <summary>
    /// 覚えてよいファイルの内容のハッシュを読む。
    /// </summary>
    /// <param name="files">候補のプレハブの絶対パス（<see cref="EnumeratePrefabFiles"/> の結果など）。</param>
    /// <param name="seedStartedUtc">覚え込みを始めた時刻（これ以後に書かれたファイルは覚えない）。</param>
    /// <param name="readLastWriteUtc">ファイルの最終更新時刻（UTC）を読む関数（読めなければ null）。</param>
    /// <param name="readHash">ファイルの内容のハッシュを読む関数（読めなければ null）。</param>
    /// <param name="cancel">取り消し（監視の終了・覚え直し）。取り消されたら、それまでに読んだ分を返す。</param>
    /// <returns>覚えてよいファイルとハッシュ（候補の順）。</returns>
    public static IReadOnlyList<PrefabKnownHash> Collect(
        IEnumerable<string> files,
        DateTime seedStartedUtc,
        Func<string, DateTime?> readLastWriteUtc,
        Func<string, string?> readHash,
        CancellationToken cancel = default)
    {
        var result = new List<PrefabKnownHash>();
        foreach (var path in files)
        {
            if (cancel.IsCancellationRequested) break;

            // 読む前後の更新時刻を比べ、読む間に書かれたファイルを外す（読み終えた内容が開始の時点の内容と言えない）
            var before = readLastWriteUtc(path);
            if (before is null || before.Value >= seedStartedUtc) continue;
            var hash = readHash(path);
            if (hash is null) continue;
            var after = readLastWriteUtc(path);
            if (after != before) continue;

            result.Add(new PrefabKnownHash(path, hash));
        }
        return result;
    }

    /// <summary>
    /// アセットルート配下の監視対象のプレハブ（<see cref="PrefabWatchPaths.IsPrefabFile"/> が拾うもの）を列挙する。
    /// 列挙の途中で読めないフォルダ（権限・消えた）に当たっても、そこまでの結果を返す（監視の開始を止めない）。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <returns>プレハブの絶対パス。</returns>
    public static IReadOnlyList<string> EnumeratePrefabFiles(string assetsRoot)
    {
        var result = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            // アクセスできないフォルダは飛ばす（例外で列挙全体を止めない）
            IgnoreInaccessible    = true,
            MatchCasing           = MatchCasing.CaseInsensitive,
        };
        foreach (var filter in PrefabWatchPaths.WatchFilters)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(assetsRoot, filter, options))
                    if (PrefabWatchPaths.IsPrefabFile(path, assetsRoot)) result.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // ルートが消えた等。覚え込みは任意の改善なので、読めた分だけで進める（従来どおり Changed に戻るだけ）
            }
        }
        return result;
    }

    /// <summary>ファイルの最終更新時刻（UTC）を読む（無い・読めなければ null）。</summary>
    /// <param name="path">ファイルの絶対パス。</param>
    public static DateTime? TryGetLastWriteUtc(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
