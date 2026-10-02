// ============================================================
//  ApiReferenceSelectionCache.cs — 節の選択結果の使い回し（最近使った順に少しだけ覚える）
//
//  【鍵】
//  ファイルの内容のハッシュ（SHA-256）＋カーソルの位置＋予算。
//  カーソルの位置も鍵に入れるのは、「カーソルの近くの語を重く数える」ため、同じ内容でも
//  カーソルが別の場所なら選ぶ節が変わりうるから（同じ内容・同じ位置・同じ予算なら必ず同じ結果）。
//  補完は打鍵ごとに走るが、手動の呼び出しを同じ場所で繰り返したときなどに選び直しを省ける。
//
//  【WPF 非依存】editor/tests/InlineCompletionTests がこのフォルダを丸ごとリンクする。
// ============================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

/// <summary>
/// 節の選択結果の LRU キャッシュ（複数のスレッドから呼んでよい）。
/// </summary>
public sealed class ApiReferenceSelectionCache
{
    /// <summary>覚える数の下限（0 以下を指定しても 1 つは覚える）。</summary>
    private const int MinCapacity = 1;

    /// <summary>キャッシュの鍵。</summary>
    /// <param name="ContentHash">ファイルの内容の SHA-256（16 進）。</param>
    /// <param name="CaretOffset">カーソルの位置。</param>
    /// <param name="BudgetChars">予算。</param>
    public readonly record struct Key(string ContentHash, int CaretOffset, int BudgetChars);

    /// <summary>覚える最大の数。</summary>
    public int Capacity { get; }

    /// <summary>使い回せた回数（テスト・診断用）。</summary>
    public int Hits { get; private set; }

    /// <summary>使い回せなかった回数（テスト・診断用）。</summary>
    public int Misses { get; private set; }

    /// <summary>今覚えている数。</summary>
    public int Count { get { lock (_gate) return _map.Count; } }

    /// <summary>最近使った順（先頭が最新）。</summary>
    private readonly LinkedList<(Key Key, ApiReferenceSelection Value)> _order = new();

    /// <summary>鍵 → 並びの節。</summary>
    private readonly Dictionary<Key, LinkedListNode<(Key Key, ApiReferenceSelection Value)>> _map = new();

    /// <summary>排他の錠。</summary>
    private readonly object _gate = new();

    /// <summary>キャッシュを作る。</summary>
    /// <param name="capacity">覚える最大の数。</param>
    public ApiReferenceSelectionCache(int capacity) => Capacity = Math.Max(MinCapacity, capacity);

    /// <summary>
    /// 鍵を作る（内容は SHA-256 にする。UTF-16 のまま読むので文字列の写しを作らない）。
    /// </summary>
    /// <param name="text">ファイルの全文。</param>
    /// <param name="caretOffset">カーソルの位置。</param>
    /// <param name="budgetChars">予算。</param>
    /// <returns>鍵。</returns>
    public static Key MakeKey(string text, int caretOffset, int budgetChars)
    {
        var bytes = MemoryMarshal.AsBytes((text ?? string.Empty).AsSpan());
        return new Key(Convert.ToHexString(SHA256.HashData(bytes)), caretOffset, budgetChars);
    }

    /// <summary>覚えていれば返す（最近使ったものとして先頭へ）。</summary>
    /// <param name="key">鍵。</param>
    /// <param name="selection">覚えていた選択。</param>
    /// <returns>覚えていれば true。</returns>
    public bool TryGet(Key key, out ApiReferenceSelection selection)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                Hits++;
                selection = node.Value.Value;
                return true;
            }
            Misses++;
            selection = ApiReferenceSelection.Empty;
            return false;
        }
    }

    /// <summary>覚える（あふれたら最も古いものを捨てる）。</summary>
    /// <param name="key">鍵。</param>
    /// <param name="selection">選択。</param>
    public void Add(Key key, ApiReferenceSelection selection)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }
            var node = _order.AddFirst((key, selection));
            _map[key] = node;
            while (_map.Count > Capacity && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }
        }
    }

    /// <summary>全部忘れる。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _order.Clear();
            _map.Clear();
        }
    }
}
