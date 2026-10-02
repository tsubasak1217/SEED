using SEEDEditor.Preview;
using SpriteRigTests;

namespace ScreenPreviewTests;

/// <summary>
/// Delete の振り分け（PreviewDeletionPlanner）。プレビューを消した後の番号のずれまで、木を実際に消して確かめる。
/// </summary>
public static class DeletionPlannerTests
{
    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("Delete: プレビューの無い選択は従来どおり（番号も変えない）", NoPreview);
        h.Add("Delete: 根は PREVIEW_CLEAR、普通のノードは消した後の番号", RootAndNormal);
        h.Add("Delete: 根が複数なら番号の大きい順に送る", MultipleRootsDescending);
        h.Add("Delete: 中だけは消さない（数を返す）・根と一緒なら黙って含める", InnerNodes);
        h.Add("Delete: 入れ子の根は外側の根と一緒に消えるので送らない", NestedRoots);
        h.Add("Delete: 詰めた番号は、木から部分木を実際に消した後の番号と一致する", AdjustedIdsMatchSimulation);
    }

    // 試しの木（DFS 前順の番号）:
    //   0 Root
    //   ├ 1 A
    //   ├ 2 P      ← プレビューの根
    //   │ └ 3 P1   ← 中
    //   │   └ 4 P2 ← 中（NestedRoots では入れ子の根）
    //   ├ 5 B
    //   │ └ 6 Q    ← プレビューの根（普通のノードの子）
    //   │   └ 7 Q1 ← 中
    //   └ 8 C
    private static List<PreviewTreeNode> Tree(bool nestedRootAt4 = false) =>
    [
        new(0, null, false, false),
        new(1, 0, false, false),
        new(2, 0, true, true),
        new(3, 2, true, false),
        new(4, 3, true, nestedRootAt4),
        new(5, 0, false, false),
        new(6, 5, true, true),
        new(7, 6, true, false),
        new(8, 0, false, false),
    ];

    private static void NoPreview()
    {
        var plan = PreviewDeletionPlanner.Plan([1, 8, 999], Tree());
        Check.True(!plan.TouchesPreview, "プレビューに触れない");
        Check.Equal("1,8,999", string.Join(",", plan.NormalIds), "普通のノード（木に無い番号も普通として扱う）");
        Check.Equal("1,8,999", string.Join(",", plan.NormalIdsAfterClear), "番号はそのまま");
        Check.Equal(0, plan.ClearRoots.Count, "消すプレビューなし");
    }

    private static void RootAndNormal()
    {
        var plan = PreviewDeletionPlanner.Plan([5, 2, 8], Tree());
        Check.True(plan.TouchesPreview, "プレビューに触れる");
        Check.Equal("2", string.Join(",", plan.ClearRoots), "根");
        Check.Equal("5,8", string.Join(",", plan.NormalIds), "普通のノード（いまの番号）");
        Check.Equal("2,5", string.Join(",", plan.NormalIdsAfterClear), "P（3 ノード）を消した後の番号");
        Check.Equal(0, plan.BlockedInnerCount, "消せない中のノードなし");

        // 根より前の普通のノードは詰めない
        var before = PreviewDeletionPlanner.Plan([1, 2], Tree());
        Check.Equal("1", string.Join(",", before.NormalIdsAfterClear), "前のノードはそのまま");
    }

    private static void MultipleRootsDescending()
    {
        var plan = PreviewDeletionPlanner.Plan([2, 6, 8], Tree());
        Check.Equal("6,2", string.Join(",", plan.ClearRoots), "番号の大きい順（後ろから消せば前の番号はずれない）");
        Check.Equal("3", string.Join(",", plan.NormalIdsAfterClear), "C は 8 - 3 - 2");
    }

    private static void InnerNodes()
    {
        var innerOnly = PreviewDeletionPlanner.Plan([3, 7], Tree());
        Check.Equal(0, innerOnly.ClearRoots.Count, "中だけなら何も消さない");
        Check.Equal(2, innerOnly.BlockedInnerCount, "消せない中のノードの数");
        Check.True(innerOnly.TouchesPreview, "プレビューに触れる（トーストを出す）");

        var withRoot = PreviewDeletionPlanner.Plan([2, 4, 3], Tree());
        Check.Equal("2", string.Join(",", withRoot.ClearRoots), "根だけを送る");
        Check.Equal(0, withRoot.BlockedInnerCount, "根と一緒に消える中のノードは数えない");

        var mixed = PreviewDeletionPlanner.Plan([2, 7], Tree());
        Check.Equal(1, mixed.BlockedInnerCount, "別のプレビューの中は消せない");
    }

    private static void NestedRoots()
    {
        var both = PreviewDeletionPlanner.Plan([4, 2], Tree(nestedRootAt4: true));
        Check.Equal("2", string.Join(",", both.ClearRoots), "外側も選ばれていれば内側は送らない");

        var innerRootOnly = PreviewDeletionPlanner.Plan([4], Tree(nestedRootAt4: true));
        Check.Equal("4", string.Join(",", innerRootOnly.ClearRoots), "入れ子の根だけなら入れ子を消す");
        Check.Equal(0, innerRootOnly.BlockedInnerCount, "入れ子の根は消せる");
    }

    private static void AdjustedIdsMatchSimulation()
    {
        var tree = Tree();
        var selections = new[] { new[] { 2, 5, 8 }, new[] { 6, 8, 1 }, new[] { 2, 6, 5, 8 }, new[] { 1, 6 } };
        foreach (var selection in selections)
        {
            var plan = PreviewDeletionPlanner.Plan(selection, tree);

            // 木から根の部分木を実際に（送る順に）消し、残ったノードの前順の番号を数え直す
            var order = tree.Select(n => n.Id).ToList();   // いまの前順
            foreach (var root in plan.ClearRoots)
            {
                var subtree = Descendants(tree, root).Append(root).ToHashSet();
                order.RemoveAll(subtree.Contains);
            }
            var simulated = plan.NormalIds.Select(id => order.IndexOf(id)).ToList();
            Check.Equal(string.Join(",", simulated), string.Join(",", plan.NormalIdsAfterClear),
                        $"選択 [{string.Join(",", selection)}] の詰めた番号");
        }
    }

    /// <summary>子孫を集める。</summary>
    private static IEnumerable<int> Descendants(List<PreviewTreeNode> tree, int id) =>
        tree.Where(n => n.ParentId == id).SelectMany(n => Descendants(tree, n.Id).Append(n.Id));
}
