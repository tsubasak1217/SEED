namespace SEEDEditor.SceneSettings;

/// <summary>
/// シーン設定ウィンドウで変更された設定の種別。
///
/// ウィンドウ自身はランタイムへ IPC を送らず、この種別を MainWindow へ通知するだけに留める。
/// MainWindow 側が種別に応じたライブ反映用 IPC（VIEWPORT_FOV / SET_POST_FX 等）と
/// 永続化用の SET_SCENE_SETTINGS を送信する（IPC 送信を 1 箇所へ集約するため）。
/// </summary>
public enum SceneSettingsChangeKind
{
    /// <summary>デバッグカメラの画角（FOV）。</summary>
    Fov,
    /// <summary>デバッグカメラの描画距離（Far）。</summary>
    Far,
    /// <summary>デバッグカメラの移動速度。</summary>
    CameraSpeed,
    /// <summary>グリッド表示の ON/OFF。</summary>
    ShowGrid,
    /// <summary>軸ガイド表示の ON/OFF。</summary>
    ShowAxisGizmo,
    /// <summary>2D（正射投影）モードの ON/OFF。</summary>
    Ortho2d,
    /// <summary>デバッグカメラの位置・回転（シーン設定には含まれず CAM_TRANSFORM のみ送る）。</summary>
    CameraTransform,
    /// <summary>デバッグカメラカテゴリを既定値へ戻した（全項目を再送する）。</summary>
    DebugCameraAll,

    /// <summary>レンダリング設定（SET_POST_FX でまとめて送る項目）。</summary>
    Rendering,
    /// <summary>環境光の色・強度。</summary>
    Ambient,
    /// <summary>シーンビュー表示モード（view_mode）。セッション限りで永続化しない。</summary>
    ViewMode,
    /// <summary>レンダリングカテゴリを既定値へ戻した（全項目を再送する）。</summary>
    RenderingAll,

    /// <summary>編集時物理の設定。</summary>
    Physics,
    /// <summary>物理カテゴリを既定値へ戻した（全項目を再送する）。</summary>
    PhysicsAll,
}
