// ============================================================
//  math/vector3.rs — 3 次元ベクトル（物理エンジン内部用）
//
//  SEED エンジン本体の Vector3<T> と同一の構造だが、
//  engine 固有の依存（Vector2, Quaternion 参照）を持たない
//  スタンドアロン版。
// ============================================================

use std::fmt;
use std::ops::{
    Add, AddAssign,
    Sub, SubAssign,
    Mul, MulAssign,
    Div, DivAssign,
    Neg,
    Index, IndexMut,
};

/// 3 次元ベクトル（汎用）。物理演算では `Vector3<f32>` として使用する。
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
        Self { x: T::default(), y: T::default(), z: T::default() }
    }
}

// ─── 演算子 ────────────────────────────────────────────────────

impl<T: Add<Output = T> + Copy> Add for Vector3<T> {
    type Output = Self;
    #[inline]
    fn add(self, rhs: Self) -> Self {
        Self::new(self.x + rhs.x, self.y + rhs.y, self.z + rhs.z)
    }
}

impl<T: AddAssign + Copy> AddAssign for Vector3<T> {
    #[inline]
    fn add_assign(&mut self, rhs: Self) {
        self.x += rhs.x;
        self.y += rhs.y;
        self.z += rhs.z;
    }
}

impl<T: Sub<Output = T> + Copy> Sub for Vector3<T> {
    type Output = Self;
    #[inline]
    fn sub(self, rhs: Self) -> Self {
        Self::new(self.x - rhs.x, self.y - rhs.y, self.z - rhs.z)
    }
}

impl<T: SubAssign + Copy> SubAssign for Vector3<T> {
    #[inline]
    fn sub_assign(&mut self, rhs: Self) {
        self.x -= rhs.x;
        self.y -= rhs.y;
        self.z -= rhs.z;
    }
}

impl<T: Mul<Output = T> + Copy> Mul<T> for Vector3<T> {
    type Output = Self;
    #[inline]
    fn mul(self, rhs: T) -> Self {
        Self::new(self.x * rhs, self.y * rhs, self.z * rhs)
    }
}

impl<T: MulAssign + Copy> MulAssign<T> for Vector3<T> {
    #[inline]
    fn mul_assign(&mut self, rhs: T) {
        self.x *= rhs;
        self.y *= rhs;
        self.z *= rhs;
    }
}

impl<T: Div<Output = T> + Copy> Div<T> for Vector3<T> {
    type Output = Self;
    #[inline]
    fn div(self, rhs: T) -> Self {
        Self::new(self.x / rhs, self.y / rhs, self.z / rhs)
    }
}

impl<T: DivAssign + Copy> DivAssign<T> for Vector3<T> {
    #[inline]
    fn div_assign(&mut self, rhs: T) {
        self.x /= rhs;
        self.y /= rhs;
        self.z /= rhs;
    }
}

impl<T: Neg<Output = T> + Copy> Neg for Vector3<T> {
    type Output = Self;
    #[inline]
    fn neg(self) -> Self {
        Self::new(-self.x, -self.y, -self.z)
    }
}

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
    /// 内積: `self · rhs`
    #[inline]
    pub fn dot(self, rhs: Self) -> T {
        self.x * rhs.x + self.y * rhs.y + self.z * rhs.z
    }
}

impl<T: Mul<Output = T> + Sub<Output = T> + Copy> Vector3<T> {
    /// 外積: `self × rhs`
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
    /// ベクトルの長さの 2 乗を返す。
    #[inline]
    pub fn length_sq(self) -> f32 {
        self.dot(self)
    }

    /// ベクトルの長さを返す。
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

impl<T: Default + Copy> From<(T, T, T)> for Vector3<T> {
    #[inline]
    fn from((x, y, z): (T, T, T)) -> Self {
        Self::new(x, y, z)
    }
}

impl<T: Default + Copy> From<[T; 3]> for Vector3<T> {
    #[inline]
    fn from(arr: [T; 3]) -> Self {
        Self::new(arr[0], arr[1], arr[2])
    }
}

impl<T: Copy> From<Vector3<T>> for [T; 3] {
    #[inline]
    fn from(v: Vector3<T>) -> Self {
        [v.x, v.y, v.z]
    }
}

// ─── 表示 ──────────────────────────────────────────────────────

impl<T: fmt::Display> fmt::Display for Vector3<T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "({}, {}, {})", self.x, self.y, self.z)
    }
}
