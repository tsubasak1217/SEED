// ============================================================
//  AnimationTimelinePanel.xaml.cs — キーフレームアニメーション編集パネル
//
//  .anim クリップ（AnimClip モデル、AnimClipIO で読み書き）を
//  トラックリスト + ドープシート（DopeSheetPanel）で編集する。
//
//  【動作モード】
//  ・アクターモード: 選択中アクターが AnimatorComponent を持つ場合、
//    そのクリップ一覧から選択して編集する。Edit モード中はプレビュー
//    （ANIM_PREVIEW）を Runtime へ送信できる。
//  ・ファイル単独モード:「直接開く」で任意の .anim を開く。アクター文脈が
//    無いためプレビュー不可（ボタンをグレーアウト）。
//
//  【時間軸はフレーム基準】
//  クリップは編集用フレームレート（AnimClip.Fps、.anim の "fps"）を持ち、
//  ルーラー・プレイヘッド・キー操作はすべてフレーム境界にスナップする。
//  秒⇔フレームの変換は AnimFrameMath に集約している（ランタイムは秒しか見ない）。
//
//  【編集文脈（Animator アクタとキー対象アクタ）】
//  選択アクタが Animator を持たない場合、ヒエラルキーを遡って最も近い
//  Animator 保持アクタを編集文脈に採用し、選択アクタ自身は「キー対象」として覚える。
//  新規トラックの actor_path はその相対パスで自動的に埋まる（AnimHierarchyNav）。
//  🔒 トグルで文脈を固定でき、以後アクタ選択が変わっても切り替わらない。
//
//  【IPC】
//  ・ANIM_PREVIEW:{actor},{clip_path},{time} — プレビュー中は毎 tick 送信
//  ・ANIM_PREVIEW_STOP:{actor} — プレビュー終了時に必ず送信し元値を復元する
//  ・ANIM_PREVIEW_CLIP:{clip_path},{json_base64} — 未保存の編集内容をランタイムへ反映（ライブプレビュー）
//  ・ANIM_RELOAD:{clip_path} — 保存直後にランタイムのキャッシュを破棄させる
//  ・GET_ACTOR_COMPONENTS:{dfs_id} — 祖先の Animator 探索で使う（選択は変えない）
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SEEDEditor.Panels.AnimationTimeline;
using SEEDEditor.Runtime;

namespace SEEDEditor.Panels;

/// <summary>
/// アニメーションタイムラインのドッキングパネル本体。
/// トラックリスト・ドープシート・値エディタ・.anim ファイル I/O・プレビュー送信を統括する。
/// </summary>
public partial class AnimationTimelinePanel : UserControl
{
    // ── Runtime 連携 ────────────────────────────────────────────
    private RuntimeManager? _runtime;
    private string          _assetsPath = "";

    /// <summary>プレビュー対象アクターの DFS ID（-1 = アクター文脈なし＝ファイル単独モード）。</summary>
    private int _actorDfsId = -1;

    /// <summary>選択中アクターが持つクリップ一覧（name, kind, path）。ComboBox の元データ。
    /// kind="model"（glTF 内蔵アニメ）のクリップは path が空文字のままで、ドープシート編集の対象外になる。</summary>
    private readonly List<(string Name, string Kind, string Path)> _actorClips = new();

    /// <summary>ファイル単独モード（「直接開く」で開いた）かどうか。true の間は選択アクターの変化を無視する。</summary>
    private bool _isFileOnlyMode;

    /// <summary>現在 ComboBox で選択中のクリップが kind=model（モデル内蔵アニメ）かどうか。
    /// true の間はドープシート・再生プレビューを無効化し、案内文を表示する
    /// （model クリップは .anim を持たず ANIM_PREVIEW/ドープシート編集の対象外のため）。</summary>
    private bool _isModelClipSelected;

    /// <summary>クリップ未選択時の既定の案内文（model クリップ選択時のみ別文言に差し替える）。</summary>
    /// <summary>再生/一時停止トグルのアイコン一辺サイズ（px）。XAML の初期表示と同値。</summary>
    private const double PlayPauseIconSize = 11.0;

    private const string DefaultNoClipHint = "Animator 付きアクタを選択するか、「直接開く」で .anim を開いてください";
    private const string ModelClipHint     = "モデル内蔵アニメは編集できません（再生設定は Inspector で行ってください）";

    // ── 編集中クリップ ──────────────────────────────────────────
    private AnimClip? _clip;
    /// <summary>編集中ファイルの絶対パス（未保存の新規クリップは null）。</summary>
    private string? _currentFilePath;
    private bool     _isDirty;
    private int      _selectedTrackIndex = -1;

    // ── プレビュー再生 ──────────────────────────────────────────
    private readonly DispatcherTimer _previewTimer;
    private float _previewTime;
    private bool  _isPlaying;
    /// <summary>ANIM_PREVIEW を送信中（=ANIM_PREVIEW_STOP で復元が必要）かどうか。</summary>
    private bool  _previewActive;

    // ── 編集文脈（Animator アクタ / キー対象アクタ）────────────────

    /// <summary>ヒエラルキー（DFS ID → 最小ノード）。HIERARCHY 通知のたびに更新する。</summary>
    private IReadOnlyDictionary<int, AnimHierarchyNode> _hierarchy =
        new Dictionary<int, AnimHierarchyNode>();

    /// <summary>ユーザーが実際に選択しているアクタの DFS ID（-1 = 未選択）。</summary>
    private int _selectedActorDfsId = -1;

    /// <summary>キーの対象アクタ（＝選択アクタ）の DFS ID。Animator アクタと同じこともある。</summary>
    private int _keyTargetDfsId = -1;

    /// <summary>Animator アクタから見たキー対象アクタへの相対 actor_path（空 = Animator 自身）。</summary>
    private string _keyTargetActorPath = "";

    /// <summary>キー対象アクタの現在値スナップショット（キー挿入で使う）。</summary>
    private AnimActorSnapshot _keyTargetSnapshot = AnimActorSnapshot.Parse("");

    /// <summary>
    /// アクター仮想ノード ID の下限。RuntimeManager.SelectionChanged が渡す id は
    /// Rust 側 send_selected() が単一選択時に付ける「999_000_000 + DFS ID」の仮想 ID であり、
    /// 生の DFS ID ではない（HierarchyPanel / InspectorPanel と同じ規約。値は両者と合わせること）。
    /// 本パネルは複数選択（SelectionMultiChanged）は購読していない＝従来通り単一選択のみ追従する。
    /// </summary>
    private const int VirtualActorNodeIdBase = 999_000_000;

    /// <summary>🔒 で文脈を固定中か。true の間はアクタ選択の変化を無視する。</summary>
    private bool _contextLocked;

    // ── 現在値スナップショットの問い合わせ（未到着時に GET_ACTOR_COMPONENTS で取りに行く）──

    /// <summary>問い合わせ中のキー対象 DFS ID（-1 = 問い合わせ中でない）。</summary>
    private int _pendingSnapshotDfsId = -1;

    /// <summary>スナップショットが届いたら再実行する処理（キー挿入・上書きの呼び出し自身）。</summary>
    private Action? _pendingSnapshotCallback;

    /// <summary>問い合わせのタイムアウト監視タイマ。</summary>
    private DispatcherTimer? _pendingSnapshotTimer;

    /// <summary>祖先 Animator 探索の対象チェーン（[自分, 親, …, ルート]）。探索していないときは null。</summary>
    private List<int>? _probeChain;

    /// <summary>_probeChain 内で現在問い合わせ中の位置。</summary>
    private int _probeCursor;

    // ── Undo / Redo ─────────────────────────────────────────────

    /// <summary>クリップ JSON スナップショットによるパネル内 Undo スタック。</summary>
    private readonly AnimUndoStack _undo = new();

    /// <summary>Undo/Redo による復元中は履歴を積まないためのガード。</summary>
    private bool _isRestoringUndo;

    /// <summary>パネルの表示タイトルが変わったことを通知する（MainWindow が LayoutAnchorable.Title へ反映）。</summary>
    public event Action<string>? TitleChanged;

    public AnimationTimelinePanel()
    {
        InitializeComponent();

        // トラック追加用ドロップダウンをレジストリから構築する
        CmbNewTrackProperty.ItemsSource       = AnimPropertyRegistry.Entries;
        CmbNewTrackProperty.DisplayMemberPath = nameof(AnimPropertyEntry.DisplayName);
        if (AnimPropertyRegistry.Entries.Count > 0) CmbNewTrackProperty.SelectedIndex = 0;

        DopeSheet.KeyAddRequested       += OnDopeSheetKeyAddRequested;
        DopeSheet.SelectionMoved        += OnDopeSheetSelectionMoved;
        DopeSheet.KeyDragEnded          += OnDopeSheetKeyDragEnded;
        DopeSheet.KeyDeleteRequested    += DeleteSelectedKeys;
        DopeSheet.SelectionChanged      += OnDopeSheetSelectionChanged;
        DopeSheet.PlayheadScrubbed      += OnPlayheadScrubbed;
        DopeSheet.PlayheadScrubEnded    += OnPlayheadScrubEnded;
        // サマリー行（「全チャンネル」）のダブルクリックは対象トラック全部への一括挿入
        DopeSheet.SummaryKeyAddRequested += OnDopeSheetSummaryKeyAddRequested;
        // トラックリストの縦位置をドープシートの縦スクロールへ追従させる
        DopeSheet.VerticalScrollChanged += OnDopeSheetVerticalScrollChanged;

        // プレビュー再生タイマ（約 60fps）。Play 中のみ Tick で時間を進める。
        _previewTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(AnimationTimelineConstants.PreviewTickIntervalMs),
        };
        _previewTimer.Tick += OnPreviewTick;

        // パネルが非表示になったら必ずプレビューを止めて元値を復元する。
        // 保留中のスナップショット問い合わせも破棄する（非表示中に応答が来ても意味が無いため）。
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                StopPreview();
                ClearPendingSnapshotRequest();
            }
        };

        // Delete キーでのキー削除（OnKeyDown）を受けるため、パネル内クリックでキーボードフォーカスを取得する。
        // ただし ComboBox / TextBox / ListBox など「自分でフォーカスを持つ入力部品」の上では奪わない。
        // 奪うと ComboBox のドロップダウンが項目を確定する前に閉じてしまい、
        // 「position 以外を選んでも position のまま」になる（実際に起きた不具合）。
        PreviewMouseDown += (_, e) =>
        {
            if (IsInsideFocusableInput(e.OriginalSource as DependencyObject)) return;
            Focus();
        };

        // ビューポート操作中（＝パネルにフォーカスが無い）でも I / U を効かせるため、
        // 所属ウィンドウの PreviewKeyDown をパネル表示中だけ購読する。
        Loaded   += OnPanelLoaded;
        Unloaded += OnPanelUnloaded;

        UpdateEmptyState();
    }

    // ── 外部からの初期化 ────────────────────────────────────────

    /// <summary>RuntimeManager を注入し、選択・コンポーネント通知を購読する。</summary>
    public void SetRuntime(RuntimeManager runtime)
    {
        if (_runtime is not null)
        {
            _runtime.SelectionChanged        -= OnSelectionChanged;
            _runtime.ActorComponentsReceived -= OnActorComponentsReceived;
            _runtime.StateChanged            -= OnRuntimeStateChanged;
            _runtime.HierarchyUpdated        -= OnHierarchyUpdated;
        }
        _runtime = runtime;
        _runtime.SelectionChanged        += OnSelectionChanged;
        _runtime.ActorComponentsReceived += OnActorComponentsReceived;
        _runtime.StateChanged            += OnRuntimeStateChanged;
        // 祖先 Animator の探索と actor_path 生成にヒエラルキーが要る
        _runtime.HierarchyUpdated        += OnHierarchyUpdated;
        UpdatePreviewAvailability();
    }

    /// <summary>アセットルート（仮想パス変換用）を設定する。</summary>
    public void SetAssetsPath(string assetsPath) => _assetsPath = assetsPath;

    /// <summary>
    /// 指定した絶対パスの .anim ファイルをファイル単独モードで開く。
    /// ProjectPanel からのダブルクリック・Inspector からの「タイムラインで編集」等の外部呼び出し用。
    /// </summary>
    public void LoadAnimFile(string fullPath)
    {
        StopPreview();
        try
        {
            _clip             = AnimClipIO.Load(fullPath);
            _currentFilePath  = fullPath;
            _isFileOnlyMode   = true;
            _isDirty          = false;
            _selectedTrackIndex = -1;
            _isModelClipSelected = false;
            _previewTime      = 0f;
            ResetUndo();
            RefreshAll();
        }
        catch (Exception ex)
        {
            EditorLog.Write($"AnimationTimelinePanel: .anim 読み込み失敗: {ex.Message}");
            MessageBox.Show(Window.GetWindow(this), $".anim の読み込みに失敗しました:\n{ex.Message}",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 現在アクター文脈で保持しているクリップ一覧から、指定パスのクリップを選択して開く。
    /// Inspector の「タイムラインで編集」ボタンから呼ばれる（アクター文脈は選択連動で既に取得済み前提）。
    /// </summary>
    public void OpenClipByPath(string virtualOrAbsolutePath)
    {
        _isFileOnlyMode = false;
        var abs = VirtualPath.ToAbsolute(virtualOrAbsolutePath, _assetsPath);
        LoadClipFromActorContext(abs);
    }

    // ── Runtime イベント ────────────────────────────────────────

    /// <summary>
    /// RuntimeManager.ChangeState は非UIスレッド（StartEditAsync 等のバックグラウンド継続）から
    /// 発火されることがあるため、他ハンドラ（OnSelectionChanged 等）と同様に Dispatcher.InvokeAsync で
    /// UIスレッドへマーシャリングしてから UI 要素（BtnPlayPause 等）を触る。
    /// </summary>
    private void OnRuntimeStateChanged(EditorState state) =>
        Dispatcher.InvokeAsync(UpdatePreviewAvailability);

    /// <summary>ヒエラルキー更新を受け取り、祖先探索用のノード表を作り直す。</summary>
    private void OnHierarchyUpdated(string json)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _hierarchy = AnimHierarchyNav.ParseHierarchy(json);
            // アクタのリネーム・親替えで actor_path が変わるため、文脈表示を張り直す
            RecomputeKeyTargetPath();
            UpdateContextInfo();
        });
    }

    private void OnSelectionChanged(int id)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_isFileOnlyMode) return; // ファイル単独モード中はアクター選択変化を無視する
            if (_contextLocked)  return; // 🔒 固定中は選択変化を無視する

            // 選択が外れた／別アクターへ移った → 進行中のプレビューを止めて元値へ復元する
            StopPreview();

            // RuntimeManager から届く id は仮想 ID（999_000_000 + DFS ID）のことがある
            // （Rust 側 send_selected() が単一選択時に必ずこの形で送る）。
            // 正規化せずに DFS ID として使うと、後続の ACTOR_COMPONENTS（id は生の DFS ID）と
            // 一切一致しなくなり、キー対象の現在値スナップショットが更新されなくなる。
            var dfsId = id >= VirtualActorNodeIdBase ? id - VirtualActorNodeIdBase : id;

            _selectedActorDfsId = dfsId;
            _probeChain         = null;   // 前回の祖先探索は破棄する
            ClearPendingSnapshotRequest(); // 選択が変わったので前回の問い合わせは無効

            if (dfsId < 0)
            {
                _actorDfsId     = -1;
                _keyTargetDfsId = -1;
                _actorClips.Clear();
                UpdateEmptyState();
                UpdateContextInfo();
            }
            // 実データは ACTOR_COMPONENTS 側で届くのでここでは文脈クリアのみ行う
        });
    }

    /// <summary>
    /// ACTOR_COMPONENTS を受け取り、編集文脈（Animator アクタ）とキー対象の現在値を更新する。
    ///
    /// 届く ACTOR_COMPONENTS は「ユーザーの選択」だけでなく、
    /// 本パネル自身の祖先探索（GET_ACTOR_COMPONENTS）や他パネルの問い合わせでも飛んでくる。
    /// そのため DFS ID を見て「選択のもの」「探索中のもの」「無関係」を明確に振り分ける。
    /// </summary>
    private void OnActorComponentsReceived(string json)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_isFileOnlyMode) return;
            if (_contextLocked)  return;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var dfsId = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : -1;
                if (dfsId < 0) return;

                var animator = FindAnimator(root);

                // ── (a) ユーザーが選択したアクタの情報 ──
                if (dfsId == _selectedActorDfsId || _selectedActorDfsId < 0)
                {
                    _selectedActorDfsId = dfsId;
                    _keyTargetDfsId     = dfsId;
                    // 現在値スナップショット（キー挿入で使う）はキー対象アクタのものだけ保持する
                    _keyTargetSnapshot  = AnimActorSnapshot.Parse(json);

                    // キー挿入・上書きが「現在値がまだ無い」ために保留していた処理があれば、
                    // ここで届いたスナップショットを使って再実行する。
                    if (_pendingSnapshotDfsId == dfsId && _pendingSnapshotCallback is { } pendingRetry)
                    {
                        ClearPendingSnapshotRequest();
                        pendingRetry();
                    }

                    if (animator is not null)
                    {
                        AdoptAnimatorContext(dfsId, animator);
                        return;
                    }

                    // Animator が無い → 祖先を遡って探す（backlog 項目 1 の修正）
                    BeginAncestorProbe(dfsId);
                    return;
                }

                // ── (b) 祖先探索の応答 ──
                if (_probeChain is not null && _probeCursor < _probeChain.Count
                                            && _probeChain[_probeCursor] == dfsId)
                {
                    if (animator is not null)
                    {
                        _probeChain = null;
                        AdoptAnimatorContext(dfsId, animator);
                        return;
                    }
                    _probeCursor++;
                    ProbeNextAncestor();
                    return;
                }

                // ── (c) 無関係な問い合わせ（他パネルのリファレンスピッカー等）は無視 ──
            }
            catch (Exception ex)
            {
                EditorLog.Write($"AnimationTimelinePanel: ACTOR_COMPONENTS 解析失敗: {ex.Message}");
            }
        });
    }

    /// <summary>ACTOR_COMPONENTS のルートから AnimatorComponent を探す（無ければ null）。</summary>
    private static AnimatorComponentInfo? FindAnimator(JsonElement root)
    {
        if (!root.TryGetProperty("components", out var comps) || comps.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var comp in comps.EnumerateArray())
        {
            var type = comp.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
            if (type != "AnimatorComponent") continue;
            return ParseAnimatorComponent(comp);
        }
        return null;
    }

    // ── 祖先 Animator の探索 ────────────────────────────────────

    /// <summary>
    /// 選択アクタが Animator を持たないとき、祖先を 1 つずつ問い合わせて
    /// 最も近い Animator 保持アクタを探し始める。
    /// ヒエラルキーが未取得（起動直後など）の場合は探索できないので空表示にする。
    /// </summary>
    private void BeginAncestorProbe(int selectedDfsId)
    {
        var chain = AnimHierarchyNav.SelfAndAncestors(_hierarchy, selectedDfsId);
        if (chain.Count <= 1)
        {
            // 祖先がいない（ルート、またはヒエラルキー未取得）
            _probeChain = null;
            ClearAnimatorContext();
            return;
        }

        _probeChain  = chain;
        _probeCursor = 1;      // 0 は自分自身（Animator 無しと判明済み）
        ProbeNextAncestor();
    }

    /// <summary>探索チェーンの次の祖先へ GET_ACTOR_COMPONENTS を投げる。尽きたら文脈なしにする。</summary>
    private void ProbeNextAncestor()
    {
        if (_probeChain is null || _probeCursor >= _probeChain.Count)
        {
            _probeChain = null;
            ClearAnimatorContext();
            return;
        }
        // GET_ACTOR_COMPONENTS は選択を変えずに 1 通だけ ACTOR_COMPONENTS を返させる
        _runtime?.SendToRuntime($"GET_ACTOR_COMPONENTS:{_probeChain[_probeCursor]}");
    }

    /// <summary>Animator が 1 つも見つからなかったときの状態（編集領域を空にする）。</summary>
    private void ClearAnimatorContext()
    {
        _actorDfsId = -1;
        _actorClips.Clear();
        _keyTargetActorPath = "";
        UpdateEmptyState();
        UpdateContextInfo();
    }

    /// <summary>
    /// 指定アクタの Animator を編集文脈として採用する。
    ///
    /// 同じ Animator・同じクリップ構成のまま子アクタを選び直しただけのときは
    /// ComboBox を作り直さない（未保存の編集内容が捨てられるのを防ぐ）。
    /// </summary>
    private void AdoptAnimatorContext(int animatorDfsId, AnimatorComponentInfo animator)
    {
        var sameContext = _actorDfsId == animatorDfsId
                       && _actorClips.Count == animator.Clips.Count
                       && !_actorClips.Where((c, i) => c != animator.Clips[i]).Any();

        _actorDfsId = animatorDfsId;
        RecomputeKeyTargetPath();

        if (sameContext)
        {
            // クリップは読み直さない。文脈表示と新規トラックの既定パスだけ更新する。
            UpdateContextInfo();
            return;
        }

        _actorClips.Clear();
        _actorClips.AddRange(animator.Clips);
        // clips が空（clips 未設定）の場合は RebuildClipCombo 内部で
        // UpdateEmptyState が呼ばれ編集領域が空になる。
        RebuildClipCombo(animator.DefaultClip);
        UpdateContextInfo();
    }

    /// <summary>Animator アクタ → キー対象アクタの相対 actor_path を計算し直す。</summary>
    private void RecomputeKeyTargetPath()
    {
        if (_actorDfsId < 0 || _keyTargetDfsId < 0) { _keyTargetActorPath = ""; return; }
        _keyTargetActorPath = AnimHierarchyNav.BuildActorPath(_hierarchy, _actorDfsId, _keyTargetDfsId) ?? "";
        // 新規トラック追加欄の既定値を、いま選んでいる子アクタへのパスにする
        TbNewTrackActorPath.Text = _keyTargetActorPath;
    }

    /// <summary>ツールバーの文脈表示（Animator アクタ / キー対象）を更新する。</summary>
    private void UpdateContextInfo()
    {
        if (_isFileOnlyMode)
        {
            TbContextInfo.Text = "ファイル単独モード（アクタ文脈なし）";
            return;
        }
        if (_actorDfsId < 0)
        {
            TbContextInfo.Text = "";
            return;
        }

        var animatorName = NodeName(_actorDfsId);
        var targetLabel  = _keyTargetActorPath.Length == 0 ? "(Animator 自身)" : _keyTargetActorPath;
        var lockMark     = _contextLocked ? " 🔒" : "";
        TbContextInfo.Text = $"Animator: {animatorName} / キー対象: {targetLabel}{lockMark}";
    }

    /// <summary>DFS ID からアクタ名を引く（未知なら "#id" 表記）。</summary>
    private string NodeName(int dfsId)
        => _hierarchy.TryGetValue(dfsId, out var n) && n.Name.Length > 0 ? n.Name : $"#{dfsId}";

    /// <summary>🔒 トグル: 編集文脈を固定／解除する。</summary>
    private void OnToggleLockContext(object sender, RoutedEventArgs e)
    {
        _contextLocked = BtnLockContext.IsChecked == true;
        UpdateContextInfo();
    }

    /// <summary>ACTOR_COMPONENTS 内の 1 コンポーネント（type=="AnimatorComponent"）を解析する。</summary>
    private sealed record AnimatorComponentInfo(List<(string Name, string Kind, string Path)> Clips, string DefaultClip);

    private static AnimatorComponentInfo ParseAnimatorComponent(JsonElement comp)
    {
        var clips = new List<(string, string, string)>();
        if (comp.TryGetProperty("clips", out var clipsEl) && clipsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in clipsEl.EnumerateArray())
            {
                var name = c.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                // kind 省略時は keyframe（旧シーン後方互換。Rust 側 #[serde(default)] と同じ既定値）
                var kind = c.TryGetProperty("kind", out var kp) ? kp.GetString() ?? "keyframe" : "keyframe";
                var path = c.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
                clips.Add((name, kind, path));
            }
        }
        var defaultClip = comp.TryGetProperty("default_clip", out var dp) ? dp.GetString() ?? "" : "";
        return new AnimatorComponentInfo(clips, defaultClip);
    }

    // ── クリップ選択 UI ─────────────────────────────────────────

    /// <summary>クリップ ComboBox の Tag（kind/path を選択変更ハンドラへ渡す）。</summary>
    private sealed record ClipComboTag(string Kind, string Path);

    /// <summary>アクターの clips 一覧から ComboBox を再構築する。可能なら defaultClip を選択状態にする。
    /// kind=model のクリップは 🎬 アイコン付きで列挙するが、選択してもドープシート編集はできない
    /// （<see cref="OnClipComboSelectionChanged"/> 側で案内表示に切り替える）。</summary>
    private void RebuildClipCombo(string defaultClip)
    {
        CmbClip.SelectionChanged -= OnClipComboSelectionChanged;
        CmbClip.Items.Clear();
        foreach (var (name, kind, path) in _actorClips)
        {
            var label = kind == "model"
                ? $"🎬 {(string.IsNullOrEmpty(name) ? "(無名アニメ)" : name)}"
                : (string.IsNullOrEmpty(name) ? path : name);
            CmbClip.Items.Add(new ComboBoxItem { Content = label, Tag = new ClipComboTag(kind, path) });
        }
        CmbClip.SelectionChanged += OnClipComboSelectionChanged;

        if (_actorClips.Count == 0)
        {
            UpdateEmptyState();
            return;
        }

        NoSelectionVisible(false);
        // default_clip は AnimClipRef.name を指す識別子（path ではない）。Inspector 側の一致判定と揃える。
        var idx = _actorClips.FindIndex(c => c.Name == defaultClip);
        CmbClip.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void OnClipComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbClip.SelectedItem is not ComboBoxItem item || item.Tag is not ClipComboTag tag) return;
        StopPreview();

        if (tag.Kind == "model")
        {
            // モデル内蔵アニメは .anim を持たない（ANIM_PREVIEW / ドープシート編集の対象外）ため、
            // 実クリップの読み込みは行わず案内表示のみに切り替える。
            LoadModelClipPlaceholder();
            return;
        }

        _isModelClipSelected = false;
        var abs = VirtualPath.ToAbsolute(tag.Path, _assetsPath);
        LoadClipFromActorContext(abs);
    }

    private void LoadClipFromActorContext(string absolutePath)
    {
        try
        {
            _clip               = File.Exists(absolutePath) ? AnimClipIO.Load(absolutePath) : new AnimClip();
            _currentFilePath    = absolutePath;
            _isDirty            = false;
            _selectedTrackIndex = -1;
            _isModelClipSelected = false;
            _previewTime        = 0f;
            ResetUndo();
            RefreshAll();
        }
        catch (Exception ex)
        {
            EditorLog.Write($"AnimationTimelinePanel: クリップ読み込み失敗: {ex.Message}");
        }
    }

    /// <summary>
    /// kind=model のクリップが選択されたときの表示状態にする。編集対象の実クリップは存在しないため
    /// _clip を null にし、RefreshAll の「クリップ無し」経路（ドープシート非表示・保存/再生無効化）へ乗せる。
    /// ComboBox 自体（_actorClips）はクリアしない点が UpdateEmptyState と異なる。
    /// </summary>
    private void LoadModelClipPlaceholder()
    {
        _clip                = null;
        _currentFilePath     = null;
        _isDirty             = false;
        _selectedTrackIndex  = -1;
        _isModelClipSelected = true;
        RefreshAll();
    }

    // ── 空状態表示 ──────────────────────────────────────────────

    /// <summary>アクター未選択・Animator 無しのとき編集領域を空にする。</summary>
    private void UpdateEmptyState()
    {
        if (_actorClips.Count == 0 && !_isFileOnlyMode)
        {
            _clip            = null;
            _currentFilePath = null;
            _isDirty         = false;
            _isModelClipSelected = false;
            CmbClip.Items.Clear();
            NoSelectionVisible(true);
            RefreshAll();
        }
    }

    private void NoSelectionVisible(bool empty)
    {
        // Animator 未所持時は「直接開く」以外の操作を無効化する（クリップが存在しないため）。
        BtnSaveClip.IsEnabled = !empty;
        BtnPlayPause.IsEnabled = !empty && CanPreview();
        CmbClip.IsEnabled = !empty;
        BtnNewClip.IsEnabled = true; // 新規作成は常時可能（保存時にパスを指定する）
        TbNoClipHint.Text       = DefaultNoClipHint;
        TbNoClipHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DopeSheet.Visibility    = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── 新規 / 開く / 保存 ──────────────────────────────────────

    private void OnNewClip(object sender, RoutedEventArgs e)
    {
        StopPreview();
        _clip = new AnimClip
        {
            Name     = "new_clip",
            Duration = 1f,
            Fps      = AnimFrameMath.DefaultFps,
            LoopMode = AnimLoopMode.Once,
        };
        _currentFilePath = null;
        _isDirty = true;
        _selectedTrackIndex = -1;
        _isFileOnlyMode = true; // 保存先未確定のため、明示的に保存するまでファイル単独扱いにする
        _isModelClipSelected = false;
        _previewTime = 0f;
        ResetUndo();
        RefreshAll();
    }

    private void OnOpenClip(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title           = ".anim ファイルを開く",
            Filter          = "アニメーションクリップ|*.anim|すべてのファイル|*.*",
            InitialDirectory = Directory.Exists(_assetsPath) ? _assetsPath : "",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        LoadAnimFile(dlg.FileName);
    }

    private void OnSaveClip(object sender, RoutedEventArgs e)
    {
        if (_clip is null) return;

        var path = _currentFilePath;
        if (path is null)
        {
            var dlg = new SaveFileDialog
            {
                Title           = ".anim を保存",
                Filter          = "アニメーションクリップ|*.anim",
                InitialDirectory = Directory.Exists(_assetsPath) ? _assetsPath : "",
                FileName        = string.IsNullOrEmpty(_clip.Name) ? "new_clip.anim" : _clip.Name + ".anim",
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            path = dlg.FileName;
        }

        try
        {
            AnimClipIO.Save(_clip, path);
            _currentFilePath = path;
            _isDirty = false;
            UpdateTitle();

            // ランタイム側のクリップキャッシュを破棄させ、次回参照時に再読込させる
            // （ライブプレビューで差し替えたキャッシュも、ここで保存済みの .anim へ戻る）
            var virtualPath = VirtualPath.ToVirtual(path, _assetsPath);
            _runtime?.SendToRuntime($"ANIM_RELOAD:{virtualPath}");
        }
        catch (Exception ex)
        {
            EditorLog.Write($"AnimationTimelinePanel: .anim 保存失敗: {ex.Message}");
            MessageBox.Show(Window.GetWindow(this), $".anim の保存に失敗しました:\n{ex.Message}",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── duration / loop_mode 編集 ───────────────────────────────

    private void OnDurationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter) { CommitDuration(); e.Handled = true; }
    }
    private void OnDurationLostFocus(object sender, RoutedEventArgs e) => CommitDuration();

    private void CommitDuration()
    {
        if (_clip is null) return;
        var v = AnimClipIO.ParseFloatOr(TbDuration.Text, _clip.Duration);
        _clip.Duration = Math.Max(v, AnimationTimelineConstants.MinDuration);
        TbDuration.Text = _clip.Duration.ToString(CultureInfo.InvariantCulture);
        // duration が縮んだらプレイヘッドも収まる位置へ寄せる
        SetPlayheadFrame(AnimFrameMath.TimeToFrame(_previewTime, ClipFps), sendPreview: false);
        CommitEdit();
    }

    // ── fps 編集 ────────────────────────────────────────────────

    private void OnFpsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter) { CommitFps(); e.Handled = true; }
    }
    private void OnFpsLostFocus(object sender, RoutedEventArgs e) => CommitFps();

    /// <summary>
    /// fps 入力を確定する。
    ///
    /// fps はスナップの格子そのものなので、変更すると既存キーの「フレーム位置」が変わる。
    /// ただしキーの**秒**は動かさない（.anim の正はあくまで秒であり、
    /// fps 変更で既存アニメの見た目が変わってしまうのを避けるため）。
    /// </summary>
    private void CommitFps()
    {
        if (_clip is null) return;
        var v   = AnimClipIO.ParseFloatOr(TbFps.Text, _clip.Fps);
        var fps = AnimFrameMath.NormalizeFps(v);
        TbFps.Text = fps.ToString(CultureInfo.InvariantCulture);
        if (Math.Abs(fps - _clip.Fps) < float.Epsilon) return;

        _clip.Fps = fps;
        // 新しい格子へプレイヘッドを乗せ直す
        _previewTime = AnimFrameMath.ClampAndSnapTime(_previewTime, fps, _clip.Duration);
        DopeSheet.SetPlayheadTime(_previewTime);
        UpdateFrameBox();
        CommitEdit();
    }

    // ── フレーム送り ────────────────────────────────────────────

    /// <summary>編集中クリップのフレームレート（未ロード時は既定 fps）。</summary>
    private float ClipFps => AnimFrameMath.NormalizeFps(_clip?.Fps ?? AnimFrameMath.DefaultFps);

    private void OnFrameStart(object sender, RoutedEventArgs e) => SetPlayheadFrame(0);
    private void OnFrameEnd(object sender, RoutedEventArgs e)
        => SetPlayheadFrame(AnimFrameMath.LastFrame(ClipFps, _clip?.Duration ?? 0f));
    private void OnFramePrev(object sender, RoutedEventArgs e) => StepFrame(-AnimationTimelineConstants.FrameStepSmall);
    private void OnFrameNext(object sender, RoutedEventArgs e) => StepFrame(+AnimationTimelineConstants.FrameStepSmall);

    /// <summary>現在フレームから delta だけ進める（負で戻る）。</summary>
    private void StepFrame(int delta)
        => SetPlayheadFrame(AnimFrameMath.TimeToFrame(_previewTime, ClipFps) + delta);

    /// <summary>
    /// プレイヘッドを指定フレームへ移動し、必要ならプレビューを送り直す。
    /// 再生中に手で動かしたときは再生を止める（意図しない上書きを避けるため）。
    /// </summary>
    private void SetPlayheadFrame(int frame, bool sendPreview = true)
    {
        if (_clip is null) return;
        if (_isPlaying) StopPreviewPlaybackOnly();

        var fps     = ClipFps;
        var clamped = AnimFrameMath.ClampFrame(frame, fps, _clip.Duration);
        _previewTime = AnimFrameMath.FrameToTime(clamped, fps);
        DopeSheet.SetPlayheadTime(_previewTime);
        UpdateFrameBox();
        if (sendPreview) SendPreview(_previewTime);
    }

    /// <summary>フレーム番号ボックスの表示を現在位置に合わせる。</summary>
    private void UpdateFrameBox()
        => TbFrame.Text = AnimFrameMath.TimeToFrame(_previewTime, ClipFps)
                                       .ToString(CultureInfo.InvariantCulture);

    private void OnFrameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter) { CommitFrameBox(); e.Handled = true; }
    }
    private void OnFrameBoxLostFocus(object sender, RoutedEventArgs e) => CommitFrameBox();

    /// <summary>フレーム番号ボックスの入力を確定してプレイヘッドを移動する。</summary>
    private void CommitFrameBox()
    {
        if (_clip is null) return;
        if (!int.TryParse(TbFrame.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
        {
            UpdateFrameBox();   // 不正入力は現在値へ戻す
            return;
        }
        SetPlayheadFrame(frame);
    }

    private void OnLoopModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_clip is null) return;
        if (CmbLoopMode.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;
        if (_clip.LoopMode == mode) return;
        _clip.LoopMode = mode;
        CommitEdit();
    }

    // ── トラック追加/削除 ───────────────────────────────────────

    private void OnAddTrack(object sender, RoutedEventArgs e)
    {
        if (_clip is null) return;
        if (CmbNewTrackProperty.SelectedItem is not AnimPropertyEntry entry) return;

        var track = new AnimTrack
        {
            Target    = new AnimTarget { ActorPath = TbNewTrackActorPath.Text.Trim(), Component = entry.Component, Property = entry.Property },
            ValueType = entry.ValueType,
        };
        _clip.Tracks.Add(track);
        CommitEdit(refreshTracks: true);
    }

    /// <summary>
    /// トラックリストの Delete キー。ListBox 自身の KeyDown（バブルの最初の受け手）で
    /// 拾えなかった場合の保険として <see cref="HandleTimelineKey"/> からも同じ
    /// <see cref="DeleteTrackAt"/> を呼べるようにしてある（実体は共通・二重削除の心配はない。
    /// ここで e.Handled=true にすれば HandleTimelineKey 側の Delete 処理は走らない）。
    /// </summary>
    private void OnTrackListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        if (_selectedTrackIndex < 0) return; // サマリー行・未選択は削除対象なし
        DeleteTrackAt(_selectedTrackIndex);
        e.Handled = true;
    }

    /// <summary>
    /// 指定インデックスのトラックを削除する（Delete キー・行の削除ボタン・右クリックメニュー共通）。
    /// 削除後は選択を解除し、Undo 履歴に積む。
    /// </summary>
    private void DeleteTrackAt(int trackIndex)
    {
        if (_clip is null || trackIndex < 0 || trackIndex >= _clip.Tracks.Count) return;
        _clip.Tracks.RemoveAt(trackIndex);
        _selectedTrackIndex = -1;
        DopeSheet.SetSelectedTrackIndex(-1);
        // トラックが 1 本消えると後続トラックの添字が繰り上がるため、キー選択は捨てる。
        // （範囲チェックだけでは「別トラックのキーを選んだまま」になり、次の一括操作が誤爆する）
        DopeSheet.Selection.Clear();
        CommitEdit(refreshTracks: true);
        DopeSheet.NotifySelectionChangedExternally();
    }

    /// <summary>
    /// トラックリストの選択変更。先頭は常に「全チャンネル」サマリー行（リスト添字 0）なので、
    /// _selectedTrackIndex（トラック配列内の添字）は 1 引いた値になる。
    /// サマリー行・未選択はどちらも -1 として扱う（キー挿入の「全トラック対象」と同義にするため）。
    /// </summary>
    private void OnTrackListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedTrackIndex = LstTracks.SelectedIndex - 1;
        DopeSheet.SetSelectedTrackIndex(_selectedTrackIndex);
    }

    /// <summary>
    /// トラックの表示文字列（"actor_path : component.property [value_type]"）を作る。
    /// いま選んでいるキー対象アクタと一致するトラックには先頭に ● を付け、
    /// 「I キーで打たれるのはどれか」を一目で分かるようにする。
    /// </summary>
    private string DescribeTrack(AnimTrack t)
    {
        var actor  = string.IsNullOrEmpty(t.Target.ActorPath) ? "(Animator 自身)" : t.Target.ActorPath;
        var isTarget = t.Target.ActorPath == _keyTargetActorPath;
        var mark   = isTarget ? "● " : "   ";
        return $"{mark}{actor} : {t.Target.Component}.{t.Target.Property}  [{t.ValueType}]";
    }

    /// <summary>
    /// サマリー行（「全チャンネル」）の行 UI を作る。削除不可・常に先頭（リスト添字 0）。
    /// </summary>
    private static UIElement BuildSummaryRowElement() => new TextBlock
    {
        Text       = AnimationTimelineConstants.SummaryRowLabel,
        FontWeight = FontWeights.Bold,
        FontSize   = 11,
        Foreground = new SolidColorBrush(AnimationTimelineConstants.PlayheadColor),
        Padding    = new Thickness(4, 3, 4, 3),
        ToolTip    = "いずれかのトラックにキーがあるフレームを表示する集計行。\n" +
                     "選択中はキー挿入・削除・移動が対象トラック全部へ一括適用される。",
    };

    /// <summary>
    /// 1 トラック行の UI（ラベル + 削除ボタン）を作る。右クリックメニューからも削除できる。
    /// index はビルド時点でのトラック配列の添字を closure で捕まえる（行 UI は毎回作り直すため、
    /// リストの増減があってもズレない）。
    /// </summary>
    private UIElement BuildTrackRowElement(AnimTrack track, int index)
    {
        var row = new DockPanel { LastChildFill = true };

        var deleteButton = new Button
        {
            Width = 16, Height = 16, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "このトラックを削除",
            Content = new SEEDEditor.Controls.AppIcon { IconKey = "Icon.Delete", Width = 10, Height = 10 },
        };
        deleteButton.Click += (_, _) => DeleteTrackAt(index);
        DockPanel.SetDock(deleteButton, Dock.Right);
        row.Children.Add(deleteButton);

        var label = new TextBlock
        {
            Text = DescribeTrack(track), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        row.Children.Add(label);

        var menu = new ContextMenu { Background = new SolidColorBrush(AnimationTimelineConstants.ToolbarBackground) };
        var deleteItem = new MenuItem
        {
            Header     = "トラックを削除",
            Foreground = new SolidColorBrush(AnimationTimelineConstants.TextColor),
        };
        deleteItem.Click += (_, _) => DeleteTrackAt(index);
        menu.Items.Add(deleteItem);
        row.ContextMenu = menu;

        return row;
    }

    private void RefreshTrackList()
    {
        LstTracks.SelectionChanged -= OnTrackListSelectionChanged;
        LstTracks.Items.Clear();

        if (_clip is not null)
        {
            // サマリー行（「全チャンネル」）は常に先頭・削除不可。クリップが無いときは
            // トラックリスト自体を空のままにする（従来通り、空状態表示に委ねる）。
            LstTracks.Items.Add(BuildSummaryRowElement());
            for (int i = 0; i < _clip.Tracks.Count; i++)
                LstTracks.Items.Add(BuildTrackRowElement(_clip.Tracks[i], i));

            // リスト添字 = トラック添字 + 1（サマリー行の分）。_selectedTrackIndex < 0 はサマリー行選択。
            var listIndex = _selectedTrackIndex < 0 ? 0 : _selectedTrackIndex + 1;
            if (listIndex < LstTracks.Items.Count) LstTracks.SelectedIndex = listIndex;
        }

        LstTracks.SelectionChanged += OnTrackListSelectionChanged;
    }

    // ── ドープシートからのキー編集イベント ──────────────────────

    private void OnDopeSheetKeyAddRequested(int trackIndex, float time)
    {
        if (_clip is null || trackIndex < 0 || trackIndex >= _clip.Tracks.Count) return;
        var track = _clip.Tracks[trackIndex];

        // アクタの現在値が取れるならそれを使う（ダブルクリックでも「いまの見た目」がキーになる）。
        // 取れない場合だけ、従来どおり直前のキーの値を複製する。
        var values = CurrentValuesForTrack(track) ?? AnimKeyEditor.PreviousOrDefaultValues(track, time);

        AnimKeyEditor.InsertOrUpdate(track, time, values, ClipFps);
        CommitEdit();
    }

    /// <summary>
    /// ◆ドラッグ中の選択キー移動。ドラッグ中は連続発火するため、ここでは
    /// ダーティ化・値エディタ更新・ライブプレビューだけを行い、Undo 履歴は積まない
    /// （履歴は <see cref="OnDopeSheetKeyDragEnded"/> で 1 段だけ積む）。
    /// モデルの書き換えと選択の張り直しは DopeSheetPanel 側
    /// （AnimKeyEditor.MoveSelectedKeys）が済ませている。
    /// </summary>
    private void OnDopeSheetSelectionMoved()
    {
        if (_clip is null) return;
        MarkDirty();
        RefreshValueEditorFromSelection();
        PushClipToRuntimeAndPreview();
    }

    /// <summary>◆ドラッグが終わった時点で Undo 履歴を 1 段だけ積む。</summary>
    private void OnDopeSheetKeyDragEnded() => PushUndoSnapshot();

    /// <summary>キー選択が変わったら値エディタを作り直す（0 個 / 1 個 / 複数で内容が変わる）。</summary>
    private void OnDopeSheetSelectionChanged() => RefreshValueEditorFromSelection();

    /// <summary>
    /// ドープシートの縦スクロールへトラックリストを追従させる。
    /// ListBox は既定でアイテム単位スクロールのため、ピクセル量を行数へ換算して渡す。
    /// </summary>
    private void OnDopeSheetVerticalScrollChanged(double scrollY)
    {
        if (FindDescendantScrollViewer(LstTracks) is { } sv)
            sv.ScrollToVerticalOffset(scrollY / AnimationTimelineConstants.TrackRowHeight);
    }

    /// <summary>コントロール配下の最初の ScrollViewer を探す（ListBox の内部スクロールを掴むため）。</summary>
    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindDescendantScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    /// <summary>値エディタの「削除」ボタン用: 単一キーを削除する（従来どおりの単体削除）。</summary>
    private void DeleteKeyAt(int trackIndex, int keyIndex)
    {
        if (_clip is null || trackIndex < 0 || trackIndex >= _clip.Tracks.Count) return;
        var track = _clip.Tracks[trackIndex];
        if (keyIndex < 0 || keyIndex >= track.Keys.Count) return;

        track.Keys.RemoveAt(keyIndex);
        DopeSheet.Selection.NormalizeAfterDelete(new[] { new AnimKeyRef(trackIndex, keyIndex) });
        CommitEdit();
        DopeSheet.NotifySelectionChangedExternally();
    }

    // ── ドープシートからのサマリー行イベント（「全チャンネル」）────
    //
    // サマリー◆の選択・移動・削除は通常の複数選択へ統合した（◆クリックで
    // そのフレームのキーが全トラックぶん選択され、以降はドラッグ移動・Delete・
    // コピー等がすべて同じ経路で効く）。ここに残るのは
    // 「サマリー行のダブルクリックによる一括挿入」だけ。

    /// <summary>サマリー行のダブルクリック: キー対象アクタに一致する全トラックへ一括挿入する。</summary>
    private void OnDopeSheetSummaryKeyAddRequested(float time)
    {
        if (_clip is null) return;
        if (!EnsureKeyTargetSnapshot(() => OnDopeSheetSummaryKeyAddRequested(time))) return;

        AnimKeyEditor.InsertOnAllTracks(
            _clip.Tracks, _keyTargetActorPath, time,
            track => CurrentValuesForTrack(track) ?? AnimKeyEditor.PreviousOrDefaultValues(track, time),
            ClipFps);
        CommitEdit();
    }

    // ── 選択キーへの一括操作（移動 / 削除 / コピー / 貼り付け / 複製）──
    //
    // ロジック本体は純ロジック側（AnimKeyEditor / AnimKeyClipboard）に置き、
    // ここでは「操作 → CommitEdit（ダーティ化 + Undo 1 段 + 再描画 + ライブプレビュー）
    // → 選択の反映」という接着だけを行う。どの操作も 1 回の CommitEdit で
    // Undo 1 段に対応する（コピーだけはモデルを変えないので履歴を積まない）。

    /// <summary>
    /// アプリ内キークリップボード（JSON）。
    /// 静的にしているのは、別のクリップを開いても内容を保つため。
    /// システムクリップボード（テキスト）にも同じ JSON を載せ、読むときはそちらを優先する。
    /// </summary>
    private static string? _keyClipboardJson;

    /// <summary>選択キーを ±delta フレーム動かす（←→ キー）。1 回の押下で Undo 1 段。</summary>
    private void NudgeSelection(int frameDelta)
    {
        if (_clip is null) return;
        var moved = AnimKeyEditor.MoveSelectedKeys(
            _clip.Tracks, DopeSheet.Selection, frameDelta, ClipFps, _clip.Duration);
        if (moved == 0) return;

        CommitEdit();
        DopeSheet.NotifySelectionChangedExternally();
    }

    /// <summary>選択キーをすべて削除する（Delete / BackSpace / ◆右クリックメニュー）。</summary>
    private void DeleteSelectedKeys()
    {
        if (_clip is null || DopeSheet.Selection.IsEmpty) return;
        if (AnimKeyEditor.DeleteSelectedKeys(_clip.Tracks, DopeSheet.Selection) == 0) return;

        CommitEdit();
        DopeSheet.NotifySelectionChangedExternally();
    }

    /// <summary>クリップ内の全キーを選択する（Ctrl+A）。</summary>
    private void SelectAllKeys()
    {
        if (_clip is null) return;
        DopeSheet.Selection.SelectAll(_clip.Tracks);
        DopeSheet.NotifySelectionChangedExternally();
    }

    /// <summary>キー選択を解除する（Esc）。</summary>
    private void ClearKeySelection()
    {
        DopeSheet.Selection.Clear();
        DopeSheet.NotifySelectionChangedExternally();
    }

    /// <summary>選択キーをクリップボードへコピーする（Ctrl+C）。モデルは変えないので Undo は積まない。</summary>
    private void CopySelectedKeys()
    {
        if (_clip is null || DopeSheet.Selection.IsEmpty) return;

        var count = DopeSheet.Selection.Count;
        var json  = AnimKeyClipboard.Serialize(
            AnimKeyClipboard.Copy(_clip.Tracks, DopeSheet.Selection, ClipFps));
        _keyClipboardJson = json;
        TrySetSystemClipboard(json);
        TbTitleStatus.Text = string.Format(CultureInfo.InvariantCulture,
            AnimationTimelineConstants.CopiedKeysStatusFormat, count);
    }

    /// <summary>選択キーを切り取る（Ctrl+X）。コピー後に削除するので Undo は削除ぶんの 1 段。</summary>
    private void CutSelectedKeys()
    {
        if (_clip is null || DopeSheet.Selection.IsEmpty) return;
        CopySelectedKeys();
        DeleteSelectedKeys();
    }

    /// <summary>
    /// クリップボードのキーをプレイヘッド位置へ貼り付ける（Ctrl+V）。
    /// 対象トラックが無ければ作成されるため、トラックリストも作り直す。
    /// </summary>
    private void PasteKeys()
    {
        if (_clip is null) return;

        var data = AnimKeyClipboard.Parse(TryGetSystemClipboard()) ?? AnimKeyClipboard.Parse(_keyClipboardJson);
        if (data is null || data.IsEmpty)
        {
            TbTitleStatus.Text = AnimationTimelineConstants.NothingToPasteStatus;
            return;
        }

        PasteClipboardData(data);
    }

    /// <summary>
    /// 選択キーを複製してプレイヘッド位置へ貼り付ける（Shift+D）。
    /// クリップボードは汚さない（コピー中の内容を失わせないため、複製専用の一時データを使う）。
    /// </summary>
    private void DuplicateSelectedKeys()
    {
        if (_clip is null || DopeSheet.Selection.IsEmpty) return;
        PasteClipboardData(AnimKeyClipboard.Copy(_clip.Tracks, DopeSheet.Selection, ClipFps));
    }

    /// <summary>クリップボード内容をプレイヘッド位置へ貼り付け、貼り付けたキーを選択し直す共通処理。</summary>
    private void PasteClipboardData(AnimClipboardData data)
    {
        if (_clip is null) return;

        var pasted = AnimKeyClipboard.Paste(_clip, data, DopeSheet.PlayheadFrame);
        if (pasted.Count == 0) return;

        DopeSheet.Selection.SetFromKeys(_clip.Tracks, pasted);
        CommitEdit(refreshTracks: true);      // トラックが増えている可能性があるため作り直す
        DopeSheet.NotifySelectionChangedExternally();
    }

    /// <summary>システムクリップボードへ書く（他プロセスが握っている等で失敗しても編集は続行する）。</summary>
    private static void TrySetSystemClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) { EditorLog.Write($"AnimationTimelinePanel: クリップボード書き込み失敗: {ex.Message}"); }
    }

    /// <summary>システムクリップボードのテキストを読む（読めなければ null）。</summary>
    private static string? TryGetSystemClipboard()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (Exception ex)
        {
            EditorLog.Write($"AnimationTimelinePanel: クリップボード読み取り失敗: {ex.Message}");
            return null;
        }
    }

    // ── キーボード操作 ──────────────────────────────────────────

    /// <summary>
    /// パネル自身がフォーカスを持っているときのキー操作。
    ///
    /// ・Delete / BackSpace        : 選択キーを削除（選択が無ければ選択トラックを削除）
    /// ・← / →                    : 選択キーを 1 フレーム移動（選択が無ければプレイヘッド送り。Shift で 10）
    /// ・Home / End                : 先頭 / 最終フレームへ
    /// ・F                         : クリップ全体を画面幅に収める
    /// ・Esc                       : 選択解除
    /// ・I                         : プレイヘッド位置へ現在値でキー挿入
    /// ・U                         : 選択キーを現在値で上書き
    /// ・Shift+D                   : 選択キーをプレイヘッド位置へ複製
    /// ・Ctrl+A / C / X / V        : 全選択 / コピー / 切り取り / 貼り付け
    /// ・Ctrl+Z / Ctrl+Shift+Z / Ctrl+Y : クリップ編集の Undo / Redo
    ///
    /// テキスト入力中（fps・長さ・フレーム番号ボックス）は素通しする。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || IsTextInputFocused()) return;
        if (HandleTimelineKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>
    /// タイムラインのキー操作を実行する。処理したら true。
    /// パネル内フォーカスとウィンドウレベルのフック（<see cref="OnWindowPreviewKeyDown"/>）の
    /// 双方から呼ばれるため、判定と実行をここへ 1 本化している。
    /// </summary>
    private bool HandleTimelineKey(Key key, ModifierKeys modifiers)
    {
        var ctrl  = (modifiers & ModifierKeys.Control) != 0;
        var shift = (modifiers & ModifierKeys.Shift)   != 0;

        if (ctrl)
        {
            switch (key)
            {
                case Key.Z when shift: RedoClipEdit(); return true;   // Ctrl+Shift+Z も Redo（一般的な別名）
                case Key.Z:            UndoClipEdit(); return true;
                case Key.Y:            RedoClipEdit(); return true;
                case Key.A:            SelectAllKeys();     return true;
                case Key.C:            CopySelectedKeys();  return true;
                case Key.X:            CutSelectedKeys();   return true;
                case Key.V:            PasteKeys();         return true;
                default:               return false;   // Ctrl+S 等はエディタ本体へ渡す
            }
        }

        var step = shift
            ? AnimationTimelineConstants.FrameStepLarge
            : AnimationTimelineConstants.FrameStepSmall;

        // キー選択があるかどうかで ←→ / Delete の対象が変わる
        var hasKeySelection = !DopeSheet.Selection.IsEmpty;

        switch (key)
        {
            case Key.Delete:
            case Key.Back:
            {
                // 1) キーが選択中なら選択キーを全部削除
                if (hasKeySelection) { DeleteSelectedKeys(); return true; }

                // 2) トラックリストでトラックそのものが選択中ならトラックを削除
                //    （ListBox 自身の KeyDown で先に処理されるのが通常経路。
                //     フォーカスの都合でここまで来た場合の保険として同じ処理を呼ぶ）
                if (_selectedTrackIndex >= 0) { DeleteTrackAt(_selectedTrackIndex); return true; }

                return false;
            }
            // キー選択中は「選択キーの移動」、非選択時は従来どおり「プレイヘッド送り」。
            // 1 つのキーに 2 つの意味を持たせるのは、ドープシートの標準的な操作感
            // （選択があるならそれを動かす）に合わせるため。
            case Key.Left:  if (hasKeySelection) NudgeSelection(-step); else StepFrame(-step); return true;
            case Key.Right: if (hasKeySelection) NudgeSelection(+step); else StepFrame(+step); return true;
            case Key.Home:  SetPlayheadFrame(0); return true;
            case Key.End:   SetPlayheadFrame(AnimFrameMath.LastFrame(ClipFps, _clip?.Duration ?? 0f)); return true;
            case Key.F:     DopeSheet.FrameAll(); return true;
            case Key.Escape: ClearKeySelection(); return true;
            case Key.D when shift: DuplicateSelectedKeys(); return true;
            case Key.I:     InsertKeyAtPlayhead(); return true;
            case Key.U:     OverwriteSelectedKey(); return true;
            default:        return false;
        }
    }

    /// <summary>いまテキスト入力欄にフォーカスがあるか（あればホットキーを横取りしない）。</summary>
    private static bool IsTextInputFocused()
        => Keyboard.FocusedElement is TextBox or System.Windows.Documents.TextElement;

    // ── ウィンドウレベルのホットキー（ビューポート操作中でも I / U を効かせる）──

    /// <summary>
    /// 所属ウィンドウの PreviewKeyDown を購読する。
    ///
    /// 埋め込み Edit モードではキーボードフォーカスがエディタ（WPF）側にあるため、
    /// ビューポートを触っている最中でもここでキーを拾える。
    /// 他パネルの入力を奪わないよう、発火条件は
    /// 「本パネルが表示中」「編集対象クリップがある」「テキスト入力中でない」
    /// 「I / U のみ」に絞る（フレーム送りや Delete はパネルフォーカス時だけ）。
    /// </summary>
    /// <summary>
    /// マウスダウン位置が「自分でキーボードフォーカスを扱う入力部品」の内側かを判定する。
    /// ComboBox（ドロップダウンの Popup 内の項目を含む）・TextBox・ListBox・Button が対象。
    /// ビジュアルツリーを親方向へたどり、途中で該当部品に当たれば true。
    /// </summary>
    /// <param name="source">ルーティングイベントの OriginalSource。</param>
    private static bool IsInsideFocusableInput(DependencyObject? source)
    {
        var node = source;
        while (node is not null)
        {
            if (node is ComboBox || node is ComboBoxItem || node is TextBoxBase
                || node is ListBox || node is ListBoxItem || node is ButtonBase
                || node is System.Windows.Controls.Primitives.Popup)
            {
                return true;
            }
            // Popup の中身は VisualTreeHelper では親へ辿れないため、論理親も併用する
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } w)
        {
            w.PreviewKeyDown -= OnWindowPreviewKeyDown;
            w.PreviewKeyDown += OnWindowPreviewKeyDown;
        }
    }

    private void OnPanelUnloaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } w) w.PreviewKeyDown -= OnWindowPreviewKeyDown;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (!IsVisible || _clip is null) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;   // Ctrl/Alt/Shift 付きは対象外
        if (e.Key is not (Key.I or Key.U)) return;
        if (IsKeyboardFocusWithin) return;                     // パネル内は OnKeyDown が処理する
        if (!IsViewportFocused()) return;                      // ビューポート操作中だけを対象にする

        if (HandleTimelineKey(e.Key, ModifierKeys.None)) e.Handled = true;
    }

    /// <summary>
    /// いまキーボードフォーカスがビューポート（ランタイムウィンドウのホスト）側にあるか。
    ///
    /// スクリプトエディタ（AvalonEdit）やインスペクタの入力欄で "i" / "u" を打っただけで
    /// キーが横取りされると実害が大きいため、ホワイトリスト方式にしている。
    /// フォーカスが誰にも無い（＝クリック直後にランタイムの子ウィンドウへ渡った）場合も
    /// ビューポート扱いにする。
    /// </summary>
    private static bool IsViewportFocused()
    {
        // ContentElement（FlowDocument 内など）はビジュアルツリーを辿れないので対象外とする。
        if (Keyboard.FocusedElement is not Visual focused) return Keyboard.FocusedElement is null;

        for (DependencyObject? d = focused; d is Visual; d = VisualTreeHelper.GetParent(d))
            if (d is SEEDEditor.Viewport.ViewportHost) return true;
        return false;
    }

    // ── キー挿入 / 現在値での上書き ─────────────────────────────

    private void OnInsertKey(object sender, RoutedEventArgs e)  => InsertKeyAtPlayhead();
    private void OnOverwriteKey(object sender, RoutedEventArgs e) => OverwriteSelectedKey();

    /// <summary>
    /// プレイヘッド位置に、選択アクタの**現在値**でキーを打つ。
    ///
    /// 対象トラックの決め方:
    ///  1. トラックリストで 1 本選択中なら、そのトラックだけ。
    ///  2. 未選択（＝サマリー行「全チャンネル」選択時と同義）なら、
    ///     キー対象アクタ（actor_path 一致）のトラック全部へ挿入する
    ///     （<see cref="AnimKeyEditor.InsertOnAllTracks"/> に委譲。1 本も一致しなければ全トラックへ挿入する）。
    ///  3. クリップにトラックが 1 本も無ければトラック生成を提案する（<see cref="OfferCreateTracks"/>）。
    ///
    /// 同じフレームに既存キーがあれば値を上書きする（AnimKeyEditor が保証）。
    /// キー対象アクタの現在値スナップショットがまだ届いていない場合は、
    /// <see cref="EnsureKeyTargetSnapshot"/> が GET_ACTOR_COMPONENTS で取りに行き、
    /// 届いた時点でこのメソッド自身を再実行する（今回の呼び出しはここで中断する）。
    /// </summary>
    private void InsertKeyAtPlayhead()
    {
        if (_clip is null) return;
        if (!EnsureKeyTargetSnapshot(InsertKeyAtPlayhead)) return;

        var fps  = ClipFps;
        var time = AnimFrameMath.ClampAndSnapTime(_previewTime, fps, _clip.Duration);

        // 1) トラックリストで 1 本だけ選択中 → そのトラックだけに打つ
        if (_selectedTrackIndex >= 0 && _selectedTrackIndex < _clip.Tracks.Count)
        {
            var track  = _clip.Tracks[_selectedTrackIndex];
            var values = CurrentValuesForTrack(track) ?? AnimKeyEditor.PreviousOrDefaultValues(track, time);
            AnimKeyEditor.InsertOrUpdate(track, time, values, fps);
            CommitEdit(refreshTracks: true);
            return;
        }

        // 2) クリップにトラックが 1 本も無い → 作成を提案する（実アクタが初めて動く場合）
        if (_clip.Tracks.Count == 0)
        {
            var created = OfferCreateTracks();
            if (created.Count == 0) return;
            foreach (var track in created)
            {
                var values = CurrentValuesForTrack(track) ?? AnimKeyEditor.PreviousOrDefaultValues(track, time);
                AnimKeyEditor.InsertOrUpdate(track, time, values, fps);
            }
            CommitEdit(refreshTracks: true);
            return;
        }

        // 3) 未選択（サマリー行と同義）→ キー対象アクタに一致する全トラックへ（無ければ全トラックへ）
        AnimKeyEditor.InsertOnAllTracks(
            _clip.Tracks, _keyTargetActorPath, time,
            track => CurrentValuesForTrack(track) ?? AnimKeyEditor.PreviousOrDefaultValues(track, time),
            fps);
        CommitEdit(refreshTracks: true);
    }

    /// <summary>
    /// キー対象アクタの現在値スナップショットが使える状態か確認する。
    ///
    /// 【なぜ要るか】
    /// 選択直後などスナップショットがまだ届いていないタイミングで I / U を押すと、
    /// 従来は「取れない現在値」を黙って直前キーの値へフォールバックしてしまい、
    /// 見た目上キーは増えるのに値が反映されない（キー挿入で値が書き込まれない）不具合の
    /// 一因になっていた。ここで明示的に GET_ACTOR_COMPONENTS を送って応答を待ち、
    /// 届いてから呼び出し元をもう一度実行することで、必ず本物の現在値でキーを打つ。
    /// </summary>
    /// <param name="retry">スナップショットが届いた後にもう一度実行する処理（呼び出し元自身）。</param>
    /// <returns>
    /// スナップショットが既に対象アクタのものであれば true（そのまま処理を続けてよい）。
    /// false のときは今回の呼び出しを中断済み。<paramref name="retry"/> は応答到着時または
    /// タイムアウト時に破棄される（タイムアウト時は再実行されない。TbTitleStatus に理由を出す）。
    /// </returns>
    private bool EnsureKeyTargetSnapshot(Action retry)
    {
        if (_keyTargetDfsId < 0) return true; // 文脈なし。既存のエラー表示に処理を委ねる
        if (_keyTargetSnapshot.ActorDfsId == _keyTargetDfsId && !_keyTargetSnapshot.IsEmpty)
            return true; // 既に対象アクタの現在値が揃っている

        _pendingSnapshotDfsId    = _keyTargetDfsId;
        _pendingSnapshotCallback = retry;
        _runtime?.SendToRuntime($"GET_ACTOR_COMPONENTS:{_keyTargetDfsId}");
        StartPendingSnapshotTimeout();
        TbTitleStatus.Text = "現在値を取得中…";
        return false;
    }

    /// <summary>スナップショット問い合わせのタイムアウト監視を（作り直して）開始する。</summary>
    private void StartPendingSnapshotTimeout()
    {
        _pendingSnapshotTimer?.Stop();
        _pendingSnapshotTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(AnimationTimelineConstants.SnapshotFetchTimeoutSeconds),
        };
        _pendingSnapshotTimer.Tick += (_, _) =>
        {
            ClearPendingSnapshotRequest();
            TbTitleStatus.Text = "現在値の取得がタイムアウトしました（対象アクタを選択し直してください）";
        };
        _pendingSnapshotTimer.Start();
    }

    /// <summary>保留中のスナップショット問い合わせを破棄する（応答到着・タイムアウト・選択変更時に呼ぶ）。</summary>
    private void ClearPendingSnapshotRequest()
    {
        _pendingSnapshotTimer?.Stop();
        _pendingSnapshotTimer    = null;
        _pendingSnapshotCallback = null;
        _pendingSnapshotDfsId    = -1;
    }

    /// <summary>ドープシートで選択中のキーを、選択アクタの現在値で上書きする（U）。</summary>
    private void OverwriteSelectedKey()
    {
        if (_clip is null) return;
        if (!EnsureKeyTargetSnapshot(OverwriteSelectedKey)) return;
        if (DopeSheet.Selection.IsEmpty) return;

        // 選択キーすべてを、それぞれのトラックに対応する現在値で上書きする
        // （複数選択に合わせた拡張。現在値が取れないトラックのキーは触らない）。
        var applied = 0;
        foreach (var r in DopeSheet.Selection.Ordered())
        {
            if (r.TrackIndex < 0 || r.TrackIndex >= _clip.Tracks.Count) continue;
            var track = _clip.Tracks[r.TrackIndex];
            if (r.KeyIndex < 0 || r.KeyIndex >= track.Keys.Count) continue;

            var values = CurrentValuesForTrack(track);
            if (values is null) continue;

            track.Keys[r.KeyIndex].Values = AnimKeyEditor.FitValues(values, track.ValueType);
            applied++;
        }

        if (applied == 0)
        {
            TbTitleStatus.Text = "現在値が取得できません（対象アクタを選択してください）";
            return;
        }

        CommitEdit();
    }

    /// <summary>
    /// トラックの (component, property) に対応する、キー対象アクタの現在値を返す。
    /// トラックの actor_path がキー対象と違う／値が取れない場合は null。
    /// </summary>
    private float[]? CurrentValuesForTrack(AnimTrack track)
    {
        if (track.Target.ActorPath != _keyTargetActorPath) return null;
        return _keyTargetSnapshot.TryGet(track.Target.Component, track.Target.Property);
    }

    /// <summary>
    /// 打てるトラックが 1 本も無いとき、選択アクタの種別に合わせたトラック生成を提案する。
    /// 「はい」= 位置・回転・スケールの 3 本（全変換キー）、「いいえ」= 位置だけ。
    /// </summary>
    /// <returns>生成したトラック（キャンセル時は空リスト）。</returns>
    private List<AnimTrack> OfferCreateTracks()
    {
        var created = new List<AnimTrack>();
        if (_clip is null) return created;

        // 2D（CanvasTransform）か 3D（Transform）かは現在値スナップショットで判別する
        var component = _keyTargetSnapshot.Has(AnimActorSnapshot.CanvasTransformComponent,
                                               AnimActorSnapshot.PositionProperty)
            ? AnimActorSnapshot.CanvasTransformComponent
            : AnimActorSnapshot.TransformComponent;

        if (!_keyTargetSnapshot.Has(component, AnimActorSnapshot.PositionProperty))
        {
            TbTitleStatus.Text = "現在値が取得できません（対象アクタを選択してください）";
            return created;
        }

        var targetLabel = _keyTargetActorPath.Length == 0 ? "(Animator 自身)" : _keyTargetActorPath;
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"「{targetLabel}」に対応するトラックがありません。\n\n" +
            "［はい］  位置・回転・スケールの 3 本を作る（全変換キー）\n" +
            "［いいえ］位置のトラックだけ作る\n" +
            "［キャンセル］何もしない",
            "トラックの作成", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Cancel) return created;

        var properties = answer == MessageBoxResult.Yes
            ? AnimActorSnapshot.TransformProperties
            : new[] { AnimActorSnapshot.PositionProperty };

        foreach (var property in properties)
        {
            var valueType = AnimPropertyRegistry.ResolveValueType(component, property);
            if (valueType is null) continue;                       // レジストリ未登録の組は作らない
            if (!_keyTargetSnapshot.Has(component, property)) continue;

            var track = new AnimTrack
            {
                Target    = new AnimTarget
                {
                    ActorPath = _keyTargetActorPath,
                    Component = component,
                    Property  = property,
                },
                ValueType = valueType,
            };
            _clip.Tracks.Add(track);
            created.Add(track);
        }
        return created;
    }

    // ── Undo / Redo（パネル内・クリップ JSON スナップショット）──

    /// <summary>
    /// いまのクリップと選択からスナップショットを作る。
    /// 選択を含めるのは「Delete を Undo したら消えたキーが選び直された状態で戻る」ようにするため。
    /// </summary>
    private AnimUndoSnapshot CurrentSnapshot()
        => new(_clip is null ? "" : AnimClipIO.Serialize(_clip), DopeSheet.Selection.Serialize());

    /// <summary>クリップを差し替えたときに履歴を張り直す。</summary>
    private void ResetUndo() => _undo.Reset(CurrentSnapshot());

    /// <summary>編集後のスナップショットを履歴へ積む。</summary>
    private void PushUndoSnapshot()
    {
        if (_isRestoringUndo || _clip is null) return;
        _undo.Push(CurrentSnapshot());
    }

    private void UndoClipEdit() => RestoreSnapshot(_undo.Undo());
    private void RedoClipEdit() => RestoreSnapshot(_undo.Redo());

    /// <summary>
    /// スナップショットからクリップと選択を復元して UI を作り直す。
    /// 復元後は必ずライブプレビューを送り直し、ビューポートの見た目を戻した状態へ揃える。
    /// </summary>
    private void RestoreSnapshot(AnimUndoSnapshot? snapshot)
    {
        if (snapshot is not { } snap || snap.IsEmpty) return;
        try
        {
            _isRestoringUndo = true;
            _clip = AnimClipIO.Parse(snap.ClipJson);
            _selectedTrackIndex = -1;
            MarkDirty();
            RefreshAll();                                   // ここで DopeSheet.SetClip が選択を捨てるので
            DopeSheet.Selection.Restore(snap.SelectionJson); // 復元はその後に行う
            DopeSheet.NotifySelectionChangedExternally();
            PushClipToRuntimeAndPreview();
        }
        catch (Exception ex)
        {
            EditorLog.Write($"AnimationTimelinePanel: Undo 復元失敗: {ex.Message}");
        }
        finally
        {
            _isRestoringUndo = false;
        }
    }

    // ── 編集の共通後処理 ────────────────────────────────────────

    /// <summary>
    /// クリップを変更した直後に必ず通す後処理。
    /// ダーティ化 → Undo 履歴 → 再描画 → ライブプレビュー送信を 1 本にまとめる
    /// （どれか 1 つを呼び忘れる事故を構造的に防ぐ）。
    /// </summary>
    /// <param name="refreshTracks">トラックの増減があった場合は true（左のリストを作り直す）。</param>
    private void CommitEdit(bool refreshTracks = false)
    {
        MarkDirty();
        PushUndoSnapshot();
        if (refreshTracks) RefreshTrackList();
        DopeSheet.NotifyClipChanged();
        RefreshValueEditorFromSelection();
        PushClipToRuntimeAndPreview();
    }

    // ── 値エディタ ──────────────────────────────────────────────

    private void ClearValueEditor() => ValueEditorHost.Children.Clear();

    /// <summary>
    /// いまの選択に合わせて値エディタを作り直す。
    /// 0 個 = 空、1 個 = 従来どおりの詳細編集、複数 = 件数表示 + 補間の一括変更。
    /// </summary>
    private void RefreshValueEditorFromSelection()
    {
        var selection = DopeSheet.Selection;
        if (_clip is null || selection.IsEmpty) { ClearValueEditor(); return; }

        if (selection.IsMultiple) { BuildMultiSelectionEditor(selection); return; }

        var only = selection.Ordered()[0];
        RefreshValueEditor(only.TrackIndex, only.KeyIndex);
    }

    /// <summary>
    /// 複数選択時の値エディタ（件数表示・補間の一括変更・一括削除）。
    /// 値そのものはトラックごとに型が違いうるため、ここでは編集させない。
    /// </summary>
    private void BuildMultiSelectionEditor(AnimKeySelection selection)
    {
        ValueEditorHost.Children.Clear();

        ValueEditorHost.Children.Add(new TextBlock
        {
            Text = string.Format(CultureInfo.InvariantCulture,
                                 AnimationTimelineConstants.MultiSelectionLabelFormat, selection.Count),
            Foreground = new SolidColorBrush(AnimationTimelineConstants.PlayheadColor),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0),
        });

        ValueEditorHost.Children.Add(new TextBlock
        {
            Text = "補間", Foreground = new SolidColorBrush(AnimationTimelineConstants.SubTextColor),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0),
        });

        var cmbInterp = new ComboBox { Width = 80, FontSize = 11, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        foreach (var interp in AnimInterp.All)
            cmbInterp.Items.Add(new ComboBoxItem { Content = interp, Tag = interp });
        // 選択キーの補間が全て同じときだけ現在値を出す（バラバラなら未選択表示のままにする）
        cmbInterp.SelectedIndex = Array.IndexOf(AnimInterp.All, CommonInterpOf(selection) ?? "");
        // 初期表示のための SelectedIndex 設定で発火させないよう、ハンドラは後から付ける
        cmbInterp.SelectionChanged += (_, _) =>
        {
            if (_clip is null || cmbInterp.SelectedItem is not ComboBoxItem item || item.Tag is not string interp) return;
            if (AnimKeyEditor.SetInterpForSelection(_clip.Tracks, selection, interp) == 0) return;
            CommitEdit();
        };
        ValueEditorHost.Children.Add(cmbInterp);

        var btnDelete = new Button
        {
            Content = "選択キーを削除", Background = new SolidColorBrush(Color.FromRgb(0x40, 0x28, 0x28)),
            Foreground = new SolidColorBrush(AnimationTimelineConstants.TextColor), BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 8, 2), FontSize = 11, Margin = new Thickness(12, 0, 0, 0), Cursor = Cursors.Hand,
        };
        btnDelete.Click += (_, _) => DeleteSelectedKeys();
        ValueEditorHost.Children.Add(btnDelete);
    }

    /// <summary>選択キーの補間方式が全て同じならその値、違うものが混ざっていれば null。</summary>
    private string? CommonInterpOf(AnimKeySelection selection)
    {
        if (_clip is null) return null;
        string? common = null;
        foreach (var r in selection.Ordered())
        {
            if (r.TrackIndex < 0 || r.TrackIndex >= _clip.Tracks.Count) continue;
            var keys = _clip.Tracks[r.TrackIndex].Keys;
            if (r.KeyIndex < 0 || r.KeyIndex >= keys.Count) continue;

            if (common is null) common = keys[r.KeyIndex].Interp;
            else if (common != keys[r.KeyIndex].Interp) return null;
        }
        return common;
    }

    /// <summary>選択中キーの time / value / interp / tangent を編集する行を再構築する。</summary>
    private void RefreshValueEditor(int trackIndex, int keyIndex)
    {
        ValueEditorHost.Children.Clear();
        if (_clip is null || trackIndex < 0 || trackIndex >= _clip.Tracks.Count) return;
        var track = _clip.Tracks[trackIndex];
        if (keyIndex < 0 || keyIndex >= track.Keys.Count) return;
        var key = track.Keys[keyIndex];

        void AddLabel(string text) => ValueEditorHost.Children.Add(new TextBlock
        {
            Text = text, Foreground = new SolidColorBrush(AnimationTimelineConstants.SubTextColor),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0),
        });

        // ── フレーム番号（編集可。時刻の正はこちらで、秒は参考表示）──
        AddLabel("フレーム");
        var tbFrame = NewNumberBox(AnimFrameMath.TimeToFrame(key.Time, ClipFps)
                                                .ToString(CultureInfo.InvariantCulture));
        tbFrame.Width = 44;
        tbFrame.LostFocus += (_, _) =>
        {
            if (!int.TryParse(tbFrame.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f))
            {
                tbFrame.Text = AnimFrameMath.TimeToFrame(key.Time, ClipFps).ToString(CultureInfo.InvariantCulture);
                return;
            }
            key.Time = AnimFrameMath.FrameToTime(
                AnimFrameMath.ClampFrame(f, ClipFps, _clip.Duration), ClipFps);
            AnimKeyEditor.SortKeys(track);
            // 並べ替えで添字が動くため、選択をキーオブジェクトから張り直す
            DopeSheet.Selection.SetFromKeys(_clip.Tracks, new[] { (trackIndex, key) });
            CommitEdit();
            DopeSheet.NotifySelectionChangedExternally();
        };
        ValueEditorHost.Children.Add(tbFrame);

        // ── 時刻（秒）──
        AddLabel("秒");
        var tbTime = NewNumberBox(key.Time.ToString("0.###", CultureInfo.InvariantCulture));
        tbTime.LostFocus += (_, _) =>
        {
            var t = AnimClipIO.ParseFloatOr(tbTime.Text, key.Time);
            // 秒で入れてもフレーム格子へスナップする（キーが格子から外れないようにする）
            key.Time = AnimFrameMath.ClampAndSnapTime(t, ClipFps, _clip.Duration);
            AnimKeyEditor.SortKeys(track);
            DopeSheet.Selection.SetFromKeys(_clip.Tracks, new[] { (trackIndex, key) });
            CommitEdit();
            DopeSheet.NotifySelectionChangedExternally();
        };
        ValueEditorHost.Children.Add(tbTime);

        // ── 値（value_type の要素数ぶん）──
        AddLabel("値");
        for (int i = 0; i < key.Values.Length; i++)
        {
            var idx = i;
            var tb = NewNumberBox(key.Values[i].ToString("0.###", CultureInfo.InvariantCulture));
            tb.LostFocus += (_, _) =>
            {
                key.Values[idx] = AnimClipIO.ParseFloatOr(tb.Text, key.Values[idx]);
                CommitEdit();
            };
            ValueEditorHost.Children.Add(tb);
        }

        // ── 補間方式 ──
        AddLabel("補間");
        var cmbInterp = new ComboBox { Width = 80, FontSize = 11, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        foreach (var interp in AnimInterp.All)
            cmbInterp.Items.Add(new ComboBoxItem { Content = interp, Tag = interp });
        cmbInterp.SelectedIndex = Array.IndexOf(AnimInterp.All, key.Interp);
        cmbInterp.SelectionChanged += (_, _) =>
        {
            if (cmbInterp.SelectedItem is not ComboBoxItem item || item.Tag is not string interp) return;
            key.Interp = interp;
            CommitEdit();
            RefreshValueEditor(trackIndex, track.Keys.IndexOf(key));
        };
        ValueEditorHost.Children.Add(cmbInterp);

        // ── ベジェタンジェント（bezier のときのみ意味を持つが、常に編集可能にしておく）──
        if (key.Interp == AnimInterp.Bezier)
        {
            AddLabel("Tan In/Out");
            key.InTangent  ??= new float[key.Values.Length];
            key.OutTangent ??= new float[key.Values.Length];
            AddTangentBoxes(key.InTangent);
            AddTangentBoxes(key.OutTangent);
        }

        // ── 削除ボタン ──
        var btnDelete = new Button
        {
            Content = "削除", Background = new SolidColorBrush(Color.FromRgb(0x40, 0x28, 0x28)),
            Foreground = new SolidColorBrush(AnimationTimelineConstants.TextColor), BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 8, 2), FontSize = 11, Margin = new Thickness(12, 0, 0, 0), Cursor = Cursors.Hand,
        };
        btnDelete.Click += (_, _) => DeleteKeyAt(trackIndex, track.Keys.IndexOf(key));
        ValueEditorHost.Children.Add(btnDelete);

        void AddTangentBoxes(float[] arr)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                var idx = i;
                var tb = NewNumberBox(arr[i].ToString("0.###", CultureInfo.InvariantCulture));
                tb.Width = 44;
                tb.LostFocus += (_, _) => { arr[idx] = AnimClipIO.ParseFloatOr(tb.Text, arr[idx]); CommitEdit(); };
                ValueEditorHost.Children.Add(tb);
            }
        }
    }

    private static TextBox NewNumberBox(string text) => new()
    {
        Text = text, Width = 54, FontSize = 11,
        Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
        Foreground = new SolidColorBrush(AnimationTimelineConstants.TextColor),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0),
    };

    // ── 再生プレビュー ──────────────────────────────────────────

    /// <summary>プレビューが可能な状態か（アクター文脈あり・Edit モード・クリップロード済み）。</summary>
    private bool CanPreview() =>
        !_isFileOnlyMode && _actorDfsId >= 0 && _clip is not null &&
        _currentFilePath is not null && _runtime?.State == EditorState.Edit;

    private void UpdatePreviewAvailability()
    {
        BtnPlayPause.IsEnabled = CanPreview();
        if (!CanPreview()) StopPreview();
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_isPlaying) { StopPreview(); return; }
        if (!CanPreview()) return;

        _isPlaying = true;
        BtnPlayPause.Content = SEEDEditor.Controls.AppIcon.Create("Icon.Pause", PlayPauseIconSize);
        _previewTimer.Start();
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        if (_clip is null) return;
        var dt = AnimationTimelineConstants.PreviewTickIntervalMs / 1000f;
        _previewTime = AdvancePreviewTime(_previewTime, dt, _clip.Duration, _clip.LoopMode);
        DopeSheet.SetPlayheadTime(_previewTime);
        UpdateFrameBox();
        SendPreview(_previewTime);
    }

    /// <summary>loop_mode に従って再生時刻を進める（once=末端で停止、loop=先頭へ折返し、ping_pong=往復）。</summary>
    private float AdvancePreviewTime(float current, float dt, float duration, string loopMode)
    {
        if (duration <= 0f) return 0f;
        var next = current + dt;
        switch (loopMode)
        {
            case AnimLoopMode.Loop:
                return next % duration;
            case AnimLoopMode.PingPong:
                var cycle = duration * 2f;
                var m = next % cycle;
                return m <= duration ? m : cycle - m;
            default: // once
                if (next >= duration) { StopPreview(); return duration; }
                return next;
        }
    }

    private void OnPlayheadScrubbed(float time)
    {
        _previewTime = time;
        UpdateFrameBox();
        SendPreview(time);
    }

    private void OnPlayheadScrubEnded() => StopPreview();

    /// <summary>プレビュー再生・スクラブを停止し、必要なら ANIM_PREVIEW_STOP を送信して元値を復元する。</summary>
    private void StopPreview()
    {
        StopPreviewPlaybackOnly();

        if (_previewActive && _actorDfsId >= 0)
            _runtime?.SendToRuntime($"ANIM_PREVIEW_STOP:{_actorDfsId}");
        _previewActive = false;
    }

    /// <summary>
    /// 再生タイマだけを止める（プレビュー適用値はそのまま残す）。
    /// フレーム送りなど「再生は止めたいが、いま見えている姿勢は保ちたい」操作で使う。
    /// </summary>
    private void StopPreviewPlaybackOnly()
    {
        _isPlaying = false;
        BtnPlayPause.Content = SEEDEditor.Controls.AppIcon.Create("Icon.Play", PlayPauseIconSize);
        _previewTimer.Stop();
    }

    private void SendPreview(float time)
    {
        if (!CanPreview() || _currentFilePath is null) return;
        var virtualPath = VirtualPath.ToVirtual(_currentFilePath, _assetsPath);
        _runtime?.SendToRuntime(FormattableString.Invariant(
            $"ANIM_PREVIEW:{_actorDfsId},{virtualPath},{time}"));
        _previewActive = true;
    }

    // ── ライブプレビュー（未保存の編集内容をランタイムへ反映）────────

    /// <summary>
    /// 編集中（未保存）のクリップ本文をランタイムのプレビューキャッシュへ押し込み、
    /// 続けて現在のプレイヘッド位置でプレビューを適用し直す。
    ///
    /// これが無いと、ランタイムはディスク上の .anim をキャッシュしたままなので
    /// 「キーを動かしても保存するまで見た目が変わらない」状態になる。
    ///
    /// JSON は改行・カンマを含むため Base64 で包む（IPC は 1 行 1 コマンドのテキストプロトコル）。
    /// </summary>
    private void PushClipToRuntimeAndPreview()
    {
        if (_clip is null || _currentFilePath is null) return;
        if (!CanPreview()) return;

        try
        {
            var virtualPath = VirtualPath.ToVirtual(_currentFilePath, _assetsPath);
            var json        = AnimClipIO.Serialize(_clip);
            var base64      = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            _runtime?.SendToRuntime($"ANIM_PREVIEW_CLIP:{virtualPath},{base64}");
            SendPreview(_previewTime);
        }
        catch (Exception ex)
        {
            EditorLog.Write($"AnimationTimelinePanel: ライブプレビュー送信失敗: {ex.Message}");
        }
    }

    // ── 全体再描画・ダーティ管理 ─────────────────────────────────

    private void MarkDirty()
    {
        if (_isDirty) return;
        _isDirty = true;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var name = _clip?.Name ?? "アニメーション";
        TitleChanged?.Invoke(_isDirty ? $"アニメーション: {name} *" : $"アニメーション: {name}");
        TbTitleStatus.Text = _isDirty ? "未保存の変更あり" : "";
    }

    /// <summary>クリップ差し替え・保存・トラック増減時に UI 全体を再構築する。</summary>
    private void RefreshAll()
    {
        TbDuration.Text = (_clip?.Duration ?? 0f).ToString(CultureInfo.InvariantCulture);
        TbFps.Text      = ClipFps.ToString(CultureInfo.InvariantCulture);

        CmbLoopMode.SelectionChanged -= OnLoopModeChanged;
        var mode = _clip?.LoopMode ?? AnimLoopMode.Once;
        CmbLoopMode.SelectedIndex = Array.IndexOf(AnimLoopMode.All, mode);
        CmbLoopMode.SelectionChanged += OnLoopModeChanged;

        RefreshTrackList();
        DopeSheet.SetClip(_clip);
        // プレイヘッドは保持する（Undo やトラック追加のたびに先頭へ飛ぶと編集にならない）。
        // クリップ差し替え時は呼び出し側が事前に _previewTime = 0 にしている。
        _previewTime = AnimFrameMath.ClampAndSnapTime(_previewTime, ClipFps, _clip?.Duration ?? 0f);
        DopeSheet.SetPlayheadTime(_previewTime);
        UpdateFrameBox();
        ClearValueEditor();
        UpdatePreviewAvailability();
        UpdateContextInfo();
        UpdateTitle();

        var hasClip = _clip is not null;
        BtnSaveClip.IsEnabled = hasClip;
        // CmbClip 自体は「選択可能なクリップが 1 つでもあるか」で有効/無効を決める（model クリップ選択中も
        // 他クリップへ切り替えられるようにするため、hasClip=false でも disable しない）。
        CmbClip.IsEnabled     = _actorClips.Count > 0 && !_isFileOnlyMode;
        // model クリップ選択中は専用の案内文に差し替える（それ以外は既定文言のまま）。
        TbNoClipHint.Text       = _isModelClipSelected ? ModelClipHint : DefaultNoClipHint;
        TbNoClipHint.Visibility = hasClip ? Visibility.Collapsed : Visibility.Visible;
        DopeSheet.Visibility    = hasClip ? Visibility.Visible   : Visibility.Collapsed;
        // duration / loop_mode は _clip 前提の値のため、model クリップ選択中は編集不可にする
        // （ドープシート同様「モデル内蔵アニメはInspectorで編集」の対象）。
        TbDuration.IsEnabled  = hasClip;
        CmbLoopMode.IsEnabled = hasClip;
        // フレーム操作・キー挿入はクリップが無いと意味がないためまとめて無効化する
        TbFps.IsEnabled           = hasClip;
        TbFrame.IsEnabled         = hasClip;
        BtnFrameStart.IsEnabled   = hasClip;
        BtnFramePrev.IsEnabled    = hasClip;
        BtnFrameNext.IsEnabled    = hasClip;
        BtnFrameEnd.IsEnabled     = hasClip;
        BtnInsertKey.IsEnabled    = hasClip;
        BtnOverwriteKey.IsEnabled = hasClip;
    }
}
