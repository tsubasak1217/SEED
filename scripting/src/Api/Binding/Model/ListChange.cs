namespace SEED.Binding;

// ============================================================
//  ListChange.cs — 一覧（ObservableList）の変化 1 件（種類と位置と項目）
//
//  購読は変化を起きた順に 1 件ずつ受け取る（ObservableList の約束）。手元に写しを持つ購読は、この順に当てれば一覧と揃う。
//  Reset（Clear・ReplaceAll）は位置を持たないので、受けたら今の一覧を読み直す。
// ============================================================

/// <summary>一覧の変化 1 件。</summary>
/// <typeparam name="T">項目の型。</typeparam>
public readonly struct ListChange<T>
{
    /// <summary>位置を持たない変化（Reset）の位置。</summary>
    public const int NoIndex = -1;

    /// <summary>変化の種類。</summary>
    public ListChangeKind Kind { get; }

    /// <summary>位置（Insert・Remove・Replace はその位置、Move は移した先、Reset は <see cref="NoIndex"/>）。</summary>
    public int Index { get; }

    /// <summary>元の位置（Move だけ意味がある。ほかは <see cref="Index"/> と同じ）。</summary>
    public int OldIndex { get; }

    /// <summary>新しい項目（Insert・Replace・Move。ほかは既定値）。</summary>
    public T Item { get; }

    /// <summary>前の項目（Remove・Replace。ほかは既定値）。</summary>
    public T OldItem { get; }

    /// <summary>変化を作る（下の作り方から）。</summary>
    private ListChange(ListChangeKind kind, int index, int oldIndex, T item, T oldItem)
    {
        Kind = kind;
        Index = index;
        OldIndex = oldIndex;
        Item = item;
        OldItem = oldItem;
    }

    /// <summary>入れた。</summary>
    /// <param name="index">入れた位置。</param>
    /// <param name="item">入れた項目。</param>
    /// <returns>変化。</returns>
    public static ListChange<T> Insert(int index, T item) => new(ListChangeKind.Insert, index, index, item, default!);

    /// <summary>外した。</summary>
    /// <param name="index">外した位置。</param>
    /// <param name="oldItem">外した項目。</param>
    /// <returns>変化。</returns>
    public static ListChange<T> Remove(int index, T oldItem) => new(ListChangeKind.Remove, index, index, default!, oldItem);

    /// <summary>置き換えた。</summary>
    /// <param name="index">位置。</param>
    /// <param name="oldItem">前の項目。</param>
    /// <param name="item">新しい項目。</param>
    /// <returns>変化。</returns>
    public static ListChange<T> Replace(int index, T oldItem, T item) => new(ListChangeKind.Replace, index, index, item, oldItem);

    /// <summary>動かした。</summary>
    /// <param name="oldIndex">元の位置。</param>
    /// <param name="index">移した先。</param>
    /// <param name="item">動かした項目。</param>
    /// <returns>変化。</returns>
    public static ListChange<T> Move(int oldIndex, int index, T item) => new(ListChangeKind.Move, index, oldIndex, item, default!);

    /// <summary>全体が変わった。</summary>
    /// <returns>変化。</returns>
    public static ListChange<T> Reset() => new(ListChangeKind.Reset, NoIndex, NoIndex, default!, default!);

    /// <summary>ログ用の文字列（例 "Insert@2"・"Move 3→0"）。</summary>
    public override string ToString() => Kind switch
    {
        ListChangeKind.Move => $"Move {OldIndex}→{Index}",
        ListChangeKind.Reset => "Reset",
        _ => $"{Kind}@{Index}",
    };
}
