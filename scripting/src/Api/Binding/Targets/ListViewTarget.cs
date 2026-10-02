using System;
using SEED.UI;

namespace SEED.Binding;

// ============================================================
//  ListViewTarget.cs — 一覧（SEED.UI.ListView）を行の並びの当てる先にする（Bind.List）
//
//  ListView 自身は変えず、既存の口を呼ぶ:
//    SetCount(n) … ListView.SetCount（行の数を変え、次の ListView.Update で見えている行を入れ直す）
//    Refresh()   … ListView.Refresh（次の ListView.Update で見えている行を入れ直す）
//    RebindRow(i)… ListView.RowOf(i) が付いていれば（見えていれば）その行へ bindRow をすぐ呼ぶ
//  ListView の作成時の行の入れ方（コンストラクタの bind）は Bind.RowBinder(items, bindRow) にして、同じ一覧・同じ処理で入れる。
//  ListView.Update は今までどおり持ち主のスクリプトの Update から毎フレーム呼ぶ。
// ============================================================

/// <summary>一覧（ListView）を行の並びの当てる先にする。</summary>
/// <typeparam name="T">項目の型。</typeparam>
internal sealed class ListViewTarget<T> : IListBindTarget
{
    /// <summary>一覧の部品。</summary>
    private readonly ListView _list;

    /// <summary>項目。</summary>
    private readonly ObservableList<T> _items;

    /// <summary>行へ項目を入れる（行・項目・位置）。</summary>
    private readonly Action<GameObject, T, int> _bindRow;

    /// <summary>スクロールの窓の見張り（破棄されたら結び付けを外す）。</summary>
    private readonly ActorLife _scroll;

    /// <summary>当てる先を作る。</summary>
    /// <param name="list">一覧の部品。</param>
    /// <param name="items">項目。</param>
    /// <param name="bindRow">行へ項目を入れる。</param>
    internal ListViewTarget(ListView list, ObservableList<T> items, Action<GameObject, T, int> bindRow)
    {
        _list = list;
        _items = items;
        _bindRow = bindRow;
        _scroll = new ActorLife(list.ScrollObject);
    }

    /// <inheritdoc />
    public bool IsAlive => _scroll.IsAlive;

    /// <inheritdoc />
    public void SetCount(int count) => _list.SetCount(count);

    /// <inheritdoc />
    public void Refresh() => _list.Refresh();

    /// <inheritdoc />
    public void RebindRow(int index)
    {
        // 範囲の外・見えていない行は何もしない（見えたときに ListView が入れる）
        if ((uint)index >= (uint)_items.Count) return;
        if (_list.RowOf(index) is { } row) _bindRow(row, _items[index], index);
    }
}
