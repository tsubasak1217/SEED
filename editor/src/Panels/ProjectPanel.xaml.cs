using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SEEDEditor;
using SEEDEditor.Panels.ScriptEditor;

namespace SEEDEditor.Panels;

/// <summary>
/// プロジェクトのアセットフォルダをエクスプローラー風に表示・操作するドッキングパネル。
/// ファイル/フォルダの一覧表示、ドラッグ&amp;ドロップ、リネーム・削除（ごみ箱送り）、
/// 新規作成、ダブルクリックでの各種エディタ起動などを担当する。
/// </summary>
public partial class ProjectPanel : UserControl
{
    // ── P/Invoke (ごみ箱へ送る) ──────────────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>
    /// Win32 SHFileOperation API のパラメータ構造体。ファイル削除を「ごみ箱へ送る」形で
    /// 実行する（FOF_ALLOWUNDO）ために使用する P/Invoke 用マーシャリング型。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEOPSTRUCT
    {
        public nint   hwnd;
        public uint   wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public bool   fAnyOperationsAborted;
        public nint   hNameMappings;
        public string lpszProgressTitle;
    }

    private const uint   FO_DELETE          = 0x0003;
    private const ushort FOF_ALLOWUNDO      = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT         = 0x0004;

    private static void RecycleFile(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc  = FO_DELETE,
            pFrom  = path + '\0' + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
        };
        SHFileOperation(ref op);
    }

    // ── 状態 ──────────────────────────────────────────────────────

    private string             _assetsRoot  = "";
    private string             _currentPath = "";
    private FileSystemWatcher? _watcher;
    private bool               _suppressTreeEvent;

    // Hierarchy からドラッグされたアクタのファイル化（EXPORT_ACTOR）送信に使う Runtime 参照。
    // MainWindow が SetRuntime で注入する。
    private SEEDEditor.Runtime.RuntimeManager? _runtime;

    // 複数選択
    private readonly HashSet<Border> _selectedItems = new();
    private Border?                  _lastClickedItem;

    // ファイルクリップボード
    private (List<string> Paths, bool IsCut)? _fileClipboard;

    // ドラッグ範囲選択
    private bool                              _dragSelectActive;
    private bool                              _dragThresholdMet;
    private Point                             _dragStart;         // FileScrollViewer 座標
    private HashSet<Border>                   _selectionAtDragStart = new();
    private System.Windows.Shapes.Rectangle? _selRectShape;

    // アイテムドラッグ（移動）
    private Border? _itemDragStartTile;
    // 複数選択中に選択済みタイルをクリックしたとき SelectSingle をマウスアップまで遅延するためのフィールド
    private Border? _pendingSingleSelectTile;

    // リネーム中フラグ・新規フォルダ用
    private bool    _isRenaming;
    private string? _pendingRenameFolder;

    // 次回 RefreshFileGrid で選択状態にして可視域へ入れるファイル/フォルダの絶対パス。
    // タブ切替時の選択復元と、参照フィールドからのジャンプ（RevealFile）で使う。
    private string? _pendingSelectPath;

    // 再クリックリネーム用タイマー
    private Border?                                        _renamePendingTile;
    private System.Windows.Threading.DispatcherTimer?     _renameTimer;

    private static TreeViewItem NewDummy() => new();

    // ── アイコン URI ──────────────────────────────────────────────

    // 拡張子・フォルダ -> アイコンの対応表は Controls/FileTypeIcons.cs が唯一の正典。
    // ここでは「FileTypeIcons が返した ImageSource を Image へ載せる」ことだけを行う。
    // PNG かベクターかは FileTypeIcons が決めるので、このパネルは区別しない。
    // Image を使い続けているのは、画像ファイルのタイルがサムネイル生成後に
    // Source を実画像へ差し替える作りだから。ベクターアイコンも DrawingImage として
    // 同じ Source に載るので、「形式アイコン → サムネイル」の差し替えが
    // 1 つのコントロールで完結する。

    /// <summary>フォルダツリーのヘッダーアイコンの一辺サイズ（px）。</summary>
    private const int FolderHeaderIconSize = 20;

    /// <summary>ファイルグリッドのタイルアイコンの一辺サイズ（px）。</summary>
    private const int TileIconSize = 66;

    // ─────────────────────────────────────────────────────────────

    public ProjectPanel()
    {
        InitializeComponent();
        MouseDown += OnPanelMouseDown;
        WireScrollViewerEvents();
    }

    // ── 公開 API (MainWindow から呼ぶ) ────────────────────────────

    /// <summary>.scene ファイルがダブルクリックされたときに発火する（フルパス）。</summary>
    public event Action<string>? SceneFileOpened;

    /// <summary>.actor ファイルがダブルクリックされたときに発火する（フルパス）。</summary>
    public event Action<string>? ActorFileOpened;

    /// <summary>.inputmap ファイルがダブルクリックされたときに発火する（フルパス）。</summary>
    public event Action<string>? InputMapFileOpened;

    /// <summary>
    /// スクリプトエディタで編集できるファイル（.cs / .wgsl）がダブルクリックされたときに
    /// 発火する（フルパス）。内蔵スクリプトエディタのタブで開く。
    /// </summary>
    public event Action<string>? ScriptFileOpened;
    /// <summary>.anim ファイルがダブルクリックされた（絶対パス）。AnimationTimelinePanel での編集起動用。</summary>
    public event Action<string>? AnimFileOpened;

    /// <summary>
    /// 画像ファイルの右クリックメニューから「スプライトリグを作成」が選ばれた（絶対パス）。
    /// スプライトリグパネルを開き、その画像の新しい編集タブを作る。
    /// </summary>
    public event Action<string>? SpriteRigCreateRequested;

    /// <summary>
    /// .sprite_mesh がダブルクリックされた／右クリックメニューから編集が選ばれた（絶対パス）。
    /// スプライトリグパネルで再編集する。
    /// </summary>
    public event Action<string>? SpriteMeshFileOpened;

    public void SetAssetsPath(string assetsPath)
    {
        _assetsRoot  = assetsPath;
        _currentPath = assetsPath;
        BuildFolderTree();
        // タブ機構を初期化する（ルートフォルダを開いた 1 枚だけの状態から始める）
        InitTabs(assetsPath);
        RefreshFileGrid();
        StartWatcher();
    }

    /// <summary>
    /// Runtime 参照を注入する。Hierarchy からドラッグされたアクタを
    /// アクタファイル化（EXPORT_ACTOR 送信）するために使用する。
    /// </summary>
    public void SetRuntime(SEEDEditor.Runtime.RuntimeManager runtime) => _runtime = runtime;

    public void HandleCopy()  => DoCopy();
    public void HandleCut()   => DoCut();
    public void HandlePaste() => DoPaste();

    // ── 新規作成ボタン ────────────────────────────────────────────

    private void OnCreateClick(object sender, RoutedEventArgs e) => OpenCreateItemWindow(_currentPath);

    /// <summary>
    /// 新規作成ウィンドウ（CreateItemWindow）を開く。作成先は引数のフォルダ。
    /// ツールバーの「新規作成」ボタンと、右クリックメニューの「新規作成」の共通入口。
    /// </summary>
    /// <param name="targetDir">作成先フォルダの絶対パス。存在しない場合は何もしない。</param>
    private void OpenCreateItemWindow(string targetDir)
    {
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;

        var win = new SEEDEditor.CreateItemWindow(targetDir)
        {
            Owner = Window.GetWindow(this),
        };
        win.ItemCreated += createdPath =>
        {
            // 作成後にインラインリネームを開始する
            _pendingRenameFolder = createdPath;
            RefreshFileGrid();
        };
        win.Show();
    }

    // ── ScrollViewer イベント配線 ─────────────────────────────────

    private void WireScrollViewerEvents()
    {
        // 空エリアクリックで hit-test を受け取るために背景を設定
        FileGrid.Background = Brushes.Transparent;

        // ドラッグ範囲選択
        FileScrollViewer.PreviewMouseLeftButtonDown += OnSVPreviewLMBDown;
        FileScrollViewer.MouseLeftButtonDown        += OnSVLMBDown;
        FileScrollViewer.PreviewMouseMove           += OnSVPreviewMouseMove;
        FileScrollViewer.PreviewMouseLeftButtonUp   += OnSVPreviewLMBUp;

        // 右クリック（背景）
        FileScrollViewer.MouseRightButtonDown += OnSVRMBDown;

        // キーボード（Delete）
        FileScrollViewer.PreviewKeyDown += OnSVPreviewKeyDown;

        // Hierarchy パネルからのアクタドロップ（背景＝現在フォルダへアクタファイル化）を受ける
        FileScrollViewer.AllowDrop =  true;
        FileScrollViewer.DragEnter += OnSVDragOverActor;
        FileScrollViewer.DragOver  += OnSVDragOverActor;
        FileScrollViewer.Drop      += OnSVDropActor;
    }

    // ── Hierarchy からのアクタドロップ（アクタファイル化） ─────────

    /// <summary>ドラッグデータに Hierarchy アクタ情報（DragIds / HierarchyActorDfsId）が含まれるか。</summary>
    private static bool HasHierarchyActorData(IDataObject data)
        => data.GetDataPresent("DragIds") || data.GetDataPresent("HierarchyActorDfsId");

    /// <summary>ファイル一覧背景の DragEnter/DragOver: アクタドラッグなら Copy 効果を表示する。</summary>
    private void OnSVDragOverActor(object sender, DragEventArgs e)
    {
        if (HasHierarchyActorData(e.Data))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    /// <summary>ファイル一覧背景への Drop: 現在フォルダへアクタファイル化する。</summary>
    private void OnSVDropActor(object sender, DragEventArgs e)
    {
        if (!HasHierarchyActorData(e.Data)) return;
        HandleHierarchyActorDrop(e.Data, _currentPath);
        e.Handled = true;
    }

    /// <summary>
    /// Hierarchy からドラッグされたアクタ群を destDir にアクタファイルとして書き出す。
    /// 各アクタについて EXPORT_ACTOR:{dfsId},{fullPath} を Runtime へ送信する。
    /// 同名ファイルが存在／同一ドロップ内で重複する場合は "名前 (2).actor" 式に連番で回避する。
    /// エクスポート完了後のファイル一覧更新は FileSystemWatcher が拾って行う。
    /// </summary>
    private void HandleHierarchyActorDrop(IDataObject data, string destDir)
    {
        if (_runtime == null || string.IsNullOrEmpty(destDir) || !Directory.Exists(destDir)) return;

        // DragIds（複数選択対応）を優先。無ければ単一 HierarchyActorDfsId にフォールバック。
        List<int> ids;
        if (data.GetDataPresent("DragIds") && data.GetData("DragIds") is List<int> dragIds)
            ids = dragIds;
        else if (data.GetDataPresent("HierarchyActorDfsId") && data.GetData("HierarchyActorDfsId") is int single)
            ids = new List<int> { single };
        else return;

        // 種別（2D/3D）と名前は DragIds と同順で積まれている（欠落時は既定値にフォールバック）
        var is2DList = data.GetData("HierarchyActorIds2D") as List<bool>;
        var nameList = data.GetData("HierarchyActorNames") as List<string>;

        // 同一ドロップ内での同名重複を避けるため、確定パスを予約集合で管理する
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < ids.Count; i++)
        {
            int    dfsId    = ids[i];
            bool   is2D     = is2DList != null && i < is2DList.Count && is2DList[i];
            string baseName = nameList != null && i < nameList.Count ? nameList[i] : $"Actor_{dfsId}";
            string ext      = is2D ? ".actor2d" : ".actor";
            string fullPath = MakeUniqueActorPath(destDir, SanitizeFileName(baseName), ext, reserved);
            reserved.Add(fullPath);
            _runtime.SendToRuntime($"EXPORT_ACTOR:{dfsId},{fullPath}");
        }
    }

    /// <summary>ファイル名に使えない文字をアンダースコアへ置換する。空になった場合は "Actor"。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb      = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? "Actor" : result;
    }

    /// <summary>
    /// destDir 内で未使用のアクタファイルパスを返す。
    /// 既存ファイルまたは reserved に含まれる場合は "stem (2).ext", "stem (3).ext" … と連番で回避する。
    /// </summary>
    private static string MakeUniqueActorPath(string dir, string stem, string ext, HashSet<string> reserved)
    {
        string candidate = Path.Combine(dir, stem + ext);
        int n = 2;
        while (File.Exists(candidate) || reserved.Contains(candidate))
        {
            candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            n++;
        }
        return candidate;
    }

    // ── フォルダツリー ────────────────────────────────────────────

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
            item.Items.Add(NewDummy());
            item.Expanded += OnNodeExpanded;
        }
        return item;
    }

    private void OnNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item) return;
        MaterializeChildren(item);
        e.Handled = true;
    }

    /// <summary>
    /// 遅延生成ノード（ダミー 1 個だけを持つ TreeViewItem）の子を実体化する。
    /// すでに実体化済み、または子を持たないノードでは何もしない。
    /// 展開イベント経由（OnNodeExpanded）とタブ復元のプログラム展開（ExpandToFolder）の
    /// 双方から呼ぶため、判定と生成をこの 1 箇所に集約している。
    /// </summary>
    private void MaterializeChildren(TreeViewItem item)
    {
        if (item.Items.Count != 1) return;
        if (item.Items[0] is not TreeViewItem { Tag: null, Header: null }) return;

        item.Items.Clear();
        if (item.Tag is string path && Directory.Exists(path))
        {
            foreach (var sub in Directory.GetDirectories(path).OrderBy(p => p))
                item.Items.Add(BuildTreeNode(new DirectoryInfo(sub)));
        }
    }

    private static StackPanel BuildFolderHeader(string name, bool isRoot)
    {
        var icon = new Image
        {
            Source            = SEEDEditor.Controls.FileTypeIcons.GetFolderImage(isEmpty: false),
            Width             = FolderHeaderIconSize,
            Height            = FolderHeaderIconSize,
            Margin            = new Thickness(0, 0, 5, 0),
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

    private void OnFolderTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeEvent) return;
        if (FolderTree.SelectedItem is TreeViewItem { Tag: string path })
            NavigateTo(path);
    }

    // ── ファイルグリッド更新 ──────────────────────────────────────

    private void RefreshFileGrid()
    {
        ClearAllSelection();
        HideDragRect();
        _dragSelectActive = false;
        FileGrid.Children.Clear();

        var rel = Path.GetRelativePath(_assetsRoot, _currentPath);
        TxtBreadcrumb.Text = rel == "." ? "Assets" : "Assets/" + rel.Replace('\\', '/');
        BtnBack.IsEnabled  = !PathEquals(_currentPath, _assetsRoot);

        if (!Directory.Exists(_currentPath)) return;

        foreach (var dir in Directory.GetDirectories(_currentPath).OrderBy(p => p))
            FileGrid.Children.Add(BuildDirItem(new DirectoryInfo(dir)));

        foreach (var file in Directory.GetFiles(_currentPath).OrderBy(p => p))
            FileGrid.Children.Add(BuildFileItem(new FileInfo(file)));

        // タブ切替・ジャンプ要求で指定されたファイルを選択状態にして可視域へ入れる。
        // 一度使ったら消費する（以降の再描画で選択が復活しないように）。
        if (_pendingSelectPath != null)
        {
            var wanted = _pendingSelectPath;
            _pendingSelectPath = null;
            var target = FileGrid.Children.OfType<Border>()
                .FirstOrDefault(b => b.Tag is string p && PathEquals(p, wanted));
            if (target != null)
            {
                SelectSingle(target, ctrl: false, shift: false);
                // レイアウト確定後でないと BringIntoView が正しい位置を計算できないため遅延実行する
                Dispatcher.BeginInvoke(() => target.BringIntoView(),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        // 新規ファイル/フォルダ作成後のリネーム
        // _pendingRenameFolder はここでは消さない。StartRenameMode が実行されるまで
        // FSW の再描画を抑制するために使い続ける。
        if (_pendingRenameFolder != null)
        {
            var target = _pendingRenameFolder;
            var tile = FileGrid.Children.OfType<Border>()
                .FirstOrDefault(b => b.Tag is string p && PathEquals(p, target));
            if (tile != null)
            {
                SelectSingle(tile, ctrl: false, shift: false);
                Dispatcher.BeginInvoke(() => StartRenameMode(tile),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }

    // ── アイテム構築 ──────────────────────────────────────────────

    private UIElement BuildDirItem(DirectoryInfo dir)
    {
        bool isEmpty = !dir.EnumerateFileSystemInfos().Any();
        var  item    = WrapTile(
            MakeIconImage(SEEDEditor.Controls.FileTypeIcons.GetFolderImage(isEmpty), TileIconSize),
            dir.Name, dir.FullName);
        AttachItemEvents(item, dir);
        AttachDropTarget(item);
        return item;
    }

    /// <summary>
    /// ファイル 1 個ぶんのタイルを作る。
    ///
    /// アイコンは必ず「拡張子から引いた形式アイコン」で先に描く。サムネイルを
    /// 生成できる形式のときだけ非同期プレビューを走らせ、成功した場合に限り
    /// 実画像へ差し替える。したがってプレビュー生成前・生成中・生成失敗の間は
    /// 常に形式アイコンが見えたままになる（未知の拡張子は汎用ファイルアイコン）。
    /// </summary>
    private UIElement BuildFileItem(FileInfo file)
    {
        var imgCtrl = MakeIconImage(
            SEEDEditor.Controls.FileTypeIcons.GetImage(file.Extension), TileIconSize);
        var item    = WrapTile(imgCtrl, file.Name, file.FullName);
        if (SEEDEditor.Controls.FileTypeIcons.SupportsThumbnail(file.Extension))
            _ = LoadImagePreviewAsync(imgCtrl, file.FullName);
        AttachItemEvents(item, file);
        return item;
    }

    /// <summary>
    /// アイコン画像から、タイル用の <see cref="Image"/> コントロールを作る。
    /// </summary>
    /// <param name="icon">FileTypeIcons が返した形式アイコン（PNG かベクター）。</param>
    /// <param name="size">一辺のサイズ（px）。</param>
    private static Image MakeIconImage(ImageSource icon, int size)
    {
        var img = new Image
        {
            Source              = icon,
            Width               = size,
            Height              = size,
            Stretch             = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 6, 0, 3),
        };
        // PNG アイコンを拡大表示するため、縮小拡大の品質を明示する。
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
            if (!_selectedItems.Contains(item))
                item.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        };
        item.MouseLeave += (_, _) =>
        {
            if (!_selectedItems.Contains(item))
                item.Background = Brushes.Transparent;
        };
        item.MouseLeftButtonDown += (_, e) =>
        {
            bool ctrl  = Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl);
            bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

            if (e.ClickCount == 2 && !ctrl && !shift)
            {
                CancelRenameSchedule();
                if (entry is DirectoryInfo dir)
                    NavigateTo(dir.FullName);
                else if (entry is FileInfo file &&
                         file.Extension.Equals(".scene", StringComparison.OrdinalIgnoreCase))
                    SceneFileOpened?.Invoke(file.FullName);
                else if (entry is FileInfo actorFile &&
                         (actorFile.Extension.Equals(".actor",   StringComparison.OrdinalIgnoreCase) ||
                          actorFile.Extension.Equals(".actor2d", StringComparison.OrdinalIgnoreCase)))
                    ActorFileOpened?.Invoke(actorFile.FullName);
                else if (entry is FileInfo imFile &&
                         imFile.Extension.Equals(".inputmap", StringComparison.OrdinalIgnoreCase))
                    InputMapFileOpened?.Invoke(imFile.FullName);
                else if (entry is FileInfo scriptFile &&
                         EditorLanguages.IsEditableExtension(scriptFile.Extension))
                    // .cs（C# スクリプト）と .wgsl（シェーディングアセット）は
                    // どちらも内蔵スクリプトエディタのタブで開く。
                    ScriptFileOpened?.Invoke(scriptFile.FullName);
                else if (entry is FileInfo animFile &&
                         animFile.Extension.Equals(".anim", StringComparison.OrdinalIgnoreCase))
                    AnimFileOpened?.Invoke(animFile.FullName);
                else if (entry is FileInfo matFile &&
                         matFile.Extension.Equals(".mat", StringComparison.OrdinalIgnoreCase))
                    // Phase R7 最小実装: 専用エディタパネルは未実装のため、既定の関連付けアプリ（テキストエディタ等）
                    // で開く。JSON テキストなので Windows の既定関連付けがあれば十分編集可能。
                    OpenMaterialFile(matFile.FullName);
                else if (entry is FileInfo meshFile &&
                         meshFile.Extension.Equals(SpriteRig.IO.SpriteMeshFile.Extension,
                                                   StringComparison.OrdinalIgnoreCase))
                    // .sprite_mesh はスプライトリグパネルで再編集する
                    SpriteMeshFileOpened?.Invoke(meshFile.FullName);
            }
            else if (e.ClickCount == 1)
            {
                bool alreadyAlone = _selectedItems.Count == 1 && _selectedItems.Contains(item);
                if (!ctrl && !shift && alreadyAlone)
                {
                    // 選択済み単独アイテムへの再クリック → リネーム予約
                    ScheduleRename(item);
                }
                else if (!ctrl && !shift && _selectedItems.Count > 1 && _selectedItems.Contains(item))
                {
                    // 複数選択中に選択済みアイテムをクリック
                    // → ドラッグ開始の可能性があるため SelectSingle をマウスアップまで遅延
                    _pendingSingleSelectTile = item;
                }
                else
                {
                    CancelRenameSchedule();
                    SelectSingle(item, ctrl, shift);
                }
                FileScrollViewer.Focus();
            }
            e.Handled = true;
        };
        item.MouseRightButtonDown += (_, e) =>
        {
            if (!_selectedItems.Contains(item))
                SelectSingle(item, ctrl: false, shift: false);
            FileScrollViewer.Focus();
            ShowItemContextMenu();
            e.Handled = true;
        };
    }

    // ── 選択管理 ─────────────────────────────────────────────────

    private void SelectSingle(Border tile, bool ctrl, bool shift)
    {
        if (shift && _lastClickedItem != null)
        {
            if (!ctrl) ClearAllSelection();
            RangeSelect(_lastClickedItem, tile);
        }
        else if (ctrl)
        {
            ToggleSelection(tile);
            _lastClickedItem = tile;
        }
        else
        {
            ClearAllSelection();
            AddToSelection(tile);
            _lastClickedItem = tile;
        }
    }

    private void RangeSelect(Border from, Border to)
    {
        var children = FileGrid.Children.OfType<Border>().ToList();
        int a = children.IndexOf(from);
        int b = children.IndexOf(to);
        if (a < 0 || b < 0) return;
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        for (int i = lo; i <= hi; i++) AddToSelection(children[i]);
    }

    private void AddToSelection(Border tile)
    {
        _selectedItems.Add(tile);
        tile.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x33, 0x99, 0xFF));
        ApplyCutOpacity(tile);
    }

    private void RemoveFromSelection(Border tile)
    {
        _selectedItems.Remove(tile);
        tile.Background = Brushes.Transparent;
        ApplyCutOpacity(tile);
    }

    private void ToggleSelection(Border tile)
    {
        if (_selectedItems.Contains(tile)) RemoveFromSelection(tile);
        else                               AddToSelection(tile);
    }

    private void ClearAllSelection()
    {
        foreach (var t in _selectedItems.ToList())
        {
            t.Background = Brushes.Transparent;
            ApplyCutOpacity(t);
        }
        _selectedItems.Clear();
    }

    private void ApplyCutOpacity(Border tile)
    {
        bool isCut = _fileClipboard is { IsCut: true }
            && _fileClipboard.Value.Paths.Contains(tile.Tag as string ?? "");
        tile.Opacity = isCut ? 0.5 : 1.0;
    }

    // ── ドラッグ範囲選択 ──────────────────────────────────────────

    private void OnSVPreviewLMBDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(FileScrollViewer);

        if (e.OriginalSource is DependencyObject src)
        {
            var tile = FindTile(src);
            if (tile != null)
            {
                // タイル上 → 範囲選択は開始しない、アイテムドラッグの起点だけ記録
                _itemDragStartTile = tile;
                return;
            }
        }

        _itemDragStartTile = null;
        // 背景クリック → 選択解除して範囲選択開始
        CancelRenameSchedule();
        bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        if (!ctrl) ClearAllSelection();
        _selectionAtDragStart = new HashSet<Border>(_selectedItems);
        _lastClickedItem      = null;
        _dragSelectActive     = true;
        _dragThresholdMet     = false;
        FileScrollViewer.CaptureMouse();
        FileScrollViewer.Focus();
    }

    private void OnSVLMBDown(object sender, MouseButtonEventArgs e)
    {
        // バブリングがここまで来るのは背景クリック時のみ（念のため残す）
        e.Handled = true;
    }

    private void OnSVPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _itemDragStartTile = null;
            return;
        }

        var cur = e.GetPosition(FileScrollViewer);
        double dx = cur.X - _dragStart.X, dy = cur.Y - _dragStart.Y;

        // アイテムドラッグ開始判定（しきい値 5px）
        if (_itemDragStartTile != null && dx * dx + dy * dy > 25)
        {
            var startTile = _itemDragStartTile;
            _itemDragStartTile = null;
            // ドラッグ起点タイルが選択に入っていなければ選択する
            if (!_selectedItems.Contains(startTile))
                SelectSingle(startTile, ctrl: false, shift: false);
            if (_selectedItems.Count > 0)
                BeginItemDrag(startTile);
            return;
        }

        if (!_dragSelectActive) return;
        if (!_dragThresholdMet && dx * dx + dy * dy > 25) _dragThresholdMet = true;
        if (!_dragThresholdMet) return;

        UpdateDragRect(cur);
        UpdateDragSelection(cur);
        e.Handled = true;
    }

    private void OnSVPreviewLMBUp(object sender, MouseButtonEventArgs e)
    {
        _itemDragStartTile = null;

        // 遅延 SelectSingle の確定（ドラッグせずにリリースした場合）
        if (_pendingSingleSelectTile != null)
        {
            var t = _pendingSingleSelectTile;
            _pendingSingleSelectTile = null;
            CancelRenameSchedule();
            SelectSingle(t, ctrl: false, shift: false);
        }

        if (!_dragSelectActive) return;
        _dragSelectActive = false;
        _dragThresholdMet = false;
        HideDragRect();
        FileScrollViewer.ReleaseMouseCapture();
        e.Handled = true;
    }

    // ── アイテムドラッグ（移動） ──────────────────────────────────────

    private void BeginItemDrag(Border sourceTile)
    {
        // ドラッグ確定 → 遅延 SelectSingle・リネームスケジュール両方をキャンセル
        _pendingSingleSelectTile = null;
        CancelRenameSchedule();

        var paths = _selectedItems
            .Select(b => b.Tag as string)
            .Where(p => p != null)
            .Cast<string>()
            .ToArray();
        if (paths.Length == 0) return;

        var mainWindow = Application.Current.MainWindow as MainWindow;

        // GiveFeedback でカーソルとビューポートハイライトを制御する。
        // HwndHost 上では OLE の DragOver が呼ばれないため GiveFeedback も1回しか発火しない。
        // ホバープレビュー位置の更新はバックグラウンドスレッドでポーリングする。
        GiveFeedbackEventHandler giveFeedback = (_, args) =>
        {
            bool overVp = mainWindow?.IsMouseOverViewportHwnd() ?? false;
            mainWindow?.SetViewportDragHighlight(overVp);
            if (overVp)
            {
                // ビューポート上では OLE デフォルトの「no-drop」を抑制し通常カーソルを表示する
                args.UseDefaultCursors = false;
                Mouse.OverrideCursor   = null;
                args.Handled           = true;
            }
            else
            {
                Mouse.OverrideCursor = null;
            }
        };

        // バックグラウンドスレッドで 30ms ごとにカーソル位置を送信する。
        // DoDragDrop が UI スレッドをブロックする間、GiveFeedback はほぼ1回しか
        // 発火しないため、スレッドでポーリングしてリアルタイム更新を実現する。
        using var cts = new System.Threading.CancellationTokenSource();
        var ct        = cts.Token;
        bool prevOverVp = false;
        var hoverThread = new System.Threading.Thread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                bool overVp = mainWindow?.IsMouseOverViewportHwnd() ?? false;
                if (overVp)
                {
                    prevOverVp = true;
                    mainWindow?.SendActorDragHover();
                }
                else if (prevOverVp)
                {
                    // ビューポートを離れた瞬間に HOVER_END を1回送信
                    prevOverVp = false;
                    mainWindow?.SendActorDragHoverEnd();
                }
                System.Threading.Thread.Sleep(30);
            }
        }) { IsBackground = true };
        hoverThread.Start();

        // ドラッグ対象にアクタファイル（.actor / .actor2d）が含まれる場合、
        // 種類に対応するシーンタブ（ワールド/ビューポート）へドロップ前に仮切替する。
        // ドロップが成立しなければ後段の EndActorDragSceneTabSwitch で元のタブへ戻す。
        mainWindow?.BeginActorDragSceneTabSwitch(paths);

        sourceTile.GiveFeedback += giveFeedback;
        var data   = new DataObject("SEEDProjectPaths", paths);
        var result = DragDrop.DoDragDrop(sourceTile, data, DragDropEffects.Copy | DragDropEffects.Move);
        sourceTile.GiveFeedback -= giveFeedback;

        // バックグラウンドスレッドを停止し、ホバープレビューを確実に消す
        cts.Cancel();
        hoverThread.Join(200);
        mainWindow?.SendActorDragHoverEnd();

        // ドラッグ終了後のクリーンアップ
        Mouse.OverrideCursor = null;
        mainWindow?.SetViewportDragHighlight(false);
        // OLE ループ終了後に残留するマウスキャプチャ状態をリセットする
        Mouse.Capture(null);

        // HwndHost 上へのドロップは OLE に届かず DragDropEffects.None で返る。
        // その場合はカーソル位置を確認してビューポートへ手動でドロップを転送する。
        bool forwardedToViewport = false;
        if (result == DragDropEffects.None)
            forwardedToViewport =
                (mainWindow as MainWindow.IViewportDropReceiver)?.TryDropActorsAtCursor(paths) ?? false;

        // ドロップが成立した場合は仮切替したシーンタブを維持し、
        // キャンセル（Esc・枠外リリース）なら元のタブへ戻す。
        mainWindow?.EndActorDragSceneTabSwitch(
            result != DragDropEffects.None || forwardedToViewport);
    }

    private void AttachDropTarget(Border tile)
    {
        tile.AllowDrop = true;

        tile.DragEnter += (_, e) =>
        {
            // フォルダタイルは「ファイル移動（SEEDProjectPaths）」と
            // 「Hierarchy アクタのファイル化」の両方を受ける。
            if (e.Data.GetDataPresent("SEEDProjectPaths"))
                e.Effects = DragDropEffects.Move;
            else if (HasHierarchyActorData(e.Data))
                e.Effects = DragDropEffects.Copy;
            else return;
            tile.BorderBrush     = new SolidColorBrush(Color.FromArgb(0xFF, 0x44, 0x88, 0xFF));
            tile.BorderThickness = new Thickness(2);
            e.Handled            = true;
        };
        tile.DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent("SEEDProjectPaths"))
                e.Effects = DragDropEffects.Move;
            else if (HasHierarchyActorData(e.Data))
                e.Effects = DragDropEffects.Copy;
            else return;
            e.Handled = true;
        };
        tile.DragLeave += (_, _) =>
        {
            tile.BorderBrush     = null;
            tile.BorderThickness = new Thickness(0);
        };
        tile.Drop += (_, e) =>
        {
            tile.BorderBrush     = null;
            tile.BorderThickness = new Thickness(0);

            var destDir = tile.Tag as string;
            if (destDir == null || !Directory.Exists(destDir)) return;

            // Hierarchy アクタのドロップ → そのフォルダへアクタファイル化する
            if (HasHierarchyActorData(e.Data))
            {
                HandleHierarchyActorDrop(e.Data, destDir);
                e.Handled = true;
                return;
            }

            if (!e.Data.GetDataPresent("SEEDProjectPaths")) return;

            var paths = (string[])e.Data.GetData("SEEDProjectPaths");
            bool anyMoved = false;
            foreach (var src in paths)
                if (MoveItemToFolder(src, destDir)) anyMoved = true;

            if (anyMoved)
            {
                ClearAllSelection();
                RefreshFileGrid();
            }
            e.Handled = true;
        };
    }

    private static bool MoveItemToFolder(string srcPath, string destDir)
    {
        // ドロップ先が移動元そのもの
        if (string.Equals(Path.GetFullPath(srcPath), Path.GetFullPath(destDir),
                StringComparison.OrdinalIgnoreCase)) return false;

        var name     = Path.GetFileName(srcPath);
        var destPath = Path.Combine(destDir, name);

        // 同じ場所への移動
        if (string.Equals(Path.GetFullPath(srcPath), Path.GetFullPath(destPath),
                StringComparison.OrdinalIgnoreCase)) return false;

        // フォルダを自分の子孫フォルダに移動しようとしている
        if (Directory.Exists(srcPath))
        {
            var srcFull  = Path.GetFullPath(srcPath).TrimEnd(Path.DirectorySeparatorChar);
            var destFull = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar);
            if (destFull.StartsWith(srcFull + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) return false;
        }

        try
        {
            if (Directory.Exists(srcPath))
            {
                if (Directory.Exists(destPath)) return false; // 同名フォルダが存在
                Directory.Move(srcPath, destPath);
            }
            else if (File.Exists(srcPath))
            {
                if (File.Exists(destPath)) return false;      // 同名ファイルが存在
                File.Move(srcPath, destPath);
            }
            return true;
        }
        catch { return false; }
    }

    private void UpdateDragRect(Point cur)
    {
        if (_selRectShape == null)
        {
            _selRectShape = new System.Windows.Shapes.Rectangle
            {
                Stroke          = new SolidColorBrush(Color.FromArgb(0xCC, 0x44, 0x88, 0xFF)),
                StrokeThickness = 1,
                Fill            = new SolidColorBrush(Color.FromArgb(0x33, 0x44, 0x88, 0xFF)),
            };
            SelectionCanvas.Children.Add(_selRectShape);
        }
        double x = Math.Min(_dragStart.X, cur.X);
        double y = Math.Min(_dragStart.Y, cur.Y);
        double w = Math.Abs(cur.X - _dragStart.X);
        double h = Math.Abs(cur.Y - _dragStart.Y);
        Canvas.SetLeft(_selRectShape, x);
        Canvas.SetTop (_selRectShape, y);
        _selRectShape.Width  = w;
        _selRectShape.Height = h;
    }

    private void HideDragRect()
    {
        SelectionCanvas.Children.Clear();
        _selRectShape = null;
    }

    private void UpdateDragSelection(Point cur)
    {
        var dragRect = new Rect(
            Math.Min(_dragStart.X, cur.X),
            Math.Min(_dragStart.Y, cur.Y),
            Math.Abs(cur.X - _dragStart.X),
            Math.Abs(cur.Y - _dragStart.Y));

        foreach (var tile in FileGrid.Children.OfType<Border>())
        {
            var tl      = tile.TranslatePoint(new Point(0, 0), FileScrollViewer);
            var tr      = new Rect(tl.X, tl.Y, tile.ActualWidth, tile.ActualHeight);
            bool inRect = dragRect.IntersectsWith(tr);
            // Ctrl+ドラッグはドラッグ開始時の選択に加算、通常ドラッグは矩形内のみ
            if (inRect || _selectionAtDragStart.Contains(tile)) AddToSelection(tile);
            else                                                 RemoveFromSelection(tile);
        }
    }

    // ── 右クリックコンテキストメニュー ────────────────────────────

    private void OnSVRMBDown(object sender, MouseButtonEventArgs e)
    {
        // 背景のみここに来る（タイル側が Handled=true にしている）
        ClearAllSelection();
        FileScrollViewer.Focus();
        ShowEmptyContextMenu();
        e.Handled = true;
    }

    private void ShowItemContextMenu()
    {
        var menu = new ContextMenu();

        // 単一選択のときだけ、ファイル種別に応じた専用コマンドを先頭に出す
        AddSpriteRigMenuItems(menu);

        Add(menu, "コピー",    "Ctrl+C", DoCopy);
        Add(menu, "切り取り",  "Ctrl+X", DoCut);
        menu.Items.Add(new Separator());
        Add(menu, "削除",      null,     DoDelete);
        if (_selectedItems.Count == 1)
        {
            menu.Items.Add(new Separator());
            Add(menu, "名前の変更", "F2", () =>
            {
                var tile = _selectedItems.First();
                Dispatcher.BeginInvoke(() => StartRenameMode(tile),
                    System.Windows.Threading.DispatcherPriority.Background);
            });
        }
        menu.Items.Add(new Separator());
        Add(menu, "エクスプローラーで開く", null, OpenInExplorer);

        menu.PlacementTarget = FileScrollViewer;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    /// <summary>
    /// 右クリックメニューへスプライトリグ関連の項目を足す。
    ///
    /// 画像を単一選択しているときは「スプライトリグを作成」、
    /// .sprite_mesh を単一選択しているときは「スプライトリグを編集」を出す。
    /// 対象外のときは何も足さない（メニューが無駄に伸びないようにする）。
    /// </summary>
    /// <param name="menu">項目を足す対象のコンテキストメニュー。</param>
    private void AddSpriteRigMenuItems(ContextMenu menu)
    {
        if (_selectedItems.Count != 1) return;
        if (_selectedItems.First().Tag is not string path) return;
        if (!File.Exists(path)) return;

        string extension = Path.GetExtension(path);
        if (SpriteRig.SpriteImageLoader.IsSupportedExtension(extension))
        {
            Add(menu, "スプライトリグを作成", null, () => SpriteRigCreateRequested?.Invoke(path));
            menu.Items.Add(new Separator());
        }
        else if (extension.Equals(SpriteRig.IO.SpriteMeshFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            Add(menu, "スプライトリグを編集", null, () => SpriteMeshFileOpened?.Invoke(path));
            menu.Items.Add(new Separator());
        }
    }

    private void ShowEmptyContextMenu()
    {
        var menu = new ContextMenu();

        // 種類別の「新規○○」項目は新規作成ウィンドウ（CreateItemWindow）へ集約した。
        // ここに残すのは、ウィンドウを介さず即座に作れる「新規フォルダ」と、
        // ウィンドウを開く「新規作成」の 2 つだけ。
        Add(menu, "新規作成",               null, () => OpenCreateItemWindow(_currentPath));
        Add(menu, "新規フォルダを作成",     null, CreateNewFolder);
        menu.Items.Add(new Separator());
        Add(menu, "エクスプローラーで開く", null, () =>
            System.Diagnostics.Process.Start("explorer.exe", _currentPath));

        menu.PlacementTarget = FileScrollViewer;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    /// <summary>
    /// Phase R7 最小実装: .mat ファイルをダブルクリックした際、専用パネルを新設せず
    /// Windows の既定関連付けアプリ（未関連付けなら「アプリの選択」ダイアログ）で開く。
    /// UseShellExecute=true で ShellExecute 経由起動する（プロセス起動失敗は無視して黙って何もしない）。
    /// </summary>
    private static void OpenMaterialFile(string fullPath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullPath)
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private static void Add(ContextMenu menu, string header, string? gesture, Action action)
    {
        var item = new MenuItem { Header = header };
        if (gesture != null) item.InputGestureText = gesture;
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    // ── キーボード ────────────────────────────────────────────────

    private void OnSVPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _selectedItems.Count > 0)
        {
            DoDelete();
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && _selectedItems.Count == 1)
        {
            var tile = _selectedItems.First();
            Dispatcher.BeginInvoke(() => StartRenameMode(tile),
                System.Windows.Threading.DispatcherPriority.Background);
            e.Handled = true;
        }
    }

    private void ScheduleRename(Border tile)
    {
        _renamePendingTile = tile;
        _renameTimer?.Stop();
        _renameTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(500) };
        _renameTimer.Tick += (_, _) =>
        {
            _renameTimer?.Stop();
            _renameTimer = null;
            var t = _renamePendingTile;
            _renamePendingTile = null;
            if (t != null) StartRenameMode(t);
        };
        _renameTimer.Start();
    }

    private void CancelRenameSchedule()
    {
        _renameTimer?.Stop();
        _renameTimer      = null;
        _renamePendingTile = null;
    }

    // ── ファイル操作 ──────────────────────────────────────────────

    public void DoCopy()
    {
        var paths = GetSelectedPaths();
        if (paths.Count == 0) return;
        _fileClipboard = (paths, IsCut: false);
        RefreshCutVisuals();
    }

    public void DoCut()
    {
        var paths = GetSelectedPaths();
        if (paths.Count == 0) return;
        _fileClipboard = (paths, IsCut: true);
        RefreshCutVisuals();
    }

    public void DoPaste()
    {
        if (_fileClipboard is not { } cb) return;

        foreach (var src in cb.Paths)
        {
            bool srcIsDir = Directory.Exists(src);
            bool srcIsFile = !srcIsDir && File.Exists(src);
            if (!srcIsDir && !srcIsFile) continue;

            string dest = GetUniquePastePath(src, _currentPath);
            try
            {
                if (srcIsDir)
                {
                    CopyDirectoryRecursive(src, dest);
                    if (cb.IsCut) Directory.Delete(src, recursive: true);
                }
                else
                {
                    File.Copy(src, dest);
                    if (cb.IsCut) File.Delete(src);
                }
            }
            catch { }
        }

        if (cb.IsCut) _fileClipboard = null;
        RefreshCutVisuals();
    }

    private void DoDelete()
    {
        var paths = GetSelectedPaths();
        if (paths.Count == 0) return;

        string msg = paths.Count == 1
            ? $"「{Path.GetFileName(paths[0])}」を削除しますか？\n\n削除したファイルはごみ箱から復元できます。"
            : $"{paths.Count} 個のアイテムを削除しますか？\n\n削除したファイルはごみ箱から復元できます。";

        var result = MessageBox.Show(msg, "削除の確認",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        foreach (var path in paths)
            RecycleFile(path);

        ClearAllSelection();
    }

    private void OpenInExplorer()
    {
        var paths = GetSelectedPaths();
        if (paths.Count == 0) return;
        // 最初の選択アイテムを選択した状態でエクスプローラーを開く
        System.Diagnostics.Process.Start("explorer.exe",
            $"/select,\"{paths[0]}\"");
    }

    private void CreateNewFolder()
    {
        string name = "新規フォルダ";
        string path = Path.Combine(_currentPath, name);
        int n = 1;
        while (Directory.Exists(path) || File.Exists(path))
        {
            name = $"新規フォルダ({n++})";
            path = Path.Combine(_currentPath, name);
        }
        try
        {
            Directory.CreateDirectory(path);
            _pendingRenameFolder = path;
            // FSW の OnFsChanged は pending 中スキップされるため、ここで直接更新する
            RefreshFileGrid();
        }
        catch { }
    }

    // ── インラインリネーム ────────────────────────────────────────

    private void StartRenameMode(Border tile)
    {
        // pending フラグをここで解除（OnFsChanged の FSW 抑制を終わらせる）
        _pendingRenameFolder = null;
        if (_isRenaming) return;
        _isRenaming = true;

        var sp        = (StackPanel)tile.Child;
        var nameBlock = sp.Children.OfType<TextBlock>().First();
        var origName  = nameBlock.Text;

        var nameBox = new TextBox
        {
            Text                = origName,
            FontSize            = 11,
            Background          = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)),
            Foreground          = Brushes.White,
            BorderBrush         = new SolidColorBrush(Color.FromArgb(0xAA, 0x44, 0x88, 0xFF)),
            BorderThickness     = new Thickness(1),
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth            = 106,
            MinWidth            = 60,
        };
        nameBox.Loaded += (_, _) =>
        {
            nameBox.Focus();
            var stemLen = origName.Length - Path.GetExtension(origName).Length;
            nameBox.Select(0, stemLen);
        };

        int blockIdx = sp.Children.IndexOf(nameBlock);
        sp.Children.RemoveAt(blockIdx);
        sp.Children.Insert(blockIdx, nameBox);

        void RestoreBlock()
        {
            if (!_isRenaming) return;
            _isRenaming = false;
            sp.Children.Remove(nameBox);
            sp.Children.Insert(blockIdx, nameBlock);
        }

        void Commit()
        {
            var newName = nameBox.Text.Trim();
            RestoreBlock();
            if (string.IsNullOrEmpty(newName) || newName == origName) return;
            var oldPath = (string)tile.Tag!;
            var dir     = Path.GetDirectoryName(oldPath)!;
            var newPath = Path.Combine(dir, newName);
            try
            {
                if (Directory.Exists(oldPath))
                {
                    Directory.Move(oldPath, newPath);
                }
                else if (File.Exists(oldPath))
                {
                    File.Move(oldPath, newPath);
                    if (Path.GetExtension(origName).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                        UpdateScriptClassName(newPath,
                            Path.GetFileNameWithoutExtension(origName),
                            Path.GetFileNameWithoutExtension(newName));
                }
            }
            catch { }
        }

        nameBox.KeyDown += (_, e) =>
        {
            if      (e.Key == Key.Return) { Commit();      e.Handled = true; }
            else if (e.Key == Key.Escape) { RestoreBlock(); e.Handled = true; }
        };
        nameBox.LostFocus += (_, _) => Commit();
    }

    // ── ユーティリティ ────────────────────────────────────────────

    private List<string> GetSelectedPaths()
        => _selectedItems
            .Select(t => t.Tag as string)
            .Where(p => p != null)
            .Cast<string>()
            .ToList();

    /// コピー先の一意パスを生成する。
    /// 「name(1)」を重複がなくなるまで付け続ける（アクタコピーと同挙動）。
    private static string GetUniquePastePath(string srcPath, string destDir)
    {
        bool   isDir = Directory.Exists(srcPath);
        string ext   = isDir ? "" : Path.GetExtension(Path.GetFileName(srcPath));
        string stem  = isDir
            ? Path.GetFileName(srcPath)!
            : Path.GetFileNameWithoutExtension(srcPath);

        string candidate = Path.Combine(destDir, stem + ext);
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            stem     += "(1)";
            candidate = Path.Combine(destDir, stem + ext);
        }
        return candidate;
    }

    private static void CopyDirectoryRecursive(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectoryRecursive(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    private void RefreshCutVisuals()
    {
        foreach (Border tile in FileGrid.Children)
            ApplyCutOpacity(tile);
    }

    // ── ナビゲーション ────────────────────────────────────────────

    private void OnBackClick(object sender, RoutedEventArgs e) => GoUp();

    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { GoUp(); e.Handled = true; }
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
        _currentPath = path;
        // アクティブタブの「開いているフォルダ位置」を更新し、タブ名（フォルダ名）を追従させる
        OnActiveFolderChanged(path);
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
            // ツリーを作り直すと展開状態が失われるため、作り直す前に現在の展開集合を
            // アクティブタブへ退避し、作り直した後で復元する。
            CaptureActiveTabState();
            BuildFolderTree();
            RestoreActiveTabTreeExpansion();
            if (!Directory.Exists(_currentPath)) _currentPath = _assetsRoot;
            // リネーム pending 中・リネーム実行中はグリッドを再構築しない。
            // FSW が StartRenameMode より先に実行されてタイルを破壊するのを防ぐ。
            if (_pendingRenameFolder == null && !_isRenaming)
                RefreshFileGrid();
        });
    }

    // ── ビジュアルツリーヘルパー ──────────────────────────────────

    /// クリックされた要素からビジュアルツリーを遡り、タイル Border を探す。
    private static Border? FindTile(DependencyObject obj)
    {
        var cur = obj;
        while (cur != null)
        {
            if (cur is Border b && b.Tag is string) return b;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    // ── 静的ユーティリティ ────────────────────────────────────────

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

    private static void UpdateScriptClassName(string filePath, string oldName, string newName)
    {
        if (oldName == newName) return;
        try
        {
            var text    = File.ReadAllText(filePath, Encoding.UTF8);
            var updated = Regex.Replace(
                text,
                $@"\bclass\s+{Regex.Escape(oldName)}\b",
                $"class {newName}");
            if (updated != text)
                File.WriteAllText(filePath, updated, Encoding.UTF8);
        }
        catch { }
    }
}
