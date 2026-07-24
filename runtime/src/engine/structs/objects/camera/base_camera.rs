use crate::engine::structs::tensor::Mat4x4;
use crate::engine::structs::transforms::Transform;
use std::f32::consts::FRAC_PI_4;

/// カメラの投影パラメータ。
#[derive(Debug, Clone, Copy)]
pub struct CameraProjection {
    /// 垂直視野角（ラジアン）
    pub fov_y_rad: f32,
    /// アスペクト比（width / height）
    pub aspect_ratio: f32,
    /// ニアクリップ距離
    pub near: f32,
    /// ファークリップ距離
    pub far: f32,
}

impl Default for CameraProjection {
    fn default() -> Self {
        Self {
            fov_y_rad: FRAC_PI_4, // 45°
            aspect_ratio: 16.0 / 9.0,
            near: 0.1,
            far: 1000.0,
        }
    }
}

/// 固定カメラ。位置・回転・投影パラメータを保持し、ビュー行列と射影行列を提供する。
///
/// 自身は自律的な移動を行わない。外部から `transform` を直接操作して使う。
/// デバッグカメラなど動きのあるカメラは `DebugCamera` のように本構造体を内包して実装する。
///
/// # 座標系
/// 左手座標系（LH）準拠。`forward = +Z`。
#[derive(Debug, Clone)]
pub struct BaseCamera {
    pub transform: Transform,
    pub projection: CameraProjection,
}

impl BaseCamera {
    pub fn new(transform: Transform, projection: CameraProjection) -> Self {
        Self {
            transform,
            projection,
        }
    }

    /// デフォルト設定（原点、無回転、FOV 45°, 16:9, near 0.1, far 1000）。
    pub fn default() -> Self {
        Self {
            transform: Transform::identity(),
            projection: CameraProjection::default(),
        }
    }

    // ─── 行列取得 ─────────────────────────────────────────────

    /// ビュー行列（ワールド空間 → カメラ空間）を返す。
    ///
    /// カメラの `transform` から forward / up を求め、`look_at_lh` で構築する。
    pub fn view_matrix(&self) -> Mat4x4<f32> {
        let pos = self.transform.position;
        let forward = self.transform.forward();
        let up = self.transform.up();
        Mat4x4::look_at_lh(pos, pos + forward, up)
    }

    /// 透視射影行列を返す（LH, depth Z∈[0,1]）。
    pub fn projection_matrix(&self) -> Mat4x4<f32> {
        let p = &self.projection;
        Mat4x4::perspective_lh(p.fov_y_rad, p.aspect_ratio, p.near, p.far)
    }

    /// ウィンドウリサイズ時にアスペクト比を更新する。
    pub fn set_aspect_ratio(&mut self, width: u32, height: u32) {
        if height > 0 {
            self.projection.aspect_ratio = width as f32 / height as f32;
        }
    }
}
