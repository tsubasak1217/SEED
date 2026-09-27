using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  SelectionModel.cs — 選択（1 つ選ぶ／複数選ぶ・選べない項目）の状態（W2-4。純粋な計算）
//
//  | 選び方          | 項目を押すと                                   | 使う部品                         |
//  |-----------------|------------------------------------------------|----------------------------------|
//  | Single          | その項目だけを選ぶ（選んだ項目を押しても同じ） | セグメント・ラジオ・1 つ選ぶチップ |
//  | SingleOptional  | その項目だけを選ぶ／選んだ項目を押すと外す     | 外せる 1 つ選ぶチップ            |
//  | Multiple        | その項目の選ぶ・外すを切り替える               | 曜日などの複数選ぶチップ          |
//  選べない項目（Disabled）は押しても変わらない（プログラムからの選択も効かない）。
// ============================================================

/// <summary>選び方。</summary>
public enum SelectionMode
{
    /// <summary>1 つ選ぶ（必ず 1 つ）。</summary>
    Single = 0,
    /// <summary>1 つ選ぶ（外せる）。</summary>
    SingleOptional = 1,
    /// <summary>複数選ぶ。</summary>
    Multiple = 2,
}

/// <summary>選択の状態。</summary>
public sealed class SelectionModel
{
    /// <summary>選んでいるか（項目ごと）。</summary>
    private readonly List<bool> _selected = new();
    /// <summary>選べないか（項目ごと）。</summary>
    private readonly List<bool> _disabled = new();

    /// <summary>選び方。</summary>
    public SelectionMode Mode { get; set; }

    /// <summary>項目の数。</summary>
    public int Count => _selected.Count;

    public SelectionModel(SelectionMode mode = SelectionMode.Single)
    {
        Mode = mode;
    }

    /// <summary>項目の数を変える（増えた項目は選んでいない・選べる）。</summary>
    public void Resize(int count)
    {
        count = Math.Max(0, count);
        while (_selected.Count < count) { _selected.Add(false); _disabled.Add(false); }
        if (_selected.Count > count)
        {
            _selected.RemoveRange(count, _selected.Count - count);
            _disabled.RemoveRange(count, _disabled.Count - count);
        }
    }

    /// <summary>項目 i を選んでいるか（範囲の外は false）。</summary>
    public bool IsSelected(int i) => i >= 0 && i < _selected.Count && _selected[i];

    /// <summary>項目 i が選べないか（範囲の外は true）。</summary>
    public bool IsDisabled(int i) => i < 0 || i >= _disabled.Count || _disabled[i];

    /// <summary>項目 i を選べなくする・戻す（選べなくしても選んだ状態は残す）。</summary>
    public void SetDisabled(int i, bool disabled)
    {
        if (i >= 0 && i < _disabled.Count) _disabled[i] = disabled;
    }

    /// <summary>最初に選んでいる項目（無ければ -1）。</summary>
    public int SelectedIndex => _selected.IndexOf(true);

    /// <summary>選んでいる項目の番号（昇順）。</summary>
    public IReadOnlyList<int> SelectedIndices
    {
        get
        {
            var list = new List<int>();
            for (int i = 0; i < _selected.Count; i++) if (_selected[i]) list.Add(i);
            return list;
        }
    }

    /// <summary>
    /// 項目 i を押した（選び方の規則で選ぶ・外す）。
    /// </summary>
    /// <returns>選択が変わったら true。</returns>
    public bool Tap(int i)
    {
        if (IsDisabled(i)) return false;
        switch (Mode)
        {
            case SelectionMode.Multiple:
                _selected[i] = !_selected[i];
                return true;
            case SelectionMode.SingleOptional when _selected[i]:
                _selected[i] = false;
                return true;
            default:
                if (_selected[i] && SelectedIndices.Count == 1) return false;
                SelectOnly(i);
                return true;
        }
    }

    /// <summary>
    /// プログラムから項目 i を選ぶ・外す（1 つ選ぶ選び方で選ぶと他は外れる。選べない項目は変えない）。
    /// </summary>
    /// <returns>選択が変わったら true。</returns>
    public bool Set(int i, bool selected)
    {
        if (IsDisabled(i)) return false;
        if (!selected)
        {
            // 外す（1 つ選ぶ選び方でもプログラムからは外せる＝何も選んでいない状態）
            if (!_selected[i]) return false;
            _selected[i] = false;
            return true;
        }
        if (Mode == SelectionMode.Multiple)
        {
            if (_selected[i]) return false;
            _selected[i] = true;
            return true;
        }
        // 1 つ選ぶ: すでにそれだけを選んでいれば変わらない
        if (_selected[i] && SelectedIndices.Count == 1) return false;
        SelectOnly(i);
        return true;
    }

    /// <summary>項目 i だけを選ぶ。</summary>
    private void SelectOnly(int i)
    {
        for (int k = 0; k < _selected.Count; k++) _selected[k] = k == i;
    }
}
