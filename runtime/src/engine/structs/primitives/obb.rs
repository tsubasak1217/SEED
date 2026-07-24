use crate::engine::structs::tensor::{Mat3x3, Vector3};

/// 向きを持つバウンディングボックス (Oriented Bounding Box)。
///
/// AABB と異なり、任意の回転を持つことができます。
///
/// # フィールド
/// - `center`    : ボックスの中心座標
/// - `half_size` : ボックスの各軸での半サイズ（ローカル空間）
/// - `axes`      : 3 本の軸ベクトル（ワールド空間、正規化済みを推奨）
///   - `axes[0]` : ローカル X 軸
///   - `axes[1]` : ローカル Y 軸
///   - `axes[2]` : ローカル Z 軸
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Obb {
    pub center: Vector3<f32>,
    pub half_size: Vector3<f32>,
    pub axes: [Vector3<f32>; 3],
}

impl Obb {
    /// 中心、半サイズ、回転行列から OBB を生成する。
    pub fn new(center: Vector3<f32>, half_size: Vector3<f32>, rotation: Mat3x3<f32>) -> Self {
        Self {
            center,
            half_size,
            axes: [
                Vector3::new(
                    rotation.data[0][0],
                    rotation.data[1][0],
                    rotation.data[2][0],
                )
                .normalize(),
                Vector3::new(
                    rotation.data[0][1],
                    rotation.data[1][1],
                    rotation.data[2][1],
                )
                .normalize(),
                Vector3::new(
                    rotation.data[0][2],
                    rotation.data[1][2],
                    rotation.data[2][2],
                )
                .normalize(),
            ],
        }
    }

    /// 中心と軸から OBB を生成する（軸は正規化されると仮定）。
    pub fn from_axes(
        center: Vector3<f32>,
        half_size: Vector3<f32>,
        axes: [Vector3<f32>; 3],
    ) -> Self {
        Self {
            center,
            half_size,
            axes,
        }
    }

    /// OBB の 8 つの頂点を返す。
    pub fn corners(self) -> [Vector3<f32>; 8] {
        let sx = self.axes[0] * self.half_size.x;
        let sy = self.axes[1] * self.half_size.y;
        let sz = self.axes[2] * self.half_size.z;

        [
            self.center - sx - sy - sz,
            self.center + sx - sy - sz,
            self.center + sx + sy - sz,
            self.center - sx + sy - sz,
            self.center - sx - sy + sz,
            self.center + sx - sy + sz,
            self.center + sx + sy + sz,
            self.center - sx + sy + sz,
        ]
    }

    /// 点が OBB 内に含まれるか判定する。
    pub fn contains(self, point: Vector3<f32>) -> bool {
        let p = point - self.center;

        let dx = (p.dot(self.axes[0])).abs();
        let dy = (p.dot(self.axes[1])).abs();
        let dz = (p.dot(self.axes[2])).abs();

        dx <= self.half_size.x && dy <= self.half_size.y && dz <= self.half_size.z
    }

    /// AABB に変換する（OBB を含む最小の AABB）。
    pub fn to_aabb(self) -> super::aabb::Aabb {
        let corners = self.corners();

        let mut min = corners[0];
        let mut max = corners[0];

        for corner in &corners[1..] {
            min = Vector3::new(
                min.x.min(corner.x),
                min.y.min(corner.y),
                min.z.min(corner.z),
            );
            max = Vector3::new(
                max.x.max(corner.x),
                max.y.max(corner.y),
                max.z.max(corner.z),
            );
        }

        super::aabb::Aabb::new(min, max)
    }

    /// 別の OBB と重なっているか判定する（分離軸定理を使用）。
    pub fn intersects(self, other: Self) -> bool {
        // 分離軸定理: 各軸の投影で重ならない軸があれば非交差
        let axes = [
            self.axes[0],
            self.axes[1],
            self.axes[2],
            other.axes[0],
            other.axes[1],
            other.axes[2],
        ];

        for axis in &axes {
            let proj_self = self.project_on_axis(*axis);
            let proj_other = other.project_on_axis(*axis);

            if proj_self.1 < proj_other.0 || proj_other.1 < proj_self.0 {
                return false;
            }
        }
        true
    }

    /// OBB の軸への投影範囲 (min, max) を返す。
    fn project_on_axis(self, axis: Vector3<f32>) -> (f32, f32) {
        let center_proj = self.center.dot(axis);
        let sx = (self.axes[0] * self.half_size.x).dot(axis).abs();
        let sy = (self.axes[1] * self.half_size.y).dot(axis).abs();
        let sz = (self.axes[2] * self.half_size.z).dot(axis).abs();
        let r = sx + sy + sz;

        (center_proj - r, center_proj + r)
    }
}
