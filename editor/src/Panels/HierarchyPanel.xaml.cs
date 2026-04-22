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
    public List<ActorNode> Children { get; } = new();
}

// ============================================================
//  HierarchyPanel
// ============================================================

public partial class HierarchyPanel : UserControl
{
    // ── 状態 ─────────────────────────────────────────────────

    private RuntimeManager? _runtime;
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

    // 複数選択時の遅延デセレクト（クリックのみでデセレクト、ドラッグはキープ）
    private bool _pendingDeselect;
    private int  _pendingDeselectId = -1;

    // D&D: MouseDown で確定したドラッグ対象（コンテキストメニュー誤操作防止）
    private ActorNode? _pendingDragNode;

    // キャッシュ
    private static readonly SolidColorBrush BrushGroupIcon = MakeFrozen(Color.FromRgb(0xFF, 0xCC, 0x44));
    private static readonly SolidColorBrush BrushActorIcon = MakeFrozen(Color.FromRgb(0x55, 0xAA, 0xFF));

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

    // ── ランタイム接続 ────────────────────────────────────────

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
        });
    }

    private void OnSelectionChanged(int idx)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _selectedId = idx;
            _selectedIds.Clear();
            if (idx >= 0)
            {
                _selectedIds.Add(idx);
                SelectTreeItem(idx);
            }
            else
            {
                DeselectAll();
            }
            _anchorId = idx;
            UpdateMultiSelectVisuals();
        });
    }

    private void OnSelectionMultiChanged(IReadOnlyList<int> ids)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _selectedIds.Clear();
            foreach (var id in ids) _selectedIds.Add(id);
            _selectedId = ids.Count > 0 ? ids[0] : -1;
            _anchorId   = _selectedId;
            if (_selectedId >= 0)
                SelectTreeItem(_selectedId);
            else
                DeselectAll();
            UpdateMultiSelectVisuals();
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
        tb.Inlines.Add(new Run(node.IsGroup ? "▶ " : "◆ ")
        {
            Foreground = node.IsGroup ? BrushGroupIcon : BrushActorIcon,
            FontSize   = 9,
        });
        tb.Inlines.Add(new Run(node.Name) { FontSize = 13 });
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
            SendSelectionToRuntime();
        }
    }

    /// <summary>
    /// _selectedIds に含まれる非グループ ID を Rust へ送信する。
    /// 単一選択は SELECT:、複数選択は SELECT_MULTI:id1,id2,... を使う。
    /// </summary>
    private void SendSelectionToRuntime()
    {
        var ids = _selectedIds
            .Select(id => FindNode(_roots, id))
            .Where(n => n is { IsGroup: false })
            .Select(n => n!.Id)
            .ToList();

        if (ids.Count == 0) return;
        if (ids.Count == 1)
            _runtime?.SendToRuntime($"SELECT:{ids[0]}");
        else
            _runtime?.SendToRuntime($"SELECT_MULTI:{string.Join(",", ids)}");
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
        AddMenuItem(menu, "コピー",                 "Ctrl+C", OnHierarchyCopy);
        AddMenuItem(menu, "削除",                   "Del / Esc",    OnHierarchyDelete);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "選択からグループを作成", null,     OnCreateGroupFromSelection);
        return menu;
    }

    private ContextMenu BuildEmptyContextMenu()
    {
        var menu = new ContextMenu();
        AddMenuItem(menu, "グループフォルダを作成", null, OnCreateGroupMenu);
        return menu;
    }

    private void OnHierarchyCopy(object sender, RoutedEventArgs e)
        => _runtime?.SendToRuntime("COPY");

    private void OnHierarchyDelete(object sender, RoutedEventArgs e)
    {
        var ids = _selectedIds.ToList();
        if (ids.Count == 0) return;
        _runtime?.SendToRuntime($"DELETE_RECURSIVE:{string.Join(",", ids)}");
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
        _dragStart       = e.GetPosition(ActorTree);
        _isDragging      = false;
        _dragNodeIds     = new();
        _pendingDragNode = null;

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

        // 通常クリック
        if (item?.Tag is ActorNode normalNode)
        {
            // 複数選択中に選択済みアイテムをクリック → ドラッグしないなら MouseUp でデセレクト
            if (_selectedIds.Count > 1 && _selectedIds.Contains(normalNode.Id))
            {
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
        DragDrop.DoDragDrop(ActorTree, data, DragDropEffects.Move);

        _isDragging  = false;
        _dragNodeIds = new();
        _dropTarget  = null;
        _dropAsRoot   = false;
        _insertTarget = null;
        DropIndicator.Visibility = Visibility.Collapsed;
        ClearDropHighlight();
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

            if (relY <= zone || relY >= item.ActualHeight - zone)
            {
                _insertBefore            = relY <= zone;
                _insertTarget            = item;
                var lineY                = _insertBefore ? itemTop : itemTop + item.ActualHeight;
                DropIndicator.Margin     = new Thickness(0, lineY - 1, 0, 0);
                DropIndicator.Visibility = Visibility.Visible;
            }
            else
            {
                // 中央 → 子として追加
                _dropTarget = item;
                var border = FindVisualChild<Border>(item, "RowBorder");
                if (border != null)
                    border.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x33, 0x99, 0xFF));
            }
        }
        else
        {
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
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
        DropIndicator.Visibility = Visibility.Collapsed;

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

        // 複数ノードをまとめて親子付け変更
        // 1 つ目は兄弟挿入位置を使い、2 つ目以降は末尾に追加する
        bool first = true;
        foreach (var dragId in dragIds)
        {
            var dragNode = FindNode(_roots, dragId);
            if (dragNode == null) continue;
            _runtime?.SendToRuntime($"REPARENT:{dragNode.Id},{newParentId}");
            if (first)
            {
                ReparentInPlace(dragNode.Id, newParentId == -1 ? null : newParentId);
                first = false;
            }
            else
            {
                // 2 つ目以降は挿入位置なし（末尾に追加）
                var saved = _insertTarget;
                _insertTarget = null;
                ReparentInPlace(dragNode.Id, newParentId == -1 ? null : newParentId);
                _insertTarget = saved;
            }
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
    public List<int> GetSelectedNonGroupIds() =>
        _selectedIds
            .Where(id => FindNode(_roots, id) is { IsGroup: false })
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
                _runtime?.SendToRuntime($"RENAME:{nodeId},{newName}");
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
