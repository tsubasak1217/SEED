// ============================================================
//  LocalizationPanel.Grid.cs — 文字列表の表（列 = 言語・行 = キー）・絞り込み・選んだ行の説明・升目の編集
//
//  【列】キー（読み取り専用）・種類（複数形の形 / 1 文）・言語ごとの文（index.json の順）。言語の列は
//  DataGridTextColumn を Cells[i].Text に結ぶ（文字を打ち始めるとそのまま編集に入れる）。未訳の升目は
//  Cells[i].IsMissing で地の色を変え、状態の説明をツールチップに出す。読めなかった表の列は読み取り専用。
//  【行】モデル（LocaleTableModel.BuildRows）の行を絞り込んで並べる。升目の編集のようにキーの並びが変わらない
//  書き換えは、行を作り直さず中身だけを入れ直す（編集中の升目・スクロールの位置を失わない）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Localization.Model;
using SEEDEditor.Panels.Localization;

namespace SEEDEditor.Panels;

public partial class LocalizationPanel
{
    /// <summary>キーの列の幅（px）。</summary>
    private const double KeyColumnWidth = 220;

    /// <summary>種類の列の幅（px）。</summary>
    private const double KindColumnWidth = 52;

    /// <summary>言語の列の最小の幅（px）。</summary>
    private const double LanguageColumnMinWidth = 120;

    /// <summary>言語の列の幅の比（残りを言語の数で等分する）。</summary>
    private const double LanguageColumnStar = 1.0;

    /// <summary>列の見出しの 2 行目（既定・fallback）の文字の大きさ。</summary>
    private const double HeaderSubFontSize = 10;

    /// <summary>モデルの行（絞り込み前・キーの並び）。</summary>
    private IReadOnlyList<LocaleRow> _allRows = Array.Empty<LocaleRow>();

    /// <summary>表に出している行（絞り込み後）。</summary>
    private readonly ObservableCollection<LocaleGridRow> _rows = new();

    /// <summary>言語の列 → 言語のコード（見出しの右クリックで言語を引く）。</summary>
    private readonly Dictionary<DataGridColumn, string> _columnCodes = new();

    /// <summary>行の組み直しの最中か（選択の変化を説明の更新へ流さない）。</summary>
    private bool _rebuildingRows;

    // ============================================================
    //  列
    // ============================================================

    /// <summary>列を組み直す（言語の追加・外す・既定・fallback・名前の変更の後）。</summary>
    private void RebuildColumns()
    {
        Table.Columns.Clear();
        _columnCodes.Clear();
        if (_model is null) return;

        // ── キーと種類（読み取り専用）──
        Table.Columns.Add(new DataGridTextColumn
        {
            Header = LocalizationPanelMessages.KeyColumnHeader,
            Binding = new Binding(nameof(LocaleGridRow.Key)) { Mode = BindingMode.OneWay },
            IsReadOnly = true,
            Width = new DataGridLength(KeyColumnWidth),
            ElementStyle = (Style)FindResource("L10n.CellText"),
        });
        Table.Columns.Add(new DataGridTextColumn
        {
            Header = LocalizationPanelMessages.KindColumnHeader,
            Binding = new Binding(nameof(LocaleGridRow.KindLabel)) { Mode = BindingMode.OneWay },
            IsReadOnly = true,
            Width = new DataGridLength(KindColumnWidth),
            ElementStyle = (Style)FindResource("L10n.DimCellText"),
        });

        // ── 言語ごとの文 ──
        var languages = _model.Languages;
        for (int i = 0; i < languages.Count; i++)
        {
            var language = languages[i];
            var column = new DataGridTextColumn
            {
                Header = BuildHeader(language),
                Binding = new Binding($"{nameof(LocaleGridRow.Cells)}[{i}].{nameof(LocaleGridCell.Text)}") { Mode = BindingMode.TwoWay },
                IsReadOnly = language.IsReadOnly || _model.IsReadOnly,
                Width = new DataGridLength(LanguageColumnStar, DataGridLengthUnitType.Star),
                MinWidth = LanguageColumnMinWidth,
                ElementStyle = (Style)FindResource("L10n.CellText"),
                EditingElementStyle = (Style)FindResource("L10n.CellEdit"),
                CellStyle = BuildLanguageCellStyle(i),
            };
            Table.Columns.Add(column);
            _columnCodes[column] = language.Code;
        }
    }

    /// <summary>言語の列の見出し（名前とコード・2 行目に既定 / 次に探す言語 / 読み取り専用）。</summary>
    private FrameworkElement BuildHeader(LocaleLanguageInfo language)
    {
        var sub = new List<string>();
        if (language.IsDefault) sub.Add(LocalizationPanelMessages.DefaultMark);
        if (language.Fallback is not null) sub.Add($"{LocalizationPanelMessages.MenuFallback}: {language.Fallback}");
        if (language.IsReadOnly) sub.Add(LocalizationPanelMessages.ReadOnlyMark);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = LocalizationPanelMessages.LanguageLabel(language.Name, language.Code) });
        if (sub.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = string.Join(" / ", sub),
                FontSize = HeaderSubFontSize,
                Foreground = (Brush)FindResource("L10n.DimText"),
            });
        }
        panel.ToolTip = LocalizationPanelMessages.ColumnToolTip(language.FileName, _model!.FallbackChain(language.Code), language.LoadError);
        return panel;
    }

    /// <summary>言語の列の升目の見た目（未訳の地の色・状態の説明のツールチップ）。</summary>
    private Style BuildLanguageCellStyle(int index)
    {
        var style = new Style(typeof(DataGridCell), (Style)FindResource("L10n.Cell"));
        style.Setters.Add(new Setter(ToolTipProperty, new Binding($"{nameof(LocaleGridRow.Cells)}[{index}].{nameof(LocaleGridCell.ToolTip)}")));
        style.Setters.Add(new Setter(ToolTipService.ShowOnDisabledProperty, true));

        var missing = new DataTrigger
        {
            Binding = new Binding($"{nameof(LocaleGridRow.Cells)}[{index}].{nameof(LocaleGridCell.IsMissing)}"),
            Value = true,
        };
        missing.Setters.Add(new Setter(BackgroundProperty, FindResource("L10n.MissingCell")));
        missing.Setters.Add(new Setter(ForegroundProperty, FindResource("L10n.MissingCellText")));
        style.Triggers.Add(missing);
        return style;
    }

    // ============================================================
    //  行
    // ============================================================

    /// <summary>行を組み直す（モデルから取り直して絞り込む）。</summary>
    private void RebuildRows()
    {
        _allRows = _model?.BuildRows() ?? Array.Empty<LocaleRow>();
        ApplyFilter();
    }

    /// <summary>絞り込みを当て直す（選んでいたキーは保つ）。</summary>
    private void ApplyFilter()
    {
        var selected = SelectedRows().Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        _rebuildingRows = true;
        try
        {
            Table.ItemsSource = null;
            _rows.Clear();
            string search = TxtSearch.Text;
            bool missingOnly = ChkMissingOnly.IsChecked == true;
            foreach (var row in _allRows)
            {
                if (LocaleRowFilter.Matches(row, search, missingOnly)) _rows.Add(new LocaleGridRow(row, CommitCell));
            }
            Table.ItemsSource = _rows;
            foreach (var row in _rows.Where(r => selected.Contains(r.Key))) Table.SelectedItems.Add(row);
        }
        finally
        {
            _rebuildingRows = false;
        }
        UpdateSummary();
        ShowDetailsForSelection();
    }

    /// <summary>
    /// 編集の後に表を今のモデルへそろえる。キーの並びが同じなら行の中身だけを入れ直し、違えば組み直す。
    /// </summary>
    /// <param name="structureChanged">列（言語）が変わったか（変われば列から組み直す）。</param>
    private void RefreshAfterEdit(bool structureChanged)
    {
        if (_model is null) return;
        if (structureChanged)
        {
            RebuildAll();
            return;
        }

        var fresh = _model.BuildRows();
        bool sameKeys = fresh.Count == _allRows.Count
            && fresh.Zip(_allRows).All(p => p.First.Key == p.Second.Key && p.First.Kind == p.Second.Kind);
        _allRows = fresh;
        if (!sameKeys)
        {
            ApplyFilter();
            UpdateChrome();
            return;
        }

        // 同じキーの並び: 表示中の行の中身だけを入れ直す（絞り込みの当たり外れが変わっても、編集中の行は消さない）
        var byKey = fresh.ToDictionary(r => r.Key, StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            if (byKey.TryGetValue(row.Key, out var latest)) row.Apply(latest);
        }
        UpdateChrome();
    }

    /// <summary>要約（未訳の数・キーの数・表示中の数）を出す。</summary>
    private void UpdateSummary()
    {
        int missing = _allRows.Sum(r => r.MissingCount);
        TxtSummary.Text = LocalizationPanelMessages.Summary(missing, _allRows.Count, _rows.Count);
    }

    /// <summary>選んでいる行（表の並び順）。</summary>
    private IReadOnlyList<LocaleGridRow> SelectedRows() =>
        Table.SelectedItems.OfType<LocaleGridRow>().OrderBy(r => _rows.IndexOf(r)).ToList();

    /// <summary>キーの行を選んで見えるところへ出す（無ければ何もしない）。</summary>
    private void SelectKey(string? key)
    {
        if (key is null) return;
        var row = _rows.FirstOrDefault(r => r.Key == key);
        if (row is null) return;
        Table.SelectedItems.Clear();
        Table.SelectedItem = row;
        Table.ScrollIntoView(row);
    }

    /// <summary>言語の列へ目を向けさせる（最初の行のその列を今の升目にする）。</summary>
    private void FocusLanguageColumn(string code)
    {
        var column = _columnCodes.FirstOrDefault(p => string.Equals(p.Value, code, StringComparison.OrdinalIgnoreCase)).Key;
        if (column is null || _rows.Count == 0) return;
        var row = Table.SelectedItem as LocaleGridRow ?? _rows[0];
        Table.ScrollIntoView(row, column);
        Table.CurrentCell = new DataGridCellInfo(row, column);
    }

    // ============================================================
    //  升目の編集
    // ============================================================

    /// <summary>
    /// 升目の編集の確定（LocaleGridCell.Text の setter から）。モデルを書き換え、表を今のモデルへそろえる。
    /// 断られたら理由を出して升目を元へ戻す。
    /// </summary>
    private void CommitCell(LocaleGridCell cell, string? text)
    {
        if (_model is null) return;
        var result = _model.SetCell(cell.Key, cell.Code, text);
        if (!result.Succeeded)
        {
            ShowStatus(LocalizationPanelMessages.Failed("書き換え", result.Message), isError: true);
            // 結び付けの更新の最中なので、表示を戻すのは一呼吸おいてから
            Dispatcher.BeginInvoke(cell.RaiseTextAgain);
            return;
        }
        if (!result.Changed) return;
        // 結び付けの更新の最中に行の中身を差し替えないよう、一呼吸おいてからそろえる
        Dispatcher.BeginInvoke(() => RefreshAfterEdit(structureChanged: false));
    }

    /// <summary>編集中の升目があれば確定する（保存・操作の前）。</summary>
    private void CommitPendingEdit()
    {
        Table.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
        Table.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
    }

    // ============================================================
    //  絞り込み・選択・説明
    // ============================================================

    /// <summary>検索の文字が変わった。</summary>
    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        CommitPendingEdit();
        ApplyFilter();
    }

    /// <summary>「未訳だけ表示」が変わった。</summary>
    private void OnMissingOnlyChanged(object sender, RoutedEventArgs e)
    {
        CommitPendingEdit();
        ApplyFilter();
    }

    /// <summary>選んだ行が変わった: 説明を出し、ボタンの有効無効をそろえる。</summary>
    private void OnTableSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingRows) return;
        ShowDetailsForSelection();
        UpdateChrome();
    }

    /// <summary>選んだ行のキーの説明（"_" で始まる鍵）を説明の帯に出す。</summary>
    private void ShowDetailsForSelection()
    {
        var row = Table.SelectedItem as LocaleGridRow;
        if (row is null || _model is null)
        {
            TxtDetails.Text = LocalizationPanelMessages.NoSelection;
            return;
        }

        var comments = _model.CommentsFor(row.Source.LogicalKey);
        if (comments.Count == 0)
        {
            TxtDetails.Text = $"{row.Key} — {LocalizationPanelMessages.NoComments}";
            return;
        }
        var text = new StringBuilder(row.Key).Append(" — ").Append(LocalizationPanelMessages.CommentsHeader);
        for (int i = 0; i < comments.Count; i++)
        {
            if (i > 0) text.Append(" / ");
            text.Append(comments[i].Location).Append(": ").Append(comments[i].Text);
        }
        TxtDetails.Text = text.ToString();
    }

    /// <summary>表のキー: Delete で選んだキーを消す（編集中は升目の文字の削除）。</summary>
    private void OnTablePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || IsEditingCell()) return;
        if (!SelectedRows().Any()) return;
        DeleteSelectedKeys();
        e.Handled = true;
    }

    /// <summary>升目を編集中か（編集の入力欄にフォーカスがある）。</summary>
    private bool IsEditingCell() => Keyboard.FocusedElement is TextBox box && IsDescendantOf(box, Table);

    /// <summary>要素が祖先の中にあるか。</summary>
    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = ParentOf(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
}
