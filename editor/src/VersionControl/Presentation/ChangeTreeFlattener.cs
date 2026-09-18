// ============================================================
//  ChangeTreeFlattener.cs — ツリー → 仮想化リストへ流せる平坦な行の並び
//
//  【役割】
//  <see cref="ChangeTreeNode"/> の木を「見えている行だけ」の 1 本の並びへ畳む。
//  畳まれたフォルダーの配下は行に出さない。各行は深さ（インデント段数）を持つ。
//
//  【なぜ TreeView を使わず平坦化するのか】
//  WPF の TreeView は入れ子の ItemsControl であり、親を展開した時点で
//  **子の中身が全部実体化される**（仮想化が効かない）。
//  「最新を取得」した直後の変更は数百〜数千件になり得るので、
//  ここは平坦な 1 本のリストにして仮想化 ListBox へ流す。
//  見た目のツリーはインデントと開閉ハンドルで作る。
//
//  【畳んでいる側を持つ理由（展開集合ではなく畳み集合）】
//  既定は「全部開いている」。展開集合で持つと、新しく現れたフォルダーが
//  最初は閉じた状態になり、「取得したのに何も出ない」ように見える。
//  畳み集合で持てば、利用者が明示的に閉じたものだけが閉じる。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.VersionControl.Presentation;

/// <summary>
/// 平坦化した 1 行（不変）。
/// </summary>
public sealed class ChangeTreeRow
{
    /// <summary>この行が表す節点。</summary>
    public ChangeTreeNode Node { get; }

    /// <summary>インデントの段数（根が 0）。</summary>
    public int Depth { get; }

    /// <summary>開いているか（子を持たない節点では常に偽）。</summary>
    public bool IsExpanded { get; }

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="node">節点。</param>
    /// <param name="depth">インデントの段数。</param>
    /// <param name="isExpanded">開いているか。</param>
    public ChangeTreeRow(ChangeTreeNode node, int depth, bool isExpanded)
    {
        Node       = node;
        Depth      = depth;
        IsExpanded = isExpanded;
    }

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString()
        => $"[{Depth}] {(IsExpanded ? "-" : "+")} {Node.RelativePath}";
}

/// <summary>
/// ツリーを「見えている行」の並びへ畳む純関数。
/// </summary>
public static class ChangeTreeFlattener
{
    /// <summary>
    /// 見えている行だけを上から順に並べる。
    /// </summary>
    /// <param name="root">根の節点（null なら空の並び）。</param>
    /// <param name="collapsedPaths">
    /// 利用者が畳んだ節点の相対パス（根は空文字で表す）。null なら全部開いている扱い。
    /// </param>
    /// <returns>表示順に並んだ行。</returns>
    public static IReadOnlyList<ChangeTreeRow> Flatten(
        ChangeTreeNode? root, IReadOnlySet<string>? collapsedPaths = null)
    {
        var rows = new List<ChangeTreeRow>();
        if (root is null) return rows;

        Walk(root, depth: 0, collapsedPaths, rows);
        return rows;
    }

    /// <summary>
    /// 節点を 1 行足し、開いていれば子も辿る（深さ優先）。
    /// </summary>
    /// <param name="node">対象の節点。</param>
    /// <param name="depth">インデントの段数。</param>
    /// <param name="collapsedPaths">畳まれている節点の集合。</param>
    /// <param name="rows">書き出し先。</param>
    private static void Walk(
        ChangeTreeNode node, int depth,
        IReadOnlySet<string>? collapsedPaths, List<ChangeTreeRow> rows)
    {
        var isExpanded = node.HasChildren && !IsCollapsed(node, collapsedPaths);

        rows.Add(new ChangeTreeRow(node, depth, isExpanded));

        if (!isExpanded) return;

        foreach (var child in node.Children)
        {
            Walk(child, depth + 1, collapsedPaths, rows);
        }
    }

    /// <summary>この節点が畳まれているか。</summary>
    /// <param name="node">対象の節点。</param>
    /// <param name="collapsedPaths">畳まれている節点の集合。</param>
    private static bool IsCollapsed(ChangeTreeNode node, IReadOnlySet<string>? collapsedPaths)
        => collapsedPaths is not null && collapsedPaths.Contains(node.RelativePath);

    /// <summary>
    /// 「すべて畳む」ための集合を作る（根と全フォルダーのパス）。
    ///
    /// <para>
    /// 「すべて展開」は空集合を渡せばよいので、対になる関数は要らない。
    /// </para>
    /// </summary>
    /// <param name="root">根の節点（null なら空集合）。</param>
    /// <returns>畳める節点すべての相対パス。</returns>
    public static IReadOnlySet<string> AllContainerPaths(ChangeTreeNode? root)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (root is null) return paths;

        Collect(root, paths);
        return paths;
    }

    /// <summary>畳める節点のパスを再帰的に集める。</summary>
    /// <param name="node">対象の節点。</param>
    /// <param name="paths">書き出し先。</param>
    private static void Collect(ChangeTreeNode node, HashSet<string> paths)
    {
        if (!node.IsContainer || !node.HasChildren) return;

        paths.Add(node.RelativePath);

        foreach (var child in node.Children)
        {
            Collect(child, paths);
        }
    }
}
