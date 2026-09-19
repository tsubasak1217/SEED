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
//
//  【サーバ往復を増やさない】
//  ロックと履歴は「その節が開いているとき」しか取りに行かない。
//  閉じている節のために通信すると、パネルを出しているだけで
//  gRPC 往復（実測 350 ms）が積み上がる。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SEEDEditor.Headless;
using SEEDEditor.Panels.VersionControl.MergeEditor;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Merge;
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
    /// （競合が起きていれば競合の節へ出す必要があるため）。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnFetchLatestClick(object sender, RoutedEventArgs e)
    {
        if (!_state.CanFetchLatest) return;

        var result = await RunAsync(
            VersionControlOperation.FetchLatest,
            async () => await VersionControlService.Provider
                                                   .FetchLatestAsync()
                                                   .ConfigureAwait(true));
        if (result is null) return;

        await ReloadStatusKeepingNoticeAsync(result);
    }

    /// <summary>
    /// 「送信」。成功したらメッセージ欄を空にして、状態を取り直す。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnSubmitClick(object sender, RoutedEventArgs e)
    {
        // Ctrl+Enter 経由でもヘッダーのアイコンからも来るので、ここでも押せる状態か確かめる。
        if (!_state.CanSubmit) return;

        var message = TxtMessage.Text;

        // ── 送信ゲート ─────────────────────────────────────────
        // サーバの push フックは「どのファイルが変わったか」を受け取れないため、
        // 他の人がロック中のファイルを送ってしまうのを防げるのはここだけ。
        //
        // ★対象は画面に出ている一覧ではなく、**いま走査し直した結果**を使う。
        //   画面の一覧は最後に更新した時点のもので、そのあとに保存されたファイルが
        //   抜けている。抜けたファイルは確かめられないまま送られてしまう。
        //   走査（ScanOffline）はサーバ往復を伴わない（実測 0.11 秒）。
        var scan = await VersionControlService.Provider
                                              .GetStatusAsync(StatusRefreshMode.ScanOffline)
                                              .ConfigureAwait(true);
        var changedPaths = scan.Value?.Changes.Select(c => c.Path).ToList()
                           ?? new List<string>();
        if (!scan.IsSuccess)
        {
            // 変更一覧を作れなければロックも確かめられない。ここでは止めず、
            // 続く送信そのものの失敗として扱う（情報が無い状態で作業を止めない）。
            EditorLog.Write($"[ロック] 送信前の走査に失敗しました: {scan.Outcome} {scan.Message}");
        }

        // ConfigureAwait(true) で UI スレッドへ戻ってから提示する（モーダルのため）。
        var gate = await SEEDEditor.VersionControl.Locking.LockGatekeeper
                                   .DecideForSubmitAsync(changedPaths)
                                   .ConfigureAwait(true);
        SEEDEditor.VersionControl.Locking.LockGatekeeper.Present(gate);
        if (!gate.CanProceed)
        {
            // 誰が押さえているのかをその場で見せる（止めた理由を確かめられるように）。
            await ReloadLocksIfVisibleAsync();
            return;
        }

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

        await ReloadStatusKeepingNoticeAsync(result);
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

    /// <summary>「すべて両方を取り込む」。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnResolveAllTakeBoth(object sender, RoutedEventArgs e)
        => await ResolveTakingBothAsync(AllConflictPaths());

    /// <summary>1 件だけ「両方を取り込む」。</summary>
    /// <param name="sender">送信元（Tag に対象の行が入っている）。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnResolveFileTakeBoth(object sender, RoutedEventArgs e)
        => await ResolveTakingBothAsync(PathsFromButton(sender));

    /// <summary>1 件だけ「編集した内容で解決」。</summary>
    /// <param name="sender">送信元（Tag に対象の行が入っている）。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnResolveFileAsIs(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VcRows.ConflictRowItem row }) return;
        await ResolveWithFileContentAsync(row);
    }

    /// <summary>行の「比較…」ボタン。マージエディタを開く。</summary>
    /// <param name="sender">送信元（Tag に対象の行が入っている）。</param>
    /// <param name="e">イベント引数。</param>
    private void OnCompareConflict(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VcRows.ConflictRowItem row }) OpenMergeEditor(row);
    }

    /// <summary>競合の行をダブルクリックしたとき。マージエディタを開く。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnConflictListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ConflictList.SelectedItem is VcRows.ConflictRowItem row) OpenMergeEditor(row);
    }

    /// <summary>いま未解決の競合になっているファイルのパスをすべて集める。</summary>
    private IReadOnlyList<string> AllConflictPaths()
        => _state.Status?.UnresolvedConflicts.Select(c => c.Path).ToList()
           ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>ボタンの Tag に入っている行から対象パスを取り出す。</summary>
    /// <param name="sender">クリックされたボタン。</param>
    private static IReadOnlyList<string> PathsFromButton(object sender)
    {
        if (sender is Button { Tag: VcRows.ConflictRowItem row }
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
        await ReloadStatusKeepingNoticeAsync(result);
    }

    /// <summary>
    /// 「両方を取り込む」で解決する。
    ///
    /// <para>
    /// 確認は挟まない。両方を残す操作は **どちらの変更も捨てない** ので、
    /// 「リモートを採用」と違って取り返しがつかない性質が無い
    /// （結果が気に入らなければ、送信する前に手で直せる）。
    /// </para>
    /// </summary>
    /// <param name="paths">対象のリポジトリ相対パス。</param>
    private async Task ResolveTakingBothAsync(IReadOnlyList<string> paths)
    {
        if (!_state.CanResolveConflicts || paths.Count == 0) return;

        var result = await RunAsync(
            VersionControlOperation.Resolve,
            async () => await VersionControlService.Provider
                                                   .ResolveConflictsTakingBothAsync(paths)
                                                   .ConfigureAwait(true));
        if (result is null) return;

        await ReloadStatusKeepingNoticeAsync(result);
    }

    /// <summary>
    /// 「編集した内容で解決」。いまファイルにある中身のまま解決済みにする。
    ///
    /// <para>
    /// 中身はここで読み直す。行の器が持っている判定は一覧を作った時点のもので、
    /// そのあと外部のエディタで保存されているかもしれないため。
    /// </para>
    /// </summary>
    /// <param name="row">対象の行。</param>
    private async Task ResolveWithFileContentAsync(VcRows.ConflictRowItem row)
    {
        if (!_state.CanResolveConflicts || row.RelativePath.Length == 0) return;

        var read = MergeFileText.Read(row.AbsolutePath);
        if (!read.Succeeded)
        {
            ShowInfoNotice(read.Reason);
            return;
        }

        var result = await RunAsync(
            VersionControlOperation.Resolve,
            async () => await VersionControlService.Provider
                                                   .ResolveConflictsWithContentAsync(
                                                       row.RelativePath, read.Text)
                                                   .ConfigureAwait(true));
        if (result is null) return;

        await ReloadStatusKeepingNoticeAsync(result);
    }

    /// <summary>
    /// マージエディタを開く。開けない形式（バイナリ等）のときは理由を出す。
    ///
    /// <para>
    /// ウィンドウは非モーダル。確定したときだけ、そのコールバックの中で
    /// 解決を頼み、成功したらパネルを更新する。
    /// </para>
    /// </summary>
    /// <param name="row">対象の行。</param>
    private void OpenMergeEditor(VcRows.ConflictRowItem row)
    {
        if (!_state.CanResolveConflicts || row.RelativePath.Length == 0) return;

        var provider = VersionControlService.Provider;

        var opened = MergeEditorWindows.TryOpen(
            row.AbsolutePath,
            provider.MergeContext,
            _state.BranchName,
            Window.GetWindow(this),
            resolvedText => CommitMergeAsync(row, resolvedText),
            out var reason);

        if (!opened && reason.Length > 0) MergeEditorWindows.ReportUnavailable(reason);
    }

    /// <summary>
    /// マージエディタの「確定」から呼ばれる処理。
    /// 結果を書き込んで解決し、成功したらパネルを更新する。
    /// </summary>
    /// <param name="row">マージエディタを開いたときの競合の行。</param>
    /// <param name="resolvedText">マージエディタが作った結果テキスト。</param>
    /// <returns>確定できたか、と利用者へ見せる 1 行。</returns>
    private async Task<MergeEditorCommitResult> CommitMergeAsync(
        VcRows.ConflictRowItem row, string resolvedText)
    {
        var provider = VersionControlService.Provider;

        // ★ウィンドウは非モーダルなので、開いたままプロジェクトを切り替えられる。
        //   相対パスだけで書くと、**別のプロジェクトの同名ファイル**へ書き込みかねない。
        //   開いたときの絶対パスと、いまのプロジェクトで解決される絶対パスが
        //   一致するときだけ進める。
        var expected = System.IO.Path.Combine(provider.WorkingCopyRoot, row.RelativePath);
        if (!string.Equals(expected, row.AbsolutePath, StringComparison.OrdinalIgnoreCase))
            return MergeEditorCommitResult.Failed(VersionControlMessages.UNAVAILABLE);

        var result = await RunAsync(
            VersionControlOperation.Resolve,
            async () => await provider.ResolveConflictsWithContentAsync(
                                          row.RelativePath, resolvedText)
                                      .ConfigureAwait(true));

        // null は「別の操作が走っていて実行しなかった」。窓は閉じずに retry させる。
        if (result is null)
            return MergeEditorCommitResult.Failed(VersionControlMessages.CANCELED);

        if (!result.IsSuccess)
            return MergeEditorCommitResult.Failed(result.Message);

        // 解決できたら残りの競合件数が変わる。パネルを取り直す。
        await ReloadStatusKeepingNoticeAsync(result);
        return MergeEditorCommitResult.Ok(result.Message);
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
                // 右クリックで「そのブランチへの操作」（マージ / 削除）を出す。
                item.MouseRightButtonUp += OnBranchItemRightClick;
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
        await ReloadStatusKeepingNoticeAsync(result);
        await ReloadExpandedSectionsAsync();

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

    // ============================================================
    //  ブランチのマージ・削除（アーカイブ）
    // ============================================================

    /// <summary>
    /// 「ブランチをマージ…」。取り込み元を選ばせて、現在のブランチへ取り込む。
    ///
    /// <para>
    /// 未送信の変更があるときは始めない。取り込みは作業コピーのファイルを
    /// 書き換えるため、手元の変更と混ざると「どちらが自分の変更か」が分からなくなる。
    /// </para>
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnMergeBranchClick(object sender, RoutedEventArgs e)
    {
        if (!_state.CanChangeBranch) return;
        if (!await EnsureNoUnsubmittedChangesForMergeAsync()) return;

        // 一覧はコンボを開いたときにしか取り直していない。ここで必ず取り直す。
        // null は「取れなかった」。その理由は RunAsync が 1 行メッセージに出しているので、
        // 「候補がありません」で上書きせずにそのまま見せる。
        var branches = await LoadBranchesForDialogAsync();
        if (branches is null) return;

        var current    = _state.BranchName;
        var candidates = BranchOperationRules.MergeSourceCandidates(branches, current);
        if (candidates.Count == 0)
        {
            ShowInfoNotice(VersionControlMessages.PANEL_BRANCH_MERGE_NO_CANDIDATES);
            return;
        }

        var source = EditorDialogs.ShowBranchPicker(
            string.Format(VersionControlMessages.PANEL_BRANCH_MERGE_DIALOG_PROMPT, current),
            VersionControlMessages.PANEL_BRANCH_MERGE_DIALOG_TITLE,
            candidates,
            VersionControlMessages.PANEL_BRANCH_MERGE_DIALOG_NOTE,
            Window.GetWindow(this));

        if (string.IsNullOrWhiteSpace(source)) return;

        await ExecuteMergeBranchAsync(source);
    }

    /// <summary>
    /// 取り込みを始めてよいか（未送信の変更が無いか）を、作業コピーを走査して確かめる。
    ///
    /// <para>
    /// ★判定に画面の一覧（<c>_state.ChangeCount</c>）を使わない。
    /// 画面の一覧は最後に更新した時点のもので、そのあとに保存されたファイルが抜けている。
    /// 抜けたまま取り込むと、その変更が取り込みの結果と混ざって見分けられなくなる。
    /// 走査（ScanOffline）はサーバ往復を伴わない（実測 0.11 秒）。送信ゲートと同じ考え方。
    /// </para>
    /// </summary>
    /// <returns>始めてよければ真。駄目なときは理由を 1 行メッセージへ出してから偽。</returns>
    private async Task<bool> EnsureNoUnsubmittedChangesForMergeAsync()
    {
        var scan = await VersionControlService.Provider
                                              .GetStatusAsync(StatusRefreshMode.ScanOffline)
                                              .ConfigureAwait(true);
        var changeCount = scan.Value?.Changes.Count ?? 0;
        if (!scan.IsSuccess)
        {
            // 走査できなければ「変更が無い」と断言できない。取り込みは作業コピーを
            // 書き換えるので、確かめられないまま始めない。
            EditorLog.Write($"[VCS] マージ前の走査に失敗しました: {scan.Outcome} {scan.Message}");
            _state.SetNotice(VersionControlNotice.FromResult(scan));
            SyncControls();
            return false;
        }

        // 未送信の変更がある間は取り込ませない（先に送信するか元に戻してもらう）。
        if (changeCount > 0)
        {
            ShowInfoNotice(string.Format(
                VersionControlMessages.PANEL_BRANCH_MERGE_DIRTY_FORMAT, changeCount));
            return false;
        }

        return true;
    }

    /// <summary>
    /// 指定のブランチを現在のブランチへ取り込み、状態と節を取り直す。
    /// （「その他 …」の選択ダイアログからも、一覧の右クリックからも、ここへ来る。）
    /// </summary>
    /// <param name="source">取り込み元のブランチ名。</param>
    private async Task ExecuteMergeBranchAsync(string source)
    {
        var result = await RunAsync(
            VersionControlOperation.Branch,
            async () => await VersionControlService.Provider
                                                   .MergeBranchAsync(source)
                                                   .ConfigureAwait(true));
        if (result is null) return;

        // 取り込みは作業コピーの中身を書き換える。競合が出ていれば競合の節へ出す必要も
        // あるので、成功・競合のどちらでも状態と節を取り直す。
        await ReloadStatusKeepingNoticeAsync(result);
        await ReloadExpandedSectionsAsync();
    }

    /// <summary>
    /// 「ブランチを削除（アーカイブ）…」。対象を選ばせ、確認してから削除する。
    ///
    /// <para>
    /// Lore にブランチの削除は無く、archive（一覧から隠す）が相当する。
    /// 取り消せないので、選択ダイアログの補足と確認ダイアログの 2 段で伝える。
    /// </para>
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnArchiveBranchClick(object sender, RoutedEventArgs e)
    {
        if (!_state.CanChangeBranch) return;

        // null は「取れなかった」。理由の 1 行メッセージを上書きしない（マージ側と同じ）。
        var branches = await LoadBranchesForDialogAsync();
        if (branches is null) return;

        var current = _state.BranchName;

        // 既定ブランチ名はプロバイダと同じ設定から取る（絞り込みと拒否をずらさない）。
        var candidates = BranchOperationRules.ArchiveCandidates(
            branches, current, VersionControlService.Settings.DefaultBranchName);
        if (candidates.Count == 0)
        {
            ShowInfoNotice(VersionControlMessages.PANEL_BRANCH_ARCHIVE_NO_CANDIDATES);
            return;
        }

        var target = EditorDialogs.ShowBranchPicker(
            VersionControlMessages.PANEL_BRANCH_ARCHIVE_DIALOG_PROMPT,
            VersionControlMessages.PANEL_BRANCH_ARCHIVE_DIALOG_TITLE,
            candidates,
            VersionControlMessages.PANEL_BRANCH_ARCHIVE_DIALOG_NOTE,
            Window.GetWindow(this));

        if (string.IsNullOrWhiteSpace(target)) return;

        await ExecuteArchiveBranchAsync(target);
    }

    /// <summary>
    /// 確認してから指定のブランチを削除（アーカイブ）し、コンボを取り直す。
    /// （「その他 …」の選択ダイアログからも、一覧の右クリックからも、ここへ来る。）
    /// </summary>
    /// <param name="target">対象のブランチ名。</param>
    private async Task ExecuteArchiveBranchAsync(string target)
    {
        // 取り返しがつかないのでここで確認する（ヘッドレスでは必ず「いいえ」）。
        if (!ConfirmArchiveBranch(target)) return;

        var result = await RunAsync(
            VersionControlOperation.Branch,
            async () => await VersionControlService.Provider
                                                   .ArchiveBranchAsync(target)
                                                   .ConfigureAwait(true));
        if (result is null || !result.IsSuccess) return;

        // 消えたブランチが残らないよう、コンボを取り直す。
        await ReloadBranchesKeepingNoticeAsync();
    }

    // ── ブランチ一覧の右クリック（そのブランチへの操作）──────

    /// <summary>右クリックメニューの中での「マージ」の位置（XAML の並びと対応）。</summary>
    private const int BRANCH_ITEM_MENU_INDEX_MERGE = 0;

    /// <summary>右クリックメニューの中での「削除」の位置（XAML の並びと対応）。</summary>
    private const int BRANCH_ITEM_MENU_INDEX_ARCHIVE = 1;

    /// <summary>
    /// ドロップダウンの中のブランチを右クリックしたとき。
    /// そのブランチへの操作（現在のブランチへマージ / 削除）をメニューで出す。
    ///
    /// <para>
    /// ★ドロップダウンは先に閉じる。コンボのポップアップはマウスの捕捉を握っており、
    /// 開いたままメニューを重ねると捕捉の取り合いでどちらかが勝手に閉じる。
    /// メニューの位置はマウスの位置に置く（閉じた項目を基準にはできない）。
    /// </para>
    /// </summary>
    /// <param name="sender">右クリックされた項目。</param>
    /// <param name="e">イベント引数。</param>
    private void OnBranchItemRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ComboBoxItem { Tag: string name } || name.Length == 0) return;

        // 右クリックで項目が選ばれた扱いにならないよう、ここで止める。
        e.Handled = true;
        CmbBranch.IsDropDownOpen = false;

        if (FindResource("Vc.BranchItemMenu") is not ContextMenu menu) return;
        if (menu.Items.Count <= BRANCH_ITEM_MENU_INDEX_ARCHIVE) return;

        var current = _state.BranchName;

        // 可否と理由はプロバイダと同じ規則から引く（一覧に出るのに必ず失敗する、を作らない）。
        var mergeCheck   = BranchOperationRules.CheckMergeSource(name, current);
        var archiveCheck = BranchOperationRules.CheckArchive(
            name, current, VersionControlService.Settings.DefaultBranchName);

        var merge = (MenuItem)menu.Items[BRANCH_ITEM_MENU_INDEX_MERGE];
        merge.Header    = current.Length > 0
            ? string.Format(VersionControlMessages.PANEL_BRANCH_ITEM_MERGE_FORMAT, name, current)
            : string.Format(
                VersionControlMessages.PANEL_BRANCH_ITEM_MERGE_UNKNOWN_CURRENT_FORMAT, name);
        merge.Tag       = name;
        merge.IsEnabled = _state.CanChangeBranch && mergeCheck.Allowed;
        merge.ToolTip   = mergeCheck.Allowed ? null : mergeCheck.Reason;

        var archive = (MenuItem)menu.Items[BRANCH_ITEM_MENU_INDEX_ARCHIVE];
        archive.Header    = string.Format(
            VersionControlMessages.PANEL_BRANCH_ITEM_ARCHIVE_FORMAT, name);
        archive.Tag       = name;
        archive.IsEnabled = _state.CanChangeBranch && archiveCheck.Allowed;
        archive.ToolTip   = archiveCheck.Allowed ? null : archiveCheck.Reason;

        menu.PlacementTarget = CmbBranch;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    /// <summary>右クリックメニュー「〜へマージ」。対象は項目の Tag に入っている。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnBranchItemMergeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string source } || source.Length == 0) return;
        if (!_state.CanChangeBranch) return;

        // 規則は開く前にも見ているが、実行の直前にもう一度見る（状態が変わっていることがある）。
        var check = BranchOperationRules.CheckMergeSource(source, _state.BranchName);
        if (!check.Allowed)
        {
            ShowInfoNotice(check.Reason);
            return;
        }

        if (!await EnsureNoUnsubmittedChangesForMergeAsync()) return;
        await ExecuteMergeBranchAsync(source);
    }

    /// <summary>右クリックメニュー「〜を削除（アーカイブ）…」。対象は項目の Tag に入っている。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnBranchItemArchiveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string target } || target.Length == 0) return;
        if (!_state.CanChangeBranch) return;

        var check = BranchOperationRules.CheckArchive(
            target, _state.BranchName, VersionControlService.Settings.DefaultBranchName);
        if (!check.Allowed)
        {
            ShowInfoNotice(check.Reason);
            return;
        }

        await ExecuteArchiveBranchAsync(target);
    }

    /// <summary>
    /// ブランチ削除（アーカイブ）の確認。ヘッドレスでは必ず「いいえ」になる。
    /// </summary>
    /// <param name="name">対象のブランチ名。</param>
    /// <returns>続行してよいか。</returns>
    private static bool ConfirmArchiveBranch(string name)
        => EditorDialogs.Show(
               string.Format(
                   VersionControlMessages.PANEL_BRANCH_ARCHIVE_CONFIRM_FORMAT, name),
               VersionControlMessages.PANEL_BRANCH_ARCHIVE_CONFIRM_TITLE,
               MessageBoxButton.YesNo,
               MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <summary>
    /// ダイアログへ並べるためにブランチ一覧を取り直す。
    ///
    /// <para>
    /// **取れなかった場合は null を返す**（空の一覧とは区別する）。
    /// 失敗の理由は <see cref="RunAsync"/> が 1 行メッセージに出しているので、
    /// 呼び出し側はそれを上書きせずにそのまま戻ること。
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<BranchInfo>?> LoadBranchesForDialogAsync()
    {
        var result = await RunAsync(
            VersionControlOperation.Branch,
            async () => await VersionControlService.Provider
                                                   .GetBranchesAsync()
                                                   .ConfigureAwait(true));

        return (result as VersionControlResult<IReadOnlyList<BranchInfo>>)?.Value;
    }

    /// <summary>
    /// ブランチ一覧だけを取り直し、**直前の操作の 1 行メッセージは残す**。
    /// （「削除しました」が「ブランチ一覧を取得しました」で潰れないようにする。）
    /// </summary>
    private async Task ReloadBranchesKeepingNoticeAsync()
    {
        var keep = _state.Notice;
        await ReloadBranchesAsync();
        _state.SetNotice(keep);
        SyncControls();
    }

    /// <summary>
    /// 操作を始めずに案内だけを 1 行メッセージへ出す。
    /// </summary>
    /// <param name="message">出す文言。</param>
    private void ShowInfoNotice(string message)
    {
        _state.SetNotice(VersionControlNotice.Info(message));
        SyncControls();
    }

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
    //  履歴
    // ============================================================

    /// <summary>履歴の節の更新ボタン。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnHistoryRefreshClick(object sender, RoutedEventArgs e)
    {
        // 取り直しは最初のページから（「さらに読み込む」で伸ばした分は畳む）。
        _historyLimit = VersionControlMessages.PANEL_HISTORY_PAGE_SIZE;
        await ReloadHistoryAsync();
    }

    /// <summary>「さらに読み込む」。取得件数の上限を 1 ページぶん伸ばして取り直す。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnHistoryLoadMoreClick(object sender, RoutedEventArgs e)
    {
        _historyLimit += VersionControlMessages.PANEL_HISTORY_PAGE_SIZE;
        await ReloadHistoryAsync();
    }

    /// <summary>
    /// 履歴を取り直す。サーバに繋がらないときは一覧を消して案内だけを出す。
    /// </summary>
    private async Task ReloadHistoryAsync()
    {
        var limit = _historyLimit;

        var result = await RunAsync(
            VersionControlOperation.History,
            async () => await VersionControlService.Provider
                                                   .GetHistoryAsync(limit)
                                                   .ConfigureAwait(true));
        if (result is null) return;

        _historyLoaded = true;
        _historyRows.Clear();
        BtnHistoryMore.Visibility = Visibility.Collapsed;

        if (result.Outcome == VersionControlOutcome.RequiresConnection)
        {
            ShowSectionMessage(TxtHistoryMessage,
                               VersionControlMessages.PANEL_HISTORY_REQUIRES_CONNECTION);

            // 案内は節の中に出しているので、1 行メッセージは重複させない。
            _state.SetNotice(VersionControlNotice.None);
            SyncControls();
            return;
        }

        if (!result.IsSuccess)
        {
            ShowSectionMessage(TxtHistoryMessage, result.Message);
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

        // 要求した上限ちょうど返ってきた＝まだ先がある可能性が高い。
        // （「あと何件あるか」を Lore は返さないので、これが唯一の手掛かり。）
        BtnHistoryMore.Visibility = _historyRows.Count >= limit
            ? Visibility.Visible
            : Visibility.Collapsed;

        ShowSectionMessage(TxtHistoryMessage,
                           _historyRows.Count == 0
                               ? VersionControlMessages.PANEL_HISTORY_EMPTY
                               : null);

        // 取得できたこと自体は利用者が頼んだ操作ではないので、静かに閉じる。
        _state.SetNotice(VersionControlNotice.None);
        SyncControls();
    }

    // ============================================================
    //  ロック
    // ============================================================

    /// <summary>ロックの節の更新ボタン。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnLocksRefreshClick(object sender, RoutedEventArgs e)
        => await ReloadLocksAsync();

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

        _locksLoaded = true;
        _lockRows.Clear();

        if (result.Outcome == VersionControlOutcome.RequiresConnection)
        {
            ShowSectionMessage(TxtLockMessage,
                               VersionControlMessages.PANEL_NOTICE_REQUIRES_CONNECTION);
            _state.SetNotice(VersionControlNotice.None);
            SyncControls();
            return;
        }

        if (!result.IsSuccess)
        {
            ShowSectionMessage(TxtLockMessage, result.Message);
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

        ShowSectionMessage(TxtLockMessage,
                           _lockRows.Count == 0 ? VersionControlMessages.PANEL_LOCKS_EMPTY : null);

        _state.SetNotice(VersionControlNotice.None);
        SyncControls();
    }

    /// <summary>節の中の案内文を出す / 消す。</summary>
    /// <param name="target">対象の TextBlock。</param>
    /// <param name="message">出す文言（null なら隠す）。</param>
    private static void ShowSectionMessage(TextBlock target, string? message)
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

    /// <summary>変更ツリーで選んだファイルをロックする。</summary>
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

        // ★手で掛けたロックは自動解放の対象から外す。
        //   自動で取ったロックの台帳に残っていると、シーンを閉じた拍子に
        //   「押さえておきたくて掛けたロック」まで外れてしまう。
        SEEDEditor.VersionControl.Locking.LockGatekeeper.ForgetTracked(paths);

        await ReloadLocksIfVisibleAsync();
    }

    /// <summary>変更ツリーで選んだファイルのロックを解除する。</summary>
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

        // もう手元には無いので台帳からも消す（残すと終了時に空振りの解放が走る）。
        SEEDEditor.VersionControl.Locking.LockGatekeeper.ForgetTracked(paths);

        await ReloadLocksIfVisibleAsync();
    }

    /// <summary>ロックの節にある解除ボタン。</summary>
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

        // 自動で取ったロックを手で外した場合もここを通る。台帳から消しておく。
        SEEDEditor.VersionControl.Locking.LockGatekeeper.ForgetTracked(new[] { row.RelativePath });

        await ReloadLocksAsync();
    }

    /// <summary>
    /// 「ロックをすべて解除」（ヘッダーの「その他」メニュー）。
    ///
    /// <para>
    /// 解除するのは **自分のロックだけ**。所有者が不明なロックまで巻き込むと、
    /// 他の人の編集権を黙って奪い得る（LockRowItem のコメントと同じ理由）。
    /// </para>
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnReleaseAllLocks(object sender, RoutedEventArgs e)
    {
        if (!_state.CanChangeLocks) return;

        // いまの一覧が古い可能性があるので、必ず取り直してから対象を決める。
        var listed = await RunAsync(
            VersionControlOperation.Lock,
            async () => await VersionControlService.Provider
                                                   .Locks
                                                   .ListAsync()
                                                   .ConfigureAwait(true));
        if (listed is null || !listed.IsSuccess) return;

        var locks = (listed as VersionControlResult<IReadOnlyList<LockInfo>>)?.Value;
        var mine  = locks?.Where(l => VersionControlDisplay.CanRelease(l.Holder))
                          .Select(l => l.Path)
                          .ToList()
                    ?? new List<string>();

        if (mine.Count == 0)
        {
            _state.SetNotice(VersionControlNotice.Info(
                VersionControlMessages.PANEL_NO_RELEASABLE_LOCKS));
            SyncControls();
            return;
        }

        await RunAsync(
            VersionControlOperation.Lock,
            async () => await VersionControlService.Provider
                                                   .Locks
                                                   .ReleaseAsync(mine)
                                                   .ConfigureAwait(true));

        // 自動で取っていたものも含めて外したので、台帳を空にしておく。
        SEEDEditor.VersionControl.Locking.LockGatekeeper.ForgetTracked(mine);

        await ReloadLocksIfVisibleAsync();
    }

    /// <summary>ロックの節が開いているときだけ一覧を取り直す（無駄な往復を避ける）。</summary>
    private async Task ReloadLocksIfVisibleAsync()
    {
        if (IsSectionExpanded(VersionControlSection.Locks)) await ReloadLocksAsync();
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
    /// <param name="previous">
    /// 直前の操作の結果。サーバが応答したと分かるときだけ、続けて
    /// <see cref="StatusRefreshMode.ScanOnline"/> で取り直して
    /// 上段の「未送信・未取得」まで更新する（詳細は
    /// <see cref="ShouldGoOnlineAfter"/>）。
    /// </param>
    private async Task ReloadStatusKeepingNoticeAsync(VersionControlResult? previous)
    {
        var keep = _state.Notice;
        var mode = ShouldGoOnlineAfter(previous)
            ? StatusRefreshMode.ScanOnline
            : StatusRefreshMode.ScanOffline;

        await RefreshAsync(mode);

        _state.SetNotice(keep);
        SyncControls();
    }

    /// <summary>
    /// 直前の操作のあと、サーバへ問い合わせる取り直しをしてよいか。
    ///
    /// <para>
    /// 「サーバが応答したことが分かっている」ときだけ真を返す。
    /// 成功はもちろん、<see cref="VersionControlOutcome.NeedsSync"/> と
    /// <see cref="VersionControlOutcome.Conflicted"/> も、サーバの内容と
    /// 比べられた結果なので応答があったと言える。
    /// </para>
    /// <para>
    /// 逆に、接続できずに終わった直後にもう一度サーバへ行くと、
    /// **同じ待ち時間（既定 2 分）をもう一度払う**ことになる。
    /// 利用者から見れば「失敗したのに、さらに固まった」だけなので、
    /// そのときはオフラインで取り直して素早く画面を戻す。
    /// </para>
    /// </summary>
    /// <param name="result">直前の操作の結果。</param>
    private static bool ShouldGoOnlineAfter(VersionControlResult? result)
        => result is not null
           && (result.IsSuccess
               || result.Outcome == VersionControlOutcome.NeedsSync
               || result.Outcome == VersionControlOutcome.Conflicted);
}
