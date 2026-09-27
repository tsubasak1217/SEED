// ============================================================
//  ui_shape/fill.rs — グラデーションの塗り（W2-4）
//
//  shaders/sprite_shape.wgsl の `gradient_t`・`sample_gradient` と同じ式（ここは単体テストで検算するための写し）。
//
//  | 種類   | 位置 t（0..1 に収める）                                                                        |
//  |--------|-----------------------------------------------------------------------------------------------|
//  | 線形   | 端点 p0 → p1 への射影。p0・p1 は矩形の中心を通る角度 a の直線上で、四隅がちょうど 0 と 1 に乗る      |
//  |        | 半分の長さ L = |w/2 · cos a| + |h/2 · sin a|（CSS の linear-gradient と同じ）                    |
//  | 放射   | |(p − 中心) / 半径|（中心・半径は矩形に対する割合を形の空間へ直したもの）                         |
//
//  色: 2〜4 色（足りなければ最後の色を繰り返す・多ければ 4 色まで）にスプライトの color を掛けたもの。
//  色の位置は昇順の 0..1（空・足りなければ等間隔、範囲の外は収め、逆順は直前の値へ揃える）。
//  補間は乗算済みアルファ（rgb × a を補間してから戻す。透明へのグラデーションで色が濁らない。CSS と同じ）。
// ============================================================

use crate::engine::components::sprite_style::{GRADIENT_MAX_COLORS, GRADIENT_MIN_COLORS};
use crate::engine::components::{SpriteFill, SpriteFillKind};

/// 半分。
const HALF: f32 = 0.5;
/// 0 とみなす長さ・アルファ（割り算の保護）。
const EPSILON: f32 = 1e-6;
/// 色が 1 つも無いときの色（白＝スプライトの color だけが効く）。
const WHITE: [f32; 4] = [1.0, 1.0, 1.0, 1.0];

/// 描くグラデーションの形（形の空間）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum GradientGeom {
    /// 単色（colors[0]）。
    Solid,
    /// 線形（端点 p0 → p1）。
    Linear {
        /// t = 0 の点。
        p0: [f32; 2],
        /// t = 1 の点。
        p1: [f32; 2],
    },
    /// 放射（中心・半径）。
    Radial {
        /// 中心。
        center: [f32; 2],
        /// 半径（x・y）。
        radius: [f32; 2],
    },
}

/// 塗りの欄から、形の空間でのグラデーションの形を決める【純関数】。
///
/// # 引数
/// * `fill` - 塗りの欄
/// * `size` - 形の空間の大きさ
pub fn gradient_geom(fill: &SpriteFill, size: [f32; 2]) -> GradientGeom {
    match fill.kind {
        SpriteFillKind::Solid => GradientGeom::Solid,
        SpriteFillKind::Linear => {
            let (p0, p1) = linear_endpoints(size, fill.angle);
            GradientGeom::Linear { p0, p1 }
        }
        SpriteFillKind::Radial => GradientGeom::Radial {
            center: [fill.center[0] * size[0], fill.center[1] * size[1]],
            radius: [fill.radius[0] * size[0], fill.radius[1] * size[1]],
        },
    }
}

/// 線形グラデーションの端点（CSS と同じ、四隅が 0 と 1 に乗る長さ）【純関数】。
///
/// # 引数
/// * `size`      - 形の空間の大きさ
/// * `angle_deg` - 角度（度。0 = 左 → 右、90 = 上 → 下）
pub fn linear_endpoints(size: [f32; 2], angle_deg: f32) -> ([f32; 2], [f32; 2]) {
    let a = angle_deg.to_radians();
    let dir = [a.cos(), a.sin()];
    let half_len = (size[0] * HALF * dir[0]).abs() + (size[1] * HALF * dir[1]).abs();
    let c = [size[0] * HALF, size[1] * HALF];
    (
        [c[0] - dir[0] * half_len, c[1] - dir[1] * half_len],
        [c[0] + dir[0] * half_len, c[1] + dir[1] * half_len],
    )
}

/// 点の位置 t（0..1）【純関数】。
///
/// # 引数
/// * `geom` - グラデーションの形
/// * `p`    - 形の空間の点
pub fn gradient_t(geom: &GradientGeom, p: [f32; 2]) -> f32 {
    let t = match *geom {
        GradientGeom::Solid => 0.0,
        GradientGeom::Linear { p0, p1 } => {
            let ab = [p1[0] - p0[0], p1[1] - p0[1]];
            let len2 = (ab[0] * ab[0] + ab[1] * ab[1]).max(EPSILON);
            ((p[0] - p0[0]) * ab[0] + (p[1] - p0[1]) * ab[1]) / len2
        }
        GradientGeom::Radial { center, radius } => {
            let q = [(p[0] - center[0]) / radius[0].max(EPSILON), (p[1] - center[1]) / radius[1].max(EPSILON)];
            (q[0] * q[0] + q[1] * q[1]).sqrt()
        }
    };
    t.clamp(0.0, 1.0)
}

/// 色の数（2〜4。単色は 1）【純関数】。
pub fn color_count(fill: &SpriteFill) -> usize {
    if fill.is_solid() {
        1
    } else {
        fill.colors.len().clamp(GRADIENT_MIN_COLORS, GRADIENT_MAX_COLORS)
    }
}

/// シェーダーへ渡す色の表（スプライトの color を掛けたもの。空きは最後の色）【純関数】。
///
/// # 引数
/// * `fill` - 塗りの欄
/// * `tint` - スプライトの color（乗算の色）
///
/// # 戻り値
/// (色の表, 色の数)。単色なら表の先頭 = tint・数 1。
pub fn resolve_colors(fill: &SpriteFill, tint: [f32; 4]) -> ([[f32; 4]; GRADIENT_MAX_COLORS], usize) {
    let count = color_count(fill);
    let mut out = [tint; GRADIENT_MAX_COLORS];
    if fill.is_solid() {
        return (out, count);
    }
    let last = fill.colors.last().copied().unwrap_or(WHITE);
    for (i, slot) in out.iter_mut().enumerate() {
        let c = fill.colors.get(i).copied().unwrap_or(last);
        *slot = [c[0] * tint[0], c[1] * tint[1], c[2] * tint[2], c[3] * tint[3]];
    }
    (out, count)
}

/// 色の位置を整える（等間隔の補い・範囲へ収める・昇順へ揃える）【純関数】。
///
/// # 引数
/// * `stops` - 欄の値（空・足りなければ等間隔）
/// * `count` - 色の数（1〜4）
pub fn normalized_stops(stops: &[f32], count: usize) -> [f32; GRADIENT_MAX_COLORS] {
    let count = count.clamp(1, GRADIENT_MAX_COLORS);
    let mut out = [1.0f32; GRADIENT_MAX_COLORS];
    if count == 1 {
        out[0] = 0.0;
        return out;
    }
    let even = stops.len() < count;
    let mut prev = 0.0f32;
    for (i, slot) in out.iter_mut().enumerate().take(count) {
        let v = if even { i as f32 / (count - 1) as f32 } else { stops[i].clamp(0.0, 1.0) };
        // 逆順は直前の値へ揃える（昇順を保つ）
        let v = if i == 0 { v } else { v.max(prev) };
        *slot = v;
        prev = v;
    }
    out
}

/// 乗算済みアルファにする。
fn premultiply(c: [f32; 4]) -> [f32; 4] {
    [c[0] * c[3], c[1] * c[3], c[2] * c[3], c[3]]
}

/// 乗算済みアルファから戻す（アルファ 0 は黒の透明）。
fn unpremultiply(c: [f32; 4]) -> [f32; 4] {
    if c[3] <= EPSILON {
        return [0.0, 0.0, 0.0, 0.0];
    }
    [c[0] / c[3], c[1] / c[3], c[2] / c[3], c[3]]
}

/// 位置 t の色（乗算済みアルファで補間して戻したもの）【純関数】。
///
/// # 引数
/// * `colors` - 色の表
/// * `stops`  - 色の位置（`normalized_stops` の結果）
/// * `count`  - 色の数（1〜4）
/// * `t`      - 位置（0..1）
pub fn sample_gradient(
    colors: &[[f32; 4]; GRADIENT_MAX_COLORS],
    stops: &[f32; GRADIENT_MAX_COLORS],
    count: usize,
    t: f32,
) -> [f32; 4] {
    let count = count.clamp(1, GRADIENT_MAX_COLORS);
    if count == 1 || t <= stops[0] {
        return colors[0];
    }
    for i in 1..count {
        if t <= stops[i] {
            let span = (stops[i] - stops[i - 1]).max(EPSILON);
            let f = ((t - stops[i - 1]) / span).clamp(0.0, 1.0);
            let (a, b) = (premultiply(colors[i - 1]), premultiply(colors[i]));
            let mixed = [0, 1, 2, 3].map(|k| a[k] + (b[k] - a[k]) * f);
            return unpremultiply(mixed);
        }
    }
    colors[count - 1]
}

// ============================================================
//  単体テスト（グラデーションの色の補間）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 浮動小数の比較の許容量。
    const EPS: f32 = 1e-4;

    fn close4(a: [f32; 4], b: [f32; 4]) -> bool {
        (0..4).all(|k| (a[k] - b[k]).abs() <= EPS)
    }

    /// 線形の端点: 0 度は左端 → 右端、90 度は上端 → 下端、45 度は四隅がちょうど 0 と 1。
    #[test]
    fn linear_endpoints_touch_the_corners() {
        let size = [200.0, 100.0];
        let fill = |angle| SpriteFill { kind: SpriteFillKind::Linear, angle, ..SpriteFill::default() };
        let g0 = gradient_geom(&fill(0.0), size);
        assert!((gradient_t(&g0, [0.0, 50.0]) - 0.0).abs() < EPS);
        assert!((gradient_t(&g0, [200.0, 0.0]) - 1.0).abs() < EPS);
        assert!((gradient_t(&g0, [100.0, 77.0]) - 0.5).abs() < EPS);
        let g90 = gradient_geom(&fill(90.0), size);
        assert!((gradient_t(&g90, [30.0, 0.0]) - 0.0).abs() < EPS);
        assert!((gradient_t(&g90, [30.0, 25.0]) - 0.25).abs() < EPS);
        let g45 = gradient_geom(&fill(45.0), size);
        assert!(gradient_t(&g45, [0.0, 0.0]).abs() < EPS, "左上の角が 0");
        assert!((gradient_t(&g45, [200.0, 100.0]) - 1.0).abs() < EPS, "右下の角が 1");
        // 範囲の外は収める
        assert_eq!(gradient_t(&g0, [-50.0, 0.0]), 0.0);
    }

    /// 放射: 中心 0・内接する楕円の縁で 1。
    #[test]
    fn radial_t() {
        let fill = SpriteFill { kind: SpriteFillKind::Radial, ..SpriteFill::default() };
        let g = gradient_geom(&fill, [200.0, 100.0]);
        assert!(gradient_t(&g, [100.0, 50.0]).abs() < EPS);
        assert!((gradient_t(&g, [200.0, 50.0]) - 1.0).abs() < EPS);
        assert!((gradient_t(&g, [100.0, 75.0]) - 0.5).abs() < EPS);
    }

    /// 2 色・3 色・4 色の補間と色の位置。
    #[test]
    fn color_interpolation() {
        let red = [1.0, 0.0, 0.0, 1.0];
        let green = [0.0, 1.0, 0.0, 1.0];
        let blue = [0.0, 0.0, 1.0, 1.0];
        let fill = SpriteFill { kind: SpriteFillKind::Linear, colors: vec![red, green, blue], ..SpriteFill::default() };
        let (colors, count) = resolve_colors(&fill, [1.0; 4]);
        assert_eq!(count, 3);
        let stops = normalized_stops(&fill.stops, count);
        assert_eq!(&stops[..3], &[0.0, 0.5, 1.0]);
        assert!(close4(sample_gradient(&colors, &stops, count, 0.0), red));
        assert!(close4(sample_gradient(&colors, &stops, count, 0.25), [0.5, 0.5, 0.0, 1.0]));
        assert!(close4(sample_gradient(&colors, &stops, count, 0.5), green));
        assert!(close4(sample_gradient(&colors, &stops, count, 0.75), [0.0, 0.5, 0.5, 1.0]));
        assert!(close4(sample_gradient(&colors, &stops, count, 1.0), blue));
        // 色の位置の指定（赤 0〜0.2 は赤のまま、0.2〜0.8 で緑へ）
        let custom = normalized_stops(&[0.2, 0.8, 1.0], count);
        assert!(close4(sample_gradient(&colors, &custom, count, 0.1), red));
        assert!(close4(sample_gradient(&colors, &custom, count, 0.5), [0.5, 0.5, 0.0, 1.0]));
    }

    /// 乗算済みアルファの補間: 不透明の赤 → 透明の青の中間は「半透明の赤」（青に濁らない）。
    #[test]
    fn premultiplied_interpolation() {
        let colors = [[1.0, 0.0, 0.0, 1.0], [0.0, 0.0, 1.0, 0.0], [0.0; 4], [0.0; 4]];
        let stops = normalized_stops(&[], 2);
        let mid = sample_gradient(&colors, &stops, 2, 0.5);
        assert!(close4(mid, [1.0, 0.0, 0.0, 0.5]));
    }

    /// 色の数の補い（1 色なら最後の色を繰り返す・5 色なら 4 色まで）と color の乗算・色の位置の整え。
    #[test]
    fn colors_and_stops_are_normalized() {
        let fill = SpriteFill { kind: SpriteFillKind::Linear, colors: vec![[0.5, 0.5, 0.5, 1.0]], ..SpriteFill::default() };
        let (colors, count) = resolve_colors(&fill, [1.0, 0.0, 1.0, 0.5]);
        assert_eq!(count, GRADIENT_MIN_COLORS);
        assert!(close4(colors[1], [0.5, 0.0, 0.5, 0.5]));
        let five = SpriteFill { kind: SpriteFillKind::Linear, colors: vec![[1.0; 4]; 5], ..SpriteFill::default() };
        assert_eq!(color_count(&five), GRADIENT_MAX_COLORS);
        // 単色は tint だけ
        let (solid, n) = resolve_colors(&SpriteFill::default(), [0.2, 0.3, 0.4, 0.5]);
        assert_eq!(n, 1);
        assert!(close4(solid[0], [0.2, 0.3, 0.4, 0.5]));
        // 範囲の外は収め、逆順は直前へ揃える
        assert_eq!(&normalized_stops(&[-1.0, 0.6, 0.3, 2.0], 4)[..], &[0.0, 0.6, 0.6, 1.0]);
    }
}
