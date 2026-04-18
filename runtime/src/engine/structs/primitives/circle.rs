use crate::engine::structs::tensor::Vector2;

/// 2D 円。
///
/// # フィールド
/// - `center`: 中心座標
/// - `radius`: 半径（正の値を想定）
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Circle {
    pub center: Vector2<f32>,
    pub radius: f32,
}

impl Circle {
    pub fn new(center: Vector2<f32>, radius: f32) -> Self {
        Self { center, radius }
    }

    /// 面積を返す。
    pub fn area(self) -> f32 {
        std::f32::consts::PI * self.radius * self.radius
    }

    /// 周囲の長さを返す。
    pub fn circumference(self) -> f32 {
        2.0 * std::f32::consts::PI * self.radius
    }

    /// 点が円の内側（境界含む）に含まれるか判定する。
    pub fn contains(self, point: Vector2<f32>) -> bool {
        (point - self.center).length_sq() <= self.radius * self.radius
    }

    /// 別の円と重なっているか判定する。
    pub fn intersects(self, other: Self) -> bool {
        let r_sum = self.radius + other.radius;
        (self.center - other.center).length_sq() <= r_sum * r_sum
    }
}
