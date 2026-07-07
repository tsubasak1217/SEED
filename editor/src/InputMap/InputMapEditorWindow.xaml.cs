// ============================================================
//  InputMapEditorWindow.xaml.cs — InputMap アセットエディタ
//
//  【構成】
//  ・左ペイン  : アクション一覧（ListBox）
//  ・右ペイン  : 選択アクションのバインディング編集
//                プラットフォーム / 入力種別 / 入力値（検索付き選択）
//  ・フッター  : 保存して閉じる / キャンセル
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace SEEDEditor.InputMap;

public partial class InputMapEditorWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    // ── フィールド ───────────────────────────────────────────

    private string       _filePath;
    private InputMapData _data;
    private InputAction? _selectedAction;
    private bool         _isDirty;

    // ── 静的データ定義 ───────────────────────────────────────

    private static readonly string[] Platforms    = ["PC", "Mobile", "PS5", "Switch"];
    private static readonly string[] PcInputTypes = ["Key", "GamepadButton", "GamepadAxis", "WASD"];
    private static readonly string[] MobInputTypes = ["VirtualButton", "VirtualStick"];
    private static readonly string[] ConInputTypes = ["GamepadButton", "GamepadAxis"];

    /// <summary>プラットフォーム + 入力種別 → 選択可能な入力値の一覧。</summary>
    private static readonly Dictionary<(string Platform, string InputType), string[]> InputValues = new()
    {
        [("PC", "Key")] = [
            "Space", "Enter", "Tab", "Backspace", "Escape", "Delete",
            "A","B","C","D","E","F","G","H","I","J","K","L","M",
            "N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
            "Alpha0","Alpha1","Alpha2","Alpha3","Alpha4",
            "Alpha5","Alpha6","Alpha7","Alpha8","Alpha9",
            "F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12",
            "UpArrow","DownArrow","LeftArrow","RightArrow",
            "LeftShift","RightShift","LeftCtrl","RightCtrl","LeftAlt","RightAlt",
            "Keypad0","Keypad1","Keypad2","Keypad3","Keypad4",
            "Keypad5","Keypad6","Keypad7","Keypad8","Keypad9",
        ],
        [("PC", "GamepadButton")] = [
            "South","North","East","West",
            "L1","R1","L2","R2","L3","R3",
            "Start","Select",
            "DPadUp","DPadDown","DPadLeft","DPadRight",
        ],
        [("PC", "GamepadAxis")]  = ["LeftX","LeftY","RightX","RightY","L2","R2"],
        [("PC", "WASD")]         = ["Horizontal","Vertical"],
        // Mobile はカスタム名のため候補なし（自由入力）
        [("Mobile", "VirtualButton")] = [],
        [("Mobile", "VirtualStick")]  = [],
        [("PS5", "GamepadButton")] = [
            "Cross","Circle","Square","Triangle",
            "L1","R1","L2","R2","L3","R3",
            "DPadUp","DPadDown","DPadLeft","DPadRight",
            "Options","Create","TouchPad",
        ],
        [("PS5", "GamepadAxis")]  = ["LeftX","LeftY","RightX","RightY","L2","R2"],
        [("Switch", "GamepadButton")] = [
            "A","B","X","Y",
            "L","R","ZL","ZR","L3","R3",
            "Plus","Minus",
            "DPadUp","DPadDown","DPadLeft","DPadRight",
        ],
        [("Switch", "GamepadAxis")] = ["LeftX","LeftY","RightX","RightY","ZL","ZR"],
    };

    // ── コンストラクタ ───────────────────────────────────────

    public InputMapEditorWindow(string filePath = "")
    {
        InitializeComponent();
        _filePath = filePath;
        _data     = InputMapData.LoadFrom(filePath);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        int dark = 1;
        DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        TbFilePath.Text = string.IsNullOrEmpty(_filePath) ? "（未保存）" : _filePath;
        RefreshActionList();
    }

    // ── アクション一覧 ───────────────────────────────────────

    /// <summary>左ペインのアクション ListBox を再構築する。</summary>
    private void RefreshActionList()
    {
        ActionList.Items.Clear();
        foreach (var action in _data.Actions)
        {
            var item = new ListBoxItem
            {
                Content    = action.Name,
                Tag        = action,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                Background = Brushes.Transparent,
                Padding    = new Thickness(8, 5, 8, 5),
            };
            ActionList.Items.Add(item);
        }

        // 選択アクションを復元する
        if (_selectedAction is not null)
        {
            foreach (ListBoxItem item in ActionList.Items)
                if (item.Tag == _selectedAction) { ActionList.SelectedItem = item; break; }
        }
    }

    private void OnAddAction(object sender, RoutedEventArgs e)
    {
        // "NewAction", "NewAction1", ... と重複しない名前を生成する
        var baseName = "NewAction";
        var name = baseName;
        int suffix = 0;
        while (_data.Actions.Any(a => a.Name == name))
            name = baseName + (++suffix);

        var action = new InputAction { Name = name };
        _data.Actions.Add(action);
        _selectedAction = action;
        _isDirty = true;
        RefreshActionList();
        RefreshBindingPane();
    }

    private void OnRemoveAction(object sender, RoutedEventArgs e)
    {
        if (_selectedAction is null) return;
        var result = MessageBox.Show(
            $"アクション \"{_selectedAction.Name}\" を削除しますか？",
            "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        _data.Actions.Remove(_selectedAction);
        _selectedAction = _data.Actions.Count > 0 ? _data.Actions[^1] : null;
        _isDirty = true;
        RefreshActionList();
        RefreshBindingPane();
    }

    private void OnActionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActionList.SelectedItem is ListBoxItem item && item.Tag is InputAction action)
        {
            _selectedAction           = action;
            BtnRemoveAction.IsEnabled = true;
        }
        else
        {
            _selectedAction           = null;
            BtnRemoveAction.IsEnabled = false;
        }
        RefreshBindingPane();
    }

    private void OnActionNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_selectedAction is null) return;
        var newName = TbActionName.Text.Trim();
        if (string.IsNullOrEmpty(newName)) return;

        _selectedAction.Name = newName;
        _isDirty = true;
        if (ActionList.SelectedItem is ListBoxItem item) item.Content = newName;
    }

    private void OnValueTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedAction is null) return;
        if (CbValueType.SelectedItem is ComboBoxItem ci && ci.Tag is string tag)
        {
            _selectedAction.ValueType = Enum.TryParse<ActionValueType>(tag, out var vt)
                ? vt : ActionValueType.Bool;
            _isDirty = true;
        }
    }

    // ── バインディング編集ペイン ─────────────────────────────

    /// <summary>右ペインを再構築する（アクション未選択時は非表示）。</summary>
    private void RefreshBindingPane()
    {
        BindingStack.Children.Clear();

        if (_selectedAction is null)
        {
            ActionHeader.Visibility  = Visibility.Collapsed;
            BindingFooter.Visibility = Visibility.Collapsed;
            return;
        }

        ActionHeader.Visibility  = Visibility.Visible;
        BindingFooter.Visibility = Visibility.Visible;

        // アクション名 TextBox を更新（TextChanged を一時停止してプログラム書き込み）
        TbActionName.TextChanged -= OnActionNameChanged;
        TbActionName.Text = _selectedAction.Name;
        TbActionName.TextChanged += OnActionNameChanged;

        // 値型コンボボックスを更新
        CbValueType.SelectionChanged -= OnValueTypeChanged;
        for (int i = 0; i < CbValueType.Items.Count; i++)
        {
            if (CbValueType.Items[i] is ComboBoxItem ci
                && ci.Tag is string tag
                && tag == _selectedAction.ValueType.ToString())
            {
                CbValueType.SelectedIndex = i;
                break;
            }
        }
        CbValueType.SelectionChanged += OnValueTypeChanged;

        // カラムヘッダー行
        BindingStack.Children.Add(BuildBindingHeaderRow());

        // バインディング行
        for (int i = 0; i < _selectedAction.Bindings.Count; i++)
            BindingStack.Children.Add(BuildBindingRow(_selectedAction.Bindings[i], i));
    }

    /// <summary>バインディング一覧のカラムヘッダー行を構築する。</summary>
    private static UIElement BuildBindingHeaderRow()
    {
        var grid = BuildBindingGrid();
        void AddHeader(int col, string text)
        {
            var tb = new TextBlock
            {
                Text              = text,
                Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize          = 10,
                FontWeight        = FontWeights.Bold,
                Margin            = new Thickness(8, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }
        AddHeader(0, "プラットフォーム");
        AddHeader(1, "入力種別");
        AddHeader(2, "入力値（検索可）");

        return new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding         = new Thickness(0, 4, 0, 4),
            Child           = grid,
        };
    }

    /// <summary>バインディング 1 行分の UI を構築する。</summary>
    private UIElement BuildBindingRow(InputBinding binding, int rowIdx)
    {
        var grid = BuildBindingGrid();

        // ── プラットフォーム ComboBox ───────────────────────
        var cbPlatform = BuildDarkComboBox();
        foreach (var p in Platforms) cbPlatform.Items.Add(p);
        cbPlatform.SelectedItem = Platforms.Contains(binding.Platform)
            ? binding.Platform : Platforms[0];

        // ── 入力種別 ComboBox ───────────────────────────────
        var cbInputType = BuildDarkComboBox();
        PopulateInputTypes(cbInputType, binding.Platform, binding.InputType);

        // ── 入力値 ComboBox（検索付き）─────────────────────
        var cbValue = BuildValueComboBox(binding);

        // ── プラットフォーム変更 ────────────────────────────
        // binding.Platform と同値の場合は初期設定時のイベントをスキップする
        cbPlatform.SelectionChanged += (_, _) =>
        {
            var p = cbPlatform.SelectedItem?.ToString() ?? "PC";
            if (binding.Platform == p) return;
            binding.Platform = p;
            _isDirty = true;
            // 入力種別の選択肢とバインディングペインを再構築する
            Dispatcher.BeginInvoke(new Action(RefreshBindingPane));
        };

        // ── 入力種別変更 ────────────────────────────────────
        cbInputType.SelectionChanged += (_, _) =>
        {
            var t = cbInputType.SelectedItem?.ToString() ?? "";
            if (binding.InputType == t) return;
            binding.InputType = t;
            _isDirty = true;
            // 入力値の選択肢を再構築するためペインを再描画する
            Dispatcher.BeginInvoke(new Action(RefreshBindingPane));
        };

        // ── 削除ボタン ───────────────────────────────────────
        var btnRemove = new Button
        {
            Content         = "✕",
            Width           = 28,
            Height          = 24,
            Background      = Brushes.Transparent,
            Foreground      = new SolidColorBrush(Color.FromRgb(0xAA, 0x44, 0x44)),
            BorderThickness = new Thickness(0),
            FontSize        = 11,
            Cursor          = Cursors.Hand,
            Margin          = new Thickness(4, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        btnRemove.Click += (_, _) =>
        {
            _selectedAction?.Bindings.Remove(binding);
            _isDirty = true;
            RefreshBindingPane();
        };

        Grid.SetColumn(cbPlatform,  0);
        Grid.SetColumn(cbInputType, 1);
        Grid.SetColumn(cbValue,     2);
        Grid.SetColumn(btnRemove,   3);
        grid.Children.Add(cbPlatform);
        grid.Children.Add(cbInputType);
        grid.Children.Add(cbValue);
        grid.Children.Add(btnRemove);

        var bg = rowIdx % 2 == 0
            ? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))
            : new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));

        return new Border
        {
            Background      = bg,
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child           = grid,
        };
    }

    /// <summary>バインディング行用の 4 列 Grid を生成する。</summary>
    private static Grid BuildBindingGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        return grid;
    }

    /// <summary>入力種別 ComboBox の選択肢をプラットフォームに応じて設定する。</summary>
    private static void PopulateInputTypes(ComboBox cb, string platform, string currentInputType)
    {
        var types = GetInputTypes(platform);
        cb.Items.Clear();
        foreach (var t in types) cb.Items.Add(t);
        cb.SelectedItem = types.Contains(currentInputType) ? currentInputType : types.FirstOrDefault();
    }

    private static string[] GetInputTypes(string platform) => platform switch
    {
        "Mobile"           => MobInputTypes,
        "PS5" or "Switch"  => ConInputTypes,
        _                  => PcInputTypes,
    };

    // ── 入力値 ComboBox（検索付き）──────────────────────────

    /// <summary>
    /// 検索付き入力値 ComboBox を構築する。
    /// 選択肢は (platform, inputType) から決定し、テキスト入力でフィルタリングする。
    /// Mobile など候補がない場合は自由入力のみとなる。
    /// </summary>
    private ComboBox BuildValueComboBox(InputBinding binding)
    {
        var key       = (binding.Platform, binding.InputType);
        var allValues = InputValues.TryGetValue(key, out var vals) ? vals : Array.Empty<string>();

        var cb = new ComboBox
        {
            IsEditable          = true,
            IsTextSearchEnabled = false,
            StaysOpenOnEdit     = true,
            // 配色（暗背景・白文字・編集用テキスト欄含む）はアプリ共通の
            // ダークテーマ暗黙スタイル（App.xaml）に任せる
            FontSize            = 11,
            Height              = 24,
            Margin              = new Thickness(4, 2, 4, 2),
        };

        // 初期候補を追加してから Text をセット（Text セット後に TextChanged が発火しないよう順序を守る）
        foreach (var v in allValues) cb.Items.Add(v);
        cb.Text = binding.Value;

        // ── テキスト変更時: 候補をフィルタリングしてドロップダウン表示 ──
        cb.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            binding.Value = cb.Text;
            _isDirty = true;

            var text = cb.Text;
            var filtered = string.IsNullOrEmpty(text)
                ? allValues
                : allValues.Where(v => v.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();

            cb.Items.Clear();
            foreach (var v in filtered) cb.Items.Add(v);

            // フィルタ候補がある場合はドロップダウンを開く
            if (filtered.Length > 0 && !string.IsNullOrEmpty(text) && !cb.IsDropDownOpen)
                cb.IsDropDownOpen = true;
        }));

        // ── ドロップダウン開閉: 全候補を表示 ──
        cb.DropDownOpened += (_, _) =>
        {
            // テキストが空の場合は全候補を再表示する
            if (string.IsNullOrEmpty(cb.Text))
            {
                cb.Items.Clear();
                foreach (var v in allValues) cb.Items.Add(v);
            }
        };

        // ── 選択変更: binding へ反映 ──
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is string v)
            {
                binding.Value = v;
                _isDirty = true;
            }
        };

        return cb;
    }

    // ── ヘルパー ─────────────────────────────────────────────

    /// <summary>バインディング行用の非編集 ComboBox を生成する。</summary>
    private static ComboBox BuildDarkComboBox() => new()
    {
        Background      = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
        Foreground      = Brushes.Black,   // 選択値表示部は白地のため黒文字
        BorderBrush     = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
        BorderThickness = new Thickness(1),
        FontSize        = 11,
        Height          = 24,
        Margin          = new Thickness(4, 2, 4, 2),
    };

    private void OnAddBinding(object sender, RoutedEventArgs e)
    {
        if (_selectedAction is null) return;
        _selectedAction.Bindings.Add(new InputBinding());
        _isDirty = true;
        RefreshBindingPane();
    }

    // ── 保存 / キャンセル ────────────────────────────────────

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_filePath))
        {
            var dlg = new SaveFileDialog
            {
                Title      = "InputMap ファイルを保存",
                Filter     = "InputMap アセット|*.inputmap|すべてのファイル|*.*",
                DefaultExt = ".inputmap",
            };
            if (dlg.ShowDialog(this) != true) return;
            _filePath = dlg.FileName;
            TbFilePath.Text = _filePath;
        }

        _data.SaveTo(_filePath);
        _isDirty = false;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_isDirty)
        {
            var result = MessageBox.Show(
                "変更が保存されていません。閉じますか？",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }
        Close();
    }
}
