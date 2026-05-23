// ============================================================
//  math/mat3x3.rs — 3x3 行列（物理エンジン内部用）
// ============================================================

use std::fmt;
use std::ops::{Add, AddAssign, Sub, SubAssign, Mul, Neg};

use super::vector3::Vector3;

/// 3x3 行列（行優先レイアウト）。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Mat3x3<T> {
    /// 行列成分（行優先）。`data[row][col]` でアクセスする。
    pub data: [[T; 3]; 3],
}

// ─── 基本コンストラクタ ─────────────────────────────────────────

impl<T: Copy> Mat3x3<T> {
    #[allow(clippy::too_many_arguments)]
    #[inline]
    pub fn new(
        m00: T, m01: T, m02: T,
        m10: T, m11: T, m12: T,
        m20: T, m21: T, m22: T,
    ) -> Self {
        Self { data: [[m00, m01, m02], [m10, m11, m12], [m20, m21, m22]] }
    }
}

impl<T: Default + Copy> Mat3x3<T> {
    /// 零行列を生成する。
    #[inline]
    pub fn zero() -> Self {
        Self { data: [[T::default(); 3]; 3] }
    }
}

impl Mat3x3<f32> {
    /// 単位行列を生成する。
    #[inline]
    pub fn identity() -> Self {
        Self::new(1.0, 0.0, 0.0,
                  0.0, 1.0, 0.0,
                  0.0, 0.0, 1.0)
    }
}

// ─── 演算子 ────────────────────────────────────────────────────

impl<T: Add<Output = T> + Copy> Add for Mat3x3<T> {
    type Output = Self;
    fn add(self, rhs: Self) -> Self {
        let a = &self.data; let b = &rhs.data;
        Self::new(
            a[0][0]+b[0][0], a[0][1]+b[0][1], a[0][2]+b[0][2],
            a[1][0]+b[1][0], a[1][1]+b[1][1], a[1][2]+b[1][2],
            a[2][0]+b[2][0], a[2][1]+b[2][1], a[2][2]+b[2][2],
        )
    }
}

impl<T: AddAssign + Copy> AddAssign for Mat3x3<T> {
    fn add_assign(&mut self, rhs: Self) {
        for r in 0..3 { for c in 0..3 { self.data[r][c] += rhs.data[r][c]; } }
    }
}

impl<T: Sub<Output = T> + Copy> Sub for Mat3x3<T> {
    type Output = Self;
    fn sub(self, rhs: Self) -> Self {
        let a = &self.data; let b = &rhs.data;
        Self::new(
            a[0][0]-b[0][0], a[0][1]-b[0][1], a[0][2]-b[0][2],
            a[1][0]-b[1][0], a[1][1]-b[1][1], a[1][2]-b[1][2],
            a[2][0]-b[2][0], a[2][1]-b[2][1], a[2][2]-b[2][2],
        )
    }
}

impl<T: SubAssign + Copy> SubAssign for Mat3x3<T> {
    fn sub_assign(&mut self, rhs: Self) {
        for r in 0..3 { for c in 0..3 { self.data[r][c] -= rhs.data[r][c]; } }
    }
}

impl<T: Neg<Output = T> + Copy> Neg for Mat3x3<T> {
    type Output = Self;
    fn neg(self) -> Self {
        let m = &self.data;
        Self::new(
            -m[0][0], -m[0][1], -m[0][2],
            -m[1][0], -m[1][1], -m[1][2],
            -m[2][0], -m[2][1], -m[2][2],
        )
    }
}

impl<T: Mul<Output = T> + Add<Output = T> + Copy> Mul<Mat3x3<T>> for Mat3x3<T> {
    type Output = Self;
    fn mul(self, rhs: Self) -> Self {
        let a = &self.data; let b = &rhs.data;
        let mut out = [[a[0][0]; 3]; 3];
        for i in 0..3 {
            for j in 0..3 {
                let mut sum = a[i][0] * b[0][j];
                for k in 1..3 { sum = sum + a[i][k] * b[k][j]; }
                out[i][j] = sum;
            }
        }
        Self { data: out }
    }
}

/// 行列ベクトル積（列ベクトル規約）: `result[i] = Σ_j M[i][j] * v[j]`
impl<T: Mul<Output = T> + Add<Output = T> + Copy> Mul<Vector3<T>> for Mat3x3<T> {
    type Output = Vector3<T>;
    fn mul(self, v: Vector3<T>) -> Vector3<T> {
        let m = &self.data;
        Vector3::new(
            m[0][0]*v.x + m[0][1]*v.y + m[0][2]*v.z,
            m[1][0]*v.x + m[1][1]*v.y + m[1][2]*v.z,
            m[2][0]*v.x + m[2][1]*v.y + m[2][2]*v.z,
        )
    }
}

// ─── f32 専用演算 ──────────────────────────────────────────────

impl Mat3x3<f32> {
    /// 転置行列を返す。
    #[inline]
    pub fn transpose(self) -> Self {
        let m = &self.data;
        Self::new(m[0][0], m[1][0], m[2][0],
                  m[0][1], m[1][1], m[2][1],
                  m[0][2], m[1][2], m[2][2])
    }

    /// 行列式を返す。
    #[inline]
    pub fn determinant(self) -> f32 {
        let m = &self.data;
        m[0][0] * (m[1][1]*m[2][2] - m[1][2]*m[2][1])
      - m[0][1] * (m[1][0]*m[2][2] - m[1][2]*m[2][0])
      + m[0][2] * (m[1][0]*m[2][1] - m[1][1]*m[2][0])
    }

    /// 逆行列を返す。特異行列（det=0）の場合は `None`。
    pub fn inverse(self) -> Option<Self> {
        let det = self.determinant();
        if det.abs() < f32::EPSILON { return None; }
        let inv = 1.0 / det;
        let m = &self.data;
        Some(Self::new(
             (m[1][1]*m[2][2] - m[1][2]*m[2][1]) * inv,
            -(m[0][1]*m[2][2] - m[0][2]*m[2][1]) * inv,
             (m[0][1]*m[1][2] - m[0][2]*m[1][1]) * inv,
            -(m[1][0]*m[2][2] - m[1][2]*m[2][0]) * inv,
             (m[0][0]*m[2][2] - m[0][2]*m[2][0]) * inv,
            -(m[0][0]*m[1][2] - m[0][2]*m[1][0]) * inv,
             (m[1][0]*m[2][1] - m[1][1]*m[2][0]) * inv,
            -(m[0][0]*m[2][1] - m[0][1]*m[2][0]) * inv,
             (m[0][0]*m[1][1] - m[0][1]*m[1][0]) * inv,
        ))
    }
}

// ─── 表示 ──────────────────────────────────────────────────────

impl<T: fmt::Display> fmt::Display for Mat3x3<T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        writeln!(f, "[ {:.4},  {:.4},  {:.4} ]", self.data[0][0], self.data[0][1], self.data[0][2])?;
        writeln!(f, "[ {:.4},  {:.4},  {:.4} ]", self.data[1][0], self.data[1][1], self.data[1][2])?;
        write!(f,   "[ {:.4},  {:.4},  {:.4} ]", self.data[2][0], self.data[2][1], self.data[2][2])
    }
}
