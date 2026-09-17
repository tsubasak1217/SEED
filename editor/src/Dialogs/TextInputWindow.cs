// ============================================================
//  TextInputWindow.cs — 1 行のテキストを入力させる汎用ダイアログ
//
//  【役割】
//  「名前を決めてもらう」だけの小さなモーダル。最初の利用者は
//  Version Control パネルの「新しいブランチ…」。
//
//  【なぜ作ったのか】
//  エディタには確認ダイアログ（EditorDialogs.Show → MessageBox）しか無く、
//  文字を入力させる共通部品が無い。既存の入力箇所（プロジェクトパネルの改名）は
//  インラインの TextBox 差し替えで、モーダルとしては再利用できない。
//
//  【ヘッドレスでは呼ばれない】
//  モーダルはヘッドレス起動で UI スレッドを永久に止める。
//  そのため呼び出しは必ず <see cref="SEEDEditor.Headless.EditorDialogs.ShowTextInput"/>
//  を経由すること（ヘッドレス時はここまで来ず null が返る）。
//
//  【XAML を使わない理由】
//  入力欄 1 つとボタン 2 つしかなく、既存の AudioSilenceTrimWindow と同じく
//  コードだけで組んだ方が読みやすい（XAML と分けると 2 ファイルを往復することになる）。
// ============================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.Dialogs;

/// <summary>
/// 1 行のテキストを入力させるモーダルダイアログ。
/// </summary>
public sealed class TextInputWindow : Window
{
    // ── 配色（エディタの他ダイアログと揃える）────────────────────

    /// <summary>ダイアログの背景。</summary>
    private static readonly Brush BackgroundBrush =
        new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));

    /// <summary>入力欄の背景。</summary>
    private static readonly Brush FieldBrush =
        new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));

    /// <summary>本文の文字色。</summary>
    private static readonly Brush TextBrush =
        new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC));

    /// <summary>枠線の色。</summary>
    private static readonly Brush FieldBorderBrush =
        new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));

    /// <summary>ボタンの背景。</summary>
    private static readonly Brush ButtonBrush =
        new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));

    // ── レイアウト寸法 ────────────────────────────────────────────

    /// <summary>ウィンドウ幅（px）。</summary>
    private const double WINDOW_WIDTH_PX = 420;

    /// <summary>ウィンドウ高さ（px）。</summary>
    private const double WINDOW_HEIGHT_PX = 176;

    /// <summary>外周の余白（px）。</summary>
    private const double CONTENT_PADDING_PX = 14;

    /// <summary>説明文と入力欄の間の余白（px）。</summary>
    private const double ROW_SPACING_PX = 10;

    /// <summary>入力欄の内側余白（px）。</summary>
    private const double FIELD_PADDING_PX = 5;

    /// <summary>ボタンの幅（px）。</summary>
    private const double BUTTON_WIDTH_PX = 84;

    /// <summary>ボタンの縦余白（px）。</summary>
    private const double BUTTON_PADDING_Y_PX = 4;

    /// <summary>ボタン同士の間隔（px）。</summary>
    private const double BUTTON_GAP_PX = 8;

    /// <summary>本文の文字サイズ。</summary>
    private const double BODY_FONT_SIZE = 12;

    // ── ボタン文言 ────────────────────────────────────────────────

    /// <summary>決定ボタンの文言。</summary>
    private const string OK_BUTTON_TEXT = "OK";

    /// <summary>取り消しボタンの文言。</summary>
    private const string CANCEL_BUTTON_TEXT = "キャンセル";

    /// <summary>入力欄。</summary>
    private readonly TextBox _inputBox;

    /// <summary>
    /// 入力された文字列（前後の空白を落としたもの）。取り消されたときは null。
    /// </summary>
    public string? InputText { get; private set; }

    /// <summary>
    /// ダイアログを構築する。
    /// </summary>
    /// <param name="title">タイトルバーに出す文字列。</param>
    /// <param name="prompt">入力欄の上に出す説明文。</param>
    /// <param name="initialText">入力欄の初期値。</param>
    public TextInputWindow(string title, string prompt, string? initialText = null)
    {
        Title                 = title;
        Width                 = WINDOW_WIDTH_PX;
        Height                = WINDOW_HEIGHT_PX;
        Background            = BackgroundBrush;
        ResizeMode            = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar         = false;

        // ── 説明文 / 入力欄 / ボタン列 の 3 段 ──
        var root = new Grid { Margin = new Thickness(CONTENT_PADDING_PX) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var promptText = new TextBlock
        {
            Text         = prompt,
            Foreground   = TextBrush,
            FontSize     = BODY_FONT_SIZE,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(promptText, 0);
        root.Children.Add(promptText);

        _inputBox = new TextBox
        {
            Text            = initialText ?? string.Empty,
            Background      = FieldBrush,
            Foreground      = TextBrush,
            BorderBrush     = FieldBorderBrush,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(FIELD_PADDING_PX),
            FontSize        = BODY_FONT_SIZE,
            Margin          = new Thickness(0, ROW_SPACING_PX, 0, 0),
        };
        _inputBox.KeyDown += OnInputKeyDown;
        Grid.SetRow(_inputBox, 1);
        root.Children.Add(_inputBox);

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Bottom,
        };
        buttons.Children.Add(NewButton(OK_BUTTON_TEXT, OnOk, isFirst: true));
        buttons.Children.Add(NewButton(CANCEL_BUTTON_TEXT, OnCancel, isFirst: false));
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;

        // 開いた直後から打ち始められるようにする（名前を入れるだけのダイアログなので）。
        Loaded += (_, _) =>
        {
            _inputBox.Focus();
            _inputBox.SelectAll();
        };
    }

    /// <summary>ボタンを 1 つ作る（見た目を 1 か所に揃えるためのヘルパー）。</summary>
    /// <param name="text">ボタンの文言。</param>
    /// <param name="onClick">押されたときの処理。</param>
    /// <param name="isFirst">左端のボタンか（左端だけ左余白を付けない）。</param>
    private static Button NewButton(string text, RoutedEventHandler onClick, bool isFirst)
    {
        var button = new Button
        {
            Content         = text,
            Width           = BUTTON_WIDTH_PX,
            Padding         = new Thickness(0, BUTTON_PADDING_Y_PX, 0, BUTTON_PADDING_Y_PX),
            Background      = ButtonBrush,
            Foreground      = TextBrush,
            BorderThickness = new Thickness(0),
            FontSize        = BODY_FONT_SIZE,
            Cursor          = Cursors.Hand,
            Margin          = new Thickness(isFirst ? 0 : BUTTON_GAP_PX, 0, 0, 0),
        };
        button.Click += onClick;
        return button;
    }

    /// <summary>Enter で確定、Escape で取り消し（キーボードだけで閉じられるように）。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">キーイベント。</param>
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnOk(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            OnCancel(sender, e);
            e.Handled = true;
        }
    }

    /// <summary>決定。空白だけの入力は「入力なし」として閉じない。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnOk(object sender, RoutedEventArgs e)
    {
        var text = _inputBox.Text.Trim();

        // 空のまま OK を押されたら閉じずに入力へ戻す
        // （空文字を返すと呼び出し側が「取り消し」と区別できない）。
        if (text.Length == 0)
        {
            _inputBox.Focus();
            return;
        }

        InputText    = text;
        DialogResult = true;
    }

    /// <summary>取り消し。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        InputText    = null;
        DialogResult = false;
    }
}
