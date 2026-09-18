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
//
//  【配色・寸法】
//  自前では持たない。Theme/SeedDialogTheme（色と部品）と
//  Theme/SeedButtonStyles.xaml（ボタンの状態別の見た目）に従う。
// ============================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SEEDEditor.Theme;

namespace SEEDEditor.Dialogs;

/// <summary>
/// 1 行のテキストを入力させるモーダルダイアログ。
/// </summary>
public sealed class TextInputWindow : Window
{
    // 配色・部品の作り方は Theme/SeedDialogTheme に集約してある（自前で色を決めない）。

    // ── レイアウト寸法（このダイアログ固有のもののみ）──────────────

    /// <summary>ウィンドウ幅（px）。</summary>
    private const double WINDOW_WIDTH_PX = 420;

    /// <summary>ウィンドウ高さ（px）。</summary>
    private const double WINDOW_HEIGHT_PX = 176;

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
        SeedDialogTheme.ApplyWindowChrome(this, title, WINDOW_WIDTH_PX, WINDOW_HEIGHT_PX);

        // ── 説明文 / 入力欄 / ボタン列 の 3 段 ──
        var root = new Grid { Margin = new Thickness(SeedDialogTheme.CONTENT_PADDING_PX) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var promptText = SeedDialogTheme.NewLabel(prompt);
        Grid.SetRow(promptText, 0);
        root.Children.Add(promptText);

        _inputBox = SeedDialogTheme.NewTextBox(
            initialText ?? string.Empty, SeedDialogTheme.ROW_SPACING_PX);
        _inputBox.KeyDown += OnInputKeyDown;
        Grid.SetRow(_inputBox, 1);
        root.Children.Add(_inputBox);

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Bottom,
        };
        buttons.Children.Add(SeedDialogTheme.NewButton(
            OK_BUTTON_TEXT, OnOk, isPrimary: true));
        buttons.Children.Add(SeedDialogTheme.NewButton(
            CANCEL_BUTTON_TEXT, OnCancel, leftMargin: SeedDialogTheme.BUTTON_GAP_PX));
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
