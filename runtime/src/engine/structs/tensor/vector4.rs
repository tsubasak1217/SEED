use std::fmt;
use std::ops::{
    Add, AddAssign,
    Sub, SubAssign,
    Mul, MulAssign,
    Div, DivAssign,
    Neg,
    Index, IndexMut,
};

use super::vector3::Vector3;

/// 4 次元ベクトル。
///
/// 同次座標として使う場合は `w=1`（位置）または `w=0`（方向）とする。
///
/// # 型パラメータ
/// - `T`: 成分の型。`f32` / `f64` / `i32` など数値型を想定。
///
/// # 成分
/// - `x`: 第 1 成分
/// - `y`: 第 2 成分
/// - `z`: 第 3 成分
/// - `w`: 第 4 成分（同次座標の場合は位置=1, 方向=0）
///
/// # 演算子 (C++ の operator 相当)
/// | Rust トレイト  | 演算       | 例                                     |
/// |---------------|------------|----------------------------------------|
/// | `Add`         | `v + v`    | ベクトル加算                            |
/// | `AddAssign`   | `v += v`   | ベクトル加算代入                         |
/// | `Sub`         | `v - v`    | ベクトル減算                            |
/// | `SubAssign`   | `v -= v`   | ベクトル減算代入                         |
/// | `Mul<T>`      | `v * s`    | スカラー乗算                            |
/// | `MulAssign<T>`| `v *= s`   | スカラー乗算代入                         |
/// | `Div<T>`      | `v / s`    | スカラー除算                            |
/// | `DivAssign<T>`| `v /= s`   | スカラー除算代入                         |
/// | `Neg`         | `-v`       | 符号反転                                |
/// | `Index`       | `v[i]`     | 成分アクセス（0=x, 1=y, 2=z, 3=w）    |
/// | `IndexMut`    | `v[i] = x` | 成分への書き込み                         |
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Vector4<T> {
    pub x: T,
    pub y: T,
    pub z: T,
    pub w: T,
}

// ─── 基本コンストラクタ ─────────────────────────────────────────

impl<T: Copy> Vector4<T> {
    /// 成分を指定してベクトルを生成する。
    #[inline]
    pub fn new(x: T, y: T, z: T, w: T) -> Self {
        Self { x, y, z, w }
    }
}

impl<T: Default + Copy> Vector4<T> {
    /// 零ベクトル (0, 0, 0, 0) を生成する。
    #[inline]
    pub fn zero() -> Self {
        Self { x: T::default(), y: T::default(), z: T::default(), w: T::default() }
    }
}

// ─── 演算子 ────────────────────────────────────────────────────

/// `v + rhs` : ベクトル加算
impl<T: Add<Output = T> + Copy> Add for Vector4<T> {
    type Output = Self;
    #[inline]
    fn add(self, rhs: Self) -> Self {
        Self::new(self.x + rhs.x, self.y + rhs.y, self.z + rhs.z, self.w + rhs.w)
    }
}

/// `v += rhs` : ベクトル加算代入
impl<T: AddAssign + Copy> AddAssign for Vector4<T> {
    #[inline]
    fn add_assign(&mut self, rhs: Self) {
        self.x += rhs.x;
        self.y += rhs.y;
        self.z += rhs.z;
        self.w += rhs.w;
    }
}

/// `v - rhs` : ベクトル減算
impl<T: Sub<Output = T> + Copy> Sub for Vector4<T> {
    type Output = Self;
    #[inline]
    fn sub(self, rhs: Self) -> Self {
        Self::new(self.x - rhs.x, self.y - rhs.y, self.z - rhs.z, self.w - rhs.w)
    }
}

/// `v -= rhs` : ベクトル減算代入
impl<T: SubAssign + Copy> SubAssign for Vector4<T> {
    #[inline]
    fn sub_assign(&mut self, rhs: Self) {
        self.x -= rhs.x;
        self.y -= rhs.y;
        self.z -= rhs.z;
        self.w -= rhs.w;
    }
}

/// `v * scalar` : スカラー乗算
impl<T: Mul<Output = T> + Copy> Mul<T> for Vector4<T> {
    type Output = Self;
    #[inline]
    fn mul(self, rhs: T) -> Self {
        Self::new(self.x * rhs, self.y * rhs, self.z * rhs, self.w * rhs)
    }
}

/// `v *= scalar` : スカラー乗算代入
impl<T: MulAssign + Copy> MulAssign<T> for Vector4<T> {
    #[inline]
    fn mul_assign(&mut self, rhs: T) {
        self.x *= rhs;
        self.y *= rhs;
        self.z *= rhs;
        self.w *= rhs;
    }
}

/// `v / scalar` : スカラー除算
impl<T: Div<Output = T> + Copy> Div<T> for Vector4<T> {
    type Output = Self;
    #[inline]
    fn div(self, rhs: T) -> Self {
        Self::new(self.x / rhs, self.y / rhs, self.z / rhs, self.w / rhs)
    }
}

/// `v /= scalar` : スカラー除算代入
impl<T: DivAssign + Copy> DivAssign<T> for Vector4<T> {
    #[inline]
    fn div_assign(&mut self, rhs: T) {
        self.x /= rhs;
        self.y /= rhs;
        self.z /= rhs;
        self.w /= rhs;
    }
}

/// `-v` : 符号反転（単項マイナス）
impl<T: Neg<Output = T> + Copy> Neg for Vector4<T> {
    type Output = Self;
    #[inline]
    fn neg(self) -> Self {
        Self::new(-self.x, -self.y, -self.z, -self.w)
    }
}

/// `v[i]` : 成分の読み取り（0=x, 1=y, 2=z, 3=w）
impl<T> Index<usize> for Vector4<T> {
    type Output = T;
    #[inline]
    fn index(&self, i: usize) -> &T {
        match i {
            0 => &self.x,
            1 => &self.y,
            2 => &self.z,
            3 => &self.w,
            _ => panic!("Vector4: index {} is out of bounds (max 3)", i),
        }
    }
}

/// `v[i] = val` : 成分への書き込み（0=x, 1=y, 2=z, 3=w）
impl<T> IndexMut<usize> for Vector4<T> {
    #[inline]
    fn index_mut(&mut self, i: usize) -> &mut T {
        match i {
            0 => &mut self.x,
            1 => &mut self.y,
            2 => &mut self.z,
            3 => &mut self.w,
            _ => panic!("Vector4: index {} is out of bounds (max 3)", i),
        }
    }
}

// ─── 数学演算（汎用） ──────────────────────────────────────────

impl<T: Mul<Output = T> + Add<Output = T> + Copy> Vector4<T> {
    /// 内積 (dot product): `self · rhs = x*rhs.x + y*rhs.y + z*rhs.z + w*rhs.w`
    #[inline]
    pub fn dot(self, rhs: Self) -> T {
        self.x * rhs.x + self.y * rhs.y + self.z * rhs.z + self.w * rhs.w
    }
}

// ─── f32 専用メソッド ──────────────────────────────────────────

impl Vector4<f32> {
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

    /// 同次座標として w 除算し、`Vector3` を取り出す。
    /// w=0 の場合（方向ベクトル）はそのまま xyz を返す。
    #[inline]
    pub fn perspective_divide(self) -> Vector3<f32> {
        if self.w != 0.0 {
            Vector3::new(self.x / self.w, self.y / self.w, self.z / self.w)
        } else {
            Vector3::new(self.x, self.y, self.z)
        }
    }
}

// ─── 型変換 ────────────────────────────────────────────────────

impl<T: Default + Copy> From<(T, T, T, T)> for Vector4<T> {
    /// タプル `(x, y, z, w)` から変換する。
    #[inline]
    fn from((x, y, z, w): (T, T, T, T)) -> Self {
        Self::new(x, y, z, w)
    }
}

impl<T: Default + Copy> From<Vector3<T>> for Vector4<T> {
    /// `Vector3` を w=0 で拡張して `Vector4` に変換する（方向ベクトル扱い）。
    #[inline]
    fn from(v: Vector3<T>) -> Self {
        Self::new(v.x, v.y, v.z, T::default())
    }
}

// ─── 表示 ──────────────────────────────────────────────────────

/// `(x, y, z, w)` の形式で表示する。
impl<T: fmt::Display> fmt::Display for Vector4<T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "({}, {}, {}, {})", self.x, self.y, self.z, self.w)
    }
}
