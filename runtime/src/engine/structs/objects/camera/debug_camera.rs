use std::f32::consts::{FRAC_PI_2, FRAC_PI_4};
use winit::keyboard::KeyCode;

use winit::event::MouseButton;
use crate::engine::core::input::{Input, InputState};
use crate::engine::structs::tensor::{Vector3, Mat4x4};
use crate::engine::structs::transforms::{Quaternion, Transform};
use super::base_camera::{BaseCamera, CameraProjection};

/// デバッグ用フリーフライカメラ。
///
/// `BaseCamera` を内包し、キーボード + マウスによる 6DoF 移動を提供する。
/// C++ の `DebugCamera` に相当し、`BaseCamera` を「親クラス」として合成している。
///
/// # 操作
/// | 入力              | 動作              |
/// |-------------------|-------------------|
/// | W / S             | 前進 / 後退       |
/// | A / D             | 左移動 / 右移動   |
/// | E / Q             | 上昇 / 下降       |
/// | マウス移動        | 視点回転          |
/// | Shift             | 高速移動（×3）    |
///
/// # 座標系
/// 左手座標系（LH）準拠。Yaw = Y 軸回転、Pitch = X 軸回転。
/// Pitch は ±89° にクランプしジンバルロックを防ぐ。
#[derive(Debug, Clone)]
pub struct DebugCamera {
    /// 共通カメラデータ（BaseCamera を合成）
    pub base: BaseCamera,

    /// 水平回転量（ラジアン）。マウス X 移動で増減。
    pub yaw:   f32,
    /// 垂直回転量（ラジアン）。マウス Y 移動で増減。正 = 下向き。
    pub pitch: f32,

    /// 移動速度（ユニット/秒）
    pub move_speed:        f32,
    /// マウス感度（ラジアン/ピクセル）
    pub mouse_sensitivity: f32,
}

impl DebugCamera {
    /// パラメータを指定して生成する。
    pub fn new(
        transform:        Transform,
        projection:       CameraProjection,
        move_speed:       f32,
        mouse_sensitivity: f32,
    ) -> Self {
        Self {
            base:             BaseCamera::new(transform, projection),
            yaw:              0.0,
            pitch:            0.0,
            move_speed,
            mouse_sensitivity,
        }
    }

    /// デフォルト設定（原点、FOV 45°, 16:9, 速度 5.0, 感度 0.002）。
    pub fn default() -> Self {
        Self::new(
            Transform::identity(),
            CameraProjection {
                fov_y_rad:    FRAC_PI_4,
                aspect_ratio: 16.0 / 9.0,
                near:         0.1,
                far:          1000.0,
            },
            5.0,
            0.002,
        )
    }

    // ─── 更新 ─────────────────────────────────────────────────

    /// 入力に応じてカメラを更新する。フレームループで毎フレーム呼ぶ。
    ///
    /// - `input`      : 入力マネージャ（`&Input`）
    /// - `delta_time` : 前フレームからの経過時間（秒）
    pub fn update(&mut self, input: &Input, delta_time: f32) {
        self.update_rotation(input);
        self.update_movement(input, delta_time);
    }

    /// マウスの raw delta でヨー・ピッチを更新し、`transform.rotation` に反映する。
    ///
    /// 右クリック中のみ回転する（カーソル移動と干渉しないように）。
    fn update_rotation(&mut self, input: &Input) {
        if !input.is_press_mouse(MouseButton::Right) { return; }
        let delta = input.mouse_vector(InputState::Current);
        self.yaw   += delta.x * self.mouse_sensitivity;
        self.pitch += delta.y * self.mouse_sensitivity;

        // ±89° にクランプして真上・真下を向いたときのジンバルロックを防ぐ
        const PITCH_LIMIT: f32 = FRAC_PI_2 - 0.02; // ≈ 89°
        self.pitch = self.pitch.clamp(-PITCH_LIMIT, PITCH_LIMIT);

        // Yaw: ワールド Y 軸回転 → Pitch: ローカル X 軸回転 の順に合成
        let yaw_q   = Quaternion::from_axis_angle(Vector3::new(0.0, 1.0, 0.0), self.yaw);
        let pitch_q = Quaternion::from_axis_angle(Vector3::new(1.0, 0.0, 0.0), self.pitch);
        self.base.transform.rotation = yaw_q * pitch_q;
    }

    /// WASD + EQ でカメラ位置を移動する。Shift で 3 倍速。
    fn update_movement(&mut self, input: &Input, delta_time: f32) {
        let speed_mul = if input.is_press_key(KeyCode::ShiftLeft)
                        || input.is_press_key(KeyCode::ShiftRight) { 3.0 } else { 1.0 };
        let speed = self.move_speed * speed_mul * delta_time;

        let forward = self.base.transform.forward();
        let right   = self.base.transform.right();
        // 上下移動はワールド Y を使って地面に対して水平に昇降する
        let world_up = Vector3::new(0.0, 1.0, 0.0);

        if input.is_press_key(KeyCode::KeyW) { self.base.transform.position += forward   * speed; }
        if input.is_press_key(KeyCode::KeyS) { self.base.transform.position -= forward   * speed; }
        if input.is_press_key(KeyCode::KeyA) { self.base.transform.position -= right     * speed; }
        if input.is_press_key(KeyCode::KeyD) { self.base.transform.position += right     * speed; }
        if input.is_press_key(KeyCode::KeyE) { self.base.transform.position += world_up  * speed; }
        if input.is_press_key(KeyCode::KeyQ) { self.base.transform.position -= world_up  * speed; }
    }

    // ─── BaseCamera への委譲 ──────────────────────────────────

    /// ビュー行列を返す（BaseCamera に委譲）。
    #[inline]
    pub fn view_matrix(&self) -> Mat4x4<f32> { self.base.view_matrix() }

    /// 射影行列を返す（BaseCamera に委譲）。
    #[inline]
    pub fn projection_matrix(&self) -> Mat4x4<f32> { self.base.projection_matrix() }

    /// アスペクト比を更新する（ウィンドウリサイズ時に呼ぶ）。
    #[inline]
    pub fn set_aspect_ratio(&mut self, width: u32, height: u32) {
        self.base.set_aspect_ratio(width, height);
    }

    /// カメラの現在位置を返す。
    #[inline]
    pub fn position(&self) -> Vector3<f32> { self.base.transform.position }
}
