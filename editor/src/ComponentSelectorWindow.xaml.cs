using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SEEDEditor.Runtime;

namespace SEEDEditor;

/// <summary>
/// アクターにアタッチする ECS コンポーネントをカテゴリ一覧から選択・検索して追加するダイアログ。
/// 静的カテゴリリストとロード済みプラグイン一覧から動的に「プラグイン」カテゴリを生成し、
/// 選択したコンポーネント種別・名前でランタイムへ ADD_COMPONENT コマンドを送信する。
/// </summary>
public partial class ComponentSelectorWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly RuntimeManager  _runtime;
    private readonly int             _actorDfsId;
    private readonly bool            _isActor2D;       // 2D アクターかどうか
    /// <summary>追加上限に達しているため選択不可のコンポーネント種別 ID セット。</summary>
    private readonly HashSet<string> _disabledTypes;
    /// <summary>ロード済みプラグイン名リスト（"プラグイン" カテゴリのエントリに使用）。</summary>
    private readonly IReadOnlyList<string> _pluginNames;

    private string? _selectedType;
    private Border? _selectedBorder;

    /// <summary>
    /// 現在表示中の「選択できる」アイテム行を上から順に保持したリスト（↑↓ キー移動用）。
    /// BuildCategoryList のたびに作り直す。追加上限に達した行（disabled）は
    /// 選択させても Enter で追加できないため含めない。
    /// </summary>
    private readonly List<(Border Row, ComponentEntry Entry)> _navigableRows = new();

    private readonly HashSet<string> _collapsedCategories = new();

    // ── コンポーネント一覧の情報源 ────────────────────────────────
    //
    // 種別・表示名・既定名・説明・対応アクター種別は ComponentCatalog（WPF 非依存）が
    // 唯一の情報源として持つ。以前はこのファイル内に一覧と「既定名を返す switch」の
    // 2 つの表があり、両者が食い違ったせいで
    // 「追加したコンポーネントの既定名が別のコンポーネント名になる」不具合が出ていた。
    // このウィンドウは表示と入力だけを担当し、名前の決定はカタログへ委ねる。

    /// <summary>
    /// ロード済みプラグインリストから動的にカテゴリエントリを生成して返す。
    /// プラグインがない場合は空のカテゴリを返す（「今後追加」として表示される）。
    /// </summary>
    private List<ComponentEntry> BuildPluginEntries() =>
        ComponentCatalog.PluginEntries(_pluginNames);

    /// <summary>コンポーネント種別アイコンの一辺サイズ（px）。</summary>
    private const double ItemIconSize = 14.0;

    /// <summary>アイコンとラベルの間隔（px）。</summary>
    private const double ItemIconGap = 6.0;

    /// <summary>通常行のラベル／アイコン色。</summary>
    private static readonly SolidColorBrush BrushLabelNormal   = new(Color.FromRgb(0xCC, 0xCC, 0xCC));

    /// <summary>追加済みで選べない行のラベル／アイコン色。</summary>
    private static readonly SolidColorBrush BrushLabelDisabled = new(Color.FromRgb(0x44, 0x44, 0x44));

    private static readonly SolidColorBrush BrushSelected  = new(Color.FromRgb(0x1A, 0x2A, 0x3A));
    private static readonly SolidColorBrush BrushHover     = new(Color.FromRgb(0x28, 0x28, 0x28));
    private static readonly SolidColorBrush BrushTransp    = Brushes.Transparent;
    private static readonly SolidColorBrush BrushAccent    = new(Color.FromRgb(0x33, 0x99, 0xFF));

    public ComponentSelectorWindow(
        RuntimeManager runtime, int actorDfsId,
        bool isActor2D = false, HashSet<string>? disabledTypes = null,
        IReadOnlyList<string>? pluginNames = null)
    {
        InitializeComponent();
        _runtime      = runtime;
        _actorDfsId   = actorDfsId;
        _isActor2D    = isActor2D;
        _disabledTypes = disabledTypes ?? new HashSet<string>();
        _pluginNames  = pluginNames ?? Array.Empty<string>();
        BuildCategoryList(filter: "");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        int dark = 1;
        DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        TxtSearch.Focus();
    }

    // ── リスト構築 ───────────────────────────────────────────

    /// <summary>
    /// エントリが現在のアクター種別に対応しているか判定する。
    /// Common はどちらにも表示、Actor2D/Actor3D は対応する種別のみ表示。
    /// </summary>
    private bool EntryMatchesActor(ComponentEntry entry) =>
        entry.Target == ComponentActorTarget.Common ||
        (_isActor2D
            ? entry.Target == ComponentActorTarget.Actor2D
            : entry.Target == ComponentActorTarget.Actor3D);

    private void BuildCategoryList(string filter)
    {
        CategoryList.Children.Clear();
        _selectedBorder = null;
        _navigableRows.Clear();
        var prevType = _selectedType;

        // 検索中はフラットリスト表示（カテゴリヘッダーなし）
        // プラグインカテゴリを動的追加して全カテゴリを対象に検索する
        if (!string.IsNullOrEmpty(filter))
        {
            var matches = ComponentCatalog.Categories
                .SelectMany(c => c.Items)
                .Concat(BuildPluginEntries())
                .Where(i => EntryMatchesActor(i)
                         && (i.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                          || i.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (matches.Count == 0)
            {
                CategoryList.Children.Add(new TextBlock
                {
                    Text       = "  一致する項目がありません",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                    FontSize   = 11,
                    Margin     = new Thickness(12, 8, 8, 4),
                });
            }
            else
            {
                foreach (var entry in matches)
                {
                    var row = BuildItemRow(entry);
                    CategoryList.Children.Add(row);
                    if (entry.TypeId == prevType) SelectRow(row, entry);
                }
            }
            SelectFirstRowIfNoneSelected();
            return;
        }

        // 通常表示: カテゴリヘッダー + 開閉
        // 静的カテゴリリストにプラグインカテゴリを動的追加して結合する
        // 静的カタログの要素型に合わせてプラグインカテゴリを結合する
        // （Categories は IReadOnlyList<ComponentEntry> を要素に持つ）。
        var allCategories = ComponentCatalog.Categories
            .Append(("プラグイン", (IReadOnlyList<ComponentEntry>)BuildPluginEntries()))
            .ToList();
        foreach (var (catName, items) in allCategories)
        {
            bool collapsed = _collapsedCategories.Contains(catName);

            var header = new TextBlock { Style = (Style)Resources["CategoryHeader"] };
            header.Inlines.Add(new Run(collapsed ? "▶ " : "▼ ")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                FontSize   = 9,
            });
            header.Inlines.Add(new Run(catName));
            header.MouseLeftButtonDown += (_, _) =>
            {
                if (_collapsedCategories.Contains(catName))
                    _collapsedCategories.Remove(catName);
                else
                    _collapsedCategories.Add(catName);
                BuildCategoryList(TxtSearch.Text.Trim());
            };
            CategoryList.Children.Add(header);

            if (collapsed) continue;

            // 現在のアクター種別でフィルタリング
            var visibleItems = items.Where(EntryMatchesActor).ToList();

            if (visibleItems.Count == 0)
            {
                CategoryList.Children.Add(new TextBlock
                {
                    Text       = "  （今後追加）",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                    FontSize   = 11,
                    Margin     = new Thickness(28, 2, 8, 4),
                });
                continue;
            }

            foreach (var entry in visibleItems)
            {
                var row = BuildItemRow(entry);
                CategoryList.Children.Add(row);
                if (entry.TypeId == prevType) SelectRow(row, entry);
            }
        }

        SelectFirstRowIfNoneSelected();
    }

    /// <summary>
    /// どの行も選択されていなければ先頭の選択可能行を選ぶ。
    /// 「開いた直後から Enter で決定できる」「検索を絞ったら先頭候補が選ばれる」を担保する
    /// （前回選択していた種別が絞り込みで消えた場合もここで拾い直す）。
    /// </summary>
    private void SelectFirstRowIfNoneSelected()
    {
        if (_selectedBorder is not null || _navigableRows.Count == 0) return;
        var (row, entry) = _navigableRows[0];
        SelectRow(row, entry);
    }

    private Border BuildItemRow(ComponentEntry entry)
    {
        var disabled = _disabledTypes.Contains(entry.TypeId);

        var label = new TextBlock
        {
            Text       = entry.Label,
            Foreground = disabled ? BrushLabelDisabled : BrushLabelNormal,
            FontSize   = 12,
        };

        // disabled の場合は「（既に追加済み）」を付記する
        var descText = disabled ? entry.Description + "  ※既に追加済み（1 つまで）" : entry.Description;
        var desc = new TextBlock
        {
            Text       = descText,
            Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            FontSize   = 10,
            Margin     = new Thickness(0, 1, 0, 0),
        };
        var texts = new StackPanel();
        texts.Children.Add(label);
        if (!string.IsNullOrEmpty(entry.Description)) texts.Children.Add(desc);

        // コンポーネント種別アイコン（対応表は Controls/ComponentIcons.cs が唯一の正典）。
        // 無効行（追加済み）はラベルと同じトーンまで落として一体で減光する。
        var icon = SEEDEditor.Controls.AppIcon.Create(
            SEEDEditor.Controls.ComponentIcons.GetIconKey(entry.TypeId), ItemIconSize);
        icon.SetBrush(disabled ? BrushLabelDisabled : BrushLabelNormal);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin            = new Thickness(0, 0, ItemIconGap, 0);

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(icon);
        sp.Children.Add(texts);

        var border = new Border
        {
            // アイコンぶんだけ左パディングを詰め、行全体の見た目の開始位置は従来どおりに保つ。
            Padding    = new Thickness(28 - ItemIconSize - ItemIconGap, 5, 8, 5),
            Cursor     = disabled ? Cursors.No : Cursors.Hand,
            Background = Brushes.Transparent,
            Child      = sp,
            Tag        = entry,
        };

        if (!disabled)
        {
            border.MouseEnter += (_, _) =>
            {
                if (border != _selectedBorder) border.Background = BrushHover;
            };
            border.MouseLeave += (_, _) =>
            {
                if (border != _selectedBorder) border.Background = Brushes.Transparent;
            };
            border.MouseLeftButtonDown += (_, _) => SelectRow(border, entry);
            // ↑↓ キーでの移動対象に登録する（表示順＝リストの並び）。
            _navigableRows.Add((border, entry));
        }

        return border;
    }

    private void SelectRow(Border row, ComponentEntry entry)
    {
        if (_selectedBorder != null) _selectedBorder.Background = Brushes.Transparent;
        _selectedBorder  = row;
        row.Background   = BrushSelected;

        // 前の型を控えてから型を更新する（自動リネーム判定に使用）。
        var prevType  = _selectedType;
        _selectedType = entry.TypeId;

        // 名前欄が空、または「直前に選んだ型の既定名のまま」＝ユーザー未編集なら、
        // 新しい型の既定名へ差し替える。判定と書き込みは同じ ComponentCatalog の
        // 既定名を使うため、種別を選び直しても前の種別の名前が残ることはない。
        TbName.Text = ComponentCatalog.NextDefaultName(TbName.Text, prevType, entry.TypeId);

        BtnConfirm.IsEnabled = true;
    }

    // ── イベント ─────────────────────────────────────────────

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        BuildCategoryList(TxtSearch.Text.Trim());
    }

    /// <summary>
    /// ウィンドウ全体のキー入力で一覧を操作する。
    /// ↑↓ で選択移動、Enter で追加を実行、Esc で閉じる。
    ///
    /// 検索ボックス・名前ボックスにフォーカスがあっても効くように PreviewKeyDown で受ける
    /// （TextBox に矢印キーや Enter を取られる前に処理する）。文字入力は素通しするため、
    /// 検索しながら ↑↓ で候補を選び Enter で追加する、という一連の操作がキーボードだけで完結する。
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;

            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;

            // Key.Enter は Key.Return と同一値（WPF の別名）なので 1 つだけ書く。
            case Key.Return:
                if (BtnConfirm.IsEnabled)
                {
                    OnConfirm(sender, e);
                    e.Handled = true;
                }
                break;

            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 選択を delta 行ぶん動かす（端で止まる。循環はしない）。
    /// 未選択なら先頭（delta が負でも先頭）を選ぶ。移動先はスクロールして見える位置へ送る。
    /// </summary>
    private void MoveSelection(int delta)
    {
        if (_navigableRows.Count == 0) return;

        int current = _selectedBorder is null
            ? -1
            : _navigableRows.FindIndex(r => ReferenceEquals(r.Row, _selectedBorder));
        int next = current < 0 ? 0 : Math.Clamp(current + delta, 0, _navigableRows.Count - 1);

        var (row, entry) = _navigableRows[next];
        SelectRow(row, entry);
        row.BringIntoView();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_selectedType is null) return;
        // 追加制限チェック（UI で弾いているが念のため再確認する）
        if (_disabledTypes.Contains(_selectedType))
        {
            MessageBox.Show(
                $"{_selectedType} は 1 アクターにつき 1 つのみ追加できます。",
                "追加不可", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var name = TbName.Text.Trim();
        if (string.IsNullOrEmpty(name)) name = ComponentCatalog.DefaultNameOf(_selectedType);

        // 空の状態で追加（パスなし）。インスペクター上で後から設定する。
        _runtime.SendToRuntime($"ADD_COMPONENT:{_actorDfsId},{_selectedType},{name},");
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
