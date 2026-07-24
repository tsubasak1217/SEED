use std::fmt;
use std::ops::{Add, AddAssign, Mul, Neg, Sub, SubAssign};

use super::vector2::Vector2;

/// 2x2 行列（行優先レイアウト）。
///
/// # メモリレイアウト（行優先 / row-major）
/// `data[row][col]` でアクセスする。
///
/// ```text
/// | data[0][0]  data[0][1] |
/// | data[1][0]  data[1][1] |
/// ```
///
/// # ベクトルとの乗算規約
/// **列ベクトル規約**（WGSL / wgpu 準拠）: `v' = M * v`
///
/// # 演算子
/// | トレイト        | 演算         | 意味               |
/// |-----------------|--------------|--------------------|
/// | `Add`           | `A + B`      | 要素ごとの加算      |
/// | `Sub`           | `A - B`      | 要素ごとの減算      |
/// | `Neg`           | `-A`         | 要素ごとの符号反転  |
/// | `Mul<Mat2x2>`   | `A * B`      | 行列乗算            |
/// | `Mul<Vector2>`  | `M * v`      | 行列ベクトル積      |
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Mat2x2<T> {
    /// 行列成分（行優先）。`data[row][col]` でアクセスする。
    pub data: [[T; 2]; 2],
}

// ─── 基本コンストラクタ ─────────────────────────────────────────

impl<T: Copy> Mat2x2<T> {
    /// 全成分を行優先順で指定して生成する。
    ///
    /// ```text
    /// Mat2x2::new(m00, m01,
    ///             m10, m11)
    /// ```
    #[inline]
    pub fn new(m00: T, m01: T, m10: T, m11: T) -> Self {
        Self {
            data: [[m00, m01], [m10, m11]],
        }
    }
}

impl<T: Default + Copy> Mat2x2<T> {
    /// 零行列（全成分 0）を生成する。
    #[inline]
    pub fn zero() -> Self {
        Self {
            data: [[T::default(); 2]; 2],
        }
    }
}

impl Mat2x2<f32> {
    /// 単位行列を生成する。
    ///
    /// ```text
    /// | 1  0 |
    /// | 0  1 |
    /// ```
    #[inline]
    pub fn identity() -> Self {
        Self::new(1.0, 0.0, 0.0, 1.0)
    }
}

// ─── 演算子 ────────────────────────────────────────────────────

/// `A + B` : 要素ごとの加算
impl<T: Add<Output = T> + Copy> Add for Mat2x2<T> {
    type Output = Self;
    fn add(self, rhs: Self) -> Self {
        Self::new(
            self.data[0][0] + rhs.data[0][0],
            self.data[0][1] + rhs.data[0][1],
            self.data[1][0] + rhs.data[1][0],
            self.data[1][1] + rhs.data[1][1],
        )
    }
}

/// `A += B`
impl<T: AddAssign + Copy> AddAssign for Mat2x2<T> {
    fn add_assign(&mut self, rhs: Self) {
        for r in 0..2 {
            for c in 0..2 {
                self.data[r][c] += rhs.data[r][c];
            }
        }
    }
}

/// `A - B` : 要素ごとの減算
impl<T: Sub<Output = T> + Copy> Sub for Mat2x2<T> {
    type Output = Self;
    fn sub(self, rhs: Self) -> Self {
        Self::new(
            self.data[0][0] - rhs.data[0][0],
            self.data[0][1] - rhs.data[0][1],
            self.data[1][0] - rhs.data[1][0],
            self.data[1][1] - rhs.data[1][1],
        )
    }
}

/// `A -= B`
impl<T: SubAssign + Copy> SubAssign for Mat2x2<T> {
    fn sub_assign(&mut self, rhs: Self) {
        for r in 0..2 {
            for c in 0..2 {
                self.data[r][c] -= rhs.data[r][c];
            }
        }
    }
}

/// `-A` : 要素ごとの符号反転
impl<T: Neg<Output = T> + Copy> Neg for Mat2x2<T> {
    type Output = Self;
    fn neg(self) -> Self {
        Self::new(
            -self.data[0][0],
            -self.data[0][1],
            -self.data[1][0],
            -self.data[1][1],
        )
    }
}

/// `A * B` : 行列乗算
///
/// `C[i][j] = Σ_k A[i][k] * B[k][j]`
impl<T: Mul<Output = T> + Add<Output = T> + Copy> Mul<Mat2x2<T>> for Mat2x2<T> {
    type Output = Self;
    fn mul(self, rhs: Self) -> Self {
        let a = &self.data;
        let b = &rhs.data;
        Self::new(
            a[0][0] * b[0][0] + a[0][1] * b[1][0],
            a[0][0] * b[0][1] + a[0][1] * b[1][1],
            a[1][0] * b[0][0] + a[1][1] * b[1][0],
            a[1][0] * b[0][1] + a[1][1] * b[1][1],
        )
    }
}

/// `M * v` : 行列ベクトル積（列ベクトル規約）
///
/// `result[i] = Σ_j M[i][j] * v[j]`
impl<T: Mul<Output = T> + Add<Output = T> + Copy> Mul<Vector2<T>> for Mat2x2<T> {
    type Output = Vector2<T>;
    fn mul(self, v: Vector2<T>) -> Vector2<T> {
        let m = &self.data;
        Vector2::new(m[0][0] * v.x + m[0][1] * v.y, m[1][0] * v.x + m[1][1] * v.y)
    }
}

// ─── 行列演算（f32） ───────────────────────────────────────────

impl Mat2x2<f32> {
    /// 転置行列を返す。行と列を入れ替える。
    ///
    /// ```text
    /// T[i][j] = M[j][i]
    /// ```
    #[inline]
    pub fn transpose(self) -> Self {
        let m = &self.data;
        Self::new(m[0][0], m[1][0], m[0][1], m[1][1])
    }

    /// 行列式 (determinant) を返す。
    ///
    /// ```text
    /// det = m00*m11 - m01*m10
    /// ```
    #[inline]
    pub fn determinant(self) -> f32 {
        let m = &self.data;
        m[0][0] * m[1][1] - m[0][1] * m[1][0]
    }

    /// 逆行列を返す。特異行列（det=0）の場合は `None`。
    ///
    /// ```text
    /// inv = (1/det) * |  m11  -m01 |
    ///                 | -m10   m00 |
    /// ```
    pub fn inverse(self) -> Option<Self> {
        let det = self.determinant();
        if det.abs() < f32::EPSILON {
            return None;
        }
        let inv_det = 1.0 / det;
        let m = &self.data;
        Some(Self::new(
            m[1][1] * inv_det,
            -m[0][1] * inv_det,
            -m[1][0] * inv_det,
            m[0][0] * inv_det,
        ))
    }
}

// ─── 表示 ──────────────────────────────────────────────────────

impl<T: fmt::Display> fmt::Display for Mat2x2<T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        writeln!(f, "[ {:.4},  {:.4} ]", self.data[0][0], self.data[0][1])?;
        write!(f, "[ {:.4},  {:.4} ]", self.data[1][0], self.data[1][1])
    }
}
