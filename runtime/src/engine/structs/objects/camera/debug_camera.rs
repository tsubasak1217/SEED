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
    pub mmb:   bool,       // 中ボタン押し込み（winit MouseInput）
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

    /// 全キー状態を強制リセットする（CAM_KEYS_CLEAR IPC 用）。
    ///
    /// エディタの Play 切替などでキー UP がこのランタイムへ届かないと、
    /// 移動キーがスタックして「RMB 押下だけでカメラが移動する」
    /// 「軸スナップが any_move_key() により即キャンセルされる」問題になる。
    pub fn clear_keys(&mut self) {
        self.w = false; self.a = false; self.s = false; self.d = false;
        self.q = false; self.e = false; self.shift = false;
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

    /// 投影ブレンド係数（0.0 = 透視投影, 1.0 = 正射投影）。
    /// `ortho_target` へ 0.3 秒かけて補間され、射影行列は両者を線形補間する。
    pub ortho_blend:  f32,
    /// 投影ブレンドの目標値（0.0 or 1.0）。トグルで切り替える。
    pub ortho_target: f32,
    /// 正射投影時の縦方向の描画範囲（ワールド単位・半分の高さ）。
    /// 正射化時に現在の透視スケール（fov × 焦点距離）から算出し、視点の見た目を維持する。
    pub ortho_half_h: f32,

    /// ビューポートの高さ（物理ピクセル）。
    /// MMB パンの「1ピクセル = 何ワールドユニットか」の換算に使う。
    /// `set_aspect_ratio`（リサイズ時）で更新される。
    pub viewport_h_px: f32,
}

/// 投影切り替えの補間時間（秒）。0.3 秒で透視↔正射をなめらかに補間する。
pub const CAMERA_ORTHO_ANIM_SECONDS: f32 = 0.3;

/// 2 つの 4x4 行列を要素ごとに線形補間する（t=0 で a, t=1 で b）。
///
/// 投影切替アニメーションで透視射影行列と正射射影行列を混ぜるために使う。
/// 幾何的に厳密な補間ではないが、near/far が共通なら視覚的に十分なめらかに遷移する。
fn lerp_mat4(a: &Mat4x4<f32>, b: &Mat4x4<f32>, t: f32) -> Mat4x4<f32> {
    let mut out = *a;
    for r in 0..4 {
        for c in 0..4 {
            out.data[r][c] = a.data[r][c] + (b.data[r][c] - a.data[r][c]) * t;
        }
    }
    out
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
            ortho_blend:  0.0,
            ortho_target: 0.0,
            ortho_half_h: 5.0,
            viewport_h_px: 720.0,
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
        // 2D（正射投影）ビュー中は RMB による視点回転を無効にする。
        // （右上の軸ギズモクリックによる軸スナップは別経路のため有効なまま）
        if self.ortho_target >= 0.5 { return; }
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

        // 正射投影モード: スクロールで正射サイズ（描画範囲）をズームする。
        // 正射は距離に依らず一定のため前後移動では拡縮しない。
        if self.ortho_target >= 0.5 {
            self.ortho_half_h = (self.ortho_half_h * 0.9_f32.powf(cam.scroll)).clamp(0.05, 100_000.0);
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

    /// 中ボタン押し込み中のカメラ平面パンを処理する（Unity / Blender 方式）。
    ///
    /// マウスの raw delta をそのままワールド移動量に換算し、カメラの right / up
    /// 方向（カメラから見た XY 平面）にのみ移動する。奥（forward）には移動しない。
    /// 換算は「カーソル下のオブジェクトがカーソルに追従して見える」スケール:
    /// - 正射投影: 1px = 2 * ortho_half_h / viewport_h（正確に 1:1）
    /// - 透視投影: 焦点距離（原点までの距離）における 1px 相当のワールド長
    fn update_mmb_pan(&mut self, cam: &CameraInput, _delta_time: f32) {
        if !cam.mmb { return; }
        if cam.mouse_dx == 0.0 && cam.mouse_dy == 0.0 { return; }

        // 1 スクリーンピクセルあたりのワールドユニット数
        let vp_h = self.viewport_h_px.max(1.0);
        let world_per_px = if self.ortho_target >= 0.5 {
            // 正射: 描画範囲（全高 2 * half_h）をビューポート高で割る
            (2.0 * self.ortho_half_h) / vp_h
        } else {
            // 透視: 焦点面（原点までの距離）での可視高をビューポート高で割る
            let focal = self.base.transform.position.length().max(1.0);
            (2.0 * (self.base.projection.fov_y_rad * 0.5).tan() * focal) / vp_h
        };

        // ドラッグ方向へシーンが追従する（= カメラは逆方向へ動く）
        // スクリーン +X（右ドラッグ）→ カメラを左へ / +Y（下ドラッグ・Y-down）→ カメラを上へ
        let right = self.base.transform.right();
        let up    = self.base.transform.up();
        self.base.transform.position -= right * cam.mouse_dx * world_per_px;
        self.base.transform.position += up    * cam.mouse_dy * world_per_px;
    }

    // ─── BaseCamera への委譲 ──────────────────────────────────

    /// ビュー行列を返す（BaseCamera に委譲）。
    #[inline]
    pub fn view_matrix(&self) -> Mat4x4<f32> { self.base.view_matrix() }

    /// 射影行列を返す。
    ///
    /// `ortho_blend` に応じて透視射影と正射射影を線形補間する（0=透視, 1=正射）。
    /// 補間中は両行列の要素を線形にブレンドすることで、投影切替をなめらかに見せる。
    pub fn projection_matrix(&self) -> Mat4x4<f32> {
        let persp = self.base.projection_matrix();
        if self.ortho_blend <= 0.0 { return persp; }

        let p      = &self.base.projection;
        let half_h = self.ortho_half_h.max(0.01);
        let half_w = half_h * p.aspect_ratio;
        let ortho  = Mat4x4::orthographic_lh(-half_w, half_w, -half_h, half_h, p.near, p.far);
        if self.ortho_blend >= 1.0 { return ortho; }

        lerp_mat4(&persp, &ortho, self.ortho_blend)
    }

    /// 現在（目標）が正射投影モードか。
    #[inline]
    pub fn is_ortho(&self) -> bool { self.ortho_target >= 0.5 }

    /// 投影方式を設定する（true = 正射, false = 透視）。0.3 秒かけて補間される。
    ///
    /// 正射化時は現在の透視スケール（fov × 焦点距離＝原点までの距離）から
    /// `ortho_half_h` を算出し、切替前後で見た目の大きさを維持する（視点は変えない）。
    pub fn set_ortho(&mut self, on: bool) {
        let target = if on { 1.0 } else { 0.0 };
        if (self.ortho_target - target).abs() < f32::EPSILON { return; }
        if on {
            let focal = self.base.transform.position.length().max(1.0);
            self.ortho_half_h = (self.base.projection.fov_y_rad * 0.5).tan() * focal;
        }
        self.ortho_target = target;
    }

    /// 投影方式を透視↔正射でトグルする。
    #[inline]
    pub fn toggle_ortho(&mut self) { self.set_ortho(self.ortho_target < 0.5); }

    /// 投影ブレンドを目標値へ 0.3 秒で補間する。フレームループで毎フレーム呼ぶ。
    pub fn update_projection_anim(&mut self, dt: f32) {
        if (self.ortho_blend - self.ortho_target).abs() < 1e-4 {
            self.ortho_blend = self.ortho_target;
            return;
        }
        let step = dt / CAMERA_ORTHO_ANIM_SECONDS;
        if self.ortho_blend < self.ortho_target {
            self.ortho_blend = (self.ortho_blend + step).min(self.ortho_target);
        } else {
            self.ortho_blend = (self.ortho_blend - step).max(self.ortho_target);
        }
    }

    /// アスペクト比を更新する（ウィンドウリサイズ時に呼ぶ）。
    /// MMB パンのピクセル→ワールド換算用にビューポート高も記録する。
    #[inline]
    pub fn set_aspect_ratio(&mut self, width: u32, height: u32) {
        self.base.set_aspect_ratio(width, height);
        if height > 0 { self.viewport_h_px = height as f32; }
    }

    /// カメラの現在位置を返す。
    #[inline]
    pub fn position(&self) -> Vector3<f32> { self.base.transform.position }
}
