using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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

    // ── マスタ・ディテール（左ナビ）配色・寸法 ─────────────────
    /// <summary>左カテゴリ一覧の背景色（内容ペインより一段暗くして領域を分ける）。</summary>
    private static readonly Brush NavBg         = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x21));
    /// <summary>選択中カテゴリの強調背景色（VS のアクティブ項目風）。</summary>
    private static readonly Brush NavSelectedBg = new SolidColorBrush(Color.FromRgb(0x37, 0x37, 0x3D));
    /// <summary>左カテゴリ一覧の幅（px）。</summary>
    private const double NavWidth = 160;

    private readonly ScriptEditorSettings _settings;

    /// <summary>Groq モデル一覧の取得状況表示（自動取得と手動取得で共有）。</summary>
    private TextBlock? _groqFetchStatus;

    /// <summary>OK で保存されたときに発火する（更新後の設定）。</summary>
    public event Action<ScriptEditorSettings>? Applied;

    public ScriptEditorSettingsWindow(ScriptEditorSettings settings)
    {
        _settings = settings;

        Title       = "スクリプトエディタ設定";
        Width       = 640;
        Height      = 560;
        Background   = Bg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ── 各カテゴリの設定内容パネルを構築する ────────────────
        // レイアウトは「左：カテゴリ一覧／右：選択中カテゴリの内容／下：OK・キャンセル」の
        // マスタ・ディテール構成。内容パネルは一度だけ生成し、選択に応じて右ペインへ差し替える。
        // 各コントロールは所属パネルを親に持ち続けるため、非表示中でも入力値（getter）は保持される。
        var codeStylePanel = BuildCodeStylePanel(out var indentGetter, out var tabsCheck, out var fontGetter);
        var colorPanel     = BuildColorPanel(out var colorGetters);
        var aiPanel        = BuildAiPanel(
            out var inlineCheck, out var groqKeyGetter, out var groqModelCombo);

        // 右ペイン（内容表示先）。選択カテゴリのパネルをここへ差し込む。
        var detailHost = new ContentControl();
        var detailScroll = new ScrollViewer
        {
            Content = detailHost,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(16, 8, 16, 8),
        };

        // ── 左：カテゴリ一覧（ナビゲーション）────────────────────
        // カテゴリ表示名と対応する内容パネルの並び。データ駆動で追加・削除できるようにする。
        var categories = new (string Label, UIElement Panel)[]
        {
            ("コードスタイル", codeStylePanel),
            ("色設定",         colorPanel),
            ("AI 補完",        aiPanel),
        };

        var navPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var navButtons = new System.Collections.Generic.List<Button>();
        for (int i = 0; i < categories.Length; i++)
        {
            int index   = i;
            var navBtn  = MakeNavButton(categories[i].Label);
            navBtn.Click += (_, _) => SelectCategory(index);
            navButtons.Add(navBtn);
            navPanel.Children.Add(navBtn);
        }

        // カテゴリ選択：右ペインを差し替え、選択中ボタンだけをハイライトする。
        void SelectCategory(int index)
        {
            detailHost.Content = categories[index].Panel;
            for (int j = 0; j < navButtons.Count; j++)
                navButtons[j].Background = j == index ? NavSelectedBg : Brushes.Transparent;
        }

        // ── OK / キャンセル（右下）──────────────────────────────
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 12, 10),
        };
        var okBtn     = MakeButton("OK");
        var cancelBtn = MakeButton("キャンセル");
        okBtn.Click += (_, _) =>
        {
            _settings.IndentationSize        = Math.Clamp(indentGetter(), 1, 16);
            _settings.ConvertTabsToSpaces    = tabsCheck.IsChecked == true;
            _settings.FontSize               = Math.Clamp(fontGetter(), 8, 40);
            _settings.InlineCompletionEnabled = inlineCheck.IsChecked == true;
            _settings.GroqApiKey = groqKeyGetter().Trim();
            var groqModel = ((groqModelCombo.SelectedItem as string) ?? "").Trim();
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

        // ── 全体レイアウトを組み立てる ──────────────────────────
        // 上段：左ナビ（固定幅）＋境界線＋右内容（可変幅）、下段：ボタン行。
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var paneGrid = new Grid();
        paneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NavWidth) });
        paneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ナビと内容の間の境界線を、右内容ペインの左枠として描く。
        var navBorder = new Border { Background = NavBg, Child = navPanel };
        Grid.SetColumn(navBorder, 0);
        var detailBorder = new Border
        {
            BorderBrush = Border2, BorderThickness = new Thickness(1, 0, 0, 0), Child = detailScroll,
        };
        Grid.SetColumn(detailBorder, 1);
        paneGrid.Children.Add(navBorder);
        paneGrid.Children.Add(detailBorder);
        Grid.SetRow(paneGrid, 0);

        // ボタン行の上に区切り線を置く。
        var footer = new Border
        {
            BorderBrush = Border2, BorderThickness = new Thickness(0, 1, 0, 0), Child = btnRow,
        };
        Grid.SetRow(footer, 1);

        layout.Children.Add(paneGrid);
        layout.Children.Add(footer);
        Content = layout;

        // 既定で先頭カテゴリを選択表示する。
        SelectCategory(0);

        // 既にキーがあれば開いた時点で Groq モデル一覧を取得しておく（AI 補完パネル用）。
        if (!string.IsNullOrWhiteSpace(_settings.GroqApiKey))
            _ = PopulateGroqModelsAsync(groqModelCombo, _groqFetchStatus!, groqKeyGetter);
    }

    // ── カテゴリ内容パネルの構築 ──────────────────────────────

    /// <summary>「コードスタイル」カテゴリ（インデント・タブ・フォント）の内容パネルを構築する。</summary>
    private StackPanel BuildCodeStylePanel(out Func<int> indentGetter, out CheckBox tabsCheck, out Func<int> fontGetter)
    {
        var panel = new StackPanel();
        panel.Children.Add(Section("書式設定"));

        panel.Children.Add(NumberField("インデント幅", _settings.IndentationSize, out indentGetter));

        tabsCheck = new CheckBox
        {
            Content    = "タブを空白に変換する",
            IsChecked  = _settings.ConvertTabsToSpaces,
            Foreground = Text,
            Margin     = new Thickness(0, 6, 0, 6),
        };
        panel.Children.Add(tabsCheck);

        panel.Children.Add(NumberField("フォントサイズ", (int)_settings.FontSize, out fontGetter));
        return panel;
    }

    /// <summary>「色設定」カテゴリ（各構文要素の配色）の内容パネルを構築する。</summary>
    private StackPanel BuildColorPanel(out System.Collections.Generic.Dictionary<string, Func<string>> colorGetters)
    {
        var panel = new StackPanel();
        panel.Children.Add(Section("配色設定"));
        panel.Children.Add(new TextBlock
        {
            Text = "各構文要素の色。左のスウォッチをクリックでカラーピッカーを開く（直接 #RRGGBB 入力も可）。",
            Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap,
        });

        colorGetters = new System.Collections.Generic.Dictionary<string, Func<string>>();
        foreach (var (label, key) in ScriptEditorSettings.ColorEntries)
        {
            var current = _settings.Colors.TryGetValue(key, out var c) ? c : "#DCDCDC";
            panel.Children.Add(ColorRow(label, current, out var getter));
            colorGetters[key] = getter;
        }
        return panel;
    }

    /// <summary>「AI 補完」カテゴリ（インライン補完・Groq 設定）の内容パネルを構築する。</summary>
    private StackPanel BuildAiPanel(
        out CheckBox inlineCheck, out Func<string> groqKeyGetter, out ComboBox groqModelCombo)
    {
        var panel = new StackPanel();
        panel.Children.Add(Section("AI 補完"));

        inlineCheck = new CheckBox
        {
            Content    = "インライン補完を有効にする（予測 → Tab で確定）",
            IsChecked  = _settings.InlineCompletionEnabled,
            Foreground = Text,
            Margin     = new Thickness(0, 6, 0, 2),
        };
        panel.Children.Add(inlineCheck);

        panel.Children.Add(new TextBlock
        {
            Text = "Groq（クラウド・OpenAI 互換）で補完します。利用には API キーが必要です。"
                 + "コード補完には非推論モデル（例: llama-3.3-70b-versatile）を推奨。",
            Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 2, 0, 4), TextWrapping = TextWrapping.Wrap,
        });

        // Groq APIキー
        panel.Children.Add(TextField("Groq APIキー", _settings.GroqApiKey, out groqKeyGetter));
        var keyGetterLocal = groqKeyGetter;

        // モデルは一覧取得してドロップダウンから選ぶ（編集不可の選択専用）。
        // 一覧取得前でも保存済みモデルを表示できるよう、初期項目として入れて選択しておく。
        groqModelCombo = MakeDarkComboBox();
        if (!string.IsNullOrWhiteSpace(_settings.GroqModel))
        {
            groqModelCombo.Items.Add(_settings.GroqModel);
            groqModelCombo.SelectedIndex = 0;
        }
        panel.Children.Add(LabeledRow("Groq モデル", groqModelCombo));
        var comboLocal = groqModelCombo;

        // モデル一覧の取得状況表示（開いた時点の自動取得でも使うためフィールドに保持）。
        _groqFetchStatus = new TextBlock
        {
            Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        var fetchBtn = MakeButton("モデル一覧を取得");
        fetchBtn.Click += async (_, _) => await PopulateGroqModelsAsync(comboLocal, _groqFetchStatus, keyGetterLocal);
        var fetchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(120, 0, 0, 2) };
        fetchRow.Children.Add(fetchBtn);
        fetchRow.Children.Add(_groqFetchStatus);
        panel.Children.Add(fetchRow);

        var groqLinkBtn = MakeButton("Groqでキーを取得/確認");
        groqLinkBtn.Click += (_, _) => OpenUrl("https://console.groq.com/keys");
        panel.Children.Add(LabeledRow("", groqLinkBtn));

        return panel;
    }

    // ── ウィジェット生成 ──────────────────────────────────────

    private static TextBlock Section(string title) => new()
    {
        Text = title,
        Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0xAA, 0xFF)),
        FontSize = 13, FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 10, 0, 6),
    };

    /// <summary>
    /// ダークテーマで確実に読める「選択専用（編集不可）」ComboBox を生成する。
    ///
    /// 既定テンプレートは選択表示欄が OS テーマの明色（白）になり、白文字と重なって
    /// 読めないため、選択欄・ドロップダウンとも暗色にしたカスタムテンプレートを適用する。
    /// 入力は不要な用途（モデル選択など）を想定し、編集用テキスト欄は持たない。
    /// </summary>
    private static ComboBox MakeDarkComboBox()
    {
        var combo = new ComboBox
        {
            IsEditable = false, Foreground = Text, Background = FieldBg, BorderBrush = Border2,
            Width = 220, HorizontalAlignment = HorizontalAlignment.Left,
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(DarkComboTemplateXaml),
        };

        // ドロップダウン項目を暗色にして「白地に白文字」を防ぐ
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, FieldBg));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
        combo.ItemContainerStyle = itemStyle;
        return combo;
    }

    /// <summary>
    /// 暗色の選択専用 ComboBox 用テンプレート（XAML 文字列）。
    /// 選択表示欄・ドロップ矢印ボタン・候補ポップアップをすべて暗色で描く。
    /// 色はエディタの他 UI と揃える（背景 #1A1A1A / 枠 #3F3F46 / 文字 #DCDCDC）。
    /// </summary>
    private const string DarkComboTemplateXaml = @"
<ControlTemplate TargetType='ComboBox'
    xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Grid>
    <ToggleButton Name='ToggleButton' Focusable='False' ClickMode='Press'
        IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
      <ToggleButton.Template>
        <ControlTemplate TargetType='ToggleButton'>
          <Border Background='#1A1A1A' BorderBrush='#3F3F46' BorderThickness='1'>
            <Path HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,8,0'
                  Fill='#DCDCDC' Data='M 0 0 L 8 0 L 4 4 Z'/>
          </Border>
        </ControlTemplate>
      </ToggleButton.Template>
    </ToggleButton>
    <ContentPresenter Name='ContentSite' IsHitTestVisible='False'
        Content='{Binding SelectionBoxItem, RelativeSource={RelativeSource TemplatedParent}}'
        ContentTemplate='{Binding SelectionBoxItemTemplate, RelativeSource={RelativeSource TemplatedParent}}'
        Margin='7,0,25,0' VerticalAlignment='Center' HorizontalAlignment='Left'
        TextElement.Foreground='#DCDCDC'/>
    <Popup Name='Popup' Placement='Bottom' Focusable='False' AllowsTransparency='True'
        IsOpen='{TemplateBinding IsDropDownOpen}' PopupAnimation='Slide'>
      <Grid Name='DropDown' SnapsToDevicePixels='True'
          MinWidth='{TemplateBinding ActualWidth}' MaxHeight='{TemplateBinding MaxDropDownHeight}'>
        <Border Background='#1A1A1A' BorderBrush='#3F3F46' BorderThickness='1'/>
        <ScrollViewer>
          <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Contained'/>
        </ScrollViewer>
      </Grid>
    </Popup>
  </Grid>
</ControlTemplate>";

    /// <summary>現在の API キーで Groq のモデル一覧を取得し、ドロップダウンへ反映する。</summary>
    private static async Task PopulateGroqModelsAsync(ComboBox combo, TextBlock status, Func<string> keyGetter)
    {
        var key = keyGetter().Trim();
        if (key.Length == 0) { status.Text = "APIキーを入力してください"; return; }

        status.Text = "取得中...";
        var models = await InlineCompletion.GroqInlineCompletionProvider.FetchModelsAsync(key);
        if (models.Count == 0) { status.Text = "取得できませんでした（キー/ネットワーク要確認）"; return; }

        var current = combo.SelectedItem as string;
        combo.Items.Clear();
        foreach (var m in models) combo.Items.Add(m);
        // 既存の選択（保存済みモデル）を尊重し、一覧に無ければ先頭に足して選ぶ
        var want = string.IsNullOrWhiteSpace(current) ? models[0] : current;
        if (!combo.Items.Contains(want)) combo.Items.Insert(0, want);
        combo.SelectedItem = want;
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
        Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(DarkButtonTemplateXaml),
    };

    /// <summary>
    /// 左カテゴリ一覧の項目ボタンを生成する。
    /// 枠なし・左寄せ・全幅で、選択状態は呼び出し側が Background を切り替えて表現する。
    /// </summary>
    private static Button MakeNavButton(string label) => new()
    {
        Content = label,
        Foreground = Text,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        HorizontalAlignment        = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        Padding = new Thickness(14, 8, 8, 8),
        Cursor  = System.Windows.Input.Cursors.Hand,
        FontSize = 13,
        Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(DarkButtonTemplateXaml),
    };

    /// <summary>
    /// ダークテーマ用ボタンテンプレート（XAML 文字列）。
    /// 既定テンプレートはホバー/押下時に OS テーマの明色背景になり、明るい文字が
    /// 埋もれて見えなくなる。ここでは全状態で背景を暗色に保ち、文字色（Foreground）は
    /// そのまま用いて可視性を確保する。
    /// 背景は通常＝Background（選択・通常色をそのまま反映）、ホバー＝#3E3E42、押下＝#2A2A2E。
    /// </summary>
    private const string DarkButtonTemplateXaml = @"
<ControlTemplate TargetType='Button'
    xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Border x:Name='bd' SnapsToDevicePixels='True'
      Background='{TemplateBinding Background}'
      BorderBrush='{TemplateBinding BorderBrush}'
      BorderThickness='{TemplateBinding BorderThickness}'>
    <ContentPresenter Margin='{TemplateBinding Padding}'
        HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'
        VerticalAlignment='Center'
        TextElement.Foreground='{TemplateBinding Foreground}'/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'>
      <Setter TargetName='bd' Property='Background' Value='#3E3E42'/>
    </Trigger>
    <Trigger Property='IsPressed' Value='True'>
      <Setter TargetName='bd' Property='Background' Value='#2A2A2E'/>
    </Trigger>
    <Trigger Property='IsEnabled' Value='False'>
      <Setter Property='Opacity' Value='0.5'/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>";

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
