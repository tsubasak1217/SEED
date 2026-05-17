using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SEEDEditor.Runtime;
using SEEDEditor.Scripting;

namespace SEEDEditor.Panels;

public partial class InspectorPanel : UserControl
{
    // ── Runtime connection ────────────────────────────────────
    private RuntimeManager? _runtime;
    private int             _currentActorId = -1;

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
    // Key: "{actorId}:{slotIdx}", Value: {fieldName → valueString}
    private readonly Dictionary<string, Dictionary<string, string>> _scriptFieldValues = new();
    private readonly Dictionary<string, Type> _scriptTypeCache = new();
    private string _lastComponentsJson = "";

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

    public void SetRuntime(RuntimeManager runtime)
    {
        if (_runtime is not null)
        {
            _runtime.SelectionChanged       -= OnSelectionChanged;
            _runtime.ActorDataReceived      -= OnActorDataReceived;
            _runtime.ActorComponentsReceived -= OnActorComponentsReceived;
        }
        _runtime = runtime;
        _runtime.SelectionChanged       += OnSelectionChanged;
        _runtime.ActorDataReceived      += OnActorDataReceived;
        _runtime.ActorComponentsReceived += OnActorComponentsReceived;
    }

    public void SetActorEditMode(bool isActorMode)
    {
        _isActorEditMode = isActorMode;
        ShowNoSelection();
    }

    /// <summary>HierarchyPanel からアクター編集モードのアクター選択を受け取る。</summary>
    public void SelectActor(int dfsId)
    {
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
        _runtime?.SendToRuntime($"GET_ACTOR_COMPONENTS:{dfsId}");
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
            // トランスフォームドラッグ中は UI を再構築しない。
            // Rust が CommitTransform のたびに send_actor_components を返すため、
            // UIリビルドで _tbPx 等の参照がクリアされて以降のドラッグ値送信が
            // 無効になってしまうのを防ぐ。
            if (_isDraggingTransform) return;
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
        // SpriteComponent 用フィールド
        string TexturePath = "",
        float SpriteR = 1f, float SpriteG = 1f, float SpriteB = 1f, float SpriteA = 1f,
        float SpriteW = 100f, float SpriteH = 100f);
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
            // SpriteComponent 用: テクスチャパス・RGBA・サイズ
            var texPath = comp.TryGetProperty("texture_path", out var tp2) ? tp2.GetString() ?? "" : "";
            var sprR = comp.TryGetProperty("cr", out var cr) ? cr.GetSingle() : 1f;
            var sprG = comp.TryGetProperty("cg", out var cg) ? cg.GetSingle() : 1f;
            var sprB = comp.TryGetProperty("cb", out var cb) ? cb.GetSingle() : 1f;
            var sprA = comp.TryGetProperty("ca", out var ca) ? ca.GetSingle() : 1f;
            var sprW = comp.TryGetProperty("sprite_w", out var sw) ? sw.GetSingle() : 100f;
            var sprH = comp.TryGetProperty("sprite_h", out var sh) ? sh.GetSingle() : 100f;

            var info = new SlotInfo(slotIdx, compName, compType, modelPath, width, height,
                scaleTransform, scaleSize, autoScale,
                texPath, sprR, sprG, sprB, sprA, sprW, sprH);
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

        // ── ヘッダー ──────────────────────────────────────────────
        var header = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
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
        if (slotIdx >= 0 && !string.IsNullOrEmpty(typeId))
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

        // タイトル
        var titleBlock = new TextBlock
        {
            Text              = title,
            Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
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
            "ModelComponent"  => BuildModelSlotContent(info),
            "ScriptComponent" => BuildScriptSlotContent(info),
            "CanvasComponent" => BuildCanvasSlotContent(info),
            "SpriteComponent" => BuildSpriteSlotContent(info),
            _ => new TextBlock
            {
                Text       = $"未対応のコンポーネント: {info.TypeId}",
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize   = 11,
                Margin     = new Thickness(0, 4, 0, 4),
            },
        };

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
                _runtime?.SendToRuntime($"SET_SPRITE_PATH:{_currentActorId},{info.SlotIdx},{path}");
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
                Color.FromArgb((byte)(curA * 255), (byte)(curR * 255), (byte)(curG * 255), (byte)(curB * 255))),
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
                Color.FromArgb((byte)(curA * 255), (byte)(curR * 255), (byte)(curG * 255), (byte)(curB * 255))),
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
                Color.FromArgb((byte)(curA * 255), (byte)(curR * 255), (byte)(curG * 255), (byte)(curB * 255)));
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
        var scaleSep = new TextBlock
        {
            Text       = "スケールモード",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize   = 10,
            Margin     = new Thickness(0, 6, 0, 2),
        };
        sp.Children.Add(scaleSep);

        // チェックボックス: トランスフォームをスケールする
        var cbTransform = new CheckBox
        {
            Content             = "トランスフォームをスケールする",
            IsChecked           = info.ScaleTransform,
            Foreground          = new SolidColorBrush(Colors.White),
            FontSize            = 11,
            Margin              = new Thickness(0, 2, 0, 2),
            VerticalAlignment   = VerticalAlignment.Center,
        };
        sp.Children.Add(cbTransform);

        // チェックボックス: UIサイズをスケールする
        var cbSize = new CheckBox
        {
            Content             = "UIサイズをスケールする",
            IsChecked           = info.ScaleSize,
            Foreground          = new SolidColorBrush(Colors.White),
            FontSize            = 11,
            Margin              = new Thickness(0, 2, 0, 8),
            VerticalAlignment   = VerticalAlignment.Center,
        };
        sp.Children.Add(cbSize);

        // ── 自動スケール セクション ──────────────────────────
        var autoScaleSep = new TextBlock
        {
            Text       = "画面対応",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize   = 10,
            Margin     = new Thickness(0, 2, 0, 2),
        };
        sp.Children.Add(autoScaleSep);

        // チェックボックス: 画面サイズに自動スケール（ルートキャンバスのみ有効）
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
        sp.Children.Add(cbAutoScale);

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

        tbW.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitSize(); e.Handled = true; } };
        tbW.LostFocus += (_, _) => CommitSize();
        tbH.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { CommitSize(); e.Handled = true; } };
        tbH.LostFocus += (_, _) => CommitSize();

        cbTransform.Checked   += (_, _) => CommitScaleMode();
        cbTransform.Unchecked += (_, _) => CommitScaleMode();
        cbSize.Checked        += (_, _) => CommitScaleMode();
        cbSize.Unchecked      += (_, _) => CommitScaleMode();

        cbAutoScale.Checked   += (_, _) => CommitAutoScale();
        cbAutoScale.Unchecked += (_, _) => CommitAutoScale();

        return sp;
    }

    /// <summary>ラベル + 数値入力フィールドの行を生成する。</summary>
    private static (UIElement element, TextBox textBox) BuildLabeledNumberRow(string label, float value)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
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

        var initText = value.ToString("F1", CultureInfo.InvariantCulture);
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

        var scriptType = GetOrCompileScript(info.ModelPath);
        if (scriptType is null) return sp;

        var fields = ScriptCompiler.GetSerializeFields(scriptType);
        if (fields.Count == 0) return sp;

        var storeKey = $"{_currentActorId}:{info.SlotIdx}";
        if (!_scriptFieldValues.ContainsKey(storeKey))
            _scriptFieldValues[storeKey] = new Dictionary<string, string>();
        var values = _scriptFieldValues[storeKey];

        // フィールドセクション
        var fieldSection = BuildSection("フィールド");
        var fieldSp = (StackPanel)fieldSection.Child;
        fieldSp.Children.Add(ScriptInspectorBuilder.Build(fields, values, (name, val) =>
        {
            values[name] = val;
        }));
        sp.Children.Add(fieldSection);

        return sp;
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
        _runtime?.SendToRuntime($"SET_MODEL_PATH:{_currentActorId},{slotIdx},{path}");
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
        var win = new ComponentSelectorWindow(_runtime, _currentActorId, _isActor2D, disabledTypes)
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
        tb.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!tb.IsKeyboardFocusWithin)
            {
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
