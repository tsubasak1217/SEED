// ============================================================
//  VersionControlPanel.xaml.cs — Version Control パネル（本体）
//
//  【役割】
//  バージョン管理の中核層（editor/src/VersionControl/）を、アーティストも迷わない
//  画面として見せる。判断ロジックは持たず、次の 2 つへ委ねる:
//    ・何ができるか / 何と出すか … VersionControlPanelState（WPF 非依存・単体テスト済み）
//    ・実際の操作                … VersionControlService.Provider（IVersionControlProvider）
//  ここに書くのは「コントロールへ写す」ことと「イベントを流す」ことだけ。
//
//  【ファイル分割】
//    ・本ファイル                         … 生成・購読・状態の反映・ヘッダー・変更一覧
//    ・VersionControlPanel.Operations.cs  … 取得 / 送信 / 競合解決 / ブランチ / ロック / 履歴
//
//  【スレッド（重要）】
//  VersionControlService.StatusChanged は **ワーカースレッドから発火し得る**。
//  必ず Dispatcher へ移してから UI を触る。
//
//  【中断ボタンを出さない理由】
//  LoreVcs には実行中の操作を止める API が無い（docs/editor_version_control.md 3 章）。
//  押しても止まらないボタンを出すのは嘘になるので、不確定プログレスだけを出す。
// ============================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Presentation;
using VcRows = SEEDEditor.Panels.VersionControl;

namespace SEEDEditor.Panels;

/// <summary>
/// バージョン管理（Version Control）パネル。
/// </summary>
public partial class VersionControlPanel : UserControl
{
    // ── 1 行メッセージの配色（重大度 → 色。ここ 1 か所だけで決める）──

    /// <summary>成功。</summary>
    private static readonly Brush NoticeSuccessBrush =
        new SolidColorBrush(Color.FromRgb(0x8F, 0xD1, 0x8F));

    /// <summary>情報。</summary>
    private static readonly Brush NoticeInfoBrush =
        new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));

    /// <summary>注意。</summary>
    private static readonly Brush NoticeWarningBrush =
        new SolidColorBrush(Color.FromRgb(0xE0, 0xC0, 0x70));

    /// <summary>失敗。</summary>
    private static readonly Brush NoticeErrorBrush =
        new SolidColorBrush(Color.FromRgb(0xE8, 0x8F, 0x8F));

    /// <summary>主操作ボタンの通常色。</summary>
    private static readonly Brush PrimaryButtonBrush =
        new SolidColorBrush(Color.FromRgb(0x0E, 0x63, 0x9C));

    /// <summary>「最新を取得」を強調するときの色（NeedsSync のとき）。</summary>
    private static readonly Brush EmphasisButtonBrush =
        new SolidColorBrush(Color.FromRgb(0xA0, 0x70, 0x20));

    // ── ブランチコンボの特別項目 ────────────────────────────

    /// <summary>
    /// 「新しいブランチ…」項目を見分けるためのタグ。
    /// ブランチ名と衝突しないよう、Lore のブランチ名に使えない文字を含める。
    /// </summary>
    private const string BRANCH_ITEM_NEW_TAG = "new-branch";

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>ボタンの有効条件・表示メッセージを決める状態機械（WPF 非依存）。</summary>
    private readonly VersionControlPanelState _state = new();

    /// <summary>変更一覧の行（見出しとファイルを 1 本に畳んだもの）。</summary>
    private readonly ObservableCollection<VcRows.ChangeRowItem> _changeRows = new();

    /// <summary>履歴タブの行。</summary>
    private readonly ObservableCollection<VcRows.HistoryRowItem> _historyRows = new();

    /// <summary>ロックタブの行。</summary>
    private readonly ObservableCollection<VcRows.LockRowItem> _lockRows = new();

    /// <summary>作業コピーの見張り（利用可能なときだけ生成する）。</summary>
    private WorkingCopyWatcher? _watcher;

    /// <summary>ブランチコンボを差し替えている間、選択変更イベントを無視するための印。</summary>
    private bool _suppressBranchEvent;

    /// <summary>初期化（最初の状態取得）を済ませたか。</summary>
    private bool _initialized;

    /// <summary>
    /// パネルを構築する。
    ///
    /// <para>
    /// この時点ではプロジェクトが開かれているとは限らないので、
    /// 実際の状態取得は <see cref="OnPanelLoaded"/>（初回表示時）で行う。
    /// </para>
    /// </summary>
    public VersionControlPanel()
    {
        InitializeComponent();

        // 固定文言を流し込む（XAML に日本語を直書きしないため）。
        TxtUnavailableTitle.Text     = VersionControlMessages.PANEL_UNAVAILABLE_TITLE;
        TxtUnavailableGuide.Text     = VersionControlMessages.PANEL_UNAVAILABLE_GUIDE;
        TxtFetchLabel.Text           = VersionControlMessages.PANEL_FETCH_BUTTON;
        TxtSubmitLabel.Text          = VersionControlMessages.PANEL_SUBMIT_BUTTON;
        TxtMessagePlaceholder.Text   = VersionControlMessages.PANEL_MESSAGE_PLACEHOLDER;
        TxtNoChanges.Text            = VersionControlMessages.PANEL_NO_CHANGES;
        TxtLockNote.Text             = VersionControlMessages.PANEL_LOCK_UNKNOWN_NOTE;
        BtnRefresh.ToolTip           = VersionControlMessages.PANEL_REFRESH_TOOLTIP;

        TabChanges.Header = VersionControlMessages.PANEL_TAB_CHANGES;
        TabHistory.Header = VersionControlMessages.PANEL_TAB_HISTORY;
        TabLocks.Header   = VersionControlMessages.PANEL_TAB_LOCKS;

        MenuShowInProject.Header = VersionControlMessages.PANEL_MENU_SHOW_IN_PROJECT;
        MenuCopyPath.Header      = VersionControlMessages.PANEL_MENU_COPY_PATH;
        MenuLock.Header          = VersionControlMessages.PANEL_MENU_LOCK;
        MenuUnlock.Header        = VersionControlMessages.PANEL_MENU_UNLOCK;

        ChangeList.ItemsSource  = _changeRows;
        HistoryList.ItemsSource = _historyRows;
        LockList.ItemsSource    = _lockRows;

        Loaded += OnPanelLoaded;
    }

    // ============================================================
    //  ライフサイクル
    // ============================================================

    /// <summary>
    /// 初回表示時に、プロバイダの有無を見て画面を作る。
    ///
    /// <para>
    /// AvalonDock はタブを切り替えるたびに Loaded を出し得るので、
    /// 重い初期化は 1 回だけにする。
    /// </para>
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        // 状態の更新はワーカースレッドから来る。購読はここで 1 回だけ。
        //
        // ★Unloaded では解除しない。AvalonDock はタブの切り替えやフローティング化でも
        //   Unloaded を出すため、そこで解除すると二度と状態が反映されなくなる。
        //   パネルはプロセスと寿命を共にするので、解除はプロセス終了に任せる。
        VersionControlService.StatusChanged += OnServiceStatusChanged;

        ApplyAvailability();

        // 使えるなら、開いた直後だけ走査つきで取り直す（エディタ外での変更を拾うため）。
        if (_state.IsAvailable)
        {
            StartWatcher();
            _ = RefreshAsync(StatusRefreshMode.ScanOffline);
        }
    }

    /// <summary>
    /// プロバイダの有無に応じて、案内画面と本体を切り替える。
    /// </summary>
    private void ApplyAvailability()
    {
        var provider = VersionControlService.Provider;
        _state.SetAvailability(provider.IsAvailable);

        UnavailableView.Visibility = provider.IsAvailable ? Visibility.Collapsed : Visibility.Visible;
        MainView.Visibility        = provider.IsAvailable ? Visibility.Visible   : Visibility.Collapsed;

        if (provider.IsAvailable)
        {
            TxtConnection.Text    = VersionControlDisplay.ToConnectionText(
                                        provider.Identity, provider.RemoteUrl);
            TxtConnection.ToolTip = TxtConnection.Text;
        }

        SyncControls();
    }

    /// <summary>
    /// 作業コピーの見張りを始める（変化があれば状態の取り直しを予約する）。
    /// </summary>
    private void StartWatcher()
    {
        if (_watcher is not null) return;

        var root = VersionControlService.Provider.WorkingCopyRoot;
        if (string.IsNullOrWhiteSpace(root)) return;

        try
        {
            _watcher = new WorkingCopyWatcher(
                root, StatusRefreshMode.ScanOffline, EditorLog.Write);
        }
        catch (Exception ex)
        {
            // 見張りが作れなくてもパネルは使える（手動の更新ボタンがある）。
            EditorLog.Write($"[VCS] ファイル監視を開始できませんでした: {ex.Message}");
        }
    }

    /// <summary>
    /// サービスが状態を更新したときに呼ばれる。**ワーカースレッドから来る**ので
    /// 必ず Dispatcher へ移す。
    /// </summary>
    /// <param name="sender">送信元（常に null）。</param>
    /// <param name="e">新しい状態と取得結果。</param>
    private void OnServiceStatusChanged(
        object? sender, VersionControlStatusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _state.ApplyStatus(e.Status);
            RebuildChangeRows();
            EnsureBranchComboShowsCurrent();
            SyncControls();
        }));
    }

    /// <summary>
    /// ブランチのコンボに、少なくとも現在のブランチが出ている状態にする。
    ///
    /// <para>
    /// 一覧の取得はサーバ往復が要るのでコンボを開いたときにしか行わない。
    /// しかし何も出ていないと「ブランチが分からないエディタ」に見えるため、
    /// 状態から分かる現在のブランチだけは往復なしで先に出しておく。
    /// </para>
    /// </summary>
    private void EnsureBranchComboShowsCurrent()
    {
        var current = _state.BranchName;
        if (current.Length == 0) return;

        var already = CmbBranch.Items
            .OfType<ComboBoxItem>()
            .Any(i => i.Tag is string tag
                      && string.Equals(tag, current, StringComparison.Ordinal));

        // すでに一覧に在るなら選択だけ合わせる。無ければ（初回など）作り直す。
        if (already)
        {
            RestoreBranchSelection();
            return;
        }

        FillBranchCombo(null);
    }

    // ============================================================
    //  状態 → コントロール
    // ============================================================

    /// <summary>
    /// 状態機械の内容をコントロールへ写す。
    ///
    /// <para>
    /// **有効/無効・表示文言の判断はここに書かない**。すべて
    /// <see cref="VersionControlPanelState"/> が持っており、ここは写すだけ。
    /// </para>
    /// </summary>
    private void SyncControls()
    {
        if (!_state.IsAvailable) return;

        // ── ボタンの有効条件 ──
        BtnFetch.IsEnabled   = _state.CanFetchLatest;
        BtnSubmit.IsEnabled  = _state.CanSubmit;
        BtnRefresh.IsEnabled = _state.CanRefresh;
        CmbBranch.IsEnabled  = _state.CanChangeBranch;
        TxtMessage.IsEnabled = _state.IsAvailable && !_state.IsBusy;

        // 押せない理由をツールチップで必ず示す（理由の無い無効ボタンは故障に見える）。
        var reason = _state.SubmitBlockedReason;
        BtnSubmit.ToolTip = reason.Length == 0 ? null : reason;

        // ── 「最新を取得」の強調（NeedsSync のとき）──
        BtnFetch.Background = _state.EmphasizeFetchLatest
            ? EmphasisButtonBrush
            : PrimaryButtonBrush;

        // ── 実行中の表示（中断ボタンは出さない）──
        BusyView.Visibility = _state.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        TxtBusy.Text        = _state.BusyText;

        // ── 1 行メッセージ ──
        var notice = _state.Notice;
        if (notice.HasText)
        {
            TxtNotice.Text       = notice.Text;
            TxtNotice.Foreground = ToBrush(notice.Severity);
            NoticeView.Visibility = Visibility.Visible;
        }
        else
        {
            NoticeView.Visibility = Visibility.Collapsed;
        }

        // ── プレースホルダ ──
        TxtMessagePlaceholder.Visibility =
            TxtMessage.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // ── 変更なしの案内 ──
        TxtNoChanges.Visibility =
            _changeRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>重大度に対応する色を返す（配色の対応表はここだけ）。</summary>
    /// <param name="severity">重大度。</param>
    private static Brush ToBrush(VersionControlNoticeSeverity severity) => severity switch
    {
        VersionControlNoticeSeverity.Success => NoticeSuccessBrush,
        VersionControlNoticeSeverity.Info    => NoticeInfoBrush,
        VersionControlNoticeSeverity.Warning => NoticeWarningBrush,
        VersionControlNoticeSeverity.Error   => NoticeErrorBrush,
        _                                    => NoticeInfoBrush,
    };

    /// <summary>
    /// 状態機械が組み立てたグループを、1 本のリストの行へ展開する。
    /// </summary>
    private void RebuildChangeRows()
    {
        _changeRows.Clear();

        foreach (var group in _state.Groups)
        {
            _changeRows.Add(VcRows.ChangeRowItem.Header(group.Header, group.IsConflictGroup));

            foreach (var file in group.Items)
            {
                _changeRows.Add(VcRows.ChangeRowItem.ForFile(file, group.IsConflictGroup));
            }
        }
    }

    // ============================================================
    //  操作の共通枠
    // ============================================================

    /// <summary>
    /// 1 つの操作を「実行中表示 → 実行 → 結果表示」の流れで走らせる。
    ///
    /// <para>
    /// 例外はここで受け止める。プロバイダ境界は例外を出さない約束だが、
    /// UI 側の想定外（null 参照など）でエディタごと落とさないための保険。
    /// </para>
    /// </summary>
    /// <param name="operation">操作の種類（実行中の表示に使う）。</param>
    /// <param name="body">実際の処理。</param>
    /// <returns>操作結果（開始できなかったときは null）。</returns>
    private async Task<VersionControlResult?> RunAsync(
        VersionControlOperation operation, Func<Task<VersionControlResult>> body)
    {
        if (!_state.BeginOperation(operation)) return null;
        SyncControls();

        VersionControlResult result;
        try
        {
            result = await body().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            EditorLog.Write($"[VCS] {VersionControlPanelState.ToOperationName(operation)}"
                            + $" で例外が発生しました: {ex}");
            result = VersionControlResult.Failed(VersionControlMessages.UNEXPECTED_FAILURE);
        }

        _state.EndOperation(result);
        SyncControls();
        return result;
    }

    /// <summary>
    /// 状態を取り直してパネルへ反映する。
    ///
    /// <para>
    /// <see cref="VersionControlService.RefreshAsync"/> は StatusChanged を発火するので、
    /// 一覧の更新はその購読側（<see cref="OnServiceStatusChanged"/>）で行われる。
    /// </para>
    /// </summary>
    /// <param name="mode">取得モード。</param>
    /// <param name="showResult">結果を 1 行メッセージに出すか（自動更新では出さない）。</param>
    private async Task RefreshAsync(StatusRefreshMode mode, bool showResult = false)
    {
        var result = await RunAsync(
            VersionControlOperation.Refresh,
            async () => await VersionControlService.RefreshAsync(mode).ConfigureAwait(true));

        // 自動的な取り直しで毎回「状態を取得しました。」と出ると邪魔なので、
        // 明示的に更新を押したとき以外は消す。
        if (!showResult && result is not null && result.IsSuccess)
        {
            _state.SetNotice(VersionControlNotice.None);
            SyncControls();
        }
    }

    // ============================================================
    //  ヘッダー
    // ============================================================

    /// <summary>更新ボタン。走査つきで取り直す（エディタ外での変更を拾う）。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await RefreshAsync(StatusRefreshMode.ScanOffline, showResult: true);
        await ReloadActiveTabAsync();
    }

    // ============================================================
    //  変更一覧の右クリックメニュー
    // ============================================================

    /// <summary>
    /// 右クリックメニューを開く直前に、選択行に応じて項目の有効・無効を決める。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnChangeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 右クリックした行がまだ選ばれていなければ、その行を選んでからメニューを出す。
        // WPF の ListBox は右クリックでは選択が動かないため、これが無いと
        // 「別の行を選んだまま」メニューが出て、利用者の意図と違う対象に操作してしまう。
        SelectRowUnderCursor();

        var paths = SelectedChangePaths();

        // 見出しだけを選んでいるときはメニューを出さない（できることが無い）。
        if (paths.Count == 0)
        {
            e.Handled = true;
            return;
        }

        MenuShowInProject.IsEnabled = paths.Count == 1;
        MenuCopyPath.IsEnabled      = true;
        MenuLock.IsEnabled          = _state.CanChangeLocks;
        MenuUnlock.IsEnabled        = _state.CanChangeLocks;
    }

    /// <summary>
    /// マウス位置の行を選択状態にする（すでに選択に含まれていれば何もしない）。
    ///
    /// <para>
    /// 複数選択した状態での右クリックは、その選択をそのまま対象にしたい。
    /// だから「含まれていない行を右クリックしたときだけ」選択し直す。
    /// </para>
    /// </summary>
    private void SelectRowUnderCursor()
    {
        var position = Mouse.GetPosition(ChangeList);
        var hit      = ChangeList.InputHitTest(position) as DependencyObject;

        // ヒットした要素から ListBoxItem まで親を辿る。
        while (hit is not null && hit is not ListBoxItem)
        {
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        }

        if (hit is not ListBoxItem { DataContext: VcRows.ChangeRowItem row }) return;
        if (ChangeList.SelectedItems.Contains(row)) return;

        ChangeList.SelectedItems.Clear();
        ChangeList.SelectedItems.Add(row);
    }

    /// <summary>選択中のファイル行のリポジトリ相対パスを集める（見出し行は除く）。</summary>
    private IReadOnlyList<string> SelectedChangePaths()
        => ChangeList.SelectedItems
                     .OfType<VcRows.ChangeRowItem>()
                     .Where(r => !r.IsHeader && r.RelativePath.Length > 0)
                     .Select(r => r.RelativePath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToList();

    /// <summary>
    /// 相対パスを作業コピー内の絶対パスへ直す。
    /// </summary>
    /// <param name="relativePath">リポジトリ相対パス。</param>
    private static string ToAbsolutePath(string relativePath)
    {
        var root = VersionControlService.Provider.WorkingCopyRoot;
        return string.IsNullOrEmpty(root)
            ? relativePath
            : Path.GetFullPath(Path.Combine(root, relativePath));
    }

    /// <summary>
    /// 「プロジェクトパネルで表示」。
    ///
    /// <para>
    /// プロジェクトパネルへのジャンプは、参照フィールドが使っている既存の静的フック
    /// （<see cref="FileRefBuilder.RevealInProjectRequested"/>）へ相乗りする。
    /// MainWindow が起動時にここへ「パネルを前面に出して RevealFile する」処理を
    /// 差し込んでいるので、同じ経路を通せば挙動が揃う。
    /// 対象が assets の外にあるなど表示できない場合はエクスプローラーで開く。
    /// </para>
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnShowInProject(object sender, RoutedEventArgs e)
    {
        var paths = SelectedChangePaths();
        if (paths.Count == 0) return;

        var absolute = ToAbsolutePath(paths[0]);
        var reveal   = FileRefBuilder.RevealInProjectRequested;

        if (reveal is not null && File.Exists(absolute))
        {
            reveal(absolute);
            return;
        }

        OpenContainingFolder(absolute);
    }

    /// <summary>
    /// エクスプローラーでファイルの場所を開く（プロジェクトパネルで出せないときの代替）。
    /// </summary>
    /// <param name="absolutePath">対象の絶対パス。</param>
    private static void OpenContainingFolder(string absolutePath)
    {
        try
        {
            if (File.Exists(absolutePath))
            {
                // /select, でファイルを選択した状態で開く。
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{absolutePath}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            // 削除されたファイルは親フォルダを開く（存在する一番近い親まで遡る）。
            var directory = Path.GetDirectoryName(absolutePath);
            while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                directory = Path.GetDirectoryName(directory);
            }

            if (string.IsNullOrEmpty(directory)) return;

            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            EditorLog.Write($"[VCS] フォルダーを開けませんでした: {ex.Message}");
        }
    }

    /// <summary>「パスをコピー」。複数選択なら改行で並べる。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        var paths = SelectedChangePaths();
        if (paths.Count == 0) return;

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
        }
        catch (Exception ex)
        {
            // クリップボードは他プロセスに掴まれていると失敗する。落とさない。
            EditorLog.Write($"[VCS] パスをコピーできませんでした: {ex.Message}");
        }
    }

    // ============================================================
    //  メッセージ欄
    // ============================================================

    /// <summary>メッセージ欄の入力を状態機械へ伝える（送信ボタンの有効条件が変わる）。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnMessageTextChanged(object sender, TextChangedEventArgs e)
    {
        _state.SetCommitMessage(TxtMessage.Text);
        SyncControls();
    }

    /// <summary>メッセージ欄で Enter を押したら送信する（押せる状態のときだけ）。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">キーイベント。</param>
    private void OnMessageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (!_state.CanSubmit) return;

        e.Handled = true;
        OnSubmitClick(sender, e);
    }
}
