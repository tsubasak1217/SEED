namespace SEED.Binding;

// ============================================================
//  ListBinding.cs — 一覧の結び付け（ObservableList → 行の並び）
//
//  Bind.List の本体。一覧の変化の種類を、行の並び（IListBindTarget）の口へ写す:
//    Insert・Remove・Reset … SetCount(今の数)（行の数を合わせ、見えている行を入れ直す）
//    Move                  … Refresh()（数はそのまま、見えている行を入れ直す）
//    Replace               … RebindRow(位置)（その行だけ。見えていなければ何もしない）
//  作った時点で SetCount(今の数) を 1 回呼ぶ。当てる先が無くなったら自分を外す。
// ============================================================

/// <summary>一覧の結び付け（ObservableList → 行の並び）。</summary>
/// <typeparam name="T">項目の型。</typeparam>
internal sealed class ListBinding<T> : BindingBase
{
    /// <summary>一覧。</summary>
    private readonly ObservableList<T> _items;

    /// <summary>行の並び。</summary>
    private readonly IListBindTarget _target;

    /// <summary>結び付けを作り、行の数を今の一覧に合わせる。</summary>
    /// <param name="items">一覧。</param>
    /// <param name="target">行の並び。</param>
    internal ListBinding(ObservableList<T> items, IListBindTarget target)
    {
        _items = items;
        _target = target;
        Own(items.Subscribe(OnChanged));
        if (_target.IsAlive) _target.SetCount(_items.Count);
        else Dispose();
    }

    /// <summary>一覧が変わった: 種類に合わせて行の並びの口を呼ぶ。</summary>
    /// <param name="change">変化。</param>
    private void OnChanged(ListChange<T> change)
    {
        if (IsDisposed) return;
        if (!_target.IsAlive)
        {
            Dispose();
            return;
        }
        switch (change.Kind)
        {
            case ListChangeKind.Replace:
                // 置き換えた行だけを入れ直す（中身は今の一覧のその位置から読む。購読の中の変更で後に続く変化があっても、
                // それも起きた順に届いて行の数・中身が揃う）
                _target.RebindRow(change.Index);
                break;
            case ListChangeKind.Move:
                // 数は変わらない: 見えている行を入れ直す
                _target.Refresh();
                break;
            default:
                // 入れた・外した・全体が変わった: 数を今の一覧に合わせる（見えている行も入れ直される）
                _target.SetCount(_items.Count);
                break;
        }
    }

    /// <inheritdoc />
    protected override bool OnFrame() => false;
}
