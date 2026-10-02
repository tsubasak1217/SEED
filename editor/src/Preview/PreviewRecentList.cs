// ============================================================
//  PreviewRecentList.cs — 最近プレビューしたプレハブの一覧の操作（純粋な関数）
//
//  【役割】
//  ヒエラルキーの右クリック「プレハブをプレビュー」の先頭と、プレハブを選ぶ窓の「最近使ったもの」に出す一覧の規則:
//    - 使ったものを先頭へ移す
//    - 同じパスは大小文字を無視して 1 つにする（Windows のパスは大小文字を区別しないため）
//    - 上限 <see cref="PreviewRecentList.MaxCount"/> 件
//  保存は PreviewRecentStore（ここはファイルを知らない）。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ScreenPreviewTests）がリンクして試すので WPF 型を使わない。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.Preview;

/// <summary>
/// 最近プレビューしたプレハブの一覧の操作。状態を持たない。
/// </summary>
public static class PreviewRecentList
{
    /// <summary>覚えておく件数の上限。</summary>
    public const int MaxCount = 8;

    /// <summary>
    /// 使ったプレハブを先頭へ移した一覧を返す（元の一覧は変えない）。
    /// </summary>
    /// <param name="current">いまの一覧（最近の順）。</param>
    /// <param name="path">使ったプレハブ（assets:// 仮想パス）。空白なら何も足さない。</param>
    /// <returns>新しい一覧（重複なし・上限まで）。</returns>
    public static IReadOnlyList<string> Push(IEnumerable<string?>? current, string? path)
    {
        var merged = new List<string?>();
        if (!string.IsNullOrWhiteSpace(path)) merged.Add(path.Trim());
        if (current is not null) merged.AddRange(current);
        return Normalize(merged);
    }

    /// <summary>
    /// 一覧を整える（空白を捨てる・前後の空白を落とす・大小文字を無視して重複を除く〈先にあるものを残す〉・上限まで）。
    /// </summary>
    /// <param name="paths">元の一覧（最近の順）。</param>
    /// <returns>整えた一覧。</returns>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? paths)
    {
        var result = new List<string>();
        if (paths is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = raw.Trim();
            if (!seen.Add(path)) continue;
            result.Add(path);
            if (result.Count >= MaxCount) break;
        }
        return result;
    }
}
