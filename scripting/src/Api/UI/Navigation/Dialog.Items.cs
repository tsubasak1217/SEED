using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  Dialog.Items.cs — 選択肢の一覧のダイアログの行（2026-10-02。Material の SimpleDialog 相当。docs/ui_navigation.md §3.2）
//
//  DialogOptions.Items の項目ごとに、行のプレハブ（DialogOptions.ItemPrefab。既定 dialog_item.actor）を札の Card/Items/Viewport の下へ作る
//  （窓の縦の CanvasStack〈Stretch〉が札の幅いっぱいに並べる。行の高さは size.dialog_item_height）。行は作った次のフレームにできあがるので、
//  行のスクリプト（SEED.UI.DialogItem）が始まるまでダイアログを見せずに待つ（Dialog.PrepareRows）。行のタップで、選べる項目なら
//  結果 Selected・選んだ番号（DialogHandle.SelectedIndex）で閉じる。
//  一覧のどれかの項目にアイコンがあれば、アイコンの無い項目もアイコン欄（size.icon ＋ size.icon_gap）の分だけ文字を右へずらしてそろえる。
//  Card/Items/Viewport の無い古いプレハブでは一覧を出せない（警告。ボタンの無いダイアログになる）。
// ============================================================

public sealed partial class Dialog
{
    /// <summary>選択肢の一覧の区画（中に切り抜く窓）。</summary>
    private const string ItemsChild = "Card/Items";
    /// <summary>選択肢の一覧の窓（CanvasClip・CanvasScroll・縦の CanvasStack〈Stretch〉。行を並べる）。</summary>
    private const string ItemsViewportChild = "Card/Items/Viewport";

    /// <summary>選択肢の一覧の区画と窓。</summary>
    private GameObject _items, _itemsViewport;
    /// <summary>作った行のノード（項目の順。作れなかった項目は飛ばす）。</summary>
    private readonly List<GameObject> _rows = new();
    /// <summary>行ごとの項目の番号（DialogOptions.Items の添字）。</summary>
    private readonly List<int> _rowItems = new();
    /// <summary>つないだ行の部品（スクリプトが始まる前は null）。</summary>
    private readonly List<DialogItem?> _rowWidgets = new();
    /// <summary>行の幅（札の幅）と高さ（最後の割り付けの値）。</summary>
    private float _rowWidth, _rowHeight;

    /// <summary>行のプレハブ（DialogOptions.ItemPrefab。空なら既定）。</summary>
    private string ItemPrefabPath => _options.ItemPrefab.Length > 0 ? _options.ItemPrefab : DialogOptions.DefaultItemPrefab;

    /// <summary>選択肢があれば行を作る（無ければ一覧の区画を隠す）。</summary>
    private void CreateItems()
    {
        if (!DialogModel.HasItems(_options))
        {
            NavNode.SetVisible(_items, false);
            return;
        }
        if (!_itemsViewport.IsValid)
        {
            Debug.LogWarning($"{LogPrefix} このダイアログのプレハブには選択肢の一覧の区画（{ItemsViewportChild}）がありません。"
                             + "templates/ui の dialog.actor（2026-10-02 版）を取り込み直してください");
            return;
        }
        for (int i = 0; i < _options.Items.Count; i++)
        {
            if (_options.Items[i] is null) continue;
            var row = GameObject.Instantiate(ItemPrefabPath, _itemsViewport);
            if (!row.IsValid)
            {
                Debug.LogWarning($"{LogPrefix} 選択肢の行のプレハブを作れませんでした（{ItemPrefabPath}）");
                continue;
            }
            _rows.Add(row);
            _rowItems.Add(i);
            _rowWidgets.Add(null);
        }
    }

    /// <summary>すべての行ができあがり、行のスクリプトが始まったか（作った行が無ければ true）。</summary>
    private bool RowsReady()
    {
        foreach (var row in _rows)
            if (!NavNode.IsBuilt(row) || Of<DialogItem>(row) is null) return false;
        return true;
    }

    /// <summary>始まった行の部品をつなぐ（中身を当て、タップを受ける）。</summary>
    private void BindItems()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rowWidgets[i] is not null || Of<DialogItem>(_rows[i]) is not { } widget) continue;
            _rowWidgets[i] = widget;
            widget.Tapped += OnItemTapped;
            ConfigureRow(i);
        }
    }

    /// <summary>行の大きさ（札の幅 × 行の高さ）を覚えて、つないだ行へ当てる（割り付けのたび）。</summary>
    /// <param name="rowWidth">行の幅（札の幅）。</param>
    /// <param name="rowHeight">行の高さ（size.dialog_item_height）。</param>
    private void ConfigureRows(float rowWidth, float rowHeight)
    {
        _rowWidth = rowWidth;
        _rowHeight = rowHeight;
        for (int i = 0; i < _rows.Count; i++) ConfigureRow(i);
    }

    /// <summary>行 i へ項目・大きさ・アイコン欄の有無を当てる（行のスクリプトが始まっていなければ何もしない）。</summary>
    private void ConfigureRow(int i)
    {
        if (i < 0 || i >= _rowWidgets.Count || _rowWidgets[i] is not { } widget) return;
        int index = _rowItems[i];
        widget.Configure(index, _options.Items[index], _rowWidth, _rowHeight, DialogModel.AnyItemIcon(_options));
    }

    /// <summary>行のタップ: 選べる項目なら結果 Selected と番号で閉じる。</summary>
    private void OnItemTapped(DialogItem item)
    {
        if (!DialogModel.CanSelect(_options, item.Index)) return;
        Choose(DialogResult.Selected, item.Index);
    }
}
