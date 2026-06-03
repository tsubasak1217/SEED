use std::f32::consts::{FRAC_PI_2, FRAC_PI_4};

/// MMB スティック HUD の外円半径（ビューポートピクセル）。
/// カーソルのクランプ距離と速度の最大値を兼ねる。
pub const MMB_OUTER_RADIUS: f32 = 100.0;

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
    pub mmb:   bool,       // 中ボタン押し込み（winit MouseInput）
    pub mouse_dx: f32,     // フレーム内累積（winit DeviceEvent）
    pub mouse_dy: f32,
    pub scroll:   f32,     // フレーム内累積（winit MouseWheel）
    /// 現在フレームのカーソル位置（ビューポートローカルピクセル）。毎フレーム更新される。
    pub cursor_x: f32,
    pub cursor_y: f32,
    /// MMB 押し込み時の起点カーソル位置。押下時に一度だけ記録する。
    pub mmb_origin_x: f32,
    pub mmb_origin_y: f32,
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

    /// WASDQE の移動キーがいずれか1つでも押されているか判定する。
    #[inline]
    pub fn any_move_key(&self) -> bool {
        self.w || self.a || self.s || self.d || self.q || self.e
    }

    /// フレーム末にデルタ・スクロールをリセットする。キー状態・MMB 状態は保持。
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
        self.update_speed_or_scroll_move(cam);
        self.update_movement(cam, delta_time);
        self.update_mmb_pan(cam, delta_time);
    }

    /// マウスの raw delta でヨー・ピッチを更新し、`transform.rotation` に反映する。
    /// 右クリック中のみ回転する。MMB 押し込み中は無効。
    fn update_rotation(&mut self, cam: &CameraInput) {
        // MMB 押し込み中は視点回転を無効にする（パン操作に専念させる）
        if !cam.rmb || cam.mmb { return; }
        self.yaw   += cam.mouse_dx * self.mouse_sensitivity;
        self.pitch += cam.mouse_dy * self.mouse_sensitivity;

        const PITCH_LIMIT: f32 = FRAC_PI_2 - 0.02;
        self.pitch = self.pitch.clamp(-PITCH_LIMIT, PITCH_LIMIT);

        let yaw_q   = Quaternion::from_axis_angle(Vector3::new(0.0, 1.0, 0.0), self.yaw);
        let pitch_q = Quaternion::from_axis_angle(Vector3::new(1.0, 0.0, 0.0), self.pitch);
        self.base.transform.rotation = yaw_q * pitch_q;
    }

    /// ホイールスクロール処理。
    ///
    /// - 右クリック中 or キーボード移動中: スクロールで移動速度を調整する
    /// - それ以外: スクロールで現在の視点方向に前後移動する
    fn update_speed_or_scroll_move(&mut self, cam: &CameraInput) {
        if cam.scroll == 0.0 { return; }

        // 右クリック中か移動キー押下中は速度調整（既存挙動）
        if cam.rmb || cam.any_move_key() {
            self.move_speed = (self.move_speed * 1.2_f32.powf(cam.scroll)).clamp(0.5, 500.0);
            return;
        }

        // それ以外: 視点方向への前後移動（ホイール1ノッチで move_speed * 0.5 ユニット移動）
        let forward = self.base.transform.forward();
        let dist    = self.move_speed * cam.scroll * 0.5;
        self.base.transform.position += forward * dist;
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

    /// 中ボタン押し込み中のパン / 前後移動を処理する。
    ///
    /// 起点（MMB 押し込み位置）から現在のカーソル位置への差分を毎フレームの速度として使う。
    /// マウスを動かしていなくても、起点からずれている限り移動し続ける。
    ///
    /// # モード
    /// - MMB 単押し: 起点からの差分でカメラ平面に平行なパン
    ///   - 水平オフセット (+X) → 右方向移動
    ///   - 垂直オフセット (+Y) → 下方向移動（スクリーン Y は下正）
    /// - MMB + RMB 同時: 起点からの差分で前後 / 左右移動
    ///   - 水平オフセット (+X) → 右方向パン
    ///   - 垂直オフセット (+Y) → 後退（下に引っ張ると後退、上に引っ張ると前進）
    fn update_mmb_pan(&mut self, cam: &CameraInput, delta_time: f32) {
        if !cam.mmb { return; }

        // 起点から現在カーソル位置への差分（ピクセル）
        let offset_x = cam.cursor_x - cam.mmb_origin_x;
        let offset_y = cam.cursor_y - cam.mmb_origin_y;

        // 起点からの距離が極小なら動かさない
        let dist = (offset_x * offset_x + offset_y * offset_y).sqrt();
        if dist < 0.5 { return; }

        // 正規化した距離 t (0〜1) に二乗カーブを適用する。
        // t^2 により低速レンジが広く、端に近いほど急激に加速する。
        let t = (dist / MMB_OUTER_RADIUS).min(1.0);
        let t_curved = t * t;

        // 最大速度係数（外円端で move_speed * delta_time * この値 だけ移動）
        const MAX_PAN_SCALE: f32 = 1.5;
        let max_speed  = self.move_speed * delta_time * MAX_PAN_SCALE;
        // 方向を offset に合わせつつ大きさを非線形にスケールする
        let actual_spd = t_curved * max_speed / dist;

        let right = self.base.transform.right();

        if cam.rmb {
            // MMB + RMB: 水平 → 左右パン、垂直 → 上下パン
            let up = self.base.transform.up();
            self.base.transform.position += right * offset_x * actual_spd;
            self.base.transform.position -= up    * offset_y * actual_spd;
        } else {
            // MMB 単押し: 水平 → 左右パン、垂直 → 前後移動
            let forward = self.base.transform.forward();
            self.base.transform.position += right   * offset_x * actual_spd;
            self.base.transform.position -= forward * offset_y * actual_spd;
        }
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
