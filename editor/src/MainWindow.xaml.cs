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

    // ── 編集時物理タイムライン ─────────────────────────────────────
    /// <summary>タイムラインが停止中かどうか。</summary>
    private bool   _physicsTimelinePaused = true;
    /// <summary>現在フレームインデックス。</summary>
    private int    _physicsCurrentFrame   = 0;
    /// <summary>記録済み総フレーム数。</summary>
    private int    _physicsTotalFrames    = 0;
    /// <summary>現在フレームのシミュレーション時間（秒）。</summary>
    private double _physicsTimeSec        = 0.0;

    // シークバー操作用
    /// <summary>スライダーのValueをプログラムから変更中かどうか（ValueChangedイベント抑制用）。</summary>
    /// <summary>プログラム側からスライダーValueを変更中（ValueChangedイベント抑制用）。</summary>
    private bool _suppressSliderEvents = false;
    /// <summary>スライダーにマウスボタンが押されているかどうか（クリック・ドラッグ共通）。</summary>
    private bool _sliderInteracting = false;
    /// <summary>
    /// シークバー操作開始時点で物理が再生中だったかどうか。
    /// 再生中にシークバーをクリックすると自動停止し、マウスリリース時に自動再開するために使用する。
    /// </summary>
    private bool _physicsWasPlayingBeforeSeek = false;
    /// <summary>スライダーテンプレート内の TrackFill への参照（進捗バー更新用）。</summary>
    private System.Windows.Controls.Border? _physicsTrackFill;
    // 後方互換用エイリアス（BindPhysicsStepButtons内部でのみ使用）
    private bool _sliderDragging { get => _sliderInteracting; set => _sliderInteracting = value; }

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

    public static string RuntimeExePath      = ResolveRuntimePath();
    public static string AssetsPath          = ResolveAssetsPath();
    public static string SettingsDir         = ResolveSettingsDir();
    public static string EditorResourcesPath = ResolveEditorResourcesPath();
    public static string EditorPluginsPath   = ResolveEditorPluginsPath();

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
    /// <summary>内蔵デバッガ（netcoredbg/DAP）のセッション。アタッチ中のみ非 null。</summary>
    private SEEDEditor.Debugger.ScriptDebugSession? _debugSession;
    /// <summary>AI アシスタントパネルのビルド済み UI 要素（LoadLayout でコンテンツを復元するために保持）</summary>
    private UIElement?             _aiPanelUi;
    /// <summary>「タブ」パネル（スクリプトで開いているファイル一覧）。LoadLayout で復元。</summary>
    private OpenDocumentsPanel?    _openDocsPanel;
    /// <summary>「エラー一覧」パネル。LoadLayout で復元。</summary>
    private ErrorListPanel?        _errorListPanel;

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
        _runtimeManager.EditPhysicsStateReceived      += OnEditPhysicsStateReceived;
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
        PanelProject.ScriptFileOpened   += OnScriptFileOpened;

        // スクリプトエディタ: アセットルートを渡して IntelliSense / F12 を有効化する
        PanelScriptEditor.SetAssetsPath(AssetsPath);
        // スクリプトエディタ: 書式・配色設定を読み込む
        PanelScriptEditor.InitSettings(SettingsDir);
        // 「タブ」「エラー一覧」パネルを生成してスクリプトエディタに接続する
        _openDocsPanel  = new OpenDocumentsPanel(PanelScriptEditor);
        _errorListPanel = new ErrorListPanel(PanelScriptEditor);
        // 左下アイコンのクリックでエラー一覧を前面表示する
        PanelScriptEditor.ShowErrorListRequested += () => ShowAnchorable("error_list");
        // スクリプトを表示したら「タブ」パネルを自動で前面表示する（フォーカスは奪わない）
        PanelScriptEditor.DocumentActivated += () => ShowAnchorable("open_documents", activate: false);
        // スクリプトエディタ: インスペクタの「スクリプトを編集」ボタンからも開ける
        PanelInspector.ScriptFileOpenRequested += OnScriptFileOpened;
        // 保存時: インスペクタの型キャッシュを無効化し、runtime にホットリロードを要求する
        PanelScriptEditor.ScriptSaved += path =>
        {
            PanelInspector.InvalidateScriptTypeCache(path);
            _runtimeManager?.SendToRuntime("RELOAD_SCRIPTS");
        };

        // 予期せぬ例外でクラッシュする直前に未保存スクリプトを退避する（復元用）。
        // UI スレッドの未処理例外は Dispatcher 経由、それ以外は AppDomain 経由で捕捉する。
        Application.Current.DispatcherUnhandledException += (_, _) => PanelScriptEditor.FlushRecovery();
        AppDomain.CurrentDomain.UnhandledException       += (_, _) => PanelScriptEditor.FlushRecovery();

        // 前回が異常終了（クラッシュ）していれば、未保存スクリプトの復元を提案する
        PanelScriptEditor.CheckCrashRecovery();

        // AI アシスタントパネルを初期化する。
        // LoadLayout() がこの後に呼ばれてコンテンツを上書きするため、
        // _aiPanelUi にビルド済み要素を保持して LoadLayout 内で参照する。
        var aiPanel = new AIAssistantPanel(_runtimeManager, AssetsPath);
        _aiPanelUi = aiPanel.Build();

        // 物理タイムラインのシークバーイベントをバインドする
        BindPhysicsStepButtons();

        _viewportHost = new ViewportHost();
        _viewportHost.ContainerCreated += OnContainerCreated;
        ViewportDocumentContent.Content = _viewportHost;

        InstallKeyboardHook();

        // ウィンドウドラッグ検出用 WndProc フック
        var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        hwndSource?.AddHook(WndProc);

        LoadLayout();

        // layout.xml が無いフレッシュ起動では LoadLayout 内の補填が走らないため、
        // XAML 既定レイアウトのパネルにもコンテンツを確実に割り当てる。
        EnsureScriptEditorDocument();
        EnsureScriptSidePanels();

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

        // シーン確認のあとに、スクリプトの未保存を独立して確認する。
        // （どちらも未保存なら シーン → スクリプト の順で確認が出る。）
        if (!PanelScriptEditor.PromptSaveOnExit())
        {
            e.Cancel = true;
            return;
        }

        // ここまで来たら正常終了。クラッシュ復元用の退避データを消す。
        PanelScriptEditor.ClearRecovery();

        SaveLayout();
        ReleasePlayClamp();
        UninstallKeyboardHook();
        ReleaseAllCamKeys();
        if (_viewportHost != null) RevokeDragDrop(_viewportHost.ContainerHwnd);
        _vpDragOverlay?.Close();
        _debugSession?.Dispose();
        _debugSession = null;
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
                    "hierarchy"     => PanelHierarchy,
                    "project"       => PanelProject,
                    "inspector"     => PanelInspector,
                    "viewport"      => ViewportGrid,
                    "output"         => PanelOutput,
                    "ai_assistant"   => _aiPanelUi,
                    "script_editor"  => PanelScriptEditor,
                    "open_documents" => _openDocsPanel,
                    "error_list"     => _errorListPanel,
                    _                => null,
                };
            };
            using var reader = new StreamReader(path);
            serializer.Deserialize(reader);

            // 旧バージョンの layout.xml には新パネルが含まれていないため、
            // デシリアライズ後に存在しなければ追加する。
            EnsureScriptEditorDocument();
            EnsureScriptSidePanels();

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

    // ── スクリプトエディタ ─────────────────────────────────────

    /// <summary>
    /// script_editor ドキュメントがレイアウト内に存在することを保証する。
    /// 旧 layout.xml のデシリアライズで消えた場合、Viewport のペインへ再追加する。
    /// </summary>
    private void EnsureScriptEditorDocument()
    {
        var existing = DockManager.Layout.Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == "script_editor");
        if (existing is not null)
        {
            // XAML 既定レイアウト（layout.xml 無し）ではコンテンツが未設定なので補填する
            existing.Content ??= PanelScriptEditor;
            return;
        }

        var pane = DockManager.Layout.Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault();
        if (pane is null) return;

        pane.Children.Add(new LayoutDocument
        {
            Title     = "Script",
            ContentId = "script_editor",
            CanClose  = false,
            Content   = PanelScriptEditor,
        });
    }

    /// <summary>
    /// 「タブ」「エラー一覧」アンカーパネルがレイアウトに存在することを保証する。
    /// 旧 layout.xml にこれらが無い場合、最初のアンカーペインへ追加する。
    /// </summary>
    private void EnsureScriptSidePanels()
    {
        EnsureAnchorable("open_documents", "タブ", _openDocsPanel);
        EnsureAnchorable("error_list",     "エラー一覧", _errorListPanel);
    }

    private void EnsureAnchorable(string contentId, string title, object? content)
    {
        if (content is null) return;
        var existing = DockManager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId);
        if (existing is not null)
        {
            // XAML 既定レイアウトではコンテンツが未設定なので補填する
            existing.Content ??= content;
            return;
        }

        var pane = DockManager.Layout.Descendents()
            .OfType<LayoutAnchorablePane>()
            .FirstOrDefault();
        if (pane is null) return;

        pane.Children.Add(new LayoutAnchorable
        {
            Title     = title,
            ContentId = contentId,
            CanClose  = false,
            // 「表示」メニューから非表示にできるよう CanHide を許可する
            CanHide   = true,
            Content   = content,
        });
    }

    /// <summary>.cs ファイルを内蔵スクリプトエディタで開き、Script タブをアクティブにする。</summary>
    private void OnScriptFileOpened(string path)
    {
        EnsureScriptEditorDocument();
        PanelScriptEditor.OpenFile(path);
        FocusScriptEditorDocument();
    }

    /// <summary>
    /// 「表示 &gt; スクリプト &gt; スクリプトエディタ」メニュー:
    /// スクリプトエディタ（LayoutDocument）の表示/非表示をトグルする。
    /// 表示中なら閉じ、非表示なら再追加して前面に出す。
    /// </summary>
    private void OnToggleScriptEditor(object sender, RoutedEventArgs e)
    {
        var doc = DockManager.Layout.Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == "script_editor");

        if (doc is not null)
        {
            // 表示中 → 閉じる
            doc.Close();
            if (sender is MenuItem mi) mi.IsChecked = false;
        }
        else
        {
            // 非表示 → 再追加して前面へ
            EnsureScriptEditorDocument();
            FocusScriptEditorDocument();
            if (sender is MenuItem mi) mi.IsChecked = true;
        }
    }

    /// <summary>
    /// 指定 ContentId のアンカーパネルを表示する。
    /// activate=true でフォーカスも移す（メニュー選択時）。
    /// activate=false は表示・選択のみでフォーカスは移さない（自動表示時）。
    /// </summary>
    private void ShowAnchorable(string contentId, bool activate = true)
    {
        EnsureScriptSidePanels();
        var anchorable = DockManager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId);
        if (anchorable is null) return;

        // 自動非表示・非表示状態なら表示する
        if (anchorable.IsHidden) anchorable.Show();
        if (anchorable.IsAutoHidden) anchorable.ToggleAutoHide();
        anchorable.IsSelected = true;
        if (activate) anchorable.IsActive = true;
    }

    /// <summary>
    /// 「デバッグ」メニュー: 実行中のランタイムに Visual Studio をアタッチする。
    ///
    /// スクリプトは埋め込み PDB 付きでコンパイルされるため、アタッチ後は
    /// .cs にブレークポイントを張ってステップ実行・変数確認ができる。
    /// 自動アタッチに失敗した場合は PID と手動手順を案内する。
    /// </summary>
    private void OnAttachVisualStudio(object sender, RoutedEventArgs e)
    {
        var pid = _runtimeManager?.CurrentProcessId;
        if (pid is null)
        {
            MessageBox.Show(
                "デバッグ対象のランタイムが起動していません。\n" +
                "Play でゲームを実行してから、もう一度お試しください。\n" +
                "（スクリプトは Play 中のランタイム内で実行されます）",
                "スクリプトデバッグ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // まず実行中の Visual Studio への自動アタッチを試みる
        bool attached = false;
        string message = "";
        try { attached = SEEDEditor.Runtime.VisualStudioAttach.TryAttach(pid.Value, out message); }
        catch (Exception ex) { message = ex.Message; }

        if (attached)
        {
            EditorLog.Write($"Visual Studio アタッチ成功: {message}");
            MessageBox.Show(
                $"{message}\n\nスクリプトの .cs にブレークポイントを設定してデバッグできます。",
                "スクリプトデバッグ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 自動アタッチ不可 → PID と手動手順を案内し、PID をクリップボードへコピーする
        try { Clipboard.SetText(pid.Value.ToString()); } catch { }
        MessageBox.Show(
            $"自動アタッチできませんでした（{message}）。\n\n" +
            "手動でアタッチしてください:\n" +
            "1. Visual Studio を開く\n" +
            "2. [デバッグ] → [プロセスにアタッチ] (Ctrl+Alt+P)\n" +
            $"3. PID = {pid.Value} の SEED.exe を選択（PID はクリップボードにコピー済み）\n" +
            "4. コードの種類で「マネージド (.NET)」が含まれることを確認してアタッチ\n\n" +
            "アタッチ後、スクリプトの .cs にブレークポイントを設定できます。",
            "スクリプトデバッグ — 手動アタッチ", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── 内蔵デバッガ（netcoredbg / DAP）────────────────────────

    /// <summary>
    /// 「デバッグ」メニュー: 実行中のランタイムへ内蔵デバッガ（netcoredbg）でアタッチし、
    /// 現在のブレークポイントを設定する。停止時はその行を開いて表示する。
    /// </summary>
    private async void OnDebugStartInEditor(object sender, RoutedEventArgs e)
    {
        var pid = _runtimeManager?.CurrentProcessId;
        if (pid is null)
        {
            MessageBox.Show(
                "デバッグ対象のランタイムが起動していません。\nPlay でゲームを実行してからお試しください。",
                "スクリプトデバッグ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_debugSession is { IsActive: true })
        {
            MessageBox.Show("既にデバッグセッションが動作中です。", "スクリプトデバッグ",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var session = new SEEDEditor.Debugger.ScriptDebugSession();
        _debugSession = session;

        // イベントは DAP 読み取りスレッドから来るため UI へマーシャリングする
        session.Log        += m => EditorLog.Write(m);
        session.Output     += t => EditorLog.Write($"[デバッグ出力] {t}");
        session.Continued  += () => EditorLog.Write("[デバッグ] 実行を継続しました。");
        session.Terminated += () => Dispatcher.BeginInvoke(() =>
        {
            EditorLog.Write("[デバッグ] セッションが終了しました。");
            _debugSession?.Dispose();
            _debugSession = null;
        });
        session.Stopped += info => Dispatcher.BeginInvoke(async () =>
        {
            EditorLog.Write($"[デバッグ] 停止しました（{info.Reason}）。");
            try
            {
                var frames = await session.GetStackTraceAsync();
                foreach (var f in frames)
                {
                    if (string.IsNullOrEmpty(f.SourcePath)) continue;
                    PanelScriptEditor.GoToLine(f.SourcePath!, f.Line);
                    EditorLog.Write($"[デバッグ] 現在位置 {System.IO.Path.GetFileName(f.SourcePath)}:{f.Line}  {f.Name}");
                    break;
                }
            }
            catch (Exception ex) { EditorLog.Write($"[デバッグ] スタック取得失敗: {ex.Message}"); }
        });

        try
        {
            var breakpoints = PanelScriptEditor.GetBreakpointsForDebug();
            await session.StartAsync(pid.Value, breakpoints);
            MessageBox.Show(
                $"内蔵デバッガでアタッチしました（PID={pid.Value}）。\n" +
                "ブレークポイントで停止すると、その行を表示します。\n" +
                "［デバッグ］メニューから継続・ステップ・デタッチができます。",
                "スクリプトデバッグ", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            EditorLog.Write($"[デバッグ] アタッチ失敗: {ex.Message}");
            _debugSession?.Dispose();
            _debugSession = null;
            MessageBox.Show(
                $"内蔵デバッガでアタッチできませんでした。\n\n{ex.Message}",
                "スクリプトデバッグ", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>「デバッグ」メニュー: 継続（F5 相当）。</summary>
    private async void OnDebugContinue(object sender, RoutedEventArgs e)
    {
        if (_debugSession is { IsActive: true } s) await SafeDebugAsync(s.ContinueAsync());
    }

    /// <summary>「デバッグ」メニュー: ステップオーバー（F10 相当）。</summary>
    private async void OnDebugStepOver(object sender, RoutedEventArgs e)
    {
        if (_debugSession is { IsActive: true } s) await SafeDebugAsync(s.StepOverAsync());
    }

    /// <summary>「デバッグ」メニュー: デタッチ（ランタイムは終了させない）。</summary>
    private async void OnDebugDetach(object sender, RoutedEventArgs e)
    {
        var s = _debugSession;
        _debugSession = null;
        if (s is not null) { await SafeDebugAsync(s.DisconnectAsync()); EditorLog.Write("[デバッグ] デタッチしました。"); }
    }

    /// <summary>デバッグ操作を安全に await する（失敗はログのみ）。</summary>
    private static async Task SafeDebugAsync(Task op)
    {
        try { await op; }
        catch (Exception ex) { EditorLog.Write($"[デバッグ] 操作に失敗: {ex.Message}"); }
    }

    /// <summary>Script ドキュメントを選択・アクティブ化して前面に出す。</summary>
    private void FocusScriptEditorDocument()
    {
        var doc = DockManager.Layout.Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(d => d.ContentId == "script_editor");
        if (doc is not null)
        {
            doc.IsSelected = true;
            doc.IsActive   = true;
        }
    }
}
