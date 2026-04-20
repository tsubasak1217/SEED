using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    private TreeViewItem? _dropTarget;
    private bool          _dropAsRoot;

    // リネーム用
    private DispatcherTimer? _renameTimer;
    private int              _pendingRenameId = -1;

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
            _runtime.HierarchyUpdated -= OnHierarchyUpdated;
            _runtime.SelectionChanged -= OnSelectionChanged;
        }
        _runtime = runtime;
        _runtime.HierarchyUpdated += OnHierarchyUpdated;
        _runtime.SelectionChanged += OnSelectionChanged;
    }

    // ── Runtime イベント ──────────────────────────────────────

    private void OnHierarchyUpdated(string json)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _roots = ParseHierarchy(json);
            RebuildTree(_roots);
        });
    }

    private void OnSelectionChanged(int idx)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _selectedId = idx;
            SelectTreeItem(idx);
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
            Text              = "◆",
            Foreground        = new SolidColorBrush(Color.FromRgb(0x55, 0xAA, 0xFF)),
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
            _runtime?.SendToRuntime($"SELECT:{node.Id}");
        }
    }

    private void OnItemSelected(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_suppressSelectionEvent) return;
        if (sender is TreeViewItem { Tag: ActorNode node })
        {
            _selectedId = node.Id;
            _runtime?.SendToRuntime($"SELECT:{node.Id}");
        }
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

    // ── ドラッグ＆ドロップ ────────────────────────────────────

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart  = e.GetPosition(ActorTree);
        _isDragging = false;
        _dragNode   = null;

        if (e.ClickCount == 2)
        {
            // ダブルクリックはリネームタイマーをキャンセル
            CancelRenameTimer();
            return;
        }

        if (e.ClickCount == 1)
        {
            var hit  = ActorTree.InputHitTest(_dragStart) as DependencyObject;
            var item = FindAncestor<TreeViewItem>(hit);

            if (item?.Tag is ActorNode node && node.Id == _selectedId)
            {
                // 選択済みアイテムへの再クリック → タイマーでリネーム起動
                _pendingRenameId = node.Id;
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

        _isDragging  = false;
        _dragNode    = null;
        _dropTarget  = null;
        _dropAsRoot  = false;
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
            _dropTarget = item;
            var border = FindVisualChild<Border>(item, "RowBorder");
            if (border != null)
                border.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x33, 0x99, 0xFF));
        }
        else
        {
            _dropAsRoot = true;
            DropIndicator.Visibility = Visibility.Visible;
        }
        e.Handled = true;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
        DropIndicator.Visibility = Visibility.Collapsed;
        _dropTarget = null;
        _dropAsRoot = false;
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
        DropIndicator.Visibility = Visibility.Collapsed;

        if (!e.Data.GetDataPresent("ActorNode")) return;
        var dragNode = (ActorNode)e.Data.GetData("ActorNode");

        string msg;
        if (_dropTarget?.Tag is ActorNode targetNode)
            msg = $"REPARENT:{dragNode.Id},{targetNode.Id}";
        else if (_dropAsRoot)
            msg = $"REPARENT:{dragNode.Id},-1";
        else
            return;

        _runtime?.SendToRuntime(msg);
        _dropTarget = null;
        _dropAsRoot = false;
        e.Handled   = true;
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
}
