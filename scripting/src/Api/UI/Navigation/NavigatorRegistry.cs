using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  NavigatorRegistry.cs — 画面のスタック・タブ（戻るを受ける「ナビゲーター」）の登録簿（W2-7）
//
//  戻るの段の Navigation の層（BackOrder.Navigation）は、登録されたナビゲーターのうち「見えていて上の段の中にある」
//  物を内側から尋ねる（NavigatorOrder.InnermostFirst）。見えているか・上の段かは祖先をたどって決める:
//    - 祖先（自分を含む）に隠れた（Visible = false）ノードがあれば見えていない（選んでいないタブ・覆われて隠れた画面）
//    - 祖先に画面のスタックの枠があり、それがそのスタックのいちばん上でなければ上の段ではない（透ける画面の下など）
//  祖先の数（深さ）が多いほど内側（タブの中のスタックは、タブを持つ画面のスタックより先に尋ねる）。
// ============================================================

/// <summary>戻るを受けるナビゲーター（画面のスタック・タブ）。</summary>
internal interface INavigator
{
    /// <summary>ナビゲーターのノード（祖先をたどる起点）。</summary>
    GameObject NavigatorNode { get; }

    /// <summary>戻るを受けたら true（下ろした・最初のタブへ戻した・画面が受けた）。</summary>
    bool HandleBack();
}

/// <summary>ナビゲーターの登録簿（静的）。</summary>
internal static class NavigatorRegistry
{
    /// <summary>祖先をたどる深さの上限（壊れた木で回り続けない）。</summary>
    private const int MaxAncestorDepth = 64;

    /// <summary>ナビゲーター（登録の順）。</summary>
    private static readonly List<INavigator> Navigators = new();
    /// <summary>画面のスタックの枠のノード → 持ち主のスタックと段の番号。</summary>
    private static readonly Dictionary<(uint, uint), (ScreenStack Stack, int EntryId)> Frames = new();

    /// <summary>登録する。</summary>
    public static void Register(INavigator navigator)
    {
        if (!Navigators.Contains(navigator)) Navigators.Add(navigator);
    }

    /// <summary>外す。</summary>
    public static void Unregister(INavigator navigator) => Navigators.Remove(navigator);

    /// <summary>画面の枠を登録する。</summary>
    public static void RegisterFrame(GameObject frame, ScreenStack stack, int entryId) => Frames[NavNode.Key(frame)] = (stack, entryId);

    /// <summary>画面の枠を外す。</summary>
    public static void UnregisterFrame(GameObject frame) => Frames.Remove(NavNode.Key(frame));

    /// <summary>
    /// 見えていて上の段の中にあるナビゲーターへ、内側から戻るを尋ねる（最初に受けた物で止める）。
    /// </summary>
    /// <returns>どれかが受けたら true。</returns>
    public static bool DispatchBack()
    {
        var candidates = new List<(INavigator, int, bool)>();
        foreach (var navigator in Navigators.ToArray())
        {
            var (depth, active) = Inspect(navigator.NavigatorNode);
            candidates.Add((navigator, depth, active));
        }
        foreach (var navigator in NavigatorOrder.InnermostFirst(candidates))
        {
            if (navigator.HandleBack()) return true;
        }
        return false;
    }

    /// <summary>ノードが見えていて、祖先の画面のスタックの上の段の中にあるか（フォーカスの範囲を前へ出すか決める）。</summary>
    public static bool IsActiveNode(GameObject node) => Inspect(node).Active;

    /// <summary>
    /// 画面の枠の中にある入れ子のスタック（見えていて上の段の中にあるもの）の上の画面の範囲を、外側から順に前へ出す
    /// （いちばん内側のスタックの範囲がいちばん前になる。シェルに戻ったとき、選んでいるタブの画面の範囲を前へ）。
    /// </summary>
    public static void BringNestedToFront(GameObject frame)
    {
        if (!frame.IsValid) return;
        var nested = new List<(ScreenStack Stack, int Depth)>();
        foreach (var navigator in Navigators.ToArray())
        {
            if (navigator is not ScreenStack stack || !IsDescendant(stack.NavigatorNodeInternal, frame)) continue;
            var (depth, active) = Inspect(stack.NavigatorNodeInternal);
            if (active) nested.Add((stack, depth));
        }
        nested.Sort((a, b) => a.Depth.CompareTo(b.Depth));
        foreach (var (stack, _) in nested) stack.BringTopScopeToFront();
    }

    /// <summary>node が ancestor の子孫か（自分自身は含めない）。</summary>
    private static bool IsDescendant(GameObject node, GameObject ancestor)
    {
        var key = NavNode.Key(ancestor);
        var current = node.Parent;
        for (int depth = 0; depth < MaxAncestorDepth && current.IsValid; depth++)
        {
            if (NavNode.Key(current) == key) return true;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>ナビゲーターの状態（診断・ログ用。名前・深さ・尋ねるか）。</summary>
    public static IEnumerable<(string Name, int Depth, bool Active)> Describe()
    {
        foreach (var navigator in Navigators)
        {
            var (depth, active) = Inspect(navigator.NavigatorNode);
            yield return (navigator.NavigatorNode.Name, depth, active);
        }
    }

    /// <summary>
    /// 祖先をたどり、深さ（祖先の数）と「見えていて上の段の中にあるか」を求める。
    /// </summary>
    private static (int Depth, bool Active) Inspect(GameObject node)
    {
        if (!node.IsValid) return (0, false);
        bool active = true;
        int depth = 0;
        var current = node;
        while (current.IsValid && depth < MaxAncestorDepth)
        {
            if (!current.Visible) active = false;
            if (Frames.TryGetValue(NavNode.Key(current), out var frame) && !frame.Stack.IsTopEntry(frame.EntryId)) active = false;
            current = current.Parent;
            depth++;
        }
        return (depth, active);
    }
}
