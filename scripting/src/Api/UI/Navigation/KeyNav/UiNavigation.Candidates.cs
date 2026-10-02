using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  UiNavigation.Candidates.cs — 方向キーの候補を集めて絞る（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  候補 = 登録簿（UiRegistry）の部品のアダプタ（NavAdapters）＋ 一覧の見せている行（ListViewNavSource）＋ スクリプトが足した部品。
//  絞り方 = いちばん前のフォーカスの範囲から移れる範囲の部品（NavScopeFilter）で、移れる（IsNavigable）もの。
//  集めるのは入力があったとき（方向・決定・最初の部品へ）だけ（毎フレームは集めない）。
// ============================================================

public static partial class UiNavigation
{
    /// <summary>すべての候補（絞る前。部品のアダプタ・一覧の行・スクリプトが足した部品）。</summary>
    private static List<IUiNavigable> CollectRaw()
    {
        var all = new List<IUiNavigable>();
        foreach (var widget in UiRegistry.Snapshot())
            if (NavAdapters.For(widget) is { } nav) all.Add(nav);
        ListViewNavSource.Collect(all);
        foreach (var item in Custom)
            if (!all.Contains(item)) all.Add(item);
        return all;
    }

    /// <summary>いちばん前の範囲から移れる、移れる候補（exclude は除く）。</summary>
    private static List<IUiNavigable> CollectAllowed(IUiNavigable? exclude)
    {
        var top = UiFocus.TopScope;
        bool overlay = UiFocus.IsOverlayScope(top);
        var inScope = NavScopeFilter.Filter(CollectRaw(), item => UiFocus.ScopeOf(item.NavNode), top,
            (a, b) => ContainsScope((FocusScope)a, (FocusScope)b), overlay);
        var result = new List<IUiNavigable>(inScope.Count);
        foreach (var item in inScope)
            if (!ReferenceEquals(item, exclude) && item.IsNavigable) result.Add(item);
        return result;
    }

    /// <summary>候補の矩形の並び（候補と同じ順）。</summary>
    private static List<Rect> RectsOf(List<IUiNavigable> items)
    {
        var rects = new List<Rect>(items.Count);
        foreach (var item in items) rects.Add(item.CanvasRect);
        return rects;
    }

    /// <summary>範囲へ、いちばん前の範囲から移れるか（NavScopeFilter.Allows）。</summary>
    private static bool IsAllowedScope(FocusScope? scope, FocusScope? top)
    {
        bool contains = scope is not null && top is not null && !ReferenceEquals(scope, top) && ContainsScope(scope, top);
        return NavScopeFilter.Allows(scope, top, contains, UiFocus.IsOverlayScope(top));
    }

    /// <summary>範囲 a が範囲 b を中に含むか（a の持ち主のノードが b の持ち主のノードの祖先）。</summary>
    private static bool ContainsScope(FocusScope a, FocusScope b) => IsAncestorOrSelf(a.Owner, b.Owner.Parent);

    /// <summary>ancestor が node 自身かその祖先か。</summary>
    private static bool IsAncestorOrSelf(GameObject ancestor, GameObject node)
    {
        if (!ancestor.IsValid) return false;
        var key = NavNode.Key(ancestor);
        var current = node;
        for (int depth = 0; depth < UiVisibility.MaxDepth && current.IsValid; depth++)
        {
            if (NavNode.Key(current) == key) return true;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// 読む順で最初の部品へフォーカスする（root が有効ならその下だけ。scopeOnly ならいちばん前の範囲の部品だけ
    /// 〈allowOtherScopes なら、それが無いとき移れるほかの範囲の部品でもよい〉。どちらでもなければ移れる部品すべて）。選べたら true。
    /// </summary>
    private static bool TryFocusFirst(GameObject root, bool scopeOnly, bool showRing, bool allowOtherScopes = true)
    {
        var candidates = CollectAllowed(exclude: null);
        if (root.IsValid)
        {
            candidates.RemoveAll(item => !IsAncestorOrSelf(root, item.NavNode));
        }
        else if (scopeOnly)
        {
            var top = UiFocus.TopScope;
            var exact = candidates.FindAll(item => ReferenceEquals(UiFocus.ScopeOf(item.NavNode), top));
            if (exact.Count > 0 || !allowOtherScopes) candidates = exact;
        }
        if (candidates.Count == 0) return false;
        int i = NavigationMath.PickFirst(RectsOf(candidates));
        if (i < 0) return false;
        SetCurrent(candidates[i], showRing, reveal: true);
        return true;
    }
}
