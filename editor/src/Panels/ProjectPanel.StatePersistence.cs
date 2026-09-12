// ============================================================
//  ProjectPanel.StatePersistence.cs — タブ状態の永続化（保存タイミングとビューへの再適用）
//
//  【役割】
//  ProjectPanel.Tabs.cs が持つセッション内のタブ状態を、エディタの再起動をまたいで保つ。
//  保存する内容は「開いているタブの一覧と各タブの場所・アクティブなタブ・
//  各タブのツリー展開集合／選択／スクロール位置」。
//
//  【責務の分け方】
//    ・JSON の読み書き、パスの相対化・絶対化、存在しないパスの間引き
//        → SEEDEditor.Assets.ProjectPanelStateStore（WPF 非依存・単体テスト対象）
//    ・いつ保存するか（デバウンス）、どうビューへ戻すか
//        → このファイル（WPF に依存する部分だけ）
//
//  【保存タイミング】
//  タブの追加・削除・切替・フォルダ移動・ツリー展開の変化・スクロールのたびに
//  書いていると、ツリーを連続開閉しただけでファイル I/O が頻発する。
//  そこで DispatcherTimer で「最後の変更から SaveDebounceMs 後に 1 回」へ倒す
//  （EditorViewState と同じ流儀）。エディタ終了時は MainWindow の Closing から
//  FlushTabState() を呼んで確実に書き出す。
//
//  【保存キー】
//  プロジェクトルート。1 つのエディタ設定フォルダを複数プロジェクトで共有するため、
//  プロジェクトごとに 1 エントリを持つ。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SEEDEditor.Assets;

namespace SEEDEditor.Panels;

public partial class ProjectPanel
{
    // ── 定数（マジックナンバー回避）────────────────────────────

    /// <summary>
    /// 保存デバウンス時間（ms）。
    /// ツリーの連続開閉・スクロール中は書かず、手が止まってから 1 回だけ書く。
    /// 「スクロール停止時に保存」もこの値で実現している（ScrollChanged のたびにタイマを倒し直す）。
    /// </summary>
    private const int TabStateSaveDebounceMs = 500;

    // ── 状態 ─────────────────────────────────────────────────

    /// <summary>
    /// 保存ストア（初回アクセス時に 1 度だけ読み込む）。
    ///
    /// <para>
    /// MainWindow から Init を注入せず遅延生成にしているのは、このパネルが
    /// MainWindow の XAML 展開時に作られ、<see cref="SetAssetsPath"/> が
    /// MainWindow 側の設定初期化より前に走るため。初期化順に依存しない形にしておく。
    /// </para>
    /// </summary>
    private ProjectPanelStateStore? _stateStore;

    /// <summary>保存デバウンス用タイマ（初回の保存要求で UI スレッド上に作る）。</summary>
    private DispatcherTimer? _stateSaveTimer;

    /// <summary>
    /// 保存要求を無視する区間か。復元中（ビューをプログラムから動かしている最中）に
    /// 保存が走ると、適用途中の中途半端な状態を書いてしまうため立てる。
    /// </summary>
    private bool _suppressTabStateSave;

    /// <summary>ストアを取得する（未読込なら読み込む）。</summary>
    private ProjectPanelStateStore StateStore
    {
        get
        {
            if (_stateStore is not null) return _stateStore;

            _stateStore = ProjectPanelStateStore.Load(SEEDEditor.Settings.EditorPaths.SettingsDir);
            foreach (var warning in _stateStore.Warnings) EditorLog.Write(warning);
            return _stateStore;
        }
    }

    /// <summary>
    /// 保存キー。プロジェクトルートを使い、プロジェクト未確定（単体起動・テスト）なら
    /// アセットルートで代用する（キーが作れないと保存も復元も行わない）。
    /// </summary>
    private string? TabStateProjectKey
    {
        get
        {
            var root = SEEDEditor.Project.ProjectContext.RootDir;
            if (string.IsNullOrEmpty(root)) root = _assetsRoot;
            return ProjectPanelStateStore.MakeProjectKey(root);
        }
    }

    // ── 配線 ─────────────────────────────────────────────────

    /// <summary>
    /// 「状態が変わった」ことを拾うイベントを張る（コンストラクタから 1 度だけ呼ぶ）。
    ///
    /// <para>
    /// タブの追加・削除・切替・フォルダ移動は ProjectPanel.Tabs.cs 側の操作関数から
    /// 直接 <see cref="RequestTabStateSave"/> を呼ぶ。ここで拾うのは
    /// 「ユーザー操作の結果としてビューが動いた」種類の 2 つだけ。
    /// </para>
    /// </summary>
    private void WireTabStatePersistence()
    {
        // フォルダツリーの展開／折りたたみ。TreeViewItem.Expanded / Collapsed は
        // バブリングするため TreeView 側で 1 箇所にまとめて受けられる。
        // OnNodeExpanded が e.Handled = true にするので handledEventsToo: true が要る。
        FolderTree.AddHandler(TreeViewItem.ExpandedEvent,
            new RoutedEventHandler(OnTreeExpansionChangedForSave), handledEventsToo: true);
        FolderTree.AddHandler(TreeViewItem.CollapsedEvent,
            new RoutedEventHandler(OnTreeExpansionChangedForSave), handledEventsToo: true);

        // ファイル一覧のスクロール。デバウンスにより実質「スクロールが止まったら保存」になる。
        FileScrollViewer.ScrollChanged += OnFileScrollChangedForSave;
    }

    /// <summary>ツリーの展開状態が変わった（保存を予約する）。</summary>
    /// <param name="sender">イベント発生元（未使用）。</param>
    /// <param name="e">ルーティングイベント引数（未使用）。</param>
    private void OnTreeExpansionChangedForSave(object sender, RoutedEventArgs e)
    {
        // プログラムからの一括展開（タブ切替・復元）は保存対象にしない。
        // その経路は操作側が適切なタイミングで保存を予約する。
        if (_suppressTreeEvent) return;
        RequestTabStateSave();
    }

    /// <summary>ファイル一覧がスクロールされた（保存を予約する）。</summary>
    /// <param name="sender">イベント発生元（未使用）。</param>
    /// <param name="e">スクロール変化の情報（未使用）。</param>
    private void OnFileScrollChangedForSave(object sender, ScrollChangedEventArgs e)
        => RequestTabStateSave();

    // ── 保存 ─────────────────────────────────────────────────

    /// <summary>
    /// 保存を予約する（デバウンス）。UI スレッドから呼ぶこと。
    /// タブ操作・フォルダ移動・ツリー展開・スクロールのすべてがここへ集まる。
    /// </summary>
    private void RequestTabStateSave()
    {
        if (_suppressTabStateSave) return;

        if (_stateSaveTimer is null)
        {
            _stateSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(TabStateSaveDebounceMs),
            };
            _stateSaveTimer.Tick += (_, _) => FlushTabState();
        }

        // Stop → Start で「最後の変更から TabStateSaveDebounceMs 後」に倒す。
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    /// <summary>
    /// 予約中の保存を打ち切って即座に書き出す。
    /// エディタ終了時（MainWindow の Closing）から呼ぶ。
    /// </summary>
    public void FlushTabState()
    {
        _stateSaveTimer?.Stop();

        // アセットが使えない状態（警告オーバーレイ表示中）では、ツリーもタブバーも
        // 空になっている。その状態を書き込むと前回の正しい状態を壊すので何もしない。
        if (!_assetsProbe.IsAvailable) return;
        if (_tabs.Count == 0) return;

        var key = TabStateProjectKey;
        if (key is null) return;

        // 画面上の最新状態をアクティブタブへ吸い上げてからスナップショットを取る。
        CaptureActiveTabState();

        var state = ProjectPanelStateStore.BuildState(
            _assetsRoot, BuildTabSnapshots(), _activeTabIndex);
        if (state is null) return;   // 保存できるタブが 1 枚も無い（前回の内容はそのまま残す）

        var store = StateStore;
        store.Set(key, state);
        if (!store.Save())
            EditorLog.Write($"プロジェクトパネル状態の保存に失敗しました: {store.LastSaveError}");
    }

    /// <summary>現在のタブ一覧を保存用スナップショット（絶対パス）へ写し取る。</summary>
    private List<ProjectPanelTabSnapshot> BuildTabSnapshots()
        => _tabs.Select(tab => new ProjectPanelTabSnapshot(
                tab.CurrentPath,
                tab.ExpandedPaths.ToArray(),
                tab.SelectedPath,
                tab.ScrollOffset))
            .ToList();

    // ── 復元 ─────────────────────────────────────────────────

    /// <summary>
    /// 保存済みのタブ構成を復元して <see cref="_tabs"/> を組み立てる。
    ///
    /// <para>
    /// 復元できたときは、アクティブタブぶんだけビューへ適用して true を返す。
    /// 非アクティブなタブは状態を保持するだけで描画しない——こうしないと、
    /// タブ枚数ぶんファイル一覧を作り直すことになり、画像／モデルのサムネイル要求が
    /// 一気に積まれてしまう（要求キューはアクティブタブぶんだけで足りる）。
    /// </para>
    /// </summary>
    /// <param name="rootPath">アセットルートの絶対パス。</param>
    /// <returns>
    /// 復元した場合は true（ビューへの適用もここで完了している）。
    /// 保存が無い・全タブが無効だった場合は false（呼び出し側が既定の 1 枚で始める）。
    /// </returns>
    private bool TryRestoreTabs(string rootPath)
    {
        var key = TabStateProjectKey;
        if (key is null) return false;

        var resolved = ProjectPanelStateStore.Resolve(StateStore.TryGet(key), rootPath);
        if (resolved is null) return false;

        // 復元中はビューをプログラムから動かすため、保存要求を止めておく。
        _suppressTabStateSave = true;
        try
        {
            foreach (var tab in resolved.Tabs)
            {
                _tabs.Add(new ProjectFolderTab
                {
                    CurrentPath   = tab.FolderPath,
                    ExpandedPaths = new HashSet<string>(tab.ExpandedPaths, StringComparer.OrdinalIgnoreCase),
                    SelectedPath  = tab.SelectedPath,
                    ScrollOffset  = tab.ScrollOffset,
                });
            }
            _activeTabIndex = resolved.ActiveTabIndex;

            RebuildTabBar();
            // 描画はアクティブタブの 1 回だけ（ファイル一覧の構築＝サムネイル要求もこの 1 回だけ）。
            ApplyTabState(_tabs[_activeTabIndex]);
        }
        finally { _suppressTabStateSave = false; }

        EditorLog.Write(
            $"プロジェクトパネルの状態を復元しました: タブ {_tabs.Count} 枚 / " +
            $"アクティブ {_activeTabIndex} / 場所 {_tabs[_activeTabIndex].CurrentPath}");
        return true;
    }
}
