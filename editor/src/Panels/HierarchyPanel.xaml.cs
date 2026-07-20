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
    /// <summary>
    /// このアクター自身が Canvas コンポーネントを持つか。
    /// 2D アクターの新しい親として「Canvas を持つ 3D アクター」を許可する判定に使用する
    /// （Canvas を持たない 3D アクターへの 2D 子付けは禁止）。
    /// </summary>
    public bool            HasCanvas { get; set; }
    /// <summary>
    /// このノードがプレハブ（アクタファイル参照リンク）インスタンスのルートか否か。
    /// Rust 側はインスタンスのルートノードにのみ is_prefab=true を付ける（子は false）。
    /// Hierarchy 上で Unity 風に青系テキスト + プレハブアイコンで区別表示する。
    /// </summary>
    public bool            IsPrefab { get; set; }
    /// <summary>
    /// 整理専用のフォルダノードか否か。Rust 側は Transform を持たないため
    /// Hierarchy 上ではグループとは別の専用アイコン・色で区別表示する。
    /// フォルダは同時に IsGroup=true でも届く（選択種別スキップ等の既存グループ挙動を流用するため）。
    /// </summary>
    public bool            IsFolder { get; set; }
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
    /// <summary>
    /// インラインリネーム（TextBox 表示）中かどうか。
    /// リネーム中の再クリックで再度リネームタイマが仕込まれ、
    /// LostFocus 確定直後にまたリネーム状態へ入ってしまう不具合を防ぐために使う。
    /// </summary>
    private bool             _isRenaming;

    // ── エクスプローラー風レイアウト定数 ──────────────────────
    /// <summary>
    /// 行のヘッダテキスト右端からこの px 数を超えて右をクリックした場合は
    /// 「空白（アイテム外）」扱いにするマージン（右クリックのみ対象）。
    /// </summary>
    private const double RowContentRightMarginPx = 48.0;

    // ── ドロップ挿入インジケータ / 階層選択定数 ──────────────────
    /// <summary>
    /// ツリー 1 階層あたりのインデント幅（px）。XAML の ItemsHost Margin="16,0,0,0" と一致させること。
    /// 変更時は HierarchyPanel.xaml の ItemsHost Margin も合わせて更新すること（ズレると
    /// 境界線ドロップの階層選択インジケータ位置が実際の挿入階層とずれる）。
    /// </summary>
    private const double IndentPerLevelPx = 16.0;
    /// <summary>
    /// ドロップ時の兄弟挿入（境界線）ゾーンが行高に占める割合（上下それぞれ）。
    /// 境界線ドロップの判定を取りやすくするため広めに設定している
    /// （残りの中央 1 - 2*係数 が子化ゾーンになる。0.32 → 上下 32% ずつが兄弟挿入、中央 36% が子化）。
    /// </summary>
    private const double SiblingInsertZoneRatio = 0.32;

    // ── ドロップ拒否理由ポップアップ定数 ──────────────────────────
    /// <summary>拒否理由ポップアップをカーソルから右下へずらすオフセット（px）。</summary>
    private const double DropRejectPopupOffsetX = 16.0;
    private const double DropRejectPopupOffsetY = 20.0;

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
    // キャンバス編集タブ（インスペクタ「キャンバスを編集」から開くシーン内キャンバスの
    // 隔離編集タブ）を表示中かどうか。true の間はルートノード（DFS id 0 = そのタブの
    // ルート親キャンバス。Rust 側 do_send_hierarchy は世界線内で DFS カウンタを
    // 0 から振るため、ルートは常に id 0 になる）の削除・3D アクター追加を UI 側でも抑止する。
    // Rust 側（handle_remove_actor / apply_delete / handle_add_actor）が最終防衛であり、
    // ここはあくまで誤操作を未然に防ぐ UX 目的。
    private bool         _isSceneCanvasEditMode     = false;
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
    /// <summary>プレハブインスタンスルートのテキスト色（Unity 風の読みやすい水色）。</summary>
    private static readonly SolidColorBrush BrushPrefabText  = MakeFrozen(Color.FromRgb(0x7F, 0xB2, 0xE5));
    /// <summary>
    /// フォルダノードのアイコン色（落ち着いた黄土/クリーム系）。
    /// グループの黄色（0xFFCC44）とはトーンを明確に分け、グループとフォルダを一目で区別できるようにする。
    /// </summary>
    private static readonly SolidColorBrush BrushFolderIcon  = MakeFrozen(Color.FromRgb(0xD8, 0xB4, 0x6B));

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
    /// プレハブルートの右クリックメニュー「アクタファイルを開く」で、
    /// 参照元 .actor をアクタ編集タブで開くよう要求する（引数は対象アクターの DFS ID）。
    /// MainWindow が Inspector.OpenPrefabSource へ橋渡しする（参照元パスは Inspector が保持）。
    /// </summary>
    public event Action<int>? PrefabSourceOpenRequested;

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
    /// isSceneCanvas に true を渡すと、キャンバス編集タブ専用のルート保護 UI
    /// （ルートノードの削除禁止・ルートへの 3D アクタ追加禁止）が有効になる。
    /// </summary>
    public void SetActorEditMode(bool isActorMode, uint worldLine = 0, bool is2D = false, bool isSceneCanvas = false)
    {
        _isActorEditMode      = isActorMode;
        _isActor2DMode        = is2D;
        _isSceneCanvasEditMode = isActorMode && isSceneCanvas;
        _activeWorldLine       = worldLine;
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
                    // Canvas 保有フラグ（旧 JSON にフィールドが無ければ false）
                    HasCanvas = e.TryGetProperty("has_canvas", out var hc) && hc.GetBoolean(),
                    // プレハブインスタンスのルートか（旧 JSON にフィールドが無ければ false）
                    IsPrefab  = e.TryGetProperty("is_prefab", out var ip) && ip.GetBoolean(),
                    // 整理専用フォルダノードか（旧 JSON にフィールドが無ければ false）
                    IsFolder  = e.TryGetProperty("is_folder", out var iff) && iff.GetBoolean(),
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
        // フォルダ: 黄土色 ▤（最優先判定。フォルダは IsGroup も true で届くため先に弾く）、
        // IsGroup: 黄色 ▶、2D アクター: オレンジ ◆、3D アクター: 青 ◆
        var iconBrush = node.IsFolder ? BrushFolderIcon
                      : node.IsGroup  ? BrushGroupIcon
                      : node.Is2D     ? BrushActor2DIcon
                      :                 BrushActorIcon;
        var iconGlyph = node.IsFolder ? "▤ "
                      : node.IsGroup  ? "▶ "
                      :                 "◆ ";
        tb.Inlines.Add(new Run(iconGlyph)
        {
            Foreground = iconBrush,
            FontSize   = 9,
        });
        // プレハブインスタンスのルートは Unity 風に、名前の前へ小さなプレハブアイコン（📦）を付け、
        // 名前テキストを青系（水色）で表示して通常アクターと区別する（子ノードは通常表示）。
        if (node.IsPrefab)
        {
            tb.Inlines.Add(new Run("📦 ") { FontSize = 10 });
            tb.Inlines.Add(new Run(node.Name) { FontSize = 13, Foreground = BrushPrefabText });
        }
        else
        {
            tb.Inlines.Add(new Run(node.Name) { FontSize = 13 });
        }
        // 非アクティブ（自身または祖先の active が false）は Unity 風に淡色表示する。
        // テキスト色（プレハブ青）と Opacity は併存し、色を保ったまま淡くなる。
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

    /// <summary>
    /// 行内のクリック座標が「コンテンツ（RowHighlight）右端 + マージンより右」＝空白エリアかどうかを判定する。
    /// RowBorder は D&D 受け入れのため行全幅のヒット領域を持つが、左右クリックでの選択・リネーム・
    /// コンテキストメニューはこの判定でコンテンツ幅に絞り、右側の空きスペースを「空白」として扱う
    /// （エクスプローラー風 UX）。判定基準は選択ハイライトの実際の描画範囲（RowHighlight）と一致させる。
    /// RowHighlight が取得できない場合は Header 要素の幅にフォールバックする。
    /// </summary>
    private bool IsBlankRowAreaClick(TreeViewItem item, Point posInTree)
    {
        FrameworkElement? contentRef = FindVisualChild<Border>(item, "RowHighlight");
        if (contentRef == null && item.Header is FrameworkElement header && header.ActualWidth > 0)
            contentRef = header; // フォールバック（従来挙動）

        if (contentRef == null || contentRef.ActualWidth <= 0) return false;

        var contentLeft  = contentRef.TranslatePoint(new Point(0, 0), ActorTree).X;
        var contentRight = contentLeft + contentRef.ActualWidth;
        return posInTree.X > contentRight + RowContentRightMarginPx;
    }

    /// <summary>
    /// ドラッグカーソル位置が行の「空白エリア」かどうかを判定する（D&D 用）。
    /// クリック判定（IsBlankRowAreaClick）と同じ「コンテンツ右端＋マージン」基準で
    /// ドロップ受け入れ範囲も絞り、右側の空きスペースへのドラッグは
    /// アイテム外（ツリー余白＝ルートへのドロップ経路）として扱えるようにする。
    ///
    /// ただし上下の兄弟挿入ゾーン（SiblingInsertZoneRatio）内では空白判定を適用せず
    /// 行全幅の判定を維持する。理由: after 挿入の挿入階層選択はカーソル X を右へ振って
    /// 深い階層を選ぶ操作（BuildAfterInsertCandidates + カーソル X 探索）であり、
    /// コンテンツ右端より右を空白扱いにすると境界線ドロップの階層選択が
    /// マージン外で無効化されて操作性を大きく損なうため（意図的な干渉回避）。
    /// </summary>
    private bool IsBlankDragPosition(TreeViewItem item, Point posInTree)
    {
        // ゾーン判定は OnTreeDragOver と同じ基準（ヘッダ行 RowBorder の高さ）を使う
        var rowBorder = FindVisualChild<Border>(item, "RowBorder");
        var itemTop   = item.TranslatePoint(new Point(0, 0), ActorTree).Y;
        var rowHeight = rowBorder is { ActualHeight: > 0 } ? rowBorder.ActualHeight : item.ActualHeight;
        var relY      = posInTree.Y - itemTop;
        var zone      = rowHeight * SiblingInsertZoneRatio;

        // 上下の兄弟挿入ゾーン内 → 空白扱いにしない（挿入ライン・階層選択の操作性を守る）
        if (relY <= zone || relY >= rowHeight - zone) return false;

        // 中央の子化ゾーン → クリックと同じコンテンツ幅基準で空白判定する
        return IsBlankRowAreaClick(item, posInTree);
    }

    private void OnTreeRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        // コンテキストメニューを閉じるLMBクリックで誤ドラッグが起きないようにリセット
        _pendingDragNode   = null;
        _pendingDeselect   = false;
        _pendingDeselectId = -1;

        var pos  = e.GetPosition(ActorTree);
        var hit  = ActorTree.InputHitTest(pos) as DependencyObject;
        var item = FindAncestor<TreeViewItem>(hit);
        _rightClickedNode = item?.Tag as ActorNode;

        // ── エクスプローラー風: 行の右側の余白を「空白」扱いにする（右クリックのみ）──
        // RowBorder は行の右端までヒット領域を持つため、コンテンツ右の余白を右クリックしても
        // アイテム扱いになる。RowHighlight（選択ハイライトの実際の描画範囲）右端 + マージンより
        // 右なら空白扱いにする。
        if (_rightClickedNode != null && item != null && IsBlankRowAreaClick(item, pos))
            _rightClickedNode = null; // 空白扱い

        // ── アクタ上の右クリックで選択 → 選択用メニュー（#5）──
        // 未選択アクタを右クリックしたらその場で単一選択へ切り替えてから選択用メニューを出す。
        // 既に選択済み（複数選択に含まれる場合も）なら選択を維持する。
        if (_rightClickedNode != null && !_selectedIds.Contains(_rightClickedNode.Id))
        {
            var id = _rightClickedNode.Id;
            _selectedIds.Clear();
            _selectedIds.Add(id);
            _selectedId = id;
            _anchorId   = id;
            SelectTreeItem(id);
            UpdateMultiSelectVisuals();
            SendSelectionToRuntime();
        }

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

        // ── アクタを追加 サブメニュー（2D/3D → 親(ラップ)/子 の2段選択）──────
        menu.Items.Add(BuildAddActorForSelectedSubMenu());
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

        // ── プレハブインスタンスのルート専用項目 ──────────────────────
        // 右クリック対象がプレハブルートのときのみ、参照元を開く／リンク解除を追加する。
        if (_rightClickedNode is { IsPrefab: true } prefabNode)
        {
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "アクタファイルを開く", null,
                (_, _) => PrefabSourceOpenRequested?.Invoke(prefabNode.Id));
            AddMenuItem(menu, "プレハブリンク解除", null,
                (_, _) => ConfirmAndUnlinkPrefab(prefabNode.Id));
        }
        return menu;
    }

    /// <summary>
    /// 確認ダイアログを表示し、承諾されたら指定アクターのプレハブリンクを解除する（UNLINK_PREFAB 送信）。
    /// Inspector 側のリンク解除と同一の導線・文言。
    /// </summary>
    private void ConfirmAndUnlinkPrefab(int actorDfsId)
    {
        var result = MessageBox.Show(
            "このアクターのプレハブ参照リンクを解除しますか？\n解除後は通常のアクターとして独立し、参照元の変更は反映されなくなります。",
            "プレハブリンク解除", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        _runtime?.SendToRuntime($"UNLINK_PREFAB:{actorDfsId}");
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

        // キャンバス編集タブでルートに 3D アクタを追加しようとする導線（ルート＝空きスペースへの
        // 右クリック追加）は Rust 側（handle_add_actor）で拒否されるため、UI 側でも無効化しておく
        // （asChild=true の子追加は既存の親ノードへの追加なので対象外）。
        bool disallowRoot3D = !asChild && _isSceneCanvasEditMode;

        var item3D = new MenuItem { Header = "3D アクタ", IsEnabled = !disallowRoot3D };
        item3D.Click += (_, _) =>
        {
            if (disallowRoot3D) return;
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

    /// <summary>
    /// 選択中アクター（右クリック対象 = _rightClickedNode）向けの「アクタを追加」サブメニューを生成する。
    /// 構造:「アクタを追加」→「2D アクタ / 3D アクタ」→各配下に「親として追加（ラップ）」「子として追加」。
    ///
    /// 表示/有効ルール（対象ノードの種別・Canvas 有無・キャンバス編集ルートに基づく）:
    ///  - 対象が 2D: 「3D アクタ」サブメニュー自体を非表示（3D は 2D の子になれず、
    ///    新規 3D 親（Canvas 無し）も 2D を子にできないため親/子とも不成立）。#2
    ///  - 「2D アクタ→親として追加（ラップ）」: 対象が 3D なら無効（3D は 2D の子になれない）。#3
    ///  - 「2D アクタ→子として追加」: 対象が Canvas 無し 3D なら無効（理由ツールチップ付き）。#3
    ///  - キャンバス編集タブのルート（DFS 0）への「親として追加（ラップ）」: 2D/3D とも無効
    ///    （ルート一意性維持。Rust 側 handle_wrap_actor が最終防衛）。#1 ガード対応
    ///
    /// 複数選択中に右クリックした場合も、判定は右クリックノード（_rightClickedNode）の種別に統一する
    /// （子/親追加はいずれも右クリックノード基準の操作のため。実装の一貫性を優先）。
    /// </summary>
    private MenuItem BuildAddActorForSelectedSubMenu()
    {
        var sub    = new MenuItem { Header = "アクタを追加" };
        var target = _rightClickedNode;
        if (target == null) return sub; // 念のため（選択メニューは通常 target 非 null）

        // キャンバス編集タブのルート（DFS 0）は「親として追加（ラップ）」を 2D/3D とも禁止する。
        bool isCanvasEditRoot = _isSceneCanvasEditMode && target.Id == 0;

        // ── 2D アクタ サブメニュー ──────────────────────────────────
        var m2d = new MenuItem { Header = "2D アクタ" };

        // 親として追加（ラップ）: 対象が 2D のときのみ有効（3D は 2D ラッパーの子になれない）。
        var wrap2d = new MenuItem { Header = "親として追加（ラップ）" };
        if (!target.Is2D)
        {
            wrap2d.IsEnabled = false;
            wrap2d.ToolTip   = "3Dアクターは2Dアクターの子にできません";
        }
        else if (isCanvasEditRoot)
        {
            wrap2d.IsEnabled = false;
            wrap2d.ToolTip   = "キャンバス編集中のルートはラップできません";
        }
        wrap2d.Click += (_, _) => { if (wrap2d.IsEnabled) SendWrapActor(target.Id, is2d: true); };

        // 子として追加: 対象が 2D なら常に有効。対象が Canvas 無し 3D なら無効。
        var child2d = new MenuItem { Header = "子として追加" };
        if (!target.Is2D && !target.HasCanvas)
        {
            child2d.IsEnabled = false;
            child2d.ToolTip   = "Canvasを持たない3Dアクターに2Dアクターは追加できません";
        }
        child2d.Click += (_, _) =>
        {
            if (!child2d.IsEnabled) return;
            PrepareRenameAfterAdd();
            _runtime?.SendToRuntime($"ADD_ACTOR_2D_CHILD:{target.Id}");
        };

        m2d.Items.Add(wrap2d);
        m2d.Items.Add(child2d);
        sub.Items.Add(m2d);

        // ── 3D アクタ サブメニュー（対象が 2D のときは非表示：#2）──────────
        if (!target.Is2D)
        {
            var m3d = new MenuItem { Header = "3D アクタ" };

            // 親として追加（ラップ）: 対象が 3D のときのみ有効。キャンバス編集ルートは無効。
            var wrap3d = new MenuItem { Header = "親として追加（ラップ）" };
            if (isCanvasEditRoot)
            {
                wrap3d.IsEnabled = false;
                wrap3d.ToolTip   = "キャンバス編集中のルートはラップできません";
            }
            wrap3d.Click += (_, _) => { if (wrap3d.IsEnabled) SendWrapActor(target.Id, is2d: false); };

            // 子として追加: 対象が 3D なら常に有効（3D→3D）。
            var child3d = new MenuItem { Header = "子として追加" };
            child3d.Click += (_, _) =>
            {
                PrepareRenameAfterAdd();
                _runtime?.SendToRuntime($"ADD_ACTOR_CHILD:{target.Id}");
            };

            m3d.Items.Add(wrap3d);
            m3d.Items.Add(child3d);
            sub.Items.Add(m3d);
        }

        return sub;
    }

    /// <summary>
    /// 指定アクターを新規アクターでラップする WRAP_ACTOR コマンドを送信する。
    /// 新規アクターが child の位置へ挿入され、child はその子へ移動する（Rust 側 handle_wrap_actor）。
    /// is2d=true なら 2D ラッパー、false なら 3D ラッパーを生成する。
    /// </summary>
    private void SendWrapActor(int childId, bool is2d)
        => _runtime?.SendToRuntime($"WRAP_ACTOR:{childId},{(is2d ? 1 : 0)}");

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
            // キャンバス編集タブ中はルートノード（DFS id 0 = ルート親キャンバス）の
            // 削除を UI 側でも抑止する（Rust 側 handle_remove_actor が最終防衛）。
            if (_isSceneCanvasEditMode && _selectedId == 0) return;
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

        // ── エクスプローラー風: 行の右側の空白をクリックした場合はアイテム外（空白）扱いにする ──
        // D&D のドロップ受け入れ判定（OnTreeDragOver）は行全幅のまま維持するため、
        // ここでの読み替えは選択・リネーム・ドラッグ開始の起点にのみ影響する。
        if (item != null && IsBlankRowAreaClick(item, _dragStart))
            item = null;

        if (item == null)
        {
            // 空白クリック: 選択解除（ツリー余白クリック時の挙動。既存の局所的な余白デセレクト処理が
            // なかったため、ここで選択解除 + ランタイムへの再通知を行う）。
            CancelRenameTimer();
            _pendingRenameId = -1;
            _pendingDragNode = null;
            if (_selectedIds.Count > 0 || _selectedId >= 0)
            {
                _selectedIds.Clear();
                _selectedId = -1;
                _anchorId   = -1;
                DeselectAll();
                UpdateMultiSelectVisuals();
                SendSelectionToRuntime();
            }
            return;
        }

        // ここに到達した時点で item は非 null（空白クリックは上で早期 return 済み）
        _pendingDragNode = item.Tag as ActorNode;

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

            // リネーム中（TextBox 表示中）はタイマを仕込まない。
            // 仕込むと LostFocus 確定直後にタイマが発火し、また即リネーム状態へ戻ってしまう。
            // TextBox 自体へのクリックはリネーム操作なのでそのまま素通しする。
            if (normalNode.Id == _selectedId && !_isRenaming)
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

        // Project パネルへのドロップ（アクタファイル化）用に、ドラッグ中の各アクタの
        // 種別（2D/3D）と名前を _dragNodeIds と同じ順序で付加する（複数ドラッグ対応）。
        // Project 側は保存拡張子（.actor2d / .actor）と既定ファイル名にこれを使う。
        var dragIs2D  = new List<bool>(_dragNodeIds.Count);
        var dragNames = new List<string>(_dragNodeIds.Count);
        foreach (var id in _dragNodeIds)
        {
            var n = FindNode(_roots, id);
            dragIs2D.Add(n?.Is2D ?? false);
            dragNames.Add(n?.Name ?? $"Actor_{id}");
        }
        data.SetData("HierarchyActorIds2D",  dragIs2D);
        data.SetData("HierarchyActorNames",  dragNames);

        // 許可効果に Copy を含める。ツリー内の並べ替えは Move（DragOver で Move 指定）だが、
        // Project パネルへのアクタファイル化ドロップは Copy を返すため、
        // Copy を許可しておかないと Project 側でカーソル表示・Drop 配送が成立しない。
        DragDrop.DoDragDrop(ActorTree, data, DragDropEffects.Move | DragDropEffects.Copy);

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
        HideDropReject();
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
            HideDropReject();
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

        // ── ドロップ受け入れ範囲もクリック可能範囲（コンテンツ幅＋マージン）に絞る ──
        // 子化ゾーン内でコンテンツ右端＋マージンより右にカーソルがある場合はアイテム外扱いにし、
        // ツリー余白と同じ経路（通常シーン: ルートへドロップ / アクター編集モード: 拒否）へ落とす。
        // 兄弟挿入ゾーン内は行全幅判定を維持する（詳細は IsBlankDragPosition のコメント参照）。
        // OnTreeDrop はここで設定した _dropTarget/_insertTarget/_dropAsRoot のみを参照するため、
        // DragOver 側で経路を落とせば Drop の受け入れ判定も自動的に一致する。
        if (item != null && IsBlankDragPosition(item, pos))
            item = null;

        if (item?.Tag is ActorNode targetNode)
        {
            // ドラッグ中のノードのいずれかがターゲット自身または祖先なら無効
            bool invalid = _dragNodeIds.Any(id =>
                id == targetNode.Id ||
                (FindNode(_roots, id) is { } n && IsDescendant(n, targetNode.Id)));
            if (invalid)
            {
                RejectDrop(e, "自分自身または子孫にはドロップできません", pos);
                return;
            }

            // ゾーン判定は「ヘッダ行（RowBorder）」の高さを基準にする。
            // item.ActualHeight は展開時に子行も含む全体高になり、ヘッダ上では
            // 常に上端 25% に入って中央（子化）ゾーンへ到達できないため。
            // RowBorder は Grid.Row=0（アイテム最上部）なので、その上端は item 上端と一致する。
            var rowBorder = FindVisualChild<Border>(item, "RowBorder");
            var itemTop   = item.TranslatePoint(new Point(0, 0), ActorTree).Y;
            // RowBorder が取得できない場合は従来どおり item.ActualHeight にフォールバック
            var rowHeight = rowBorder is { ActualHeight: > 0 } ? rowBorder.ActualHeight : item.ActualHeight;
            var relY      = pos.Y - itemTop;
            var zone      = rowHeight * SiblingInsertZoneRatio;

            // ドラッグ中のノードに 3D アクター（!Is2D）が含まれるかどうか。
            // 「2D アクターの子に 3D アクターを配置する」組み合わせを弾くための判定に使う。
            bool draggingHas3D = _dragNodeIds.Any(id => FindNode(_roots, id) is { Is2D: false });
            // ドラッグ中のノードに 2D アクターが含まれるかどうか。
            // 「2D アクターを Canvas 無し 3D アクターの子にする」組み合わせを弾く判定に使う。
            bool draggingHas2D = _dragNodeIds.Any(id => FindNode(_roots, id) is { Is2D: true });

            if (relY <= zone)
            {
                // ── 上端 25% → before 挿入（候補は 1 つ：targetNode の弟＝targetNode の兄）──
                // 実効的な新しい親は targetNode の親ノード（ルート直下なら null）。
                var effectiveParent = FindParentNode(_roots, targetNode.Id);
                if (!CheckSiblingInsertAllowed(e, effectiveParent, draggingHas3D, draggingHas2D, pos))
                    return;

                _insertBefore            = true;
                _insertTarget            = item;
                var indentLeft           = GetItemIndentX(item);
                DropIndicator.Margin     = new Thickness(indentLeft, itemTop - 1, 0, 0);
                DropIndicator.Visibility = Visibility.Visible;
                HideDropReject();
            }
            else if (relY >= rowHeight - zone)
            {
                // ── 下端の兄弟挿入ゾーン（およびヘッダ高超え）→ after 挿入 ──────
                // 【展開中かつ子を持つ対象の特例（Unity/VSCode 準拠）】
                // 展開中の親ヘッダ行の下端境界線は、見た目上「親と最初の子の間」を指す。
                // ここで「親の弟(after)」へ挿入すると、インジケータ表示位置（親と子の間）と
                // 実挿入位置（サブツリー全体の下）が食い違って直感的でないため、
                // 「最初の子の前(before)」＝対象の第一子として挿入する。
                // 最初の子の TreeViewItem が未実体化などで取得できない場合は、
                // フォールバックとして従来どおり弟(after)挿入の経路へ進む。
                //
                // アンカーにはドラッグ中でない最初の子を選ぶ。ドラッグ中ノードを
                // アンカーにすると、Rust 側（handle_reparent_actor）が抽出後の兄弟
                // リストからアンカー Entity を見つけられず末尾追加へフォールバック
                // してしまうため（例: 第一子自身を親下端へドラッグしたケース）。
                // 全子がドラッグ対象で先頭アンカーを取れない場合も従来経路へ進む。
                TreeViewItem? firstChildItem = null;
                if (item.IsExpanded)
                {
                    foreach (var childObj in item.Items)
                    {
                        if (childObj is TreeViewItem tvi
                            && tvi.Tag is ActorNode childNode
                            && !_dragNodeIds.Contains(childNode.Id))
                        {
                            firstChildItem = tvi;
                            break;
                        }
                    }
                }

                if (firstChildItem != null)
                {
                    // 実効親は対象アイテム自身になるため、種別ガード
                    // （3D→2D禁止・2D→Canvas無し3D禁止）も対象を親として判定する。
                    if (!CheckSiblingInsertAllowed(e, targetNode, draggingHas3D, draggingHas2D, pos))
                        return;

                    // _insertTarget=第一子 + _insertBefore=true により、既存の
                    // anchor/placeBefore 送信ロジック（OnTreeDrop）とローカル楽観反映
                    // （ReparentInPlace）がそのまま「対象の第一子として挿入」になる。
                    _insertBefore            = true;
                    _insertTarget            = firstChildItem;
                    // インジケータは子のインデント位置（対象 +1 階層）で、
                    // 親ヘッダと最初の子の間（＝親ヘッダ行の下端）に表示する。
                    var childIndentLeft      = GetItemIndentX(firstChildItem);
                    DropIndicator.Margin     = new Thickness(childIndentLeft, itemTop + rowHeight - 1, 0, 0);
                    DropIndicator.Visibility = Visibility.Visible;
                    HideDropReject();
                    e.Handled = true;
                    return;
                }

                // 【従来経路: 折りたたみ中・子なしアイテムの下端 → 弟(after)挿入】
                // targetNode が「親の最後の子」である連鎖を上へ辿り、挿入先候補を深い順に構築する。
                // カーソル X（インデント）でどの階層へ挿入するかを VSCode/Unity 方式で選ぶ（#5）。
                var candidates = BuildAfterInsertCandidates(targetNode, item);

                // カーソル X から挿入階層を決める。深い候補ほどインデント X が大きい。
                // cursorX がある候補のインデント以右ならその候補（先頭＝最深から探索）。
                // すべての候補より左なら最も浅い候補へクランプする。
                int chosen = candidates.Count - 1;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (pos.X >= GetItemIndentX(candidates[i].AnchorItem)) { chosen = i; break; }
                }
                var cand = candidates[chosen];

                // 種別ガードは「選ばれた候補の実効親」に対して判定する（挿入先の親が階層で変わるため）。
                if (!CheckSiblingInsertAllowed(e, cand.EffectiveParent, draggingHas3D, draggingHas2D, pos))
                    return;

                _insertBefore            = false;
                _insertTarget            = cand.AnchorItem;
                // ライン Y は常に「カーソルがいる targetNode 行の下端」。X のみ選ばれた階層のインデントへ。
                var indentLeft           = GetItemIndentX(cand.AnchorItem);
                DropIndicator.Margin     = new Thickness(indentLeft, itemTop + rowHeight - 1, 0, 0);
                DropIndicator.Visibility = Visibility.Visible;
                HideDropReject();
            }
            else
            {
                // ── 中央 → 子として追加。実効的な新しい親は targetNode 自身。──
                // targetNode が 2D かつドラッグ中に 3D が含まれる場合はドロップ不可とする。
                if (draggingHas3D && targetNode.Is2D)
                {
                    RejectDrop(e, "3Dアクターは2Dアクターの子にできません", pos);
                    return;
                }
                // ドラッグに 2D が含まれ、子化先が「Canvas 無しの 3D アクター」なら禁止。
                if (draggingHas2D && targetNode is { Is2D: false, HasCanvas: false })
                {
                    RejectDrop(e, "Canvasを持たない3Dアクターに2Dアクターは配置できません", pos);
                    return;
                }

                _dropTarget = item;
                // 子化ハイライトも視覚一貫性のため RowHighlight 側に描画する
                // （見つからない場合は RowBorder にフォールバック＝従来どおり行全幅で塗る）。
                var highlightBorder = FindVisualChild<Border>(item, "RowHighlight") ?? rowBorder;
                if (highlightBorder != null)
                    highlightBorder.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x33, 0x99, 0xFF));
                HideDropReject();
            }
        }
        else
        {
            // アクター編集モードではルートへのドロップを禁止
            if (_isActorEditMode)
            {
                RejectDrop(e, "アクター編集中はルート階層へは移動できません", pos);
                e.Handled = true;
                return;
            }
            _dropAsRoot              = true;
            DropIndicator.Margin     = new Thickness(0);
            DropIndicator.Visibility = Visibility.Visible;
            HideDropReject();
        }
        e.Handled = true;
    }

    /// <summary>
    /// after 挿入時の挿入先候補を深い順に構築する（#5）。
    /// 候補[0] は targetNode の弟としての挿入（アンカー=targetNode）。
    /// targetNode が「その親の最後の子」である限り、親を次のアンカー候補として上へ辿る
    /// （親の弟として → 祖父の弟として …ルートまで）。非末尾で打ち切る。
    /// 各候補は (アンカーノード, アンカー TreeViewItem, その挿入先の実効親) を持つ。
    /// </summary>
    private List<(ActorNode Anchor, TreeViewItem AnchorItem, ActorNode? EffectiveParent)>
        BuildAfterInsertCandidates(ActorNode targetNode, TreeViewItem targetItem)
    {
        var candidates = new List<(ActorNode, TreeViewItem, ActorNode?)>();
        var curNode = targetNode;
        var curItem = targetItem;
        while (true)
        {
            var parent   = FindParentNode(_roots, curNode.Id);
            candidates.Add((curNode, curItem, parent));

            // curNode がその親（親なしならルート）の最後の子でなければ上位候補は無い。
            var siblings = parent?.Children ?? _roots;
            bool isLast  = siblings.Count > 0 && siblings[siblings.Count - 1].Id == curNode.Id;
            if (!isLast || parent == null) break;

            // 親アイテムが取得できなければ（仮想化で未実体化等）そこで打ち切る。
            var parentItem = FindTreeItemById(ActorTree.Items, parent.Id);
            if (parentItem == null) break;

            curNode = parent;
            curItem = parentItem;
        }
        return candidates;
    }

    /// <summary>
    /// 兄弟挿入（before/after）の種別ガード。許可なら true、拒否なら e.Effects=None にして
    /// 拒否理由ポップアップを出し false を返す。effectiveParent が null（ルート直下）は許可。
    /// </summary>
    private bool CheckSiblingInsertAllowed(
        DragEventArgs e, ActorNode? effectiveParent,
        bool draggingHas3D, bool draggingHas2D, Point pos)
    {
        // 3D を含むドラッグを 2D 親の兄弟へ入れる（＝2D 親の子になる）のは禁止。
        if (draggingHas3D && effectiveParent is { Is2D: true })
        {
            RejectDrop(e, "3Dアクターは2Dアクターの子にできません", pos);
            return false;
        }
        // 2D を含むドラッグの実効親が「Canvas 無しの 3D アクター」なら禁止。
        if (draggingHas2D && effectiveParent is { Is2D: false, HasCanvas: false })
        {
            RejectDrop(e, "Canvasを持たない3Dアクターに2Dアクターは配置できません", pos);
            return false;
        }
        return true;
    }

    /// <summary>
    /// ツリーアイテムの左端 X（ActorTree 基準）を返す。挿入インジケータのインデント合わせと
    /// 階層選択のしきい値に使う（1 階層 = IndentPerLevelPx px、XAML の ItemsHost Margin と一致）。
    /// 取得に失敗した場合は 0（最左）を返す。
    /// </summary>
    private double GetItemIndentX(TreeViewItem item)
    {
        try { return item.TranslatePoint(new Point(0, 0), ActorTree).X; }
        catch { return 0; }
    }

    // ── ドロップ拒否理由ポップアップ（#4）──────────────────────────

    /// <summary>
    /// ドロップを拒否し、理由をカーソル付近にポップアップ表示する。
    /// DragOver 中の拒否分岐から呼ぶ。許可状態になったら HideDropReject で消す。
    /// </summary>
    private void RejectDrop(DragEventArgs e, string reason, Point posInTree)
    {
        e.Effects = DragDropEffects.None;
        // 拒否中は挿入/子化ハイライトを消す（許可の見た目と混在させない）
        ClearDropHighlight();
        DropIndicator.Visibility = Visibility.Collapsed;
        ShowDropReject(reason, posInTree);
    }

    /// <summary>拒否理由ポップアップをカーソル右下に表示・追従させる。</summary>
    private void ShowDropReject(string reason, Point posInTree)
    {
        DropRejectText.Text          = reason;
        DropRejectPopup.PlacementTarget   = ActorTree;
        DropRejectPopup.Placement         = PlacementMode.Relative;
        DropRejectPopup.HorizontalOffset  = posInTree.X + DropRejectPopupOffsetX;
        DropRejectPopup.VerticalOffset    = posInTree.Y + DropRejectPopupOffsetY;
        if (!DropRejectPopup.IsOpen) DropRejectPopup.IsOpen = true;
    }

    /// <summary>拒否理由ポップアップを非表示にする（許可状態・ドロップ・離脱・ドラッグ終了で呼ぶ）。</summary>
    private void HideDropReject()
    {
        if (DropRejectPopup.IsOpen) DropRejectPopup.IsOpen = false;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
        DropIndicator.Visibility = Visibility.Collapsed;
        _dropTarget   = null;
        _dropAsRoot   = false;
        _insertTarget = null;
        HideDropReject();
        StopAutoScroll();
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
        DropIndicator.Visibility = Visibility.Collapsed;
        HideDropReject();
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

        // リネーム開始。再クリックタイマの仕込みを抑止する（#4 の再リネーム防止）。
        // 念のため保留中のタイマも停止しておく。
        _isRenaming = true;
        _renameTimer?.Stop();

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
            // リネーム終了。以降の再クリックはタイマを仕込んでよい。
            _isRenaming = false;
            _renameTimer?.Stop();

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
            // リネーム終了。以降の再クリックはタイマを仕込んでよい。
            _isRenaming = false;
            _renameTimer?.Stop();
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
        // 子化ハイライトは RowHighlight に描画するが、フォールバックで RowBorder に
        // 描画された場合もあり得るため両方クリアする（塗り残し防止）。
        var highlight = FindVisualChild<Border>(_dropTarget, "RowHighlight");
        if (highlight != null) highlight.ClearValue(Border.BackgroundProperty);
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
                // 複数選択の薄いハイライトもコンテンツ幅の RowHighlight に描画する
                // （見つからない場合は RowBorder にフォールバック）。
                var border = FindVisualChild<Border>(item, "RowHighlight")
                             ?? FindVisualChild<Border>(item, "RowBorder");
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
