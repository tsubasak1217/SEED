// ============================================================
//  LoreBranchTranslator.cs — LOCAL / REMOTE に分かれたブランチ行の統合
//
//  【役割】
//  Lore の branch list は同じブランチを location ごとに別イベントで返す。
//  例: "main" が LOCAL と REMOTE で 2 件。そのまま並べるとパネルに重複が出るし、
//  「まだサーバへ送っていないブランチ」も見分けられない。
//  名前をキーに 1 件へ統合し、どこに在るかをフラグで持たせる。
//
//  【統合の規則】
//  ・並び順は **最初に現れた順** を保つ（Lore が返す順＝自然な順序を壊さない）。
//  ・IsCurrent は LOCAL / REMOTE のどちらかで真なら真（論理和）。
//    「現在のブランチ」は 1 つしか無いので、どちらの行で報告されても拾う。
//  ・未知の location（Lore が値を増やした場合）は **ローカル扱い** にする。
//    見えないブランチができるより、余分に見える方が害が小さい。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using SEEDEditor.VersionControl.Lore.Backend;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Lore;

/// <summary>
/// ブランチ行を統合する純粋関数。
/// </summary>
public static class LoreBranchTranslator
{
    /// <summary>Lore の location が「ローカル」を表す値。</summary>
    private const string LOCATION_LOCAL = "LOCAL";

    /// <summary>Lore の location が「リモート」を表す値。</summary>
    private const string LOCATION_REMOTE = "REMOTE";

    /// <summary>
    /// LOCAL / REMOTE に分かれた行を、ブランチ名ごとに 1 件へ統合する。
    /// </summary>
    /// <param name="rows">branch list が返した行。</param>
    /// <returns>統合後のブランチ一覧（最初に現れた順）。</returns>
    public static IReadOnlyList<BranchInfo> Merge(IReadOnlyList<LoreBranchRow>? rows)
    {
        if (rows is null || rows.Count == 0) return Array.Empty<BranchInfo>();

        // 名前 → 蓄積中の値。並び順を保つため、キーの出現順も別に記録する。
        var accumulated = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var order       = new List<string>();

        foreach (var row in rows)
        {
            // 名前が空の行は統合キーを作れない。落とすしかないので飛ばす。
            if (string.IsNullOrWhiteSpace(row.Name)) continue;

            var name     = row.Name.Trim();
            var isRemote = IsRemoteLocation(row.Location);

            if (!accumulated.TryGetValue(name, out var acc))
            {
                acc = new Accumulator();
                accumulated[name] = acc;
                order.Add(name);
            }

            // 論理和で足し込む。同じ行が 2 回来ても結果は変わらない（冪等）。
            acc.IsCurrent      |= row.IsCurrent;
            acc.ExistsOnRemote |= isRemote;
            acc.ExistsLocally  |= !isRemote;
        }

        var result = new List<BranchInfo>(order.Count);
        foreach (var name in order)
        {
            var acc = accumulated[name];
            result.Add(new BranchInfo(
                name, acc.IsCurrent, acc.ExistsLocally, acc.ExistsOnRemote));
        }
        return result;
    }

    /// <summary>
    /// location の文字列が「リモート」を表すか判定する。
    /// 未知の値はローカル扱い（見えないブランチを作らない）。
    /// </summary>
    /// <param name="location">Lore が返した location の文字列表現。</param>
    public static bool IsRemoteLocation(string? location)
        => !string.IsNullOrWhiteSpace(location)
           && string.Equals(location.Trim(), LOCATION_REMOTE, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// location の文字列が「ローカル」を表すか判定する（診断・テスト用）。
    /// </summary>
    /// <param name="location">Lore が返した location の文字列表現。</param>
    public static bool IsLocalLocation(string? location)
        => !string.IsNullOrWhiteSpace(location)
           && string.Equals(location.Trim(), LOCATION_LOCAL, StringComparison.OrdinalIgnoreCase);

    /// <summary>統合中の値を溜める入れ物（このファイル内でのみ使う）。</summary>
    private sealed class Accumulator
    {
        /// <summary>現在のブランチか。</summary>
        public bool IsCurrent;

        /// <summary>ローカルに存在するか。</summary>
        public bool ExistsLocally;

        /// <summary>リモートに存在するか。</summary>
        public bool ExistsOnRemote;
    }
}
