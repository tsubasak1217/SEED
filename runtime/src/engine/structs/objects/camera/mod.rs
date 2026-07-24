pub mod base_camera;
pub mod debug_camera;

pub use base_camera::{BaseCamera, CameraProjection};
pub use debug_camera::DebugCamera;

use crate::engine::structs::tensor::Mat4x4;
use crate::engine::structs::transforms::Transform;

/// カメラの共通インターフェース。
///
/// `BaseCamera` と `DebugCamera` の両方が実装する。
/// レンダラーはこのトレイトを通じてビュー行列・射影行列を取得できる。
pub trait Camera {
    fn view_matrix(&self) -> Mat4x4<f32>;
    fn projection_matrix(&self) -> Mat4x4<f32>;
    fn transform(&self) -> &Transform;
    fn transform_mut(&mut self) -> &mut Transform;
}

impl Camera for BaseCamera {
    fn view_matrix(&self) -> Mat4x4<f32> {
        self.view_matrix()
    }
    fn projection_matrix(&self) -> Mat4x4<f32> {
        self.projection_matrix()
    }
    fn transform(&self) -> &Transform {
        &self.transform
    }
    fn transform_mut(&mut self) -> &mut Transform {
        &mut self.transform
    }
}

impl Camera for DebugCamera {
    fn view_matrix(&self) -> Mat4x4<f32> {
        self.view_matrix()
    }
    fn projection_matrix(&self) -> Mat4x4<f32> {
        self.projection_matrix()
    }
    fn transform(&self) -> &Transform {
        &self.base.transform
    }
    fn transform_mut(&mut self) -> &mut Transform {
        &mut self.base.transform
    }
}
