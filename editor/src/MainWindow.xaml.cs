using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Reflection;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using Microsoft.Win32;
using SEEDEditor.AI;
using SEEDEditor.Native;
using SEEDEditor.Panels;
using SEEDEditor.Runtime;
using SEEDEditor.Viewport;
using static SEEDEditor.Native.NativeInterop;

namespace SEEDEditor;

public partial class MainWindow : Window, MainWindow.IViewportDropReceiver
{
    /// ProjectPanel が DoDragDrop 後にビューポートドロップを転送するためのインターフェース。
    public interface IViewportDropReceiver
    {
        void TryDropActorsAtCursor(string[] paths);
    }

    // ── フィールド ──────────────────────────────────────────────

    private LowLevelKeyboardProc? _llKeyProc;
    private nint                  _llKeyHook;

    private readonly HashSet<uint> _pressedVks = new();

    private bool _clampInPlay                  = false;
    private bool _isDragging                   = false;
    private bool _ctrlHeld                     = false;
    private bool _deleteDialogOpen             = false;
    private bool _viewportSettingsInitialized  = false;

    // ── アクタータブ管理 ───────────────────────────────────────
    /// <summary>アクター編集タブ情報。IsActor2D は actor_kind="Actor2D" のとき true。</summary>
    private record ActorTab(string Path, string Name, uint WorldLine, bool IsActor2D = false);
    private readonly List<ActorTab> _actorTabs       = new();
    /// <summary>現在アクター編集モードで開いているアクターのパス。null = シーンモード。</summary>
    private string? _activeActorPath                 = null;
    /// <summary>次に開くアクタータブに割り当てる世界線インデックス。</summary>
    private uint _nextWorldLineIdx                   = 1;

    // ── Play 設定 ──────────────────────────────────────────────
    /// <summary>true のとき project_settings.json の開始シーンから Play する。false は現在のシーン。</summary>
    private bool _playFromStartScene = false;
    /// <summary>true のとき Play 中もコライダーのワイヤーフレームを描画する。</summary>
    private bool _playColliderDraw = false;

    // ── 編集時物理シミュレーション設定 ────────────────────────────
    /// <summary>編集モードで物理シミュレーションを動作させるかどうか。</summary>
    private bool _editPhysicsEnabled = false;
    /// <summary>編集時物理で RigidBody（重力・ダイナミクス）も有効にするかどうか。</summary>
    private bool _editPhysicsWithRigidbody = false;
    /// <summary>編集モードで 2D 物理シミュレーションを動作させるかどうか。</summary>
    private bool _editPhysics2dEnabled = false;
    /// <summary>編集時 2D 物理で RigidBody（重力・ダイナミクス）も有効にするかどうか。</summary>
    private bool _editPhysics2dWithRigidbody = false;

    // ── シーン保存状態 ─────────────────────────────────────────
    /// <summary>現在開いているシーンのファイルパス。null = 新規未保存シーン。</summary>
    private string? _currentScenePath         = null;
    /// <summary>前回保存後に変更があれば true。</summary>
    private bool    _isDirty                  = false;
    /// <summary>アクター保存中フラグ（OnSaveCompleted でトースト文言を切り替えるため）。</summary>
    private bool    _isSavingActor            = false;
    /// <summary>
    /// ナビゲーション系コマンド（世界線切り替え・シーンロード等）が
    /// 引き起こす HIERARCHY 更新をダーティ判定から除外するカウンター。
    /// SendNavCommand() を呼ぶたびに +1、hierarchy 受信のたびに -1 する。
    /// </summary>
    private int     _suppressHierarchyDirtyCount = 1; // 起動直後の初期 hierarchy を無視
    /// <summary>保存完了後に続けてロードするシーンパス（保存→ロードの連鎖用）。</summary>
    private string? _pendingSceneLoad         = null;
    /// <summary>保存完了後にウィンドウを閉じるフラグ（終了時確認フローで使用）。</summary>
    private bool    _pendingClose             = false;
    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    private static readonly Dictionary<uint, string> VkKeyMap = new()
    {
        { 0x57, "W"     }, // W
        { 0x41, "A"     }, // A
        { 0x53, "S"     }, // S
        { 0x44, "D"     }, // D
        { 0x51, "Q"     }, // Q
        { 0x45, "E"     }, // E
        { 0xA0, "SHIFT" }, // VK_LSHIFT
        { 0xA1, "SHIFT" }, // VK_RSHIFT
        { 0x10, "SHIFT" }, // VK_SHIFT
    };

    private static readonly string RuntimeExePath      = ResolveRuntimePath();
    private static readonly string AssetsPath          = ResolveAssetsPath();
    private static readonly string SettingsDir         = ResolveSettingsDir();
    private static readonly string EditorResourcesPath = ResolveEditorResourcesPath();
    private static readonly string EditorPluginsPath   = ResolveEditorPluginsPath();

    private static string ResolveSettingsDir()
    {
        var dir = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\settings"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// エディタに同梱するリソースディレクトリを解決する。
    /// 開発時は bin/Debug/net8.0-windows から ../../../resources へ遡る。
    /// リリース時はエグゼの隣の resources/ を使う。
    /// </summary>
    private static string ResolveEditorResourcesPath()
    {
        // 開発時: bin/Debug/net8.0-windows/ → editor/resources/
        var devPath = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\resources"));
        if (Directory.Exists(devPath)) return devPath;

        // リリース時: exe の隣の resources/
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources");
    }

    /// <summary>
    /// エディタ同梱のプラグインライブラリディレクトリを解決する。
    /// 開発時は bin/Debug/net8.0-windows/ → editor/plugins/
    /// リリース時は exe の隣の plugins/
    /// </summary>
    private static string ResolveEditorPluginsPath()
    {
        // 開発時: bin/Debug/net8.0-windows/ → editor/plugins/
        var devPath = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\plugins"));
        if (Directory.Exists(devPath)) return devPath;

        // リリース時: exe の隣の plugins/
        var relPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
        Directory.CreateDirectory(relPath);
        return relPath;
    }

    private static string ResolveRuntimePath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var sameDir = Path.Combine(baseDir, "SEED.exe");
        if (File.Exists(sameDir)) return sameDir;

        var devPath = Path.GetFullPath(
            Path.Combine(baseDir, @"..\..\..\..\runtime\target\debug\SEED.exe"));
        if (File.Exists(devPath)) return devPath;

        var relPath = Path.GetFullPath(
            Path.Combine(baseDir, @"..\..\..\..\runtime\target\release\SEED.exe"));
        return relPath;
    }

    private static string ResolveAssetsPath()
    {
        var exeDir    = Path.GetDirectoryName(RuntimeExePath)!;
        var buildType = Path.GetFileName(exeDir);
        var targetDir = Path.GetFileName(Path.GetDirectoryName(exeDir)!);

        // dev: runtime/target/debug → runtime/
        string runtimeRoot = (buildType is "debug" or "release") && targetDir == "target"
            ? Path.GetFullPath(Path.Combine(exeDir, @"..\..\"))
            : exeDir;

        var assetsDir = Path.Combine(runtimeRoot, "assets");
        Directory.CreateDirectory(assetsDir);
        return assetsDir;
    }

    private static readonly BitmapImage _imgPlay  = new(new Uri("pack://application:,,,/resources/icons/playbar/play.png"));
    private static readonly BitmapImage _imgPause = new(new Uri("pack://application:,,,/resources/icons/playbar/pause.png"));

    private static readonly SolidColorBrush _brushPlay  = new(Color.FromRgb(0x1F, 0x4A, 0x22));
    private static readonly SolidColorBrush _brushStop  = new(Color.FromRgb(0x4A, 0x1F, 0x1F));
    private static readonly SolidColorBrush _brushPause = new(Color.FromRgb(0x4A, 0x30, 0x00));

    private ViewportHost?          _viewportHost;
    private ViewportOleDropTarget? _viewportDropTarget;
    private Window?                _vpDragOverlay;
    private RuntimeManager?        _runtimeManager;
    /// <summary>AI アシスタントパネルのビルド済み UI 要素（LoadLayout でコンテンツを復元するために保持）</summary>
    private UIElement?             _aiPanelUi;

    public MainWindow()
    {
        InitializeComponent();
        ApplyDockTheme();
    }

    // ── ウィンドウ初期化 ─────────────────────────────────────────

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // ダークモードタイトルバー（Windows 10 20H1+）
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, Marshal.SizeOf(dark));

        // キャプション色をツールバー色に合わせる（Windows 11+）
        // COLORREF = 0x00BBGGRR → #2D2D2D の場合 R=G=B=0x2D なので同値
        int captionColor = 0x001D1D1D;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, Marshal.SizeOf(captionColor));

        // ウィンドウ枠色も合わせる（Windows 11+）
        int borderColor = 0x003A3A3A;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, Marshal.SizeOf(borderColor));
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplyDarkTitleBar();
        EditorLog.Write($"OnWindowLoaded — RuntimeExePath={RuntimeExePath}");

        _runtimeManager = new RuntimeManager(RuntimeExePath)
        {
            AssetsPath          = AssetsPath,
            EditorResourcesPath = EditorResourcesPath,
        };
        _runtimeManager.StateChanged         += OnStateChanged;
        _runtimeManager.RuntimeHwndAvailable += OnRuntimeHwndAvailable;
        _runtimeManager.RuntimeMoveStart     += () => { _isDragging = true;  Dispatcher.BeginInvoke(ReleasePlayClamp); };
        _runtimeManager.RuntimeMoveEnd       += () => { _isDragging = false; Dispatcher.BeginInvoke(() => { if (_clampInPlay && _runtimeManager?.State == EditorState.Play) ApplyPlayClamp(); }); };

        _runtimeManager.SaveCompleted                 += OnSaveCompleted;
        _runtimeManager.ViewportContextMenuRequested  += OnViewportContextMenuRequested;
        _runtimeManager.FirstFrameReady               += OnFirstFrameReady;
        _runtimeManager.CameraStateReceived           += OnCameraStateReceived;
        _runtimeManager.HierarchyUpdated              += _ => MarkDirtyFromHierarchy();
        _runtimeManager.SceneModified                 += MarkDirty;
        _runtimeManager.ActorEditStarted              += OnActorEditStarted;
        _runtimeManager.ActorEditEnded                += OnActorEditEnded;
        _runtimeManager.FpsReceived                   += OnFpsReceived;

        PanelHierarchy.SetRuntime(_runtimeManager);
        PanelHierarchy.SetAssetsPath(AssetsPath);
        PanelHierarchy.ActorDfsSelected += id => PanelInspector.SelectActor(id);
        PanelInspector.SetRuntime(_runtimeManager);
        PanelInspector.SetAssetsPath(AssetsPath);
        PanelInspector.TransformCommitted += MarkDirty;
        PanelProject.SetAssetsPath(AssetsPath);
        PanelProject.SceneFileOpened    += OnSceneFileOpened;
        PanelProject.ActorFileOpened    += OnActorFileOpened;
        PanelProject.InputMapFileOpened += OnInputMapFileOpened;

        // AI アシスタントパネルを初期化する。
        // LoadLayout() がこの後に呼ばれてコンテンツを上書きするため、
        // _aiPanelUi にビルド済み要素を保持して LoadLayout 内で参照する。
        var aiPanel = new AIAssistantPanel(_runtimeManager, AssetsPath);
        _aiPanelUi = aiPanel.Build();

        _viewportHost = new ViewportHost();
        _viewportHost.ContainerCreated += OnContainerCreated;
        ViewportDocumentContent.Content = _viewportHost;

        InstallKeyboardHook();

        // ウィンドウドラッグ検出用 WndProc フック
        var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        hwndSource?.AddHook(WndProc);

        LoadLayout();

        EditorLog.Write("OnWindowLoaded — ViewportHost assigned, waiting for ContainerCreated");
    }

    private void OnContainerCreated(object? sender, EventArgs e)
    {
        var hwnd = _viewportHost!.ContainerHwnd;
        EditorLog.Write($"OnContainerCreated — ContainerHwnd=0x{hwnd:X}");

        // ContainerHwnd を OLE DropTarget として登録する。
        // HwndHost 上では WPF DragOver/Drop が発火しないため Win32 レベルで受け取る。
        _viewportDropTarget = new ViewportOleDropTarget(this);
        int hr = RegisterDragDrop(hwnd, _viewportDropTarget);
        EditorLog.Write($"RegisterDragDrop hr=0x{hr:X8}");

        // ドラッグハイライト用オーバーレイを事前生成する。
        // OLE メッセージループ中に Window を生成するとデッドロックの恐れがあるため、
        // ここで生成し HWND を確保したうえで WS_EX_TRANSPARENT を付与してクリックスルーにする。
        _vpDragOverlay = new Window
        {
            WindowStyle        = WindowStyle.None,
            AllowsTransparency = true,
            Background         = Brushes.Transparent,   // ← これがないと白くなる
            ShowInTaskbar      = false,
            Focusable          = false,
            Owner              = this,
            Content            = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            },
        };
        // HWND を強制生成して WS_EX_TRANSPARENT を付与（OLE ドラッグイベントが透過する）
        var helper = new System.Windows.Interop.WindowInteropHelper(_vpDragOverlay);
        helper.EnsureHandle();
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT);

        _ = Task.Run(async () =>
        {
            EditorLog.Write("Task.Run — calling StartEditAsync");
            try
            {
                await _runtimeManager!.StartEditAsync(hwnd);
                EditorLog.Write("StartEditAsync completed");
            }
            catch (Exception ex)
            {
                EditorLog.Write($"StartEditAsync EXCEPTION: {ex}");
                Dispatcher.BeginInvoke(() =>
                    MessageBox.Show($"Runtime 起動失敗:\n{ex}", "SEED Editor",
                        MessageBoxButton.OK, MessageBoxImage.Error));
            }
        });
    }

    // ── ビューポート D&D ──────────────────────────────────────────

    /// Window 全体のドラッグオーバー（フォールバック）。
    /// HwndHost 上では WPF DragOver が発火しないため、ビューポート周囲の WPF 領域（リサイズグリッパー等）でのみ動作する。
    /// ビューポート直上のドロップは ViewportOleDropTarget が処理する。
    private void OnViewportDragOver(object sender, DragEventArgs e)
    {
        var paths = GetActorPathsFromWpf(e.Data).ToList();
        e.Effects = (paths.Any() && IsNearViewport()) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// Window 全体のドロップ（フォールバック）。ビューポート周囲の WPF 領域からのドロップを処理する。
    private void OnViewportDrop(object sender, DragEventArgs e)
    {
        if (!IsNearViewport()) return;
        var paths = GetActorPathsFromWpf(e.Data).ToList();
        if (!paths.Any()) return;

        var (localX, localY) = GetViewportLocalCursorPos();
        HandleViewportDrop(paths, localX, localY);
    }

    /// カーソルがビューポート HWND 矩形の近傍（40px マージン）にあるかを確認する。
    /// WPF DragOver は HwndHost 内では発火しないため、周囲の WPF 領域（リサイズグリッパー等）を許容する。
    private bool IsNearViewport()
    {
        const int Margin = 40;
        if (_viewportHost == null) return false;
        GetCursorPos(out var cursor);
        GetWindowRect(_viewportHost.ContainerHwnd, out var rect);
        return cursor.X >= rect.Left  - Margin && cursor.X <= rect.Right  + Margin
            && cursor.Y >= rect.Top   - Margin && cursor.Y <= rect.Bottom + Margin;
    }

    /// DoDragDrop が DragDropEffects.None で返った場合（HwndHost 上へのドロップ）に
    /// カーソル位置とビューポート HWND 矩形を比較してドロップを転送する。
    public void TryDropActorsAtCursor(string[] paths)
    {
        if (!IsMouseOverViewportHwnd()) return;

        var actorPaths = paths
            .Where(p => IsActorFile(p))
            .ToList();
        if (actorPaths.Count == 0) return;

        var (localX, localY) = GetViewportLocalCursorPos();
        HandleViewportDrop(actorPaths, localX, localY);
    }

    /// カーソルがビューポート ContainerHwnd 矩形内にあるかを物理ピクセル座標で確認する。
    public bool IsMouseOverViewportHwnd()
    {
        if (_viewportHost == null) return false;
        GetCursorPos(out var cursor);
        GetWindowRect(_viewportHost.ContainerHwnd, out var rect);
        return cursor.X >= rect.Left && cursor.X <= rect.Right
            && cursor.Y >= rect.Top  && cursor.Y <= rect.Bottom;
    }

    /// カーソルのビューポートローカル座標（物理ピクセル）を返す。
    private (uint x, uint y) GetViewportLocalCursorPos()
    {
        GetCursorPos(out var cursor);
        GetWindowRect(_viewportHost!.ContainerHwnd, out var rect);
        var localX = (uint)Math.Clamp(cursor.X - rect.Left, 0, rect.Right  - rect.Left - 1);
        var localY = (uint)Math.Clamp(cursor.Y - rect.Top,  0, rect.Bottom - rect.Top  - 1);
        return (localX, localY);
    }

    /// <summary>
    /// ドラッグ中のカーソル位置をランタイムに送信する。
    /// GiveFeedback コールバックからカーソルがビューポート上にいる間呼び出す。
    /// </summary>
    internal void SendActorDragHover()
    {
        var (localX, localY) = GetViewportLocalCursorPos();
        _runtimeManager?.SendToRuntime($"DRAG_HOVER:{localX},{localY}");
    }

    /// <summary>ドラッグがビューポートから離れた、またはドラッグ終了時に呼び出す。</summary>
    internal void SendActorDragHoverEnd()
    {
        _runtimeManager?.SendToRuntime("DRAG_HOVER_END");
    }

    /// ドラッグ中にビューポート上をハイライトする透明オーバーレイを表示 / 非表示にする。
    /// オーバーレイは OnContainerCreated で事前生成済み・WS_EX_TRANSPARENT 付き。
    public void SetViewportDragHighlight(bool active)
    {
        if (_vpDragOverlay == null) return;

        if (active && _viewportHost != null)
        {
            // ContainerHwnd の物理ピクセル座標を WPF 論理座標に変換して配置する
            GetWindowRect(_viewportHost.ContainerHwnd, out var rect);
            var topLeft  = PointFromScreen(new Point(rect.Left,  rect.Top));
            var botRight = PointFromScreen(new Point(rect.Right, rect.Bottom));

            _vpDragOverlay.Left   = topLeft.X;
            _vpDragOverlay.Top    = topLeft.Y;
            _vpDragOverlay.Width  = botRight.X - topLeft.X;
            _vpDragOverlay.Height = botRight.Y - topLeft.Y;

            if (!_vpDragOverlay.IsVisible)
                _vpDragOverlay.Show();
        }
        else
        {
            _vpDragOverlay.Hide();
        }
    }

    /// ドロップされたアクターパスをランタイムに送信する共通処理。
    /// WPF フォールバックパスと Win32 OLE DropTarget パスの両方から呼ばれる。
    internal void HandleViewportDrop(IReadOnlyList<string> paths, uint viewportX, uint viewportY)
    {
        var state = _runtimeManager?.State;
        EditorLog.Write($"[Drop] HandleViewportDrop paths={paths.Count} pos=({viewportX},{viewportY}) state={state}");
        if (state != EditorState.Edit && state != EditorState.Pause) return;

        foreach (var path in paths)
        {
            var msg = $"DROP_ACTOR:{path},{viewportX},{viewportY}";
            EditorLog.Write($"[Drop] Sending: {msg}");
            _runtimeManager?.SendToRuntime(msg);
        }
    }

    /// WPF IDataObject から .actor パス一覧を取得する。
    private static IEnumerable<string> GetActorPathsFromWpf(IDataObject data)
    {
        if (data.GetDataPresent("SEEDProjectPaths"))
        {
            var paths = data.GetData("SEEDProjectPaths") as string[];
            if (paths != null)
                return paths.Where(p => IsActorFile(p));
        }
        if (data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null)
                return paths.Where(p => IsActorFile(p));
        }
        return Enumerable.Empty<string>();
    }

    /// <summary>.actor / .actor2d いずれかの拡張子を持つパスかを返す。</summary>
    private static bool IsActorFile(string path)
        => path.EndsWith(".actor",   StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".actor2d", StringComparison.OrdinalIgnoreCase);

    // ── Win32 OLE DropTarget（ビューポート HWND への直接ドロップ）──

    /// ContainerHwnd に登録する Win32 OLE DropTarget。
    /// WPF DragDrop はイン-プロセスで動作するため、pDataObj を System.Windows.IDataObject にキャストできる。
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ViewportOleDropTarget : IOleDropTarget
    {
        private const uint DROPEFFECT_NONE = 0;
        private const uint DROPEFFECT_COPY = 1;
        private const int  S_OK            = 0;

        private readonly MainWindow _owner;
        /// <summary>現在ドラッグ中の .actor パスが存在するかどうか。</summary>
        private bool _isDraggingActors = false;

        public ViewportOleDropTarget(MainWindow owner) => _owner = owner;

        public int DragEnter(object pDataObj, uint grfKeyState, POINT pt, ref uint pdwEffect)
        {
            var actorPaths = GetActorPaths(pDataObj).ToList();
            _isDraggingActors = actorPaths.Any();
            pdwEffect = _isDraggingActors ? DROPEFFECT_COPY : DROPEFFECT_NONE;
            EditorLog.Write($"[OLE] DragEnter: isDraggingActors={_isDraggingActors} actorCount={actorPaths.Count} pt=({pt.X},{pt.Y})");
            if (_isDraggingActors)
                SendHover(pt);
            return S_OK;
        }

        public int DragOver(uint grfKeyState, POINT pt, ref uint pdwEffect)
        {
            // エフェクトを維持しつつホバー位置をランタイムに通知する。
            // pdwEffect を明示的に設定しないと OLE が DROPEFFECT_NONE と解釈することがある。
            if (_isDraggingActors)
            {
                pdwEffect = DROPEFFECT_COPY;
                SendHover(pt);
            }
            else
            {
                pdwEffect = DROPEFFECT_NONE;
                EditorLog.Write("[OLE] DragOver: _isDraggingActors=false, skipping hover");
            }
            return S_OK;
        }

        public int DragLeave()
        {
            // ドラッグ離脱: プレビュー球体を消す
            if (_isDraggingActors)
            {
                _isDraggingActors = false;
                _owner._runtimeManager?.SendToRuntime("DRAG_HOVER_END");
            }
            return S_OK;
        }

        public int Drop(object pDataObj, uint grfKeyState, POINT pt, ref uint pdwEffect)
        {
            _isDraggingActors = false;
            var paths = GetActorPaths(pDataObj).ToList();
            if (paths.Count == 0) { pdwEffect = DROPEFFECT_NONE; return S_OK; }

            // スクリーン座標をビューポートローカル座標に変換する
            GetWindowRect(_owner._viewportHost!.ContainerHwnd, out var vpRect);
            var localX = (uint)Math.Max(0, pt.X - vpRect.Left);
            var localY = (uint)Math.Max(0, pt.Y - vpRect.Top);

            _owner.Dispatcher.BeginInvoke(() => _owner.HandleViewportDrop(paths, localX, localY));
            pdwEffect = DROPEFFECT_COPY;
            return S_OK;
        }

        /// <summary>スクリーン座標をビューポートローカル座標に変換して DRAG_HOVER を送信する。</summary>
        private void SendHover(POINT pt)
        {
            if (_owner._viewportHost == null) return;
            GetWindowRect(_owner._viewportHost.ContainerHwnd, out var vpRect);
            var localX = (uint)Math.Max(0, pt.X - vpRect.Left);
            var localY = (uint)Math.Max(0, pt.Y - vpRect.Top);
            _owner._runtimeManager?.SendToRuntime($"DRAG_HOVER:{localX},{localY}");
        }

        /// イン-プロセス WPF ドラッグの場合、pDataObj は System.Windows.IDataObject にキャスト可能。
        private static IEnumerable<string> GetActorPaths(object pDataObj)
        {
            if (pDataObj is not System.Windows.IDataObject data) return Enumerable.Empty<string>();
            if (data.GetDataPresent("SEEDProjectPaths"))
            {
                var paths = data.GetData("SEEDProjectPaths") as string[];
                if (paths != null)
                    return paths.Where(p => IsActorFile(p));
            }
            return Enumerable.Empty<string>();
        }
    }

    // ── WndProc：ウィンドウドラッグ監視 ──────────────────────────

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_MOVE)
        {
            // タイトルバードラッグ開始 → クランプ一時解除
            _isDragging = true;
            ReleasePlayClamp();
        }
        else if (msg == WM_EXITSIZEMOVE)
        {
            // ドラッグ終了 → 必要ならクランプ再適用
            _isDragging = false;
            if (_clampInPlay && _runtimeManager?.State == EditorState.Play)
                ApplyPlayClamp();
        }
        return nint.Zero;
    }

    // ── ボタンイベント ────────────────────────────────────────────

    private async void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_runtimeManager is null) return;
        var state = _runtimeManager.State;
        if (state == EditorState.Edit)
        {
            if (_playFromStartScene)
            {
                // 「開始シーンからプレイ」ON: null を渡してランタイムに start_scene を使わせる
                _runtimeManager.PlayScenePath = null;
            }
            else
            {
                // 現在の Edit ランタイムのシーン状態（未保存変更を含む）を
                // 一時ファイルへ保存してから Play する。
                // これによりファイル保存なしでも常に最新状態で実行できる。
                var tempPath = await _runtimeManager.SaveCurrentSceneToTempAsync();
                if (tempPath is null)
                {
                    // 一時保存に失敗した場合はファイル保存済みのパスにフォールバックする
                    EditorLog.Write("OnPlayPause — 一時保存失敗。_currentScenePath にフォールバック");
                    _runtimeManager.PlayScenePath = _currentScenePath;
                }
                else
                {
                    _runtimeManager.PlayScenePath = tempPath;
                }
            }

            // Play 起動フラグを設定してから PlayAsync を呼ぶ
            _runtimeManager.PlayColliderDraw = _playColliderDraw;
            try { await _runtimeManager.PlayAsync(); }
            catch (Exception ex)
            {
                EditorLog.Write($"OnPlayPause(Play) EXCEPTION: {ex}");
                MessageBox.Show($"Play 起動失敗:\n{ex.Message}", "SEED Editor",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else if (state == EditorState.Play)
            _runtimeManager.Pause();
        else if (state == EditorState.Pause)
            _runtimeManager.Resume();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (_runtimeManager is null) return;
        _runtimeManager.Stop();
    }

    /// <summary>「開始シーンからプレイ」チェックボックスの変化を受け取る。</summary>
    private void OnPlayFromStartSceneChanged(object sender, RoutedEventArgs e)
    {
        _playFromStartScene = ChkPlayFromStartScene.IsChecked == true;
        EditorLog.Write($"PlayFromStartScene = {_playFromStartScene}");
    }

    /// <summary>
    /// VS2013 DarkTheme を ResourceDictionary のマージで適用する。
    /// DockingManager.Theme プロパティの型依存を避けるため pack URI 方式を使用する。
    /// </summary>
    private void ApplyDockTheme()
    {
        try
        {
            // VS2013 DarkBrushs → Generic テンプレートの順でマージ
            var brushUri = new Uri(
                "pack://application:,,,/AvalonDock.Themes.VS2013;component/DarkBrushs.xaml",
                UriKind.Absolute);
            var genericUri = new Uri(
                "pack://application:,,,/AvalonDock.Themes.VS2013;component/Themes/Generic.xaml",
                UriKind.Absolute);

            var dicts = DockManager.Resources.MergedDictionaries;
            dicts.Add(new ResourceDictionary { Source = brushUri });
            dicts.Add(new ResourceDictionary { Source = genericUri });
        }
        catch
        {
            // テーマ適用失敗時はデフォルトのまま続行
        }
    }

    private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isDirty)
        {
            var result = MessageBox.Show(
                "未保存の変更があります。終了する前に保存しますか？",
                "SEED Editor",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            switch (result)
            {
                case MessageBoxResult.Cancel:
                    e.Cancel = true;
                    return;

                case MessageBoxResult.Yes:
                    e.Cancel = true;
                    if (_activeActorPath != null)
                    {
                        _pendingClose = true;
                        ExecuteActorSave(_activeActorPath);
                    }
                    else if (_currentScenePath != null)
                    {
                        _pendingClose = true;
                        ExecuteSave(_currentScenePath);
                    }
                    else
                    {
                        var dlg = new SaveFileDialog
                        {
                            Title            = "名前を付けてシーンを保存",
                            Filter           = "Scene Files (*.scene)|*.scene|All Files (*.*)|*.*",
                            DefaultExt       = ".scene",
                            InitialDirectory = AssetsPath,
                            OverwritePrompt  = true,
                        };
                        if (dlg.ShowDialog(this) == true)
                        {
                            _pendingClose = true;
                            ExecuteSave(dlg.FileName);
                        }
                        // Save As でキャンセルした場合は閉じない（e.Cancel = true のまま）
                    }
                    return;

                // No = 変更を破棄して終了（fall through）
            }
        }

        SaveLayout();
        ReleasePlayClamp();
        UninstallKeyboardHook();
        ReleaseAllCamKeys();
        if (_viewportHost != null) RevokeDragDrop(_viewportHost.ContainerHwnd);
        _vpDragOverlay?.Close();
        _runtimeManager?.Dispose();
        ViewportDocumentContent.Content = null;
    }

    // ── レイアウト保存 / 読み込み ────────────────────────────────

    private void SaveLayout()
    {
        try
        {
            var path = Path.Combine(SettingsDir, "layout.xml");
            var serializer = new XmlLayoutSerializer(DockManager);
            using var writer = new StreamWriter(path);
            serializer.Serialize(writer);
            EditorLog.Write($"レイアウトを保存しました: {path}");
        }
        catch (Exception ex)
        {
            EditorLog.Write($"レイアウト保存失敗: {ex.Message}");
        }
    }

    private void LoadLayout()
    {
        var path = Path.Combine(SettingsDir, "layout.xml");
        if (!File.Exists(path)) return;
        try
        {
            var serializer = new XmlLayoutSerializer(DockManager);
            serializer.LayoutSerializationCallback += (_, args) =>
            {
                args.Content = args.Model.ContentId switch
                {
                    "hierarchy"    => PanelHierarchy,
                    "project"      => PanelProject,
                    "inspector"    => PanelInspector,
                    "viewport"     => ViewportGrid,
                    "output"       => PanelOutput,
                    "ai_assistant" => _aiPanelUi,
                    _              => null,
                };
            };
            using var reader = new StreamReader(path);
            serializer.Deserialize(reader);
            EditorLog.Write($"レイアウトを読み込みました: {path}");
        }
        catch (Exception ex)
        {
            EditorLog.Write($"レイアウト読み込み失敗（デフォルトレイアウトを使用）: {ex.Message}");
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    // ── プロジェクト設定 ──────────────────────────────────────────

    /// <summary>
    /// 「プロジェクト設定」ボタン: プロジェクト設定ウィンドウをモーダルで開く。
    /// 設定ファイルは AssetsPath/project_settings.json に保存される。
    /// </summary>
    private void OnOpenProjectSettings(object sender, RoutedEventArgs e)
    {
        var win = new SEEDEditor.ProjectSettings.ProjectSettingsWindow(AssetsPath, EditorPluginsPath)
        {
            Owner = this,
        };
        win.ShowDialog();
    }

    // ── メニューバー ──────────────────────────────────────────────

    private void OnMenuQuickSave(object sender, RoutedEventArgs e)
        => DoQuickSave();

    private void OnMenuSaveAs(object sender, RoutedEventArgs e)
        => ShowSaveAsDialog();

    private void OnMenuExit(object sender, RoutedEventArgs e)
        => Close();

    /// <summary>パッケージ化ウィンドウを開く。</summary>
    private void OnOpenPackaging(object sender, RoutedEventArgs e)
    {
        var win = new SEEDEditor.Packaging.PackagingWindow(AssetsPath) { Owner = this };
        win.ShowDialog();
    }

    private void OnMenuUndo(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("UNDO");

    private void OnMenuRedo(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("REDO");

    private void OnMenuCopy(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("COPY");

    private void OnMenuPaste(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("PASTE");

    private void OnMenuDelete(object sender, RoutedEventArgs e)
        => TryDeleteSelected();

    // 表示メニューが開くたびに実際の表示状態でチェックを更新する
    private void OnViewMenuOpened(object sender, RoutedEventArgs e)
    {
        MenuItemHierarchy.IsChecked = IsPanelVisible("hierarchy");
        MenuItemInspector.IsChecked = IsPanelVisible("inspector");
        MenuItemProject.IsChecked   = IsPanelVisible("project");
        MenuItemOutput.IsChecked    = IsPanelVisible("output");
    }

    private bool IsPanelVisible(string contentId) =>
        DockManager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .Any(a => a.ContentId == contentId && a.IsVisible);

    private void OnTogglePanel(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string contentId) return;

        var panel = DockManager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId);
        if (panel is null) return;

        if (panel.IsVisible) panel.Hide();
        else panel.Show();

        // 実際の状態でチェックを確定する（WPF の自動トグルを上書き）
        item.IsChecked = panel.IsVisible;
    }

    // ── シーン保存 ────────────────────────────────────────────────

    // ── 選択インスタンス削除 ──────────────────────────────────

    private void TryDeleteSelected()
    {
        if (_deleteDialogOpen) return;
        if (_runtimeManager?.State != EditorState.Edit) return;

        // リネーム中（TextBox にフォーカスあり）は削除しない
        if (FocusManager.GetFocusedElement(this) is TextBox) return;

        var ids = PanelHierarchy.GetSelectedNonGroupIds();
        if (ids.Count == 0) return;

        if (!PanelHierarchy.AnyHasChildren(ids))
        {
            _runtimeManager!.SendToRuntime($"DELETE:{string.Join(",", ids)}");
            return;
        }

        _deleteDialogOpen = true;
        try
        {
            var result = MessageBox.Show(
                "選択中のオブジェクトに子オブジェクトが含まれています。\n\n" +
                "「はい」　— 子も含めてすべて削除\n" +
                "「いいえ」— 選択オブジェクトのみ削除（子は切り離してルートへ）",
                "オブジェクトの削除",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            var idsStr = string.Join(",", ids);
            if (result == MessageBoxResult.Yes)
                _runtimeManager!.SendToRuntime($"DELETE_RECURSIVE:{idsStr}");
            else if (result == MessageBoxResult.No)
                _runtimeManager!.SendToRuntime($"DELETE:{idsStr}");
        }
        finally
        {
            _deleteDialogOpen = false;
        }
    }

    // ── シーン保存ロジック ────────────────────────────────────────

    /// <summary>Ctrl+S: アクター編集中はアクターを、それ以外はシーンを上書き保存する。</summary>
    private void DoQuickSave()
    {
        if (_runtimeManager?.State != EditorState.Edit) return;
        if (_activeActorPath != null)
            ExecuteActorSave(_activeActorPath);
        else if (_currentScenePath != null)
            ExecuteSave(_currentScenePath);
        else
            ShowSaveAsDialog();
    }

    /// <summary>Ctrl+Shift+S / 名前を付けて保存。</summary>
    private void ShowSaveAsDialog()
    {
        if (_runtimeManager?.State != EditorState.Edit) return;
        if (_activeActorPath != null)
        {
            var dlg = new SaveFileDialog
            {
                Title            = "名前を付けてアクターを保存",
                Filter           = "Actor Files (*.actor)|*.actor|All Files (*.*)|*.*",
                DefaultExt       = ".actor",
                InitialDirectory = AssetsPath,
                OverwritePrompt  = true,
                FileName         = System.IO.Path.GetFileName(_activeActorPath),
            };
            if (dlg.ShowDialog(this) == true)
            {
                _activeActorPath = dlg.FileName;
                ExecuteActorSave(dlg.FileName);
            }
        }
        else
        {
            var dlg = new SaveFileDialog
            {
                Title            = "名前を付けてシーンを保存",
                Filter           = "Scene Files (*.scene)|*.scene|All Files (*.*)|*.*",
                DefaultExt       = ".scene",
                InitialDirectory = AssetsPath,
                OverwritePrompt  = true,
            };
            if (_currentScenePath != null)
                dlg.FileName = System.IO.Path.GetFileName(_currentScenePath);

            if (dlg.ShowDialog(this) == true)
                ExecuteSave(dlg.FileName);
        }
    }

    /// <summary>IPC でシーン保存コマンドを送出し、パスを記録する。</summary>
    private void ExecuteSave(string path)
    {
        _currentScenePath = path;
        _runtimeManager?.SendToRuntime($"SAVE_SCENE:{path}");
        EditorLog.Write($"ExecuteSave — SAVE_SCENE:{path}");
    }

    /// <summary>IPC でアクター保存コマンドを送出する。</summary>
    private void ExecuteActorSave(string path)
    {
        _isSavingActor = true;
        _runtimeManager?.SendToRuntime($"SAVE_ACTOR:{path}");
        EditorLog.Write($"ExecuteActorSave — SAVE_ACTOR:{path}");
    }

    private void OnSaveCompleted(bool ok, string errorMsg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ok)
            {
                EditorLog.Write("OnSaveCompleted — 保存成功");
                MarkClean();
                string toast = _isSavingActor ? "アクターを保存しました" : "シーンを保存しました";
                _isSavingActor = false;
                ShowToast(toast);
                UpdateTitle();

                // 保存→ウィンドウを閉じる（終了時確認フロー）
                if (_pendingClose)
                {
                    _pendingClose = false;
                    Close();
                    return;
                }

                // 保存→ロードの連鎖
                if (_pendingSceneLoad != null)
                {
                    var path = _pendingSceneLoad;
                    _pendingSceneLoad = null;
                    LoadScene(path);
                }
            }
            else
            {
                _isSavingActor = false;
                _pendingSceneLoad = null;
                MessageBox.Show($"保存に失敗しました:\n{errorMsg}", "SEED Editor",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    // ── ダーティ状態管理 ─────────────────────────────────────────

    /// <summary>
    /// シーンが変更されたことをマークする。UI・非UIスレッドどちらからでも呼べる。
    /// </summary>
    private void MarkDirty()
    {
        if (_isDirty) return;
        _isDirty = true;
        Dispatcher.BeginInvoke(UpdateTitle);
    }

    private void MarkDirtyFromHierarchy()
    {
        if (_suppressHierarchyDirtyCount > 0) { _suppressHierarchyDirtyCount--; return; }
        MarkDirty();
    }

    /// <summary>
    /// ナビゲーション系コマンドを送信する。
    /// 送信によって発生する HIERARCHY 更新をダーティ扱いしないようカウンターを +1 する。
    /// </summary>
    private void SendNavCommand(string cmd)
    {
        if (_runtimeManager != null)
            _suppressHierarchyDirtyCount++;
        _runtimeManager?.SendToRuntime(cmd);
    }

    private void MarkClean()
    {
        _isDirty = false;
    }

    private void UpdateTitle()
    {
        var name = _currentScenePath != null
            ? System.IO.Path.GetFileNameWithoutExtension(_currentScenePath)
            : "新規シーン";
        Title = _isDirty ? $"SEED Editor — {name}*" : $"SEED Editor — {name}";
        MenuQuickSave.Header = _currentScenePath != null ? "上書き保存" : "上書き保存（未保存）";
    }

    // ── トースト通知 ─────────────────────────────────────────────

    private void ShowToast(string message)
    {
        ToastText.Text      = message;
        ToastBorder.Visibility = Visibility.Visible;

        _toastTimer?.Stop();
        _toastTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5),
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };
        _toastTimer.Start();
    }

    // ── ビューポートコンテキストメニュー (C) ─────────────────────

    private void OnViewportContextMenuRequested()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_runtimeManager?.State != EditorState.Edit) return;

            var selectedIds = PanelHierarchy.GetSelectedNonGroupIds();
            bool hasSelection = selectedIds.Count > 0;

            var menu = new ContextMenu();

            if (hasSelection)
            {
                AddViewportMenuItem(menu, "コピー", "Ctrl+C", () => _runtimeManager?.SendToRuntime("COPY"));
                AddViewportMenuItem(menu, "削除",   "Del / Esc",    () =>
                {
                    var ids = PanelHierarchy.GetSelectedNonGroupIds();
                    if (ids.Count > 0)
                        _runtimeManager?.SendToRuntime($"DELETE_RECURSIVE:{string.Join(",", ids)}");
                });
                menu.Items.Add(new Separator());
                AddViewportMenuItem(menu, "アクタファイル化", null,
                    () => PanelHierarchy.ShowExportActorDialog());
                menu.Items.Add(new Separator());
            }
            // ── アクタを追加 サブメニュー ──────────────────────
            var addActorMenu = new MenuItem { Header = "アクタを追加" };

            var add3DItem = new MenuItem { Header = "3Dアクタ" };
            add3DItem.Click += (_, _) =>
            {
                PanelHierarchy.PrepareRenameAfterAdd();
                _runtimeManager?.SendToRuntime("ADD_ACTOR:0,-1");
            };
            addActorMenu.Items.Add(add3DItem);

            var add2DItem = new MenuItem { Header = "2Dアクタ" };
            add2DItem.Click += (_, _) =>
            {
                PanelHierarchy.PrepareRenameAfterAdd();
                _runtimeManager?.SendToRuntime("ADD_ACTOR_2D:0,-1");
            };
            addActorMenu.Items.Add(add2DItem);

            menu.Items.Add(addActorMenu);
            menu.Items.Add(new Separator());
            AddViewportMenuItem(menu, "ペースト", "Ctrl+V", () => _runtimeManager?.SendToRuntime("PASTE"));

            menu.PlacementTarget = ViewportDocumentContent;
            menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen          = true;
        });
    }

    private static void AddViewportMenuItem(ContextMenu menu, string header, string? gesture, Action action)
    {
        var item = new MenuItem { Header = header };
        if (gesture is not null) item.InputGestureText = gesture;
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    // ── ツールモード ──────────────────────────────────────────────

    private void OnToolSelect(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("TOOL:SELECT");

    private void OnToolMove(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("TOOL:MOVE");

    private void OnToolRotate(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("TOOL:ROTATE");

    private void OnToolScale(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("TOOL:SCALE");

    private void OnClampCursorChanged(object sender, RoutedEventArgs e)
    {
        _clampInPlay = ChkClampCursor.IsChecked == true;

        if (_runtimeManager?.State == EditorState.Play)
        {
            if (_clampInPlay && !_isDragging)
                ApplyPlayClamp();
            else
                ReleasePlayClamp();
        }
    }

    // ── 実行時コライダー描画 ────────────────────────────────────────

    /// <summary>
    /// "実行時コライダー描画" チェックボックスが変更されたときに呼ばれる。
    /// フラグを更新し、IPC コマンドで Runtime に通知する。
    /// </summary>
    private void OnPlayColliderDrawChanged(object sender, RoutedEventArgs e)
    {
        _playColliderDraw = ChkPlayColliderDraw.IsChecked == true;
        _runtimeManager?.SendToRuntime($"SET_PLAY_COLLIDER_DRAW:{(_playColliderDraw ? 1 : 0)}");
    }

    // ── 編集時の物理シミュレーション ────────────────────────────────

    /// <summary>
    /// "編集時の物理シミュレーション" チェックボックスが変更されたときに呼ばれる。
    /// RigidBody サブパネルの表示を切り替え、IPC コマンドで Runtime に通知する。
    /// </summary>
    private void OnEditPhysicsChanged(object sender, RoutedEventArgs e)
    {
        _editPhysicsEnabled = ChkEditPhysics.IsChecked == true;
        // RigidBody サブオプションの表示・非表示を切り替える
        PnlEditPhysicsRigidbody.Visibility = _editPhysicsEnabled
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        // 無効化時は RigidBody チェックもリセットする
        if (!_editPhysicsEnabled)
        {
            ChkEditPhysicsRigidbody.IsChecked = false;
            _editPhysicsWithRigidbody = false;
        }
        _runtimeManager?.SendToRuntime(
            $"SET_EDIT_PHYSICS:{(_editPhysicsEnabled ? 1 : 0)},{(_editPhysicsWithRigidbody ? 1 : 0)}");
    }

    /// <summary>
    /// "RigidBody" サブチェックボックスが変更されたときに呼ばれる。
    /// フラグを更新し、IPC コマンドで Runtime に通知する。
    /// </summary>
    private void OnEditPhysicsRigidbodyChanged(object sender, RoutedEventArgs e)
    {
        _editPhysicsWithRigidbody = ChkEditPhysicsRigidbody.IsChecked == true;
        _runtimeManager?.SendToRuntime(
            $"SET_EDIT_PHYSICS:{(_editPhysicsEnabled ? 1 : 0)},{(_editPhysicsWithRigidbody ? 1 : 0)}");
    }

    /// <summary>
    /// "編集時の 2D 物理シミュレーション" チェックボックスが変更されたときに呼ばれる。
    /// RigidBody サブパネルの表示を切り替え、IPC コマンドで Runtime に通知する。
    /// </summary>
    private void OnEditPhysics2dChanged(object sender, RoutedEventArgs e)
    {
        _editPhysics2dEnabled = ChkEditPhysics2d.IsChecked == true;
        // RigidBody サブオプションの表示・非表示を切り替える
        PnlEditPhysics2dRigidbody.Visibility = _editPhysics2dEnabled
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        // 無効化時は RigidBody チェックもリセットする
        if (!_editPhysics2dEnabled)
        {
            ChkEditPhysics2dRigidbody.IsChecked = false;
            _editPhysics2dWithRigidbody = false;
        }
        _runtimeManager?.SendToRuntime(
            $"SET_EDIT_PHYSICS_2D:{(_editPhysics2dEnabled ? 1 : 0)},{(_editPhysics2dWithRigidbody ? 1 : 0)}");
    }

    /// <summary>
    /// 2D 物理の "RigidBody" サブチェックボックスが変更されたときに呼ばれる。
    /// フラグを更新し、IPC コマンドで Runtime に通知する。
    /// </summary>
    private void OnEditPhysics2dRigidbodyChanged(object sender, RoutedEventArgs e)
    {
        _editPhysics2dWithRigidbody = ChkEditPhysics2dRigidbody.IsChecked == true;
        _runtimeManager?.SendToRuntime(
            $"SET_EDIT_PHYSICS_2D:{(_editPhysics2dEnabled ? 1 : 0)},{(_editPhysics2dWithRigidbody ? 1 : 0)}");
    }

    // ── Play 時カーソルクランプ（IPC 経由で Rust 側が毎フレーム適用）──────

    private void OnRuntimeHwndAvailable(nint hwnd)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // 最大化起動などでコンテナサイズと Runtime ウィンドウサイズがズレる場合に補正
            if (_runtimeManager?.State == EditorState.Edit)
                _runtimeManager.ResizeRuntimeToContainer();

            if (_clampInPlay && !_isDragging && _runtimeManager?.State == EditorState.Play)
                ApplyPlayClamp();

            SyncViewportSettings();

            // FIRST_FRAME が届かない場合のフォールバック（リリースビルドの Runtime 等）。
            // READY 受信から 3 秒経ってもオーバーレイが残っていれば強制的に閉じる。
            Task.Delay(3000).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (ViewportLoadingOverlay.Visibility != Visibility.Collapsed)
                    ViewportLoadingOverlay.Visibility = Visibility.Collapsed;
            }));
        });
    }

    /// <summary>
    /// ランタイムが最初の実フレームを描画したときに呼ばれる（デバッグビルドのみ）。
    /// このタイミングで起動中オーバーレイを非表示にする。
    /// </summary>
    private void OnFirstFrameReady()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ViewportLoadingOverlay.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>
    /// Rust ランタイムへ PLAY_CLAMP:1 を送信する。
    /// Rust 側が毎フレーム ClipCursor を再適用するため C# 側タイマーは不要。
    /// </summary>
    private void ApplyPlayClamp()
    {
        _runtimeManager?.SendToRuntime("PLAY_CLAMP:1");
    }

    private void ReleasePlayClamp()
    {
        _runtimeManager?.SendToRuntime("PLAY_CLAMP:0");
    }

    // ── グローバルキーボードフック ────────────────────────────────

    private void InstallKeyboardHook()
    {
        _llKeyProc = LLKeyboardCallback;
        var hMod = GetModuleHandle(null);
        _llKeyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _llKeyProc, hMod, 0);
        EditorLog.Write($"InstallKeyboardHook — hook=0x{_llKeyHook:X}");
    }

    private void UninstallKeyboardHook()
    {
        if (_llKeyHook != 0)
        {
            UnhookWindowsHookEx(_llKeyHook);
            _llKeyHook = 0;
        }
    }

    private nint LLKeyboardCallback(int nCode, nint wParam, nint lParam)
    {
        // IsEditorForeground() が false になるケース:
        //   Pause中に埋め込みPlayウィンドウがフォーカスを持つと
        //   GetForegroundWindow() がランタイムPIDを返すため false になる。
        // IsCamInputActive() が true の場合（Edit/Pause）はカメラキー転送を
        // 行う必要があるため、フォーカス不問でブロックに入る。
        // 内部の Ctrl+Z 等は別途 State == Edit で保護されているので問題なし。
        if (nCode >= 0 && (IsEditorForeground() || IsCamInputActive()))
        {
            var kb     = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var vk     = kb.vkCode;
            bool isDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            bool isUp   = wParam == WM_KEYUP   || wParam == WM_SYSKEYUP;

            // Ctrl キー追跡 + Rust へ転送
            if (vk == 0x11 || vk == 0xA2 || vk == 0xA3)
            {
                if (isDown && !_ctrlHeld)
                    _runtimeManager?.SendToRuntime("CTRL_DOWN");
                else if (isUp && _ctrlHeld)
                    _runtimeManager?.SendToRuntime("CTRL_UP");
                _ctrlHeld = isDown;
            }
            // Ctrl+Z / Ctrl+Y / Ctrl+S → IPC 経由で転送（Edit モードのみ）
            else if (isDown && _ctrlHeld && _runtimeManager?.State == EditorState.Edit)
            {
                if (vk == 0x5A) // Z
                {
                    _runtimeManager?.SendToRuntime("UNDO");
                    return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                }
                else if (vk == 0x59) // Y
                {
                    _runtimeManager?.SendToRuntime("REDO");
                    return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                }
                else if (vk == 0x53) // S
                {
                    bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
                    Dispatcher.BeginInvoke(shift ? (Action)ShowSaveAsDialog : DoQuickSave);
                    return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                }
                else if (vk == 0x43) // C
                {
                    Dispatcher.BeginInvoke(() => {
                        if (FocusManager.GetFocusedElement(this) is TextBox) return;
                        if (PanelProject.IsKeyboardFocusWithin) PanelProject.HandleCopy();
                        else _runtimeManager?.SendToRuntime("COPY");
                    });
                    return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                }
                else if (vk == 0x58) // X
                {
                    Dispatcher.BeginInvoke(() => {
                        if (FocusManager.GetFocusedElement(this) is TextBox) return;
                        if (PanelProject.IsKeyboardFocusWithin) PanelProject.HandleCut();
                    });
                    return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                }
                else if (vk == 0x56) // V
                {
                    Dispatcher.BeginInvoke(() => {
                        if (FocusManager.GetFocusedElement(this) is TextBox) return;
                        if (PanelProject.IsKeyboardFocusWithin) PanelProject.HandlePaste();
                        else _runtimeManager?.SendToRuntime("PASTE");
                    });
                    return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                }
            }

            // ESC / Del → 選択インスタンス削除（ダイアログあり）
            if (isDown && (vk == 0x1B || vk == 0x2E) && !_ctrlHeld
                && _runtimeManager?.State == EditorState.Edit
                && !_deleteDialogOpen)
            {
                Dispatcher.BeginInvoke(TryDeleteSelected);
                return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
            }

            if (VkKeyMap.TryGetValue(vk, out var keyName) && IsCamInputActive())
            {
                if (isDown && _pressedVks.Add(vk))
                    _runtimeManager?.SendToRuntime($"CAM_KEY_DOWN:{keyName}");
                else if (isUp && _pressedVks.Remove(vk))
                    _runtimeManager?.SendToRuntime($"CAM_KEY_UP:{keyName}");
            }
            else if (isUp)
            {
                _pressedVks.Remove(vk);
            }
        }
        return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
    }

    private static bool IsEditorForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == 0) return false;
        GetWindowThreadProcessId(fg, out var fgPid);
        return fgPid == (uint)Environment.ProcessId;
    }

    private bool IsCamInputActive()
    {
        var state = _runtimeManager?.State;
        return state == EditorState.Edit || state == EditorState.Pause;
    }

    // ── シーンファイル読み込み ────────────────────────────────────

    private void OnSceneFileOpened(string path)
    {
        if (_runtimeManager?.State != EditorState.Edit) return;

        if (_isDirty)
        {
            var result = MessageBox.Show(
                "未保存の変更があります。シーンを切り替える前に保存しますか？",
                "SEED Editor",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel) return;

            if (result == MessageBoxResult.Yes)
            {
                if (_currentScenePath != null)
                {
                    // 上書き保存してから読み込み
                    _pendingSceneLoad = path;
                    ExecuteSave(_currentScenePath);
                }
                else
                {
                    // 名前を付けて保存してから読み込み
                    var dlg = new SaveFileDialog
                    {
                        Title            = "名前を付けてシーンを保存",
                        Filter           = "Scene Files (*.scene)|*.scene|All Files (*.*)|*.*",
                        DefaultExt       = ".scene",
                        InitialDirectory = AssetsPath,
                        OverwritePrompt  = true,
                    };
                    if (dlg.ShowDialog(this) == true)
                    {
                        _pendingSceneLoad = path;
                        ExecuteSave(dlg.FileName);
                    }
                    // キャンセルした場合は何もしない
                }
                return;
            }
            // No = 変更を破棄して読み込み
        }

        // アクター編集中の場合はシーンモードに戻す（タブは保持）
        if (_activeActorPath != null)
        {
            _activeActorPath = null;
            PanelHierarchy.SetActorEditMode(false);
            PanelInspector.SetActorEditMode(false);
            RebuildActorTabBar();
        }

        LoadScene(path);
    }

    // ── アクターファイル編集 ─────────────────────────────────────

    private void OnActorFileOpened(string path)
    {
        if (_runtimeManager?.State != EditorState.Edit) return;

        // 同じアクターが既にアクティブなら何もしない
        if (_activeActorPath == path) return;

        var existing = _actorTabs.FirstOrDefault(t => t.Path == path);
        if (existing != null)
        {
            // 既存タブに切り替え（再ロードなし）
            _activeActorPath = path;
            SendNavCommand($"SET_ACTIVE_WORLD_LINE:{existing.WorldLine}");
            EditorLog.Write($"OnActorFileOpened — SET_ACTIVE_WORLD_LINE:{existing.WorldLine}");
        }
        else
        {
            // 新規タブ: 世界線を割り当ててロード
            var wl      = _nextWorldLineIdx++;
            var name    = System.IO.Path.GetFileNameWithoutExtension(path);
            // actor_kind を読み取って 2D アクターか判定する
            var is2D    = DetectActorIs2D(path);
            _actorTabs.Add(new ActorTab(path, name, wl, is2D));
            _activeActorPath = path;
            SendNavCommand($"OPEN_ACTOR:{wl},{path}");
            EditorLog.Write($"OnActorFileOpened — OPEN_ACTOR:{wl},{path} (is2D={is2D})");
        }

        RebuildActorTabBar();
    }

    // ── InputMap ファイル編集 ────────────────────────────────────

    /// <summary>.inputmap ファイルをダブルクリックしたときに InputMap エディタを開く。</summary>
    private void OnInputMapFileOpened(string path)
    {
        var win = new SEEDEditor.InputMap.InputMapEditorWindow(path) { Owner = this };
        win.Show();
    }

    private void OnActorEditStarted()
    {
        Dispatcher.InvokeAsync(() =>
        {
            var tab   = _actorTabs.LastOrDefault();
            var wl    = tab?.WorldLine ?? 1u;
            var is2D  = tab?.IsActor2D ?? false;
            PanelHierarchy.SetActorEditMode(true, wl, is2D);
            PanelInspector.SetActorEditMode(true);
            RebuildActorTabBar();
        });
    }

    private void OnActorEditEnded()
    {
        _activeActorPath = null;
        Dispatcher.InvokeAsync(() =>
        {
            PanelHierarchy.SetActorEditMode(false);
            PanelInspector.SetActorEditMode(false);
            RebuildActorTabBar();
        });
    }

    private void OnReturnToScene(object sender, RoutedEventArgs e)
    {
        if (_runtimeManager?.State != EditorState.Edit) return;
        SendNavCommand("SET_ACTIVE_WORLD_LINE:0");
        _activeActorPath = null;
        PanelHierarchy.SetActorEditMode(false);
        PanelInspector.SetActorEditMode(false);
        RebuildActorTabBar();
        EditorLog.Write("OnReturnToScene — SET_ACTIVE_WORLD_LINE:0");
    }

    /// <summary>タブをクリックしてそのアクターに切り替える。</summary>
    private void ActivateActorTab(string path)
    {
        if (_runtimeManager?.State != EditorState.Edit) return;
        if (_activeActorPath == path) return;
        var tab = _actorTabs.FirstOrDefault(t => t.Path == path);
        if (tab == null) return;
        _activeActorPath = path;
        SendNavCommand($"SET_ACTIVE_WORLD_LINE:{tab.WorldLine}");
        PanelHierarchy.SetActorEditMode(true, tab.WorldLine, tab.IsActor2D);
        PanelInspector.SetActorEditMode(true);
        RebuildActorTabBar();
    }

    /// <summary>
    /// アクターファイルを読み取り、actor_kind が "Actor2D" かどうかを返す。
    /// ファイルが読めない場合は false を返す。
    /// </summary>
    private static bool DetectActorIs2D(string path)
    {
        // .actor2d 拡張子は定義上 2D アクター。JSON 解析不要。
        if (path.EndsWith(".actor2d", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var json = System.IO.File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("actor_kind", out var kind)
                && kind.GetString() == "Actor2D";
        }
        catch { return false; }
    }

    /// <summary>タブの × を押してそのタブを閉じる。</summary>
    private void CloseActorTab(string path)
    {
        var idx = _actorTabs.FindIndex(t => t.Path == path);
        if (idx < 0) return;

        var closingTab = _actorTabs[idx];
        _actorTabs.RemoveAt(idx);

        // 閉じるタブの世界線アクターを除去（ナビゲーションではないので SendNavCommand 不要）
        _runtimeManager?.SendToRuntime($"REMOVE_WORLD_LINE:{closingTab.WorldLine}");

        if (_activeActorPath == path)
        {
            if (_actorTabs.Count > 0)
            {
                // 隣のタブに切り替え
                var next = _actorTabs[Math.Min(idx, _actorTabs.Count - 1)];
                _activeActorPath = next.Path;
                SendNavCommand($"SET_ACTIVE_WORLD_LINE:{next.WorldLine}");
            }
            else
            {
                // タブが空になったのでシーンに戻る
                _activeActorPath = null;
                SendNavCommand("SET_ACTIVE_WORLD_LINE:0");
            }
        }

        RebuildActorTabBar();
    }

    /// <summary>タブバー UI を再構築する。</summary>
    private void RebuildActorTabBar()
    {
        ActorTabsPanel.Children.Clear();

        foreach (var tab in _actorTabs)
        {
            bool isActive = tab.Path == _activeActorPath;

            // タブ外枠
            var tabBorder = new Border
            {
                Height          = 28,
                MinWidth        = 60,
                MaxWidth        = 200,
                Cursor          = System.Windows.Input.Cursors.Hand,
                Background      = isActive
                                    ? new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C))
                                    : Brushes.Transparent,
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                BorderThickness = isActive
                                    ? new Thickness(0, 2, 1, 0)   // 上 2px はオレンジ（上書き後）
                                    : new Thickness(0, 0, 1, 0),
            };

            // アクティブタブはオレンジのトップアクセント
            if (isActive)
                tabBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x78, 0x20));

            // ホバー効果
            tabBorder.MouseEnter += (s, e) =>
            {
                if (tab.Path != _activeActorPath)
                    tabBorder.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E));
            };
            tabBorder.MouseLeave += (s, e) =>
            {
                if (tab.Path != _activeActorPath)
                    tabBorder.Background = Brushes.Transparent;
            };

            // タブクリック → アクター切り替え
            tabBorder.MouseLeftButtonDown += (s, e) => ActivateActorTab(tab.Path);

            // 内部レイアウト
            var dp = new DockPanel { Margin = new Thickness(8, 0, 4, 0) };

            // × ボタン
            var closeBtn = new System.Windows.Controls.Button
            {
                Content         = "×",
                Width           = 16,
                Height          = 16,
                Margin          = new Thickness(4, 0, 0, 0),
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground      = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize        = 11,
                Cursor          = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Padding         = new Thickness(0),
            };
            // × ボタンのホバー
            closeBtn.MouseEnter += (s, e) =>
                closeBtn.Foreground = Brushes.White;
            closeBtn.MouseLeave += (s, e) =>
                closeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            closeBtn.Click += (s, e) =>
            {
                e.Handled = true;
                CloseActorTab(tab.Path);
            };

            // アクター名テキスト
            var txt = new TextBlock
            {
                Text              = tab.Name,
                Foreground        = isActive ? Brushes.White
                                             : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize          = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };

            DockPanel.SetDock(closeBtn, Dock.Right);
            dp.Children.Add(closeBtn);
            dp.Children.Add(txt);
            tabBorder.Child = dp;

            ActorTabsPanel.Children.Add(tabBorder);
        }

        // タブバー全体の表示制御
        ActorTabBar.Visibility  = _actorTabs.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        // "シーンに戻る" はアクター編集中のみ表示
        BtnReturnToScene.Visibility = _activeActorPath != null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>シーンを読み込む（ダーティチェック済みの場合に直接呼ぶ）。</summary>
    private void LoadScene(string path)
    {
        _currentScenePath = path;
        _isDirty = false;
        SendNavCommand($"LOAD_SCENE:{path}");
        UpdateTitle();
        EditorLog.Write($"LoadScene — LOAD_SCENE:{path}");
    }

    // ── Viewport オプション ──────────────────────────────────────

    private bool _updatingControls = false;

    /// <summary>ポップアップをアイコン右上に配置するコールバック。</summary>
    public System.Windows.Controls.Primitives.CustomPopupPlacement[]
        OnViewportPopupPlacement(Size popupSize, Size targetSize, Point offset)
    {
        // ボタン右端・ボタン下端にポップアップ底面を揃えて上方向へ展開
        double x = targetSize.Width + 4;
        double y = targetSize.Height - popupSize.Height;
        return [new(new Point(x, y), System.Windows.Controls.Primitives.PopupPrimaryAxis.Vertical)];
    }

    private void OnViewportOptions(object sender, RoutedEventArgs e)
    {
        bool opening = !ViewportOptionsPopup.IsOpen;
        ViewportOptionsPopup.IsOpen = opening;
        if (opening && _runtimeManager?.State == EditorState.Edit)
            _runtimeManager.SendToRuntime("GET_CAM_STATE");
    }

    private void OnFovChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_viewportSettingsInitialized || _updatingControls) return;
        var v = (int)SldFov.Value;
        _updatingControls = true;
        TbFovInput.Text = v.ToString();
        _updatingControls = false;
        _runtimeManager?.SendToRuntime($"VIEWPORT_FOV:{v}");
    }

    private void OnFarChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_viewportSettingsInitialized || _updatingControls) return;
        var v = (int)SldFar.Value;
        _updatingControls = true;
        TbFarInput.Text = v.ToString();
        _updatingControls = false;
        _runtimeManager?.SendToRuntime($"VIEWPORT_FAR:{v}");
    }

    private void OnShowGridChanged(object sender, RoutedEventArgs e)
    {
        if (!_viewportSettingsInitialized) return;
        _runtimeManager?.SendToRuntime($"SHOW_GRID:{(ChkShowGrid.IsChecked == true ? "1" : "0")}");
    }

    private void OnShowAxisGizmoChanged(object sender, RoutedEventArgs e)
    {
        if (!_viewportSettingsInitialized) return;
        _runtimeManager?.SendToRuntime($"SHOW_AXIS_GIZMO:{(ChkShowAxisGizmo.IsChecked == true ? "1" : "0")}");
    }

    /// キャンバス表示モード切り替え（スクリーンスペース / ワールドスペース）
    private void OnCanvasScreenSpaceChanged(object sender, RoutedEventArgs e)
    {
        if (!_viewportSettingsInitialized) return;
        _runtimeManager?.SendToRuntime($"CANVAS_SS_OVERLAY:{(ChkCanvasScreenSpace.IsChecked == true ? "1" : "0")}");
    }

    private void OnCamSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_viewportSettingsInitialized || _updatingControls) return;
        var v = Math.Round(SldCamSpeed.Value, 2);
        _updatingControls = true;
        TbSpeedInput.Text = $"{v:F1}";
        _updatingControls = false;
        _runtimeManager?.SendToRuntime($"CAM_SPEED:{v.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    // ── スライダー横の数値入力 ────────────────────────────────────

    private void OnSliderNumKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb)
        {
            ApplySliderTextInput(tb);
            e.Handled = true;
        }
    }

    private void OnSliderNumLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) ApplySliderTextInput(tb);
    }

    private void ApplySliderTextInput(TextBox tb)
    {
        if (_updatingControls || !_viewportSettingsInitialized) return;
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        if (!double.TryParse(tb.Text, System.Globalization.NumberStyles.Float, ic, out var v)) return;

        if (tb == TbFovInput)
        {
            v = Math.Clamp(v, SldFov.Minimum, SldFov.Maximum);
            var vi = (int)v;
            _updatingControls = true;
            SldFov.Value = vi;
            tb.Text = vi.ToString();
            _updatingControls = false;
            _runtimeManager?.SendToRuntime($"VIEWPORT_FOV:{vi}");
        }
        else if (tb == TbFarInput)
        {
            v = Math.Clamp(v, SldFar.Minimum, SldFar.Maximum);
            var vi = (int)v;
            _updatingControls = true;
            SldFar.Value = vi;
            tb.Text = vi.ToString();
            _updatingControls = false;
            _runtimeManager?.SendToRuntime($"VIEWPORT_FAR:{vi}");
        }
        else if (tb == TbSpeedInput)
        {
            v = Math.Clamp(v, SldCamSpeed.Minimum, SldCamSpeed.Maximum);
            _updatingControls = true;
            SldCamSpeed.Value = v;
            tb.Text = $"{v:F1}";
            _updatingControls = false;
            _runtimeManager?.SendToRuntime($"CAM_SPEED:{v.ToString(ic)}");
        }
    }

    // ── カメラ Transform 入力 ─────────────────────────────────────

    private void OnCamFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitCamTransform();
    }

    private void OnCamFieldLostFocus(object sender, RoutedEventArgs e)
    {
        CommitCamTransform();
    }

    private void CommitCamTransform()
    {
        if (!_viewportSettingsInitialized) return;
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var ns = System.Globalization.NumberStyles.Float;
        if (!float.TryParse(TbCamPx.Text,    ns, ic, out float px))    return;
        if (!float.TryParse(TbCamPy.Text,    ns, ic, out float py))    return;
        if (!float.TryParse(TbCamPz.Text,    ns, ic, out float pz))    return;
        if (!float.TryParse(TbCamYaw.Text,   ns, ic, out float yaw))   return;
        if (!float.TryParse(TbCamPitch.Text, ns, ic, out float pitch)) return;
        _runtimeManager?.SendToRuntime(
            $"CAM_TRANSFORM:{px.ToString(ic)},{py.ToString(ic)},{pz.ToString(ic)},{yaw.ToString(ic)},{pitch.ToString(ic)}");
    }

    // ── カメラ状態受信 ────────────────────────────────────────────

    private void OnCameraStateReceived(string payload)
    {
        // CAM_STATE:{px},{py},{pz},{yaw_deg},{pitch_deg},{fov_deg},{far},{speed}
        var parts = payload.Split(',');
        if (parts.Length < 8) return;
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var ns = System.Globalization.NumberStyles.Float;
        if (!float.TryParse(parts[0], ns, ic, out float px))    return;
        if (!float.TryParse(parts[1], ns, ic, out float py))    return;
        if (!float.TryParse(parts[2], ns, ic, out float pz))    return;
        if (!float.TryParse(parts[3], ns, ic, out float yaw))   return;
        if (!float.TryParse(parts[4], ns, ic, out float pitch)) return;
        if (!float.TryParse(parts[5], ns, ic, out float fov))   return;
        if (!float.TryParse(parts[6], ns, ic, out float far))   return;
        if (!float.TryParse(parts[7], ns, ic, out float speed)) return;

        Dispatcher.InvokeAsync(() =>
        {
            bool prev = _viewportSettingsInitialized;
            _viewportSettingsInitialized = false;
            _updatingControls = true;
            TbCamPx.Text    = Fmt(px);
            TbCamPy.Text    = Fmt(py);
            TbCamPz.Text    = Fmt(pz);
            TbCamYaw.Text   = Fmt(yaw);
            TbCamPitch.Text = Fmt(pitch);
            var fovC   = Math.Clamp(fov,   SldFov.Minimum,      SldFov.Maximum);
            var farC   = Math.Clamp(far,   SldFar.Minimum,      SldFar.Maximum);
            var spdC   = Math.Clamp(speed, SldCamSpeed.Minimum, SldCamSpeed.Maximum);
            SldFov.Value      = fovC;   TbFovInput.Text   = ((int)fovC).ToString();
            SldFar.Value      = farC;   TbFarInput.Text   = ((int)farC).ToString();
            SldCamSpeed.Value = spdC;   TbSpeedInput.Text = $"{spdC:F1}";
            _updatingControls = false;
            _viewportSettingsInitialized = prev;
        });
    }

    private static string Fmt(float v) =>
        v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    private void OnResetViewportSettings(object sender, RoutedEventArgs e)
    {
        _updatingControls = true;
        SldFov.Value      = 45;   TbFovInput.Text   = "45";
        SldFar.Value      = 1000; TbFarInput.Text   = "1000";
        SldCamSpeed.Value = 5;    TbSpeedInput.Text = "5.0";
        _updatingControls = false;
        ChkShowGrid.IsChecked        = true;
        ChkShowAxisGizmo.IsChecked   = true;
        ChkCanvasScreenSpace.IsChecked = false;
        if (_viewportSettingsInitialized)
        {
            _runtimeManager?.SendToRuntime("VIEWPORT_FOV:45");
            _runtimeManager?.SendToRuntime("VIEWPORT_FAR:1000");
            _runtimeManager?.SendToRuntime("CAM_SPEED:5");
            _runtimeManager?.SendToRuntime("CANVAS_SS_OVERLAY:0");
        }
        TbCamPx.Text = "0"; TbCamPy.Text = "2"; TbCamPz.Text = "-10";
        TbCamYaw.Text = "0"; TbCamPitch.Text = "0";
        CommitCamTransform();
    }

    private void SyncViewportSettings()
    {
        _viewportSettingsInitialized = true;
        var fov = (int)SldFov.Value;
        TbFovInput.Text = fov.ToString();
        _runtimeManager?.SendToRuntime($"VIEWPORT_FOV:{fov}");
        var far = (int)SldFar.Value;
        TbFarInput.Text = far.ToString();
        _runtimeManager?.SendToRuntime($"VIEWPORT_FAR:{far}");
        _runtimeManager?.SendToRuntime($"SHOW_GRID:{(ChkShowGrid.IsChecked == true ? "1" : "0")}");
        _runtimeManager?.SendToRuntime($"SHOW_AXIS_GIZMO:{(ChkShowAxisGizmo.IsChecked == true ? "1" : "0")}");
        var spd = Math.Round(SldCamSpeed.Value, 2);
        TbSpeedInput.Text = $"{spd:F1}";
        _runtimeManager?.SendToRuntime($"CAM_SPEED:{spd.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        // 編集時物理設定は Edit runtime にのみ送信する（Play runtime へ送ると物理スレッドが停止する）
        if (_runtimeManager?.State == EditorState.Edit)
        {
            _runtimeManager?.SendToRuntime(
                $"SET_EDIT_PHYSICS:{(_editPhysicsEnabled ? 1 : 0)},{(_editPhysicsWithRigidbody ? 1 : 0)}");
            _runtimeManager?.SendToRuntime(
                $"SET_EDIT_PHYSICS_2D:{(_editPhysics2dEnabled ? 1 : 0)},{(_editPhysics2dWithRigidbody ? 1 : 0)}");
        }
        // コライダー描画は Play/Edit 両方で送信する
        _runtimeManager?.SendToRuntime($"SET_PLAY_COLLIDER_DRAW:{(_playColliderDraw ? 1 : 0)}");
    }

    private void ReleaseAllCamKeys()
    {
        foreach (var vk in _pressedVks)
        {
            if (VkKeyMap.TryGetValue(vk, out var keyName))
                _runtimeManager?.SendToRuntime($"CAM_KEY_UP:{keyName}");
        }
        _pressedVks.Clear();
    }

    // ── 状態変化への UI 反応 ───────────────────────────────────────

    private void OnStateChanged(EditorState state)
    {
        EditorLog.Write($"OnStateChanged — {state}");
        Dispatcher.BeginInvoke(() => ApplyUiState(state));
    }

    private void ApplyUiState(EditorState state)
    {
        if (state != EditorState.Edit && state != EditorState.Pause)
            _pressedVks.Clear();

        // Play 移行時はクランプ適用、それ以外は解除
        // RuntimeHwnd が 0 の場合は OnRuntimeHwndAvailable でリトライされる
        if (state == EditorState.Play && _clampInPlay && !_isDragging)
            ApplyPlayClamp();
        else
            ReleasePlayClamp();

        EditorLog.Write($"ApplyUiState — {state}");
        switch (state)
        {
            case EditorState.Edit:
                _pressedVks.Clear();
                BtnPlayPause.IsEnabled   = true;
                BtnPlayPause.Background  = _brushPlay;
                ImgPlayPause.Source      = _imgPlay;
                BtnStop.IsEnabled        = false;
                LblState.Text            = "● EDIT";
                LblState.Foreground      = System.Windows.Media.Brushes.LightGreen;
                ViewportDocumentContent.Visibility = Visibility.Visible;
                TxtViewportStatus.Text             = "";
                ViewportLoadingOverlay.Visibility  = Visibility.Visible;
                Activate();
                break;

            case EditorState.Play:
                BtnPlayPause.IsEnabled   = true;
                BtnPlayPause.Background  = _brushPause;
                ImgPlayPause.Source      = _imgPause;
                BtnStop.IsEnabled        = true;
                LblState.Text            = "▶ PLAY";
                LblState.Foreground      = System.Windows.Media.Brushes.LightSkyBlue;
                ViewportDocumentContent.Visibility = Visibility.Hidden;
                ViewportLoadingOverlay.Visibility  = Visibility.Collapsed;
                break;

            case EditorState.Pause:
                BtnPlayPause.IsEnabled   = true;
                BtnPlayPause.Background  = _brushPlay;
                ImgPlayPause.Source      = _imgPlay;
                BtnStop.IsEnabled        = true;
                LblState.Text            = "⏸ PAUSE";
                LblState.Foreground      = System.Windows.Media.Brushes.Orange;
                ViewportDocumentContent.Visibility = Visibility.Visible;
                ViewportLoadingOverlay.Visibility  = Visibility.Collapsed;
                break;

            case EditorState.Building:
                BtnPlayPause.IsEnabled   = false;
                BtnPlayPause.Background  = _brushPlay;
                ImgPlayPause.Source      = _imgPlay;
                BtnStop.IsEnabled        = false;
                LblState.Text            = "⚙ BUILDING...";
                LblState.Foreground      = System.Windows.Media.Brushes.Yellow;
                TxtViewportStatus.Text            = "ビルド中...";
                ViewportLoadingOverlay.Visibility = Visibility.Visible;
                break;

            case EditorState.Idle:
                BtnPlayPause.IsEnabled   = false;
                BtnPlayPause.Background  = _brushPlay;
                ImgPlayPause.Source      = _imgPlay;
                BtnStop.IsEnabled        = false;
                LblState.Text            = "○ IDLE";
                LblState.Foreground      = System.Windows.Media.Brushes.Gray;
                TxtViewportStatus.Text            = "再起動中...";
                ViewportLoadingOverlay.Visibility = Visibility.Visible;
                break;
        }

        // FPS 表示: Edit / Pause / Play のときのみ表示（Idle / Building では非表示）
        var showFps = state == EditorState.Edit
                   || state == EditorState.Pause
                   || state == EditorState.Play;
        TxtFps.Visibility = showFps ? Visibility.Visible : Visibility.Collapsed;
        if (!showFps) TxtFps.Text = "";
    }

    /// <summary>ランタイムからFPS通知を受け取ったときにUI上の表示を更新する。</summary>
    private void OnFpsReceived(float fps)
    {
        // IPC コールバックはバックグラウンドスレッドから来るため Dispatcher 経由で更新する
        Dispatcher.BeginInvoke(() =>
        {
            TxtFps.Text = $"FPS: {fps:F1}";
        });
    }
}
