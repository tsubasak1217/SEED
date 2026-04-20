using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Reflection;
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

    private bool _clampInPlay = false;
    private bool _isDragging  = false;
    private bool _ctrlHeld    = false;

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

    private ViewportHost?   _viewportHost;
    private RuntimeManager? _runtimeManager;

    public MainWindow()
    {
        InitializeComponent();
        ApplyDockTheme();
    }

    // ── ウィンドウ初期化 ─────────────────────────────────────────

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        EditorLog.Write($"OnWindowLoaded — RuntimeExePath={RuntimeExePath}");

        _runtimeManager = new RuntimeManager(RuntimeExePath);
        _runtimeManager.StateChanged         += OnStateChanged;
        _runtimeManager.RuntimeHwndAvailable += OnRuntimeHwndAvailable;
        _runtimeManager.RuntimeMoveStart     += () => { _isDragging = true;  Dispatcher.BeginInvoke(ReleasePlayClamp); };
        _runtimeManager.RuntimeMoveEnd       += () => { _isDragging = false; Dispatcher.BeginInvoke(() => { if (_clampInPlay && _runtimeManager?.State == EditorState.Play) ApplyPlayClamp(); }); };

        _runtimeManager.SaveCompleted += OnSaveCompleted;

        PanelHierarchy.SetRuntime(_runtimeManager);
        PanelProject.SetAssetsPath(AssetsPath);

        _viewportHost = new ViewportHost();
        _viewportHost.ContainerCreated += OnContainerCreated;
        ViewportDocumentContent.Content = _viewportHost;

        InstallKeyboardHook();

        // ウィンドウドラッグ検出用 WndProc フック
        var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        hwndSource?.AddHook(WndProc);

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

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_runtimeManager is null) return;
        try
        {
            await _runtimeManager.PlayAsync();
        }
        catch (Exception ex)
        {
            EditorLog.Write($"OnPlay EXCEPTION: {ex}");
            MessageBox.Show($"Play 起動失敗:\n{ex.Message}", "SEED Editor",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        if (_runtimeManager is null) return;

        if (_runtimeManager.State == EditorState.Play)
            _runtimeManager.Pause();
        else if (_runtimeManager.State == EditorState.Pause)
            _runtimeManager.Resume();
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (_runtimeManager is null) return;
        _runtimeManager.Stop();
        await Task.CompletedTask;
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
        ReleasePlayClamp();
        UninstallKeyboardHook();
        ReleaseAllCamKeys();
        _runtimeManager?.Dispose();
        ViewportDocumentContent.Content = null;
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    // ── シーン保存 ────────────────────────────────────────────────

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
        // ステートチェックは UI スレッドで行う
        Dispatcher.BeginInvoke(() =>
        {
            if (_clampInPlay && !_isDragging && _runtimeManager?.State == EditorState.Play)
                ApplyPlayClamp();
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
                BtnPlay.IsEnabled  = true;
                BtnPause.IsEnabled = false;
                BtnStop.IsEnabled  = false;
                BtnPause.Content   = "⏸  Pause";
                LblState.Text      = "● EDIT";
                LblState.Foreground = System.Windows.Media.Brushes.LightGreen;
                ViewportDocumentContent.Visibility = Visibility.Visible;
                break;

            case EditorState.Play:
                BtnPlay.IsEnabled  = false;
                BtnPause.IsEnabled = true;
                BtnStop.IsEnabled  = true;
                BtnPause.Content   = "⏸  Pause";
                LblState.Text      = "▶ PLAY";
                LblState.Foreground = System.Windows.Media.Brushes.LightSkyBlue;
                ViewportDocumentContent.Visibility = Visibility.Hidden;
                break;

            case EditorState.Pause:
                BtnPlay.IsEnabled  = false;
                BtnPause.IsEnabled = true;
                BtnStop.IsEnabled  = true;
                BtnPause.Content   = "▶  Resume";
                LblState.Text      = "⏸ PAUSE";
                LblState.Foreground = System.Windows.Media.Brushes.Orange;
                ViewportDocumentContent.Visibility = Visibility.Visible;
                break;

            case EditorState.Building:
                BtnPlay.IsEnabled  = false;
                BtnPause.IsEnabled = false;
                BtnStop.IsEnabled  = false;
                LblState.Text      = "⚙ BUILDING...";
                LblState.Foreground = System.Windows.Media.Brushes.Yellow;
                break;

            case EditorState.Idle:
                BtnPlay.IsEnabled  = false;
                BtnPause.IsEnabled = false;
                BtnStop.IsEnabled  = false;
                LblState.Text      = "○ IDLE";
                LblState.Foreground = System.Windows.Media.Brushes.Gray;
                break;
        }
    }
}
