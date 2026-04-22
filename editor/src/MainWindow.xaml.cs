using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using SEEDEditor.Panels;
using SEEDEditor.Runtime;
using SEEDEditor.Viewport;

namespace SEEDEditor;

public partial class MainWindow : Window
{
    // ── P/Invoke ────────────────────────────────────────────────

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint  vkCode;
        public uint  scanCode;
        public uint  flags;
        public uint  time;
        public nint  dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // Windows 10 20H1+
    private const int DWMWA_CAPTION_COLOR           = 35; // Windows 11+
    private const int DWMWA_BORDER_COLOR            = 34; // Windows 11+

    [DllImport("user32.dll")] static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] static extern nint GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern bool ClipCursor(ref RECT lpRect);
    [DllImport("user32.dll")] static extern bool ClipCursor(nint lpRect); // null 用

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;
    private const int WM_SYSCOMMAND  = 0x0112;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int SC_MOVE        = 0xF010;

    // ── フィールド ──────────────────────────────────────────────

    private LowLevelKeyboardProc? _llKeyProc;
    private nint                  _llKeyHook;

    private readonly HashSet<uint> _pressedVks = new();

    private bool _clampInPlay      = false;
    private bool _isDragging       = false;
    private bool _ctrlHeld         = false;
    private bool _deleteDialogOpen = false;

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

    private static readonly string RuntimeExePath = ResolveRuntimePath();
    private static readonly string AssetsPath     = ResolveAssetsPath();
    private static readonly string SettingsDir    = ResolveSettingsDir();

    private static string ResolveSettingsDir()
    {
        var dir = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\settings"));
        Directory.CreateDirectory(dir);
        return dir;
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

    private ViewportHost?   _viewportHost;
    private RuntimeManager? _runtimeManager;

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

        _runtimeManager = new RuntimeManager(RuntimeExePath);
        _runtimeManager.StateChanged         += OnStateChanged;
        _runtimeManager.RuntimeHwndAvailable += OnRuntimeHwndAvailable;
        _runtimeManager.RuntimeMoveStart     += () => { _isDragging = true;  Dispatcher.BeginInvoke(ReleasePlayClamp); };
        _runtimeManager.RuntimeMoveEnd       += () => { _isDragging = false; Dispatcher.BeginInvoke(() => { if (_clampInPlay && _runtimeManager?.State == EditorState.Play) ApplyPlayClamp(); }); };

        _runtimeManager.SaveCompleted                 += OnSaveCompleted;
        _runtimeManager.ViewportContextMenuRequested  += OnViewportContextMenuRequested;
        _runtimeManager.FirstFrameReady               += OnFirstFrameReady;

        PanelHierarchy.SetRuntime(_runtimeManager);
        PanelInspector.SetRuntime(_runtimeManager);
        PanelProject.SetAssetsPath(AssetsPath);

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
        SaveLayout();
        ReleasePlayClamp();
        UninstallKeyboardHook();
        ReleaseAllCamKeys();
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
                    "hierarchy" => PanelHierarchy,
                    "project"   => PanelProject,
                    "inspector" => PanelInspector,
                    "viewport"  => ViewportGrid,
                    "output"    => PanelOutput,
                    _           => null,
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

    // ── メニューバー ──────────────────────────────────────────────

    private void OnMenuSaveScene(object sender, RoutedEventArgs e)
        => ShowSaveDialog();

    private void OnMenuExit(object sender, RoutedEventArgs e)
        => Close();

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

    private void ShowSaveDialog()
    {
        var dlg = new SaveFileDialog
        {
            Title            = "シーンを保存",
            Filter           = "Scene Files (*.scene)|*.scene|All Files (*.*)|*.*",
            DefaultExt       = ".scene",
            InitialDirectory = AssetsPath,
            OverwritePrompt  = true,
        };

        if (dlg.ShowDialog(this) == true)
        {
            _runtimeManager?.SendToRuntime($"SAVE_SCENE:{dlg.FileName}");
            EditorLog.Write($"ShowSaveDialog — SAVE_SCENE:{dlg.FileName}");
        }
    }

    private void OnSaveCompleted(bool ok, string errorMsg)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ok)
                EditorLog.Write("OnSaveCompleted — 保存成功");
            else
                MessageBox.Show($"シーンの保存に失敗しました:\n{errorMsg}", "SEED Editor",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        });
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
                AddViewportMenuItem(menu, "コピー",  () => _runtimeManager?.SendToRuntime("COPY"));
                AddViewportMenuItem(menu, "削除",    () =>
                {
                    var ids = PanelHierarchy.GetSelectedNonGroupIds();
                    if (ids.Count > 0)
                        _runtimeManager?.SendToRuntime($"DELETE_RECURSIVE:{string.Join(",", ids)}");
                });
                menu.Items.Add(new Separator());
            }
            AddViewportMenuItem(menu, "ペースト", () => _runtimeManager?.SendToRuntime("PASTE"));

            menu.PlacementTarget = ViewportDocumentContent;
            menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen          = true;
        });
    }

    private static void AddViewportMenuItem(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
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
        if (nCode >= 0 && IsEditorForeground())
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
                    Dispatcher.BeginInvoke(ShowSaveDialog);
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

            // ESC → 選択インスタンス削除（ダイアログあり）
            if (isDown && vk == 0x1B && !_ctrlHeld
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
    }
}
