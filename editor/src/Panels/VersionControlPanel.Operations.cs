// ============================================================
//  VersionControlPanel.Operations.cs — パネルから呼ぶ操作
//
//  【役割】
//  「最新を取得」「送信」「競合の解決」「ブランチ」「履歴」「ロック」の
//  ボタン・メニューのハンドラ。処理そのものは中核層（プロバイダ）が行うので、
//  ここが持つのは「確認を挟むか」と「終わったあと何を取り直すか」だけ。
//
//  【モーダルを出す場所を限定する】
//  ダイアログは **取り返しのつかない操作の確認** だけに使う:
//    ・「リモートを採用」… 自分の変更が消える
//    ・ブランチの切り替え … 未送信の変更があると失われ得る
//  それ以外（成功・失敗・競合の通知）はパネル内の 1 行メッセージで済ませる。
//  ダイアログは必ず EditorDialogs 経由にする（ヘッドレスで UI スレッドが
//  永久に止まるのを避けるため）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SEEDEditor.Headless;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Presentation;
using VcRows = SEEDEditor.Panels.VersionControl;

namespace SEEDEditor.Panels;

public partial class VersionControlPanel
{
    // ============================================================
    //  主操作
    // ============================================================

    /// <summary>
    /// 「最新を取得」。取得後は必ず状態を取り直す
    /// （競合が起きていれば一覧の最上部へ出す必要があるため）。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnFetchLatestClick(object sender, RoutedEventArgs e)
    {
        var result = await RunAsync(
            VersionControlOperation.FetchLatest,
            async () => await VersionControlService.Provider
                                                   .FetchLatestAsync()
                                                   .ConfigureAwait(true));
        if (result is null) return;

        await ReloadStatusKeepingNoticeAsync();
    }

    /// <summary>
    /// 「送信」。成功したらメッセージ欄を空にして、状態を取り直す。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnSubmitClick(object sender, RoutedEventArgs e)
    {
        // Enter キー経由でも来るので、ここでも押せる状態か確かめる。
        if (!_state.CanSubmit) return;

        var message = TxtMessage.Text;

        var result = await RunAsync(
            VersionControlOperation.Submit,
            async () => await VersionControlService.Provider
                                                   .SubmitAsync(message)
                                                   .ConfigureAwait(true));
        if (result is null) return;

        // 送れたときだけ入力を消す。失敗時に消すと、書いた文を失わせてしまう。
        if (result.IsSuccess)
        {
            TxtMessage.Clear();
            _state.ClearCommitMessage();
        }

        await ReloadStatusKeepingNoticeAsync();
    }

    // ============================================================
    //  競合の解決
    // ============================================================

    /// <summary>「すべて自分の変更を残す」。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnResolveAllKeepMine(object sender, RoutedEventArgs e)
        => await ResolveAsync(AllConflictPaths(), ConflictResolutionChoice.KeepMine);

    /// <summary>「すべてリモートを採用」。自分の変更が消えるので確認を挟む。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnResolveAllTakeRemote(object sender, RoutedEventArgs e)
        => await ResolveAsync(AllConflictPaths(), ConflictResolutionChoice.TakeRemote);

    /// <summary>1 件だけ「自分の変更を残す」。</summary>
    /// <param name="sender">送信元（Tag に対象の行が入っている）。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnResolveFileKeepMine(object sender, RoutedEventArgs e)
        => await ResolveAsync(PathsFromButton(sender), ConflictResolutionChoice.KeepMine);

    /// <summary>1 件だけ「リモートを採用」。自分の変更が消えるので確認を挟む。</summary>
    /// <param name="sender">送信元（Tag に対象の行が入っている）。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnResolveFileTakeRemote(object sender, RoutedEventArgs e)
        => await ResolveAsync(PathsFromButton(sender), ConflictResolutionChoice.TakeRemote);

    /// <summary>いま未解決の競合になっているファイルのパスをすべて集める。</summary>
    private IReadOnlyList<string> AllConflictPaths()
        => _state.Status?.UnresolvedConflicts.Select(c => c.Path).ToList()
           ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>ボタンの Tag に入っている行から対象パスを取り出す。</summary>
    /// <param name="sender">クリックされたボタン。</param>
    private static IReadOnlyList<string> PathsFromButton(object sender)
    {
        if (sender is Button { Tag: VcRows.ChangeRowItem row }
            && !row.IsHeader
            && row.RelativePath.Length > 0)
        {
            return new[] { row.RelativePath };
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// 競合を解決する。「リモートを採用」だけは取り返しがつかないので確認する。
    /// </summary>
    /// <param name="paths">対象のリポジトリ相対パス。</param>
    /// <param name="choice">利用者の選択。</param>
    private async Task ResolveAsync(
        IReadOnlyList<string> paths, ConflictResolutionChoice choice)
    {
        if (!_state.CanResolveConflicts || paths.Count == 0) return;

        // 「リモートを採用」= 自分の変更を捨てる。ここだけモーダルで確認する。
        if (choice == ConflictResolutionChoice.TakeRemote && !ConfirmTakeRemote(paths.Count))
        {
            return;
        }

        var result = await RunAsync(
            VersionControlOperation.Resolve,
            async () => await VersionControlService.Provider
                                                   .ResolveConflictsAsync(paths, choice)
                                                   .ConfigureAwait(true));
        if (result is null) return;

        // 解決後は必ず状態を取り直す（残りの競合件数が変わる）。
        await ReloadStatusKeepingNoticeAsync();
    }

    /// <summary>
    /// 「リモートを採用」の確認。ヘッドレスでは必ず「いいえ」になる
    /// （EditorDialogs が破壊的でない側を返すため）。
    /// </summary>
    /// <param name="count">対象件数。</param>
    /// <returns>続行してよいか。</returns>
    private static bool ConfirmTakeRemote(int count)
        => EditorDialogs.Show(
               string.Format(
                   VersionControlMessages.PANEL_RESOLVE_TAKE_REMOTE_CONFIRM_FORMAT, count),
               VersionControlMessages.PANEL_RESOLVE_TAKE_REMOTE_CONFIRM_TITLE,
               MessageBoxButton.YesNo,
               MessageBoxImage.Warning) == MessageBoxResult.Yes;

    // ============================================================
    //  ブランチ
    // ============================================================

    /// <summary>
    /// コンボを開いたときにブランチ一覧を取り直す
    /// （他の人が作ったブランチは開くまで分からないため）。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnBranchDropDownOpened(object sender, EventArgs e)
    {
        if (!_state.CanChangeBranch) return;
        await ReloadBranchesAsync();
    }

    /// <summary>
    /// ブランチ一覧を取り直してコンボへ流し込む。
    /// 末尾に「新しいブランチ…」を足す。
    /// </summary>
    private async Task ReloadBranchesAsync()
    {
        var result = await RunAsync(
            VersionControlOperation.Branch,
            async () => await VersionControlService.Provider
                                                   .GetBranchesAsync()
                                                   .ConfigureAwait(true));

        // ブランチ一覧はサーバに繋がらないと取れないことがある。
        // 取れなくても現在のブランチ名だけは状態から分かるので、それを出す。
        var branches = (result as VersionControlResult<IReadOnlyList<BranchInfo>>)?.Value;
        FillBranchCombo(branches);

        // 一覧の取得は利用者が頼んだ操作ではないので、成功メッセージは出さない。
        if (result is not null && result.IsSuccess)
        {
            _state.SetNotice(VersionControlNotice.None);
            SyncControls();
        }
    }

    /// <summary>
    /// ブランチコンボの中身を差し替える。
    /// </summary>
    /// <param name="branches">取得できたブランチ（取れなければ null）。</param>
    private void FillBranchCombo(IReadOnlyList<BranchInfo>? branches)
    {
        _suppressBranchEvent = true;
        try
        {
            CmbBranch.Items.Clear();

            var current = _state.BranchName;
            var names   = new List<string>();

            if (branches is not null)
            {
                names.AddRange(branches.Select(b => b.Name).Where(n => n.Length > 0));
            }

            // 一覧に現在のブランチが無い場合（オフライン等）でも必ず出す。
            if (current.Length > 0
                && !names.Contains(current, StringComparer.Ordinal))
            {
                names.Insert(0, current);
            }

            ComboBoxItem? selected = null;
            foreach (var name in names)
            {
                var item = new ComboBoxItem { Content = name, Tag = name };
                CmbBranch.Items.Add(item);
                if (string.Equals(name, current, StringComparison.Ordinal)) selected = item;
            }

            // 末尾に「新しいブランチ…」。区切り線で操作と一覧を分ける。
            if (CmbBranch.Items.Count > 0) CmbBranch.Items.Add(new Separator());
            CmbBranch.Items.Add(new ComboBoxItem
            {
                Content = VersionControlMessages.PANEL_BRANCH_NEW_ITEM,
                Tag     = BRANCH_ITEM_NEW_TAG,
            });

            CmbBranch.SelectedItem = selected;
        }
        finally
        {
            _suppressBranchEvent = false;
        }
    }

    /// <summary>
    /// ブランチが選ばれたとき。「新しいブランチ…」なら作成、
    /// 別のブランチなら切り替える。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnBranchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBranchEvent) return;
        if (CmbBranch.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

        if (string.Equals(tag, BRANCH_ITEM_NEW_TAG, StringComparison.Ordinal))
        {
            await CreateBranchAsync();
            return;
        }

        // 同じブランチを選び直しただけなら何もしない。
        if (string.Equals(tag, _state.BranchName, StringComparison.Ordinal)) return;

        await SwitchBranchAsync(tag);
    }

    /// <summary>
    /// 新しいブランチを作って切り替える。
    /// 名前は入力ダイアログで尋ねる（取り消されたら何もしない）。
    /// </summary>
    private async Task CreateBranchAsync()
    {
        // 「新しいブランチ…」の選択状態を残さない（次に開いたとき紛らわしい）。
        RestoreBranchSelection();

        var name = EditorDialogs.ShowTextInput(
            VersionControlMessages.PANEL_BRANCH_NEW_DIALOG_PROMPT,
            VersionControlMessages.PANEL_BRANCH_NEW_DIALOG_TITLE,
            initialText: null,
            owner: Window.GetWindow(this));

        if (string.IsNullOrWhiteSpace(name)) return;

        var created = await RunAsync(
            VersionControlOperation.Branch,
            async () => await VersionControlService.Provider
                                                   .CreateBranchAsync(name)
                                                   .ConfigureAwait(true));

        if (created is null || !created.IsSuccess) return;

        // 作ったら切り替える（作っただけで切り替わらないと利用者が混乱する）。
        await SwitchBranchAsync(name, skipConfirm: true);
    }

    /// <summary>
    /// ブランチを切り替える。未送信の変更があるときは確認を挟む。
    /// </summary>
    /// <param name="name">切り替え先のブランチ名。</param>
    /// <param name="skipConfirm">確認を省くか（作成直後は手元の変更をそのまま持ち込む）。</param>
    private async Task SwitchBranchAsync(string name, bool skipConfirm = false)
    {
        // 未送信の変更があると、切り替えで失われ得る。ここは確認する。
        if (!skipConfirm && _state.ChangeCount > 0 && !ConfirmSwitchBranch(_state.ChangeCount))
        {
            RestoreBranchSelection();
            return;
        }

        var result = await RunAsync(
            VersionControlOperation.Branch,
            async () => await VersionControlService.Provider
                                                   .SwitchBranchAsync(name)
                                                   .ConfigureAwait(true));

        if (result is null) return;

        // 切り替え後は中身が丸ごと変わる。状態も一覧も取り直す。
        await ReloadStatusKeepingNoticeAsync();
        await ReloadActiveTabAsync();

        // コンボの選択を実際のブランチへ合わせ直す（失敗していれば元へ戻る）。
        FillBranchCombo(null);
    }

    /// <summary>
    /// ブランチ切り替えの確認。ヘッドレスでは必ず「いいえ」になる。
    /// </summary>
    /// <param name="changeCount">未送信の変更件数。</param>
    /// <returns>続行してよいか。</returns>
    private static bool ConfirmSwitchBranch(int changeCount)
        => EditorDialogs.Show(
               string.Format(
                   VersionControlMessages.PANEL_BRANCH_SWITCH_CONFIRM_FORMAT, changeCount),
               VersionControlMessages.PANEL_BRANCH_SWITCH_CONFIRM_TITLE,
               MessageBoxButton.YesNo,
               MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <summary>コンボの選択を現在のブランチへ戻す（切り替えをやめたとき）。</summary>
    private void RestoreBranchSelection()
    {
        _suppressBranchEvent = true;
        try
        {
            var current = _state.BranchName;
            CmbBranch.SelectedItem = CmbBranch.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag is string tag
                                     && string.Equals(tag, current, StringComparison.Ordinal));
        }
        finally
        {
            _suppressBranchEvent = false;
        }
    }

    // ============================================================
    //  タブ（履歴 / ロック）
    // ============================================================

    /// <summary>
    /// タブが切り替わったら、その中身を取り直す。
    /// 履歴とロックはサーバ往復が要るので、**開いたときだけ**取りに行く。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // TabControl の中の ListBox の選択変更も同じイベントで上がってくる。
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (!_state.IsAvailable) return;

        await ReloadActiveTabAsync();
    }

    /// <summary>いま開いているタブの中身を取り直す。</summary>
    private async Task ReloadActiveTabAsync()
    {
        if (ReferenceEquals(Tabs.SelectedItem, TabHistory)) await ReloadHistoryAsync();
        else if (ReferenceEquals(Tabs.SelectedItem, TabLocks)) await ReloadLocksAsync();
    }

    /// <summary>
    /// 履歴を取り直す。サーバに繋がらないときは一覧を消して案内だけを出す。
    /// </summary>
    private async Task ReloadHistoryAsync()
    {
        var result = await RunAsync(
            VersionControlOperation.History,
            async () => await VersionControlService.Provider
                                                   .GetHistoryAsync()
                                                   .ConfigureAwait(true));
        if (result is null) return;

        _historyRows.Clear();

        if (result.Outcome == VersionControlOutcome.RequiresConnection)
        {
            ShowTabMessage(TxtHistoryMessage,
                           VersionControlMessages.PANEL_HISTORY_REQUIRES_CONNECTION);

            // 案内はタブの中に出しているので、1 行メッセージは重複させない。
            _state.SetNotice(VersionControlNotice.None);
            SyncControls();
            return;
        }

        if (!result.IsSuccess)
        {
            ShowTabMessage(TxtHistoryMessage, result.Message);
            return;
        }

        var revisions = (result as VersionControlResult<IReadOnlyList<RevisionInfo>>)?.Value;
        if (revisions is not null)
        {
            foreach (var revision in revisions)
            {
                _historyRows.Add(new VcRows.HistoryRowItem(revision));
            }
        }

        ShowTabMessage(TxtHistoryMessage,
                       _historyRows.Count == 0
                           ? VersionControlMessages.PANEL_HISTORY_EMPTY
                           : null);

        // 取得できたこと自体は利用者が頼んだ操作ではないので、静かに閉じる。
        _state.SetNotice(VersionControlNotice.None);
        SyncControls();
    }

    /// <summary>
    /// ロック一覧を取り直す。
    /// </summary>
    private async Task ReloadLocksAsync()
    {
        var result = await RunAsync(
            VersionControlOperation.Lock,
            async () => await VersionControlService.Provider
                                                   .Locks
                                                   .ListAsync()
                                                   .ConfigureAwait(true));
        if (result is null) return;

        _lockRows.Clear();

        if (result.Outcome == VersionControlOutcome.RequiresConnection)
        {
            ShowTabMessage(TxtLockMessage,
                           VersionControlMessages.PANEL_NOTICE_REQUIRES_CONNECTION);
            _state.SetNotice(VersionControlNotice.None);
            SyncControls();
            return;
        }

        if (!result.IsSuccess)
        {
            ShowTabMessage(TxtLockMessage, result.Message);
            return;
        }

        var locks = (result as VersionControlResult<IReadOnlyList<LockInfo>>)?.Value;
        if (locks is not null)
        {
            foreach (var info in locks)
            {
                // 照会と違い、一覧には「ロックされていないもの」は返らない想定だが、
                // 万一混ざっても行としては意味が無いので落とす。
                if (info.Holder == LockHolder.None) continue;
                _lockRows.Add(new VcRows.LockRowItem(info));
            }
        }

        ShowTabMessage(TxtLockMessage,
                       _lockRows.Count == 0 ? VersionControlMessages.PANEL_LOCKS_EMPTY : null);

        _state.SetNotice(VersionControlNotice.None);
        SyncControls();
    }

    /// <summary>タブ内の案内文を出す / 消す。</summary>
    /// <param name="target">対象の TextBlock。</param>
    /// <param name="message">出す文言（null なら隠す）。</param>
    private static void ShowTabMessage(TextBlock target, string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            target.Visibility = Visibility.Collapsed;
            return;
        }

        target.Text       = message;
        target.Visibility = Visibility.Visible;
    }

    // ============================================================
    //  ロックの取得・解放
    // ============================================================

    /// <summary>変更一覧で選んだファイルをロックする。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnLockSelected(object sender, RoutedEventArgs e)
    {
        var paths = SelectedChangePaths();
        if (paths.Count == 0 || !_state.CanChangeLocks) return;

        await RunAsync(
            VersionControlOperation.Lock,
            async () => await VersionControlService.Provider
                                                   .Locks
                                                   .AcquireAsync(paths)
                                                   .ConfigureAwait(true));

        await ReloadLocksIfVisibleAsync();
    }

    /// <summary>変更一覧で選んだファイルのロックを解除する。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnUnlockSelected(object sender, RoutedEventArgs e)
    {
        var paths = SelectedChangePaths();
        if (paths.Count == 0 || !_state.CanChangeLocks) return;

        await RunAsync(
            VersionControlOperation.Lock,
            async () => await VersionControlService.Provider
                                                   .Locks
                                                   .ReleaseAsync(paths)
                                                   .ConfigureAwait(true));

        await ReloadLocksIfVisibleAsync();
    }

    /// <summary>ロックタブの解除ボタン。</summary>
    /// <param name="sender">送信元（Tag に対象の行が入っている）。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnReleaseLockClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VcRows.LockRowItem row }) return;
        if (!_state.CanChangeLocks) return;

        await RunAsync(
            VersionControlOperation.Lock,
            async () => await VersionControlService.Provider
                                                   .Locks
                                                   .ReleaseAsync(new[] { row.RelativePath })
                                                   .ConfigureAwait(true));

        await ReloadLocksAsync();
    }

    /// <summary>ロックタブが開いているときだけ一覧を取り直す（無駄な往復を避ける）。</summary>
    private async Task ReloadLocksIfVisibleAsync()
    {
        if (ReferenceEquals(Tabs.SelectedItem, TabLocks)) await ReloadLocksAsync();
    }

    // ============================================================
    //  共通
    // ============================================================

    /// <summary>
    /// 状態だけを取り直し、**直前の操作の 1 行メッセージは残す**。
    ///
    /// <para>
    /// 「送信しました」の直後に「状態を取得しました」で上書きされると、
    /// 利用者は自分の操作が成功したのか分からなくなる。
    /// </para>
    /// </summary>
    private async Task ReloadStatusKeepingNoticeAsync()
    {
        var keep = _state.Notice;

        await RefreshAsync(StatusRefreshMode.ScanOffline);

        _state.SetNotice(keep);
        SyncControls();
    }
}
