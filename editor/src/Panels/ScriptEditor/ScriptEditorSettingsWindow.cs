using System;
using System.Globalization;
using System.Threading.Tasks;
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

        // バックエンド選択（ローカル 1.5B / Groq クラウド）。
        // ComboBox はダークテーマだとドロップダウンが白地・白文字で読めないため、
        // 確実に読めるラジオボタンで選ばせる。
        var isGroq  = _settings.InlineCompletionBackend == Backend.Groq;
        var rbLocal = new RadioButton
        {
            Content = "ローカル (1.5B)", Foreground = Text, GroupName = "inlineBackend",
            IsChecked = !isGroq, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0),
        };
        var rbGroq = new RadioButton
        {
            Content = "Groq (クラウド)", Foreground = Text, GroupName = "inlineBackend",
            IsChecked = isGroq, VerticalAlignment = VerticalAlignment.Center,
        };
        var backendRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        backendRow.Children.Add(new TextBlock
        {
            Text = "バックエンド", Foreground = Text, Width = 120, VerticalAlignment = VerticalAlignment.Center,
        });
        backendRow.Children.Add(rbLocal);
        backendRow.Children.Add(rbGroq);
        root.Children.Add(backendRow);

        root.Children.Add(new TextBlock
        {
            Text = "ローカル: 初回約1.0GBのモデルDL。Groq: 低スペック機でも高速だがAPIキーが必要。",
            Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap,
        });

        // Groq APIキー
        var groqKeyBox = TextField("Groq APIキー", _settings.GroqApiKey, out var groqKeyGetter);
        root.Children.Add(groqKeyBox);

        // モデルは一覧取得してドロップダウンで選ぶ（一覧に無い名前も入力可）
        var groqModelCombo = MakeDarkComboBox(editable: true);
        groqModelCombo.Text = _settings.GroqModel;
        root.Children.Add(LabeledRow("Groq モデル", groqModelCombo));

        var fetchStatus = new TextBlock
        {
            Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        var fetchBtn = MakeButton("モデル一覧を取得");
        fetchBtn.Click += async (_, _) => await PopulateGroqModelsAsync(groqModelCombo, fetchStatus, groqKeyGetter);
        var fetchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(120, 0, 0, 2) };
        fetchRow.Children.Add(fetchBtn);
        fetchRow.Children.Add(fetchStatus);
        root.Children.Add(fetchRow);

        var groqLinkBtn = MakeButton("Groqでキーを取得/確認");
        groqLinkBtn.Click += (_, _) => OpenUrl("https://console.groq.com/keys");
        root.Children.Add(LabeledRow("", groqLinkBtn));

        // 既にキーがあれば開いた時点でモデル一覧を取得しておく
        if (!string.IsNullOrWhiteSpace(_settings.GroqApiKey))
            _ = PopulateGroqModelsAsync(groqModelCombo, fetchStatus, groqKeyGetter);

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
            _settings.InlineCompletionBackend = rbGroq.IsChecked == true ? Backend.Groq : Backend.Local;
            _settings.GroqApiKey = groqKeyGetter().Trim();
            var groqModel = (groqModelCombo.Text ?? "").Trim();
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

    /// <summary>ダークテーマで読める ComboBox を生成する（項目・編集欄を暗色化）。</summary>
    private static ComboBox MakeDarkComboBox(bool editable)
    {
        var combo = new ComboBox
        {
            IsEditable = editable, Foreground = Text, Background = FieldBg, BorderBrush = Border2,
            Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(3, 1, 3, 1),
        };

        // ドロップダウン項目を暗色にして「白地に白文字」を防ぐ
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, FieldBg));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 2, 4, 2)));
        combo.ItemContainerStyle = itemStyle;

        // 編集用テキストボックス部分も暗色に
        if (editable)
        {
            var tbStyle = new Style(typeof(TextBox));
            tbStyle.Setters.Add(new Setter(TextBox.ForegroundProperty, Text));
            tbStyle.Setters.Add(new Setter(TextBox.BackgroundProperty, FieldBg));
            tbStyle.Setters.Add(new Setter(TextBox.CaretBrushProperty, Text));
            combo.Resources.Add(typeof(TextBox), tbStyle);
        }
        return combo;
    }

    /// <summary>現在の API キーで Groq のモデル一覧を取得し、ドロップダウンへ反映する。</summary>
    private static async Task PopulateGroqModelsAsync(ComboBox combo, TextBlock status, Func<string> keyGetter)
    {
        var key = keyGetter().Trim();
        if (key.Length == 0) { status.Text = "APIキーを入力してください"; return; }

        status.Text = "取得中...";
        var models = await InlineCompletion.GroqInlineCompletionProvider.FetchModelsAsync(key);
        if (models.Count == 0) { status.Text = "取得できませんでした（キー/ネットワーク要確認）"; return; }

        var current = combo.Text;
        combo.Items.Clear();
        foreach (var m in models) combo.Items.Add(m);
        // 既存の選択（保存済みモデル）を尊重し、無ければ先頭を選ぶ
        combo.Text = string.IsNullOrWhiteSpace(current) ? models[0] : current;
        status.Text = $"{models.Count} 件取得";
    }

    /// <summary>既定ブラウザで URL を開く（失敗時はログのみ）。</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SEEDEditor.EditorLog.Write($"URL を開けませんでした ({url}): {ex.Message}");
        }
    }

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
