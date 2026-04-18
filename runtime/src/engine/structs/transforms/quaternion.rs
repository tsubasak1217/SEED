use std::ops::Mul;
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
/// # 回転方向
/// `from_axis_angle` は軸の正方向に右手の親指を向けたときの
/// 指の巻き方向（CCW from +axis）で回転する。
/// ただし左手座標系のため、`rotation_x/y/z` 行列と符号が一致する向き。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Quaternion {
    pub x: f32,
    pub y: f32,
    pub z: f32,
    pub w: f32,
}

impl Quaternion {
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

    // ─── 基本演算 ─────────────────────────────────────────────

    #[inline]
    pub fn length_sq(self) -> f32 {
        self.x * self.x + self.y * self.y + self.z * self.z + self.w * self.w
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
