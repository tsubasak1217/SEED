// ============================================================
//  SeedDialogTheme.cs — コードで組む小さなモーダルの見た目を 1 か所へ
//
//  【役割】
//  配色・寸法・部品（ラベル / 入力欄 / ボタン）の作り方をまとめる。
//  XAML を持たない小さなダイアログ（アカウント関連・テキスト入力・音声トリムなど）が
//  それぞれ自前で色と余白を書くと、「1 つだけ背景色が違う」ダイアログが必ず生まれる。
//
//  【このファイルが統合したもの】
//  以前は同じ値が 3 か所に複製されていた:
//    - Accounts/Views/AccountDialogTheme.cs（アカウント系 4 ダイアログ）
//    - Dialogs/TextInputWindow.cs（private な色定数 5 個）
//    - Dialogs/AudioSilenceTrimWindow.cs（private な色定数 5 個。補足色だけ値がずれていた）
//  色の出所は Theme/SeedColorTable.cs、ボタンの見た目は Theme/SeedButtonStyles.xaml に一本化した。
//
//  【XAML を使わない理由】
//  入力欄とボタンが数個の画面は、XAML と分けると 2 ファイルを往復することになる。
//  加えて XAML の StaticResource は綴りを間違えてもビルドが通るため、
//  起動しないと分からない壊れ方をする。コードで組めばコンパイラが見てくれる。
//
//  【ボタンの色をここに持たない理由】
//  ボタンは Application.Current のリソース（SeedButtonStyles.xaml）が持つ暗黙スタイル・
//  名前付きスタイルで決まる。ここで色を指定すると二重管理が復活する。
// ============================================================

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SEEDEditor.Theme;

/// <summary>
/// コードで組むモーダルダイアログの配色・寸法・部品。
/// </summary>
public static class SeedDialogTheme
{
    // ── 配色（実体は SeedColorTable）──────────────────────────

    /// <summary>色表の色から凍結済みブラシを作る。</summary>
    /// <param name="color">元になる色。</param>
    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>ダイアログの背景。</summary>
    public static readonly Brush Background = Frozen(SeedThemeColors.DialogSurface);

    /// <summary>入力欄の背景。</summary>
    public static readonly Brush Field = Frozen(SeedThemeColors.FieldBg);

    /// <summary>本文の文字色。</summary>
    public static readonly Brush Text = Frozen(SeedThemeColors.DialogText);

    /// <summary>補足の文字色。</summary>
    public static readonly Brush DimText = Frozen(SeedThemeColors.DialogDimText);

    /// <summary>成功・肯定の文字色。</summary>
    public static readonly Brush SuccessText = Frozen(SeedThemeColors.DialogSuccessText);

    /// <summary>エラーの文字色。</summary>
    public static readonly Brush ErrorText = Frozen(SeedThemeColors.DialogErrorText);

    /// <summary>枠線の色。</summary>
    public static readonly Brush FieldBorder = Frozen(SeedThemeColors.FieldBorder);

    /// <summary>一覧の選択行の背景。</summary>
    public static readonly Brush ListSelectionBg = Frozen(SeedThemeColors.DialogListSelectionBg);

    /// <summary>一覧のホバー行の背景。</summary>
    public static readonly Brush ListHoverBg = Frozen(SeedThemeColors.DialogListHoverBg);

    // ── 寸法 ────────────────────────────────────────────────

    /// <summary>外周の余白 [px]。</summary>
    public const double CONTENT_PADDING_PX = 14;

    /// <summary>行間の余白 [px]。</summary>
    public const double ROW_SPACING_PX = 10;

    /// <summary>入力欄の内側余白 [px]。</summary>
    public const double FIELD_PADDING_PX = 5;

    /// <summary>ボタン同士の間隔 [px]。ボタン自体の寸法は共通スタイルが決める。</summary>
    public const double BUTTON_GAP_PX = SeedButtonMetrics.DIALOG_BUTTON_GAP_PX;

    /// <summary>本文の文字サイズ。</summary>
    public const double BODY_FONT_SIZE = 12;

    /// <summary>補足の文字サイズ。</summary>
    public const double NOTE_FONT_SIZE = 11;

    /// <summary>枠線の太さ [px]。</summary>
    public const double BORDER_THICKNESS_PX = 1;

    /// <summary>一覧の 1 行の左右の内側余白 [px]。</summary>
    private const double LIST_ITEM_PADDING_X_PX = 6;

    /// <summary>一覧の 1 行の上下の内側余白 [px]。</summary>
    private const double LIST_ITEM_PADDING_Y_PX = 3;

    // ── 部品 ────────────────────────────────────────────────

    /// <summary>
    /// 本文のテキストブロックを作る。
    /// </summary>
    /// <param name="text">文言。</param>
    /// <param name="brush">文字色（省略時は本文色）。</param>
    /// <param name="fontSize">文字サイズ（省略時は本文サイズ）。</param>
    /// <param name="topMargin">上の余白 [px]。</param>
    public static TextBlock NewLabel(
        string text, Brush? brush = null, double fontSize = BODY_FONT_SIZE,
        double topMargin = 0)
        => new()
        {
            Text         = text,
            Foreground   = brush ?? Text,
            FontSize     = fontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, topMargin, 0, 0),
        };

    /// <summary>
    /// 1 行入力欄を作る。
    /// </summary>
    /// <param name="initialText">初期値。</param>
    /// <param name="topMargin">上の余白 [px]。</param>
    public static TextBox NewTextBox(string initialText = "", double topMargin = 0)
        => new()
        {
            Text            = initialText,
            Background      = Field,
            Foreground      = Text,
            BorderBrush     = FieldBorder,
            BorderThickness = new Thickness(BORDER_THICKNESS_PX),
            Padding         = new Thickness(FIELD_PADDING_PX),
            FontSize        = BODY_FONT_SIZE,
            Margin          = new Thickness(0, topMargin, 0, 0),
        };

    /// <summary>
    /// パスフレーズ入力欄を作る（伏せ字）。
    /// </summary>
    /// <param name="topMargin">上の余白 [px]。</param>
    public static PasswordBox NewPasswordBox(double topMargin = 0)
        => new()
        {
            Background      = Field,
            Foreground      = Text,
            BorderBrush     = FieldBorder,
            BorderThickness = new Thickness(BORDER_THICKNESS_PX),
            Padding         = new Thickness(FIELD_PADDING_PX),
            FontSize        = BODY_FONT_SIZE,
            Margin          = new Thickness(0, topMargin, 0, 0),
        };

    /// <summary>
    /// 選択肢を 1 つ選ばせる一覧を作る。
    ///
    /// <para>
    /// ★行の見た目（選択・ホバー）を **WPF 既定に任せない**。
    /// 既定の <see cref="ListBoxItem"/> はフォーカスが外れた選択行を明るい灰色で塗り、
    /// 暗いダイアログで継いだ明るい文字色と重なって**選択した行だけ読めなくなる**。
    /// ボタンのホバー色を共通書式が置き換えているのと同じ理由。
    /// </para>
    /// </summary>
    /// <param name="items">並べる文字列（各行の <c>Tag</c> にも同じ値を入れる）。</param>
    /// <param name="height">一覧の高さ [px]。</param>
    /// <param name="topMargin">上の余白 [px]。</param>
    public static ListBox NewListBox(
        IEnumerable<string> items, double height, double topMargin = 0)
    {
        var list = new ListBox
        {
            Background         = Field,
            Foreground         = Text,
            BorderBrush        = FieldBorder,
            BorderThickness    = new Thickness(BORDER_THICKNESS_PX),
            FontSize           = BODY_FONT_SIZE,
            Height             = height,
            Margin             = new Thickness(0, topMargin, 0, 0),
            ItemContainerStyle = NewListItemStyle(),
        };

        foreach (var text in items)
        {
            list.Items.Add(new ListBoxItem { Content = text, Tag = text });
        }

        return list;
    }

    /// <summary>
    /// 一覧の 1 行の見た目（通常・ホバー・選択）を作る。
    /// 文字色は一覧から継ぐので、ここでは背景だけを決める。
    /// </summary>
    private static Style NewListItemStyle()
    {
        var template = new ControlTemplate(typeof(ListBoxItem));

        // Border 1 枚 + 中身。既定テンプレートを丸ごと置き換える。
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(
            Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new Thickness(
            LIST_ITEM_PADDING_X_PX, LIST_ITEM_PADDING_Y_PX,
            LIST_ITEM_PADDING_X_PX, LIST_ITEM_PADDING_Y_PX));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        template.VisualTree = border;

        // ホバーより選択を後に置く（同時に成立したときは選択色を採る）。
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, ListHoverBg, "Bd"));
        template.Triggers.Add(hover);

        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, ListSelectionBg, "Bd"));
        template.Triggers.Add(selected);

        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(
            Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    /// <summary>
    /// ダイアログのボタンを 1 つ作る。
    /// 色・余白・ホバーの挙動はすべて共通スタイル（SeedButtonStyles.xaml）が決める。
    /// </summary>
    /// <param name="text">文言。</param>
    /// <param name="onClick">押されたときの処理。</param>
    /// <param name="isPrimary">主操作か（アクセント色にする）。</param>
    /// <param name="leftMargin">左の余白 [px]。ボタン列の 2 個目以降に付ける。</param>
    public static Button NewButton(
        string text, RoutedEventHandler onClick, bool isPrimary = false,
        double leftMargin = 0)
    {
        var button = new Button
        {
            Content  = text,
            FontSize = BODY_FONT_SIZE,
            Margin   = new Thickness(leftMargin, 0, 0, 0),
        };
        SeedButtonStyle.Apply(
            button,
            isPrimary ? SeedButtonStyle.DIALOG_PRIMARY : SeedButtonStyle.DIALOG);
        button.Click += onClick;
        return button;
    }

    /// <summary>
    /// 既に作ってあるボタンへダイアログ用の共通スタイルを当てる
    /// （XAML 由来のボタンや、生成箇所を変えたくないボタン向け）。
    /// </summary>
    /// <param name="button">対象のボタン。</param>
    /// <param name="isPrimary">主操作か。</param>
    public static void ApplyButtonStyle(ButtonBase button, bool isPrimary = false)
        => SeedButtonStyle.Apply(
            button,
            isPrimary ? SeedButtonStyle.DIALOG_PRIMARY : SeedButtonStyle.DIALOG);

    /// <summary>
    /// ダイアログの共通設定を適用する（背景・配置・タスクバーに出さない）。
    /// </summary>
    /// <param name="window">対象のウィンドウ。</param>
    /// <param name="title">タイトル。</param>
    /// <param name="width">幅 [px]。</param>
    /// <param name="height">高さ [px]。</param>
    public static void ApplyWindowChrome(
        Window window, string title, double width, double height)
    {
        window.Title                 = title;
        window.Width                 = width;
        window.Height                = height;
        window.Background            = Background;
        window.ResizeMode            = ResizeMode.NoResize;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.ShowInTaskbar         = false;
    }
}
