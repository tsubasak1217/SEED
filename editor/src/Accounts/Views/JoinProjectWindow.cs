// ============================================================
//  JoinProjectWindow.cs — 「プロジェクトに参加」のモーダル
//
//  【役割】
//  サーバのアドレス・招待コード・保存先を受け取り、
//  <see cref="ProjectJoinService"/> に段取り（join → ログイン → クローン）を任せる。
//  このファイルは **入力を集めて結果を見せるだけ**。判断は一切しない。
//
//  【なぜ判断を持たないのか】
//  参加は 4 往復あり、失敗の言い分けが多い。ここに書くと
//  GUI を起動しないと 1 行も検証できない。段取りは
//  ProjectJoinService（WPF 非依存）にあり、単体テストで固定してある。
//
//  【招待コードをログへ出さない】
//  入力欄の値は資格そのもの。ログ・タイトル・例外メッセージへ載せない。
// ============================================================

using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SEEDEditor.VersionControl.Lore.Backend;
using SEEDEditor.Theme;

namespace SEEDEditor.Accounts.Views;

/// <summary>
/// プロジェクトへ参加するモーダルダイアログ。
/// </summary>
public sealed class JoinProjectWindow : Window
{
    /// <summary>ウィンドウ幅 [px]。</summary>
    private const double WINDOW_WIDTH_PX = 520;

    /// <summary>ウィンドウ高さ [px]。</summary>
    private const double WINDOW_HEIGHT_PX = 340;

    /// <summary>保存先欄と参照ボタンの間の余白 [px]。</summary>
    private const double BROWSE_BUTTON_GAP_PX = 6;

    /// <summary>サーバのアドレス欄。</summary>
    private readonly TextBox _hostBox;

    /// <summary>招待コード欄。</summary>
    private readonly TextBox _inviteBox;

    /// <summary>保存先欄。</summary>
    private readonly TextBox _destinationBox;

    /// <summary>状態表示の行。</summary>
    private readonly TextBlock _statusText;

    /// <summary>参加ボタン（実行中は押せなくする）。</summary>
    private readonly Button _joinButton;

    /// <summary>プロジェクトファイルの拡張子（呼び出し側から受け取る）。</summary>
    private readonly string _projectFileExtension;

    /// <summary>参加できたときの .seedproj の絶対パス（失敗・取り消しなら null）。</summary>
    public string? JoinedProjectFilePath { get; private set; }

    /// <summary>
    /// ダイアログを構築する。
    /// </summary>
    /// <param name="projectFileExtension">
    /// 探すプロジェクトファイルの拡張子（`.seedproj`）。
    /// プロジェクト層の定数を呼び出し側から渡す。
    /// </param>
    public JoinProjectWindow(string projectFileExtension)
    {
        _projectFileExtension = projectFileExtension;

        SeedDialogTheme.ApplyWindowChrome(
            this, AccountMessages.JOIN_DIALOG_TITLE, WINDOW_WIDTH_PX, WINDOW_HEIGHT_PX);

        var root = new StackPanel
        {
            Margin = new Thickness(SeedDialogTheme.CONTENT_PADDING_PX),
        };

        // ── サーバのアドレス ──
        root.Children.Add(SeedDialogTheme.NewLabel(
            AccountMessages.LABEL_SERVER_HOST, SeedDialogTheme.DimText,
            SeedDialogTheme.NOTE_FONT_SIZE));
        _hostBox = SeedDialogTheme.NewTextBox(topMargin: 2);
        root.Children.Add(_hostBox);

        // ── 招待コード ──
        root.Children.Add(SeedDialogTheme.NewLabel(
            AccountMessages.LABEL_INVITE_CODE, SeedDialogTheme.DimText,
            SeedDialogTheme.NOTE_FONT_SIZE,
            topMargin: SeedDialogTheme.ROW_SPACING_PX));
        _inviteBox = SeedDialogTheme.NewTextBox(topMargin: 2);
        root.Children.Add(_inviteBox);

        // ── 保存先（入力欄 ＋ 参照ボタン）──
        root.Children.Add(SeedDialogTheme.NewLabel(
            AccountMessages.LABEL_DESTINATION, SeedDialogTheme.DimText,
            SeedDialogTheme.NOTE_FONT_SIZE,
            topMargin: SeedDialogTheme.ROW_SPACING_PX));

        var destinationRow = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        destinationRow.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        destinationRow.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });

        _destinationBox = SeedDialogTheme.NewTextBox();
        Grid.SetColumn(_destinationBox, 0);
        destinationRow.Children.Add(_destinationBox);

        var browseButton = SeedDialogTheme.NewButton(
            AccountMessages.BUTTON_BROWSE, OnBrowse, leftMargin: BROWSE_BUTTON_GAP_PX);
        Grid.SetColumn(browseButton, 1);
        destinationRow.Children.Add(browseButton);
        root.Children.Add(destinationRow);

        // ── 状態 ──
        _statusText = SeedDialogTheme.NewLabel(
            string.Empty, SeedDialogTheme.DimText,
            SeedDialogTheme.NOTE_FONT_SIZE,
            topMargin: SeedDialogTheme.ROW_SPACING_PX);
        root.Children.Add(_statusText);

        // ── ボタン ──
        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, SeedDialogTheme.ROW_SPACING_PX, 0, 0),
        };
        _joinButton = SeedDialogTheme.NewButton(
            AccountMessages.BUTTON_JOIN, OnJoin, isPrimary: true);
        buttons.Children.Add(_joinButton);
        buttons.Children.Add(SeedDialogTheme.NewButton(
            AccountMessages.BUTTON_CANCEL, OnCancel,
            leftMargin: SeedDialogTheme.BUTTON_GAP_PX));
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => _hostBox.Focus();
    }

    /// <summary>保存先フォルダを選ぶ。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = AccountMessages.FOLDER_PICKER_TITLE,
        };
        if (dialog.ShowDialog(this) != true) return;

        _destinationBox.Text = dialog.FolderName;
    }

    /// <summary>参加する。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        // アカウントが無ければ参加できない（自分の公開鍵を登録する操作なので）。
        if (!AccountService.HasAccount)
        {
            ShowStatus(AccountMessages.JOIN_ACCOUNT_REQUIRED, isError: true);
            return;
        }

        // 実行中は二重に押させない。中断ボタンは出さない
        // （Lore に実行中の操作を止める API が無く、押しても止まらないため）。
        SetBusy(true);
        ShowStatus(AccountMessages.JOIN_CLONING, isError: false);

        try
        {
            var request = new JoinProjectRequest(
                _hostBox.Text.Trim(), _inviteBox.Text.Trim(), _destinationBox.Text.Trim());

            var progress = new Progress<string>(text => ShowStatus(text, isError: false));

            var result = await AccountService.JoinProjectAsync(
                request,
                new LoreNativeCloner(),
                _projectFileExtension,
                clientFactory: null,
                progress: progress,
                cancellationToken: CancellationToken.None).ConfigureAwait(true);

            if (!result.Success)
            {
                ShowStatus(result.Message, isError: true);
                SetBusy(false);
                return;
            }

            // 参加できた。保存したアカウントは変わらないので読み直しは要らない。
            JoinedProjectFilePath = result.ProjectFilePath;
            DialogResult          = true;
        }
        catch (Exception ex)
        {
            // ProjectJoinService は例外を返さない契約だが、
            // 想定外（フォルダを作れない等）が来てもダイアログを落とさない。
            ShowStatus(ex.Message, isError: true);
            SetBusy(false);
        }
    }

    /// <summary>取り消す。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        JoinedProjectFilePath = null;
        DialogResult          = false;
    }

    /// <summary>実行中かどうかで入力を止める。</summary>
    /// <param name="busy">実行中か。</param>
    private void SetBusy(bool busy)
    {
        _joinButton.IsEnabled     = !busy;
        _hostBox.IsEnabled        = !busy;
        _inviteBox.IsEnabled      = !busy;
        _destinationBox.IsEnabled = !busy;
    }

    /// <summary>状態の 1 行を出す。</summary>
    /// <param name="message">文言。</param>
    /// <param name="isError">失敗なら真（色を変える）。</param>
    private void ShowStatus(string message, bool isError)
    {
        _statusText.Text       = message;
        _statusText.Foreground = isError
            ? SeedDialogTheme.ErrorText
            : SeedDialogTheme.DimText;
    }
}
