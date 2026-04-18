use crate::engine::structs::tensor::{Vector2, Vector3};

/// 2D 空間上の線分。
///
/// # フィールド
/// - `start`: 始点
/// - `end`  : 終点
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Line2 {
    pub start: Vector2<f32>,
    pub end: Vector2<f32>,
}

impl Line2 {
    pub fn new(start: Vector2<f32>, end: Vector2<f32>) -> Self {
        Self { start, end }
    }

    /// 線分の長さを返す。
    pub fn length(self) -> f32 {
        (self.end - self.start).length()
    }

    /// 線分の長さの2乗を返す（比較用）。
    pub fn length_sq(self) -> f32 {
        (self.end - self.start).length_sq()
    }

    /// 線分の中点を返す。
    pub fn midpoint(self) -> Vector2<f32> {
        (self.start + self.end) * 0.5
    }

    /// 線分の方向ベクトル（正規化なし）を返す。
    pub fn direction(self) -> Vector2<f32> {
        self.end - self.start
    }
}

/// 3D 空間上の線分。
///
/// # フィールド
/// - `start`: 始点
/// - `end`  : 終点
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Line3 {
    pub start: Vector3<f32>,
    pub end: Vector3<f32>,
}

impl Line3 {
    pub fn new(start: Vector3<f32>, end: Vector3<f32>) -> Self {
        Self { start, end }
    }

    /// 線分の長さを返す。
    pub fn length(self) -> f32 {
        (self.end - self.start).length()
    }

    /// 線分の長さの2乗を返す（比較用）。
    pub fn length_sq(self) -> f32 {
        (self.end - self.start).length_sq()
    }

    /// 線分の中点を返す。
    pub fn midpoint(self) -> Vector3<f32> {
        (self.start + self.end) * 0.5
    }

    /// 線分の方向ベクトル（正規化なし）を返す。
    pub fn direction(self) -> Vector3<f32> {
        self.end - self.start
    }
}
