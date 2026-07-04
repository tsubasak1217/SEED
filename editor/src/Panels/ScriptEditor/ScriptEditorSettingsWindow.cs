using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Backend = SEEDEditor.Panels.ScriptEditor.InlineCompletion.RoutingInlineCompletionProvider.Backend;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// スクリプトエディタの書式・配色を編集するダイアログ。
/// OK で設定を保存し、Applied イベントで呼び出し側（パネル）に反映を促す。
/// </summary>
public sealed class ScriptEditorSettingsWindow : Window
{
    private static readonly Brush Bg     = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
    private static readonly Brush FieldBg= new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly Brush Text   = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC));
    private static readonly Brush Border2= new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly Brush Dim    = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));

    private readonly ScriptEditorSettings _settings;

    /// <summary>OK で保存されたときに発火する（更新後の設定）。</summary>
    public event Action<ScriptEditorSettings>? Applied;

    public ScriptEditorSettingsWindow(ScriptEditorSettings settings)
    {
        _settings = settings;

        Title       = "スクリプトエディタ設定";
        Width       = 420;
        Height      = 560;
        Background   = Bg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(16) };

        // ── 書式設定 ──
        root.Children.Add(Section("書式設定"));

        var indentBox = NumberField("インデント幅", _settings.IndentationSize, out var indentGetter);
        root.Children.Add(indentBox);

        var tabsCheck = new CheckBox
        {
            Content    = "タブを空白に変換する",
            IsChecked  = _settings.ConvertTabsToSpaces,
            Foreground = Text,
            Margin     = new Thickness(0, 6, 0, 6),
        };
        root.Children.Add(tabsCheck);

        var fontBox = NumberField("フォントサイズ", (int)_settings.FontSize, out var fontGetter);
        root.Children.Add(fontBox);

        // ── AI 補完 ──
        root.Children.Add(Section("AI 補完"));
        var inlineCheck = new CheckBox
        {
            Content    = "インライン補完を有効にする（予測 → Tab で確定）",
            IsChecked  = _settings.InlineCompletionEnabled,
            Foreground = Text,
            Margin     = new Thickness(0, 6, 0, 2),
        };
        root.Children.Add(inlineCheck);

        // バックエンド選択（ローカル 1.5B / Groq クラウド）
        var backendCombo = new ComboBox
        {
            Foreground = Text, Background = FieldBg, BorderBrush = Border2,
            Margin = new Thickness(0, 2, 0, 2), Width = 220, HorizontalAlignment = HorizontalAlignment.Left,
        };
        backendCombo.Items.Add("ローカル (Qwen2.5-Coder 1.5B)");
        backendCombo.Items.Add("Groq (クラウド・OpenAI互換)");
        backendCombo.SelectedIndex =
            _settings.InlineCompletionBackend == Backend.Groq ? 1 : 0;
        root.Children.Add(LabeledRow("バックエンド", backendCombo));

        root.Children.Add(new TextBlock
        {
            Text = "ローカル: 初回約1.0GBのモデルDL。Groq: 低スペック機でも高速だがAPIキーが必要（console.groq.com）。",
            Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap,
        });

        // Groq 用の設定（API キー・モデル名）
        var groqKeyBox   = TextField("Groq APIキー", _settings.GroqApiKey, out var groqKeyGetter);
        var groqModelBox = TextField("Groq モデル", _settings.GroqModel, out var groqModelGetter);
        root.Children.Add(groqKeyBox);
        root.Children.Add(groqModelBox);

        // ── 配色設定 ──
        root.Children.Add(Section("配色設定"));
        root.Children.Add(new TextBlock
        {
            Text = "各構文要素の色。左のスウォッチをクリックでカラーピッカーを開く（直接 #RRGGBB 入力も可）。",
            Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap,
        });

        var colorGetters = new System.Collections.Generic.Dictionary<string, Func<string>>();
        foreach (var (label, key) in ScriptEditorSettings.ColorEntries)
        {
            var current = _settings.Colors.TryGetValue(key, out var c) ? c : "#DCDCDC";
            root.Children.Add(ColorRow(label, current, out var getter));
            colorGetters[key] = getter;
        }

        // ── ボタン ──
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var okBtn     = MakeButton("OK");
        var cancelBtn = MakeButton("キャンセル");
        okBtn.Click += (_, _) =>
        {
            _settings.IndentationSize        = Math.Clamp(indentGetter(), 1, 16);
            _settings.ConvertTabsToSpaces    = tabsCheck.IsChecked == true;
            _settings.FontSize               = Math.Clamp(fontGetter(), 8, 40);
            _settings.InlineCompletionEnabled = inlineCheck.IsChecked == true;
            _settings.InlineCompletionBackend = backendCombo.SelectedIndex == 1 ? Backend.Groq : Backend.Local;
            _settings.GroqApiKey = groqKeyGetter().Trim();
            var groqModel = groqModelGetter().Trim();
            if (groqModel.Length > 0) _settings.GroqModel = groqModel;
            foreach (var (_, key) in ScriptEditorSettings.ColorEntries)
                _settings.Colors[key] = NormalizeHex(colorGetters[key](), _settings.Colors[key]);
            Applied?.Invoke(_settings);
            DialogResult = true;
            Close();
        };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        root.Children.Add(btnRow);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ── ウィジェット生成 ──────────────────────────────────────

    private static TextBlock Section(string title) => new()
    {
        Text = title,
        Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0xAA, 0xFF)),
        FontSize = 13, FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 10, 0, 6),
    };

    /// <summary>ラベル付きの 1 行テキスト入力を生成する。</summary>
    private static UIElement TextField(string label, string value, out Func<string> getter)
    {
        var tb = new TextBox
        {
            Text = value ?? "", Width = 220, Background = FieldBg, Foreground = Text, CaretBrush = Text,
            BorderBrush = Border2, BorderThickness = new Thickness(1), Padding = new Thickness(3, 1, 3, 1),
        };
        getter = () => tb.Text ?? "";
        return LabeledRow(label, tb);
    }

    /// <summary>ラベル＋コントロールを横並びにした 1 行を生成する。</summary>
    private static UIElement LabeledRow(string label, UIElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock
        {
            Text = label, Foreground = Text, Width = 120, VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(control);
        return row;
    }

    private static UIElement NumberField(string label, int value, out Func<int> getter)
    {
        var tb = new TextBox
        {
            Text = value.ToString(CultureInfo.InvariantCulture),
            Width = 60, Background = FieldBg, Foreground = Text, CaretBrush = Text,
            BorderBrush = Border2, BorderThickness = new Thickness(1),
            Padding = new Thickness(3, 1, 3, 1),
        };
        getter = () => int.TryParse(tb.Text, out var v) ? v : value;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock { Text = label, Foreground = Text, Width = 140, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(tb);
        return row;
    }

    private UIElement ColorRow(string label, string hex, out Func<string> getter)
    {
        var preview = new Border
        {
            Width = 22, Height = 22, BorderBrush = Border2, BorderThickness = new Thickness(1),
            Background = HexToBrush(hex, Colors.Gray), Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "クリックでカラーピッカーを開く",
        };
        var tb = new TextBox
        {
            Text = hex, Width = 90, Background = FieldBg, Foreground = Text, CaretBrush = Text,
            BorderBrush = Border2, BorderThickness = new Thickness(1), Padding = new Thickness(3, 1, 3, 1),
        };
        // テキスト変更でプレビューを追従させる
        tb.TextChanged += (_, _) => preview.Background = HexToBrush(tb.Text, ((SolidColorBrush)preview.Background).Color);
        // スウォッチをクリックしたらカラーピッカーを開き、選んだ色をテキストへ反映する
        preview.MouseLeftButtonDown += (_, _) =>
        {
            var current = ((SolidColorBrush)preview.Background).Color;
            var picked = ColorPickerWindow.ShowDialogSrgb(this, current);
            if (picked is { } c)
                tb.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; // TextChanged がプレビューへ反映
        };
        getter = () => tb.Text;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Foreground = Text, Width = 120, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(preview);
        row.Children.Add(tb);
        return row;
    }

    private static Button MakeButton(string content) => new()
    {
        Content = content, MinWidth = 88, Margin = new Thickness(6, 0, 0, 0),
        Padding = new Thickness(8, 3, 8, 3), Foreground = Text, Background = FieldBg, BorderBrush = Border2,
    };

    private static Brush HexToBrush(string hex, Color fallback)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(fallback); }
    }

    /// <summary>不正な色文字列は従来値を維持する。</summary>
    private static string NormalizeHex(string input, string fallback)
    {
        try { _ = (Color)ColorConverter.ConvertFromString(input); return input; }
        catch { return fallback; }
    }
}
