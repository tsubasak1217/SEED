// ============================================================
//  camera_ops.rs — カメラ状態の取得・適用
//
//  cam_state_tuple / apply_camera_transform / apply_camera_data
//  sync_debug_camera_to_main_camera
// ============================================================

use crate::engine::core::app_base::scene::DebugCameraData;
use crate::engine::structs::tensor::Vector3;
use crate::engine::structs::transforms::Quaternion;

use super::App;

impl App {
    /// カメラ状態をタプル文字列形式で返す（IPC 送信用）。
    ///
    /// 戻り値: (pos_str, yaw_deg_str, pitch_deg_str, fov_deg_str, far_str, speed_str)
    pub(super) fn cam_state_tuple(&self) -> (String, String, String, String, String, String) {
        let pos   = self.camera.base.transform.position;
        let yaw   = self.camera.yaw.to_degrees();
        let pitch = self.camera.pitch.to_degrees();
        let fov   = self.camera.base.projection.fov_y_rad.to_degrees();
        let far   = self.camera.base.projection.far;
        let spd   = self.camera.move_speed;
        (
            format!("{},{},{}", pos.x, pos.y, pos.z),
            format!("{yaw}"),
            format!("{pitch}"),
            format!("{fov}"),
            format!("{far}"),
            format!("{spd}"),
        )
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
