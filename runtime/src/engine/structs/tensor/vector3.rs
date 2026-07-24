use std::fmt;
use std::ops::{
    Add, AddAssign, Div, DivAssign, Index, IndexMut, Mul, MulAssign, Neg, Sub, SubAssign,
};

use super::vector2::Vector2;
use crate::engine::structs::transforms::Quaternion;

/// 3 次元ベクトル。
///
/// # 型パラメータ
/// - `T`: 成分の型。`f32` / `f64` / `i32` など数値型を想定。
///
/// # 成分
/// - `x`: 第 1 成分
/// - `y`: 第 2 成分
/// - `z`: 第 3 成分
///
/// # 演算子 (C++ の operator 相当)
/// | Rust トレイト  | 演算       | 例                              |
/// |---------------|------------|---------------------------------|
/// | `Add`         | `v + v`    | ベクトル加算                     |
/// | `AddAssign`   | `v += v`   | ベクトル加算代入                  |
/// | `Sub`         | `v - v`    | ベクトル減算                     |
/// | `SubAssign`   | `v -= v`   | ベクトル減算代入                  |
/// | `Mul<T>`      | `v * s`    | スカラー乗算                     |
/// | `MulAssign<T>`| `v *= s`   | スカラー乗算代入                  |
/// | `Div<T>`      | `v / s`    | スカラー除算                     |
/// | `DivAssign<T>`| `v /= s`   | スカラー除算代入                  |
/// | `Neg`         | `-v`       | 符号反転                         |
/// | `Index`       | `v[i]`     | 成分アクセス（0=x, 1=y, 2=z）   |
/// | `IndexMut`    | `v[i] = x` | 成分への書き込み                  |
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Vector3<T> {
    pub x: T,
    pub y: T,
    pub z: T,
}

// ─── 基本コンストラクタ ─────────────────────────────────────────

impl<T: Copy> Vector3<T> {
    /// 成分を指定してベクトルを生成する。
    #[inline]
    pub fn new(x: T, y: T, z: T) -> Self {
        Self { x, y, z }
    }
}

impl<T: Default + Copy> Vector3<T> {
    /// 零ベクトル (0, 0, 0) を生成する。
    #[inline]
    pub fn zero() -> Self {
        Self {
            x: T::default(),
            y: T::default(),
            z: T::default(),
        }
    }
}

// ─── 演算子 ────────────────────────────────────────────────────

/// `v + rhs` : ベクトル加算
impl<T: Add<Output = T> + Copy> Add for Vector3<T> {
    type Output = Self;
    #[inline]
    fn add(self, rhs: Self) -> Self {
        Self::new(self.x + rhs.x, self.y + rhs.y, self.z + rhs.z)
    }
}

/// `v += rhs` : ベクトル加算代入
impl<T: AddAssign + Copy> AddAssign for Vector3<T> {
    #[inline]
    fn add_assign(&mut self, rhs: Self) {
        self.x += rhs.x;
        self.y += rhs.y;
        self.z += rhs.z;
    }
}

/// `v - rhs` : ベクトル減算
impl<T: Sub<Output = T> + Copy> Sub for Vector3<T> {
    type Output = Self;
    #[inline]
    fn sub(self, rhs: Self) -> Self {
        Self::new(self.x - rhs.x, self.y - rhs.y, self.z - rhs.z)
    }
}

/// `v -= rhs` : ベクトル減算代入
impl<T: SubAssign + Copy> SubAssign for Vector3<T> {
    #[inline]
    fn sub_assign(&mut self, rhs: Self) {
        self.x -= rhs.x;
        self.y -= rhs.y;
        self.z -= rhs.z;
    }
}

/// `v * scalar` : スカラー乗算
impl<T: Mul<Output = T> + Copy> Mul<T> for Vector3<T> {
    type Output = Self;
    #[inline]
    fn mul(self, rhs: T) -> Self {
        Self::new(self.x * rhs, self.y * rhs, self.z * rhs)
    }
}

/// `v *= scalar` : スカラー乗算代入
impl<T: MulAssign + Copy> MulAssign<T> for Vector3<T> {
    #[inline]
    fn mul_assign(&mut self, rhs: T) {
        self.x *= rhs;
        self.y *= rhs;
        self.z *= rhs;
    }
}

/// `v / scalar` : スカラー除算
impl<T: Div<Output = T> + Copy> Div<T> for Vector3<T> {
    type Output = Self;
    #[inline]
    fn div(self, rhs: T) -> Self {
        Self::new(self.x / rhs, self.y / rhs, self.z / rhs)
    }
}

/// `v /= scalar` : スカラー除算代入
impl<T: DivAssign + Copy> DivAssign<T> for Vector3<T> {
    #[inline]
    fn div_assign(&mut self, rhs: T) {
        self.x /= rhs;
        self.y /= rhs;
        self.z /= rhs;
    }
}

/// `-v` : 符号反転（単項マイナス）
impl<T: Neg<Output = T> + Copy> Neg for Vector3<T> {
    type Output = Self;
    #[inline]
    fn neg(self) -> Self {
        Self::new(-self.x, -self.y, -self.z)
    }
}

/// `v[i]` : 成分の読み取り（0=x, 1=y, 2=z）
impl<T> Index<usize> for Vector3<T> {
    type Output = T;
    #[inline]
    fn index(&self, i: usize) -> &T {
        match i {
            0 => &self.x,
            1 => &self.y,
            2 => &self.z,
            _ => panic!("Vector3: index {} is out of bounds (max 2)", i),
        }
    }
}

/// `v[i] = val` : 成分への書き込み（0=x, 1=y, 2=z）
impl<T> IndexMut<usize> for Vector3<T> {
    #[inline]
    fn index_mut(&mut self, i: usize) -> &mut T {
        match i {
            0 => &mut self.x,
            1 => &mut self.y,
            2 => &mut self.z,
            _ => panic!("Vector3: index {} is out of bounds (max 2)", i),
        }
    }
}

// ─── 数学演算（汎用） ──────────────────────────────────────────

impl<T: Mul<Output = T> + Add<Output = T> + Copy> Vector3<T> {
    /// 内積 (dot product): `self · rhs = x*rhs.x + y*rhs.y + z*rhs.z`
    #[inline]
    pub fn dot(self, rhs: Self) -> T {
        self.x * rhs.x + self.y * rhs.y + self.z * rhs.z
    }
}

impl<T: Mul<Output = T> + Sub<Output = T> + Copy> Vector3<T> {
    /// 外積 (cross product): `self × rhs`
    ///
    /// 結果は `self` と `rhs` の両方に直交するベクトル。
    /// 右手系では `x × y = z` の向きになる。
    #[inline]
    pub fn cross(self, rhs: Self) -> Self {
        Self::new(
            self.y * rhs.z - self.z * rhs.y,
            self.z * rhs.x - self.x * rhs.z,
            self.x * rhs.y - self.y * rhs.x,
        )
    }
}

// ─── f32 専用メソッド ──────────────────────────────────────────

impl Vector3<f32> {
    /// ベクトルの長さの 2 乗を返す（`sqrt` を避けたい場合に使う）。
    #[inline]
    pub fn length_sq(self) -> f32 {
        self.dot(self)
    }

    /// ベクトルの長さ（ユークリッドノルム）を返す。
    #[inline]
    pub fn length(self) -> f32 {
        self.length_sq().sqrt()
    }

    /// 長さ 1 の単位ベクトルを返す。零ベクトルの場合はそのまま返す。
    #[inline]
    pub fn normalize(self) -> Self {
        let len = self.length();
        if len > 0.0 { self / len } else { self }
    }

    /// YXZ オイラー角（ラジアン）としてクォータニオンに変換する。
    ///
    /// `(x=pitch, y=yaw, z=roll)` と解釈し `Quaternion::from_euler(self)` を呼ぶ。
    #[inline]
    pub fn to_quaternion(self) -> Quaternion {
        Quaternion::from_euler(self)
    }
}

// ─── 型変換 ────────────────────────────────────────────────────

impl<T: Default + Copy> From<(T, T, T)> for Vector3<T> {
    /// タプル `(x, y, z)` から変換する。
    #[inline]
    fn from((x, y, z): (T, T, T)) -> Self {
        Self::new(x, y, z)
    }
}

impl<T: Default + Copy> From<Vector2<T>> for Vector3<T> {
    /// `Vector2` を z=0 で拡張して `Vector3` に変換する。
    #[inline]
    fn from(v: Vector2<T>) -> Self {
        Self::new(v.x, v.y, T::default())
    }
}

// ─── 表示 ──────────────────────────────────────────────────────

/// `(x, y, z)` の形式で表示する。
impl<T: fmt::Display> fmt::Display for Vector3<T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "({}, {}, {})", self.x, self.y, self.z)
    }
}
