using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ListViewRecycler.cs — 一覧の行の使い回し（作った行＝スロットを、見える行の番号へ割り当てる。W2-3）
//
//  エンジンの API に触れない純粋な計算（editor/tests/UiListViewTests から単体で試せる）。
//  スロットは「プレハブから作った行の入れ物」の番号で、行の番号（データの添字）へ 1 対 1 で割り当てる。
//
//  【割り当ての規則】（Assign。フレームごとに見える行の範囲を渡す）
//    1. 範囲の外の行に付いていたスロットを外す
//    2. 範囲の中でスロットの無い行へ、空いたスロットを割り当てる（いま外したものを先に使う＝表示の切り替えを減らす）
//    3. 空きが足りなければ、その行を Missing に積む（呼び出し側が行を作ってスロットを足し、次のフレームに割り当てる）
//    4. 空いたままのスロットのうち、前に行が付いていたものを Released に積む（呼び出し側が隠す）
//  範囲の中で既に付いている行のスロットは動かさない（行の中身を作り直さない）。
// ============================================================

/// <summary>スロットの割り当ての 1 件。</summary>
public readonly struct RowAssignment
{
    /// <summary>スロット（作った行の入れ物の番号）。</summary>
    public int Slot { get; }
    /// <summary>新しく付いた行（Released では −1）。</summary>
    public int Index { get; }
    /// <summary>前に付いていた行（空きだったら −1）。−1 以外なら、呼び出し側は前の行の押下を取り消す。</summary>
    public int PreviousIndex { get; }

    /// <summary>作る。</summary>
    public RowAssignment(int slot, int index, int previousIndex)
    {
        Slot = slot;
        Index = index;
        PreviousIndex = previousIndex;
    }

    /// <summary>表示用。</summary>
    public override string ToString() => $"slot{Slot}:{PreviousIndex}->{Index}";
}

/// <summary><see cref="ListViewRecycler.Assign"/> の結果。</summary>
public sealed class RecycleResult
{
    /// <summary>行が新しく付いたスロット（位置を合わせ、中身を入れ、見せる）。</summary>
    public List<RowAssignment> Bound { get; } = new();
    /// <summary>行が外れて空いたスロット（隠す）。</summary>
    public List<RowAssignment> Released { get; } = new();
    /// <summary>スロットが足りずに置けなかった行（呼び出し側が行を作る）。</summary>
    public List<int> Missing { get; } = new();
}

/// <summary>一覧の行の使い回し（スロット ⇔ 行の番号の割り当て）。</summary>
public sealed class ListViewRecycler
{
    /// <summary>スロット → 付いている行（空きは −1）。</summary>
    private readonly List<int> _indexOfSlot = new();
    /// <summary>行 → スロット。</summary>
    private readonly Dictionary<int, int> _slotOfIndex = new();

    /// <summary>スロットの数。</summary>
    public int SlotCount => _indexOfSlot.Count;

    /// <summary>行が付いているスロットの数。</summary>
    public int BoundCount => _slotOfIndex.Count;

    /// <summary>空きのスロットを 1 つ足して、その番号を返す。</summary>
    public int AddSlot()
    {
        _indexOfSlot.Add(-1);
        return _indexOfSlot.Count - 1;
    }

    /// <summary>スロットに付いている行（空きは −1）。</summary>
    public int IndexOfSlot(int slot) => slot >= 0 && slot < _indexOfSlot.Count ? _indexOfSlot[slot] : -1;

    /// <summary>行が付いているスロット（無ければ −1）。</summary>
    public int SlotOfIndex(int index) => _slotOfIndex.TryGetValue(index, out var slot) ? slot : -1;

    /// <summary>行が付いているスロットと行の組（行の番号の順）。</summary>
    public IEnumerable<(int Slot, int Index)> BoundSlots()
    {
        var pairs = new List<(int Slot, int Index)>(_slotOfIndex.Count);
        foreach (var (index, slot) in _slotOfIndex) pairs.Add((slot, index));
        pairs.Sort((a, b) => a.Index.CompareTo(b.Index));
        return pairs;
    }

    /// <summary>見える行の範囲へスロットを割り当て直す（冒頭の規則）。</summary>
    public RecycleResult Assign(ListRange range)
    {
        var result = new RecycleResult();
        // 1. 範囲の外の行を外す（外したスロットは先に使う）
        var freed = new List<(int Slot, int Previous)>();
        var alreadyFree = new List<int>();
        for (int slot = 0; slot < _indexOfSlot.Count; slot++)
        {
            int index = _indexOfSlot[slot];
            if (index < 0)
            {
                alreadyFree.Add(slot);
            }
            else if (!range.Contains(index))
            {
                _slotOfIndex.Remove(index);
                _indexOfSlot[slot] = -1;
                freed.Add((slot, index));
            }
        }
        var queue = new Queue<(int Slot, int Previous)>(freed);
        foreach (var slot in alreadyFree) queue.Enqueue((slot, -1));

        // 2〜3. 範囲の中でスロットの無い行へ割り当てる
        if (!range.IsEmpty)
        {
            for (int index = range.First; index <= range.Last; index++)
            {
                if (_slotOfIndex.ContainsKey(index)) continue;
                if (queue.Count == 0)
                {
                    result.Missing.Add(index);
                    continue;
                }
                var (slot, previous) = queue.Dequeue();
                _indexOfSlot[slot] = index;
                _slotOfIndex[index] = slot;
                result.Bound.Add(new RowAssignment(slot, index, previous));
            }
        }

        // 4. 空いたままのスロットのうち、前に行が付いていたもの
        foreach (var (slot, previous) in queue)
        {
            if (previous >= 0) result.Released.Add(new RowAssignment(slot, -1, previous));
        }
        return result;
    }

    /// <summary>すべての行を外す（行の数が変わった・一覧を作り直す）。外したスロットと前の行を返す。</summary>
    public List<RowAssignment> ReleaseAll()
    {
        var released = new List<RowAssignment>();
        for (int slot = 0; slot < _indexOfSlot.Count; slot++)
        {
            if (_indexOfSlot[slot] >= 0) released.Add(new RowAssignment(slot, -1, _indexOfSlot[slot]));
            _indexOfSlot[slot] = -1;
        }
        _slotOfIndex.Clear();
        return released;
    }
}
