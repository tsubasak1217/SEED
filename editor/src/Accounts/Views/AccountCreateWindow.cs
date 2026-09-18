// ============================================================
//  AccountCreateWindow.cs — アカウントを作るモーダル
//
//  【役割】
//  名前を 1 つ入力させ、規則（契約 2 章）を **その場で** 見せる。
//  作成そのものは <see cref="AccountService.CreateAccount"/> が行う。
//
//  【入力中に判定を出す理由】
//  名前は作成後に変更できない（JWT の sub とロックの所有者表示に使う）。
//  押してから「使えない文字です」と言われるより、打っている最中に
//  分かる方が安全側。判定は <see cref="Crypto.AccountNameRule"/> の 1 か所。
//
//  【ヘッドレスでは呼ばれない】
//  モーダルはヘッドレス起動で UI スレッドを止める。Hub（スタート画面）
//  からしか開かないので、ヘッドレスでは到達しない。
// ============================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SEEDEditor.Accounts.Crypto;
using SEEDEditor.Accounts.Storage;
using SEEDEditor.Theme;

namespace SEEDEditor.Accounts.Views;

/// <summary>
/// アカウント作成のモーダルダイアログ。
/// </summary>
public sealed class AccountCreateWindow : Window
{
    /// <summary>ウィンドウ幅 [px]。</summary>
    private const double WINDOW_WIDTH_PX = 440;

    /// <summary>ウィンドウ高さ [px]。</summary>
    private const double WINDOW_HEIGHT_PX = 236;

    /// <summary>名前の入力欄。</summary>
    private readonly TextBox _nameBox;

    /// <summary>判定結果を出す行。</summary>
    private readonly TextBlock _statusText;

    /// <summary>作成ボタン（規則を満たすまで押せない）。</summary>
    private readonly Button _createButton;

    /// <summary>作成できた名前（取り消し・失敗なら null）。</summary>
    public string? CreatedName { get; private set; }

    /// <summary>ダイアログを構築する。</summary>
    public AccountCreateWindow()
    {
        SeedDialogTheme.ApplyWindowChrome(
            this, AccountMessages.CREATE_DIALOG_TITLE, WINDOW_WIDTH_PX, WINDOW_HEIGHT_PX);

        var root = new StackPanel
        {
            Margin = new Thickness(SeedDialogTheme.CONTENT_PADDING_PX),
        };

        root.Children.Add(SeedDialogTheme.NewLabel(AccountMessages.CREATE_DIALOG_PROMPT));
        root.Children.Add(SeedDialogTheme.NewLabel(
            AccountMessages.CREATE_DIALOG_RULE_NOTE,
            SeedDialogTheme.DimText, SeedDialogTheme.NOTE_FONT_SIZE, topMargin: 4));

        _nameBox = SeedDialogTheme.NewTextBox(
            topMargin: SeedDialogTheme.ROW_SPACING_PX);
        _nameBox.TextChanged += (_, _) => SyncValidation();
        _nameBox.KeyDown     += OnNameKeyDown;
        root.Children.Add(_nameBox);

        _statusText = SeedDialogTheme.NewLabel(
            string.Empty, SeedDialogTheme.ErrorText,
            SeedDialogTheme.NOTE_FONT_SIZE, topMargin: 6);
        root.Children.Add(_statusText);

        root.Children.Add(SeedDialogTheme.NewLabel(
            AccountMessages.NAME_IMMUTABLE_NOTE,
            SeedDialogTheme.DimText, SeedDialogTheme.NOTE_FONT_SIZE,
            topMargin: SeedDialogTheme.ROW_SPACING_PX));

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, SeedDialogTheme.ROW_SPACING_PX, 0, 0),
        };
        _createButton = SeedDialogTheme.NewButton(
            AccountMessages.BUTTON_OK, OnCreate, isPrimary: true);
        buttons.Children.Add(_createButton);
        buttons.Children.Add(SeedDialogTheme.NewButton(
            AccountMessages.BUTTON_CANCEL, OnCancel,
            leftMargin: SeedDialogTheme.BUTTON_GAP_PX));
        root.Children.Add(buttons);

        Content = root;

        SyncValidation();
        Loaded += (_, _) => _nameBox.Focus();
    }

    /// <summary>
    /// 入力内容から、判定メッセージとボタンの有効・無効を決める。
    /// </summary>
    private void SyncValidation()
    {
        // 空のうちはエラーを出さない（開いた瞬間に赤字が出るのは不快なので）。
        if (_nameBox.Text.Length == 0)
        {
            _statusText.Text          = string.Empty;
            _createButton.IsEnabled   = false;
            return;
        }

        var check = AccountNameRule.Check(_nameBox.Text);
        _statusText.Text        = check.IsValid ? string.Empty : check.Error;
        _createButton.IsEnabled = check.IsValid;
    }

    /// <summary>Enter で作成、Escape で取り消し。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">キーイベント。</param>
    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _createButton.IsEnabled)
        {
            OnCreate(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            OnCancel(sender, e);
            e.Handled = true;
        }
    }

    /// <summary>作成する。失敗したら閉じずに理由を出す。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnCreate(object sender, RoutedEventArgs e)
    {
        try
        {
            AccountService.CreateAccount(_nameBox.Text);
            CreatedName  = _nameBox.Text;
            DialogResult = true;
        }
        catch (AccountStoreException ex)
        {
            // 保存できない（フォルダを作れない等）。閉じずに理由を見せる。
            _statusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            _statusText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                AccountMessages.ACCOUNT_SAVE_FAILED_FORMAT, ex.Message);
        }
    }

    /// <summary>取り消す。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CreatedName  = null;
        DialogResult = false;
    }
}
