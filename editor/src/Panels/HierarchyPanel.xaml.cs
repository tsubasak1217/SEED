using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private ActorNode?    _dragNode;
    private TreeViewItem? _dropTarget;   // 子として追加するターゲット
    private bool          _dropAsRoot;
    private TreeViewItem? _insertTarget; // 兄弟として挿入するターゲット
    private bool          _insertBefore; // insertTarget の前 or 後

    // リネーム用
    private DispatcherTimer? _renameTimer;
    private int              _pendingRenameId = -1;

    // 右クリック用
    private ActorNode? _rightClickedNode;

    // 複数選択用
    private HashSet<int> _selectedIds = new();
    private int          _anchorId    = -1;

    // ============================================================

    public HierarchyPanel()
    {
        InitializeComponent();
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
        });
    }

    private void OnSelectionChanged(int idx)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _selectedId = idx;
            _selectedIds.Clear();
            if (idx >= 0) _selectedIds.Add(idx);
            _anchorId = idx;
            SelectTreeItem(idx);
            UpdateMultiSelectVisuals();
        });
    }

    private void OnSelectionMultiChanged(IReadOnlyList<int> ids)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _selectedIds.Clear();
            foreach (var id in ids) _selectedIds.Add(id);
            _selectedId  = ids.Count > 0 ? ids[0] : -1;
            _anchorId    = _selectedId;
            SelectTreeItem(_selectedId);
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

    private static StackPanel BuildItemHeader(ActorNode node)
    {
        var icon = new TextBlock
        {
            Text              = node.IsGroup ? "▶" : "◆",
            Foreground        = node.IsGroup
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x44))
                : new SolidColorBrush(Color.FromRgb(0x55, 0xAA, 0xFF)),
            FontSize          = 9,
            Margin            = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text              = node.Name,
            FontSize          = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(icon);
        sp.Children.Add(label);
        return sp;
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

    private void OnTreeRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        var hit  = ActorTree.InputHitTest(e.GetPosition(ActorTree)) as DependencyObject;
        var item = FindAncestor<TreeViewItem>(hit);
        _rightClickedNode = item?.Tag as ActorNode;
    }

    private void OnCreateGroupMenu(object sender, RoutedEventArgs e)
    {
        // 右クリックしたノードの親と同じ階層にグループを作成
        int parentId = _rightClickedNode?.ParentId ?? -1;
        var name     = GetUniqueName("Group", -1);
        _runtime?.SendToRuntime($"CREATE_GROUP:{parentId},{name}");
    }

    // ── ドラッグ＆ドロップ ────────────────────────────────────

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart  = e.GetPosition(ActorTree);
        _isDragging = false;
        _dragNode   = null;

        if (e.ClickCount == 2)
        {
            CancelRenameTimer();
            return;
        }

        if (e.ClickCount != 1) return;

        var hit  = ActorTree.InputHitTest(_dragStart) as DependencyObject;
        var item = FindAncestor<TreeViewItem>(hit);

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

        // ドラッグ開始 → リネームタイマーをキャンセル
        CancelRenameTimer();
        _pendingRenameId = -1;

        var hit  = ActorTree.InputHitTest(_dragStart) as DependencyObject;
        var item = FindAncestor<TreeViewItem>(hit);
        if (item?.Tag is not ActorNode node) return;

        _isDragging = true;
        _dragNode   = node;

        var data = new DataObject("ActorNode", node);
        DragDrop.DoDragDrop(ActorTree, data, DragDropEffects.Move);

        _isDragging   = false;
        _dragNode     = null;
        _dropTarget   = null;
        _dropAsRoot   = false;
        _insertTarget = null;
        DropIndicator.Visibility = Visibility.Collapsed;
        ClearDropHighlight();
    }

    private void OnTreeDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("ActorNode"))
            e.Effects = DragDropEffects.None;
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("ActorNode"))
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
            if (_dragNode != null &&
                (targetNode.Id == _dragNode.Id || IsDescendant(_dragNode, targetNode.Id)))
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

        if (!e.Data.GetDataPresent("ActorNode")) return;
        var dragNode = (ActorNode)e.Data.GetData("ActorNode");

        int newParentId;
        if (_insertTarget?.Tag is ActorNode siblingNode)
        {
            // 兄弟として挿入 → sibling と同じ親にする（親子付け解除に相当）
            newParentId = siblingNode.ParentId ?? -1;
        }
        else if (_dropTarget?.Tag is ActorNode targetNode)
        {
            newParentId = targetNode.Id;
        }
        else if (_dropAsRoot)
        {
            newParentId = -1;
        }
        else
        {
            return;
        }

        _runtime?.SendToRuntime($"REPARENT:{dragNode.Id},{newParentId}");
        ReparentInPlace(dragNode.Id, newParentId == -1 ? null : newParentId);

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

        tb.KeyDown  += (_, e) =>
        {
            if (e.Key == Key.Return) { Commit(); e.Handled = true; }
            if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
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
            obj = VisualTreeHelper.GetParent(obj);
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
