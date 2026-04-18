use crate::engine::structs::tensor::Vector3;

/// 3D 球。
///
/// # フィールド
/// - `center`: 中心座標
/// - `radius`: 半径（正の値を想定）
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Sphere {
    pub center: Vector3<f32>,
    pub radius: f32,
}

impl Sphere {
    pub fn new(center: Vector3<f32>, radius: f32) -> Self {
        Self { center, radius }
    }

    /// 体積を返す。
    pub fn volume(self) -> f32 {
        (4.0 / 3.0) * std::f32::consts::PI * self.radius.powi(3)
    }

    /// 表面積を返す。
    pub fn surface_area(self) -> f32 {
        4.0 * std::f32::consts::PI * self.radius * self.radius
    }

    /// 点が球の内側（境界含む）に含まれるか判定する。
    pub fn contains(self, point: Vector3<f32>) -> bool {
        (point - self.center).length_sq() <= self.radius * self.radius
    }

    /// 別の球と重なっているか判定する。
    pub fn intersects(self, other: Self) -> bool {
        let r_sum = self.radius + other.radius;
        (self.center - other.center).length_sq() <= r_sum * r_sum
    }
}
