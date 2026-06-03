// ============================================================
//  camera_ops.rs — カメラ状態の取得・適用
//
//  cam_state_tuple / apply_camera_transform / apply_camera_data
//  sync_debug_camera_to_main_camera
// ============================================================

use crate::engine::core::app_base::scene::DebugCameraData;
use crate::engine::structs::tensor::Vector3;
use crate::engine::structs::transforms::Quaternion;
use super::CameraSnapAnim;

use super::App;

impl App {
    /// カメラ状態をタプル文字列形式で返す（IPC 送信用）。
    ///
    /// 戻り値: (pos_str, euler_x_deg_str, euler_y_deg_str, euler_z_deg_str, fov_deg_str, far_str, speed_str)
    /// Euler XYZ は YXZ 合成順（Y=Yaw, X=Pitch, Z=Roll）。DebugCamera は Roll なしのため Z=0。
    pub(super) fn cam_state_tuple(&self) -> (String, String, String, String, String, String, String) {
        let pos   = self.camera.base.transform.position;
        let fov   = self.camera.base.projection.fov_y_rad.to_degrees();
        let far   = self.camera.base.projection.far;
        let spd   = self.camera.move_speed;

        // DebugCamera は Roll なしのため yaw/pitch から直接 Euler X/Y/Z を生成する
        let euler_x = self.camera.pitch.to_degrees(); // Pitch = X 軸回転
        let euler_y = self.camera.yaw.to_degrees();   // Yaw   = Y 軸回転
        let euler_z = 0.0_f32;                         // Roll  = Z 軸回転（常に 0）
        (
            format!("{},{},{}", pos.x, pos.y, pos.z),
            format!("{euler_x}"),
            format!("{euler_y}"),
            format!("{euler_z}"),
            format!("{fov}"),
            format!("{far}"),
            format!("{spd}"),
        )
    }

    /// Euler XYZ（度、YXZ 合成順）からカメラ位置・回転を設定する。
    ///
    /// R = Ry(euler_y) * Rx(euler_x) * Rz(euler_z) でQuaternionを構築する。
    /// yaw/pitch は forward ベクトルから逆算して RMB ドラッグとの整合を保つ。
    pub(super) fn apply_camera_euler(
        &mut self,
        px: f32, py: f32, pz: f32,
        euler_x_deg: f32, euler_y_deg: f32, euler_z_deg: f32,
    ) {
        let ex = euler_x_deg.to_radians();
        let ey = euler_y_deg.to_radians();
        let ez = euler_z_deg.to_radians();

        // YXZ 合成順: Yaw(Y) → Pitch(X) → Roll(Z)
        let qy = Quaternion::from_axis_angle(Vector3::new(0.0, 1.0, 0.0), ey);
        let qx = Quaternion::from_axis_angle(Vector3::new(1.0, 0.0, 0.0), ex);
        let qz = Quaternion::from_axis_angle(Vector3::new(0.0, 0.0, 1.0), ez);
        let rot = qy * qx * qz;

        self.camera.base.transform.position = Vector3::new(px, py, pz);
        self.camera.base.transform.rotation = rot;

        // forward から yaw/pitch を逆算して RMB ドラッグとの整合を保つ
        let fwd = rot.forward();
        self.camera.pitch = (-fwd.y).clamp(-1.0, 1.0).asin();
        self.camera.yaw   = fwd.x.atan2(fwd.z);
    }

    /// カメラのトランスフォームを位置・yaw/pitch（度）から設定する。
    pub(super) fn apply_camera_transform(&mut self, px: f32, py: f32, pz: f32, yaw_deg: f32, pitch_deg: f32) {
        // pitch を ±(π/2 - 0.02) rad にクランプしてジンバルロックを防ぐ
        const PITCH_LIMIT: f32 = std::f32::consts::FRAC_PI_2 - 0.02;
        self.camera.base.transform.position = Vector3::new(px, py, pz);
        self.camera.yaw   = yaw_deg.to_radians();
        self.camera.pitch = pitch_deg.to_radians().clamp(-PITCH_LIMIT, PITCH_LIMIT);
        let yaw_q   = Quaternion::from_axis_angle(Vector3::new(0.0, 1.0, 0.0), self.camera.yaw);
        let pitch_q = Quaternion::from_axis_angle(Vector3::new(1.0, 0.0, 0.0), self.camera.pitch);
        self.camera.base.transform.rotation = yaw_q * pitch_q;
    }

    /// Pause 時にデバッグカメラをシーンのメインカメラ視点に同期する。
    ///
    /// `is_main = true` の CameraComponent を持つ Actor を DFS で探し、
    /// その位置と向きをデバッグカメラに引き継ぐ。
    /// メインカメラが存在しない場合は何もしない。
    ///
    /// # 向きの変換
    /// `components::Transform` の YXZ オイラー角から forward ベクトルを算出し、
    /// DebugCamera の yaw / pitch（ラジアン）へ変換する。
    ///   forward = (sy·cx, −sx, cy·cx)  [sy=sin(Ey), cx=cos(Ex) など]
    ///   pitch   = asin(−fy)
    ///   yaw     = atan2(fx, fz)
    pub(super) fn sync_debug_camera_to_main_camera(&mut self) {
        let cam_tf = self.scene.as_ref()
            .and_then(|s| s.find_main_camera())
            .map(|(tf, _)| tf);

        if let Some(tf) = cam_tf {
            let [px, py, pz] = tf.position;
            let [fx, fy, fz] = tf.forward();
            // pitch = asin(−fy), yaw = atan2(fx, fz)
            let pitch_deg = (-fy).clamp(-1.0, 1.0).asin().to_degrees();
            let yaw_deg   = fx.atan2(fz).to_degrees();
            self.apply_camera_transform(px, py, pz, yaw_deg, pitch_deg);
        }
    }

    /// 軸ギズモドットのクリックに応じてカメラ回転スナップアニメーションを開始する。
    ///
    /// カメラ位置は変えず yaw / pitch のみ Slerp で補間する。
    /// - +X → forward = +X (yaw=90°)
    /// - -X → forward = -X (yaw=-90°)
    /// - +Y → forward = +Y (pitch=89°, ほぼ真上)
    /// - -Y → forward = -Y (pitch=-89°, ほぼ真下)
    /// - +Z → forward = +Z (yaw=0°)
    /// - -Z → forward = -Z (yaw=180°)
    pub(super) fn snap_camera_to_axis(
        &mut self,
        hit: crate::engine::core::font::axis_gizmo::AxisHit,
    ) {
        const SNAP_DURATION: f32 = 0.28;
        const PITCH_LIMIT:   f32 = std::f32::consts::FRAC_PI_2 - 0.02;

        // Y軸: 現在 yaw を最近傍 90° にスナップして XZ 平面を揃える
        let snapped_yaw = snap_to_nearest_90(self.camera.yaw);

        let (target_yaw_rad, target_pitch_rad): (f32, f32) = match (hit.axis, hit.pos) {
            (0, true)  => ((-90.0_f32).to_radians(),  0.0),          // +X 側から
            (0, false) => (( 90.0_f32).to_radians(),  0.0),          // -X 側から
            (1, true)  => (snapped_yaw,                89.0_f32.to_radians()),  // 上から
            (1, false) => (snapped_yaw,               -89.0_f32.to_radians()),  // 下から
            (2, true)  => ((180.0_f32).to_radians(),   0.0),          // +Z 側から
            (2, false) => (0.0_f32,                    0.0),          // -Z 側から
            _          => return,
        };
        let target_pitch_rad = target_pitch_rad.clamp(-PITCH_LIMIT, PITCH_LIMIT);

        // 目標 Quaternion を Yaw/Pitch から正確に構築する
        let yq = Quaternion::from_axis_angle(Vector3::new(0.0, 1.0, 0.0), target_yaw_rad);
        let pq = Quaternion::from_axis_angle(Vector3::new(1.0, 0.0, 0.0), target_pitch_rad);
        let target_rot = yq * pq;

        self.camera_snap_anim = Some(CameraSnapAnim {
            start_rot:    self.camera.base.transform.rotation,
            target_rot,
            target_yaw:   target_yaw_rad,
            target_pitch: target_pitch_rad,
            duration: SNAP_DURATION,
            elapsed:  0.0,
        });
    }

    /// 毎フレーム呼び出し。Quaternion Slerp でスナップアニメーションを進行させる。
    ///
    /// RMB / 移動キーが押されたらキャンセルして通常操作を優先する。
    /// 完了時は target_rot と target_yaw/pitch を直接代入して誤差をゼロにする。
    pub(super) fn update_camera_snap_anim(&mut self, delta_time: f32) {
        if self.cam_input.rmb || self.cam_input.any_move_key() {
            self.camera_snap_anim = None;
            return;
        }

        // 借用スコープを限定して必要な値だけ取り出す
        let (rot, target_yaw, target_pitch, done) = {
            let Some(anim) = &mut self.camera_snap_anim else { return };
            anim.elapsed += delta_time;
            let t        = (anim.elapsed / anim.duration).min(1.0);
            let done     = t >= 1.0;
            // smoothstep でイーズイン・アウト
            let t_smooth = t * t * (3.0 - 2.0 * t);
            // 完了時は Slerp を通さず target_rot をそのまま使用（誤差排除）
            let rot = if done { anim.target_rot } else { anim.start_rot.slerp(anim.target_rot, t_smooth) };
            (rot, anim.target_yaw, anim.target_pitch, done)
        }; // anim の借用終了

        if done { self.camera_snap_anim = None; }

        // カメラ回転のみ更新（位置は変えない）
        self.camera.base.transform.rotation = rot;

        if done {
            // 完了時は正確な target 値を直接代入
            self.camera.yaw   = target_yaw;
            self.camera.pitch = target_pitch;
        } else {
            // 補間中は Quaternion の forward から yaw/pitch を逆算して整合を保つ
            let fwd = rot.forward();
            self.camera.pitch = (-fwd.y).clamp(-1.0, 1.0).asin();
            self.camera.yaw   = fwd.x.atan2(fwd.z);
        }
    }

    /// DebugCameraData をカメラに一括適用する。
    pub(super) fn apply_camera_data(&mut self, cam: &DebugCameraData) {
        const PITCH_LIMIT: f32 = std::f32::consts::FRAC_PI_2 - 0.02;
        self.camera.base.transform.position = Vector3::new(cam.position[0], cam.position[1], cam.position[2]);
        self.camera.yaw   = cam.yaw;
        self.camera.pitch = cam.pitch.clamp(-PITCH_LIMIT, PITCH_LIMIT);
        let yaw_q   = Quaternion::from_axis_angle(Vector3::new(0.0, 1.0, 0.0), self.camera.yaw);
        let pitch_q = Quaternion::from_axis_angle(Vector3::new(1.0, 0.0, 0.0), self.camera.pitch);
        self.camera.base.transform.rotation = yaw_q * pitch_q;
        self.camera.base.projection.fov_y_rad = cam.fov_deg.to_radians();
        self.camera.base.projection.far = cam.far;
        self.camera.move_speed = cam.speed;
    }
}

/// yaw（ラジアン）を最も近い 90° 単位（π/2 の整数倍）へスナップする。
///
/// Y 軸ビュー時に XZ 平面が画面の軸と揃うようにするために使用する。
#[inline]
fn snap_to_nearest_90(yaw: f32) -> f32 {
    use std::f32::consts::FRAC_PI_2;
    (yaw / FRAC_PI_2).round() * FRAC_PI_2
}
