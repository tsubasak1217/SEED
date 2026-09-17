// ============================================================
//  ChangeListGroup.cs — 変更一覧のグループ分け（競合を最上部へ）
//
//  【役割】
//  <see cref="WorkingCopyStatus.Changes"/> を、パネルが 1 本のリストへ
//  そのまま流し込める「見出し + 項目」の並びに畳む。
//
//  【なぜ競合を分けるのか】
//  未解決の競合があるうちは送信できず、利用者は必ず 2 択（自分の変更を残す /
//  リモートを採用）を選ばなければならない。100 件の変更に紛れて 1 件の競合が
//  埋もれると、利用者は「送信できない理由」に辿り着けない。
//  **競合は常に最上部の独立したグループ**にして、必ず目に入るようにする。
//
//  【なぜ WPF に依存させないのか】
//  「どの順で・どう束ねるか」はロジックであってビューではない。
//  ここを切り出しておけば、並び順と見出し件数を単体テストで固定できる。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Presentation;

/// <summary>
/// 変更一覧のグループの種類。
/// </summary>
public enum ChangeGroupKind
{
    /// <summary>未解決の競合（常に最上部）。</summary>
    Conflicts,

    /// <summary>それ以外の変更。</summary>
    Changes,
}

/// <summary>
/// 変更一覧の 1 グループ（不変）。
/// </summary>
public sealed class ChangeListGroup
{
    /// <summary>グループの種類。</summary>
    public ChangeGroupKind Kind { get; }

    /// <summary>見出しに出す文字列（件数を含む）。</summary>
    public string Header { get; }

    /// <summary>このグループに属する変更。</summary>
    public IReadOnlyList<ChangedFile> Items { get; }

    /// <summary>競合グループか（2 択ボタンを出すかの判定に使う）。</summary>
    public bool IsConflictGroup => Kind == ChangeGroupKind.Conflicts;

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="kind">グループの種類。</param>
    /// <param name="header">見出し。</param>
    /// <param name="items">属する変更。</param>
    public ChangeListGroup(ChangeGroupKind kind, string header, IReadOnlyList<ChangedFile> items)
    {
        Kind   = kind;
        Header = header ?? string.Empty;
        Items  = items  ?? Array.Empty<ChangedFile>();
    }

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString() => $"{Kind} ({Items.Count}) {Header}";
}

/// <summary>
/// 状態から変更一覧のグループを組み立てる純関数。
/// </summary>
public static class ChangeListBuilder
{
    /// <summary>
    /// 状態を「競合グループ → 変更グループ」の並びへ畳む。
    ///
    /// <para>
    /// 空のグループは作らない（見出しだけが並ぶのを避けるため）。
    /// どちらも空なら空の並びを返し、パネルは「変更はありません」と出す。
    /// </para>
    /// </summary>
    /// <param name="status">作業コピーの状態（null なら空）。</param>
    /// <returns>表示順に並んだグループ。</returns>
    public static IReadOnlyList<ChangeListGroup> Build(WorkingCopyStatus? status)
    {
        if (status is null || status.Changes.Count == 0)
        {
            return Array.Empty<ChangeListGroup>();
        }

        var groups = new List<ChangeListGroup>(2);

        // ── 1. 未解決の競合（最上部）──
        var conflicts = Sort(status.Changes.Where(c => c.IsUnresolvedConflict));
        if (conflicts.Count > 0)
        {
            groups.Add(new ChangeListGroup(
                ChangeGroupKind.Conflicts,
                string.Format(
                    VersionControlMessages.PANEL_GROUP_CONFLICTS_FORMAT, conflicts.Count),
                conflicts));
        }

        // ── 2. それ以外の変更 ──
        //  解決済みの競合（AutoMerged / ResolvedKeepMine / ResolvedTakeRemote）は
        //  もう利用者の操作が要らないので、こちら側に入れる。
        var changes = Sort(status.Changes.Where(c => !c.IsUnresolvedConflict));
        if (changes.Count > 0)
        {
            groups.Add(new ChangeListGroup(
                ChangeGroupKind.Changes,
                string.Format(VersionControlMessages.PANEL_GROUP_CHANGES_FORMAT, changes.Count),
                changes));
        }

        return groups;
    }

    /// <summary>
    /// 表示順にそろえる（パスの辞書順。大文字小文字は区別しない）。
    ///
    /// <para>
    /// Lore が返す順は状態の内部表現に依存し、更新のたびに入れ替わり得る。
    /// 並びが毎回動くと利用者は同じ項目を目で追えないので、ここで固定する。
    /// </para>
    /// </summary>
    /// <param name="source">並べ替える変更。</param>
    private static IReadOnlyList<ChangedFile> Sort(IEnumerable<ChangedFile> source)
        => source.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
}
