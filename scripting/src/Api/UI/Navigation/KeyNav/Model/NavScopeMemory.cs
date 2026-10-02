using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  NavScopeMemory.cs — 範囲ごとに最後にフォーカスした部品を覚える（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  ダイアログを開いて閉じたら、開く前に方向キーで選んでいた部品へ戻る（Flutter の FocusScope・UiFocus の覚えと同じ考え方）。
//  範囲の鍵は object（null = 根の範囲）。思い出すときに相手がまだ生きているかを問い、死んでいれば忘れる。
//  エンジンに触れない（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>範囲ごとに最後にフォーカスした相手を覚える。</summary>
/// <typeparam name="T">相手の型。</typeparam>
public sealed class NavScopeMemory<T> where T : class
{
    /// <summary>根の範囲の鍵（null の代わり）。</summary>
    private static readonly object RootKey = new();

    /// <summary>範囲 → 覚えている相手。</summary>
    private readonly Dictionary<object, T> _items = new(ReferenceEqualityComparer.Instance);

    /// <summary>覚えている数。</summary>
    public int Count => _items.Count;

    /// <summary>範囲の相手として覚える（同じ相手を別の範囲で覚えていたらそちらは忘れる）。</summary>
    /// <param name="scope">範囲（null = 根）。</param>
    /// <param name="item">相手。</param>
    public void Remember(object? scope, T item)
    {
        Forget(item);
        _items[scope ?? RootKey] = item;
    }

    /// <summary>範囲の覚えている相手（生きていなければ忘れて null）。</summary>
    /// <param name="scope">範囲（null = 根）。</param>
    /// <param name="alive">相手がまだ使えるか。</param>
    public T? Recall(object? scope, Func<T, bool> alive)
    {
        var key = scope ?? RootKey;
        if (!_items.TryGetValue(key, out var item)) return null;
        if (alive(item)) return item;
        _items.Remove(key);
        return null;
    }

    /// <summary>相手を忘れる（どの範囲の覚えからも）。</summary>
    /// <param name="item">相手。</param>
    public void Forget(T item)
    {
        List<object>? keys = null;
        foreach (var pair in _items)
            if (ReferenceEquals(pair.Value, item)) (keys ??= new List<object>()).Add(pair.Key);
        if (keys is null) return;
        foreach (var key in keys) _items.Remove(key);
    }

    /// <summary>生きていない相手をすべて忘れる（外れた範囲の覚えを溜めない）。</summary>
    /// <param name="alive">相手がまだ使えるか。</param>
    public void Prune(Func<T, bool> alive)
    {
        List<object>? keys = null;
        foreach (var pair in _items)
            if (!alive(pair.Value)) (keys ??= new List<object>()).Add(pair.Key);
        if (keys is null) return;
        foreach (var key in keys) _items.Remove(key);
    }

    /// <summary>すべて忘れる。</summary>
    public void Clear() => _items.Clear();
}
