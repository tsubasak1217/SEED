using System;
using System.Collections.Generic;

namespace SEED.Scripting;

// ============================================================
//  ScriptHierarchySearch.cs — 子孫・祖先のアクタからスクリプトを引く（GetScriptInChildren / GetScriptInParent の本体）
//
//  たどる順は ScriptTreeWalk（Model/。テストで固定）に任せ、ここはエンジンの木の読み方をつなぐだけ:
//    子の一覧 … ScriptHost.NodeChildren（GameObject.Children と同じ論理の子。フォルダは透過して中の子をその位置へ展開）
//    親       … ScriptHost.TryGetParent（GameObject.Parent と同じ直接の親。フォルダにはスクリプトが無いので素通りになる）
//    見る処理 … ScriptRegistry.FirstOn（GetScript と同じ。OnStart 前のスクリプトも引く）
//  木の読みはどれもフレームの始めの木（同じフレームの Create / Instantiate / SetParent はまだ反映されていない）。
// ============================================================

/// <summary>子孫・祖先のアクタからスクリプトを引く。</summary>
internal static class ScriptHierarchySearch
{
    /// <summary>論理の子の一覧（フォルダは透過）。</summary>
    private static readonly Func<Entity, IReadOnlyList<Entity>> ChildrenOf = ScriptHost.NodeChildren;

    /// <summary>直接の親（ルート直下なら false）。</summary>
    private static readonly ScriptTreeWalk.TryGetParent<Entity> ParentOf = ScriptHost.TryGetParent;

    /// <summary>
    /// 自分（<paramref name="includeSelf"/> のとき）→ 子孫を行きがけ順にたどり、T に当たる最初のスクリプトを返す。
    /// </summary>
    /// <typeparam name="T">探す型（派生型も当たる）。</typeparam>
    /// <param name="root">たどり始めるアクタ（無効なら null）。</param>
    /// <param name="includeSelf">自分のスクリプトも見るか。</param>
    internal static T? InChildren<T>(Entity root, bool includeSelf) where T : class
        => root.IsValid
            ? ScriptTreeWalk.FirstInSubtree<Entity, T>(root, includeSelf, ChildrenOf, ScriptRegistry.FirstOn<T>)
            : null;

    /// <summary>
    /// 自分（<paramref name="includeSelf"/> のとき）→ 親 → 親の親 … とたどり、T に当たる最初のスクリプトを返す。
    /// </summary>
    /// <typeparam name="T">探す型（派生型も当たる）。</typeparam>
    /// <param name="start">たどり始めるアクタ（無効なら null）。</param>
    /// <param name="includeSelf">自分のスクリプトも見るか。</param>
    internal static T? InParent<T>(Entity start, bool includeSelf) where T : class
        => start.IsValid
            ? ScriptTreeWalk.FirstInAncestors<Entity, T>(start, includeSelf, ParentOf, ScriptRegistry.FirstOn<T>)
            : null;
}
