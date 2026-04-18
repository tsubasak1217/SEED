use std::fmt;
use std::ops::{Add, AddAssign, Sub, SubAssign, Mul, Neg};

use super::vector3::Vector3;

/// 3x3 行列（行優先レイアウト）。
///
/// # メモリレイアウト（行優先 / row-major）
/// `data[row][col]` でアクセスする。
///
/// ```text
/// | data[0][0]  data[0][1]  data[0][2] |
/// | data[1][0]  data[1][1]  data[1][2] |
/// | data[2][0]  data[2][1]  data[2][2] |
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
/// | `Mul<Mat3x3>`   | `A * B`      | 行列乗算            |
/// | `Mul<Vector3>`  | `M * v`      | 行列ベクトル積      |
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Mat3x3<T> {
    /// 行列成分（行優先）。`data[row][col]` でアクセスする。
    pub data: [[T; 3]; 3],
}

// ─── 基本コンストラクタ ─────────────────────────────────────────

impl<T: Copy> Mat3x3<T> {
    /// 全成分を行優先順で指定して生成する。
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
    /// 零行列（全成分 0）を生成する。
    #[inline]
    pub fn zero() -> Self {
        Self { data: [[T::default(); 3]; 3] }
    }
}

impl Mat3x3<f32> {
    /// 単位行列を生成する。
    ///
    /// ```text
    /// | 1  0  0 |
    /// | 0  1  0 |
    /// | 0  0  1 |
    /// ```
    #[inline]
    pub fn identity() -> Self {
        Self::new(1.0, 0.0, 0.0,
                  0.0, 1.0, 0.0,
                  0.0, 0.0, 1.0)
    }
}

// ─── 演算子 ────────────────────────────────────────────────────

/// `A + B` : 要素ごとの加算
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

/// `A += B`
impl<T: AddAssign + Copy> AddAssign for Mat3x3<T> {
    fn add_assign(&mut self, rhs: Self) {
        for r in 0..3 { for c in 0..3 { self.data[r][c] += rhs.data[r][c]; } }
    }
}

/// `A - B` : 要素ごとの減算
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

/// `A -= B`
impl<T: SubAssign + Copy> SubAssign for Mat3x3<T> {
    fn sub_assign(&mut self, rhs: Self) {
        for r in 0..3 { for c in 0..3 { self.data[r][c] -= rhs.data[r][c]; } }
    }
}

/// `-A` : 要素ごとの符号反転
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

/// `A * B` : 行列乗算
///
/// `C[i][j] = Σ_k A[i][k] * B[k][j]`
impl<T: Mul<Output = T> + Add<Output = T> + Copy> Mul<Mat3x3<T>> for Mat3x3<T> {
    type Output = Self;
    fn mul(self, rhs: Self) -> Self {
        let a = &self.data; let b = &rhs.data;
        let mut out = [[a[0][0]; 3]; 3]; // 型推論用の初期化（後で全上書き）
        for i in 0..3 {
            for j in 0..3 {
                // Σ_k A[i][k] * B[k][j]
                let mut sum = a[i][0] * b[0][j];
                for k in 1..3 { sum = sum + a[i][k] * b[k][j]; }
                out[i][j] = sum;
            }
        }
        Self { data: out }
    }
}

/// `M * v` : 行列ベクトル積（列ベクトル規約）
///
/// `result[i] = Σ_j M[i][j] * v[j]`
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

// ─── 行列演算（f32） ───────────────────────────────────────────

impl Mat3x3<f32> {
    /// 転置行列を返す。行と列を入れ替える。
    ///
    /// ```text
    /// T[i][j] = M[j][i]
    /// ```
    #[inline]
    pub fn transpose(self) -> Self {
        let m = &self.data;
        Self::new(m[0][0], m[1][0], m[2][0],
                  m[0][1], m[1][1], m[2][1],
                  m[0][2], m[1][2], m[2][2])
    }

    /// 行列式 (determinant) を返す。
    ///
    /// 第 1 行に沿って余因子展開する。
    /// ```text
    /// det = m00*(m11*m22 - m12*m21)
    ///     - m01*(m10*m22 - m12*m20)
    ///     + m02*(m10*m21 - m11*m20)
    /// ```
    #[inline]
    pub fn determinant(self) -> f32 {
        let m = &self.data;
        m[0][0] * (m[1][1]*m[2][2] - m[1][2]*m[2][1])
      - m[0][1] * (m[1][0]*m[2][2] - m[1][2]*m[2][0])
      + m[0][2] * (m[1][0]*m[2][1] - m[1][1]*m[2][0])
    }

    /// 逆行列を返す。特異行列（det=0）の場合は `None`。
    ///
    /// 余因子行列（cofactor matrix）の転置（= 随伴行列）を行列式で割って求める。
    pub fn inverse(self) -> Option<Self> {
        let det = self.determinant();
        if det.abs() < f32::EPSILON { return None; }
        let inv = 1.0 / det;
        let m = &self.data;
        // 余因子 C[i][j] = (-1)^(i+j) * minor(i,j) を転置して配置する
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
