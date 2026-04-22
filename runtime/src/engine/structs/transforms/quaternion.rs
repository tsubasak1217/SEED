use std::ops::{Add, Sub, Mul, Div, Neg};
use crate::engine::structs::tensor::{Vector3, Mat4x4};

/// クォータニオン（四元数）。回転表現に使用する。
///
/// # 成分
/// - `(x, y, z)`: ベクトル部（虚部）
/// - `w`         : スカラー部（実部）
///
/// 単位クォータニオンは `x²+y²+z²+w²=1` を満たし、任意の 3D 回転を表現できる。
///
/// # 座標系
/// 左手座標系（LH）準拠。`forward = +Z`。
///
/// # オイラー角規約
/// `from_euler` / `to_euler` は **YXZ** 規約（先に Z、次に X、最後に Y を適用）。
/// これは C++ 版の `ToQuaternion` / static `ToEuler` と同一の規約。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Quaternion {
    pub x: f32,
    pub y: f32,
    pub z: f32,
    pub w: f32,
}

impl Quaternion {
    // ─── コンストラクタ ────────────────────────────────────────

    /// 成分を直接指定して生成する。
    #[inline]
    pub fn new(x: f32, y: f32, z: f32, w: f32) -> Self {
        Self { x, y, z, w }
    }

    /// 恒等回転（無回転）を返す。
    #[inline]
    pub fn identity() -> Self {
        Self { x: 0.0, y: 0.0, z: 0.0, w: 1.0 }
    }

    /// 軸と角度からクォータニオンを生成する。
    ///
    /// - `axis`      : 回転軸（自動正規化される）
    /// - `angle_rad` : 回転量（ラジアン）
    pub fn from_axis_angle(axis: Vector3<f32>, angle_rad: f32) -> Self {
        let half = angle_rad * 0.5;
        let s = half.sin();
        let a = axis.normalize();
        Self::new(a.x * s, a.y * s, a.z * s, half.cos())
    }

    // ─── オイラー角変換（YXZ 規約）────────────────────────────

    /// YXZ オイラー角（ラジアン）からクォータニオンを生成する。
    ///
    /// 適用順: Z → X → Y（イントリンシック）= q = Qy ∗ Qx ∗ Qz。
    /// C++ `Quaternion::ToQuaternion(euler)` と同一の規約。
    pub fn from_euler(euler: Vector3<f32>) -> Self {
        let (sx, cx) = (euler.x * 0.5).sin_cos();
        let (sy, cy) = (euler.y * 0.5).sin_cos();
        let (sz, cz) = (euler.z * 0.5).sin_cos();
        Self::new(
            cy * sx * cz + sy * cx * sz,
            sy * cx * cz - cy * sx * sz,
            cy * cx * sz - sy * sx * cz,
            cy * cx * cz + sy * sx * sz,
        )
    }

    /// クォータニオンを YXZ オイラー角（ラジアン）に変換する。
    ///
    /// 返値: `(x=pitch, y=yaw, z=roll)`（ラジアン）。
    /// C++ static `Quaternion::ToEuler(q)` と同一の規約。
    pub fn to_euler(self) -> Vector3<f32> {
        let q = self.normalize();
        let (x, y, z, w) = (q.x, q.y, q.z, q.w);
        Vector3::new(
            (2.0 * (w * x - y * z)).clamp(-1.0, 1.0).asin(),          // pitch (X)
            (2.0 * (w * y + z * x)).atan2(1.0 - 2.0 * (y*y + x*x)),   // yaw   (Y)
            (2.0 * (w * z + x * y)).atan2(1.0 - 2.0 * (z*z + x*x)),   // roll  (Z)
        )
    }

    // ─── 行列変換 ─────────────────────────────────────────────

    /// 4x4 回転行列に変換する（列ベクトル規約, row-major 格納）。
    ///
    /// ```text
    /// | 1-2(y²+z²)  2(xy-wz)    2(xz+wy)   0 |
    /// | 2(xy+wz)    1-2(x²+z²)  2(yz-wx)   0 |
    /// | 2(xz-wy)    2(yz+wx)    1-2(x²+y²) 0 |
    /// | 0           0           0           1 |
    /// ```
    pub fn to_rotation_matrix(self) -> Mat4x4<f32> {
        let q = self.normalize();
        let (x, y, z, w) = (q.x, q.y, q.z, q.w);
        Mat4x4::new(
            1.0 - 2.0*(y*y + z*z),  2.0*(x*y - w*z),        2.0*(x*z + w*y),        0.0,
            2.0*(x*y + w*z),        1.0 - 2.0*(x*x + z*z),  2.0*(y*z - w*x),        0.0,
            2.0*(x*z - w*y),        2.0*(y*z + w*x),        1.0 - 2.0*(x*x + y*y),  0.0,
            0.0,                    0.0,                     0.0,                     1.0,
        )
    }

    /// 列ベクトル規約・row-major 格納の純回転行列からクォータニオンを取得する。
    ///
    /// Shepperd 法を使用（数値安定性が高い）。
    /// スケール成分が含まれる場合は各列を事前に正規化した行列を渡すこと。
    /// C++ `Quaternion::MatrixToQuaternion` の列ベクトル版。
    pub fn from_matrix(mat: &Mat4x4<f32>) -> Self {
        let d = &mat.data;
        let r22 = d[2][2];
        if r22 <= 0.0 {
            let dif10 = d[1][1] - d[0][0];
            let omr22 = 1.0 - r22;
            if dif10 <= 0.0 {
                // 4x² = 1 + R00 - R11 - R22
                let four_x_sq = omr22 - dif10;
                let inv4x = 0.5 / four_x_sq.sqrt();
                Self::new(
                    four_x_sq        * inv4x,
                    (d[0][1] + d[1][0]) * inv4x,   // 4xy
                    (d[0][2] + d[2][0]) * inv4x,   // 4xz
                    (d[2][1] - d[1][2]) * inv4x,   // 4wx
                ).normalize()
            } else {
                // 4y² = 1 - R00 + R11 - R22
                let four_y_sq = omr22 + dif10;
                let inv4y = 0.5 / four_y_sq.sqrt();
                Self::new(
                    (d[0][1] + d[1][0]) * inv4y,   // 4xy
                    four_y_sq        * inv4y,
                    (d[1][2] + d[2][1]) * inv4y,   // 4yz
                    (d[0][2] - d[2][0]) * inv4y,   // 4wy
                ).normalize()
            }
        } else {
            let sum10 = d[1][1] + d[0][0];
            let opr22 = 1.0 + r22;
            if sum10 <= 0.0 {
                // 4z² = 1 - R00 - R11 + R22
                let four_z_sq = opr22 - sum10;
                let inv4z = 0.5 / four_z_sq.sqrt();
                Self::new(
                    (d[0][2] + d[2][0]) * inv4z,   // 4xz
                    (d[1][2] + d[2][1]) * inv4z,   // 4yz
                    four_z_sq        * inv4z,
                    (d[1][0] - d[0][1]) * inv4z,   // 4wz
                ).normalize()
            } else {
                // 4w² = 1 + R00 + R11 + R22
                let four_w_sq = opr22 + sum10;
                let inv4w = 0.5 / four_w_sq.sqrt();
                Self::new(
                    (d[2][1] - d[1][2]) * inv4w,   // 4wx
                    (d[0][2] - d[2][0]) * inv4w,   // 4wy
                    (d[1][0] - d[0][1]) * inv4w,   // 4wz
                    four_w_sq        * inv4w,
                ).normalize()
            }
        }
    }

    // ─── 基本演算 ─────────────────────────────────────────────

    /// クォータニオン同士の内積。
    #[inline]
    pub fn dot(self, other: Self) -> f32 {
        self.x*other.x + self.y*other.y + self.z*other.z + self.w*other.w
    }

    #[inline]
    pub fn length_sq(self) -> f32 {
        self.x*self.x + self.y*self.y + self.z*self.z + self.w*self.w
    }

    #[inline]
    pub fn length(self) -> f32 {
        self.length_sq().sqrt()
    }

    /// 正規化した単位クォータニオンを返す。零クォータニオンの場合は identity を返す。
    #[inline]
    pub fn normalize(self) -> Self {
        let len = self.length();
        if len > 1e-10 {
            Self::new(self.x / len, self.y / len, self.z / len, self.w / len)
        } else {
            Self::identity()
        }
    }

    /// 共役（回転の逆方向）を返す。単位クォータニオンなら `inverse()` と同値。
    #[inline]
    pub fn conjugate(self) -> Self {
        Self::new(-self.x, -self.y, -self.z, self.w)
    }

    /// 逆クォータニオンを返す（任意のノルム対応）。
    pub fn inverse(self) -> Self {
        let ls = self.length_sq();
        if ls > 1e-10 {
            Self::new(-self.x / ls, -self.y / ls, -self.z / ls, self.w / ls)
        } else {
            Self::identity()
        }
    }

    // ─── 補間 ──────────────────────────────────────────────────

    /// 球面線形補間（Slerp）。`t=0` → `self`、`t=1` → `other`。
    ///
    /// 最短経路を保証するため内積が負の場合は `other` を反転する。
    pub fn slerp(self, other: Self, t: f32) -> Self {
        let mut dot   = self.dot(other).clamp(-1.0, 1.0);
        let mut other = other;
        if dot < 0.0 {
            other = -other;
            dot   = -dot;
        }
        if dot > 1.0 - 1e-6 {
            return self.lerp(other, t);
        }
        let theta     = dot.acos();
        let sin_theta = theta.sin();
        let w1 = ((1.0 - t) * theta).sin() / sin_theta;
        let w2 = (t          * theta).sin() / sin_theta;
        (self * w1 + other * w2).normalize()
    }

    /// 線形補間（Lerp、結果を正規化）。`t=0` → `self`、`t=1` → `other`。
    #[inline]
    pub fn lerp(self, other: Self, t: f32) -> Self {
        (self * (1.0 - t) + other * t).normalize()
    }

    // ─── LookAt ────────────────────────────────────────────────

    /// `from` 方向から `to` 方向へ向ける最小回転クォータニオンを返す。
    ///
    /// 逆方向（dot ≈ -1）の場合は任意の垂直軸で 180° 回転する。
    pub fn look_at(from: Vector3<f32>, to: Vector3<f32>) -> Self {
        let from = from.normalize();
        let to   = to.normalize();
        let dot  = from.dot(to);
        if dot < -0.999_999 {
            let mut axis = Vector3::new(0.0_f32, 0.0, 1.0).cross(from);
            if axis.length_sq() < 1e-12 {
                axis = Vector3::new(1.0, 0.0, 0.0).cross(from);
            }
            return Self::from_axis_angle(axis.normalize(), std::f32::consts::PI);
        }
        if dot > 0.999_999 {
            return Self::identity();
        }
        let axis  = from.cross(to).normalize();
        let angle = dot.clamp(-1.0, 1.0).acos();
        Self::from_axis_angle(axis, angle)
    }

    // ─── Log / Exp ─────────────────────────────────────────────

    /// クォータニオンの対数 `ln(q)`。単位クォータニオンを想定（結果の w = 0）。
    pub fn log(self) -> Self {
        let a     = self.w.clamp(-1.0, 1.0).acos();
        let sin_a = a.sin();
        if sin_a.abs() > 1e-6 {
            let c = a / sin_a;
            Self::new(self.x * c, self.y * c, self.z * c, 0.0)
        } else {
            Self::new(self.x, self.y, self.z, 0.0)
        }
    }

    /// クォータニオンの指数関数 `exp(q)`。純虚クォータニオン（w=0）を想定。
    pub fn exp(self) -> Self {
        let a     = (self.x*self.x + self.y*self.y + self.z*self.z).sqrt();
        let cos_a = a.cos();
        if a.abs() > 1e-6 {
            let c = a.sin() / a;
            Self::new(self.x * c, self.y * c, self.z * c, cos_a)
        } else {
            Self::new(self.x, self.y, self.z, cos_a)
        }
    }

    // ─── ベクトル回転 ─────────────────────────────────────────

    /// ベクトル `v` をこのクォータニオンで回転させる。
    ///
    /// 最適化形式: `v' = v + 2w·(q×v) + 2·(q×(q×v))`
    pub fn rotate(self, v: Vector3<f32>) -> Vector3<f32> {
        let q = Vector3::new(self.x, self.y, self.z);
        let t = q.cross(v) * 2.0;
        v + t * self.w + q.cross(t)
    }

    // ─── 方向ベクトル（LH: forward=+Z） ──────────────────────

    /// カメラ/オブジェクトの前方向（+Z）を返す。
    #[inline]
    pub fn forward(self) -> Vector3<f32> {
        self.rotate(Vector3::new(0.0, 0.0, 1.0))
    }

    /// カメラ/オブジェクトの右方向（+X）を返す。
    #[inline]
    pub fn right(self) -> Vector3<f32> {
        self.rotate(Vector3::new(1.0, 0.0, 0.0))
    }

    /// カメラ/オブジェクトの上方向（+Y）を返す。
    #[inline]
    pub fn up(self) -> Vector3<f32> {
        self.rotate(Vector3::new(0.0, 1.0, 0.0))
    }
}

// ─── 演算子 ───────────────────────────────────────────────────

/// `q1 + q2` : 成分ごとの加算
impl Add for Quaternion {
    type Output = Self;
    #[inline]
    fn add(self, rhs: Self) -> Self {
        Self::new(self.x+rhs.x, self.y+rhs.y, self.z+rhs.z, self.w+rhs.w)
    }
}

/// `q1 - q2` : 成分ごとの減算
impl Sub for Quaternion {
    type Output = Self;
    #[inline]
    fn sub(self, rhs: Self) -> Self {
        Self::new(self.x-rhs.x, self.y-rhs.y, self.z-rhs.z, self.w-rhs.w)
    }
}

/// `-q` : 成分ごとの符号反転（`Slerp` で最短経路選択に使用）
impl Neg for Quaternion {
    type Output = Self;
    #[inline]
    fn neg(self) -> Self {
        Self::new(-self.x, -self.y, -self.z, -self.w)
    }
}

/// `q * scalar`
impl Mul<f32> for Quaternion {
    type Output = Self;
    #[inline]
    fn mul(self, s: f32) -> Self {
        Self::new(self.x*s, self.y*s, self.z*s, self.w*s)
    }
}

/// `q / scalar`
impl Div<f32> for Quaternion {
    type Output = Self;
    #[inline]
    fn div(self, s: f32) -> Self {
        Self::new(self.x/s, self.y/s, self.z/s, self.w/s)
    }
}

/// Hamilton 積: `q1 * q2`（回転の合成。先に `q2` を適用し次に `q1`）。
impl Mul for Quaternion {
    type Output = Self;
    #[inline]
    fn mul(self, rhs: Self) -> Self {
        Self::new(
            self.w*rhs.x + self.x*rhs.w + self.y*rhs.z - self.z*rhs.y,
            self.w*rhs.y - self.x*rhs.z + self.y*rhs.w + self.z*rhs.x,
            self.w*rhs.z + self.x*rhs.y - self.y*rhs.x + self.z*rhs.w,
            self.w*rhs.w - self.x*rhs.x - self.y*rhs.y - self.z*rhs.z,
        )
    }
}
