// ============================================================
//  AccountDialogTheme.cs — アカウント関連ダイアログの見た目を 1 か所へ
//
//  【役割】
//  配色・寸法・部品（ラベル / 入力欄 / ボタン）の作り方をまとめる。
//  アカウント関連のモーダルは 4 つあり、それぞれが自前で色と余白を書くと
//  「1 つだけ背景色が違う」ダイアログが必ず生まれる。
//
//  【XAML を使わない理由】
//  既存の小さなモーダル（TextInputWindow / AudioSilenceTrimWindow）と同じ流儀。
//  入力欄とボタンが数個の画面は、XAML と分けると 2 ファイルを往復することになる。
//  加えて XAML の StaticResource は **綴りを間違えてもビルドが通る**ため、
//  起動しないと分からない壊れ方をする。コードで組めばコンパイラが見てくれる。
//
//  【配色の出所】
//  エディタ本体のダイアログ（ProjectSettingsWindow / TextInputWindow）と同じ値。
// ============================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.Accounts.Views;

/// <summary>
/// アカウント関連ダイアログの配色・寸法・部品。
/// </summary>
public static class AccountDialogTheme
{
    // ── 配色 ────────────────────────────────────────────────

    /// <summary>ダイアログの背景。</summary>
    public static readonly Brush Background =
        new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));

    /// <summary>入力欄の背景。</summary>
    public static readonly Brush Field =
        new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));

    /// <summary>本文の文字色。</summary>
    public static readonly Brush Text =
        new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC));

    /// <summary>補足の文字色。</summary>
    public static readonly Brush DimText =
        new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));

    /// <summary>成功・肯定の文字色。</summary>
    public static readonly Brush SuccessText =
        new SolidColorBrush(Color.FromRgb(0x8A, 0xCB, 0x8A));

    /// <summary>エラーの文字色。</summary>
    public static readonly Brush ErrorText =
        new SolidColorBrush(Color.FromRgb(0xE8, 0x8F, 0x8F));

    /// <summary>枠線の色。</summary>
    public static readonly Brush FieldBorder =
        new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));

    /// <summary>ボタンの背景。</summary>
    public static readonly Brush Button =
        new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));

    /// <summary>主要ボタンの背景。</summary>
    public static readonly Brush PrimaryButton =
        new SolidColorBrush(Color.FromRgb(0x0E, 0x63, 0x9C));

    // ── 寸法 ────────────────────────────────────────────────

    /// <summary>外周の余白 [px]。</summary>
    public const double CONTENT_PADDING_PX = 14;

    /// <summary>行間の余白 [px]。</summary>
    public const double ROW_SPACING_PX = 10;

    /// <summary>入力欄の内側余白 [px]。</summary>
    public const double FIELD_PADDING_PX = 5;

    /// <summary>ボタンの幅 [px]。</summary>
    public const double BUTTON_WIDTH_PX = 110;

    /// <summary>ボタンの縦余白 [px]。</summary>
    public const double BUTTON_PADDING_Y_PX = 4;

    /// <summary>ボタン同士の間隔 [px]。</summary>
    public const double BUTTON_GAP_PX = 8;

    /// <summary>本文の文字サイズ。</summary>
    public const double BODY_FONT_SIZE = 12;

    /// <summary>補足の文字サイズ。</summary>
    public const double NOTE_FONT_SIZE = 11;

    /// <summary>枠線の太さ [px]。</summary>
    public const double BORDER_THICKNESS_PX = 1;

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
    /// ボタンを 1 つ作る。
    /// </summary>
    /// <param name="text">文言。</param>
    /// <param name="onClick">押されたときの処理。</param>
    /// <param name="isPrimary">主要ボタンか（色を変える）。</param>
    /// <param name="leftMargin">左の余白 [px]。</param>
    public static Button NewButton(
        string text, RoutedEventHandler onClick, bool isPrimary = false,
        double leftMargin = 0)
    {
        var button = new Button
        {
            Content         = text,
            MinWidth        = BUTTON_WIDTH_PX,
            Padding         = new Thickness(BUTTON_GAP_PX, BUTTON_PADDING_Y_PX,
                                            BUTTON_GAP_PX, BUTTON_PADDING_Y_PX),
            Background      = isPrimary ? PrimaryButton : Button,
            Foreground      = Text,
            BorderThickness = new Thickness(0),
            FontSize        = BODY_FONT_SIZE,
            Cursor          = Cursors.Hand,
            Margin          = new Thickness(leftMargin, 0, 0, 0),
        };
        button.Click += onClick;
        return button;
    }

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
