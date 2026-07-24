use crate::engine::structs::tensor::Vector3;

/// 軸平行バウンディングボックス (Axis-Aligned Bounding Box)。
///
/// # フィールド
/// - `min`: 各軸の最小座標（左下奥）
/// - `max`: 各軸の最大座標（右上前）
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Aabb {
    pub min: Vector3<f32>,
    pub max: Vector3<f32>,
}

impl Aabb {
    pub fn new(min: Vector3<f32>, max: Vector3<f32>) -> Self {
        Self { min, max }
    }

    /// 中心座標と各軸の半サイズから生成する。
    pub fn from_center_half_size(center: Vector3<f32>, half_size: Vector3<f32>) -> Self {
        Self {
            min: center - half_size,
            max: center + half_size,
        }
    }

    /// 中心座標を返す。
    pub fn center(self) -> Vector3<f32> {
        (self.min + self.max) * 0.5
    }

    /// 各軸のサイズ (幅・高さ・奥行き) を返す。
    pub fn size(self) -> Vector3<f32> {
        self.max - self.min
    }

    /// 体積を返す。
    pub fn volume(self) -> f32 {
        let s = self.size();
        s.x * s.y * s.z
    }

    /// 点が AABB の内側（境界含む）に含まれるか判定する。
    pub fn contains(self, point: Vector3<f32>) -> bool {
        point.x >= self.min.x
            && point.x <= self.max.x
            && point.y >= self.min.y
            && point.y <= self.max.y
            && point.z >= self.min.z
            && point.z <= self.max.z
    }

    /// 別の AABB と重なっているか判定する。
    pub fn intersects(self, other: Self) -> bool {
        self.min.x <= other.max.x
            && self.max.x >= other.min.x
            && self.min.y <= other.max.y
            && self.max.y >= other.min.y
            && self.min.z <= other.max.z
            && self.max.z >= other.min.z
    }

    /// 2つの AABB を包む最小の AABB を返す。
    pub fn merge(self, other: Self) -> Self {
        Self {
            min: Vector3::new(
                self.min.x.min(other.min.x),
                self.min.y.min(other.min.y),
                self.min.z.min(other.min.z),
            ),
            max: Vector3::new(
                self.max.x.max(other.max.x),
                self.max.y.max(other.max.y),
                self.max.z.max(other.max.z),
            ),
        }
    }
}
