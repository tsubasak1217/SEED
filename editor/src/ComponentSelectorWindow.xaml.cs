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

public partial class ComponentSelectorWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly RuntimeManager _runtime;
    private readonly int            _actorDfsId;

    private string? _selectedType;
    private Border? _selectedBorder;

    private readonly HashSet<string> _collapsedCategories = new();

    private record ComponentEntry(string TypeId, string Label, string Description);

    private static readonly List<(string Category, List<ComponentEntry> Items)> Categories = new()
    {
        ("レンダリング", new()
        {
            new("ModelComponent", "Model", "3D モデルをアクタにアタッチ"),
        }),
        ("ライト", new()),
        ("エフェクト", new()),
        ("カメラ", new()),
        ("物理", new()),
        ("サウンド", new()),
        ("スクリプト", new()
        {
            new("ScriptComponent", "Script", "スクリプトをアクタにアタッチ"),
        }),
    };

    private static readonly SolidColorBrush BrushSelected  = new(Color.FromRgb(0x1A, 0x2A, 0x3A));
    private static readonly SolidColorBrush BrushHover     = new(Color.FromRgb(0x28, 0x28, 0x28));
    private static readonly SolidColorBrush BrushTransp    = Brushes.Transparent;
    private static readonly SolidColorBrush BrushAccent    = new(Color.FromRgb(0x33, 0x99, 0xFF));

    public ComponentSelectorWindow(RuntimeManager runtime, int actorDfsId)
    {
        InitializeComponent();
        _runtime    = runtime;
        _actorDfsId = actorDfsId;
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

    private void BuildCategoryList(string filter)
    {
        CategoryList.Children.Clear();
        _selectedBorder = null;
        var prevType = _selectedType;

        // 検索中はフラットリスト表示（カテゴリヘッダーなし）
        if (!string.IsNullOrEmpty(filter))
        {
            var matches = Categories
                .SelectMany(c => c.Items)
                .Where(i => i.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || i.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
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
            return;
        }

        // 通常表示: カテゴリヘッダー + 開閉
        foreach (var (catName, items) in Categories)
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

            if (items.Count == 0)
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

            foreach (var entry in items)
            {
                var row = BuildItemRow(entry);
                CategoryList.Children.Add(row);
                if (entry.TypeId == prevType) SelectRow(row, entry);
            }
        }
    }

    private Border BuildItemRow(ComponentEntry entry)
    {
        var label = new TextBlock
        {
            Text       = entry.Label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize   = 12,
        };
        var desc = new TextBlock
        {
            Text       = entry.Description,
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
            FontSize   = 10,
            Margin     = new Thickness(0, 1, 0, 0),
        };
        var sp = new StackPanel();
        sp.Children.Add(label);
        if (!string.IsNullOrEmpty(entry.Description)) sp.Children.Add(desc);

        var border = new Border
        {
            Padding    = new Thickness(28, 5, 8, 5),
            Cursor     = Cursors.Hand,
            Background = Brushes.Transparent,
            Child      = sp,
            Tag        = entry,
        };

        border.MouseEnter += (_, _) =>
        {
            if (border != _selectedBorder) border.Background = BrushHover;
        };
        border.MouseLeave += (_, _) =>
        {
            if (border != _selectedBorder) border.Background = Brushes.Transparent;
        };
        border.MouseLeftButtonDown += (_, _) => SelectRow(border, entry);

        return border;
    }

    private void SelectRow(Border row, ComponentEntry entry)
    {
        if (_selectedBorder != null) _selectedBorder.Background = Brushes.Transparent;
        _selectedBorder  = row;
        row.Background   = BrushSelected;
        _selectedType    = entry.TypeId;

        TbName.Text      = string.IsNullOrEmpty(TbName.Text) || TbName.Text == GetDefaultName(_selectedType ?? "")
            ? entry.Label
            : TbName.Text;

        BtnConfirm.IsEnabled = true;
    }

    private static string GetDefaultName(string typeId) => typeId switch
    {
        "ModelComponent"  => "Model",
        "ScriptComponent" => "Script",
        _                 => typeId,
    };

    // ── イベント ─────────────────────────────────────────────

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        BuildCategoryList(TxtSearch.Text.Trim());
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            // 最初のアイテムを選択
            foreach (var child in CategoryList.Children)
            {
                if (child is Border b && b.Tag is ComponentEntry entry)
                {
                    SelectRow(b, entry);
                    TbName.Focus();
                    e.Handled = true;
                    break;
                }
            }
        }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && BtnConfirm.IsEnabled) { OnConfirm(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_selectedType is null) return;
        var name = TbName.Text.Trim();
        if (string.IsNullOrEmpty(name)) name = GetDefaultName(_selectedType);

        // 空の状態で追加（パスなし）。インスペクター上で後から設定する。
        _runtime.SendToRuntime($"ADD_COMPONENT:{_actorDfsId},{_selectedType},{name},");
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
