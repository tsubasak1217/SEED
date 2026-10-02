// ============================================================
//  font/msdf/geometry.rs — MSDF の作成で使う 2 次元の幾何（ベクトル・符号つき距離・多項式の解）
//
//  【役割】
//  輪郭の辺（直線・2 次・3 次のベジェ）への距離を求めるための小さな道具をまとめる。
//  座標は **f64**（msdfgen と同じ。テクセルの単位で数十の大きさの値を扱い、3 次方程式の解の誤差を抑える）。
//
//  【座標の向き】
//  MSDF の作成は **y が上向き**（フォントの単位と同じ向き）の空間で行う（msdfgen の式をそのまま使うため。
//  内側の距離が正になる向きの決まりは segment.rs の冒頭）。アトラスへ書くときに行を上から並べ直す（bake.rs）。
// ============================================================

use std::ops::{Add, AddAssign, Mul, Neg, Sub};

/// 2 次元のベクトル（点にも使う）。
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Vec2 {
    pub x: f64,
    pub y: f64,
}

/// `Vec2` を作る短い書き方。
#[inline]
pub const fn v2(x: f64, y: f64) -> Vec2 {
    Vec2 { x, y }
}

impl Vec2 {
    /// 原点（零ベクトル）。
    pub const ZERO: Vec2 = Vec2 { x: 0.0, y: 0.0 };

    /// 内積。
    #[inline]
    pub fn dot(self, o: Vec2) -> f64 {
        self.x * o.x + self.y * o.y
    }

    /// 外積（z 成分）: `self.x * o.y - self.y * o.x`。
    #[inline]
    pub fn cross(self, o: Vec2) -> f64 {
        self.x * o.y - self.y * o.x
    }

    /// 長さ。
    #[inline]
    pub fn length(self) -> f64 {
        self.dot(self).sqrt()
    }

    /// 零ベクトルか（長さが厳密に 0）。
    #[inline]
    pub fn is_zero(self) -> bool {
        self.x == 0.0 && self.y == 0.0
    }

    /// 長さ 1 にしたベクトル。零ベクトルは `allow_zero` が真なら零、偽なら (0, 1)（msdfgen の normalize と同じ）。
    #[inline]
    pub fn normalize(self, allow_zero: bool) -> Vec2 {
        let len = self.length();
        if len != 0.0 {
            v2(self.x / len, self.y / len)
        } else if allow_zero {
            Vec2::ZERO
        } else {
            v2(0.0, 1.0)
        }
    }

    /// 2 点（ベクトル）の線形補間 `self + (o - self) * t`。
    #[inline]
    pub fn lerp(self, o: Vec2, t: f64) -> Vec2 {
        v2(self.x + (o.x - self.x) * t, self.y + (o.y - self.y) * t)
    }
}

impl Add for Vec2 {
    type Output = Vec2;
    #[inline]
    fn add(self, o: Vec2) -> Vec2 {
        v2(self.x + o.x, self.y + o.y)
    }
}

impl AddAssign for Vec2 {
    #[inline]
    fn add_assign(&mut self, o: Vec2) {
        self.x += o.x;
        self.y += o.y;
    }
}

impl Sub for Vec2 {
    type Output = Vec2;
    #[inline]
    fn sub(self, o: Vec2) -> Vec2 {
        v2(self.x - o.x, self.y - o.y)
    }
}

impl Neg for Vec2 {
    type Output = Vec2;
    #[inline]
    fn neg(self) -> Vec2 {
        v2(-self.x, -self.y)
    }
}

impl Mul<f64> for Vec2 {
    type Output = Vec2;
    #[inline]
    fn mul(self, s: f64) -> Vec2 {
        v2(self.x * s, self.y * s)
    }
}

impl Mul<Vec2> for f64 {
    type Output = Vec2;
    #[inline]
    fn mul(self, v: Vec2) -> Vec2 {
        v2(v.x * self, v.y * self)
    }
}

/// 0 を正として扱う符号（+1 / -1）。msdfgen の nonZeroSign と同じ（距離が 0 のときも符号を決めるため）。
#[inline]
pub fn non_zero_sign(v: f64) -> f64 {
    if v > 0.0 { 1.0 } else { -1.0 }
}

/// 3 つの値の中央値（MSDF の 3 チャネルから形の距離を取り出す式）。
#[inline]
pub fn median3(a: f64, b: f64, c: f64) -> f64 {
    a.min(b).max(a.max(b).min(c))
}

/// f32 版の中央値（誤差の補正・検査でチャネルの値を比べるときに使う）。
#[inline]
pub fn median3_f32(a: f32, b: f32, c: f32) -> f32 {
    a.min(b).max(a.max(b).min(c))
}

// ─── 符号つき距離 ─────────────────────────────────────────────

/// 辺への符号つき距離と、同じ距離の辺を比べるための副の値（msdfgen の SignedDistance）。
///
/// - `distance`: 符号つき距離（内側が正。テクセルの単位）
/// - `dot`     : 最近点が辺の端点のとき、辺の向きと「端点 → 点」の向きのなす角の余弦の絶対値。
///   2 つの辺が同じ端点を共有して距離が等しいとき、点の方向により垂直な辺（dot が小さい辺）を選ぶ
///   （その辺の符号が正しい）。最近点が辺の途中なら 0。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SignedDistance {
    pub distance: f64,
    pub dot: f64,
}

impl SignedDistance {
    /// 「まだ何も見ていない」距離（どの実際の距離よりも遠い）。
    pub const INFINITE: SignedDistance = SignedDistance { distance: -f64::MAX, dot: 0.0 };

    /// 値を作る。
    #[inline]
    pub const fn new(distance: f64, dot: f64) -> Self {
        Self { distance, dot }
    }

    /// `self` が `other` より近いか（距離の絶対値、等しければ dot の小さいほう）。
    #[inline]
    pub fn closer_than(&self, other: &SignedDistance) -> bool {
        let a = self.distance.abs();
        let b = other.distance.abs();
        a < b || (a == b && self.dot < other.dot)
    }
}

// ─── 多項式の解（msdfgen の equation-solver と同じ式） ──────────────

/// 3 次の係数がこれより小さい（2 次の係数との比）なら 2 次方程式として解く（数値の誤差のほうが大きくなるため）。
const CUBIC_TO_QUADRATIC_RATIO: f64 = 1e6;
/// 1 次の係数が 2 次の係数のこの倍より大きければ 1 次方程式として解く。
const QUADRATIC_TO_LINEAR_RATIO: f64 = 1e12;
/// 重解とみなす差の比（3 次方程式の 2 つの実根が近いとき）。
const DOUBLE_ROOT_EPSILON: f64 = 1e-12;

/// `a x² + b x + c = 0` を解く。返り値は解の数（`out` の先頭から入る）。全ての x が解（0 = 0）なら -1。
pub fn solve_quadratic(out: &mut [f64; 3], a: f64, b: f64, c: f64) -> i32 {
    // a = 0（または 1 次の項に比べて無視できる）なら 1 次方程式
    if a == 0.0 || b.abs() > QUADRATIC_TO_LINEAR_RATIO * a.abs() {
        if b == 0.0 {
            return if c == 0.0 { -1 } else { 0 };
        }
        out[0] = -c / b;
        return 1;
    }
    let discriminant = b * b - 4.0 * a * c;
    if discriminant > 0.0 {
        let root = discriminant.sqrt();
        out[0] = (-b + root) / (2.0 * a);
        out[1] = (-b - root) / (2.0 * a);
        2
    } else if discriminant == 0.0 {
        out[0] = -b / (2.0 * a);
        1
    } else {
        0
    }
}

/// 正規化した 3 次方程式 `x³ + a x² + b x + c = 0` を解く（カルダノ・三角関数の解法）。
fn solve_cubic_normed(out: &mut [f64; 3], a: f64, b: f64, c: f64) -> i32 {
    let a2 = a * a;
    let mut q = (a2 - 3.0 * b) / 9.0;
    let r = (a * (2.0 * a2 - 9.0 * b) + 27.0 * c) / 54.0;
    let r2 = r * r;
    let q3 = q * q * q;
    let a3 = a / 3.0;
    if r2 < q3 {
        // 3 つの実根
        let t = (r / q3.sqrt()).clamp(-1.0, 1.0).acos();
        q = -2.0 * q.sqrt();
        let third = 1.0 / 3.0;
        out[0] = q * (third * t).cos() - a3;
        out[1] = q * (third * (t + 2.0 * std::f64::consts::PI)).cos() - a3;
        out[2] = q * (third * (t - 2.0 * std::f64::consts::PI)).cos() - a3;
        3
    } else {
        let u = (if r < 0.0 { 1.0 } else { -1.0 }) * (r.abs() + (r2 - q3).sqrt()).powf(1.0 / 3.0);
        let v = if u == 0.0 { 0.0 } else { q / u };
        out[0] = (u + v) - a3;
        if u == v || (u - v).abs() < DOUBLE_ROOT_EPSILON * (u + v).abs() {
            out[1] = -0.5 * (u + v) - a3;
            return 2;
        }
        1
    }
}

/// `a x³ + b x² + c x + d = 0` を解く。返り値は解の数（-1 は全ての x が解）。
pub fn solve_cubic(out: &mut [f64; 3], a: f64, b: f64, c: f64, d: f64) -> i32 {
    if a != 0.0 {
        let bn = b / a;
        // 3 次の係数が小さすぎると正規化の誤差が 2 次として解くより大きくなる
        if bn.abs() < CUBIC_TO_QUADRATIC_RATIO {
            return solve_cubic_normed(out, bn, c / a, d / a);
        }
    }
    solve_quadratic(out, b, c, d)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 中央値は並びによらず真ん中の値。
    #[test]
    fn median_is_middle_value() {
        for (a, b, c) in [(1.0, 2.0, 3.0), (3.0, 1.0, 2.0), (2.0, 3.0, 1.0), (5.0, 5.0, 1.0), (-1.0, 4.0, 0.0)] {
            let mut v = [a, b, c];
            v.sort_by(|x: &f64, y| x.partial_cmp(y).unwrap());
            assert_eq!(median3(a, b, c), v[1]);
            assert_eq!(median3_f32(a as f32, b as f32, c as f32), v[1] as f32);
        }
    }

    /// 2 次方程式: 2 根・重根・虚根・1 次への退化。
    #[test]
    fn quadratic_roots() {
        let mut x = [0.0; 3];
        assert_eq!(solve_quadratic(&mut x, 1.0, -3.0, 2.0), 2);
        let mut r = [x[0], x[1]];
        r.sort_by(|a, b| a.partial_cmp(b).unwrap());
        assert!((r[0] - 1.0).abs() < 1e-12 && (r[1] - 2.0).abs() < 1e-12);
        assert_eq!(solve_quadratic(&mut x, 1.0, 2.0, 1.0), 1);
        assert!((x[0] + 1.0).abs() < 1e-12);
        assert_eq!(solve_quadratic(&mut x, 1.0, 0.0, 1.0), 0);
        assert_eq!(solve_quadratic(&mut x, 0.0, 2.0, -4.0), 1);
        assert!((x[0] - 2.0).abs() < 1e-12);
        assert_eq!(solve_quadratic(&mut x, 0.0, 0.0, 0.0), -1);
    }

    /// 3 次方程式: 3 実根・1 実根・3 次の係数が 0 の退化。どの根も式を満たす。
    #[test]
    fn cubic_roots_satisfy_equation() {
        let cases = [(1.0, -6.0, 11.0, -6.0), (1.0, 0.0, 0.0, -8.0), (2.0, -3.0, -11.0, 6.0), (0.0, 1.0, -3.0, 2.0), (1.0, -3.0, 3.0, -1.0)];
        for (a, b, c, d) in cases {
            let mut x = [0.0; 3];
            let n = solve_cubic(&mut x, a, b, c, d);
            assert!(n >= 1, "解が無い: {a} {b} {c} {d}");
            for &r in x.iter().take(n as usize) {
                let v = a * r * r * r + b * r * r + c * r + d;
                assert!(v.abs() < 1e-6, "根 {r} が式を満たさない（{v}）: {a} {b} {c} {d}");
            }
        }
        // (x-1)(x-2)(x-3): 3 つの根がそろう
        let mut x = [0.0; 3];
        assert_eq!(solve_cubic(&mut x, 1.0, -6.0, 11.0, -6.0), 3);
        let mut r = x;
        r.sort_by(|a, b| a.partial_cmp(b).unwrap());
        assert!((r[0] - 1.0).abs() < 1e-9 && (r[1] - 2.0).abs() < 1e-9 && (r[2] - 3.0).abs() < 1e-9);
    }

    /// 符号つき距離の比較: 絶対値の小さいほう、等しければ dot の小さいほうが近い。
    #[test]
    fn signed_distance_ordering() {
        let a = SignedDistance::new(-1.0, 0.5);
        let b = SignedDistance::new(2.0, 0.0);
        assert!(a.closer_than(&b));
        let c = SignedDistance::new(1.0, 0.2);
        assert!(c.closer_than(&a), "同じ距離なら dot の小さいほう");
        assert!(a.closer_than(&SignedDistance::INFINITE));
    }
}
