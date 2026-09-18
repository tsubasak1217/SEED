// ============================================================
//  VersionControlPanel.Sections.cs — 折りたたみ節の開閉・見出し・保存
//
//  【役割】
//  パネル下半分の 4 つの節（競合 / 変更 / ロック / 履歴）について、
//    ・見出しの文言（件数つき）を貼る
//    ・開閉に応じて高さの配分と本文の表示を切り替える
//    ・開閉状態をエディタ設定へ保存し、次回の起動で戻す
//    ・開いたときに中身を取りに行く（ロック・履歴はサーバ往復が要る）
//  を受け持つ。
//
//  【なぜ別ファイルにするのか】
//  VersionControlPanel.xaml.cs は生成・購読・変更ツリーで既に長い。
//  節の開閉は別の関心事なので partial で分ける（単一責任）。
//
//  【判断はここに書かない】
//  「どの節をどんな見出しで出すか」「既定で開いているか」「保存キーは何か」は
//  すべて VersionControlSections（WPF 非依存・単体テスト済み）が持つ。
//  ここがやるのは、その答えをコントロールへ写すことだけ。
//
//  【保存のタイミング】
//  開閉のたびにファイルを書くと、連続で開閉しただけで I/O が頻発する。
//  ProjectPanel.StatePersistence と同じ流儀で、最後の変更から一定時間後に 1 回書く。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Presentation;

namespace SEEDEditor.Panels;

public partial class VersionControlPanel
{
    // ── 定数（マジックナンバー回避）────────────────────────────

    /// <summary>開閉状態を書き出すまでの待ち時間（ms）。連続操作を 1 回にまとめる。</summary>
    private const int SectionStateSaveDebounceMs = 500;

    /// <summary>
    /// 開いている「変更」節に配る高さの重み。
    /// 主役の一覧なので、他の節より広く取る。
    /// </summary>
    private const double ChangesSectionWeight = 3.0;

    /// <summary>開いているその他の節に配る高さの重み。</summary>
    private const double OtherSectionWeight = 2.0;

    // ── 節とコントロールの対応 ────────────────────────────────

    /// <summary>
    /// 1 つの節に対応するコントロール一式。
    /// 「節 → どのコントロールか」の対応表をここ 1 か所に閉じ込め、
    /// 各所に switch を複製しない。
    /// </summary>
    private sealed class SectionUi
    {
        /// <summary>どの節か。</summary>
        public required VersionControlSection Section { get; init; }

        /// <summary>節全体（見出し＋本文）を載せている入れ物。表示・非表示はこれで切る。</summary>
        public required FrameworkElement Container { get; init; }

        /// <summary>見出しのトグル（開閉ハンドルと文言を持つ）。</summary>
        public required ToggleButton Header { get; init; }

        /// <summary>本文。閉じているときは折りたたむ。</summary>
        public required FrameworkElement Body { get; init; }

        /// <summary>この節が占めるグリッドの行。開閉で高さを切り替える。</summary>
        public required RowDefinition Row { get; init; }
    }

    /// <summary>節とコントロールの対応表（表示順）。</summary>
    private SectionUi[] _sections = Array.Empty<SectionUi>();

    /// <summary>開閉状態の保存先（初回アクセス時に 1 度だけ読み込む）。</summary>
    private VersionControlPanelStateStore? _sectionStore;

    /// <summary>保存デバウンス用タイマ（初回の保存要求で UI スレッド上に作る）。</summary>
    private DispatcherTimer? _sectionSaveTimer;

    /// <summary>復元中は保存要求を無視する（途中の状態を書かないため）。</summary>
    private bool _suppressSectionSave;

    /// <summary>ロック一覧を 1 度でも取りに行ったか（見出しの件数を「…」にするかの判定）。</summary>
    private bool _locksLoaded;

    /// <summary>履歴を 1 度でも取りに行ったか。</summary>
    private bool _historyLoaded;

    /// <summary>いま履歴を何件まで要求しているか（「さらに読み込む」で増える）。</summary>
    private int _historyLimit = VersionControlMessages.PANEL_HISTORY_PAGE_SIZE;

    /// <summary>開閉状態の保存先（未読込なら読み込む）。</summary>
    private VersionControlPanelStateStore SectionStore
    {
        get
        {
            if (_sectionStore is not null) return _sectionStore;

            _sectionStore = VersionControlPanelStateStore.Load(
                SEEDEditor.Settings.EditorPaths.SettingsDir);

            foreach (var warning in _sectionStore.Warnings) EditorLog.Write(warning);
            return _sectionStore;
        }
    }

    /// <summary>
    /// 保存キー。プロジェクトルートを使う（プロジェクト未確定なら保存も復元もしない）。
    /// </summary>
    private string? SectionStateProjectKey
        => VersionControlPanelStateStore.MakeProjectKey(SEEDEditor.Project.ProjectContext.RootDir);

    // ============================================================
    //  初期化
    // ============================================================

    /// <summary>
    /// 節とコントロールを結び付け、保存済みの開閉状態を復元する
    /// （コンストラクタから 1 度だけ呼ぶ）。
    /// </summary>
    private void InitializeSections()
    {
        _sections = new[]
        {
            new SectionUi
            {
                Section   = VersionControlSection.Conflicts,
                Container = SectionConflicts,
                Header    = HeaderConflicts,
                Body      = BodyConflicts,
                Row       = RowConflicts,
            },
            new SectionUi
            {
                Section   = VersionControlSection.Changes,
                Container = SectionChanges,
                Header    = HeaderChanges,
                Body      = BodyChanges,
                Row       = RowChanges,
            },
            new SectionUi
            {
                Section   = VersionControlSection.Locks,
                Container = SectionLocks,
                Header    = HeaderLocks,
                Body      = BodyLocks,
                Row       = RowLocks,
            },
            new SectionUi
            {
                Section   = VersionControlSection.History,
                Container = SectionHistory,
                Header    = HeaderHistory,
                Body      = BodyHistory,
                Row       = RowHistory,
            },
        };

        // 保存が無い節は VersionControlSections の既定値で埋まる。
        var saved = SectionStore.GetSections(SectionStateProjectKey);

        _suppressSectionSave = true;
        try
        {
            foreach (var ui in _sections)
            {
                ui.Header.IsChecked = saved.TryGetValue(ui.Section, out var expanded)
                    ? expanded
                    : VersionControlSections.DefaultIsExpanded(ui.Section);
            }
        }
        finally { _suppressSectionSave = false; }

        SyncSectionHeaders();
        ApplySectionLayout();
    }

    // ============================================================
    //  見出しと配置
    // ============================================================

    /// <summary>
    /// 節の見出し（件数つき）と表示可否を、いまの状態から作り直す。
    /// </summary>
    private void SyncSectionHeaders()
    {
        foreach (var ui in _sections)
        {
            var count = CountFor(ui.Section);

            ui.Header.Content       = VersionControlSections.ToHeaderText(ui.Section, count);
            ui.Container.Visibility = VersionControlSections.IsVisible(ui.Section, count)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        ApplySectionLayout();
    }

    /// <summary>
    /// 節の見出しに出す件数を求める（まだ取りに行っていないものは null）。
    /// </summary>
    /// <param name="section">節。</param>
    private int? CountFor(VersionControlSection section) => section switch
    {
        // 競合と変更は状態から即座に分かる。状態そのものが未取得なら「…」。
        VersionControlSection.Conflicts => _state.Status is null
            ? null
            : _state.UnresolvedConflictCount,

        // ツリーへ入った件数を使う（見出しと一覧の件数を必ず一致させるため）。
        VersionControlSection.Changes => _state.Status is null ? null : _tree?.FileCount ?? 0,

        // ロックはサーバ往復が要る。開くまでは 0 件と断定しない。
        VersionControlSection.Locks => _locksLoaded ? _lockRows.Count : null,

        // 履歴は件数を出さない（ToHeaderText 側で無視される）。
        _ => null,
    };

    /// <summary>
    /// 開閉に応じて本文の表示と行の高さを切り替える。
    ///
    /// <para>
    /// 開いている節は高さを分け合い（<c>Star</c>）、閉じている節は見出しの高さだけ
    /// （<c>Auto</c>）になる。こうすると各節の一覧が自前でスクロールでき、
    /// 仮想化が効いたままになる。
    /// </para>
    /// </summary>
    private void ApplySectionLayout()
    {
        foreach (var ui in _sections)
        {
            var expanded = ui.Header.IsChecked == true
                           && ui.Container.Visibility == Visibility.Visible;

            ui.Body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

            if (!expanded)
            {
                ui.Row.Height = GridLength.Auto;
                continue;
            }

            var weight = ui.Section == VersionControlSection.Changes
                ? ChangesSectionWeight
                : OtherSectionWeight;

            ui.Row.Height = new GridLength(weight, GridUnitType.Star);
        }
    }

    // ============================================================
    //  開閉
    // ============================================================

    /// <summary>
    /// 節の見出しが押された。配置を組み直し、必要なら中身を取りに行く。
    /// </summary>
    /// <param name="sender">押された見出しのトグル。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnSectionToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle) return;

        var ui = _sections.FirstOrDefault(s => ReferenceEquals(s.Header, toggle));
        if (ui is null) return;

        ApplySectionLayout();
        RequestSectionStateSave();

        // 開いたときだけ取りに行く（閉じている節のために通信しない）。
        if (toggle.IsChecked == true && VersionControlSections.NeedsFetchOnExpand(ui.Section))
        {
            await ReloadSectionAsync(ui.Section);
        }
    }

    /// <summary>
    /// 「すべての履歴を表示する」リンク。履歴の節を開いて先頭へスクロールする。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private async void OnShowHistoryLinkClick(object sender, RoutedEventArgs e)
    {
        var ui = _sections.First(s => s.Section == VersionControlSection.History);

        var wasCollapsed = ui.Header.IsChecked != true;
        if (wasCollapsed)
        {
            ui.Header.IsChecked = true;
            ApplySectionLayout();
            RequestSectionStateSave();
        }

        // 見出しを画面内へ入れてから、一覧の先頭へ戻す。
        ui.Header.BringIntoView();
        if (_historyRows.Count > 0) HistoryList.ScrollIntoView(_historyRows[0]);

        // まだ取っていない（または閉じていた）なら取りに行く。
        if (wasCollapsed || !_historyLoaded) await ReloadHistoryAsync();
    }

    /// <summary>
    /// いま開いている節のうち、サーバ往復が要るものを取り直す。
    /// </summary>
    private async Task ReloadExpandedSectionsAsync()
    {
        foreach (var ui in _sections)
        {
            if (ui.Header.IsChecked != true) continue;
            if (ui.Container.Visibility != Visibility.Visible) continue;
            if (!VersionControlSections.NeedsFetchOnExpand(ui.Section)) continue;

            await ReloadSectionAsync(ui.Section);
        }
    }

    /// <summary>1 つの節の中身を取り直す。</summary>
    /// <param name="section">節。</param>
    private async Task ReloadSectionAsync(VersionControlSection section)
    {
        switch (section)
        {
            case VersionControlSection.Locks:
                await ReloadLocksAsync();
                break;

            case VersionControlSection.History:
                await ReloadHistoryAsync();
                break;
        }
    }

    /// <summary>その節がいま開いているか。</summary>
    /// <param name="section">節。</param>
    private bool IsSectionExpanded(VersionControlSection section)
        => _sections.FirstOrDefault(s => s.Section == section)?.Header.IsChecked == true;

    // ============================================================
    //  保存
    // ============================================================

    /// <summary>
    /// 開閉状態の保存を予約する（デバウンス）。UI スレッドから呼ぶこと。
    /// </summary>
    private void RequestSectionStateSave()
    {
        if (_suppressSectionSave) return;

        if (_sectionSaveTimer is null)
        {
            _sectionSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(SectionStateSaveDebounceMs),
            };
            _sectionSaveTimer.Tick += (_, _) => FlushSectionState();
        }

        // Stop → Start で「最後の変更から SectionStateSaveDebounceMs 後」に倒す。
        _sectionSaveTimer.Stop();
        _sectionSaveTimer.Start();
    }

    /// <summary>
    /// 予約中の保存を打ち切って即座に書き出す。
    /// </summary>
    public void FlushSectionState()
    {
        _sectionSaveTimer?.Stop();

        var key = SectionStateProjectKey;
        if (key is null) return;

        var snapshot = new Dictionary<VersionControlSection, bool>();
        foreach (var ui in _sections)
        {
            snapshot[ui.Section] = ui.Header.IsChecked == true;
        }

        var store = SectionStore;
        store.SetSections(key, snapshot);

        if (!store.Save())
        {
            EditorLog.Write(
                $"バージョン管理パネルの節の状態を保存できませんでした: {store.LastSaveError}");
        }
    }
}
