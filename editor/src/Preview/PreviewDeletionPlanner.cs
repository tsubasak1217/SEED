// ============================================================
//  PreviewDeletionPlanner.cs — Delete で消す選択を「プレビュー」と「普通のノード」に分ける
//
//  【規則】（docs/editor_screen_preview.md §3。Delete キー・シーンビューの「削除」）
//  - プレビューの根     → PREVIEW_CLEAR（根ごとに 1 通。プレビューはシーンに無いので未保存にしない）
//  - プレビューの中     → 消さない（呼び出し側がトースト「根を選んで消すと、プレビューごと消えます」）。
//                         ただし同じ選択に祖先の根があれば、その根と一緒に消えるので黙って含める
//  - 普通のノード       → 従来どおり DELETE / DELETE_RECURSIVE
//
//  【番号のずれ】
//  ランタイムは命令ごとに「いまの木」の DFS 番号（前順）で相手を引く。プレビューを 1 つ消すと、
//  それより後ろのノードの番号は消えた部分木の大きさだけ詰まる。そこで
//    1) 根は**番号の大きい順**に消す（後ろを消しても前の番号は変わらないので、残りの根の番号はずれない）
//    2) 普通のノードは「自分より前で消えた部分木の大きさの合計」を引いた番号で消す
//       （普通のノードはプレビューの部分木の中に無いので、消えた部分木は丸ごと前か後ろにある）
//  入れ子（根の中の根）は外側の根と一緒に消えるので、外側も選ばれていれば内側は送らない。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ScreenPreviewTests）がリンクして試すので WPF 型を使わない。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Preview;

/// <summary>
/// 木の 1 ノード（分けるのに要る分だけ）。
/// </summary>
/// <param name="Id">DFS 番号（前順）。</param>
/// <param name="ParentId">親の DFS 番号（ルートなら null）。</param>
/// <param name="IsPreview">プレビューの部分木の中か（根を含む）。</param>
/// <param name="IsPreviewRoot">プレビューの根か。</param>
public readonly record struct PreviewTreeNode(int Id, int? ParentId, bool IsPreview, bool IsPreviewRoot);

/// <summary>
/// 分けた結果。
/// </summary>
/// <param name="ClearRoots">PREVIEW_CLEAR で消す根（番号の大きい順。送る順）。</param>
/// <param name="NormalIds">普通のノード（いまの番号。選択の順）。</param>
/// <param name="NormalIdsAfterClear">普通のノード（<paramref name="ClearRoots"/> を消した後の番号。並びは NormalIds と同じ）。</param>
/// <param name="BlockedInnerCount">消せない「プレビューの中のノード」の数（根が選ばれていないもの）。</param>
public sealed record PreviewDeletionPlan(
    IReadOnlyList<int> ClearRoots,
    IReadOnlyList<int> NormalIds,
    IReadOnlyList<int> NormalIdsAfterClear,
    int BlockedInnerCount)
{
    /// <summary>
    /// 選択にプレビューのノードがあったか（無ければ従来どおりでよい）。
    /// 中のノードは、根と一緒に消えるなら根が <see cref="ClearRoots"/> に、消せないなら <see cref="BlockedInnerCount"/> に入る。
    /// </summary>
    public bool TouchesPreview => ClearRoots.Count > 0 || BlockedInnerCount > 0;
}

/// <summary>
/// Delete で消す選択を分ける。状態を持たない。
/// </summary>
public static class PreviewDeletionPlanner
{
    /// <summary>
    /// 選択を分ける。
    /// </summary>
    /// <param name="selectedIds">選択（DFS 番号。木に無い番号〈カメラの仮想ノードなど〉は普通のノードとして扱う）。</param>
    /// <param name="tree">表示中の木の全ノード。</param>
    /// <returns>分けた結果。</returns>
    public static PreviewDeletionPlan Plan(IReadOnlyList<int> selectedIds, IEnumerable<PreviewTreeNode> tree)
    {
        var byId     = new Dictionary<int, PreviewTreeNode>();
        var children = new Dictionary<int, List<int>>();
        foreach (var node in tree)
        {
            byId[node.Id] = node;
            if (node.ParentId is int parent)
            {
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<int>();
                list.Add(node.Id);
            }
        }

        // ── 選択を 3 つに分ける ──
        var normal       = new List<int>();
        var selectedRoot = new HashSet<int>();
        var inner        = new List<int>();
        foreach (var id in selectedIds.Distinct())
        {
            if (!byId.TryGetValue(id, out var node) || !node.IsPreview) normal.Add(id);
            else if (node.IsPreviewRoot) selectedRoot.Add(id);
            else inner.Add(id);
        }

        // ── 祖先に選ばれた根がある根・中のノードは、その根と一緒に消える ──
        bool CoveredBySelectedAncestorRoot(int id)
        {
            var current = byId.TryGetValue(id, out var n) ? n.ParentId : null;
            while (current is int parentId && byId.TryGetValue(parentId, out var parent))
            {
                if (parent.IsPreviewRoot && selectedRoot.Contains(parentId)) return true;
                current = parent.ParentId;
            }
            return false;
        }

        var clearRoots = selectedRoot
            .Where(id => !CoveredBySelectedAncestorRoot(id))
            .OrderByDescending(id => id)
            .ToList();
        var blocked = inner.Count(id => !CoveredBySelectedAncestorRoot(id));

        // ── 普通のノードの番号を、前で消えた部分木の大きさだけ詰める ──
        var removed = clearRoots.Select(root => (Root: root, Size: SubtreeSize(root, children))).ToList();
        var adjusted = normal
            .Select(id => id - removed.Where(r => r.Root < id).Sum(r => r.Size))
            .ToList();

        return new PreviewDeletionPlan(clearRoots, normal, adjusted, blocked);
    }

    /// <summary>
    /// 部分木のノード数（自分を含む）を数える（深い木でもスタックを溢れさせないよう繰り返しで数える）。
    /// </summary>
    /// <param name="rootId">部分木の根。</param>
    /// <param name="children">親 → 子の対応。</param>
    /// <returns>ノード数。</returns>
    private static int SubtreeSize(int rootId, IReadOnlyDictionary<int, List<int>> children)
    {
        var count   = 0;
        var pending = new Stack<int>();
        pending.Push(rootId);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            count++;
            if (children.TryGetValue(id, out var kids))
                foreach (var kid in kids) pending.Push(kid);
        }
        return count;
    }
}
