// ============================================================
//  MainWindow.AndroidSnapshot.cs — Android の一時停止中に、端末のシーンの写しをシーンパネルへ閲覧専用で出す結線
//                                  （docs/android.md §20.17）
//
//  【役割】WPF の結線だけ。判断と段取りは WPF 非依存のクラス（単体テスト editor/tests/AndroidRunUiTests）:
//    - 写しを出す・戻す段取り（いつ出すか・保存の確認・未保存フラグ）… AndroidPauseSnapshotViewCoordinator
//    - 編集用ランタイムへの命令と応答（SNAPSHOT_VIEW_BEGIN / END・カメラ）… SceneSnapshotViewSession
//    - 閲覧専用の判断（保存・編集・シーンの切り替えの拒否・無効表示・バナー・タイトル）… Scene/EditorReadOnlyPolicy
//    - ビューポート（写しを出している間はランタイムを見せてバナー）… AndroidViewportPolicy（MainWindow.AndroidRun.cs が当てる）
//  端末からの写しの取り出し（SNAPSHOT_SCENE → run-as）は AndroidRunController（一時停止を送った後）。
//
//  【スレッド】段取りの知らせ（StateChanged）は任意のスレッドから届くので Dispatcher へ回す。段取りそのもの
//  （OnRunStateChanged・保存の確認）は UI スレッドから呼び、await の続きも UI スレッドで走る。
// ============================================================

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SEEDEditor.AndroidRun;
using SEEDEditor.Runtime;
using SEEDEditor.Scene;
using SEEDEditor.SceneSnapshot;

namespace SEEDEditor;

public partial class MainWindow : IAndroidSnapshotViewHost
{
    // ── 文言 ─────────────────────────────────────────────────

    /// <summary>編集用ランタイムがまだ無いときの理由。</summary>
    private const string SnapshotNoRuntimeReason = "エディタの編集用ランタイムがまだ起動していません";

    /// <summary>編集用ランタイムが Edit でないときの理由の書式（{0}=状態）。</summary>
    private const string SnapshotNotEditReasonFormat = "エディタの編集用ランタイムが Edit ではありません（{0}）";

    /// <summary>編集用ランタイムとの通信路がつながっていないときの理由。</summary>
    private const string SnapshotPipeNotConnectedReason = "エディタの編集用ランタイムとの通信路がつながっていません";

    /// <summary>退避できなかったときの確認の題。</summary>
    private const string SnapshotSavePromptTitle = "端末の写しを表示する前に";

    /// <summary>退避できなかったときの確認の本文の書式（{0}=理由）。</summary>
    private const string SnapshotSavePromptFormat =
        "端末の一時停止の写しをシーンパネルに表示するには、編集中のシーンを一時的に退避する必要がありますが、退避できませんでした。\n" +
        "理由: {0}\n\n" +
        "未保存の変更を保存してから表示しますか？（表示しない場合も一時停止は続いています）";

    /// <summary>確認の「保存して表示」の文言。</summary>
    private const string SnapshotSaveAndShowText = "保存して表示";

    /// <summary>確認の「表示しない」の文言。</summary>
    private const string SnapshotDontShowText = "表示しない";

    /// <summary>確認の「キャンセル」の文言（表示しないのと同じ）。</summary>
    private const string SnapshotPromptCancelText = "キャンセル";

    /// <summary>閲覧専用で命令を捨てたときのトースト。</summary>
    private const string SnapshotRefusedToast = "端末の写し（閲覧専用）は編集できません";

    /// <summary>閲覧専用で別のシーンを開かせないときのトースト。</summary>
    private const string SnapshotSceneSwitchToast = "端末の写しを表示中はシーンを切り替えられません（再開・停止で戻ります）";

    /// <summary>シーン編集モードの「通常」の項目の Tag（地形モードを抜けるときに選ぶ）。</summary>
    private const string CommonSceneModeTag = "common";

    /// <summary>写しの表示中にシーンの自動再読込を見送った理由（シーンの自動再読込の状態表示に出る）。</summary>
    private const string SnapshotReloadSkippedMessage =
        "端末の一時停止の写しを表示中のため、シーンの再読込を見送りました（再開・停止で戻った後に「ディスクから再読込」で取り込めます）";

    // ── 状態 ─────────────────────────────────────────────────

    /// <summary>写しをシーンパネルへ出す・戻す段取り（Android の実行を使えないときは null）。</summary>
    private AndroidPauseSnapshotViewCoordinator? _snapshotView;

    /// <summary>「保存して表示」で保存の完了を待っているときの待ち合わせ（待っていなければ null）。</summary>
    private TaskCompletionSource<bool>? _pendingSnapshotSave;

    /// <summary>写しを出している間の閉じる操作を、戻し終えてからやり直すか（二重に戻さない）。</summary>
    private bool _closeAfterSnapshotView;

    /// <summary>いまの読み取り専用・閲覧専用の判断（保存・編集の入口とパネルの無効表示が使う）。</summary>
    private EditorReadOnlyState CurrentReadOnlyState => EditorReadOnlyPolicy.Decide(new EditorReadOnlyInput
    {
        SceneLocked        = _sceneReadOnly,
        SceneLockReason    = _sceneReadOnly
            ? string.Format(SceneLock.DENY_LOCKED_FORMAT, _sceneLockHolder?.Describe() ?? "別プロセス")
            : null,
        SnapshotViewActive = _snapshotView?.IsViewActive ?? false,
        SnapshotTargetText = _snapshotView?.ShownTargetText,
    });

    // ── 初期化 ───────────────────────────────────────────────

    /// <summary>
    /// 写しをシーンパネルへ出す段取りを用意する（InitRunTargets で Android の実行を用意したときに 1 回）。
    /// </summary>
    private void InitAndroidSnapshotView()
    {
        var session = new SceneSnapshotViewSession(new RuntimeManagerSnapshotViewRuntime(() => _runtimeManager));
        var coordinator = new AndroidPauseSnapshotViewCoordinator(session, this);
        coordinator.OutputWritten += line => EditorLog.Write(line.Text, line.Style);
        coordinator.StateChanged += () => Dispatcher.BeginInvoke(() =>
        {
            ApplyAndroidViewport();
            ApplyEditorReadOnly();
        });
        _snapshotView = coordinator;
    }

    /// <summary>
    /// 編集用ランタイムから 1 行届いた（受信のスレッドから）。閲覧専用で捨てた知らせを間引いて出す。
    /// </summary>
    /// <param name="line">行。</param>
    private void OnRuntimeLineForSnapshotView(string line)
    {
        if (!SceneSnapshotWire.IsViewRefused(line)) return;
        var command = line[SceneSnapshotWire.ViewRefusedPrefix.Length..];
        Dispatcher.BeginInvoke(() =>
        {
            if (_snapshotView?.OnRefused(command) == true) ShowToast(SnapshotRefusedToast);
        });
    }

    /// <summary>
    /// PC のランタイムの状態が変わった（ApplyUiState の最後。UI スレッド）。写しを出している間に編集用ランタイムが
    /// Edit でなくなった（落ちて作り直し等）なら、退避も写しも失われたので出していない状態へ戻す。
    /// </summary>
    /// <param name="state">PC のランタイムの状態。</param>
    private void OnPcStateChangedForSnapshotView(EditorState state)
    {
        if (state != EditorState.Edit) _snapshotView?.OnEditRuntimeLost();
        ApplyEditorReadOnly();
    }

    // ── 閲覧専用の適用 ───────────────────────────────────────

    /// <summary>
    /// 読み取り専用・閲覧専用の判断をパネルとタイトルへ当てる（UI スレッド）。
    /// 写しの表示中は、インスペクタ・ヒエラルキーの編集・ギズモの移動/回転/拡縮・地形モード・アクタータブを無効表示にする。
    /// </summary>
    private void ApplyEditorReadOnly()
    {
        var state = CurrentReadOnlyState;
        PanelInspector.SetReadOnly(state.EditDenialReason);
        PanelHierarchy.SetReadOnly(state.EditDenialReason);

        // シーンビューのギズモ（移動・回転・拡縮）。選択ツールは見るために残す（ランタイムも写しの表示中は選択ツールに固定する）
        var editing = state.EditingUiEnabled;
        BtnToolMove.IsEnabled   = editing;
        BtnToolRotate.IsEnabled = editing;
        BtnToolScale.IsEnabled  = editing;
        // 地形モードの切り替えとアクター編集のタブ（どちらも編集の入口）
        CmbSceneMode.IsEnabled = editing;
        ActorTabBar.IsEnabled  = editing && _runtimeManager?.State == EditorState.Edit;
        UpdateTitle();
    }

    /// <summary>
    /// 写しの表示中に別のシーンを開こうとしたら止める（トーストを出して true）。
    /// </summary>
    /// <returns>止めたら true。</returns>
    private bool RefuseSceneSwitchIfSnapshotView()
    {
        if (CurrentReadOnlyState.SceneSwitchAllowed) return false;
        EditorLog.Write($"シーンの切り替えを止めました: {EditorReadOnlyPolicy.SnapshotEditDenialReason}");
        ShowToast(SnapshotSceneSwitchToast);
        return true;
    }

    // ── 閉じる ───────────────────────────────────────────────

    /// <summary>
    /// 閉じる前に写しの表示をやめる。出していれば閉じる操作を取り消し、戻し終えてから閉じ直す（true を返す）。
    /// 戻した後の「保存しますか」は元の編集中のシーンについて尋ねられる。
    /// </summary>
    /// <returns>閉じる操作を取り消したら true。</returns>
    private bool DeferCloseUntilSnapshotViewEnds()
    {
        if (_snapshotView is not { IsViewActive: true } coordinator) return false;
        if (_closeAfterSnapshotView) return true;
        _closeAfterSnapshotView = true;
        _ = EndSnapshotViewThenCloseAsync(coordinator);
        return true;
    }

    /// <summary>写しの表示をやめてから閉じ直す。</summary>
    private async Task EndSnapshotViewThenCloseAsync(AndroidPauseSnapshotViewCoordinator coordinator)
    {
        try
        {
            await coordinator.EndForShutdownAsync();
        }
        finally
        {
            _closeAfterSnapshotView = false;
            Close();
        }
    }

    // ── IAndroidSnapshotViewHost（段取りから UI スレッドで呼ばれる）──────────

    /// <inheritdoc />
    bool IAndroidSnapshotViewHost.IsEditorDirty => _isDirty;

    /// <inheritdoc />
    string? IAndroidSnapshotViewHost.SnapshotViewBlocker => _runtimeManager switch
    {
        null => SnapshotNoRuntimeReason,
        { State: not EditorState.Edit } runtime => string.Format(SnapshotNotEditReasonFormat, runtime.State),
        { IsPipeConnected: false } => SnapshotPipeNotConnectedReason,
        _ => null,
    };

    /// <inheritdoc />
    void IAndroidSnapshotViewHost.PrepareForSnapshotView()
    {
        // 押下中のカメラキー・モーダルの変形・配置モードを終える（写しへ持ち込まない）
        ReleaseAllCamKeys();
        if (_modalTransformActive)
        {
            _runtimeManager?.SendToRuntime("MODAL:CANCEL");
            SetModalTransformActive(false);
        }
        if (_placementModeActive) SendPlacementCancel();
        // キャンバス編集タブは閉じてアクターをシーンへ戻す（PC の Play の前と同じ。退避に正しく含めるため）
        CloseActiveSceneCanvasTab();
        EndInactiveSceneCanvasTabs();
        // アクター編集中ならシーンモードへ戻す（タブは残す。シーンを開くときと同じ）
        if (_activeActorPath != null)
        {
            _activeActorPath = null;
            PanelHierarchy.SetActorEditMode(false);
            PanelInspector.SetActorEditMode(false);
            RebuildActorTabBar();
        }
        // 地形モードなら通常モードへ（地形の編集は写しに対してできない。帯と同じ行の地形ツールバーも閉じる）
        if (_terrainMode)
        {
            var common = CmbSceneMode.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (item.Tag as string) == CommonSceneModeTag);
            if (common is not null) CmbSceneMode.SelectedItem = common;
        }
    }

    /// <inheritdoc />
    void IAndroidSnapshotViewHost.OnSnapshotShown()
    {
        // 写しの .scene の settings 節で入れ替わったビューポートの設定（表示モード・ポストエフェクト・2D/3D のタブ等）を
        // エディタの今の値へ揃え直す（シーンは汚さない。SyncViewportSettings の説明どおり SET_SCENE_SETTINGS は送らない）
        if (_viewportSettingsInitialized) SyncViewportSettings();
    }

    /// <inheritdoc />
    void IAndroidSnapshotViewHost.ApplyDirtyAfterRestore(bool dirty)
    {
        _isDirty = dirty;
        UpdateTitle();
        // 写しの読み込みで入れ替わったビューポートの設定（グリッド・ポストエフェクト等）をエディタの値へ揃え直す
        if (_viewportSettingsInitialized) SyncViewportSettings();
    }

    /// <inheritdoc />
    async Task<bool> IAndroidSnapshotViewHost.SaveBeforeViewAsync(string reason)
    {
        // 押すと何が起きるかが分かる文言の 3 択（docs/editor_ui_style.md 8 章。はい／いいえの MessageBox は使わない）。
        // 表示しない・キャンセルはどちらも出さない（一時停止は続く）。ヘッドレスでは確認を出せないのでキャンセル＝出さない
        var choice = SEEDEditor.Headless.EditorDialogs.ShowActionChoice(
            string.Format(SnapshotSavePromptFormat, reason),
            SnapshotSavePromptTitle,
            SnapshotSaveAndShowText,
            SnapshotDontShowText,
            SnapshotPromptCancelText,
            this);
        if (choice != SEEDEditor.Dialogs.ActionChoice.Primary) return false;

        // 保存は非同期（完了は OnSaveCompleted → ContinuePendingSnapshotSave）。送れなければ表示しない
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSnapshotSave = pending;
        if (!DoQuickSave())
        {
            _pendingSnapshotSave = null;
            return false;
        }
        return await pending.Task;
    }

    /// <summary>
    /// 「保存して表示」の続き（OnSaveCompleted から呼ぶ。UI スレッド）。待っていなければ何もしない。
    /// </summary>
    /// <param name="saved">保存に成功したか。</param>
    private void ContinuePendingSnapshotSave(bool saved)
    {
        var pending = _pendingSnapshotSave;
        _pendingSnapshotSave = null;
        pending?.TrySetResult(saved);
    }

    // ── 編集用ランタイムの窓口 ─────────────────────────────────

    /// <summary>
    /// 写しを出す先（エディタの編集用ランタイム）の窓口。RuntimeManager の通信路で送り、届いた行を渡す。
    /// RuntimeManager は作り直されることがあるので、使うたびに今のものを取り出す。
    /// </summary>
    /// <param name="runtime">今の RuntimeManager を返す関数。</param>
    private sealed class RuntimeManagerSnapshotViewRuntime(Func<RuntimeManager?> runtime) : ISceneSnapshotViewRuntime
    {
        /// <summary>購読している RuntimeManager（作り直されたら付け替える）。</summary>
        private RuntimeManager? _subscribed;

        /// <summary>受け手（購読の付け替えのため自前で持つ）。</summary>
        private Action<string>? _handlers;

        /// <inheritdoc />
        public bool CanSend => runtime() is { State: EditorState.Edit, IsPipeConnected: true };

        /// <inheritdoc />
        public bool Send(string command)
        {
            var current = runtime();
            if (current is not { State: EditorState.Edit, IsPipeConnected: true }) return false;
            current.SendToRuntime(command);
            return true;
        }

        /// <inheritdoc />
        public event Action<string>? MessageReceived
        {
            add
            {
                _handlers += value;
                Resubscribe();
            }
            remove
            {
                _handlers -= value;
                Resubscribe();
            }
        }

        /// <summary>今の RuntimeManager の生の行を購読し直す（受け手が居なければ外す）。</summary>
        private void Resubscribe()
        {
            var current = _handlers is null ? null : runtime();
            if (ReferenceEquals(current, _subscribed)) return;
            if (_subscribed is not null) _subscribed.RawMessageReceived -= OnLine;
            _subscribed = current;
            if (_subscribed is not null) _subscribed.RawMessageReceived += OnLine;
        }

        /// <summary>届いた行を受け手へ渡す。</summary>
        private void OnLine(string line) => _handlers?.Invoke(line);
    }
}
