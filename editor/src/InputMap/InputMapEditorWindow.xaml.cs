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
    //  ランタイムは PC プラットフォームのみ評価するため、UI もプラットフォーム選択を
    //  廃止し、生成するバインディングは常に Platform="PC" とする。
    //  入力種別は Key / GamepadButton / GamepadAxis の 3 種。値の一覧は
    //  Rust 側 action_map / gamepad の対応表と 1:1 で一致させる（ここが正典）。

    /// <summary>入力種別（Key / GamepadButton / GamepadAxis）。</summary>
    private static readonly string[] InputTypes = ["Key", "GamepadButton", "GamepadAxis"];

    private const string TypeKey  = "Key";
    private const string TypeBtn  = "GamepadButton";
    private const string TypeAxis = "GamepadAxis";
    private const string PlatformPc = "PC";

    /// <summary>Key の選択肢（Rust 側 key_from_name と一致）。</summary>
    private static readonly string[] KeyValues = [
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
    ];

    /// <summary>GamepadButton の選択肢（Rust 側 PadButton::from_name と一致）。</summary>
    private static readonly string[] GamepadButtons = [
        "South","East","West","North",
        "DPadUp","DPadDown","DPadLeft","DPadRight",
        "LeftShoulder","RightShoulder",
        "LeftStickPress","RightStickPress",
        "Start","Select",
    ];

    /// <summary>GamepadAxis の選択肢（Rust 側 PadAxis::from_name と一致）。</summary>
    private static readonly string[] GamepadAxes = [
        "LeftStickX","LeftStickY","RightStickX","RightStickY",
        "LeftTrigger","RightTrigger",
    ];

    /// <summary>入力種別ごとの値選択肢を返す。未知種別は空。</summary>
    private static string[] ValuesForType(string inputType) => inputType switch
    {
        TypeKey  => KeyValues,
        TypeBtn  => GamepadButtons,
        TypeAxis => GamepadAxes,
        _        => Array.Empty<string>(),
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
            // 型に応じてバインディング編集レイアウトを切り替える。
            RefreshBindingPane();
        }
    }

    // ── バインディング編集ペイン ─────────────────────────────

    /// <summary>
    /// 右ペインを再構築する（アクション未選択時は非表示）。
    /// value_type に応じてバインディング編集レイアウトを切り替える:
    ///   Bool   → 条件コンボ + フラットなバインド一覧。
    ///   Axis1D → 正バインド / 負バインドの 2 グループ。
    ///   Axis2D → 正規化チェック + 軸X(正/負) + 軸Y(正/負)。
    /// </summary>
    private void RefreshBindingPane()
    {
        BindingStack.Children.Clear();
        // 追加ボタンは各グループ内にインライン配置するため、静的フッターは常に非表示。
        BindingFooter.Visibility = Visibility.Collapsed;

        if (_selectedAction is null)
        {
            ActionHeader.Visibility = Visibility.Collapsed;
            return;
        }

        ActionHeader.Visibility = Visibility.Visible;

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

        // 値型に応じてレイアウトを構築する。
        switch (_selectedAction.ValueType)
        {
            case ActionValueType.Bool:
                BindingStack.Children.Add(BuildConditionRow(_selectedAction));
                BindingStack.Children.Add(BuildBindingGroup("バインド", _selectedAction.EnsureBindings()));
                break;

            case ActionValueType.Axis1D:
                BindingStack.Children.Add(BuildBindingGroup("正バインド", _selectedAction.EnsurePositive()));
                BindingStack.Children.Add(BuildBindingGroup("負バインド", _selectedAction.EnsureNegative()));
                break;

            case ActionValueType.Axis2D:
                BindingStack.Children.Add(BuildNormalizeRow(_selectedAction));
                BindingStack.Children.Add(BuildAxisHeader("軸 X"));
                BindingStack.Children.Add(BuildBindingGroup("正バインド", _selectedAction.EnsureX().EnsurePositive(), indent: true));
                BindingStack.Children.Add(BuildBindingGroup("負バインド", _selectedAction.EnsureX().EnsureNegative(), indent: true));
                BindingStack.Children.Add(BuildAxisHeader("軸 Y"));
                BindingStack.Children.Add(BuildBindingGroup("正バインド", _selectedAction.EnsureY().EnsurePositive(), indent: true));
                BindingStack.Children.Add(BuildBindingGroup("負バインド", _selectedAction.EnsureY().EnsureNegative(), indent: true));
                break;
        }
    }

    // ── 見出し・条件・正規化 UI ───────────────────────────────

    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Brush AxisHeaderBrush = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));

    /// <summary>Bool アクションの条件（Trigger/Press/Release）コンボ行を構築する。</summary>
    private UIElement BuildConditionRow(InputAction action)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 8, 6) };
        panel.Children.Add(new TextBlock
        {
            Text = "条件",
            Foreground = LabelBrush,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });

        var cb = BuildCombo(editable: false);
        cb.Width = 180;
        foreach (var name in Enum.GetNames<ActionCondition>()) cb.Items.Add(name);
        cb.SelectedItem = (action.Condition ?? ActionCondition.Press).ToString();
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is string s && Enum.TryParse<ActionCondition>(s, out var c))
            {
                action.Condition = c;
                _isDirty = true;
            }
        };
        panel.Children.Add(cb);
        return panel;
    }

    /// <summary>Axis2D の「正規化するか」チェック行を構築する。</summary>
    private UIElement BuildNormalizeRow(InputAction action)
    {
        var chk = new CheckBox
        {
            Content = "正規化する（長さ>1 のとき単位長へ。斜め入力が 0.707 になる）",
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            IsChecked = action.Normalize ?? false,
            Margin = new Thickness(12, 10, 8, 6),
            FontSize = 11,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        chk.Checked   += (_, _) => { action.Normalize = true;  _isDirty = true; };
        chk.Unchecked += (_, _) => { action.Normalize = false; _isDirty = true; };
        return chk;
    }

    /// <summary>「軸 X」「軸 Y」の見出しを構築する。</summary>
    private static UIElement BuildAxisHeader(string text) => new Border
    {
        Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
        Padding = new Thickness(12, 6, 8, 6),
        Margin = new Thickness(0, 4, 0, 0),
        Child = new TextBlock
        {
            Text = text,
            Foreground = AxisHeaderBrush,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
        },
    };

    // ── バインドグループ（見出し + 行 + 追加ボタン）───────────

    /// <summary>
    /// 1 グループ（見出し + バインド行 + 「バインドを追加」ボタン）の UI を構築する。
    /// list は編集対象の実体リスト（追加/削除がそのまま反映される）。
    /// </summary>
    private UIElement BuildBindingGroup(string title, List<InputBinding> list, bool indent = false)
    {
        var outer = new StackPanel { Margin = new Thickness(indent ? 24 : 12, 6, 8, 4) };

        // グループ見出し
        outer.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = LabelBrush,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 2),
        });

        // 各バインド行
        for (int i = 0; i < list.Count; i++)
            outer.Children.Add(BuildBindingRow(list[i], list));

        // 追加ボタン
        var add = new Button
        {
            Content = "+ バインドを追加",
            Style = (Style)FindResource("ActionBtn"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 0),
        };
        add.Click += (_, _) =>
        {
            list.Add(new InputBinding { Platform = PlatformPc, InputType = TypeKey, Value = "" });
            _isDirty = true;
            RefreshBindingPane();
        };
        outer.Children.Add(add);
        return outer;
    }

    /// <summary>
    /// バインド 1 行分の UI を構築する。
    /// 行 = [入力種別コンボ][入力値コンボ][deadZone 数値(アナログのみ)][削除]。
    /// owningList は削除時にこの binding を取り除く対象リスト。
    /// </summary>
    private UIElement BuildBindingRow(InputBinding binding, List<InputBinding> owningList)
    {
        // ランタイムは PC のみ評価。UI 生成分は常に PC に固定する。
        binding.Platform = PlatformPc;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });       // 入力種別
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 入力値
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });        // deadZone
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });        // 削除

        // ── 入力種別 ComboBox ───────────────────────────────
        var cbType = BuildCombo(editable: false);
        foreach (var t in InputTypes) cbType.Items.Add(t);
        cbType.SelectedItem = InputTypes.Contains(binding.InputType) ? binding.InputType : InputTypes[0];

        // ── 入力値 ComboBox（検索付き）─────────────────────
        var cbValue = BuildValueComboBox(binding);

        // ── deadZone 入力欄（GamepadAxis のときだけ表示）────
        var tbDead = new TextBox
        {
            Text = binding.DeadZone?.ToString("0.###") ?? "",
            FontSize = 11,
            Height = 24,
            Margin = new Thickness(4, 2, 4, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC)),
            ToolTip = "デッドゾーン（0〜1・省略時 0.2）",
            Visibility = binding.InputType == TypeAxis ? Visibility.Visible : Visibility.Collapsed,
        };
        tbDead.TextChanged += (_, _) =>
        {
            var s = tbDead.Text.Trim();
            if (string.IsNullOrEmpty(s)) { binding.DeadZone = null; _isDirty = true; return; }
            if (float.TryParse(s, out var f)) { binding.DeadZone = f; _isDirty = true; }
        };

        // ── 入力種別変更: 値候補と deadZone 表示を切り替えるため再描画 ──
        cbType.SelectionChanged += (_, _) =>
        {
            var t = cbType.SelectedItem?.ToString() ?? TypeKey;
            if (binding.InputType == t) return;
            binding.InputType = t;
            // 種別が変わると値候補が変わるので値をクリアして選び直しやすくする。
            binding.Value = "";
            // GamepadAxis 以外に切り替えたら deadZone は無意味なのでクリアする。
            if (t != TypeAxis) binding.DeadZone = null;
            _isDirty = true;
            Dispatcher.BeginInvoke(new Action(RefreshBindingPane));
        };

        // ── 削除ボタン ───────────────────────────────────────
        var btnRemove = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 24,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0x44, 0x44)),
            BorderThickness = new Thickness(0),
            FontSize = 11,
            Cursor = Cursors.Hand,
            Margin = new Thickness(4, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        btnRemove.Click += (_, _) =>
        {
            owningList.Remove(binding);
            _isDirty = true;
            RefreshBindingPane();
        };

        Grid.SetColumn(cbType, 0);
        Grid.SetColumn(cbValue, 1);
        Grid.SetColumn(tbDead, 2);
        Grid.SetColumn(btnRemove, 3);
        grid.Children.Add(cbType);
        grid.Children.Add(cbValue);
        grid.Children.Add(tbDead);
        grid.Children.Add(btnRemove);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Margin = new Thickness(0, 1, 0, 1),
            Child = grid,
        };
    }

    // ── 入力値 ComboBox（検索付き）──────────────────────────

    /// <summary>
    /// 検索付き入力値 ComboBox を構築する。
    /// 選択肢は入力種別（Key/GamepadButton/GamepadAxis）から決定し、テキスト入力でフィルタする。
    /// </summary>
    private ComboBox BuildValueComboBox(InputBinding binding)
    {
        var allValues = ValuesForType(binding.InputType);

        var cb = BuildCombo(editable: true);

        // 初期候補を追加してから Text をセット（順序を守る）
        foreach (var v in allValues) cb.Items.Add(v);
        cb.Text = binding.Value;

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

            if (filtered.Length > 0 && !string.IsNullOrEmpty(text) && !cb.IsDropDownOpen)
                cb.IsDropDownOpen = true;
        }));

        cb.DropDownOpened += (_, _) =>
        {
            if (string.IsNullOrEmpty(cb.Text))
            {
                cb.Items.Clear();
                foreach (var v in allValues) cb.Items.Add(v);
            }
        };

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

    /// <summary>
    /// ダークテーマ ComboBox を生成する。
    /// 配色（黒地・白文字。ドロップダウン Popup 内も含む）は App.xaml の暗黙 ComboBox
    /// スタイルに委ねる（ローカルで色を上書きしない）。以前はローカルで Foreground=Black を
    /// 指定していたため「灰色地に黒文字」で読めなかった。それを撤廃したのが本修正。
    /// </summary>
    private static ComboBox BuildCombo(bool editable) => new()
    {
        IsEditable = editable,
        IsTextSearchEnabled = false,
        StaysOpenOnEdit = editable,
        FontSize = 11,
        Height = 24,
        Margin = new Thickness(4, 2, 4, 2),
    };

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
