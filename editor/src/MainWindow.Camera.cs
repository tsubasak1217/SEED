// ============================================================
//  MainWindow.Camera.cs — シーン設定とデバッグカメラ制御
//
//  担当:
//   - シーン設定ウィンドウ（旧ビューポートオプションポップアップ）の開閉と変更通知の受け口
//   - シーン設定（SceneSettingsData）からランタイムへのライブ反映 IPC 送信
//   - シーン設定の .scene への永続化要求（SET_SCENE_SETTINGS）
//   - デバッグカメラの 2D/3D 切り替えとギズモ座標系トグル
//   - カメラ状態（CAM_STATE）受信と表示同期
//   - 全カメラキーのリリース
//   - エディタ状態変化への UI 反応（ボタン・ラベル・FPS など）
//
//  設計方針:
//   ランタイムへの IPC 送信はこのファイル（MainWindow）へ集約する。シーン設定ウィンドウは
//   値を SceneSettingsData へ書いてコールバックで種別を通知するだけで、IPC は送らない。
// ============================================================

using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using SEEDEditor.Native;
using SEEDEditor.Runtime;
using SEEDEditor.SceneSettings;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── シーン設定の状態 ─────────────────────────────────────────

    /// <summary>
    /// 現在のシーンのビューポート／レンダリング／編集時物理設定。
    /// 保存先は .scene ファイル（settings 節）で、書き込みはランタイムが行う
    /// （エディタは SET_SCENE_SETTINGS で内容を渡すだけ）。
    /// </summary>
    private SceneSettingsData _sceneSettings = new();

    /// <summary>
    /// シーンビュー表示モードの既定値（ライティングON）。
    /// ツールバーの CmbViewMode が未初期化・非 ComboBoxItem 選択のときのフォールバック。
    /// </summary>
    private const string DefaultViewMode = "lit";

    /// <summary>デバッグカメラ位置（X, Y, Z）。CAM_TRANSFORM / CAM_STATE で同期する。</summary>
    private float[] _debugCamPosition = { 0f, 2f, -10f };

    /// <summary>デバッグカメラ回転（オイラー角 X, Y, Z）。</summary>
    private float[] _debugCamEuler = { 0f, 0f, 0f };

    /// <summary>グリッド表示 / 軸ガイド表示の既定値（起動時は常に表示）。</summary>
    private const bool DefaultShowGuide = true;

    /// <summary>
    /// シーンビューの地面グリッドを表示するか（IPC SHOW_GRID に対応）。
    /// シーンパネル上部のトグルが持つセッション限りの状態で、.scene には保存しない
    /// （切り替え頻度が高く、シーンをダーティにすべき設定ではないため）。
    /// </summary>
    private bool _showGrid = DefaultShowGuide;

    /// <summary>
    /// 画面隅の XYZ 軸ギズモを表示するか（IPC SHOW_AXIS_GIZMO に対応）。
    /// _showGrid と同じくセッション限りの非永続状態。
    /// </summary>
    private bool _showAxisGizmo = DefaultShowGuide;

    /// <summary>開いているシーン設定ウィンドウ（多重起動防止用。閉じたら null）。</summary>
    private SceneSettingsWindow? _sceneSettingsWindow;

    /// <summary>project_settings.json のファイル名（旧設定からのフォールバック読み込み元）。</summary>
    private const string ProjectSettingsFileName = "project_settings.json";

    // ── シーン設定変更時の自動保存 ───────────────────────────────

    /// <summary>
    /// シーン設定変更後、実際に .scene を保存するまでの待ち時間（ミリ秒）。
    /// スライダーのドラッグ中は変更通知が毎フレーム飛ぶため、この時間だけ
    /// 追加の変更が来なくなってから 1 回だけ保存する（＝最後の変更のみ保存）。
    /// </summary>
    private const int SceneSettingsAutoSaveDebounceMs = 500;

    /// <summary>
    /// シーン設定変更の自動保存デバウンス用タイマー。変更のたびに Stop → Start して
    /// 期限を後ろへずらし、静止した時点で 1 度だけ Tick させる。
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _sceneSettingsAutoSaveTimer;

    // ── シーン設定ウィンドウ ─────────────────────────────────────

    /// <summary>
    /// ビューポート左下の歯車ボタン: シーン設定ウィンドウをモーダレスで開く。
    /// 既に開いている場合はアクティブ化するだけ（多重起動防止。プロジェクト設定と同じ流儀）。
    /// 開いた時点のカメラ状態を表示へ反映するため、Edit 中は GET_CAM_STATE を送る。
    /// </summary>
    private void OnViewportOptions(object sender, RoutedEventArgs e)
    {
        if (_sceneSettingsWindow is not null)
        {
            _sceneSettingsWindow.Activate();
        }
        else
        {
            var win = new SceneSettingsWindow(
                _sceneSettings,
                AssetsPath,
                SceneSettingsData.LoadSceneShadingAsset(_currentScenePath),
                _debugCamPosition,
                _debugCamEuler)
            {
                // Owner 指定によりエディタ本体より常に前面に表示される（モーダレスでも維持）
                Owner = this,
            };
            win.SettingChanged      += OnSceneSettingChanged;
            win.ShadingAssetChanged += OnSceneShadingAssetChanged;
            win.Closed              += (_, _) => _sceneSettingsWindow = null;
            _sceneSettingsWindow = win;
            win.Show();
        }

        if (_runtimeManager?.State == EditorState.Edit)
            _runtimeManager.SendToRuntime("GET_CAM_STATE");
    }

    /// <summary>
    /// シーン設定ウィンドウからの変更通知。種別に応じてライブ反映 IPC を送り、
    /// 最後に .scene への永続化（SET_SCENE_SETTINGS）を要求する。
    ///
    /// 例外は 1 つ:
    ///  - CameraTransform: カメラの位置・回転はシーン設定（settings 節）ではなく
    ///    .scene の debug_camera 節が持つため、CAM_TRANSFORM だけを送る。
    ///
    /// なおシーンビュー表示モード（view_mode）はこのウィンドウの管轄外で、
    /// ツールバーの CmbViewMode が直接持つ（OnViewModeChanged → SendPostFx）。
    /// </summary>
    private void OnSceneSettingChanged(SceneSettingsChangeKind kind)
    {
        switch (kind)
        {
            case SceneSettingsChangeKind.Fov:
                SendViewportFov();
                break;

            case SceneSettingsChangeKind.Far:
                SendViewportFar();
                break;

            case SceneSettingsChangeKind.CameraSpeed:
                SendCameraSpeed();
                break;

            case SceneSettingsChangeKind.Ortho2d:
                ApplyEditorCam2D();
                break;

            case SceneSettingsChangeKind.CameraTransform:
                // ウィンドウ側が保持する入力値を取り込んで送信する（永続化はしない）
                PullCameraTransformFromWindow();
                SendCameraTransform();
                return;

            case SceneSettingsChangeKind.DebugCameraAll:
                PullCameraTransformFromWindow();
                SendViewportFov();
                SendViewportFar();
                SendCameraSpeed();
                ApplyEditorCam2D();
                SendCameraTransform();
                break;

            case SceneSettingsChangeKind.Rendering:
                SendPostFx();
                break;

            case SceneSettingsChangeKind.Ambient:
                SendAmbient();
                break;

            case SceneSettingsChangeKind.RenderingAll:
                SendPostFx();
                SendAmbient();
                break;

            case SceneSettingsChangeKind.Physics:
            case SceneSettingsChangeKind.PhysicsAll:
                ApplyEditPhysicsFromSettings();
                break;
        }

        SendSceneSettings();
    }

    /// <summary>
    /// シーン設定ウィンドウの「シーンのシェーダー」欄が変更されたときの処理。
    /// この項目だけは .scene のトップレベル "shading_asset" に保存されるため、
    /// SET_SCENE_SETTINGS ではなく専用コマンドで送る（空文字列で解除）。
    /// </summary>
    private void OnSceneShadingAssetChanged(string virtualPath)
    {
        _runtimeManager?.SendToRuntime($"SET_SCENE_SHADING_ASSET:{virtualPath}");
        // シェーダー変更も .scene への永続化対象なので自動保存を予約する
        RequestSceneSettingsAutoSave();
    }

    /// <summary>
    /// シーン設定の変更を .scene へ自動保存するよう予約する（デバウンス付き）。
    ///
    /// 目的:
    ///   シーン設定ウィンドウでの変更は従来ダーティ化のみで、Ctrl+S を押すまで
    ///   ファイルに書かれなかった。「変更したら常に保存されるように」という要望に対し、
    ///   Ctrl+S と同じ保存経路（ExecuteSave → IPC SAVE_SCENE）を静かに実行する。
    ///
    /// 重要な副作用（仕様上避けられない）:
    ///   SAVE_SCENE はシーン全体を書き出すため、ユーザーが未保存のままにしていた
    ///   他の編集（アクターの移動・コンポーネント変更など）も同時に .scene へ保存される。
    ///   シーン設定だけを部分保存する手段はランタイム側に存在しないため、
    ///   この挙動は本要望を満たす限り不可避である。
    ///
    /// 自動保存をスキップする条件:
    ///   - Edit モードでない（Play 中の設定変更は保存しない）
    ///   - 新規シーンでファイルパスが未確定（_currentScenePath == null）
    ///     → 保存先が無く、ダイアログを出すのは「静かな保存」に反するため従来どおりダーティ化のみ
    ///   - アクター編集タブ / キャンバス編集タブが開いている（_activeActorPath != null）
    ///     → 対象アクターが編集用世界線に居るため、そのまま SAVE_SCENE すると
    ///       シーンが正しく書き出されない（DoQuickSave がタブを閉じてから保存しているのと同じ理由）。
    ///       自動保存でタブを勝手に閉じるのは破壊的なので、ここでは保存を見送る。
    /// </summary>
    private void RequestSceneSettingsAutoSave()
    {
        // ランタイム未接続（初期化中の同期送信など）では保存しない
        if (!_viewportSettingsInitialized) return;

        // タイマーは初回のみ生成し、以降は Stop/Start で期限を延長する
        if (_sceneSettingsAutoSaveTimer is null)
        {
            _sceneSettingsAutoSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(SceneSettingsAutoSaveDebounceMs),
            };
            _sceneSettingsAutoSaveTimer.Tick += (_, _) =>
            {
                _sceneSettingsAutoSaveTimer!.Stop();
                ExecuteSceneSettingsAutoSave();
            };
        }

        // ドラッグ中の連続変更では毎回ここを通り、最後の変更から
        // SceneSettingsAutoSaveDebounceMs 経過した 1 回だけが実際に保存される
        _sceneSettingsAutoSaveTimer.Stop();
        _sceneSettingsAutoSaveTimer.Start();
    }

    /// <summary>
    /// デバウンス満了時の実保存。スキップ条件は RequestSceneSettingsAutoSave のコメント参照。
    /// 予約時点と満了時点で状態（Play 開始・アクター編集開始など）が変わり得るため、
    /// 判定はここ（実行直前）で行う。
    /// </summary>
    private void ExecuteSceneSettingsAutoSave()
    {
        if (_runtimeManager?.State != EditorState.Edit) return;
        if (_activeActorPath != null) return;   // アクター／キャンバス編集中は見送り
        if (_currentScenePath == null) return;  // 新規シーン（保存先未確定）は従来どおりダーティのみ

        // Ctrl+S と同一経路。確認ダイアログは出ず、保存完了時に
        // OnSaveCompleted がダーティ解除とトースト表示を行う。
        ExecuteSave(_currentScenePath);
        EditorLog.Write("ExecuteSceneSettingsAutoSave — シーン設定変更による自動保存");
    }

    /// <summary>シーン設定ウィンドウが持つカメラ位置・回転の入力値を MainWindow 側へ取り込む。</summary>
    private void PullCameraTransformFromWindow()
    {
        if (_sceneSettingsWindow is null) return;
        _debugCamPosition = (float[])_sceneSettingsWindow.CameraPosition.Clone();
        _debugCamEuler    = (float[])_sceneSettingsWindow.CameraEuler.Clone();
    }

    // ── 個別設定のライブ反映 IPC ─────────────────────────────────
    //  送信フォーマットはランタイム側の受信パーサと 1:1 対応するため変更しないこと。

    /// <summary>デバッグカメラの画角をランタイムへ送信する。</summary>
    private void SendViewportFov()
    {
        if (!_viewportSettingsInitialized) return;
        _runtimeManager?.SendToRuntime($"VIEWPORT_FOV:{(int)_sceneSettings.DebugCamera.Fov}");
    }

    /// <summary>デバッグカメラの描画距離をランタイムへ送信する。</summary>
    private void SendViewportFar()
    {
        if (!_viewportSettingsInitialized) return;
        _runtimeManager?.SendToRuntime($"VIEWPORT_FAR:{(int)_sceneSettings.DebugCamera.Far}");
    }

    /// <summary>デバッグカメラの移動速度をランタイムへ送信する。</summary>
    private void SendCameraSpeed()
    {
        if (!_viewportSettingsInitialized) return;
        var speed = Math.Round(_sceneSettings.DebugCamera.Speed, 2);
        _runtimeManager?.SendToRuntime($"CAM_SPEED:{speed.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>グリッド表示の有無をランタイムへ送信する。</summary>
    private void SendShowGrid()
    {
        if (!_viewportSettingsInitialized) return;
        _runtimeManager?.SendToRuntime($"SHOW_GRID:{(_showGrid ? "1" : "0")}");
    }

    /// <summary>軸ギズモ表示の有無をランタイムへ送信する。</summary>
    private void SendShowAxisGizmo()
    {
        if (!_viewportSettingsInitialized) return;
        _runtimeManager?.SendToRuntime($"SHOW_AXIS_GIZMO:{(_showAxisGizmo ? "1" : "0")}");
    }

    /// <summary>デバッグカメラの位置・回転をランタイムへ送信する。</summary>
    private void SendCameraTransform()
    {
        if (!_viewportSettingsInitialized) return;
        var ci = CultureInfo.InvariantCulture;
        _runtimeManager?.SendToRuntime(
            $"CAM_TRANSFORM:{_debugCamPosition[0].ToString(ci)},{_debugCamPosition[1].ToString(ci)},{_debugCamPosition[2].ToString(ci)}," +
            $"{_debugCamEuler[0].ToString(ci)},{_debugCamEuler[1].ToString(ci)},{_debugCamEuler[2].ToString(ci)}");
    }

    /// <summary>
    /// ポストプロセス設定（Bloom / FXAA / Deferred / 各強度 / 機能マトリクス / 透明描画 /
    /// 表示モード）を 1 つの JSON にまとめてランタイムへ送信する。
    /// 送信フォーマット（SET_POST_FX:{json}）はランタイムのパーサと一致させること。
    /// </summary>
    private void SendPostFx()
    {
        if (!_viewportSettingsInitialized) return;

        var r  = _sceneSettings.Rendering;
        var ci = CultureInfo.InvariantCulture;

        // シーンビュー表示モード（"lit" / "unlit" / "wireframe" / "gbuffer_*"）。
        // ツールバーの CmbViewMode の選択項目 Tag をそのまま送る。シーン設定には含めない
        // 非永続項目のため、ここが唯一の取得元。Separator など ComboBoxItem 以外が
        // 選択された場合・XAML 初期化中で null の場合は既定（ライティングON）へフォールバックする。
        string viewMode =
            (CmbViewMode?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string
            ?? DefaultViewMode;

        // 新キー "features"（機能マトリクス）。旧キー gi_enabled は features.gi へ移行したため送らない。
        string features =
            $"\"features\":{{\"shadow\":\"{r.Features.Shadow}\",\"gi\":\"{r.Features.Gi}\"," +
            $"\"reflection\":\"{r.Features.Reflection}\",\"ao\":\"{r.Features.Ao}\"," +
            $"\"translucency\":\"{r.Features.Translucency}\"}}";

        // メッシュレットカリングは常時有効化したため "meshlet_cull" キーは送信しない（ランタイム側で常時 ON）。
        string json =
            $"{{\"bloom\":{Bool(r.Bloom)},\"fxaa\":{Bool(r.Fxaa)}," +
            $"\"bloom_intensity\":{r.BloomIntensity.ToString(ci)}," +
            $"\"transparency\":\"{r.Transparency}\",\"deferred\":{Bool(r.Deferred)}," +
            $"\"refract_sequential_grab\":{Bool(r.RefractSequentialGrab)}," +
            $"\"view_mode\":\"{viewMode}\"," +
            $"\"gi_intensity\":{r.GiIntensity.ToString(ci)}," +
            $"\"reflection_intensity\":{r.ReflectionIntensity.ToString(ci)}," +
            $"\"ao_intensity\":{r.AoIntensity.ToString(ci)},{features}}}";

        _runtimeManager?.SendToRuntime($"SET_POST_FX:{json}");
    }

    /// <summary>
    /// ツールバーのシーンビュー表示モードコンボ（CmbViewMode）の選択変更ハンドラ。
    /// 表示モードは非永続（.scene にもランタイム側スキーマにも保存しない）ため、
    /// SET_POST_FX の再送のみを行い SET_SCENE_SETTINGS は送らない
    /// （＝この操作でシーンがダーティにならない）。
    /// ランタイム未接続（_viewportSettingsInitialized == false）のときは
    /// SendPostFx 側のガードで何も送られず、接続時に SyncViewportSettings で再送される。
    /// </summary>
    private void OnViewModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SendPostFx();
    }

    /// <summary>bool を JSON のリテラル文字列へ変換する（SET_POST_FX 組み立て用）。</summary>
    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>
    /// 環境光（アンビエント）の色・強度をランタイムへ送信する（SET_AMBIENT:{r},{g},{b},{intensity}）。
    /// 色はリニア RGB。強度 0 で完全な暗闇になる。
    /// </summary>
    private void SendAmbient()
    {
        if (!_viewportSettingsInitialized) return;
        var r  = _sceneSettings.Rendering;
        var ci = CultureInfo.InvariantCulture;
        _runtimeManager?.SendToRuntime(
            $"SET_AMBIENT:{r.AmbientColor[0].ToString(ci)},{r.AmbientColor[1].ToString(ci)}," +
            $"{r.AmbientColor[2].ToString(ci)},{r.AmbientIntensity.ToString(ci)}");
    }

    /// <summary>
    /// 現在のシーン設定を .scene へ保存させる（ランタイムが settings 節へ書き込み、
    /// SCENE_MODIFIED を返してエディタのダーティ表示が更新される）。
    /// JSON は改行を含まない圧縮形式で送る（IPC は 1 コマンド 1 行のため）。
    /// </summary>
    private void SendSceneSettings()
    {
        if (!_viewportSettingsInitialized) return;
        _runtimeManager?.SendToRuntime($"SET_SCENE_SETTINGS:{_sceneSettings.ToCompactJsonString()}");
        // ランタイム側の scene.settings 更新後にファイルへ書き出す（IPC は順序保証のため
        // SAVE_SCENE は必ずこの SET_SCENE_SETTINGS の後に処理される）。デバウンス付き。
        RequestSceneSettingsAutoSave();
    }

    // ── シーン設定のロードと再同期 ───────────────────────────────

    /// <summary>
    /// 現在開いているシーンのシーン設定を読み込み、エディタ側の状態へ反映する。
    /// .scene に settings 節が無い旧シーンでは project_settings.json のルートキーから
    /// レンダリング設定をフォールバック生成する（読むだけで書き戻しはしない）。
    /// </summary>
    private void LoadSceneSettingsForCurrentScene()
    {
        _sceneSettings = SceneSettingsData.LoadForScene(
            _currentScenePath, Path.Combine(AssetsPath, ProjectSettingsFileName));

        // 編集時物理・2D トグルなど、UI 側にミラーしている状態を新しい設定に合わせる
        MirrorEditPhysicsFlags();
        RefreshPhysicsTimelineVisibility();
        Update2DCamToggleVisual();

        // 設定ウィンドウが開いていれば表示を差し替える
        _sceneSettingsWindow?.SetData(
            _sceneSettings,
            SceneSettingsData.LoadSceneShadingAsset(_currentScenePath),
            _debugCamPosition,
            _debugCamEuler);
    }

    /// <summary>
    /// ランタイム接続時（および Play→Edit 復帰時）に、シーン設定の全項目を再送する。
    /// ランタイムプロセスは新規起動のたびに既定値で始まるため、ここで完全に同期し直す。
    /// なお SET_SCENE_SETTINGS はここでは送らない（保存済みの値を送り返すだけで
    /// シーンをダーティにしてしまうため）。
    /// </summary>
    private void SyncViewportSettings()
    {
        _viewportSettingsInitialized = true;

        SendViewportFov();
        SendViewportFar();
        SendShowGrid();
        SendShowAxisGizmo();
        SendCameraSpeed();
        // 2D（正射投影）の状態も再送する（ランタイムは透視投影で起動するため）
        _runtimeManager?.SendToRuntime(
            $"EDITOR_CAM_ORTHO:{(_sceneSettings.DebugCamera.Ortho2d ? "1" : "0")}");
        // ポストプロセス設定・環境光を再同期する
        SendPostFx();
        SendAmbient();

        // 編集時物理設定は Edit runtime にのみ送信する（Play runtime へ送ると物理スレッドが停止する）
        if (_runtimeManager?.State == EditorState.Edit)
        {
            // 現在のシーンタブ（ワールド/ビューポート）のビューモードを同期する。
            // 起動時・ランタイム再接続時・Play→Edit 復帰時のいずれもここを通るため、
            // ランタイムのビューモードが常にタブ UI と一致する。
            SendCurrentEditView();
            // Edit ランタイムのカメラ移動キー状態を強制リセットする。
            // Play 切替中に届かなかった CAM_KEY_UP でキーがスタックしていると
            // 「RMB 押下だけでカメラが移動」「軸スナップが即キャンセル」になるため、
            // Edit 復帰・再同期のたびにクリーンな状態から始める。
            _runtimeManager?.SendToRuntime("CAM_KEYS_CLEAR");
            // 編集時物理は 2D/3D 統合コマンド 1 本で再同期する（2D/3D 常に同値）。
            SendEditPhysicsAll();
            // ギズモ座標系（World/Local）もランタイム再接続時に再同期する
            // （ランタイム側は新規プロセスごとに GizmoSpace::World で初期化されるため）。
            _runtimeManager?.SendToRuntime(_gizmoLocalSpace ? "GIZMO_SPACE:LOCAL" : "GIZMO_SPACE:WORLD");
        }
        // コライダー描画は Play/Edit 両方で送信する
        _runtimeManager?.SendToRuntime($"SET_PLAY_COLLIDER_DRAW:{(_playColliderDraw ? 1 : 0)}");
    }

    // ── デバッグカメラ 2D（正射投影）トグル ────────────────────────

    /// <summary>
    /// デバッグカメラの投影方式（2D＝正射 / 3D＝透視）を設定する。
    /// タブバー右端の「2D」ボタンから呼ばれる。視点は維持したまま、
    /// ランタイム側で 0.3 秒かけて補間される。
    /// </summary>
    private void SetEditorCam2D(bool on)
    {
        _sceneSettings.DebugCamera.Ortho2d = on;
        ApplyEditorCam2D();
        // 設定ウィンドウが開いていれば表示も追従させる
        _sceneSettingsWindow?.RefreshDisplay();
        SendSceneSettings();
    }

    /// <summary>
    /// 現在のシーン設定にある 2D フラグをランタイムとトグルボタンの見た目へ反映する
    /// （永続化はここでは行わない。呼び出し側が必要に応じて SendSceneSettings する）。
    /// </summary>
    private void ApplyEditorCam2D()
    {
        _runtimeManager?.SendToRuntime(
            $"EDITOR_CAM_ORTHO:{(_sceneSettings.DebugCamera.Ortho2d ? "1" : "0")}");
        Update2DCamToggleVisual();
    }

    // ── シーンパネル上部トグルボタンの共通見た目 ──────────────────

    /// <summary>トグルボタンのアクティブ時の背景・枠線色（タブのアクセントと同じオレンジ）。</summary>
    private static readonly SolidColorBrush ToggleActiveBrush   = new(Color.FromRgb(0xE8, 0x78, 0x20));
    /// <summary>トグルボタンの非アクティブ時の背景色。</summary>
    private static readonly SolidColorBrush ToggleInactiveBg     = new(Color.FromRgb(0x3A, 0x3A, 0x3A));
    /// <summary>トグルボタンの非アクティブ時の枠線色。</summary>
    private static readonly SolidColorBrush ToggleInactiveBorder = new(Color.FromRgb(0x55, 0x55, 0x55));
    /// <summary>トグルボタンの非アクティブ時の文字色。</summary>
    private static readonly SolidColorBrush ToggleInactiveFg     = new(Color.FromRgb(0xCC, 0xCC, 0xCC));

    /// <summary>
    /// シーンパネル上部のトグルボタン（2D / World-Local / グリッド / 軸）の見た目を
    /// ON/OFF 状態に合わせて更新する共通処理。ON はオレンジで強調する。
    /// </summary>
    /// <param name="button">対象ボタン（XAML 初期化前は null になり得る）。</param>
    /// <param name="active">ON なら true。</param>
    private static void ApplyToggleVisual(System.Windows.Controls.Button? button, bool active)
    {
        if (button == null) return;
        button.Background  = active ? ToggleActiveBrush : ToggleInactiveBg;
        button.BorderBrush = active ? ToggleActiveBrush : ToggleInactiveBorder;
        button.Foreground  = active ? Brushes.White     : ToggleInactiveFg;
    }

    /// <summary>タブバー右端「2D」ボタンの見た目をトグル状態に合わせて更新する。</summary>
    private void Update2DCamToggleVisual()
        => ApplyToggleVisual(Btn2DCamToggle, _sceneSettings.DebugCamera.Ortho2d);

    /// <summary>タブバー右端「2D」ボタン: 押すたびに 2D⇄3D をトグルする。</summary>
    private void On2DCamToggleClicked(object sender, RoutedEventArgs e)
        => SetEditorCam2D(!_sceneSettings.DebugCamera.Ortho2d);

    // ── グリッド表示 / 軸ガイド表示トグル ──────────────────────────

    /// <summary>
    /// シーンパネル上部「グリッド」ボタン: 地面グリッドの表示を切り替える。
    /// セッション限りの状態のため .scene へは保存しない（SET_SCENE_SETTINGS を送らない）。
    /// </summary>
    private void OnGridToggleClicked(object sender, RoutedEventArgs e)
    {
        _showGrid = !_showGrid;
        SendShowGrid();
        UpdateGridToggleVisual();
    }

    /// <summary>
    /// シーンパネル上部「軸」ボタン: 画面隅の XYZ 軸ギズモの表示を切り替える。
    /// グリッドと同じくセッション限りの非永続項目。
    /// </summary>
    private void OnAxisGizmoToggleClicked(object sender, RoutedEventArgs e)
    {
        _showAxisGizmo = !_showAxisGizmo;
        SendShowAxisGizmo();
        UpdateAxisGizmoToggleVisual();
    }

    /// <summary>「グリッド」トグルボタンの見た目を現在の状態に合わせて更新する。</summary>
    private void UpdateGridToggleVisual() => ApplyToggleVisual(BtnGridToggle, _showGrid);

    /// <summary>「軸」トグルボタンの見た目を現在の状態に合わせて更新する。</summary>
    private void UpdateAxisGizmoToggleVisual() => ApplyToggleVisual(BtnAxisGizmoToggle, _showAxisGizmo);

    // ── ギズモ座標系（World / Local）トグル ────────────────────────

    /// <summary>
    /// ギズモ（移動/回転/スケール）の座標系が Local（選択アクターのローカル軸）なら true。
    /// デフォルトは false（World＝ワールド軸整列、従来の挙動）。
    /// ギズモの真の状態はランタイム（Rust 側 App::gizmo_space）が保持するため、
    /// この変数はあくまで UI 表示・IPC 送信トリガー用のミラーである。
    /// </summary>
    private bool _gizmoLocalSpace = false;

    /// <summary>
    /// ギズモ座標系を設定し、ランタイムへ GIZMO_SPACE:WORLD / GIZMO_SPACE:LOCAL を送信する。
    /// タブバーの World/Local トグルボタンから呼ばれる。
    /// </summary>
    private void SetGizmoSpace(bool local)
    {
        _gizmoLocalSpace = local;
        _runtimeManager?.SendToRuntime(local ? "GIZMO_SPACE:LOCAL" : "GIZMO_SPACE:WORLD");
        UpdateGizmoSpaceToggleVisual();
    }

    /// <summary>タブバー「World/Local」ボタンの見た目・ラベルをトグル状態に合わせて更新する。</summary>
    private void UpdateGizmoSpaceToggleVisual()
    {
        if (BtnGizmoSpaceToggle == null) return;
        // ラベルだけはこのボタン固有（World / Local の二値表示）
        BtnGizmoSpaceToggle.Content = _gizmoLocalSpace ? "Local" : "World";
        ApplyToggleVisual(BtnGizmoSpaceToggle, _gizmoLocalSpace);
    }

    /// <summary>タブバー「World/Local」ボタン: 押すたびに World⇄Local をトグルする。</summary>
    private void OnGizmoSpaceToggleClicked(object sender, RoutedEventArgs e)
        => SetGizmoSpace(!_gizmoLocalSpace);

    // ── カメラ状態受信 ────────────────────────────────────────────

    /// <summary>
    /// ランタイムからのカメラ状態通知（CAM_STATE）を取り込む。
    /// 位置・回転・画角・描画距離・速度をエディタ側の状態へ反映し、
    /// シーン設定ウィンドウが開いていれば表示も更新する。
    ///
    /// ここでは SET_SCENE_SETTINGS を送らない。カメラを動かすたびにシーンが
    /// ダーティになってしまうため（保存はユーザーの明示操作に任せる）。
    /// また送り返しのループを防ぐため、ライブ反映 IPC の再送も行わない。
    /// </summary>
    private void OnCameraStateReceived(string payload)
    {
        // CAM_STATE:{px},{py},{pz},{euler_x},{euler_y},{euler_z},{fov_deg},{far},{speed}
        const int expectedFieldCount = 9;
        var parts = payload.Split(',');
        if (parts.Length < expectedFieldCount) return;

        var ci = CultureInfo.InvariantCulture;
        var ns = NumberStyles.Float;
        var values = new float[expectedFieldCount];
        for (int i = 0; i < expectedFieldCount; i++)
        {
            if (!float.TryParse(parts[i], ns, ci, out values[i])) return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            _debugCamPosition = new[] { values[0], values[1], values[2] };
            _debugCamEuler    = new[] { values[3], values[4], values[5] };

            var cam = _sceneSettings.DebugCamera;
            cam.Fov   = values[6];
            cam.Far   = values[7];
            cam.Speed = values[8];

            // 表示のみ更新する（ウィンドウ側は抑制フラグを立てて更新するため通知は発火しない）
            _sceneSettingsWindow?.UpdateCameraTransform(_debugCamPosition, _debugCamEuler);
        });
    }

    // ── カメラキー ────────────────────────────────────────────────

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
        Dispatcher.BeginInvoke(() =>
        {
            ApplyUiState(state);
            // Play へ遷移したら、スクリプトデバッグが有効な場合に
            // 新しい Play プロセスへ自動アタッチする（スクリプトは Play 中のみ動くため）。
            if (state == EditorState.Play)
                TryAutoAttachDebuggerOnPlay();

            // ランタイムが Edit で立ち上がった（＝IPC が繋がった）タイミングで、
            // 既に開いている .wgsl タブを検証し直す。
            // ファイルを開いた時点でランタイム未接続だと検証依頼が捨てられ、
            // 「一文字打つまで赤下線が出ない」状態になるため、接続を契機に追いつかせる。
            if (state == EditorState.Edit)
                PanelScriptEditor.RevalidateWgslDocuments();

            // 埋め込みインプレース Play の入力フォーカス制御。
            // 埋め込み Play では同じ子 HWND がゲーム描画も担うため、キーボード入力を
            // ランタイム側へ流すには OS フォーカスを子 HWND へ移す必要がある。
            // Play 開始時に子へ SetFocus し、Edit 復帰時はエディタへ戻す。
            if (_embeddedPlay)
            {
                if (state == EditorState.Play)      FocusRuntimeChild();
                else if (state == EditorState.Edit) ReturnFocusToEditor();
            }
        });
    }

    /// <summary>
    /// 埋め込み Play 中に、ランタイムの子 HWND へ OS キーボードフォーカスを移す。
    /// 子はエディタと別スレッド/別プロセスの winit ウィンドウのため、AttachThreadInput で
    /// スレッド入力を結合してから SetFocus する（クロススレッド SetFocus の定石）。
    /// ビューポートクリック時にも呼んで再フォーカスする。UI スレッドから呼ぶこと。
    /// </summary>
    private void FocusRuntimeChild()
    {
        var child = _runtimeManager?.RuntimeHwnd ?? 0;
        if (child == 0) return;

        var editorThread = NativeInterop.GetWindowThreadProcessId(
            new System.Windows.Interop.WindowInteropHelper(this).Handle, out _);
        var childThread  = NativeInterop.GetWindowThreadProcessId(child, out _);

        if (editorThread != childThread)
            NativeInterop.AttachThreadInput(editorThread, childThread, true);
        NativeInterop.SetForegroundWindow(child);
        NativeInterop.SetFocus(child);
        if (editorThread != childThread)
            NativeInterop.AttachThreadInput(editorThread, childThread, false);
    }

    /// <summary>埋め込み Play 停止時に、キーボードフォーカスをエディタ本体へ戻す。</summary>
    private void ReturnFocusToEditor()
    {
        var editorHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (editorHwnd != 0)
        {
            NativeInterop.SetForegroundWindow(editorHwnd);
            Activate();
        }
    }

    private void ApplyUiState(EditorState state)
    {
        // Edit/Pause 以外へ遷移するときは、押下中キーの CAM_KEY_UP を送ってから
        // クリアする（Pause 中に押したキーが Play 再開でスタックするのを防ぐ）。
        // bare Clear だと以降の実 KeyUp が _pressedVks に無いため UP が送信されない。
        if (state != EditorState.Edit && state != EditorState.Pause)
            ReleaseAllCamKeys();

        // Play 移行時はクランプ適用、それ以外は解除
        // RuntimeHwnd が 0 の場合は OnRuntimeHwndAvailable でリトライされる
        if (state == EditorState.Play && _clampInPlay && !_isDragging)
            ApplyPlayClamp();
        else
            ReleasePlayClamp();

        EditorLog.Write($"ApplyUiState — {state}");

        // シーン/アクタータブバーは Edit モードのみ操作可能にする
        // （Play 中はランタイムが EDIT_VIEW を無視するため UI 側も無効化する）
        ActorTabBar.IsEnabled = state == EditorState.Edit;

        switch (state)
        {
            case EditorState.Edit:
                _pressedVks.Clear();
                BtnPlayPause.IsEnabled   = true;
                BtnPlayPause.Background  = _brushPlay;
                ImgPlayPause.Source      = _imgPlay;
                BtnStop.IsEnabled        = false;
                LblState.Text            = "● EDIT";
                LblState.Foreground      = Brushes.LightGreen;
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
                LblState.Foreground      = Brushes.LightSkyBlue;
                // ビューポートホストの表示制御:
                // - ウィンドウ Play: ランタイムは別ウィンドウなのでホストを隠す（従来動作）
                // - 埋め込みインプレース Play: この WPF 要素がランタイム子 HWND のホストそのもの。
                //   Hidden にすると子 HWND ごと WS_VISIBLE が外れ、OS が WM_PAINT（＝winit の
                //   RedrawRequested）の配達を停止して描画が永久に止まる（黒画面の原因）。
                //   そのため埋め込み Play 中は必ず Visible を維持する。
                ViewportDocumentContent.Visibility =
                    (_runtimeManager?.InEmbeddedPlay ?? false) ? Visibility.Visible
                                                               : Visibility.Hidden;
                ViewportLoadingOverlay.Visibility  = Visibility.Collapsed;
                break;

            case EditorState.Pause:
                BtnPlayPause.IsEnabled   = true;
                BtnPlayPause.Background  = _brushPlay;
                ImgPlayPause.Source      = _imgPlay;
                BtnStop.IsEnabled        = true;
                LblState.Text            = "⏸ PAUSE";
                LblState.Foreground      = Brushes.Orange;
                ViewportDocumentContent.Visibility = Visibility.Visible;
                ViewportLoadingOverlay.Visibility  = Visibility.Collapsed;
                break;

            case EditorState.Building:
                BtnPlayPause.IsEnabled   = false;
                BtnPlayPause.Background  = _brushPlay;
                ImgPlayPause.Source      = _imgPlay;
                BtnStop.IsEnabled        = false;
                LblState.Text            = "⚙ BUILDING...";
                LblState.Foreground      = Brushes.Yellow;
                TxtViewportStatus.Text            = "ビルド中...";
                ViewportLoadingOverlay.Visibility = Visibility.Visible;
                break;

            case EditorState.Launching:
                // Play ランタイム起動シーケンス進行中（プロセス起動〜Play 遷移前）。
                // ・実行ボタンは無効化して連打による多重起動を UI 側でも防ぐ（不具合2）。
                // ・Stop ボタンは有効化し、ウィンドウ出現前でも起動をキャンセルできるようにする（不具合1）。
                BtnPlayPause.IsEnabled   = false;
                BtnPlayPause.Background  = _brushPlay;
                ImgPlayPause.Source      = _imgPlay;
                BtnStop.IsEnabled        = true;
                LblState.Text            = "▶ LAUNCHING...";
                LblState.Foreground      = Brushes.LightSkyBlue;
                TxtViewportStatus.Text            = "起動中...";
                ViewportLoadingOverlay.Visibility = Visibility.Visible;
                break;

            case EditorState.Idle:
                BtnPlayPause.IsEnabled   = false;
                BtnPlayPause.Background  = _brushPlay;
                ImgPlayPause.Source      = _imgPlay;
                BtnStop.IsEnabled        = false;
                LblState.Text            = "○ IDLE";
                LblState.Foreground      = Brushes.Gray;
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
