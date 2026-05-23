// ============================================================
//  math/quaternion.rs — クォータニオン（物理エンジン内部用）
//
//  SEED エンジン本体の Quaternion と同一の構造だが、
//  Mat4x4 への依存（to_rotation_matrix, from_matrix）を取り除いた
//  スタンドアロン版。
// ============================================================

use std::ops::{Add, Sub, Mul, Div, Neg};
use super::vector3::Vector3;

/// クォータニオン（四元数）。回転表現に使用する。
///
/// 単位クォータニオンは `x²+y²+z²+w²=1` を満たし、任意の 3D 回転を表現できる。
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
    pub fn from_axis_angle(axis: Vector3<f32>, angle_rad: f32) -> Self {
        let half = angle_rad * 0.5;
        let s = half.sin();
        let a = axis.normalize();
        Self::new(a.x * s, a.y * s, a.z * s, half.cos())
    }

    /// YXZ オイラー角（ラジアン）からクォータニオンを生成する。
    ///
    /// 適用順: Z → X → Y（イントリンシック）。
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
    pub fn to_euler(self) -> Vector3<f32> {
        let q = self.normalize();
        let (x, y, z, w) = (q.x, q.y, q.z, q.w);
        Vector3::new(
            (2.0 * (w * x - y * z)).clamp(-1.0, 1.0).asin(),
            (2.0 * (w * y + z * x)).atan2(1.0 - 2.0 * (y*y + x*x)),
            (2.0 * (w * z + x * y)).atan2(1.0 - 2.0 * (z*z + x*x)),
        )
    }

    // ─── 基本演算 ─────────────────────────────────────────────

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

    /// 正規化した単位クォータニオンを返す。零の場合は identity を返す。
    #[inline]
    pub fn normalize(self) -> Self {
        let len = self.length();
        if len > 1e-10 {
            Self::new(self.x / len, self.y / len, self.z / len, self.w / len)
        } else {
            Self::identity()
        }
    }

    /// 共役（回転の逆方向）を返す。単位クォータニオンなら逆と同値。
    #[inline]
    pub fn conjugate(self) -> Self {
        Self::new(-self.x, -self.y, -self.z, self.w)
    }

    /// 逆クォータニオンを返す。
    pub fn inverse(self) -> Self {
        let ls = self.length_sq();
        if ls > 1e-10 {
            Self::new(-self.x / ls, -self.y / ls, -self.z / ls, self.w / ls)
        } else {
            Self::identity()
        }
    }

    // ─── 補間 ──────────────────────────────────────────────────

    /// 球面線形補間（Slerp）。
    pub fn slerp(self, other: Self, t: f32) -> Self {
        let mut dot   = self.dot(other).clamp(-1.0, 1.0);
        let mut other = other;
        if dot < 0.0 { other = -other; dot = -dot; }
        if dot > 1.0 - 1e-6 { return self.lerp(other, t); }
        let theta     = dot.acos();
        let sin_theta = theta.sin();
        let w1 = ((1.0 - t) * theta).sin() / sin_theta;
        let w2 = (t          * theta).sin() / sin_theta;
        (self * w1 + other * w2).normalize()
    }

    /// 線形補間（Lerp、結果を正規化）。
    #[inline]
    pub fn lerp(self, other: Self, t: f32) -> Self {
        (self * (1.0 - t) + other * t).normalize()
    }

    // ─── ベクトル回転 ─────────────────────────────────────────

    /// ベクトル `v` をこのクォータニオンで回転させる。
    pub fn rotate(self, v: Vector3<f32>) -> Vector3<f32> {
        let q = Vector3::new(self.x, self.y, self.z);
        let t = q.cross(v) * 2.0;
        v + t * self.w + q.cross(t)
    }
}

// ─── 演算子 ───────────────────────────────────────────────────

impl Add for Quaternion {
    type Output = Self;
    #[inline]
    fn add(self, rhs: Self) -> Self {
        Self::new(self.x+rhs.x, self.y+rhs.y, self.z+rhs.z, self.w+rhs.w)
    }
}

impl Sub for Quaternion {
    type Output = Self;
    #[inline]
    fn sub(self, rhs: Self) -> Self {
        Self::new(self.x-rhs.x, self.y-rhs.y, self.z-rhs.z, self.w-rhs.w)
    }
}

impl Neg for Quaternion {
    type Output = Self;
    #[inline]
    fn neg(self) -> Self {
        Self::new(-self.x, -self.y, -self.z, -self.w)
    }
}

impl Mul<f32> for Quaternion {
    type Output = Self;
    #[inline]
    fn mul(self, s: f32) -> Self {
        Self::new(self.x*s, self.y*s, self.z*s, self.w*s)
    }
}

impl Div<f32> for Quaternion {
    type Output = Self;
    #[inline]
    fn div(self, s: f32) -> Self {
        Self::new(self.x/s, self.y/s, self.z/s, self.w/s)
    }
}

/// Hamilton 積: `q1 * q2`（回転の合成）。
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

// ─── 型変換 ────────────────────────────────────────────────────

impl From<[f32; 4]> for Quaternion {
    /// `[x, y, z, w]` 配列から変換する。
    #[inline]
    fn from(arr: [f32; 4]) -> Self {
        Self::new(arr[0], arr[1], arr[2], arr[3])
    }
}

impl From<Quaternion> for [f32; 4] {
    #[inline]
    fn from(q: Quaternion) -> Self {
        [q.x, q.y, q.z, q.w]
    }
}
