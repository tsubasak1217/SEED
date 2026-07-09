using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SEEDEditor.Runtime;

namespace SEEDEditor.Panels;

// ============================================================
//  データモデル
// ============================================================

public class ActorNode
{
    public int             Id       { get; set; }
    public string          Name     { get; set; } = "";
    public int?            ParentId { get; set; }
    public bool            IsGroup  { get; set; }
    /// <summary>2D アクター（CanvasTransform）か否か。Hierarchy アイコン色分けに使用する。</summary>
    public bool            Is2D     { get; set; }
    /// <summary>
    /// ビューポート所属か否か。トップレベルのルートアクターが Actor2D
    /// （スクリーンスペースキャンバス系）のサブツリーに属するとき true。
    /// 3D ワールドキャンバス配下の 2D スプライトは Is2D=true でも IsVp=false。
    /// シーンタブ（ワールド/ビューポート）の自動切替判定に使用する。
    /// </summary>
    public bool            IsVp     { get; set; }
    /// <summary>実効アクティブか（自身と全祖先の active が true）。false は淡色表示する。</summary>
    public bool            Active   { get; set; } = true;
    public List<ActorNode> Children { get; } = new();
}

// ============================================================
//  HierarchyPanel
// ============================================================

public partial class HierarchyPanel : UserControl
{
    // ── 状態 ─────────────────────────────────────────────────

    private RuntimeManager? _runtime;
    private string          _assetsPath = "";
    private List<ActorNode> _roots      = new();
    private int             _selectedId = -1;
    private bool            _suppressSelectionEvent;

    // ドラッグ用
    private Point         _dragStart;
    private bool          _isDragging;
    private List<int>     _dragNodeIds = new();
    private TreeViewItem? _dropTarget;   // 子として追加するターゲット
    private bool          _dropAsRoot;
    private TreeViewItem? _insertTarget; // 兄弟として挿入するターゲット
    private bool          _insertBefore; // insertTarget の前 or 後

    // ドラッグ中オートスクロール用
    /// <summary>この距離（px）以内へカーソルが入ったら自動スクロールを開始する上下端マージン。</summary>
    private const double AutoScrollEdgeMargin = 24.0;
    /// <summary>オートスクロール 1 ティックあたりの移動量（px）。</summary>
    private const double AutoScrollStep = 16.0;
    /// <summary>オートスクロールの実行間隔（ミリ秒）。</summary>
    private const int    AutoScrollIntervalMs = 30;
    private DispatcherTimer? _autoScrollTimer;
    private int               _autoScrollDirection; // -1=上, 0=停止, +1=下
    private ScrollViewer?     _treeScrollViewerCache;

    // リネーム用
    private DispatcherTimer? _renameTimer;
    private int              _pendingRenameId = -1;

    // 右クリック用
    private ActorNode? _rightClickedNode;

    // グループ作成後リネーム用
    private string? _pendingRenameGroupName;

    // 複数選択用
    private HashSet<int> _selectedIds = new();
    private int          _anchorId    = -1;

    // アクター編集モードで使用する仮想アクターノードの ID 下限値
    // Rust 側の do_send_hierarchy と合わせること
    private const int VirtualActorNodeIdBase = 999_000_000;

    // アクター編集モード
    private bool         _isActorEditMode          = false;
    private bool         _isActor2DMode            = false;  // 編集中アクターが 2D Actor かどうか
    private uint         _activeWorldLine           = 0;
    private bool         _pendingActorRenameAfterAdd = false;
    private HashSet<int> _preAddNodeIds             = new();

    // 複数選択時の遅延デセレクト（クリックのみでデセレクト、ドラッグはキープ）
    private bool _pendingDeselect;
    private int  _pendingDeselectId = -1;

    // D&D: MouseDown で確定したドラッグ対象（コンテキストメニュー誤操作防止）
    private ActorNode? _pendingDragNode;

    // ドラッグ中の Inspector 切り替え抑制:
    // MouseDown でフラグを立て、MouseUp（ドラッグなし）またはドラッグ完了後に SendSelectionToRuntime を実行する。
    private bool _deferRuntimeSelection = false;

    // キャッシュ
    private static readonly SolidColorBrush BrushGroupIcon   = MakeFrozen(Color.FromRgb(0xFF, 0xCC, 0x44));
    /// <summary>3D アクターのアイコン色（青系）。</summary>
    private static readonly SolidColorBrush BrushActorIcon   = MakeFrozen(Color.FromRgb(0x55, 0xAA, 0xFF));
    /// <summary>2D アクターのアイコン色（オレンジ系）。</summary>
    private static readonly SolidColorBrush BrushActor2DIcon = MakeFrozen(Color.FromRgb(0xFF, 0x88, 0x44));

    private static SolidColorBrush MakeFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }


    // ============================================================

    public HierarchyPanel()
    {
        InitializeComponent();
        ActorTree.PreviewKeyDown            += OnTreeKeyDown;
        ActorTree.PreviewMouseLeftButtonUp  += OnTreeMouseUp;
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && _selectedId >= 0)
        {
            e.Handled = true;
            CancelRenameTimer();
            StartRename(_selectedId);
        }
    }

    // ── アクター編集モード ────────────────────────────────────

    /// <summary>アクター編集モードでアクターが選択されたときに発火する（DFS ID）。</summary>
    public event Action<int>? ActorDfsSelected;

    /// <summary>
    /// 選択アクターのワールド/ビューポート所属が一意に定まったときに発火する
    /// （true = ビューポート所属）。
    /// 所属は Is2D ではなく IsVp（トップレベルルートが Actor2D か）で判定するため、
    /// 3D ワールドキャンバス配下の 2D スプライトはワールド所属として扱われる。
    /// 所属の混在複数選択やグループのみの選択では発火しない。
    /// MainWindow がシーンタブ（ワールド/ビューポート）の自動切替に使用する。
    /// </summary>
    public event Action<bool>? SelectionKindResolved;

    /// <summary>
    /// 現在の選択（_selectedIds）の非グループノードのワールド/ビューポート所属を判定し、
    /// 一意に定まる場合のみ SelectionKindResolved を発火する。
    /// </summary>
    private void NotifySelectionKind()
    {
        bool anyViewport = false;
        bool anyWorld    = false;
        foreach (var id in _selectedIds)
        {
            var node = FindNode(_roots, id);
            // ツリーに存在しないノードやグループは所属判定の対象外
            if (node == null || node.IsGroup) continue;
            if (node.IsVp) anyViewport = true; else anyWorld = true;
        }
        // 混在（両方 true）または実アクター選択なし（両方 false）は通知しない
        if (anyViewport == anyWorld) return;
        SelectionKindResolved?.Invoke(anyViewport);
    }

    /// <summary>
    /// 次のヒエラルキー更新後に追加されたアクターをリネームモードにする準備をする。
    /// ビューポートコンテキストメニューなど、アクター編集モード外から
    /// アクタを追加する場合に呼び出す。
    /// </summary>
    public void PrepareRenameAfterAdd()
    {
        _preAddNodeIds             = GetAllNodes(_roots).Select(n => n.Id).ToHashSet();
        _pendingActorRenameAfterAdd = true;
    }

    /// <summary>
    /// アクター編集モードの切り替え。
    /// is2D に true を渡すと、アクタ追加コマンドが ADD_ACTOR_2D に切り替わる。
    /// </summary>
    public void SetActorEditMode(bool isActorMode, uint worldLine = 0, bool is2D = false)
    {
        _isActorEditMode = isActorMode;
        _isActor2DMode   = is2D;
        _activeWorldLine = worldLine;
        ActorToolbar.Visibility = isActorMode ? Visibility.Visible : Visibility.Collapsed;
        ActorToolbarRow.Height  = isActorMode ? new GridLength(28) : new GridLength(0);
        SearchBarRow.Height     = isActorMode ? new GridLength(0)  : new GridLength(28);
    }

    private void OnAddActorClicked(object sender, RoutedEventArgs e)
    {
        if (_runtime is null) return;
        var parentId = _isActorEditMode && _selectedId >= 0 ? _selectedId : -1;
        if (_isActorEditMode)
        {
            _pendingActorRenameAfterAdd = true;
            _preAddNodeIds = GetAllNodes(_roots).Select(n => n.Id).ToHashSet();
        }
        // 2D アクター編集中は ADD_ACTOR_2D を使う
        var cmd = _isActor2DMode ? "ADD_ACTOR_2D" : "ADD_ACTOR";
        _runtime.SendToRuntime($"{cmd}:{_activeWorldLine},{parentId}");
    }

    // ── ランタイム接続 ────────────────────────────────────────

    /// <summary>アセットルートパスを設定する。アクタファイル保存ダイアログの初期ディレクトリに使用する。</summary>
    public void SetAssetsPath(string assetsPath) => _assetsPath = assetsPath;

    public void SetRuntime(RuntimeManager runtime)
    {
        if (_runtime is not null)
        {
            _runtime.HierarchyUpdated      -= OnHierarchyUpdated;
            _runtime.SelectionChanged      -= OnSelectionChanged;
            _runtime.SelectionMultiChanged -= OnSelectionMultiChanged;
        }
        _runtime = runtime;
        _runtime.HierarchyUpdated      += OnHierarchyUpdated;
        _runtime.SelectionChanged      += OnSelectionChanged;
        _runtime.SelectionMultiChanged += OnSelectionMultiChanged;
    }

    // ── Runtime イベント ──────────────────────────────────────

    private void OnHierarchyUpdated(string json)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _roots = ParseHierarchy(json);
            _selectedIds.Clear();
            if (_selectedId >= 0) _selectedIds.Add(_selectedId);
            _anchorId = _selectedId;
            RebuildTree(_roots);

            // グループ作成直後のリネーム
            if (_pendingRenameGroupName != null)
            {
                var name = _pendingRenameGroupName;
                _pendingRenameGroupName = null;
                var node = GetAllNodes(_roots).FirstOrDefault(n => n.Name == name && n.IsGroup);
                if (node != null)
                    Dispatcher.BeginInvoke(() => StartRename(node.Id), DispatcherPriority.Background);
            }

            // アクター追加直後のリネーム（アクター編集モード・シーンモード共通）
            if (_pendingActorRenameAfterAdd)
            {
                _pendingActorRenameAfterAdd = false;
                var newNode = GetAllNodes(_roots).FirstOrDefault(n => !_preAddNodeIds.Contains(n.Id));
                if (newNode != null)
                {
                    _selectedId = newNode.Id;
                    _selectedIds.Clear();
                    _selectedIds.Add(newNode.Id);
                    SelectTreeItem(newNode.Id);
                    ActorDfsSelected?.Invoke(newNode.Id);
                    _runtime?.SendToRuntime($"SELECT:{999_000_000u + (uint)newNode.Id}");
                    Dispatcher.BeginInvoke(() => StartRename(newNode.Id), DispatcherPriority.Background);
                }
            }
        });
    }

    private void OnSelectionChanged(int idx)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (idx < 0)
            {
                // 空クリック: 選択解除
                _selectedId = -1;
                _selectedIds.Clear();
                _anchorId = -1;
                DeselectAll();
                UpdateMultiSelectVisuals();
                return;
            }

            if (idx >= VirtualActorNodeIdBase)
            {
                // Viewport からアクターツリーノードが選択された（シーンモード・アクター編集モード共通）
                var dfsId = idx - VirtualActorNodeIdBase;
                _selectedId = dfsId;
                _selectedIds.Clear();
                _selectedIds.Add(dfsId);
                _anchorId = dfsId;
                SelectTreeItem(dfsId);
                UpdateMultiSelectVisuals();
                ActorDfsSelected?.Invoke(dfsId);
                // ビューポートピック由来の選択でも種別通知を行う（シーンタブ自動切替用）
                NotifySelectionKind();
                return;
            }

            // レガシー: インスタンスインデックス直接選択（アクター編集モード以外の後方互換）
            _selectedId = idx;
            _selectedIds.Clear();
            _selectedIds.Add(idx);
            SelectTreeItem(idx);
            _anchorId = idx;
            UpdateMultiSelectVisuals();
        });
    }

    private void OnSelectionMultiChanged(IReadOnlyList<int> ids)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _selectedIds.Clear();
            foreach (var id in ids)
            {
                // 仮想 ID（999_000_000 + dfs）はデコードして DFS ID として保存する
                var dfsId = id >= VirtualActorNodeIdBase ? id - VirtualActorNodeIdBase : id;
                _selectedIds.Add(dfsId);
            }
            _selectedId = _selectedIds.Count > 0 ? _selectedIds.First() : -1;
            _anchorId   = _selectedId;
            if (_selectedId >= 0)
                SelectTreeItem(_selectedId);
            else
                DeselectAll();
            UpdateMultiSelectVisuals();
            // ビューポートの複数選択でも全アクターの種別が一致すれば通知する
            NotifySelectionKind();
        });
    }

    // ── JSON パース ───────────────────────────────────────────

    private static List<ActorNode> ParseHierarchy(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var nodes = doc.RootElement.EnumerateArray()
                .Select(e => new ActorNode
                {
                    Id       = e.GetProperty("id").GetInt32(),
                    Name     = e.GetProperty("name").GetString() ?? $"Actor_{e.GetProperty("id").GetInt32()}",
                    ParentId = e.GetProperty("parent").ValueKind == JsonValueKind.Null
                                ? null
                                : e.GetProperty("parent").GetInt32(),
                    IsGroup  = e.TryGetProperty("is_group", out var ig) && ig.GetBoolean(),
                    Is2D     = e.TryGetProperty("is_2d",    out var i2) && i2.GetBoolean(),
                    // ビューポート所属（ルートが Actor2D のサブツリー）。タブ自動切替用。
                    IsVp     = e.TryGetProperty("is_vp",    out var iv) && iv.GetBoolean(),
                    // 実効アクティブ（省略時は true = アクティブ扱い）
                    Active   = !e.TryGetProperty("active",   out var ac) || ac.GetBoolean(),
                })
                .ToList();

            var map   = nodes.ToDictionary(n => n.Id);
            var roots = new List<ActorNode>();
            foreach (var node in nodes)
            {
                if (node.ParentId is int pid && map.TryGetValue(pid, out var parent))
                    parent.Children.Add(node);
                else
                    roots.Add(node);
            }

            // グループが末尾にならないよう、各レベルを「最小インスタンス ID」でソートする。
            // 非グループは自身の ID、グループは子孫インスタンスの最小 ID をキーとする。
            static int MinKey(ActorNode n)
            {
                if (!n.IsGroup) return n.Id;
                var min = int.MaxValue;
                foreach (var c in n.Children) min = Math.Min(min, MinKey(c));
                return min < int.MaxValue ? min : n.Id;
            }
            foreach (var node in nodes)
                node.Children.Sort((a, b) => MinKey(a).CompareTo(MinKey(b)));
            roots.Sort((a, b) => MinKey(a).CompareTo(MinKey(b)));

            return roots;
        }
        catch { return new List<ActorNode>(); }
    }

    // ── ツリー構築 ────────────────────────────────────────────

    private void RebuildTree(List<ActorNode> roots)
    {
        var filter = TxtSearch.Text.Trim();
        ActorTree.Items.Clear();
        foreach (var root in roots)
        {
            var item = BuildTreeItem(root, filter);
            if (item != null) ActorTree.Items.Add(item);
        }
        if (_selectedId >= 0) SelectTreeItem(_selectedId);
    }

    private TreeViewItem? BuildTreeItem(ActorNode node, string filter)
    {
        if (!string.IsNullOrEmpty(filter) &&
            !node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
            node.Children.Count == 0)
            return null;

        var item = new TreeViewItem { Tag = node, IsExpanded = true };
        item.Header   = BuildItemHeader(node);
        item.Selected += OnItemSelected;

        foreach (var child in node.Children)
        {
            var childItem = BuildTreeItem(child, filter);
            if (childItem != null) item.Items.Add(childItem);
        }
        return item;
    }

    private static TextBlock BuildItemHeader(ActorNode node)
    {
        var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        // IsGroup: 黄色 ▶、2D アクター: オレンジ ◆、3D アクター: 青 ◆
        var iconBrush = node.IsGroup ? BrushGroupIcon
                      : node.Is2D   ? BrushActor2DIcon
                      :               BrushActorIcon;
        tb.Inlines.Add(new Run(node.IsGroup ? "▶ " : "◆ ")
        {
            Foreground = iconBrush,
            FontSize   = 9,
        });
        tb.Inlines.Add(new Run(node.Name) { FontSize = 13 });
        // 非アクティブ（自身または祖先の active が false）は Unity 風に淡色表示する
        if (!node.Active) tb.Opacity = 0.45;
        return tb;
    }

    // ── 選択 ─────────────────────────────────────────────────

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressSelectionEvent) return;
        if (ActorTree.SelectedItem is TreeViewItem { Tag: ActorNode node })
        {
            _selectedId = node.Id;
            if (!_selectedIds.Contains(node.Id))
            {
                _selectedIds.Clear();
                _selectedIds.Add(node.Id);
                _anchorId = node.Id;
            }
            UpdateMultiSelectVisuals();
            // ドラッグの可能性があるため MouseUp まで遅延する場合は送信しない
            if (!_deferRuntimeSelection)
                SendSelectionToRuntime();
        }
    }

    private void OnItemSelected(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_suppressSelectionEvent) return;
        if (sender is TreeViewItem { Tag: ActorNode node })
        {
            _selectedId = node.Id;
            if (!_selectedIds.Contains(node.Id))
            {
                _selectedIds.Clear();
                _selectedIds.Add(node.Id);
                _anchorId = node.Id;
            }
            UpdateMultiSelectVisuals();
            // ドラッグの可能性があるため MouseUp まで遅延する場合は送信しない
            if (!_deferRuntimeSelection)
                SendSelectionToRuntime();
        }
    }

    /// <summary>
    /// _selectedIds に含まれる非グループ ID を Rust へ送信する。
    /// 単一選択は SELECT:、複数選択は SELECT_MULTI:id1,id2,... を使う。
    /// アクター編集モードでは全ノードをアクターとして扱う。
    /// </summary>
    private void SendSelectionToRuntime()
    {
        var tid  = System.Threading.Thread.CurrentThread.ManagedThreadId;
        var isUi = Dispatcher.CheckAccess();
        SEEDEditor.EditorLog.Write($"[Hierarchy.SendSelection] tid={tid} ui={isUi} id={_selectedId} actorMode={_isActorEditMode}");
        // 選択種別（2D/3D）が一意ならシーンタブ自動切替用に通知する
        NotifySelectionKind();
        if (_isActorEditMode)
        {
            if (_selectedIds.Count > 1)
            {
                // アクター編集モード・マルチ選択: 全 DFS id を仮想 ID に変換して送信
                var ids = string.Join(",", _selectedIds.Select(id => (999_000_000u + (uint)id).ToString()));
                _runtime?.SendToRuntime($"SELECT_MULTI:{ids}");
                if (_selectedId >= 0) ActorDfsSelected?.Invoke(_selectedId);
                return;
            }
            if (_selectedId >= 0)
            {
                // アクター編集モード・単一選択: 仮想 ID（DFS + VirtualActorNodeIdBase）で送信
                _runtime?.SendToRuntime($"SELECT:{999_000_000u + (uint)_selectedId}");
                ActorDfsSelected?.Invoke(_selectedId);
            }
            return;
        }

        // シーンモードもアクターツリー表示に統一したため、同じ仮想 ID 方式で送信する
        if (_selectedIds.Count > 1)
        {
            // シーンモード・マルチ選択
            var ids = string.Join(",", _selectedIds.Select(id => (999_000_000u + (uint)id).ToString()));
            _runtime?.SendToRuntime($"SELECT_MULTI:{ids}");
            if (_selectedId >= 0) ActorDfsSelected?.Invoke(_selectedId);
            return;
        }
        if (_selectedId >= 0)
        {
            _runtime?.SendToRuntime($"SELECT:{999_000_000u + (uint)_selectedId}");
            ActorDfsSelected?.Invoke(_selectedId);
        }
    }

    private void SelectTreeItem(int id)
    {
        _suppressSelectionEvent = true;
        try { SelectItemById(ActorTree.Items, id); }
        finally { _suppressSelectionEvent = false; }
    }

    private void DeselectAll()
    {
        _suppressSelectionEvent = true;
        try { DeselectAllItems(ActorTree.Items); }
        finally { _suppressSelectionEvent = false; }
    }

    private static void DeselectAllItems(ItemCollection items)
    {
        foreach (TreeViewItem item in items)
        {
            item.IsSelected = false;
            DeselectAllItems(item.Items);
        }
    }

    private static bool SelectItemById(ItemCollection items, int id)
    {
        foreach (TreeViewItem item in items)
        {
            if (item.Tag is ActorNode node && node.Id == id)
            {
                item.IsSelected = true;
                item.BringIntoView();
                return true;
            }
            if (SelectItemById(item.Items, id)) return true;
        }
        return false;
    }

    // ── 検索 ─────────────────────────────────────────────────

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        RebuildTree(_roots);
    }

    // ── 右クリック / コンテキストメニュー ─────────────────────

    private void OnTreeMouseUp(object sender, MouseButtonEventArgs e)
    {
        // 遅延させていた選択通知を確定する（ドラッグが発生しなかった場合）
        if (_deferRuntimeSelection)
        {
            _deferRuntimeSelection = false;
            if (!_isDragging)
                SendSelectionToRuntime();
        }

        if (!_pendingDeselect) return;
        _pendingDeselect = false;
        var id = _pendingDeselectId;
        _pendingDeselectId = -1;

        _selectedIds.Clear();
        _selectedIds.Add(id);
        _selectedId = id;
        _anchorId   = id;
        SelectTreeItem(id);
        UpdateMultiSelectVisuals();
        SendSelectionToRuntime();
    }

    private void OnTreeRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        // コンテキストメニューを閉じるLMBクリックで誤ドラッグが起きないようにリセット
        _pendingDragNode   = null;
        _pendingDeselect   = false;
        _pendingDeselectId = -1;

        var hit  = ActorTree.InputHitTest(e.GetPosition(ActorTree)) as DependencyObject;
        var item = FindAncestor<TreeViewItem>(hit);
        _rightClickedNode = item?.Tag as ActorNode;

        bool clickedOnSelected = _rightClickedNode != null
            && _selectedIds.Contains(_rightClickedNode.Id);

        // WPF 推奨: ContextMenu プロパティに代入し、WPF に開かせる
        ActorTree.ContextMenu = clickedOnSelected
            ? BuildSelectedContextMenu()
            : BuildEmptyContextMenu();
    }

    private ContextMenu BuildSelectedContextMenu()
    {
        var menu = new ContextMenu();

        // ── アクタを追加 サブメニュー（子として作成）──────────────────
        menu.Items.Add(BuildAddActorSubMenu(asChild: true));
        menu.Items.Add(new Separator());

        if (_isActorEditMode)
        {
            AddMenuItem(menu, "削除", "Del", OnHierarchyDelete);
        }
        else
        {
            AddMenuItem(menu, "コピー",                 "Ctrl+C",    OnHierarchyCopy);
            AddMenuItem(menu, "削除",                   "Del / Esc", OnHierarchyDelete);
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "選択からグループを作成", null,        OnCreateGroupFromSelection);
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "アクタファイル化", null, OnExportActorMenu);
        }
        return menu;
    }

    private ContextMenu BuildEmptyContextMenu()
    {
        var menu = new ContextMenu();

        // ── アクタを追加 サブメニュー（ルートに作成）──────────────────
        menu.Items.Add(BuildAddActorSubMenu(asChild: false));

        if (!_isActorEditMode)
        {
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "グループフォルダを作成", null, OnCreateGroupMenu);
        }
        return menu;
    }

    /// <summary>
    /// 「アクタを追加」サブメニューを生成する。
    /// asChild=true のとき右クリックノードの子として追加、false のときルートに追加する。
    /// </summary>
    private MenuItem BuildAddActorSubMenu(bool asChild)
    {
        var sub = new MenuItem { Header = "アクタを追加" };

        var item3D = new MenuItem { Header = "3D アクタ" };
        item3D.Click += (_, _) =>
        {
            PrepareRenameAfterAdd();
            if (asChild && _rightClickedNode is not null)
                _runtime?.SendToRuntime($"ADD_ACTOR_CHILD:{_rightClickedNode.Id}");
            else
                _runtime?.SendToRuntime($"ADD_ACTOR:{_activeWorldLine},-1");
        };

        var item2D = new MenuItem { Header = "2D アクタ" };
        item2D.Click += (_, _) =>
        {
            PrepareRenameAfterAdd();
            if (asChild && _rightClickedNode is not null)
                _runtime?.SendToRuntime($"ADD_ACTOR_2D_CHILD:{_rightClickedNode.Id}");
            else
                _runtime?.SendToRuntime($"ADD_ACTOR_2D:{_activeWorldLine},-1");
        };

        sub.Items.Add(item3D);
        sub.Items.Add(item2D);
        return sub;
    }

    private void OnAddRootActorMenu(object sender, RoutedEventArgs e)
    {
        PrepareRenameAfterAdd();
        var cmd = _isActor2DMode ? "ADD_ACTOR_2D" : "ADD_ACTOR";
        _runtime?.SendToRuntime($"{cmd}:{_activeWorldLine},-1");
    }

    private void OnHierarchyCopy(object sender, RoutedEventArgs e)
        => _runtime?.SendToRuntime("COPY");

    private void OnHierarchyDelete(object sender, RoutedEventArgs e)
    {
        if (_isActorEditMode)
        {
            // アクター編集モードでは選択アクターを削除
            if (_selectedId < 0) return;
            _runtime?.SendToRuntime($"REMOVE_ACTOR:{_selectedId}");
        }
        else
        {
            var ids = _selectedIds.ToList();
            if (ids.Count == 0) return;
            _runtime?.SendToRuntime($"DELETE_RECURSIVE:{string.Join(",", ids)}");
        }
    }

    private void OnCreateGroupFromSelection(object sender, RoutedEventArgs e)
    {
        // 先頭選択アクターの親をグループの親とする
        var flat = GetFlatItems(ActorTree.Items);
        var firstNode = flat
            .Select(i => i.Tag as ActorNode)
            .FirstOrDefault(n => n != null && _selectedIds.Contains(n.Id));

        int parentId = firstNode?.ParentId ?? -1;

        var name = GetUniqueName("Group", -1);
        _pendingRenameGroupName = name;
        var childIds = string.Join(",", _selectedIds);
        _runtime?.SendToRuntime($"CREATE_GROUP_WITH_CHILDREN:{parentId}|{name}|{childIds}");
    }

    private void OnCreateGroupMenu(object sender, RoutedEventArgs e)
    {
        int parentId = _rightClickedNode?.ParentId ?? -1;
        var name     = GetUniqueName("Group", -1);
        _pendingRenameGroupName = name;
        _runtime?.SendToRuntime($"CREATE_GROUP:{parentId},{name}");
    }

    /// <summary>
    /// ルートに空の Actor を作成し、作成後にインライン名前変更を開始する。
    /// ProjectPanel の + ボタンから呼ぶ。
    /// </summary>
    public void CreateActorAtRoot()
    {
        var name = GetUniqueName("Actor", -1);
        _pendingRenameGroupName = name;
        _runtime?.SendToRuntime($"CREATE_GROUP:-1,{name}");
    }

    /// <summary>右クリック "アクタファイル化" が選択されたときの処理。</summary>
    private void OnExportActorMenu(object sender, RoutedEventArgs e)
    {
        if (_rightClickedNode is null) return;
        SendExportActorCommand(_rightClickedNode.Id, _rightClickedNode.Name, _rightClickedNode.Is2D);
    }

    /// <summary>
    /// 選択中アクターのファイル化ダイアログを開いて保存コマンドを送信する。
    /// ビューポートのコンテキストメニューなど外部から呼び出す場合に使用する。
    /// </summary>
    public void ShowExportActorDialog()
    {
        // プライマリ選択 ID を決定する（単一選択 → _selectedId、複数選択 → 先頭）
        int targetId = _selectedId >= 0 ? _selectedId
                     : _selectedIds.Count > 0 ? _selectedIds.First()
                     : -1;
        if (targetId < 0) return;

        var node = GetAllNodes(_roots).FirstOrDefault(n => n.Id == targetId && !n.IsGroup);
        if (node is null) return;

        SendExportActorCommand(node.Id, node.Name, node.Is2D);
    }

    /// <summary>
    /// Windows 標準の SaveFileDialog でパスを取得し EXPORT_ACTOR コマンドを送信する。
    /// 2D アクターは .actor2d、3D アクターは .actor 拡張子を使用する。
    /// </summary>
    private void SendExportActorCommand(int nodeId, string defaultName, bool is2D)
    {
        var ext    = is2D ? ".actor2d" : ".actor";
        var filter = is2D
            ? "2D Actorファイル (*.actor2d)|*.actor2d|すべてのファイル (*.*)|*.*"
            : "Actorファイル (*.actor)|*.actor|すべてのファイル (*.*)|*.*";

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title            = "アクタファイルの保存",
            FileName         = defaultName,
            DefaultExt       = ext,
            Filter           = filter,
            InitialDirectory = _assetsPath,
        };

        if (dlg.ShowDialog() != true) return;

        _runtime?.SendToRuntime($"EXPORT_ACTOR:{nodeId},{dlg.FileName}");
    }

    private static void AddMenuItem(ContextMenu menu, string header, string? gesture, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header };
        if (gesture != null) item.InputGestureText = gesture;
        item.Click += handler;
        menu.Items.Add(item);
    }

    // ── ドラッグ＆ドロップ ────────────────────────────────────

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart             = e.GetPosition(ActorTree);
        _isDragging            = false;
        _dragNodeIds           = new();
        _pendingDragNode       = null;
        _deferRuntimeSelection = false; // 前回の残りをリセット

        if (e.ClickCount == 2)
        {
            CancelRenameTimer();
            return;
        }

        if (e.ClickCount != 1) return;

        var hit  = ActorTree.InputHitTest(_dragStart) as DependencyObject;
        var item = FindAncestor<TreeViewItem>(hit);
        _pendingDragNode = item?.Tag as ActorNode;

        if (FindAncestor<ToggleButton>(hit) != null)
        {
            CancelRenameTimer();
            _pendingRenameId = -1;
            return;
        }

        bool isCtrl  = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift)   != 0;

        if (item?.Tag is ActorNode node && (isCtrl || isShift))
        {
            // Ctrl/Shift クリック → TreeView のデフォルト選択を抑制して手動管理
            e.Handled = true;
            CancelRenameTimer();
            _pendingRenameId = -1;
            ActorTree.Focus();

            if (isShift && _anchorId >= 0)
            {
                // アンカーからクリック位置までの範囲を選択
                var flat = GetFlatItems(ActorTree.Items);
                int ai   = flat.FindIndex(i => (i.Tag as ActorNode)?.Id == _anchorId);
                int ci   = flat.FindIndex(i => (i.Tag as ActorNode)?.Id == node.Id);
                if (ai >= 0 && ci >= 0)
                {
                    _selectedIds.Clear();
                    int lo = Math.Min(ai, ci), hi = Math.Max(ai, ci);
                    for (int i = lo; i <= hi; i++)
                        if (flat[i].Tag is ActorNode n) _selectedIds.Add(n.Id);
                }
                // アンカーは変えない（連続 Shift 選択のため）
            }
            else if (isCtrl)
            {
                if (_selectedIds.Contains(node.Id))
                    _selectedIds.Remove(node.Id);
                else
                    _selectedIds.Add(node.Id);
                _anchorId = node.Id;
            }

            _selectedId = node.Id;
            SelectTreeItem(node.Id);
            UpdateMultiSelectVisuals();
            SendSelectionToRuntime();
            return;
        }

        // 通常クリック: ドラッグの可能性があるため選択通知を MouseUp まで遅延する。
        // これによりドラッグ中は Inspector が切り替わらず、D&D が完了してから切り替わる。
        if (item?.Tag is ActorNode normalNode)
        {
            _deferRuntimeSelection = true;

            // 複数選択中に選択済みアイテムをクリック → ドラッグしないなら MouseUp でデセレクト
            // この分岐は e.Handled = true で TreeView 選択を抑制するため defer は不要
            if (_selectedIds.Count > 1 && _selectedIds.Contains(normalNode.Id))
            {
                _deferRuntimeSelection = false; // _pendingDeselect 機構に任せる
                _pendingDeselect   = true;
                _pendingDeselectId = normalNode.Id;
                _pendingDragNode   = normalNode;
                e.Handled = true;
                return;
            }

            _selectedIds.Clear();
            _selectedIds.Add(normalNode.Id);
            _anchorId = normalNode.Id;

            if (normalNode.Id == _selectedId)
            {
                _pendingRenameId = normalNode.Id;
                CancelRenameTimer();
                _renameTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500),
                };
                _renameTimer.Tick += (_, _) =>
                {
                    CancelRenameTimer();
                    if (_pendingRenameId >= 0 && !_isDragging)
                        StartRename(_pendingRenameId);
                    _pendingRenameId = -1;
                };
                _renameTimer.Start();
            }
            else
            {
                CancelRenameTimer();
                _pendingRenameId = -1;
            }
        }
        else
        {
            CancelRenameTimer();
            _pendingRenameId = -1;
        }
    }

    private void CancelRenameTimer()
    {
        _renameTimer?.Stop();
        _renameTimer = null;
    }

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _isDragging) return;
        var pos  = e.GetPosition(ActorTree);
        var diff = pos - _dragStart;
        if (Math.Abs(diff.X) < 4 && Math.Abs(diff.Y) < 4) return;

        // _pendingDragNode が null = OnTreeMouseDown が呼ばれていない（コンテキストメニュー誤操作防止）
        if (_pendingDragNode == null) return;

        CancelRenameTimer();
        _pendingRenameId = -1;

        // ドラッグ開始時は遅延デセレクトをキャンセル（複数選択を維持）
        _pendingDeselect   = false;
        _pendingDeselectId = -1;

        _isDragging  = true;
        // 選択中の全ノードをドラッグ。クリックしたノードが選択外なら単独ドラッグ
        _dragNodeIds = _selectedIds.Contains(_pendingDragNode.Id)
            ? _selectedIds.ToList()
            : new List<int> { _pendingDragNode.Id };

        var data = new DataObject("DragIds", _dragNodeIds);
        // VP ref ドロップゾーン用: 単一アクタードラッグ時に DFS ID をカスタムキーで付加する
        if (_dragNodeIds.Count == 1)
            data.SetData("HierarchyActorDfsId", _dragNodeIds[0]);
        DragDrop.DoDragDrop(ActorTree, data, DragDropEffects.Move);

        // ドラッグ完了: 遅延フラグだけクリアして選択通知は送らない。
        // D&D 後に Inspector が切り替わらないようにするためのユーザー仕様。
        _deferRuntimeSelection = false;

        _isDragging  = false;
        _dragNodeIds = new();
        _dropTarget  = null;
        _dropAsRoot   = false;
        _insertTarget = null;
        DropIndicator.Visibility = Visibility.Collapsed;
        ClearDropHighlight();
        // DoDragDrop 復帰時点で DragOver/DragLeave の取りこぼしがあってもここで確実に停止する
        StopAutoScroll();
    }

    private void OnTreeDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("DragIds"))
            e.Effects = DragDropEffects.None;
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("DragIds"))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;

        ClearDropHighlight();
        DropIndicator.Visibility = Visibility.Collapsed;
        _insertTarget = null;
        _dropTarget   = null;
        _dropAsRoot   = false;

        var pos  = e.GetPosition(ActorTree);

        // ドラッグカーソルが上下端マージン内にあれば自動スクロールを開始/継続する。
        // TreeView は表示範囲外へドロップできないため、長いツリーで下（上）へ運べない不具合の対策。
        UpdateAutoScroll(pos.Y);

        var hit  = ActorTree.InputHitTest(pos) as DependencyObject;
        var item = FindAncestor<TreeViewItem>(hit);

        if (item?.Tag is ActorNode targetNode)
        {
            // ドラッグ中のノードのいずれかがターゲット自身または祖先なら無効
            bool invalid = _dragNodeIds.Any(id =>
                id == targetNode.Id ||
                (FindNode(_roots, id) is { } n && IsDescendant(n, targetNode.Id)));
            if (invalid)
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            // アイテム上端・下端 25% → 兄弟挿入ライン表示
            var itemTop = item.TranslatePoint(new Point(0, 0), ActorTree).Y;
            var relY    = pos.Y - itemTop;
            var zone    = item.ActualHeight * 0.25;

            // ドラッグ中のノードに 3D アクター（!Is2D）が含まれるかどうか。
            // 「2D アクターの子に 3D アクターを配置する」組み合わせを弾くための判定に使う。
            bool draggingHas3D = _dragNodeIds.Any(id => FindNode(_roots, id) is { Is2D: false });

            if (relY <= zone || relY >= item.ActualHeight - zone)
            {
                // 兄弟として挿入する場合、実効的な新しい親は targetNode の親ノード
                // （親が無い＝ルート直下の場合は 3D/2D 混在制約の対象外）。
                var effectiveParent = FindParentNode(_roots, targetNode.Id);
                if (draggingHas3D && effectiveParent is { Is2D: true })
                {
                    e.Effects = DragDropEffects.None;
                    return;
                }

                _insertBefore            = relY <= zone;
                _insertTarget            = item;
                var lineY                = _insertBefore ? itemTop : itemTop + item.ActualHeight;
                DropIndicator.Margin     = new Thickness(0, lineY - 1, 0, 0);
                DropIndicator.Visibility = Visibility.Visible;
            }
            else
            {
                // 中央 → 子として追加。実効的な新しい親は targetNode 自身。
                // targetNode が 2D かつドラッグ中に 3D が含まれる場合はドロップ不可とする。
                if (draggingHas3D && targetNode.Is2D)
                {
                    e.Effects = DragDropEffects.None;
                    return;
                }

                _dropTarget = item;
                var border = FindVisualChild<Border>(item, "RowBorder");
                if (border != null)
                    border.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x33, 0x99, 0xFF));
            }
        }
        else
        {
            // アクター編集モードではルートへのドロップを禁止
            if (_isActorEditMode)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            _dropAsRoot              = true;
            DropIndicator.Margin     = new Thickness(0);
            DropIndicator.Visibility = Visibility.Visible;
        }
        e.Handled = true;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
        DropIndicator.Visibility = Visibility.Collapsed;
        _dropTarget   = null;
        _dropAsRoot   = false;
        _insertTarget = null;
        StopAutoScroll();
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
        DropIndicator.Visibility = Visibility.Collapsed;
        StopAutoScroll();

        if (!e.Data.GetDataPresent("DragIds")) return;
        var dragIds = (List<int>)e.Data.GetData("DragIds");

        int newParentId;
        if (_insertTarget?.Tag is ActorNode siblingNode)
            newParentId = siblingNode.ParentId ?? -1;
        else if (_dropTarget?.Tag is ActorNode targetNode)
            newParentId = targetNode.Id;
        else if (_dropAsRoot)
            newParentId = -1;
        else
            return;

        // アクター編集モードではパネル主（ルートアクター）の外に出さない
        if (_isActorEditMode && newParentId == -1)
        {
            newParentId = _roots.Count > 0 ? _roots[0].Id : -1;
            if (newParentId == -1) return;
        }

        // 複数ノードをまとめて親子付け変更
        // 1 つ目はドロップ位置（_insertTarget/_insertBefore）を基準に挿入し、
        // 2 つ目以降は直前に移動したノードの直後へ順に並べる（挿入順を維持する）。
        // Rust 側へは REPARENT:{child},{parent},{anchorSiblingId},{placeBefore} で
        // アンカー兄弟（挿入位置の基準となる DFS ID。-1 = 末尾追加）を明示的に送る。
        // アンカー方式にしているのは、削除に伴う添字ズレを気にせず堅牢に位置指定できるため。
        TreeViewItem? prevMovedItem = null;
        bool first = true;
        foreach (var dragId in dragIds)
        {
            var dragNode = FindNode(_roots, dragId);
            if (dragNode == null) continue;

            int  anchorId;
            bool placeBefore;
            if (first)
            {
                anchorId    = _insertTarget?.Tag is ActorNode initialAnchor ? initialAnchor.Id : -1;
                placeBefore = _insertBefore;
            }
            else
            {
                // 2 つ目以降は直前に移動したノードの直後へ挿入する
                anchorId    = prevMovedItem?.Tag is ActorNode prevAnchor ? prevAnchor.Id : -1;
                placeBefore = false;
            }

            _runtime?.SendToRuntime($"REPARENT:{dragNode.Id},{newParentId},{anchorId},{(placeBefore ? 1 : 0)}");

            if (first)
            {
                ReparentInPlace(dragNode.Id, newParentId == -1 ? null : newParentId);
                first = false;
            }
            else
            {
                // 2 つ目以降はローカル反映も直前ノードの直後へ挿入する（Rust 側と挙動を揃える）
                var savedTarget = _insertTarget;
                var savedBefore = _insertBefore;
                _insertTarget = prevMovedItem;
                _insertBefore = false;
                ReparentInPlace(dragNode.Id, newParentId == -1 ? null : newParentId);
                _insertTarget = savedTarget;
                _insertBefore = savedBefore;
            }

            prevMovedItem = FindTreeItemById(ActorTree.Items, dragNode.Id);
        }

        _dropTarget   = null;
        _dropAsRoot   = false;
        _insertTarget = null;
        e.Handled     = true;
    }

    /// <summary>
    /// _roots と ActorTree をその場で更新する。JSON ラウンドトリップなし。
    /// </summary>
    private void ReparentInPlace(int childId, int? newParentId)
    {
        // 挿入モードのとき、兄弟ノードを先に取得しておく（DetachNode 前に）
        var siblingNode = _insertTarget?.Tag as ActorNode;

        // ── データモデル更新 ──────────────────────────────────────
        var node = DetachNode(_roots, childId);
        if (node == null) return;

        node.ParentId = newParentId;

        if (newParentId is int pid)
        {
            var parentNode = FindNode(_roots, pid);
            if (parentNode == null) { _roots.Add(node); return; }
            InsertIntoList(parentNode.Children, node, siblingNode, _insertBefore);
        }
        else
        {
            InsertIntoList(_roots, node, siblingNode, _insertBefore);
        }

        // ── TreeViewItem 移動 ─────────────────────────────────────
        // DetachTreeItem 前に insertTarget の参照を保持
        var insertNearItem = _insertTarget;

        var childItem = FindTreeItemById(ActorTree.Items, childId);
        if (childItem == null) return;

        DetachTreeItem(ActorTree.Items, childId);

        if (newParentId is int parentId)
        {
            var parentItem = FindTreeItemById(ActorTree.Items, parentId);
            var dest       = parentItem?.Items ?? ActorTree.Items;
            InsertIntoItemCollection(dest, childItem, insertNearItem, _insertBefore);
        }
        else
        {
            InsertIntoItemCollection(ActorTree.Items, childItem, insertNearItem, _insertBefore);
        }
    }

    // リストへの挿入（sibling が見つかれば前後に、見つからなければ末尾）
    private static void InsertIntoList<T>(List<T> list, T node, T? sibling, bool before)
        where T : class
    {
        if (sibling != null)
        {
            int idx = list.IndexOf(sibling);
            if (idx >= 0) { list.Insert(before ? idx : idx + 1, node); return; }
        }
        list.Add(node);
    }

    // ItemCollection への挿入（sibling が見つかれば前後に、見つからなければ末尾）
    private static void InsertIntoItemCollection(
        ItemCollection items, TreeViewItem child,
        TreeViewItem? sibling, bool before)
    {
        if (sibling != null)
        {
            int idx = items.IndexOf(sibling);
            if (idx >= 0) { items.Insert(before ? idx : idx + 1, child); return; }
        }
        items.Add(child);
    }

    // _roots から指定 Id のノードを取り外して返す
    private static ActorNode? DetachNode(List<ActorNode> list, int id)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Id == id) { var n = list[i]; list.RemoveAt(i); return n; }
            var found = DetachNode(list[i].Children, id);
            if (found != null) return found;
        }
        return null;
    }

    // _roots から指定 Id のノードを探す（取り外さない）
    // ── 外部公開ヘルパー ──────────────────────────────────────

    /// 現在選択中の非グループ ID リストを返す。
    /// ノードがツリーに存在しない場合（カメラアクターの仮想ノード等）はアクターとして扱う。
    public List<int> GetSelectedNonGroupIds() =>
        _selectedIds
            .Where(id =>
            {
                var node = FindNode(_roots, id);
                // ツリーに見つからない場合はグループ外アクターとして扱う
                return node == null || !node.IsGroup;
            })
            .ToList();

    /// ids のうち少なくとも 1 つが子を持つか返す。
    public bool AnyHasChildren(IEnumerable<int> ids) =>
        ids.Any(id => FindNode(_roots, id) is { } n && n.Children.Count > 0);

    private static ActorNode? FindNode(List<ActorNode> list, int id)
    {
        foreach (var n in list)
        {
            if (n.Id == id) return n;
            var found = FindNode(n.Children, id);
            if (found != null) return found;
        }
        return null;
    }

    /// 指定 id のノードの親ノードを探す。ルート直下（親なし）の場合は null を返す。
    private static ActorNode? FindParentNode(List<ActorNode> list, int id)
    {
        foreach (var n in list)
        {
            if (n.Children.Any(c => c.Id == id)) return n;
            var found = FindParentNode(n.Children, id);
            if (found != null) return found;
        }
        return null;
    }

    // TreeView から指定 Id のアイテムを取り外す
    private static bool DetachTreeItem(ItemCollection items, int id)
    {
        foreach (TreeViewItem item in items)
        {
            if (item.Tag is ActorNode n && n.Id == id) { items.Remove(item); return true; }
            if (DetachTreeItem(item.Items, id)) return true;
        }
        return false;
    }

    // ── インラインリネーム ────────────────────────────────────

    private void StartRename(int nodeId)
    {
        var item = FindTreeItemById(ActorTree.Items, nodeId);
        if (item?.Tag is not ActorNode node) return;

        var currentName = node.Name;
        var committed   = false;

        var tb = new TextBox
        {
            Text              = currentName,
            Background        = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
            Foreground        = Brushes.White,
            CaretBrush        = Brushes.White,
            BorderThickness   = new Thickness(1),
            BorderBrush       = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)),
            Padding           = new Thickness(3, 1, 3, 1),
            FontSize          = 13,
            MinWidth          = 80,
            VerticalAlignment = VerticalAlignment.Center,
        };

        void Commit()
        {
            if (committed) return;
            committed = true;

            var raw     = tb.Text.Trim();
            var newName = string.IsNullOrEmpty(raw) ? currentName
                        : GetUniqueName(raw, nodeId);

            node.Name   = newName;
            item.Header = BuildItemHeader(node);

            if (newName != currentName)
            {
                // シーンモード・アクター編集モードともにノードはアクタ（DFS ID）なので
                // 常に RENAME_ACTOR を使用する（RENAME はインスタンスリネーム用）
                _runtime?.SendToRuntime($"RENAME_ACTOR:{nodeId},{newName}");
            }
        }

        void Cancel()
        {
            if (committed) return;
            committed   = true;
            item.Header = BuildItemHeader(node);
        }

        // PreviewKeyDown を使うことで TreeViewItem の Enter 横取りを防ぐ
        tb.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape)          { Cancel(); e.Handled = true; }
        };
        tb.LostFocus += (_, _) => Commit();

        // ヘッダーをアイコン＋TextBox に置き換え
        var icon = new TextBlock
        {
            Text              = "◆",
            Foreground        = new SolidColorBrush(Color.FromRgb(0x55, 0xAA, 0xFF)),
            FontSize          = 9,
            Margin            = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(icon);
        sp.Children.Add(tb);
        item.Header = sp;

        // レンダリング後にフォーカスを当てて全選択
        Dispatcher.BeginInvoke(() =>
        {
            tb.Focus();
            tb.SelectAll();
        }, DispatcherPriority.Input);
    }

    // ── 重複名の解決 ─────────────────────────────────────────

    /// <summary>
    /// 全インスタンス名の中に name が重複する場合は "name(n)" を返す。
    /// excludeId のノード自身は比較対象から除外する。
    /// </summary>
    private string GetUniqueName(string name, int excludeId)
    {
        var existing = GetAllNodes(_roots)
            .Where(n => n.Id != excludeId)
            .Select(n => n.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (!existing.Contains(name)) return name;

        int n = 1;
        while (existing.Contains($"{name}({n})")) n++;
        return $"{name}({n})";
    }

    private static IEnumerable<ActorNode> GetAllNodes(IEnumerable<ActorNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in GetAllNodes(node.Children))
                yield return child;
        }
    }

    // ── ヘルパー ─────────────────────────────────────────────

    private static bool IsDescendant(ActorNode root, int targetId)
    {
        foreach (var child in root.Children)
        {
            if (child.Id == targetId || IsDescendant(child, targetId)) return true;
        }
        return false;
    }

    private void ClearDropHighlight()
    {
        if (_dropTarget == null) return;
        var border = FindVisualChild<Border>(_dropTarget, "RowBorder");
        if (border != null) border.ClearValue(Border.BackgroundProperty);
    }

    private static TreeViewItem? FindTreeItemById(ItemCollection items, int id)
    {
        foreach (TreeViewItem item in items)
        {
            if (item.Tag is ActorNode node && node.Id == id) return item;
            var found = FindTreeItemById(item.Items, id);
            if (found != null) return found;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj != null)
        {
            if (obj is T t) return t;
            // Run 等の非 Visual DependencyObject は VisualTreeHelper が使えないので
            // LogicalTreeHelper で論理親を辿る
            obj = obj is Visual
                ? VisualTreeHelper.GetParent(obj)
                : LogicalTreeHelper.GetParent(obj);
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name) return fe;
            var found = FindVisualChild<T>(child, name);
            if (found != null) return found;
        }
        return null;
    }

    // 名前を問わず、指定型の最初のビジュアル子孫を返す（ActorTree 内部の無名 ScrollViewer 取得用）
    private static T? FindVisualChildOfType<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindVisualChildOfType<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    // ── ドラッグ中オートスクロール ────────────────────────────

    /// <summary>
    /// ActorTree 内部の ScrollViewer を取得する（初回のみビジュアルツリー探索し、以降キャッシュを使う）。
    /// TreeView のテンプレート内 ScrollViewer には x:Name が付いていないため型のみで探索する。
    /// </summary>
    private ScrollViewer? GetTreeScrollViewer()
    {
        if (_treeScrollViewerCache != null) return _treeScrollViewerCache;
        _treeScrollViewerCache = FindVisualChildOfType<ScrollViewer>(ActorTree);
        return _treeScrollViewerCache;
    }

    /// <summary>
    /// ドラッグ中カーソルの Y 座標（ActorTree 基準）を見て、上下端マージン内なら
    /// オートスクロールを開始/継続し、マージン外なら停止する。
    /// DispatcherTimer で一定間隔ごとにスクロールすることで、可視範囲外へも
    /// ドラッグして運べるようにする（TreeView は表示範囲外へ直接ドロップできないため）。
    /// </summary>
    private void UpdateAutoScroll(double cursorY)
    {
        var viewer = GetTreeScrollViewer();
        if (viewer == null) { StopAutoScroll(); return; }

        int direction;
        if (cursorY <= AutoScrollEdgeMargin) direction = -1;
        else if (cursorY >= ActorTree.ActualHeight - AutoScrollEdgeMargin) direction = 1;
        else direction = 0;

        if (direction == 0) { StopAutoScroll(); return; }

        _autoScrollDirection = direction;

        if (_autoScrollTimer == null)
        {
            _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoScrollIntervalMs) };
            _autoScrollTimer.Tick += (_, _) =>
            {
                var sv = GetTreeScrollViewer();
                if (sv == null) { StopAutoScroll(); return; }
                var next = sv.VerticalOffset + _autoScrollDirection * AutoScrollStep;
                sv.ScrollToVerticalOffset(Math.Max(0, Math.Min(sv.ScrollableHeight, next)));
            };
        }

        if (!_autoScrollTimer.IsEnabled) _autoScrollTimer.Start();
    }

    /// <summary>オートスクロールタイマーを停止する。ドラッグ終了・離脱・ドロップの全経路で必ず呼ぶ。</summary>
    private void StopAutoScroll()
    {
        _autoScrollTimer?.Stop();
        _autoScrollDirection = 0;
    }

    // ── 複数選択ビジュアル ────────────────────────────────────

    /// <summary>
    /// _selectedIds に含まれるがプライマリでない項目に薄いハイライトを設定する。
    /// </summary>
    private void UpdateMultiSelectVisuals() => UpdateMultiSelectVisuals(ActorTree.Items);

    private void UpdateMultiSelectVisuals(ItemCollection items)
    {
        foreach (TreeViewItem item in items)
        {
            if (item.Tag is ActorNode node)
            {
                var border = FindVisualChild<Border>(item, "RowBorder");
                if (border != null)
                {
                    if (_selectedIds.Contains(node.Id) && node.Id != _selectedId)
                        border.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x33, 0x99, 0xFF));
                    else
                        border.ClearValue(Border.BackgroundProperty);
                }
            }
            UpdateMultiSelectVisuals(item.Items);
        }
    }

    /// <summary>
    /// 展開されている TreeViewItem を表示順（深さ優先）にフラットなリストで返す。
    /// Shift 範囲選択のインデックス計算に使用する。
    /// </summary>
    private static List<TreeViewItem> GetFlatItems(ItemCollection items)
    {
        var list = new List<TreeViewItem>();
        CollectFlatItems(items, list);
        return list;
    }

    private static void CollectFlatItems(ItemCollection items, List<TreeViewItem> list)
    {
        foreach (TreeViewItem item in items)
        {
            list.Add(item);
            if (item.IsExpanded) CollectFlatItems(item.Items, list);
        }
    }
}
