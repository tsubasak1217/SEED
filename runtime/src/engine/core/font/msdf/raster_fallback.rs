// ============================================================
//  font/msdf/raster_fallback.rs — 安全弁の最後の落ち先: ラスタから作る真の SDF（MTSDF の置き場・RGBA の 4 チャネルに同じ値）
//
//  【なぜ要るか（2026-10-03。docs/reviews/2026-10-02_code_review.md #4）】
//  検査（verify.rs）に落ちた字は、まずアルファ（真の SDF）を RGB へ写して描く（bake.rs）。ところがアルファも
//  同じ合成（distance.rs の combine・遠い輪郭の深さ）から作るので、重なった輪郭で合成が選び間違えると
//  アルファも同じように壊れ、写しても形は崩れたまま（以前はログだけで次の手が無かった）。
//  ここでは輪郭の合成を一切使わず、ab_glyph のラスタ（被覆率 ≥ 0.5 を内側。非ゼロ規則の塗り）から距離変換
//  （sdf_edt.rs。1 チャネルの SDF と同じ厳密な二乗距離）で真の SDF を作る。形はラスタと必ず一致する。
//
//  【作り方】
//  1. MTSDF の em の `RASTER_FALLBACK_OVERSAMPLE` 倍の大きさで字を塗る（ペンの基点を原点。verify.rs の参照と同じ置き方）。
//  2. 距離場の置き場（余白込み）を同じ倍率の画素で切り出し、内外の 2 値にする。
//  3. 内側の画素 → いちばん近い外側の画素・外側の画素 → いちばん近い内側の画素の二乗距離（画素の中心どうし）。
//     縁までの距離 = 中心までの距離 − 半画素（rasterizer.rs の generate_sdf と同じ決まり）。
//  4. MTSDF のテクセルの中心で、細かい画素の符号つき距離を双線形に読み、倍率で割ってテクセルの単位へ。
//  5. 値 = 0.5 + 距離 ÷ (2 × 片側の幅)（MTSDF と同じ。px → 値の変換・シェーダーの式がそのまま使える）→ RGBA8 の 4 チャネルへ。
//  輪郭の格子の丸め（1 チャネルの SDF の em 64 の 2 値化）より細かい（倍率の分の 1 テクセル）ので、大きな文字でも崩れにくい。
// ============================================================

use ab_glyph::{point, Font, FontArc, GlyphId, PxScale};

use super::params::{FieldScale, MTSDF_BYTES_PER_TEXEL, MTSDF_EDGE_VALUE, RASTER_FALLBACK_OVERSAMPLE, TEXEL_CENTER_OFFSET};
use super::verify::FieldPlacement;
use crate::engine::core::font::sdf_edt::squared_distance_to_targets;

/// 参照の被覆率でこれ以上を内側とする（verify.rs の参照・rasterizer.rs の 2 値化と同じ 0.5）。
const INSIDE_COVERAGE: f32 = 0.5;
/// 画素の中心から縁までの距離（画素。縁までの距離 = 反対側の画素の中心までの距離 − これ）。
const PIXEL_CENTER_TO_EDGE: f64 = 0.5;
/// u8 の値の最大。
const BYTE_MAX: f64 = 255.0;
/// 距離の打ち切りの余裕（テクセル。片側の幅 + これより遠い距離は値が張り付くので切り詰める）。
const FAR_MARGIN_TEXELS: f64 = 2.0;

/// ラスタから真の SDF を作り、MTSDF の置き場（`place`）の RGBA8（4 チャネルに同じ値）で返す。輪郭の無い字は None。
///
/// `scale` は MTSDF を焼いた大きさ（em・片側の幅）。返す値の決まりは MTSDF と同じ（0.5 = 縁・1 テクセル = 0.5 ÷ 片側の幅）。
pub fn raster_true_sdf_rgba(font: &FontArc, glyph_id: GlyphId, scale: &FieldScale, place: FieldPlacement) -> Option<Vec<u8>> {
    let k = RASTER_FALLBACK_OVERSAMPLE as i64;
    let kf = RASTER_FALLBACK_OVERSAMPLE as f64;

    // ── 1. 細かい画素で字を塗る（ペンの基点を原点・y 下向き）──
    let glyph = glyph_id.with_scale_and_position(PxScale::from(scale.em_px * RASTER_FALLBACK_OVERSAMPLE as f32), point(0.0, 0.0));
    let outlined = font.outline_glyph(glyph)?;
    let bounds = outlined.px_bounds();
    let (rx0, ry0) = (bounds.min.x.round() as i64, bounds.min.y.round() as i64);
    let rw = bounds.width().round().max(0.0) as usize;
    let rh = bounds.height().round().max(0.0) as usize;
    let mut coverage = vec![0.0f32; rw * rh];
    outlined.draw(|x, y, c| {
        let idx = y as usize * rw + x as usize;
        if idx < coverage.len() {
            coverage[idx] = c;
        }
    });

    // ── 2. 置き場を同じ倍率の画素で切り出して 2 値にする ──
    let hw = place.width * RASTER_FALLBACK_OVERSAMPLE as usize;
    let hh = place.height * RASTER_FALLBACK_OVERSAMPLE as usize;
    let inside: Vec<bool> = (0..hw * hh)
        .map(|i| {
            let (hx, hy) = ((i % hw) as i64, (i / hw) as i64);
            let rx = i64::from(place.left) * k - rx0 + hx;
            let ry = i64::from(place.top) * k - ry0 + hy;
            rx >= 0 && ry >= 0 && (rx as usize) < rw && (ry as usize) < rh && coverage[ry as usize * rw + rx as usize] >= INSIDE_COVERAGE
        })
        .collect();

    // ── 3. 反対側の画素までの二乗距離 → 細かい画素の符号つき距離（縁まで。内側が正。遠い所は切り詰める）──
    let to_inside = squared_distance_to_targets(hw, hh, |i| inside[i]);
    let to_outside = squared_distance_to_targets(hw, hh, |i| !inside[i]);
    let far_px = (f64::from(scale.spread_px) + FAR_MARGIN_TEXELS) * kf;
    let far_sq = (far_px * far_px) as i64;
    let signed_px: Vec<f64> = (0..hw * hh)
        .map(|i| {
            let (d_sq, sign) = if inside[i] { (to_outside[i], 1.0) } else { (to_inside[i], -1.0) };
            let to_edge = ((d_sq.min(far_sq) as f64).sqrt() - PIXEL_CENTER_TO_EDGE).max(0.0);
            sign * to_edge
        })
        .collect();

    // ── 4・5. テクセルの中心で双線形に読み、テクセルの単位の距離 → 値 → RGBA8 ──
    let read = |x: i64, y: i64| -> f64 {
        let x = x.clamp(0, hw as i64 - 1) as usize;
        let y = y.clamp(0, hh as i64 - 1) as usize;
        signed_px[y * hw + x]
    };
    let mut data = vec![0u8; place.width * place.height * MTSDF_BYTES_PER_TEXEL];
    for j in 0..place.height {
        for i in 0..place.width {
            // テクセルの中心（テクセルの単位で i + 0.5）を細かい画素の中心の座標へ（画素の中心は整数 + 0.5）
            let fx = (i as f64 + f64::from(TEXEL_CENTER_OFFSET)) * kf - PIXEL_CENTER_TO_EDGE;
            let fy = (j as f64 + f64::from(TEXEL_CENTER_OFFSET)) * kf - PIXEL_CENTER_TO_EDGE;
            let (x0, y0) = (fx.floor(), fy.floor());
            let (ax, ay) = (fx - x0, fy - y0);
            let (x0, y0) = (x0 as i64, y0 as i64);
            let top = read(x0, y0) + (read(x0 + 1, y0) - read(x0, y0)) * ax;
            let bottom = read(x0, y0 + 1) + (read(x0 + 1, y0 + 1) - read(x0, y0 + 1)) * ax;
            let distance_texels = (top + (bottom - top) * ay) / kf;
            let value = f64::from(MTSDF_EDGE_VALUE) + distance_texels / (2.0 * f64::from(scale.spread_px));
            let byte = (value.clamp(0.0, 1.0) * BYTE_MAX).round() as u8;
            let k_out = (j * place.width + i) * MTSDF_BYTES_PER_TEXEL;
            data[k_out..k_out + MTSDF_BYTES_PER_TEXEL].fill(byte);
        }
    }
    Some(data)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::font::msdf::bake::bake_glyph_mtsdf;
    use crate::engine::core::font::msdf::verify::{verify_glyph, VerifyChannel};
    use crate::engine::core::font::msdf::ColoringStrategy;

    fn builtin() -> FontArc {
        FontArc::try_from_slice(crate::engine::core::font::DEFAULT_FONT_BYTES).expect("組み込みフォント")
    }

    /// ラスタの真の SDF は検査に通り、ふつうの字では MTSDF のアルファ（輪郭からの真の距離）とほぼ同じ値（置き場がずれていない）。
    #[test]
    fn raster_sdf_matches_contour_alpha_and_passes_verify() {
        /// 縁の近く（値 0.5 ± この幅）で比べる。
        const NEAR_EDGE_VALUE: f32 = 0.3;
        /// 縁の近くの値の差の平均・最大の許容（値 0.1 = em 40 の 1 テクセル）。
        const MEAN_TOLERANCE: f32 = 0.02;
        const MAX_TOLERANCE: f32 = 0.08;
        let font = builtin();
        for ch in ['A', '図', '鬱', 'g'] {
            let (g, stats) = bake_glyph_mtsdf(&font, ch, ColoringStrategy::InkTrap).expect("輪郭がある");
            assert!(!stats.fallback, "'{ch}' はふつうに通る");
            let em = g.em_px;
            let place = FieldPlacement { left: (g.bearing_em[0] * em).round() as i32, top: (g.bearing_em[1] * em).round() as i32, width: g.width as usize, height: g.height as usize };
            let scale = FieldScale::with_em(em);
            let raster = raster_true_sdf_rgba(&font, font.glyph_id(ch), &scale, place).expect("輪郭がある");
            assert_eq!(raster.len(), g.data.len());
            let report = verify_glyph(&font, font.glyph_id(ch), em, &raster, place, VerifyChannel::Median);
            assert!(report.passed(), "'{ch}' のラスタの真の SDF が検査に落ちた: {report:?}");
            let (mut sum, mut n, mut max) = (0.0f32, 0usize, 0.0f32);
            for (r, m) in raster.chunks_exact(MTSDF_BYTES_PER_TEXEL).zip(g.data.chunks_exact(MTSDF_BYTES_PER_TEXEL)) {
                let (rv, av) = (f32::from(r[3]) / 255.0, f32::from(m[3]) / 255.0);
                if (av - 0.5).abs() < NEAR_EDGE_VALUE {
                    let d = (rv - av).abs();
                    sum += d;
                    n += 1;
                    max = max.max(d);
                }
                assert!(r[0] == r[3] && r[1] == r[3] && r[2] == r[3], "4 チャネルに同じ値");
            }
            let mean = sum / n.max(1) as f32;
            assert!(mean < MEAN_TOLERANCE && max < MAX_TOLERANCE, "'{ch}' 縁の近くの値の差 平均 {mean} 最大 {max}");
        }
    }
}
