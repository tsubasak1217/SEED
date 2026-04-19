use std::f32::consts::{FRAC_PI_2, FRAC_PI_4};

use crate::engine::structs::tensor::{Vector3, Mat4x4};
use crate::engine::structs::transforms::{Quaternion, Transform};
use super::base_camera::{BaseCamera, CameraProjection};

// ============================================================
//  CameraInput — Editor から IPC + winit イベントで組み立てる
// ============================================================

/// デバッグカメラへの入力をまとめた構造体。
///
/// キーボード状態は Editor 側から IPC で受け取り、
/// マウスボタン・デルタ・スクロールは winit イベントから直接受け取る。
#[derive(Default)]
pub struct CameraInput {
    pub w:     bool,
    pub a:     bool,
    pub s:     bool,
    pub d:     bool,
    pub q:     bool,
    pub e:     bool,
    pub shift: bool,
    pub rmb:   bool,       // 右クリック（winit MouseInput）
    pub mouse_dx: f32,     // フレーム内累積（winit DeviceEvent）
    pub mouse_dy: f32,
    pub scroll:   f32,     // フレーム内累積（winit MouseWheel）
}

impl CameraInput {
    /// IPC キー名でキー状態を更新する。
    pub fn set_key(&mut self, key: &str, pressed: bool) {
        match key {
            "W"     => self.w     = pressed,
            "A"     => self.a     = pressed,
            "S"     => self.s     = pressed,
            "D"     => self.d     = pressed,
            "Q"     => self.q     = pressed,
            "E"     => self.e     = pressed,
            "SHIFT" => self.shift = pressed,
            _       => {}
        }
    }

    /// フレーム末にデルタ・スクロールをリセットする。キー状態は保持。
    pub fn end_frame(&mut self) {
        self.mouse_dx = 0.0;
        self.mouse_dy = 0.0;
        self.scroll   = 0.0;
    }
}

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
    pub fn update(&mut self, cam: &CameraInput, delta_time: f32) {
        self.update_rotation(cam);
        self.update_speed(cam);
        self.update_movement(cam, delta_time);
    }

    /// マウスの raw delta でヨー・ピッチを更新し、`transform.rotation` に反映する。
    /// 右クリック中のみ回転する。
    fn update_rotation(&mut self, cam: &CameraInput) {
        if !cam.rmb { return; }
        self.yaw   += cam.mouse_dx * self.mouse_sensitivity;
        self.pitch += cam.mouse_dy * self.mouse_sensitivity;

        const PITCH_LIMIT: f32 = FRAC_PI_2 - 0.02;
        self.pitch = self.pitch.clamp(-PITCH_LIMIT, PITCH_LIMIT);

        let yaw_q   = Quaternion::from_axis_angle(Vector3::new(0.0, 1.0, 0.0), self.yaw);
        let pitch_q = Quaternion::from_axis_angle(Vector3::new(1.0, 0.0, 0.0), self.pitch);
        self.base.transform.rotation = yaw_q * pitch_q;
    }

    /// ホイールスクロールで移動速度を調整する。
    fn update_speed(&mut self, cam: &CameraInput) {
        if cam.scroll != 0.0 {
            self.move_speed = (self.move_speed * 1.2_f32.powf(cam.scroll)).clamp(0.5, 500.0);
        }
    }

    /// 右クリック中のみ WASDQE でカメラ位置を移動する。Shift で 3 倍速。
    fn update_movement(&mut self, cam: &CameraInput, delta_time: f32) {
        if !cam.rmb { return; }

        let speed_mul = if cam.shift { 3.0 } else { 1.0 };
        let speed = self.move_speed * speed_mul * delta_time;

        let forward  = self.base.transform.forward();
        let right    = self.base.transform.right();
        let world_up = Vector3::new(0.0, 1.0, 0.0);

        if cam.w { self.base.transform.position += forward  * speed; }
        if cam.s { self.base.transform.position -= forward  * speed; }
        if cam.a { self.base.transform.position -= right    * speed; }
        if cam.d { self.base.transform.position += right    * speed; }
        if cam.e { self.base.transform.position += world_up * speed; }
        if cam.q { self.base.transform.position -= world_up * speed; }
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
