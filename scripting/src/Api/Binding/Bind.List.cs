using System;
using SEED.UI;
using SEEDEditor.Scripting;

namespace SEED.Binding;

// ============================================================
//  Bind.List.cs — 観測できる一覧（ObservableList）と SEED.UI.ListView の結び付け（実行中だけの部分）
//
//      _list = new ListView(scroll, RowPrefab, 0, RowExtent, Bind.RowBinder(_alarms, BindRow));   // 行の入れ方は一覧から
//      Bind.List(this, _list, _alarms, BindRow);                                                   // 数・中身の変化を写す
//      public override void Update(ref NativeFrameContext ctx) => _list.Update();                  // 今までどおり毎フレーム
//      private void BindRow(GameObject row, Alarm alarm, int index) { … }
//  ListView 自身は変えない。ListView は作るときに行の入れ方（bind）を受け取って変えられないので、作るときの bind は
//  Bind.RowBinder(一覧, bindRow) にする（同じ一覧・同じ bindRow を Bind.List にも渡す）。
//  変化の写し方: 入れた・外した・全体 → ListView.SetCount（次の Update で見えている行を入れ直す）、動かした → Refresh、
//  置き換えた → 見えていればその行へ bindRow をすぐ呼ぶ。
// ============================================================

public static partial class Bind
{
    /// <summary>
    /// 観測できる一覧を ListView へ結ぶ（数・中身の変化を ListView の SetCount・Refresh と行の入れ直しへ写す）。
    /// ListView を作るときの bind は <see cref="RowBinder{T}"/>（同じ一覧・同じ bindRow）にする。
    /// </summary>
    /// <typeparam name="T">項目の型。</typeparam>
    /// <param name="list">一覧の部品。</param>
    /// <param name="items">観測できる一覧。</param>
    /// <param name="bindRow">行へ項目を入れる（行・項目・位置）。</param>
    /// <returns>外す口。</returns>
    public static IDisposable List<T>(ListView list, ObservableList<T> items, Action<GameObject, T, int> bindRow)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(bindRow);
        return List(new ListViewTarget<T>(list, items, bindRow), items);
    }

    /// <summary>観測できる一覧を ListView へ結ぶ（owner の破棄で自動で外れる）。</summary>
    /// <typeparam name="T">項目の型。</typeparam>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="list">一覧の部品。</param>
    /// <param name="items">観測できる一覧。</param>
    /// <param name="bindRow">行へ項目を入れる（行・項目・位置）。</param>
    /// <returns>外す口。</returns>
    public static IDisposable List<T>(SEEDScript owner, ListView list, ObservableList<T> items, Action<GameObject, T, int> bindRow)
        => Own(owner, List(list, items, bindRow));

    /// <summary>
    /// ListView を作るときの行の入れ方（bind）を一覧から作る（位置の項目を読んで bindRow を呼ぶ。範囲の外の位置は何もしない）。
    /// </summary>
    /// <typeparam name="T">項目の型。</typeparam>
    /// <param name="items">観測できる一覧。</param>
    /// <param name="bindRow">行へ項目を入れる（行・項目・位置）。</param>
    /// <returns>ListView のコンストラクタの bind へ渡す処理。</returns>
    public static Action<GameObject, int> RowBinder<T>(ObservableList<T> items, Action<GameObject, T, int> bindRow)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(bindRow);
        return (row, index) =>
        {
            // 数を変えてから ListView.Update までの間に一覧が縮んだときの保険（範囲の外は入れない）
            if ((uint)index < (uint)items.Count) bindRow(row, items[index], index);
        };
    }
}
