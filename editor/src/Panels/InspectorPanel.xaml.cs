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
using SEEDEditor.Runtime;
using SEEDEditor.Scripting;

namespace SEEDEditor.Panels;

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

    /// <summary>次回の BuildActorComponentList 完了後に自動リネームモードを開始するスロットのベース名。</summary>
    private bool   _pendingDuplicateRename   = false;
    private string _pendingDuplicateBaseName = "";

    // ── Script state ─────────────────────────────────────────
    private readonly Dictionary<string, Type> _scriptTypeCache = new();
    private string _lastComponentsJson = "";

    /// <summary>「スクリプトを編集」ボタンで .cs を内蔵エディタで開くよう要求する（フルパス）。</summary>
    public event Action<string>? ScriptFileOpenRequested;

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
        }
        _runtime = runtime;
        _runtime.SelectionChanged        += OnSelectionChanged;
        _runtime.ActorDataReceived       += OnActorDataReceived;
        _runtime.ActorComponentsReceived += OnActorComponentsReceived;
        _runtime.PluginListReceived      += OnPluginListReceived;
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

            // VP ref 解決待ちの場合は、ドロップされたアクターの Camera スロットを抽出して
            // インスペクタ UI 再構築ではなく参照設定処理に転用する。
            if (_pendingVpRefActorDfsId >= 0)
            {
                ResolveCameraRefFromComponents(json);
                return;
            }

            try { BuildActorComponentList(json); }
            catch (Exception ex) { EditorLog.Write($"InspectorPanel: ACTOR_COMPONENTS parse error: {ex.Message}"); }
        });
    }

    // ── No selection ─────────────────────────────────────────

    private void ShowNoSelection()
    {
        _isVirtualActorSelected     = false;
        _isActor2D                  = false;
        ActorNameBlock.Text         = "選択なし";
        ActorModelBlock.Visibility  = Visibility.Collapsed;
        ComponentScroll.Visibility  = Visibility.Collapsed;
        ActorEditGrid.Visibility    = Visibility.Collapsed;
        NoSelectionBlock.Visibility = Visibility.Visible;
        ComponentStack.Children.Clear();
        ComponentListStack.Children.Clear();
        AccordionStack.Children.Clear();
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
            ComponentStack.Children.Add(BuildCanvas2DTransformSection(px, py, rot, sx, sy, pivx, pivy, ancX, ancY));
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

    /// <summary>コンポーネントスロット 1 件分の情報。TypeId ごとに追加フィールドを持つ。</summary>
    private record SlotInfo(int SlotIdx, string Name, string TypeId, string ModelPath,
        float Width = 0f, float Height = 0f,
        // CanvasComponent 用スケールモードフラグ
        bool ScaleTransform = false, bool ScaleSize = false,
        // CanvasComponent 用自動スケールフラグ
        bool AutoScale = true,
        // CanvasComponent 用ビューポート参照（"window" or "camera"）
        string VpRefType = "window", string VpRefActor = "", string VpRefSlot = "",
        // CanvasComponent 用アスペクト比維持（scale_size=true のときのみ有効）
        bool KeepAspectRatio = false, string AspectRatioAxis = "width",
        // CanvasComponent 用重力方向モード（0=スクリーン下, 1=キャンバス下）
        int GravityMode = 0,
        // 3D CanvasComponent 用ピボット（正規化値 [0,1]）。Actor3D アタッチ時のみ有効。
        float Canvas3dPivotX = 0f, float Canvas3dPivotY = 0f,
        // SpriteComponent 用フィールド
        string TexturePath = "",
        float SpriteR = 1f, float SpriteG = 1f, float SpriteB = 1f, float SpriteA = 1f,
        float SpriteW = 100f, float SpriteH = 100f,
        // InputMapComponent 用フィールド
        string InputMapPath = "",
        // CameraComponent 用フィールド
        float FovYDeg = 45f, float CamNear = 0.1f, float CamFar = 1000f,
        bool  IsMain  = false,
        float CamCR = 0.1f, float CamCG = 0.1f, float CamCB = 0.1f, float CamCA = 1f,
        string CamScalingMode = "vert_minus", int CamTargetW = 1920, int CamTargetH = 1080,
        float CamBarCR = 0f, float CamBarCG = 0f, float CamBarCB = 0f, float CamBarCA = 1f,
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
        float AudioPan = 0f);

    private List<SlotInfo> _slotInfos = new();

    private void BuildActorComponentList(string json)
    {
        _lastComponentsJson = json;

        using var doc  = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name = root.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
        ActorNameBlock.Text = string.IsNullOrEmpty(name) ? $"Actor #{_currentActorId}" : name;

        // 複製後の新スロット検出用に現在のスロット ID セットを保存する
        var prevSlotIdxSet = _slotInfos.Select(s => s.SlotIdx).ToHashSet();
        ComponentListStack.Children.Clear();
        AccordionStack.Children.Clear();
        ClearTransformRefs();
        _slotInfos.Clear();

        // ── 基本情報 アコーディオン ────────────────────────────────────
        UIElement transformContent;
        if (root.TryGetProperty("canvas_transform", out var ct))
        {
            // 2D アクター: CanvasTransform（位置XY・回転・スケールXY・ピボットXY・アンカーXY）
            _isActor2D = true;
            float px   = Fp(ct, "px"),  py   = Fp(ct, "py");
            float rot  = Fp(ct, "rotation");
            float sx   = Fp(ct, "sx"),  sy   = Fp(ct, "sy");
            float pivx = Fp(ct, "pivx"), pivy = Fp(ct, "pivy");
            float ancX = Fp(ct, "anchor_x"), ancY = Fp(ct, "anchor_y");
            transformContent = BuildCanvas2DTransformSection(px, py, rot, sx, sy, pivx, pivy, ancX, ancY);
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
        AccordionStack.Children.Add(BuildAccordionSection("基本情報", "", transformContent, -1));

        // ── コンポーネント アコーディオン ─────────────────────────────
        if (!root.TryGetProperty("components", out var comps)) return;

        _componentSlotCount = comps.GetArrayLength();
        var prevSlot = _selectedSlotIdx;
        _selectedSlotIdx = -1;

        foreach (var comp in comps.EnumerateArray())
        {
            var slotIdx  = comp.TryGetProperty("slot",       out var si)  ? si.GetInt32()    : 0;
            var compName = comp.TryGetProperty("name",       out var cn)  ? cn.GetString() ?? "" : "";
            var compType = comp.TryGetProperty("type",       out var ctp) ? ctp.GetString() ?? "" : "";
            var modelPath = comp.TryGetProperty("model_path", out var mp) ? mp.GetString() ?? "" : "";
            // CanvasComponent 用: 幅・高さ・スケールモード
            var width          = comp.TryGetProperty("width",           out var wd)  ? wd.GetSingle()  : 0f;
            var height         = comp.TryGetProperty("height",          out var ht)  ? ht.GetSingle()  : 0f;
            // Rust の serde_json は bool を JSON 真偽値（true/false）としてシリアライズするため
            // GetInt32() ではなく ValueKind で判定する。数値（0/1）も念のため許容する。
            var scaleTransform = comp.TryGetProperty("scale_transform", out var stv) ? ReadJsonBool(stv, false) : false;
            var scaleSize      = comp.TryGetProperty("scale_size",      out var ssv) ? ReadJsonBool(ssv, false) : false;
            var autoScale      = comp.TryGetProperty("auto_scale",      out var asv) ? ReadJsonBool(asv, true)  : true;
            // CanvasComponent 用: ビューポート参照
            var vpRefType  = comp.TryGetProperty("vp_ref_type",  out var vrt) ? vrt.GetString() ?? "window" : "window";
            var vpRefActor = comp.TryGetProperty("vp_ref_actor", out var vra) ? vra.GetString() ?? ""       : "";
            var vpRefSlot  = comp.TryGetProperty("vp_ref_slot",  out var vrs) ? vrs.GetString() ?? ""       : "";
            // CanvasComponent 用: アスペクト比維持
            var keepAspectRatio = comp.TryGetProperty("keep_aspect_ratio", out var kar) ? ReadJsonBool(kar, false) : false;
            var aspectRatioAxis = comp.TryGetProperty("aspect_ratio_axis", out var ara) ? ara.GetString() ?? "width" : "width";
            // CanvasComponent 用: 重力方向モード
            var gravityMode = comp.TryGetProperty("gravity_mode", out var gm)  ? gm.GetInt32()  : 0;
            // 3D CanvasComponent 用: ピボット
            var canvas3dPivotX = comp.TryGetProperty("pivot_x", out var pvx) ? pvx.GetSingle() : 0f;
            var canvas3dPivotY = comp.TryGetProperty("pivot_y", out var pvy) ? pvy.GetSingle() : 0f;
            // SpriteComponent 用: テクスチャパス・RGBA・サイズ
            var texPath = comp.TryGetProperty("texture_path", out var tp2) ? tp2.GetString() ?? "" : "";
            var sprR = comp.TryGetProperty("cr", out var cr) ? cr.GetSingle() : 1f;
            var sprG = comp.TryGetProperty("cg", out var cg) ? cg.GetSingle() : 1f;
            var sprB = comp.TryGetProperty("cb", out var cb) ? cb.GetSingle() : 1f;
            var sprA = comp.TryGetProperty("ca", out var ca) ? ca.GetSingle() : 1f;
            var sprW = comp.TryGetProperty("sprite_w", out var sw) ? sw.GetSingle() : 100f;
            var sprH = comp.TryGetProperty("sprite_h", out var sh) ? sh.GetSingle() : 100f;
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

            var info = new SlotInfo(slotIdx, compName, compType, modelPath, width, height,
                scaleTransform, scaleSize, autoScale,
                VpRefType: vpRefType, VpRefActor: vpRefActor, VpRefSlot: vpRefSlot,
                KeepAspectRatio: keepAspectRatio, AspectRatioAxis: aspectRatioAxis,
                GravityMode: gravityMode,
                Canvas3dPivotX: canvas3dPivotX, Canvas3dPivotY: canvas3dPivotY,
                TexturePath: texPath, SpriteR: sprR, SpriteG: sprG, SpriteB: sprB, SpriteA: sprA,
                SpriteW: sprW, SpriteH: sprH,
                InputMapPath: inputMapPath,
                FovYDeg: fovYDeg, CamNear: camNear, CamFar: camFar, IsMain: isMain,
                CamCR: camCR, CamCG: camCG, CamCB: camCB, CamCA: camCA,
                CamScalingMode: camScalingMode, CamTargetW: camTargetW, CamTargetH: camTargetH,
                CamBarCR: camBarCR, CamBarCG: camBarCG, CamBarCB: camBarCB, CamBarCA: camBarCA,
                PluginName: pluginName, PluginFieldsJson: pluginFieldsJson,
                ColliderDataJson: colliderDataJson, RigidbodyDataJson: rigidbodyDataJson,
                ScriptFieldsJson: scriptFieldsJson,
                AudioPath: audioPath, AudioVolume: audioVolume,
                AudioLoop: audioLoop, AudioPlayOnStart: audioPlayOnStart, AudioSpatial: audioSpatial,
                AudioMinDistance: audioMinDistance, AudioMaxDistance: audioMaxDistance,
                AudioPan: audioPan);
            _slotInfos.Add(info);

            // 上部チップリストに追加
            ComponentListStack.Children.Add(BuildComponentChip(info.SlotIdx, info.Name, info.TypeId));

            // アコーディオンにパラメータ編集エリアを追加
            var propsContent = BuildSlotPropsContent(info);
            AccordionStack.Children.Add(BuildAccordionSection(info.Name, info.TypeId, propsContent, info.SlotIdx));

            if (slotIdx == prevSlot) _selectedSlotIdx = slotIdx;
        }

        // 前回未選択かつコンポーネントが存在する場合は最後のスロットを自動選択する
        if (_selectedSlotIdx < 0 && _slotInfos.Count > 0)
            _selectedSlotIdx = _slotInfos[^1].SlotIdx;

        RefreshChipSelection();

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
        "PluginComponent"     => "Plugin",
        _ when typeId.StartsWith("Plugin:", StringComparison.Ordinal) => typeId["Plugin:".Length..],
        _                     => typeId,
    };

    /// <summary>
    /// アコーディオンセクションを生成する。
    /// ヘッダー行（▼/▶ + アイコン + タイトル）と折り畳み可能なコンテンツエリアを持つ。
    /// パラメータ編集専用のため、削除・リネームなどの操作ボタンは持たない。
    /// slotIdx が -1 の場合は基本情報セクション。
    /// </summary>
    private StackPanel BuildAccordionSection(string title, string typeId, UIElement content, int slotIdx)
    {
        var isExpanded = true;

        // ── コンテナ（ヘッダー + コンテンツ）────────────────────
        var container = new StackPanel { Tag = slotIdx };

        // ── ヘッダー（コンポーネント種別に応じた有彩色背景）────────
        var isComponentSlot = slotIdx >= 0 && !string.IsNullOrEmpty(typeId);
        var headerBgColor   = isComponentSlot ? GetTypeHeaderColor(typeId) : Color.FromRgb(0x2A, 0x2A, 0x2A);

        var header = new Border
        {
            Background      = new SolidColorBrush(headerBgColor),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding         = new Thickness(6, 6, 6, 6),
            Cursor          = Cursors.Hand,
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // 矢印
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // アイコン
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // タイトル

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
            Grid.SetColumn(icon, 1);
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
        Grid.SetColumn(titleBlock, 2);
        headerGrid.Children.Add(titleBlock);

        header.Child = headerGrid;

        // ── コンテンツラッパー（左インデント）────────────────────
        var contentWrapper = new Border
        {
            Padding         = new Thickness(16, 0, 0, 4),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child           = content,
        };

        // ヘッダークリックで展開/折り畳みトグル
        header.MouseLeftButtonDown += (_, _) =>
        {
            isExpanded = !isExpanded;
            contentWrapper.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = isExpanded ? "▼" : "▶";
        };

        container.Children.Add(header);
        container.Children.Add(contentWrapper);
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

        var combo = new ComboBox
        {
            Height     = 22,
            Background = System.Windows.Media.Brushes.White,
            Foreground = System.Windows.Media.Brushes.Black,
        };
        foreach (var opt in options)
            combo.Items.Add(new ComboBoxItem { Content = opt, Foreground = System.Windows.Media.Brushes.Black });

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

        // FOV（垂直視野角）
        var rowFov = BuildLabeledNumberRow("FOV (垂直°)", info.FovYDeg);
        sp.Children.Add(rowFov.element);
        void CommitFov()
        {
            if (_currentActorId < 0) return;
            if (!float.TryParse(rowFov.textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
            _runtime?.SendToRuntime(FormattableString.Invariant($"SET_CAMERA_FOV:{_currentActorId},{info.SlotIdx},{v}"));
        }
        rowFov.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitFov(); e.Handled = true; } };
        rowFov.textBox.LostFocus += (_, _) => CommitFov();
        NumericDragBehavior.SetOnDrag(rowFov.textBox, CommitFov);

        // ニアクリップ
        var rowNear = BuildLabeledNumberRow("Near", info.CamNear);
        sp.Children.Add(rowNear.element);
        void CommitNear()
        {
            if (_currentActorId < 0) return;
            if (!float.TryParse(rowNear.textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
            _runtime?.SendToRuntime(FormattableString.Invariant($"SET_CAMERA_NEAR:{_currentActorId},{info.SlotIdx},{v}"));
        }
        rowNear.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitNear(); e.Handled = true; } };
        rowNear.textBox.LostFocus += (_, _) => CommitNear();
        NumericDragBehavior.SetOnDrag(rowNear.textBox, CommitNear);

        // ファークリップ
        var rowFar = BuildLabeledNumberRow("Far", info.CamFar);
        sp.Children.Add(rowFar.element);
        void CommitFar()
        {
            if (_currentActorId < 0) return;
            if (!float.TryParse(rowFar.textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
            _runtime?.SendToRuntime(FormattableString.Invariant($"SET_CAMERA_FAR:{_currentActorId},{info.SlotIdx},{v}"));
        }
        rowFar.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitFar(); e.Handled = true; } };
        rowFar.textBox.LostFocus += (_, _) => CommitFar();
        NumericDragBehavior.SetOnDrag(rowFar.textBox, CommitFar);

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
        sp.Children.Add(clearRow);

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
            Width      = 170,
            FontSize   = 11,
            Margin     = new Thickness(4, 0, 0, 0),
            Background = System.Windows.Media.Brushes.White,
            Foreground = System.Windows.Media.Brushes.Black,
        };
        foreach (var (val, label) in scalingModes)
            scalingCombo.Items.Add(new ComboBoxItem
            {
                Content    = label,
                Tag        = val,
                Foreground = System.Windows.Media.Brushes.Black,
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

        // ターゲット解像度（スケーリングモード用）— 整数入力
        // "F0" フォーマット（小数点なし）を使うことで int.TryParse がそのまま使える。
        // デフォルトの "F1" だと "1920.0" のように表示され int.TryParse が失敗するため。
        var rowTW = BuildLabeledNumberRow("解像度 W", info.CamTargetW, "F0");
        NumericDragBehavior.Attach(rowTW.textBox, sensitivity: 1.0, isInteger: true);
        var rowTH = BuildLabeledNumberRow("解像度 H", info.CamTargetH, "F0");
        NumericDragBehavior.Attach(rowTH.textBox, sensitivity: 1.0, isInteger: true);
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
        sp.Children.Add(barRow);

        // LetterBox / PillarBox のときのみ帯カラー行を表示する
        static bool IsBarMode(string mode) => mode is "letter_box" or "pillar_box" or "letter_pillar_box";
        barRow.Visibility = IsBarMode(info.CamScalingMode) ? Visibility.Visible : Visibility.Collapsed;
        scalingCombo.SelectionChanged += (_, _) =>
        {
            if (scalingCombo.SelectedItem is ComboBoxItem item && item.Tag is string mode)
                barRow.Visibility = IsBarMode(mode) ? Visibility.Visible : Visibility.Collapsed;
        };

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
            Background = System.Windows.Media.Brushes.White,
            Foreground = System.Windows.Media.Brushes.Black,
        };
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Box",      Foreground = System.Windows.Media.Brushes.Black });
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Sphere",   Foreground = System.Windows.Media.Brushes.Black });
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Capsule",  Foreground = System.Windows.Media.Brushes.Black });
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Cylinder", Foreground = System.Windows.Media.Brushes.Black });
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Cone",     Foreground = System.Windows.Media.Brushes.Black });
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
        (UIElement element, TextBox textBox) MakeF(string label, float val) => BuildLabeledNumberRow(label, val);

        var rowMass = MakeF("質量 (kg)",        curMass);
        var rowRest = MakeF("反発係数",          curRest);
        var rowFric = MakeF("摩擦係数",          curFric);
        var rowLinD = MakeF("移動速度の減衰",    curLinDamp);
        var rowAngD = MakeF("回転速度の減衰",    curAngD);
        var rowGrav = MakeF("重力の倍率",        curGrav);
        rbParamsPanel.Children.Add(rowMass.element);
        rbParamsPanel.Children.Add(rowRest.element);
        rbParamsPanel.Children.Add(rowFric.element);
        rbParamsPanel.Children.Add(rowLinD.element);
        rbParamsPanel.Children.Add(rowAngD.element);
        rbParamsPanel.Children.Add(rowGrav.element);

        void CommitFloats() {
            if (!TryParseF(rowMass.textBox, out curMass))    return;
            if (!TryParseF(rowRest.textBox, out curRest))    return;
            if (!TryParseF(rowFric.textBox, out curFric))    return;
            if (!TryParseF(rowLinD.textBox, out curLinDamp)) return;
            if (!TryParseF(rowAngD.textBox, out curAngD))    return;
            if (!TryParseF(rowGrav.textBox, out curGrav))    return;
            CommitCollider();
        }
        foreach (var row in new[] { rowMass, rowRest, rowFric, rowLinD, rowAngD, rowGrav })
        {
            row.textBox.LostFocus += (_, _) => CommitFloats();
            row.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitFloats(); };
            NumericDragBehavior.SetOnDrag(row.textBox, CommitFloats);
        }

        // --- キネマティック（スクリプトで直接制御） ---
        rbParamsPanel.Children.Add(BuildCheckRow("キネマティック", curKinem,
            v => { curKinem = v; CommitCollider(); }));

        // --- フリーズ（軸固定） ---
        rbParamsPanel.Children.Add(BuildFreezeRow("静止移動軸", curFreezeP, () => CommitCollider()));
        rbParamsPanel.Children.Add(BuildFreezeRow("静止回転軸", curFreezeR, () => CommitCollider()));

        // --- 初速度 ---
        var rowLinV = BuildXYZRowSimple("初速度 (m/s)", curLinV[0], curLinV[1], curLinV[2]);
        rbParamsPanel.Children.Add(rowLinV.element);
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
        rbParamsPanel.Children.Add(rowAngV.element);
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
            Background = System.Windows.Media.Brushes.White,
            Foreground = System.Windows.Media.Brushes.Black,
        };
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Box",     Foreground = System.Windows.Media.Brushes.Black });
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Circle",  Foreground = System.Windows.Media.Brushes.Black });
        shapeCombo.Items.Add(new ComboBoxItem { Content = "Capsule", Foreground = System.Windows.Media.Brushes.Black });
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

        (UIElement element, TextBox textBox) MakeF2d(string label, float val) => BuildLabeledNumberRow(label, val);
        var rowMass2d = MakeF2d("質量 (kg)",     curMass);
        var rowRest2d = MakeF2d("反発係数",       curRest);
        var rowFric2d = MakeF2d("摩擦係数",       curFric);
        var rowLinD2d = MakeF2d("移動速度の減衰", curLinDamp);
        var rowAngD2d = MakeF2d("回転速度の減衰", curAngD);
        var rowGrav2d = MakeF2d("重力の倍率",     curGrav);
        rbParamsPanel2d.Children.Add(rowMass2d.element);
        rbParamsPanel2d.Children.Add(rowRest2d.element);
        rbParamsPanel2d.Children.Add(rowFric2d.element);
        rbParamsPanel2d.Children.Add(rowLinD2d.element);
        rbParamsPanel2d.Children.Add(rowAngD2d.element);
        rbParamsPanel2d.Children.Add(rowGrav2d.element);

        void CommitFloats2d() {
            if (!TryParseF(rowMass2d.textBox, out curMass))    return;
            if (!TryParseF(rowRest2d.textBox, out curRest))    return;
            if (!TryParseF(rowFric2d.textBox, out curFric))    return;
            if (!TryParseF(rowLinD2d.textBox, out curLinDamp)) return;
            if (!TryParseF(rowAngD2d.textBox, out curAngD))    return;
            if (!TryParseF(rowGrav2d.textBox, out curGrav))    return;
            CommitCollider2d();
        }
        foreach (var row2d in new[] { rowMass2d, rowRest2d, rowFric2d, rowLinD2d, rowAngD2d, rowGrav2d })
        {
            row2d.textBox.LostFocus += (_, _) => CommitFloats2d();
            row2d.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitFloats2d(); };
            NumericDragBehavior.SetOnDrag(row2d.textBox, CommitFloats2d);
        }

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
        var rowLinV2d = BuildXYRowSimple2d("初速度 (px/s)", curLinV[0], curLinV[1]);
        rbParamsPanel2d.Children.Add(rowLinV2d.element);
        void CommitLinV2d() {
            if (!TryParseF(rowLinV2d.tx, out var x)) return;
            if (!TryParseF(rowLinV2d.ty, out var y)) return;
            curLinV[0] = x; curLinV[1] = y; CommitCollider2d();
        }
        rowLinV2d.tx.LostFocus += (_, _) => CommitLinV2d(); rowLinV2d.tx.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitLinV2d(); };
        rowLinV2d.ty.LostFocus += (_, _) => CommitLinV2d(); rowLinV2d.ty.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitLinV2d(); };
        NumericDragBehavior.SetOnDrag(rowLinV2d.tx, CommitLinV2d); NumericDragBehavior.SetOnDrag(rowLinV2d.ty, CommitLinV2d);

        // --- 初期角速度 (rad/s) ---
        var rowAngV2d = BuildLabeledNumberRow("初角速度 (rad/s)", curAngV2);
        rbParamsPanel2d.Children.Add(rowAngV2d.element);
        void CommitAngV2d() {
            if (TryParseF(rowAngV2d.textBox, out var v)) { curAngV2 = v; CommitCollider2d(); }
        }
        rowAngV2d.textBox.LostFocus += (_, _) => CommitAngV2d();
        rowAngV2d.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) CommitAngV2d(); };
        NumericDragBehavior.SetOnDrag(rowAngV2d.textBox, CommitAngV2d);

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
            Width      = 100,
            FontSize   = 11,
            Background = System.Windows.Media.Brushes.White,
            Foreground = System.Windows.Media.Brushes.Black,
            Margin     = new Thickness(4, 0, 0, 0),
        };
        cmbAspectAxis2d.Items.Add(new ComboBoxItem { Content = "横（幅）基準",   Tag = "width",  Foreground = System.Windows.Media.Brushes.Black });
        cmbAspectAxis2d.Items.Add(new ComboBoxItem { Content = "縦（高さ）基準", Tag = "height", Foreground = System.Windows.Media.Brushes.Black });
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

    private UIElement BuildModelSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        sp.Children.Add(BuildModelPathRow(info));
        return sp;
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
        sp.Children.Add(colorRow);

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

        return sp;
    }

    // ── AudioComponent inspector ──────────────────────────────

    /// <summary>パン値の下限（完全に左）。</summary>
    private const float AudioPanMin = -1f;
    /// <summary>パン値の上限（完全に右）。</summary>
    private const float AudioPanMax = 1f;
    /// <summary>音量の下限（無音）。</summary>
    private const float AudioVolumeMin = 0f;

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
        var rowVol = BuildLabeledNumberRow("音量", info.AudioVolume, "F2");
        sp.Children.Add(rowVol.element);
        void CommitVolume()
        {
            if (!float.TryParse(rowVol.textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
            // 負値は無効なため下限でクランプする
            v = MathF.Max(AudioVolumeMin, v);
            SendField("volume", v.ToString(CultureInfo.InvariantCulture));
        }
        rowVol.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitVolume(); e.Handled = true; } };
        rowVol.textBox.LostFocus += (_, _) => CommitVolume();
        NumericDragBehavior.SetOnDrag(rowVol.textBox, CommitVolume);

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

        // 数値フィールド + SET_AUDIO_FIELD 送信をまとめて構築するローカル関数
        void AddFloatRow(string label, float value, string key)
        {
            var row = BuildLabeledNumberRow(label, value);
            sp.Children.Add(row.element);
            void Commit()
            {
                if (!float.TryParse(row.textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
                SendField(key, v.ToString(CultureInfo.InvariantCulture));
            }
            row.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; } };
            row.textBox.LostFocus += (_, _) => Commit();
            NumericDragBehavior.SetOnDrag(row.textBox, Commit);
        }
        AddFloatRow("減衰開始距離", info.AudioMinDistance, "min_distance");
        AddFloatRow("無音距離",     info.AudioMaxDistance, "max_distance");

        // ── パン（3D空間再生 OFF 時のみ有効）──────────────────
        AddHint($"パン（{AudioPanMin:F0}=左 〜 {AudioPanMax:F0}=右）は 3D空間再生が OFF のときのみ有効です");
        var rowPan = BuildLabeledNumberRow("パン", info.AudioPan, "F2");
        sp.Children.Add(rowPan.element);
        void CommitPan()
        {
            if (!float.TryParse(rowPan.textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
            // パンは [-1, 1] にクランプして送信する
            v = Math.Clamp(v, AudioPanMin, AudioPanMax);
            SendField("pan", v.ToString(CultureInfo.InvariantCulture));
        }
        rowPan.textBox.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitPan(); e.Handled = true; } };
        rowPan.textBox.LostFocus += (_, _) => CommitPan();
        NumericDragBehavior.SetOnDrag(rowPan.textBox, CommitPan);

        return sp;
    }

    // ── CanvasComponent inspector ─────────────────────────────

    /// <summary>CanvasComponent の幅・高さ・スケールモードを表示して IPC を送信する。</summary>
    private UIElement BuildCanvasSlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // 幅フィールド
        var rowW = BuildLabeledNumberRow("幅",  info.Width);
        var tbW  = rowW.textBox;
        sp.Children.Add(rowW.element);

        // 高さフィールド
        var rowH = BuildLabeledNumberRow("高さ", info.Height);
        var tbH  = rowH.textBox;
        sp.Children.Add(rowH.element);

        // ── スケールモード セクション ──────────────────────────
        // 「画面サイズに自動スケール」が ON のときだけ意味を持つ設定のため、
        // 専用パネルにまとめて自動スケールのチェック状態と連動して表示/非表示を切り替える
        //（Actor3D の 3D ワールドキャンバスは自動スケール設定自体が無いため常時表示）。
        var scaleModePanel = new StackPanel();
        var scaleSep = new TextBlock
        {
            Text       = "スケールモード",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize   = 10,
            Margin     = new Thickness(0, 6, 0, 2),
        };
        scaleModePanel.Children.Add(scaleSep);

        // チェックボックス: アイテムのトランスフォームをスケールする
        var cbTransform = new CheckBox
        {
            Content             = "アイテムのトランスフォームをスケールする",
            IsChecked           = info.ScaleTransform,
            Foreground          = new SolidColorBrush(Colors.White),
            FontSize            = 11,
            Margin              = new Thickness(0, 2, 0, 2),
            VerticalAlignment   = VerticalAlignment.Center,
        };
        scaleModePanel.Children.Add(cbTransform);

        // チェックボックス: アイテムのサイズをスケールする
        var cbSize = new CheckBox
        {
            Content             = "アイテムのサイズをスケールする",
            IsChecked           = info.ScaleSize,
            Foreground          = new SolidColorBrush(Colors.White),
            FontSize            = 11,
            Margin              = new Thickness(0, 2, 0, 2),
            VerticalAlignment   = VerticalAlignment.Center,
        };
        scaleModePanel.Children.Add(cbSize);

        // アスペクト比維持パネル（アイテムのサイズをスケールする のときのみ表示）
        var aspectPanel = new StackPanel
        {
            Margin     = new Thickness(16, 0, 0, 6),
            Visibility = info.ScaleSize ? Visibility.Visible : Visibility.Collapsed,
        };

        // チェックボックス: アイテムのアスペクト比を維持
        var cbKeepAspect = new CheckBox
        {
            Content           = "アイテムのアスペクト比を維持",
            IsChecked         = info.KeepAspectRatio,
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
            Visibility  = info.KeepAspectRatio ? Visibility.Visible : Visibility.Collapsed,
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
            Width      = 100,
            FontSize   = 11,
            Background = System.Windows.Media.Brushes.White,
            Foreground = System.Windows.Media.Brushes.Black,
            Margin     = new Thickness(4, 0, 0, 0),
        };
        cmbAspectAxis.Items.Add(new ComboBoxItem { Content = "横（幅）基準",   Tag = "width",  Foreground = System.Windows.Media.Brushes.Black });
        cmbAspectAxis.Items.Add(new ComboBoxItem { Content = "縦（高さ）基準", Tag = "height", Foreground = System.Windows.Media.Brushes.Black });
        cmbAspectAxis.SelectedIndex = info.AspectRatioAxis == "height" ? 1 : 0;
        axisPanel.Children.Add(cmbAspectAxis);
        aspectPanel.Children.Add(axisPanel);
        scaleModePanel.Children.Add(aspectPanel);

        // ── 自動スケール セクション（Actor2D = 2D キャンバス時のみ表示）──────────────────────────
        // Actor3D に Canvas をアタッチした場合（3D ワールドキャンバス）はこの設定を使わない。
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

            // スケールモードは自動スケールが ON のときだけ意味を持つため、
            // チェックボックスの直下に配置し、OFF のときは非表示にする
            scaleModePanel.Margin     = new Thickness(16, 0, 0, 0);   // 従属設定であることを字下げで示す
            scaleModePanel.Visibility = info.AutoScale ? Visibility.Visible : Visibility.Collapsed;
            sp.Children.Add(scaleModePanel);
        }
        else
        {
            // Actor3D（3D ワールドキャンバス）は自動スケール設定が無いため常時表示する
            sp.Children.Add(scaleModePanel);
        }

        // ── ビューポート参照 セクション（Actor2D = 2D キャンバス時のみ表示）──────────────────────────
        // Actor3D の 3D ワールドキャンバスにはビューポート参照は不要。
        var vpRefSep = new TextBlock
        {
            Text       = "ビューポート参照",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize   = 10,
            Margin     = new Thickness(0, 8, 0, 2),
        };
        if (_isActor2D) sp.Children.Add(vpRefSep);

        // 参照種別ドロップダウン（ウィンドウ / カメラ）
        var cmbVpRef = new ComboBox
        {
            Foreground   = new SolidColorBrush(Colors.Black),
            Background   = new SolidColorBrush(Colors.White),
            FontSize     = 11,
            Margin       = new Thickness(0, 2, 0, 4),
            Padding      = new Thickness(4, 2, 4, 2),
        };
        cmbVpRef.Items.Add(new ComboBoxItem { Content = "ウィンドウ", Tag = "window", Foreground = new SolidColorBrush(Colors.Black) });
        cmbVpRef.Items.Add(new ComboBoxItem { Content = "カメラ",     Tag = "camera", Foreground = new SolidColorBrush(Colors.Black) });
        cmbVpRef.SelectedIndex = info.VpRefType == "camera" ? 1 : 0;
        if (_isActor2D) sp.Children.Add(cmbVpRef);

        // カメラ参照フィールド（D&D 受け付けエリア）
        var vpRefCameraPanel = new StackPanel { Visibility = info.VpRefType == "camera" ? Visibility.Visible : Visibility.Collapsed };

        // 現在の参照表示ラベル
        var vpRefLabel = new TextBlock
        {
            Text       = info.VpRefType == "camera" && !string.IsNullOrEmpty(info.VpRefActor)
                             ? $"{info.VpRefActor} / {info.VpRefSlot}"
                             : "（未設定）",
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize   = 11,
            Margin     = new Thickness(4, 0, 4, 2),
        };

        // D&D ドロップゾーン: シーンビューポートやヒエラルキーからカメラアクターをドロップできる
        var vpDropZone = new Border
        {
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x55, 0x77, 0x99)),
            BorderThickness = new Thickness(1),
            Background      = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            CornerRadius    = new CornerRadius(3),
            Padding         = new Thickness(6, 4, 6, 4),
            Margin          = new Thickness(0, 0, 0, 2),
            AllowDrop       = true,
            Child           = vpRefLabel,
            ToolTip         = "シーンビューポートまたはヒエラルキーからカメラアクターをドロップして参照を設定",
        };

        // クリア（ウィンドウに戻す）ボタン
        var btnClearVp = new Button
        {
            Content         = "✕ 参照解除",
            FontSize        = 10,
            Foreground      = new SolidColorBrush(Color.FromRgb(0xAA, 0x55, 0x55)),
            Background      = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x44, 0x22, 0x22)),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(6, 2, 6, 2),
            Margin          = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        vpRefCameraPanel.Children.Add(vpDropZone);
        vpRefCameraPanel.Children.Add(btnClearVp);
        if (_isActor2D) sp.Children.Add(vpRefCameraPanel);

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

        // スケールモードを送信するローカル関数
        void CommitScaleMode()
        {
            if (_currentActorId < 0) return;
            int st = (cbTransform.IsChecked == true) ? 1 : 0;
            int ss = (cbSize.IsChecked      == true) ? 1 : 0;
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"SET_CANVAS_SCALE_MODE:{_currentActorId},{info.SlotIdx},{st},{ss}"));
        }

        // 画面サイズ自動スケールを送信するローカル関数
        void CommitAutoScale()
        {
            if (_currentActorId < 0) return;
            int v = (cbAutoScale.IsChecked == true) ? 1 : 0;
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"SET_CANVAS_AUTO_SCALE:{_currentActorId},{info.SlotIdx},{v}"));
        }

        // ビューポート参照種別変更
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

        // D&D: ドラッグオーバー（カメラアクター DFS ID を期待）
        // HierarchyPanel は DoDragDrop を DragDropEffects.Move で呼ぶため、
        // ここも Move を要求しないと Effects の AND が None になってドロップが拒否される。
        vpDropZone.DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent("HierarchyActorDfsId") || e.Data.GetDataPresent("SceneViewActorDfsId"))
                e.Effects = DragDropEffects.Move;
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        };

        // D&D: ドロップ処理
        vpDropZone.Drop += (_, e) =>
        {
            if (_currentActorId < 0 || _runtime is null) return;
            int? droppedDfsId = null;
            if (e.Data.GetDataPresent("HierarchyActorDfsId"))
                droppedDfsId = e.Data.GetData("HierarchyActorDfsId") as int?;
            else if (e.Data.GetDataPresent("SceneViewActorDfsId"))
                droppedDfsId = e.Data.GetData("SceneViewActorDfsId") as int?;
            if (droppedDfsId is null) return;
            // ドロップされたアクターのカメラスロットを解決する（IPC 経由でスロット一覧を取得）
            ResolveAndApplyCameraRef(droppedDfsId.Value, info.SlotIdx, vpRefLabel);
            e.Handled = true;
        };

        // 参照解除ボタン
        btnClearVp.Click += (_, _) =>
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"SET_CANVAS_VIEWPORT_REF_WINDOW:{_currentActorId},{info.SlotIdx}");
            vpRefLabel.Text = "（未設定）";
        };

        tbW.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitSize(); e.Handled = true; } };
        tbW.LostFocus += (_, _) => CommitSize();
        tbH.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitSize(); e.Handled = true; } };
        tbH.LostFocus += (_, _) => CommitSize();
        NumericDragBehavior.SetOnDrag(tbW, CommitSize); NumericDragBehavior.SetOnDrag(tbH, CommitSize);

        cbTransform.Checked   += (_, _) => CommitScaleMode();
        cbTransform.Unchecked += (_, _) => CommitScaleMode();
        cbSize.Checked        += (_, _) =>
        {
            CommitScaleMode();
            aspectPanel.Visibility = Visibility.Visible;
        };
        cbSize.Unchecked += (_, _) =>
        {
            CommitScaleMode();
            aspectPanel.Visibility = Visibility.Collapsed;
        };

        // アスペクト比維持を送信するローカル関数
        void CommitAspectRatio()
        {
            if (_currentActorId < 0) return;
            int keep = (cbKeepAspect.IsChecked == true) ? 1 : 0;
            var axis = (cmbAspectAxis.SelectedItem as ComboBoxItem)?.Tag as string ?? "width";
            _runtime?.SendToRuntime($"SET_CANVAS_ASPECT_RATIO:{_currentActorId},{info.SlotIdx},{keep},{axis}");
        }

        cbKeepAspect.Checked   += (_, _) => { axisPanel.Visibility = Visibility.Visible;  CommitAspectRatio(); };
        cbKeepAspect.Unchecked += (_, _) => { axisPanel.Visibility = Visibility.Collapsed; CommitAspectRatio(); };
        cmbAspectAxis.SelectionChanged += (_, _) => CommitAspectRatio();

        cbAutoScale.Checked   += (_, _) => { CommitAutoScale(); scaleModePanel.Visibility = Visibility.Visible; };
        cbAutoScale.Unchecked += (_, _) => { CommitAutoScale(); scaleModePanel.Visibility = Visibility.Collapsed; };

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
            Foreground = new SolidColorBrush(Colors.Black),
            Background = new SolidColorBrush(Colors.White),
            FontSize   = 11,
            Margin     = new Thickness(0, 2, 0, 4),
            Padding    = new Thickness(4, 2, 4, 2),
            ToolTip    = "スクリーン下方向: キャンバスを回転しても「画面の下」が常に重力方向です。\n" +
                         "キャンバス下方向: キャンバスを回転すると重力も追従します。",
        };
        cmbGravity.Items.Add(new ComboBoxItem
        {
            Content    = "スクリーン下方向を正とする（デフォルト）",
            Tag        = 0,
            Foreground = new SolidColorBrush(Colors.Black),
        });
        cmbGravity.Items.Add(new ComboBoxItem
        {
            Content    = "キャンバス下方向を正とする",
            Tag        = 1,
            Foreground = new SolidColorBrush(Colors.Black),
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

            sp.Children.Add(piv3dRow);
        }

        return sp;
    }

    /// <summary>
    /// ドロップされたアクターの Camera スロットを解決して参照を設定する。
    /// 同一アクターに複数の Camera スロットがある場合はポップアップで選択させる。
    /// actorファイルからのドロップは DFS ID が存在しないため不可（インスタンス化済みのみ対応）。
    /// </summary>
    private void ResolveAndApplyCameraRef(int droppedActorDfsId, int canvasSlotIdx, TextBlock displayLabel)
    {
        if (_runtime is null || _currentActorId < 0) return;

        // ランタイムへアクターのコンポーネント一覧を要求して Camera スロットを収集する
        // NOTE: GET_ACTOR_COMPONENTS の応答は非同期 IPC のため、ここでは pending 状態にして
        //       OnActorComponentsReceived で続きを処理する。
        _pendingVpRefActorDfsId  = droppedActorDfsId;
        _pendingVpRefCanvasSlotIdx = canvasSlotIdx;
        _pendingVpRefDisplayLabel  = displayLabel;
        _runtime.SendToRuntime($"GET_ACTOR_COMPONENTS:{droppedActorDfsId}");
    }

    // ── CanvasViewportRef ドロップ解決用 pending フィールド ──────────────
    private int     _pendingVpRefActorDfsId    = -1;
    private int     _pendingVpRefCanvasSlotIdx = -1;
    private TextBlock? _pendingVpRefDisplayLabel = null;

    /// <summary>
    /// GET_ACTOR_COMPONENTS の応答 JSON から CameraComponent スロットを抽出して VP ref を設定する。
    /// Camera が 1 件なら即時適用、複数件ならポップアップで選択させる。
    /// </summary>
    private void ResolveCameraRefFromComponents(string json)
    {
        // pending フィールドをローカルに退避してからリセットする（再帰ガード）
        var pendingDfsId   = _pendingVpRefActorDfsId;
        var canvasSlotIdx  = _pendingVpRefCanvasSlotIdx;
        var displayLabel   = _pendingVpRefDisplayLabel;
        _pendingVpRefActorDfsId    = -1;
        _pendingVpRefCanvasSlotIdx = -1;
        _pendingVpRefDisplayLabel  = null;

        if (displayLabel is null || _runtime is null || _currentActorId < 0) return;

        string actorName;
        var cameraSlots = new List<(int slotIdx, string slotName)>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            actorName = root.TryGetProperty("name", out var np) ? np.GetString() ?? "" : $"Actor#{pendingDfsId}";

            if (root.TryGetProperty("components", out var comps))
            {
                foreach (var comp in comps.EnumerateArray())
                {
                    var compType = comp.TryGetProperty("type", out var ct) ? ct.GetString() ?? "" : "";
                    if (compType != "CameraComponent") continue;
                    var slotIdx  = comp.TryGetProperty("slot", out var si) ? si.GetInt32()    : 0;
                    var slotName = comp.TryGetProperty("name", out var cn) ? cn.GetString() ?? $"Camera[{slotIdx}]" : $"Camera[{slotIdx}]";
                    cameraSlots.Add((slotIdx, slotName));
                }
            }
        }
        catch (Exception ex)
        {
            EditorLog.Write($"InspectorPanel: VP ref resolve error: {ex.Message}");
            return;
        }

        if (cameraSlots.Count == 0)
        {
            MessageBox.Show(
                "選択されたアクターには CameraComponent がありません。",
                "参照設定エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // VP ref を確定して IPC 送信・ラベル更新するローカル関数
        void Apply(string slotName)
        {
            _runtime.SendToRuntime(
                $"SET_CANVAS_VIEWPORT_REF_CAMERA:{_currentActorId},{canvasSlotIdx},{actorName},{slotName}");
            displayLabel.Text = $"{actorName} / {slotName}";
        }

        if (cameraSlots.Count == 1)
        {
            // Camera が 1 件のみ → 即時適用
            Apply(cameraSlots[0].slotName);
        }
        else
        {
            // Camera が複数 → ポップアップで選択させる
            ShowCameraSlotSelectionPopup(cameraSlots.Select(s => s.slotName).ToList(), Apply);
        }
    }

    /// <summary>
    /// 複数 CameraComponent がある場合のスロット選択ポップアップを表示する。
    /// 選択確定後に onSelected コールバックを呼ぶ。
    /// </summary>
    private void ShowCameraSlotSelectionPopup(List<string> slotNames, Action<string> onSelected)
    {
        var dlg = new Window
        {
            Title                 = "Camera スロットを選択",
            Width                 = 300,
            SizeToContent         = SizeToContent.Height,
            Owner                 = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background            = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            ResizeMode            = ResizeMode.NoResize,
        };

        var sp = new StackPanel { Margin = new Thickness(12, 12, 12, 12) };

        sp.Children.Add(new TextBlock
        {
            Text         = "同一アクター内に複数の CameraComponent があります。\n参照するスロットを選択してください。",
            Foreground   = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 8),
        });

        var listBox = new ListBox
        {
            Background  = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Foreground  = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            MaxHeight   = 150,
            Margin      = new Thickness(0, 0, 0, 8),
        };
        foreach (var name in slotNames)
            listBox.Items.Add(new ListBoxItem
            {
                Content    = name,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            });
        listBox.SelectedIndex = 0;
        sp.Children.Add(listBox);

        var btnOk = new Button
        {
            Content             = "選択",
            Padding             = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        sp.Children.Add(btnOk);

        // 確定処理（ボタン・ダブルクリック共通）
        void Confirm()
        {
            if (listBox.SelectedItem is ListBoxItem item && item.Content is string selected)
            {
                onSelected(selected);
                dlg.Close();
            }
        }

        btnOk.Click              += (_, _) => Confirm();
        listBox.MouseDoubleClick += (_, _) => Confirm();
        // Enter キーで確定
        dlg.KeyDown += (_, e) => { if (e.Key == Key.Return || e.Key == Key.Enter) { Confirm(); e.Handled = true; } };

        dlg.Content = sp;
        dlg.ShowDialog();
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
    private static (UIElement element, TextBox textBox) BuildLabeledNumberRow(string label, float value, string format = "F1")
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
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
        }));
        sp.Children.Add(fieldSection);
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

    private Border BuildComponentChip(int slotIdx, string name, string typeName)
    {
        var isSelected = slotIdx == _selectedSlotIdx;

        var chip = new Border
        {
            Background      = isSelected
                ? new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x3A))
                : new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            BorderBrush     = isSelected
                ? new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(3),
            Margin          = new Thickness(4, 2, 4, 2),
            Padding         = new Thickness(6, 4, 6, 4),
            Cursor          = Cursors.Hand,
            Tag             = slotIdx,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var typeIcon = new TextBlock
        {
            Text              = typeName == "ModelComponent" ? "◈" : "⬡",
            Foreground        = new SolidColorBrush(Color.FromRgb(0x55, 0xAA, 0xFF)),
            FontSize          = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 4, 0),
        };

        var nameBlock = new TextBlock
        {
            Text              = name,
            Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var innerStack = new StackPanel { Orientation = Orientation.Horizontal };
        innerStack.Children.Add(typeIcon);
        innerStack.Children.Add(nameBlock);
        Grid.SetColumn(innerStack, 0);
        grid.Children.Add(innerStack);

        var removeBtn = new TextBlock
        {
            Text              = "✕",
            Foreground        = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontSize          = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor            = Cursors.Hand,
            Padding           = new Thickness(4, 2, 0, 2),
            Tag               = slotIdx,
        };
        removeBtn.MouseEnter += (_, _) =>
            removeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66));
        removeBtn.MouseLeave += (_, _) =>
            removeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        removeBtn.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_currentActorId >= 0)
                _runtime?.SendToRuntime($"REMOVE_COMPONENT:{_currentActorId},{slotIdx}");
        };
        Grid.SetColumn(removeBtn, 1);
        grid.Children.Add(removeBtn);

        chip.Child = grid;

        chip.MouseLeftButtonDown += (_, _) =>
        {
            _selectedSlotIdx = slotIdx;
            RefreshChipSelection();
        };

        chip.MouseRightButtonDown += (_, e) =>
        {
            _selectedSlotIdx = slotIdx;
            RefreshChipSelection();
            ShowComponentChipContextMenu(chip, slotIdx, name);
            e.Handled = true;
        };

        return chip;
    }

    private void RefreshChipSelection()
    {
        foreach (var child in ComponentListStack.Children)
        {
            if (child is not Border b || b.Tag is not int idx) continue;
            bool sel     = idx == _selectedSlotIdx;
            b.Background  = sel
                ? new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x3A))
                : new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
            b.BorderBrush = sel
                ? new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
        }
    }

    private void ShowComponentChipContextMenu(UIElement target, int slotIdx, string currentName)
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
            (_, _) =>
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
            }));
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

    /// <param name="currentName">Runtime 上の現在の名前（変更判定の基準値）。</param>
    /// <param name="initialText">TextBox に初期表示するテキスト。null のとき currentName を使う。</param>
    private void StartComponentRename(int slotIdx, string currentName, string? initialText = null)
    {
        // 該当チップを TextBox に差し替える
        foreach (var child in ComponentListStack.Children)
        {
            if (child is not Border b || b.Tag is not int idx || idx != slotIdx) continue;

            var committed = false;
            var tb = new TextBox
            {
                // initialText が指定されていれば TextBox にはそちらを表示する。
                // 変更判定（RENAME_COMPONENT_SLOT を送るかどうか）は currentName との比較で行う。
                Text              = initialText ?? currentName,
                Background        = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
                Foreground        = Brushes.White,
                CaretBrush        = Brushes.White,
                BorderBrush       = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)),
                BorderThickness   = new Thickness(1),
                Padding           = new Thickness(4, 3, 4, 3),
                FontSize          = 12,
                Margin            = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };

            void Commit()
            {
                if (committed) return;
                committed = true;
                var newName = tb.Text.Trim();
                if (!string.IsNullOrEmpty(newName) && newName != currentName && _currentActorId >= 0)
                {
                    // Rust パーサーは "RENAME_COMPONENT:" プレフィックスで処理する。
                    // "_SLOT" を付けると先頭一致で誤マッチするが数値パースが失敗しコマンドが破棄される。
                    _runtime?.SendToRuntime($"RENAME_COMPONENT:{_currentActorId},{slotIdx},{newName}");
                }
                else if (_currentActorId >= 0)
                {
                    // 名前変更なし（キャンセルと同等）: GET で UI をリフレッシュしてチップを復元する。
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

            b.Child = tb;
            Dispatcher.BeginInvoke(() => { tb.Focus(); tb.SelectAll(); },
                System.Windows.Threading.DispatcherPriority.Input);
            return;
        }
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
                // CanvasComponent は 1 アクターにつき 1 つのみ許可する
                if (_copiedSlot.TypeId == "CanvasComponent" &&
                    _slotInfos.Any(s => s.TypeId == "CanvasComponent"))
                {
                    MessageBox.Show(
                        "CanvasComponent は 1 アクターにつき 1 つのみ追加できます。",
                        "追加不可", MessageBoxButton.OK, MessageBoxImage.Warning);
                    e.Handled = true;
                    return;
                }
                _pendingDuplicateRename   = true;
                _pendingDuplicateBaseName = _copiedSlot.Name;
                _runtime?.SendToRuntime($"DUPLICATE_COMPONENT:{_currentActorId},{_copiedSlot.SlotIdx}");
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
        float pivx, float pivy, float ancX, float ancY)
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
