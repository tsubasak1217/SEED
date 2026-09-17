// ============================================================
//  StartWindow.Accounts.cs — スタート画面のアカウント欄
//
//  【役割】
//  Hub（スタート画面）の右ペインに足したアカウント欄の振る舞い。
//    ・未作成なら「アカウントを作成」
//    ・作成済みなら名前の表示と「書き出し」「読み込み」
//    ・「プロジェクトに参加」（招待コード → 取得 → そのまま開く）
//
//  【なぜ別ファイルにするのか】
//  StartWindow.xaml.cs は「最近のプロジェクト」と「関連付け」で既に埋まっている。
//  アカウントは別の関心事なので partial で分け、片方を読むときに
//  もう片方を読まなくて済むようにする。
//
//  【判断を持たない】
//  作成・書き出し・読み込み・参加の中身は AccountService / ProjectJoinService が持つ。
//  ここは「ダイアログを開いて、結果をステータス行に出す」だけ。
// ============================================================

using System;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using SEEDEditor.Accounts;
using SEEDEditor.Accounts.Storage;
using SEEDEditor.Accounts.Views;
using SEEDEditor.Project;

namespace SEEDEditor.Startup;

public partial class StartWindow
{
    /// <summary>
    /// アカウント欄の表示を、今の状態に合わせて作り直す。
    /// </summary>
    private void RefreshAccountSection()
    {
        TxtAccountHeader.Text      = AccountMessages.HUB_SECTION_TITLE;
        TxtCreateAccountTitle.Text = AccountMessages.HUB_CREATE_TITLE;
        TxtCreateAccountNote.Text  = AccountMessages.HUB_CREATE_NOTE;
        TxtJoinTitle.Text          = AccountMessages.HUB_JOIN_TITLE;
        TxtJoinNote.Text           = AccountMessages.HUB_JOIN_NOTE;
        BtnExportAccount.Content   = AccountMessages.HUB_EXPORT_TITLE;
        BtnImportAccount.Content   = AccountMessages.HUB_IMPORT_TITLE;

        var identity  = AccountService.Identity;
        var loadError = AccountService.LoadError;

        // ★どちらの分岐でも 3 つのボタンすべての表示状態を決める。
        //   片方の分岐で触らずに残すと、作成した直後に「書き出し」が
        //   隠れたままになる（前の状態が残る）。
        var hasAccount = identity.HasValue;

        TxtAccountName.Text = hasAccount
            ? identity.Name
            : (loadError.Length > 0 ? loadError : AccountMessages.ACCOUNT_NOT_CREATED);

        TxtAccountName.Foreground = hasAccount
            ? System.Windows.Media.Brushes.White
            : (loadError.Length > 0
                ? System.Windows.Media.Brushes.IndianRed
                : System.Windows.Media.Brushes.Gray);

        // 作成ボタンは未作成のときだけ。書き出しは「書き出すものがある」ときだけ。
        // 読み込みは常に出す（別 PC で作ったものを取り込む経路が要る）。
        BtnCreateAccount.Visibility   = hasAccount ? Visibility.Collapsed : Visibility.Visible;
        BtnExportAccount.Visibility   = hasAccount ? Visibility.Visible   : Visibility.Collapsed;
        BtnImportAccount.Visibility   = Visibility.Visible;
        AccountTransferRow.Visibility = Visibility.Visible;
    }

    /// <summary>「アカウントを作成...」。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnCreateAccountClick(object sender, RoutedEventArgs e)
    {
        // 作り直すと参加中のプロジェクトへ入れなくなる。既にあるなら止める。
        if (AccountService.HasAccount)
        {
            ShowStatus(AccountMessages.ACCOUNT_ALREADY_EXISTS, isError: true);
            return;
        }

        var dialog = new AccountCreateWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.CreatedName is null) return;

        RefreshAccountSection();
        ShowStatus(string.Format(CultureInfo.CurrentCulture,
                                 AccountMessages.ACCOUNT_CREATED_FORMAT, dialog.CreatedName),
                   isError: false);
    }

    /// <summary>「書き出し...」。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnExportAccountClick(object sender, RoutedEventArgs e)
    {
        if (!AccountService.HasAccount)
        {
            ShowStatus(AccountMessages.ACCOUNT_NOT_CREATED, isError: true);
            return;
        }

        var save = new SaveFileDialog
        {
            Title      = AccountMessages.EXPORT_DIALOG_TITLE,
            Filter     = AccountMessages.EXPORT_FILE_FILTER,
            DefaultExt = AccountSettings.EXPORT_FILE_EXTENSION,
            FileName   = AccountService.Identity.Name + AccountSettings.EXPORT_FILE_EXTENSION,
        };
        if (save.ShowDialog(this) != true) return;

        // パスフレーズは確認欄つきで聞く（打ち間違えると二度と読めない）。
        var passphraseDialog = new PassphraseWindow(
            AccountMessages.EXPORT_DIALOG_TITLE,
            AccountMessages.EXPORT_DIALOG_PROMPT,
            requireConfirmation: true)
        { Owner = this };
        if (passphraseDialog.ShowDialog() != true || passphraseDialog.Passphrase is null) return;

        try
        {
            AccountService.ExportAccount(save.FileName, passphraseDialog.Passphrase);
            ShowStatus(string.Format(CultureInfo.CurrentCulture,
                                     AccountMessages.EXPORT_OK_FORMAT, save.FileName),
                       isError: false);
        }
        catch (AccountStoreException ex)
        {
            ShowStatus(ex.Message, isError: true);
        }
    }

    /// <summary>「読み込み...」。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnImportAccountClick(object sender, RoutedEventArgs e)
    {
        var open = new OpenFileDialog
        {
            Title           = AccountMessages.IMPORT_DIALOG_TITLE,
            Filter          = AccountMessages.EXPORT_FILE_FILTER,
            DefaultExt      = AccountSettings.EXPORT_FILE_EXTENSION,
            CheckFileExists = true,
        };
        if (open.ShowDialog(this) != true) return;

        var passphraseDialog = new PassphraseWindow(
            AccountMessages.IMPORT_DIALOG_TITLE,
            AccountMessages.IMPORT_DIALOG_PROMPT,
            requireConfirmation: false)
        { Owner = this };
        if (passphraseDialog.ShowDialog() != true || passphraseDialog.Passphrase is null) return;

        try
        {
            var name = AccountService.ImportAccount(open.FileName, passphraseDialog.Passphrase);
            RefreshAccountSection();
            ShowStatus(string.Format(CultureInfo.CurrentCulture,
                                     AccountMessages.IMPORT_OK_FORMAT, name),
                       isError: false);
        }
        catch (AccountStoreException ex)
        {
            ShowStatus(ex.Message, isError: true);
        }
    }

    /// <summary>「プロジェクトに参加...」。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnJoinProjectClick(object sender, RoutedEventArgs e)
    {
        if (!AccountService.HasAccount)
        {
            ShowStatus(AccountMessages.JOIN_ACCOUNT_REQUIRED, isError: true);
            return;
        }

        var dialog = new JoinProjectWindow(SeedProjectFile.EXTENSION) { Owner = this };
        if (dialog.ShowDialog() != true
            || string.IsNullOrWhiteSpace(dialog.JoinedProjectFilePath))
        {
            return;
        }

        // 参加できたので、そのまま開く（開く経路は既存のものを使う）。
        OpenProject(dialog.JoinedProjectFilePath!);
    }
}
