using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SEEDEditor;
using SEEDEditor.Controls;
using SEEDEditor.Runtime;
using SEEDEditor.Scripting;

namespace SEEDEditor.Panels;

/// <summary>
/// 選択中アクター／インスタンスのプロパティ・コンポーネントを表示編集するドッキングパネル。
/// Rust ランタイムから届く ACTOR_COMPONENTS 等の JSON を基にアコーディオン形式の
/// コンポーネントエディタを構築し、値変更・コンポーネント追加削除・複製・
/// マテリアル編集・プレハブ参照操作などを SET_* / ADD_COMPONENT 等のコマンドとして送信する。
/// </summary>
public partial class InspectorPanel : UserControl
{
    // ── Runtime connection ────────────────────────────────────
    private RuntimeManager? _runtime;
    private int             _currentActorId = -1;

    // ── Assets path ──────────────────────────────────────────
    /// <summary>仮想パス変換に使用するアセットルートパス。</summary>
    private string _assetsPath = "";

    // ── Mode ─────────────────────────────────────────────────
    private bool     _isActorEditMode       = false;
    /// <summary>
    /// 仮想アクターノード（DFS ID）での選択かどうか。
    /// シーンモードでもアクターが仮想選択された場合は SET_ACTOR_TRANSFORM を使う。
    /// </summary>
    private bool     _isVirtualActorSelected = false;
    private int      _selectedSlotIdx    = -1;   // 選択中のコンポーネントスロット
    private int      _componentSlotCount = 0;
    private SlotInfo? _copiedSlot        = null;  // Ctrl+C でコピーしたスロット

    /// <summary>
    /// スロット番号 → そのコンポーネントのアコーディオンヘッダー要素群。
    /// コンポーネント一覧廃止に伴い、リネーム・選択ハイライト・右クリックメニューは
    /// このヘッダーへ移譲された。BuildActorComponentList のたびに再構築される。
    /// </summary>
    private readonly Dictionary<int, AccordionHeaderRefs> _accordionHeaders = new();

    /// <summary>アコーディオンヘッダーを構成する要素への参照（リネーム・選択ハイライト用）。</summary>
    private sealed class AccordionHeaderRefs
    {
        public required Border    Header;
        public required Grid      HeaderGrid;
        public required TextBlock TitleBlock;
        public required string    ComponentName;
    }

    /// <summary>次回の BuildActorComponentList 完了後に自動リネームモードを開始するスロットのベース名。</summary>
    private bool   _pendingDuplicateRename   = false;
    private string _pendingDuplicateBaseName = "";

    // ── ControlPointComponent の状態（フェーズB）───────────────

    /// <summary>
    /// ビューポート側で選択中の制御点（actorDfsId, slotIdx, index）。未選択なら null。
    /// Rust は制御点を編集するたびに ACTOR_COMPONENTS を再送してくるためインスペクタ UI は
    /// 頻繁に作り直される。選択状態をこのフィールドに保持しておき、再構築後も
    /// 該当行のハイライトを復元する。
    /// </summary>
    private (int actor, int slot, int index)? _selectedControlPoint;

    /// <summary>
    /// slotIdx → 制御点リストの行 Border（インデックス順）。ハイライト付け替えに使う。
    /// RebuildPoints のたびに該当スロットのエントリを差し替える。
    /// </summary>
    private readonly Dictionary<int, List<Border>> _controlPointRows = new();

    /// <summary>
    /// 「spline_points からコントロールポイントへ移行」ボタン押下後に、
    /// ADD_COMPONENT の結果として返ってくる ACTOR_COMPONENTS を待つための保留データ
    /// （アクタ相対 XYZ の制御点列）。未保留なら null。
    /// _pendingDuplicateRename と同じ「保留フラグ + 次回 BuildActorComponentList で処理」パターン。
    /// </summary>
    private List<(float x, float y, float z)>? _pendingSplineMigrationPoints;

    /// <summary>
    /// 上記移行の対象アクタ DFS ID（押下時の _currentActorId）。
    /// 保留中に別のアクタを選択すると、そのアクタの ACTOR_COMPONENTS で移行が誤発火して
    /// 無関係なアクタへ点列を書き込んでしまう。ID を照合して取り違えを防ぐ。
    /// </summary>
    private int _pendingSplineMigrationActorId = -1;

    /// <summary>
    /// 上記移行の要求元 WaterVolumeComponent スロット添字。
    /// 移行完了時に、その水スロットの <c>control_point_ref</c> へ
    /// 「このアクタ自身の名前」を書き込んで結線を完成させる（W4.1）。
    /// 未保留なら -1。
    /// </summary>
    private int _pendingSplineMigrationWaterSlotIdx = -1;

    // ── Script state ─────────────────────────────────────────
    private readonly Dictionary<string, Type> _scriptTypeCache = new();
    private string _lastComponentsJson = "";

    /// <summary>「スクリプトを編集」ボタンで .cs を内蔵エディタで開くよう要求する（フルパス）。</summary>
    public event Action<string>? ScriptFileOpenRequested;

    /// <summary>
    /// プレハブ参照バーのダブルクリック等で、参照元 .actor をアクタ編集タブで開くよう要求する（フルパス）。
    /// MainWindow の OnActorFileOpened に配線される。
    /// </summary>
    public event Action<string>? ActorFileOpenRequested;

    // ── プレハブ参照バー ─────────────────────────────────────
    /// <summary>
    /// 現在選択中アクターのプレハブ参照元パス（assets:// 仮想パス or 絶対パス）。
    /// ACTOR_COMPONENTS の prefab_source が非 null のときのみ設定される（非プレハブは null）。
    /// Hierarchy からの「アクタファイルを開く」要求（OpenPrefabSource）でも参照する。
    /// </summary>
    private string? _currentPrefabSource;
    /// <summary>
    /// Hierarchy 側からプレハブ参照元を開く要求が来たが、まだ対象アクターの
    /// ACTOR_COMPONENTS（prefab_source）が届いていない場合に、その対象 DFS ID を保持する。
    /// 次回 BuildActorComponentList で一致すれば自動で開く（選択直後の非同期ギャップ対策）。
    /// 未使用時は -1。
    /// </summary>
    private int _pendingOpenPrefabForDfs = -1;

    /// <summary>
    /// 「キャンバスを編集」ボタンでキャンバス編集タブを開くよう要求する
    /// （引数はキャンバスを所有するアクターの DFS ID）。
    /// シーン内の 2D スクリーンスペースキャンバス・3D ワールドキャンバス共通。
    /// </summary>
    public event Action<int>? CanvasEditRequested;

    /// <summary>スクリプト保存などで型キャッシュを無効化する（次回表示時に再コンパイル）。</summary>
    public void InvalidateScriptTypeCache(string? path = null)
    {
        if (path is null) _scriptTypeCache.Clear();
        else              _scriptTypeCache.Remove(path);
    }

    // ── Plugin state ─────────────────────────────────────────
    /// <summary>ロード済みプラグイン名リスト。PLUGIN_LIST IPC メッセージで更新される。</summary>
    private List<string> _pluginNames = new();

    // ── Transform fields ─────────────────────────────────────
    private TextBox? _tbPx, _tbPy, _tbPz;
    private TextBox? _tbEx, _tbEy, _tbEz;
    private TextBox? _tbSx, _tbSy, _tbSz;
    /// <summary>2D Actor 専用: ピボット X/Y フィールド。</summary>
    private TextBox? _tbPivotX, _tbPivotY;
    /// <summary>2D Actor 専用: アンカー X/Y フィールド。</summary>
    private TextBox? _tbAnchorX, _tbAnchorY;
    private bool     _isDraggingTransform = false;
    /// <summary>現在選択中のアクターが 2D Actor（CanvasTransform 持ち）かどうか。</summary>
    private bool     _isActor2D = false;
    /// <summary>
    /// 現在選択中のアクターが「ビューポート所属のルートキャンバス」
    /// （トップレベル Actor2D + CanvasComponent）かどうか。
    /// ACTOR_COMPONENTS の is_root / is_vp フラグと Canvas スロットの有無から判定する。
    /// シーンモード限定（アクター編集タブでは常に false = 従来の編集 UI を維持）。
    /// ルートキャンバスは解像度が自動計算・Transform が恒等固定のため、
    /// 幅/高さフィールドの非表示・CanvasTransform の読み取り専用化に使用する。
    /// </summary>
    private bool     _isViewportRootCanvas = false;

    public InspectorPanel()
    {
        InitializeComponent();
    }

    // ── Events ───────────────────────────────────────────────

    public event Action? TransformCommitted;

    // ── Runtime binding ──────────────────────────────────────

    /// <summary>アセットルートパスを設定する（仮想パス変換に使用）。</summary>
    public void SetAssetsPath(string assetsPath) => _assetsPath = assetsPath;

    public void SetRuntime(RuntimeManager runtime)
    {
        if (_runtime is not null)
        {
            _runtime.SelectionChanged        -= OnSelectionChanged;
            _runtime.ActorDataReceived       -= OnActorDataReceived;
            _runtime.ActorComponentsReceived -= OnActorComponentsReceived;
            _runtime.PluginListReceived      -= OnPluginListReceived;
            _runtime.ControlPointSelected    -= OnControlPointSelected;
            _runtime.ControlPointDeselected  -= OnControlPointDeselected;
            _runtime.BindableSourcesReceived -= OnBindableSourcesReceived;
        }
        _runtime = runtime;
        _runtime.SelectionChanged        += OnSelectionChanged;
        _runtime.ActorDataReceived       += OnActorDataReceived;
        _runtime.ActorComponentsReceived += OnActorComponentsReceived;
        _runtime.PluginListReceived      += OnPluginListReceived;
        _runtime.ControlPointSelected    += OnControlPointSelected;
        _runtime.ControlPointDeselected  += OnControlPointDeselected;
        _runtime.BindableSourcesReceived += OnBindableSourcesReceived;
    }

    /// <summary>
    /// ビューポートで制御点が選択された通知を受け、対応するリスト行をハイライトする。
    /// IPC はワーカースレッドから来るため Dispatcher へマーシャリングする。
    /// </summary>
    private void OnControlPointSelected(int actorDfsId, int slotIdx, int index)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _selectedControlPoint = (actorDfsId, slotIdx, index);
            RefreshControlPointRowHighlight();
        });
    }

    /// <summary>ビューポートの制御点選択が解除された通知を受け、ハイライトを消す。</summary>
    private void OnControlPointDeselected()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _selectedControlPoint = null;
            RefreshControlPointRowHighlight();
        });
    }

    /// <summary>
    /// 制御点リストの各行の背景を _selectedControlPoint に合わせて塗り直す。
    /// UI 再構築後（RebuildPoints 直後）にも呼び出して選択ハイライトを復元する。
    /// </summary>
    private void RefreshControlPointRowHighlight()
    {
        foreach (var (slotIdx, rows) in _controlPointRows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                bool selected = _selectedControlPoint is { } sel
                             && sel.actor == _currentActorId
                             && sel.slot  == slotIdx
                             && sel.index == i;
                rows[i].Background = selected
                    ? new SolidColorBrush(ControlPointSelectedRowColor)
                    : Brushes.Transparent;
            }
        }
    }

    /// <summary>PLUGIN_LIST メッセージを受信してプラグイン名リストを更新する。</summary>
    private void OnPluginListReceived(string json)
    {
        // [{"name":"...","version":"...","description":"..."},...]
        try
        {
            using var doc = JsonDocument.Parse(json);
            _pluginNames = doc.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                .Where(n => n.Length > 0)
                .ToList();
        }
        catch { _pluginNames = new(); }
    }

    public void SetActorEditMode(bool isActorMode)
    {
        _isActorEditMode = isActorMode;
        ShowNoSelection();
    }

    /// <summary>HierarchyPanel からアクター編集モードのアクター選択を受け取る。</summary>
    public void SelectActor(int dfsId)
    {
        var tid  = System.Threading.Thread.CurrentThread.ManagedThreadId;
        var isUi = Dispatcher.CheckAccess();
        SEEDEditor.EditorLog.Write($"[Inspector.SelectActor] dfsId={dfsId} tid={tid} ui={isUi} currentId={_currentActorId}");

        // 表示モードを確実に切り替える（同一アクターへの再呼び出しでも必要）
        _isDraggingTransform    = false;
        _isVirtualActorSelected = true; // 仮想 DFS 選択 → SET_ACTOR_TRANSFORM を使う
        ActorEditGrid.Visibility    = Visibility.Visible;
        ComponentScroll.Visibility  = Visibility.Collapsed;
        NoSelectionBlock.Visibility = Visibility.Collapsed;

        // 同一アクターへの重複選択を排除する。
        // ActorDfsSelected と OnSelectionChanged の両経路から呼ばれると
        // GET_ACTOR_COMPONENTS が二重送信されて表示が遅くなるため。
        if (_currentActorId == dfsId) return;

        // シーンモード・アクター編集モード共通: DFS id でアクタを選択してコンポーネントを表示する
        ClearTransformRefs();
        _currentActorId = dfsId;
        // Rust 側にコンポーネントデータを要求する（ビューポートからの選択時でも確実に反映される）
        SEEDEditor.EditorLog.Write($"[Inspector.SelectActor] SendToRuntime GET_ACTOR_COMPONENTS:{dfsId}");
        _runtime?.SendToRuntime($"GET_ACTOR_COMPONENTS:{dfsId}");
        SEEDEditor.EditorLog.Write($"[Inspector.SelectActor] SendToRuntime done");
    }

    // ── Runtime events ───────────────────────────────────────

    // アクター仮想ノード ID の下限（HierarchyPanel と合わせること）
    private const int VirtualActorNodeIdBase = 999_000_000;

    private void OnSelectionChanged(int id)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (id < 0)
            {
                ShowNoSelection();
                return;
            }

            // 仮想ノード ID（アクターツリー DFS 選択）: シーンモード・アクター編集モード共通
            if (id >= VirtualActorNodeIdBase)
            {
                var dfsId = id - VirtualActorNodeIdBase;
                // ActorDfsSelected 経由（MainWindow 配線）で既に SelectActor が呼ばれているため、
                // 同一アクターへの二重呼び出しをスキップして不要な IPC ラウンドトリップを防ぐ。
                if (dfsId != _currentActorId)
                    SelectActor(dfsId);
                return;
            }

            // レガシー: インスタンスインデックス直接選択（シーン編集モード後方互換）
            ClearTransformRefs();
            _currentActorId         = id;
            _isVirtualActorSelected = false; // インスタンス直接選択 → SET_TRANSFORM を使う
            ActorNameBlock.Text         = $"Actor #{id}";
            ActorModelBlock.Visibility  = Visibility.Collapsed;
            ComponentStack.Children.Clear();
            ComponentScroll.Visibility  = Visibility.Visible;
            ActorEditGrid.Visibility    = Visibility.Collapsed;
            NoSelectionBlock.Visibility = Visibility.Collapsed;
            _runtime?.SendToRuntime($"GET_ACTOR:{id}");
        });
    }

    private void OnActorDataReceived(string json)
    {
        if (_isActorEditMode) return;
        Dispatcher.InvokeAsync(() =>
        {
            try { BuildSceneInspector(json); }
            catch (Exception ex) { EditorLog.Write($"InspectorPanel: JSON parse error: {ex.Message}"); }
        });
    }

    private void OnActorComponentsReceived(string json)
    {
        // シーンモード・アクター編集モード共通: アクタが選択されていれば常に更新する
        if (_currentActorId < 0) return;
        Dispatcher.InvokeAsync(() =>
        {
            // ドラッグ操作中は UI を再構築しない。
            // Rust が SET_*_TRANSFORM 等のたびに send_actor_components を返すため、
            // UIリビルドで TextBox 参照がクリアされてドラッグが中断されるのを防ぐ。
            // _isDraggingTransform: ラベルドラッグ中（MakeAxisLabel）
            // Mouse.Captured != null: NumericDragBehavior などによるキャプチャ中
            if (_isDraggingTransform || Mouse.Captured != null) return;

            // どのアクタの応答であっても構成キャッシュへ入れる。
            // 参照ピッカーがドラッグ中に「適合ゼロならドロップ拒否」を判定するために使う。
            var incomingId = TryGetActorComponentsId(json);
            if (ActorComponentSnapshot.TryParse(json) is { } snapshot)
                ActorComponentCache.Store(snapshot);

            // 参照ドロップの解決待ちなら、ドロップされたアクタの構成を参照設定処理へ転用する。
            // ドロップとは無関係な ACTOR_COMPONENTS（選択更新など）を取り違えないよう、
            // 応答の id が問い合わせた DFS ID と一致する場合に限る。
            if (_pendingReference is { } pending && incomingId == pending.DroppedActorDfsId)
            {
                ResolvePendingReference(json);
                return;
            }

            // 選択中でないアクタの応答（ドラッグ先読み等）でインスペクタを描き替えない。
            // id が取れない応答は従来どおり通す（後方互換）。
            if (incomingId >= 0 && incomingId != _currentActorId) return;

            try { BuildActorComponentList(json); }
            catch (Exception ex) { EditorLog.Write($"InspectorPanel: ACTOR_COMPONENTS parse error: {ex.Message}"); }
        });
    }

    // ── No selection ─────────────────────────────────────────

    private void ShowNoSelection()
    {
        _isVirtualActorSelected     = false;
        _isActor2D                  = false;
        _isViewportRootCanvas       = false;
        ActorNameBlock.Text         = "選択なし";
        ActorModelBlock.Visibility  = Visibility.Collapsed;
        ComponentScroll.Visibility  = Visibility.Collapsed;
        ActorEditGrid.Visibility    = Visibility.Collapsed;
        NoSelectionBlock.Visibility = Visibility.Visible;
        ComponentStack.Children.Clear();
        AccordionStack.Children.Clear();
        _accordionHeaders.Clear();
        _currentPrefabSource = null;
        ClearTransformRefs();
    }

    private void ClearTransformRefs()
    {
        _tbPx = _tbPy = _tbPz = null;
        _tbEx = _tbEy = _tbEz = null;
        _tbSx = _tbSy = _tbSz = null;
        _tbPivotX = _tbPivotY = null;
        _tbAnchorX = _tbAnchorY = null;
    }

    // ── Scene mode inspector ─────────────────────────────────

    private void BuildSceneInspector(string json)
    {
        using var doc  = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name      = root.TryGetProperty("name",       out var np) ? np.GetString() ?? "" : "";
        var modelPath = root.TryGetProperty("model_path", out var mp) ? mp.GetString() ?? "" : "";

        ActorNameBlock.Text = string.IsNullOrEmpty(name) ? $"Actor #{_currentActorId}" : name;
        if (!string.IsNullOrEmpty(modelPath))
        {
            ActorModelBlock.Text       = System.IO.Path.GetFileName(modelPath);
            ActorModelBlock.Visibility = Visibility.Visible;
        }

        ComponentStack.Children.Clear();
        ClearTransformRefs();

        if (root.TryGetProperty("canvas_transform", out var ct))
        {
            // 2D Actor: CanvasTransform
            _isActor2D = true;
            float px   = Fp(ct, "px"),  py   = Fp(ct, "py");
            float rot  = Fp(ct, "rotation");
            float sx   = Fp(ct, "sx"),  sy   = Fp(ct, "sy");
            float pivx = Fp(ct, "pivx"), pivy = Fp(ct, "pivy");
            float ancX = Fp(ct, "anchor_x"), ancY = Fp(ct, "anchor_y");
            // スケールモード（親キャンバスのスケール追従）。CanvasComponent から CanvasTransform へ移動した。
            bool scaleTransform = ct.TryGetProperty("scale_transform",   out var st1) && ReadJsonBool(st1, false);
            bool scaleSize      = ct.TryGetProperty("scale_size",        out var ss1) && ReadJsonBool(ss1, false);
            bool keepAspect     = ct.TryGetProperty("keep_aspect_ratio", out var ka1) && ReadJsonBool(ka1, false);
            int  aspectAxis     = ct.TryGetProperty("aspect_ratio_axis", out var ax1) && ax1.ValueKind == JsonValueKind.Number ? ax1.GetInt32() : 0;
            ComponentStack.Children.Add(BuildCanvas2DTransformSection(
                px, py, rot, sx, sy, pivx, pivy, ancX, ancY,
                scaleTransform: scaleTransform, scaleSize: scaleSize,
                keepAspect: keepAspect, aspectAxis: aspectAxis));
        }
        else if (root.TryGetProperty("transform", out var tf))
        {
            // 3D Actor: Transform
            _isActor2D = false;
            float px = Fp(tf, "px"), py = Fp(tf, "py"), pz = Fp(tf, "pz");
            float ex = Fp(tf, "ex"), ey = Fp(tf, "ey"), ez = Fp(tf, "ez");
            float sx = Fp(tf, "sx"), sy = Fp(tf, "sy"), sz = Fp(tf, "sz");

            var section = BuildSection("Transform");
            var grid = BuildXYZGrid();

            (_tbPx, _tbPy, _tbPz) = AddXYZRow(grid, 0, "位置",    px, py, pz, "#E06C75", "#98C379", "#61AFEF", 0.1);
            (_tbEx, _tbEy, _tbEz) = AddXYZRow(grid, 1, "回転",    ex, ey, ez, "#E06C75", "#98C379", "#61AFEF", 1.0);
            (_tbSx, _tbSy, _tbSz) = AddXYZRow(grid, 2, "スケール", sx, sy, sz, "#E06C75", "#98C379", "#61AFEF", 0.01);

            ((StackPanel)section.Child).Children.Add(grid);
            ComponentStack.Children.Add(section);
        }

        if (!string.IsNullOrEmpty(modelPath))
        {
            var section = BuildSection("Model");
            var sp = (StackPanel)section.Child;
            sp.Children.Add(BuildPropertyRow("パス", System.IO.Path.GetFileName(modelPath)));
            ComponentStack.Children.Add(section);
        }

        ComponentScroll.Visibility  = Visibility.Visible;
        NoSelectionBlock.Visibility = Visibility.Collapsed;
    }

    // ── Actor edit mode: component list ──────────────────────

    // ── WaterVolumeComponent の既定値 ─────────────────────────
    // Rust 側 WaterVolumeComponentData の既定値と厳密に一致させること。
    // SlotInfo のフィールド既定値と ACTOR_COMPONENTS 受信時のフォールバックの
    // 両方から参照するため、リテラルの二重管理を避けて定数化している。

    /// <summary>水領域の種別既定値（"Ocean" / "Region" / "Spline"）。</summary>
    private const string WaterKindDefault = "Region";
    /// <summary>水面高さの既定値（Ocean=ワールド Y / Region=アクタ相対 Y）。</summary>
    private const float WaterSurfaceHeightDefault = 0f;
    /// <summary>Region 種別の領域半径 X の既定値（m）。</summary>
    private const float WaterRegionHalfXDefault = 10f;
    /// <summary>Region 種別の領域半径 Y の既定値（m）。</summary>
    private const float WaterRegionHalfYDefault = 5f;
    /// <summary>Region 種別の領域半径 Z の既定値（m）。</summary>
    private const float WaterRegionHalfZDefault = 10f;
    /// <summary>Ocean 種別の水面描画半径の既定値（m）。</summary>
    private const float WaterOceanExtentDefault = 2000f;
    /// <summary>浅場の色（リニア RGB）の既定値。</summary>
    private const float WaterShallowRDefault = 0.10f;
    private const float WaterShallowGDefault = 0.45f;
    private const float WaterShallowBDefault = 0.42f;
    /// <summary>深場の色（リニア RGB）の既定値。</summary>
    private const float WaterDeepRDefault = 0.01f;
    private const float WaterDeepGDefault = 0.06f;
    private const float WaterDeepBDefault = 0.12f;
    /// <summary>浅場色から深場色へ遷移しきる吸収距離の既定値（m）。</summary>
    private const float WaterAbsorptionDistanceDefault = 8f;
    /// <summary>水面の最大不透明度の既定値。</summary>
    private const float WaterSurfaceOpacityDefault = 0.92f;
    /// <summary>岸のフォーム色（リニア RGB）の既定値。</summary>
    private const float WaterFoamRDefault = 1f;
    private const float WaterFoamGDefault = 1f;
    private const float WaterFoamBDefault = 1f;
    /// <summary>フォーム帯の幅の既定値（m）。</summary>
    private const float WaterFoamWidthDefault = 0.35f;
    /// <summary>フォームの強度の既定値。</summary>
    private const float WaterFoamIntensityDefault = 0.8f;
    /// <summary>波の高さ（振幅）の既定値。</summary>
    private const float WaterWaveAmplitudeDefault = 0.06f;
    /// <summary>波のノイズスケールの既定値。</summary>
    private const float WaterWaveScaleDefault = 0.12f;
    /// <summary>波のスクロール速度の既定値。</summary>
    private const float WaterWaveSpeedDefault = 0.6f;
    /// <summary>波の進行方位角（度）の既定値。0 = +Z 方向へ進む。</summary>
    private const float WaterWaveDirectionDegDefault = 0f;
    /// <summary>フレネル指数の既定値。</summary>
    private const float WaterFresnelPowerDefault = 5f;
    /// <summary>フレネル強度の既定値。</summary>
    private const float WaterFresnelStrengthDefault = 1f;
    /// <summary>反射の全体強度の既定値（Phase W5.2。1.0 = フレネルどおりに映す／0 で無効）。</summary>
    private const float WaterReflectionIntensityDefault = 1f;
    /// <summary>波による反射のぼけ（粗さ）の既定値（Phase W5.2。0 で厳密な鏡面）。</summary>
    private const float WaterReflectionRoughnessDefault = 0.15f;
    /// <summary>屈折の歪み量の既定値。</summary>
    private const float WaterRefractionDistortionDefault = 0.03f;
    /// <summary>波紋・航跡（インタラクションフィールド）の法線摂動スケールの既定値。</summary>
    private const float WaterRippleStrengthDefault = 1f;
    /// <summary>波紋フォームが出る波高しきい値の既定値（m 相当）。</summary>
    private const float WaterRippleFoamThresholdDefault = 0.05f;
    /// <summary>水域の粘度の既定値（Phase I2.1。0 = ふつうの水＝従来と同一挙動）。</summary>
    private const float WaterViscosityDefault = 0f;
    /// <summary>
    /// 波紋の減衰率の既定値（1/s。Phase I2.1）。
    /// Rust 側 <c>default_ripple_damping</c>（エンジンの減衰時定数 1.5 秒の逆数）と一致させること。
    /// </summary>
    private const float WaterRippleDampingDefault = 1f / 1.5f;
    /// <summary>岸波（Phase W1.5）の強さの既定値。</summary>
    private const float WaterShoreStrengthDefault = 1f;
    /// <summary>岸波（Phase W1.5）のうねりの波長の既定値（m）。</summary>
    private const float WaterShoreLengthDefault = 12f;
    /// <summary>岸波（Phase W1.5）のうねりの周期の既定値（秒）。</summary>
    private const float WaterShorePeriodDefault = 4f;
    /// <summary>岸波（Phase W1.5）の泡量の既定値（0..1）。</summary>
    private const float WaterShoreFoamDefault = 0.8f;
    /// <summary>水の各色はアルファを持たないため、カラーピッカーへ渡す固定アルファ。</summary>
    private const float WaterColorAlpha = 1f;
    /// <summary>
    /// 水面シェーダのパラメータ（Phase W8.2）を運ぶ成分数。
    /// 型に依らず常に 4 成分で送受信する（color は xyz、スカラーは x のみ使う）。
    /// </summary>
    private const int ShaderParamComponentCount = 4;
    /// <summary>水面シェーダのパラメータ値の表示書式（小数 3 桁）。</summary>
    private const string ShaderParamNumberFormat = "F3";
    /// <summary>
    /// range 宣言が退化していた（max &lt;= min）場合に UI 側で確保する最小の幅。
    /// スライダーの Minimum == Maximum は操作不能になるため、表示だけを守るための保険。
    /// </summary>
    private const float ShaderParamMinRangeWidth = 1f;
    // 「デフォルトに戻す」ボタン（アセットが @reset を付けた行にだけ出る）の見た目は
    // SEEDEditor.Controls.ResetButtonFactory が持つ（スクリプトの [ResetButton] と共通）。
    /// <summary>水位シミュレーション（水位グラフ、Phase W2.5）を有効にするかの既定値。</summary>
    private const bool WaterSimulateLevelDefault = false;

    // ── WaterLinkComponent（水位グラフの開口、Phase W2.5）の既定値 ──
    // Rust 側 WaterLinkComponentData の既定値と厳密に一致させること。

    /// <summary>開口下端のアクタ相対 Y の既定値（m）。</summary>
    private const float WaterLinkOpeningBottomDefault = 0f;
    /// <summary>開口の高さの既定値（m）。標準的な扉の高さ 2m（Rust 側 default_opening_height と一致）。</summary>
    private const float WaterLinkOpeningHeightDefault = 2f;
    /// <summary>開口の幅の既定値（m）。</summary>
    private const float WaterLinkOpeningWidthDefault = 1f;
    /// <summary>開閉率（0..1）の既定値。0 でバルブ全閉。</summary>
    private const float WaterLinkOpennessDefault = 1f;
    /// <summary>流量係数（1/s）の既定値。</summary>
    private const float WaterLinkFlowCoefficientDefault = 1f;

    // ── 川（スプライン水面、W4）の既定値 ───────────────────────
    // kind == "Spline" のときのみ使用する。既定値は Rust 側 WaterVolumeComponentData と一致させる。

    /// <summary>川幅の既定値（m）。</summary>
    private const float WaterRiverWidthDefault = 4.0f;
    /// <summary>流速の既定値（m/s）。</summary>
    private const float WaterFlowSpeedDefault = 1.5f;
    /// <summary>川の深さの既定値（m）。水面からこの深さまでが水中判定になる。</summary>
    private const float WaterRiverDepthDefault = 2.0f;
    /// <summary>地形スナップボタンのオフセット Y の既定値（m）。制御点 Y = 地形高さ + このオフセット。</summary>
    private const float WaterSplineSnapOffsetYDefault = 0.2f;
    /// <summary>川の描画に最低限必要な制御点数。これ未満では未描画になる旨の注意書きを出す。</summary>
    private const int WaterSplineMinPointsForRender = 2;
    /// <summary>制御点が 1 個のときに「制御点を追加」した際、直前点から Z 方向へずらす距離の既定値（m）。</summary>
    private const float WaterSplineNewPointOffsetDefault = 2.0f;

    // ── 川の分割数・制御点参照（W4.1）─────────────────────────
    // Rust 側 WaterVolumeComponentData / spline.rs の定数と一致させること。

    /// <summary>川の折れ線 1 分割ぶんの目標長の既定値（m）。Rust 側 default_river_segment_length と一致。</summary>
    private const float WaterRiverSegmentLengthDefault = 2.0f;
    /// <summary>川の分割長の下限（m）。Rust 側 RIVER_SEGMENT_LENGTH_MIN と一致させること。</summary>
    private const float WaterRiverSegmentLengthMin = 0.25f;
    /// <summary>川の総分割数の上限。Rust 側 RIVER_MAX_SEGMENTS と一致（案内文で使う）。</summary>
    private const int WaterRiverMaxSegments = 256;
    /// <summary>制御点参照が未設定のときにドロップゾーンへ出す文言。</summary>
    private const string WaterControlPointRefUnsetText = "（未設定：spline_points を使用）";

    // ── InteractionSourceComponent の既定値（Phase I1）────────
    // Rust 側 InteractionSourceComponentData の既定値と厳密に一致させること。

    /// <summary>影響半径の既定値（m）。人型キャラの足元が草を踏み分ける幅。</summary>
    private const float InteractionRadiusDefault = 1f;
    /// <summary>書き込みの強さの既定値（0..1）。</summary>
    private const float InteractionStrengthDefault = 1f;
    /// <summary>有効フラグの既定値。</summary>
    private const bool InteractionEnabledDefault = true;

    // ── CoverEmitterComponent の既定値（Phase I3.1）────────────
    // Rust 側 CoverEmitterComponentData の既定値と厳密に一致させること。

    /// <summary>範囲種別の既定値。追加した瞬間に世界中が雪で埋まらないよう Region。</summary>
    private const string CoverRangeKindDefault = "Region";
    /// <summary>直方体の各軸半径の既定値（m）。既定チャンク 1 個ぶん（16m）の半分。</summary>
    private const float CoverExtentDefault = 8f;
    /// <summary>境界フェード幅の既定値（m）。0 だと縁に直線が出る。</summary>
    private const float CoverFadeDefault = 1f;
    /// <summary>マスク矩形の XZ サイズの既定値（m）。</summary>
    private const float CoverMaskSizeDefault = 32f;
    /// <summary>素材 ID の既定値（cover_materials.json の先頭素材）。</summary>
    private const string CoverMaterialIdDefault = "snow";
    /// <summary>強度の既定値（量/秒）。0.2 = 5 秒で満量。</summary>
    private const float CoverStrengthDefault = 0.2f;
    /// <summary>有効フラグの既定値。</summary>
    private const bool CoverEnabledDefault = true;
    /// <summary>シミュレート秒数入力の既定表示（空 = 連続シミュレート）。</summary>
    private const string CoverSimulateSecondsDefault = "";

    // ── ControlPointComponent（フェーズB）の定数 ───────────────
    // 川・巡回ルート・カメラパスなど用途中立の「順序付き点列」を編集するための定数群。
    // Rust 側 ControlPointComponent の定数と必ず一致させること。

    /// <summary>1 コンポーネントが保持できる制御点の上限。Rust 側 MAX_CONTROL_POINTS と一致させること。</summary>
    private const int ControlPointMaxCount = 256;
    /// <summary>「制御点を追加」で末尾に点を足すときの time 増分。Rust 側 DEFAULT_TIME_STEP と一致させること。</summary>
    private const float ControlPointTimeStepDefault = 1.0f;
    /// <summary>新規制御点の既定補間方式。Rust 側 Interp の既定（CatmullRom）と一致させること。</summary>
    private const string ControlPointInterpDefault = "CatmullRom";
    /// <summary>制御点が 1 個のときに追加した点を直前点から Z 方向へずらす距離（m）。川セクションと同じ規則。</summary>
    private const float ControlPointNewPointOffsetDefault = 2.0f;
    /// <summary>ビューポートで選択中の制御点行の背景ハイライト色（アコーディオンの選択枠と同系の青）。</summary>
    private static readonly Color ControlPointSelectedRowColor = Color.FromRgb(0x1A, 0x35, 0x55);
    /// <summary>
    /// 「制御点を追加」ボタンのドラッグ中にビューポートハイライトを更新するポーリング間隔（ms）。
    /// DoDragDrop が UI スレッドをブロックする間 GiveFeedback がほぼ発火しないため、
    /// 別スレッドからこの間隔で更新する（ProjectPanel のアイテムドラッグと同じ手法）。
    /// </summary>
    private const int ViewportHighlightPollIntervalMs = 30;
    /// <summary>spline_points からの移行で生成する制御点の time 増分（0,1,2,... の連番にする）。</summary>
    private const float SplineMigrationTimeStep = 1.0f;

    /// <summary>コンポーネントスロット 1 件分の情報。TypeId ごとに追加フィールドを持つ。</summary>
    private record SlotInfo(int SlotIdx, string Name, string TypeId, string ModelPath,
        float Width = 0f, float Height = 0f,
        // CanvasComponent 用自動スケールフラグ
        bool AutoScale = true,
        // CanvasComponent 用基準領域参照（"main_camera" / "window" / "camera"）
        string VpRefType = "window", string VpRefActor = "", string VpRefSlot = "",
        // CanvasComponent 用重力方向モード（0=スクリーン下, 1=キャンバス下）
        int GravityMode = 0,
        // CanvasComponent 用描画ゾーン（"foreground"=3Dワールドの手前・デフォルト / "background"=奥）
        // ビューポート所属のルートキャンバスのみ UI に表示する
        string DrawZone = "foreground",
        // CanvasComponent 用自動解像度（ビューポート・ルートキャンバスのみランタイムが送信。
        // プロジェクト設定解像度×参照カメラのスケーリングモードから算出された読み取り専用値）
        float AutoW = 0f, float AutoH = 0f,
        // 3D CanvasComponent 用ピボット（正規化値 [0,1]）。Actor3D アタッチ時のみ有効。
        float Canvas3dPivotX = 0f, float Canvas3dPivotY = 0f,
        // SpriteComponent 用フィールド
        string TexturePath = "",
        float SpriteR = 1f, float SpriteG = 1f, float SpriteB = 1f, float SpriteA = 1f,
        float SpriteW = 100f, float SpriteH = 100f,
        // SpriteComponent 用描画優先度レイヤー（大きいほど手前・同値はヒエラルキー順）
        int SpriteLayer = 0,
        // SpriteComponent 用: テクスチャ単位ポストエフェクト（.postfx アセット）参照。空文字列 = 未設定
        string PostFxPath = "",
        // InputMapComponent 用フィールド
        string InputMapPath = "",
        // CameraComponent 用フィールド
        float FovYDeg = 45f, float CamNear = 0.1f, float CamFar = 1000f,
        bool  IsMain  = false,
        float CamCR = 0.1f, float CamCG = 0.1f, float CamCB = 0.1f, float CamCA = 1f,
        string CamScalingMode = "vert_minus", int CamTargetW = 1920, int CamTargetH = 1080,
        float CamBarCR = 0f, float CamBarCG = 0f, float CamBarCB = 0f, float CamBarCA = 1f,
        // 投影方式（"perspective" / "orthographic"）と正射投影の縦描画範囲（ワールド単位）
        string CamProjection = "perspective", float CamOrthoHeight = 10f,
        // シェーディングアセット（.wgsl）参照。空文字列 = 未設定（シーン既定 → 組み込み標準 PBR）
        string CamShadingAsset = "",
        // シェーディングアセットが宣言した `override` パラメータの配列 JSON。
        // 水面の WaterShaderParamsJson と**同一のワイヤ表現**（解析の正典はランタイム）。
        // 空配列 = アセット未指定／宣言なし（＝パラメータ行を 1 つも作らない）。
        string CamShadingParamsJson = "[]",
        // PluginComponent 用フィールド
        // plugin_fields は [{"key":...,"label":...,"kind":{...},"current_value":...,"tooltip":...},...] JSON
        string PluginName = "", string PluginFieldsJson = "",
        // ColliderComponent 用フィールド（collider_data JSON 全体）
        string ColliderDataJson = "{}",
        // RigidbodyComponent 用フィールド（rigidbody_data JSON 全体）
        string RigidbodyDataJson = "{}",
        // ScriptComponent 用フィールド（[SerializeField] 現在値の JSON オブジェクト）
        string ScriptFieldsJson = "{}",
        // AudioComponent 用フィールド（音声パス・音量・ループ・自動再生・3D空間再生・減衰距離・パン）
        string AudioPath = "",
        float AudioVolume = 1f,
        bool AudioLoop = false, bool AudioPlayOnStart = false, bool AudioSpatial = false,
        float AudioMinDistance = 2f, float AudioMaxDistance = 50f,
        float AudioPan = 0f,
        // AnimatorComponent 用フィールド（clips は JSON 配列文字列 [{"name":..,"path":..},...] のまま保持し、
        // UI 構築時にパースする。値そのものは Rust 側 AnimatorComponentData と同一構造）
        string AnimClipsJson = "[]",
        string AnimDefaultClip = "",
        bool AnimPlayOnStart = true,
        float AnimSpeed = 1f,
        // LightComponent 用フィールド（種別・色・強度・range・スポット内外角・rect サイズ・影フラグ）
        string LightKind = "directional",
        float LightR = 1f, float LightG = 1f, float LightB = 1f,
        float LightIntensity = 3f, float LightRange = 10f,
        float LightInnerAngle = 25f, float LightOuterAngle = 35f,
        float LightRectWidth = 1f, float LightRectHeight = 1f,
        bool LightCastShadows = true,
        // ソフト影の見込み半径（directional=角径(度) / 局所光=ワールド半径。0 でハード影。RT 影時のみ効果）
        float LightSoftRadius = 0.25f,
        // 疑似バウンス（間接光近似）の強度。0 で無効（既定）。directional では非表示。
        float LightBounceIntensity = 0f,
        // ModelComponent 用フィールド（影を落とすか。シャドウマップレンダリングで使用）
        bool ModelCastShadows = true,
        // ModelComponent 用フィールド（セマンティックな描画タグ。0 = タグ無し）。
        // 合成/演出でグループ指定に使うため G-Buffer へ焼かれる値で、
        // 有効範囲は ModelRenderTagMin..ModelRenderTagMax（ランタイムの RENDER_TAG_BITS と一致）。
        int ModelRenderTag = 0,
        // JointAttachComponent 用フィールド（追従先ジョイント名・親モデルのジョイント一覧・
        // 位置/回転(YXZオイラー角・度)/スケールのオフセット）。
        // Joints が空/null の場合は「親アクターに Model がありません」の警告を表示する。
        string JointName = "", string[]? Joints = null,
        float JaOffPX = 0f, float JaOffPY = 0f, float JaOffPZ = 0f,
        float JaOffEX = 0f, float JaOffEY = 0f, float JaOffEZ = 0f,
        float JaOffSX = 1f, float JaOffSY = 1f, float JaOffSZ = 1f,
        // SkyboxComponent 用フィールド（equirectangular テクスチャパス・追従モード・強度・ティント）
        string SkyboxTexturePath = "",
        string SkyboxMode = "camera_locked",
        float SkyboxIntensity = 1f,
        float SkyboxTintR = 1f, float SkyboxTintG = 1f, float SkyboxTintB = 1f,
        // ParticleEmitterComponent 用フィールド（形状・出現範囲・放出制御・寿命・速度・方向・物理・
        // 回転・サイズ・テクスチャ・ブレンド・空間）。デフォルト値は Rust 側
        // ParticleEmitterComponentData と一致させる（受信欠落時のフォールバックにも使用）。
        int PeMaxParticles = 1024,
        // 形状（pixel/sphere/box/plane/model。旧 "point" は "pixel" に改名）と Model 形状時のパス
        string PeShape = "pixel", string PeShapeModelPath = "",
        // 出現範囲（point/box/sphere）とパラメータ
        string PeSpawnVolume = "point",
        float PeSpawnBoxX = 0f, float PeSpawnBoxY = 0f, float PeSpawnBoxZ = 0f,
        float PeSpawnSphereRadius = 0f,
        // 放出制御（loop/once/count）
        string PeEmitMode = "loop", int PeEmitCountTotal = 0,
        float PeInitialDelay = 0f, float PePrewarmTime = 0f,
        float PeEmitInterval = 0.05f, int PeParticlesPerEmit = 1,
        float PeLifetimeMin = 1f, float PeLifetimeMax = 2f,
        float PeSpeedMin = 1f, float PeSpeedMax = 3f,
        float PeDirX = 0f, float PeDirY = 1f, float PeDirZ = 0f,
        float PeDirectionRandomness = 0f,
        float PeGravityX = 0f, float PeGravityY = -9.8f, float PeGravityZ = 0f,
        float PeDrag = 0f,
        float PeRotSpeedMin = 0f, float PeRotSpeedMax = 0f,
        float PeSizeMin = 1f, float PeSizeMax = 1f,
        // 初期回転範囲（度）。Pixel 形状時は UI 上非表示（意味を持たないため）。
        float PeInitRotMin = 0f, float PeInitRotMax = 0f,
        // ブレンド（none/normal/add/sub/mul/screen）とシミュレーション空間
        string PeBlend = "add", string PeSimSpace = "world",
        bool PePlaying = true,
        // カーブ JSON（Rust ParamCurve の serde 形。カーブエディタで編集）。
        // speed/rot_speed=1ch、scale=3ch(xyz)。
        string PeSpeedCurveJson = "{}", string PeRotSpeedCurveJson = "{}",
        string PeScaleCurveJson = "{}",
        // 色カーブ配列（4ch HSVA ParamCurve の JSON 配列。必ず 1 要素以上）。
        string PeColorCurvesJson = "[]",
        // テクスチャパス配列（JSON 文字列配列。空可、最大 8）。
        string PeTexturePathsJson = "[]",
        // ── WaterVolumeComponent 用フィールド ──────────────────
        // 種別・水面高さ・領域サイズ・色・透明度・フォーム・波・反射/屈折。
        // 既定値は Rust 側 WaterVolumeComponentData と一致させる（受信欠落時のフォールバックにも使用）。
        string WaterKind = WaterKindDefault,
        float WaterSurfaceHeight = WaterSurfaceHeightDefault,
        // Region 種別の領域半径（アクタ中心からの XYZ 半径。Ocean/Spline では未使用）
        float WaterRegionHX = WaterRegionHalfXDefault,
        float WaterRegionHY = WaterRegionHalfYDefault,
        float WaterRegionHZ = WaterRegionHalfZDefault,
        // Ocean 種別の水面描画半径（m。Region/Spline では未使用）
        float WaterOceanExtent = WaterOceanExtentDefault,
        // 浅場・深場の色（リニア RGB）と吸収距離・最大不透明度
        float WaterShallowR = WaterShallowRDefault,
        float WaterShallowG = WaterShallowGDefault,
        float WaterShallowB = WaterShallowBDefault,
        float WaterDeepR = WaterDeepRDefault,
        float WaterDeepG = WaterDeepGDefault,
        float WaterDeepB = WaterDeepBDefault,
        float WaterAbsorptionDistance = WaterAbsorptionDistanceDefault,
        float WaterSurfaceOpacity = WaterSurfaceOpacityDefault,
        // 水面シェーディングアセット（Phase W8。.wgsl の仮想パス）。空文字列 = エンジン標準の水面シェーディング。
        // 形状・波・反射・屈折には影響せず、水面の「色だけ」を差し替えるアセット。
        string WaterSurfaceShader = "",
        // 水面シェーダのパラメータ注釈（Phase W8.2）。ランタイムが解析して送る JSON 配列の生テキスト。
        // 要素: {"name","type"("color"/"range"/"float"),"label","min","max","value":[x,y,z,w]}
        // 空配列 = アセット未指定／宣言なし（＝パラメータ行を 1 つも作らない）。
        string WaterShaderParamsJson = "[]",
        // 岸のフォーム（色・幅・強度）
        float WaterFoamR = WaterFoamRDefault,
        float WaterFoamG = WaterFoamGDefault,
        float WaterFoamB = WaterFoamBDefault,
        float WaterFoamWidth = WaterFoamWidthDefault,
        float WaterFoamIntensity = WaterFoamIntensityDefault,
        // 波（振幅・スケール・速度）
        float WaterWaveAmplitude = WaterWaveAmplitudeDefault,
        float WaterWaveScale = WaterWaveScaleDefault,
        float WaterWaveSpeed = WaterWaveSpeedDefault,
        // 波の進行方位角（度）。0 = +Z へ進み、正の角度で +X 側へ回る（上から見て時計回り）。
        float WaterWaveDirectionDeg = WaterWaveDirectionDegDefault,
        // 反射・屈折（フレネル指数/強度・反射色・屈折の歪み）
        float WaterFresnelPower = WaterFresnelPowerDefault,
        float WaterFresnelStrength = WaterFresnelStrengthDefault,
        // 反射（Phase W5.2）: 強度と粗さ。旧「反射色」は本物の反射像へ置き換わり撤去済み。
        float WaterReflectionIntensity = WaterReflectionIntensityDefault,
        float WaterReflectionRoughness = WaterReflectionRoughnessDefault,
        float WaterRefractionDistortion = WaterRefractionDistortionDefault,
        // 波紋・航跡（Phase I2）: 法線摂動スケール・フォームしきい値
        float WaterRippleStrength = WaterRippleStrengthDefault,
        float WaterRippleFoamThreshold = WaterRippleFoamThresholdDefault,
        // 水域ごとの物性（Phase I2.1）: 粘度・波紋の減衰率
        float WaterViscosity = WaterViscosityDefault,
        float WaterRippleDamping = WaterRippleDampingDefault,
        // 岸波（Phase W1.5）: 岸に寄せるうねりの強さ・波長・周期・泡量
        float WaterShoreStrength = WaterShoreStrengthDefault,
        float WaterShoreLength = WaterShoreLengthDefault,
        float WaterShorePeriod = WaterShorePeriodDefault,
        float WaterShoreFoam = WaterShoreFoamDefault,
        // 川（スプライン、W4）: 川幅・流速・制御点リスト（アクタ相対 "x,y,z;x,y,z;..." 形式。無ければ空文字列）
        float WaterRiverWidth = WaterRiverWidthDefault,
        float WaterFlowSpeed = WaterFlowSpeedDefault,
        float WaterRiverDepth = WaterRiverDepthDefault,
        string WaterSplinePoints = "",
        // 川（W4.1）: 折れ線 1 分割ぶんの目標長（m）と、制御点を借りる参照先アクタ名（空 = 参照なし）
        float WaterRiverSegmentLength = WaterRiverSegmentLengthDefault,
        string WaterControlPointRef = "",
        // 水位シミュレーション（水位グラフ、Phase W2.5）を有効にするか。
        // true のとき、この WaterVolume は WaterLinkComponent を介した連通ボリューム浸水の対象になる。
        // 既定値は Rust 側 WaterVolumeComponentData::simulate_level と一致させる。
        bool WaterSimulateLevel = WaterSimulateLevelDefault,
        // ── WaterLinkComponent 用フィールド（Phase W2.5）───────────
        // 2 つの WaterVolume をつなぐ開口（扉・窓・穴・バルブ）。既定値は Rust 側
        // WaterLinkComponentData と一致させる（受信欠落時のフォールバックにも使用）。
        // 接続先 A/B（アクタ名。空文字列 = 未接続）。
        string WaterLinkVolumeA = "", string WaterLinkVolumeB = "",
        // 開口下端のアクタ相対 Y（m）・高さ（m）・幅（m）。
        float WaterLinkOpeningBottom = WaterLinkOpeningBottomDefault,
        float WaterLinkOpeningHeight = WaterLinkOpeningHeightDefault,
        float WaterLinkOpeningWidth  = WaterLinkOpeningWidthDefault,
        // 開閉率（0..1。0 = バルブ全閉）と流量係数（1/s）。
        float WaterLinkOpenness         = WaterLinkOpennessDefault,
        float WaterLinkFlowCoefficient  = WaterLinkFlowCoefficientDefault,
        // ── InteractionSourceComponent 用フィールド（Phase I1）──
        // 影響半径・強さ・有効フラグ。既定値は Rust 側 InteractionSourceComponentData と一致。
        float InteractionRadius = InteractionRadiusDefault,
        float InteractionStrength = InteractionStrengthDefault,
        bool InteractionEnabled = InteractionEnabledDefault,
        // ── ControlPointComponent 用フィールド（フェーズB）──────
        // 制御点配列は「生 JSON 文字列」のまま保持する。1 点が position/rotation/time/interp の
        // 4 属性を持つため、独自の区切り記法（"x,y,z;..." 等）を作るとエスケープと拡張で破綻する。
        // PeColorCurvesJson と同じく JSON をそのまま往復させ、送信時も JSON 配列で全置換する。
        string ControlPointsJson = "[]",
        // ── CoverEmitterComponent 用フィールド（Phase I3.1）──
        // 範囲種別・範囲パラメータ・素材 ID・強度・有効フラグ。
        // 既定値は Rust 側 CoverEmitterComponentData と一致。
        string CoverRangeKind = CoverRangeKindDefault,
        float CoverExtentsX = CoverExtentDefault,
        float CoverExtentsY = CoverExtentDefault,
        float CoverExtentsZ = CoverExtentDefault,
        float CoverFade = CoverFadeDefault,
        string CoverMaskPath = "",
        float CoverMaskSizeX = CoverMaskSizeDefault,
        float CoverMaskSizeZ = CoverMaskSizeDefault,
        string CoverMaterialId = CoverMaterialIdDefault,
        float CoverStrength = CoverStrengthDefault,
        bool CoverEnabled = CoverEnabledDefault);

    private List<SlotInfo> _slotInfos = new();

    /// <summary>スロット番号 → 有効フラグ（ACTOR_COMPONENTS の enabled）。ヘッダーのチェックボックス初期値に使う。</summary>
    private readonly Dictionary<int, bool> _slotEnabledMap = new();

    /// <summary>
    /// 現在表示中のアクタの名前（ACTOR_COMPONENTS の "name"）。
    /// 川の制御点参照（<c>control_point_ref</c>）は**アクタ名**で持つため、
    /// 「このアクタに制御点を作って参照に設定」ボタンが自分自身の名前を書き込むのに使う。
    /// </summary>
    private string _currentActorName = "";

    // ── 折りたたみセクションの展開状態（再構築ごしの復元）──────────
    //
    // Rust は SET_* 系（SET_MATERIAL_OVERRIDE / SET_PARTICLE_FIELD / SET_SCRIPT_FIELD 等）を
    // 受けるたびに ACTOR_COMPONENTS を再送してくる仕様（OnActorComponentsReceived 参照）。
    // その結果 BuildActorComponentList がインスペクタ内の全セクションを作り直すため、
    // Expander / 自前アコーディオンは IsExpanded 初期値の新インスタンスへ置き換わり、
    // 「値を編集した瞬間に開いていたヘッダーが閉じる」症状になる（イベントバブリングではなく
    // UI 全体再構築による状態消失）。ControlPoint 行ハイライトの復元（_controlPointRows）と
    // 同じ「状態を退避して再構築後に復元する」方式を ExpandStateStore で汎用化して使う。

    /// <summary>
    /// インスペクタ内の全折りたたみセクションの展開状態。キーの体系は下記の Prefix 定数を参照。
    /// スコープはアクタ単位（BuildActorComponentList でアクタ ID を宣言し、切替時に自動破棄）。
    /// </summary>
    private readonly ExpandStateStore _expandStates = new();

    /// <summary>展開状態スコープのキー接頭辞（アクタ単位で状態を分離する）。</summary>
    private const string ExpandScopeActorPrefix = "actor:";

    /// <summary>コンポーネントアコーディオン（スロット単位）のキー接頭辞。後続はスロット添字。</summary>
    private const string ExpandKeyComponentPrefix = "component:";

    /// <summary>スロットに属さない固定セクション（基本情報など）のキー接頭辞。後続は見出し名。</summary>
    private const string ExpandKeySectionPrefix = "section:";

    /// <summary>マテリアル一覧まとめヘッダーのキー接頭辞。後続はモデルスロット添字。</summary>
    private const string ExpandKeyMaterialsPrefix = "materials:";

    /// <summary>マテリアル 1 スロットの Expander のキー接頭辞。後続は「モデルスロット添字:マテリアルスロット番号」。</summary>
    private const string ExpandKeyMaterialSlotPrefix = "material_slot:";

    /// <summary>パーティクルのカーブ行 Expander のキー接頭辞。後続は「スロット添字:カーブID[:要素添字]」。</summary>
    private const string ExpandKeyParticleCurvePrefix = "particle_curve:";

    /// <summary>スクリプトの [Serializable] ネスト Expander のキー接頭辞。後続は「スロット添字:フィールドのドットパス」。</summary>
    private const string ExpandKeyScriptFieldPrefix = "script_field:";
    /// <summary>アクターのアクティブチェックボックス更新中の再帰イベント抑止。</summary>
    private bool _updatingActorActive;

    /// <summary>アクターのアクティブチェックボックス変更 → ランタイムへ SET_ACTOR_ACTIVE を送信する。</summary>
    private void OnActorActiveChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingActorActive || _currentActorId < 0) return;
        var on = ActorActiveCheck.IsChecked == true;
        _runtime?.SendToRuntime($"SET_ACTOR_ACTIVE:{_currentActorId},{(on ? 1 : 0)}");
    }

    private void BuildActorComponentList(string json)
    {
        _lastComponentsJson = json;

        // 折りたたみセクションの展開状態はアクタ単位で保持する。
        // 同一アクタの再構築（値編集による ACTOR_COMPONENTS 再送）では状態が維持され、
        // 別アクタへ切り替わったタイミングでのみ破棄される。
        _expandStates.BeginScope(ExpandScopeActorPrefix + _currentActorId);

        using var doc  = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name = root.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
        ActorNameBlock.Text = string.IsNullOrEmpty(name) ? $"Actor #{_currentActorId}" : name;
        // 川の制御点参照（W4.1）はアクタ「名」で持つため、表示中アクタの名前を控えておく
        //（「このアクタに制御点を作って参照に設定」ボタンが自分自身の名前を書き込む）。
        _currentActorName = name;

        // アクターのアクティブチェックボックス（Unity の SetActive 相当）を同期する。
        // イベント再帰（Checked → 送信 → 再受信 → Checked...）は抑止フラグで防ぐ。
        var actorActive = !root.TryGetProperty("active", out var aav) || ReadJsonBool(aav, true);
        _updatingActorActive = true;
        ActorActiveCheck.IsChecked  = actorActive;
        ActorActiveCheck.Visibility = Visibility.Visible;
        _updatingActorActive = false;

        // 複製後の新スロット検出用に現在のスロット ID セットを保存する
        var prevSlotIdxSet = _slotInfos.Select(s => s.SlotIdx).ToHashSet();
        AccordionStack.Children.Clear();
        _accordionHeaders.Clear();
        // 制御点リストの行参照は UI ごと作り直されるので、ここで古い参照を捨てる
        // （残すと破棄済みの Border を塗り続けることになる）。
        _controlPointRows.Clear();
        ClearTransformRefs();
        _slotInfos.Clear();
        _slotEnabledMap.Clear();

        // ── ルートキャンバス判定（Phase B）────────────────────────────
        // is_root / is_vp（ランタイム送信）+ Canvas スロットの有無から
        // 「ビューポート所属のルートキャンバス」かを判定する。
        // アクター編集タブ（.actor2d ファイル編集・キャンバス編集タブ）では
        // 保存データを直接編集させるため対象外とする。
        var isRootActor  = root.TryGetProperty("is_root", out var irj) && ReadJsonBool(irj, false);
        var isVpActor    = root.TryGetProperty("is_vp",   out var ivj) && ReadJsonBool(ivj, false);
        var hasCanvasSlot = false;
        if (root.TryGetProperty("components", out var compsScan))
        {
            foreach (var comp in compsScan.EnumerateArray())
            {
                if (comp.TryGetProperty("type", out var ctScan) && ctScan.GetString() == "CanvasComponent")
                {
                    hasCanvasSlot = true;
                    break;
                }
            }
        }
        _isViewportRootCanvas = !_isActorEditMode && isRootActor && isVpActor && hasCanvasSlot;

        // ── 基本情報 アコーディオン ────────────────────────────────────
        UIElement transformContent;
        // フォルダノード判定（最優先）。フォルダは整理専用でTransformを持たないため、
        // ACTOR_COMPONENTS のルートに transform / canvas_transform フィールド自体が含まれない。
        // 通常アクターの「トランスフォームデータなし」（旧JSON/異常系フォールバック）とは別物として
        // 明示的にフォルダ用の説明文を出す。
        var isFolder = root.TryGetProperty("is_folder", out var ifj) && ReadJsonBool(ifj, false);
        if (isFolder)
        {
            // フォルダは 2D/3D どちらでもないため 2D アクター判定は false に固定する
            _isActor2D = false;
            transformContent = new TextBlock
            {
                Text       = "フォルダ（整理用ノード・Transform なし）",
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize   = 11,
                Margin     = new Thickness(0, 4, 0, 4),
            };
        }
        else if (root.TryGetProperty("canvas_transform", out var ct))
        {
            // 2D アクター: CanvasTransform（位置XY・回転・スケールXY・ピボットXY・アンカーXY）
            _isActor2D = true;
            float px   = Fp(ct, "px"),  py   = Fp(ct, "py");
            float rot  = Fp(ct, "rotation");
            float sx   = Fp(ct, "sx"),  sy   = Fp(ct, "sy");
            float pivx = Fp(ct, "pivx"), pivy = Fp(ct, "pivy");
            float ancX = Fp(ct, "anchor_x"), ancY = Fp(ct, "anchor_y");
            // スケールモード（親キャンバスのスケール追従）。CanvasComponent から CanvasTransform へ移動した。
            bool scaleTransform = ct.TryGetProperty("scale_transform",   out var st1) && ReadJsonBool(st1, false);
            bool scaleSize      = ct.TryGetProperty("scale_size",        out var ss1) && ReadJsonBool(ss1, false);
            bool keepAspect     = ct.TryGetProperty("keep_aspect_ratio", out var ka1) && ReadJsonBool(ka1, false);
            int  aspectAxis     = ct.TryGetProperty("aspect_ratio_axis", out var ax1) && ax1.ValueKind == JsonValueKind.Number ? ax1.GetInt32() : 0;
            // ルートキャンバスは Transform 恒等固定のため読み取り専用で表示する
            transformContent = BuildCanvas2DTransformSection(
                px, py, rot, sx, sy, pivx, pivy, ancX, ancY,
                scaleTransform: scaleTransform, scaleSize: scaleSize,
                keepAspect: keepAspect, aspectAxis: aspectAxis,
                locked: _isViewportRootCanvas);
        }
        else if (root.TryGetProperty("transform", out var tf))
        {
            // 3D アクター: Transform（位置XYZ・回転XYZ・スケールXYZ）
            _isActor2D = false;
            float px = Fp(tf, "px"), py = Fp(tf, "py"), pz = Fp(tf, "pz");
            float ex = Fp(tf, "ex"), ey = Fp(tf, "ey"), ez = Fp(tf, "ez");
            float sx = Fp(tf, "sx"), sy = Fp(tf, "sy"), sz = Fp(tf, "sz");

            var section = BuildSection("Transform");
            var grid    = BuildXYZGrid();
            (_tbPx, _tbPy, _tbPz) = AddXYZRow(grid, 0, "位置",    px, py, pz, "#E06C75", "#98C379", "#61AFEF", 0.1);
            (_tbEx, _tbEy, _tbEz) = AddXYZRow(grid, 1, "回転",    ex, ey, ez, "#E06C75", "#98C379", "#61AFEF", 1.0);
            (_tbSx, _tbSy, _tbSz) = AddXYZRow(grid, 2, "スケール", sx, sy, sz, "#E06C75", "#98C379", "#61AFEF", 0.01);
            ((StackPanel)section.Child).Children.Add(grid);
            transformContent = section;
        }
        else
        {
            transformContent = new TextBlock
            {
                Text       = "トランスフォームデータなし",
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize   = 11,
                Margin     = new Thickness(0, 4, 0, 4),
            };
        }
        // ── プレハブ参照バー（アコーディオン最上部・基本情報より上）──────────
        // ACTOR_COMPONENTS の prefab_source が非 null のときのみ、Unity 風に
        // プレハブと一目で分かる参照バーを最上部へ差し込む。非プレハブは従来の見た目のまま。
        _currentPrefabSource =
            root.TryGetProperty("prefab_source", out var psEl) && psEl.ValueKind == JsonValueKind.String
                ? psEl.GetString()
                : null;
        if (!string.IsNullOrEmpty(_currentPrefabSource))
            AccordionStack.Children.Add(BuildPrefabRefBar(_currentPrefabSource));

        AccordionStack.Children.Add(BuildAccordionSection("基本情報", "", transformContent, -1));

        // Hierarchy から「アクタファイルを開く」要求が保留されており、今回の再構築対象が
        // その対象アクターかつプレハブ参照を持つなら、ここで自動的に開く（選択→取得の非同期ギャップ対策）。
        if (_pendingOpenPrefabForDfs == _currentActorId && !string.IsNullOrEmpty(_currentPrefabSource))
        {
            _pendingOpenPrefabForDfs = -1;
            TryOpenPrefabSourcePath(_currentPrefabSource);
        }

        // ── コンポーネント アコーディオン ─────────────────────────────
        if (!root.TryGetProperty("components", out var comps)) return;

        _componentSlotCount = comps.GetArrayLength();
        var prevSlot = _selectedSlotIdx;
        _selectedSlotIdx = -1;

        // ※ W4.1 以前はここで「同一アクタに ControlPointComponent があるか」を先読みし、
        //    川セクションの旧 spline_points UI を隠す判定に使っていた。制御点の参照は
        //    control_point_ref による明示指定へ変わったため、その先読みは廃止した
        //    （UI の出し分けは「参照が設定されているか」だけを見る）。

        foreach (var comp in comps.EnumerateArray())
        {
            var slotIdx  = comp.TryGetProperty("slot",       out var si)  ? si.GetInt32()    : 0;
            var compName = comp.TryGetProperty("name",       out var cn)  ? cn.GetString() ?? "" : "";
            // コンポーネントの有効フラグ（省略時は true）。ヘッダーのチェックボックスに反映する
            _slotEnabledMap[slotIdx] = !comp.TryGetProperty("enabled", out var env) || ReadJsonBool(env, true);
            var compType = comp.TryGetProperty("type",       out var ctp) ? ctp.GetString() ?? "" : "";
            var modelPath = comp.TryGetProperty("model_path", out var mp) ? mp.GetString() ?? "" : "";
            // ModelComponent 用: 影を落とすか（シャドウマップレンダリングで使用。既定 true）
            var modelCastShadows = comp.TryGetProperty("cast_shadows", out var mcs) ? ReadJsonBool(mcs, true) : true;
            // ModelComponent 用: セマンティックな描画タグ（0 = タグ無し）。
            // 旧ランタイム／旧シーンではキーが欠落しうるため、その場合は既定 0 とする。
            var modelRenderTag = comp.TryGetProperty("render_tag", out var mrt) && mrt.TryGetInt32(out var mrtv)
                ? Math.Clamp(mrtv, ModelRenderTagMin, ModelRenderTagMax)
                : ModelRenderTagMin;
            // CanvasComponent 用: 幅・高さ・スケールモード
            var width          = comp.TryGetProperty("width",           out var wd)  ? wd.GetSingle()  : 0f;
            var height         = comp.TryGetProperty("height",          out var ht)  ? ht.GetSingle()  : 0f;
            // Rust の serde_json は bool を JSON 真偽値（true/false）としてシリアライズするため
            // GetInt32() ではなく ValueKind で判定する。数値（0/1）も念のため許容する。
            // スケールモード（scale_transform / scale_size / keep_aspect_ratio / aspect_ratio_axis）は
            // CanvasComponent から各 2D アクターの CanvasTransform へ移動したためここでは扱わない。
            var autoScale      = comp.TryGetProperty("auto_scale",      out var asv) ? ReadJsonBool(asv, true)  : true;
            // CanvasComponent 用: ビューポート参照
            var vpRefType  = comp.TryGetProperty("vp_ref_type",  out var vrt) ? vrt.GetString() ?? "window" : "window";
            var vpRefActor = comp.TryGetProperty("vp_ref_actor", out var vra) ? vra.GetString() ?? ""       : "";
            var vpRefSlot  = comp.TryGetProperty("vp_ref_slot",  out var vrs) ? vrs.GetString() ?? ""       : "";
            // CanvasComponent 用: 重力方向モード
            var gravityMode = comp.TryGetProperty("gravity_mode", out var gm)  ? gm.GetInt32()  : 0;
            // CanvasComponent 用: 描画ゾーン（"foreground" / "background"）
            var drawZone = comp.TryGetProperty("draw_zone", out var dzj) ? dzj.GetString() ?? "foreground" : "foreground";
            // CanvasComponent 用: 自動解像度（ビューポート・ルートキャンバスのみ送信される）
            var autoW = comp.TryGetProperty("auto_w", out var awj) ? awj.GetSingle() : 0f;
            var autoH = comp.TryGetProperty("auto_h", out var ahj) ? ahj.GetSingle() : 0f;
            // 3D CanvasComponent 用: ピボット
            var canvas3dPivotX = comp.TryGetProperty("pivot_x", out var pvx) ? pvx.GetSingle() : 0f;
            var canvas3dPivotY = comp.TryGetProperty("pivot_y", out var pvy) ? pvy.GetSingle() : 0f;
            // SpriteComponent 用: テクスチャパス・RGBA・サイズ
            var texPath = comp.TryGetProperty("texture_path", out var tp2) ? tp2.GetString() ?? "" : "";
            // SpriteComponent 用: テクスチャ単位ポストエフェクト（.postfx アセット）参照パス
            var postFxPath = comp.TryGetProperty("postfx_path", out var pfp) ? pfp.GetString() ?? "" : "";
            var sprR = comp.TryGetProperty("cr", out var cr) ? cr.GetSingle() : 1f;
            var sprG = comp.TryGetProperty("cg", out var cg) ? cg.GetSingle() : 1f;
            var sprB = comp.TryGetProperty("cb", out var cb) ? cb.GetSingle() : 1f;
            var sprA = comp.TryGetProperty("ca", out var ca) ? ca.GetSingle() : 1f;
            var sprW = comp.TryGetProperty("sprite_w", out var sw) ? sw.GetSingle() : 100f;
            var sprH = comp.TryGetProperty("sprite_h", out var sh) ? sh.GetSingle() : 100f;
            // SpriteComponent 用: 描画優先度レイヤー
            var sprLayer = comp.TryGetProperty("layer", out var slj) ? slj.GetInt32() : 0;
            // InputMapComponent 用: アセットパス
            var inputMapPath = comp.TryGetProperty("asset_path", out var ap) ? ap.GetString() ?? "" : "";
            // CameraComponent 用: FOV / near / far / is_main / clear_color
            var fovYDeg = comp.TryGetProperty("fov_y_deg", out var fov) ? fov.GetSingle() : 45f;
            var camNear = comp.TryGetProperty("near",      out var nr)  ? nr.GetSingle()  : 0.1f;
            var camFar  = comp.TryGetProperty("far",       out var fr)  ? fr.GetSingle()  : 1000f;
            var isMain  = comp.TryGetProperty("is_main",   out var im)  ? ReadJsonBool(im, false) : false;
            var camCR   = comp.TryGetProperty("cr",        out var ccr) ? ccr.GetSingle() : 0.1f;
            var camCG   = comp.TryGetProperty("cg",        out var ccg) ? ccg.GetSingle() : 0.1f;
            var camCB   = comp.TryGetProperty("cb",        out var ccb) ? ccb.GetSingle() : 0.1f;
            var camCA         = comp.TryGetProperty("ca",             out var cca) ? cca.GetSingle()  : 1.0f;
            var camScalingMode = comp.TryGetProperty("scaling_mode",  out var csm) ? csm.GetString() ?? "vert_minus" : "vert_minus";
            var camTargetW     = comp.TryGetProperty("target_width",  out var ctw) ? (int)ctw.GetUInt32() : 1920;
            var camTargetH     = comp.TryGetProperty("target_height", out var cth) ? (int)cth.GetUInt32() : 1080;
            var camBarCR       = comp.TryGetProperty("bar_cr", out var bcrj) ? bcrj.GetSingle() : 0f;
            var camBarCG       = comp.TryGetProperty("bar_cg", out var bcgj) ? bcgj.GetSingle() : 0f;
            var camBarCB       = comp.TryGetProperty("bar_cb", out var bcbj) ? bcbj.GetSingle() : 0f;
            var camBarCA       = comp.TryGetProperty("bar_ca", out var bcaj) ? bcaj.GetSingle() : 1f;
            var camProjection  = comp.TryGetProperty("projection",   out var cpj) ? cpj.GetString() ?? "perspective" : "perspective";
            var camOrthoHeight = comp.TryGetProperty("ortho_height", out var cohj) ? cohj.GetSingle() : 10f;
            // CameraComponent 用: シェーディングアセット（.wgsl）のパス。空文字は未設定を意味する
            var camShadingAsset = comp.TryGetProperty("shading_asset", out var csaj) ? csaj.GetString() ?? "" : "";
            // CameraComponent 用: シェーディングアセットのパラメータ宣言＋現在値（配列 JSON）
            var camShadingParams = comp.TryGetProperty("shading_params", out var cspj)
                ? cspj.GetRawText() : "[]";
            // PluginComponent 用: プラグイン名とフィールド定義 JSON
            var pluginName       = comp.TryGetProperty("plugin_name",   out var pn)  ? pn.GetString()  ?? "" : "";
            var pluginFieldsJson = comp.TryGetProperty("plugin_fields",  out var pf)  ? pf.GetRawText()      : "[]";
            // ColliderComponent 用: collider_data 全体 JSON
            var colliderDataJson   = comp.TryGetProperty("collider_data",   out var cd)  ? cd.GetRawText() : "{}";
            // RigidbodyComponent 用: rigidbody_data 全体 JSON
            var rigidbodyDataJson  = comp.TryGetProperty("rigidbody_data",  out var rd)  ? rd.GetRawText() : "{}";
            // ScriptComponent 用: [SerializeField] 現在値の JSON オブジェクト
            var scriptFieldsJson   = comp.TryGetProperty("script_fields",   out var sfj) ? sfj.GetRawText() : "{}";
            // AudioComponent 用: 音声パス・音量・ループ・自動再生・3D空間再生・減衰距離・パン
            // loop / play_on_start / spatial はランタイムから 0/1 の数値で送られるため ReadJsonBool で判定する
            var audioPath        = comp.TryGetProperty("audio_path",    out var aup) ? aup.GetString() ?? "" : "";
            var audioVolume      = comp.TryGetProperty("volume",        out var avo) ? avo.GetSingle() : 1f;
            var audioLoop        = comp.TryGetProperty("loop",          out var alp) ? ReadJsonBool(alp, false) : false;
            var audioPlayOnStart = comp.TryGetProperty("play_on_start", out var aps) ? ReadJsonBool(aps, false) : false;
            var audioSpatial     = comp.TryGetProperty("spatial",       out var asp) ? ReadJsonBool(asp, false) : false;
            var audioMinDistance = comp.TryGetProperty("min_distance",  out var amn) ? amn.GetSingle() : 2f;
            var audioMaxDistance = comp.TryGetProperty("max_distance",  out var amx) ? amx.GetSingle() : 50f;
            var audioPan         = comp.TryGetProperty("pan",           out var apn) ? apn.GetSingle() : 0f;
            // AnimatorComponent 用: クリップ一覧（生 JSON のまま保持）・既定クリップ・自動再生・速度
            var animClipsJson    = comp.TryGetProperty("clips",         out var acj) ? acj.GetRawText() : "[]";
            var animDefaultClip  = comp.TryGetProperty("default_clip",  out var adc) ? adc.GetString() ?? "" : "";
            var animPlayOnStart  = comp.TryGetProperty("play_on_start", out var apos) ? ReadJsonBool(apos, true) : true;
            var animSpeed        = comp.TryGetProperty("speed",         out var asp2) ? asp2.GetSingle() : 1f;
            // LightComponent 用: 種別・色（リニア RGB）・強度・range・スポット内外角・rect サイズ・影フラグ
            var lightKind        = comp.TryGetProperty("kind",         out var lki) ? lki.GetString() ?? "directional" : "directional";
            var lightR           = comp.TryGetProperty("lr",           out var llr) ? llr.GetSingle() : 1f;
            var lightG           = comp.TryGetProperty("lg",           out var llg) ? llg.GetSingle() : 1f;
            var lightB           = comp.TryGetProperty("lb",           out var llb) ? llb.GetSingle() : 1f;
            var lightIntensity   = comp.TryGetProperty("intensity",    out var lin) ? lin.GetSingle() : 3f;
            var lightRange       = comp.TryGetProperty("range",        out var lrg) ? lrg.GetSingle() : 10f;
            var lightInnerAngle  = comp.TryGetProperty("inner_angle",  out var lia) ? lia.GetSingle() : 25f;
            var lightOuterAngle  = comp.TryGetProperty("outer_angle",  out var loa) ? loa.GetSingle() : 35f;
            var lightRectWidth   = comp.TryGetProperty("rect_width",   out var lrw) ? lrw.GetSingle() : 1f;
            var lightRectHeight  = comp.TryGetProperty("rect_height",  out var lrh) ? lrh.GetSingle() : 1f;
            var lightSoftRadius  = comp.TryGetProperty("soft_radius",  out var lsr) ? lsr.GetSingle() : 0.25f;
            var lightBounce      = comp.TryGetProperty("bounce_intensity", out var lbi) ? lbi.GetSingle() : 0f;
            var lightCastShadows = comp.TryGetProperty("cast_shadows", out var lcs) ? ReadJsonBool(lcs, true) : true;
            // JointAttachComponent 用: 追従先ジョイント名・親モデルのジョイント名一覧（ドロップダウン選択肢）・
            // 位置/回転(YXZオイラー角・度)/スケールのオフセット。joints は文字列配列。欠落時は空配列
            // （＝親アクターに Model が無いことを示す）扱いにする。
            var jaJointName = comp.TryGetProperty("joint_name", out var jjn) ? jjn.GetString() ?? "" : "";
            string[] jaJoints;
            if (comp.TryGetProperty("joints", out var jjs) && jjs.ValueKind == JsonValueKind.Array)
                jaJoints = jjs.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
            else
                jaJoints = Array.Empty<string>();
            var jaOffPX = comp.TryGetProperty("offset_px", out var japx) ? japx.GetSingle() : 0f;
            var jaOffPY = comp.TryGetProperty("offset_py", out var japy) ? japy.GetSingle() : 0f;
            var jaOffPZ = comp.TryGetProperty("offset_pz", out var japz) ? japz.GetSingle() : 0f;
            var jaOffEX = comp.TryGetProperty("offset_ex", out var jaex) ? jaex.GetSingle() : 0f;
            var jaOffEY = comp.TryGetProperty("offset_ey", out var jaey) ? jaey.GetSingle() : 0f;
            var jaOffEZ = comp.TryGetProperty("offset_ez", out var jaez) ? jaez.GetSingle() : 0f;
            var jaOffSX = comp.TryGetProperty("offset_sx", out var jasx) ? jasx.GetSingle() : 1f;
            var jaOffSY = comp.TryGetProperty("offset_sy", out var jasy) ? jasy.GetSingle() : 1f;
            var jaOffSZ = comp.TryGetProperty("offset_sz", out var jasz) ? jasz.GetSingle() : 1f;
            // SkyboxComponent 用: テクスチャパス（assets:// 仮想パス）・追従モード・強度・ティント（リニア RGB 各成分）
            var skyboxTexturePath = comp.TryGetProperty("texture_path", out var sktp) ? sktp.GetString() ?? "" : "";
            var skyboxMode        = comp.TryGetProperty("mode",         out var skmd) ? skmd.GetString() ?? "camera_locked" : "camera_locked";
            var skyboxIntensity   = comp.TryGetProperty("intensity",    out var skin) ? skin.GetSingle() : 1f;
            var skyboxTintR       = comp.TryGetProperty("tr",           out var sktr) ? sktr.GetSingle() : 1f;
            var skyboxTintG       = comp.TryGetProperty("tg",           out var sktg) ? sktg.GetSingle() : 1f;
            var skyboxTintB       = comp.TryGetProperty("tb",           out var sktb) ? sktb.GetSingle() : 1f;
            // ParticleEmitterComponent 用: 形状・出現範囲・放出制御・寿命・速度・方向・物理・回転・
            // サイズ・テクスチャ・ブレンド・空間を受け取る。欠落時は Rust 側デフォルトと一致する
            // フォールバック値を用いる（カーブ JSON は今回未実装のカーブエディタでは未使用のため保持しない）。
            var peMaxParticles      = comp.TryGetProperty("max_particles",       out var pmp)  ? pmp.GetInt32()  : 1024;
            // 形状: 旧 "point" は "pixel" に改名済み。フォールバックも "pixel"。
            var peShape             = comp.TryGetProperty("shape",               out var psh)  ? psh.GetString() ?? "pixel" : "pixel";
            var peShapeModelPath    = comp.TryGetProperty("shape_model_path",    out var pshp) ? pshp.GetString() ?? "" : "";
            var peSpawnVolume       = comp.TryGetProperty("spawn_volume",        out var psv)  ? psv.GetString() ?? "point" : "point";
            var peSpawnBoxX         = comp.TryGetProperty("spawn_box_x",         out var psbx) ? psbx.GetSingle() : 0f;
            var peSpawnBoxY         = comp.TryGetProperty("spawn_box_y",         out var psby) ? psby.GetSingle() : 0f;
            var peSpawnBoxZ         = comp.TryGetProperty("spawn_box_z",         out var psbz) ? psbz.GetSingle() : 0f;
            var peSpawnSphereRadius = comp.TryGetProperty("spawn_sphere_radius", out var pssr) ? pssr.GetSingle() : 0f;
            var peEmitMode          = comp.TryGetProperty("emit_mode",           out var pem)  ? pem.GetString() ?? "loop" : "loop";
            var peEmitCountTotal    = comp.TryGetProperty("emit_count_total",    out var pect) ? pect.GetInt32()  : 0;
            var peInitialDelay      = comp.TryGetProperty("initial_delay",       out var pid)  ? pid.GetSingle()  : 0f;
            var pePrewarmTime       = comp.TryGetProperty("prewarm_time",        out var pwt)  ? pwt.GetSingle()  : 0f;
            var peEmitInterval      = comp.TryGetProperty("emit_interval",       out var pei)  ? pei.GetSingle()  : 0.05f;
            var peParticlesPerEmit  = comp.TryGetProperty("particles_per_emit",  out var ppe)  ? ppe.GetInt32()   : 1;
            var peLifetimeMin       = comp.TryGetProperty("lifetime_min",        out var plmn) ? plmn.GetSingle() : 1f;
            var peLifetimeMax       = comp.TryGetProperty("lifetime_max",        out var plmx) ? plmx.GetSingle() : 2f;
            var peSpeedMin          = comp.TryGetProperty("speed_min",           out var psmn) ? psmn.GetSingle() : 1f;
            var peSpeedMax          = comp.TryGetProperty("speed_max",           out var psmx) ? psmx.GetSingle() : 3f;
            var peDirX              = comp.TryGetProperty("dir_x",               out var pdx)  ? pdx.GetSingle()  : 0f;
            var peDirY              = comp.TryGetProperty("dir_y",               out var pdy)  ? pdy.GetSingle()  : 1f;
            var peDirZ              = comp.TryGetProperty("dir_z",               out var pdz)  ? pdz.GetSingle()  : 0f;
            var peDirectionRandomness = comp.TryGetProperty("direction_randomness", out var pdr) ? pdr.GetSingle() : 0f;
            var peGravityX          = comp.TryGetProperty("gravity_x",           out var pgx)  ? pgx.GetSingle()  : 0f;
            var peGravityY          = comp.TryGetProperty("gravity_y",           out var pgy)  ? pgy.GetSingle()  : -9.8f;
            var peGravityZ          = comp.TryGetProperty("gravity_z",           out var pgz)  ? pgz.GetSingle()  : 0f;
            var peDrag              = comp.TryGetProperty("drag",                out var pdg)  ? pdg.GetSingle()  : 0f;
            var peRotSpeedMin       = comp.TryGetProperty("rot_speed_min",       out var prsn) ? prsn.GetSingle() : 0f;
            var peRotSpeedMax       = comp.TryGetProperty("rot_speed_max",       out var prsx) ? prsx.GetSingle() : 0f;
            var peSizeMin           = comp.TryGetProperty("size_min",            out var pszn) ? pszn.GetSingle() : 1f;
            var peSizeMax           = comp.TryGetProperty("size_max",            out var pszx) ? pszx.GetSingle() : 1f;
            // 初期回転範囲（度）。
            var peInitRotMin        = comp.TryGetProperty("initial_rot_min",     out var pirn) ? pirn.GetSingle() : 0f;
            var peInitRotMax        = comp.TryGetProperty("initial_rot_max",     out var pirx) ? pirx.GetSingle() : 0f;
            var peBlend             = comp.TryGetProperty("blend",               out var pbl)  ? pbl.GetString() ?? "add" : "add";
            var peSimSpace          = comp.TryGetProperty("sim_space",           out var pss)  ? pss.GetString() ?? "world" : "world";
            var pePlaying           = comp.TryGetProperty("playing",             out var ppl)  ? ReadJsonBool(ppl, true) : true;
            // カーブ JSON（ParamCurve オブジェクトの生 JSON）を保持する。
            // 欠落耐性: プロパティが無い旧データでも空既定でフォールバックする。
            var peSpeedCurveJson         = comp.TryGetProperty("speed_curve",         out var pscv) ? pscv.GetRawText() : "{}";
            var peRotSpeedCurveJson      = comp.TryGetProperty("rot_speed_curve",     out var prcv) ? prcv.GetRawText() : "{}";
            var peScaleCurveJson         = comp.TryGetProperty("scale_curve",         out var pslv) ? pslv.GetRawText() : "{}";
            // 色カーブ配列（4ch HSVA ParamCurve の JSON 配列）。欠落時は空配列（UI 側で最低 1 本を生成する）。
            var peColorCurvesJson        = comp.TryGetProperty("color_curves",        out var pccv) ? pccv.GetRawText() : "[]";
            // テクスチャパス配列（JSON 文字列配列）。欠落時は空配列。
            var peTexturePathsJson       = comp.TryGetProperty("texture_paths",       out var ptps) ? ptps.GetRawText() : "[]";
            // WaterVolumeComponent 用: 種別・水面高さ・領域サイズ・色・透明度・フォーム・波・反射/屈折。
            // 種別のみ文字列で、それ以外はすべて float。欠落時は Rust 側既定値と一致する定数へフォールバックする。
            var waterKind         = comp.TryGetProperty("kind",             out var wkd)  ? wkd.GetString() ?? WaterKindDefault : WaterKindDefault;
            var waterSurfaceH     = comp.TryGetProperty("surface_height",   out var wsh)  ? wsh.GetSingle() : WaterSurfaceHeightDefault;
            var waterRegionHX     = comp.TryGetProperty("region_hx",        out var wrhx) ? wrhx.GetSingle() : WaterRegionHalfXDefault;
            var waterRegionHY     = comp.TryGetProperty("region_hy",        out var wrhy) ? wrhy.GetSingle() : WaterRegionHalfYDefault;
            var waterRegionHZ     = comp.TryGetProperty("region_hz",        out var wrhz) ? wrhz.GetSingle() : WaterRegionHalfZDefault;
            var waterOceanExtent  = comp.TryGetProperty("ocean_extent",     out var woe)  ? woe.GetSingle() : WaterOceanExtentDefault;
            var waterShallowR     = comp.TryGetProperty("shallow_r",        out var wsr)  ? wsr.GetSingle() : WaterShallowRDefault;
            var waterShallowG     = comp.TryGetProperty("shallow_g",        out var wsg)  ? wsg.GetSingle() : WaterShallowGDefault;
            var waterShallowB     = comp.TryGetProperty("shallow_b",        out var wsb)  ? wsb.GetSingle() : WaterShallowBDefault;
            var waterDeepR        = comp.TryGetProperty("deep_r",           out var wdr)  ? wdr.GetSingle() : WaterDeepRDefault;
            var waterDeepG        = comp.TryGetProperty("deep_g",           out var wdg)  ? wdg.GetSingle() : WaterDeepGDefault;
            var waterDeepB        = comp.TryGetProperty("deep_b",           out var wdb)  ? wdb.GetSingle() : WaterDeepBDefault;
            var waterAbsorption   = comp.TryGetProperty("absorption_distance", out var wab) ? wab.GetSingle() : WaterAbsorptionDistanceDefault;
            var waterOpacity      = comp.TryGetProperty("surface_opacity",  out var wso)  ? wso.GetSingle() : WaterSurfaceOpacityDefault;
            // 水面シェーディングアセット（Phase W8）: .wgsl の仮想パス。欠落/空文字はエンジン標準を意味する。
            var waterSurfaceShader = comp.TryGetProperty("surface_shader", out var wssh) ? wssh.GetString() ?? "" : "";
            // 水面シェーダのパラメータ注釈（Phase W8.2）。**解析はランタイム側が済ませてある**ので、
            // ここは生 JSON をそのまま持ち回り、UI 構築時に 1 度だけ読む。
            // 旧ランタイム（キー欠落）では空配列＝行なしになる。
            var waterShaderParams = comp.TryGetProperty("shader_params", out var wsp2) ? wsp2.GetRawText() : "[]";
            var waterFoamR        = comp.TryGetProperty("foam_r",           out var wfr)  ? wfr.GetSingle() : WaterFoamRDefault;
            var waterFoamG        = comp.TryGetProperty("foam_g",           out var wfg)  ? wfg.GetSingle() : WaterFoamGDefault;
            var waterFoamB        = comp.TryGetProperty("foam_b",           out var wfb)  ? wfb.GetSingle() : WaterFoamBDefault;
            var waterFoamWidth    = comp.TryGetProperty("foam_width",       out var wfw)  ? wfw.GetSingle() : WaterFoamWidthDefault;
            var waterFoamIntensity = comp.TryGetProperty("foam_intensity",  out var wfi)  ? wfi.GetSingle() : WaterFoamIntensityDefault;
            var waterWaveAmp      = comp.TryGetProperty("wave_amplitude",   out var wwa)  ? wwa.GetSingle() : WaterWaveAmplitudeDefault;
            var waterWaveScale    = comp.TryGetProperty("wave_scale",       out var wws)  ? wws.GetSingle() : WaterWaveScaleDefault;
            var waterWaveSpeed    = comp.TryGetProperty("wave_speed",       out var wwsp) ? wwsp.GetSingle() : WaterWaveSpeedDefault;
            var waterWaveDir      = comp.TryGetProperty("wave_direction_deg", out var wwd) ? wwd.GetSingle() : WaterWaveDirectionDegDefault;
            var waterFresnelPow   = comp.TryGetProperty("fresnel_power",    out var wfp)  ? wfp.GetSingle() : WaterFresnelPowerDefault;
            var waterFresnelStr   = comp.TryGetProperty("fresnel_strength", out var wfs)  ? wfs.GetSingle() : WaterFresnelStrengthDefault;
            // 反射（Phase W5.2）。旧シーン（フィールド欠落）でも既定値で表示できる。
            var waterReflIntensity = comp.TryGetProperty("reflection_intensity", out var wri) ? wri.GetSingle() : WaterReflectionIntensityDefault;
            var waterReflRoughness = comp.TryGetProperty("reflection_roughness", out var wrg) ? wrg.GetSingle() : WaterReflectionRoughnessDefault;
            var waterRefractDist  = comp.TryGetProperty("refraction_distortion", out var wrd) ? wrd.GetSingle() : WaterRefractionDistortionDefault;
            // 波紋・航跡（Phase I2）。旧シーン（フィールド欠落）でも既定値で表示できる。
            var waterRippleStr    = comp.TryGetProperty("ripple_strength", out var wrs) ? wrs.GetSingle() : WaterRippleStrengthDefault;
            var waterRippleFoamTh = comp.TryGetProperty("ripple_foam_threshold", out var wrft) ? wrft.GetSingle() : WaterRippleFoamThresholdDefault;
            // 水域ごとの物性（Phase I2.1）。旧シーン／旧ランタイム（フィールド欠落）でも既定値で表示できる。
            var waterViscosity    = comp.TryGetProperty("viscosity", out var wvis) ? wvis.GetSingle() : WaterViscosityDefault;
            var waterRippleDamp   = comp.TryGetProperty("ripple_damping", out var wrdm) ? wrdm.GetSingle() : WaterRippleDampingDefault;
            // 岸波（Phase W1.5）。旧シーン（フィールド欠落）でも既定値で表示できる。
            var waterShoreStr    = comp.TryGetProperty("shore_wave_strength", out var wss) ? wss.GetSingle() : WaterShoreStrengthDefault;
            var waterShoreLen    = comp.TryGetProperty("shore_wave_length",   out var wsl) ? wsl.GetSingle() : WaterShoreLengthDefault;
            var waterShorePeriod = comp.TryGetProperty("shore_wave_period",   out var wsp) ? wsp.GetSingle() : WaterShorePeriodDefault;
            var waterShoreFoam   = comp.TryGetProperty("shore_wave_foam",     out var wsf) ? wsf.GetSingle() : WaterShoreFoamDefault;
            // 川（スプライン、W4）: 川幅・流速・制御点リスト。制御点は "x,y,z;x,y,z;..." の文字列（アクタ相対座標）で、
            // 欠落/空文字列時は「制御点なし」として扱う（UI 側で 2 点未満の注意書きを出す）。
            var waterRiverWidth   = comp.TryGetProperty("river_width",   out var wrw)  ? wrw.GetSingle()  : WaterRiverWidthDefault;
            var waterFlowSpeed    = comp.TryGetProperty("flow_speed",    out var wflw) ? wflw.GetSingle() : WaterFlowSpeedDefault;
            var waterRiverDepth   = comp.TryGetProperty("river_depth",   out var wrdp) ? wrdp.GetSingle() : WaterRiverDepthDefault;
            var waterSplinePoints = comp.TryGetProperty("spline_points", out var wspl) ? wspl.GetString() ?? "" : "";
            // 川（W4.1）: 分割長と制御点参照。どちらも旧ランタイム／旧シーンでは欠落しうるので既定値へ落とす。
            var waterRiverSegLen  = comp.TryGetProperty("river_segment_length", out var wrsl) ? wrsl.GetSingle() : WaterRiverSegmentLengthDefault;
            var waterCpRef        = comp.TryGetProperty("control_point_ref",    out var wcpr) ? wcpr.GetString() ?? "" : "";
            // 水位シミュレーション（水位グラフ、Phase W2.5）フラグ。JSON bool（true/false）として送られる。
            var waterSimulateLevel = comp.TryGetProperty("simulate_level", out var wsimv) ? ReadJsonBool(wsimv, WaterSimulateLevelDefault) : WaterSimulateLevelDefault;
            // WaterLinkComponent 用（Phase W2.5）: 接続先アクタ名 2 本・開口の寸法・開閉率・流量係数。
            // 欠落時は Rust 側 WaterLinkComponentData の既定値と一致する定数へフォールバックする。
            var waterLinkVolumeA        = comp.TryGetProperty("volume_a",         out var wlva) ? wlva.GetString() ?? "" : "";
            var waterLinkVolumeB        = comp.TryGetProperty("volume_b",         out var wlvb) ? wlvb.GetString() ?? "" : "";
            var waterLinkOpeningBottom  = comp.TryGetProperty("opening_bottom",   out var wlob) ? wlob.GetSingle() : WaterLinkOpeningBottomDefault;
            var waterLinkOpeningHeight  = comp.TryGetProperty("opening_height",   out var wloh) ? wloh.GetSingle() : WaterLinkOpeningHeightDefault;
            var waterLinkOpeningWidth   = comp.TryGetProperty("opening_width",    out var wlow) ? wlow.GetSingle() : WaterLinkOpeningWidthDefault;
            var waterLinkOpenness       = comp.TryGetProperty("openness",         out var wlop) ? wlop.GetSingle() : WaterLinkOpennessDefault;
            var waterLinkFlowCoeff      = comp.TryGetProperty("flow_coefficient", out var wlfc) ? wlfc.GetSingle() : WaterLinkFlowCoefficientDefault;
            // InteractionSourceComponent 用（Phase I1）: 半径・強さ・有効フラグ。
            // 欠落時は Rust 側既定値と一致する定数へフォールバックする。
            var interactRadius   = comp.TryGetProperty("radius",   out var isr) ? isr.GetSingle()  : InteractionRadiusDefault;
            var interactStrength = comp.TryGetProperty("strength", out var iss) ? iss.GetSingle()  : InteractionStrengthDefault;
            // 【重要】キーは "source_enabled"。スロット共通の "enabled"(数値0/1)とキー重複
            // させると GetProperty が数値側を返し GetBoolean() が例外になる（既往リグレッション）。
            var interactEnabled  = comp.TryGetProperty("source_enabled", out var ise) ? ise.GetBoolean() : InteractionEnabledDefault;
            // ControlPointComponent 用（フェーズB）: 制御点配列を生 JSON のまま保持する。
            // 要素は {"position":[x,y,z],"rotation":[x,y,z],"time":t,"interp":"..."}。欠落時は空配列。
            var controlPointsJson = comp.TryGetProperty("points", out var cpp) ? cpp.GetRawText() : "[]";
            // CoverEmitterComponent 用（Phase I3.1）。欠落時は Rust 側既定値と一致する定数へ。
            var coverRangeKind  = comp.TryGetProperty("range_kind",  out var ck)  ? ck.GetString() ?? CoverRangeKindDefault : CoverRangeKindDefault;
            var coverExtentsX   = comp.TryGetProperty("extents_x",   out var cex) ? cex.GetSingle() : CoverExtentDefault;
            var coverExtentsY   = comp.TryGetProperty("extents_y",   out var cey) ? cey.GetSingle() : CoverExtentDefault;
            var coverExtentsZ   = comp.TryGetProperty("extents_z",   out var cez) ? cez.GetSingle() : CoverExtentDefault;
            var coverFade       = comp.TryGetProperty("fade",        out var cf)  ? cf.GetSingle()  : CoverFadeDefault;
            var coverMaskPath   = comp.TryGetProperty("mask_path",   out var cmp) ? cmp.GetString() ?? "" : "";
            var coverMaskSizeX  = comp.TryGetProperty("mask_size_x", out var cmx) ? cmx.GetSingle() : CoverMaskSizeDefault;
            var coverMaskSizeZ  = comp.TryGetProperty("mask_size_z", out var cmz) ? cmz.GetSingle() : CoverMaskSizeDefault;
            var coverMaterialId = comp.TryGetProperty("material_id", out var cmi) ? cmi.GetString() ?? CoverMaterialIdDefault : CoverMaterialIdDefault;
            // 【重要】強度のキーは "cover_strength"。InteractionSource の "strength" と
            // 同名にすると、同一スロットの JSON では衝突しないものの、
            // 読み違いの温床になるため型ごとに別名を割り当てている。
            var coverStrength   = comp.TryGetProperty("cover_strength", out var cs) ? cs.GetSingle() : CoverStrengthDefault;
            // 【重要】キーは "emitter_enabled"。スロット共通の "enabled"(数値0/1)と
            // キー重複させると GetProperty が数値側を返し GetBoolean() が例外になる。
            var coverEnabled    = comp.TryGetProperty("emitter_enabled", out var ce) ? ce.GetBoolean() : CoverEnabledDefault;

            var info = new SlotInfo(slotIdx, compName, compType, modelPath, width, height,
                AutoScale: autoScale,
                VpRefType: vpRefType, VpRefActor: vpRefActor, VpRefSlot: vpRefSlot,
                GravityMode: gravityMode,
                DrawZone: drawZone,
                AutoW: autoW, AutoH: autoH,
                Canvas3dPivotX: canvas3dPivotX, Canvas3dPivotY: canvas3dPivotY,
                TexturePath: texPath, SpriteR: sprR, SpriteG: sprG, SpriteB: sprB, SpriteA: sprA,
                SpriteW: sprW, SpriteH: sprH,
                SpriteLayer: sprLayer,
                PostFxPath: postFxPath,
                InputMapPath: inputMapPath,
                FovYDeg: fovYDeg, CamNear: camNear, CamFar: camFar, IsMain: isMain,
                CamCR: camCR, CamCG: camCG, CamCB: camCB, CamCA: camCA,
                CamScalingMode: camScalingMode, CamTargetW: camTargetW, CamTargetH: camTargetH,
                CamBarCR: camBarCR, CamBarCG: camBarCG, CamBarCB: camBarCB, CamBarCA: camBarCA,
                CamProjection: camProjection, CamOrthoHeight: camOrthoHeight,
                CamShadingAsset: camShadingAsset,
                CamShadingParamsJson: camShadingParams,
                PluginName: pluginName, PluginFieldsJson: pluginFieldsJson,
                ColliderDataJson: colliderDataJson, RigidbodyDataJson: rigidbodyDataJson,
                ScriptFieldsJson: scriptFieldsJson,
                AudioPath: audioPath, AudioVolume: audioVolume,
                AudioLoop: audioLoop, AudioPlayOnStart: audioPlayOnStart, AudioSpatial: audioSpatial,
                AudioMinDistance: audioMinDistance, AudioMaxDistance: audioMaxDistance,
                AudioPan: audioPan,
                AnimClipsJson: animClipsJson, AnimDefaultClip: animDefaultClip,
                AnimPlayOnStart: animPlayOnStart, AnimSpeed: animSpeed,
                LightKind: lightKind,
                LightR: lightR, LightG: lightG, LightB: lightB,
                LightIntensity: lightIntensity, LightRange: lightRange,
                LightInnerAngle: lightInnerAngle, LightOuterAngle: lightOuterAngle,
                LightRectWidth: lightRectWidth, LightRectHeight: lightRectHeight,
                LightCastShadows: lightCastShadows,
                LightSoftRadius: lightSoftRadius,
                LightBounceIntensity: lightBounce,
                ModelCastShadows: modelCastShadows,
                ModelRenderTag: modelRenderTag,
                JointName: jaJointName, Joints: jaJoints,
                JaOffPX: jaOffPX, JaOffPY: jaOffPY, JaOffPZ: jaOffPZ,
                JaOffEX: jaOffEX, JaOffEY: jaOffEY, JaOffEZ: jaOffEZ,
                JaOffSX: jaOffSX, JaOffSY: jaOffSY, JaOffSZ: jaOffSZ,
                SkyboxTexturePath: skyboxTexturePath, SkyboxMode: skyboxMode,
                SkyboxIntensity: skyboxIntensity,
                SkyboxTintR: skyboxTintR, SkyboxTintG: skyboxTintG, SkyboxTintB: skyboxTintB,
                PeMaxParticles: peMaxParticles,
                PeShape: peShape, PeShapeModelPath: peShapeModelPath,
                PeSpawnVolume: peSpawnVolume,
                PeSpawnBoxX: peSpawnBoxX, PeSpawnBoxY: peSpawnBoxY, PeSpawnBoxZ: peSpawnBoxZ,
                PeSpawnSphereRadius: peSpawnSphereRadius,
                PeEmitMode: peEmitMode, PeEmitCountTotal: peEmitCountTotal,
                PeInitialDelay: peInitialDelay, PePrewarmTime: pePrewarmTime,
                PeEmitInterval: peEmitInterval, PeParticlesPerEmit: peParticlesPerEmit,
                PeLifetimeMin: peLifetimeMin, PeLifetimeMax: peLifetimeMax,
                PeSpeedMin: peSpeedMin, PeSpeedMax: peSpeedMax,
                PeDirX: peDirX, PeDirY: peDirY, PeDirZ: peDirZ,
                PeDirectionRandomness: peDirectionRandomness,
                PeGravityX: peGravityX, PeGravityY: peGravityY, PeGravityZ: peGravityZ,
                PeDrag: peDrag,
                PeRotSpeedMin: peRotSpeedMin, PeRotSpeedMax: peRotSpeedMax,
                PeSizeMin: peSizeMin, PeSizeMax: peSizeMax,
                PeInitRotMin: peInitRotMin, PeInitRotMax: peInitRotMax,
                PeBlend: peBlend, PeSimSpace: peSimSpace,
                PePlaying: pePlaying,
                PeSpeedCurveJson: peSpeedCurveJson, PeRotSpeedCurveJson: peRotSpeedCurveJson,
                PeScaleCurveJson: peScaleCurveJson,
                PeColorCurvesJson: peColorCurvesJson,
                PeTexturePathsJson: peTexturePathsJson,
                // WaterVolumeComponent 用フィールド
                WaterKind: waterKind, WaterSurfaceHeight: waterSurfaceH,
                WaterRegionHX: waterRegionHX, WaterRegionHY: waterRegionHY, WaterRegionHZ: waterRegionHZ,
                WaterOceanExtent: waterOceanExtent,
                WaterShallowR: waterShallowR, WaterShallowG: waterShallowG, WaterShallowB: waterShallowB,
                WaterDeepR: waterDeepR, WaterDeepG: waterDeepG, WaterDeepB: waterDeepB,
                WaterAbsorptionDistance: waterAbsorption, WaterSurfaceOpacity: waterOpacity,
                WaterSurfaceShader: waterSurfaceShader,
                WaterShaderParamsJson: waterShaderParams,
                WaterFoamR: waterFoamR, WaterFoamG: waterFoamG, WaterFoamB: waterFoamB,
                WaterFoamWidth: waterFoamWidth, WaterFoamIntensity: waterFoamIntensity,
                WaterWaveAmplitude: waterWaveAmp, WaterWaveScale: waterWaveScale, WaterWaveSpeed: waterWaveSpeed,
                WaterWaveDirectionDeg: waterWaveDir,
                WaterFresnelPower: waterFresnelPow, WaterFresnelStrength: waterFresnelStr,
                WaterReflectionIntensity: waterReflIntensity,
                WaterReflectionRoughness: waterReflRoughness,
                WaterRefractionDistortion: waterRefractDist,
                WaterRippleStrength: waterRippleStr,
                WaterRippleFoamThreshold: waterRippleFoamTh,
                WaterViscosity: waterViscosity,
                WaterRippleDamping: waterRippleDamp,
                WaterShoreStrength: waterShoreStr, WaterShoreLength: waterShoreLen,
                WaterShorePeriod: waterShorePeriod, WaterShoreFoam: waterShoreFoam,
                WaterRiverWidth: waterRiverWidth, WaterFlowSpeed: waterFlowSpeed,
                WaterRiverDepth: waterRiverDepth,
                WaterSplinePoints: waterSplinePoints,
                WaterRiverSegmentLength: waterRiverSegLen,
                WaterControlPointRef: waterCpRef,
                WaterSimulateLevel: waterSimulateLevel,
                // WaterLinkComponent 用フィールド
                WaterLinkVolumeA: waterLinkVolumeA, WaterLinkVolumeB: waterLinkVolumeB,
                WaterLinkOpeningBottom: waterLinkOpeningBottom,
                WaterLinkOpeningHeight: waterLinkOpeningHeight,
                WaterLinkOpeningWidth: waterLinkOpeningWidth,
                WaterLinkOpenness: waterLinkOpenness,
                WaterLinkFlowCoefficient: waterLinkFlowCoeff,
                // InteractionSourceComponent 用フィールド
                InteractionRadius: interactRadius, InteractionStrength: interactStrength,
                InteractionEnabled: interactEnabled,
                // ControlPointComponent 用フィールド
                ControlPointsJson: controlPointsJson,
                // CoverEmitterComponent 用フィールド（I3.1）
                CoverRangeKind: coverRangeKind,
                CoverExtentsX: coverExtentsX, CoverExtentsY: coverExtentsY, CoverExtentsZ: coverExtentsZ,
                CoverFade: coverFade,
                CoverMaskPath: coverMaskPath,
                CoverMaskSizeX: coverMaskSizeX, CoverMaskSizeZ: coverMaskSizeZ,
                CoverMaterialId: coverMaterialId,
                CoverStrength: coverStrength,
                CoverEnabled: coverEnabled);
            _slotInfos.Add(info);

            // アコーディオンにパラメータ編集エリアを追加（ヘッダーがリネーム・削除・複製・選択を兼ねる）
            var propsContent = BuildSlotPropsContent(info);
            AccordionStack.Children.Add(BuildAccordionSection(info.Name, info.TypeId, propsContent, info.SlotIdx));

            if (slotIdx == prevSlot) _selectedSlotIdx = slotIdx;
        }

        // 前回未選択かつコンポーネントが存在する場合は最後のスロットを自動選択する
        if (_selectedSlotIdx < 0 && _slotInfos.Count > 0)
            _selectedSlotIdx = _slotInfos[^1].SlotIdx;

        RefreshAccordionSelection();

        // 複製直後: 新スロットを検出してデフォルト名でリネームモードを開始する
        if (_pendingDuplicateRename)
        {
            _pendingDuplicateRename = false;
            var newSlot = _slotInfos.FirstOrDefault(s => !prevSlotIdxSet.Contains(s.SlotIdx));
            if (newSlot != null)
            {
                var defaultName = ComputeDuplicateName(_pendingDuplicateBaseName, newSlot.SlotIdx);
                Dispatcher.BeginInvoke(
                    // currentName = runtime 上の実際の名前（例: "Sprite Copy"）
                    // initialText = 表示する候補名（例: "Sprite(1)"）
                    // → TextBox には "Sprite(1)" が表示され、変更なしで確定しても
                    //   "Sprite Copy" != "Sprite(1)" なので RENAME_COMPONENT_SLOT が送られる。
                    () => StartComponentRename(newSlot.SlotIdx, newSlot.Name, defaultName),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        // 川の spline_points からの移行待ち: ADD_COMPONENT の結果として現れた
        // ControlPointComponent スロットを見つけ、保留中の点列を変換して流し込み、
        // 最後に水スロットの control_point_ref へ自分自身のアクタ名を書いて結線する（W4.1）。
        if (_pendingSplineMigrationPoints is { Count: > 0 } migrationPoints)
        {
            if (_pendingSplineMigrationActorId != _currentActorId)
            {
                // 保留中に別アクタへ選択が移った ＝ この ACTOR_COMPONENTS は移行対象ではない。
                // 待ち続けても対象アクタへ戻ってくる保証が無いため、保留を破棄する
                //（無関係なアクタの ControlPoint を上書きする事故を防ぐ）。
                _pendingSplineMigrationPoints        = null;
                _pendingSplineMigrationActorId       = -1;
                _pendingSplineMigrationWaterSlotIdx  = -1;
            }
            // **今回新しく現れた** ControlPointComponent スロットだけを対象にする。
            // 単に「型が一致する最初のスロット」を選ぶと、押下前から ControlPoint が
            // 付いていたアクタで既存の点列を破壊してしまう（_pendingDuplicateRename と
            // 同じく prevSlotIdxSet で新旧を判別する）。
            else if (_slotInfos.FirstOrDefault(
                         s => s.TypeId == "ControlPointComponent" && !prevSlotIdxSet.Contains(s.SlotIdx))
                     is { } cpSlot)
            {
                var waterSlotIdx = _pendingSplineMigrationWaterSlotIdx;
                _pendingSplineMigrationPoints       = null;
                _pendingSplineMigrationActorId      = -1;
                _pendingSplineMigrationWaterSlotIdx = -1;
                // spline_points はアクタ相対 XYZ のみを持つため、回転 = 0、スケール = 1、
                // time = 0,1,2,... の連番、補間 = 既定（CatmullRom）で埋める。
                var converted = migrationPoints
                    .Select((p, i) => new ControlPointEntry
                    {
                        X = p.x, Y = p.y, Z = p.z,
                        Time   = i * SplineMigrationTimeStep,
                        Interp = ControlPointInterpDefault,
                    })
                    .ToList();
                _runtime?.SendToRuntime(
                    $"SET_CONTROL_POINTS:{_currentActorId},{cpSlot.SlotIdx},{SerializeControlPoints(converted)}");
                // 参照を張って初めて川が制御点を見る（W4.1 の明示参照方式）。
                // アクタ名が空だと解決できないので、その場合は参照を設定しない
                //（ユーザーがアクタ名を付けてから D&D で結線できる）。
                if (waterSlotIdx >= 0 && !string.IsNullOrEmpty(_currentActorName))
                {
                    _runtime?.SendToRuntime(
                        $"SET_WATER_FIELD:{_currentActorId},{waterSlotIdx},control_point_ref,{_currentActorName}");
                }
            }
        }
    }

    /// <summary>
    /// 複製時のデフォルト名を計算する。"baseName(1)"、"baseName(2)" の形式で既存名と重複しない最小番号を返す。
    /// excludeSlotIdx の名前は比較対象から除外する（複製直後は元と同名の場合があるため）。
    /// </summary>
    private string ComputeDuplicateName(string baseName, int excludeSlotIdx)
    {
        var existing = _slotInfos
            .Where(s => s.SlotIdx != excludeSlotIdx)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.Ordinal);
        for (int n = 1; ; n++)
        {
            var candidate = $"{baseName}({n})";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    /// <summary>コンポーネント種別 ID からヘッダー背景色を返す。基本情報はニュートラルグレー。</summary>
    private static Color GetTypeHeaderColor(string typeId) => typeId switch
    {
        "ModelComponent"      => Color.FromRgb(0x1C, 0x32, 0x1C), // 暗緑
        "CanvasComponent"     => Color.FromRgb(0x18, 0x28, 0x3C), // 暗青
        "SpriteComponent"     => Color.FromRgb(0x28, 0x18, 0x38), // 暗紫
        "InputMapComponent"   => Color.FromRgb(0x38, 0x26, 0x12), // 暗橙
        "CameraComponent"     => Color.FromRgb(0x12, 0x30, 0x38), // 暗シアン
        "ColliderComponent"   => Color.FromRgb(0x38, 0x16, 0x16), // 暗赤
        "Collider2dComponent" => Color.FromRgb(0x38, 0x16, 0x16), // 暗赤
        "ScriptComponent"     => Color.FromRgb(0x20, 0x34, 0x20), // 暗緑（スクリプト）
        "AudioComponent"      => Color.FromRgb(0x12, 0x2C, 0x34), // 暗青緑（オーディオ）
        "AnimatorComponent"   => Color.FromRgb(0x2C, 0x20, 0x38), // 暗紫（アニメーション）
        "LightComponent"      => Color.FromRgb(0x3A, 0x32, 0x10), // 暗黄橙（ライト）
        "JointAttachComponent" => Color.FromRgb(0x30, 0x10, 0x2C), // 暗マゼンタ（ジョイントアタッチ。ライトと区別しやすい色）
        "SkyboxComponent"     => Color.FromRgb(0x10, 0x1C, 0x38), // 暗青（スカイボックス）
        "WaterVolumeComponent" => Color.FromRgb(0x0E, 0x2A, 0x3A), // 暗い青（水）
        "WaterLinkComponent"  => Color.FromRgb(0x14, 0x22, 0x3A), // 暗い青紫（水の暗青と区別できるトーン。開口＝リンクの意味合い）
        "InteractionSourceComponent" => Color.FromRgb(0x18, 0x30, 0x18), // 暗い緑（草・環境への干渉）
        "CoverEmitterComponent" => Color.FromRgb(0x2E, 0x2E, 0x38), // 明るめの灰青（雪・地表を覆うもの）
        "ControlPointComponent" => Color.FromRgb(0x24, 0x1E, 0x38), // 暗い紫（汎用パス。水の暗青・草の暗緑と識別できる色）
        "PluginComponent"     => Color.FromRgb(0x34, 0x2C, 0x12), // 暗黄
        _                     => Color.FromRgb(0x2A, 0x2A, 0x2A), // ニュートラル（基本情報）
    };

    /// <summary>コンポーネント種別 ID から表示ラベルを返す。</summary>
    private static string GetTypeDisplayLabel(string typeId) => typeId switch
    {
        "ModelComponent"      => "Model",
        "CanvasComponent"     => "Canvas",
        "SpriteComponent"     => "Sprite",
        "InputMapComponent"   => "InputMap",
        "CameraComponent"     => "Camera",
        "ColliderComponent"   => "Collider",
        "Collider2dComponent" => "Collider 2D",
        "ScriptComponent"     => "Script",
        "AudioComponent"      => "Audio Source",
        "AnimatorComponent"   => "Animator",
        "LightComponent"      => "Light",
        "JointAttachComponent" => "JointAttach",
        "SkyboxComponent"     => "Skybox",
        "WaterVolumeComponent" => "Water Volume",
        "WaterLinkComponent"  => "Water Link",
        "InteractionSourceComponent" => "Interaction Source",
        "CoverEmitterComponent" => "Cover Emitter",
        "ControlPointComponent" => "Control Point",
        "PluginComponent"     => "Plugin",
        _ when typeId.StartsWith("Plugin:", StringComparison.Ordinal) => typeId["Plugin:".Length..],
        _                     => typeId,
    };

    /// <summary>
    /// アコーディオンセクションを生成する。
    /// ヘッダー行（▼/▶ + アイコン + タイトル + 削除×）と折り畳み可能なコンテンツエリアを持つ。
    /// コンポーネント一覧廃止に伴い、選択・リネーム（タイトルのダブルクリック）・削除（×ボタン）・
    /// 複製（右クリックメニュー）はすべてこのヘッダーが担う。slotIdx が -1 の場合は基本情報セクションで、
    /// これらの操作は無効（isComponentSlot=false）。
    /// </summary>
    private StackPanel BuildAccordionSection(string title, string typeId, UIElement content, int slotIdx)
    {
        // ── コンテナ（ヘッダー + コンテンツ）────────────────────
        var container = new StackPanel { Tag = slotIdx };

        // ── ヘッダー（コンポーネント種別に応じた有彩色背景）────────
        var isComponentSlot = slotIdx >= 0 && !string.IsNullOrEmpty(typeId);
        var headerBgColor   = isComponentSlot ? GetTypeHeaderColor(typeId) : Color.FromRgb(0x2A, 0x2A, 0x2A);

        // 選択ハイライト用のデフォルト/選択時の枠線（コンポーネント一覧のチップと同じ配色を踏襲）
        var defaultBorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
        var selectedBorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF));
        var defaultBorderThickness = new Thickness(0, 0, 0, 1);
        var selectedBorderThickness = new Thickness(2);

        var header = new Border
        {
            Background      = new SolidColorBrush(headerBgColor),
            BorderBrush     = isComponentSlot && slotIdx == _selectedSlotIdx ? selectedBorderBrush : defaultBorderBrush,
            BorderThickness = isComponentSlot && slotIdx == _selectedSlotIdx ? selectedBorderThickness : defaultBorderThickness,
            Padding         = new Thickness(6, 6, 6, 6),
            Cursor          = Cursors.Hand,
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // 矢印
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // 有効チェックボックス
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // アイコン
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // タイトル
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // 削除×ボタン

        // 矢印（展開/折り畳みインジケータ）
        var arrow = new TextBlock
        {
            Text              = "▼",
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize          = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 6, 0),
        };
        Grid.SetColumn(arrow, 0);
        headerGrid.Children.Add(arrow);

        // 種別アイコン（コンポーネントスロットのみ）
        if (isComponentSlot)
        {
            var icon = new TextBlock
            {
                Text              = typeId == "ModelComponent" ? "◈" : "⬡",
                Foreground        = new SolidColorBrush(Color.FromRgb(0x55, 0xAA, 0xFF)),
                FontSize          = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 4, 0),
            };
            Grid.SetColumn(icon, 2);
            headerGrid.Children.Add(icon);
        }

        // タイトル: "コンポーネント名 - 種別" 形式（種別部分は薄い色で表示）
        var titleBlock = new TextBlock
        {
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleBlock.Inlines.Add(new Run(title)
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
        });
        if (isComponentSlot)
        {
            titleBlock.Inlines.Add(new Run($" - {GetTypeDisplayLabel(typeId)}")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize   = 10,
            });
        }
        Grid.SetColumn(titleBlock, 3);
        headerGrid.Children.Add(titleBlock);

        // ── 有効チェックボックス（Unity の enabled 相当。コンポーネントスロットのみ）──
        // CheckBox はマウスダウンを自身で処理するため、ヘッダーの折り畳みトグルとは干渉しない。
        if (isComponentSlot)
        {
            var slotEnabled = !_slotEnabledMap.TryGetValue(slotIdx, out var en0) || en0;
            titleBlock.Opacity = slotEnabled ? 1.0 : 0.5;
            var enableChk = new CheckBox
            {
                IsChecked         = slotEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0),
                ToolTip           = "コンポーネントの有効/無効（OFF で描画・実行・物理の対象外）",
            };
            void SendEnabled(bool on)
            {
                _slotEnabledMap[slotIdx] = on;
                titleBlock.Opacity = on ? 1.0 : 0.5;
                _runtime?.SendToRuntime($"SET_SLOT_ENABLED:{_currentActorId},{slotIdx},{(on ? 1 : 0)}");
            }
            enableChk.Checked   += (_, _) => SendEnabled(true);
            enableChk.Unchecked += (_, _) => SendEnabled(false);
            Grid.SetColumn(enableChk, 1);
            headerGrid.Children.Add(enableChk);
        }

        // ── 削除×ボタン（旧コンポーネント一覧チップの削除機能を移設。コンポーネントスロットのみ）──
        if (isComponentSlot)
        {
            var removeBtn = new TextBlock
            {
                Text              = "✕",
                Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontSize          = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor            = Cursors.Hand,
                Padding           = new Thickness(6, 0, 0, 0),
                ToolTip           = "コンポーネントを削除",
            };
            removeBtn.MouseEnter += (_, _) =>
                removeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66));
            removeBtn.MouseLeave += (_, _) =>
                removeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            removeBtn.MouseLeftButtonDown += (_, e) =>
            {
                // 開閉トグルへ伝播させない（削除操作を折り畳みと混同しないため）
                e.Handled = true;
                if (_currentActorId >= 0)
                    _runtime?.SendToRuntime($"REMOVE_COMPONENT:{_currentActorId},{slotIdx}");
            };
            Grid.SetColumn(removeBtn, 4);
            headerGrid.Children.Add(removeBtn);
        }

        header.Child = headerGrid;

        // ── コンテンツラッパー（左インデント）────────────────────
        var contentWrapper = new Border
        {
            Padding         = new Thickness(16, 0, 0, 4),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child           = content,
        };

        // ── 開閉状態（値編集による UI 再構築をまたいで復元する）──────
        // キーはスロット添字（構造上の識別子）を優先する。コンポーネント名は
        // リネームで変わるため、名前をキーにすると開閉状態が失われてしまう。
        // スロットを持たない固定セクション（基本情報など）は見出し名で識別する。
        var expandKey = slotIdx >= 0
            ? ExpandKeyComponentPrefix + slotIdx
            : ExpandKeySectionPrefix + title;
        // 既定は「開」（従来挙動を維持。コンポーネントは開いた状態で見せる）。
        var toggle = _expandStates.TrackCustom(expandKey, defaultExpanded: true, expanded =>
        {
            contentWrapper.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text                = expanded ? "▼" : "▶";
        });

        // ── ヘッダーのマウス操作（開閉トグル / リネーム / 参照ドラッグ元）──
        //
        // コンポーネント見出しは参照ピッカーへのドラッグ元でもあるため、開閉トグルは
        // 「押した瞬間」ではなく「ドラッグせずに離したとき」に行う。こうしないと
        // ドラッグを始めるたびにセクションが開閉してしまう。
        // 単クリックの体感は変わらない（押下→即離しでトグルされる）。
        var headerPressPoint = new Point();
        bool headerPressed   = false;

        header.MouseLeftButtonDown += (_, e) =>
        {
            // ダブルクリックは 1 クリック目の Up で走った開閉トグルを打ち消してから
            // リネームを開始する（開閉とリネームが同時発火しないようにするため）。
            if (isComponentSlot && e.ClickCount == 2)
            {
                headerPressed = false;
                toggle.Toggle();
                StartComponentRename(slotIdx, title);
                e.Handled = true;
                return;
            }

            if (isComponentSlot)
            {
                _selectedSlotIdx = slotIdx;
                RefreshAccordionSelection();
            }

            headerPressPoint = e.GetPosition(header);
            headerPressed    = true;
        };

        header.MouseLeftButtonUp += (_, _) =>
        {
            if (!headerPressed) return;
            headerPressed = false;
            toggle.Toggle();
        };

        // コンポーネントスロットのヘッダーは、参照ピッカーへドラッグして
        // 「このコンポーネント（またはその所有アクタ）」を参照に設定できる。
        if (isComponentSlot)
        {
            header.MouseMove += (_, e) =>
            {
                if (!headerPressed || e.LeftButton != MouseButtonState.Pressed) return;
                var diff = e.GetPosition(header) - headerPressPoint;
                if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

                headerPressed = false;   // ドラッグに移行したので離しても開閉しない
                StartComponentReferenceDrag(header, slotIdx, title, typeId);
            };
        }

        // 右クリックで削除・複製・リネーム・コピー/貼り付けの操作メニューを表示する
        if (isComponentSlot)
        {
            header.MouseRightButtonDown += (_, e) =>
            {
                _selectedSlotIdx = slotIdx;
                RefreshAccordionSelection();
                ShowComponentContextMenu(header, slotIdx, title);
                e.Handled = true;
            };
        }

        container.Children.Add(header);
        container.Children.Add(contentWrapper);

        // ヘッダー要素を辞書へ登録し、外部（複製直後の自動リネーム等）から参照できるようにする
        if (isComponentSlot)
            _accordionHeaders[slotIdx] = new AccordionHeaderRefs
            {
                Header = header, HeaderGrid = headerGrid, TitleBlock = titleBlock, ComponentName = title,
            };

        return container;
    }

    /// <summary>スロット情報から コンポーネントプロパティ UI を生成して返す。</summary>
    private UIElement BuildSlotPropsContent(SlotInfo info) =>
        info.TypeId switch
        {
            "ModelComponent"    => BuildModelSlotContent(info),
            "ScriptComponent"   => BuildScriptSlotContent(info),
            "CanvasComponent"    => BuildCanvasSlotContent(info),
            "SpriteComponent"    => BuildSpriteSlotContent(info),
            "InputMapComponent"  => BuildInputMapSlotContent(info),
            "CameraComponent"    => BuildCameraSlotContent(info),
            "AudioComponent"     => BuildAudioSlotContent(info),
            "AnimatorComponent"  => BuildAnimatorSlotContent(info),
            "LightComponent"     => BuildLightSlotContent(info),
            "JointAttachComponent" => BuildJointAttachSlotContent(info),
            "SkyboxComponent"    => BuildSkyboxSlotContent(info),
            "ParticleEmitterComponent" => BuildParticleSlotContent(info),
            "WaterVolumeComponent" => BuildWaterVolumeSlotContent(info),
            "WaterLinkComponent"  => BuildWaterLinkSlotContent(info),
            "InteractionSourceComponent" => BuildInteractionSourceSlotContent(info),
            "CoverEmitterComponent" => BuildCoverEmitterSlotContent(info),
            "ControlPointComponent" => BuildControlPointSlotContent(info),
            "PluginComponent"    => BuildPluginSlotContent(info),
            "ColliderComponent"  => BuildColliderSlotContent(info),
            "Collider2dComponent" => BuildCollider2dSlotContent(info),
            _ => new TextBlock
            {
                Text       = $"未対応のコンポーネント: {info.TypeId}",
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize   = 11,
                Margin     = new Thickness(0, 4, 0, 4),
            },
        };

    // ── PluginComponent inspector ─────────────────────────────

    /// <summary>
    /// PluginComponent のインスペクター UI を動的に構築して返す。
    /// plugin_fields JSON をパースしてフィールド種別ごとに適切な入力 UI を生成する。
    /// 値変更時は SET_PLUGIN_FIELD:{actor},{slot},{key},{value} を送信する。
    /// </summary>
    private UIElement BuildPluginSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // プラグイン名の表示（読み取り専用ヘッダー）
        sp.Children.Add(new TextBlock
        {
            Text       = $"Plugin: {info.PluginName}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xAA, 0xFF)),
            FontSize   = 11,
            Margin     = new Thickness(0, 0, 0, 8),
        });

        // plugin_fields JSON をパースしてフィールド行を生成する
        List<JsonElement> fields;
        try
        {
            using var doc = JsonDocument.Parse(info.PluginFieldsJson);
            fields = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch
        {
            sp.Children.Add(new TextBlock
            {
                Text       = "フィールド定義の読み込みに失敗しました",
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0x44, 0x44)),
                FontSize   = 11,
            });
            return sp;
        }

        if (fields.Count == 0)
        {
            sp.Children.Add(new TextBlock
            {
                Text       = "（フィールドなし）",
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                FontSize   = 11,
            });
            return sp;
        }

        foreach (var field in fields)
        {
            var key          = field.TryGetProperty("key",           out var k)   ? k.GetString()   ?? "" : "";
            var label        = field.TryGetProperty("label",         out var lb)  ? lb.GetString()  ?? "" : key;
            var currentValue = field.TryGetProperty("current_value", out var cv)  ? cv.GetString()  ?? "" : "";
            var tooltip      = field.TryGetProperty("tooltip",       out var tt)  ? tt.GetString()  ?? "" : "";

            // フィールド種別を取得する（kind.type）
            var kindType = "";
            JsonElement kindParams = default;
            if (field.TryGetProperty("kind", out var kindEl))
            {
                if (kindEl.TryGetProperty("type",   out var kt)) kindType   = kt.GetString() ?? "";
                kindEl.TryGetProperty("params", out kindParams);
            }

            // フィールド行を生成してアタッチする
            var row = BuildPluginFieldRow(info, key, label, kindType, kindParams, currentValue, tooltip);
            sp.Children.Add(row);
        }

        return sp;
    }

    /// <summary>
    /// プラグインフィールド 1 行分の UI を生成して返す。
    /// kindType に応じてテキストボックス・チェックボックス・コンボボックス等を生成する。
    /// </summary>
    private UIElement BuildPluginFieldRow(
        SlotInfo    info,
        string      key,
        string      label,
        string      kindType,
        JsonElement kindParams,
        string      currentValue,
        string      tooltip)
    {
        // ラベル列（固定幅） + コントロール列（可変）の 2 カラムグリッド
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = string.IsNullOrEmpty(tooltip) ? null : tooltip,
        };
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        // フィールド種別ごとに入力コントロールを生成する
        UIElement control = kindType switch
        {
            "Float"    => BuildPluginFloatField(info, key, kindParams, currentValue),
            "Int"      => BuildPluginIntField(info, key, kindParams, currentValue),
            "Bool"     => BuildPluginBoolField(info, key, currentValue),
            "Color"    => BuildPluginColorField(info, key, currentValue),
            "FilePath" => BuildPluginFilePathField(info, key, kindParams, currentValue),
            "Enum"     => BuildPluginEnumField(info, key, kindParams, currentValue),
            _          => BuildPluginStringField(info, key, currentValue), // String or unknown
        };
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        return grid;
    }

    /// <summary>プラグインフィールド値変更を Rust へ送信するヘルパー。</summary>
    private void SendPluginFieldChange(SlotInfo info, string key, string value)
        => _runtime?.SendToRuntime($"SET_PLUGIN_FIELD:{_currentActorId},{info.SlotIdx},{key},{value}");

    /// <summary>Float フィールド: ドラッグ可能な数値入力。</summary>
    private UIElement BuildPluginFloatField(SlotInfo info, string key, JsonElement kindParams, string currentValue)
    {
        // min/max/step を取得する（未指定時はデフォルト値を使用）
        var min  = kindParams.ValueKind != JsonValueKind.Undefined && kindParams.TryGetProperty("min",  out var minEl)  ? (double)minEl.GetSingle()  : -1e9;
        var max  = kindParams.ValueKind != JsonValueKind.Undefined && kindParams.TryGetProperty("max",  out var maxEl)  ? (double)maxEl.GetSingle()  :  1e9;
        var step = kindParams.ValueKind != JsonValueKind.Undefined && kindParams.TryGetProperty("step", out var stepEl) ? (double)stepEl.GetSingle() :  0.1;

        float.TryParse(currentValue, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var initVal);

        var tb = new TextBox
        {
            Text   = initVal.ToString("G7", System.Globalization.CultureInfo.InvariantCulture),
            Style  = TryFindResource("NumericTextBox") as Style,
            Height = 22,
        };
        NumericDragBehavior.SetEnabled(tb, true);

        void CommitPluginFloat()
        {
            if (float.TryParse(tb.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                v       = (float)Math.Clamp(v, min, max);
                tb.Text = v.ToString("G7", System.Globalization.CultureInfo.InvariantCulture);
                SendPluginFieldChange(info, key, tb.Text);
            }
        }
        tb.LostFocus += (_, _) => CommitPluginFloat();
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return) { tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); e.Handled = true; }
        };
        NumericDragBehavior.SetOnDrag(tb, CommitPluginFloat);

        return tb;
    }

    /// <summary>Int フィールド: 整数値入力 TextBox。</summary>
    private UIElement BuildPluginIntField(SlotInfo info, string key, JsonElement kindParams, string currentValue)
    {
        var min = kindParams.ValueKind != JsonValueKind.Undefined && kindParams.TryGetProperty("min", out var minEl) ? minEl.GetInt32() : int.MinValue;
        var max = kindParams.ValueKind != JsonValueKind.Undefined && kindParams.TryGetProperty("max", out var maxEl) ? maxEl.GetInt32() : int.MaxValue;

        var tb = new TextBox
        {
            Text   = currentValue,
            Style  = TryFindResource("NumericTextBox") as Style,
            Height = 22,
        };
        void CommitPluginInt()
        {
            if (int.TryParse(tb.Text, out var v))
            {
                v       = Math.Clamp(v, min, max);
                tb.Text = v.ToString();
                SendPluginFieldChange(info, key, tb.Text);
            }
        }
        NumericDragBehavior.Attach(tb, sensitivity: 1.0, isInteger: true, onDrag: CommitPluginInt, min: min, max: max);
        tb.LostFocus += (_, _) => CommitPluginInt();
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return) { tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); e.Handled = true; }
        };
        return tb;
    }

    /// <summary>String フィールド: テキスト入力 TextBox。</summary>
    private UIElement BuildPluginStringField(SlotInfo info, string key, string currentValue)
    {
        var tb = new TextBox
        {
            Text   = currentValue,
            Style  = TryFindResource("NumericTextBox") as Style,
            Height = 22,
        };
        tb.LostFocus += (_, _) => SendPluginFieldChange(info, key, tb.Text);
        tb.KeyDown   += (_, e) =>
        {
            if (e.Key == Key.Return) { tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); e.Handled = true; }
        };
        return tb;
    }

    /// <summary>Bool フィールド: チェックボックス。</summary>
    private UIElement BuildPluginBoolField(SlotInfo info, string key, string currentValue)
    {
        var cb = new CheckBox
        {
            IsChecked         = currentValue == "true" || currentValue == "1",
            VerticalAlignment = VerticalAlignment.Center,
        };
        cb.Checked   += (_, _) => SendPluginFieldChange(info, key, "true");
        cb.Unchecked += (_, _) => SendPluginFieldChange(info, key, "false");
        return cb;
    }

    /// <summary>Color フィールド: RGBA カラーピッカーボタン。値は "r,g,b,a"（0.0〜1.0）形式。</summary>
    private UIElement BuildPluginColorField(SlotInfo info, string key, string currentValue)
    {
        // "r,g,b,a" をパースして初期カラーを設定する
        var parts = currentValue.Split(',');
        float TryParsePart(int i, float def)
        {
            if (i < parts.Length && float.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
            return def;
        }
        float r = TryParsePart(0, 1f), g = TryParsePart(1, 1f),
              b = TryParsePart(2, 1f), a = TryParsePart(3, 1f);

        // アルファはリニアのまま、RGB は sRGB 変換して WPF に渡す
        byte AlphaByte(float f) => (byte)Math.Clamp((int)(f * 255), 0, 255);
        var  initColor = Color.FromArgb(AlphaByte(a), LinearToSrgbByte(r), LinearToSrgbByte(g), LinearToSrgbByte(b));

        var btn = new Button
        {
            Height     = 22,
            Background = new SolidColorBrush(initColor),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            Cursor     = Cursors.Hand,
        };
        btn.Click += (_, _) =>
        {
            // ColorPickerWindow は静的 ShowDialog を使用する
            var owner = Window.GetWindow(btn);
            var result = ColorPickerWindow.ShowDialog(owner!, r, g, b, a);
            if (result.HasValue)
            {
                var (nr, ng, nb, na) = result.Value;
                byte AlphaByte2(float f) => (byte)Math.Clamp((int)(f * 255), 0, 255);
                btn.Background = new SolidColorBrush(Color.FromArgb(AlphaByte2(na), LinearToSrgbByte(nr), LinearToSrgbByte(ng), LinearToSrgbByte(nb)));
                var val = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:F4},{1:F4},{2:F4},{3:F4}", nr, ng, nb, na);
                SendPluginFieldChange(info, key, val);
                // ローカル変数を更新して次回ピッカー起動時の初期値として使う
                r = nr; g = ng; b = nb; a = na;
            }
        };
        return btn;
    }

    /// <summary>FilePath フィールド: TextBox + 参照ボタン。</summary>
    private UIElement BuildPluginFilePathField(SlotInfo info, string key, JsonElement kindParams, string currentValue)
    {
        var filter = kindParams.ValueKind != JsonValueKind.Undefined &&
                     kindParams.TryGetProperty("filter", out var fEl) ? fEl.GetString() ?? "*.*" : "*.*";

        var stack = new StackPanel { Orientation = Orientation.Horizontal };

        var tb = new TextBox
        {
            Text   = currentValue,
            Style  = TryFindResource("NumericTextBox") as Style,
            Height = 22,
            Width  = 160,
        };
        tb.LostFocus += (_, _) => SendPluginFieldChange(info, key, tb.Text);
        tb.KeyDown   += (_, e) =>
        {
            if (e.Key == Key.Return) { tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); e.Handled = true; }
        };
        stack.Children.Add(tb);

        var btn = new Button
        {
            Content = "...",
            Width   = 26,
            Height  = 22,
            Margin  = new Thickness(2, 0, 0, 0),
        };
        btn.Click += (_, _) =>
        {
            // "*.png;*.jpg" 形式のフィルタを OpenFileDialog 形式に変換する
            var dlgFilter = string.IsNullOrEmpty(filter) || filter == "*.*"
                ? "All Files (*.*)|*.*"
                : $"Files ({filter})|{filter}|All Files (*.*)|*.*";
            var dlg = new OpenFileDialog
            {
                Filter           = dlgFilter,
                InitialDirectory = Directory.Exists(_assetsPath) ? _assetsPath : Environment.CurrentDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                tb.Text = dlg.FileName;
                SendPluginFieldChange(info, key, tb.Text);
            }
        };
        stack.Children.Add(btn);

        return stack;
    }

    /// <summary>Enum フィールド: ドロップダウン。値はインデックス文字列（"0", "1", ...）で保存する。</summary>
    private UIElement BuildPluginEnumField(SlotInfo info, string key, JsonElement kindParams, string currentValue)
    {
        var options = new List<string>();
        if (kindParams.ValueKind != JsonValueKind.Undefined && kindParams.TryGetProperty("options", out var opts))
            foreach (var opt in opts.EnumerateArray())
                options.Add(opt.GetString() ?? "");

        // 配色はアプリ共通のダークテーマ暗黙スタイル（App.xaml）に任せる
        var combo = new ComboBox { Height = 22 };
        foreach (var opt in options)
            combo.Items.Add(new ComboBoxItem { Content = opt });

        int.TryParse(currentValue, out var selectedIdx);
        if (selectedIdx >= 0 && selectedIdx < options.Count)
            combo.SelectedIndex = selectedIdx;
        else if (options.Count > 0)
            combo.SelectedIndex = 0;

        combo.SelectionChanged += (_, _) =>
        {
            var idx = combo.SelectedIndex >= 0 ? combo.SelectedIndex : 0;
            SendPluginFieldChange(info, key, idx.ToString());
        };

        return combo;
    }

    // ── CameraComponent inspector ─────────────────────────────

    /// <summary>CameraComponent のインスペクター UI を構築して返す。</summary>
    private UIElement BuildCameraSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // ── 投影方式（透視 / 正射）────────────────────────────────────
        // 透視時は FOV、正射時は正射高さフィールドを表示切替する。
        var projectionModes = new[]
        {
            ("perspective",  "透視投影 (Perspective)"),
            ("orthographic", "正射投影 (Orthographic)"),
        };
        var projRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        projRow.Children.Add(new TextBlock
        {
            Text       = "投影方式",
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize   = 11,
            Width      = 90,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var projCombo = new ComboBox { Width = 170, FontSize = 11, Margin = new Thickness(4, 0, 0, 0) };
        foreach (var (val, label) in projectionModes)
            projCombo.Items.Add(new ComboBoxItem { Content = label, Tag = val });
        var curProjIdx = Array.FindIndex(projectionModes, t => t.Item1 == info.CamProjection);
        projCombo.SelectedIndex = curProjIdx >= 0 ? curProjIdx : 0;
        projRow.Children.Add(projCombo);
        sp.Children.Add(projRow);

        // ── 数値フィールド + SET_CAMERA_* 送信をまとめて構築 ──
        // 行の作り（値域が確定していればスライダー・無ければ数値入力）と
        // 「⟲ 既定値に戻す」は共通入口 BuildResettableFloatRow に一任する
        //（行ごとの個別対応・個別 Commit 配線を書かないため）。
        // command は SET コマンド名、serdeField は Rust の CameraComponentData のフィールド名で、
        // 両者は別体系なので必ず両方渡す（例: SET_CAMERA_FOV / fov_y_deg）。
        // 戻り値の行要素は投影方式による表示切替に使う。
        UIElement AddFloatRow(string label, float value, string command, string serdeField)
        {
            var row = BuildResettableFloatRow(
                info.SlotIdx, CameraComponentType, label, value, serdeField, CameraNumberFormat,
                v =>
                {
                    if (_currentActorId < 0) return;
                    _runtime?.SendToRuntime(FormattableString.Invariant(
                        $"{command}:{_currentActorId},{info.SlotIdx},{v}"));
                });
            sp.Children.Add(row);
            return row;
        }

        // FOV（垂直視野角）— 透視投影時のみ表示。値域 1..179 が値域表にあるためスライダーになる。
        var rowFov = AddFloatRow("FOV (垂直°)", info.FovYDeg, "SET_CAMERA_FOV", "fov_y_deg");

        // 正射高さ（縦・ワールド単位）— 正射投影時のみ表示
        var rowOrtho = AddFloatRow("正射高さ (縦)", info.CamOrthoHeight, "SET_CAMERA_ORTHO_HEIGHT", "ortho_height");

        // 投影方式に応じた FOV / 正射高さ フィールドの表示切替
        void UpdateProjectionVisibility(string proj)
        {
            bool ortho = proj == "orthographic";
            rowFov.Visibility   = ortho ? Visibility.Collapsed : Visibility.Visible;
            rowOrtho.Visibility = ortho ? Visibility.Visible   : Visibility.Collapsed;
        }
        UpdateProjectionVisibility(info.CamProjection);
        projCombo.SelectionChanged += (_, _) =>
        {
            if (projCombo.SelectedItem is ComboBoxItem item && item.Tag is string proj)
            {
                _runtime?.SendToRuntime($"SET_CAMERA_PROJECTION:{_currentActorId},{info.SlotIdx},{proj}");
                UpdateProjectionVisibility(proj);
            }
        };

        // ニアクリップ（Rust 側は `value.max(0.0001)` で下限のみ。上限が無いので数値行のまま）
        AddFloatRow("Near", info.CamNear, "SET_CAMERA_NEAR", "near");

        // ファークリップ（Rust 側は `value.max(near + 0.1)` で下限のみ。同上）
        AddFloatRow("Far", info.CamFar, "SET_CAMERA_FAR", "far");

        // メインカメラフラグ
        var mainRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        mainRow.Children.Add(new TextBlock
        {
            Text       = "Is Main",
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize   = 11,
            Width      = 90,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var mainCheck = new CheckBox
        {
            IsChecked         = info.IsMain,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
        };
        mainCheck.Checked   += (_, _) => _runtime?.SendToRuntime($"SET_CAMERA_MAIN:{_currentActorId},{info.SlotIdx},1");
        mainCheck.Unchecked += (_, _) => _runtime?.SendToRuntime($"SET_CAMERA_MAIN:{_currentActorId},{info.SlotIdx},0");
        mainRow.Children.Add(mainCheck);
        sp.Children.Add(mainRow);

        // クリアカラー
        float curR = info.CamCR, curG = info.CamCG, curB = info.CamCB, curA = info.CamCA;
        var colorSwatch = new Border
        {
            Width           = 120,
            Height          = 22,
            Margin          = new Thickness(0, 2, 0, 2),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            Background      = new SolidColorBrush(
                Color.FromArgb((byte)(curA * 255), LinearToSrgbByte(curR), LinearToSrgbByte(curG), LinearToSrgbByte(curB))),
            Cursor          = Cursors.Hand,
        };
        var clearRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        clearRow.Children.Add(new TextBlock
        {
            Text       = "クリアカラー",
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize   = 11,
            Width      = 90,
            VerticalAlignment = VerticalAlignment.Center,
        });
        clearRow.Children.Add(colorSwatch);
        colorSwatch.MouseLeftButtonDown += (_, _) =>
        {
            var result = ColorPickerWindow.ShowDialog(Window.GetWindow(this), curR, curG, curB, curA);
            if (result is null) return;
            (curR, curG, curB, curA) = result.Value;
            colorSwatch.Background = new SolidColorBrush(
                Color.FromArgb((byte)(curA * 255), LinearToSrgbByte(curR), LinearToSrgbByte(curG), LinearToSrgbByte(curB)));
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"SET_CAMERA_CLEAR_COLOR:{_currentActorId},{info.SlotIdx},{curR},{curG},{curB},{curA}"));
        };
        // 色は値域表の対象外（HDR 許容で上限が無い）だが、既定値へ戻す操作は数値行と同じく提供する。
        sp.Children.Add(WithFieldReset(clearRow, info.SlotIdx, CameraComponentType, "clear_color", "クリアカラー"));

        // スケーリングモード
        var scalingModes = new[]
        {
            ("vert_minus",          "Vert- (縦FOV固定)"),
            ("hor_plus",            "Hor+ (横FOV固定)"),
            ("letter_box",          "レターボックス (上下黒帯)"),
            ("pillar_box",          "ピラーボックス (左右黒帯)"),
            ("letter_pillar_box",   "レター+ピラー (自動帯)"),
            ("full_scale",          "フルスケール (伸縮)"),
        };
        var scalingRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        scalingRow.Children.Add(new TextBlock
        {
            Text       = "スケーリング",
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize   = 11,
            Width      = 90,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var scalingCombo = new ComboBox
        {
            Width    = 170,
            FontSize = 11,
            Margin   = new Thickness(4, 0, 0, 0),
        };
        foreach (var (val, label) in scalingModes)
            scalingCombo.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag     = val,
            });
        var currentScalingIdx = Array.FindIndex(scalingModes, t => t.Item1 == info.CamScalingMode);
        scalingCombo.SelectedIndex = currentScalingIdx >= 0 ? currentScalingIdx : 0;
        scalingCombo.SelectionChanged += (_, _) =>
        {
            if (scalingCombo.SelectedItem is ComboBoxItem item && item.Tag is string mode)
                _runtime?.SendToRuntime($"SET_CAMERA_SCALING_MODE:{_currentActorId},{info.SlotIdx},{mode}");
        };
        scalingRow.Children.Add(scalingCombo);
        sp.Children.Add(scalingRow);

        // ── アスペクト比 セクション ──────────────────────────────────
        // target_width / target_height は実質「アスペクト比」として扱われる
        //（値の比のみが意味を持つ。データ・serde は従来の解像度フィールドのまま維持）。
        const string AspectRatioTooltip =
            "アスペクト比は比として扱われます（16:9 でも 1920:1080 でも同じ意味）。\n" +
            "スケーリングモードのレターボックス等はこの比を基準に計算されます。";
        var aspectSep = new TextBlock
        {
            Text       = "アスペクト比",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize   = 10,
            Margin     = new Thickness(0, 6, 0, 2),
            ToolTip    = AspectRatioTooltip,
        };
        sp.Children.Add(aspectSep);

        // 横・縦の 2 値直入力 — 整数入力
        // "F0" フォーマット（小数点なし）を使うことで int.TryParse がそのまま使える。
        // デフォルトの "F1" だと "1920.0" のように表示され int.TryParse が失敗するため。
        var rowTW = BuildLabeledNumberRow("横", info.CamTargetW, "F0");
        NumericDragBehavior.Attach(rowTW.textBox, sensitivity: 1.0, isInteger: true);
        var rowTH = BuildLabeledNumberRow("縦", info.CamTargetH, "F0");
        NumericDragBehavior.Attach(rowTH.textBox, sensitivity: 1.0, isInteger: true);
        // 各入力行にも比としての意味をツールチップで明示する
        if (rowTW.element is FrameworkElement twEl) twEl.ToolTip = AspectRatioTooltip;
        if (rowTH.element is FrameworkElement thEl) thEl.ToolTip = AspectRatioTooltip;
        sp.Children.Add(rowTW.element);
        sp.Children.Add(rowTH.element);
        void CommitTargetSize()
        {
            if (_currentActorId < 0) return;
            // float.TryParse で受け付けることで、ドラッグ中など小数点が混入しても安全に処理する
            if (!float.TryParse(rowTW.textBox.Text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var wf) || wf < 1) return;
            if (!float.TryParse(rowTH.textBox.Text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var hf) || hf < 1) return;
            var w = Math.Max(1, (int)wf);
            var h = Math.Max(1, (int)hf);
            _runtime?.SendToRuntime($"SET_CAMERA_TARGET_SIZE:{_currentActorId},{info.SlotIdx},{w},{h}");
        }
        rowTW.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitTargetSize(); e.Handled = true; } };
        rowTW.textBox.LostFocus += (_, _) => CommitTargetSize();
        rowTH.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitTargetSize(); e.Handled = true; } };
        rowTH.textBox.LostFocus += (_, _) => CommitTargetSize();
        NumericDragBehavior.SetOnDrag(rowTW.textBox, CommitTargetSize); NumericDragBehavior.SetOnDrag(rowTH.textBox, CommitTargetSize);

        // 「ウィンドウアスペクト比を適用」ボタン:
        // プロジェクト設定のウィンドウ解像度（window_width / window_height）を
        // 横・縦フィールドへ設定し、そのまま SET_CAMERA_TARGET_SIZE を送信する。
        var btnApplyWindowAspect = new Button
        {
            Content             = "ウィンドウアスペクト比を適用",
            FontSize            = 11,
            Foreground          = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            Background          = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            BorderBrush         = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness     = new Thickness(1),
            Padding             = new Thickness(8, 3, 8, 3),
            Margin              = new Thickness(0, 2, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip             = "プロジェクト設定の解像度（ウィンドウサイズ）の値をアスペクト比として設定します。",
        };
        btnApplyWindowAspect.Click += (_, _) =>
        {
            // プロジェクト設定を読み込み、ウィンドウ解像度を横・縦フィールドへ反映する
            var settings = SEEDEditor.ProjectSettings.ProjectSettingsData.LoadFrom(
                System.IO.Path.Combine(MainWindow.AssetsPath, "project_settings.json"));
            rowTW.textBox.Text = settings.WindowWidth.ToString(CultureInfo.InvariantCulture);
            rowTH.textBox.Text = settings.WindowHeight.ToString(CultureInfo.InvariantCulture);
            CommitTargetSize();
        };
        sp.Children.Add(btnApplyWindowAspect);

        // ── 帯カラー（LetterBox / PillarBox 選択時のみ表示）──────────
        float curBarR = info.CamBarCR, curBarG = info.CamBarCG, curBarB = info.CamBarCB, curBarA = info.CamBarCA;
        var barColorSwatch = new Border
        {
            Width           = 120,
            Height          = 22,
            Margin          = new Thickness(0, 2, 0, 2),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            Background      = new SolidColorBrush(
                Color.FromArgb((byte)(curBarA * 255), LinearToSrgbByte(curBarR),
                               LinearToSrgbByte(curBarG), LinearToSrgbByte(curBarB))),
            Cursor = Cursors.Hand,
        };
        var barRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        barRow.Children.Add(new TextBlock
        {
            Text              = "帯カラー",
            Foreground        = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize          = 11,
            Width             = 90,
            VerticalAlignment = VerticalAlignment.Center,
        });
        barRow.Children.Add(barColorSwatch);
        barColorSwatch.MouseLeftButtonDown += (_, _) =>
        {
            var result = ColorPickerWindow.ShowDialog(Window.GetWindow(this), curBarR, curBarG, curBarB, curBarA);
            if (result is null) return;
            (curBarR, curBarG, curBarB, curBarA) = result.Value;
            barColorSwatch.Background = new SolidColorBrush(
                Color.FromArgb((byte)(curBarA * 255), LinearToSrgbByte(curBarR),
                               LinearToSrgbByte(curBarG), LinearToSrgbByte(curBarB)));
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"SET_CAMERA_BAR_COLOR:{_currentActorId},{info.SlotIdx},{curBarR},{curBarG},{curBarB},{curBarA}"));
        };
        // 色は値域表の対象外。⟲ だけ添える。
        // 表示切替は **包んだ側**（⟲ ボタンごと隠す）に掛けること。
        var barRowElement = WithFieldReset(barRow, info.SlotIdx, CameraComponentType, "bar_color", "帯カラー");
        sp.Children.Add(barRowElement);

        // LetterBox / PillarBox のときのみ帯カラー行を表示する
        static bool IsBarMode(string mode) => mode is "letter_box" or "pillar_box" or "letter_pillar_box";
        barRowElement.Visibility = IsBarMode(info.CamScalingMode) ? Visibility.Visible : Visibility.Collapsed;
        scalingCombo.SelectionChanged += (_, _) =>
        {
            if (scalingCombo.SelectedItem is ComboBoxItem item && item.Tag is string mode)
                barRowElement.Visibility = IsBarMode(mode) ? Visibility.Visible : Visibility.Collapsed;
        };

        // ── シェーディングアセット参照（.wgsl。テクスチャ参照と同じ FileRefBuilder + D&D の流儀）──
        // 未設定のときはシーン既定 → 組み込み標準 PBR へフォールバックする。
        // 空文字列 virtualPath を送るとランタイム側で未設定（None）に戻る。
        var shadingRow = FileRefBuilder.Build(
            "Shading", info.CamShadingAsset,
            [".wgsl"],
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "シェーディングアセット（WGSL）を選択",
                    Filter = "WGSL シェーダ|*.wgsl|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path =>
            {
                if (_currentActorId < 0) return;
                // 絶対パスを仮想パスに変換してからランタイムへ送信する
                var virtualPath = VirtualPath.ToVirtual(path, _assetsPath);
                _runtime?.SendToRuntime($"SET_CAMERA_SHADING_ASSET:{_currentActorId},{info.SlotIdx},{virtualPath}");
            },
            // 行末の「×」ボタンで指定を解除する（空パス送信で未設定へ戻る）。
            // 従来からある右クリック解除も残してある。
            () =>
            {
                if (_currentActorId < 0) return;
                _runtime?.SendToRuntime($"SET_CAMERA_SHADING_ASSET:{_currentActorId},{info.SlotIdx},");
            });
        // クリアボタンに加え、従来どおり右クリックでも未設定に戻せるようにする。
        if (shadingRow is FrameworkElement shadingFe)
        {
            shadingFe.ToolTip = "このカメラの描画に使う WGSL シェーディングアセット。\n" +
                                "未設定の場合はシーン既定 → 組み込み標準 PBR を使用します。\n" +
                                "「×」ボタンまたは右クリックで指定を解除";
            shadingFe.MouseRightButtonUp += (_, e) =>
            {
                if (_currentActorId < 0) return;
                // 空パスを送ると未設定（None）に戻る
                _runtime?.SendToRuntime($"SET_CAMERA_SHADING_ASSET:{_currentActorId},{info.SlotIdx},");
                e.Handled = true;
            };
        }
        sp.Children.Add(shadingRow);

        // ── アセットが宣言したパラメータ行（L3）──────────────────
        // 「シェーディングアセット」行の**直下**へ動的生成する（水面と同じ流儀）。
        // 行の形（カラーピッカー／スライダー／数値／参照バインド）はランタイムが送る
        // 宣言 JSON が決める。C# は構文解析を一切持たない。
        AddShaderParamRows(sp, new ShaderParamTarget(
            info.CamShadingParamsJson,
            () => $"SET_CAMERA_SHADING_PARAM:{_currentActorId},{info.SlotIdx},",
            () => $"RESET_CAMERA_SHADING_PARAM:{_currentActorId},{info.SlotIdx},",
            () => $"SET_CAMERA_SHADING_BINDING:{_currentActorId},{info.SlotIdx},",
            "シェーディングアセット",
            RequiresActorSelection: true));

        return sp;
    }

    // ── ColliderComponent inspector（リジッドボディ統合版）────────────

    /// <summary>
    /// ColliderComponent のインスペクター UI を構築して返す。
    /// 形状・オフセット・トリガー・レイヤーに加え、
    /// 「リジッドボディを使用」チェックをオンにすると物理演算パラメータが展開される。
    /// 値変更時は SET_COLLIDER_DATA:{actorId},{slotIdx},{json} を送信する。
    /// </summary>
    private UIElement BuildColliderSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // ── 現在の collider_data JSON をパース ──────────────────────────
        string shapeType  = "Box";
        float  heX = 0.5f, heY = 0.5f, heZ = 0.5f;
        float  radius     = 0.5f;
        float  halfHeight = 1.0f;
        float  offX = 0f, offY = 0f, offZ = 0f;
        bool   isTrigger  = false;
        int    physLayer  = 1;
        int    layerMask  = 0;
        // キャラクターコントローラー設定
        bool   isCharCtrl = false;
        // リジッドボディ設定
        bool   useRb      = false;
        float  mass = 1f, rest = 0.3f, fric = 0.5f, linDamp = 0.01f, angDamp = 0.05f, gravScale = 1f;
        bool   isKinematic = false;
        var    freezePos   = new bool[3];
        var    freezeRot   = new bool[3];
        var    initLinVel  = new float[3];
        var    initAngVel  = new float[3];

        try
        {
            using var doc = JsonDocument.Parse(info.ColliderDataJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("shape", out var shape))
            {
                shapeType = shape.TryGetProperty("type", out var t) ? t.GetString() ?? "Box" : "Box";
                if (shape.TryGetProperty("half_extents", out var he) && he.GetArrayLength() == 3)
                { heX = he[0].GetSingle(); heY = he[1].GetSingle(); heZ = he[2].GetSingle(); }
                if (shape.TryGetProperty("radius",      out var r))  radius     = r.GetSingle();
                if (shape.TryGetProperty("half_height", out var hh)) halfHeight = hh.GetSingle();
            }
            if (root.TryGetProperty("offset", out var off) && off.GetArrayLength() == 3)
            { offX = off[0].GetSingle(); offY = off[1].GetSingle(); offZ = off[2].GetSingle(); }
            if (root.TryGetProperty("is_trigger",    out var it2)) isTrigger = it2.GetBoolean();
            if (root.TryGetProperty("physics_layer", out var pl))  physLayer = pl.GetInt32();
            if (root.TryGetProperty("layer_mask",    out var lm))  layerMask = lm.GetInt32();
            // キャラクターコントローラー設定
            if (root.TryGetProperty("is_character_controller", out var cc)) isCharCtrl = cc.GetBoolean();
            // リジッドボディ設定
            if (root.TryGetProperty("use_rigidbody",   out var ur))  useRb       = ur.GetBoolean();
            if (root.TryGetProperty("mass",            out var mv))  mass        = mv.GetSingle();
            if (root.TryGetProperty("restitution",     out var rv2)) rest        = rv2.GetSingle();
            if (root.TryGetProperty("friction",        out var fv))  fric        = fv.GetSingle();
            if (root.TryGetProperty("linear_damping",  out var ld))  linDamp     = ld.GetSingle();
            if (root.TryGetProperty("angular_damping", out var ad))  angDamp     = ad.GetSingle();
            if (root.TryGetProperty("gravity_scale",   out var gs))  gravScale   = gs.GetSingle();
            if (root.TryGetProperty("is_kinematic",    out var ik))  isKinematic = ik.GetBoolean();
            ReadBoolArray(root,  "freeze_position",          freezePos);
            ReadBoolArray(root,  "freeze_rotation",          freezeRot);
            ReadFloatArray(root, "initial_linear_velocity",  initLinVel);
            ReadFloatArray(root, "initial_angular_velocity", initAngVel);
        }
        catch { /* デフォルト値を使用 */ }

        // ── 現在値（クロージャ共有） ─────────────────────────────────────
        var curShapeType  = shapeType;
        var curHe         = new float[] { heX, heY, heZ };
        var curRadius     = radius;
        var curHalfHeight = halfHeight;
        var curOffset     = new float[] { offX, offY, offZ };
        var curTrigger    = isTrigger;
        var curLayer      = physLayer;
        var curMask       = layerMask;
        var curUseRb      = useRb;
        var curMass       = mass;      var curRest  = rest;    var curFric    = fric;
        var curLinDamp    = linDamp;   var curAngD  = angDamp; var curGrav    = gravScale;
        var curKinem      = isKinematic;
        var curCharCtrl   = isCharCtrl;
        var curFreezeP    = (bool[])freezePos.Clone();
        var curFreezeR    = (bool[])freezeRot.Clone();
        var curLinV       = (float[])initLinVel.Clone();
        var curAngV       = (float[])initAngVel.Clone();

        // ── JSON 再構築 + 送信ヘルパー ───────────────────────────────────
        void CommitCollider()
        {
            if (_currentActorId < 0) return;
            static string F(float v) => v.ToString("G", CultureInfo.InvariantCulture);
            static string B(bool  v) => v ? "true" : "false";
            string shapeJson = curShapeType switch
            {
                "Sphere"   => $"{{\"type\":\"Sphere\",\"radius\":{F(curRadius)}}}",
                "Capsule"  => $"{{\"type\":\"Capsule\",\"radius\":{F(curRadius)},\"half_height\":{F(curHalfHeight)}}}",
                "Cylinder" => $"{{\"type\":\"Cylinder\",\"radius\":{F(curRadius)},\"half_height\":{F(curHalfHeight)}}}",
                "Cone"     => $"{{\"type\":\"Cone\",\"radius\":{F(curRadius)},\"half_height\":{F(curHalfHeight)}}}",
                _          => $"{{\"type\":\"Box\",\"half_extents\":[{F(curHe[0])},{F(curHe[1])},{F(curHe[2])}]}}",
            };
            var json =
                $"{{\"shape\":{shapeJson}," +
                $"\"offset\":[{F(curOffset[0])},{F(curOffset[1])},{F(curOffset[2])}]," +
                $"\"is_trigger\":{B(curTrigger)}," +
                $"\"physics_layer\":{curLayer}," +
                $"\"layer_mask\":{curMask}," +
                $"\"is_character_controller\":{B(curCharCtrl)}," +
                $"\"use_rigidbody\":{B(curUseRb)}," +
                $"\"mass\":{F(curMass)}," +
                $"\"restitution\":{F(curRest)}," +
                $"\"friction\":{F(curFric)}," +
                $"\"linear_damping\":{F(curLinDamp)}," +
                $"\"angular_damping\":{F(curAngD)}," +
                $"\"gravity_scale\":{F(curGrav)}," +
                $"\"is_kinematic\":{B(curKinem)}," +
                $"\"freeze_position\":[{B(curFreezeP[0])},{B(curFreezeP[1])},{B(curFreezeP[2])}]," +
                $"\"freeze_rotation\":[{B(curFreezeR[0])},{B(curFreezeR[1])},{B(curFreezeR[2])}]," +
                $"\"initial_linear_velocity\":[{F(curLinV[0])},{F(curLinV[1])},{F(curLinV[2])}]," +
                $"\"initial_angular_velocity\":[{F(curAngV[0])},{F(curAngV[1])},{F(curAngV[2])}]}}";
            _runtime?.SendToRuntime($"SET_COLLIDER_DATA:{_currentActorId},{info.SlotIdx},{json}");
        }

        // ────────────────────────────────────────────────────────────────
        // コライダー設定
        // ────────────────────────────────────────────────────────────────

        // --- 形状タイプ コンボボックス ---
        var shapeLabel = new TextBlock
        {
            Text = "形状", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        };
        var shapeCombo = new ComboBox
        {
            Width = 120, Height = 22, FontSize = 11, Margin = new Thickness(4, 0, 0, 0),
        };
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Box"      });
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Sphere"   });
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Capsule"  });
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Cylinder" });
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Cone"     });
        foreach (ComboBoxItem ci in shapeCombo.Items)
            if (ci.Content as string == curShapeType) { shapeCombo.SelectedItem = ci; break; }
        var shapeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        shapeRow.Children.Add(shapeLabel);
        shapeRow.Children.Add(shapeCombo);
        sp.Children.Add(shapeRow);

        // --- 形状別パラメータエリア（動的に差し替え） ---
        var shapeParamPanel = new StackPanel();
        sp.Children.Add(shapeParamPanel);

        void RebuildShapeParams()
        {
            shapeParamPanel.Children.Clear();
            if (curShapeType == "Box")
            {
                var row = BuildXYZRowSimple("半辺 (HalfExtents)", curHe[0], curHe[1], curHe[2]);
                shapeParamPanel.Children.Add(row.element);
                void CommitHe()
                {
                    if (!float.TryParse(row.tx.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return;
                    if (!float.TryParse(row.ty.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return;
                    if (!float.TryParse(row.tz.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return;
                    curHe[0] = x; curHe[1] = y; curHe[2] = z; CommitCollider();
                }
                row.tx.LostFocus += (_, _) => CommitHe(); row.tx.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitHe(); };
                row.ty.LostFocus += (_, _) => CommitHe(); row.ty.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitHe(); };
                row.tz.LostFocus += (_, _) => CommitHe(); row.tz.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitHe(); };
                NumericDragBehavior.SetOnDrag(row.tx, CommitHe); NumericDragBehavior.SetOnDrag(row.ty, CommitHe); NumericDragBehavior.SetOnDrag(row.tz, CommitHe);
            }
            else if (curShapeType == "Sphere")
            {
                var rowR = BuildLabeledNumberRow("半径 (m)", curRadius);
                shapeParamPanel.Children.Add(rowR.element);
                void CommitR() {
                    if (float.TryParse(rowR.textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    { curRadius = v; CommitCollider(); }
                }
                rowR.textBox.LostFocus += (_, _) => CommitR();
                rowR.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitR(); };
                NumericDragBehavior.SetOnDrag(rowR.textBox, CommitR);
            }
            else if (curShapeType is "Capsule" or "Cylinder" or "Cone")
            {
                // Capsule / Cylinder / Cone は半径 + 半高さ の 2 パラメータ
                var rowR  = BuildLabeledNumberRow("半径 (m)",   curRadius);
                var rowHH = BuildLabeledNumberRow("半高さ (m)", curHalfHeight);
                shapeParamPanel.Children.Add(rowR.element);
                shapeParamPanel.Children.Add(rowHH.element);
                void CommitRH() {
                    if (!float.TryParse(rowR.textBox.Text,  NumberStyles.Float, CultureInfo.InvariantCulture, out var r))  return;
                    if (!float.TryParse(rowHH.textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hh)) return;
                    curRadius = r; curHalfHeight = hh; CommitCollider();
                }
                rowR.textBox.LostFocus  += (_, _) => CommitRH(); rowR.textBox.KeyDown  += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitRH(); };
                rowHH.textBox.LostFocus += (_, _) => CommitRH(); rowHH.textBox.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitRH(); };
                NumericDragBehavior.SetOnDrag(rowR.textBox, CommitRH); NumericDragBehavior.SetOnDrag(rowHH.textBox, CommitRH);
            }
        }
        RebuildShapeParams();

        shapeCombo.SelectionChanged += (_, _) =>
        {
            curShapeType  = (shapeCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "Box";
            curHe         = new float[] { 0.5f, 0.5f, 0.5f };
            curRadius     = 0.5f;
            curHalfHeight = 1.0f;
            RebuildShapeParams();
            CommitCollider();
        };

        // --- オフセット ---
        var offRow = BuildXYZRowSimple("オフセット", curOffset[0], curOffset[1], curOffset[2]);
        sp.Children.Add(offRow.element);
        void CommitOff() {
            if (!float.TryParse(offRow.tx.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return;
            if (!float.TryParse(offRow.ty.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return;
            if (!float.TryParse(offRow.tz.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return;
            curOffset[0] = x; curOffset[1] = y; curOffset[2] = z; CommitCollider();
        }
        offRow.tx.LostFocus += (_, _) => CommitOff(); offRow.tx.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitOff(); };
        offRow.ty.LostFocus += (_, _) => CommitOff(); offRow.ty.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitOff(); };
        offRow.tz.LostFocus += (_, _) => CommitOff(); offRow.tz.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitOff(); };
        NumericDragBehavior.SetOnDrag(offRow.tx, CommitOff); NumericDragBehavior.SetOnDrag(offRow.ty, CommitOff); NumericDragBehavior.SetOnDrag(offRow.tz, CommitOff);

        // --- 押し戻し ---
        // 内部フィールド is_trigger (true=押し戻しなし) とは論理が逆。
        // チェックON = 押し戻す (is_trigger=false)、チェックOFF = 押し戻さない/trigger (is_trigger=true)
        var triggerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        triggerRow.Children.Add(new TextBlock
        {
            Text = "押し戻し", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        var triggerCheck = new CheckBox { IsChecked = !curTrigger, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        triggerRow.Children.Add(triggerCheck);
        sp.Children.Add(triggerRow);

        // --- 物理レイヤー ---
        var layerRow = BuildLabeledIntRow("物理レイヤー", curLayer, min: 0);
        sp.Children.Add(layerRow.element);
        void CommitLayer() { if (int.TryParse(layerRow.textBox.Text, out var v)) { curLayer = Math.Max(0, v); CommitCollider(); } }
        layerRow.textBox.LostFocus += (_, _) => CommitLayer();
        layerRow.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitLayer(); };
        NumericDragBehavior.SetOnDrag(layerRow.textBox, CommitLayer);

        // --- レイヤーマスク ---
        var maskRow = BuildLabeledIntRow("レイヤーマスク", curMask, min: 0);
        sp.Children.Add(maskRow.element);
        void CommitMask() { if (int.TryParse(maskRow.textBox.Text, out var v)) { curMask = Math.Max(0, v); CommitCollider(); } }
        maskRow.textBox.LostFocus += (_, _) => CommitMask();
        maskRow.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitMask(); };
        NumericDragBehavior.SetOnDrag(maskRow.textBox, CommitMask);

        // --- キャラクターコントローラー ---
        // ON にすると、スクリプトが Transform.Position を書き換えるだけで地形に衝突解決され、
        // めり込んだぶんが物理ステップ同期（60Hz）で自動的に押し戻される。
        // 瞬間移動（衝突無視）は Transform.Teleport()、接地判定は Physics.IsGrounded() を使う。
        var charCtrlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        charCtrlRow.Children.Add(new TextBlock
        {
            Text = "キャラクターコントローラー", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Width = 130, VerticalAlignment = VerticalAlignment.Center,
        });
        var charCtrlCheck = new CheckBox { IsChecked = curCharCtrl, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        charCtrlCheck.Checked   += (_, _) => { curCharCtrl = true;  CommitCollider(); };
        charCtrlCheck.Unchecked += (_, _) => { curCharCtrl = false; CommitCollider(); };
        charCtrlRow.Children.Add(charCtrlCheck);
        sp.Children.Add(charCtrlRow);

        // ────────────────────────────────────────────────────────────────
        // リジッドボディ設定（「リジッドボディを使用」チェックで展開）
        // is_trigger=true（押し戻しなし）の場合は RigidBody は使用できないため
        // セクション全体を非表示にする。
        // ────────────────────────────────────────────────────────────────

        // RigidBody セクション全体コンテナ（trigger 状態に連動して表示/非表示を切り替え）
        var rbSectionContainer = new StackPanel
        {
            Visibility = curTrigger ? Visibility.Collapsed : Visibility.Visible,
        };
        sp.Children.Add(rbSectionContainer);

        // trigger チェックボックスのハンドラをここで登録（rbSectionContainer 参照が確定した後）
        triggerCheck.Checked   += (_, _) => { curTrigger = false; rbSectionContainer.Visibility = Visibility.Visible;   CommitCollider(); };
        triggerCheck.Unchecked += (_, _) => { curTrigger = true;  rbSectionContainer.Visibility = Visibility.Collapsed; CommitCollider(); };

        // 区切り線
        rbSectionContainer.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 2), Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)) });

        // --- 「リジッドボディを使用」チェックボックス ---
        var rbPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        var useRbRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        useRbRow.Children.Add(new TextBlock
        {
            Text = "リジッドボディを使用", Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize = 11, Width = 130, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold,
        });
        var useRbCheck = new CheckBox { IsChecked = curUseRb, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        useRbRow.Children.Add(useRbCheck);
        rbSectionContainer.Children.Add(useRbRow);

        // リジッドボディパラメータ展開パネル（左アクセントライン付きのインデントブロック）
        var rbParamsPanel = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 0),
        };
        // Inspector 上で RigidBody セクションをサブセクションとしてインデント表示する
        var rbBorder = new Border
        {
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Margin          = new Thickness(8, 2, 0, 4),
            Padding         = new Thickness(8, 4, 0, 4),
            Child           = rbParamsPanel,
            Visibility      = curUseRb ? Visibility.Visible : Visibility.Collapsed,
        };

        // --- 数値フィールド群 ---
        // 行の作り（値域が確定していればスライダー）と「⟲ 既定値に戻す」は
        // 共通入口 BuildResettableFloatRow に一任する（行ごとの個別配線を書かないため）。
        // Collider は SET_COLLIDER_DATA で ColliderComponentData と同形の JSON を丸ごと送るため
        // 「送信キー」という概念が無く、serde フィールド名がそのままリセットのパスになる。
        // なお値域表への追記は無い（physics_component_ops.rs に clamp が無く、
        // 反発・摩擦の上限を UI 側で勝手に決めるとランタイムが受け付ける値に手が届かなくなるため）。
        void AddRbFloatRow(string label, float value, string serdeField, Action<float> assign)
        {
            rbParamsPanel.Children.Add(BuildResettableFloatRow(
                info.SlotIdx, ColliderComponentType, label, value, serdeField, ColliderNumberFormat,
                v => { assign(v); CommitCollider(); }));
        }

        AddRbFloatRow("質量 (kg)",     curMass,    "mass",            v => curMass    = v);
        AddRbFloatRow("反発係数",       curRest,    "restitution",     v => curRest    = v);
        AddRbFloatRow("摩擦係数",       curFric,    "friction",        v => curFric    = v);
        AddRbFloatRow("移動速度の減衰", curLinDamp, "linear_damping",  v => curLinDamp = v);
        AddRbFloatRow("回転速度の減衰", curAngD,    "angular_damping", v => curAngD    = v);
        AddRbFloatRow("重力の倍率",     curGrav,    "gravity_scale",   v => curGrav    = v);

        // --- キネマティック（スクリプトで直接制御） ---
        rbParamsPanel.Children.Add(BuildCheckRow("キネマティック", curKinem,
            v => { curKinem = v; CommitCollider(); }));

        // --- フリーズ（軸固定） ---
        rbParamsPanel.Children.Add(BuildFreezeRow("静止移動軸", curFreezeP, () => CommitCollider()));
        rbParamsPanel.Children.Add(BuildFreezeRow("静止回転軸", curFreezeR, () => CommitCollider()));

        // --- 初速度 ---
        // 3 成分まとめて 1 パス（serde 名 initial_linear_velocity）でリセットする＝ Undo も 1 回で戻る。
        var rowLinV = BuildXYZRowSimple("初速度 (m/s)", curLinV[0], curLinV[1], curLinV[2]);
        rbParamsPanel.Children.Add(WithFieldReset(
            rowLinV.element, info.SlotIdx, ColliderComponentType,
            "initial_linear_velocity", "初速度 (m/s)"));
        void CommitLinV() {
            if (!TryParseF(rowLinV.tx, out var x)) return;
            if (!TryParseF(rowLinV.ty, out var y)) return;
            if (!TryParseF(rowLinV.tz, out var z)) return;
            curLinV[0] = x; curLinV[1] = y; curLinV[2] = z; CommitCollider();
        }
        rowLinV.tx.LostFocus += (_, _) => CommitLinV(); rowLinV.tx.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitLinV(); };
        rowLinV.ty.LostFocus += (_, _) => CommitLinV(); rowLinV.ty.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitLinV(); };
        rowLinV.tz.LostFocus += (_, _) => CommitLinV(); rowLinV.tz.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitLinV(); };
        NumericDragBehavior.SetOnDrag(rowLinV.tx, CommitLinV); NumericDragBehavior.SetOnDrag(rowLinV.ty, CommitLinV); NumericDragBehavior.SetOnDrag(rowLinV.tz, CommitLinV);

        // --- 初期角速度 ---
        var rowAngV = BuildXYZRowSimple("初角速度 (rad/s)", curAngV[0], curAngV[1], curAngV[2]);
        rbParamsPanel.Children.Add(WithFieldReset(
            rowAngV.element, info.SlotIdx, ColliderComponentType,
            "initial_angular_velocity", "初角速度 (rad/s)"));
        void CommitAngV() {
            if (!TryParseF(rowAngV.tx, out var x)) return;
            if (!TryParseF(rowAngV.ty, out var y)) return;
            if (!TryParseF(rowAngV.tz, out var z)) return;
            curAngV[0] = x; curAngV[1] = y; curAngV[2] = z; CommitCollider();
        }
        rowAngV.tx.LostFocus += (_, _) => CommitAngV(); rowAngV.tx.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitAngV(); };
        rowAngV.ty.LostFocus += (_, _) => CommitAngV(); rowAngV.ty.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitAngV(); };
        rowAngV.tz.LostFocus += (_, _) => CommitAngV(); rowAngV.tz.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitAngV(); };
        NumericDragBehavior.SetOnDrag(rowAngV.tx, CommitAngV); NumericDragBehavior.SetOnDrag(rowAngV.ty, CommitAngV); NumericDragBehavior.SetOnDrag(rowAngV.tz, CommitAngV);

        rbSectionContainer.Children.Add(rbBorder);

        // チェック切り替え時にボーダーごと表示/非表示を切り替える
        useRbCheck.Checked   += (_, _) => { curUseRb = true;  rbBorder.Visibility = Visibility.Visible;   CommitCollider(); };
        useRbCheck.Unchecked += (_, _) => { curUseRb = false; rbBorder.Visibility  = Visibility.Collapsed; CommitCollider(); };

        return sp;
    }

    // ── Collider2dComponent inspector（2D リジッドボディ統合版）────────────

    /// <summary>
    /// Collider2dComponent のインスペクター UI を構築して返す（2D キャンバス用）。
    /// 形状・オフセット・トリガー・レイヤーに加え、
    /// 「リジッドボディを使用」チェックをオンにすると物理演算パラメータが展開される。
    /// 値変更時は SET_COLLIDER2D_DATA:{actorId},{slotIdx},{json} を送信する。
    /// </summary>
    private UIElement BuildCollider2dSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // ── 現在の collider_data JSON をパース ──────────────────────────
        string shapeType  = "Box";
        float  heX = 50f, heY = 50f;   // ピクセル単位
        float  radius     = 50f;
        float  halfHeight = 100f;
        float  offX = 0f, offY = 0f;
        bool   isTrigger  = false;
        int    physLayer  = 1;
        int    layerMask  = 0;
        // リジッドボディ設定
        bool   useRb      = false;
        float  mass = 1f, rest = 0.3f, fric = 0.5f, linDamp = 0.01f, angDamp = 0.05f, gravScale = 1f;
        bool   isKinematic = false;
        var    freezePos2  = new bool[2];
        bool   freezeRot2  = false;
        var    initLinVel2 = new float[2];
        float  initAngVel2 = 0f;
        // アスペクト比設定
        bool   keepAspect2d    = false;
        string aspectAxis2d    = "width";

        try
        {
            using var doc = JsonDocument.Parse(info.ColliderDataJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("shape", out var shape))
            {
                shapeType = shape.TryGetProperty("type", out var t) ? t.GetString() ?? "Box" : "Box";
                if (shape.TryGetProperty("half_extents", out var he) && he.GetArrayLength() == 2)
                { heX = he[0].GetSingle(); heY = he[1].GetSingle(); }
                if (shape.TryGetProperty("radius",      out var r))  radius     = r.GetSingle();
                if (shape.TryGetProperty("half_height", out var hh)) halfHeight = hh.GetSingle();
            }
            if (root.TryGetProperty("offset", out var off) && off.GetArrayLength() == 2)
            { offX = off[0].GetSingle(); offY = off[1].GetSingle(); }
            if (root.TryGetProperty("is_trigger",    out var it2)) isTrigger = it2.GetBoolean();
            if (root.TryGetProperty("physics_layer", out var pl))  physLayer = pl.GetInt32();
            if (root.TryGetProperty("layer_mask",    out var lm))  layerMask = lm.GetInt32();
            if (root.TryGetProperty("use_rigidbody",   out var ur))  useRb       = ur.GetBoolean();
            if (root.TryGetProperty("mass",            out var mv))  mass        = mv.GetSingle();
            if (root.TryGetProperty("restitution",     out var rv2)) rest        = rv2.GetSingle();
            if (root.TryGetProperty("friction",        out var fv))  fric        = fv.GetSingle();
            if (root.TryGetProperty("linear_damping",  out var ld))  linDamp     = ld.GetSingle();
            if (root.TryGetProperty("angular_damping", out var ad))  angDamp     = ad.GetSingle();
            if (root.TryGetProperty("gravity_scale",   out var gs))  gravScale   = gs.GetSingle();
            if (root.TryGetProperty("is_kinematic",    out var ik))  isKinematic = ik.GetBoolean();
            if (root.TryGetProperty("freeze_position", out var fp) && fp.GetArrayLength() == 2)
            { freezePos2[0] = fp[0].GetBoolean(); freezePos2[1] = fp[1].GetBoolean(); }
            if (root.TryGetProperty("freeze_rotation", out var fr))  freezeRot2  = fr.GetBoolean();
            if (root.TryGetProperty("initial_linear_velocity", out var ilv) && ilv.GetArrayLength() == 2)
            { initLinVel2[0] = ilv[0].GetSingle(); initLinVel2[1] = ilv[1].GetSingle(); }
            if (root.TryGetProperty("initial_angular_velocity", out var iav)) initAngVel2 = iav.GetSingle();
            if (root.TryGetProperty("keep_aspect_ratio", out var kar2)) keepAspect2d = kar2.GetBoolean();
            if (root.TryGetProperty("aspect_ratio_axis", out var ara2)) aspectAxis2d = ara2.GetString() ?? "width";
        }
        catch { /* デフォルト値を使用 */ }

        // ── 現在値（クロージャ共有） ─────────────────────────────────────
        var curShapeType  = shapeType;
        var curHe         = new float[] { heX, heY };
        var curRadius     = radius;
        var curHalfHeight = halfHeight;
        var curOffset     = new float[] { offX, offY };
        var curTrigger    = isTrigger;
        var curLayer      = physLayer;
        var curMask       = layerMask;
        var curUseRb      = useRb;
        var curMass       = mass;      var curRest  = rest;    var curFric    = fric;
        var curLinDamp    = linDamp;   var curAngD  = angDamp; var curGrav    = gravScale;
        var curKinem      = isKinematic;
        var curFreezeP    = (bool[])freezePos2.Clone();
        var curFreezeR    = freezeRot2;
        var curLinV       = (float[])initLinVel2.Clone();
        var curAngV2      = initAngVel2;
        var curKeepAspect2d = keepAspect2d;
        var curAspectAxis2d = aspectAxis2d;

        // ── JSON 再構築 + 送信ヘルパー ───────────────────────────────────
        void CommitCollider2d()
        {
            if (_currentActorId < 0) return;
            static string F(float v) => v.ToString("G", CultureInfo.InvariantCulture);
            static string B(bool  v) => v ? "true" : "false";
            string shapeJson = curShapeType switch
            {
                "Circle"  => $"{{\"type\":\"Circle\",\"radius\":{F(curRadius)}}}",
                "Capsule" => $"{{\"type\":\"Capsule\",\"radius\":{F(curRadius)},\"half_height\":{F(curHalfHeight)}}}",
                _         => $"{{\"type\":\"Box\",\"half_extents\":[{F(curHe[0])},{F(curHe[1])}]}}",
            };
            var json =
                $"{{\"shape\":{shapeJson}," +
                $"\"offset\":[{F(curOffset[0])},{F(curOffset[1])}]," +
                $"\"is_trigger\":{B(curTrigger)}," +
                $"\"physics_layer\":{curLayer}," +
                $"\"layer_mask\":{curMask}," +
                $"\"use_rigidbody\":{B(curUseRb)}," +
                $"\"mass\":{F(curMass)}," +
                $"\"restitution\":{F(curRest)}," +
                $"\"friction\":{F(curFric)}," +
                $"\"linear_damping\":{F(curLinDamp)}," +
                $"\"angular_damping\":{F(curAngD)}," +
                $"\"gravity_scale\":{F(curGrav)}," +
                $"\"is_kinematic\":{B(curKinem)}," +
                $"\"freeze_position\":[{B(curFreezeP[0])},{B(curFreezeP[1])}]," +
                $"\"freeze_rotation\":{B(curFreezeR)}," +
                $"\"initial_linear_velocity\":[{F(curLinV[0])},{F(curLinV[1])}]," +
                $"\"initial_angular_velocity\":{F(curAngV2)}," +
                $"\"keep_aspect_ratio\":{B(curKeepAspect2d)}," +
                $"\"aspect_ratio_axis\":\"{curAspectAxis2d}\"}}";
            _runtime?.SendToRuntime($"SET_COLLIDER2D_DATA:{_currentActorId},{info.SlotIdx},{json}");
        }

        // ────────────────────────────────────────────────────────────────
        // コライダー設定
        // ────────────────────────────────────────────────────────────────

        // --- 形状タイプ コンボボックス ---
        var shapeCombo = new ComboBox
        {
            Width = 120, Height = 22, FontSize = 11, Margin = new Thickness(4, 0, 0, 0),
        };
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Box"     });
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Circle"  });
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Capsule" });
        foreach (ComboBoxItem ci in shapeCombo.Items)
            if (ci.Content as string == curShapeType) { shapeCombo.SelectedItem = ci; break; }
        var shapeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        shapeRow.Children.Add(new TextBlock
        {
            Text = "形状", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        shapeRow.Children.Add(shapeCombo);
        sp.Children.Add(shapeRow);

        // --- 形状別パラメータエリア（動的に差し替え） ---
        var shapeParamPanel = new StackPanel();
        sp.Children.Add(shapeParamPanel);

        // 2 値ラベル行ヘルパー（X, Y）
        static (UIElement element, TextBox tx, TextBox ty) BuildXYRowSimple2d(string label, float vx, float vy)
        {
            TextBox MakeTb(float val)
            {
                var t = new TextBox
                {
                    Text              = val.ToString("F2", CultureInfo.InvariantCulture),
                    Background        = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                    Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    BorderBrush       = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                    BorderThickness   = new Thickness(1),
                    FontSize          = 11,
                    Padding           = new Thickness(4, 1, 4, 1),
                    Width             = 68,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(2, 0, 0, 0),
                };
                NumericDragBehavior.SetEnabled(t, true);
                return t;
            }
            var tx = MakeTb(vx); var ty = MakeTb(vy);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = label, FontSize = 11, Width = 90,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(tx); row.Children.Add(ty);
            return (row, tx, ty);
        }

        void RebuildShapeParams2d()
        {
            shapeParamPanel.Children.Clear();
            if (curShapeType == "Box")
            {
                var row = BuildXYRowSimple2d("半辺 HE (px)", curHe[0], curHe[1]);
                shapeParamPanel.Children.Add(row.element);
                void CommitHe() {
                    if (!TryParseF(row.tx, out var x)) return;
                    if (!TryParseF(row.ty, out var y)) return;
                    curHe[0] = x; curHe[1] = y; CommitCollider2d();
                }
                row.tx.LostFocus += (_, _) => CommitHe(); row.tx.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitHe(); };
                row.ty.LostFocus += (_, _) => CommitHe(); row.ty.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitHe(); };
                NumericDragBehavior.SetOnDrag(row.tx, CommitHe); NumericDragBehavior.SetOnDrag(row.ty, CommitHe);
            }
            else if (curShapeType == "Circle")
            {
                var rowR = BuildLabeledNumberRow("半径 (px)", curRadius);
                shapeParamPanel.Children.Add(rowR.element);
                void CommitR() {
                    if (TryParseF(rowR.textBox, out var v)) { curRadius = v; CommitCollider2d(); }
                }
                rowR.textBox.LostFocus += (_, _) => CommitR();
                rowR.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitR(); };
                NumericDragBehavior.SetOnDrag(rowR.textBox, CommitR);
            }
            else if (curShapeType == "Capsule")
            {
                var rowR  = BuildLabeledNumberRow("半径 (px)",   curRadius);
                var rowHH = BuildLabeledNumberRow("半高さ (px)", curHalfHeight);
                shapeParamPanel.Children.Add(rowR.element);
                shapeParamPanel.Children.Add(rowHH.element);
                void CommitRH() {
                    if (!TryParseF(rowR.textBox,  out var r))  return;
                    if (!TryParseF(rowHH.textBox, out var hh)) return;
                    curRadius = r; curHalfHeight = hh; CommitCollider2d();
                }
                rowR.textBox.LostFocus  += (_, _) => CommitRH(); rowR.textBox.KeyDown  += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitRH(); };
                rowHH.textBox.LostFocus += (_, _) => CommitRH(); rowHH.textBox.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitRH(); };
                NumericDragBehavior.SetOnDrag(rowR.textBox, CommitRH); NumericDragBehavior.SetOnDrag(rowHH.textBox, CommitRH);
            }
        }
        RebuildShapeParams2d();

        shapeCombo.SelectionChanged += (_, _) =>
        {
            curShapeType  = (shapeCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "Box";
            curHe         = new float[] { 50f, 50f };
            curRadius     = 50f;
            curHalfHeight = 100f;
            RebuildShapeParams2d();
            CommitCollider2d();
        };

        // --- オフセット (px) ---
        var offRow2d = BuildXYRowSimple2d("オフセット (px)", curOffset[0], curOffset[1]);
        sp.Children.Add(offRow2d.element);
        void CommitOff2d() {
            if (!TryParseF(offRow2d.tx, out var x)) return;
            if (!TryParseF(offRow2d.ty, out var y)) return;
            curOffset[0] = x; curOffset[1] = y; CommitCollider2d();
        }
        offRow2d.tx.LostFocus += (_, _) => CommitOff2d(); offRow2d.tx.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitOff2d(); };
        offRow2d.ty.LostFocus += (_, _) => CommitOff2d(); offRow2d.ty.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitOff2d(); };
        NumericDragBehavior.SetOnDrag(offRow2d.tx, CommitOff2d); NumericDragBehavior.SetOnDrag(offRow2d.ty, CommitOff2d);

        // --- 押し戻し ---
        var triggerRow2d = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        triggerRow2d.Children.Add(new TextBlock
        {
            Text = "押し戻し", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        var triggerCheck2d = new CheckBox { IsChecked = !curTrigger, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        triggerRow2d.Children.Add(triggerCheck2d);
        sp.Children.Add(triggerRow2d);

        // --- 物理レイヤー・マスク ---
        var layerRow2d = BuildLabeledIntRow("物理レイヤー", curLayer, min: 0);
        sp.Children.Add(layerRow2d.element);
        void CommitLayer2d() { if (int.TryParse(layerRow2d.textBox.Text, out var v)) { curLayer = Math.Max(0, v); CommitCollider2d(); } }
        layerRow2d.textBox.LostFocus += (_, _) => CommitLayer2d();
        layerRow2d.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitLayer2d(); };
        NumericDragBehavior.SetOnDrag(layerRow2d.textBox, CommitLayer2d);

        var maskRow2d = BuildLabeledIntRow("レイヤーマスク", curMask, min: 0);
        sp.Children.Add(maskRow2d.element);
        void CommitMask2d() { if (int.TryParse(maskRow2d.textBox.Text, out var v)) { curMask = Math.Max(0, v); CommitCollider2d(); } }
        maskRow2d.textBox.LostFocus += (_, _) => CommitMask2d();
        maskRow2d.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitMask2d(); };
        NumericDragBehavior.SetOnDrag(maskRow2d.textBox, CommitMask2d);

        // ────────────────────────────────────────────────────────────────
        // リジッドボディ設定
        // ────────────────────────────────────────────────────────────────
        var rbSectionContainer2d = new StackPanel
        {
            Visibility = curTrigger ? Visibility.Collapsed : Visibility.Visible,
        };
        sp.Children.Add(rbSectionContainer2d);

        triggerCheck2d.Checked   += (_, _) => { curTrigger = false; rbSectionContainer2d.Visibility = Visibility.Visible;   CommitCollider2d(); };
        triggerCheck2d.Unchecked += (_, _) => { curTrigger = true;  rbSectionContainer2d.Visibility = Visibility.Collapsed; CommitCollider2d(); };

        rbSectionContainer2d.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 2), Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)) });

        var useRbRow2d = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        useRbRow2d.Children.Add(new TextBlock
        {
            Text = "リジッドボディを使用", Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize = 11, Width = 130, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold,
        });
        var useRbCheck2d = new CheckBox { IsChecked = curUseRb, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        useRbRow2d.Children.Add(useRbCheck2d);
        rbSectionContainer2d.Children.Add(useRbRow2d);

        var rbParamsPanel2d = new StackPanel();
        var rbBorder2d = new Border
        {
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Margin          = new Thickness(8, 2, 0, 4),
            Padding         = new Thickness(8, 4, 0, 4),
            Child           = rbParamsPanel2d,
            Visibility      = curUseRb ? Visibility.Visible : Visibility.Collapsed,
        };

        // 行の作りと「⟲ 既定値に戻す」は 3D コライダーと同じく共通入口へ一任する。
        // SET_COLLIDER2D_DATA も Collider2dComponentData と同形の JSON を丸ごと送るため、
        // serde フィールド名がそのままリセットのパスになる（値域表への追記は無し。理由は 3D 版と同じ）。
        void AddRbFloatRow2d(string label, float value, string serdeField, Action<float> assign)
        {
            rbParamsPanel2d.Children.Add(BuildResettableFloatRow(
                info.SlotIdx, Collider2dComponentType, label, value, serdeField, ColliderNumberFormat,
                v => { assign(v); CommitCollider2d(); }));
        }

        AddRbFloatRow2d("質量 (kg)",     curMass,    "mass",            v => curMass    = v);
        AddRbFloatRow2d("反発係数",       curRest,    "restitution",     v => curRest    = v);
        AddRbFloatRow2d("摩擦係数",       curFric,    "friction",        v => curFric    = v);
        AddRbFloatRow2d("移動速度の減衰", curLinDamp, "linear_damping",  v => curLinDamp = v);
        AddRbFloatRow2d("回転速度の減衰", curAngD,    "angular_damping", v => curAngD    = v);
        AddRbFloatRow2d("重力の倍率",     curGrav,    "gravity_scale",   v => curGrav    = v);

        // --- キネマティック ---
        rbParamsPanel2d.Children.Add(BuildCheckRow("キネマティック", curKinem,
            v => { curKinem = v; CommitCollider2d(); }));

        // --- 位置フリーズ [X, Y] ---
        var freezePRow2d = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 2) };
        freezePRow2d.Children.Add(new TextBlock
        {
            Text = "静止移動軸", FontSize = 11, Width = 90,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        for (int i2 = 0; i2 < 2; i2++)
        {
            int idx2 = i2;
            var lbl2 = new TextBlock { Text = i2 == 0 ? "X" : "Y", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), Margin = new Thickness(4, 0, 0, 0) };
            var chk2 = new CheckBox { IsChecked = curFreezeP[idx2], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
            chk2.Checked   += (_, _) => { curFreezeP[idx2] = true;  CommitCollider2d(); };
            chk2.Unchecked += (_, _) => { curFreezeP[idx2] = false; CommitCollider2d(); };
            freezePRow2d.Children.Add(lbl2);
            freezePRow2d.Children.Add(chk2);
        }
        rbParamsPanel2d.Children.Add(freezePRow2d);

        // --- 回転フリーズ（Z 軸のみ）---
        rbParamsPanel2d.Children.Add(BuildCheckRow("静止回転軸 Z", curFreezeR,
            v => { curFreezeR = v; CommitCollider2d(); }));

        // --- 初速度 (px/s) ---
        // 2 成分まとめて 1 パス（serde 名 initial_linear_velocity）でリセットする。
        var rowLinV2d = BuildXYRowSimple2d("初速度 (px/s)", curLinV[0], curLinV[1]);
        rbParamsPanel2d.Children.Add(WithFieldReset(
            rowLinV2d.element, info.SlotIdx, Collider2dComponentType,
            "initial_linear_velocity", "初速度 (px/s)"));
        void CommitLinV2d() {
            if (!TryParseF(rowLinV2d.tx, out var x)) return;
            if (!TryParseF(rowLinV2d.ty, out var y)) return;
            curLinV[0] = x; curLinV[1] = y; CommitCollider2d();
        }
        rowLinV2d.tx.LostFocus += (_, _) => CommitLinV2d(); rowLinV2d.tx.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitLinV2d(); };
        rowLinV2d.ty.LostFocus += (_, _) => CommitLinV2d(); rowLinV2d.ty.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitLinV2d(); };
        NumericDragBehavior.SetOnDrag(rowLinV2d.tx, CommitLinV2d); NumericDragBehavior.SetOnDrag(rowLinV2d.ty, CommitLinV2d);

        // --- 初期角速度 (rad/s) ---
        // 2D の角速度は Z 軸まわりのスカラー（serde 名 initial_angular_velocity）。
        AddRbFloatRow2d("初角速度 (rad/s)", curAngV2, "initial_angular_velocity", v => curAngV2 = v);

        rbSectionContainer2d.Children.Add(rbBorder2d);

        useRbCheck2d.Checked   += (_, _) => { curUseRb = true;  rbBorder2d.Visibility = Visibility.Visible;   CommitCollider2d(); };
        useRbCheck2d.Unchecked += (_, _) => { curUseRb = false; rbBorder2d.Visibility = Visibility.Collapsed; CommitCollider2d(); };

        // ── アスペクト比セクション ──────────────────────────────────────────
        var aspectSep2d = new TextBlock
        {
            Text       = "スケール設定",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize   = 10,
            Margin     = new Thickness(0, 8, 0, 2),
        };
        sp.Children.Add(aspectSep2d);

        // チェックボックス: アスペクト比を維持
        var cbKeepAspect2d = new CheckBox
        {
            Content           = "アスペクト比を維持（scale_size 時）",
            IsChecked         = curKeepAspect2d,
            Foreground        = new SolidColorBrush(Colors.White),
            FontSize          = 11,
            Margin            = new Thickness(0, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = "親 CanvasComponent の scale_size=true 時に、形状のスケールをアスペクト比維持で適用します。",
        };
        sp.Children.Add(cbKeepAspect2d);

        // 基準軸選択パネル（アスペクト比維持がオンのときのみ表示）
        var axisPanel2d = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(16, 0, 0, 4),
            Visibility  = curKeepAspect2d ? Visibility.Visible : Visibility.Collapsed,
        };
        axisPanel2d.Children.Add(new TextBlock
        {
            Text              = "基準軸",
            Foreground        = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize          = 11,
            Width             = 60,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var cmbAspectAxis2d = new ComboBox
        {
            Width    = 100,
            FontSize = 11,
            Margin   = new Thickness(4, 0, 0, 0),
        };
        cmbAspectAxis2d.Items.Add(new ComboBoxItem { Content = "横（幅）基準",   Tag = "width"  });
        cmbAspectAxis2d.Items.Add(new ComboBoxItem { Content = "縦（高さ）基準", Tag = "height" });
        cmbAspectAxis2d.SelectedIndex = curAspectAxis2d == "height" ? 1 : 0;
        axisPanel2d.Children.Add(cmbAspectAxis2d);
        sp.Children.Add(axisPanel2d);

        // アスペクト比を送信するローカル関数
        void CommitAspectRatio2d()
        {
            if (_currentActorId < 0) return;
            int keep = (cbKeepAspect2d.IsChecked == true) ? 1 : 0;
            var axis = (cmbAspectAxis2d.SelectedItem as ComboBoxItem)?.Tag as string ?? "width";
            curKeepAspect2d = keep == 1;
            curAspectAxis2d = axis;
            CommitCollider2d();
        }
        cbKeepAspect2d.Checked   += (_, _) => { axisPanel2d.Visibility = Visibility.Visible;  CommitAspectRatio2d(); };
        cbKeepAspect2d.Unchecked += (_, _) => { axisPanel2d.Visibility = Visibility.Collapsed; CommitAspectRatio2d(); };
        cmbAspectAxis2d.SelectionChanged += (_, _) => CommitAspectRatio2d();

        return sp;
    }

    // ── InputMapComponent inspector ───────────────────────────

    /// <summary>InputMapComponent のインスペクター UI を構築して返す。</summary>
    private UIElement BuildInputMapSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // .inputmap アセットパス選択行
        sp.Children.Add(FileRefBuilder.Build(
            "InputMap", info.InputMapPath,
            [".inputmap"],
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "InputMap ファイルを選択",
                    Filter = "InputMap アセット|*.inputmap|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path =>
            {
                if (_currentActorId < 0) return;
                // 絶対パスを仮想パスに変換してからランタイムへ送信する
                var virtualPath = VirtualPath.ToVirtual(path, _assetsPath);
                _runtime?.SendToRuntime($"SET_INPUTMAP_PATH:{_currentActorId},{info.SlotIdx},{virtualPath}");
            }));

        // InputMap エディタを開くボタン
        var editBtn = new Button
        {
            Content             = "InputMap エディタを開く",
            Margin              = new Thickness(0, 4, 0, 0),
            Padding             = new Thickness(8, 4, 8, 4),
            Background          = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x2A)),
            Foreground          = new SolidColorBrush(Color.FromRgb(0xAA, 0xCC, 0xAA)),
            BorderBrush         = new SolidColorBrush(Color.FromRgb(0x44, 0x66, 0x44)),
            BorderThickness     = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize            = 11,
            Cursor              = Cursors.Hand,
        };
        editBtn.Click += (_, _) =>
        {
            // 既に存在するウィンドウがあればアクティブにする、なければ新規作成
            var path = info.InputMapPath;
            var win  = new SEEDEditor.InputMap.InputMapEditorWindow(path)
            {
                Owner = Window.GetWindow(this),
            };
            win.Show();
        };
        sp.Children.Add(editBtn);

        return sp;
    }

    // ── ModelComponent レンダータグ（セマンティックタグ）───────────
    //
    // ランタイム側 `renderer::surface_id::RENDER_TAG_BITS`（= 4bit）と一致させること。
    // 範囲外の値を送ってもランタイムがマスクするが、UI 側でも同じ範囲にクランプして
    // 「入力した値と実際に焼かれる値」がズレないようにする。

    /// <summary>レンダータグの最小値（0 = タグ無し）。</summary>
    private const int ModelRenderTagMin = 0;
    /// <summary>レンダータグの最大値（ランタイムの RENDER_TAG_BITS=4 に対応する 15）。</summary>
    private const int ModelRenderTagMax = 15;
    /// <summary>レンダータグ入力欄のツールチップ（用途の説明）。</summary>
    private const string ModelRenderTagTooltip =
        "合成/演出でグループ指定に使う描画タグ（0=なし）。第3層アセットから読める";

    private UIElement BuildModelSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        sp.Children.Add(BuildModelPathRow(info));

        // ── 影を落とす（シャドウマップレンダリングで使用）────────────
        // LightComponent の同名チェックボックス（BuildLightSlotContent）とスタイル・配置を揃える。
        var shadowRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        shadowRow.Children.Add(new TextBlock
        {
            Text = "影を落とす", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        var shadowCheck = new CheckBox
        {
            IsChecked = info.ModelCastShadows, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        shadowCheck.Checked   += (_, _) => _runtime?.SendToRuntime($"SET_MODEL_FIELD:{_currentActorId},{info.SlotIdx},cast_shadows,1");
        shadowCheck.Unchecked += (_, _) => _runtime?.SendToRuntime($"SET_MODEL_FIELD:{_currentActorId},{info.SlotIdx},cast_shadows,0");
        shadowRow.Children.Add(shadowCheck);
        sp.Children.Add(shadowRow);

        // ── レンダータグ（セマンティックタグ）─────────────────────
        // 整数入力（ModelRenderTagMin..ModelRenderTagMax）。SpriteComponent の「レイヤー」行と
        // 同じ「数値行 + 整数丸め + コミット時送信」パターンで作る。
        var rowTag = BuildLabeledNumberRow("レンダータグ", info.ModelRenderTag, "F0");
        rowTag.textBox.ToolTip = ModelRenderTagTooltip;
        sp.Children.Add(rowTag.element);

        // レンダータグ変更を送信するローカル関数（整数へ丸めて範囲クランプする）
        void CommitRenderTag()
        {
            if (_currentActorId < 0) return;
            if (!float.TryParse(rowTag.textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var tf)) return;
            // 数値ドラッグ等で小数が入っても整数へ丸め、有効範囲へクランプしてから送信する
            var tag = Math.Clamp((int)MathF.Round(tf), ModelRenderTagMin, ModelRenderTagMax);
            // クランプ結果を UI にも書き戻し、表示と実際に送った値を一致させる
            rowTag.textBox.Text = tag.ToString(CultureInfo.InvariantCulture);
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"SET_MODEL_FIELD:{_currentActorId},{info.SlotIdx},render_tag,{tag}"));
        }
        rowTag.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitRenderTag(); e.Handled = true; } };
        rowTag.textBox.LostFocus += (_, _) => CommitRenderTag();
        NumericDragBehavior.SetOnDrag(rowTag.textBox, CommitRenderTag);

        // ── マテリアル一覧（Phase R7: .mat マテリアル＋マルチマテリアル編集） ──────
        // materials 配列が無い/空の場合は後方互換のため何も表示しない。
        var materialsSection = BuildModelMaterialsSection(info);
        if (materialsSection is not null)
            sp.Children.Add(materialsSection);

        return sp;
    }

    // ── ModelComponent マテリアル編集（Phase R7） ──────────────

    /// <summary>
    /// ACTOR_COMPONENTS の 1 マテリアルスロット分のデータ（現在の実効値）。
    /// mode は "embedded"（glTF埋込・既定）/ "mat"（.mat割当）/ "inline"（インライン上書き）。
    /// base_color/emissive はリニア RGB(A)。
    /// CullFace は "back" | "front" | "none"（glTF の double_sided=true は "none" として届く）。
    ///
    /// OverrideIndex は ModelComponentData.material_overrides（Vec）内での **位置**で、
    /// 上書きが無いスロットは -1。マテリアルスロット番号（Slot）とは別物である点に注意
    /// （SET_MATERIAL_OVERRIDE は同一 slot を retain で除いてから push するので並びが変わる）。
    /// 「⟲ 既定値に戻す」の field_path はこの位置を使って組み立てる。
    /// </summary>
    private sealed record MaterialSlotData(
        int Slot, string Name, string Mode,
        float R, float G, float B, float A,
        float Metallic, float Roughness,
        float ER, float EG, float EB,
        string AlphaMode, float AlphaCutoff, float Ior, float Transmission, float DiffuseTransmission, bool MrTexIgnore, string CullFace,
        int ShadingModel, string Path, int OverrideIndex);

    /// <summary>
    /// SET_MATERIAL_OVERRIDE の "kind":"mat_asset" 送信用 JSON ペイロード（System.Text.Json でシリアライズ）。
    /// </summary>
    private sealed class MatAssetOverridePayload
    {
        public string kind { get; set; } = "mat_asset";
        public string path { get; set; } = "";
    }

    /// <summary>
    /// SET_MATERIAL_OVERRIDE の "kind":"inline" 送信用 JSON ペイロード（System.Text.Json でシリアライズ）。
    /// System.Text.Json の数値書式はスレッドカルチャに依存せず常に InvariantCulture 相当のため、
    /// ここで数値を文字列補間しない限り CultureInfo を明示指定する必要はない。
    /// </summary>
    private sealed class InlineOverridePayload
    {
        public string kind { get; set; } = "inline";
        public float[] base_color { get; set; } = [1f, 1f, 1f, 1f];
        public float metallic { get; set; } = 1f;
        public float roughness { get; set; } = 1f;
        public float[] emissive { get; set; } = [0f, 0f, 0f];
        public string alpha_mode { get; set; } = "opaque";
        public float alpha_cutoff { get; set; } = 0.5f;
        /// <summary>屈折率（IOR, Phase RT-Translucency）。1.0=屈折なし。Blend のときのみ意味を持つ。</summary>
        public float ior { get; set; } = 1f;
        /// <summary>透過率（transmission, ガラス表現）。0..1。0=従来動作。Blend のときのみ意味を持つ。</summary>
        public float transmission { get; set; } = 0f;
        /// <summary>拡散透過（葉・布・紙の逆光透け）。0..1。0=従来動作。AlphaMode に関わらず常時有効。</summary>
        public float diffuse_transmission { get; set; } = 0f;
        /// <summary>MR テクスチャ無視トグル。true で metallic/roughness テクスチャの乗算をスキップし factor を実効値にする（既定 false）。</summary>
        public bool mr_tex_ignore { get; set; } = false;
        /// <summary>カリング面 "back" | "front" | "none"。ランタイム側は大小文字非依存・不明値は Back 扱い。</summary>
        public string cull_face { get; set; } = CullFaceValues[0];
        /// <summary>
        /// シェーディングモデル ID（Phase L3-a）。0=標準 PBR、1..3=カメラのシェーディングアセットで
        /// 定義した shade_model_N。ランタイム側は実効 2bit（0..3）へマスクして G-Buffer へパックする。
        /// </summary>
        public int shading_model { get; set; } = ShadingModelDefaultPbr;
    }

    /// <summary>
    /// カリング面の内部値（ランタイムへ送る文字列）と、それに対応する UI 表示ラベル。
    /// 添字が 1:1 対応する前提でコンボボックスの選択インデックス⇔文字列変換に使う
    /// （マジックナンバー禁止のため、ここを唯一の定義元とする）。
    /// </summary>
    private static readonly string[] CullFaceValues = ["back", "front", "none"];
    private static readonly string[] CullFaceLabels = ["Back", "Front", "None"];

    /// <summary>
    /// シェーディングモデルの既定値（標準 PBR）。ランタイムの
    /// <c>surface_id::SHADING_MODEL_DEFAULT_PBR</c> と一致させること。
    /// </summary>
    private const int ShadingModelDefaultPbr = 0;

    /// <summary>
    /// シェーディングモデルの選択肢ラベル。添字がそのまま送信値（0..3）になる前提で使うため、
    /// ランタイムの実効ビット幅（surface_id::SHADING_MODEL_BITS = 2bit）と要素数を一致させること
    /// （マジックナンバー禁止のため、ここを唯一の定義元とする）。
    /// </summary>
    private static readonly string[] ShadingModelLabels =
        ["0: 標準PBR", "1: アセット定義1", "2: アセット定義2", "3: アセット定義3"];

    /// <summary>
    /// 「このマテリアルスロットには上書きが無い」を表す override_index の値。
    /// ランタイムの build_materials_json が同じ意味で -1 を送ってくる。
    /// </summary>
    private const int NoMaterialOverrideIndex = -1;

    /// <summary>
    /// マテリアルのインライン上書き 1 フィールドのリセットパス書式。
    /// {0}=material_overrides（Vec）内の位置、{1}=MaterialOverrideKind::Inline の serde フィールド名。
    /// MaterialOverrideKind は内部タグ（<c>#[serde(tag = "kind")]</c>）なので kind を 1 段挟む。
    /// </summary>
    private const string MaterialInlineResetPathFormat = "material_overrides/{0}/kind/{1}";

    /// <summary>マテリアルのモード文字列: インライン上書き（ACTOR_COMPONENTS の materials[].mode）。</summary>
    private const string MaterialModeInline = "inline";

    /// <summary>屈折率（IOR）入力の下限（1.0 = 屈折なし）。UI 側の目安レンジ。</summary>
    private const float MaterialIorMin = 1.0f;
    /// <summary>屈折率（IOR）入力の上限（ダイヤモンド級 ≈2.4 を含む 2.5）。UI 側の目安レンジ。</summary>
    private const float MaterialIorMax = 2.5f;
    /// <summary>マテリアルの正規化パラメータ（透過率等）の下限。</summary>
    private const float MaterialNormalizedMin = 0.0f;
    /// <summary>マテリアルの正規化パラメータ（透過率等）の上限。</summary>
    private const float MaterialNormalizedMax = 1.0f;

    /// <summary>シェーディングモデル コンボのツールチップ（インスペクタ表示用の説明文）。</summary>
    private const string ShadingModelTooltip =
        "ライティングの応答式。0=標準PBR、1〜3=カメラのシェーディングアセットで定義した shade_model_N（未定義なら標準）";

    /// <summary>
    /// 現在のアクターが持つ、指定 Model スロット（info.SlotIdx）の materials 配列を
    /// ACTOR_COMPONENTS の生 JSON（<see cref="_lastComponentsJson"/>）から読み取る。
    /// "slot" フィールドでコンポーネントスロットを一意に特定するため、
    /// GetModelSlotAnimations と異なり同一アクターに複数 Model スロットがあっても取り違えない。
    /// materials キー自体が無い/空配列の場合は空リストを返す（後方互換：一覧 UI 非表示のトリガー）。
    /// </summary>
    private List<MaterialSlotData> GetModelSlotMaterials(SlotInfo info)
    {
        var result = new List<MaterialSlotData>();
        if (string.IsNullOrEmpty(_lastComponentsJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(_lastComponentsJson);
            if (!doc.RootElement.TryGetProperty("components", out var comps)) return result;
            foreach (var comp in comps.EnumerateArray())
            {
                var slotIdx = comp.TryGetProperty("slot", out var si) ? si.GetInt32() : -1;
                if (slotIdx != info.SlotIdx) continue;
                var type = comp.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                if (type != "ModelComponent") return result;
                if (!comp.TryGetProperty("materials", out var matsEl) || matsEl.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (var m in matsEl.EnumerateArray())
                {
                    int slot   = m.TryGetProperty("slot", out var ms) ? ms.GetInt32()    : 0;
                    var name   = m.TryGetProperty("name", out var mn) ? mn.GetString() ?? "" : "";
                    var mode   = m.TryGetProperty("mode", out var mm) ? mm.GetString() ?? "embedded" : "embedded";

                    float r = 1f, g = 1f, b = 1f, a = 1f;
                    if (m.TryGetProperty("base_color", out var bc) && bc.ValueKind == JsonValueKind.Array)
                    {
                        var arr = bc.EnumerateArray().ToArray();
                        if (arr.Length > 0) r = arr[0].GetSingle();
                        if (arr.Length > 1) g = arr[1].GetSingle();
                        if (arr.Length > 2) b = arr[2].GetSingle();
                        if (arr.Length > 3) a = arr[3].GetSingle();
                    }
                    var metallic  = m.TryGetProperty("metallic",  out var mt) ? mt.GetSingle() : 1f;
                    var roughness = m.TryGetProperty("roughness", out var ro) ? ro.GetSingle() : 1f;

                    float er = 0f, eg = 0f, eb = 0f;
                    if (m.TryGetProperty("emissive", out var em) && em.ValueKind == JsonValueKind.Array)
                    {
                        var arr = em.EnumerateArray().ToArray();
                        if (arr.Length > 0) er = arr[0].GetSingle();
                        if (arr.Length > 1) eg = arr[1].GetSingle();
                        if (arr.Length > 2) eb = arr[2].GetSingle();
                    }
                    var alphaMode   = m.TryGetProperty("alpha_mode",   out var am) ? am.GetString() ?? "opaque" : "opaque";
                    var alphaCutoff = m.TryGetProperty("alpha_cutoff", out var ac) ? ac.GetSingle() : 0.5f;
                    // ior キーを持たない旧ランタイムの ACTOR_COMPONENTS でも動くよう既定 1.0（屈折なし）にフォールバックする。
                    var ior         = m.TryGetProperty("ior",          out var io) ? io.GetSingle() : 1f;
                    // transmission キーを持たない旧ランタイムの ACTOR_COMPONENTS でも動くよう既定 0.0（透過なし）にフォールバックする。
                    var transmission = m.TryGetProperty("transmission", out var tr) ? tr.GetSingle() : 0f;
                    // diffuse_transmission キーを持たない旧ランタイムの ACTOR_COMPONENTS でも動くよう既定 0.0（拡散透過なし）にフォールバックする。
                    var diffuseTransmission = m.TryGetProperty("diffuse_transmission", out var dt) ? dt.GetSingle() : 0f;
                    // mr_tex_ignore キーを持たない旧ランタイムの ACTOR_COMPONENTS でも動くよう既定 false（乗算）にフォールバックする。
                    var mrTexIgnore  = m.TryGetProperty("mr_tex_ignore", out var mi) && mi.ValueKind == JsonValueKind.True;
                    // cull_face キーを持たない旧ランタイムの ACTOR_COMPONENTS でも動くよう既定 "back" にフォールバックする。
                    var cullFace    = m.TryGetProperty("cull_face",    out var cf) ? cf.GetString() ?? CullFaceValues[0] : CullFaceValues[0];
                    // shading_model キーを持たない旧ランタイムの ACTOR_COMPONENTS でも動くよう
                    // 既定 0（標準 PBR）にフォールバックする。範囲外の値も選択肢数でクランプする。
                    var shadingModel = m.TryGetProperty("shading_model", out var sm) && sm.TryGetInt32(out var smv)
                        ? Math.Clamp(smv, 0, ShadingModelLabels.Length - 1)
                        : ShadingModelDefaultPbr;
                    var path        = m.TryGetProperty("path",        out var mp) ? mp.GetString() ?? ""       : "";
                    // override_index キーを持たない旧ランタイムの ACTOR_COMPONENTS でも動くよう
                    // 既定 -1（上書き無し＝⟲ 非表示）にフォールバックする。
                    var overrideIndex = m.TryGetProperty("override_index", out var oi) && oi.TryGetInt32(out var oiv)
                        ? oiv : NoMaterialOverrideIndex;

                    result.Add(new MaterialSlotData(slot, name, mode, r, g, b, a, metallic, roughness,
                        er, eg, eb, alphaMode, alphaCutoff, ior, transmission, diffuseTransmission, mrTexIgnore, cullFace,
                        shadingModel, path, overrideIndex));
                }
                return result;
            }
        }
        catch (JsonException) { /* 不正 JSON は空リスト扱い（後方互換） */ }
        return result;
    }

    /// <summary>
    /// マテリアルスロット一覧セクションを構築する。materials が空/無し（後方互換の旧シーン等）なら null を返し、
    /// 呼び出し側で一覧そのものを表示しない。
    /// 個々のスロット Expander をコンポーネントアコーディオン風の「まとめヘッダー」（▼/▶ + "マテリアル (N)"）
    /// で束ね、1クリックで一覧全体を開閉できるようにする。既定は閉（スロット数が多い場合に Inspector が
    /// 縦に長くなり過ぎないようにするため）。まとめヘッダー右側の「すべて展開/折りたたみ」ボタンで
    /// 配下スロットの個別 Expander を一括操作できる。
    /// </summary>
    private UIElement? BuildModelMaterialsSection(SlotInfo info)
    {
        var mats = GetModelSlotMaterials(info);
        if (mats.Count == 0) return null;

        var outer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        // ── 配下スロット一覧（まとめヘッダーの開閉対象。既定は閉なので非表示で構築）──
        var slotsPanel = new StackPanel { Visibility = Visibility.Collapsed };
        var slotExpanders = new List<Expander>();
        foreach (var mat in mats)
        {
            var expander = (Expander)BuildMaterialSlotExpander(info, mat);
            slotExpanders.Add(expander);
            slotsPanel.Children.Add(expander);
        }

        // ── まとめヘッダー（コンポーネントアコーディオンの見た目に寄せる: 矢印 + タイトル）──
        var header = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding         = new Thickness(6, 5, 6, 5),
            Cursor          = Cursors.Hand,
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // 矢印
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // タイトル
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // 一括展開/折畳

        var arrow = new TextBlock
        {
            Text              = "▶",
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize          = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 6, 0),
        };
        Grid.SetColumn(arrow, 0);
        headerGrid.Children.Add(arrow);

        var titleBlock = new TextBlock
        {
            Text              = $"マテリアル ({mats.Count})",
            FontWeight        = FontWeights.Bold,
            FontSize          = 11,
            Foreground        = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(titleBlock, 1);
        headerGrid.Children.Add(titleBlock);

        var bulkToggleBtn = new TextBlock
        {
            Text              = "すべて展開",
            FontSize          = 10,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x55, 0xAA, 0xFF)),
            Cursor            = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(bulkToggleBtn, 2);
        headerGrid.Children.Add(bulkToggleBtn);

        header.Child = headerGrid;

        // まとめヘッダーの開閉状態も値編集による UI 再構築をまたいで復元する
        // （マテリアル値を 1 つ触るたびに一覧全体が閉じてしまうのを防ぐ）。
        // キーは所有モデルスロットの添字（一覧はスロットごとに 1 つだけ存在する）。
        // 既定は「閉」（スロット数が多いと Inspector が縦に伸び過ぎるため）。
        var groupToggle = _expandStates.TrackCustom(
            ExpandKeyMaterialsPrefix + info.SlotIdx, defaultExpanded: false, expanded =>
            {
                slotsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                arrow.Text            = expanded ? "▼" : "▶";
            });

        header.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            groupToggle.Toggle();
        };

        // 「すべて展開/折りたたみ」: 配下スロットの個別 Expander を一括操作する。
        // まとめヘッダー自体が閉じていると操作結果が見えないため、実行時にまとめヘッダーも開く。
        bulkToggleBtn.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true; // ヘッダーの開閉トグルへ伝播させない
            if (!groupToggle.IsExpanded) groupToggle.Set(true);
            var expandAll = slotExpanders.Any(x => !x.IsExpanded);
            foreach (var x in slotExpanders) x.IsExpanded = expandAll;
            bulkToggleBtn.Text = expandAll ? "すべて折りたたみ" : "すべて展開";
        };

        outer.Children.Add(header);
        outer.Children.Add(slotsPanel);
        return outer;
    }

    /// <summary>
    /// マテリアル 1 スロット分の折りたたみセクション（スロット番号 + 名前 + 現在モードをヘッダーに表示）。
    /// モード切替コンボで 埋込/.mat/インライン を選び、選択モードに応じた編集ウィジェットを表示する。
    /// </summary>
    private UIElement BuildMaterialSlotExpander(SlotInfo info, MaterialSlotData mat)
    {
        string ModeLabel(string mode) => mode switch
        {
            "mat"    => ".matアセット",
            "inline" => "インライン上書き",
            _        => "埋め込み(glTF)",
        };

        var content = new StackPanel { Margin = new Thickness(6, 2, 0, 2) };

        // ── モード切替コンボ ──────────────────────────────────
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        modeRow.Children.Add(new TextBlock
        {
            Text = "モード", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, Width = 52, VerticalAlignment = VerticalAlignment.Center,
        });
        var modeCombo = new ComboBox { Width = 150, FontSize = 11 };
        modeCombo.Items.Add("埋め込み(glTF)");
        modeCombo.Items.Add(".matアセット");
        modeCombo.Items.Add("インライン上書き");
        modeCombo.SelectedIndex = mat.Mode switch { "mat" => 1, "inline" => 2, _ => 0 };
        modeRow.Children.Add(modeCombo);
        content.Children.Add(modeRow);

        // ── .matアセット割当 UI（FileRefBuilder: D&D + 参照ボタン）────
        var matFileRow = (FrameworkElement)FileRefBuilder.Build(
            ".matファイル", mat.Path, [".mat"],
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "マテリアルファイルを選択",
                    Filter = "マテリアル|*.mat|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path =>
            {
                if (_currentActorId < 0) return;
                // 絶対パスを assets:// 仮想パスへ変換してから送信する（アセット外の場合は絶対パスのまま）。
                var virtualPath = VirtualPath.ToVirtual(path, _assetsPath);
                var json = JsonSerializer.Serialize(new MatAssetOverridePayload { kind = "mat_asset", path = virtualPath });
                _runtime?.SendToRuntime($"SET_MATERIAL_OVERRIDE:{_currentActorId},{info.SlotIdx},{mat.Slot},{json}");
            });
        matFileRow.Visibility = mat.Mode == "mat" ? Visibility.Visible : Visibility.Collapsed;
        content.Children.Add(matFileRow);

        // ── インライン編集 UI ────────────────────────────────
        // 現在値をローカル変数として保持し、いずれかの編集操作のたびに全フィールドをまとめて送信する。
        float curR = mat.R, curG = mat.G, curB = mat.B, curA = mat.A;
        float curMetallic = mat.Metallic, curRoughness = mat.Roughness;
        float curER = mat.ER, curEG = mat.EG, curEB = mat.EB;
        string curAlphaMode = mat.AlphaMode;
        float curAlphaCutoff = mat.AlphaCutoff;
        float curIor = mat.Ior;
        float curTransmission = mat.Transmission;
        float curDiffuseTransmission = mat.DiffuseTransmission;
        bool curMrTexIgnore = mat.MrTexIgnore;
        string curCullFace = mat.CullFace;
        int curShadingModel = mat.ShadingModel;

        var inlinePanel = new StackPanel { Visibility = mat.Mode == "inline" ? Visibility.Visible : Visibility.Collapsed };

        void SendInline()
        {
            if (_currentActorId < 0) return;
            var payload = new InlineOverridePayload
            {
                kind         = "inline",
                base_color   = [curR, curG, curB, curA],
                metallic     = curMetallic,
                roughness    = curRoughness,
                emissive     = [curER, curEG, curEB],
                alpha_mode   = curAlphaMode,
                alpha_cutoff = curAlphaCutoff,
                ior          = curIor,
                transmission = curTransmission,
                diffuse_transmission = curDiffuseTransmission,
                mr_tex_ignore = curMrTexIgnore,
                cull_face    = curCullFace,
                shading_model = curShadingModel,
            };
            var json = JsonSerializer.Serialize(payload);
            _runtime?.SendToRuntime($"SET_MATERIAL_OVERRIDE:{_currentActorId},{info.SlotIdx},{mat.Slot},{json}");
        }

        // ── インライン値の「⟲ 既定値に戻す」────────────────────
        // リセット対象は ModelComponent のスロット（info.SlotIdx）であって
        // マテリアルスロット（mat.Slot）ではない。パスは
        // material_overrides/{Vec 内の位置}/kind/{フィールド} で、
        // 位置はランタイムが materials JSON に載せてくる override_index を使う
        //（マテリアルスロット番号とは一致しない）。
        //
        // Inline の各フィールドは Rust 側で Option<T> であり、None は
        // 「モデル埋め込みの値を使う」を意味する。したがってリセット＝null 化＝
        // 「モデル本来の値へ戻る」という正しい意味になる（ランタイム側で実装済み）。
        //
        // ⟲ を出さないのは次の場合で、いずれも「既に既定値」または「インライン値の行ではない」:
        //   ・上書きがまだ無い（override_index = -1。埋込モードのまま値を見ているだけ）
        //   ・.mat アセット種別の上書き（インライン値を持たない）
        // モードを「インライン上書き」へ切り替えた直後もまだ上書きが無いので ⟲ は出ないが、
        // 最初の編集で SET_MATERIAL_OVERRIDE → ACTOR_COMPONENTS 再送 → 再構築が走り ⟲ が現れる。
        string? InlineResetPath(string serdeField)
            => mat.Mode == MaterialModeInline && mat.OverrideIndex != NoMaterialOverrideIndex
                ? string.Format(CultureInfo.InvariantCulture, MaterialInlineResetPathFormat,
                                mat.OverrideIndex, serdeField)
                : null;

        // 行要素に ⟲ を添える（リセット不能なら行をそのまま返す）。
        UIElement WithInlineReset(UIElement row, string serdeField, string label)
        {
            var path = InlineResetPath(serdeField);
            return path is null
                ? row
                : WithFieldReset(row, info.SlotIdx, ModelComponentType, path, label);
        }

        // base_color スウォッチ（Sprite カラーの市松実装を流用）
        var baseColorSwatch = BuildColorSwatch(curR, curG, curB, curA);
        baseColorSwatch.swatch.MouseLeftButtonUp += (_, _) =>
        {
            var result = ColorPickerWindow.ShowDialog(Window.GetWindow(this), curR, curG, curB, curA);
            if (result is null) return;
            (curR, curG, curB, curA) = result.Value;
            baseColorSwatch.setColor(curR, curG, curB, curA);
            SendInline();
        };
        var baseColorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        baseColorRow.Children.Add(new TextBlock
        {
            Text = "ベースカラー", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        baseColorRow.Children.Add(baseColorSwatch.swatch);
        inlinePanel.Children.Add(WithInlineReset(baseColorRow, "base_color", "ベースカラー"));

        // metallic / roughness スライダー（0..1）
        // 0..1 は glTF PBR の定義そのもので、ランタイム側 clamp 由来ではないため
        // 値域表（ComponentFieldRanges）には載せず、この専用スライダー行のまま ⟲ だけ添える。
        inlinePanel.Children.Add(WithInlineReset(
            BuildMaterialSliderRow("メタリック", curMetallic, v => { curMetallic = v; SendInline(); }),
            "metallic", "メタリック"));
        inlinePanel.Children.Add(WithInlineReset(
            BuildMaterialSliderRow("ラフネス", curRoughness, v => { curRoughness = v; SendInline(); }),
            "roughness", "ラフネス"));

        // 拡散透過スライダー（0..1。葉・布・紙の逆光透け＝KHR_materials_diffuse_transmission 簡易版）。
        // 【一時無効化】パラメータの割に制御が難しく狙った見た目にならないため 2026-07-20 時点で
        // 非表示にしている（削除ではない）。ランタイム側も build_material_uniform で 0.0 を強制しており、
        // ここを操作しても反映されない。再有効化する場合はこの Visibility.Collapsed を外す。
        // ⟲ を包んだ側に Visibility を掛ける（包んだ側を隠さないとボタンだけ残る）。
        var diffuseTransmissionRow = WithInlineReset(
            BuildMaterialSliderRow("拡散透過", curDiffuseTransmission, v => { curDiffuseTransmission = v; SendInline(); }),
            "diffuse_transmission", "拡散透過");
        diffuseTransmissionRow.Visibility = Visibility.Collapsed;
        inlinePanel.Children.Add(diffuseTransmissionRow);

        // MR テクスチャ無視トグル（常時表示）。glTF PBR は実効 metallic/roughness = factor × MR テクスチャのため、
        // MR テクスチャ持ちの面はスライダを最大にしても実効 roughness をテクスチャ値以上へ上げられない。
        // ON にすると MR テクスチャの乗算をスキップし、上の metallic/roughness スライダ値を実効値にする。
        // MR テクスチャの有無はエディタからは判別できないため、条件表示せず常に出す。
        var mrIgnoreRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        mrIgnoreRow.Children.Add(new TextBlock
        {
            Text = "MRテクスチャを無視", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        var mrIgnoreCheck = new CheckBox
        {
            IsChecked = curMrTexIgnore, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        mrIgnoreCheck.Checked   += (_, _) => { curMrTexIgnore = true;  SendInline(); };
        mrIgnoreCheck.Unchecked += (_, _) => { curMrTexIgnore = false; SendInline(); };
        mrIgnoreRow.Children.Add(mrIgnoreCheck);
        inlinePanel.Children.Add(mrIgnoreRow);

        // emissive スウォッチ（RGB のみ。ColorPickerWindow は a 必須のため a=1 固定で呼び出し RGB だけ使う）
        var emissiveSwatch = BuildColorSwatch(curER, curEG, curEB, 1f);
        emissiveSwatch.swatch.MouseLeftButtonUp += (_, _) =>
        {
            var result = ColorPickerWindow.ShowDialog(Window.GetWindow(this), curER, curEG, curEB, 1f);
            if (result is null) return;
            (curER, curEG, curEB, _) = result.Value;
            emissiveSwatch.setColor(curER, curEG, curEB, 1f);
            SendInline();
        };
        var emissiveRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        emissiveRow.Children.Add(new TextBlock
        {
            Text = "発光色", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        emissiveRow.Children.Add(emissiveSwatch.swatch);
        inlinePanel.Children.Add(WithInlineReset(emissiveRow, "emissive", "発光色"));

        // 屈折率（IOR）／透過率行への前方参照。alpha_mode コンボの変更時に表示/非表示を切り替えるため、
        // コンボのハンドラより前に宣言する（クロージャは変数を捕捉するので後から代入した実体が見える）。
        UIElement? iorRowElement = null;
        UIElement? transmissionRowElement = null;

        // alpha_mode ドロップダウン（opaque/mask/blend）
        var alphaModeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        alphaModeRow.Children.Add(new TextBlock
        {
            Text = "アルファモード", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        var alphaModeCombo = new ComboBox { Width = 100, FontSize = 11 };
        string[] alphaModeValues = ["opaque", "mask", "blend"];
        string[] alphaModeLabels = ["不透明", "マスク", "ブレンド"];
        foreach (var lbl in alphaModeLabels) alphaModeCombo.Items.Add(lbl);
        alphaModeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(alphaModeValues, curAlphaMode));
        alphaModeCombo.SelectionChanged += (_, _) =>
        {
            if (alphaModeCombo.SelectedIndex < 0) return;
            curAlphaMode = alphaModeValues[alphaModeCombo.SelectedIndex];
            // 屈折率・透過率は Blend のときのみ意味を持つため、その行だけ表示/非表示を切り替える
            //（条件付き表示の基本方針。ライトの UpdateKindVisibility と同じ流儀）。
            var showGlass = curAlphaMode == "blend" ? Visibility.Visible : Visibility.Collapsed;
            if (iorRowElement != null)          iorRowElement.Visibility = showGlass;
            if (transmissionRowElement != null) transmissionRowElement.Visibility = showGlass;
            SendInline();
        };
        alphaModeRow.Children.Add(alphaModeCombo);
        inlinePanel.Children.Add(alphaModeRow);

        // alpha_cutoff 数値フィールド（alpha_mode=mask のときに参照される閾値）。
        // 行の作りと ⟲ の配線は共通入口 BuildResettableFloatRow + WithInlineReset に一任する。
        // 値域表には載っていないので素の数値行になる。
        inlinePanel.Children.Add(WithInlineReset(
            BuildResettableFloatRow(
                info.SlotIdx, ModelComponentType, "カットオフ", curAlphaCutoff,
                // 値域引き当てを行わせないため field は null（⟲ は WithInlineReset が添える）。
                field: null, MaterialNumberFormat,
                v => { curAlphaCutoff = v; SendInline(); }),
            "alpha_cutoff", "カットオフ"));

        // 屈折率（IOR, Phase RT-Translucency）。AlphaMode=Blend のときだけ表示する（条件付き表示）。
        // レンダリング機能の「半透明＝レイトレ」選択時、Blend マテリアルのスクリーンスペース屈折に使う。
        // 1.0=屈折なし、ガラス≈1.5、水≈1.33。
        // 現実的な屈折率レンジ（1.0=なし〜ダイヤ級 2.4）へ UI 側でクランプする。
        // これは C# 側の目安であってランタイムの clamp ではないため値域表には載せない
        //（載せるとスライダー化され「ランタイムが受け付ける値に手が届かない」状態になる）。
        var iorRow = WithInlineReset(
            BuildResettableFloatRow(
                info.SlotIdx, ModelComponentType, "屈折率", curIor,
                field: null, MaterialNumberFormat,
                v => { curIor = Math.Clamp(v, MaterialIorMin, MaterialIorMax); SendInline(); }),
            "ior", "屈折率");
        // 初期表示は現在の alpha_mode に応じる（Blend のみ表示）。⟲ ごと隠すため包んだ側に掛ける。
        iorRow.Visibility = curAlphaMode == "blend" ? Visibility.Visible : Visibility.Collapsed;
        iorRowElement = iorRow;
        inlinePanel.Children.Add(iorRow);

        // 透過率（transmission, ガラス表現）。AlphaMode=Blend のときだけ表示する（条件付き表示）。
        // アルファ（被覆）と分離した「向こうがどれだけ透けるか」。0.0=従来動作、1.0=最大透過。
        // レンダリング機能の「半透明＝レイトレ」選択時、Blend マテリアルの屈折透過合成に使う。
        // 透過率は 0..1（glTF の定義）。こちらもランタイム clamp 由来ではないため値域表には載せない。
        var transmissionRow = WithInlineReset(
            BuildResettableFloatRow(
                info.SlotIdx, ModelComponentType, "透過率", curTransmission,
                field: null, MaterialNumberFormat,
                v => { curTransmission = Math.Clamp(v, MaterialNormalizedMin, MaterialNormalizedMax); SendInline(); }),
            "transmission", "透過率");
        // 初期表示は現在の alpha_mode に応じる（Blend のみ表示）。⟲ ごと隠すため包んだ側に掛ける。
        transmissionRow.Visibility = curAlphaMode == "blend" ? Visibility.Visible : Visibility.Collapsed;
        transmissionRowElement = transmissionRow;
        inlinePanel.Children.Add(transmissionRow);

        // cull_face ドロップダウン（back/front/none）。
        // カリング面は全マテリアルで意味を持つが、値の送信経路はインライン上書き（SET_MATERIAL_OVERRIDE:"inline"）
        // しか無いため、他のインライン項目と同じ inlinePanel 内に置く。
        var cullFaceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        cullFaceRow.Children.Add(new TextBlock
        {
            Text = "カリング面", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        var cullFaceCombo = new ComboBox { Width = 100, FontSize = 11 };
        foreach (var lbl in CullFaceLabels) cullFaceCombo.Items.Add(lbl);
        // 未知値（旧データ・不正値）は先頭 = "back" にフォールバック（ランタイムの parse_cull_face と同じ扱い）。
        cullFaceCombo.SelectedIndex = Math.Max(0, Array.IndexOf(CullFaceValues, curCullFace));
        cullFaceCombo.SelectionChanged += (_, _) =>
        {
            if (cullFaceCombo.SelectedIndex < 0) return;
            curCullFace = CullFaceValues[cullFaceCombo.SelectedIndex];
            SendInline();
        };
        cullFaceRow.Children.Add(cullFaceCombo);
        inlinePanel.Children.Add(cullFaceRow);

        // シェーディングモデル ドロップダウン（0=標準PBR / 1..3=シェーディングアセット定義, Phase L3-a）。
        // 値は G-Buffer の surface_id へパックされ、ライティングの shade_model_N 分岐を選ぶ。
        // カリング面と同じく送信経路はインライン上書き（SET_MATERIAL_OVERRIDE:"inline"）しか無いため、
        // 他のインライン項目と同じ inlinePanel 内に置く。
        var shadingModelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        shadingModelRow.Children.Add(new TextBlock
        {
            Text = "シェーディングモデル", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = ShadingModelTooltip,
        });
        var shadingModelCombo = new ComboBox { Width = 100, FontSize = 11, ToolTip = ShadingModelTooltip };
        foreach (var lbl in ShadingModelLabels) shadingModelCombo.Items.Add(lbl);
        // 範囲外（旧データ・不正値）は先頭 = 標準 PBR にフォールバック（ランタイムの 2bit マスクと同じ安全側）。
        shadingModelCombo.SelectedIndex = curShadingModel >= 0 && curShadingModel < ShadingModelLabels.Length
            ? curShadingModel : ShadingModelDefaultPbr;
        shadingModelCombo.SelectionChanged += (_, _) =>
        {
            if (shadingModelCombo.SelectedIndex < 0) return;
            curShadingModel = shadingModelCombo.SelectedIndex; // 添字＝送信値（0..3）
            SendInline();
        };
        shadingModelRow.Children.Add(shadingModelCombo);
        inlinePanel.Children.Add(shadingModelRow);

        content.Children.Add(inlinePanel);

        // ── 埋込に戻すボタン ─────────────────────────────────
        var resetBtn = new Button
        {
            Content = "埋込に戻す",
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x28, 0x20)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)), BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 8, 2), FontSize = 10, Margin = new Thickness(0, 6, 0, 0),
            Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left,
        };
        resetBtn.Click += (_, _) =>
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_MATERIAL_OVERRIDE:{_currentActorId},{info.SlotIdx},{mat.Slot},{{\"kind\":\"embedded\"}}");
            modeCombo.SelectedIndex = 0;
        };
        content.Children.Add(resetBtn);

        // モード切替でウィジェットの表示/非表示を切り替える。
        // "mat"/"inline" へ切り替えた瞬間はまだユーザーが値を選んでいないため送信しない
        // （.mat はファイル選択時、インラインは編集操作時にそれぞれ SendInline/割当処理が送信する）。
        // ただし埋込へ戻す場合は即座に送信する（値の再選択を要求しないため）。
        modeCombo.SelectionChanged += (_, _) =>
        {
            var idx = modeCombo.SelectedIndex;
            matFileRow.Visibility  = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
            inlinePanel.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (idx == 0 && _currentActorId >= 0)
                _runtime?.SendToRuntime($"SET_MATERIAL_OVERRIDE:{_currentActorId},{info.SlotIdx},{mat.Slot},{{\"kind\":\"embedded\"}}");
        };

        var header = new TextBlock
        {
            Text = $"#{mat.Slot} {(string.IsNullOrEmpty(mat.Name) ? "(無名)" : mat.Name)}  [{ModeLabel(mat.Mode)}]",
            FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
        };
        var slotExpander = new Expander
        {
            Header = header, Content = content,
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
        };
        // マテリアル値の編集は SET_MATERIAL_OVERRIDE → ACTOR_COMPONENTS 再送 →
        // インスペクタ全再構築を招くため、開閉状態を退避して復元する
        // （これを入れないと 1 項目編集するたびにスロットが閉じる）。
        // キーは「モデルスロット添字 + マテリアルスロット番号」（どちらも構造上の識別子で、
        // マテリアル名やモードが変わっても不変）。既定は「閉」（従来挙動）。
        _expandStates.Track(slotExpander,
            $"{ExpandKeyMaterialSlotPrefix}{info.SlotIdx}:{mat.Slot}", defaultExpanded: false);
        return slotExpander;
    }

    /// <summary>
    /// 市松背景付きのカラースウォッチ（Border）を生成する。クリックイベントは呼び出し側で購読し、
    /// 色更新時は返り値の setColor でスウォッチ表示を更新する（Sprite カラー編集 UI の実装を共通化）。
    /// </summary>
    private (Border swatch, Action<float, float, float, float> setColor) BuildColorSwatch(float r, float g, float b, float a)
    {
        var checkerGrid = new Grid();
        for (int ci = 0; ci < 2; ci++)
            checkerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int ri = 0; ri < 2; ri++)
            checkerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int ri = 0; ri < 2; ri++)
            for (int ci = 0; ci < 2; ci++)
            {
                bool dark = (ri + ci) % 2 == 0;
                var cell = new Border { Background = dark
                    ? new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60))
                    : new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)) };
                Grid.SetRow(cell, ri); Grid.SetColumn(cell, ci);
                checkerGrid.Children.Add(cell);
            }
        var overlay = new Border
        {
            Background = new SolidColorBrush(
                Color.FromArgb((byte)(a * 255), LinearToSrgbByte(r), LinearToSrgbByte(g), LinearToSrgbByte(b))),
        };
        var panel = new Grid();
        panel.Children.Add(checkerGrid);
        panel.Children.Add(overlay);

        var swatch = new Border
        {
            Width = 120, Height = 20, Margin = new Thickness(0, 1, 0, 1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = panel,
        };

        void SetColor(float nr, float ng, float nb, float na) =>
            overlay.Background = new SolidColorBrush(
                Color.FromArgb((byte)(na * 255), LinearToSrgbByte(nr), LinearToSrgbByte(ng), LinearToSrgbByte(nb)));

        return (swatch, SetColor);
    }

    /// <summary>[0..1] レンジのスライダー + 数値ボックス行を生成する（メタリック/ラフネス用）。</summary>
    private UIElement BuildMaterialSliderRow(string label, float value, Action<float> onChange)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

        var lbl = new TextBlock
        {
            Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
        };
        var slider = new Slider
        {
            Minimum = 0, Maximum = 1, Value = value, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 4, 0),
        };
        var box = new TextBox
        {
            Text = value.ToString("F2", CultureInfo.InvariantCulture),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
        };

        bool updating = false;
        slider.ValueChanged += (_, e) =>
        {
            if (updating) return;
            updating = true;
            box.Text = ((float)e.NewValue).ToString("F2", CultureInfo.InvariantCulture);
            onChange((float)e.NewValue);
            updating = false;
        };
        void CommitBox()
        {
            if (updating) return;
            if (!float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
            v = Math.Clamp(v, 0f, 1f);
            updating = true;
            box.Text = v.ToString("F2", CultureInfo.InvariantCulture);
            slider.Value = v;
            updating = false;
            onChange(v);
        }
        box.LostFocus += (_, _) => CommitBox();
        box.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitBox(); e.Handled = true; } };

        Grid.SetColumn(lbl, 0);    grid.Children.Add(lbl);
        Grid.SetColumn(slider, 1); grid.Children.Add(slider);
        Grid.SetColumn(box, 2);    grid.Children.Add(box);
        return grid;
    }

    // ── SpriteComponent inspector ─────────────────────────────

    /// <summary>SpriteComponent のインスペクター UI を構築して返す。</summary>
    private UIElement BuildSpriteSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // テクスチャパス選択行
        sp.Children.Add(FileRefBuilder.Build(
            "テクスチャ", info.TexturePath,
            [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".webp"],
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "テクスチャファイルを選択",
                    Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.webp|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path =>
            {
                if (_currentActorId < 0) return;
                // 絶対パスを仮想パスに変換してからランタイムへ送信する
                var virtualPath = VirtualPath.ToVirtual(path, _assetsPath);
                _runtime?.SendToRuntime($"SET_SPRITE_PATH:{_currentActorId},{info.SlotIdx},{virtualPath}");
            }));

        // ポストエフェクト参照行（.postfx アセット。テクスチャ参照と同じ FileRefBuilder + D&D の流儀）
        // 空文字列 virtualPath を送るとランタイム側でポストエフェクト適用を無効化（クリア）する。
        sp.Children.Add(FileRefBuilder.Build(
            "PostFX", info.PostFxPath,
            [".postfx"],
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "ポストエフェクトファイルを選択",
                    Filter = "ポストエフェクトファイル|*.postfx|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path =>
            {
                if (_currentActorId < 0) return;
                // 絶対パスを仮想パスに変換してからランタイムへ送信する
                var virtualPath = VirtualPath.ToVirtual(path, _assetsPath);
                _runtime?.SendToRuntime($"SET_SPRITE_POSTFX:{_currentActorId},{info.SlotIdx},{virtualPath}");
            }));

        // カラーピッカーボタン（現在色のスウォッチ表示）
        // クリックで ColorPickerWindow を開き、結果を即時送信する。
        float curR = info.SpriteR, curG = info.SpriteG, curB = info.SpriteB, curA = info.SpriteA;

        var colorSwatch = new Border
        {
            Width           = 120,
            Height          = 22,
            Margin          = new Thickness(0, 2, 0, 2),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            Background      = new SolidColorBrush(
                Color.FromArgb((byte)(curA * 255), LinearToSrgbByte(curR), LinearToSrgbByte(curG), LinearToSrgbByte(curB))),
            Cursor          = Cursors.Hand,
        };

        // 市松背景（透明部の視覚化）
        var checkerGrid = new Grid();
        for (int ci = 0; ci < 2; ci++)
            checkerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int ri = 0; ri < 2; ri++)
            checkerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int ri = 0; ri < 2; ri++)
            for (int ci = 0; ci < 2; ci++)
            {
                bool dark = (ri + ci) % 2 == 0;
                var cell = new Border { Background = dark
                    ? new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60))
                    : new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)) };
                Grid.SetRow(cell, ri); Grid.SetColumn(cell, ci);
                checkerGrid.Children.Add(cell);
            }
        var swatchOverlay = new Border
        {
            Background = new SolidColorBrush(
                Color.FromArgb((byte)(curA * 255), LinearToSrgbByte(curR), LinearToSrgbByte(curG), LinearToSrgbByte(curB))),
        };
        var swatchPanel = new Grid();
        swatchPanel.Children.Add(checkerGrid);
        swatchPanel.Children.Add(swatchOverlay);
        colorSwatch.Child = swatchPanel;

        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var colorLabel = new TextBlock
        {
            Text              = "カラー",
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Width             = 52,
        };
        colorRow.Children.Add(colorLabel);
        colorRow.Children.Add(colorSwatch);
        // 色は値域表の対象外（アルファ込み 4 成分・HDR 許容）だが、既定値へ戻す操作は提供する。
        sp.Children.Add(WithFieldReset(colorRow, info.SlotIdx, SpriteComponentType, "color", "カラー"));

        colorSwatch.MouseLeftButtonUp += (_, _) =>
        {
            var win = Window.GetWindow(this);
            var result = ColorPickerWindow.ShowDialog(win, curR, curG, curB, curA);
            if (result is null) return;
            (curR, curG, curB, curA) = result.Value;
            // スウォッチ色を更新する
            swatchOverlay.Background = new SolidColorBrush(
                Color.FromArgb((byte)(curA * 255), LinearToSrgbByte(curR), LinearToSrgbByte(curG), LinearToSrgbByte(curB)));
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"SET_SPRITE_COLOR:{_currentActorId},{info.SlotIdx},{curR},{curG},{curB},{curA}"));
        };

        // 幅・高さフィールド
        var rowW = BuildLabeledNumberRow("幅",  info.SpriteW);
        var rowH = BuildLabeledNumberRow("高さ", info.SpriteH);
        sp.Children.Add(rowW.element);
        sp.Children.Add(rowH.element);

        // サイズ変更を送信するローカル関数
        void CommitSize()
        {
            if (_currentActorId < 0) return;
            if (!float.TryParse(rowW.textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) return;
            if (!float.TryParse(rowH.textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return;
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"SET_SPRITE_SIZE:{_currentActorId},{info.SlotIdx},{w},{h}"));
        }

        // 幅・高さのテキストボックスにイベントを登録する
        rowW.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitSize(); e.Handled = true; } };
        rowW.textBox.LostFocus += (_, _) => CommitSize();
        rowH.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitSize(); e.Handled = true; } };
        rowH.textBox.LostFocus += (_, _) => CommitSize();
        NumericDragBehavior.SetOnDrag(rowW.textBox, CommitSize); NumericDragBehavior.SetOnDrag(rowH.textBox, CommitSize);

        // ── レイヤー（描画優先度）フィールド ──────────────────────
        // 大きいほど手前に描画される（既定 0・同値はヒエラルキー順）。
        // 比較は同一描画ゾーン内（ビューポートはゾーン単位で全キャンバス横断、
        // ワールドキャンバスはそのキャンバス内）で行われる。
        // 行の作りと「⟲ 既定値に戻す」は共通入口 BuildResettableFloatRow に一任する。
        // SET キー体系は SET_SPRITE_LAYER、serde 名は layer（SpriteComponentData）。
        // 値域表には載せない（レイヤー番号に上限は無い）ので素の数値行になる。
        // 数値ドラッグ等で小数が入っても整数へ丸めてから送信する。
        var rowLayer = BuildResettableFloatRow(
            info.SlotIdx, SpriteComponentType, "レイヤー", info.SpriteLayer, "layer",
            SpriteLayerNumberFormat,
            v =>
            {
                if (_currentActorId < 0) return;
                var layer = (int)MathF.Round(v);
                _runtime?.SendToRuntime(FormattableString.Invariant(
                    $"SET_SPRITE_LAYER:{_currentActorId},{info.SlotIdx},{layer}"));
            });
        if (rowLayer is FrameworkElement layerFe)
            layerFe.ToolTip = "描画優先度。大きいほど手前に描画されます。\n同じ値はヒエラルキー順。同一描画ゾーン内で比較されます。";
        sp.Children.Add(rowLayer);

        return sp;
    }

    // ── AudioComponent inspector ──────────────────────────────

    /// <summary>パン値の下限（完全に左）。</summary>
    private const float AudioPanMin = -1f;
    /// <summary>パン値の上限（完全に右）。</summary>
    private const float AudioPanMax = 1f;
    /// <summary>音量の下限（無音）。</summary>
    private const float AudioVolumeMin = 0f;

    /// <summary>ライトの数値行の表示書式（強度・距離・角度とも小数 1 桁）。</summary>
    private const string LightNumberFormat = "F1";
    /// <summary>オーディオの距離行の表示書式（m 単位。小数 1 桁）。</summary>
    private const string AudioDistanceNumberFormat = "F1";
    /// <summary>オーディオの音量・パン行の表示書式（0..1 前後の値なので小数 2 桁）。</summary>
    private const string AudioLevelNumberFormat = "F2";
    /// <summary>スカイボックスの強度行の表示書式。</summary>
    private const string SkyboxNumberFormat = "F1";
    /// <summary>カメラの数値行の表示書式（FOV・クリップ面・正射高さとも小数 1 桁）。</summary>
    private const string CameraNumberFormat = "F1";
    /// <summary>コライダー（3D/2D 共通）の物理パラメータ行の表示書式。</summary>
    private const string ColliderNumberFormat = "F1";
    /// <summary>スプライトのレイヤー行の表示書式（整数）。</summary>
    private const string SpriteLayerNumberFormat = "F0";
    /// <summary>アニメータの再生速度行の表示書式。</summary>
    private const string AnimatorNumberFormat = "F2";
    /// <summary>マテリアルのインライン数値行（カットオフ・屈折率・透過率）の表示書式。</summary>
    private const string MaterialNumberFormat = "F2";

    /// <summary>
    /// AnimatorComponent のインスペクター UI を構築して返す。
    /// クリップ一覧（追加/削除）・既定クリップ・自動再生・速度を編集し、変更のたびに
    /// SET_ANIMATOR_CLIPS:{actor_dfs},{slot_idx},{json} で一括送信する
    /// （Rust 側 AnimatorComponentData は clips/default_clip/play_on_start/speed をまとめて 1 コマンドで更新する設計のため）。
    /// クリップは kind で 2 種類に分かれる:
    ///   - "keyframe": .anim アセット参照（path を使用）。従来のクリップ。
    ///   - "model"   : 同アクターの Model スロットが持つ glTF 内蔵アニメ（anim/loop_mode を使用）。
    /// 「タイムラインで編集」ボタンで AnimationTimelinePanel を開き、選択中クリップのキーフレーム編集へ遷移する
    /// （model クリップはタイムライン編集非対応のため案内のみ表示する）。
    /// </summary>
    private UIElement BuildAnimatorSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // ── clips JSON（[{"name":..,"kind":..,"path":..,"anim":..,"loop_mode":..},...]）を
        //    パースして編集用リストへ展開する。kind/anim/loop_mode は欠落耐性を持たせる
        //    （旧シーン・Rust 側 #[serde(default)] と同じ既定値: kind=keyframe, loop_mode=loop）。
        var clips = new List<(string Name, string Kind, string Path, string Anim, string LoopMode)>();
        try
        {
            using var doc = JsonDocument.Parse(info.AnimClipsJson);
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var name = c.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                var kind = c.TryGetProperty("kind", out var kp) ? kp.GetString() ?? "keyframe" : "keyframe";
                var path = c.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
                var anim = c.TryGetProperty("anim", out var ap) ? ap.GetString() ?? "" : "";
                var loopMode = c.TryGetProperty("loop_mode", out var lp) ? lp.GetString() ?? "loop" : "loop";
                clips.Add((name, kind, path, anim, loopMode));
            }
        }
        catch (JsonException) { /* 不正 JSON は空リスト扱い */ }

        // UI が保持する編集中の状態（コミット時にまとめて JSON 化する）
        var curDefaultClip = info.AnimDefaultClip;
        var curPlayOnStart = info.AnimPlayOnStart;
        var curSpeed       = info.AnimSpeed;

        // ── clips/default_clip/play_on_start/speed をまとめて 1 メッセージで送信するローカル関数 ──
        // kind/anim/loop_mode は keyframe クリップでも明示送信する（ラウンドトリップで情報を落とさないため）。
        void CommitAnimator()
        {
            if (_currentActorId < 0) return;
            var payload = new
            {
                clips = clips.Select(c => new
                {
                    name = c.Name, kind = c.Kind, path = c.Path, anim = c.Anim, loop_mode = c.LoopMode,
                }).ToArray(),
                default_clip = curDefaultClip,
                play_on_start = curPlayOnStart,
                speed        = curSpeed,
            };
            var json = JsonSerializer.Serialize(payload);
            _runtime?.SendToRuntime($"SET_ANIMATOR_CLIPS:{_currentActorId},{info.SlotIdx},{json}");
        }

        // ── クリップ一覧 ──────────────────────────────────────
        sp.Children.Add(new TextBlock
        {
            Text = "クリップ一覧", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, Margin = new Thickness(0, 0, 0, 2),
        });

        // ドラッグ中/通常時で切り替えるリスト背景（ProjectPanel からの .anim ドロップの可視化用）
        var clipListNormalBg = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        var clipListHoverBg  = new SolidColorBrush(Color.FromRgb(0x1A, 0x33, 0x1A));
        var clipList = new ListBox
        {
            Height = 76, Background = clipListNormalBg,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)), FontSize = 11,
            AllowDrop = true,
        };
        var defaultClipCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 2), FontSize = 11, Height = 22 };

        // ── 選択中クリップが kind=model のときだけ表示するループ種別編集行 ──
        var modelLoopRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        modelLoopRow.Children.Add(new TextBlock
        {
            Text = "ループ種別 (モデル)", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Width = 100, VerticalAlignment = VerticalAlignment.Center,
        });
        var modelLoopCombo = new ComboBox { Width = 90, FontSize = 11, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        modelLoopCombo.Items.Add(new ComboBoxItem { Content = "loop", Tag = "loop" });
        modelLoopCombo.Items.Add(new ComboBoxItem { Content = "once", Tag = "once" });
        modelLoopRow.Children.Add(modelLoopCombo);

        // clips / defaultClipCombo / modelLoopRow の内容を現在の clips リストから再構築するローカル関数
        void RebuildClipUi()
        {
            clipList.Items.Clear();
            foreach (var c in clips)
            {
                if (c.Kind == "model")
                {
                    var animLabel = string.IsNullOrEmpty(c.Anim) ? "(無名アニメ)" : c.Anim;
                    clipList.Items.Add($"🎬 {animLabel}  (モデル内蔵・{c.LoopMode})");
                }
                else
                {
                    clipList.Items.Add(string.IsNullOrEmpty(c.Name) ? $"📄 {c.Path}" : $"📄 {c.Name}  ({c.Path})");
                }
            }

            defaultClipCombo.SelectionChanged -= OnDefaultClipChanged;
            defaultClipCombo.Items.Clear();
            defaultClipCombo.Items.Add(new ComboBoxItem { Content = "（なし）", Tag = "" });
            foreach (var c in clips)
            {
                var label = !string.IsNullOrEmpty(c.Name) ? c.Name : (c.Kind == "model" ? c.Anim : c.Path);
                defaultClipCombo.Items.Add(new ComboBoxItem { Content = label, Tag = c.Name });
            }
            var idx = clips.FindIndex(c => c.Name == curDefaultClip);
            defaultClipCombo.SelectedIndex = idx >= 0 ? idx + 1 : 0;
            defaultClipCombo.SelectionChanged += OnDefaultClipChanged;

            UpdateModelLoopRow();
        }

        // 選択中クリップが model のときだけループ種別行を表示し、現在値を反映する
        void UpdateModelLoopRow()
        {
            var idx = clipList.SelectedIndex;
            if (idx >= 0 && idx < clips.Count && clips[idx].Kind == "model")
            {
                modelLoopRow.Visibility = Visibility.Visible;
                modelLoopCombo.SelectionChanged -= OnModelLoopChanged;
                modelLoopCombo.SelectedIndex = clips[idx].LoopMode == "once" ? 1 : 0;
                modelLoopCombo.SelectionChanged += OnModelLoopChanged;
            }
            else
            {
                modelLoopRow.Visibility = Visibility.Collapsed;
            }
        }

        void OnModelLoopChanged(object? s, SelectionChangedEventArgs e)
        {
            var idx = clipList.SelectedIndex;
            if (idx < 0 || idx >= clips.Count || clips[idx].Kind != "model") return;
            if (modelLoopCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;
            var c = clips[idx];
            if (c.LoopMode == mode) return;
            clips[idx] = (c.Name, c.Kind, c.Path, c.Anim, mode);
            RebuildClipUi();
            clipList.SelectedIndex = idx;
            CommitAnimator();
        }

        clipList.SelectionChanged += (_, _) => UpdateModelLoopRow();

        void OnDefaultClipChanged(object? s, SelectionChangedEventArgs e)
        {
            if (defaultClipCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string name) return;
            if (curDefaultClip == name) return;
            curDefaultClip = name;
            CommitAnimator();
        }

        // ── D&D: ProjectPanel からの .anim ドロップでキーフレームクリップを追加する ──
        void OnClipListDragOver(object? s, DragEventArgs e)
        {
            var accept = e.Data.GetDataPresent("SEEDProjectPaths") &&
                e.Data.GetData("SEEDProjectPaths") is string[] dragPaths &&
                dragPaths.Any(p => string.Equals(Path.GetExtension(p), ".anim", StringComparison.OrdinalIgnoreCase));
            e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
            clipList.Background = accept ? clipListHoverBg : clipListNormalBg;
            e.Handled = true;
        }
        clipList.DragEnter += OnClipListDragOver;
        clipList.DragOver   += OnClipListDragOver;
        clipList.DragLeave  += (_, e) => { clipList.Background = clipListNormalBg; e.Handled = true; };
        clipList.Drop += (_, e) =>
        {
            clipList.Background = clipListNormalBg;
            if (e.Data.GetDataPresent("SEEDProjectPaths") && e.Data.GetData("SEEDProjectPaths") is string[] dropPaths)
            {
                var added = false;
                foreach (var p in dropPaths)
                {
                    if (!string.Equals(Path.GetExtension(p), ".anim", StringComparison.OrdinalIgnoreCase)) continue;
                    var virtualPath = VirtualPath.ToVirtual(p, _assetsPath);
                    var name = Path.GetFileNameWithoutExtension(p);
                    clips.Add((name, "keyframe", virtualPath, "", "loop"));
                    added = true;
                }
                if (added) { RebuildClipUi(); CommitAnimator(); }
            }
            e.Handled = true;
        };

        sp.Children.Add(clipList);
        sp.Children.Add(modelLoopRow);

        var clipBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var addClipBtn = new Button
        {
            Content = "追加", Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)), BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 8, 2), FontSize = 10, Cursor = Cursors.Hand,
        };
        addClipBtn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Title = "アニメーションクリップを選択", Filter = "アニメーションクリップ|*.anim|すべてのファイル|*.*" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            var virtualPath = VirtualPath.ToVirtual(dlg.FileName, _assetsPath);
            var name = Path.GetFileNameWithoutExtension(dlg.FileName);
            // .anim アセット参照クリップとして明示的に kind:"keyframe" を付与する
            clips.Add((name, "keyframe", virtualPath, "", "loop"));
            RebuildClipUi();
            CommitAnimator();
        };
        var removeClipBtn = new Button
        {
            Content = "削除", Background = new SolidColorBrush(Color.FromRgb(0x40, 0x28, 0x28)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)), BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 8, 2), FontSize = 10, Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand,
        };
        removeClipBtn.Click += (_, _) =>
        {
            var idx = clipList.SelectedIndex;
            if (idx < 0 || idx >= clips.Count) return;
            clips.RemoveAt(idx);
            RebuildClipUi();
            CommitAnimator();
        };
        clipBtnRow.Children.Add(addClipBtn);
        clipBtnRow.Children.Add(removeClipBtn);
        sp.Children.Add(clipBtnRow);

        // ── モデル内蔵アニメを追加 ────────────────────────────
        // 同アクターの Model スロットが ACTOR_COMPONENTS で送ってくる animations 一覧（glTF 内蔵アニメ名）を
        // ポップアップメニューで列挙し、選択で model クリップを追加する。Model スロットが無い/アニメ0件なら無効化する。
        var modelAnims = GetModelSlotAnimations();
        var addModelClipBtn = new Button
        {
            Content = "モデル内蔵アニメを追加",
            Background = new SolidColorBrush(Color.FromRgb(0x28, 0x30, 0x40)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)), BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 8, 2), FontSize = 10, Margin = new Thickness(0, 4, 0, 0),
            Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (modelAnims.Count == 0)
        {
            addModelClipBtn.IsEnabled = false;
            addModelClipBtn.ToolTip = "同アクターの Model スロットに glTF 内蔵アニメがありません";
        }
        else
        {
            addModelClipBtn.ToolTip = "Model スロットの glTF 内蔵アニメから追加します";
            addModelClipBtn.Click += (_, _) =>
            {
                var menu = new ContextMenu();
                for (int i = 0; i < modelAnims.Count; i++)
                {
                    var animName = modelAnims[i];
                    var isFirst  = i == 0;
                    var label    = string.IsNullOrEmpty(animName) ? $"(無名アニメ #{i})" : animName;
                    var menuItem = new MenuItem
                    {
                        Header = isFirst ? label : $"{label}  ※先頭以外は現行GPU非対応",
                        // 先頭以外は薄字にして現行の制約（GPUスキニングは Model::animations[0] のみ再生可能）を示す
                        Foreground = isFirst
                            ? null
                            : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                        ToolTip = isFirst
                            ? null
                            : "現行GPUは Model 内の先頭アニメ (index 0) のみ再生可能です。このアニメは再生時にフォールバックされます。",
                    };
                    menuItem.Click += (_, _) =>
                    {
                        // {name: anim名, kind:"model", anim: anim名, loop_mode:"loop"} で追加する
                        clips.Add((animName, "model", "", animName, "loop"));
                        RebuildClipUi();
                        CommitAnimator();
                    };
                    menu.Items.Add(menuItem);
                }
                menu.PlacementTarget = addModelClipBtn;
                menu.IsOpen = true;
            };
        }
        sp.Children.Add(addModelClipBtn);

        // ── 既定クリップ ──────────────────────────────────────
        sp.Children.Add(new TextBlock
        {
            Text = "既定クリップ", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, Margin = new Thickness(0, 6, 0, 0),
        });
        sp.Children.Add(defaultClipCombo);
        RebuildClipUi();

        // ── 自動再生 ──────────────────────────────────────────
        var playOnStartRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 2) };
        playOnStartRow.Children.Add(new TextBlock
        {
            Text = "自動再生", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        var playOnStartCheck = new CheckBox { IsChecked = curPlayOnStart, VerticalAlignment = VerticalAlignment.Center };
        playOnStartCheck.Checked   += (_, _) => { curPlayOnStart = true;  CommitAnimator(); };
        playOnStartCheck.Unchecked += (_, _) => { curPlayOnStart = false; CommitAnimator(); };
        playOnStartRow.Children.Add(playOnStartCheck);
        sp.Children.Add(playOnStartRow);

        // ── 再生速度 ──────────────────────────────────────────
        // 行の作りと「⟲ 既定値に戻す」は共通入口 BuildResettableFloatRow に一任する。
        // Animator は SET_ANIMATOR_CLIPS で AnimatorComponentData を丸ごと送るため、
        // 送信キーという概念が無く serde 名（speed）がそのままフィールドパスになる。
        // ランタイム側に clamp が無い（負値で逆再生できる）ので値域表には載せない。
        sp.Children.Add(BuildResettableFloatRow(
            info.SlotIdx, AnimatorComponentType, "速度", curSpeed, "speed",
            AnimatorNumberFormat,
            v => { curSpeed = v; CommitAnimator(); }));

        // ── タイムラインで編集 ──────────────────────────────────
        var editTimelineBtn = new Button
        {
            Content = "タイムラインで編集", Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x20, 0x38)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 3, 8, 3), FontSize = 11, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        editTimelineBtn.Click += (_, _) =>
        {
            // 既定クリップ、無ければ先頭クリップを編集対象として開く
            // （tuple の各フィールドは name/path とも空文字を正当な値として取り得るため、
            //   「見つかったか」は FindIndex の結果で判定する）
            var targetIdx = clips.FindIndex(c => c.Name == curDefaultClip);
            if (targetIdx < 0 && clips.Count > 0) targetIdx = 0;
            if (targetIdx < 0)
            {
                MessageBox.Show(Window.GetWindow(this), "編集するクリップがありません。先にクリップを追加してください。",
                    "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var target = clips[targetIdx];
            if (target.Kind == "model")
            {
                // モデル内蔵アニメは ANIM_PREVIEW / ドープシート編集の対象外（Rust 側 Edit プレビュー未対応）
                MessageBox.Show(Window.GetWindow(this),
                    "モデル内蔵アニメはタイムラインで編集できません。再生設定（ループ種別など）は Inspector で編集してください。",
                    "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            TimelineEditRequested?.Invoke(target.Path);
        };
        sp.Children.Add(editTimelineBtn);

        return sp;
    }

    /// <summary>
    /// Inspector の「タイムラインで編集」ボタンが押されたことを通知する（引数はクリップの仮想パス）。
    /// MainWindow がこれを購読し、AnimationTimelinePanel を表示して該当クリップを開かせる。
    /// </summary>
    public event Action<string>? TimelineEditRequested;

    /// <summary>
    /// 現在のアクターが持つ Model スロットの glTF 内蔵アニメ名一覧を取得する。
    /// ACTOR_COMPONENTS（<see cref="_lastComponentsJson"/>）内の ModelComponent が送ってくる
    /// "animations" 配列（component_ops.rs 側で付与）を読み取る。複数 Model スロットがある場合は
    /// 最初にアニメを持つスロットのものを採用する（このアクターに 1 Model スロットのみの想定が主用途）。
    /// </summary>
    private List<string> GetModelSlotAnimations()
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(_lastComponentsJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(_lastComponentsJson);
            if (!doc.RootElement.TryGetProperty("components", out var comps)) return result;
            foreach (var comp in comps.EnumerateArray())
            {
                var type = comp.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                if (type != "ModelComponent") continue;
                if (!comp.TryGetProperty("animations", out var animsEl) || animsEl.ValueKind != JsonValueKind.Array) continue;
                foreach (var a in animsEl.EnumerateArray())
                    result.Add(a.GetString() ?? "");
                if (result.Count > 0) break;
            }
        }
        catch (JsonException) { /* 不正 JSON は空リスト扱い */ }
        return result;
    }

    /// <summary>
    /// LightComponent のインスペクター UI を構築して返す。
    /// 種別（ドロップダウン）・色・強度・range・スポット内外角・rect サイズ・影フラグを編集し、
    /// 変更時は SET_LIGHT_FIELD:{actor},{slot},{key},{value} を送信する。
    /// 種別に応じて関連フィールドのみ表示する。
    /// </summary>
    private UIElement BuildLightSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // フィールド変更をランタイムへ送信するローカル関数。
        void SendField(string key, string value)
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_LIGHT_FIELD:{_currentActorId},{info.SlotIdx},{key},{value}");
        }

        // ── 種別ドロップダウン ─────────────────────────────────
        var kinds = new[]
        {
            ("directional", "平行光 (Directional)"),
            ("point",       "点光源 (Point)"),
            ("spot",        "スポット (Spot)"),
            ("rect",        "矩形エリア (Rect)"),
        };
        var kindRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        kindRow.Children.Add(new TextBlock
        {
            Text = "種別", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        var kindCombo = new ComboBox { Width = 170, FontSize = 11, Margin = new Thickness(4, 0, 0, 0) };
        foreach (var (val, label) in kinds)
            kindCombo.Items.Add(new ComboBoxItem { Content = label, Tag = val });
        var curKindIdx = Array.FindIndex(kinds, t => t.Item1 == info.LightKind);
        kindCombo.SelectedIndex = curKindIdx >= 0 ? curKindIdx : 0;
        kindRow.Children.Add(kindCombo);
        sp.Children.Add(kindRow);

        // ── 色（リニア RGB）────────────────────────────────────
        float curR = info.LightR, curG = info.LightG, curB = info.LightB;
        var colorSwatch = new Border
        {
            Width = 120, Height = 22, Margin = new Thickness(0, 2, 0, 2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(
                Color.FromRgb(LinearToSrgbByte(curR), LinearToSrgbByte(curG), LinearToSrgbByte(curB))),
            Cursor = Cursors.Hand,
        };
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        colorRow.Children.Add(new TextBlock
        {
            Text = "色", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        colorRow.Children.Add(colorSwatch);
        colorSwatch.MouseLeftButtonDown += (_, _) =>
        {
            // ライト色はアルファを持たない（a=1 固定）。ColorPickerWindow の a は無視する。
            var result = ColorPickerWindow.ShowDialog(Window.GetWindow(this), curR, curG, curB, 1f);
            if (result is null) return;
            (curR, curG, curB, _) = result.Value;
            colorSwatch.Background = new SolidColorBrush(
                Color.FromRgb(LinearToSrgbByte(curR), LinearToSrgbByte(curG), LinearToSrgbByte(curB)));
            SendField("color", FormattableString.Invariant($"{curR},{curG},{curB}"));
        };
        // 色は値域表の対象外（HDR 許容で上限が無い）だが、既定値へ戻す操作は数値行と同じく提供する。
        sp.Children.Add(WithFieldReset(colorRow, info.SlotIdx, LightComponentType, "color", "色"));

        // ── 数値フィールド + SET_LIGHT_FIELD 送信をまとめて構築 ──
        // 行の作り（スライダー / 数値入力）と「⟲ 既定値に戻す」は共通入口
        // BuildResettableFloatRow に一任する（行ごとの個別対応を書かないため）。
        // serdeField は Rust の LightComponentData のフィールド名。SET キーと綴りが
        // 異なるもの（角度系）だけ明示的に渡す。
        // 戻り値の行要素を種別ごとの表示切替に使えるよう返す。
        UIElement AddFloatRow(string label, float value, string key, string? serdeField = null)
        {
            var row = BuildResettableFloatRow(
                info.SlotIdx, LightComponentType, label, value, serdeField ?? key,
                LightNumberFormat,
                v => SendField(key, v.ToString(CultureInfo.InvariantCulture)));
            sp.Children.Add(row);
            return row;
        }

        // 強度は全種別で共通。
        AddFloatRow("強度", info.LightIntensity, "intensity");

        // 種別ごとに表示切替するフィールド群。
        var rangeRow  = AddFloatRow("減衰距離 (Range)", info.LightRange,       "range");
        // 角度 2 行は SET キー（inner_angle / outer_angle）と serde 名（*_deg）が食い違う。
        // リセットと値域引き当ては serde 名で行う必要があるため明示的に渡す。
        var innerRow  = AddFloatRow("内側角 (°)",        info.LightInnerAngle,  "inner_angle", "inner_angle_deg");
        var outerRow  = AddFloatRow("外側角 (°)",        info.LightOuterAngle,  "outer_angle", "outer_angle_deg");
        var rectWRow  = AddFloatRow("矩形 幅",            info.LightRectWidth,   "rect_width");
        var rectHRow  = AddFloatRow("矩形 高さ",          info.LightRectHeight,  "rect_height");
        // ソフト影半径: directional は角径(度)、point/spot/rect はワールド半径。0 でハード影。
        // RT 影が有効なときのみ影のボケに反映される（品質オプション）。全種別で表示。
        AddFloatRow("ソフト影半径", info.LightSoftRadius, "soft_radius");
        // 疑似バウンス（間接光近似）強度。0..1 目安・既定 0。影の中にも光が回り込む見た目を出す。
        // directional は減衰の基準距離が無いため対象外＝directional 選択時は非表示にする。
        var bounceRow = AddFloatRow("バウンス強度", info.LightBounceIntensity, "bounce_intensity");

        // ── 影を落とす（R2 で使用）────────────────────────────
        var shadowRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        shadowRow.Children.Add(new TextBlock
        {
            Text = "影を落とす", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        var shadowCheck = new CheckBox
        {
            IsChecked = info.LightCastShadows, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        shadowCheck.Checked   += (_, _) => SendField("cast_shadows", "1");
        shadowCheck.Unchecked += (_, _) => SendField("cast_shadows", "0");
        shadowRow.Children.Add(shadowCheck);
        sp.Children.Add(shadowRow);

        // 種別に応じた関連フィールドの表示切替。
        void UpdateKindVisibility(string kind)
        {
            bool isPoint = kind == "point";
            bool isSpot  = kind == "spot";
            bool isRect  = kind == "rect";
            // range は point / spot / rect で有効（directional は減衰なし）。
            rangeRow.Visibility = (isPoint || isSpot || isRect) ? Visibility.Visible : Visibility.Collapsed;
            innerRow.Visibility = isSpot ? Visibility.Visible : Visibility.Collapsed;
            outerRow.Visibility = isSpot ? Visibility.Visible : Visibility.Collapsed;
            rectWRow.Visibility = isRect ? Visibility.Visible : Visibility.Collapsed;
            rectHRow.Visibility = isRect ? Visibility.Visible : Visibility.Collapsed;
            // バウンス（疑似間接光）は directional 以外（point/spot/rect）でのみ表示する。
            bounceRow.Visibility = (isPoint || isSpot || isRect) ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateKindVisibility(info.LightKind);
        kindCombo.SelectionChanged += (_, _) =>
        {
            if (kindCombo.SelectedItem is ComboBoxItem item && item.Tag is string kind)
            {
                SendField("kind", kind);
                UpdateKindVisibility(kind);
            }
        };

        return sp;
    }

    /// <summary>
    /// WaterVolumeComponent のインスペクター UI を構築して返す。
    /// 「領域」「色と透明度」「岸のフォーム」「波」「反射・屈折」の 5 セクションで構成し、
    /// 変更時は SET_WATER_FIELD:{actor},{slot},{key},{value} を送信する。
    /// 種別（Ocean/Region/Spline）に応じて、その種別で意味を持たない行は非表示にする
    /// （Ocean のとき領域半径を隠し、Region のとき海の描画半径を隠す）。
    /// </summary>
    /// <summary>
    /// 水面シェーディングアセットのパラメータ宣言（Phase W8.2）の行を生成して親へ追加する。
    ///
    /// ランタイムが送ってきた JSON 配列（名前・型・ラベル・範囲・現在値・リセット可否）から、
    /// 型ごとに行の形を選ぶ:
    ///   color … カラーピッカー（リニア RGB。アルファは持たない）
    ///   range … スライダー＋数値ボックス（min/max はアセットの宣言どおり）
    ///   float … 数値行（範囲なし）
    ///
    /// アセットが <c>@reset</c> を付けた行だけ、右端に「デフォルトに戻す」ボタンを添える
    /// （RESET_WATER_SHADER_PARAM を送る。Undo はランタイム側の共通機構が担う）。
    ///
    /// **構文解析は一切行わない**（正典はランタイムの water/shade_params.rs）。
    /// 値の送信は SET_WATER_SHADER_PARAM で、型に依らず常に 4 成分を送る。
    /// </summary>
    public void AddShaderParamRows(StackPanel parent, ShaderParamTarget target)
    {
        // 宣言が無ければ何も作らない（アセット未指定・読めないパス・宣言なし）。
        if (string.IsNullOrWhiteSpace(target.ParamsJson)) return;

        // JsonDocument は using で確実に解放し、要素は Clone して外へ持ち出す
        //（RootElement を Document の寿命より長く持ち回らないための定石。
        //  プラグインフィールド行と同じ流儀）。
        List<JsonElement> items;
        try
        {
            using var doc = JsonDocument.Parse(target.ParamsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            items = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch (JsonException)
        {
            // 壊れた JSON は「パラメータ無し」として扱う（インスペクタ全体を落とさない）。
            return;
        }
        if (items.Count == 0) return;

        // パラメータ 1 個の値をランタイムへ送る（常に 4 成分）。
        void SendParam(string name, float x, float y, float z, float w)
        {
            if (target.RequiresActorSelection && _currentActorId < 0) return;
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"{target.SetPrefix()}{name},{x},{y},{z},{w}"));
        }

        // パラメータ 1 個をアセットの既定値へ戻す（`@reset` の付いた行のボタン）。
        // ランタイムはシーン側の上書き値を消すだけで、既定値の解釈はアセットが持ち続ける。
        void SendReset(string name)
        {
            if (target.RequiresActorSelection && _currentActorId < 0) return;
            _runtime?.SendToRuntime(FormattableString.Invariant($"{target.ResetPrefix()}{name}"));
        }

        // 行の右端へ「デフォルトに戻す」ボタンを添えた要素を返す
        //（見た目・包み方はスクリプトの [ResetButton] と共通のファクトリに任せる）。
        UIElement WithResetButton(UIElement row, string name, string label)
            => SEEDEditor.Controls.ResetButtonFactory.Wrap(
                row,
                $"「{label}」をアセットの既定値に戻す（Ctrl+Z で取り消せます）",
                () => SendReset(name));

        foreach (var item in items)
        {
            var name = item.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(name)) continue;
            var type  = item.TryGetProperty("type",  out var pt) ? pt.GetString() ?? "" : "";
            var label = item.TryGetProperty("label", out var pl) ? pl.GetString() ?? name : name;
            var min   = item.TryGetProperty("min",   out var pmin) ? pmin.GetSingle() : 0f;
            var max   = item.TryGetProperty("max",   out var pmax) ? pmax.GetSingle() : 1f;
            // `@reset` 属性の有無（旧ランタイムはキーを送らない＝ボタン無しで安全側）。
            var canReset = item.TryGetProperty("reset", out var pr) && pr.ValueKind == JsonValueKind.True;
            // `@ref` 属性（Phase W8.3）。true の行は値の入力欄ではなく参照バインド行になる。
            // 旧ランタイムはキーを送らないので「参照ではない＝従来どおりの値行」に倒す。
            var isRef = item.TryGetProperty("ref", out var pref) && pref.ValueKind == JsonValueKind.True;
            // 現在のバインド先 "アクタ名|スロット名|変数名"（未設定なら空文字列）。
            var binding = item.TryGetProperty("binding", out var pb) ? pb.GetString() ?? "" : "";
            // バインドが今解決できているか。キーが無い旧ランタイムは「解決済み」＝警告なしに倒す。
            var bindingOk = !item.TryGetProperty("binding_ok", out var pbo)
                            || pbo.ValueKind != JsonValueKind.False;
            // 値は常に 4 成分。欠けている場合は 0 で埋める（旧ランタイム耐性）。
            float[] v = new float[ShaderParamComponentCount];
            if (item.TryGetProperty("value", out var pv) && pv.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var c in pv.EnumerateArray())
                {
                    if (i >= ShaderParamComponentCount) break;
                    v[i++] = c.GetSingle();
                }
            }

            // ── `@ref` の行は値 UI を作らず、参照バインド行に差し替える ──
            //    値の入力は「バインド元から流れてくる」ので、ここで編集させない。
            if (isRef)
            {
                parent.Children.Add(BuildShaderBindingRow(
                    target, name, label, type, binding, bindingOk, canReset, SendReset));
                continue;
            }

            switch (type)
            {
                // ── 色（リニア RGB。アセット側は vec3 として読む）──
                case "color":
                {
                    float curR = v[0], curG = v[1], curB = v[2];
                    var (swatch, setColor) = BuildColorSwatch(curR, curG, curB, WaterColorAlpha);
                    var row = new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2),
                    };
                    row.Children.Add(new TextBlock
                    {
                        Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                        FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
                    });
                    row.Children.Add(swatch);
                    swatch.MouseLeftButtonDown += (_, _) =>
                    {
                        var result = ColorPickerWindow.ShowDialog(
                            target.DialogOwner ?? Window.GetWindow(this),
                            curR, curG, curB, WaterColorAlpha);
                        if (result is null) return;
                        (curR, curG, curB, _) = result.Value;
                        setColor(curR, curG, curB, WaterColorAlpha);
                        // 4 成分目は色では使わないので 0 を送る（受け側もそのまま保存する）。
                        SendParam(name, curR, curG, curB, 0f);
                    };
                    row.ToolTip = $"{name}（{target.Description}のパラメータ）";
                    parent.Children.Add(canReset ? WithResetButton(row, name, label) : row);
                    break;
                }
                // ── 範囲つきスカラー（スライダー）──
                case "range":
                {
                    var row = BuildRangeSliderRow(label, v[0], min, max,
                        nv => SendParam(name, nv, 0f, 0f, 0f));
                    if (row is FrameworkElement fe) fe.ToolTip = $"{name}（{min} 〜 {max}）";
                    parent.Children.Add(canReset ? WithResetButton(row, name, label) : row);
                    break;
                }
                // ── 範囲なしスカラー（数値行）。未知の型もここへ落とす（値は編集できる）──
                default:
                {
                    var numeric = BuildLabeledNumberRow(label, v[0], ShaderParamNumberFormat);
                    void Commit()
                    {
                        if (!float.TryParse(numeric.textBox.Text, NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var nv)) return;
                        SendParam(name, nv, 0f, 0f, 0f);
                    }
                    numeric.textBox.KeyDown += (_, e) =>
                    {
                        if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; }
                    };
                    numeric.textBox.LostFocus += (_, _) => Commit();
                    NumericDragBehavior.SetOnDrag(numeric.textBox, Commit);
                    if (numeric.element is FrameworkElement nfe)
                        nfe.ToolTip = $"{name}（{target.Description}のパラメータ）";
                    parent.Children.Add(
                        canReset ? WithResetButton(numeric.element, name, label) : numeric.element);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 任意の [min..max] レンジのスライダー + 数値ボックス行を生成する（Phase W8.2）。
    ///
    /// <see cref="BuildMaterialSliderRow"/> は 0..1 固定なので、アセットが宣言した
    /// 任意の範囲を扱えるようにした版である（レイアウトの流儀は同一）。
    /// </summary>
    /// <param name="labelWidth">
    /// 行ラベルの幅（px）。既定は <see cref="InspectorRowLabelWidth"/>。
    /// ラベル幅が標準と異なるセクションでは、同じセクション内の他の行と
    /// ラベル位置がそろうように呼び出し側がそのセクションの幅を渡す。
    /// </param>
    private UIElement BuildRangeSliderRow(string label, float value, float min, float max, Action<float> onChange,
                                          double labelWidth = InspectorRowLabelWidth)
    {
        // 反転・退化した範囲が来ても UI が壊れないよう最低限の保護を入れる
        //（ランタイム側でも弾いているので通常は起きない）。
        if (max <= min) max = min + ShaderParamMinRangeWidth;

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

        var lbl = new TextBlock
        {
            Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
        };
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0),
        };
        var box = new TextBox
        {
            Text = value.ToString(ShaderParamNumberFormat, CultureInfo.InvariantCulture),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
        };

        // スライダー ⇔ テキストの相互更新で無限ループにならないようにする番人。
        bool updating = false;
        slider.ValueChanged += (_, e) =>
        {
            if (updating) return;
            updating = true;
            box.Text = ((float)e.NewValue).ToString(ShaderParamNumberFormat, CultureInfo.InvariantCulture);
            onChange((float)e.NewValue);
            updating = false;
        };
        void CommitBox()
        {
            if (updating) return;
            if (!float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
            v = Math.Clamp(v, min, max);
            updating = true;
            box.Text = v.ToString(ShaderParamNumberFormat, CultureInfo.InvariantCulture);
            slider.Value = v;
            updating = false;
            onChange(v);
        }
        box.LostFocus += (_, _) => CommitBox();
        box.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitBox(); e.Handled = true; } };

        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(box, 2);
        grid.Children.Add(lbl);
        grid.Children.Add(slider);
        grid.Children.Add(box);
        return grid;
    }

    private UIElement BuildWaterVolumeSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // フィールド変更をランタイムへ送信するローカル関数。
        // key はフィールド表の SET_WATER_FIELD キー名（受信 JSON のキーとは別体系）。
        void SendField(string key, string value)
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_WATER_FIELD:{_currentActorId},{info.SlotIdx},{key},{value}");
        }

        // ── 共通ヘルパ ─────────────────────────────────────────

        // 数値入力行を親セクションへ追加する。行の作り（値域が確定していればスライダー）と
        // 「⟲ 既定値に戻す」は共通入口 BuildResettableFloatRow に一任する。
        // WaterVolume は SET_WATER_FIELD の key と Rust の serde 名が全て一致しているため、
        // リセット用のフィールドパスは key をそのまま使える。
        // resettable=false は「値の調整ではなく器の定義（水面高さ・領域サイズ）なので
        // 既定値へ戻す操作を出さない」行のためのスイッチ。
        // 表示切替に使えるよう生成した行要素を返す。
        UIElement AddFloatRow(StackPanel parent, string label, float value, string key, string format,
                              bool resettable = true)
        {
            var row = BuildResettableFloatRow(
                info.SlotIdx, WaterVolumeComponentType, label, value,
                resettable ? key : null, format,
                v => SendField(key, v.ToString(CultureInfo.InvariantCulture)));
            parent.Children.Add(row);
            return row;
        }

        // 色（リニア RGB）行を親セクションへ追加する。水の色はアルファを持たないため
        // カラーピッカーへは a=1 固定で渡し、送信は "r,g,b" のカンマ区切り 3 値とする。
        void AddColorRow(StackPanel parent, string label, float r, float g, float b, string key)
        {
            float curR = r, curG = g, curB = b;
            var (swatch, setColor) = BuildColorSwatch(curR, curG, curB, WaterColorAlpha);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(swatch);
            swatch.MouseLeftButtonDown += (_, _) =>
            {
                var result = ColorPickerWindow.ShowDialog(Window.GetWindow(this), curR, curG, curB, WaterColorAlpha);
                if (result is null) return;
                (curR, curG, curB, _) = result.Value;
                setColor(curR, curG, curB, WaterColorAlpha);
                SendField(key, FormattableString.Invariant($"{curR},{curG},{curB}"));
            };
            // 色は値域表の対象外（HDR 許容で上限が無い）。⟲ だけ添える。
            parent.Children.Add(WithFieldReset(row, info.SlotIdx, WaterVolumeComponentType, key, label));
        }

        // 補足説明を薄い色で表示する行を生成して返す（表示切替に使うため要素を返す）。
        TextBlock MakeHint(string text) => new()
        {
            Text         = text,
            Foreground   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontSize     = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 2, 0, 2),
        };

        // ── 領域セクション ─────────────────────────────────────
        var regionSection = BuildSection("領域");
        var regionSp      = (StackPanel)regionSection.Child;

        // 種別ドロップダウン（Ocean / Region / Spline）。値は Rust 側の種別文字列と一致させる。
        var kinds = new[]
        {
            ("Ocean",  "海 (Ocean)"),
            ("Region", "領域 (Region)"),
            ("Spline", "スプライン (Spline)"),
        };
        var kindRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        kindRow.Children.Add(new TextBlock
        {
            Text = "種類", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        var kindCombo = new ComboBox { Width = 170, FontSize = 11, Margin = new Thickness(4, 0, 0, 0) };
        foreach (var (val, label) in kinds)
            kindCombo.Items.Add(new ComboBoxItem { Content = label, Tag = val });
        var curKindIdx = Array.FindIndex(kinds, t => t.Item1 == info.WaterKind);
        // 未知の種別が来た場合は既定種別（Region）の位置へフォールバックする。
        kindCombo.SelectedIndex = curKindIdx >= 0
            ? curKindIdx
            : Array.FindIndex(kinds, t => t.Item1 == WaterKindDefault);
        kindRow.Children.Add(kindCombo);
        regionSp.Children.Add(kindRow);

        // Spline（川）選択時、詳細設定は下部の「川（スプライン）」セクションにまとめて表示するため、
        // ここでは領域セクション内の他フィールド（半径系）が無関係であることを示す注意書きのみ出す。
        var splineHint = MakeHint("スプライン（川）は下部の「川（スプライン）」セクションで制御点を編集します。");
        regionSp.Children.Add(splineHint);

        // 水面高さ（Ocean=ワールド Y / Region=アクタ相対 Y）。全種別で共通。
        // 「器の定義」なので ⟲（既定値に戻す）は付けない
        //（配置した水面を既定の 0 へ戻す操作は事故にしかならない）。
        AddFloatRow(regionSp, "水面高さ", info.WaterSurfaceHeight, "surface_height", "F2", resettable: false);

        // 領域半径 XYZ（Region 専用）。3 つのテキストボックスいずれの確定でも 3 値まとめて送信する。
        var extentsRow = BuildXYZRowSimple("領域半径 XYZ", info.WaterRegionHX, info.WaterRegionHY, info.WaterRegionHZ);
        regionSp.Children.Add(extentsRow.element);
        void CommitExtents()
        {
            if (!float.TryParse(extentsRow.tx.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hx)) return;
            if (!float.TryParse(extentsRow.ty.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hy)) return;
            if (!float.TryParse(extentsRow.tz.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz)) return;
            SendField("region_half_extents", FormattableString.Invariant($"{hx},{hy},{hz}"));
        }
        foreach (var tb in new[] { extentsRow.tx, extentsRow.ty, extentsRow.tz })
        {
            tb.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitExtents(); e.Handled = true; } };
            tb.LostFocus += (_, _) => CommitExtents();
            NumericDragBehavior.SetOnDrag(tb, CommitExtents);
        }

        // 海の描画半径（Ocean 専用）。領域サイズ＝器の定義なので ⟲ は付けない。
        var oceanExtentRow = AddFloatRow(regionSp, "海の描画半径(m)", info.WaterOceanExtent, "ocean_extent", "F1",
            resettable: false);

        // 水位シミュレーション（水位グラフ、Phase W2.5）。Region 種別の水域のみが水位グラフの
        // ノードになれる（川・海は底面積が定義できないため対象外）ので Region のときだけ表示する。
        var simulateLevelRow = BuildCheckRow("水位シミュレーション（水位グラフ）", info.WaterSimulateLevel,
            v => SendField("simulate_level", v ? "true" : "false"));
        regionSp.Children.Add(simulateLevelRow);

        sp.Children.Add(regionSection);

        // ── 色と透明度セクション ───────────────────────────────
        var colorSection = BuildSection("色と透明度");
        var colorSp      = (StackPanel)colorSection.Child;

        // 水面シェーディングアセット参照（Phase W8。.wgsl。カメラの shading_asset 行と同じ
        // FileRefBuilder + D&D の流儀を流用する）。種別（Ocean/Region/Spline）に関わらず共通。
        // 未設定（空文字列）のときはエンジン標準の水面シェーディングを使う。
        // 空文字列 virtualPath を送るとランタイム側で標準へ戻る。
        var waterShadingRow = FileRefBuilder.Build(
            "水面シェーダ", info.WaterSurfaceShader,
            [".wgsl"],
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "水面シェーディングアセット（WGSL）を選択",
                    Filter = "WGSL シェーダ|*.wgsl|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path =>
            {
                if (_currentActorId < 0) return;
                var virtualPath = VirtualPath.ToVirtual(path, _assetsPath);
                SendField("surface_shader", virtualPath);
            },
            // 行末の「×」ボタンで指定を解除する（空パス送信でエンジン標準へ戻る）
            () => SendField("surface_shader", ""));
        if (waterShadingRow is FrameworkElement waterShadingFe)
        {
            waterShadingFe.ToolTip = "空欄＝エンジン標準の水。指定した .wgsl が水面の色だけを決めます（形状・波・反射は不変）。\n" +
                                      "「×」ボタンまたは右クリックで指定を解除";
            waterShadingFe.MouseRightButtonUp += (_, e) =>
            {
                SendField("surface_shader", "");
                e.Handled = true;
            };
        }
        colorSp.Children.Add(waterShadingRow);

        // ── 水面シェーダのパラメータ（Phase W8.2）─────────────────
        // アセットが `override` で宣言したパラメータを、参照行の直下へ動的に並べる。
        // 構文解析はランタイムが済ませてあるので、ここは種別に応じて行の形
        //（カラーピッカー／スライダー／数値）を選ぶだけである。
        AddShaderParamRows(colorSp, new ShaderParamTarget(
            info.WaterShaderParamsJson,
            () => $"SET_WATER_SHADER_PARAM:{_currentActorId},{info.SlotIdx},",
            () => $"RESET_WATER_SHADER_PARAM:{_currentActorId},{info.SlotIdx},",
            () => $"SET_WATER_SHADER_BINDING:{_currentActorId},{info.SlotIdx},",
            "水面シェーダ",
            RequiresActorSelection: true));

        AddColorRow(colorSp, "浅場の色", info.WaterShallowR, info.WaterShallowG, info.WaterShallowB, "shallow_color");
        AddColorRow(colorSp, "深場の色", info.WaterDeepR,    info.WaterDeepG,    info.WaterDeepB,    "deep_color");
        AddFloatRow(colorSp, "吸収距離(m)",   info.WaterAbsorptionDistance, "absorption_distance", "F2");
        AddFloatRow(colorSp, "最大不透明度", info.WaterSurfaceOpacity,     "surface_opacity",     "F2");
        sp.Children.Add(colorSection);

        // ── 岸のフォームセクション ─────────────────────────────
        var foamSection = BuildSection("岸のフォーム");
        var foamSp      = (StackPanel)foamSection.Child;
        AddColorRow(foamSp, "フォーム色", info.WaterFoamR, info.WaterFoamG, info.WaterFoamB, "foam_color");
        AddFloatRow(foamSp, "フォーム幅(m)", info.WaterFoamWidth,     "foam_width",     "F2");
        AddFloatRow(foamSp, "フォーム強度",  info.WaterFoamIntensity, "foam_intensity", "F2");
        sp.Children.Add(foamSection);

        // ── 波セクション ───────────────────────────────────────
        var waveSection = BuildSection("波");
        var waveSp      = (StackPanel)waveSection.Child;
        AddFloatRow(waveSp, "波の強さ",     info.WaterWaveAmplitude, "wave_amplitude", "F3");
        AddFloatRow(waveSp, "波のスケール", info.WaterWaveScale,     "wave_scale",     "F3");
        AddFloatRow(waveSp, "波の速度",     info.WaterWaveSpeed,     "wave_speed",     "F3");
        AddFloatRow(waveSp, "波の方向(度)", info.WaterWaveDirectionDeg, "wave_direction_deg", "F1");
        waveSp.Children.Add(MakeHint("波の方向は方位角（度）です。0 = +Z 方向へ進み、正の角度で +X 側へ回ります（上から見て時計回り）。"));
        sp.Children.Add(waveSection);

        // ── 波紋・航跡セクション（Phase I2）─────────────────────
        // InteractionSource を持つアクタが水面付近を動くと立つ波紋の見え方を調整する。
        // 波の伝播速度・減衰そのものは下の「物性」セクション（Phase I2.1）で調整する。
        var rippleSection = BuildSection("波紋・航跡");
        var rippleSp      = (StackPanel)rippleSection.Child;
        AddFloatRow(rippleSp, "波紋の強さ",     info.WaterRippleStrength,     "ripple_strength",       "F2");
        AddFloatRow(rippleSp, "泡のしきい値",   info.WaterRippleFoamThreshold, "ripple_foam_threshold", "F3");
        rippleSp.Children.Add(MakeHint("動くアクタに InteractionSource を付けると、水面付近で波紋と航跡の泡が出ます。"));
        sp.Children.Add(rippleSection);

        // ── 物性セクション（Phase I2.1）─────────────────────────
        // 「水／泥／マグマ」を 1 つの WaterVolume で作り分けるためのノブ。
        // 見た目ではなく水の“中身”なので、波紋・航跡とは別セクションに分ける。
        var physicsSection = BuildSection("物性");
        var physicsSp      = (StackPanel)physicsSection.Child;
        AddFloatRow(physicsSp, "粘度(0..1)",       info.WaterViscosity,    "viscosity",      "F2");
        AddFloatRow(physicsSp, "波紋の減衰率(1/s)", info.WaterRippleDamping, "ripple_damping", "F3");
        physicsSp.Children.Add(MakeHint("粘度を上げると波紋が立ちにくく・伝播が遅くなり、水面模様のスクロールもなまります（0 = ふつうの水／1 = マグマ相当）。減衰率は大きいほど波紋が速く消えます。"));
        sp.Children.Add(physicsSection);

        // ── 岸波セクション（Phase W1.5）─────────────────────────
        // 岸に向かって寄せるうねり（強さ・波長・周期）と、そのうねりが生む泡量を調整する。
        var shoreSection = BuildSection("岸波");
        var shoreSp       = (StackPanel)shoreSection.Child;
        AddFloatRow(shoreSp, "岸波の強さ",     info.WaterShoreStrength, "shore_wave_strength", "F2");
        AddFloatRow(shoreSp, "うねりの波長(m)", info.WaterShoreLength,   "shore_wave_length",   "F2");
        AddFloatRow(shoreSp, "うねりの周期(s)", info.WaterShorePeriod,   "shore_wave_period",   "F2");
        AddFloatRow(shoreSp, "岸波の泡量",     info.WaterShoreFoam,     "shore_wave_foam",     "F2");
        sp.Children.Add(shoreSection);

        // ── 反射・屈折セクション ───────────────────────────────
        var reflectSection = BuildSection("反射・屈折");
        var reflectSp      = (StackPanel)reflectSection.Child;
        AddFloatRow(reflectSp, "フレネル指数", info.WaterFresnelPower,    "fresnel_power",    "F2");
        AddFloatRow(reflectSp, "フレネル強度", info.WaterFresnelStrength, "fresnel_strength", "F2");
        // 反射（Phase W5.2）: シーン内のオブジェクト・空が実際に映り込む。
        //   強度 0 でこの水域の反射を完全に切れる（描画コストも消える）。
        //   粗さは「波による反射のぼけ」で、0 なら厳密な鏡面。
        AddFloatRow(reflectSp, "反射の強さ", info.WaterReflectionIntensity, "reflection_intensity", "F2");
        AddFloatRow(reflectSp, "反射のぼけ", info.WaterReflectionRoughness, "reflection_roughness", "F2");
        AddFloatRow(reflectSp, "屈折の歪み", info.WaterRefractionDistortion, "refraction_distortion", "F3");
        sp.Children.Add(reflectSection);

        // ── 川（スプライン）セクション（W4 / 参照方式は W4.1）───────
        // kind == "Spline" のときのみ表示する。川幅・流速・分割長といった実効パラメータに加え、
        // 「制御点をどこから取るか」を決める**制御点参照**（control_point_ref）を提供する。
        //
        // 制御点の出どころは 2 通りで、**参照が設定されているかどうかだけ**で決まる:
        //   ・参照あり → 参照先アクタの ControlPointComponent（D&D 配置対応・推奨）
        //   ・参照なし → このセクションの spline_points（旧・リスト直接編集）
        // W4.1 以前は「同一アクタに ControlPointComponent があれば自動優先」だったが、
        // どちらが効いているか UI から判別できず混乱を生んだため廃止した。
        // 参照が設定されている間は spline_points 編集 UI を丸ごと隠し、案内だけを出す
        //（両方の「＋ 制御点を追加」が並んで見える取り違えを防ぐ）。
        var riverSection = BuildSection("川（スプライン）");
        var riverSp      = (StackPanel)riverSection.Child;

        // 制御点リスト本体（"x,y,z;x,y,z;..." を都度パースして保持し、編集のたびにリスト全体を送信する）。
        // 参照設定時は UI に出さないが、移行ボタンの活性判定・スナップショットに
        // 必要なため分岐に関わらずここでパースしておく。
        var controlPoints = ParseSplinePoints(info.WaterSplinePoints);

        // ── 制御点参照（W4.1）─────────────────────────────────
        // 現在の参照値。D&D／クリアで書き換わるのでローカルに保持する
        //（SerializeField の参照 UI と同じ流儀。再構築を待たずに表示が追従する）。
        var cpRef = info.WaterControlPointRef ?? "";

        // 参照が設定されているときにだけ出す案内（spline_points UI の代わり）。
        var refActiveHint = MakeHint(
            "制御点は参照先アクタの ControlPointComponent で編集します（spline_points は使用されません）。");

        // 参照設定時に隠す旧 spline_points 編集 UI 一式の入れ物。
        // 個々の要素ではなくこのパネル 1 つの可視性だけを切り替える。
        var legacySplinePanel = new StackPanel();

        // 参照の有無で「案内」「旧 UI」の可視性をまとめて更新する
        //（ラベル表示・警告表示は参照ピッカーが担当する）。
        void UpdateControlPointRefUi()
        {
            bool hasRef = !string.IsNullOrEmpty(cpRef);
            refActiveHint.Visibility     = hasRef ? Visibility.Visible : Visibility.Collapsed;
            legacySplinePanel.Visibility  = hasRef ? Visibility.Collapsed : Visibility.Visible;
        }

        // 制御点参照は共通の参照ピッカーで組み立てる。
        // 保存値はアクタ名のみ（ランタイムは参照先アクタの先頭 ControlPointComponent を使う）。
        var cpRefPicker = ReferencePicker.Create(
            new ReferenceFieldSpec
            {
                Kind         = ReferenceKindCatalog.ControlPointKind,
                WantSlotName = false,     // control_point_ref はアクタ名のみを保持する
                Caption      = "制御点の参照",
                UnsetText    = WaterControlPointRefUnsetText,
                ClearTooltip = "参照を解除する（spline_points に戻る）",
                ExtraTooltip = "川のスプラインの制御点にするアクタ\n"
                             + "未設定のあいだは spline_points が使われます。",
            },
            cpRef, null,
            (actorName, _) =>
            {
                cpRef = actorName ?? "";
                SendField("control_point_ref", cpRef);
                UpdateControlPointRefUi();
            },
            this);

        cpRefPicker.Element.Margin = new Thickness(0, 4, 0, 2);
        riverSp.Children.Add(cpRefPicker.Element);
        riverSp.Children.Add(refActiveHint);

        AddFloatRow(riverSp, "川幅(m)",     info.WaterRiverWidth, "river_width", "F2");
        AddFloatRow(riverSp, "流速(m/s)",   info.WaterFlowSpeed,  "flow_speed",  "F2");
        AddFloatRow(riverSp, "川の深さ(m)", info.WaterRiverDepth, "river_depth", "F2");
        // 分割長（W4.1）: 小さいほど滑らかになるが、総分割数の上限があるので長い川では効かなくなる。
        AddFloatRow(riverSp, "分割長(m)",   info.WaterRiverSegmentLength, "river_segment_length", "F2");
        riverSp.Children.Add(MakeHint(
            $"分割長は曲線長 何 m ごとに 1 分割するか（下限 {WaterRiverSegmentLengthMin:0.##}m）。"
            + $"総分割数の上限は {WaterRiverMaxSegments} のため、長い川では設定より粗くなります。"));

        riverSp.Children.Add(legacySplinePanel);

        {
            // ── このアクタに制御点を作って参照に設定するボタン（W4.1）──────
            // ① ADD_COMPONENT で ControlPoint を足し、② その結果として返ってくる ACTOR_COMPONENTS で
            // 現れた新スロットへ現在の spline_points を変換して SET_CONTROL_POINTS で流し込み、
            // ③ 続けて control_point_ref に**このアクタ自身の名前**を書いて結線する。
            // ② ③ の待ち合わせは _pendingDuplicateRename と同じ「保留 + 次回 BuildActorComponentList で処理」方式。
            // spline_points 自体は削除しない（参照を外したときのフォールバックとして残置する）。
            // 移行を促したいので、ユーザーがまず目にする位置に置く。
            var migrateBtn = new Button
            {
                Content = "このアクタに制御点を作って参照に設定",
                FontSize = 11, Height = 22, Padding = new Thickness(6, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2),
                ToolTip = "ControlPointComponent を追加し、現在の spline_points を移して、"
                        + "この水ボリュームの制御点参照に設定します。",
            };
            migrateBtn.Click += (_, _) =>
            {
                if (_currentActorId < 0 || controlPoints.Count == 0) return;
                // 押下時点のスナップショットを保留に置き、コンポーネント追加を要求する。
                _pendingSplineMigrationPoints       = new List<(float x, float y, float z)>(controlPoints);
                _pendingSplineMigrationActorId      = _currentActorId;
                _pendingSplineMigrationWaterSlotIdx = info.SlotIdx;
                _runtime?.SendToRuntime($"ADD_COMPONENT:{_currentActorId},ControlPointComponent,ControlPoint,");
            };
            // 制御点が 0 個なら移すものが無いので押せない。
            migrateBtn.IsEnabled = controlPoints.Count > 0;
            legacySplinePanel.Children.Add(migrateBtn);
            legacySplinePanel.Children.Add(MakeHint(
                "移行後も spline_points は残ります（参照を外したときのフォールバック）。"));
        }

        {
            var pointsPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            var pointsHint  = MakeHint($"制御点が{WaterSplineMinPointsForRender}点以上必要です（川は描画されません）");

            // 制御点リスト全体を spline_points として送信するローカル関数。
            void SendSplinePoints() => SendField("spline_points", SerializeSplinePoints(controlPoints));

            // 制御点が既定点数を下回る間だけ注意書きを表示する。
            void UpdatePointsHint() =>
                pointsHint.Visibility = controlPoints.Count < WaterSplineMinPointsForRender
                    ? Visibility.Visible : Visibility.Collapsed;

            // 制御点リスト UI を作り直すローカル関数（追加/削除のたびに全再構築する）。
            void RebuildPoints()
            {
                pointsPanel.Children.Clear();
                for (int i = 0; i < controlPoints.Count; i++)
                {
                    int idx = i;
                    var rowGrid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var (px, py, pz) = controlPoints[idx];
                    var xyzRow = BuildXYZRowSimple($"#{idx}", px, py, pz);
                    Grid.SetColumn(xyzRow.element, 0);
                    rowGrid.Children.Add(xyzRow.element);

                    // この制御点の X/Y/Z いずれかが確定したらリスト全体を送信する。
                    void CommitPoint()
                    {
                        if (!float.TryParse(xyzRow.tx.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vx)) return;
                        if (!float.TryParse(xyzRow.ty.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vy)) return;
                        if (!float.TryParse(xyzRow.tz.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vz)) return;
                        controlPoints[idx] = (vx, vy, vz);
                        SendSplinePoints();
                    }
                    foreach (var tb in new[] { xyzRow.tx, xyzRow.ty, xyzRow.tz })
                    {
                        tb.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitPoint(); e.Handled = true; } };
                        tb.LostFocus += (_, _) => CommitPoint();
                        NumericDragBehavior.SetOnDrag(tb, CommitPoint);
                    }

                    var delPointBtn = new Button
                    {
                        Content = "削除", FontSize = 10, Width = 40, Height = 20,
                        Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    };
                    delPointBtn.Click += (_, _) =>
                    {
                        controlPoints.RemoveAt(idx);
                        SendSplinePoints();
                        RebuildPoints();
                        UpdatePointsHint();
                    };
                    Grid.SetColumn(delPointBtn, 1);
                    rowGrid.Children.Add(delPointBtn);

                    pointsPanel.Children.Add(rowGrid);
                }
            }
            RebuildPoints();
            legacySplinePanel.Children.Add(pointsPanel);

            // 「制御点を追加（リストに）」ボタン。末尾に新しい制御点を挿入する初期位置は次の規則で決める:
            // ・2点以上: 最後の点 + (最後の点 − 直前の点) の外挿位置（線の延長上）
            // ・1点   : 最後の点から WaterSplineNewPointOffsetDefault だけ Z 方向へずらした位置
            // ・0点   : 原点
            (float x, float y, float z) ComputeNextControlPoint()
            {
                if (controlPoints.Count >= 2)
                {
                    var last = controlPoints[^1];
                    var prev = controlPoints[^2];
                    return (last.x + (last.x - prev.x), last.y + (last.y - prev.y), last.z + (last.z - prev.z));
                }
                if (controlPoints.Count == 1)
                {
                    var last = controlPoints[0];
                    return (last.x, last.y, last.z + WaterSplineNewPointOffsetDefault);
                }
                return (0f, 0f, 0f);
            }
            var addPointBtn = new Button
            {
                // ラベルと Tooltip で「これはリスト直接編集用（クリック専用）」であり、
                // D&D 配置は ControlPointComponent（推奨）側の機能であることを明示する。
                Content = "＋ 制御点を追加（リストに）", FontSize = 11, Width = 170, Height = 22,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2),
                ToolTip = "ビューポートへの D&D 配置はコントロールポイントコンポーネント（推奨）で利用できます。",
            };
            addPointBtn.Click += (_, _) =>
            {
                controlPoints.Add(ComputeNextControlPoint());
                SendSplinePoints();
                RebuildPoints();
                UpdatePointsHint();
            };
            legacySplinePanel.Children.Add(addPointBtn);
            legacySplinePanel.Children.Add(pointsHint);
            UpdatePointsHint();

            // 「制御点を地形へスナップ」ボタン。オフセット Y はボタンの隣に置き、押下時にのみ値を読む
            // （spline_snap_terrain はランタイム側が処理してコンポーネントを送り返すため、C# 側で結果を反映する必要はない）。
            var snapRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 2) };
            var snapBtn = new Button
            {
                Content = "制御点を地形へスナップ", FontSize = 11, Height = 22,
                Padding = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            snapRow.Children.Add(snapBtn);
            snapRow.Children.Add(new TextBlock
            {
                Text = "オフセットY", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 11, Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            var snapOffsetBox = new TextBox
            {
                Text              = WaterSplineSnapOffsetYDefault.ToString("F2", CultureInfo.InvariantCulture),
                Background        = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                BorderBrush       = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness   = new Thickness(1),
                FontSize          = 11,
                Padding           = new Thickness(4, 1, 4, 1),
                Width             = 60,
                VerticalAlignment = VerticalAlignment.Center,
            };
            NumericDragBehavior.SetEnabled(snapOffsetBox, true);
            snapRow.Children.Add(snapOffsetBox);
            snapBtn.Click += (_, _) =>
            {
                var offsetY = float.TryParse(snapOffsetBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                    ? v : WaterSplineSnapOffsetYDefault;
                SendField("spline_snap_terrain", offsetY.ToString(CultureInfo.InvariantCulture));
            };
            legacySplinePanel.Children.Add(snapRow);
        }

        // 参照の有無に応じた初期表示（旧 spline_points UI の可視性もここで決まる）。
        UpdateControlPointRefUi();

        sp.Children.Add(riverSection);

        // ── 種別に応じた行・セクションの表示切替 ─────────────────
        // Ocean: 海の描画半径のみ / Region: 領域半径のみ / Spline: 川セクションのみ。
        void UpdateKindVisibility(string kind)
        {
            bool isOcean  = kind == "Ocean";
            bool isRegion = kind == "Region";
            bool isSpline = kind == "Spline";
            extentsRow.element.Visibility  = isRegion ? Visibility.Visible : Visibility.Collapsed;
            oceanExtentRow.Visibility      = isOcean  ? Visibility.Visible : Visibility.Collapsed;
            simulateLevelRow.Visibility    = isRegion ? Visibility.Visible : Visibility.Collapsed;
            splineHint.Visibility          = isSpline ? Visibility.Visible : Visibility.Collapsed;
            riverSection.Visibility        = isSpline ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateKindVisibility(info.WaterKind);
        kindCombo.SelectionChanged += (_, _) =>
        {
            if (kindCombo.SelectedItem is ComboBoxItem item && item.Tag is string kind)
            {
                SendField("kind", kind);
                UpdateKindVisibility(kind);
            }
        };

        return sp;
    }

    /// <summary>
    /// WaterLinkComponent（水位グラフの開口。Phase W2.5）のインスペクター UI を構築して返す。
    /// 「接続先」（volume_a / volume_b のアクタ名参照）「開口」（下端 Y・高さ・幅）
    /// 「制御」（開閉率・流量係数）の 3 セクションで構成し、変更時は
    /// SET_WATER_LINK_FIELD:{actor},{slot},{key},{value} を送信する。
    /// アクタ名参照ボックスは WaterVolumeComponent の川セクションにある
    /// control_point_ref 参照ボックス（D&D＋ダブルクリックジャンプ＋✕クリア）と同じ流儀を踏襲する。
    /// </summary>
    private UIElement BuildWaterLinkSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // フィールド変更をランタイムへ送信するローカル関数。
        // key は SET_WATER_LINK_FIELD のキー名（volume_a / volume_b / opening_bottom / opening_height /
        // opening_width / openness / flow_coefficient）。
        void SendField(string key, string value)
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_WATER_LINK_FIELD:{_currentActorId},{info.SlotIdx},{key},{value}");
        }

        // 数値入力行を親セクションへ追加する（BuildWaterVolumeSlotContent の同名ローカル関数と同じ流儀）。
        // 行の作りと「⟲ 既定値に戻す」は共通入口 BuildResettableFloatRow に一任する。
        // SET_WATER_LINK_FIELD の key は Rust の WaterLinkComponentData の serde 名と一致しているため、
        // リセット用のフィールドパスは key をそのまま使える。
        void AddFloatRow(StackPanel parent, string label, float value, string key, string format,
                         string? tooltip = null, bool resettable = true)
        {
            var row = BuildResettableFloatRow(
                info.SlotIdx, WaterLinkComponentType, label, value,
                resettable ? key : null, format,
                v => SendField(key, v.ToString(CultureInfo.InvariantCulture)));
            if (tooltip is not null) ToolTipService.SetToolTip(row, tooltip);
            parent.Children.Add(row);
        }

        // 接続先アクタ参照ボックスを共通の参照ピッカーで 1 本組み立てて親セクションへ追加する。
        // volume_a / volume_b の両方で使い回す。保存値はアクタ名のみ
        // （ランタイムは参照先アクタの先頭 WaterVolumeComponent を水位グラフのノードにする）。
        void AddActorRefRow(StackPanel parent, string caption, string initialActorName, string key, string tooltip)
        {
            var picker = ReferencePicker.Create(
                new ReferenceFieldSpec
                {
                    Kind         = ReferenceKindCatalog.WaterVolumeKind,
                    WantSlotName = false,
                    Caption      = caption,
                    UnsetText    = "（未接続）",
                    ClearTooltip = "接続を解除する",
                    ExtraTooltip = tooltip,
                },
                initialActorName, null,
                (actorName, _) => SendField(key, actorName ?? ""),
                this);
            picker.Element.Margin = new Thickness(0, 4, 0, 2);
            parent.Children.Add(picker.Element);
        }

        // ── 接続先セクション ───────────────────────────────────
        var connSection = BuildSection("接続先");
        var connSp      = (StackPanel)connSection.Child;
        AddActorRefRow(connSp, "接続先 A", info.WaterLinkVolumeA, "volume_a",
            "水位グラフの接続先 A にする水域（Region 種別の WaterVolume）");
        AddActorRefRow(connSp, "接続先 B", info.WaterLinkVolumeB, "volume_b",
            "水位グラフの接続先 B にする水域（Region 種別の WaterVolume）");
        connSp.Children.Add(new TextBlock
        {
            Text         = "川（Spline）・海（Ocean）は水位グラフのノードになれません（底面積が定義できないため）。"
                          + "接続先には領域（Region）種別の Water Volume を指定してください。",
            Foreground   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontSize     = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 2, 0, 2),
        });
        sp.Children.Add(connSection);

        // ── 開口セクション ─────────────────────────────────────
        var openingSection = BuildSection("開口");
        var openingSp      = (StackPanel)openingSection.Child;
        // 開口の 3 行は「開口という構造の定義」（オフセット位置と寸法）なので ⟲ は付けない。
        // 既定値へ戻すと配置した扉・階段穴の形が黙って変わってしまう。
        AddFloatRow(openingSp, "開口下端(m)", info.WaterLinkOpeningBottom, "opening_bottom", "F2",
            "開口下端のアクタ相対 Y（m）。低い位置に置いた開口ほど早く水を通します（例: 階段穴の下端）。",
            resettable: false);
        AddFloatRow(openingSp, "高さ(m)",     info.WaterLinkOpeningHeight, "opening_height", "F2",
            "開口の高さ（m）。この高さぶん水没すると全断面が流れます。",
            resettable: false);
        AddFloatRow(openingSp, "幅(m)",       info.WaterLinkOpeningWidth,  "opening_width",  "F2",
            "開口の幅（m）。幅 × 濡れ高さ × 開閉率が実効断面積になります。",
            resettable: false);
        sp.Children.Add(openingSection);

        // ── 制御セクション ─────────────────────────────────────
        var controlSection = BuildSection("制御");
        var controlSp      = (StackPanel)controlSection.Child;
        AddFloatRow(controlSp, "開閉率(0-1)", info.WaterLinkOpenness, "openness", "F2",
            "開閉率（0..1）。0 でバルブ全閉＝水は 1 滴も通りません。1 で全開。ゲームプレイでバルブ・扉を演出するときはこの値を変えます。");
        AddFloatRow(controlSp, "流量係数(1/s)", info.WaterLinkFlowCoefficient, "flow_coefficient", "F2",
            "流量係数（1/s）。大きいほど水位差が速く釣り合います（発振はしません）。");
        sp.Children.Add(controlSection);

        return sp;
    }

    // ── ControlPointComponent inspector（フェーズB）─────────────

    /// <summary>
    /// 制御点 1 点分の編集用モデル（可変）。
    /// Rust 側 ControlPoint（position / rotation / time / interp）と 1 対 1 で対応する。
    /// 行編集で頻繁に一部フィールドだけ書き換えるため record ではなく可変クラスにしている。
    /// </summary>
    private sealed class ControlPointEntry
    {
        /// <summary>位置（アクタ相対 XYZ）。</summary>
        public float X, Y, Z;
        /// <summary>回転（オイラー角 XYZ・度）。</summary>
        public float RX, RY, RZ;
        /// <summary>
        /// 拡縮（XYZ 倍率）。既定は等倍。
        /// **0 で初期化してはいけない**（Rust 側の既定は 1 で、0 だとビューポートの
        /// キューブが潰れて掴めなくなる）。
        /// </summary>
        public float SX = 1f, SY = 1f, SZ = 1f;
        /// <summary>この点に到達する時刻（秒相当。カメラパス等で使用）。</summary>
        public float Time;
        /// <summary>次の点までの補間方式（"Linear" / "CatmullRom" / "Step"）。</summary>
        public string Interp = ControlPointInterpDefault;
    }

    /// <summary>
    /// JSON 配列（[x,y,z]）を 3 成分へ読み出す。要素不足・数値以外は 0 として扱う
    /// （旧データや壊れた点が混ざっていてもリスト全体を捨てないため）。
    /// </summary>
    private static (float x, float y, float z) ReadVec3(JsonElement arr)
    {
        var v = new float[3];
        int n = Math.Min(v.Length, arr.GetArrayLength());
        for (int i = 0; i < n; i++)
            v[i] = arr[i].ValueKind == JsonValueKind.Number ? arr[i].GetSingle() : 0f;
        return (v[0], v[1], v[2]);
    }

    /// <summary>
    /// ControlPointComponent の points 配列（生 JSON）を編集用モデルへパースする。
    /// 壊れた JSON・想定外の形が来てもインスペクタ全体を落とさないため、
    /// 例外は握りつぶして空リスト（または解析できたところまで）を返す。
    /// </summary>
    private static List<ControlPointEntry> ParseControlPoints(string json)
    {
        var list = new List<ControlPointEntry>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var e = new ControlPointEntry();
                // position / rotation は 3 要素配列。欠落・要素不足でも既定 0 のまま進める。
                if (el.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Array)
                {
                    var p = ReadVec3(pos);
                    e.X = p.x; e.Y = p.y; e.Z = p.z;
                }
                if (el.TryGetProperty("rotation", out var rot) && rot.ValueKind == JsonValueKind.Array)
                {
                    var r = ReadVec3(rot);
                    e.RX = r.x; e.RY = r.y; e.RZ = r.z;
                }
                // scale は「キーが在るときだけ」読む。旧データ（scale 無し）で
                // ReadVec3 の 0 埋めを採ると等倍が 0 倍になってしまうため、
                // 既定値（1,1,1）はフィールド初期値のまま残す。
                if (el.TryGetProperty("scale", out var scl) && scl.ValueKind == JsonValueKind.Array)
                {
                    var s = ReadVec3(scl);
                    e.SX = s.x; e.SY = s.y; e.SZ = s.z;
                }
                if (el.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number)
                    e.Time = t.GetSingle();
                if (el.TryGetProperty("interp", out var ip) && ip.ValueKind == JsonValueKind.String)
                    e.Interp = ip.GetString() ?? ControlPointInterpDefault;
                list.Add(e);
            }
        }
        catch (JsonException)
        {
            // 壊れた JSON はここで捨てる（インスペクタの他セクションは表示を続ける）
        }
        return list;
    }

    /// <summary>
    /// 編集用モデルを SET_CONTROL_POINTS で送る JSON 配列文字列へ直列化する。
    /// position / rotation / scale / time / interp を必ず全部書き出す
    /// （欠けると Rust 側 serde の default が効いて意図せず既定値に戻るため）。
    /// 数値は CultureInfo.InvariantCulture 固定（ロケール依存の "1,5" を出さない）。
    /// </summary>
    private static string SerializeControlPoints(List<ControlPointEntry> points)
    {
        var items = points.Select(p => FormattableString.Invariant(
            $"{{\"position\":[{p.X},{p.Y},{p.Z}],\"rotation\":[{p.RX},{p.RY},{p.RZ}],\"scale\":[{p.SX},{p.SY},{p.SZ}],\"time\":{p.Time},\"interp\":\"{p.Interp}\"}}"));
        return "[" + string.Join(",", items) + "]";
    }

    /// <summary>
    /// ControlPointComponent のインスペクター UI を構築して返す（フェーズB）。
    /// 「制御点」セクション 1 つで、点の追加・削除・位置/時刻/補間の編集を提供する。
    /// 点の追加・削除・属性編集はすべて SET_CONTROL_POINTS による「リスト全置換」1 本で行う
    /// （水の spline_points と同じ流儀。差分コマンドを増やさず順序ずれを起こさないため）。
    /// </summary>
    private UIElement BuildControlPointSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // 受信 JSON を編集用モデルへ（パース失敗時は空リスト）。
        var points = ParseControlPoints(info.ControlPointsJson);
        // 切り分け計器: セクションが実際に構築されたか（D&D 不発調査用・軽量なので常設）。
        EditorLog.Write($"[CtrlPoint] セクション構築 slot={info.SlotIdx} points={points.Count}");

        // リスト全体を全置換で送信するローカル関数。
        void SendPoints()
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime(
                $"SET_CONTROL_POINTS:{_currentActorId},{info.SlotIdx},{SerializeControlPoints(points)}");
        }

        // 補足説明を薄い色で表示する行を生成する（水セクションの MakeHint と同じ体裁）。
        TextBlock MakeHint(string text) => new()
        {
            Text         = text,
            Foreground   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontSize     = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 2, 0, 2),
        };

        var section = BuildSection("制御点");
        var body    = (StackPanel)section.Child;

        // 点数表示と上限到達時の注意書き。
        var countText   = MakeHint("");
        var limitHint   = MakeHint($"制御点が上限（{ControlPointMaxCount} 点）に達しています。これ以上追加できません。");
        var pointsPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

        // この UI が管理する行 Border（インデックス順）。選択ハイライトの塗り直しに使う。
        var rowBorders = new List<Border>();
        _controlPointRows[info.SlotIdx] = rowBorders;

        body.Children.Add(countText);
        body.Children.Add(pointsPanel);

        // 追加ボタンは RebuildPoints から有効/無効を切り替えるため先に生成する。
        var addPointBtn = new Button
        {
            Content = "＋ 制御点を追加", FontSize = 11, Width = 150, Height = 22,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2),
            ToolTip = "クリックで末尾に追加。ビューポートへドラッグ＆ドロップすると、落とした先の地形・メッシュ・水面上に追加します。",
        };

        // 点数表示・上限注意書き・追加ボタンの活性を現在の点数に合わせて更新する。
        void UpdateCountUi()
        {
            countText.Text        = $"{points.Count} 点 / 上限 {ControlPointMaxCount} 点";
            bool full             = points.Count >= ControlPointMaxCount;
            limitHint.Visibility  = full ? Visibility.Visible : Visibility.Collapsed;
            addPointBtn.IsEnabled = !full;
        }

        // 制御点リスト UI を全再構築するローカル関数（川セクションと同じ流儀）。
        //
        // 1 点は **2 段組み**で表示する:
        //   上段: #index + 位置 XYZ + time + 補間 + 削除
        //   下段: 回転 XYZ + スケール XYZ
        // 位置・回転・スケール・time・補間を 1 行に詰めると、XYZ の入力欄だけで 9 個並び
        // インスペクタ幅では判読も操作も成立しない。「点の同一性（#index）と最も使う位置」を
        // 上段に置き、姿勢系を下段へ回すことで、上段だけ見れば従来と同じ操作感になる。
        void RebuildPoints()
        {
            pointsPanel.Children.Clear();
            rowBorders.Clear();

            for (int i = 0; i < points.Count; i++)
            {
                int idx   = i;
                var entry = points[idx];

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 位置 XYZ
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // time
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // 補間
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // 削除

                // ── 位置 XYZ（ラベル部が "#index" のインデックス表示を兼ねる）──
                var xyzRow = BuildXYZRowSimple($"#{idx}", entry.X, entry.Y, entry.Z);
                Grid.SetColumn(xyzRow.element, 0);
                rowGrid.Children.Add(xyzRow.element);

                void CommitPos()
                {
                    if (!float.TryParse(xyzRow.tx.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vx)) return;
                    if (!float.TryParse(xyzRow.ty.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vy)) return;
                    if (!float.TryParse(xyzRow.tz.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vz)) return;
                    entry.X = vx; entry.Y = vy; entry.Z = vz;
                    SendPoints();
                }
                foreach (var tb in new[] { xyzRow.tx, xyzRow.ty, xyzRow.tz })
                {
                    tb.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitPos(); e.Handled = true; } };
                    tb.LostFocus += (_, _) => CommitPos();
                    NumericDragBehavior.SetOnDrag(tb, CommitPos);
                }

                // ── time（小さな数値ボックス）──
                var timeBox = new TextBox
                {
                    Text              = entry.Time.ToString("F2", CultureInfo.InvariantCulture),
                    Background        = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                    Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    BorderBrush       = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                    BorderThickness   = new Thickness(1),
                    FontSize          = 11,
                    Padding           = new Thickness(4, 1, 4, 1),
                    Width             = 46,
                    Margin            = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip           = "この点に到達する時刻",
                };
                NumericDragBehavior.SetEnabled(timeBox, true);
                void CommitTime()
                {
                    if (!float.TryParse(timeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vt)) return;
                    entry.Time = vt;
                    SendPoints();
                }
                timeBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitTime(); e.Handled = true; } };
                timeBox.LostFocus += (_, _) => CommitTime();
                NumericDragBehavior.SetOnDrag(timeBox, CommitTime);
                Grid.SetColumn(timeBox, 1);
                rowGrid.Children.Add(timeBox);

                // ── 補間方式コンボ（Tag に Rust 側の文字列をそのまま入れる）──
                var interpCombo = new ComboBox
                {
                    Width = 92, FontSize = 11,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "次の点までの補間方式",
                };
                foreach (var (val, label) in ControlPointInterpChoices)
                    interpCombo.Items.Add(new ComboBoxItem { Content = label, Tag = val });
                var curInterpIdx = Array.FindIndex(ControlPointInterpChoices, t => t.value == entry.Interp);
                // 未知の補間方式が来た場合は既定（CatmullRom）の位置へフォールバックする。
                interpCombo.SelectedIndex = curInterpIdx >= 0
                    ? curInterpIdx
                    : Array.FindIndex(ControlPointInterpChoices, t => t.value == ControlPointInterpDefault);
                interpCombo.SelectionChanged += (_, _) =>
                {
                    if (interpCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string v) return;
                    if (v == entry.Interp) return; // 初期選択・再構築時の空振り送信を避ける
                    entry.Interp = v;
                    SendPoints();
                };
                Grid.SetColumn(interpCombo, 2);
                rowGrid.Children.Add(interpCombo);

                // ── 削除ボタン ──
                var delBtn = new Button
                {
                    Content = "削除", FontSize = 10, Width = 40, Height = 20,
                    Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                delBtn.Click += (_, _) =>
                {
                    points.RemoveAt(idx);
                    SendPoints();
                    RebuildPoints();
                    UpdateCountUi();
                };
                Grid.SetColumn(delBtn, 3);
                rowGrid.Children.Add(delBtn);

                // ── 下段: 回転 XYZ ＋ スケール XYZ ──
                // 2 つの XYZ 行を等幅の 2 カラムに並べる（どちらも 3 個の入力欄を持つため
                // 幅を揃えないと桁が食い違って読みにくい）。
                var attrGrid = new Grid { Margin = new Thickness(0, 1, 0, 0) };
                attrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                attrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var rotRow = BuildXYZRowSimple("回転", entry.RX, entry.RY, entry.RZ);
                Grid.SetColumn(rotRow.element, 0);
                attrGrid.Children.Add(rotRow.element);
                void CommitRot()
                {
                    if (!float.TryParse(rotRow.tx.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vx)) return;
                    if (!float.TryParse(rotRow.ty.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vy)) return;
                    if (!float.TryParse(rotRow.tz.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vz)) return;
                    entry.RX = vx; entry.RY = vy; entry.RZ = vz;
                    SendPoints();
                }
                foreach (var tb in new[] { rotRow.tx, rotRow.ty, rotRow.tz })
                {
                    tb.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitRot(); e.Handled = true; } };
                    tb.LostFocus += (_, _) => CommitRot();
                    NumericDragBehavior.SetOnDrag(tb, CommitRot);
                }

                var sclRow = BuildXYZRowSimple("スケール", entry.SX, entry.SY, entry.SZ);
                Grid.SetColumn(sclRow.element, 1);
                attrGrid.Children.Add(sclRow.element);
                void CommitScale()
                {
                    if (!float.TryParse(sclRow.tx.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vx)) return;
                    if (!float.TryParse(sclRow.ty.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vy)) return;
                    if (!float.TryParse(sclRow.tz.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vz)) return;
                    entry.SX = vx; entry.SY = vy; entry.SZ = vz;
                    SendPoints();
                }
                foreach (var tb in new[] { sclRow.tx, sclRow.ty, sclRow.tz })
                {
                    tb.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitScale(); e.Handled = true; } };
                    tb.LostFocus += (_, _) => CommitScale();
                    NumericDragBehavior.SetOnDrag(tb, CommitScale);
                }

                // 上段・下段をまとめる（Border の Child は 1 つしか持てないため縦積み）。
                var rowStack = new StackPanel();
                rowStack.Children.Add(rowGrid);
                rowStack.Children.Add(attrGrid);

                // 行 Border（選択ハイライトの受け皿を兼ねる）。
                var rowBorder = new Border
                {
                    Background   = Brushes.Transparent,
                    CornerRadius = new CornerRadius(2),
                    Padding      = new Thickness(2, 1, 2, 1),
                    Margin       = new Thickness(0, 1, 0, 1),
                    Child        = rowStack,
                    Cursor       = Cursors.Hand,
                };
                // 行クリック → ビューポート側の点を選択させる（リスト → ビューポートの逆方向同期）。
                // TextBox / Button 上のクリックはそれらがイベントを処理するのでここへは来ない。
                rowBorder.MouseLeftButtonDown += (_, _) =>
                {
                    if (_currentActorId < 0) return;
                    _runtime?.SendToRuntime($"SELECT_CONTROL_POINT:{_currentActorId},{info.SlotIdx},{idx}");
                };

                rowBorders.Add(rowBorder);
                pointsPanel.Children.Add(rowBorder);
            }

            // Rust は編集のたびに ACTOR_COMPONENTS を再送してくるため UI は頻繁に作り直される。
            // 保持しておいた選択状態からハイライトを復元する。
            RefreshControlPointRowHighlight();
        }

        RebuildPoints();
        UpdateCountUi();

        // ── 「＋ 制御点を追加」（クリック = 末尾追加 / ドラッグ = ビューポートへドロップ）──

        // クリック追加時の初期位置。川セクションの ComputeNextControlPoint と同じ規則:
        // ・2点以上: 最後の点 + (最後の点 − 直前の点) の外挿位置（線の延長上）
        // ・1点   : 最後の点から ControlPointNewPointOffsetDefault だけ Z 方向へずらした位置
        // ・0点   : 原点
        (float x, float y, float z) ComputeNextControlPoint()
        {
            if (points.Count >= 2)
            {
                var last = points[^1];
                var prev = points[^2];
                return (last.X + (last.X - prev.X), last.Y + (last.Y - prev.Y), last.Z + (last.Z - prev.Z));
            }
            if (points.Count == 1)
            {
                var last = points[0];
                return (last.X, last.Y, last.Z + ControlPointNewPointOffsetDefault);
            }
            return (0f, 0f, 0f);
        }

        addPointBtn.Click += (_, _) =>
        {
            if (points.Count >= ControlPointMaxCount) return;
            var (nx, ny, nz) = ComputeNextControlPoint();
            points.Add(new ControlPointEntry
            {
                X = nx, Y = ny, Z = nz,
                // time は「最後の点の time + 既定ステップ」。0 点なら 0 から始める。
                Time   = points.Count > 0 ? points[^1].Time + ControlPointTimeStepDefault : 0f,
                Interp = ControlPointInterpDefault,
            });
            SendPoints();
            RebuildPoints();
            UpdateCountUi();
        };

        // ドラッグ開始判定用の押下位置（クリックとドラッグを両立させるため、
        // システムの最小ドラッグ距離を超えて初めてドラッグとみなす）。
        Point? addBtnPressPos = null;
        addPointBtn.PreviewMouseLeftButtonDown += (_, e) =>
        {
            addBtnPressPos = e.GetPosition(addPointBtn);
            // 切り分け計器: 押下が本ハンドラへ届いているか（D&D 不発調査用・常設）。
            EditorLog.Write("[CtrlPoint] 追加ボタン押下（ドラッグ待機）");
        };
        // 【重要】バブリングの MouseMove ではなくトンネリングの PreviewMouseMove を使う。
        // 実機で「押下は届くのに MouseMove が一度も発火しない」現象を確認した
        // （他のドラッグ系ハンドラが移動イベントを Handled にするとバブリング側の
        //   通常ハンドラは抑止されるが、Preview はその影響を受けない）。
        bool addBtnMoveProbeLogged = false;
        addPointBtn.PreviewMouseMove += (_, e) =>
        {
            if (addBtnPressPos is not { } start) return;
            // 切り分け計器: 押下後に移動イベントが届いているか（押下1回につき1行だけ）。
            if (!addBtnMoveProbeLogged)
            {
                addBtnMoveProbeLogged = true;
                EditorLog.Write("[CtrlPoint] 移動イベント受信（ドラッグ判定中）");
            }
            if (e.LeftButton != MouseButtonState.Pressed) { addBtnPressPos = null; return; }
            if (!addPointBtn.IsEnabled) return;
            var cur = e.GetPosition(addPointBtn);
            if (Math.Abs(cur.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
             && Math.Abs(cur.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            addBtnPressPos = null;
            addBtnMoveProbeLogged = false;
            BeginAddPointDrag(addPointBtn, info.SlotIdx);
        };
        addPointBtn.PreviewMouseLeftButtonUp += (_, _) => addBtnPressPos = null;

        body.Children.Add(addPointBtn);
        body.Children.Add(limitHint);
        body.Children.Add(MakeHint(
            "ボタンをビューポートへドラッグ＆ドロップすると、落とした先の地形・メッシュ・水面上に点を追加します（続けてドロップすれば連続で置けます）。"));
        body.Children.Add(MakeHint(
            "回転はこのリストには表示しませんが、保存・送信では保持されます（カメラパス等で使用）。"));

        sp.Children.Add(section);
        return sp;
    }

    /// <summary>制御点の補間方式の選択肢（送信値, 表示ラベル）。値は Rust 側 Interp の文字列と一致させること。</summary>
    private static readonly (string value, string label)[] ControlPointInterpChoices =
    {
        ("Linear",     "直線 (Linear)"),
        ("CatmullRom", "曲線 (CatmullRom)"),
        ("Step",       "階段 (Step)"),
    };

    /// <summary>
    /// 「制御点を追加」ボタンからビューポートへのドラッグ＆ドロップを開始する。
    /// </summary>
    /// <remarks>
    /// ドロップの受け口はビューポート HWND に登録済みの OLE ドロップターゲット
    /// （<c>MainWindow.ViewportOleDropTarget</c>）である。ここでは
    /// <c>BeginControlPointDrag</c> で「どのアクタ・どのスロットへ足すのか」を登録してから
    /// DoDragDrop を回すだけで、実際の座標確定と ADD_CONTROL_POINT_AT_SCREEN 送信は
    /// ドロップターゲット側が行う（ドロップ座標が OLE から確定値として渡るため）。
    ///
    /// 旧実装は「HwndHost 上には OLE ターゲットが無い」という誤った前提で
    /// DoDragDrop 復帰後の GetCursorPos に頼っており、実際にはドロップターゲットが
    /// 制御点ペイロードを DROPEFFECT_NONE で拒否していたため点が追加されなかった。
    /// 復帰後のカーソル位置は「離した位置」である保証も無い。
    /// フォールバック（<c>TryAddControlPointAtCursor</c>）は残してあるが、
    /// OLE 経路でドロップ済みの場合は二重追加にならないよう抑止される。
    /// </remarks>
    private void BeginAddPointDrag(UIElement dragSource, int slotIdx)
    {
        // 対象アクタ／メインウィンドウが不明な状態でドラッグを始めても着地点が無いので何もしない。
        // パターン変数で受けることで以降 mainWindow は非 null として扱える
        //（ラムダに捕捉される変数は null 解析が効かないため、宣言時点で確定させる）。
        if (Application.Current.MainWindow is not MainWindow mainWindow) return;
        if (_currentActorId < 0) return;

        // ドロップターゲットへ「どのアクタ・どのスロットへ足すのか」を渡す。
        mainWindow.BeginControlPointDrag(_currentActorId, slotIdx);

        // GiveFeedback でビューポートのハイライトとカーソルを制御する。
        // ビューポート上では OLE 既定の「no-drop」カーソルを抑制して通常カーソルにする。
        GiveFeedbackEventHandler giveFeedback = (_, args) =>
        {
            bool overVp = mainWindow.IsMouseOverViewportHwnd();
            mainWindow.SetViewportDragHighlight(overVp);
            if (overVp)
            {
                args.UseDefaultCursors = false;
                Mouse.OverrideCursor   = null;
                args.Handled           = true;
            }
            else
            {
                Mouse.OverrideCursor = null;
            }
        };

        // DoDragDrop は UI スレッドをブロックし GiveFeedback がほぼ 1 回しか発火しないため、
        // ハイライト更新はバックグラウンドスレッドから一定間隔でポーリングする。
        // ※ ランタイムへのホバー通知（DRAG_HOVER）はここでは送らない。
        //    あれはアクタのスポーンプレビュー球用で、制御点ドロップでは余計な球が出るだけのため。
        using var cts = new System.Threading.CancellationTokenSource();
        var ct = cts.Token;
        var highlightThread = new System.Threading.Thread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                bool overVp = mainWindow.IsMouseOverViewportHwnd();
                mainWindow.Dispatcher.BeginInvoke(() => mainWindow.SetViewportDragHighlight(overVp));
                System.Threading.Thread.Sleep(ViewportHighlightPollIntervalMs);
            }
        }) { IsBackground = true };
        highlightThread.Start();

        // データ本体は使わない。形式名だけが「これは制御点の追加ドラッグである」ことを
        // ドロップターゲットへ伝える識別子になる（対象アクタ/スロットは MainWindow 側に登録済み）。
        dragSource.GiveFeedback += giveFeedback;
        var data = new DataObject(MainWindow.ControlPointDragFormat, string.Empty);
        DragDrop.DoDragDrop(dragSource, data, DragDropEffects.Copy);
        dragSource.GiveFeedback -= giveFeedback;

        cts.Cancel();
        highlightThread.Join(ViewportHighlightPollIntervalMs * 2);

        // ドラッグ終了後の後始末（ProjectPanel と同じ。OLE ループ後に残るキャプチャも解除する）。
        Mouse.OverrideCursor = null;
        mainWindow.SetViewportDragHighlight(false);
        Mouse.Capture(null);

        // ドラッグ状態を解除し、OLE 経路でドロップ済みかを受け取る。
        // 追加結果は Rust が ACTOR_COMPONENTS を送り返してリストへ反映されるので、
        // ここではダイアログ等を出さず即座に制御を返す（連続ドロップの UX を壊さないため）。
        bool droppedViaOle = mainWindow.EndControlPointDrag();
        if (droppedViaOle) return;

        // OLE のドロップが届かなかった場合のみ、カーソル位置から復元して送る。
        // （ドロップターゲットの登録に失敗している環境などへの保険）
        mainWindow.TryAddControlPointAtCursor(_currentActorId, slotIdx);
    }

    /// <summary>
    /// CoverEmitterComponent のインスペクター UI を構築して返す（Phase I3.1）。
    ///
    /// 構成は 2 セクション:
    ///   ・「カバーエミッタ」… 範囲種別と、種別に応じたパラメータ（無関係な項目は非表示）
    ///   ・「シミュレート」  … Edit で積算を回すボタン群（秒数入力・停止・全消去）
    ///
    /// 変更時は SET_COVER_FIELD:{actor},{slot},{key},{value} を送信する。
    /// 中心位置はアクタの Transform から解決されるため UI には現れない
    /// （＝アクタを動かすだけで降る場所が動く。docs/cover_field.md）。
    /// </summary>
    private UIElement BuildCoverEmitterSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // フィールド変更をランタイムへ送信するローカル関数。
        void SendField(string key, string value)
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_COVER_FIELD:{_currentActorId},{info.SlotIdx},{key},{value}");
        }

        // 数値入力行。行の作り（値域が確定していればスライダー）と「⟲ 既定値に戻す」は
        // 共通入口 BuildResettableFloatRow に一任する。
        //
        // 【ipcKey と resetPath を分ける理由】
        //   SET_COVER_FIELD の key は「1 スカラー = 1 キー」（extents_x など）だが、
        //   ⟲ が送る RESET_COMPONENT_FIELD は **Rust の serde フィールドパス**
        //   （`extents/0` のような配列添字込み）でなければ解決できない。
        //   両者を同じ文字列にすると ⟲ が黙って何もしない行ができる。
        UIElement MakeFloatRow(string label, float value, string ipcKey, string resetPath, string format)
            => BuildResettableFloatRow(
                info.SlotIdx, CoverEmitterComponentType, label, value, resetPath, format,
                v => SendField(ipcKey, v.ToString(CultureInfo.InvariantCulture)));

        // 文字列入力行（素材 ID・マスク画像パス）。フォーカスが外れた時点で送信する
        // （1 文字打つたびに IPC を撃たない）。
        UIElement MakeTextRow(string label, string value, string key, string tooltip)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = label, FontSize = 11, Width = 90,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var box = new TextBox
            {
                Text            = value,
                Background      = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                Foreground      = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                FontSize        = 11,
                Padding         = new Thickness(4, 1, 4, 1),
                Width           = 170,
                ToolTip         = tooltip,
                VerticalAlignment = VerticalAlignment.Center,
            };
            box.LostFocus += (_, _) => SendField(key, box.Text);
            row.Children.Add(box);
            return row;
        }

        // ── ① カバーエミッタ本体 ──────────────────────────────
        var section = BuildSection("カバーエミッタ");
        var body    = (StackPanel)section.Child;

        // 有効フラグ（ゲームロジックから一時的に降雪を止めるためのデータ側スイッチ）。
        body.Children.Add(BuildCheckRow("有効", info.CoverEnabled,
            v => SendField("enabled", v ? "true" : "false")));

        // 範囲種別ドロップダウン。値は Rust 側 CoverEmitterRangeKind::as_str と一致させる。
        var kinds = new[]
        {
            ("Global",      "全域 (Global)"),
            ("Region",      "領域 (Region)"),
            ("TextureMask", "マスク画像 (TextureMask)"),
        };
        var kindRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        kindRow.Children.Add(new TextBlock
        {
            Text = "範囲", FontSize = 11, Width = 90,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var kindCombo = new ComboBox { Width = 170, FontSize = 11 };
        foreach (var (val, label) in kinds)
            kindCombo.Items.Add(new ComboBoxItem { Content = label, Tag = val });
        var curKindIdx = Array.FindIndex(kinds, t => t.Item1 == info.CoverRangeKind);
        kindCombo.SelectedIndex = curKindIdx >= 0
            ? curKindIdx
            : Array.FindIndex(kinds, t => t.Item1 == CoverRangeKindDefault);
        kindRow.Children.Add(kindCombo);
        body.Children.Add(kindRow);

        // 素材 ID と強度は種別によらず常に効く。
        body.Children.Add(MakeTextRow("素材ID", info.CoverMaterialId, "material_id",
            "assets/terrain/cover_materials.json の id（例: snow / leaf_carpet / wet / mud）"));
        body.Children.Add(MakeFloatRow("強度(量/秒)", info.CoverStrength, "strength", "strength", "F3"));

        // Region 専用の行（半径 3 軸＋境界フェード）。
        var extentsRowX = MakeFloatRow("半径X(m)", info.CoverExtentsX, "extents_x", "extents/0", "F2");
        var extentsRowY = MakeFloatRow("半径Y(m)", info.CoverExtentsY, "extents_y", "extents/1", "F2");
        var extentsRowZ = MakeFloatRow("半径Z(m)", info.CoverExtentsZ, "extents_z", "extents/2", "F2");
        var fadeRow     = MakeFloatRow("境界ぼかし(m)", info.CoverFade, "fade", "fade", "F2");
        body.Children.Add(extentsRowX);
        body.Children.Add(extentsRowY);
        body.Children.Add(extentsRowZ);
        body.Children.Add(fadeRow);

        // TextureMask 専用の行（画像パス＋矩形サイズ）。
        var maskPathRow  = MakeTextRow("マスク画像", info.CoverMaskPath, "mask_path",
            "グレースケール画像（白=強度そのまま / 黒=降らない）。空にするとこのエミッタは無効になります。");
        var maskSizeXRow = MakeFloatRow("マスク幅X(m)", info.CoverMaskSizeX, "mask_size_x", "mask_size/0", "F2");
        var maskSizeZRow = MakeFloatRow("マスク幅Z(m)", info.CoverMaskSizeZ, "mask_size_z", "mask_size/1", "F2");
        body.Children.Add(maskPathRow);
        body.Children.Add(maskSizeXRow);
        body.Children.Add(maskSizeZRow);

        var kindHint = new TextBlock
        {
            Foreground   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontSize     = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 2),
        };
        body.Children.Add(kindHint);

        // ── 種別に応じた行の表示切替（無関係なパラメータは非表示にする）──
        void UpdateKindVisibility(string kind)
        {
            bool isRegion = kind == "Region";
            bool isMask   = kind == "TextureMask";
            var regionVis = isRegion ? Visibility.Visible : Visibility.Collapsed;
            var maskVis   = isMask   ? Visibility.Visible : Visibility.Collapsed;
            extentsRowX.Visibility  = regionVis;
            extentsRowY.Visibility  = regionVis;
            extentsRowZ.Visibility  = regionVis;
            fadeRow.Visibility      = regionVis;
            maskPathRow.Visibility  = maskVis;
            maskSizeXRow.Visibility = maskVis;
            maskSizeZRow.Visibility = maskVis;
            kindHint.Text = kind switch
            {
                "Global"      => "ワールド全域に降ります（位置・大きさは無関係）。天候そのものを表す設定です。",
                "TextureMask" => "アクタ位置を中心とした XZ 矩形へ画像を真上から投影します。",
                _             => "アクタ位置を中心とした直方体の内側に降ります。中心はギズモで動かせます。",
            };
        }
        UpdateKindVisibility(info.CoverRangeKind);
        kindCombo.SelectionChanged += (_, _) =>
        {
            if (kindCombo.SelectedItem is ComboBoxItem item && item.Tag is string kind)
            {
                SendField("range_kind", kind);
                UpdateKindVisibility(kind);
            }
        };

        sp.Children.Add(section);

        // ── ② シミュレート（Edit 専用）────────────────────────
        //   Play 中の積算は Stop で捨てられる（揮発）ので、保存したい積もり方は
        //   ここで作る。結果は編集データとしてカバー場へ書かれ、地形保存の対象になる。
        var simSection = BuildSection("シミュレート（編集）");
        var simBody    = (StackPanel)simSection.Child;

        var simRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var simBtn = new Button
        {
            Content = "シミュレート", FontSize = 11, Height = 22,
            Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "秒数を入れるとその秒数ぶんを即座に計算して止まります。空欄なら停止するまで積もり続けます。",
        };
        simRow.Children.Add(simBtn);
        simRow.Children.Add(new TextBlock
        {
            Text = "秒数", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        var secondsBox = new TextBox
        {
            Text            = CoverSimulateSecondsDefault,
            Background      = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground      = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            FontSize        = 11,
            Padding         = new Thickness(4, 1, 4, 1),
            Width           = 56,
            ToolTip         = "空欄 = 停止まで連続シミュレート",
            VerticalAlignment = VerticalAlignment.Center,
        };
        NumericDragBehavior.SetEnabled(secondsBox, true);
        simRow.Children.Add(secondsBox);
        simBtn.Click += (_, _) =>
        {
            // 秒数が読めなければ 0 を送る＝ランタイム側は「連続シミュレート」と解釈する。
            var seconds = float.TryParse(secondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v : 0f;
            _runtime?.SendToRuntime($"TERRAIN_COVER_SIMULATE:{seconds.ToString(CultureInfo.InvariantCulture)}");
        };
        simBody.Children.Add(simRow);

        var ctrlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        var stopBtn = new Button
        {
            Content = "停止", FontSize = 11, Height = 22,
            Padding = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "連続シミュレートを止めます。",
        };
        stopBtn.Click += (_, _) => _runtime?.SendToRuntime("TERRAIN_COVER_SIMULATE_STOP");
        ctrlRow.Children.Add(stopBtn);

        var clearBtn = new Button
        {
            Content = "全消去", FontSize = 11, Height = 22,
            Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "全チャンクのカバー場を消します（取り消せません）。",
        };
        clearBtn.Click += (_, _) => _runtime?.SendToRuntime("TERRAIN_COVER_CLEAR");
        ctrlRow.Children.Add(clearBtn);
        simBody.Children.Add(ctrlRow);

        simBody.Children.Add(new TextBlock
        {
            Text         = "シミュレート結果は地形の保存（地形メニュー）で .tcover として保存されます。"
                         + "Play 中の積もりは Stop で捨てられ、保存した状態へ戻ります。",
            Foreground   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontSize     = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 2),
        });

        sp.Children.Add(simSection);
        return sp;
    }

    /// <summary>
    /// InteractionSourceComponent のインスペクター UI を構築して返す（Phase I1）。
    /// 「インタラクション」セクション 1 つだけの最小構成で、変更時は
    /// SET_INTERACTION_FIELD:{actor},{slot},{key},{value} を送信する。
    /// 位置と速度はアクタの Transform から自動で解決されるため、UI には現れない
    /// （＝アクタを動かすだけで草が反応する。docs/water_interaction_roadmap.md §1.3）。
    /// </summary>
    private UIElement BuildInteractionSourceSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // フィールド変更をランタイムへ送信するローカル関数。
        void SendField(string key, string value)
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_INTERACTION_FIELD:{_currentActorId},{info.SlotIdx},{key},{value}");
        }

        // 数値入力行。行の作り（値域が確定していればスライダー）と「⟲ 既定値に戻す」は
        // 共通入口 BuildResettableFloatRow に一任する。
        // SET_INTERACTION_FIELD の key は InteractionSourceComponentData の serde 名と一致。
        void AddFloatRow(StackPanel parent, string label, float value, string key, string format)
        {
            parent.Children.Add(BuildResettableFloatRow(
                info.SlotIdx, InteractionSourceComponentType, label, value, key, format,
                v => SendField(key, v.ToString(CultureInfo.InvariantCulture))));
        }

        var section = BuildSection("インタラクション");
        var body    = (StackPanel)section.Child;

        // 有効フラグ（ゲームロジックから一時的に場への書き込みを止めるためのデータ側スイッチ）。
        body.Children.Add(BuildCheckRow("有効", info.InteractionEnabled,
            v => SendField("enabled", v ? "true" : "false")));
        // 影響半径（m）。この半径内の草が押し倒される。
        AddFloatRow(body, "影響半径(m)", info.InteractionRadius,   "radius",   "F2");
        // 書き込みの強さ（0..1）。0 で無効、1 で速度をそのまま場へ書く。
        AddFloatRow(body, "強さ(0-1)",   info.InteractionStrength, "strength", "F2");

        body.Children.Add(new TextBlock
        {
            Text         = "移動速度は Transform の変化から自動算出されます（速度の指定は不要）。"
                         + "止まると数秒で草が元に戻ります。",
            Foreground   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontSize     = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 2),
        });

        sp.Children.Add(section);
        return sp;
    }

    /// <summary>
    /// JointAttachComponent のインスペクター UI を構築して返す。
    /// 追従先ジョイント（親モデルのボーン）選択ドロップダウンと、位置/回転(YXZオイラー角・度)/
    /// スケールのオフセット3行を提供する。変更時は SET_JOINTATTACH_FIELD:{actor},{slot},{key},{value}
    /// を送信する（LightComponent の SET_LIGHT_FIELD と同じ流儀）。
    /// 親アクターに ModelComponent が無い場合、ランタイムから joints は空配列で送られてくるため、
    /// その場合はドロップダウンの代わりに警告テキストを表示する。
    /// </summary>
    private UIElement BuildJointAttachSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // フィールド変更をランタイムへ送信するローカル関数。
        void SendField(string key, string value)
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_JOINTATTACH_FIELD:{_currentActorId},{info.SlotIdx},{key},{value}");
        }

        var joints = info.Joints ?? Array.Empty<string>();

        // ── ジョイント選択ドロップダウン ─────────────────────────
        if (joints.Length == 0)
        {
            // 親アクターに Model が無い（またはジョイント情報が空の）場合の警告表示。
            sp.Children.Add(new TextBlock
            {
                Text = "親アクターに Model がありません",
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0x88, 0x44)),
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4),
            });
        }
        else
        {
            var jointRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
            jointRow.Children.Add(new TextBlock
            {
                Text = "ジョイント", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
            });
            var jointCombo = new ComboBox { Width = 170, FontSize = 11, Margin = new Thickness(4, 0, 0, 0) };
            foreach (var j in joints)
                jointCombo.Items.Add(new ComboBoxItem { Content = j, Tag = j });
            var curJointIdx = Array.FindIndex(joints, j => j == info.JointName);
            jointCombo.SelectedIndex = curJointIdx >= 0 ? curJointIdx : 0;
            jointCombo.SelectionChanged += (_, _) =>
            {
                if (jointCombo.SelectedItem is ComboBoxItem item && item.Tag is string joint)
                    SendField("joint_name", joint);
            };
            jointRow.Children.Add(jointCombo);
            sp.Children.Add(jointRow);
        }

        // ── オフセット3行（位置/回転/スケール）を送信するローカル関数群 ──
        // XYZ 3成分をまとめて "x,y,z" 形式でコミットする共通処理。
        //
        // resetField は Rust の JointAttachComponentData の serde フィールド名。
        // 非 null なら行の右端に「⟲ 既定値に戻す」を添える（null＝リセット対象外）。
        // SET キーが 1 本でも Rust 側は 3 成分の配列フィールドなので、
        // **行全体で 1 パス**を指定すればよく Undo も 1 回で戻る。
        // なお SET キー offset_rot と serde 名 offset_rot_deg は綴りが食い違うため、
        // 両者を混同しないよう別引数で受け取る。
        void AddOffsetRow(string label, float x, float y, float z, string key, string? resetField = null)
        {
            var row = BuildXYZRowSimple(label, x, y, z);
            sp.Children.Add(resetField is null
                ? row.element
                : WithFieldReset(row.element, info.SlotIdx, JointAttachComponentType, resetField, label));
            void Commit()
            {
                if (!float.TryParse(row.tx.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vx)) return;
                if (!float.TryParse(row.ty.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vy)) return;
                if (!float.TryParse(row.tz.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var vz)) return;
                SendField(key, FormattableString.Invariant($"{vx},{vy},{vz}"));
            }
            row.tx.LostFocus += (_, _) => Commit(); row.tx.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; } };
            row.ty.LostFocus += (_, _) => Commit(); row.ty.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; } };
            row.tz.LostFocus += (_, _) => Commit(); row.tz.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; } };
            NumericDragBehavior.SetOnDrag(row.tx, Commit);
            NumericDragBehavior.SetOnDrag(row.ty, Commit);
            NumericDragBehavior.SetOnDrag(row.tz, Commit);
        }

        // 位置オフセット（配置＝構造の定義に当たるので ⟲ は付けない）
        AddOffsetRow("位置オフセット", info.JaOffPX, info.JaOffPY, info.JaOffPZ, "offset_pos");
        // 回転オフセット（YXZ オイラー角・度）。既定 [0,0,0] へ戻せるようにする。
        // SET キー offset_rot に対し serde 名は offset_rot_deg（食い違うので明示的に渡す）。
        AddOffsetRow("回転オフセット", info.JaOffEX, info.JaOffEY, info.JaOffEZ, "offset_rot", "offset_rot_deg");
        // スケールオフセット（既定 1。負値はスケール反転になり得るため通常は正値を推奨するが、
        // Light 同様の常識的な範囲として特にクランプはしない＝入力値をそのまま送信する）
        AddOffsetRow("スケールオフセット", info.JaOffSX, info.JaOffSY, info.JaOffSZ, "offset_scale", "offset_scale");

        return sp;
    }

    /// <summary>
    /// SkyboxComponent のインスペクター UI を構築して返す。
    /// equirectangular（正距円筒）テクスチャの参照・追従モード（カメラ固定/ワールド配置）・
    /// 強度・ティント（リニア RGB）を編集し、変更時は SET_SKYBOX_FIELD:{actor},{slot},{key},{value} を送信する。
    /// </summary>
    private UIElement BuildSkyboxSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // フィールド変更をランタイムへ送信するローカル関数。
        void SendField(string key, string value)
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_SKYBOX_FIELD:{_currentActorId},{info.SlotIdx},{key},{value}");
        }

        // ── テクスチャ参照（equirectangular 画像。SpriteComponent と同じ FileRefBuilder + D&D 流儀）──
        sp.Children.Add(FileRefBuilder.Build(
            "テクスチャ", info.SkyboxTexturePath,
            [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".webp", ".hdr"],
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "Skybox テクスチャファイルを選択（equirectangular）",
                    Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.webp;*.hdr|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path =>
            {
                if (_currentActorId < 0) return;
                // 絶対パスを仮想パスに変換してからランタイムへ送信する
                var virtualPath = VirtualPath.ToVirtual(path, _assetsPath);
                SendField("texture_path", virtualPath);
            }));

        // ── 追従モード（カメラ固定 / ワールド配置）ドロップダウン ──
        var modes = new[]
        {
            ("camera_locked",  "カメラ固定 (Camera Locked)"),
            ("world_anchored", "ワールド配置 (World Anchored)"),
        };
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        modeRow.Children.Add(new TextBlock
        {
            Text = "追従モード", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        var modeCombo = new ComboBox { Width = 200, FontSize = 11, Margin = new Thickness(4, 0, 0, 0) };
        foreach (var (val, label) in modes)
            modeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = val });
        var curModeIdx = Array.FindIndex(modes, t => t.Item1 == info.SkyboxMode);
        modeCombo.SelectedIndex = curModeIdx >= 0 ? curModeIdx : 0;
        modeCombo.SelectionChanged += (_, _) =>
        {
            if (modeCombo.SelectedItem is ComboBoxItem item && item.Tag is string mode)
                SendField("mode", mode);
        };
        modeRow.Children.Add(modeCombo);
        sp.Children.Add(modeRow);

        // ── 強度 ──
        // 行の作りと「⟲ 既定値に戻す」は共通入口へ一任する（SET キー "intensity" は serde 名と一致）。
        // 強度は上限クランプが無い（HDR 許容）ため値域表には無く、数値入力行になる。
        sp.Children.Add(BuildResettableFloatRow(
            info.SlotIdx, SkyboxComponentType, "強度", info.SkyboxIntensity, "intensity",
            SkyboxNumberFormat,
            v => SendField("intensity", v.ToString(CultureInfo.InvariantCulture))));

        // ── ティント（リニア RGB）────────────────────────────
        float curR = info.SkyboxTintR, curG = info.SkyboxTintG, curB = info.SkyboxTintB;
        var tintSwatch = new Border
        {
            Width = 120, Height = 22, Margin = new Thickness(0, 2, 0, 2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(
                Color.FromRgb(LinearToSrgbByte(curR), LinearToSrgbByte(curG), LinearToSrgbByte(curB))),
            Cursor = Cursors.Hand,
        };
        var tintRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        tintRow.Children.Add(new TextBlock
        {
            Text = "ティント", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11, Width = 90, VerticalAlignment = VerticalAlignment.Center,
        });
        tintRow.Children.Add(tintSwatch);
        tintSwatch.MouseLeftButtonDown += (_, _) =>
        {
            // ティントはアルファを持たない（a=1 固定）。ColorPickerWindow の a は無視する。
            var result = ColorPickerWindow.ShowDialog(Window.GetWindow(this), curR, curG, curB, 1f);
            if (result is null) return;
            (curR, curG, curB, _) = result.Value;
            tintSwatch.Background = new SolidColorBrush(
                Color.FromRgb(LinearToSrgbByte(curR), LinearToSrgbByte(curG), LinearToSrgbByte(curB)));
            SendField("tint", FormattableString.Invariant($"{curR},{curG},{curB}"));
        };
        // 色は値域表の対象外（HDR 許容で上限が無い）。⟲ だけ添える。
        sp.Children.Add(WithFieldReset(tintRow, info.SlotIdx, SkyboxComponentType, "tint", "ティント"));

        return sp;
    }

    /// <summary>
    /// ParticleEmitterComponent のインスペクター UI を構築して返す。
    /// 再生・形状・出現範囲・放出制御・寿命/速度・方向・物理・回転/サイズ・テクスチャリスト・
    /// ブレンド・空間を編集し、変更時は SET_PARTICLE_FIELD:{actor},{slot},{key},{value} を送信する。
    /// カーブ（速度/回転速度/スケール/色カーブ配列）は CurveEditorControl（per-key 補間タイプ対応）で編集する。
    /// </summary>
    private UIElement BuildParticleSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // Pixel 形状時は無意味になる行（サイズ/スケール/回転関連）をここへ集め、
        // 形状ドロップダウン変更時にまとめて表示/非表示を切り替える。
        var pixelHiddenRows = new List<UIElement>();

        // マジックナンバー禁止: フィールドごとの妥当な下限/上限をここで名前付き定数化する。
        const double MinZero          = 0.0;                    // 秒/寿命/初速/サイズ/半径/drag/prewarm/delay/interval/particles_per_emit の下限
        const double NoLimit          = double.PositiveInfinity; // 上限なし
        const double MaxParticlesCap  = 65536.0;                 // max_particles の上限
        const double RandomnessMax    = 1.0;                     // direction_randomness の上限（0..1）
        const int    MaxTexturePaths  = 8;                       // texture_paths の要素数上限
        const string ParticleNumberFormat = "F3";                // 数値行の表示書式（AddFloatRowN の非整数と同じ）

        // フィールド変更をランタイムへ送信するローカル関数。
        // key は ACTOR_COMPONENTS の JSON フィールド名と同一（emit_interval / dir_x / shape / playing 等）。
        void SendField(string key, string value)
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_PARTICLE_FIELD:{_currentActorId},{info.SlotIdx},{key},{value}");
        }

        // 見出し（薄い色の小ラベル）を追加するローカル関数。セクション区切りに使う。
        void AddHeading(string text) => sp.Children.Add(new TextBlock
        {
            Text       = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x99, 0xBB)),
            FontSize   = 10, FontWeight = FontWeights.Bold,
            Margin     = new Thickness(0, 8, 0, 2),
        });

        // ラベル + CheckBox の横並び行を生成し、bool を "1"/"0" で送信するローカル関数。
        void AddCheckRow(string label, bool isChecked, string key)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 11, Width = ParticleRowLabelWidth, VerticalAlignment = VerticalAlignment.Center,
            });
            var check = new CheckBox
            {
                IsChecked = isChecked, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
            };
            check.Checked   += (_, _) => SendField(key, "1");
            check.Unchecked += (_, _) => SendField(key, "0");
            row.Children.Add(check);
            sp.Children.Add(row);
        }

        // BuildLabeledNumberRow と同じ見た目の数値 TextBox を単体で作るローカル関数
        // （横並び行を自前の Grid で組み立てるために使う。スタイルは既存 UI と統一する）。
        TextBox MakeNumberBox(float value, string format)
        {
            var initText = value.ToString(format, CultureInfo.InvariantCulture);
            var tb = new TextBox
            {
                Text              = initText,
                Background        = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
                Foreground        = new SolidColorBrush(Colors.White),
                BorderBrush       = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
                BorderThickness   = new Thickness(1),
                FontSize          = 11,
                Padding           = new Thickness(3, 1, 3, 1),
                Margin            = new Thickness(1, 1, 2, 1),
                VerticalAlignment = VerticalAlignment.Center,
                SelectionBrush    = new SolidColorBrush(Color.FromArgb(0x66, 0x33, 0x99, 0xFF)),
            };
            AttachAutoSelectBehavior(tb);
            return tb;
        }

        // N 個の数値フィールドを 1 つのラベルの右に横並びで配置する行を生成する共通ヘルパー。
        // fields は (初期値, 送信キー) の組。ドラッグ/Enter/フォーカス喪失で該当キーを個別に送信する。
        // min/max はドラッグ中・確定時の両方でクランプする（NumericDragBehavior.Attach に一元化）。
        //
        // resetField は Rust の ParticleEmitterComponentData の serde フィールドパス。
        // 非 null なら行の右端に「⟲ 既定値に戻す」を添える（null＝リセット対象外）。
        // SET キーが x/y/z・min/max に割れている行でも、Rust 側は 1 本の配列フィールド
        // （gravity / lifetime / size_range 等）なので **行全体で 1 パス**を指定すればよく、
        // Undo も 1 回で戻る。
        UIElement AddFloatRowN(string label, (float value, string key)[] fields, bool isInt,
                                double min = double.NegativeInfinity, double max = double.PositiveInfinity,
                                string? resetField = null)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ParticleRowLabelWidth) });
            for (int i = 0; i < fields.Length; i++)
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lbl = new TextBlock
            {
                Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(lbl, 0);
            row.Children.Add(lbl);

            string fmt = isInt ? "F0" : "F3";
            for (int i = 0; i < fields.Length; i++)
            {
                var (value, key) = fields[i];
                var tb = MakeNumberBox(value, fmt);
                Grid.SetColumn(tb, i + 1);
                row.Children.Add(tb);

                void Commit()
                {
                    if (!float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
                    v = (float)Math.Clamp(v, min, max);
                    var text = isInt
                        ? ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture)
                        : v.ToString(CultureInfo.InvariantCulture);
                    SendField(key, text);
                }
                tb.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; } };
                tb.LostFocus += (_, _) => Commit();
                NumericDragBehavior.Attach(tb, sensitivity: isInt ? 1.0 : 0.1, isInteger: isInt, onDrag: Commit, min: min, max: max);
            }
            // ⟲ を添える場合は包んだ側を親へ入れる（表示切替も包んだ側に掛かるようにする）。
            UIElement element = resetField is null
                ? row
                : WithFieldReset(row, info.SlotIdx, ParticleEmitterComponentType, resetField, label);
            sp.Children.Add(element);
            return element;
        }

        // 数値 1 個の行。Enter / フォーカス喪失 / ドラッグで指定キーの値を送信する。
        // isInt=true のときは整数化してから送信する（max_particles 用）。min/max はドラッグ・確定時共にクランプ。
        //
        // 値域が確定している単一スカラー（値域表に載っているもの）は共通入口
        // BuildResettableFloatRow へ回してスライダー行にする。多値行・整数行は
        // 専用レイアウト（横並び・整数化）が要るためこの行ビルダのまま ⟲ だけ添える。
        UIElement AddFloatRow(string label, float value, string key, bool isInt = false,
                               double min = double.NegativeInfinity, double max = double.PositiveInfinity,
                               string? resetField = null)
        {
            if (!isInt && resetField is not null
                && ComponentFieldRanges.TryGet(ParticleEmitterComponentType, resetField, out _))
            {
                var slider = BuildResettableFloatRow(
                    info.SlotIdx, ParticleEmitterComponentType, label, value, resetField,
                    ParticleNumberFormat,
                    v => SendField(key, v.ToString(CultureInfo.InvariantCulture)),
                    // 多値行（AddFloatRowN）と同じラベル幅を渡し、セクション内でラベル位置をそろえる。
                    ParticleRowLabelWidth);
                sp.Children.Add(slider);
                return slider;
            }
            return AddFloatRowN(label, new[] { (value, key) }, isInt, min, max, resetField);
        }

        // 2 個の数値フィールド（min/max ペア等）を横並びで配置する行。
        UIElement AddFloatRow2(string label, float v1, string k1, float v2, string k2,
                                double min = double.NegativeInfinity, double max = double.PositiveInfinity,
                                string? resetField = null)
            => AddFloatRowN(label, new[] { (v1, k1), (v2, k2) }, false, min, max, resetField);

        // 3 個の数値フィールド（XYZ 等）を横並びで配置する行。
        UIElement AddFloatRow3(string label, float v1, string k1, float v2, string k2, float v3, string k3,
                                double min = double.NegativeInfinity, double max = double.PositiveInfinity,
                                string? resetField = null)
            => AddFloatRowN(label, new[] { (v1, k1), (v2, k2), (v3, k3) }, false, min, max, resetField);

        // ドロップダウン（enum tag）行を生成するローカル関数。
        // options は (tag, label) のリスト。選択変更時に SendField(key, tag) を送る。
        // extra は選択変更時の追加コールバック（表示切替などに使う。無ければ null）。
        ComboBox AddDropdownRow(string label, (string tag, string text)[] options, string current, string key,
                                 Action<string>? extra = null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 11, Width = ParticleRowLabelWidth, VerticalAlignment = VerticalAlignment.Center,
            });
            var combo = new ComboBox { Width = 150, FontSize = 11, Margin = new Thickness(4, 0, 0, 0) };
            foreach (var (tag, text) in options)
                combo.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
            var curIdx = Array.FindIndex(options, o => o.tag == current);
            combo.SelectedIndex = curIdx >= 0 ? curIdx : 0;
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem item && item.Tag is string val)
                {
                    SendField(key, val);
                    extra?.Invoke(val);
                }
            };
            row.Children.Add(combo);
            sp.Children.Add(row);
            return combo;
        }

        // ── 再生 ───────────────────────────────────────────────
        AddHeading("再生");
        AddCheckRow("再生", info.PePlaying, "playing");

        // ── 形状 ───────────────────────────────────────────────
        AddHeading("形状");
        // "pixel" が旧 "point" 相当（先頭）。sphere/box/plane/model は据え置き。
        var shapeOptions = new (string, string)[]
        {
            ("pixel", "ピクセル (Pixel)"), ("sphere", "球 (Sphere)"), ("box", "箱 (Box)"),
            ("plane", "平面 (Plane)"), ("model", "モデル (Model)"),
        };
        // Model 形状時のみ意味を持つモデル参照行（要求11: shape!=model のとき非表示）。
        var shapeModelRow = FileRefBuilder.Build(
            "形状モデル", info.PeShapeModelPath, [".gltf", ".glb"],
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "パーティクル形状モデルを選択",
                    Filter = "モデルファイル|*.gltf;*.glb|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path =>
            {
                if (_currentActorId < 0) return;
                var virtualPath = VirtualPath.ToVirtual(path, _assetsPath);
                SendField("shape_model_path", virtualPath);
            });
        AddDropdownRow("形状", shapeOptions, info.PeShape, "shape",
            extra: shape =>
            {
                UpdatePixelHiddenVisibility(pixelHiddenRows, shape);
                shapeModelRow.Visibility = shape == "model" ? Visibility.Visible : Visibility.Collapsed;
            });
        sp.Children.Add(shapeModelRow);
        shapeModelRow.Visibility = info.PeShape == "model" ? Visibility.Visible : Visibility.Collapsed;

        // ── 出現範囲 ───────────────────────────────────────────
        AddHeading("出現範囲");
        var spawnVolumeOptions = new (string, string)[]
        {
            ("point", "点 (Point)"), ("box", "箱 (Box)"), ("sphere", "球 (Sphere)"),
        };
        // ドロップダウン → 箱半径 → 球半径 の表示順で追加する。box/sphere 行への参照は
        // まだ無いため、切替コールバックは行を作った後で AddDropdownRow の extra へ渡す
        // （ローカル関数 UpdateSpawnVolumeVisibility を経由し、行変数の前方参照を避ける）。
        List<UIElement>? spawnBoxRows = null, spawnSphereRows = null;
        void UpdateSpawnVolumeVisibility(string volume)
        {
            if (spawnBoxRows    != null) foreach (var r in spawnBoxRows)    r.Visibility = volume == "box"    ? Visibility.Visible : Visibility.Collapsed;
            if (spawnSphereRows != null) foreach (var r in spawnSphereRows) r.Visibility = volume == "sphere" ? Visibility.Visible : Visibility.Collapsed;
        }
        AddDropdownRow("出現範囲", spawnVolumeOptions, info.PeSpawnVolume, "spawn_volume",
            extra: UpdateSpawnVolumeVisibility);
        // 出現範囲が box/sphere のときだけ、それぞれ対応するパラメータ行を表示する（要求11）。
        // 箱半径・球半径は「出現範囲という構造の定義（サイズ）」なので ⟲ は付けない
        //（Rust 側も spawn_volume という enum の内側にあり、単独フィールドとして戻せない）。
        spawnBoxRows    = new List<UIElement> { AddFloatRow3("箱 半径 XYZ", info.PeSpawnBoxX, "spawn_box_x", info.PeSpawnBoxY, "spawn_box_y", info.PeSpawnBoxZ, "spawn_box_z", min: MinZero, max: NoLimit) };
        spawnSphereRows = new List<UIElement> { AddFloatRow("球 半径",  info.PeSpawnSphereRadius, "spawn_sphere_radius", min: MinZero, max: NoLimit) };
        UpdateSpawnVolumeVisibility(info.PeSpawnVolume);

        // ── 放出 ───────────────────────────────────────────────
        AddHeading("放出");
        AddFloatRow("最大パーティクル数", info.PeMaxParticles, "max_particles", isInt: true, min: 1, max: MaxParticlesCap,
            resetField: "max_particles");
        var emitModeOptions = new (string, string)[]
        {
            ("loop", "ループ"), ("once", "一回"), ("count", "回数指定"),
        };
        // 放出総数(emit_count_total)は emit_mode="count" のときのみ意味を持つ（要求11）。
        UIElement? emitCountRow = null;
        AddDropdownRow("放出モード", emitModeOptions, info.PeEmitMode, "emit_mode",
            extra: mode => { if (emitCountRow != null) emitCountRow.Visibility = mode == "count" ? Visibility.Visible : Visibility.Collapsed; });
        // 放出総数は Rust 側では EmitMode::Count { total } という enum の内側の値であり、
        // 単独フィールドとして既定値へ戻せない（既定は Loop で total 自体が存在しない）ため ⟲ は付けない。
        emitCountRow = AddFloatRow("放出総数", info.PeEmitCountTotal, "emit_count_total", isInt: true, min: MinZero, max: NoLimit);
        emitCountRow.Visibility = info.PeEmitMode == "count" ? Visibility.Visible : Visibility.Collapsed;
        AddFloatRow("開始遅延(秒)",   info.PeInitialDelay,     "initial_delay", min: MinZero, max: NoLimit,
            resetField: "initial_delay");
        AddFloatRow("プリウォーム(秒)", info.PePrewarmTime,     "prewarm_time", min: MinZero, max: NoLimit,
            resetField: "prewarm_time");
        AddFloatRow("放出間隔(秒)",   info.PeEmitInterval,     "emit_interval", min: MinZero, max: NoLimit,
            resetField: "emit_interval");
        AddFloatRow("1回の放出数",    info.PeParticlesPerEmit, "particles_per_emit", isInt: true, min: MinZero, max: NoLimit,
            resetField: "particles_per_emit");

        // ── 寿命 / 速度 ─────────────────────────────────────────
        AddHeading("寿命 / 速度");
        // SET キーは min/max に割れているが Rust 側は [f32; 2] の 1 フィールド。行全体で 1 パスを指定する。
        AddFloatRow2("寿命(秒) min/max", info.PeLifetimeMin, "lifetime_min", info.PeLifetimeMax, "lifetime_max", min: MinZero, max: NoLimit,
            resetField: "lifetime");
        AddFloatRow2("初速 min/max",     info.PeSpeedMin,    "speed_min",    info.PeSpeedMax,    "speed_max",    min: MinZero, max: NoLimit,
            resetField: "initial_speed");

        // ── 方向 ───────────────────────────────────────────────
        AddHeading("方向");
        // 符号付き（ローカル方向ベクトル）のため clamp なし。
        AddFloatRow3("放出方向(ローカル) XYZ", info.PeDirX, "dir_x", info.PeDirY, "dir_y", info.PeDirZ, "dir_z",
            resetField: "direction_local");
        // 0..1 に確定した単一スカラーなので共通入口へ回り、スライダー行になる。
        AddFloatRow("方向ランダム度(0..1)", info.PeDirectionRandomness, "direction_randomness", min: MinZero, max: RandomnessMax,
            resetField: "direction_randomness");

        // ── 物理 ───────────────────────────────────────────────
        AddHeading("物理");
        // 重力は符号付きのため clamp なし。
        AddFloatRow3("重力 XYZ", info.PeGravityX, "gravity_x", info.PeGravityY, "gravity_y", info.PeGravityZ, "gravity_z",
            resetField: "gravity");
        AddFloatRow("空気抵抗 (Drag)", info.PeDrag, "drag", min: MinZero, max: NoLimit, resetField: "drag");

        // ── 回転 / サイズ ───────────────────────────────────────
        // Pixel 形状では意味を持たないため pixelHiddenRows に集約し、形状変更時に表示/非表示を切り替える。
        AddHeading("回転 / サイズ");
        // 回転速度（度/秒）は符号付きのため clamp なし。
        pixelHiddenRows.Add(AddFloatRow2("回転速度(度/秒) min/max", info.PeRotSpeedMin, "rot_speed_min", info.PeRotSpeedMax, "rot_speed_max",
            resetField: "rot_speed_range"));
        pixelHiddenRows.Add(AddFloatRow2("サイズ倍率 min/max", info.PeSizeMin, "size_min", info.PeSizeMax, "size_max", min: MinZero, max: NoLimit,
            resetField: "size_range"));
        // 初期回転範囲（度）。符号付きのため clamp なし。
        pixelHiddenRows.Add(AddFloatRow2("初期回転(度) min/max", info.PeInitRotMin, "initial_rot_min", info.PeInitRotMax, "initial_rot_max",
            resetField: "initial_rotation_range"));

        // ── カーブ ─────────────────────────────────────────────
        // 速度/回転速度(1ch)・スケール(3ch=xyz) を CurveEditorControl（per-key 補間タイプ対応）で編集する。
        // 編集確定（CurveChanged）時に SET_PARTICLE_CURVE:{actor},{slot},{curve_id},{json} を送信する。
        AddHeading("カーブ");

        // カーブ送信のローカル関数。curve_id ∈ speed|rot_speed|scale|colors。
        // json は ParamCurve の serde JSON（colors のみ ParamCurve 配列）。
        void SendCurve(string curveId, string json)
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_PARTICLE_CURVE:{_currentActorId},{info.SlotIdx},{curveId},{json}");
        }

        // 折りたたみカーブ行（ラベル＋ミニプレビュー＋展開でエディタ）を作るローカル関数。
        // channelCount はパース失敗時のフォールバック用チャンネル数（speed/rot_speed=1、scale=3）。
        // expandKey は _expandStates での展開状態の永続化キー
        // （Rust が SET_PARTICLE_FIELD/CURVE のたびに ACTOR_COMPONENTS を再送し、その結果
        //   このパネル全体が再構築されて Expander が作り直される仕様のため、素の IsExpanded 初期値
        //   だけでは編集直後に閉じたように見えてしまう。詳細は _expandStates のコメント）。
        Expander BuildCurveRow(string label, string curveId, string json, bool isHsva, int channelCount, string expandKey)
        {
            var curve  = ParamCurve.FromJson(json) ?? ParamCurve.DefaultWithChannels(channelCount);
            var editor = new CurveEditorControl(curve, isHsva);

            // ヘッダー: ラベル＋ミニプレビュー（編集で作り直す）。
            var miniHost = new ContentControl
            {
                Content = CurveEditorControl.BuildMiniPreview(curve, isHsva, 64, 18),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 90,
            });
            header.Children.Add(miniHost);

            // 編集確定でランタイムへ送信し、ミニプレビューを更新する。
            editor.CurveChanged += (_, _) =>
            {
                SendCurve(curveId, editor.Curve.ToJson());
                miniHost.Content = CurveEditorControl.BuildMiniPreview(editor.Curve, isHsva, 64, 18);
            };

            var expander = new Expander
            {
                Header = header, Content = editor,
                Margin = new Thickness(0, 1, 0, 1),
            };
            _expandStates.Track(expander, ExpandKeyParticleCurvePrefix + expandKey, defaultExpanded: false);
            return expander;
        }

        // speed(1ch) / rot_speed(1ch) / scale(3ch xyz)。scale はサイズと同様 Pixel 時は非表示。
        var speedCurveRow = BuildCurveRow("速度", "speed", info.PeSpeedCurveJson, isHsva: false, channelCount: 1, expandKey: $"{info.SlotIdx}:speed");
        sp.Children.Add(speedCurveRow);

        var rotSpeedCurveRow = BuildCurveRow("回転速度", "rot_speed", info.PeRotSpeedCurveJson, isHsva: false, channelCount: 1, expandKey: $"{info.SlotIdx}:rot_speed");
        sp.Children.Add(rotSpeedCurveRow);
        pixelHiddenRows.Add(rotSpeedCurveRow);

        var scaleCurveRow = BuildCurveRow("スケール", "scale", info.PeScaleCurveJson, isHsva: false, channelCount: 3, expandKey: $"{info.SlotIdx}:scale");
        sp.Children.Add(scaleCurveRow);
        pixelHiddenRows.Add(scaleCurveRow);

        // ── 色カーブ（color_curves：4ch HSVA の配列。最低 1 要素）──────────
        // 単体の色カーブ／ランダム色カーブを廃止し、常に「色カーブのリスト」として編集する。
        // 要素が 1 つだけのときは削除不可（最後の 1 本は必ず残す）。
        AddHeading("色カーブ");
        var colorCurves = ParamCurve.ListFromJson(info.PeColorCurvesJson);
        if (colorCurves.Count == 0) colorCurves.Add(ParamCurve.DefaultWithChannels(4)); // 欠落時は最低 1 本を用意する
        var colorPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

        // 色カーブ配列全体をランタイムへ送信するローカル関数（curve_id="colors"）。
        void SendColorCurves() => SendCurve("colors", ParamCurve.ToJsonArray(colorCurves));

        // 色カーブリスト UI を作り直すローカル関数（追加/削除で全再構築）。
        void RebuildColorCurves()
        {
            colorPanel.Children.Clear();
            for (int i = 0; i < colorCurves.Count; i++)
            {
                int idx = i;
                var expandKey = $"{info.SlotIdx}:colors:{idx}";
                var editor = new CurveEditorControl(colorCurves[idx], isHsva: true);
                // 編集で該当要素を差し替えて配列全体を送信する。
                editor.CurveChanged += (_, _) => { colorCurves[idx] = editor.Curve; SendColorCurves(); };

                var miniHost = new ContentControl
                {
                    Content = CurveEditorControl.BuildMiniPreview(colorCurves[idx], true, 64, 18),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                };
                editor.CurveChanged += (_, _) => miniHost.Content = CurveEditorControl.BuildMiniPreview(editor.Curve, true, 64, 18);

                var header = new StackPanel { Orientation = Orientation.Horizontal };
                header.Children.Add(new TextBlock
                {
                    Text = $"#{idx}", Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 32,
                });
                header.Children.Add(miniHost);
                // 最後の 1 本は削除不可（色カーブは必ず 1 要素以上を維持する契約）。
                var delBtn = new Button
                {
                    Content = "削除", FontSize = 10, Width = 40, Height = 20,
                    Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    IsEnabled = colorCurves.Count > 1,
                };
                delBtn.Click += (_, _) =>
                {
                    if (colorCurves.Count <= 1) return;
                    colorCurves.RemoveAt(idx);
                    // 削除した要素のキーを消す（残すと添字が繰り上がった別要素へ状態が漏れる）。
                    _expandStates.Remove(ExpandKeyParticleCurvePrefix + expandKey);
                    SendColorCurves();
                    RebuildColorCurves();
                };
                header.Children.Add(delBtn);

                var expander = new Expander
                {
                    Header = header, Content = editor,
                    Margin = new Thickness(0, 1, 0, 1),
                };
                _expandStates.Track(expander, ExpandKeyParticleCurvePrefix + expandKey, defaultExpanded: false);
                colorPanel.Children.Add(expander);
            }
        }
        RebuildColorCurves();

        // 追加ボタン（既定 4ch HSVA カーブを 1 本追加）。
        var addColorBtn = new Button
        {
            Content = "＋ 色カーブを追加", FontSize = 11, Width = 150, Height = 22,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2),
        };
        addColorBtn.Click += (_, _) =>
        {
            colorCurves.Add(ParamCurve.DefaultWithChannels(4));
            SendColorCurves();
            RebuildColorCurves();
        };
        sp.Children.Add(addColorBtn);
        sp.Children.Add(colorPanel);

        // ── テクスチャ（texture_paths：文字列配列、最大 8）─────────────
        AddHeading("テクスチャ");
        var texturePaths = ParseTexturePaths(info.PeTexturePathsJson);
        var texturePanel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

        // テクスチャ配列全体をランタイムへ送信するローカル関数。
        void SendTexturePaths() => SendField("texture_paths", JsonSerializer.Serialize(texturePaths));

        void RebuildTexturePaths()
        {
            texturePanel.Children.Clear();
            if (texturePaths.Count == 0)
            {
                texturePanel.Children.Add(new TextBlock
                {
                    Text = "（空：単色で描画）",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontSize = 10, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 2, 0, 2),
                });
            }
            for (int i = 0; i < texturePaths.Count; i++)
            {
                int idx = i;
                var rowGrid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var fileRow = FileRefBuilder.Build(
                    $"#{idx}", texturePaths[idx], [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".webp"],
                    () =>
                    {
                        var dlg = new OpenFileDialog
                        {
                            Title  = "パーティクルテクスチャを選択",
                            Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.webp|すべてのファイル|*.*",
                        };
                        return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
                    },
                    path =>
                    {
                        var virtualPath = VirtualPath.ToVirtual(path, _assetsPath);
                        texturePaths[idx] = virtualPath;
                        SendTexturePaths();
                        RebuildTexturePaths();
                    });
                Grid.SetColumn(fileRow, 0);
                rowGrid.Children.Add(fileRow);

                var delBtn = new Button
                {
                    Content = "削除", FontSize = 10, Width = 40, Height = 20,
                    Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                delBtn.Click += (_, _) =>
                {
                    texturePaths.RemoveAt(idx);
                    SendTexturePaths();
                    RebuildTexturePaths();
                };
                Grid.SetColumn(delBtn, 1);
                rowGrid.Children.Add(delBtn);

                texturePanel.Children.Add(rowGrid);
            }
        }
        RebuildTexturePaths();
        sp.Children.Add(texturePanel);

        var addTexBtn = new Button
        {
            Content = "＋ テクスチャを追加", FontSize = 11, Width = 150, Height = 22,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2),
        };
        addTexBtn.Click += (_, _) =>
        {
            if (texturePaths.Count >= MaxTexturePaths) return; // 上限（最大 8 枚）を超えない
            texturePaths.Add("");
            SendTexturePaths();
            RebuildTexturePaths();
        };
        sp.Children.Add(addTexBtn);

        // ── ブレンド ───────────────────────────────────────────
        AddHeading("ブレンド");
        var blends = new (string, string)[]
        {
            ("none",   "不透明 (None)"),
            ("normal", "通常 (Normal)"),
            ("add",    "加算 (Add)"),
            ("sub",    "減算 (Sub)"),
            ("mul",    "乗算 (Mul)"),
            ("screen", "スクリーン (Screen)"),
        };
        AddDropdownRow("合成方法", blends, info.PeBlend, "blend");

        // ── 空間 ───────────────────────────────────────────────
        AddHeading("シミュレーション空間");
        var spaces = new (string, string)[] { ("world", "ワールド (World)"), ("local", "ローカル (Local)") };
        AddDropdownRow("空間", spaces, info.PeSimSpace, "sim_space");

        // 初期構築時点での形状に合わせて Pixel 専用非表示行を反映する。
        UpdatePixelHiddenVisibility(pixelHiddenRows, info.PeShape);

        return sp;
    }

    /// <summary>
    /// パーティクルの形状が "pixel" のとき、サイズ/スケール/回転関連の行（意味を持たない）を
    /// 一括で非表示にする。BuildParticleSlotContent の形状ドロップダウン変更時と初期構築時に呼ぶ。
    /// </summary>
    private static void UpdatePixelHiddenVisibility(List<UIElement> rows, string shape)
    {
        var visibility = shape == "pixel" ? Visibility.Collapsed : Visibility.Visible;
        foreach (var row in rows) row.Visibility = visibility;
    }

    /// <summary>texture_paths の生 JSON（文字列配列）をパースする。失敗・欠落時は空リストを返す。</summary>
    private static List<string> ParseTexturePaths(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// WaterVolumeComponent の spline_points（"x,y,z;x,y,z;..." 形式・アクタ相対座標）を
    /// 制御点リストへパースする。空文字列/不正な要素は無視する（壊れた1点を捨てても致命傷にならないため）。
    /// </summary>
    private static List<(float x, float y, float z)> ParseSplinePoints(string raw)
    {
        var points = new List<(float x, float y, float z)>();
        if (string.IsNullOrWhiteSpace(raw)) return points;
        foreach (var token in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split(',');
            if (parts.Length != 3) continue;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) continue;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) continue;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) continue;
            points.Add((x, y, z));
        }
        return points;
    }

    /// <summary>制御点リストを spline_points の送信形式（"x,y,z;x,y,z;..."）へ直列化する。空リスト = 空文字列。</summary>
    private static string SerializeSplinePoints(List<(float x, float y, float z)> points) =>
        string.Join(";", points.Select(p => FormattableString.Invariant($"{p.x},{p.y},{p.z}")));

    /// <summary>
    /// AudioComponent のインスペクター UI を構築して返す。
    /// 音声ファイル・音量・ループ・自動再生・3D空間再生・減衰距離・パンを編集し、
    /// 変更時は SET_AUDIO_FIELD:{actor},{slot},{key},{value} を送信する。
    /// </summary>
    private UIElement BuildAudioSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // フィールド変更をランタイムへ送信するローカル関数（key ∈ path/volume/loop/play_on_start/spatial/min_distance/max_distance/pan）
        void SendField(string key, string value)
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_AUDIO_FIELD:{_currentActorId},{info.SlotIdx},{key},{value}");
        }

        // ── 音声ファイル選択行 ─────────────────────────────────
        sp.Children.Add(FileRefBuilder.Build(
            "音声",
            info.AudioPath,
            [".wav", ".ogg", ".mp3", ".flac"],
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "音声ファイルを選択",
                    Filter = "音声ファイル|*.wav;*.ogg;*.mp3;*.flac|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path =>
            {
                if (_currentActorId < 0) return;
                // 絶対パスを assets:// 仮想パスに変換してからランタイムへ送信する
                var virtualPath = VirtualPath.ToVirtual(path, _assetsPath);
                SendField("path", virtualPath);
            }));

        // ── 音量フィールド ─────────────────────────────────────
        // 行の作りと「⟲ 既定値に戻す」は共通入口へ一任する（SET キー "volume" は serde 名と一致）。
        // 音量は上限クランプが無い（1.0 超の増幅を許す）ため値域表には無く、数値入力行になる。
        sp.Children.Add(BuildResettableFloatRow(
            info.SlotIdx, AudioComponentType, "音量", info.AudioVolume, "volume",
            AudioLevelNumberFormat,
            // 負値は無効なため下限でクランプしてから送信する。
            v => SendField("volume", MathF.Max(AudioVolumeMin, v).ToString(CultureInfo.InvariantCulture))));

        // ── チェックボックス行（ループ・自動再生・3D空間再生）────
        // ラベル + CheckBox の横並び行を生成して SET_AUDIO_FIELD を送信するローカル関数
        CheckBox AddCheckRow(string label, bool isChecked, string key)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text              = label,
                Foreground        = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize          = 11,
                Width             = 90,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var check = new CheckBox
            {
                IsChecked         = isChecked,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(4, 0, 0, 0),
            };
            // bool 値は "1"/"0" で送信する
            check.Checked   += (_, _) => SendField(key, "1");
            check.Unchecked += (_, _) => SendField(key, "0");
            row.Children.Add(check);
            sp.Children.Add(row);
            return check;
        }
        AddCheckRow("ループ",      info.AudioLoop,        "loop");
        AddCheckRow("自動再生",    info.AudioPlayOnStart, "play_on_start");
        AddCheckRow("3D空間再生",  info.AudioSpatial,     "spatial");

        // 補足説明を薄い色で表示するローカル関数
        void AddHint(string text) => sp.Children.Add(new TextBlock
        {
            Text         = text,
            Foreground   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontSize     = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 2, 0, 2),
        });

        // ── 減衰開始距離 / 無音距離（3D空間再生 ON 時のみ有効）──
        AddHint("以下の距離設定は 3D空間再生が ON のときに適用されます");

        // 数値フィールド + SET_AUDIO_FIELD 送信をまとめて構築するローカル関数。
        // 行の作りと ⟲ は共通入口へ一任する（距離系の SET キーは serde 名と一致）。
        void AddFloatRow(string label, float value, string key)
        {
            sp.Children.Add(BuildResettableFloatRow(
                info.SlotIdx, AudioComponentType, label, value, key, AudioDistanceNumberFormat,
                v => SendField(key, v.ToString(CultureInfo.InvariantCulture))));
        }
        AddFloatRow("減衰開始距離", info.AudioMinDistance, "min_distance");
        AddFloatRow("無音距離",     info.AudioMaxDistance, "max_distance");

        // ── パン（3D空間再生 OFF 時のみ有効）──────────────────
        // パンは値域が -1..1 に確定している（audio_ops.rs の clamp）ので、
        // 値域表の引き当てによりスライダー行になる。
        AddHint($"パン（{AudioPanMin:F0}=左 〜 {AudioPanMax:F0}=右）は 3D空間再生が OFF のときのみ有効です");
        sp.Children.Add(BuildResettableFloatRow(
            info.SlotIdx, AudioComponentType, "パン", info.AudioPan, "pan", AudioLevelNumberFormat,
            // ランタイム側と同じ範囲で二重にクランプしてから送信する。
            v => SendField("pan", Math.Clamp(v, AudioPanMin, AudioPanMax).ToString(CultureInfo.InvariantCulture))));

        return sp;
    }

    // ── CanvasComponent inspector ─────────────────────────────

    /// <summary>CanvasComponent の幅・高さ・スケールモードを表示して IPC を送信する。</summary>
    private UIElement BuildCanvasSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // ── キャンバスを編集 ボタン ─────────────────────────────
        // シーン内キャンバスの中身（スプライト配置）を隔離した 2D 編集タブで開く。
        // アクター編集タブ表示中（ファイルタブ・キャンバス編集タブ）は既に隔離編集中のため非表示。
        if (!_isActorEditMode)
        {
            sp.Children.Add(BuildOpenCanvasEditButton());
        }

        // ── 解像度（幅・高さ）──────────────────────────────────
        // ビューポート所属のルートキャンバスは解像度が
        // 「プロジェクト設定の解像度 × 参照カメラのスケーリングモード」から自動計算されるため、
        // 手動入力フィールドの代わりに読み取り専用の算出値を表示する
        //（データの width / height は温存され、ワールドキャンバス・子キャンバスでは従来どおり編集可能）。
        var rowW = BuildLabeledNumberRow("幅",  info.Width);
        var tbW  = rowW.textBox;
        var rowH = BuildLabeledNumberRow("高さ", info.Height);
        var tbH  = rowH.textBox;
        if (_isViewportRootCanvas)
        {
            sp.Children.Add(new TextBlock
            {
                Text         = FormattableString.Invariant(
                                   $"自動: {info.AutoW:F0} × {info.AutoH:F0}（プロジェクト設定×カメラ設定から算出）"),
                Foreground   = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize     = 11,
                Margin       = new Thickness(0, 4, 0, 2),
                TextWrapping = TextWrapping.Wrap,
                ToolTip      = "ルートキャンバスの解像度はプロジェクト設定の解像度と\n"
                             + "基準領域（カメラ参照）のスケーリングモードから自動計算されます。",
            });
        }
        else
        {
            sp.Children.Add(rowW.element);
            sp.Children.Add(rowH.element);
        }

        // ── 描画ゾーン セクション（ビューポート・ルートキャンバスのみ）────────
        // 描画順（奥→手前）: 背景キャンバス | 3D ワールド | 前面キャンバス。
        // 子キャンバスはルートに従属し、ワールドキャンバスは 3D 深度で決まるため、
        // ビューポート所属のルートキャンバスにのみ表示する（Phase C）。
        if (_isViewportRootCanvas)
        {
            var zoneSep = new TextBlock
            {
                Text       = "描画ゾーン",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize   = 10,
                Margin     = new Thickness(0, 6, 0, 2),
            };
            sp.Children.Add(zoneSep);

            // 前面 / 背景 の選択専用コンボボックス
            var cmbZone = new ComboBox
            {
                FontSize   = 11,
                Margin     = new Thickness(0, 2, 0, 4),
                Padding    = new Thickness(4, 2, 4, 2),
                ToolTip    = "前面: 3D ワールドの手前に重ねるオーバーレイ（デフォルト）。\n" +
                             "背景: 3D ワールドの奥（カメラのクリアカラーの上）に描画され、\n" +
                             "必ずワールドの背景になります。",
            };
            cmbZone.Items.Add(new ComboBoxItem
            {
                Content = "前面（ワールドの手前・デフォルト）",
                Tag     = "foreground",
            });
            cmbZone.Items.Add(new ComboBoxItem
            {
                Content = "背景（ワールドの奥）",
                Tag     = "background",
            });
            cmbZone.SelectedIndex = info.DrawZone == "background" ? 1 : 0;
            sp.Children.Add(cmbZone);

            // 描画ゾーン変更を IPC 送信するローカル関数
            void CommitDrawZone()
            {
                if (_currentActorId < 0) return;
                var zone = (cmbZone.SelectedItem as ComboBoxItem)?.Tag as string ?? "foreground";
                _runtime?.SendToRuntime($"SET_CANVAS_DRAW_ZONE:{_currentActorId},{info.SlotIdx},{zone}");
            }
            cmbZone.SelectionChanged += (_, _) => CommitDrawZone();
        }

        // ── 自動スケール セクション（Actor2D = 2D キャンバス時のみ表示）──────────────────────────
        // Actor3D に Canvas をアタッチした場合（3D ワールドキャンバス）はこの設定を使わない。
        // ※ スケールモード（トランスフォーム/サイズをスケールする・アスペクト比維持）は
        //   CanvasComponent から各 2D アクターの CanvasTransform へ移動した（BuildCanvas2DTransformSection 参照）。
        var cbAutoScale = new CheckBox
        {
            Content             = "画面サイズに自動スケール",
            IsChecked           = info.AutoScale,
            Foreground          = new SolidColorBrush(Colors.White),
            FontSize            = 11,
            Margin              = new Thickness(0, 2, 0, 2),
            ToolTip             = "親キャンバスを持たないルートキャンバスにのみ有効。\nビューポートサイズの変化に応じて子 UI を自動スケールします。",
            VerticalAlignment   = VerticalAlignment.Center,
        };
        if (_isActor2D)
        {
            var autoScaleSep = new TextBlock
            {
                Text       = "画面対応",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize   = 10,
                Margin     = new Thickness(0, 2, 0, 2),
            };
            sp.Children.Add(autoScaleSep);
            sp.Children.Add(cbAutoScale);
        }

        // ── 基準領域 セクション ──────────────────────────
        // Actor3D の 3D ワールドキャンバスには基準領域の設定は不要。
        // 子キャンバス（ルートでない 2D キャンバス）は親キャンバス内に配置されるだけで
        // 基準領域の概念を持たないため非表示にする（Phase B）。
        // → シーンモードではビューポート所属のルートキャンバスのみ表示する。
        //   アクター編集タブでは従来どおり 2D アクターなら表示する（ファイル側で事前設定できるように）。
        // 「メインカメラを参照」チェックボックス（デフォルトオン）と、
        // オフ時のみ表示される手動設定 UI（ウィンドウ/カメラのコンボ + カメラドロップゾーン）で構成する。
        bool showVpRef = _isActor2D && (_isActorEditMode || _isViewportRootCanvas);
        var vpRefSep = new TextBlock
        {
            Text       = "基準領域",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize   = 10,
            Margin     = new Thickness(0, 8, 0, 2),
        };
        if (showVpRef) sp.Children.Add(vpRefSep);

        // メインカメラ参照チェックボックス（vp_ref_type == "main_camera" のときオン）
        bool isMainCamRef = info.VpRefType == "main_camera";
        var cbMainCamRef = new CheckBox
        {
            Content           = "メインカメラを参照",
            IsChecked         = isMainCamRef,
            Foreground        = new SolidColorBrush(Colors.White),
            FontSize          = 11,
            Margin            = new Thickness(0, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = "レターボックス等でゲーム表示領域が黒帯を除く矩形になる場合、\n"
                              + "UI をその内側に収めます。\n"
                              + "カメラが存在しない場合は自動的にウィンドウ基準になります。",
        };
        if (showVpRef) sp.Children.Add(cbMainCamRef);

        // 手動設定パネル（チェックがオフのときのみ表示。字下げで従属設定であることを示す）
        var vpManualPanel = new StackPanel
        {
            Margin     = new Thickness(16, 0, 0, 0),
            Visibility = isMainCamRef ? Visibility.Collapsed : Visibility.Visible,
        };

        // 参照種別ドロップダウン（ウィンドウ / カメラ）
        var cmbVpRef = new ComboBox
        {
            FontSize = 11,
            Margin   = new Thickness(0, 2, 0, 4),
            Padding  = new Thickness(4, 2, 4, 2),
        };
        cmbVpRef.Items.Add(new ComboBoxItem { Content = "ウィンドウ", Tag = "window" });
        cmbVpRef.Items.Add(new ComboBoxItem { Content = "カメラ",     Tag = "camera" });
        // main_camera のときはチェックオフ時の初期値としてウィンドウを選択しておく
        cmbVpRef.SelectedIndex = info.VpRefType == "camera" ? 1 : 0;
        vpManualPanel.Children.Add(cmbVpRef);

        // カメラ参照フィールド（D&D 受け付けエリア）
        var vpRefCameraPanel = new StackPanel { Visibility = info.VpRefType == "camera" ? Visibility.Visible : Visibility.Collapsed };

        // 基準カメラ参照は共通の参照ピッカーで組み立てる
        // （表示・D&D・✕解除・ダブルクリックジャンプ・参照先消失の警告が共通実装になる）。
        // 解除＝「ウィンドウ基準へ戻す」なので、確定・解除の両方をここで IPC へ振り分ける。
        var vpRefPicker = ReferencePicker.Create(
            new ReferenceFieldSpec
            {
                Kind         = ReferenceKindCatalog.CameraKind,
                WantSlotName = true,   // actor_name + slot_name で保存する
                UnsetText    = "（未設定）",
                ClearTooltip = "参照を解除してウィンドウ基準に戻す",
                ExtraTooltip = "キャンバスの基準領域にするカメラ",
            },
            info.VpRefType == "camera" ? info.VpRefActor : null,
            info.VpRefType == "camera" ? info.VpRefSlot  : null,
            (actorName, slotName) =>
            {
                if (_currentActorId < 0) return;
                if (actorName is null)
                    _runtime?.SendToRuntime($"SET_CANVAS_VIEWPORT_REF_WINDOW:{_currentActorId},{info.SlotIdx}");
                else
                    _runtime?.SendToRuntime(
                        $"SET_CANVAS_VIEWPORT_REF_CAMERA:{_currentActorId},{info.SlotIdx},{actorName},{slotName}");
            },
            this);

        vpRefCameraPanel.Children.Add(vpRefPicker.Element);
        vpManualPanel.Children.Add(vpRefCameraPanel);
        if (showVpRef) sp.Children.Add(vpManualPanel);

        // ── イベント ──────────────────────────────────────────

        // 幅・高さを送信するローカル関数
        void CommitSize()
        {
            if (_currentActorId < 0) return;
            if (!float.TryParse(tbW.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) return;
            if (!float.TryParse(tbH.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return;
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"SET_CANVAS_SIZE:{_currentActorId},{info.SlotIdx},{w},{h}"));
        }

        // 画面サイズ自動スケールを送信するローカル関数
        void CommitAutoScale()
        {
            if (_currentActorId < 0) return;
            int v = (cbAutoScale.IsChecked == true) ? 1 : 0;
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"SET_CANVAS_AUTO_SCALE:{_currentActorId},{info.SlotIdx},{v}"));
        }

        // 手動設定（コンボの現在値）を IPC 送信するローカル関数。
        // ウィンドウ選択 → ウィンドウ基準。カメラ選択 → 参照先が設定済みならカメラ基準、
        // 未設定ならドロップで設定されるまでウィンドウ基準にしておく。
        void SendManualVpRef()
        {
            if (_currentActorId < 0) return;
            var selTag = (cmbVpRef.SelectedItem as ComboBoxItem)?.Tag as string ?? "window";
            // 参照先は UI 構築時の info ではなくピッカーの現在値から読む
            // （ドロップで張り替えた直後にコンボを切り替えても最新値が送られる）。
            if (selTag == "camera" && !string.IsNullOrEmpty(vpRefPicker.ActorName))
            {
                _runtime?.SendToRuntime(
                    $"SET_CANVAS_VIEWPORT_REF_CAMERA:{_currentActorId},{info.SlotIdx},"
                    + $"{vpRefPicker.ActorName},{vpRefPicker.SlotName}");
            }
            else
            {
                _runtime?.SendToRuntime($"SET_CANVAS_VIEWPORT_REF_WINDOW:{_currentActorId},{info.SlotIdx}");
            }
        }

        // メインカメラ参照チェック切替:
        // オン → メインカメラ基準を送信し手動 UI を隠す
        // オフ → 手動 UI を表示しコンボの現在値を送信する
        cbMainCamRef.Checked += (_, _) =>
        {
            vpManualPanel.Visibility = Visibility.Collapsed;
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_CANVAS_VIEWPORT_REF_MAIN_CAMERA:{_currentActorId},{info.SlotIdx}");
        };
        cbMainCamRef.Unchecked += (_, _) =>
        {
            vpManualPanel.Visibility = Visibility.Visible;
            SendManualVpRef();
        };

        // 手動設定: 参照種別変更（ウィンドウ / カメラ）
        cmbVpRef.SelectionChanged += (_, _) =>
        {
            if (_currentActorId < 0) return;
            var selTag = (cmbVpRef.SelectedItem as ComboBoxItem)?.Tag as string ?? "window";
            vpRefCameraPanel.Visibility = selTag == "camera" ? Visibility.Visible : Visibility.Collapsed;
            if (selTag == "window")
            {
                _runtime?.SendToRuntime($"SET_CANVAS_VIEWPORT_REF_WINDOW:{_currentActorId},{info.SlotIdx}");
            }
        };

        // D&D・参照解除は共通の参照ピッカー（vpRefPicker）が担当するため、ここには何も要らない。

        tbW.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitSize(); e.Handled = true; } };
        tbW.LostFocus += (_, _) => CommitSize();
        tbH.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitSize(); e.Handled = true; } };
        tbH.LostFocus += (_, _) => CommitSize();
        NumericDragBehavior.SetOnDrag(tbW, CommitSize); NumericDragBehavior.SetOnDrag(tbH, CommitSize);

        cbAutoScale.Checked   += (_, _) => CommitAutoScale();
        cbAutoScale.Unchecked += (_, _) => CommitAutoScale();

        // ── 重力方向セクション ──────────────────────────────────────────────────
        var gravitySep = new TextBlock
        {
            Text       = "2D 物理 – 重力方向",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize   = 10,
            Margin     = new Thickness(0, 8, 0, 2),
        };
        sp.Children.Add(gravitySep);

        // 重力方向ドロップダウン
        var cmbGravity = new ComboBox
        {
            FontSize   = 11,
            Margin     = new Thickness(0, 2, 0, 4),
            Padding    = new Thickness(4, 2, 4, 2),
            ToolTip    = "ワールド下方向: キャンバスを3Dで回転させても、中のオブジェクトは常に「ワールドの下」へ落ちます（薄い箱を傾けるとビー玉が転がる向き）。\n" +
                         "キャンバス下方向: キャンバスを回転すると重力もキャンバスに追従します（箱の同じ壁へ落ち続ける）。",
        };
        cmbGravity.Items.Add(new ComboBoxItem
        {
            Content = "ワールド下方向を正とする（デフォルト）",
            Tag     = 0,
        });
        cmbGravity.Items.Add(new ComboBoxItem
        {
            Content = "キャンバス下方向を正とする",
            Tag     = 1,
        });
        cmbGravity.SelectedIndex = info.GravityMode == 1 ? 1 : 0;
        sp.Children.Add(cmbGravity);

        // 重力方向変更を IPC 送信するローカル関数
        void CommitGravityMode()
        {
            if (_currentActorId < 0) return;
            var mode = (cmbGravity.SelectedItem as ComboBoxItem)?.Tag as int? ?? 0;
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"SET_CANVAS_GRAVITY_MODE:{_currentActorId},{info.SlotIdx},{mode}"));
        }
        cmbGravity.SelectionChanged += (_, _) => CommitGravityMode();

        // ── 3D キャンバス専用: ピボット設定（Actor2D では非表示）──────────────────────────
        // 2D アクタの CanvasTransform ピボットと同じレイアウト（3×3 プリセットグリッド + XY フィールド）。
        // アクター位置がキャンバスのどの点に対応するかを決める。
        // (0,0)=左上原点（デフォルト）, (0.5,0.5)=中央, (1,1)=右下。
        if (!_isActor2D)
        {
            var pivotLabel3d = new TextBlock
            {
                Text       = "ピボット",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize   = 11,
                Margin     = new Thickness(0, 8, 0, 2),
                ToolTip    = "アクター位置がキャンバスのどの点に対応するかを指定します。\n(0,0)=左上, (0.5,0.5)=中央, (1,1)=右下",
            };
            sp.Children.Add(pivotLabel3d);

            float curPivX = info.Canvas3dPivotX;
            float curPivY = info.Canvas3dPivotY;

            // 数値フィールド（プリセットボタンのクロージャから先行参照するため先に生成）
            var tbPivX = MakeAxisField(curPivX, "#E06C75");
            var tbPivY = MakeAxisField(curPivY, "#98C379");

            // ピボット変更を IPC 送信するローカル関数
            void CommitPivot()
            {
                if (_currentActorId < 0) return;
                if (!float.TryParse(tbPivX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) return;
                if (!float.TryParse(tbPivY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var py)) return;
                px = Math.Clamp(px, 0f, 1f);
                py = Math.Clamp(py, 0f, 1f);
                tbPivX.Text = px.ToString("G7", CultureInfo.InvariantCulture);
                tbPivY.Text = py.ToString("G7", CultureInfo.InvariantCulture);
                _runtime?.SendToRuntime(FormattableString.Invariant(
                    $"SET_CANVAS_3D_PIVOT:{_currentActorId},{info.SlotIdx},{px},{py}"));
            }

            // 3×3 プリセットグリッドと数値入力を横並び（2D CanvasTransform ピボットと同じ構成）
            var piv3dRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

            var piv3dPresetGrid = new Grid { Width = 60, Height = 60, Margin = new Thickness(0, 0, 8, 0) };
            for (int i = 0; i < 3; i++)
            {
                piv3dPresetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                piv3dPresetGrid.RowDefinitions.Add(new RowDefinition    { Height = new GridLength(1, GridUnitType.Star) });
            }
            float[] pvVals = { 0f, 0.5f, 1f };
            for (int pvRowIdx = 0; pvRowIdx < 3; pvRowIdx++)
            {
                for (int pvColIdx = 0; pvColIdx < 3; pvColIdx++)
                {
                    float pv = pvVals[pvColIdx];
                    float qv = pvVals[pvRowIdx];
                    bool pvActive = Math.Abs(curPivX - pv) < 0.01f && Math.Abs(curPivY - qv) < 0.01f;
                    var pvBtn = new Button
                    {
                        Width           = 16,
                        Height          = 16,
                        Margin          = new Thickness(1),
                        Background      = pvActive
                            ? new SolidColorBrush(Color.FromRgb(0x61, 0xAF, 0xEF))
                            : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                        BorderBrush     = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                        BorderThickness = new Thickness(1),
                        Padding         = new Thickness(0),
                        Tag             = (pv, qv),
                    };
                    pvBtn.Click += (_, _) =>
                    {
                        var (fpx, fpy) = ((float, float))pvBtn.Tag;
                        tbPivX.Text = fpx.ToString("F2", CultureInfo.InvariantCulture);
                        tbPivY.Text = fpy.ToString("F2", CultureInfo.InvariantCulture);
                        CommitPivot();
                    };
                    Grid.SetRow(pvBtn, pvRowIdx);
                    Grid.SetColumn(pvBtn, pvColIdx);
                    piv3dPresetGrid.Children.Add(pvBtn);
                }
            }
            piv3dRow.Children.Add(piv3dPresetGrid);

            // 数値入力エリア（AddXYRow と同じレイアウト。CommitPivot を直接フックして CommitTransform を呼ばない）
            var piv3dFieldGrid = BuildXYGrid();
            piv3dFieldGrid.VerticalAlignment = VerticalAlignment.Center;
            piv3dFieldGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            var piv3dRowLbl = new TextBlock
            {
                Text              = "値",
                Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize          = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(piv3dRowLbl, 0); Grid.SetColumn(piv3dRowLbl, 0); piv3dFieldGrid.Children.Add(piv3dRowLbl);
            Grid.SetRow(tbPivX, 0);      Grid.SetColumn(tbPivX, 2);      piv3dFieldGrid.Children.Add(tbPivX);
            Grid.SetRow(tbPivY, 0);      Grid.SetColumn(tbPivY, 4);      piv3dFieldGrid.Children.Add(tbPivY);
            var piv3dLblX = MakeAxisLabel("X", "#E06C75", tbPivX, 0.01);
            var piv3dLblY = MakeAxisLabel("Y", "#98C379", tbPivY, 0.01);
            Grid.SetRow(piv3dLblX, 0);   Grid.SetColumn(piv3dLblX, 1);   piv3dFieldGrid.Children.Add(piv3dLblX);
            Grid.SetRow(piv3dLblY, 0);   Grid.SetColumn(piv3dLblY, 3);   piv3dFieldGrid.Children.Add(piv3dLblY);
            tbPivX.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitPivot(); e.Handled = true; } };
            tbPivY.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitPivot(); e.Handled = true; } };
            tbPivX.LostFocus += (_, _) => CommitPivot();
            tbPivY.LostFocus += (_, _) => CommitPivot();
            NumericDragBehavior.SetOnDrag(tbPivX, CommitPivot); NumericDragBehavior.SetOnDrag(tbPivY, CommitPivot);
            piv3dRow.Children.Add(piv3dFieldGrid);

            // ピボットは「アクター位置がキャンバスのどの点に対応するか」の割合値（0..1）で、
            // キャンバスサイズのような器の定義ではなく調整値なので ⟲ を添える。
            // 2 成分まとめて 1 パス（serde 名 pivot）でリセットする＝ Undo も 1 回で戻る。
            // 値域は Rust 側 `pivot_x.clamp(0.0, 1.0)`（transform_ops.rs）で 0..1 だが、
            // 2 成分行のため単一スカラー用の値域表（スライダー化）の対象外である。
            sp.Children.Add(WithFieldReset(piv3dRow, info.SlotIdx, CanvasComponentType, "pivot", "ピボット"));
        }

        return sp;
    }

    /// <summary>
    /// リニア値 (0-1) を sRGB バイト (0-255) に変換する。
    /// WPF の Color.FromArgb は sRGB バイト値を期待するため、
    /// Rust ランタイムから受け取るリニア値を表示用に変換する際に使用する。
    /// </summary>
    private static byte LinearToSrgbByte(float linear)
    {
        linear = Math.Clamp(linear, 0f, 1f);
        float srgb = linear <= 0.0031308f
            ? 12.92f * linear
            : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)(srgb * 255f + 0.5f), 0, 255);
    }

    /// <summary>ラベル + 数値入力フィールドの行を生成する。</summary>
    /// <param name="format">数値の書式。デフォルトは "F1"（小数第1位）。整数表示には "F0" を指定。</param>
    /// <param name="labelWidth">
    /// 行ラベルの幅（px）。既定は <see cref="InspectorRowLabelWidth"/>。
    /// ラベル幅が標準と異なるセクションでは、そのセクションの幅を呼び出し側が渡す。
    /// </param>
    private static (UIElement element, TextBox textBox) BuildLabeledNumberRow(
        string label, float value, string format = "F1", double labelWidth = InspectorRowLabelWidth)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var initText = value.ToString(format, CultureInfo.InvariantCulture);
        var tb = new TextBox
        {
            Text              = initText,
            Tag               = initText, // フォーカス前の最終有効値を保持する
            Background        = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Foreground        = new SolidColorBrush(Colors.White),
            BorderBrush       = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            BorderThickness   = new Thickness(1),
            FontSize          = 11,
            Padding           = new Thickness(3, 1, 3, 1),
            Margin            = new Thickness(1, 1, 2, 1),
            VerticalAlignment = VerticalAlignment.Center,
            SelectionBrush    = new SolidColorBrush(Color.FromArgb(0x66, 0x33, 0x99, 0xFF)),
        };
        AttachAutoSelectBehavior(tb);
        NumericDragBehavior.SetEnabled(tb, true);
        Grid.SetColumn(tb, 1);
        grid.Children.Add(tb);

        return (grid, tb);
    }

    // ── ScriptComponent inspector ─────────────────────────────

    private UIElement BuildScriptSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // スクリプトパス選択行
        sp.Children.Add(BuildScriptPathRow(info));

        // スクリプトが設定されていなければここで終了
        if (string.IsNullOrEmpty(info.ModelPath)) return sp;

        // スクリプトファイルを開くボタン（内蔵スクリプトエディタ）
        sp.Children.Add(BuildOpenScriptButton(info.ModelPath));

        // スクリプト型の解決（[SerializeField] フィールド表示用）。
        // 未キャッシュのコンパイルは Roslyn のフルコンパイル（Emit + Assembly.Load）で
        // 数百 ms〜1 秒かかる。これを UI スレッドで行うとインスペクタ表示が固まる（＝選択の遅延）。
        // よってキャッシュヒット時のみ即時にフィールドを描画し、未キャッシュ時は
        // プレースホルダを出してバックグラウンドでコンパイルし、完了後に差し込む。
        if (_scriptTypeCache.TryGetValue(info.ModelPath, out var cachedType) && cachedType is not null)
        {
            AppendScriptFields(sp, cachedType, info);
            return sp;
        }

        var loading = new TextBlock
        {
            Text       = "スクリプト情報を読み込み中…",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize   = 11,
            Margin     = new Thickness(2, 4, 0, 2),
        };
        sp.Children.Add(loading);

        var path           = info.ModelPath;
        int actorAtRequest = _currentActorId;
        var infoCopy       = info;
        Task.Run(() =>
            {
                try { return ScriptCompiler.CompileFile(path); }
                catch (Exception ex) { return ((Type?)null, (IReadOnlyList<string>)new[] { ex.Message }); }
            })
            .ContinueWith(t =>
            {
                var (type, errors) = t.Result;
                if (type is not null) _scriptTypeCache[path] = type;
                else EditorLog.Write($"Script compile error [{Path.GetFileName(path)}]: {string.Join("; ", errors)}");

                // コンパイル中に選択が変わっていたら反映しない（この sp は破棄済み）
                if (_currentActorId != actorAtRequest) return;
                sp.Children.Remove(loading);
                if (type is not null) AppendScriptFields(sp, type, infoCopy);
            }, TaskScheduler.FromCurrentSynchronizationContext());

        return sp;
    }

    /// <summary>スクリプトの [SerializeField] フィールドセクションを sp に追加する（UI スレッドで呼ぶ）。</summary>
    private void AppendScriptFields(StackPanel sp, Type scriptType, SlotInfo info)
    {
        var fields = ScriptCompiler.GetSerializeFields(scriptType);
        if (fields.Count == 0) return;

        // 現在値: runtime が ACTOR_COMPONENTS の script_fields で送ってくる
        // （シーンに保存された [SerializeField] 値）。編集は SET_SCRIPT_FIELD で書き戻す。
        var values  = ParseScriptFieldValues(info.ScriptFieldsJson);
        var slotIdx = info.SlotIdx;

        var fieldSection = BuildSection("フィールド");
        var fieldSp = (StackPanel)fieldSection.Child;
        fieldSp.Children.Add(ScriptInspectorBuilder.Build(fields, values, (name, val) =>
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_SCRIPT_FIELD:{_currentActorId},{slotIdx},{name},{val}");
        // 参照フィールドのドロップ解決はエディタ共通の参照ピッカー基盤へ委譲する
        // （InspectorPanel が IReferenceDropResolver を実装している）。
        }, this,
        // [Serializable] ネストの折りたたみ状態を再構築ごしに復元する
        // （SET_SCRIPT_FIELD のたびに ACTOR_COMPONENTS が再送されてこの UI は作り直される）。
        // キーは「スロット添字 + フィールドのドットパス」。
        expandStates: _expandStates,
        expandKeyPrefix: $"{ExpandKeyScriptFieldPrefix}{slotIdx}:"));
        sp.Children.Add(fieldSection);
    }

    /// <summary>
    /// ACTOR_COMPONENTS 応答 JSON のアクター DFS ID（"id"）を取り出す。取得できなければ -1。
    /// 非同期の問い合わせ結果を取り違えないための照合に使う。
    /// </summary>
    private static int TryGetActorComponentsId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id)
                ? id : -1;
        }
        catch { return -1; }
    }

    /// <summary>script_fields JSON（{"name":"value",...}）を辞書に変換する。</summary>
    private static Dictionary<string, string> ParseScriptFieldValues(string json)
    {
        var values = new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
                values[prop.Name] = prop.Value.GetString() ?? "";
        }
        catch { /* 不正な JSON は空扱い */ }
        return values;
    }

    /// <summary>スクリプトを内蔵エディタで開くボタン行を生成する。</summary>
    /// <summary>
    /// 「キャンバスを編集」ボタンを生成する（CanvasComponent インスペクタ用）。
    /// クリックで CanvasEditRequested を発火し、MainWindow がキャンバス編集タブを開く。
    /// 2D スクリーンスペースキャンバス・3D ワールドキャンバスの両方で使用する。
    /// </summary>
    private UIElement BuildOpenCanvasEditButton()
    {
        var btn = new Button
        {
            Content             = "キャンバスを編集",
            Margin              = new Thickness(0, 4, 0, 2),
            Padding             = new Thickness(8, 3, 8, 3),
            FontSize            = 11,
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip             = "キャンバスの中身（スプライト配置）を専用の 2D 編集タブで開きます",
        };
        btn.Click += (_, _) =>
        {
            if (_currentActorId < 0) return;
            CanvasEditRequested?.Invoke(_currentActorId);
        };
        return btn;
    }

    // ── プレハブ参照バー ─────────────────────────────────────
    // Unity 風にプレハブインスタンスと一目で分かる配色（薄い青系）。色はここで定数化する。
    /// <summary>プレハブ参照バーの背景色（薄い青系）。</summary>
    private static readonly SolidColorBrush PrefabBarBgBrush     = MakeFrozenBrush(Color.FromRgb(0x25, 0x38, 0x52));
    /// <summary>プレハブ参照バーの左端アクセント帯・アイコン/文字色（明るい水色）。</summary>
    private static readonly SolidColorBrush PrefabBarAccentBrush = MakeFrozenBrush(Color.FromRgb(0x7F, 0xB2, 0xE5));
    /// <summary>プレハブ参照バーのホバー時背景色（少し明るい青）。</summary>
    private static readonly SolidColorBrush PrefabBarHoverBrush  = MakeFrozenBrush(Color.FromRgb(0x30, 0x47, 0x66));

    private static SolidColorBrush MakeFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>
    /// プレハブ参照バー（アコーディオン最上部に表示）を生成する。
    /// ・薄い青系の帯 + 📦 アイコン + 参照ファイル名（フルパスはツールチップ）。
    /// ・ダブルクリックで参照元 .actor をアクタ編集タブで開く（ActorFileOpenRequested）。
    /// ・右端の「リンク解除」ボタンで確認ダイアログ後 UNLINK_PREFAB を送信する。
    /// </summary>
    /// <param name="source">プレハブ参照元パス（assets:// 仮想パス or 絶対パス）。</param>
    private UIElement BuildPrefabRefBar(string source)
    {
        // 表示ファイル名（拡張子含む）。空なら仮想パス全体をフォールバック表示。
        var fileName = Path.GetFileName(source);
        if (string.IsNullOrEmpty(fileName)) fileName = source;

        // 3 列グリッド: [アイコン] [ファイル名(伸縮)] [リンク解除ボタン]
        var grid = new Grid { Margin = new Thickness(6, 3, 6, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 📦 アイコン
        var icon = new TextBlock
        {
            Text              = "📦",
            FontSize          = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(2, 0, 6, 0),
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        // ファイル名（フルパスはツールチップ）。幅が足りなければ省略表示。
        var nameBlock = new TextBlock
        {
            Text              = fileName,
            Foreground        = PrefabBarAccentBrush,
            FontSize          = 12,
            FontWeight        = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            ToolTip           = $"プレハブ参照元:\n{source}\n\nダブルクリックで参照元アクタを開きます",
        };
        Grid.SetColumn(nameBlock, 1);
        grid.Children.Add(nameBlock);

        // リンク解除ボタン（小さめ・確認ダイアログ付き）
        var unlinkBtn = new Button
        {
            Content             = "リンク解除",
            FontSize            = 10,
            Padding             = new Thickness(6, 1, 6, 1),
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip             = "このアクターとプレハブファイルの参照リンクを解除します",
        };
        unlinkBtn.Click += (_, _) => ConfirmAndUnlinkPrefab();
        Grid.SetColumn(unlinkBtn, 2);
        grid.Children.Add(unlinkBtn);

        // 帯本体（左端アクセント + 薄い青背景）。ダブルクリックで参照元を開く。
        var bar = new Border
        {
            Background       = PrefabBarBgBrush,
            BorderBrush      = PrefabBarAccentBrush,
            BorderThickness  = new Thickness(3, 0, 0, 0), // 左端のみアクセント帯
            CornerRadius     = new CornerRadius(2),
            Margin           = new Thickness(0, 0, 0, 4),
            Cursor           = System.Windows.Input.Cursors.Hand,
            Child            = grid,
        };
        // ホバーで軽くハイライト（プレハブ帯であることを視覚的に強調）
        bar.MouseEnter += (_, _) => bar.Background = PrefabBarHoverBrush;
        bar.MouseLeave += (_, _) => bar.Background = PrefabBarBgBrush;
        // ダブルクリックで参照元 .actor を開く（ボタン上のクリックは Button 側で消費される）
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && !string.IsNullOrEmpty(_currentPrefabSource))
                TryOpenPrefabSourcePath(_currentPrefabSource);
        };
        return bar;
    }

    /// <summary>
    /// Hierarchy 側の「アクタファイルを開く」から、指定 DFS ID のプレハブ参照元を開く。
    /// 対象が現在選択中で参照元が判明済みなら即開き、そうでなければ ACTOR_COMPONENTS を
    /// 要求して取得後に自動で開く（BuildActorComponentList の保留処理で拾う）。
    /// </summary>
    public void OpenPrefabSource(int dfsId)
    {
        if (dfsId == _currentActorId && !string.IsNullOrEmpty(_currentPrefabSource))
        {
            TryOpenPrefabSourcePath(_currentPrefabSource);
            return;
        }
        // まだ対象の prefab_source を持っていない → 取得を要求し、届いたら開く
        _pendingOpenPrefabForDfs = dfsId;
        _runtime?.SendToRuntime($"GET_ACTOR_COMPONENTS:{dfsId}");
    }

    /// <summary>
    /// プレハブ参照元パス（assets:// 仮想パス or 絶対パス）を実ファイルへ解決し、
    /// アクタ編集タブで開くよう要求する。欠損時はメッセージを表示する。
    /// </summary>
    private void TryOpenPrefabSourcePath(string source)
    {
        // assets:// 仮想パス → 絶対パスへ変換（絶対パスならそのまま返る）
        var abs = VirtualPath.ToAbsolute(source, _assetsPath);
        if (!File.Exists(abs))
        {
            MessageBox.Show(
                $"参照元のアクタファイルが見つかりません:\n{source}",
                "プレハブ参照", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ActorFileOpenRequested?.Invoke(abs);
    }

    /// <summary>
    /// 確認ダイアログを表示し、承諾されたら現在アクターのプレハブリンクを解除する（UNLINK_PREFAB 送信）。
    /// </summary>
    private void ConfirmAndUnlinkPrefab()
    {
        if (_currentActorId < 0) return;
        var result = MessageBox.Show(
            "このアクターのプレハブ参照リンクを解除しますか？\n解除後は通常のアクターとして独立し、参照元の変更は反映されなくなります。",
            "プレハブリンク解除", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        _runtime?.SendToRuntime($"UNLINK_PREFAB:{_currentActorId}");
    }

    private UIElement BuildOpenScriptButton(string scriptPath)
    {
        var btn = new Button
        {
            Content             = "スクリプトを編集",
            Margin              = new Thickness(0, 4, 0, 2),
            Padding             = new Thickness(8, 3, 8, 3),
            FontSize            = 11,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        btn.Click += (_, _) =>
        {
            // ScriptComponent の type_name は絶対パスとは限らない
            //（ファイル名のみ・assets:// 仮想パスの形式もある）ため、実ファイルへ解決してから開く
            var resolved = ResolveScriptFilePath(scriptPath);
            if (resolved is null)
            {
                MessageBox.Show(
                    $"スクリプトファイルが見つかりません:\n{scriptPath}\n\nアセットフォルダ内を検索しましたが該当がありませんでした。",
                    "スクリプトを編集", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ScriptFileOpenRequested?.Invoke(resolved);
        };
        return btn;
    }

    /// <summary>
    /// ScriptComponent の type_name（スクリプト参照文字列）を実ファイルの絶対パスへ解決する。
    ///
    /// type_name は以下のいずれの形式でも保存されうるため、順に解決を試みる:
    ///  1. 絶対パス（旧形式。そのファイルが存在すればそのまま使う）
    ///  2. assets:// 仮想パス（アセットルート相対へ変換）
    ///  3. ファイル名のみ / 相対パス（アセットフォルダ配下を再帰検索して同名 .cs を探す。
    ///     デモシーン等の移植可能な参照形式。ScriptAssemblyManager のファイル名解決と同等）
    /// 見つからなければ null。
    /// </summary>
    private string? ResolveScriptFilePath(string scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath)) return null;

        try
        {
            // 1. 絶対パスがそのまま存在する場合（従来形式）
            if (Path.IsPathRooted(scriptPath) && File.Exists(scriptPath)) return scriptPath;

            // 2. assets:// 仮想パス → アセットルート相対へ変換する
            if (VirtualPath.IsVirtual(scriptPath))
            {
                var abs = VirtualPath.ToAbsolute(scriptPath, _assetsPath);
                if (File.Exists(abs)) return abs;
            }

            // 3. ファイル名（または相対パス）としてアセットフォルダ配下を再帰検索する
            if (!string.IsNullOrEmpty(_assetsPath) && Directory.Exists(_assetsPath))
            {
                var fileName = Path.GetFileName(scriptPath);
                if (fileName.Length > 0)
                {
                    var hit = Directory.EnumerateFiles(_assetsPath, fileName, SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (hit is not null) return hit;
                }
            }
        }
        catch { /* アクセス不可ディレクトリ等は未解決として扱う */ }
        return null;
    }

    private UIElement BuildScriptPathRow(SlotInfo info) =>
        FileRefBuilder.Build(
            "スクリプト", info.ModelPath,
            [".cs"],
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "スクリプトファイルを選択",
                    Filter = "C# スクリプト|*.cs|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path => SetScriptPath(info.SlotIdx, path));

    private void SetScriptPath(int slotIdx, string path)
    {
        if (_currentActorId < 0) return;
        // 既存のキャッシュを無効化
        var old = _slotInfos.FirstOrDefault(s => s.SlotIdx == slotIdx)?.ModelPath;
        if (old is not null) _scriptTypeCache.Remove(old);

        // クラス属性（RequireComponent / DisallowMultipleComponent）を解決する。
        // 他スクリプトを typeof 参照する場合に備え、まず全体コンパイルで解決し、
        // 失敗時は単一ファイルコンパイルにフォールバックする。
        var type = ScriptCompiler.ResolveScriptTypeInProject(path, _assetsPath)
                   ?? GetOrCompileScript(path);

        // [DisallowMultipleComponent]: 同じスクリプトが別スロットに既にあれば中止する
        if (type is not null && ScriptCompiler.HasDisallowMultiple(type) &&
            _slotInfos.Any(s => s.SlotIdx != slotIdx && s.TypeId == "ScriptComponent" && SamePath(s.ModelPath, path)))
        {
            MessageBox.Show(
                $"{Path.GetFileNameWithoutExtension(path)} は 1 アクターにつき 1 つのみ追加できます。",
                "追加不可", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _runtime?.SendToRuntime($"SET_MODEL_PATH:{_currentActorId},{slotIdx},{path}");

        // [RequireComponent]: 不足している要求コンポーネントを自動追加する
        if (type is not null) EnforceRequireComponents(type, path);
    }

    /// <summary>
    /// スクリプトの [RequireComponent] 要求を満たすよう、不足コンポーネントを自動追加する。
    /// ネイティブコンポーネントは名前で、他スクリプトは型名から .cs を探してアタッチする。
    /// </summary>
    private void EnforceRequireComponents(Type scriptType, string scriptPath)
    {
        if (_runtime is null || _currentActorId < 0) return;
        var root = Directory.Exists(_assetsPath)
            ? _assetsPath
            : (Path.GetDirectoryName(scriptPath) ?? "");

        foreach (var req in ScriptCompiler.GetRequiredComponents(scriptType))
        {
            if (req.ComponentName is not null)
            {
                // ネイティブコンポーネント: 未アタッチなら追加する
                if (_slotInfos.Any(s => s.TypeId == req.ComponentName)) continue;
                _runtime.SendToRuntime(
                    $"ADD_COMPONENT:{_currentActorId},{req.ComponentName},{req.ComponentName},");
            }
            else if (req.ScriptTypeName is not null)
            {
                // 他スクリプト: 型名から .cs を探し、未アタッチならパス付きで追加する
                var file = ScriptCompiler.FindScriptFile(req.ScriptTypeName, root);
                if (file is null)
                {
                    EditorLog.Write($"[RequireComponent] スクリプト '{req.ScriptTypeName}' の .cs が見つかりません");
                    continue;
                }
                if (_slotInfos.Any(s => s.TypeId == "ScriptComponent" && SamePath(s.ModelPath, file))) continue;
                var name = Path.GetFileNameWithoutExtension(file);
                _runtime.SendToRuntime($"ADD_COMPONENT:{_currentActorId},ScriptComponent,{name},{file}");
            }
        }
    }

    /// <summary>2 つのパスを正規化して同一か判定する（大文字小文字・区切りを無視）。</summary>
    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private Type? GetOrCompileScript(string path)
    {
        if (_scriptTypeCache.TryGetValue(path, out var cached)) return cached;

        var (type, errors) = ScriptCompiler.CompileFile(path);
        if (type is null)
        {
            EditorLog.Write($"Script compile error [{Path.GetFileName(path)}]: {string.Join("; ", errors)}");
            return null;
        }
        _scriptTypeCache[path] = type;
        return type;
    }

    private UIElement BuildModelPathRow(SlotInfo info) =>
        FileRefBuilder.Build(
            "モデル", info.ModelPath,
            [".glb", ".gltf", ".obj"],
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "モデルファイルを選択",
                    Filter = "3D モデル|*.glb;*.gltf;*.obj|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path => SetModelPath(info.SlotIdx, path));

    private void SetModelPath(int slotIdx, string path)
    {
        if (_currentActorId < 0) return;
        _runtime?.SendToRuntime($"SET_MODEL_PATH:{_currentActorId},{slotIdx},{path}");
    }

    /// <summary>
    /// アコーディオンヘッダーの選択ハイライト（枠線）を、現在の _selectedSlotIdx に合わせて全ヘッダーへ反映する。
    /// 旧コンポーネント一覧チップの RefreshChipSelection 相当。
    /// </summary>
    private void RefreshAccordionSelection()
    {
        var selectedBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF));
        var defaultBrush  = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
        foreach (var (slotIdx, refs) in _accordionHeaders)
        {
            var sel = slotIdx == _selectedSlotIdx;
            refs.Header.BorderBrush     = sel ? selectedBrush : defaultBrush;
            refs.Header.BorderThickness = sel ? new Thickness(2) : new Thickness(0, 0, 0, 1);
        }
    }

    /// <summary>
    /// アコーディオンヘッダーの右クリックメニュー（リネーム・複製・削除・コピー/貼り付け）を表示する。
    /// 旧コンポーネント一覧チップの ShowComponentChipContextMenu 相当。
    /// </summary>
    private void ShowComponentContextMenu(UIElement target, int slotIdx, string currentName)
    {
        var menu = new ContextMenu
        {
            Background  = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
        };

        MenuItem MakeItem(string header, SolidColorBrush fg, RoutedEventHandler handler)
        {
            var item = new MenuItem
            {
                Header     = header,
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                Foreground = fg,
                FontSize   = 11,
            };
            item.Click += handler;
            return item;
        }

        menu.Items.Add(MakeItem("リネーム", new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            (_, _) => StartComponentRename(slotIdx, currentName)));
        menu.Items.Add(MakeItem("複製", new SolidColorBrush(Color.FromRgb(0xAA, 0xDD, 0xAA)),
            (_, _) => DuplicateComponentSlot(slotIdx, currentName)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("コピー", new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            (_, _) => _copiedSlot = _slotInfos.FirstOrDefault(s => s.SlotIdx == slotIdx)));
        var pasteItem = MakeItem("貼り付け", new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            (_, _) => PasteCopiedComponentSlot());
        pasteItem.IsEnabled = _copiedSlot != null;
        menu.Items.Add(pasteItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("削除", new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)),
            (_, _) =>
            {
                if (_currentActorId >= 0)
                    _runtime?.SendToRuntime($"REMOVE_COMPONENT:{_currentActorId},{slotIdx}");
            }));

        menu.PlacementTarget = target;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    /// <summary>
    /// 指定スロットを複製する（CanvasComponent は 1 アクターにつき 1 つのみのガード付き）。
    /// 複製直後は BuildActorComponentList 側で新スロットを検出し、自動でリネームモードへ入る。
    /// </summary>
    private void DuplicateComponentSlot(int slotIdx, string currentName)
    {
        if (_currentActorId < 0) return;
        // CanvasComponent は 1 アクターにつき 1 つのみ許可する
        var srcInfo = _slotInfos.FirstOrDefault(s => s.SlotIdx == slotIdx);
        if (srcInfo?.TypeId == "CanvasComponent" &&
            _slotInfos.Any(s => s.TypeId == "CanvasComponent"))
        {
            MessageBox.Show(
                "CanvasComponent は 1 アクターにつき 1 つのみ追加できます。",
                "追加不可", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _pendingDuplicateRename   = true;
        _pendingDuplicateBaseName = currentName;
        _runtime?.SendToRuntime($"DUPLICATE_COMPONENT:{_currentActorId},{slotIdx}");
    }

    /// <summary>
    /// _copiedSlot（Ctrl+C またはヘッダーメニューの「コピー」でコピーされたスロット）を複製として貼り付ける。
    /// Ctrl+V とヘッダーメニュー「貼り付け」の共通処理。
    /// </summary>
    private void PasteCopiedComponentSlot()
    {
        if (_copiedSlot is null || _currentActorId < 0) return;
        DuplicateComponentSlot(_copiedSlot.SlotIdx, _copiedSlot.Name);
    }

    /// <param name="currentName">Runtime 上の現在の名前（変更判定の基準値）。</param>
    /// <param name="initialText">TextBox に初期表示するテキスト。null のとき currentName を使う。</param>
    private void StartComponentRename(int slotIdx, string currentName, string? initialText = null)
    {
        // 該当スロットのアコーディオンヘッダーを特定し、タイトル部分（headerGrid の列3）を TextBox に差し替える
        if (!_accordionHeaders.TryGetValue(slotIdx, out var refs)) return;

        var committed = false;
        var tb = new TextBox
        {
            // initialText が指定されていれば TextBox にはそちらを表示する。
            // 変更判定（RENAME_COMPONENT を送るかどうか）は currentName との比較で行う。
            Text              = initialText ?? currentName,
            Background        = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
            Foreground        = Brushes.White,
            CaretBrush        = Brushes.White,
            BorderBrush       = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)),
            BorderThickness   = new Thickness(1),
            Padding           = new Thickness(4, 2, 4, 2),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(tb, 3);

        void Commit()
        {
            if (committed) return;
            committed = true;
            var newName = tb.Text.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != currentName && _currentActorId >= 0)
            {
                // Rust パーサーは "RENAME_COMPONENT:" プレフィックスで処理する。
                // "_SLOT" を付けると先頭一致で誤マッチするが数値パースが失敗しコマンドが破棄される。
                // 成功後はランタイムから ACTOR_COMPONENTS が再送されアコーディオン全体が再構築されるため、
                // ここでヘッダーを手動で元に戻す必要はない。
                _runtime?.SendToRuntime($"RENAME_COMPONENT:{_currentActorId},{slotIdx},{newName}");
            }
            else if (_currentActorId >= 0)
            {
                // 名前変更なし（キャンセルと同等）: GET で UI をリフレッシュしてヘッダーを復元する。
                _runtime?.SendToRuntime($"GET_ACTOR_COMPONENTS:{_currentActorId}");
            }
        }

        tb.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape)
            {
                committed = true;
                e.Handled = true;
                if (_currentActorId >= 0) _runtime?.SendToRuntime($"GET_ACTOR_COMPONENTS:{_currentActorId}");
            }
        };
        tb.LostFocus += (_, _) => Commit();

        refs.HeaderGrid.Children.Remove(refs.TitleBlock);
        refs.HeaderGrid.Children.Add(tb);
        Dispatcher.BeginInvoke(() => { tb.Focus(); tb.SelectAll(); },
            System.Windows.Threading.DispatcherPriority.Input);
    }

    // ── Add Component (actor edit mode) ─────────────────────

    private void OnAddComponentClicked(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || _currentActorId < 0) return;
        // CanvasComponent が既に存在する場合は追加できないコンポーネント種別として渡す
        var disabledTypes = new HashSet<string>();
        if (_slotInfos.Any(s => s.TypeId == "CanvasComponent"))
            disabledTypes.Add("CanvasComponent");
        var win = new ComponentSelectorWindow(_runtime, _currentActorId, _isActor2D, disabledTypes, _pluginNames)
        {
            Owner = Window.GetWindow(this),
        };
        win.ShowDialog();
    }

    // ── Component copy/paste (Ctrl+C / Ctrl+V) ───────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_isActorEditMode || _currentActorId < 0) return;

        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (_selectedSlotIdx >= 0)
            {
                _copiedSlot = _slotInfos.FirstOrDefault(s => s.SlotIdx == _selectedSlotIdx);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (_copiedSlot != null)
            {
                PasteCopiedComponentSlot();
                e.Handled = true;
            }
        }
    }

    // ── JSON helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// JSON 要素を bool として読む。
    /// Rust の serde_json は bool を JSON 真偽値（true/false）として出力するため
    /// GetInt32() ではなく ValueKind で判定する。数値（0 以外 = true）も許容する。
    /// </summary>
    private static bool ReadJsonBool(System.Text.Json.JsonElement elem, bool fallback) =>
        elem.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True   => true,
            System.Text.Json.JsonValueKind.False  => false,
            System.Text.Json.JsonValueKind.Number => elem.GetInt32() != 0,
            _                                     => fallback,
        };

    // ── 物理コンポーネント用ヘルパー ──────────────────────────────

    /// <summary>ラベル + 整数入力フィールドの行を生成する。</summary>
    private static (UIElement element, TextBox textBox) BuildLabeledIntRow(
        string label, int value,
        int min = int.MinValue, int max = int.MaxValue)
    {
        var tb = new TextBox
        {
            Text              = value.ToString(CultureInfo.InvariantCulture),
            Background        = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush       = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness   = new Thickness(1),
            FontSize          = 11,
            Padding           = new Thickness(4, 1, 4, 1),
            Width             = 80,
            VerticalAlignment = VerticalAlignment.Center,
        };
        NumericDragBehavior.Attach(tb, sensitivity: 1.0, isInteger: true, min: min, max: max);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text      = label, FontSize = 11, Width = 90,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(tb);
        return (row, tb);
    }

    /// <summary>ラベル + X/Y/Z テキストボックス行を生成する（シンプル版）。</summary>
    private static (UIElement element, TextBox tx, TextBox ty, TextBox tz) BuildXYZRowSimple(
        string label, float vx, float vy, float vz)
    {
        TextBox MakeTb(float val)
        {
            var t = new TextBox
            {
                Text              = val.ToString("F3", CultureInfo.InvariantCulture),
                Background        = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                BorderBrush       = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness   = new Thickness(1),
                FontSize          = 11,
                Padding           = new Thickness(4, 1, 4, 1),
                Width             = 58,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(2, 0, 0, 0),
            };
            NumericDragBehavior.SetEnabled(t, true);
            return t;
        }
        var tx = MakeTb(vx); var ty = MakeTb(vy); var tz = MakeTb(vz);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11, Width = 90,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(tx); row.Children.Add(ty); row.Children.Add(tz);
        return (row, tx, ty, tz);
    }

    /// <summary>ラベル + チェックボックス行を生成する。</summary>
    private static UIElement BuildCheckRow(string label, bool value, Action<bool> onChange)
    {
        var check = new CheckBox
        {
            IsChecked = value,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        check.Checked   += (_, _) => onChange(true);
        check.Unchecked += (_, _) => onChange(false);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11, Width = 90,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(check);
        return row;
    }

    /// <summary>ラベル + X/Y/Z チェックボックス行（フリーズ用）を生成する。</summary>
    private static UIElement BuildFreezeRow(string label, bool[] values, Action onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11, Width = 90,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        string[] axes = { "X", "Y", "Z" };
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var lbl = new TextBlock
            {
                Text = axes[idx], FontSize = 10, Width = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };
            var check = new CheckBox
            {
                IsChecked = values[idx],
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            };
            check.Checked   += (_, _) => { values[idx] = true;  onChange(); };
            check.Unchecked += (_, _) => { values[idx] = false; onChange(); };
            row.Children.Add(lbl);
            row.Children.Add(check);
        }
        return row;
    }

    /// <summary>TextBox の浮動小数点パース（InvariantCulture）。</summary>
    private static bool TryParseF(TextBox tb, out float value) =>
        float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>JsonElement から bool 配列を読み込む（要素数チェック付き）。</summary>
    private static void ReadBoolArray(System.Text.Json.JsonElement root, string key, bool[] dest)
    {
        if (!root.TryGetProperty(key, out var arr)) return;
        for (int i = 0; i < Math.Min(dest.Length, arr.GetArrayLength()); i++)
            dest[i] = arr[i].ValueKind == System.Text.Json.JsonValueKind.True;
    }

    /// <summary>JsonElement から float 配列を読み込む（要素数チェック付き）。</summary>
    private static void ReadFloatArray(System.Text.Json.JsonElement root, string key, float[] dest)
    {
        if (!root.TryGetProperty(key, out var arr)) return;
        for (int i = 0; i < Math.Min(dest.Length, arr.GetArrayLength()); i++)
            dest[i] = arr[i].GetSingle();
    }

    // ── Section / grid helpers ────────────────────────────────

    private static Border BuildSection(string title)
    {
        var sp = new StackPanel();
        var header = new TextBlock
        {
            Text       = title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize   = 11,
            FontWeight = FontWeights.Bold,
            Margin     = new Thickness(0, 0, 0, 6),
        };
        sp.Children.Add(header);

        return new Border
        {
            Background   = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            CornerRadius = new CornerRadius(3),
            Padding      = new Thickness(8, 6, 8, 6),
            Margin       = new Thickness(4, 0, 4, 4),
            Child        = sp,
        };
    }

    private static Grid BuildXYZGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    /// <summary>
    /// CanvasTransform（2D）用の Transform セクションを生成する。
    /// 位置(X,Y)・回転(単一値)・スケール(X,Y)・ピボット(X,Y)・アンカー(3×3プリセット + X,Y) の構成。
    /// _tbPx/_tbPy = 位置, _tbEz = 回転, _tbSx/_tbSy = スケール, _tbPivotX/_tbPivotY = ピボット,
    /// _tbAnchorX/_tbAnchorY = アンカー。
    /// </summary>
    private Border BuildCanvas2DTransformSection(
        float px, float py, float rot, float sx, float sy,
        float pivx, float pivy, float ancX, float ancY,
        // スケールモード（親キャンバスのスケール追従設定。CanvasComponent から移動）
        bool scaleTransform = false, bool scaleSize = false,
        bool keepAspect = false, int aspectAxis = 0,
        // true = ビューポート所属ルートキャンバス: Transform 恒等固定のため読み取り専用表示にする
        bool locked = false)
    {
        var section = BuildSection("CanvasTransform");
        var sp      = (StackPanel)section.Child;
        var grid    = BuildXYGrid();

        // 位置行: X, Y
        (_tbPx, _tbPy) = AddXYRow(grid, 0, "位置", px, py, "#E06C75", "#98C379", 0.1);
        // 回転行: Z（2D では Z 軸周り単一値）
        _tbEz = AddSingleValueRow(grid, 1, "回転", "Z", rot, "#61AFEF", 1.0);
        // スケール行: X, Y
        (_tbSx, _tbSy) = AddXYRow(grid, 2, "スケール", sx, sy, "#E06C75", "#98C379", 0.01);

        sp.Children.Add(grid);

        // ── ピボットセクション ────────────────────────────────────────────
        // ラベル
        var pivotLabel = new TextBlock
        {
            Text       = "ピボット",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize   = 11,
            Margin     = new Thickness(0, 6, 0, 2),
        };
        sp.Children.Add(pivotLabel);

        // 3×3 プリセットグリッドと数値入力を横並び
        var pivotRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

        // 3×3 プリセットボタングリッド（各セルは pivot (col/2, row/2) に対応）
        var pivotPresetGrid = new Grid { Width = 60, Height = 60, Margin = new Thickness(0, 0, 8, 0) };
        for (int i = 0; i < 3; i++)
        {
            pivotPresetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pivotPresetGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }
        float[] pvPresetVals = { 0f, 0.5f, 1f };
        for (int pvRow = 0; pvRow < 3; pvRow++)
        {
            for (int pvCol = 0; pvCol < 3; pvCol++)
            {
                float pv = pvPresetVals[pvCol];
                float qv = pvPresetVals[pvRow];
                // 現在選択中のプリセットを強調表示する
                bool pvActive = (Math.Abs(pivx - pv) < 0.01f && Math.Abs(pivy - qv) < 0.01f);
                var pvBtn = new Button
                {
                    Width           = 16,
                    Height          = 16,
                    Margin          = new Thickness(1),
                    Background      = pvActive
                        ? new SolidColorBrush(Color.FromRgb(0x61, 0xAF, 0xEF))
                        : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                    BorderBrush     = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                    BorderThickness = new Thickness(1),
                    Padding         = new Thickness(0),
                    Tag             = (pv, qv),
                };
                // プリセットボタンクリック時: 数値フィールドを更新して SET_CANVAS_TRANSFORM を送信
                pvBtn.Click += (_, _) =>
                {
                    var (fpx, fpy) = ((float, float))pvBtn.Tag;
                    if (_tbPivotX is not null) _tbPivotX.Text = fpx.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    if (_tbPivotY is not null) _tbPivotY.Text = fpy.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    CommitTransform();
                };
                Grid.SetRow(pvBtn, pvRow); Grid.SetColumn(pvBtn, pvCol);
                pivotPresetGrid.Children.Add(pvBtn);
            }
        }
        pivotRow.Children.Add(pivotPresetGrid);

        // 数値入力エリア（X, Y フィールド）
        var pivotFieldGrid = BuildXYGrid();
        pivotFieldGrid.VerticalAlignment = VerticalAlignment.Center;
        // AddXYRow が OnFieldLostFocus（→ CommitTransform）を自動登録する
        (_tbPivotX, _tbPivotY) = AddXYRow(pivotFieldGrid, 0, "値", pivx, pivy, "#E06C75", "#98C379", 0.01);
        pivotRow.Children.Add(pivotFieldGrid);

        sp.Children.Add(pivotRow);

        // ── アンカーセクション ────────────────────────────────────────────
        // ラベル
        var anchorLabel = new TextBlock
        {
            Text       = "アンカー",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize   = 11,
            Margin     = new Thickness(0, 6, 0, 2),
        };
        sp.Children.Add(anchorLabel);

        // 3×3 プリセットグリッドと数値入力を横並び
        var anchorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

        // 3×3 プリセットボタングリッド
        // 各セルは anchor (col/2, row/2) に対応する
        var presetGrid = new Grid { Width = 60, Height = 60, Margin = new Thickness(0, 0, 8, 0) };
        for (int i = 0; i < 3; i++)
        {
            presetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            presetGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }
        float[] presetVals = { 0f, 0.5f, 1f };
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                float av = presetVals[col];
                float bv = presetVals[row];
                // 現在選択中のプリセットを強調表示する
                bool isActive = (Math.Abs(ancX - av) < 0.01f && Math.Abs(ancY - bv) < 0.01f);
                var btn = new Button
                {
                    Width             = 16,
                    Height            = 16,
                    Margin            = new Thickness(1),
                    Background        = isActive
                        ? new SolidColorBrush(Color.FromRgb(0x61, 0xAF, 0xEF))
                        : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                    BorderBrush       = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                    BorderThickness   = new Thickness(1),
                    Padding           = new Thickness(0),
                    Tag               = (av, bv),
                };
                // プリセットボタンクリック時: 数値フィールドを更新してコマンド送信
                btn.Click += (_, _) =>
                {
                    var (fax, fay) = ((float, float))btn.Tag;
                    if (_tbAnchorX is not null) _tbAnchorX.Text = fax.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    if (_tbAnchorY is not null) _tbAnchorY.Text = fay.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    SendCanvasAnchor();
                };
                Grid.SetRow(btn, row); Grid.SetColumn(btn, col);
                presetGrid.Children.Add(btn);
            }
        }
        anchorRow.Children.Add(presetGrid);

        // 数値入力エリア（X, Y フィールド）
        var anchorFieldGrid = BuildXYGrid();
        anchorFieldGrid.VerticalAlignment = VerticalAlignment.Center;
        (_tbAnchorX, _tbAnchorY) = AddXYRow(anchorFieldGrid, 0, "値", ancX, ancY, "#E06C75", "#98C379", 0.01);
        _tbAnchorX.LostFocus += (_, _) => SendCanvasAnchor();
        _tbAnchorY.LostFocus += (_, _) => SendCanvasAnchor();
        _tbAnchorX.KeyDown   += (s, e) => { if (e.Key == System.Windows.Input.Key.Return) SendCanvasAnchor(); };
        _tbAnchorY.KeyDown   += (s, e) => { if (e.Key == System.Windows.Input.Key.Return) SendCanvasAnchor(); };
        NumericDragBehavior.SetOnDrag(_tbAnchorX, SendCanvasAnchor); NumericDragBehavior.SetOnDrag(_tbAnchorY, SendCanvasAnchor);
        anchorRow.Children.Add(anchorFieldGrid);

        sp.Children.Add(anchorRow);

        // ── スケールモード セクション ──────────────────────────
        // 親キャンバスのスケールにこのアイテムの位置/サイズを追従させるかの設定。
        // 以前は CanvasComponent（キャンバス全体）にあったが、各 2D アクターの
        // CanvasTransform（オブジェクト単位）へ移動した。
        var scaleModePanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        scaleModePanel.Children.Add(new TextBlock
        {
            Text       = "スケールモード",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize   = 10,
            Margin     = new Thickness(0, 6, 0, 2),
        });

        // チェックボックス: アイテムのトランスフォームをスケールする（位置が親スケールに追従）
        var cbTransform = new CheckBox
        {
            Content           = "アイテムのトランスフォームをスケールする",
            IsChecked         = scaleTransform,
            Foreground        = new SolidColorBrush(Colors.White),
            FontSize          = 11,
            Margin            = new Thickness(0, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        scaleModePanel.Children.Add(cbTransform);

        // チェックボックス: アイテムのサイズをスケールする
        var cbSize = new CheckBox
        {
            Content           = "アイテムのサイズをスケールする",
            IsChecked         = scaleSize,
            Foreground        = new SolidColorBrush(Colors.White),
            FontSize          = 11,
            Margin            = new Thickness(0, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        scaleModePanel.Children.Add(cbSize);

        // アスペクト比維持パネル（アイテムのサイズをスケールする のときのみ表示）
        var aspectPanel = new StackPanel
        {
            Margin     = new Thickness(16, 0, 0, 6),
            Visibility = scaleSize ? Visibility.Visible : Visibility.Collapsed,
        };

        // チェックボックス: アイテムのアスペクト比を維持
        var cbKeepAspect = new CheckBox
        {
            Content           = "アイテムのアスペクト比を維持",
            IsChecked         = keepAspect,
            Foreground        = new SolidColorBrush(Colors.White),
            FontSize          = 11,
            Margin            = new Thickness(0, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        aspectPanel.Children.Add(cbKeepAspect);

        // 基準軸選択パネル（アスペクト比維持がオンのときのみ表示）
        var axisPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(16, 0, 0, 2),
            Visibility  = keepAspect ? Visibility.Visible : Visibility.Collapsed,
        };
        axisPanel.Children.Add(new TextBlock
        {
            Text              = "基準軸",
            Foreground        = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize          = 11,
            Width             = 60,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var cmbAspectAxis = new ComboBox
        {
            Width    = 100,
            FontSize = 11,
            Margin   = new Thickness(4, 0, 0, 0),
        };
        // Tag は IPC の軸整数（0=Width, 1=Height）に対応する
        cmbAspectAxis.Items.Add(new ComboBoxItem { Content = "横（幅）基準",   Tag = 0 });
        cmbAspectAxis.Items.Add(new ComboBoxItem { Content = "縦（高さ）基準", Tag = 1 });
        cmbAspectAxis.SelectedIndex = aspectAxis == 1 ? 1 : 0;
        axisPanel.Children.Add(cmbAspectAxis);
        aspectPanel.Children.Add(axisPanel);
        scaleModePanel.Children.Add(aspectPanel);

        sp.Children.Add(scaleModePanel);

        // スケールモードを送信するローカル関数。
        // CanvasTransform はアクタールート（DFS ID）で特定するため、スロット番号は使わない。
        // 4 値（scale_transform / scale_size / keep_aspect / axis）を常にまとめて送る。
        void CommitCanvasTransformScaleMode()
        {
            if (_currentActorId < 0) return;
            int st   = cbTransform.IsChecked  == true ? 1 : 0;
            int ss   = cbSize.IsChecked       == true ? 1 : 0;
            int keep = cbKeepAspect.IsChecked == true ? 1 : 0;
            int axis = (cmbAspectAxis.SelectedItem as ComboBoxItem)?.Tag as int? ?? 0;
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"SET_CANVAS_TRANSFORM_SCALE_MODE:{_currentActorId},{st},{ss},{keep},{axis}"));
        }

        cbTransform.Checked   += (_, _) => CommitCanvasTransformScaleMode();
        cbTransform.Unchecked += (_, _) => CommitCanvasTransformScaleMode();
        cbSize.Checked   += (_, _) => { aspectPanel.Visibility = Visibility.Visible;   CommitCanvasTransformScaleMode(); };
        cbSize.Unchecked += (_, _) => { aspectPanel.Visibility = Visibility.Collapsed; CommitCanvasTransformScaleMode(); };
        cbKeepAspect.Checked   += (_, _) => { axisPanel.Visibility = Visibility.Visible;   CommitCanvasTransformScaleMode(); };
        cbKeepAspect.Unchecked += (_, _) => { axisPanel.Visibility = Visibility.Collapsed; CommitCanvasTransformScaleMode(); };
        cmbAspectAxis.SelectionChanged += (_, _) => CommitCanvasTransformScaleMode();

        // ── ルートキャンバスの Transform 固定（Phase B）────────────────────────
        // ビューポート所属のルートキャンバスは position/rotation/pivot/anchor = 0, scale = 1 に
        // 恒等固定されるため、全編集フィールドを不活性化し注記を表示する
        //（保存データは書き換えられず、レイアウト計算側で恒等として扱われる）。
        if (locked)
        {
            // 不活性表示の減光率（編集不可であることを視覚的に示す）
            const double LockedOpacity = 0.45;
            sp.Children.Insert(0, new TextBlock
            {
                Text       = "ルートキャンバスは固定（0/1）",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize   = 10,
                Margin     = new Thickness(0, 2, 0, 4),
                ToolTip    = "ビューポート所属のルートキャンバスは位置/回転/ピボット/アンカー = 0、\n"
                           + "スケール = 1 に固定されます（保存データは温存されます）。",
            });
            foreach (var target in new FrameworkElement[] { grid, pivotLabel, pivotRow, anchorLabel, anchorRow, scaleModePanel })
            {
                target.IsEnabled = false;
                target.Opacity   = LockedOpacity;
            }
        }

        return section;
    }

    /// <summary>
    /// アンカー値をランタイムに送信する。
    /// _tbAnchorX/_tbAnchorY の現在値を [0,1] にクランプして SET_CANVAS_ANCHOR を送る。
    /// </summary>
    private void SendCanvasAnchor()
    {
        if (_tbAnchorX is null || _tbAnchorY is null) return;
        if (!TryParse(_tbAnchorX, out float ax) || !TryParse(_tbAnchorY, out float ay)) return;
        ax = Math.Clamp(ax, 0f, 1f);
        ay = Math.Clamp(ay, 0f, 1f);
        _runtime?.SendToRuntime(FormattableString.Invariant(
            $"SET_CANVAS_ANCHOR:{_currentActorId},{ax},{ay}"));
    }

    /// <summary>XY 2列グリッド（ラベル + X軸 + Y軸）を生成する。</summary>
    private static Grid BuildXYGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    /// <summary>XY グリッドに X, Y 2フィールド行を追加する。</summary>
    private (TextBox x, TextBox y) AddXYRow(
        Grid grid, int row, string label,
        float vx, float vy,
        string colorX, string colorY,
        double dragSpeed)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);

        var tbX = MakeAxisField(vx, colorX); Grid.SetRow(tbX, row); Grid.SetColumn(tbX, 2); grid.Children.Add(tbX);
        var tbY = MakeAxisField(vy, colorY); Grid.SetRow(tbY, row); Grid.SetColumn(tbY, 4); grid.Children.Add(tbY);

        var lblX = MakeAxisLabel("X", colorX, tbX, dragSpeed); Grid.SetRow(lblX, row); Grid.SetColumn(lblX, 1); grid.Children.Add(lblX);
        var lblY = MakeAxisLabel("Y", colorY, tbY, dragSpeed); Grid.SetRow(lblY, row); Grid.SetColumn(lblY, 3); grid.Children.Add(lblY);

        tbX.KeyDown   += OnFieldKeyDown; tbY.KeyDown   += OnFieldKeyDown;
        tbX.LostFocus += OnFieldLostFocus; tbY.LostFocus += OnFieldLostFocus;
        NumericDragBehavior.SetOnDrag(tbX, CommitTransform); NumericDragBehavior.SetOnDrag(tbY, CommitTransform);

        return (tbX, tbY);
    }

    /// <summary>XY グリッドに単一値行（回転などに使用）を追加する。Y列はフィールドが全幅を占める。</summary>
    private TextBox AddSingleValueRow(
        Grid grid, int row, string label, string axisLabel,
        float value, string color, double dragSpeed)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);

        var tb = MakeAxisField(value, color);
        Grid.SetRow(tb, row); Grid.SetColumn(tb, 2);
        Grid.SetColumnSpan(tb, 3); // Y 列も含めて全幅表示
        grid.Children.Add(tb);

        var lblAxis = MakeAxisLabel(axisLabel, color, tb, dragSpeed);
        Grid.SetRow(lblAxis, row); Grid.SetColumn(lblAxis, 1);
        grid.Children.Add(lblAxis);

        tb.KeyDown   += OnFieldKeyDown;
        tb.LostFocus += OnFieldLostFocus;
        NumericDragBehavior.SetOnDrag(tb, CommitTransform);

        return tb;
    }

    private (TextBox x, TextBox y, TextBox z) AddXYZRow(
        Grid grid, int row, string label,
        float vx, float vy, float vz,
        string colorX, string colorY, string colorZ,
        double dragSpeed)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);

        var tbX = MakeAxisField(vx, colorX); Grid.SetRow(tbX, row); Grid.SetColumn(tbX, 2); grid.Children.Add(tbX);
        var tbY = MakeAxisField(vy, colorY); Grid.SetRow(tbY, row); Grid.SetColumn(tbY, 4); grid.Children.Add(tbY);
        var tbZ = MakeAxisField(vz, colorZ); Grid.SetRow(tbZ, row); Grid.SetColumn(tbZ, 6); grid.Children.Add(tbZ);

        var lblX = MakeAxisLabel("X", colorX, tbX, dragSpeed); Grid.SetRow(lblX, row); Grid.SetColumn(lblX, 1); grid.Children.Add(lblX);
        var lblY = MakeAxisLabel("Y", colorY, tbY, dragSpeed); Grid.SetRow(lblY, row); Grid.SetColumn(lblY, 3); grid.Children.Add(lblY);
        var lblZ = MakeAxisLabel("Z", colorZ, tbZ, dragSpeed); Grid.SetRow(lblZ, row); Grid.SetColumn(lblZ, 5); grid.Children.Add(lblZ);

        tbX.KeyDown  += OnFieldKeyDown; tbY.KeyDown  += OnFieldKeyDown; tbZ.KeyDown  += OnFieldKeyDown;
        tbX.LostFocus += OnFieldLostFocus; tbY.LostFocus += OnFieldLostFocus; tbZ.LostFocus += OnFieldLostFocus;
        NumericDragBehavior.SetOnDrag(tbX, CommitTransform); NumericDragBehavior.SetOnDrag(tbY, CommitTransform); NumericDragBehavior.SetOnDrag(tbZ, CommitTransform);

        return (tbX, tbY, tbZ);
    }

    private TextBlock MakeAxisLabel(string text, string colorHex, TextBox target, double dragSpeed)
    {
        var label = new TextBlock
        {
            Text                = text,
            Foreground          = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)),
            FontSize            = 10,
            FontWeight          = FontWeights.Bold,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor              = Cursors.SizeWE,
        };

        double dragOriginX     = 0;
        float  dragOriginValue = 0f;

        label.MouseLeftButtonDown += (_, e) =>
        {
            if (float.TryParse(target.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                dragOriginX     = e.GetPosition(null).X;
                dragOriginValue = v;
                label.CaptureMouse();
                BeginTransformDrag();
            }
            e.Handled = true;
        };
        label.MouseMove += (_, e) =>
        {
            if (!label.IsMouseCaptured) return;
            var dx    = e.GetPosition(null).X - dragOriginX;
            var speed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? dragSpeed * 0.1 : dragSpeed;
            target.Text = Fmt(dragOriginValue + (float)(dx * speed));
            CommitTransform();
        };
        label.MouseLeftButtonUp += (_, e) =>
        {
            if (label.IsMouseCaptured)
            {
                label.ReleaseMouseCapture();
                EndTransformDrag();
            }
            e.Handled = true;
        };
        return label;
    }

    private static TextBox MakeAxisField(float value, string colorHex)
    {
        var text = Fmt(value);
        var tb = new TextBox
        {
            Text              = text,
            // フォーカス前の最終有効値を保持する（不正入力時の復元用）
            Tag               = text,
            Background        = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Foreground        = new SolidColorBrush(Colors.White),
            BorderBrush       = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            BorderThickness   = new Thickness(1),
            FontSize          = 11,
            Padding           = new Thickness(3, 1, 3, 1),
            Margin            = new Thickness(1, 1, 2, 1),
            VerticalAlignment = VerticalAlignment.Center,
            // 選択ハイライト: 青半透明で統一
            SelectionBrush    = new SolidColorBrush(Color.FromArgb(0x66, 0x33, 0x99, 0xFF)),
        };
        AttachAutoSelectBehavior(tb);
        NumericDragBehavior.SetEnabled(tb, true);
        return tb;
    }

    /// <summary>
    /// TextBox クリック時に全選択する動作を付与する。
    /// フォーカス取得時に現在値を Tag に保存し、不正入力時の復元に使う。
    /// GotKeyboardFocus + PreviewMouseLeftButtonDown の組み合わせにより、
    /// クリック後にカーソル位置でテキストが再選択解除される WPF の動作を防ぐ。
    /// </summary>
    private static void AttachAutoSelectBehavior(TextBox tb)
    {
        // フォーカス取得時: 編集前の値を Tag に保存してから全選択
        tb.GotKeyboardFocus += (_, _) =>
        {
            tb.Tag = tb.Text; // 編集開始前の値を保存（不正入力時の復元用）
            tb.SelectAll();
        };

        // クリックでフォーカスを当てる場合: まだフォーカスがなければクリックを横取りして
        // Focus() を呼び出す。これにより GotKeyboardFocus → SelectAll の後に
        // マウスイベントがカーソル位置を上書きするのを防ぐ。
        // NumericDragBehavior が有効な場合はドラッグ側に制御を渡す
        //（ドラッグか通常クリックかを NumericDragBehavior が OnPreviewMouseUp で判断する）。
        tb.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!tb.IsKeyboardFocusWithin)
            {
                if (NumericDragBehavior.GetEnabled(tb)) return;
                e.Handled = true;
                tb.Focus();
            }
        };

        // Enter による確定後はキーボードフォーカスを外す。
        //
        // 【理由】Ctrl+Z / Ctrl+Y はグローバルキーボードフック（MainWindow.Input.cs）で
        // 拾ってランタイムへ送るが、TextBox にフォーカスがある間は「テキスト入力中」と
        // 判定して送らない（IsTextInputFocused）。フォーカスが残ったままだと、
        // 値を確定した直後の Ctrl+Z がランタイムに届かず「Undo が効かない」ように見える。
        // 数値ドラッグ確定時に NumericDragBehavior がフォーカスを外しているのと同じ扱いに揃える。
        //
        // 確定処理（呼び出し側が KeyDown / LostFocus に登録する Commit）より後に外すため、
        // Dispatcher で現在のイベント処理が終わってから実行する。
        tb.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            tb.Dispatcher.BeginInvoke(
                new Action(() => { if (tb.IsKeyboardFocusWithin) Keyboard.ClearFocus(); }),
                System.Windows.Threading.DispatcherPriority.Background);
        };
    }

    private static UIElement BuildPropertyRow(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lbl = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var val = new TextBlock { Text = value, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);
        Grid.SetColumn(val, 1); grid.Children.Add(val);
        return grid;
    }

    // ── Transform drag (undo batching) ───────────────────────

    private void BeginTransformDrag()
    {
        if (_currentActorId < 0) return;
        _isDraggingTransform = true;
        // アクター編集モード・仮想DFS選択のどちらも type=1（アクタートランスフォームモード）
        int type = (_isActorEditMode || _isVirtualActorSelected) ? 1 : 0;
        _runtime?.SendToRuntime($"BEGIN_TRANSFORM_DRAG:{type},{_currentActorId}");
    }

    private void EndTransformDrag()
    {
        if (!_isDraggingTransform) return;
        _isDraggingTransform = false;
        _runtime?.SendToRuntime("END_TRANSFORM_DRAG");
    }

    // ── Field commit ─────────────────────────────────────────

    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            CommitTransform();
            (sender as TextBox)?.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_isActorEditMode && _currentActorId >= 0)
                _runtime?.SendToRuntime($"GET_ACTOR_COMPONENTS:{_currentActorId}");
            else if (_currentActorId >= 0)
                _runtime?.SendToRuntime($"GET_ACTOR:{_currentActorId}");
            e.Handled = true;
        }
    }

    private void OnFieldLostFocus(object sender, RoutedEventArgs e) => CommitTransform();

    private void CommitTransform()
    {
        if (_currentActorId < 0 || _tbPx is null) return;

        // 2D Actor の場合は CanvasTransform 専用コマンドを送信する
        if (_isActor2D && (_isActorEditMode || _isVirtualActorSelected))
        {
            float px   = ParseOrRestore(_tbPx);
            float py   = ParseOrRestore(_tbPy);
            float rot  = ParseOrRestore(_tbEz);
            float sx   = ParseOrRestore(_tbSx);
            float sy   = ParseOrRestore(_tbSy);
            // ピボット: フィールドが未設定の場合は 0.0 を使用する
            float pivx = _tbPivotX is not null ? ParseOrRestore(_tbPivotX) : 0f;
            float pivy = _tbPivotY is not null ? ParseOrRestore(_tbPivotY) : 0f;
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"SET_CANVAS_TRANSFORM:{_currentActorId},{px},{py},{rot},{sx},{sy},{pivx},{pivy}"));
            TransformCommitted?.Invoke();
            return;
        }

        // 3D Actor の場合は従来の 9 軸トランスフォームコマンドを送信する
        float px3 = ParseOrRestore(_tbPx), py3 = ParseOrRestore(_tbPy), pz3 = ParseOrRestore(_tbPz);
        float ex3 = ParseOrRestore(_tbEx), ey3 = ParseOrRestore(_tbEy), ez3 = ParseOrRestore(_tbEz);
        float sx3 = ParseOrRestore(_tbSx), sy3 = ParseOrRestore(_tbSy), sz3 = ParseOrRestore(_tbSz);

        string msg;
        // アクター編集モード、またはシーンモードで仮想DFS選択された場合は SET_ACTOR_TRANSFORM を使う。
        // それ以外（レガシーインスタンス直接選択）は SET_TRANSFORM を使う。
        if (_isActorEditMode || _isVirtualActorSelected)
        {
            msg = FormattableString.Invariant(
                $"SET_ACTOR_TRANSFORM:{_currentActorId},{px3},{py3},{pz3},{ex3},{ey3},{ez3},{sx3},{sy3},{sz3}");
        }
        else
        {
            msg = FormattableString.Invariant(
                $"SET_TRANSFORM:{_currentActorId},{px3},{py3},{pz3},{ex3},{ey3},{ez3},{sx3},{sy3},{sz3}");
        }
        _runtime?.SendToRuntime(msg);
        TransformCommitted?.Invoke();
    }

    private static bool TryParse(TextBox? tb, out float value)
    {
        value = 0f;
        return tb is not null && float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// TextBox のテキストを float にパースする。
    /// 空欄の場合は "0.000" を書き戻して 0 を返す。
    /// 無効な文字列の場合はフォーカス取得時に Tag へ保存した直前の値を復元して返す。
    /// </summary>
    private static float ParseOrRestore(TextBox? tb)
    {
        if (tb is null) return 0f;

        // 空欄 → 0 に確定
        if (string.IsNullOrWhiteSpace(tb.Text))
        {
            tb.Text = Fmt(0f);
            return 0f;
        }

        // 有効な数値 → そのまま使用
        if (float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;

        // 無効な文字列 → Tag に保存した直前の有効値を復元
        if (tb.Tag is string prev
            && float.TryParse(prev, NumberStyles.Float, CultureInfo.InvariantCulture, out var pv))
        {
            tb.Text = prev;
            return pv;
        }

        // フォールバック: 0
        tb.Text = Fmt(0f);
        return 0f;
    }

    // ── Helpers ──────────────────────────────────────────────

    private static float Fp(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) ? v.GetSingle() : 0f;

    private static string Fmt(float v) =>
        v.ToString("F3", CultureInfo.InvariantCulture);
}
