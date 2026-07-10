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
        /// <summary>ドロップを転送できた（DROP_ACTOR を送信した）場合 true を返す。</summary>
        bool TryDropActorsAtCursor(string[] paths);
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
    /// <summary>
    /// アクター編集タブ情報。IsActor2D は actor_kind="Actor2D" のとき true。
    /// IsSceneCanvas はシーン内キャンバスの隔離編集タブ（ファイル非対応。Path は
    /// "canvas://{世界線}" の合成キー）。RootIs2D はキャンバス所有アクターの元の種別で、
    /// タブを閉じたとき戻るシーンタブ（ビューポート / ワールド）の判定に使う。
    /// </summary>
    private record ActorTab(
        string Path, string Name, uint WorldLine, bool IsActor2D = false,
        bool IsSceneCanvas = false, bool RootIs2D = false);
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
    /// <summary>起動時の前回シーン復元を一度だけ行うためのフラグ。</summary>
    private bool    _initialSceneLoaded       = false;
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
        if (File.Exists(relPath)) return relPath;

        // debug / release どちらの成果物もまだ存在しない（クリーンな初回起動など）。
        // このあと RuntimeManager が RuntimeSourceWatcher の指示で `cargo build`
        // （＝ debug 出力）を実行して SEED.exe を生成する。
        // ここで release パスを返すと、debug がビルドされても起動時に
        // target/release/SEED.exe を探して「指定されたファイルが見つかりません」
        // (Win32Exception 2) で失敗するため、ビルド後に生成される debug パスを
        // デフォルトのフォールバックとして返す。
        return devPath;
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
    /// <summary>
    /// スクリプトデバッグの自動アタッチが有効か。true の間は Play 開始のたびに
    /// 新しい Play プロセスへ自動的にデバッガをアタッチする。
    /// スクリプトは Play 中のみ実行され、かつ Play は毎回別プロセスとして
    /// 起動するため、Play プロセスへ自動追従する必要がある。
    /// 既定で有効（メニュー操作なしで常にデバッグできるようにする）。
    /// </summary>
    private bool _debugAutoAttach = true;
    /// <summary>
    /// ブレークポイント停止中に、凍結した Play ウィンドウの最終フレームを
    /// Viewport 上へ静止表示する DWM サムネイルプレビュー。
    /// </summary>
    private readonly SEEDEditor.Debugger.FrozenFramePreview _frozenPreview = new();
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
        // キャンバス編集タブ開始応答（EDIT_CANVAS_BEGIN → CANVAS_EDIT_WL）
        _runtimeManager.CanvasEditStarted             += OnCanvasEditStarted;
        _runtimeManager.FpsReceived                   += OnFpsReceived;

        PanelHierarchy.SetRuntime(_runtimeManager);
        PanelHierarchy.SetAssetsPath(AssetsPath);
        PanelHierarchy.ActorDfsSelected += id => PanelInspector.SelectActor(id);
        // 選択アクターのビューポート所属（is_vp）に応じてシーンタブ（ワールド/ビューポート）を自動切替する
        PanelHierarchy.SelectionKindResolved += OnHierarchySelectionKindResolved;
        PanelInspector.SetRuntime(_runtimeManager);
        PanelInspector.SetAssetsPath(AssetsPath);
        PanelInspector.TransformCommitted += MarkDirty;
        PanelProject.SetAssetsPath(AssetsPath);
        // Hierarchy からドラッグしたアクタをアクタファイル化（EXPORT_ACTOR 送信）するため Runtime を注入
        PanelProject.SetRuntime(_runtimeManager);
        PanelProject.SceneFileOpened    += OnSceneFileOpened;
        PanelProject.ActorFileOpened    += OnActorFileOpened;
        PanelProject.InputMapFileOpened += OnInputMapFileOpened;
        PanelProject.ScriptFileOpened   += OnScriptFileOpened;

        // スクリプトエディタ: アセットルートを渡して IntelliSense / F12 を有効化する
        PanelScriptEditor.SetAssetsPath(AssetsPath);
        // スクリプトエディタ: 書式・配色設定を読み込む
        PanelScriptEditor.InitSettings(SettingsDir);
        // エディタ全体の環境設定（タッチパッドスクロール係数など）を読み込む
        EditorPreferences.Init(SettingsDir);
        // 「タブ」「エラー一覧」パネルを生成してスクリプトエディタに接続する
        _openDocsPanel  = new OpenDocumentsPanel(PanelScriptEditor);
        _errorListPanel = new ErrorListPanel(PanelScriptEditor);
        // 左下アイコンのクリックでエラー一覧を前面表示する
        PanelScriptEditor.ShowErrorListRequested += () => ShowAnchorable("error_list");
        // スクリプトを表示したら「タブ」パネルを自動で前面表示する（フォーカスは奪わない）
        PanelScriptEditor.DocumentActivated += () => ShowAnchorable("open_documents", activate: false);
        // スクリプトエディタ: インスペクタの「スクリプトを編集」ボタンからも開ける
        PanelInspector.ScriptFileOpenRequested += OnScriptFileOpened;
        // インスペクタの「キャンバスを編集」ボタンでキャンバス編集タブを開く
        PanelInspector.CanvasEditRequested += OnCanvasEditRequested;
        // 保存時: インスペクタの型キャッシュを無効化し、runtime にホットリロードを要求する
        PanelScriptEditor.ScriptSaved += path =>
        {
            PanelInspector.InvalidateScriptTypeCache(path);
            _runtimeManager?.SendToRuntime("RELOAD_SCRIPTS");
        };
        // 実行中にブレークポイントを設置・解除したら、その場でデバッガへ反映する
        // （デバッグセッションが有効なときのみ）。
        PanelScriptEditor.BreakpointsChanged += OnBreakpointsChanged;

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

        // 固定シーンタブ（ワールド/ビューポート）を初期表示する
        RebuildActorTabBar();

        _viewportHost = new ViewportHost();
        _viewportHost.ContainerCreated += OnContainerCreated;
        ViewportDocumentContent.Content = _viewportHost;

        // 停止フレームプレビュー（DWM サムネイル）を Viewport パネルのサイズ・位置変更へ
        // 追従させる。パネル自体（ドッキングのスプリッター操作）は
        // ViewportDocumentContent.SizeChanged、ウィンドウ全体はウィンドウの
        // SizeChanged/LocationChanged で捕捉する。
        ViewportDocumentContent.SizeChanged += (_, _) => UpdateFrozenFramePreviewRect();
        _viewportHost.SizeChanged           += (_, _) => UpdateFrozenFramePreviewRect();
        SizeChanged                         += (_, _) => UpdateFrozenFramePreviewRect();
        LocationChanged                     += (_, _) => UpdateFrozenFramePreviewRect();

        InstallKeyboardHook();

        // デバッグ操作のショートカット（F5/F10/F11）。停止中のみ機能する。
        // PreviewKeyDown（トンネル）でエディタより先に捕捉する。
        PreviewKeyDown += OnGlobalDebugKeys;

        // スクリプトのメタデータ参照構築を起動時にバックグラウンドで先取りし、
        // 最初のスクリプト付きアクター選択時のインスペクタ表示を速くする。
        Task.Run(() => { try { SEEDEditor.Scripting.ScriptCompiler.WarmUp(); } catch { } });

        // ウィンドウドラッグ検出用 WndProc フック
        var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        hwndSource?.AddHook(WndProc);

        // タッチパッドスクロール挙動（縦の減衰・横スワイプ対応）を全ウィンドウへ導入する
        //（フローティングパネルや設定ウィンドウにも適用される）
        EditorScrollBehavior.Install();

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

    /// <summary>
    /// Play 開始前にスクリプトの全体コンパイルを検証する。
    ///
    /// エラーがある場合:
    ///   1. エラー一覧パネルへ診断を反映してアクティブ化（前面表示）
    ///   2. 「実行できない」旨をダイアログで表示
    ///   3. false を返して Play をブロックする
    /// エラーがなければ診断をクリアして true を返す。
    /// </summary>
    private bool CheckScriptsBeforePlay()
    {
        var diags = PanelScriptEditor.RunProjectCompileCheck(AssetsPath);
        if (diags.Count == 0) return true;

        EditorLog.Write($"[スクリプトエラー] コンパイルエラー {diags.Count} 件のため実行をブロックしました。");

        // エラー一覧ウィンドウを自動でアクティブにしてからダイアログを表示する
        ShowAnchorable("error_list");
        MessageBox.Show(
            $"スクリプトにコンパイルエラーが {diags.Count} 件あるため実行できません。\n\n" +
            "エラー一覧ウィンドウの内容を確認し、修正してから再度実行してください。\n" +
            "（行をダブルクリックすると該当箇所へジャンプします）",
            "SEED Editor — 実行できません",
            MessageBoxButton.OK, MessageBoxImage.Error);
        return false;
    }

    private async void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_runtimeManager is null) return;
        var state = _runtimeManager.State;
        if (state == EditorState.Edit)
        {
            // スクリプトの全体コンパイルを検証し、エラーがあれば実行をブロックする。
            // ランタイムは全 .cs を 1 アセンブリに一括コンパイルするため、
            // エラーが 1 つでもあると全スクリプトが実行されない（サイレント故障）。
            // 事前に検出してエラー一覧ウィンドウ + ダイアログで通知する。
            if (!CheckScriptsBeforePlay()) return;

            // Play へ切り替える前に、押下中のカメラキーの UP を Edit ランタイムへ送る。
            // 切替後は送信先が Play パイプになるため、ここで送らないと
            // Edit ランタイム側でキーがスタックする（RMB だけでカメラが移動する原因）。
            ReleaseAllCamKeys();

            // キャンバス編集タブが開いていたら閉じてアクターをシーンへ戻す。
            // 開いたまま Play すると一時シーン保存にアクターが正しく含まれない。
            CloseActiveSceneCanvasTab();
            EndInactiveSceneCanvasTabs();

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
        {
            // ブレークポイントで停止中は、ランタイムのメインスレッドが凍結しているため
            // Pause（ウィンドウ埋め込み）を行うとデッドロックする。停止中に Play/Pause を
            // 押した場合は「継続（実行再開）」として扱う。
            if (_debugSession is { IsStopped: true } s)
                await SafeDebugAsync(s.ContinueAsync());
            else
                _runtimeManager.Pause();
        }
        else if (state == EditorState.Pause)
            _runtimeManager.Resume();
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (_runtimeManager is null) return;

        // デバッグセッション中に Stop する場合は、先にデバッガをデタッチして
        // ランタイムの凍結を解除する。凍結したままだと Kill 前後のウィンドウ操作
        // （復元時の ShowWindow/リサイズ等）や後片付けがブロックしうるため。
        if (_debugSession is { IsActive: true } s)
        {
            _frozenPreview.Hide();
            _runtimeManager.DebuggerSuspended = false;
            _debugSession = null;
            await SafeDebugAsync(s.DisconnectAsync());
        }

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
                    // キャンバス編集タブが開いていたら閉じてアクターをシーンへ戻してから保存する
                    if (_runtimeManager?.State == EditorState.Edit)
                    {
                        CloseActiveSceneCanvasTab();
                        EndInactiveSceneCanvasTabs();
                    }
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
        _frozenPreview.Dispose();
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

            // 旧 layout.xml には旧タイトル（"Viewport"）が保存されているため、
            // 表示タイトルを現行の「シーン」へ上書きする（ContentId は互換のため不変）。
            var viewportDoc = DockManager.Layout.Descendents()
                .OfType<LayoutDocument>()
                .FirstOrDefault(d => d.ContentId == "viewport");
            if (viewportDoc != null) viewportDoc.Title = "シーン";

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
    //
    // スクリプトデバッグはメニュー操作不要で常に自動。スクリプトは Play 中のみ実行され、
    // かつ Play は毎回 Edit とは別プロセスとして起動するため、Play へ遷移するたびに
    // OnStateChanged(Play) → TryAutoAttachDebuggerOnPlay で Play プロセスへ自動アタッチする。
    // ブレークポイントを置いておけば（実行前でも実行中でも）その行で停止する。

    /// <summary>
    /// 指定 PID（実行中の Play ランタイム）へ内蔵デバッガ（netcoredbg）をアタッチし、
    /// 現在のブレークポイントを設定する。停止時はその行を開いて表示する。
    /// </summary>
    /// <param name="pid">アタッチ対象プロセス ID（Play ランタイム）。</param>
    private async Task AttachDebuggerAsync(int pid)
    {
        if (_debugSession is { IsActive: true }) return;

        var session = new SEEDEditor.Debugger.ScriptDebugSession();
        _debugSession = session;

        // イベントは DAP 読み取りスレッドから来るため UI へマーシャリングする
        session.Log        += m => EditorLog.Write(m);
        session.Output     += t => EditorLog.Write($"[デバッグ出力] {t}");
        session.Continued  += () => Dispatcher.BeginInvoke(() =>
        {
            // ステップ操作に伴う一時的な継続（直後に再停止する）では、画面・状態を
            // 一切いじらない。プレビューを消したり停止行ハイライトを消したりすると、
            // 次の停止までの一瞬でちらつくため。次の stopped でまとめて更新される。
            if (_debugStepping) return;

            // 完全な継続: ランタイムのメインスレッドが再開する。凍結解除に合わせて
            // 停止フレームのプレビューを消し、ウィンドウ操作の抑止も解除し、
            // 停止行ハイライトを消して、ゲームウィンドウを最前面へ戻す。
            EditorLog.Write("[デバッグ] 実行を継続しました。");
            if (_runtimeManager is not null) _runtimeManager.DebuggerSuspended = false;
            _frozenPreview.Hide();
            PanelScriptEditor.ClearDebugStoppedLine();
            PanelScriptEditor.ClearDebugEvaluator();
            DebugStepBar.Visibility = Visibility.Collapsed;
            BringGameWindowToFront();
        });
        session.Terminated += () => Dispatcher.BeginInvoke(() =>
        {
            // Play 終了（プロセス消滅）でここに来る。_debugAutoAttach は保持したままにし、
            // 次の Play 開始時に再アタッチできるようにする（デタッチ操作でのみ無効化）。
            EditorLog.Write("[デバッグ] セッションが終了しました。");
            _debugStepping = false;
            if (_runtimeManager is not null) _runtimeManager.DebuggerSuspended = false;
            _frozenPreview.Hide();
            PanelScriptEditor.ClearDebugStoppedLine();
            PanelScriptEditor.ClearDebugEvaluator();
            DebugStepBar.Visibility = Visibility.Collapsed;
            _debugSession?.Dispose();
            _debugSession = null;
        });
        session.Stopped += info => Dispatcher.BeginInvoke(async () =>
        {
            EditorLog.Write($"[デバッグ] 停止しました（{info.Reason}）。");
            _debugStepping = false;
            // 停止中はランタイムのメインスレッドが凍結する。ウィンドウ埋め込み等の
            // 危険な操作を抑止し、凍結フレームを Viewport 上へ静止表示する。
            if (_runtimeManager is not null) _runtimeManager.DebuggerSuspended = true;
            ShowFrozenFramePreview();
            // デバッグ操作ツールバー（継続・ステップ）を表示する。
            DebugStepBar.Visibility = Visibility.Visible;
            // 停止したらエディタを前面へ（継続時に前面化したゲームより上に出す）。
            // 自ウィンドウの操作なのでデッドロックしない。
            Activate();
            try
            {
                var frames = await session.GetStackTraceAsync();
                foreach (var f in frames)
                {
                    if (string.IsNullOrEmpty(f.SourcePath)) continue;
                    // 停止行へジャンプ＋赤背景ハイライト＋ブレークポイントをヒットアイコンに切替
                    PanelScriptEditor.SetDebugStoppedLine(f.SourcePath!, f.Line);
                    EditorLog.Write($"[デバッグ] 現在位置 {System.IO.Path.GetFileName(f.SourcePath)}:{f.Line}  {f.Name}");
                    break;
                }
                // 変数ホバー表示を有効化（GetStackTraceAsync で最上位フレーム ID が確定した後）。
                PanelScriptEditor.SetDebugEvaluator(
                    expr   => session.EvaluateAsync(expr),
                    varRef => session.GetVariablesAsync(varRef));
            }
            catch (Exception ex) { EditorLog.Write($"[デバッグ] スタック取得失敗: {ex.Message}"); }
        });

        try
        {
            var breakpoints = PanelScriptEditor.GetBreakpointsForDebug();
            await session.StartAsync(pid, breakpoints);
            // デバッガ稼働中は Play プロセスのクロックにブレークポイント停止ガードを効かせる。
            // これにより、ブレークポイントで長時間停止した復帰フレームで delta が丸められ、
            // ConstantUpdate が数百回追いつき実行されてブレークポイントに延々と
            // 再ヒットする（ステップイン/オーバーが終わらない）現象を防ぐ。
            _runtimeManager?.SendToRuntime("DBG_GUARD:1");
        }
        catch (Exception ex)
        {
            EditorLog.Write($"[デバッグ] アタッチ失敗: {ex.Message}");
            _debugSession?.Dispose();
            _debugSession = null;
        }
    }

    /// <summary>
    /// Play 開始時に呼ばれ、スクリプトデバッグが有効なら Play プロセスへ自動アタッチする。
    /// OnStateChanged（状態遷移）から呼ばれる。
    /// </summary>
    private void TryAutoAttachDebuggerOnPlay()
    {
        if (!_debugAutoAttach) return;
        if (_debugSession is { IsActive: true }) return;   // Pause→Play 等では再アタッチ不要
        if (_runtimeManager?.State != EditorState.Play)    return;
        if (_runtimeManager.CurrentProcessId is not int pid) return;

        EditorLog.Write($"[デバッグ] Play 開始を検知 — Play プロセス PID={pid} へ自動アタッチします。");
        _ = AttachDebuggerAsync(pid);
    }

    /// <summary>
    /// エディタでブレークポイントを設置・解除したときに呼ばれる。
    /// デバッグセッション中なら、その場で netcoredbg へ反映して
    /// 実行中に置いたブレークポイントでも停止できるようにする。
    /// （netcoredbg の setBreakpoints はソース単位で全置換のため、
    ///   そのファイルの現在の全行を渡す）。
    /// </summary>
    private async void OnBreakpointsChanged(string filePath, IReadOnlyList<int> lines)
    {
        if (_debugSession is not { IsActive: true } s) return;
        try
        {
            await s.SetBreakpointsAsync(filePath, lines);
        }
        catch (Exception ex)
        {
            EditorLog.Write($"[デバッグ] 実行中ブレークポイント反映に失敗: {ex.Message}");
        }
    }

    /// <summary>
    /// ブレークポイント停止中に、凍結した Play ウィンドウの最終フレームを
    /// Viewport 領域へ DWM サムネイルで静止表示する。
    /// （停止中はエンジンのメインスレッドが凍結し描画ループが止まるため、
    ///   自由に見回せるデバッグカメラ表示は不可。最後のフレームの静止表示のみ。）
    /// </summary>
    private void ShowFrozenFramePreview()
    {
        try
        {
            // 既に表示中なら貼り直さない。DWM サムネイルはソース（ゲームウィンドウ）の
            // 最新内容を自動で反映するため、ステップ等で停止するたびに Hide→再登録すると
            // 一瞬空になって画面がちらつく。位置だけ更新して貼り直しは避ける。
            if (_frozenPreview.IsShowing)
            {
                UpdateFrozenFramePreviewRect();
                return;
            }

            var mainHwnd = new WindowInteropHelper(this).Handle;
            var srcHwnd  = _runtimeManager?.RuntimeHwnd ?? 0;
            if (mainHwnd == 0 || srcHwnd == 0) return;
            if (!TryGetViewportRectInWindow(mainHwnd, out int l, out int t, out int r, out int b)) return;

            _frozenPreview.Show(mainHwnd, srcHwnd, l, t, r, b);
        }
        catch (Exception ex)
        {
            EditorLog.Write($"[デバッグ] 停止フレームのプレビュー表示に失敗: {ex.Message}");
        }
    }

    /// <summary>
    /// 停止フレームプレビュー表示中に、Viewport パネルのサイズ・位置変更へ追従して
    /// DWM サムネイルの表示矩形を更新する。ウィンドウ移動・リサイズ、ドッキングパネルの
    /// スプリッター操作などから呼ばれる。
    /// </summary>
    private void UpdateFrozenFramePreviewRect()
    {
        if (!_frozenPreview.IsShowing) return;
        var mainHwnd = new WindowInteropHelper(this).Handle;
        if (mainHwnd == 0) return;
        if (TryGetViewportRectInWindow(mainHwnd, out int l, out int t, out int r, out int b))
            _frozenPreview.UpdateRect(l, t, r, b);
    }

    /// <summary>
    /// Viewport パネルの矩形を、メインウィンドウのクライアント座標（物理ピクセル）へ
    /// 変換して返す。取得できなければ false。
    ///
    /// ContainerHwnd（HwndHost の子ウィンドウ）は Play 中に Viewport ドキュメントが
    /// Hidden になると位置・サイズが更新されず陳腐化するため使わない。代わりに
    /// WPF 要素（ViewportDocumentContent）の現在のレイアウトから PointToScreen で
    /// 実座標を求める。Hidden でもレイアウトは有効なので、パネルのサイズ変更に追従できる。
    /// </summary>
    private bool TryGetViewportRectInWindow(nint mainHwnd, out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;
        var el = ViewportDocumentContent;
        if (el is null || el.ActualWidth <= 0 || el.ActualHeight <= 0) return false;

        try
        {
            // 要素の左上・右下をスクリーン（物理ピクセル）座標へ変換する。
            var p0 = el.PointToScreen(new System.Windows.Point(0, 0));
            var p1 = el.PointToScreen(new System.Windows.Point(el.ActualWidth, el.ActualHeight));

            var origin = new NativeInterop.POINT { X = 0, Y = 0 };
            NativeInterop.ClientToScreen(mainHwnd, ref origin);

            left   = (int)Math.Round(Math.Min(p0.X, p1.X)) - origin.X;
            top    = (int)Math.Round(Math.Min(p0.Y, p1.Y)) - origin.Y;
            right  = (int)Math.Round(Math.Max(p0.X, p1.X)) - origin.X;
            bottom = (int)Math.Round(Math.Max(p0.Y, p1.Y)) - origin.Y;
            return right > left && bottom > top;
        }
        catch
        {
            // PointToScreen は要素が PresentationSource に未接続だと失敗する。
            return false;
        }
    }

    /// <summary>
    /// ゲーム（Play ランタイム）ウィンドウを最前面へ表示する。
    /// ブレークポイント停止でエディタが前面に出た後、継続でゲームへ戻すために使う。
    ///
    /// エディタがフォアグラウンドプロセスなので、SetForegroundWindow で他ウィンドウ
    /// （ゲーム）へ前面を渡せる。ただし BringWindowToTop / SetForegroundWindow は
    /// 対象スレッドへ同期メッセージを送るため、呼び出し中にゲームがブレークポイントで
    /// 凍結すると応答待ちでブロックしうる（ブレークポイントを残したまま継続 → 数フレーム後に
    /// 再停止するケース）。
    /// これを UI スレッドで行うとエディタごとデッドロックするため、ワーカースレッドで実行する。
    /// 万一ブロックしても待つのはそのワーカーだけで、UI は固まらない。
    /// </summary>
    private void BringGameWindowToFront()
    {
        var hwnd = _runtimeManager?.RuntimeHwnd ?? 0;
        if (hwnd == 0) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                NativeInterop.BringWindowToTop(hwnd);
                NativeInterop.SetForegroundWindow(hwnd);
            }
            catch { /* 前面化失敗は致命ではないので無視 */ }
        });
    }

    /// <summary>
    /// ステップ操作中かどうか（step over/in/out）。
    /// ステップ時は継続→停止が瞬時に繰り返されるため、その間の「継続」で
    /// ゲームを最前面化したりツールバーを隠したりしないための一時フラグ。
    /// </summary>
    private bool _debugStepping;

    /// <summary>「デバッグ」: 継続（F5）。</summary>
    private async void OnDebugContinue(object sender, RoutedEventArgs e)
    {
        if (_debugSession is { IsStopped: true } s) await SafeDebugAsync(s.ContinueAsync());
    }

    /// <summary>「デバッグ」: ステップオーバー（F10）。現在行を実行し次の行で停止（関数へは入らない）。</summary>
    private async void OnDebugStepOver(object sender, RoutedEventArgs e)
    {
        if (_debugSession is { IsStopped: true } s) { _debugStepping = true; await SafeDebugAsync(s.StepOverAsync()); }
    }

    /// <summary>「デバッグ」: ステップイン（F11）。呼び出し先の関数内へ入って停止。</summary>
    private async void OnDebugStepIn(object sender, RoutedEventArgs e)
    {
        if (_debugSession is { IsStopped: true } s) { _debugStepping = true; await SafeDebugAsync(s.StepInAsync()); }
    }

    /// <summary>「デバッグ」: ステップアウト（Shift+F11）。現在の関数を抜けて呼び出し元で停止。</summary>
    private async void OnDebugStepOut(object sender, RoutedEventArgs e)
    {
        if (_debugSession is { IsStopped: true } s) { _debugStepping = true; await SafeDebugAsync(s.StepOutAsync()); }
    }

    /// <summary>F5/F10/F11 のデバッグ操作ショートカット（停止中のみ）。ウィンドウの PreviewKeyDown から呼ぶ。</summary>
    private void OnGlobalDebugKeys(object sender, KeyEventArgs e)
    {
        if (_debugSession is not { IsStopped: true } s) return;
        // F10 は Windows/WPF ではメニュー活性化用の「システムキー」として届くため、
        // e.Key は Key.System になり実キーは e.SystemKey に入る。両者を吸収する。
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.F5:
                _ = SafeDebugAsync(s.ContinueAsync());
                e.Handled = true;
                break;
            case Key.F10:
                _debugStepping = true;
                _ = SafeDebugAsync(s.StepOverAsync());
                e.Handled = true;
                break;
            case Key.F11:
                _debugStepping = true;
                _ = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                    ? SafeDebugAsync(s.StepOutAsync())
                    : SafeDebugAsync(s.StepInAsync());
                e.Handled = true;
                break;
        }
    }

    /// <summary>「デバッグ」メニュー: デタッチ（ランタイムは終了させない）。</summary>
    private async void OnDebugDetach(object sender, RoutedEventArgs e)
    {
        // 現在のセッションのみ切断する。自動アタッチ自体は有効のままにし、
        // 次の Play 開始時には再びアタッチする（デバッグは常時自動が既定のため）。
        var s = _debugSession;
        _debugSession = null;
        // ブレークポイント停止ガードを解除し、通常のフレームタイミングへ戻す。
        _runtimeManager?.SendToRuntime("DBG_GUARD:0");
        if (s is not null) { await SafeDebugAsync(s.DisconnectAsync()); EditorLog.Write("[デバッグ] デタッチしました（次の Play で再アタッチします）。"); }
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
