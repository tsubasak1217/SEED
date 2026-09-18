// ============================================================
//  BranchOperationRules.cs — ブランチのマージ／削除（アーカイブ）の可否を決める規則
//
//  【役割】
//  「このブランチはマージ元にできるか」「このブランチは削除（アーカイブ）してよいか」
//  の判断を 1 か所へ閉じる。判断に使うのは名前だけで、Lore も WPF も呼ばない。
//
//  【なぜ 1 か所に閉じるのか】
//  この規則は **2 か所で必要になる**:
//    ・パネル … ダイアログの一覧から除外する（選ばせない）
//    ・プロバイダ … 実際に実行する直前に弾く（パネル以外から呼ばれても守る）
//  同じ規則を両方で書くと、片方だけ直したときに
//  「一覧には出ないのに実行できてしまう」「一覧に出るのに必ず失敗する」が生まれる。
//
//  【守っていること】
//  1. 現在のブランチはマージ元にできない（自分自身を取り込む操作に意味が無い）。
//  2. 現在のブランチはアーカイブできない（足場を消す操作になる）。
//  3. 既定ブランチ（通常 "main"）はアーカイブできない。
//     Lore の archive は取り消せないうえ、既定ブランチを隠すと
//     ほかの参加者の一覧からも消えるため、UI からは行えないようにする。
//
//  【名前の比較を大文字小文字を区別しないで行う理由】
//  Lore のブランチ名が大文字小文字を区別するかは pre-1.0 で保証が無い。
//  ここでの比較は「守るべきブランチを取り違えないため」の防御であり、
//  厳密一致にして "Main" を削除できてしまうより、
//  区別せずに弾いて「消せない」側へ倒す方が損失が小さい。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.VersionControl.Model;

/// <summary>
/// 規則の判定結果（不変）。許されないときは利用者向けの理由を持つ。
/// </summary>
/// <param name="Allowed">実行してよいか。</param>
/// <param name="Reason">
/// 許されない理由（利用者にそのまま見せられる 1 行）。許されるときは空文字。
/// </param>
public readonly record struct BranchOperationCheck(bool Allowed, string Reason)
{
    /// <summary>許可する。</summary>
    public static BranchOperationCheck Allow() => new(true, string.Empty);

    /// <summary>理由つきで拒否する。</summary>
    /// <param name="reason">利用者向けの理由。</param>
    public static BranchOperationCheck Deny(string reason) => new(false, reason ?? string.Empty);
}

/// <summary>
/// ブランチのマージ／削除（アーカイブ）の可否を決める純粋な規則。
/// </summary>
public static class BranchOperationRules
{
    /// <summary>
    /// ブランチ名の比較方法。大文字小文字を区別しない（理由はファイル冒頭）。
    /// </summary>
    private const StringComparison NAME_COMPARISON = StringComparison.OrdinalIgnoreCase;

    // ── 単体の可否 ──────────────────────────────────────────

    /// <summary>
    /// 指定したブランチを「現在のブランチへ取り込むマージ元」にしてよいか。
    /// </summary>
    /// <param name="sourceBranch">取り込み元のブランチ名。</param>
    /// <param name="currentBranch">現在のブランチ名。</param>
    public static BranchOperationCheck CheckMergeSource(
        string? sourceBranch, string? currentBranch)
    {
        if (string.IsNullOrWhiteSpace(sourceBranch))
            return BranchOperationCheck.Deny(VersionControlMessages.BRANCH_NAME_REQUIRED);

        // 現在のブランチが分からない状況（状態未取得・オフライン直後）では、
        // 「同じブランチではない」と決めつけない。ここで弾くと正当な操作まで止まるので、
        // 名前が空のときだけは通し、実行時の Lore 側の判断に委ねる。
        if (!string.IsNullOrEmpty(currentBranch)
            && string.Equals(sourceBranch, currentBranch, NAME_COMPARISON))
        {
            return BranchOperationCheck.Deny(VersionControlMessages.BRANCH_MERGE_SELF);
        }

        return BranchOperationCheck.Allow();
    }

    /// <summary>
    /// 指定したブランチを削除（アーカイブ）してよいか。
    /// </summary>
    /// <param name="name">対象のブランチ名。</param>
    /// <param name="currentBranch">現在のブランチ名。</param>
    /// <param name="defaultBranchName">既定ブランチの名前（通常 "main"）。</param>
    public static BranchOperationCheck CheckArchive(
        string? name, string? currentBranch, string? defaultBranchName)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BranchOperationCheck.Deny(VersionControlMessages.BRANCH_NAME_REQUIRED);

        if (!string.IsNullOrEmpty(currentBranch)
            && string.Equals(name, currentBranch, NAME_COMPARISON))
        {
            return BranchOperationCheck.Deny(VersionControlMessages.BRANCH_ARCHIVE_CURRENT);
        }

        if (!string.IsNullOrEmpty(defaultBranchName)
            && string.Equals(name, defaultBranchName, NAME_COMPARISON))
        {
            return BranchOperationCheck.Deny(
                string.Format(VersionControlMessages.BRANCH_ARCHIVE_DEFAULT_FORMAT,
                              defaultBranchName));
        }

        return BranchOperationCheck.Allow();
    }

    // ── 一覧の絞り込み（ダイアログに並べる候補）──────────────

    /// <summary>
    /// マージ元として選べるブランチ名だけを取り出す。
    /// </summary>
    /// <param name="branches">取得できたブランチ一覧（null 可）。</param>
    /// <param name="currentBranch">現在のブランチ名。</param>
    /// <returns>選べるブランチ名（元の並び順のまま）。</returns>
    public static IReadOnlyList<string> MergeSourceCandidates(
        IReadOnlyList<BranchInfo>? branches, string? currentBranch)
        => Filter(branches, name => CheckMergeSource(name, currentBranch).Allowed);

    /// <summary>
    /// 削除（アーカイブ）できるブランチ名だけを取り出す。
    /// </summary>
    /// <param name="branches">取得できたブランチ一覧（null 可）。</param>
    /// <param name="currentBranch">現在のブランチ名。</param>
    /// <param name="defaultBranchName">既定ブランチの名前。</param>
    /// <returns>選べるブランチ名（元の並び順のまま）。</returns>
    public static IReadOnlyList<string> ArchiveCandidates(
        IReadOnlyList<BranchInfo>? branches, string? currentBranch, string? defaultBranchName)
        => Filter(branches, name => CheckArchive(name, currentBranch, defaultBranchName).Allowed);

    /// <summary>
    /// 一覧から名前を取り出し、条件に合うものだけを返す共通処理。
    /// 空の名前と重複は落とす（Lore が LOCAL / REMOTE を別行で返すことがあるため）。
    /// </summary>
    /// <param name="branches">ブランチ一覧（null 可）。</param>
    /// <param name="accept">残す条件。</param>
    private static IReadOnlyList<string> Filter(
        IReadOnlyList<BranchInfo>? branches, Func<string, bool> accept)
    {
        if (branches is null) return Array.Empty<string>();

        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(branches.Count);

        foreach (var branch in branches)
        {
            var name = branch?.Name ?? string.Empty;
            if (name.Length == 0) continue;
            if (!seen.Add(name)) continue;
            if (!accept(name)) continue;

            result.Add(name);
        }

        return result;
    }
}
