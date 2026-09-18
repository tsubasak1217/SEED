// ============================================================
//  ProjectAccountsWindow.cs — オーナー向けの操作（Version Control パネルから開く）
//
//  【役割】
//  契約（docs/seed_accounts.md 6 章）の「プロジェクトを守る（オーナー）」を 1 枚に収める。
//    ・このプロジェクトでアカウントを有効にする（bootstrap）
//    ・招待コードを発行する
//    ・参加者の一覧と失効
//
//  【出し分け】
//  ・オーナーでなければ、招待と参加者の節そのものを出さない
//    （押せないボタンを並べると「権限が無いのか壊れているのか」が分からない）。
//  ・オーナーが未登録のときだけ「有効にする」を出す。
//
//  【招待コードは 1 回しか出ない】
//  サーバはハッシュだけを保存し、コードは発行時に 1 回返すだけ（契約 3 章）。
//  閉じたら二度と見られないことを画面にはっきり書き、コピーボタンを添える。
//  **ログには絶対に出さない。**
//
//  【ループバック制限】
//  bootstrap はサーバと同じ PC からしか通らない。押す前に案内を出しておき、
//  失敗したときはサーバからのメッセージをそのまま見せる。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SEEDEditor.Accounts.Http;
using SEEDEditor.Theme;

namespace SEEDEditor.Accounts.Views;

/// <summary>
/// プロジェクトのアカウント設定・参加者管理のモーダルダイアログ。
/// </summary>
public sealed class ProjectAccountsWindow : Window
{
    /// <summary>ウィンドウ幅 [px]。</summary>
    private const double WINDOW_WIDTH_PX = 560;

    /// <summary>ウィンドウ高さ [px]。</summary>
    private const double WINDOW_HEIGHT_PX = 520;

    /// <summary>参加者一覧の高さ [px]。</summary>
    private const double MEMBER_LIST_HEIGHT_PX = 150;

    /// <summary>節と節の間の余白 [px]。</summary>
    private const double SECTION_SPACING_PX = 14;

    /// <summary>区切り線の太さ [px]。</summary>
    private const double SEPARATOR_THICKNESS_PX = 1;

    // ── 呼び出し側から受け取る情報 ──────────────────────────

    /// <summary>Lore のリモート URL（窓口のホストを決めるのに使う）。</summary>
    private readonly string _remoteUrl;

    /// <summary>リポジトリ ID（`.lore/id`）。</summary>
    private readonly string _repositoryId;

    /// <summary>プロジェクト表示名（bootstrap のときサーバへ伝える）。</summary>
    private readonly string _projectName;

    // ── 画面の部品 ──────────────────────────────────────────

    /// <summary>接続先とログイン状態の表示。</summary>
    private readonly TextBlock _headerText;

    /// <summary>オーナー登録の節。</summary>
    private readonly StackPanel _bootstrapSection;

    /// <summary>招待と参加者の節。</summary>
    private readonly StackPanel _ownerSection;

    /// <summary>発行された招待コードの表示欄（読み取り専用）。</summary>
    private readonly TextBox _inviteCodeBox;

    /// <summary>招待コードまわりの説明。</summary>
    private readonly TextBlock _inviteNoteText;

    /// <summary>参加者の一覧。</summary>
    private readonly ListBox _memberList;

    /// <summary>参加取り消しボタン（owner を選んでいる間は押せなくする）。</summary>
    private readonly Button _revokeButton;

    /// <summary>状態表示の行。</summary>
    private readonly TextBlock _statusText;

    /// <summary>取得済みの参加者（失効操作で名前を引くのに使う）。</summary>
    private readonly List<AuthMember> _members = new();

    /// <summary>
    /// ダイアログを構築する。
    /// </summary>
    /// <param name="remoteUrl">Lore のリモート URL。</param>
    /// <param name="repositoryId">リポジトリ ID（`.lore/id`）。</param>
    /// <param name="projectName">プロジェクトの表示名。</param>
    public ProjectAccountsWindow(string remoteUrl, string repositoryId, string projectName)
    {
        _remoteUrl    = remoteUrl    ?? string.Empty;
        _repositoryId = repositoryId ?? string.Empty;
        _projectName  = projectName  ?? string.Empty;

        SeedDialogTheme.ApplyWindowChrome(
            this, AccountMessages.OWNER_DIALOG_TITLE, WINDOW_WIDTH_PX, WINDOW_HEIGHT_PX);

        var root = new StackPanel
        {
            Margin = new Thickness(SeedDialogTheme.CONTENT_PADDING_PX),
        };

        // ── ヘッダー（自分が誰で、どこへ繋がっているか）──
        _headerText = SeedDialogTheme.NewLabel(string.Empty);
        root.Children.Add(_headerText);

        root.Children.Add(NewSeparator());

        // ── オーナー登録の節 ──
        _bootstrapSection = new StackPanel();
        _bootstrapSection.Children.Add(SeedDialogTheme.NewLabel(
            AccountMessages.OWNER_BOOTSTRAP_LOOPBACK_NOTE,
            SeedDialogTheme.DimText, SeedDialogTheme.NOTE_FONT_SIZE));
        _bootstrapSection.Children.Add(SeedDialogTheme.NewButton(
            AccountMessages.OWNER_BOOTSTRAP_BUTTON, OnBootstrap, isPrimary: true));
        root.Children.Add(_bootstrapSection);

        // ── 招待と参加者の節 ──
        _ownerSection = new StackPanel();

        var inviteRow = new StackPanel { Orientation = Orientation.Horizontal };
        inviteRow.Children.Add(SeedDialogTheme.NewButton(
            AccountMessages.OWNER_INVITE_BUTTON, OnCreateInvite, isPrimary: true));
        inviteRow.Children.Add(SeedDialogTheme.NewButton(
            AccountMessages.BUTTON_COPY, OnCopyInvite,
            leftMargin: SeedDialogTheme.BUTTON_GAP_PX));
        _ownerSection.Children.Add(inviteRow);

        _inviteCodeBox = SeedDialogTheme.NewTextBox(
            topMargin: SeedDialogTheme.ROW_SPACING_PX);
        _inviteCodeBox.IsReadOnly = true;
        _ownerSection.Children.Add(_inviteCodeBox);

        _inviteNoteText = SeedDialogTheme.NewLabel(
            string.Empty, SeedDialogTheme.DimText,
            SeedDialogTheme.NOTE_FONT_SIZE, topMargin: 4);
        _ownerSection.Children.Add(_inviteNoteText);

        _ownerSection.Children.Add(NewSeparator());

        _ownerSection.Children.Add(SeedDialogTheme.NewLabel(
            AccountMessages.OWNER_MEMBERS_TITLE));

        _memberList = new ListBox
        {
            Height          = MEMBER_LIST_HEIGHT_PX,
            Background      = SeedDialogTheme.Field,
            Foreground      = SeedDialogTheme.Text,
            BorderBrush     = SeedDialogTheme.FieldBorder,
            BorderThickness = new Thickness(SeedDialogTheme.BORDER_THICKNESS_PX),
            FontSize        = SeedDialogTheme.BODY_FONT_SIZE,
            Margin          = new Thickness(0, 4, 0, 0),
        };
        // owner の権限は失効できない（契約 3 章）。選んだ行に合わせてボタンを出し入れする。
        _memberList.SelectionChanged += (_, _) => SyncRevokeButton();
        _ownerSection.Children.Add(_memberList);

        var memberButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 6, 0, 0),
        };
        memberButtons.Children.Add(SeedDialogTheme.NewButton(
            AccountMessages.OWNER_REFRESH_BUTTON, OnRefreshMembers));
        _revokeButton = SeedDialogTheme.NewButton(
            AccountMessages.OWNER_REVOKE_BUTTON, OnRevokeMember,
            leftMargin: SeedDialogTheme.BUTTON_GAP_PX);
        memberButtons.Children.Add(_revokeButton);
        _ownerSection.Children.Add(memberButtons);

        root.Children.Add(_ownerSection);

        // ── 状態 ──
        _statusText = SeedDialogTheme.NewLabel(
            string.Empty, SeedDialogTheme.DimText,
            SeedDialogTheme.NOTE_FONT_SIZE, topMargin: SECTION_SPACING_PX);
        root.Children.Add(_statusText);

        // ── 閉じる ──
        var closeRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, SeedDialogTheme.ROW_SPACING_PX, 0, 0),
        };
        closeRow.Children.Add(SeedDialogTheme.NewButton(
            AccountMessages.BUTTON_CLOSE, (_, _) => Close()));
        root.Children.Add(closeRow);

        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Loaded += OnDialogLoaded;
    }

    // ── 画面の同期 ──────────────────────────────────────────

    /// <summary>開いたら状態を反映し、オーナーなら参加者を読む。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        SyncSections();
        if (IsOwner()) await RefreshMembersAsync().ConfigureAwait(true);
    }

    /// <summary>現在の状態に合わせて節の出し分けとヘッダーを更新する。</summary>
    private void SyncSections()
    {
        var state   = AccountService.AuthState;
        var isOwner = IsOwner();

        _headerText.Text = string.Format(
            CultureInfo.CurrentCulture,
            VersionControl.VersionControlMessages.PANEL_CONNECTION_FORMAT,
            state.Description,
            string.IsNullOrWhiteSpace(_remoteUrl)
                ? VersionControl.VersionControlMessages.PANEL_REMOTE_UNKNOWN
                : _remoteUrl);

        // オーナーなら招待と参加者、そうでなければ「有効にする」だけを出す。
        _bootstrapSection.Visibility = isOwner ? Visibility.Collapsed : Visibility.Visible;
        _ownerSection.Visibility     = isOwner ? Visibility.Visible   : Visibility.Collapsed;

        // 何も選んでいない状態から始まるので、取り消しボタンは押せない。
        SyncRevokeButton();
    }

    /// <summary>
    /// 選んでいる参加者に合わせて、参加取り消しボタンの有効・無効を決める。
    ///
    /// <para>
    /// owner の権限は失効できない（契約 3 章。自分自身を含む）。
    /// 押せるように見せてサーバに断られるより、押せないほうが分かりやすい。
    /// </para>
    /// </summary>
    private void SyncRevokeButton()
    {
        var index = _memberList.SelectedIndex;
        if (index < 0 || index >= _members.Count)
        {
            _revokeButton.IsEnabled = false;
            return;
        }

        _revokeButton.IsEnabled = !string.Equals(
            _members[index].Role, AccountSettings.ROLE_OWNER, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>自分がこのリポジトリのオーナーか。</summary>
    private static bool IsOwnerOf(string repositoryId)
        => AccountService.CurrentSession?.IsOwnerOf(repositoryId) ?? false;

    /// <summary>自分がこのリポジトリのオーナーか。</summary>
    private bool IsOwner() => IsOwnerOf(_repositoryId);

    // ── 操作 ────────────────────────────────────────────────

    /// <summary>このプロジェクトでアカウントを有効にする（オーナー登録）。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnBootstrap(object sender, RoutedEventArgs e)
    {
        if (!CheckPrerequisites()) return;

        var identity = AccountService.Identity;

        await RunGatewayAsync(async (client, token) =>
        {
            var response = await client.BootstrapAsync(
                new AuthBootstrapRequest
                {
                    Name         = identity.Name,
                    PublicKey    = identity.PublicKeyBase64Url,
                    RepositoryId = _repositoryId,
                    ProjectName  = _projectName,
                },
                token).ConfigureAwait(true);

            ShowStatus(string.Format(
                CultureInfo.CurrentCulture,
                AccountMessages.OWNER_BOOTSTRAP_OK_FORMAT, response.Name), isError: false);

            // オーナーになったので、権限入りのトークンを取り直す
            // （今持っているトークンには resources が入っていない）。
            await AccountService.AttachToProjectAsync(_remoteUrl).ConfigureAwait(true);

            SyncSections();
            if (IsOwner()) await RefreshMembersAsync().ConfigureAwait(true);
        },
        // すでにオーナーが居るのは「異常」ではなく、よくある状況。
        // それ以外（loopback_only など）は AuthFailureText に任せる。
        ex => ex.Is(AuthErrorCodes.OWNER_EXISTS)
            ? AccountMessages.OWNER_ALREADY_EXISTS
            : AuthFailureText.Describe(ex)).ConfigureAwait(true);
    }

    /// <summary>招待コードを発行する。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnCreateInvite(object sender, RoutedEventArgs e)
    {
        if (!CheckPrerequisites()) return;

        var token = AccountService.CurrentSession?.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            ShowStatus(AccountMessages.OWNER_PERMISSION_REQUIRED, isError: true);
            return;
        }

        await RunGatewayAsync(async (client, cancellation) =>
        {
            var response = await client.CreateInviteAsync(
                token!,
                new AuthInviteRequest
                {
                    RepositoryId   = _repositoryId,
                    Role           = AccountSettings.ROLE_MEMBER,
                    ExpiresInHours = AccountSettings.Default.InviteExpiresInHours,
                },
                cancellation).ConfigureAwait(true);

            // ★コードは画面にだけ出す。ログへは絶対に書かない。
            _inviteCodeBox.Text  = response.InviteCode;
            _inviteNoteText.Text = AccountMessages.OWNER_INVITE_CREATED;
            ShowStatus(AccountMessages.OWNER_INVITE_CREATED, isError: false);
        }).ConfigureAwait(true);
    }

    /// <summary>発行済みの招待コードをクリップボードへ写す。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnCopyInvite(object sender, RoutedEventArgs e)
    {
        if (_inviteCodeBox.Text.Length == 0) return;

        try
        {
            Clipboard.SetText(_inviteCodeBox.Text);
            ShowStatus(AccountMessages.OWNER_INVITE_COPIED, isError: false);
        }
        catch (Exception ex)
        {
            // 他のアプリがクリップボードを掴んでいると失敗することがある。
            ShowStatus(ex.Message, isError: true);
        }
    }

    /// <summary>参加者の一覧を取り直す。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnRefreshMembers(object sender, RoutedEventArgs e)
        => await RefreshMembersAsync().ConfigureAwait(true);

    /// <summary>参加者の一覧を読み、リストへ流し込む。</summary>
    private async Task RefreshMembersAsync()
    {
        var token = AccountService.CurrentSession?.AccessToken;
        if (string.IsNullOrWhiteSpace(token)) return;

        await RunGatewayAsync(async (client, cancellation) =>
        {
            var members = await client.GetMembersAsync(token!, _repositoryId, cancellation)
                                      .ConfigureAwait(true);

            _members.Clear();
            _memberList.Items.Clear();

            foreach (var member in members)
            {
                _members.Add(member);
                _memberList.Items.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    AccountMessages.OWNER_MEMBER_ROW_FORMAT,
                    member.Name, member.Role, member.Status));
            }

            if (_members.Count == 0) ShowStatus(AccountMessages.OWNER_NO_MEMBERS, isError: false);

            // 一覧を作り直すと選択が外れるので、ボタンの状態も合わせ直す。
            SyncRevokeButton();
        }).ConfigureAwait(true);
    }

    /// <summary>選んだ参加者を失効させる。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnRevokeMember(object sender, RoutedEventArgs e)
    {
        var index = _memberList.SelectedIndex;
        if (index < 0 || index >= _members.Count) return;

        var member = _members[index];

        // ★owner の権限は失効できない（契約 3 章。自分自身を含む）。
        //   サーバは 409 owner_exists で断るが、押す前に止める方が分かりやすい。
        if (string.Equals(member.Role, AccountSettings.ROLE_OWNER,
                          StringComparison.OrdinalIgnoreCase))
        {
            // 自分かどうかで言い方を変える（「自分は消せない」の方が納得しやすい）。
            ShowStatus(
                string.Equals(member.Name, AccountService.Identity.Name, StringComparison.Ordinal)
                    ? AccountMessages.OWNER_CANNOT_REVOKE_SELF
                    : AccountMessages.OWNER_CANNOT_REVOKE_OWNER,
                isError: true);
            return;
        }

        // 取り返しのつかない操作なので確認を挟む（ヘッドレスでは
        // EditorDialogs が破壊的でない側＝「いいえ」を返す）。
        var confirm = SEEDEditor.Headless.EditorDialogs.Show(
            string.Format(CultureInfo.CurrentCulture,
                          AccountMessages.OWNER_MEMBER_REVOKED_FORMAT, member.Name),
            AccountMessages.OWNER_DIALOG_TITLE,
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var token = AccountService.CurrentSession?.AccessToken;
        if (string.IsNullOrWhiteSpace(token)) return;

        await RunGatewayAsync(async (client, cancellation) =>
        {
            var response = await client.RevokeMemberAsync(
                token!,
                new AuthRevokeRequest { RepositoryId = _repositoryId, Name = member.Name },
                cancellation).ConfigureAwait(true);

            ShowStatus(string.Format(
                CultureInfo.CurrentCulture,
                AccountMessages.OWNER_MEMBER_REVOKED_FORMAT, response.Name), isError: false);

            await RefreshMembersAsync().ConfigureAwait(true);
        },
        // owner の権限を失効させようとしたときも owner_exists が返る。
        ex => ex.Is(AuthErrorCodes.OWNER_EXISTS)
            ? AccountMessages.OWNER_CANNOT_REVOKE_OWNER
            : AuthFailureText.Describe(ex)).ConfigureAwait(true);
    }

    // ── 共通ヘルパー ────────────────────────────────────────

    /// <summary>
    /// アカウントとリポジトリ ID が揃っているかを確かめ、足りなければ理由を出す。
    /// </summary>
    /// <returns>揃っていれば真。</returns>
    private bool CheckPrerequisites()
    {
        if (!AccountService.HasAccount)
        {
            ShowStatus(AccountMessages.ACCOUNT_NOT_CREATED, isError: true);
            return false;
        }

        if (_repositoryId.Length == 0)
        {
            ShowStatus(AccountMessages.OWNER_REPOSITORY_ID_MISSING, isError: true);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 窓口クライアントを作って処理を走らせ、失敗を 1 行のメッセージへ畳む。
    /// </summary>
    /// <param name="action">クライアントを使う処理。</param>
    /// <param name="describeError">
    /// 失敗の言い換え（省略時はサーバのメッセージをそのまま出す）。
    /// </param>
    private async Task RunGatewayAsync(
        Func<IAuthGatewayClient, CancellationToken, Task> action,
        Func<AuthGatewayException, string>? describeError = null)
    {
        var client = AccountService.CreateGatewayClient(_remoteUrl);
        if (client is null)
        {
            ShowStatus(AccountMessages.GATEWAY_ADDRESS_INVALID, isError: true);
            return;
        }

        try
        {
            await action(client, CancellationToken.None).ConfigureAwait(true);
        }
        catch (AuthGatewayException ex)
        {
            // 既定の言い換えは AuthFailureText の 1 か所。
            // 操作ごとに意味が変わるコードだけを呼び出し側が上書きする。
            var message = describeError?.Invoke(ex) ?? AuthFailureText.Describe(ex);
            ShowStatus(message, isError: true);
        }
        catch (Exception ex)
        {
            // 想定外でもダイアログを落とさない。
            ShowStatus(ex.Message, isError: true);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>状態の 1 行を出す。</summary>
    /// <param name="message">文言。</param>
    /// <param name="isError">失敗なら真。</param>
    private void ShowStatus(string message, bool isError)
    {
        _statusText.Text       = message;
        _statusText.Foreground = isError
            ? SeedDialogTheme.ErrorText
            : SeedDialogTheme.SuccessText;
    }

    /// <summary>節の区切り線を作る。</summary>
    private static Border NewSeparator()
        => new()
        {
            Height     = SEPARATOR_THICKNESS_PX,
            Background = SeedDialogTheme.FieldBorder,
            Margin     = new Thickness(0, SECTION_SPACING_PX, 0, SECTION_SPACING_PX),
        };
}
