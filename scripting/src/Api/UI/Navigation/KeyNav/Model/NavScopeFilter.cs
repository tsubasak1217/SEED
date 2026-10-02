using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  NavScopeFilter.cs — 方向キーで移れる候補を、フォーカスの範囲（FocusScope）で絞る（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  いちばん前の範囲（UiFocus.TopScope）から見て、候補の範囲が次のどれかなら移れる:
//    1. いちばん前の範囲そのもの
//    2. いちばん前の範囲を中に含む範囲（候補の範囲の持ち主のノードが、いちばん前の範囲の持ち主の祖先）。
//       シェル（根の画面）の中のタブの画面がいちばん前でも、シェルの下のタブのバー・ヘッダーへ移れる
//    3. 根の範囲（どの範囲にも属さない部品）は、いちばん前の範囲が重ねる範囲（ダイアログ・シート・覆い）でないとき
//       （画面のスタックの外に置いた部品。ダイアログが開いている間は下の部品へ移らない）
//  いちばん前が根（範囲が 1 つも無い）なら根の部品だけ。範囲の鍵は object（偽の候補で検算できる）。
//  エンジンに触れない（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>移れる候補を範囲で絞る（純粋な計算）。</summary>
public static class NavScopeFilter
{
    /// <summary>
    /// 候補の範囲へ、いちばん前の範囲から移れるか。
    /// </summary>
    /// <param name="candidateScope">候補の範囲（null = 根の範囲）。</param>
    /// <param name="topScope">いちばん前の範囲（null = 根）。</param>
    /// <param name="candidateContainsTop">候補の範囲がいちばん前の範囲を中に含むか（持ち主のノードが祖先）。</param>
    /// <param name="topIsOverlay">いちばん前の範囲が重ねる範囲（ダイアログ・シート・覆い）か。</param>
    public static bool Allows(object? candidateScope, object? topScope, bool candidateContainsTop, bool topIsOverlay)
    {
        if (ReferenceEquals(candidateScope, topScope)) return true;
        if (candidateScope is null) return topScope is not null && !topIsOverlay;
        return topScope is not null && candidateContainsTop;
    }

    /// <summary>
    /// 候補を範囲で絞る（並びは保つ）。
    /// </summary>
    /// <typeparam name="T">候補の型。</typeparam>
    /// <param name="items">候補。</param>
    /// <param name="scopeOf">候補の範囲（null = 根）。</param>
    /// <param name="topScope">いちばん前の範囲（null = 根）。</param>
    /// <param name="contains">範囲 a が範囲 b を中に含むか（a・b とも null でない）。</param>
    /// <param name="topIsOverlay">いちばん前の範囲が重ねる範囲か。</param>
    public static List<T> Filter<T>(IEnumerable<T> items, Func<T, object?> scopeOf, object? topScope,
        Func<object, object, bool> contains, bool topIsOverlay)
    {
        var result = new List<T>();
        // 同じ範囲を何度も調べない（候補は同じ範囲にまとまっていることが多い）
        var known = new Dictionary<object, bool>(ReferenceEqualityComparer.Instance);
        foreach (var item in items)
        {
            var scope = scopeOf(item);
            bool allowed;
            if (scope is null || topScope is null || ReferenceEquals(scope, topScope))
            {
                allowed = Allows(scope, topScope, candidateContainsTop: false, topIsOverlay);
            }
            else if (!known.TryGetValue(scope, out allowed))
            {
                allowed = Allows(scope, topScope, contains(scope, topScope), topIsOverlay);
                known[scope] = allowed;
            }
            if (allowed) result.Add(item);
        }
        return result;
    }
}
