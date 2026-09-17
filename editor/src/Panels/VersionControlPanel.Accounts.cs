// ============================================================
//  VersionControlPanel.Accounts.cs — パネルのアカウント関連
//
//  【役割】
//  ・ヘッダーの identity 表示（ログイン中は「名前（ログイン中）」）
//  ・「アカウント」ボタン → オーナー向けダイアログ（ProjectAccountsWindow）
//
//  【なぜ別ファイルにするのか】
//  VersionControlPanel.xaml.cs は生成・購読・変更一覧で既に長い。
//  アカウントは別の関心事なので partial で分ける。
//
//  【スレッド（重要）】
//  AccountService.AuthStateChanged は **ワーカースレッド（タイマー）から飛ぶ**。
//  必ず Dispatcher へ移してから UI を触る。
// ============================================================

using System;
using System.Windows;
using SEEDEditor.Accounts;
using SEEDEditor.Accounts.Model;
using SEEDEditor.Accounts.Views;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Presentation;

namespace SEEDEditor.Panels;

public partial class VersionControlPanel
{
    /// <summary>アカウント関連の固定文言をコントロールへ流し込む。</summary>
    private void InitializeAccountsUi()
    {
        BtnAccounts.Content = VersionControlMessages.PANEL_ACCOUNTS_BUTTON;
        BtnAccounts.ToolTip = VersionControlMessages.PANEL_ACCOUNTS_TOOLTIP;
    }

    /// <summary>
    /// ログイン状態の変化を購読する（初回表示時に 1 回だけ）。
    /// </summary>
    private void SubscribeAccountState()
    {
        AccountService.AuthStateChanged += OnAccountAuthStateChanged;
    }

    /// <summary>
    /// ログイン状態が変わったときに呼ばれる。**ワーカースレッドから来る**ので
    /// 必ず Dispatcher へ移す。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="state">新しい状態。</param>
    private void OnAccountAuthStateChanged(object? sender, AccountAuthState state)
    {
        Dispatcher.BeginInvoke(new Action(RefreshConnectionText));
    }

    /// <summary>
    /// ヘッダーの「identity — リモート」を今の状態で作り直す。
    ///
    /// <para>
    /// identity は <c>IVersionControlProvider.Identity</c> から取る。
    /// ログイン中はバックエンドがアカウント名を返すので、ここでは
    /// 「ログイン中かどうか」だけを足せばよい（名前の決め方は
    /// <c>LoreCredentialResolver.ResolveDisplayIdentity</c> の 1 か所）。
    /// </para>
    /// </summary>
    private void RefreshConnectionText()
    {
        var provider = VersionControlService.Provider;
        if (!provider.IsAvailable) return;

        TxtConnection.Text = VersionControlDisplay.ToConnectionText(
            provider.Identity, provider.RemoteUrl, AccountService.AuthState.IsSignedIn);
        TxtConnection.ToolTip = TxtConnection.Text;
    }

    /// <summary>
    /// 「アカウント」ボタン。オーナー向けの操作をまとめたダイアログを開く。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnAccountsClick(object sender, RoutedEventArgs e)
    {
        var provider = VersionControlService.Provider;

        // リポジトリ ID は `.lore/id`。読めない場合もダイアログは開く
        // （理由をダイアログの中で説明する方が、押しても何も起きないより良い）。
        var repositoryId = VersionControlPaths.ReadRepositoryId(provider.WorkingCopyRoot);

        var dialog = new ProjectAccountsWindow(
            provider.RemoteUrl, repositoryId, Project.ProjectContext.DisplayName)
        {
            Owner = Window.GetWindow(this),
        };
        dialog.ShowDialog();

        // ダイアログの中でログインし直していることがあるので、表示を作り直す。
        RefreshConnectionText();
    }
}
