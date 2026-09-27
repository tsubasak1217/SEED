// ============================================================
//  ui_shape/sdf.rs — 形の SDF（符号付き距離）とアンチエイリアス（W2-4）
//
//  距離は「形の空間」（スプライトの矩形 [0, 幅]×[0, 高さ]・Y 下向き・キャンバスの単位）で、負 = 内側・正 = 外側。
//  shaders/sprite_shape.wgsl の同名の関数と**同じ式**（ここは単体テストで検算するための写し）。
//
//  | 形            | 式                                                                                   |
//  |---------------|--------------------------------------------------------------------------------------|
//  | 角丸の矩形    | Inigo Quilez の sdRoundBox を四隅ごとの半径にしたもの（点のある象限の角の半径を使う）      |
//  | 楕円          | 近似 k0 (k0 − 1) / k1（k0 = |p/r|、k1 = |p/r²|）。円なら厳密、縁の近くで十分に正確        |
//  | 弧（リング）  | 中心線の半径 ra・半分の太さ rb。角度の範囲の中は |‖p‖ − ra| − rb、外は端（丸い端 = 端点の円、 |
//  |               | 切りっぱなし = 端の線分）までの距離                                                     |
//
//  アンチエイリアス: 被覆率 = clamp(0.5 − d / w, 0, 1)（w = 画面の 1 画素が形の空間で何単位か）。
//  境界 d = 0 で 0.5、内側へ 0.5 画素で 1。画素の格子に揃った直線の辺は従来のラスタライズと同じにくっきり出る。
//  影: 被覆率 = 0.5 (1 − erf(d / (σ√2)))（σ = ぼかし ÷ 2。ガウス関数でぼかした半平面の近似）。
// ============================================================

use std::f32::consts::{SQRT_2, TAU};

use crate::engine::components::{SpriteShape, SpriteShapeKind};

/// 半分（中心・被覆率の境界）。
const HALF: f32 = 0.5;
/// 0 とみなす長さ（割り算の保護）。
const LENGTH_EPSILON: f32 = 1e-6;
/// 影のぼかしの σ をぼかしの幅の何倍にするか（CSS の box-shadow と同じ σ = ぼかし ÷ 2）。
pub const SHADOW_SIGMA_PER_BLUR: f32 = 0.5;
/// 影の四角形を形の外へ何 σ 広げるか（3σ の外の被覆率は 0.0014 未満）。
pub const SHADOW_EXTENT_SIGMAS: f32 = 3.0;

// ── erf の近似（Abramowitz & Stegun 7.1.26。最大誤差 1.5e-7）の係数 ──
/// 係数 p。
const ERF_P: f32 = 0.327_591_1;
/// 係数 a1。
const ERF_A1: f32 = 0.254_829_592;
/// 係数 a2。
const ERF_A2: f32 = -0.284_496_736;
/// 係数 a3。
const ERF_A3: f32 = 1.421_413_741;
/// 係数 a4。
const ERF_A4: f32 = -1.453_152_027;
/// 係数 a5。
const ERF_A5: f32 = 1.061_405_429;

/// 描く形の種類（描画側。コンポーネントの形から決める）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ShapeGeomKind {
    /// 形の SDF を使わない（矩形いっぱい・被覆率 1。グラデーション・9 スライスだけのスプライト、切り抜きだけ受けるスプライト）。
    None,
    /// 角丸の矩形（半径 0 の角は直角）。
    RoundedRect,
    /// 矩形に内接する楕円。
    Ellipse,
    /// 矩形の中心の円の弧（リング）。
    Arc,
}

/// 弧の形（形の空間の中心に置く）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ArcGeom {
    /// 中心線の半径。
    pub radius: f32,
    /// 太さの半分。
    pub half_thickness: f32,
    /// 開始角（ラジアン。0 = +X・時計回りが正）。
    pub start: f32,
    /// 角度（ラジアン。0..=2π。2π 以上は一周）。
    pub sweep: f32,
    /// 端を丸くするか。
    pub round_caps: bool,
}

/// 描く形（形の空間の大きさと、種類ごとの寸法）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ShapeGeom {
    /// 種類。
    pub kind: ShapeGeomKind,
    /// 形の空間の大きさ（幅・高さ。キャンバスの単位）。
    pub size: [f32; 2],
    /// 四隅の半径（左上・右上・右下・左下。縮め済み）。RoundedRect のときだけ使う。
    pub radii: [f32; 4],
    /// 弧の寸法。Arc のときだけ使う。
    pub arc: ArcGeom,
}

impl ShapeGeom {
    /// 形を使わない矩形（被覆率 1）。
    pub fn none(size: [f32; 2]) -> Self {
        Self { kind: ShapeGeomKind::None, size, radii: [0.0; 4], arc: ArcGeom::NONE }
    }

    /// 角丸の矩形（半径は `clamp_radii` で縮める）。
    pub fn rounded_rect(size: [f32; 2], radii: [f32; 4]) -> Self {
        Self { kind: ShapeGeomKind::RoundedRect, size, radii: clamp_radii(size, radii), arc: ArcGeom::NONE }
    }

    /// 矩形に内接する楕円。
    pub fn ellipse(size: [f32; 2]) -> Self {
        Self { kind: ShapeGeomKind::Ellipse, size, radii: [0.0; 4], arc: ArcGeom::NONE }
    }

    /// コンポーネントの形から描く形を決める【純関数】。
    ///
    /// 直角の矩形・縁なし（`is_plain_rect`）は None（形の SDF を使わない）。縁だけある直角の矩形は
    /// RoundedRect（半径 0）にする（縁の線を SDF で引くため）。
    ///
    /// # 引数
    /// * `shape` - コンポーネントの形
    /// * `size`  - 形の空間の大きさ（キャンバスの単位）
    pub fn from_sprite_shape(shape: &SpriteShape, size: [f32; 2]) -> Self {
        match shape.kind {
            SpriteShapeKind::Rect if shape.is_plain_rect() => Self::none(size),
            SpriteShapeKind::Rect => Self::rounded_rect(size, shape.corner_radii),
            SpriteShapeKind::Ellipse => Self::ellipse(size),
            SpriteShapeKind::Arc => Self {
                kind: ShapeGeomKind::Arc,
                size,
                radii: [0.0; 4],
                arc: ArcGeom::from_shape(shape, size),
            },
        }
    }

    /// 形の空間の点 `p`（矩形の左上が原点）の符号付き距離【純関数】。None は矩形の SDF（矩形いっぱい）。
    pub fn distance(&self, p: [f32; 2]) -> f32 {
        let half = [self.size[0] * HALF, self.size[1] * HALF];
        let c = [p[0] - half[0], p[1] - half[1]];
        match self.kind {
            ShapeGeomKind::None => sd_rounded_rect(c, half, [0.0; 4]),
            ShapeGeomKind::RoundedRect => sd_rounded_rect(c, half, self.radii),
            ShapeGeomKind::Ellipse => sd_ellipse(c, half),
            ShapeGeomKind::Arc => sd_arc(c, &self.arc),
        }
    }
}

impl ArcGeom {
    /// 弧を使わないときの値（使われない）。
    pub const NONE: ArcGeom = ArcGeom { radius: 0.0, half_thickness: 0.0, start: 0.0, sweep: TAU, round_caps: false };

    /// コンポーネントの弧の欄から、矩形の中心に置く弧を決める【純関数】。
    ///
    /// 外側の半径 = 短い辺の半分、太さは 0〜外側の半径に収める。角度は 0〜360 度に収める。
    pub fn from_shape(shape: &SpriteShape, size: [f32; 2]) -> Self {
        let outer = size[0].min(size[1]).max(0.0) * HALF;
        let thickness = shape.arc_thickness.clamp(0.0, outer);
        Self {
            radius: outer - thickness * HALF,
            half_thickness: thickness * HALF,
            start: shape.arc_start.to_radians(),
            sweep: shape.arc_sweep.to_radians().clamp(0.0, TAU),
            round_caps: shape.arc_round_caps,
        }
    }
}

/// 四隅の半径を CSS と同じ規則で縮める【純関数】。
///
/// 負の半径は 0。同じ辺に接する 2 つの半径の和が辺の長さを超えるなら、4 つすべてを同じ割合
/// （辺ごとの「辺の長さ ÷ 半径の和」の最小）で縮める（CSS Backgrounds 3 §5.5 の「角の重なり」）。
///
/// # 引数
/// * `size`  - 矩形の大きさ（幅・高さ）
/// * `radii` - 左上・右上・右下・左下の半径
pub fn clamp_radii(size: [f32; 2], radii: [f32; 4]) -> [f32; 4] {
    let r = radii.map(|v| if v.is_finite() { v.max(0.0) } else { 0.0 });
    let [w, h] = [size[0].max(0.0), size[1].max(0.0)];
    // (辺の長さ, その辺に接する 2 つの半径の和)。上・下・左・右
    let sides = [(w, r[0] + r[1]), (w, r[3] + r[2]), (h, r[0] + r[3]), (h, r[1] + r[2])];
    let factor = sides
        .iter()
        .filter(|(_, sum)| *sum > LENGTH_EPSILON)
        .map(|(len, sum)| len / sum)
        .fold(1.0f32, f32::min);
    r.map(|v| v * factor)
}

/// 角丸の矩形の符号付き距離【純関数】（中心が原点・Y 下向き）。
///
/// # 引数
/// * `p`     - 点（矩形の中心からの位置）
/// * `half`  - 矩形の大きさの半分
/// * `radii` - 左上・右上・右下・左下の半径（縮め済み）
pub fn sd_rounded_rect(p: [f32; 2], half: [f32; 2], radii: [f32; 4]) -> f32 {
    // 点のある象限の角の半径（Y 下向きなので p.y < 0 が上）
    let r = if p[0] < 0.0 {
        if p[1] < 0.0 { radii[0] } else { radii[3] }
    } else if p[1] < 0.0 {
        radii[1]
    } else {
        radii[2]
    };
    let q = [p[0].abs() - half[0] + r, p[1].abs() - half[1] + r];
    let outside = [q[0].max(0.0), q[1].max(0.0)];
    q[0].max(q[1]).min(0.0) + (outside[0] * outside[0] + outside[1] * outside[1]).sqrt() - r
}

/// 楕円の符号付き距離の近似【純関数】（中心が原点）。円（半径が等しい）なら厳密。
///
/// # 引数
/// * `p` - 点（楕円の中心からの位置）
/// * `r` - 半径（x・y）
pub fn sd_ellipse(p: [f32; 2], r: [f32; 2]) -> f32 {
    if r[0] <= LENGTH_EPSILON || r[1] <= LENGTH_EPSILON {
        return f32::INFINITY;
    }
    let a = [p[0] / r[0], p[1] / r[1]];
    let b = [p[0] / (r[0] * r[0]), p[1] / (r[1] * r[1])];
    let k0 = (a[0] * a[0] + a[1] * a[1]).sqrt();
    let k1 = (b[0] * b[0] + b[1] * b[1]).sqrt();
    if k1 <= LENGTH_EPSILON {
        // 中心ちょうど: 内側へ短い半径の分
        return -r[0].min(r[1]);
    }
    k0 * (k0 - 1.0) / k1
}

/// 点から線分 [a, b] までの距離【純関数】。
fn segment_distance(p: [f32; 2], a: [f32; 2], b: [f32; 2]) -> f32 {
    let ab = [b[0] - a[0], b[1] - a[1]];
    let ap = [p[0] - a[0], p[1] - a[1]];
    let len2 = ab[0] * ab[0] + ab[1] * ab[1];
    let t = if len2 > LENGTH_EPSILON { ((ap[0] * ab[0] + ap[1] * ab[1]) / len2).clamp(0.0, 1.0) } else { 0.0 };
    let d = [ap[0] - ab[0] * t, ap[1] - ab[1] * t];
    (d[0] * d[0] + d[1] * d[1]).sqrt()
}

/// 弧（リング）の符号付き距離【純関数】（中心が原点・Y 下向き・角度は時計回り）。
///
/// # 引数
/// * `p`   - 点（弧の中心からの位置）
/// * `arc` - 弧の寸法
pub fn sd_arc(p: [f32; 2], arc: &ArcGeom) -> f32 {
    let len = (p[0] * p[0] + p[1] * p[1]).sqrt();
    let ring = (len - arc.radius).abs() - arc.half_thickness;
    if arc.sweep >= TAU {
        return ring;
    }
    // 開始角からの角度（0..2π）
    let mut theta = p[1].atan2(p[0]) - arc.start;
    theta -= (theta / TAU).floor() * TAU;
    let e0 = [arc.start.cos(), arc.start.sin()];
    let end = arc.start + arc.sweep;
    let e1 = [end.cos(), end.sin()];
    if arc.round_caps {
        if theta <= arc.sweep {
            return ring;
        }
        // 範囲の外: 端点の円（中心線上の端点・半径 = 太さの半分）までの距離
        let d0 = ((p[0] - e0[0] * arc.radius).powi(2) + (p[1] - e0[1] * arc.radius).powi(2)).sqrt();
        let d1 = ((p[0] - e1[0] * arc.radius).powi(2) + (p[1] - e1[1] * arc.radius).powi(2)).sqrt();
        return d0.min(d1) - arc.half_thickness;
    }
    // 切りっぱなしの端: 内側の半径から外側の半径までの線分
    let inner = arc.radius - arc.half_thickness;
    let outer = arc.radius + arc.half_thickness;
    let cap0 = segment_distance(p, [e0[0] * inner, e0[1] * inner], [e0[0] * outer, e0[1] * outer]);
    let cap1 = segment_distance(p, [e1[0] * inner, e1[1] * inner], [e1[0] * outer, e1[1] * outer]);
    let cap = cap0.min(cap1);
    if theta <= arc.sweep {
        // 範囲の中: リングの縁か端の線のうち近い方（内側なので負）
        ring.max(-cap)
    } else {
        cap
    }
}

/// 画素の幅のアンチエイリアスの被覆率【純関数】。
///
/// # 引数
/// * `d`  - 符号付き距離（形の空間）
/// * `aa` - 画面の 1 画素が形の空間で何単位か（0 以下ならくっきり＝境界の内側だけ 1）
pub fn coverage(d: f32, aa: f32) -> f32 {
    if aa <= 0.0 {
        return if d <= 0.0 { 1.0 } else { 0.0 };
    }
    (HALF - d / aa).clamp(0.0, 1.0)
}

/// erf の近似（Abramowitz & Stegun 7.1.26）【純関数】。
pub fn erf_approx(x: f32) -> f32 {
    let sign = if x < 0.0 { -1.0 } else { 1.0 };
    let x = x.abs();
    let t = 1.0 / (1.0 + ERF_P * x);
    let poly = ((((ERF_A5 * t + ERF_A4) * t + ERF_A3) * t + ERF_A2) * t + ERF_A1) * t;
    sign * (1.0 - poly * (-x * x).exp())
}

/// ぼかしの影の被覆率【純関数】（σ がアンチエイリアスの幅より小さければ通常の被覆率）。
///
/// # 引数
/// * `d`     - 影の形の符号付き距離
/// * `sigma` - ガウス関数の σ（形の空間）
/// * `aa`    - 画面の 1 画素が形の空間で何単位か
pub fn shadow_coverage(d: f32, sigma: f32, aa: f32) -> f32 {
    if sigma <= aa.max(LENGTH_EPSILON) {
        return coverage(d, aa);
    }
    HALF * (1.0 - erf_approx(d / (sigma * SQRT_2)))
}

// ============================================================
//  単体テスト（SDF の距離・縁・アンチエイリアス）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 浮動小数の比較の許容量。
    const EPS: f32 = 1e-4;

    fn close(a: f32, b: f32) -> bool {
        (a - b).abs() <= EPS
    }

    /// 角の直角な矩形: 辺までの距離、中心は −短い辺の半分、角の外は角までのユークリッド距離。
    #[test]
    fn rect_distance() {
        let g = ShapeGeom::rounded_rect([100.0, 50.0], [0.0; 4]);
        assert!(close(g.distance([50.0, 25.0]), -25.0), "中心");
        assert!(close(g.distance([0.0, 25.0]), 0.0), "左の辺の上");
        assert!(close(g.distance([-3.0, 25.0]), 3.0), "左の辺の外 3");
        assert!(close(g.distance([103.0, 54.0]), 5.0), "右下の角の外 (3,4) → 5");
        // 形を使わない（None）も同じ矩形の距離
        assert!(close(ShapeGeom::none([100.0, 50.0]).distance([-3.0, 25.0]), 3.0));
    }

    /// 四隅ごとの角丸: 半径 r の角の中心から r の円弧が境界。半径の違う角はそれぞれの半径で丸まる。
    #[test]
    fn rounded_rect_per_corner_radii() {
        // 左上 20・右上 0・右下 10・左下 0
        let g = ShapeGeom::rounded_rect([100.0, 60.0], [20.0, 0.0, 10.0, 0.0]);
        // 左上の角の頂点 (0,0) は角の円の中心 (20,20) から 20√2 → 距離 20√2 − 20
        assert!(close(g.distance([0.0, 0.0]), 20.0 * SQRT_2 - 20.0));
        // 左上の角の円の上の点は境界
        let a = std::f32::consts::FRAC_PI_4;
        assert!(close(g.distance([20.0 - 20.0 * a.cos(), 20.0 - 20.0 * a.sin()]), 0.0));
        // 右上（半径 0）の頂点は境界ちょうど
        assert!(close(g.distance([100.0, 0.0]), 0.0));
        // 右下（半径 10）の頂点は 10√2 − 10
        assert!(close(g.distance([100.0, 60.0]), 10.0 * SQRT_2 - 10.0));
        // 左下（半径 0）の頂点は境界
        assert!(close(g.distance([0.0, 60.0]), 0.0));
    }

    /// 半径が辺より大きいと CSS と同じ規則で一律に縮める（半径の和が辺の長さに収まる）。
    #[test]
    fn radii_are_clamped_like_css() {
        // 幅 100・高さ 40 に全角 50 → 縦の辺（40）で 50+50=100 → 0.4 倍 → 20
        let r = clamp_radii([100.0, 40.0], [50.0; 4]);
        for v in r {
            assert!(close(v, 20.0));
        }
        // 収まっていれば変えない・負は 0
        assert_eq!(clamp_radii([100.0, 40.0], [10.0, -5.0, 3.0, 0.0]), [10.0, 0.0, 3.0, 0.0]);
        // 全角が半分 = 短い辺の半分（丸いピル形）
        let pill = clamp_radii([120.0, 48.0], [999.0; 4]);
        assert!(close(pill[0], 24.0));
    }

    /// 円（正方形の楕円）は厳密な距離、楕円は縁の上で 0。
    #[test]
    fn ellipse_distance() {
        let circle = ShapeGeom::ellipse([80.0, 80.0]);
        assert!(close(circle.distance([40.0, 40.0]), -40.0), "中心");
        assert!(close(circle.distance([40.0, 0.0]), 0.0), "上の縁");
        assert!(close(circle.distance([0.0, 0.0]), 40.0 * SQRT_2 - 40.0), "角（円の外）");
        let e = ShapeGeom::ellipse([200.0, 100.0]);
        assert!(close(e.distance([0.0, 50.0]), 0.0), "左の縁");
        assert!(close(e.distance([100.0, 0.0]), 0.0), "上の縁");
        assert!(e.distance([100.0, 50.0]) < 0.0 && e.distance([0.0, 0.0]) > 0.0);
    }

    /// 弧: 一周のリング、角度の範囲の中と外、丸い端と切りっぱなしの端。
    #[test]
    fn arc_distance() {
        let shape = SpriteShape {
            kind: SpriteShapeKind::Arc,
            arc_start: -90.0,
            arc_sweep: 90.0,
            arc_thickness: 10.0,
            ..SpriteShape::default()
        };
        let g = ShapeGeom::from_sprite_shape(&shape, [100.0, 100.0]);
        // 中心線の半径 = 50 − 5 = 45。12 時 → 3 時の四分円
        assert!(close(g.arc.radius, 45.0) && close(g.arc.half_thickness, 5.0));
        let c = [50.0, 50.0];
        // 1 時半の方向（範囲の中）の中心線の上は −5
        let a = -std::f32::consts::FRAC_PI_4;
        assert!(close(g.distance([c[0] + 45.0 * a.cos(), c[1] + 45.0 * a.sin()]), -5.0));
        // 9 時の方向（範囲の外）の中心線の上は外（端の線分までの距離）
        assert!(g.distance([c[0] - 45.0, c[1]]) > 40.0);
        // 丸い端: 12 時の端点の外側 5 は境界（端の円の上）
        let round = ShapeGeom::from_sprite_shape(&SpriteShape { arc_round_caps: true, ..shape.clone() }, [100.0, 100.0]);
        assert!(close(round.distance([c[0] - 5.0, c[1] - 45.0]), 0.0));
        // 切りっぱなし: 同じ点は端の線分から 5 の外
        assert!(close(g.distance([c[0] - 5.0, c[1] - 45.0]), 5.0));
        // 一周（360 度）はリング
        let full = ShapeGeom::from_sprite_shape(&SpriteShape { arc_sweep: 360.0, ..shape }, [100.0, 100.0]);
        assert!(close(full.distance([c[0] - 45.0, c[1]]), -5.0));
    }

    /// アンチエイリアス: 境界で 0.5、内側へ半画素で 1、外側へ半画素で 0。画素の幅に比例する。
    #[test]
    fn coverage_is_pixel_width() {
        assert!(close(coverage(0.0, 1.0), 0.5));
        assert!(close(coverage(-0.5, 1.0), 1.0));
        assert!(close(coverage(0.5, 1.0), 0.0));
        // 1 画素 = 2 単位（dp 0.5 倍の画面）なら境界の幅も 2 単位
        assert!(close(coverage(0.5, 2.0), 0.25));
        // 画素の格子に揃った辺（x = 10）: 画素の中心 9.5 は外 → 0、10.5 は内 → 1（従来どおりくっきり）
        let g = ShapeGeom::rounded_rect([20.0, 20.0], [0.0; 4]);
        let offset = 10.0; // 矩形の左端を x = 10 に置いたとみなす
        assert!(close(coverage(g.distance([9.5 - offset, 10.0]), 1.0), 0.0));
        assert!(close(coverage(g.distance([10.5 - offset, 10.0]), 1.0), 1.0));
    }

    /// 縁の線（内側に引く）: 縁の内側の境界は d = −太さ。
    #[test]
    fn border_band_is_inside() {
        let g = ShapeGeom::rounded_rect([100.0, 100.0], [0.0; 4]);
        let border = 4.0;
        // 左の辺から 2 の点は縁の中（外形の内側・縁の内側の境界の外）
        let d = g.distance([2.0, 50.0]);
        assert!(coverage(d, 1.0) > 0.99 && coverage(d + border, 1.0) < 0.01);
        // 左の辺から 6 の点は塗りの中
        assert!(coverage(g.distance([6.0, 50.0]) + border, 1.0) > 0.99);
    }

    /// 影: 境界で 0.5、σ の 3 倍の外でほぼ 0、内側でほぼ 1。σ が画素より小さいと通常の被覆率。
    #[test]
    fn shadow_coverage_is_gaussian_edge() {
        let sigma = 4.0;
        assert!(close(shadow_coverage(0.0, sigma, 1.0), 0.5));
        assert!(shadow_coverage(SHADOW_EXTENT_SIGMAS * sigma, sigma, 1.0) < 0.002);
        assert!(shadow_coverage(-SHADOW_EXTENT_SIGMAS * sigma, sigma, 1.0) > 0.998);
        assert!(close(shadow_coverage(0.25, 0.1, 1.0), coverage(0.25, 1.0)));
        // erf の近似の誤差（既知の値 erf(1) = 0.8427008）
        assert!((erf_approx(1.0) - 0.842_700_8).abs() < 1e-5);
        assert!((erf_approx(-0.5) + 0.520_499_9).abs() < 1e-5);
    }

    /// コンポーネントの形 → 描く形: 直角の矩形・縁なしは None、縁だけは半径 0 の角丸。
    #[test]
    fn geom_kind_from_component() {
        let size = [10.0, 10.0];
        assert_eq!(ShapeGeom::from_sprite_shape(&SpriteShape::default(), size).kind, ShapeGeomKind::None);
        let border = SpriteShape { border_width: 1.0, ..SpriteShape::default() };
        assert_eq!(ShapeGeom::from_sprite_shape(&border, size).kind, ShapeGeomKind::RoundedRect);
        let ellipse = SpriteShape { kind: SpriteShapeKind::Ellipse, ..SpriteShape::default() };
        assert_eq!(ShapeGeom::from_sprite_shape(&ellipse, size).kind, ShapeGeomKind::Ellipse);
    }
}
