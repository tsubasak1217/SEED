using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ListView.cs — 行の多い一覧（見えている行だけをプレハブから作って使い回す。W2-3。docs/ui_scroll_list.md §6）
//
//  【分担】スクロール（位置・慣性・跳ね返り・窓の外を飛ばす）はエンジン（CanvasScroll）。この部品は
//    - スクロールの中身の長さを行の数と長さから決める（CanvasScroll の Fixed の中身の大きさ）
//    - スクロールの位置と窓の長さから見える行の範囲を求め（ListViewLayout）、作った行を割り当て直す（ListViewRecycler）
//    - 割り当てた行をその行の位置へ置き、Bind(行, 番号) でデータを入れて見せる。外れた行は隠す
//    - 行を別の番号へ使い回す前に、その行の押下・ドラッグを取り消す（GameObject.CancelGestures。押している行が別のデータに
//      変わっても元の押下の Tap が届かない＝W2-2 の持ち越し「押している行が消えると PressCancel の届け先が無い」への手当て）
//  見た目（行の形）はプレハブ、データはスクリプト（Bind）。
//
//  【使い方】（持ち主のスクリプトの Update から毎フレーム Update を呼ぶ）
//      _list = new ListView(scrollObject, "assets://ui/alarm_row.actor", count, rowExtent: 72f, (row, i) => { ... });
//      public override void Update() => _list.Update();
//  行のプレハブは anchor (0,0)・スクロールの軸の pivot は 0 を推奨（横いっぱいにするなら CanvasLayoutItem の fill_width）。
//  行はスクロールの窓（または Content）の子として作る。作った行は次のフレームから使える（プレハブの構築はフレーム末尾）。
// ============================================================

/// <summary>一覧のスクロールの軸。</summary>
public enum ListAxis
{
    /// <summary>縦（上から下へ並ぶ）。</summary>
    Vertical = 0,
    /// <summary>横（左から右へ並ぶ）。</summary>
    Horizontal = 1,
}

/// <summary>
/// 行の多い一覧。見えている行（と前後の余白の分）だけをプレハブから作って使い回す。
/// </summary>
public sealed class ListView
{
    /// <summary>作った行（スロット 1 つ）。</summary>
    private sealed class RowSlot
    {
        /// <summary>行のアクター。</summary>
        public GameObject Row;
        /// <summary>見せているか（Visible の最後に書いた値）。</summary>
        public bool Shown;

        public RowSlot(GameObject row) { Row = row; }
    }

    /// <summary>スクロールの窓（CanvasScroll を持つノード）。</summary>
    public GameObject ScrollObject { get; }
    /// <summary>行を置く親（既定はスクロールの窓）。</summary>
    public GameObject Content { get; }
    /// <summary>行のプレハブ（assets:// の .actor）。</summary>
    public string RowPrefab { get; }
    /// <summary>行へデータを入れる（行・行の番号）。</summary>
    public Action<GameObject, int> Bind { get; }
    /// <summary>行が番号から外れる前（使い回す・隠す）に呼ばれる（行・前の番号）。押下の見た目の後始末などに使う。</summary>
    public Action<GameObject, int>? Recycled { get; set; }

    /// <summary>行の数。</summary>
    public int Count => _count;
    /// <summary>行の長さ（固定のとき）。</summary>
    public float RowExtent => _rowExtent;
    /// <summary>行ごとの長さ（null なら固定）。</summary>
    public Func<int, float>? ExtentOf => _extentOf;
    /// <summary>行と行の間隔。</summary>
    public float Spacing { get => _spacing; set { _spacing = value; _layoutDirty = true; } }
    /// <summary>先頭の余白。</summary>
    public float LeadingPadding { get => _leading; set { _leading = value; _layoutDirty = true; } }
    /// <summary>末尾の余白。</summary>
    public float TrailingPadding { get => _trailing; set { _trailing = value; _layoutDirty = true; } }
    /// <summary>窓の外に前もって置いておく長さ（既定はスクロールの CacheExtent）。</summary>
    public float? CacheExtent { get; set; }

    /// <summary>見えている行の範囲（最後の Update の結果）。</summary>
    public ListRange VisibleRange { get; private set; } = ListRange.Empty;
    /// <summary>作った行の数（使い回しの入れ物の数）。</summary>
    public int CreatedRowCount => _slots.Count + _pending.Count;

    /// <summary>行の並びの計算（行の数・長さが変わったら作り直す）。</summary>
    public ListViewLayout Layout => EnsureLayout();

    private int _count;
    private float _rowExtent;
    private Func<int, float>? _extentOf;
    private float _spacing;
    private float _leading;
    private float _trailing;
    private ListViewLayout? _layout;
    private bool _layoutDirty = true;
    /// <summary>スクロールの中身の長さを書き直す必要があるか。</summary>
    private bool _contentDirty = true;
    /// <summary>付いている行の中身を入れ直す必要があるか（Refresh）。</summary>
    private bool _rebindAll;
    private readonly ListViewRecycler _recycler = new();
    private readonly List<RowSlot> _slots = new();
    /// <summary>作ったがまだ構築されていない行（プレハブの構築は作ったフレームの末尾）。</summary>
    private readonly List<GameObject> _pending = new();

    /// <summary>作る（行の長さが全部同じ一覧）。</summary>
    /// <param name="scrollObject">スクロールの窓（CanvasScroll を持つノード）。</param>
    /// <param name="rowPrefab">行のプレハブ（assets:// の .actor）。</param>
    /// <param name="count">行の数。</param>
    /// <param name="rowExtent">行の長さ（スクロールの軸に沿って。キャンバスの単位）。</param>
    /// <param name="bind">行へデータを入れる（行・行の番号）。</param>
    /// <param name="content">行を置く親（省略するとスクロールの窓）。</param>
    public ListView(GameObject scrollObject, string rowPrefab, int count, float rowExtent, Action<GameObject, int> bind, GameObject? content = null)
    {
        ScrollObject = scrollObject;
        Content = content ?? scrollObject;
        RowPrefab = rowPrefab;
        Bind = bind;
        _count = Math.Max(0, count);
        _rowExtent = rowExtent;
    }

    /// <summary>スクロールの軸（窓の CanvasScroll の向き。横なら Horizontal、それ以外は Vertical）。</summary>
    public ListAxis Axis
        => ScrollObject.GetComponent<CanvasScroll>() is { } s && s.Direction == ScrollDirection.Horizontal
            ? ListAxis.Horizontal
            : ListAxis.Vertical;

    /// <summary>行の数を変える（行の中身は次の Update で入れ直す）。</summary>
    public void SetCount(int count)
    {
        _count = Math.Max(0, count);
        _layoutDirty = true;
        _rebindAll = true;
    }

    /// <summary>行ごとの長さにする（null で固定の長さへ戻す）。</summary>
    public void SetExtentOf(Func<int, float>? extentOf)
    {
        _extentOf = extentOf;
        _layoutDirty = true;
    }

    /// <summary>行の長さ（固定）を変える。</summary>
    public void SetRowExtent(float rowExtent)
    {
        _rowExtent = rowExtent;
        _layoutDirty = true;
    }

    /// <summary>データが変わった: 付いている行の中身を次の Update で入れ直す。</summary>
    public void Refresh() => _rebindAll = true;

    /// <summary>行の番号の行（付いていなければ null＝見えていない）。</summary>
    public GameObject? RowOf(int index)
    {
        int slot = _recycler.SlotOfIndex(index);
        return slot >= 0 ? _slots[slot].Row : null;
    }

    /// <summary>行が付いている番号（付いていなければ −1）。</summary>
    public int IndexOf(GameObject row)
    {
        for (int slot = 0; slot < _slots.Count; slot++)
        {
            var e = _slots[slot].Row.Entity;
            if (e.Index == row.Entity.Index && e.Generation == row.Entity.Generation) return _recycler.IndexOfSlot(slot);
        }
        return -1;
    }

    /// <summary>行が見える位置へスクロールする（揃えは 0 = 先頭・0.5 = 中央・1 = 終わり。時間 0 はすぐ移す）。</summary>
    public bool ScrollToIndex(int index, float duration = CanvasScroll.DefaultScrollToDuration, float alignment = 0f)
    {
        if (ScrollObject.GetComponent<CanvasScroll>() is not { } scroll || !scroll.HasMetrics) return false;
        int axis = (int)Axis;
        var viewport = scroll.ViewportSize;
        float target = EnsureLayout().ScrollPositionFor(index, axis == 0 ? viewport.y : viewport.x, alignment);
        var current = scroll.Position;
        var position = axis == 0 ? new Vector2(current.x, target) : new Vector2(target, current.y);
        return duration > 0f ? scroll.ScrollTo(position, duration) : scroll.JumpTo(position);
    }

    /// <summary>行の並びを（変わっていれば）作り直す。</summary>
    private ListViewLayout EnsureLayout()
    {
        if (_layout is null || _layoutDirty)
        {
            _layout = _extentOf is null
                ? ListViewLayout.Fixed(_count, _rowExtent, _spacing, _leading, _trailing)
                : ListViewLayout.Variable(_count, _extentOf, _spacing, _leading, _trailing);
            _layoutDirty = false;
            _contentDirty = true;
        }
        return _layout;
    }

    /// <summary>
    /// 毎フレーム呼ぶ（持ち主のスクリプトの Update から）。スクロールの位置から見える行を求め、行を割り当て直して置く。
    /// 変化が無ければ何もしない（行の中身を作り直さない）。
    /// </summary>
    public void Update()
    {
        if (ScrollObject.GetComponent<CanvasScroll>() is not { } scroll) return;
        var layout = EnsureLayout();
        int axis = (int)Axis;
        // スクロールの中身の長さ = 行の並びの全体（交差の軸は 0 = スクロールしない）
        if (_contentDirty)
        {
            scroll.ContentSizeMode = ScrollContentSize.Fixed;
            scroll.FixedContentSize = axis == 0 ? new Vector2(0f, layout.TotalExtent) : new Vector2(layout.TotalExtent, 0f);
            _contentDirty = false;
        }
        AdoptConstructedRows();
        // 窓の大きさは最初の描画の後に分かる（それまでは行を置かない）
        if (!scroll.HasMetrics) return;
        var position = scroll.Position;
        var viewport = scroll.ViewportSize;
        float cache = CacheExtent ?? scroll.CacheExtent;
        var range = layout.VisibleRange(axis == 0 ? position.y : position.x, axis == 0 ? viewport.y : viewport.x, cache);
        VisibleRange = range;

        var result = _recycler.Assign(range);
        // 外れた行: 押下を取り消し、後始末を呼び、隠す
        foreach (var released in result.Released)
        {
            var slot = _slots[released.Slot];
            slot.Row.CancelGestures();
            Recycled?.Invoke(slot.Row, released.PreviousIndex);
            if (slot.Shown)
            {
                slot.Row.Visible = false;
                slot.Shown = false;
            }
        }
        // 付いた行: 前の行があれば押下を取り消し・後始末、位置を合わせ、中身を入れ、見せる
        foreach (var bound in result.Bound)
        {
            var slot = _slots[bound.Slot];
            if (bound.PreviousIndex >= 0)
            {
                slot.Row.CancelGestures();
                Recycled?.Invoke(slot.Row, bound.PreviousIndex);
            }
            Place(slot, bound.Index, layout, axis);
            Bind(slot.Row, bound.Index);
            if (!slot.Shown)
            {
                slot.Row.Visible = true;
                slot.Shown = true;
            }
        }
        // データ・並びが変わった: 付いたままの行も置き直して入れ直す
        if (_rebindAll)
        {
            foreach (var (slotIndex, index) in _recycler.BoundSlots())
            {
                bool justBound = result.Bound.Exists(b => b.Slot == slotIndex);
                if (justBound) continue;
                Place(_slots[slotIndex], index, layout, axis);
                Bind(_slots[slotIndex].Row, index);
            }
            _rebindAll = false;
        }
        // 足りない行を作る（作った行は次のフレームから使える。作っている途中の数だけ差し引く）
        int toCreate = result.Missing.Count - _pending.Count;
        for (int i = 0; i < toCreate; i++)
        {
            var row = GameObject.Instantiate(RowPrefab, Content);
            if (!row.IsValid) break;
            // 構築されるまで（と割り当てられるまで）は隠しておく（プレハブの位置に一瞬出ないように）
            row.Visible = false;
            _pending.Add(row);
        }
    }

    /// <summary>構築の済んだ行（CanvasTransform が引ける）を使い回しの入れ物に加える。</summary>
    private void AdoptConstructedRows()
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var row = _pending[i];
            if (!row.IsValid)
            {
                _pending.RemoveAt(i);
                continue;
            }
            if (row.GetComponent<CanvasTransform>() is null) continue;
            _pending.RemoveAt(i);
            _recycler.AddSlot();
            _slots.Add(new RowSlot(row));
        }
    }

    /// <summary>行をその番号の位置へ置く（スクロールの軸だけ書く。交差の軸はプレハブの値のまま）。</summary>
    private static void Place(RowSlot slot, int index, ListViewLayout layout, int axis)
    {
        if (slot.Row.GetComponent<CanvasTransform>() is not { } ct) return;
        var pos = ct.Position;
        var pivot = ct.Pivot;
        float extent = layout.ExtentOf(index);
        float main = layout.OffsetOf(index) + (axis == 0 ? pivot.y : pivot.x) * extent;
        ct.Position = axis == 0 ? new Vector2(pos.x, main) : new Vector2(main, pos.y);
    }
}
