use crate::engine::structs::tensor::{Vector2, Vector3};

/// 2D 三角形。
///
/// # フィールド
/// - `a`, `b`, `c`: 3頂点の座標
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Triangle2 {
    pub a: Vector2<f32>,
    pub b: Vector2<f32>,
    pub c: Vector2<f32>,
}

impl Triangle2 {
    pub fn new(a: Vector2<f32>, b: Vector2<f32>, c: Vector2<f32>) -> Self {
        Self { a, b, c }
    }

    /// 重心を返す。
    pub fn centroid(self) -> Vector2<f32> {
        (self.a + self.b + self.c) * (1.0 / 3.0)
    }

    /// 符号付き面積を返す。正値 = 反時計回り、負値 = 時計回り。
    pub fn signed_area(self) -> f32 {
        let ab = self.b - self.a;
        let ac = self.c - self.a;
        (ab.x * ac.y - ab.y * ac.x) * 0.5
    }

    /// 面積を返す（符号なし）。
    pub fn area(self) -> f32 {
        self.signed_area().abs()
    }

    /// 点が三角形の内側に含まれるか判定する（バリセントリック座標を使用）。
    pub fn contains(self, point: Vector2<f32>) -> bool {
        let d1 = sign(point, self.a, self.b);
        let d2 = sign(point, self.b, self.c);
        let d3 = sign(point, self.c, self.a);
        let has_neg = d1 < 0.0 || d2 < 0.0 || d3 < 0.0;
        let has_pos = d1 > 0.0 || d2 > 0.0 || d3 > 0.0;
        !(has_neg && has_pos)
    }
}

fn sign(p: Vector2<f32>, a: Vector2<f32>, b: Vector2<f32>) -> f32 {
    (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y)
}

/// 3D 三角形。
///
/// # フィールド
/// - `a`, `b`, `c`: 3頂点の座標
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Triangle3 {
    pub a: Vector3<f32>,
    pub b: Vector3<f32>,
    pub c: Vector3<f32>,
}

impl Triangle3 {
    pub fn new(a: Vector3<f32>, b: Vector3<f32>, c: Vector3<f32>) -> Self {
        Self { a, b, c }
    }

    /// 重心を返す。
    pub fn centroid(self) -> Vector3<f32> {
        (self.a + self.b + self.c) * (1.0 / 3.0)
    }

    /// 面法線を返す（正規化なし、外積の向きは `ab × ac`）。
    pub fn normal(self) -> Vector3<f32> {
        let ab = self.b - self.a;
        let ac = self.c - self.a;
        ab.cross(ac)
    }

    /// 単位法線を返す。
    pub fn unit_normal(self) -> Vector3<f32> {
        self.normal().normalize()
    }

    /// 面積を返す。
    pub fn area(self) -> f32 {
        self.normal().length() * 0.5
    }
}
