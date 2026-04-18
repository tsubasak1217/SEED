use crate::engine::structs::tensor::Vector3;

/// 3D 空間上の半直線（レイ）。
///
/// # フィールド
/// - `origin`   : 始点
/// - `direction`: 方向（正規化済みを推奨）
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Ray {
    pub origin: Vector3<f32>,
    pub direction: Vector3<f32>,
}

impl Ray {
    /// 方向ベクトルを正規化してレイを生成する。
    pub fn new(origin: Vector3<f32>, direction: Vector3<f32>) -> Self {
        Self {
            origin,
            direction: direction.normalize(),
        }
    }

    /// `origin + direction * t` の位置を返す。
    pub fn at(self, t: f32) -> Vector3<f32> {
        self.origin + self.direction * t
    }
}
