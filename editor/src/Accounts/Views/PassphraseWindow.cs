// ============================================================
//  PassphraseWindow.cs — パスフレーズを入力させるモーダル（書き出し／読み込み共用）
//
//  【役割】
//  書き出し時は「新しいパスフレーズ ＋ 確認」、読み込み時は「パスフレーズ 1 つ」。
//  2 つのダイアログを別々に作ると、片方だけ最小文字数の判定を忘れる。
//
//  【PasswordBox を使う理由】
//  TextBox だと肩越しに見られる。加えて PasswordBox は
//  値をコピーされにくい（自動入力ツールの対象にもなりにくい）。
//
//  【値をログへ出さない】
//  このウィンドウが扱うのは秘密そのもの。<see cref="Passphrase"/> を
//  ログ・例外メッセージ・タイトルへ載せないこと。
// ============================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SEEDEditor.Accounts.Views;

/// <summary>
/// パスフレーズ入力のモーダルダイアログ。
/// </summary>
public sealed class PassphraseWindow : Window
{
    /// <summary>ウィンドウ幅 [px]。</summary>
    private const double WINDOW_WIDTH_PX = 440;

    /// <summary>確認欄ありのときの高さ [px]。</summary>
    private const double WINDOW_HEIGHT_WITH_CONFIRM_PX = 268;

    /// <summary>確認欄なしのときの高さ [px]。</summary>
    private const double WINDOW_HEIGHT_PX = 212;

    /// <summary>パスフレーズ欄。</summary>
    private readonly PasswordBox _passphraseBox;

    /// <summary>確認欄（読み込み時は null）。</summary>
    private readonly PasswordBox? _confirmBox;

    /// <summary>エラー表示の行。</summary>
    private readonly TextBlock _statusText;

    /// <summary>入力されたパスフレーズ（取り消しなら null）。</summary>
    public string? Passphrase { get; private set; }

    /// <summary>
    /// ダイアログを構築する。
    /// </summary>
    /// <param name="title">タイトル。</param>
    /// <param name="prompt">説明文。</param>
    /// <param name="requireConfirmation">
    /// 確認欄を出すか（書き出しのときだけ真。打ち間違えたまま書き出すと
    /// **二度と読めないファイル** ができるため）。
    /// </param>
    public PassphraseWindow(string title, string prompt, bool requireConfirmation)
    {
        AccountDialogTheme.ApplyWindowChrome(
            this, title, WINDOW_WIDTH_PX,
            requireConfirmation ? WINDOW_HEIGHT_WITH_CONFIRM_PX : WINDOW_HEIGHT_PX);

        var root = new StackPanel
        {
            Margin = new Thickness(AccountDialogTheme.CONTENT_PADDING_PX),
        };

        root.Children.Add(AccountDialogTheme.NewLabel(prompt));

        root.Children.Add(AccountDialogTheme.NewLabel(
            AccountMessages.LABEL_PASSPHRASE, AccountDialogTheme.DimText,
            AccountDialogTheme.NOTE_FONT_SIZE,
            topMargin: AccountDialogTheme.ROW_SPACING_PX));
        _passphraseBox = AccountDialogTheme.NewPasswordBox(topMargin: 2);
        _passphraseBox.KeyDown += OnKeyDown;
        root.Children.Add(_passphraseBox);

        if (requireConfirmation)
        {
            root.Children.Add(AccountDialogTheme.NewLabel(
                AccountMessages.LABEL_PASSPHRASE_CONFIRM, AccountDialogTheme.DimText,
                AccountDialogTheme.NOTE_FONT_SIZE,
                topMargin: AccountDialogTheme.ROW_SPACING_PX));
            _confirmBox = AccountDialogTheme.NewPasswordBox(topMargin: 2);
            _confirmBox.KeyDown += OnKeyDown;
            root.Children.Add(_confirmBox);
        }

        _statusText = AccountDialogTheme.NewLabel(
            string.Empty, AccountDialogTheme.ErrorText,
            AccountDialogTheme.NOTE_FONT_SIZE, topMargin: 6);
        root.Children.Add(_statusText);

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, AccountDialogTheme.ROW_SPACING_PX, 0, 0),
        };
        buttons.Children.Add(AccountDialogTheme.NewButton(
            AccountMessages.BUTTON_OK, OnOk, isPrimary: true));
        buttons.Children.Add(AccountDialogTheme.NewButton(
            AccountMessages.BUTTON_CANCEL, OnCancel,
            leftMargin: AccountDialogTheme.BUTTON_GAP_PX));
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => _passphraseBox.Focus();
    }

    /// <summary>Enter で決定、Escape で取り消し。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">キーイベント。</param>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  { OnOk(sender, e);     e.Handled = true; return; }
        if (e.Key == Key.Escape) { OnCancel(sender, e); e.Handled = true; }
    }

    /// <summary>決定する。規則を満たさなければ閉じずに理由を出す。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnOk(object sender, RoutedEventArgs e)
    {
        var passphrase = _passphraseBox.Password;

        // 長さの下限は書き出し・読み込みで同じ判定（AccountExportFile）を使う。
        // 読み込み側で弾いておくと、短いパスフレーズで延々 PBKDF2 を回さずに済む。
        if (passphrase.Length < AccountSettings.EXPORT_PASSPHRASE_MIN_LENGTH)
        {
            _statusText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                AccountMessages.PASSPHRASE_TOO_SHORT_FORMAT,
                AccountSettings.EXPORT_PASSPHRASE_MIN_LENGTH);
            return;
        }

        if (_confirmBox is not null && !string.Equals(passphrase, _confirmBox.Password,
                                                      System.StringComparison.Ordinal))
        {
            _statusText.Text = AccountMessages.PASSPHRASE_MISMATCH;
            return;
        }

        Passphrase   = passphrase;
        DialogResult = true;
    }

    /// <summary>取り消す。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Passphrase   = null;
        DialogResult = false;
    }
}
