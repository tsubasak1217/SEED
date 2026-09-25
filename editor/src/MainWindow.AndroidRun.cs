// ============================================================
//  MainWindow.AndroidRun.cs — 実行先セレクタ（PC / Android（自動）/ Android の実機・エミュレータ）と Android での実行の結線
//                             （段階C-2・C-3）
//
//  【役割】WPF の結線だけ。判断はすべて AndroidRun/ の WPF 非依存のクラス（単体テスト editor/tests/AndroidRunUiTests）:
//    - 実行先コンボ（CmbRunTarget）の一覧と選択 … RunTargetCatalogBuilder（開くたびに adb で端末を探し直す）
//    - 前回の選択（プロジェクトごと）           … RunTargetSelectionStore（cache/android/run_state.json）
//    - 実行・停止ボタン・状態表示・進捗          … PlayBarPolicy（PC の表示もここから当てる。PC との排他もここ）
//    - Android の実行（端末の用意〈要ればエミュレータを起動〉→ ビルド → インストール → 起動 → logcat → 停止・
//      アプリの終了の検知・実行バーからの一時停止と再開〈段階D-1。端末のアプリと adb forward ＋ TCP の IPC でつなぐ〉）
//      … AndroidRunController（中核への指定は AndroidEditorRunRequests）
//    - 実行中の差し替え（端末のアプリが動いている間、保存したアセット・シーン・スクリプトを送って取り込ませる。§23）
//      … AndroidHotReloadController（監視・デバウンス・Output）→ 中核の Android/HotReload/AndroidHotReloadApplier
//    - 起動するシーン（PC の Play と同じ「開いているシーン」）… AndroidRunSceneChoice
//    - 未保存の変更の確認（保存して実行 / 保存せず実行 / キャンセル）… AndroidUnsavedChangesPrompt
//    - Output パネルの行                        … AndroidRunOutputFormatter の行を色付きで EditorLog へ
//    - Android の実行中のビューポート（PC のランタイムを隠して「Android で実行中（端末: …）」を出す。一時停止中は
//      端末のシーンの写しを編集用ランタイムに閲覧専用で出し、バナーを出す〈§20.17。段取りは MainWindow.AndroidSnapshot.cs〉）
//      … AndroidViewportPolicy（当てるのは ApplyAndroidViewport）
//  PC の実行（従来の Play）は OnPlayPause / OnStop のまま。実行・停止ボタンの Click はここで行き先を振り分ける。
//  エミュレータの AVD はエディタの設定 android.emulator_avd（EditorPreferences.Android。設定の画面は無い）。
//
//  【スレッド】AndroidRunController のイベントは任意のスレッドから届く。状態の変化は Dispatcher へ回して
//  プレイバーを当て直し、行は EditorLog.Write（スレッド安全）へそのまま流す。
//
//  【関連】docs/android.md §20（エディタからの実行）・docs/editor_ui_style.md 7 章（Output の色）
// ============================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Android.HotReload;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.AndroidRun;
using SEEDEditor.Project;
using SEEDEditor.Runtime;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── 表示文言（マジックストリングの一元化）────────────────────

    /// <summary>実行先を選んだときのログの書式（{0}=実行先）。</summary>
    private const string RunTargetChosenLogFormat = "[Android] 実行先: {0}";

    /// <summary>実行先の選択を保存できなかったときのログの書式（{0}=理由）。</summary>
    private const string RunTargetSaveFailedFormat = "[Android] 実行先の選択を保存できません: {0}";

    /// <summary>Android の実行先を使えないときの起動時のログの書式（{0}=理由）。</summary>
    private const string AndroidUnavailableLogFormat = "[Android] 実行先は PC だけにします: {0}";

    /// <summary>端末の一覧が時間内に返らなかったときの理由の書式（{0}=秒数）。</summary>
    private const string RunTargetListTimeoutFormat = "端末の一覧が {0} 秒以内に返りませんでした（adb の応答を確認してください）。";

    /// <summary>停止で予期しない例外が出たときのログの書式（{0}=理由）。</summary>
    private const string AndroidStopFailedLogFormat = "[Android] 停止の途中で予期しないエラー: {0}";

    /// <summary>既に動いているため始めなかったときの理由。</summary>
    private const string AndroidAlreadyRunningReason = "Android の実行が動いています。";

    /// <summary>AI ツール（seed_play）が Android の実行中に PC の Play を求めたときの返事。</summary>
    private const string AndroidRunBlocksPcPlayMessage =
        "Android で実行中のため PC の Play は始められません（プレイバーの停止ボタンで Android の実行を止めてから）。";

    /// <summary>端末の一覧（adb devices -l ＋ 端末ごとの getprop）の時間切れ（秒）。adb のサーバーの起動を含めても数秒で返る。</summary>
    private const double RunTargetListTimeoutSeconds = 20.0;

    /// <summary>
    /// 状態表示の色の種類 → ブラシ（従来 ApplyUiState で使っていた色。EDIT=緑・PLAY=水色・PAUSE=橙・BUILDING=黄・IDLE=灰）。
    /// </summary>
    private static readonly IReadOnlyDictionary<PlayBarTone, Brush> PlayBarToneBrushes = new Dictionary<PlayBarTone, Brush>
    {
        [PlayBarTone.Edit]    = Brushes.LightGreen,
        [PlayBarTone.Running] = Brushes.LightSkyBlue,
        [PlayBarTone.Paused]  = Brushes.Orange,
        [PlayBarTone.Busy]    = Brushes.Yellow,
        [PlayBarTone.Idle]    = Brushes.Gray,
    };

    // ── 状態 ─────────────────────────────────────────────────

    /// <summary>Android の実行先を使えるかと、その材料（起動時に 1 回調べる）。</summary>
    private AndroidRunEnvironment? _androidEnvironment;

    /// <summary>Android の実行（エンジンのリポジトリが見つからなければ null）。</summary>
    private AndroidRunController? _androidRun;

    /// <summary>
    /// Android の実行中の差し替え（保存したアセット・シーン・スクリプトを動いている端末へ送って取り込ませる。docs/android.md §23）。
    /// エンジンのリポジトリが見つからなければ null。
    /// </summary>
    private AndroidHotReloadController? _androidHotReload;

    /// <summary>実行先の一覧と選択（コンボに出しているもの）。</summary>
    private RunTargetCatalog _runTargets = RunTargetCatalogBuilder.PcOnly;

    /// <summary>コンボの項目（その場で入れ替える。ItemsSource を差し替えると開いているドロップダウンが乱れるため）。</summary>
    private readonly ObservableCollection<RunTargetEntry> _runTargetItems = new();

    /// <summary>最後に取れた端末の一覧（まだ取っていなければ null）。</summary>
    private IReadOnlyList<AndroidDeviceEntry>? _androidDevices;

    /// <summary>最後の端末の一覧の取得が失敗した理由（成功していれば null）。</summary>
    private string? _androidDeviceListError;

    /// <summary>端末の一覧を取り直している途中か。</summary>
    private bool _runTargetRefreshing;

    /// <summary>利用者がコンボで選んだか（起動時の復元が、待っている間の利用者の選択を上書きしないように）。</summary>
    private bool _runTargetChosenByUser;

    /// <summary>コンボの選択をコードから書き換えている間 true（SelectionChanged の再入を防ぐ）。</summary>
    private bool _suppressRunTargetSelection;

    /// <summary>端末の一覧の取得の中断の合図（取り直していなければ null。エディタを閉じるときに止める）。</summary>
    private CancellationTokenSource? _runTargetListCancellation;

    /// <summary>
    /// 「保存して実行」で保存の完了を待っているか（保存が終わったら Android の実行を始める。失敗したら取りやめる）。
    /// </summary>
    private bool _pendingAndroidRun;

    /// <summary>エミュレータを起動するときの AVD（エディタの設定 android.emulator_avd。未設定なら null）。</summary>
    private static string? ConfiguredEmulatorAvd => EditorPreferences.Instance.Android?.EmulatorAvd;

    /// <summary>
    /// 端末のランタイムが一時停止などの IPC を待ち受けるポート（エディタの設定 android.ipc_port。未設定なら null＝既定。段階D-1）。
    /// </summary>
    private static int? ConfiguredIpcPort => EditorPreferences.Instance.Android?.IpcPort;

    /// <summary>Android の実行が動いているか（Building / Running / Stopping）。</summary>
    private bool IsAndroidRunActive => _androidRun?.Snapshot.IsActive ?? false;

    /// <summary>
    /// ビューポートに Android の実行の案内を出している（PC のランタイムを隠している）か。
    /// true の間は、ランタイムの READY・最初のフレーム・起動時スプラッシュの解除でも
    /// ランタイムの子ウィンドウを出さず、案内も閉じない（MainWindow.Input.cs の各所が見る）。
    /// </summary>
    private bool _androidViewportNoticeShown;

    // ── 初期化・後始末 ───────────────────────────────────────

    /// <summary>
    /// 実行先セレクタと Android の実行を用意する（OnWindowLoaded から 1 回）。
    /// 前回 Android の端末を選んでいたときだけ、起動時に端末を探して選択を戻す（adb のサーバーを無用に起こさない）。
    /// </summary>
    private void InitRunTargets()
    {
        var toolchain = AndroidToolchain.Detect();
        var environment = AndroidRunEnvironment.Detect(
            ProjectContext.RootDir, toolchain, AppContext.BaseDirectory, Environment.CurrentDirectory);
        _androidEnvironment = environment;

        if (environment.Engine is { } engine)
        {
            var controller = new AndroidRunController(new AndroidRunBackend(engine, toolchain), AndroidRunTimings.Default);
            // 実行中の差し替え（§23）: 端末のアプリが動いている間だけアセットルートを監視し、保存を端末へ送る
            var projectDir = string.IsNullOrWhiteSpace(ProjectContext.RootDir) ? AssetsPath : ProjectContext.RootDir;
            var hotReload = new AndroidHotReloadController(
                new AndroidHotReloadBackend(toolchain, engine, projectDir),
                () => controller.HotReloadTarget,
                pollInterval: AndroidHotReloadController.DefaultPollInterval,
                // スクリプト・シーンは PC の自動再読込の設定（表示メニュー）がオフなら差し替えない。ほかのアセットはいつも
                kindEnabled: kind => kind switch
                {
                    AndroidHotReloadKind.Scripts => EditorPreferences.Instance.AutoReloadScripts,
                    AndroidHotReloadKind.Scene => EditorPreferences.Instance.AutoReloadScene,
                    _ => true,
                });
            hotReload.OutputWritten += line => EditorLog.Write(line.Text, line.Style);
            hotReload.InfoWritten += text =>
            {
                var line = AndroidHotReloadOutputFormatter.Info(text);
                EditorLog.Write(line.Text, line.Style);
            };
            controller.StateChanged += () => Dispatcher.BeginInvoke(() =>
            {
                ApplyPlayBar();
                // ビューポート: 動いている間は PC のランタイムを隠して案内を出し、Idle に戻ったら元に戻す
                ApplyAndroidViewport();
                _androidHotReload?.OnRunStateChanged(controller.Snapshot, AssetsPath);
                // 一時停止中の端末のシーンの写し: 取り出せたらシーンパネルへ閲覧専用で出し、一時停止を出たら戻す（§20.17）
                _snapshotView?.OnRunStateChanged(controller.Snapshot);
            });
            controller.OutputWritten += line => EditorLog.Write(line.Text, line.Style);
            _androidRun = controller;
            _androidHotReload = hotReload;
            // 写しをシーンパネルへ出す段取り（MainWindow.AndroidSnapshot.cs）と、閲覧専用で捨てた命令の知らせ
            InitAndroidSnapshotView();
            if (_runtimeManager is not null) _runtimeManager.RawMessageReceived += OnRuntimeLineForSnapshotView;
        }
        if (!environment.Availability.IsAvailable)
        {
            EditorLog.Write(string.Format(AndroidUnavailableLogFormat, environment.Availability.Reason));
        }

        CmbRunTarget.ItemsSource = _runTargetItems;
        var memory = RunTargetSelectionStore.Load(ProjectContext.RootDir);
        ApplyRunTargetCatalog(BuildRunTargetCatalog(RunTargetSelectionMode.Restore, memory.PreferredId, memory.PreferredName));
        if (environment.Availability.IsAvailable && memory.PrefersAndroidDevice)
        {
            _ = RefreshRunTargetsAsync(RunTargetSelectionMode.Restore, memory);
        }
    }

    /// <summary>
    /// エディタを閉じるとき: Android の実行（ビルドの子プロセス・logcat）と端末の一覧の取得を止める（待たない）。
    /// 端末のアプリは止めない（閉じる操作を adb で待たせない。docs/android.md §20）。
    /// </summary>
    private void ShutdownAndroidRun()
    {
        _androidHotReload?.Dispose();
        _androidRun?.Dispose();
        try
        {
            _runTargetListCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 取得が終わった直後
        }
    }

    // ── 実行先の一覧 ─────────────────────────────────────────

    /// <summary>いまの材料で一覧を組み立てる。</summary>
    /// <param name="mode">選び方。</param>
    /// <param name="preferredId">選んでおきたいもの（"pc" か端末のシリアル）。</param>
    /// <param name="preferredName">選んでおきたい端末の名前（見えなくなった端末の行に使う）。</param>
    /// <returns>一覧。</returns>
    private RunTargetCatalog BuildRunTargetCatalog(RunTargetSelectionMode mode, string? preferredId, string? preferredName) =>
        RunTargetCatalogBuilder.Build(new RunTargetCatalogInput
        {
            Android = _androidEnvironment?.Availability ?? AndroidTargetAvailability.Unavailable(AndroidRunEnvironment.NoEngineReason),
            Devices = _androidDevices,
            Refreshing = _runTargetRefreshing,
            ListError = _androidDeviceListError,
            PreferredId = preferredId,
            PreferredName = preferredName,
            Mode = mode,
            EmulatorAvd = ConfiguredEmulatorAvd,
        });

    /// <summary>いまの選択を保ったまま一覧を組み立て直す。</summary>
    /// <returns>一覧。</returns>
    private RunTargetCatalog RebuildRunTargetsKeepingSelection()
    {
        var current = _runTargets.Selected;
        return BuildRunTargetCatalog(RunTargetSelectionMode.Keep, current.Id, current.IsAndroid ? current.Name : null);
    }

    /// <summary>
    /// 端末の一覧を adb で取り直して反映する（取り直している間は「探しています」の行を出す）。
    /// </summary>
    /// <param name="mode">選び方（起動時は Restore、コンボを開いたときは Keep）。</param>
    /// <param name="memory">前回の選択（Restore のときだけ）。</param>
    private async Task RefreshRunTargetsAsync(RunTargetSelectionMode mode, RunTargetMemory? memory)
    {
        var environment = _androidEnvironment;
        if (environment is null || !environment.Availability.IsAvailable || _runTargetRefreshing) return;

        _runTargetRefreshing = true;
        ApplyRunTargetCatalog(RebuildRunTargetsKeepingSelection());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(RunTargetListTimeoutSeconds));
        _runTargetListCancellation = cancellation;
        try
        {
            // 中核はスレッドプールで adb を動かす（UI スレッドは止めない）。戻りは UI スレッド
            _androidDevices = await new AndroidDeviceActions(environment.Toolchain).ListDevicesAsync(cancellation.Token);
            _androidDeviceListError = null;
        }
        catch (OperationCanceledException)
        {
            _androidDeviceListError = string.Format(RunTargetListTimeoutFormat, RunTargetListTimeoutSeconds);
        }
        catch (Exception ex)
        {
            // adb が無い・一覧を取れない（AndroidPipelineException）ほか、想定外の失敗も「一覧を取れません」の行で見せる
            // （呼び出し側は投げっぱなしの非同期なので、ここで受け止めないと黙って消える）
            _androidDeviceListError = ex.Message;
        }
        finally
        {
            _runTargetRefreshing = false;
            _runTargetListCancellation = null;
        }

        // 起動時の復元は、待っている間に利用者が選んでいなければだけ行う
        var restore = mode == RunTargetSelectionMode.Restore && memory is not null && !_runTargetChosenByUser;
        ApplyRunTargetCatalog(restore
            ? BuildRunTargetCatalog(RunTargetSelectionMode.Restore, memory!.PreferredId, memory.PreferredName)
            : RebuildRunTargetsKeepingSelection());
    }

    /// <summary>
    /// 一覧と選択をコンボへ当てる（中身が同じなら項目は入れ替えない）。最後にプレイバーを当て直す。
    /// </summary>
    /// <param name="catalog">一覧。</param>
    private void ApplyRunTargetCatalog(RunTargetCatalog catalog)
    {
        _runTargets = catalog;
        _suppressRunTargetSelection = true;
        try
        {
            if (!_runTargetItems.SequenceEqual(catalog.Entries))
            {
                _runTargetItems.Clear();
                foreach (var entry in catalog.Entries) _runTargetItems.Add(entry);
            }
            if (!Equals(CmbRunTarget.SelectedItem, catalog.Selected)) CmbRunTarget.SelectedItem = catalog.Selected;
        }
        finally
        {
            _suppressRunTargetSelection = false;
        }
        ApplyPlayBar();
    }

    /// <summary>コンボを開いた: 端末を探し直す（いまの選択は保つ）。</summary>
    private void OnRunTargetDropDownOpened(object? sender, EventArgs e)
    {
        _ = RefreshRunTargetsAsync(RunTargetSelectionMode.Keep, null);
    }

    /// <summary>コンボで選んだ: 覚えて（プロジェクトごと）プレイバーを当て直す。</summary>
    private void OnRunTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRunTargetSelection) return;
        if (CmbRunTarget.SelectedItem is not RunTargetEntry entry || entry.Kind == RunTargetKind.Notice)
        {
            // 案内の行は選べない（無効の行）。万一選ばれても元の選択へ戻す
            ApplyRunTargetCatalog(_runTargets);
            return;
        }

        _runTargetChosenByUser = true;
        _runTargets = _runTargets with { Selected = entry };
        EditorLog.Write(string.Format(RunTargetChosenLogFormat, entry.Text));
        try
        {
            RunTargetSelectionStore.Save(ProjectContext.RootDir, entry.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EditorLog.Write(string.Format(RunTargetSaveFailedFormat, ex.Message));
        }
        ApplyPlayBar();
    }

    // ── プレイバー ───────────────────────────────────────────

    /// <summary>いまのプレイバーの判断。</summary>
    /// <param name="pcState">PC の状態（null ならランタイムの今の状態）。</param>
    /// <returns>見た目と動き。</returns>
    private PlayBarView ComputePlayBarView(EditorState? pcState = null) =>
        PlayBarPolicy.Compute(new PlayBarInput(
            pcState ?? _runtimeManager?.State ?? EditorState.Idle,
            _runTargets.Selected,
            _androidRun?.Snapshot ?? AndroidRunSnapshot.Idle));

    /// <summary>
    /// プレイバー（状態表示・実行／停止ボタン・実行先コンボ・Android の進捗）を判断どおりに当てる。
    /// PC の状態遷移（ApplyUiState）・Android の実行の変化・実行先の選択のたびに呼ぶ（UI スレッド）。
    /// </summary>
    /// <param name="pcState">PC の状態（ApplyUiState から渡す。null ならランタイムの今の状態）。</param>
    private void ApplyPlayBar(EditorState? pcState = null)
    {
        var view = ComputePlayBarView(pcState);

        var pause = view.PlayGlyph == PlayGlyph.Pause;
        BtnPlayPause.IsEnabled  = view.PlayEnabled;
        BtnPlayPause.Background = pause ? _brushPause : _brushPlay;
        ImgPlayPause.Source     = pause ? _imgPause : _imgPlay;
        BtnPlayPause.ToolTip    = view.PlayToolTip;
        BtnStop.IsEnabled       = view.StopEnabled;
        BtnStop.ToolTip         = view.StopToolTip;

        var brush = PlayBarToneBrushes[view.StateTone];
        LblState.Text        = view.StateLabel;
        LblState.Foreground  = brush;
        IconState.IconKey    = view.StateIconKey;
        IconState.Foreground = brush;

        CmbRunTarget.IsEnabled = view.TargetSelectorEnabled;
        CmbRunTarget.ToolTip   = view.TargetSelectorToolTip;

        AndroidRunProgressPanel.Visibility = view.ProgressText is null ? Visibility.Collapsed : Visibility.Visible;
        TxtAndroidRunProgress.Text         = view.ProgressText ?? string.Empty;
        PbAndroidRun.Visibility            = view.ProgressFraction is null ? Visibility.Collapsed : Visibility.Visible;
        PbAndroidRun.Value                 = view.ProgressFraction ?? 0;
    }

    // ── ビューポート（Android の実行中の案内）────────────────────

    /// <summary>
    /// Android の実行の状態に合わせてビューポート（シーンパネル）を当てる（判断は AndroidViewportPolicy。UI スレッド）。
    /// 動いている間は PC のランタイムの子ウィンドウとホストを隠し、起動中画面（ViewportLoadingOverlay）に
    /// 「Android で実行中（端末: …）」などの案内を出す。一時停止中に端末のシーンの写しを出している間は、PC のランタイム
    /// （写しを読み込んだ編集用ランタイム）を見せてビューポートの上に閲覧専用のバナーを出す（§20.17）。
    /// Idle に戻ったら PC の状態の表示へ戻す。
    /// Android が動いていないうちは何もしない（PC の Play / Edit の表示は ApplyUiState が当てたまま）。
    /// Android の状態の変化（StateChanged）・写しの段取りの変化・PC の状態遷移（ApplyUiState の最後）から呼ぶ。
    /// </summary>
    /// <param name="pcState">PC の状態（ApplyUiState から渡す。null ならランタイムの今の状態）。</param>
    private void ApplyAndroidViewport(EditorState? pcState = null)
    {
        var view = AndroidViewportPolicy.Compute(
            _androidRun?.Snapshot ?? AndroidRunSnapshot.Idle,
            _snapshotView?.ViewPhase ?? SEEDEditor.SceneSnapshot.SceneSnapshotViewPhase.Idle,
            _snapshotView?.GivenUpGeneration ?? 0);
        // 閲覧専用のバナー（写しを出している間だけ。帯はビューポートと別の行なので HwndHost に隠れない）
        SnapshotViewBanner.Show(view.BannerText);
        if (view.HidesRuntime)
        {
            var first = !_androidViewportNoticeShown;
            _androidViewportNoticeShown = true;
            // ホストごと隠す（子ウィンドウだけを隠すとホストの HWND が WPF の描画に穴を開けたままで案内が黒く抜ける。
            // 起動時スプラッシュの保持と同じ隠し方）。判定は UpdateViewportHostVisibility が _androidViewportNoticeShown を見る
            if (first) _runtimeManager?.SetRuntimeWindowVisible(false);
            UpdateViewportHostVisibility();
            TxtViewportStatus.Text            = view.NoticeText ?? string.Empty;
            ViewportLoadingOverlay.Visibility = Visibility.Visible;
            return;
        }

        // PC のランタイム（Idle に戻った）か、端末の写しを読み込んだ編集用ランタイム（一時停止中・閲覧専用）を見せる。
        // どちらも「案内を閉じて PC のランタイムの子ウィンドウを出し直す」同じ手順
        if (!_androidViewportNoticeShown) return;
        _androidViewportNoticeShown = false;
        RestorePcViewportAfterAndroid(pcState ?? _runtimeManager?.State ?? EditorState.Idle);
    }

    /// <summary>
    /// Android の案内を閉じて、PC の状態の表示へ戻す。
    /// Edit でランタイムが準備済みなら子ウィンドウを出し直して案内を閉じる（起動時スプラッシュの解除と同じ手順）。
    /// それ以外（ランタイムの作り直し・起動中など）は ApplyUiState でその状態の表示を当て直す
    /// （Android の実行中は PC の Play を始められないので、ここへ来る PC の状態は Edit かランタイムの準備中だけ）。
    /// </summary>
    /// <param name="pcState">PC の状態。</param>
    private void RestorePcViewportAfterAndroid(EditorState pcState)
    {
        if (pcState != EditorState.Edit)
        {
            // 案内を出すときに隠した子ウィンドウを出し直しておく（ホストは準備ができるまで隠れたままなので見えない。
            // 起動時スプラッシュの保持中は保持の解除に任せる）。表示はその状態の表で当て直す
            if (!_startupSplashHold) _runtimeManager?.SetRuntimeWindowVisible(true);
            ApplyUiState(pcState);
            return;
        }
        UpdateViewportHostVisibility();
        if (_startupSplashHold)
        {
            // 起動時スプラッシュの保持中なら、子ウィンドウを出すのも案内を閉じるのも保持の解除に任せる
            TxtViewportStatus.Text = StartupSplashStatusText;
            return;
        }
        _runtimeManager?.SetRuntimeWindowVisible(true);
        // 隠している間にレイアウトが変わっていても、子ウィンドウをコンテナに合わせ直す
        _runtimeManager?.ResizeRuntimeToContainer();
        TxtViewportStatus.Text = string.Empty;
        // ランタイムがまだ READY でなければ起動中画面のまま（READY・最初のフレームで閉じる）
        if (_viewportRuntimeReady) ViewportLoadingOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 実行ボタン: PC の Play / Pause / Resume か、選んでいる端末での実行を始める、または Android の実行の一時停止・再開
    /// （段階D-1。PC の Play と同じボタン。判断は PlayBarPolicy）。
    /// </summary>
    private void OnPlayBarPlayClick(object sender, RoutedEventArgs e)
    {
        switch (ComputePlayBarView().PlayAction)
        {
            case PlayBarAction.TogglePc:
                OnPlayPause(sender, e);
                break;
            case PlayBarAction.StartAndroid:
                StartAndroidRun();
                break;
            case PlayBarAction.PauseAndroid:
                // 送るだけ（状態の変化は StateChanged でプレイバーへ戻ってくる）
                _androidRun?.TryPause();
                break;
            case PlayBarAction.ResumeAndroid:
                _androidRun?.TryResume();
                break;
        }
    }

    /// <summary>停止ボタン: PC の実行か Android の実行を止める（判断は PlayBarPolicy）。</summary>
    private void OnPlayBarStopClick(object sender, RoutedEventArgs e)
    {
        switch (ComputePlayBarView().StopAction)
        {
            case StopBarAction.StopPc:
                OnStop(sender, e);
                break;
            case StopBarAction.StopAndroid:
                _ = StopAndroidRunAsync();
                break;
        }
    }

    // ── Android の実行 ───────────────────────────────────────

    /// <summary>
    /// 選んでいる実行先（Android（自動）か端末）で実行を始める（PC の Play と同じ事前確認: アセットフォルダ・
    /// スクリプトのコンパイル）。未保存の変更があれば「保存して実行 / 保存せず実行 / キャンセル」を尋ねる。
    /// 端末で起動するシーンは PC の Play と同じ「開いているシーン」（「開始シーンからプレイ」がオンなら開始シーン）。
    /// 進み具合とログは Output パネルへ流れる（パネルを前面へ出す）。
    /// </summary>
    private void StartAndroidRun()
    {
        var controller = _androidRun;
        var target = _runTargets.Selected;
        if (controller is null || !target.IsAndroid || !target.CanRun) return;
        if (!target.IsAndroidAuto && target.Serial is null) return;

        if (!CheckAssetsBeforeRun()) return;
        // APK にはスクリプトを事前コンパイルして入れる。エラーがあれば PC の Play と同じくここで止める
        if (!CheckScriptsBeforePlay()) return;

        // APK は保存済みのファイルから作るので、未保存の変更は端末に届かない（PC の Play は編集中の状態で動く）
        var runWithoutSaving = false;
        if (AndroidUnsavedChangesPrompt.NeedsPrompt(_isDirty))
        {
            switch (AskUnsavedChangesBeforeAndroidRun())
            {
                case AndroidUnsavedChoice.SaveAndRun:
                    // 保存は非同期（完了は OnSaveCompleted → ContinuePendingAndroidRun）。送れなければ取りやめる
                    _pendingAndroidRun = true;
                    if (DoQuickSave())
                    {
                        WriteAndroidLine(AndroidRunOutputFormatter.SavingBeforeRun());
                    }
                    else
                    {
                        _pendingAndroidRun = false;
                        WriteAndroidLine(AndroidRunOutputFormatter.SaveNotStartedRunCanceled());
                    }
                    return;
                case AndroidUnsavedChoice.RunWithoutSaving:
                    runWithoutSaving = true;
                    break;
                default:
                    WriteAndroidLine(AndroidRunOutputFormatter.UnsavedPromptCanceled());
                    return;
            }
        }

        var projectDir = string.IsNullOrWhiteSpace(ProjectContext.RootDir) ? AssetsPath : ProjectContext.RootDir;
        var scene = AndroidRunSceneChoice.Decide(_playFromStartScene, _currentScenePath, AssetsPath);
        var request = AndroidEditorRunRequests.ForPlay(projectDir, target, scene.ScenePath, ConfiguredEmulatorAvd, ConfiguredIpcPort);
        if (!controller.TryStart(request, target.Text))
        {
            WriteAndroidLine(AndroidRunOutputFormatter.NotStarted(AndroidAlreadyRunningReason));
            return;
        }
        if (runWithoutSaving) WriteAndroidLine(AndroidRunOutputFormatter.UnsavedChangesWarning());
        WriteAndroidLine(AndroidRunOutputFormatter.SceneChosen(scene));
        ShowAnchorable("output", activate: false);
        ApplyPlayBar();
    }

    /// <summary>未保存の変更があるときに、Android で実行する前に保存するかを尋ねる（ヘッドレスではキャンセル）。</summary>
    /// <returns>答え。</returns>
    private AndroidUnsavedChoice AskUnsavedChangesBeforeAndroidRun()
    {
        var choice = SEEDEditor.Headless.EditorDialogs.ShowActionChoice(
            AndroidUnsavedChangesPrompt.Message,
            AndroidUnsavedChangesPrompt.Title,
            AndroidUnsavedChangesPrompt.SaveAndRunText,
            AndroidUnsavedChangesPrompt.RunWithoutSavingText,
            AndroidUnsavedChangesPrompt.CancelText,
            this);
        return choice switch
        {
            SEEDEditor.Dialogs.ActionChoice.Primary   => AndroidUnsavedChoice.SaveAndRun,
            SEEDEditor.Dialogs.ActionChoice.Secondary => AndroidUnsavedChoice.RunWithoutSaving,
            _                                         => AndroidUnsavedChoice.Cancel,
        };
    }

    /// <summary>
    /// 「保存して実行」の続き（OnSaveCompleted から呼ぶ。UI スレッド）。保存できたら実行を始め、失敗したら取りやめる。
    /// 待っていなければ何もしない。
    /// </summary>
    /// <param name="saved">保存に成功したか。</param>
    private void ContinuePendingAndroidRun(bool saved)
    {
        if (!_pendingAndroidRun) return;
        _pendingAndroidRun = false;
        if (!saved)
        {
            WriteAndroidLine(AndroidRunOutputFormatter.SaveFailedRunCanceled());
            return;
        }
        // 保存の間に実行先が PC へ変わった・PC の実行が始まった等なら、判断（PlayBarPolicy）に従って始めない
        if (ComputePlayBarView().PlayAction == PlayBarAction.StartAndroid) StartAndroidRun();
    }

    /// <summary>Android の実行を止める（ビルド中は子プロセスの終了を待ち、起動後はアプリも止める）。</summary>
    private async Task StopAndroidRunAsync()
    {
        if (_androidRun is null) return;
        try
        {
            await _androidRun.StopAsync();
        }
        catch (Exception ex)
        {
            EditorLog.Write(string.Format(AndroidStopFailedLogFormat, ex.Message));
        }
    }

    /// <summary>Android の実行の行を Output パネルへ出す。</summary>
    /// <param name="line">行。</param>
    private static void WriteAndroidLine(AndroidRunOutputLine line) => EditorLog.Write(line.Text, line.Style);
}
