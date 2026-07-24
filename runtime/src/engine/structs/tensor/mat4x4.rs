use std::fmt;
use std::ops::{Add, AddAssign, Mul, Neg, Sub, SubAssign};

use super::vector3::Vector3;
use super::vector4::Vector4;

/// 4x4 行列（行優先レイアウト）。
///
/// 左手座標系（X=右, Y=上, Z=手前から奥）・列ベクトル規約で設計されており、
/// wgpu (WebGPU 仕様) および DirectX 12 と同じ座標系・深度範囲 Z∈[0,1] に対応する。
///
/// # メモリレイアウト（行優先 / row-major）
/// `data[row][col]` でアクセスする。
///
/// ```text
/// | data[0][0]  data[0][1]  data[0][2]  data[0][3] |   ← 第 1 行
/// | data[1][0]  data[1][1]  data[1][2]  data[1][3] |
/// | data[2][0]  data[2][1]  data[2][2]  data[2][3] |
/// | data[3][0]  data[3][1]  data[3][2]  data[3][3] |
/// ```
///
/// # ベクトルとの乗算規約
/// **列ベクトル規約**（WGSL / wgpu 準拠）: `v' = M * v`
///
/// 平行移動成分は第 4 **列**（data[0][3], data[1][3], data[2][3]）に格納される。
///
/// # 演算子
/// | トレイト        | 演算         | 意味                        |
/// |-----------------|--------------|-----------------------------|
/// | `Add`           | `A + B`      | 要素ごとの加算               |
/// | `Sub`           | `A - B`      | 要素ごとの減算               |
/// | `Neg`           | `-A`         | 要素ごとの符号反転            |
/// | `Mul<Mat4x4>`   | `A * B`      | 行列乗算                     |
/// | `Mul<Vector4>`  | `M * v`      | 行列ベクトル積               |
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Mat4x4<T> {
    /// 行列成分（行優先）。`data[row][col]` でアクセスする。
    pub data: [[T; 4]; 4],
}

// ─── 基本コンストラクタ ─────────────────────────────────────────

impl<T: Copy> Mat4x4<T> {
    /// 全成分を行優先順で指定して生成する。
    #[allow(clippy::too_many_arguments)]
    #[inline]
    pub fn new(
        m00: T,
        m01: T,
        m02: T,
        m03: T,
        m10: T,
        m11: T,
        m12: T,
        m13: T,
        m20: T,
        m21: T,
        m22: T,
        m23: T,
        m30: T,
        m31: T,
        m32: T,
        m33: T,
    ) -> Self {
        Self {
            data: [
                [m00, m01, m02, m03],
                [m10, m11, m12, m13],
                [m20, m21, m22, m23],
                [m30, m31, m32, m33],
            ],
        }
    }
}

impl<T: Default + Copy> Mat4x4<T> {
    /// 零行列（全成分 0）を生成する。
    #[inline]
    pub fn zero() -> Self {
        Self {
            data: [[T::default(); 4]; 4],
        }
    }
}

impl Mat4x4<f32> {
    /// 単位行列を生成する。
    ///
    /// ```text
    /// | 1  0  0  0 |
    /// | 0  1  0  0 |
    /// | 0  0  1  0 |
    /// | 0  0  0  1 |
    /// ```
    #[inline]
    pub fn identity() -> Self {
        Self::new(
            1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0,
        )
    }
}

// ─── 演算子 ────────────────────────────────────────────────────

/// `A + B` : 要素ごとの加算
impl<T: Add<Output = T> + Copy> Add for Mat4x4<T> {
    type Output = Self;
    fn add(self, rhs: Self) -> Self {
        let a = &self.data;
        let b = &rhs.data;
        Self::new(
            a[0][0] + b[0][0],
            a[0][1] + b[0][1],
            a[0][2] + b[0][2],
            a[0][3] + b[0][3],
            a[1][0] + b[1][0],
            a[1][1] + b[1][1],
            a[1][2] + b[1][2],
            a[1][3] + b[1][3],
            a[2][0] + b[2][0],
            a[2][1] + b[2][1],
            a[2][2] + b[2][2],
            a[2][3] + b[2][3],
            a[3][0] + b[3][0],
            a[3][1] + b[3][1],
            a[3][2] + b[3][2],
            a[3][3] + b[3][3],
        )
    }
}

/// `A += B`
impl<T: AddAssign + Copy> AddAssign for Mat4x4<T> {
    fn add_assign(&mut self, rhs: Self) {
        for r in 0..4 {
            for c in 0..4 {
                self.data[r][c] += rhs.data[r][c];
            }
        }
    }
}

/// `A - B` : 要素ごとの減算
impl<T: Sub<Output = T> + Copy> Sub for Mat4x4<T> {
    type Output = Self;
    fn sub(self, rhs: Self) -> Self {
        let a = &self.data;
        let b = &rhs.data;
        Self::new(
            a[0][0] - b[0][0],
            a[0][1] - b[0][1],
            a[0][2] - b[0][2],
            a[0][3] - b[0][3],
            a[1][0] - b[1][0],
            a[1][1] - b[1][1],
            a[1][2] - b[1][2],
            a[1][3] - b[1][3],
            a[2][0] - b[2][0],
            a[2][1] - b[2][1],
            a[2][2] - b[2][2],
            a[2][3] - b[2][3],
            a[3][0] - b[3][0],
            a[3][1] - b[3][1],
            a[3][2] - b[3][2],
            a[3][3] - b[3][3],
        )
    }
}

/// `A -= B`
impl<T: SubAssign + Copy> SubAssign for Mat4x4<T> {
    fn sub_assign(&mut self, rhs: Self) {
        for r in 0..4 {
            for c in 0..4 {
                self.data[r][c] -= rhs.data[r][c];
            }
        }
    }
}

/// `-A` : 要素ごとの符号反転
impl<T: Neg<Output = T> + Copy> Neg for Mat4x4<T> {
    type Output = Self;
    fn neg(self) -> Self {
        let m = &self.data;
        Self::new(
            -m[0][0], -m[0][1], -m[0][2], -m[0][3], -m[1][0], -m[1][1], -m[1][2], -m[1][3],
            -m[2][0], -m[2][1], -m[2][2], -m[2][3], -m[3][0], -m[3][1], -m[3][2], -m[3][3],
        )
    }
}

/// `A * B` : 行列乗算
///
/// `C[i][j] = Σ_k A[i][k] * B[k][j]`
impl<T: Mul<Output = T> + Add<Output = T> + Copy> Mul<Mat4x4<T>> for Mat4x4<T> {
    type Output = Self;
    fn mul(self, rhs: Self) -> Self {
        let a = &self.data;
        let b = &rhs.data;
        let mut out = [[a[0][0]; 4]; 4];
        for i in 0..4 {
            for j in 0..4 {
                let mut sum = a[i][0] * b[0][j];
                for k in 1..4 {
                    sum = sum + a[i][k] * b[k][j];
                }
                out[i][j] = sum;
            }
        }
        Self { data: out }
    }
}

/// `M * v` : 行列ベクトル積（列ベクトル規約）
///
/// `result[i] = Σ_j M[i][j] * v[j]`
impl<T: Mul<Output = T> + Add<Output = T> + Copy> Mul<Vector4<T>> for Mat4x4<T> {
    type Output = Vector4<T>;
    fn mul(self, v: Vector4<T>) -> Vector4<T> {
        let m = &self.data;
        Vector4::new(
            m[0][0] * v.x + m[0][1] * v.y + m[0][2] * v.z + m[0][3] * v.w,
            m[1][0] * v.x + m[1][1] * v.y + m[1][2] * v.z + m[1][3] * v.w,
            m[2][0] * v.x + m[2][1] * v.y + m[2][2] * v.z + m[2][3] * v.w,
            m[3][0] * v.x + m[3][1] * v.y + m[3][2] * v.z + m[3][3] * v.w,
        )
    }
}

// ─── 基本行列演算（f32） ───────────────────────────────────────

impl Mat4x4<f32> {
    /// 転置行列を返す。行と列を入れ替える。
    ///
    /// ```text
    /// T[i][j] = M[j][i]
    /// ```
    #[inline]
    pub fn transpose(self) -> Self {
        let m = &self.data;
        Self::new(
            m[0][0], m[1][0], m[2][0], m[3][0], m[0][1], m[1][1], m[2][1], m[3][1], m[0][2],
            m[1][2], m[2][2], m[3][2], m[0][3], m[1][3], m[2][3], m[3][3],
        )
    }

    /// 3x3 小行列式（minor）を計算する内部ヘルパー。
    ///
    /// `row` 行と `col` 列を除いた 3x3 部分行列の行列式を返す。
    fn minor(m: &[[f32; 4]; 4], row: usize, col: usize) -> f32 {
        let mut s = [[0.0f32; 3]; 3];
        let mut si = 0;
        for i in 0..4 {
            if i == row {
                continue;
            }
            let mut sj = 0;
            for j in 0..4 {
                if j == col {
                    continue;
                }
                s[si][sj] = m[i][j];
                sj += 1;
            }
            si += 1;
        }
        s[0][0] * (s[1][1] * s[2][2] - s[1][2] * s[2][1])
            - s[0][1] * (s[1][0] * s[2][2] - s[1][2] * s[2][0])
            + s[0][2] * (s[1][0] * s[2][1] - s[1][1] * s[2][0])
    }

    /// 行列式 (determinant) を返す。
    ///
    /// 第 1 行に沿って余因子展開する。
    pub fn determinant(self) -> f32 {
        let m = &self.data;
        let mut det = 0.0_f32;
        for j in 0..4 {
            let sign = if j % 2 == 0 { 1.0_f32 } else { -1.0_f32 };
            det += sign * m[0][j] * Self::minor(m, 0, j);
        }
        det
    }

    /// 逆行列を返す。特異行列（det≈0）の場合は `None`。
    ///
    /// 余因子行列の転置（随伴行列）を行列式で割って求める。
    pub fn inverse(self) -> Option<Self> {
        let det = self.determinant();
        if det.abs() < f32::EPSILON {
            return None;
        }
        let inv = 1.0 / det;
        let m = &self.data;
        let mut out = [[0.0f32; 4]; 4];
        for i in 0..4 {
            for j in 0..4 {
                let sign = if (i + j) % 2 == 0 { 1.0_f32 } else { -1.0_f32 };
                // 転置して格納する（j, i の順）
                out[j][i] = sign * Self::minor(m, i, j) * inv;
            }
        }
        Some(Self { data: out })
    }
}

// ─── 3D 変換行列（左手座標系, 列ベクトル規約）────────────────────

impl Mat4x4<f32> {
    /// 平行移動行列を生成する。
    ///
    /// 列ベクトル規約では平行移動成分は第 4 **列** に入る。
    ///
    /// ```text
    /// | 1  0  0  tx |   | x |   | x + tx |
    /// | 0  1  0  ty | * | y | = | y + ty |
    /// | 0  0  1  tz |   | z |   | z + tz |
    /// | 0  0  0  1  |   | 1 |   |   1    |
    /// ```
    #[inline]
    pub fn translation(tx: f32, ty: f32, tz: f32) -> Self {
        Self::new(
            1.0, 0.0, 0.0, tx, 0.0, 1.0, 0.0, ty, 0.0, 0.0, 1.0, tz, 0.0, 0.0, 0.0, 1.0,
        )
    }

    /// スケール行列を生成する。
    ///
    /// ```text
    /// | sx  0   0   0 |
    /// | 0   sy  0   0 |
    /// | 0   0   sz  0 |
    /// | 0   0   0   1 |
    /// ```
    #[inline]
    pub fn scale(sx: f32, sy: f32, sz: f32) -> Self {
        Self::new(
            sx, 0.0, 0.0, 0.0, 0.0, sy, 0.0, 0.0, 0.0, 0.0, sz, 0.0, 0.0, 0.0, 0.0, 1.0,
        )
    }

    /// X 軸周りの回転行列を生成する（左手座標系, ラジアン）。
    ///
    /// 正の角度 = 左手の親指を +X に向けたときの指の巻き方向（時計回り / YZ 平面）。
    ///
    /// ```text
    /// | 1    0       0    0 |
    /// | 0    cos(θ)  sin(θ) 0 |
    /// | 0   -sin(θ)  cos(θ) 0 |
    /// | 0    0       0    1 |
    /// ```
    #[inline]
    pub fn rotation_x(angle_rad: f32) -> Self {
        let (s, c) = angle_rad.sin_cos();
        Self::new(
            1.0, 0.0, 0.0, 0.0, 0.0, c, s, 0.0, 0.0, -s, c, 0.0, 0.0, 0.0, 0.0, 1.0,
        )
    }

    /// Y 軸周りの回転行列を生成する（左手座標系, ラジアン）。
    ///
    /// 正の角度 = 左手の親指を +Y に向けたときの指の巻き方向。
    ///
    /// ```text
    /// | cos(θ)   0  -sin(θ)  0 |
    /// | 0        1   0       0 |
    /// | sin(θ)   0   cos(θ)  0 |
    /// | 0        0   0       1 |
    /// ```
    #[inline]
    pub fn rotation_y(angle_rad: f32) -> Self {
        let (s, c) = angle_rad.sin_cos();
        Self::new(
            c, 0.0, -s, 0.0, 0.0, 1.0, 0.0, 0.0, s, 0.0, c, 0.0, 0.0, 0.0, 0.0, 1.0,
        )
    }

    /// Z 軸周りの回転行列を生成する（左手座標系, ラジアン）。
    ///
    /// 正の角度 = 左手の親指を +Z に向けたときの指の巻き方向。
    ///
    /// ```text
    /// | cos(θ)  -sin(θ)  0  0 |
    /// | sin(θ)   cos(θ)  0  0 |
    /// | 0        0       1  0 |
    /// | 0        0       0  1 |
    /// ```
    #[inline]
    pub fn rotation_z(angle_rad: f32) -> Self {
        let (s, c) = angle_rad.sin_cos();
        Self::new(
            c, -s, 0.0, 0.0, s, c, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0,
        )
    }

    /// 透視投影行列を生成する（左手座標系, Z∈[0,1], wgpu/DX12 準拠）。
    ///
    /// # 引数
    /// - `fov_y_rad` : 垂直視野角（ラジアン）
    /// - `aspect`    : ビューポートの幅 / 高さ
    /// - `near`      : 近クリップ面の距離（> 0）
    /// - `far`       : 遠クリップ面の距離（> near）
    ///
    /// # 行列
    /// ```text
    /// f = 1 / tan(fov_y / 2)
    ///
    /// | f/aspect  0   0                        0              |
    /// | 0         f   0                        0              |
    /// | 0         0   far/(far-near)           -near*far/(far-near) |
    /// | 0         0   1                        0              |
    /// ```
    ///
    /// # 深度のマッピング
    /// - ビュー空間 z = near → クリップ空間 z/w = 0
    /// - ビュー空間 z = far  → クリップ空間 z/w = 1
    pub fn perspective_lh(fov_y_rad: f32, aspect: f32, near: f32, far: f32) -> Self {
        let f = 1.0 / (fov_y_rad * 0.5).tan();
        let range_inv = 1.0 / (far - near);
        Self::new(
            f / aspect,
            0.0,
            0.0,
            0.0,
            0.0,
            f,
            0.0,
            0.0,
            0.0,
            0.0,
            far * range_inv,
            -near * far * range_inv,
            0.0,
            0.0,
            1.0,
            0.0,
        )
    }

    /// 平行投影（正射影）行列を生成する（左手座標系, Z∈[0,1], wgpu/DX12 準拠）。
    ///
    /// # 引数
    /// - `left`, `right`   : 視錐台の左右端の X 座標
    /// - `bottom`, `top`   : 視錐台の上下端の Y 座標
    /// - `near`, `far`     : 深度範囲
    ///
    /// # 行列
    /// ```text
    /// | 2/(r-l)   0        0          -(r+l)/(r-l) |
    /// | 0         2/(t-b)  0          -(t+b)/(t-b) |
    /// | 0         0        1/(f-n)    -n/(f-n)     |
    /// | 0         0        0           1           |
    /// ```
    pub fn orthographic_lh(
        left: f32,
        right: f32,
        bottom: f32,
        top: f32,
        near: f32,
        far: f32,
    ) -> Self {
        let rcp_width = 1.0 / (right - left);
        let rcp_height = 1.0 / (top - bottom);
        let rcp_depth = 1.0 / (far - near);
        Self::new(
            2.0 * rcp_width,
            0.0,
            0.0,
            -(right + left) * rcp_width,
            0.0,
            2.0 * rcp_height,
            0.0,
            -(top + bottom) * rcp_height,
            0.0,
            0.0,
            rcp_depth,
            -near * rcp_depth,
            0.0,
            0.0,
            0.0,
            1.0,
        )
    }

    /// ビュー行列（look-at）を生成する（左手座標系）。
    ///
    /// # 引数
    /// - `eye`    : カメラ位置
    /// - `center` : 注視点
    /// - `up`     : 上方向ベクトル（通常 `Vector3::new(0,1,0)`）
    ///
    /// # 処理
    /// 左手座標系では Z 軸が奥向き（forward）なので:
    /// ```text
    /// forward = normalize(center - eye)       // +Z 方向
    /// right   = normalize(cross(up, forward)) // +X 方向
    /// up'     = cross(forward, right)         // +Y 方向（再正規化）
    /// ```
    ///
    /// 生成される行列は「ワールド空間 → カメラ空間」の変換を表す。
    pub fn look_at_lh(eye: Vector3<f32>, center: Vector3<f32>, up: Vector3<f32>) -> Self {
        let forward = (center - eye).normalize();
        let right = up.cross(forward).normalize();
        let up_corr = forward.cross(right);

        // 回転部分の転置（逆回転）+ 平行移動部分の内積
        Self::new(
            right.x,
            right.y,
            right.z,
            -right.dot(eye),
            up_corr.x,
            up_corr.y,
            up_corr.z,
            -up_corr.dot(eye),
            forward.x,
            forward.y,
            forward.z,
            -forward.dot(eye),
            0.0,
            0.0,
            0.0,
            1.0,
        )
    }
}

// ─── 表示 ──────────────────────────────────────────────────────

impl<T: fmt::Display> fmt::Display for Mat4x4<T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        writeln!(
            f,
            "[ {:.4},  {:.4},  {:.4},  {:.4} ]",
            self.data[0][0], self.data[0][1], self.data[0][2], self.data[0][3]
        )?;
        writeln!(
            f,
            "[ {:.4},  {:.4},  {:.4},  {:.4} ]",
            self.data[1][0], self.data[1][1], self.data[1][2], self.data[1][3]
        )?;
        writeln!(
            f,
            "[ {:.4},  {:.4},  {:.4},  {:.4} ]",
            self.data[2][0], self.data[2][1], self.data[2][2], self.data[2][3]
        )?;
        write!(
            f,
            "[ {:.4},  {:.4},  {:.4},  {:.4} ]",
            self.data[3][0], self.data[3][1], self.data[3][2], self.data[3][3]
        )
    }
}
