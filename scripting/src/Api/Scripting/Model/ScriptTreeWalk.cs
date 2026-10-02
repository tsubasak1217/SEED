using System;
using System.Collections.Generic;

namespace SEED.Scripting;

// ============================================================
//  ScriptTreeWalk.cs — 木を決まった順にたどって最初に見つかったものを返す（GetScriptInChildren / InParent の順の正典）
//
//  たどる順:
//    部分木（GetScriptInChildren）… 行きがけ順の深さ優先: 自分 → 子 0 の部分木 → 子 1 の部分木 …
//                                   （Unity の GetComponentInChildren と同じ。子の並びは渡された一覧の順＝ GameObject.Children の順）
//    祖先（GetScriptInParent）     … 近い順: 自分 → 親 → 親の親 … → 根で止まる
//  木の読み方（子の一覧・親）と見る処理は委譲で受け取る（エンジンでは ScriptHost の NodeChildren / TryGetParent と
//  スクリプトの登録簿。テストでは偽の木）。再帰ではなく明示のスタックでたどる（深い木でも呼び出しの深さが増えない）。
// ============================================================

/// <summary>木を決まった順にたどって最初に見つかったものを返す補助。</summary>
internal static class ScriptTreeWalk
{
    /// <summary>親を引く（無ければ false）。</summary>
    /// <typeparam name="TNode">節の型。</typeparam>
    /// <param name="node">節。</param>
    /// <param name="parent">親（無ければ未定義の値）。</param>
    /// <returns>親が居れば true。</returns>
    internal delegate bool TryGetParent<TNode>(TNode node, out TNode parent);

    /// <summary>
    /// 部分木を行きがけ順（自分 → 子 0 の部分木 → 子 1 の部分木 …）にたどり、<paramref name="visit"/> が
    /// 最初に null 以外を返した値を返す。
    /// </summary>
    /// <typeparam name="TNode">節の型。</typeparam>
    /// <typeparam name="TResult">見つけるものの型。</typeparam>
    /// <param name="root">部分木の根。</param>
    /// <param name="includeRoot">根そのものも見るか（false なら子孫だけ）。</param>
    /// <param name="childrenOf">節の子の一覧（並びの順にたどる）。</param>
    /// <param name="visit">節を見る（見つからなければ null）。</param>
    /// <returns>最初に見つかったもの。無ければ null。</returns>
    internal static TResult? FirstInSubtree<TNode, TResult>(
        TNode root,
        bool includeRoot,
        Func<TNode, IReadOnlyList<TNode>> childrenOf,
        Func<TNode, TResult?> visit)
        where TResult : class
    {
        // まだ見ていない節（取り出す順が行きがけ順になるよう、子は逆順に積む）
        var pending = new Stack<TNode>();
        if (includeRoot) pending.Push(root);
        else PushChildren(pending, childrenOf(root));

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (visit(node) is { } found) return found;
            PushChildren(pending, childrenOf(node));
        }
        return null;
    }

    /// <summary>
    /// 祖先を近い順（自分 → 親 → 親の親 …）にたどり、<paramref name="visit"/> が最初に null 以外を返した値を返す。
    /// 根（親が無い節）を見たら止まる。
    /// </summary>
    /// <typeparam name="TNode">節の型。</typeparam>
    /// <typeparam name="TResult">見つけるものの型。</typeparam>
    /// <param name="start">たどり始める節。</param>
    /// <param name="includeStart">始めの節そのものも見るか（false なら祖先だけ）。</param>
    /// <param name="tryGetParent">親を引く。</param>
    /// <param name="visit">節を見る（見つからなければ null）。</param>
    /// <returns>最初に見つかったもの。無ければ null。</returns>
    internal static TResult? FirstInAncestors<TNode, TResult>(
        TNode start,
        bool includeStart,
        TryGetParent<TNode> tryGetParent,
        Func<TNode, TResult?> visit)
        where TResult : class
    {
        var node = start;
        // 自分を見ないときは親から始める（親が無ければ見るものが無い）
        if (!includeStart && !tryGetParent(node, out node)) return null;

        while (true)
        {
            if (visit(node) is { } found) return found;
            if (!tryGetParent(node, out node)) return null;
        }
    }

    /// <summary>子を逆順に積む（スタックから子 0 が先に取り出されるように）。</summary>
    private static void PushChildren<TNode>(Stack<TNode> pending, IReadOnlyList<TNode> children)
    {
        for (int i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
    }
}
