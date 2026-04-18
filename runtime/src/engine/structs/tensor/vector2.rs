use std::fmt;
use std::ops::{
    Add, AddAssign,
    Sub, SubAssign,
    Mul, MulAssign,
    Div, DivAssign,
    Neg,
    Index, IndexMut,
};

/// 2 次元ベクトル。
///
/// # 型パラメータ
/// - `T`: 成分の型。`f32` / `f64` / `i32` など数値型を想定。
///
/// # 成分
/// - `x`: 第 1 成分（横方向）
/// - `y`: 第 2 成分（縦方向）
///
/// # 演算子 (C++ の operator 相当)
/// | Rust トレイト  | 演算       | 例                        |
/// |---------------|------------|---------------------------|
/// | `Add`         | `v + v`    | ベクトル加算               |
/// | `AddAssign`   | `v += v`   | ベクトル加算代入            |
/// | `Sub`         | `v - v`    | ベクトル減算               |
/// | `SubAssign`   | `v -= v`   | ベクトル減算代入            |
/// | `Mul<T>`      | `v * s`    | スカラー乗算               |
/// | `MulAssign<T>`| `v *= s`   | スカラー乗算代入            |
/// | `Div<T>`      | `v / s`    | スカラー除算               |
/// | `DivAssign<T>`| `v /= s`   | スカラー除算代入            |
/// | `Neg`         | `-v`       | 符号反転                   |
/// | `Index`       | `v[i]`     | 成分アクセス（0=x, 1=y）   |
/// | `IndexMut`    | `v[i] = x` | 成分への書き込み            |
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Vector2<T> {
    pub x: T,
    pub y: T,
}

// ─── 基本コンストラクタ ─────────────────────────────────────────

impl<T: Copy> Vector2<T> {
    /// 成分を指定してベクトルを生成する。
    #[inline]
    pub fn new(x: T, y: T) -> Self {
        Self { x, y }
    }
}

impl<T: Default + Copy> Vector2<T> {
    /// 零ベクトル (0, 0) を生成する。
    #[inline]
    pub fn zero() -> Self {
        Self { x: T::default(), y: T::default() }
    }
}

// ─── 演算子 ────────────────────────────────────────────────────

/// `v + rhs` : ベクトル加算
impl<T: Add<Output = T> + Copy> Add for Vector2<T> {
    type Output = Self;
    #[inline]
    fn add(self, rhs: Self) -> Self {
        Self::new(self.x + rhs.x, self.y + rhs.y)
    }
}

/// `v += rhs` : ベクトル加算代入
impl<T: AddAssign + Copy> AddAssign for Vector2<T> {
    #[inline]
    fn add_assign(&mut self, rhs: Self) {
        self.x += rhs.x;
        self.y += rhs.y;
    }
}

/// `v - rhs` : ベクトル減算
impl<T: Sub<Output = T> + Copy> Sub for Vector2<T> {
    type Output = Self;
    #[inline]
    fn sub(self, rhs: Self) -> Self {
        Self::new(self.x - rhs.x, self.y - rhs.y)
    }
}

/// `v -= rhs` : ベクトル減算代入
impl<T: SubAssign + Copy> SubAssign for Vector2<T> {
    #[inline]
    fn sub_assign(&mut self, rhs: Self) {
        self.x -= rhs.x;
        self.y -= rhs.y;
    }
}

/// `v * scalar` : スカラー乗算
impl<T: Mul<Output = T> + Copy> Mul<T> for Vector2<T> {
    type Output = Self;
    #[inline]
    fn mul(self, rhs: T) -> Self {
        Self::new(self.x * rhs, self.y * rhs)
    }
}

/// `v *= scalar` : スカラー乗算代入
impl<T: MulAssign + Copy> MulAssign<T> for Vector2<T> {
    #[inline]
    fn mul_assign(&mut self, rhs: T) {
        self.x *= rhs;
        self.y *= rhs;
    }
}

/// `v / scalar` : スカラー除算
impl<T: Div<Output = T> + Copy> Div<T> for Vector2<T> {
    type Output = Self;
    #[inline]
    fn div(self, rhs: T) -> Self {
        Self::new(self.x / rhs, self.y / rhs)
    }
}

/// `v /= scalar` : スカラー除算代入
impl<T: DivAssign + Copy> DivAssign<T> for Vector2<T> {
    #[inline]
    fn div_assign(&mut self, rhs: T) {
        self.x /= rhs;
        self.y /= rhs;
    }
}

/// `-v` : 符号反転（単項マイナス）
impl<T: Neg<Output = T> + Copy> Neg for Vector2<T> {
    type Output = Self;
    #[inline]
    fn neg(self) -> Self {
        Self::new(-self.x, -self.y)
    }
}

/// `v[i]` : 成分の読み取り（0=x, 1=y）
impl<T> Index<usize> for Vector2<T> {
    type Output = T;
    #[inline]
    fn index(&self, i: usize) -> &T {
        match i {
            0 => &self.x,
            1 => &self.y,
            _ => panic!("Vector2: index {} is out of bounds (max 1)", i),
        }
    }
}

/// `v[i] = val` : 成分への書き込み（0=x, 1=y）
impl<T> IndexMut<usize> for Vector2<T> {
    #[inline]
    fn index_mut(&mut self, i: usize) -> &mut T {
        match i {
            0 => &mut self.x,
            1 => &mut self.y,
            _ => panic!("Vector2: index {} is out of bounds (max 1)", i),
        }
    }
}

// ─── 数学演算（汎用） ──────────────────────────────────────────

impl<T: Mul<Output = T> + Add<Output = T> + Copy> Vector2<T> {
    /// 内積 (dot product): `self · rhs = x*rhs.x + y*rhs.y`
    #[inline]
    pub fn dot(self, rhs: Self) -> T {
        self.x * rhs.x + self.y * rhs.y
    }
}

// ─── f32 専用メソッド ──────────────────────────────────────────

impl Vector2<f32> {
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
}

// ─── 型変換 ────────────────────────────────────────────────────

impl<T: Default + Copy> From<(T, T)> for Vector2<T> {
    /// タプル `(x, y)` から変換する。
    #[inline]
    fn from((x, y): (T, T)) -> Self {
        Self::new(x, y)
    }
}

impl<T: Copy> From<Vector2<T>> for (T, T) {
    /// タプル `(x, y)` へ変換する。
    #[inline]
    fn from(v: Vector2<T>) -> (T, T) {
        (v.x, v.y)
    }
}

// ─── 表示 ──────────────────────────────────────────────────────

/// `(x, y)` の形式で表示する。
impl<T: fmt::Display> fmt::Display for Vector2<T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "({}, {})", self.x, self.y)
    }
}
