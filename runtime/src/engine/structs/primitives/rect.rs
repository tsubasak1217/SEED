use crate::engine::structs::tensor::Vector2;

/// 軸平行な 2D 矩形。
///
/// # フィールド
/// - `position`: 左上隅の座標
/// - `size`    : 幅 (x) と高さ (y)
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Rect {
    pub position: Vector2<f32>,
    pub size: Vector2<f32>,
}

impl Rect {
    pub fn new(position: Vector2<f32>, size: Vector2<f32>) -> Self {
        Self { position, size }
    }

    /// `(x, y, width, height)` から生成する。
    pub fn from_xywh(x: f32, y: f32, w: f32, h: f32) -> Self {
        Self {
            position: Vector2::new(x, y),
            size: Vector2::new(w, h),
        }
    }

    /// 右端の x 座標を返す。
    pub fn right(self) -> f32 {
        self.position.x + self.size.x
    }

    /// 下端の y 座標を返す。
    pub fn bottom(self) -> f32 {
        self.position.y + self.size.y
    }

    /// 中心座標を返す。
    pub fn center(self) -> Vector2<f32> {
        self.position + self.size * 0.5
    }

    /// 面積を返す。
    pub fn area(self) -> f32 {
        self.size.x * self.size.y
    }

    /// 点が矩形内に含まれるか判定する。
    pub fn contains(self, point: Vector2<f32>) -> bool {
        point.x >= self.position.x
            && point.x <= self.right()
            && point.y >= self.position.y
            && point.y <= self.bottom()
    }

    /// 別の矩形と重なっているか判定する。
    pub fn intersects(self, other: Self) -> bool {
        self.position.x < other.right()
            && self.right() > other.position.x
            && self.position.y < other.bottom()
            && self.bottom() > other.position.y
    }
}
