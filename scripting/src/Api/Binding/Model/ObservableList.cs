using System;
using System.Collections;
using System.Collections.Generic;

namespace SEED.Binding;

// ============================================================
//  ObservableList.cs — 観測できる一覧（変化の種類と位置を知らせる）
//
//  【使い方】画面のスクリプトが一覧のデータをこれで持ち、ListView へは Bind.List で結ぶ（行の数・中身を書き直す時を書かない）。
//      private readonly ObservableList<Alarm> _alarms = new();
//      _list = new ListView(scroll, RowPrefab, 0, RowExtent, Bind.RowBinder(_alarms, BindRow));
//      Bind.List(this, _list, _alarms, BindRow);
//      _alarms.Add(alarm);   // → 行の数が増え、見えている行が入れ直される
//  【約束】
//    - 変化は即時に 1 件ずつ、起きた順に知らせる（ListChange。Insert・Remove・Replace・Move・Reset）。
//    - this[i] の set は同じ項目でも知らせる（Replace。参照型の中身を書き換えた行の描き直しの合図に使える）。
//    - Clear は空なら知らせない。Move は同じ位置なら知らせない。
//    - 購読の中で一覧を変える再入は、今の変化を配り終えてから順に配る（BindingLimits.MaxReentrantDepth 段まで。ListNotifier）。
//    - 範囲の外の位置は List<T> と同じく ArgumentOutOfRangeException。
//    - 列挙の途中で変えると List<T> と同じく InvalidOperationException。
//  正典は docs/ui_binding.md §3.3。
// ============================================================

/// <summary>観測できる一覧（変化の種類と位置を即座に知らせる）。</summary>
/// <typeparam name="T">項目の型。</typeparam>
public sealed class ObservableList<T> : IReadOnlyList<T>
{
    /// <summary>項目。</summary>
    private readonly List<T> _items;

    /// <summary>知らせ方。</summary>
    private readonly ListNotifier<T> _notifier = new();

    /// <summary>空の一覧を作る。</summary>
    public ObservableList()
    {
        _items = new List<T>();
    }

    /// <summary>項目を写して一覧を作る（知らせない）。</summary>
    /// <param name="items">最初の項目。</param>
    public ObservableList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = new List<T>(items);
    }

    /// <summary>項目の数。</summary>
    public int Count => _items.Count;

    /// <summary>今の購読の数（結び付けを含む。診断・テスト用）。</summary>
    public int SubscriberCount => _notifier.SubscriberCount;

    /// <summary>位置 index の項目。set は置き換えて Replace を知らせる（同じ項目でも知らせる）。</summary>
    /// <param name="index">位置（0 から Count − 1）。</param>
    public T this[int index]
    {
        get => _items[index];
        set
        {
            // 範囲の外は List<T> と同じ例外（知らせない）
            T old = _items[index];
            _items[index] = value;
            _notifier.Publish(ListChange<T>.Replace(index, old, value));
        }
    }

    /// <summary>末尾へ足す（Insert を知らせる）。</summary>
    /// <param name="item">足す項目。</param>
    public void Add(T item) => Insert(_items.Count, item);

    /// <summary>位置 index へ入れる（Insert を知らせる）。</summary>
    /// <param name="index">入れる位置（0 から Count。Count なら末尾）。</param>
    /// <param name="item">入れる項目。</param>
    public void Insert(int index, T item)
    {
        _items.Insert(index, item);
        _notifier.Publish(ListChange<T>.Insert(index, item));
    }

    /// <summary>位置 index の項目を外す（Remove を知らせる）。</summary>
    /// <param name="index">外す位置。</param>
    public void RemoveAt(int index)
    {
        T old = _items[index];
        _items.RemoveAt(index);
        _notifier.Publish(ListChange<T>.Remove(index, old));
    }

    /// <summary>最初に見つかった項目を外す（Remove を知らせる）。</summary>
    /// <param name="item">外す項目。</param>
    /// <returns>外したら true（見つからなければ false で知らせない）。</returns>
    public bool Remove(T item)
    {
        int index = _items.IndexOf(item);
        if (index < 0) return false;
        RemoveAt(index);
        return true;
    }

    /// <summary>全部を外す（Reset を知らせる。もともと空なら何もしない）。</summary>
    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        _notifier.Publish(ListChange<T>.Reset());
    }

    /// <summary>全部を入れ替える（Reset を 1 回だけ知らせる。読み込んだデータで一覧を作り直すとき）。</summary>
    /// <param name="items">新しい項目。</param>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        // 先に写す（items が自分自身の列挙でも壊れないように）
        var next = new List<T>(items);
        _items.Clear();
        _items.AddRange(next);
        _notifier.Publish(ListChange<T>.Reset());
    }

    /// <summary>項目を動かす（Move を知らせる。同じ位置なら何もしない）。</summary>
    /// <param name="oldIndex">元の位置。</param>
    /// <param name="newIndex">移す先（動かした後の一覧での位置）。</param>
    public void Move(int oldIndex, int newIndex)
    {
        // 範囲の外は List<T> と同じ例外（先に確かめて、半端に動かさない）
        if ((uint)oldIndex >= (uint)_items.Count) throw new ArgumentOutOfRangeException(nameof(oldIndex));
        if ((uint)newIndex >= (uint)_items.Count) throw new ArgumentOutOfRangeException(nameof(newIndex));
        if (oldIndex == newIndex) return;
        T item = _items[oldIndex];
        _items.RemoveAt(oldIndex);
        _items.Insert(newIndex, item);
        _notifier.Publish(ListChange<T>.Move(oldIndex, newIndex, item));
    }

    /// <summary>項目の位置（無ければ −1）。</summary>
    /// <param name="item">探す項目。</param>
    /// <returns>位置。</returns>
    public int IndexOf(T item) => _items.IndexOf(item);

    /// <summary>項目を含むか。</summary>
    /// <param name="item">探す項目。</param>
    /// <returns>含めば true。</returns>
    public bool Contains(T item) => _items.Contains(item);

    /// <summary>
    /// 変化を購読する（変化を起きた順に 1 件ずつ受け取る。購読した時点の中身では呼ばない）。戻り値を Dispose すると解除する。
    /// スクリプトの寿命に合わせるなら <c>Subscribe(owner: this, handler)</c>。
    /// </summary>
    /// <param name="handler">変化を受け取る処理。</param>
    /// <returns>解除の口（null は返さない）。</returns>
    public IDisposable Subscribe(Action<ListChange<T>> handler) => _notifier.Subscribe(handler);

    /// <summary>列挙する（途中で変えると InvalidOperationException。List&lt;T&gt; と同じ）。</summary>
    /// <returns>列挙子。</returns>
    public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc />
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}
