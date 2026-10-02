using SEED.Scripting;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace ScriptRegistryTests;

/// <summary>
/// 木のたどり方のテスト（GetScriptInChildren / GetScriptInParent の順の正典 ScriptTreeWalk）。
/// 偽の木:
/// <code>
/// root
///  ├ a
///  │  ├ a1
///  │  └ a2
///  └ b
///     └ b1
/// </code>
/// </summary>
public static class TreeWalkTests
{
    /// <summary>節 → 子の一覧（並びの順）。</summary>
    private static readonly Dictionary<string, string[]> Children = new()
    {
        ["root"] = new[] { "a", "b" },
        ["a"] = new[] { "a1", "a2" },
        ["b"] = new[] { "b1" },
    };

    /// <summary>節 → 親。</summary>
    private static readonly Dictionary<string, string> Parents = new()
    {
        ["a"] = "root",
        ["b"] = "root",
        ["a1"] = "a",
        ["a2"] = "a",
        ["b1"] = "b",
    };

    /// <summary>子の一覧（葉は空）。</summary>
    private static IReadOnlyList<string> ChildrenOf(string node)
        => Children.TryGetValue(node, out var list) ? list : Array.Empty<string>();

    /// <summary>親を引く（根は false）。</summary>
    private static bool TryGetParent(string node, out string parent)
    {
        if (Parents.TryGetValue(node, out var found))
        {
            parent = found;
            return true;
        }
        parent = string.Empty;
        return false;
    }

    /// <summary>見た順を記録し、<paramref name="hits"/> に入っている節で見つかったことにする見る処理を作る。</summary>
    /// <param name="visited">見た順の記録先。</param>
    /// <param name="hits">見つかったことにする節。</param>
    private static Func<string, string?> Visitor(List<string> visited, params string[] hits)
        => node =>
        {
            visited.Add(node);
            return hits.Contains(node) ? $"found:{node}" : null;
        };

    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("木: 部分木は行きがけ順（自分 → 子 0 の部分木 → 子 1 の部分木）で、無ければ全部見て null", () =>
        {
            var visited = new List<string>();
            var found = ScriptTreeWalk.FirstInSubtree<string, string>("root", true, ChildrenOf, Visitor(visited));
            Check.True(found is null, "見つからなければ null");
            Check.Equal("root,a,a1,a2,b,b1", string.Join(",", visited), "行きがけ順");
        });

        h.Add("木: 部分木は最初に見つかったものを返し、以降は見ない（a2 は b より先）", () =>
        {
            var visited = new List<string>();
            var found = ScriptTreeWalk.FirstInSubtree<string, string>("root", true, ChildrenOf, Visitor(visited, "b", "a2"));
            Check.Equal("found:a2", found, "深さ優先で先に来る a2");
            Check.Equal("root,a,a1,a2", string.Join(",", visited), "見つけたら止まる");
        });

        h.Add("木: 部分木で自分を含めないと根を見ない（子孫だけ）・子の無い根なら何も見ない", () =>
        {
            var visited = new List<string>();
            var withSelf = ScriptTreeWalk.FirstInSubtree<string, string>("root", true, ChildrenOf, Visitor(visited, "root", "b1"));
            Check.Equal("found:root", withSelf, "自分を含めると根");
            visited.Clear();
            var withoutSelf = ScriptTreeWalk.FirstInSubtree<string, string>("root", false, ChildrenOf, Visitor(visited, "root", "b1"));
            Check.Equal("found:b1", withoutSelf, "自分を含めないと子孫の b1");
            Check.True(!visited.Contains("root"), "根は見ない");
            visited.Clear();
            var leaf = ScriptTreeWalk.FirstInSubtree<string, string>("a1", false, ChildrenOf, Visitor(visited, "a1"));
            Check.True(leaf is null, "子の無い葉で自分を含めないと null");
            Check.Equal(0, visited.Count, "何も見ない");
        });

        h.Add("木: 祖先は近い順（自分 → 親 → 親の親）で、根で止まる", () =>
        {
            var visited = new List<string>();
            var found = ScriptTreeWalk.FirstInAncestors<string, string>("a1", true, TryGetParent, Visitor(visited));
            Check.True(found is null, "見つからなければ null");
            Check.Equal("a1,a,root", string.Join(",", visited), "近い順・根で止まる");
        });

        h.Add("木: 祖先で自分を含めないと親から（根から始めると見るものが無い）", () =>
        {
            var visited = new List<string>();
            var withSelf = ScriptTreeWalk.FirstInAncestors<string, string>("a1", true, TryGetParent, Visitor(visited, "a1", "root"));
            Check.Equal("found:a1", withSelf, "自分を含めると自分");
            visited.Clear();
            var withoutSelf = ScriptTreeWalk.FirstInAncestors<string, string>("a1", false, TryGetParent, Visitor(visited, "a1", "root"));
            Check.Equal("found:root", withoutSelf, "自分を含めないと祖先の root");
            Check.Equal("a,root", string.Join(",", visited), "親から見る");
            visited.Clear();
            var fromRoot = ScriptTreeWalk.FirstInAncestors<string, string>("root", false, TryGetParent, Visitor(visited, "root"));
            Check.True(fromRoot is null, "根から始めて自分を含めないと null");
            Check.Equal(0, visited.Count, "何も見ない");
        });
    }
}
