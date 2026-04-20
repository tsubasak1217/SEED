using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SEEDEditor.Panels;

public partial class ProjectPanel : UserControl
{
    // ── 状態 ──────────────────────────────────────────────────────

    private string             _assetsRoot  = "";
    private string             _currentPath = "";
    private Border?            _selectedItem;
    private FileSystemWatcher? _watcher;
    private bool               _suppressTreeEvent;

    private static readonly TreeViewItem DummyItem = new();

    // ── アイコン URI ──────────────────────────────────────────────

    private static Uri PackUri(string name)
        => new($"pack://application:,,,/resources/icons/{name}", UriKind.Absolute);

    private static readonly Uri UriFolder      = PackUri("folder.png");
    private static readonly Uri UriFolderEmpty = PackUri("folder_empty.png");
    private static readonly Uri UriImage       = PackUri("image.png");
    private static readonly Uri UriModel       = PackUri("model.png");
    private static readonly Uri UriScene       = PackUri("scene.png");
    private static readonly Uri UriScript      = PackUri("script.png");

    // 画像プレビュー対象の拡張子
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tga", ".hdr", ".exr", ".webp" };

    // 拡張子 → アイコン URI
    private static Uri GetFileIconUri(string ext) => ext.ToLowerInvariant() switch
    {
        ".scene"                              => UriScene,
        ".glb" or ".gltf" or ".obj" or ".fbx" => UriModel,
        ".lua" or ".cs" or ".py" or ".wgsl"  => UriScript,
        _ when ImageExts.Contains(ext)        => UriImage,
        _                                     => UriImage,
    };

    // ─────────────────────────────────────────────────────────────

    public ProjectPanel()
    {
        InitializeComponent();
        MouseDown += OnPanelMouseDown;
    }

    // ── 公開 API ──────────────────────────────────────────────────

    public void SetAssetsPath(string assetsPath)
    {
        _assetsRoot  = assetsPath;
        _currentPath = assetsPath;

        BuildFolderTree();
        RefreshFileGrid();
        StartWatcher();
    }

    // ── フォルダツリー構築 ────────────────────────────────────────

    private void BuildFolderTree()
    {
        FolderTree.Items.Clear();
        if (!Directory.Exists(_assetsRoot)) return;

        var root = BuildTreeNode(new DirectoryInfo(_assetsRoot));
        root.IsExpanded = true;
        root.Collapsed  += (_, _) => root.IsExpanded = true;
        FolderTree.Items.Add(root);
    }

    private TreeViewItem BuildTreeNode(DirectoryInfo dir)
    {
        var item = new TreeViewItem
        {
            Header = BuildFolderHeader(dir.Name, isRoot: dir.FullName == _assetsRoot),
            Tag    = dir.FullName,
        };
        if (dir.EnumerateDirectories().Any())
        {
            item.Items.Add(DummyItem);
            item.Expanded += OnNodeExpanded;
        }
        return item;
    }

    private void OnNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item) return;
        if (item.Items.Count == 1 && item.Items[0] == DummyItem)
        {
            item.Items.Clear();
            if (item.Tag is string path && Directory.Exists(path))
            {
                foreach (var sub in Directory.GetDirectories(path).OrderBy(p => p))
                    item.Items.Add(BuildTreeNode(new DirectoryInfo(sub)));
            }
        }
        e.Handled = true;
    }

    private static StackPanel BuildFolderHeader(string name, bool isRoot)
    {
        var icon = new Image
        {
            Source = new BitmapImage(UriFolder),
            Width  = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var label = new TextBlock
        {
            Text              = isRoot ? "Assets" : name,
            FontSize          = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(icon);
        sp.Children.Add(label);
        return sp;
    }

    // ── ツリー選択 ────────────────────────────────────────────────

    private void OnFolderTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeEvent) return;
        if (FolderTree.SelectedItem is TreeViewItem { Tag: string path })
            NavigateTo(path);
    }

    // ── ファイルグリッド更新 ──────────────────────────────────────

    private void RefreshFileGrid()
    {
        FileGrid.Children.Clear();

        var rel = Path.GetRelativePath(_assetsRoot, _currentPath);
        TxtBreadcrumb.Text = rel == "." ? "Assets" : "Assets/" + rel.Replace('\\', '/');
        BtnBack.IsEnabled  = !PathEquals(_currentPath, _assetsRoot);

        if (!Directory.Exists(_currentPath)) return;

        foreach (var dir in Directory.GetDirectories(_currentPath).OrderBy(p => p))
            FileGrid.Children.Add(BuildDirItem(new DirectoryInfo(dir)));

        foreach (var file in Directory.GetFiles(_currentPath).OrderBy(p => p))
            FileGrid.Children.Add(BuildFileItem(new FileInfo(file)));
    }

    // ── アイテム構築 ──────────────────────────────────────────────

    private UIElement BuildDirItem(DirectoryInfo dir)
    {
        bool isEmpty = !dir.EnumerateFileSystemInfos().Any();
        var  uri     = isEmpty ? UriFolderEmpty : UriFolder;
        var  imgCtrl = MakeIconImage(uri, 66);
        var  item    = WrapTile(imgCtrl, dir.Name, dir.FullName);
        AttachItemEvents(item, dir);
        return item;
    }

    private UIElement BuildFileItem(FileInfo file)
    {
        bool isImageFile = ImageExts.Contains(file.Extension);
        var  iconUri     = GetFileIconUri(file.Extension);
        var  imgCtrl     = MakeIconImage(iconUri, 66);
        var  item        = WrapTile(imgCtrl, file.Name, file.FullName);

        if (isImageFile)
            _ = LoadImagePreviewAsync(imgCtrl, file.FullName);

        AttachItemEvents(item, file);
        return item;
    }

    private static Image MakeIconImage(Uri uri, int size)
    {
        var bmp = new BitmapImage(uri);
        var img = new Image
        {
            Source  = bmp,
            Width   = size,
            Height  = size,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin  = new Thickness(0, 6, 0, 3),
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    private static Border WrapTile(Image iconCtrl, string name, string? fullPath)
    {
        var nameBlock = new TextBlock
        {
            Text                = name,
            Foreground          = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize            = 11,
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping        = TextWrapping.Wrap,
            MaxWidth            = 106,
            MaxHeight           = 32,
        };
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(iconCtrl);
        sp.Children.Add(nameBlock);

        return new Border
        {
            Width        = 116,
            Height       = 116,
            Margin       = new Thickness(3),
            Background   = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Cursor       = Cursors.Hand,
            Tag          = fullPath,
            ToolTip      = fullPath,
            Child        = sp,
        };
    }

    // ── 画像プレビュー非同期ロード ────────────────────────────────

    private async Task LoadImagePreviewAsync(Image imgCtrl, string filePath)
    {
        var bitmap = await Task.Run(() =>
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource        = new Uri(filePath, UriKind.Absolute);
                bmp.DecodePixelWidth = 80;
                bmp.CacheOption      = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return (BitmapSource)bmp;
            }
            catch { return null; }
        });

        if (bitmap != null)
        {
            imgCtrl.Source  = bitmap;
            imgCtrl.Width   = 90;
            imgCtrl.Height  = 90;
            imgCtrl.Margin  = new Thickness(0, 3, 0, 0);
            imgCtrl.Stretch = Stretch.Uniform;
            imgCtrl.Clip    = new RectangleGeometry(new Rect(0, 0, 90, 90), 3, 3);
        }
    }

    // ── アイテムイベント ──────────────────────────────────────────

    private void AttachItemEvents(Border item, FileSystemInfo? entry)
    {
        item.MouseEnter += (_, _) =>
        {
            if (item != _selectedItem)
                item.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        };
        item.MouseLeave += (_, _) =>
        {
            if (item != _selectedItem)
                item.Background = Brushes.Transparent;
        };
        item.MouseLeftButtonDown += (_, e) =>
        {
            SelectItem(item);

            if (e.ClickCount == 2)
            {
                if (entry is DirectoryInfo dir)
                {
                    NavigateTo(dir.FullName);
                }
                else if (item.Tag is string t && t == "..")
                {
                    var parent = Directory.GetParent(_currentPath)?.FullName;
                    if (parent != null && IsUnderAssets(parent))
                        NavigateTo(parent);
                }
            }

            e.Handled = true;
        };
    }

    private void SelectItem(Border item)
    {
        if (_selectedItem != null)
            _selectedItem.Background = Brushes.Transparent;
        _selectedItem = item;
        item.Background = new SolidColorBrush(Color.FromArgb(0x44, 0x33, 0x99, 0xFF));
    }

    // ── ナビゲーション ────────────────────────────────────────────

    private void OnBackClick(object sender, RoutedEventArgs e) => GoUp();

    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
        {
            GoUp();
            e.Handled = true;
        }
    }

    private void GoUp()
    {
        if (PathEquals(_currentPath, _assetsRoot)) return;
        var parent = Directory.GetParent(_currentPath)?.FullName;
        if (parent != null && IsUnderAssets(parent))
            NavigateTo(parent);
    }

    private void NavigateTo(string path)
    {
        if (!Directory.Exists(path)) return;
        _currentPath  = path;
        _selectedItem = null;
        RefreshFileGrid();
        SyncTreeSelection(path);
    }

    private void SyncTreeSelection(string dirPath)
    {
        _suppressTreeEvent = true;
        try { SyncTreeItem(FolderTree.Items, dirPath); }
        finally { _suppressTreeEvent = false; }
    }

    private bool SyncTreeItem(ItemCollection items, string target)
    {
        foreach (TreeViewItem item in items)
        {
            if (item.Tag is string path && PathEquals(path, target))
            {
                item.IsSelected = true;
                item.BringIntoView();
                return true;
            }
            if (SyncTreeItem(item.Items, target)) return true;
        }
        return false;
    }

    // ── FileSystemWatcher ─────────────────────────────────────────

    private void StartWatcher()
    {
        _watcher?.Dispose();
        if (!Directory.Exists(_assetsRoot)) return;

        _watcher = new FileSystemWatcher(_assetsRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter          = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            EnableRaisingEvents   = true,
        };
        _watcher.Created += OnFsChanged;
        _watcher.Deleted += OnFsChanged;
        _watcher.Renamed += OnFsChanged;
    }

    private void OnFsChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            BuildFolderTree();
            if (!Directory.Exists(_currentPath))
                _currentPath = _assetsRoot;
            RefreshFileGrid();
        });
    }

    // ── ユーティリティ ────────────────────────────────────────────

    private static bool PathEquals(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private bool IsUnderAssets(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(_assetsRoot).TrimEnd(Path.DirectorySeparatorChar);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
