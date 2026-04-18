use crate::engine::structs::tensor::Vector3;

/// 3D 空間上の平面。
///
/// 平面の方程式: `normal · point + distance = 0`
///
/// # フィールド
/// - `normal`  : 平面の法線ベクトル（正規化済みを推奨）
/// - `distance`: 原点から平面までの符号付き距離
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Plane {
    pub normal: Vector3<f32>,
    pub distance: f32,
}

impl Plane {
    /// 法線を正規化して平面を生成する。
    pub fn new(normal: Vector3<f32>, distance: f32) -> Self {
        Self {
            normal: normal.normalize(),
            distance,
        }
    }

    /// 平面上の点と法線から生成する。
    pub fn from_point_normal(point: Vector3<f32>, normal: Vector3<f32>) -> Self {
        let n = normal.normalize();
        Self {
            normal: n,
            distance: -n.dot(point),
        }
    }

    /// 点から平面への符号付き距離を返す。
    /// 正値 = 法線側、負値 = 反法線側。
    pub fn signed_distance(self, point: Vector3<f32>) -> f32 {
        self.normal.dot(point) + self.distance
    }

    /// 点が法線の向いている側（正側）にあるか判定する。
    pub fn is_front_side(self, point: Vector3<f32>) -> bool {
        self.signed_distance(point) > 0.0
    }
}
