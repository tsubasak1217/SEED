// ============================================================
//  font/rasterizer.rs — グリフラスタライズ + SDF 生成
// ============================================================

use ab_glyph::{Font, FontArc, PxScale, ScaleFont};

// ── ビットマップラスタライズ ──────────────────────────────────

/// グリフを指定サイズでビットマップにラスタライズする。
///
/// 戻り値: `(bitmap, width, height, bearing, advance)`
/// - `bitmap`  : R8 グレースケール、行優先（左上起点）
/// - `bearing` : ペン基点からビットマップ左上へのオフセット（スクリーン座標 Y 下向き）
/// - `advance` : 水平アドバンス幅（ピクセル）
///
/// スペース等、アウトラインのないグリフは `None`。
pub fn rasterize_glyph_bitmap(
    font: &FontArc,
    codepoint: char,
    font_size_px: f32,
) -> Option<(Vec<u8>, u32, u32, [f32; 2], f32)> {
    let scale = PxScale::from(font_size_px);
    let scaled_font = font.as_scaled(scale);
    let glyph_id = font.glyph_id(codepoint);
    let advance = scaled_font.h_advance(glyph_id);

    let glyph = glyph_id.with_scale_and_position(scale, ab_glyph::point(0.0, 0.0));
    let outlined = font.outline_glyph(glyph)?;
    let bounds = outlined.px_bounds();

    let width = bounds.width().ceil() as u32;
    let height = bounds.height().ceil() as u32;
    if width == 0 || height == 0 {
        return None;
    }

    let mut bitmap = vec![0u8; (width * height) as usize];
    outlined.draw(|x, y, coverage| {
        let idx = (y * width + x) as usize;
        if idx < bitmap.len() {
            bitmap[idx] = (coverage * 255.0).clamp(0.0, 255.0) as u8;
        }
    });

    let bearing = [bounds.min.x, bounds.min.y];
    Some((bitmap, width, height, bearing, advance))
}

// ── SDF 生成 ──────────────────────────────────────────────────

/// ビットマップ（閾値 128）から Single-channel SDF を生成する。
///
/// 出力値（**字の縁までの距離**。縁 = 2 値にしたビットマップの内側と外側の画素の境目）:
/// - `255 (1.0)` = 内側で縁から `spread` ピクセル以上離れている
/// - `128 (0.5)` = 縁の上（内側と外側の隣り合う画素のちょうど真ん中）
/// - `0   (0.0)` = 外側で縁から `spread` ピクセル以上離れている
///
/// 値は 1 ピクセルあたり `0.5 / spread` で一定に変わる（`sdf::SDF_VALUE_PER_TEXEL`）。シェーダー（text.wgsl）は
/// この傾きを前提に「画面の 1 画素ぶんの値の変化」を UV の微分から求めて平滑化するので、傾きが揃っていることが要る。
///
/// `spread` はサーチ半径（ピクセル）。大きいほど遠くまで勾配が続く。
///
/// 【2026-09-28 から厳密な距離変換（sdf_edt.rs）で求める】総当たり（各画素のまわりを全部見る）と
/// 全画素で同じ値になる（理由は sdf_edt.rs の冒頭。下のテストが実際のグリフで確かめる）。
///
/// 【2026-10-01 から縁までの距離にした（docs/ui_components.md §12）】距離変換が返す「反対側の画素の中心までの距離」から
/// 半画素（`SDF_PIXEL_CENTER_TO_EDGE_PX`）を引く。以前は中心までの距離のままで、縁の前後 1 ピクセルだけ傾きが 2 倍・
/// ほかは縁から半画素遠い値だった（0.5 の等値線の位置＝字の形は変わらない。縁取り・太さは半ピクセル細かった）。
pub fn generate_sdf(bitmap: &[u8], width: u32, height: u32, spread: u32) -> Vec<u8> {
    let w = width as usize;
    let h = height as usize;
    // これより遠い画素は縁までの距離が spread を超えて値が変わらない（1.0 / 0.0）ので切り詰める
    let far_sq = sdf_far_distance_sq(spread);
    let inside_at = |i: usize| bitmap[i] >= SDF_INSIDE_THRESHOLD;
    // 外側の画素 → いちばん近い内側の画素、内側の画素 → いちばん近い外側の画素（どちらも二乗距離の整数）
    let to_inside = super::sdf_edt::squared_distance_to_targets(w, h, inside_at);
    let to_outside = super::sdf_edt::squared_distance_to_targets(w, h, |i| !inside_at(i));
    let mut sdf = vec![0u8; w * h];

    for (i, out) in sdf.iter_mut().enumerate() {
        let inside = inside_at(i);
        let nearest_opposite = if inside { to_outside[i] } else { to_inside[i] };
        *out = sdf_value(nearest_opposite.min(far_sq), inside, spread);
    }

    sdf
}

/// カバレッジ（0..255）がこれ以上の画素を「内側」とする（総当たりの頃からの閾値）。
const SDF_INSIDE_THRESHOLD: u8 = 128;

/// SDF の値の最大（u8）。
const SDF_VALUE_MAX: f32 = 255.0;

/// 「反対側の画素の中心までの二乗距離」の切り詰めの値（`(spread + 1)²`）。
///
/// これ以上離れた画素は縁までの距離（中心までの距離 − 半画素）が `spread` を超え、値は 1.0 / 0.0 で変わらない。
/// 総当たりの検算（テスト）もこの半径の正方形の中だけを探せば同じ値になる。
fn sdf_far_distance_sq(spread: u32) -> i64 {
    let radius = i64::from(spread) + 1;
    radius * radius
}

/// 「反対側の画素の中心までの二乗距離」→ SDF の値（u8）【純関数。生成と検算で共有する唯一の式】。
///
/// 縁までの距離 = 中心までの距離 − 半画素。`spread` で 0..1 に正規化し、内側は 0.5 から上、外側は 0.5 から下へ。
/// 0..1 → 0..255 は四捨五入（切り捨てだと全体が 1/255 ずつ外側へ寄る）。
fn sdf_value(dist_sq_to_opposite_center: i64, inside: bool, spread: u32) -> u8 {
    use super::sdf::{SDF_EDGE_VALUE, SDF_PIXEL_CENTER_TO_EDGE_PX};
    let to_edge = ((dist_sq_to_opposite_center as f32).sqrt() - SDF_PIXEL_CENTER_TO_EDGE_PX).max(0.0);
    let norm = (to_edge / spread as f32).min(1.0);
    // 縁（0.5）から、縁までの距離の割合だけ内側は上・外側は下へ（spread 以上離れると 1.0 / 0.0）
    let offset = SDF_EDGE_VALUE * norm;
    let val = if inside { SDF_EDGE_VALUE + offset } else { SDF_EDGE_VALUE - offset };
    (val * SDF_VALUE_MAX).round().clamp(0.0, SDF_VALUE_MAX) as u8
}


// ── サイズ非依存 SDF グリフ ───────────────────────────────────

/// 固定 em サイズで焼いた 1 グリフぶんの SDF とそのメトリクス。
///
/// メトリクスはすべて **em 単位**（フォントサイズ 1.0 相当）で保持する。
/// 描画時にフォントサイズを掛けるだけで任意サイズへ拡大縮小できる。
pub struct GlyphSdf {
    /// R8 の距離場データ（`width * height` バイト、行優先）。
    pub data: Vec<u8>,
    /// 距離場の幅（スプレッドぶんのパディング込み）。
    pub width: u32,
    /// 距離場の高さ（スプレッドぶんのパディング込み）。
    pub height: u32,
    /// ペン基点 → クアッド左上（Y 下向き、em 単位）。
    pub bearing_em: [f32; 2],
    /// パディング込みクアッドサイズ（em 単位）。
    pub size_em: [f32; 2],
    /// 水平アドバンス幅（em 単位）。
    pub advance_em: f32,
}

/// グリフを固定 em サイズ（`SDF_EM_PX`）で SDF 化する。
///
/// 手順:
///   1. `SDF_EM_PX` でカバレッジビットマップを焼く
///   2. 四方に `SDF_SPREAD_PX` のパディングを付けたバッファへコピーする
///   3. パディング込みバッファに対して距離場を生成する
///   4. 全メトリクスを `SDF_EM_PX` で割って em 単位へ正規化する
///
/// **パディングは必須**。付けないと外側の距離場がグリフ矩形で切れてしまい、
/// 縁取り（エッジより外側を塗る）が途中で欠ける。
///
/// スペース等アウトラインを持たないグリフは `None`（送り幅だけは
/// `FontSystem::advance_em` が別途フォントから直接引く）。
pub fn rasterize_glyph_sdf(font: &FontArc, codepoint: char) -> Option<GlyphSdf> {
    use super::sdf::{SDF_EM_PX, SDF_SPREAD_PX};

    // ── 1. 基準 em サイズでカバレッジを焼く ──
    let (bitmap, bw, bh, bearing_px, advance_px) =
        rasterize_glyph_bitmap(font, codepoint, SDF_EM_PX)?;

    // ── 2. 四方にスプレッドぶんのパディングを付けたバッファへコピー ──
    let pad = SDF_SPREAD_PX;
    let padded_w = bw + pad * 2;
    let padded_h = bh + pad * 2;
    let mut padded = vec![0u8; (padded_w * padded_h) as usize];
    for row in 0..bh as usize {
        let src = &bitmap[row * bw as usize..(row + 1) * bw as usize];
        let dst_off = (row + pad as usize) * padded_w as usize + pad as usize;
        padded[dst_off..dst_off + bw as usize].copy_from_slice(src);
    }

    // ── 3. 距離場を生成する（サーチ半径 = スプレッド）──
    let data = generate_sdf(&padded, padded_w, padded_h, pad);

    // ── 4. メトリクスを em 単位へ正規化する ──
    let inv_em = 1.0 / SDF_EM_PX;
    Some(GlyphSdf {
        data,
        width: padded_w,
        height: padded_h,
        // パディングぶんクアッドは左上へ広がる。
        bearing_em: [
            (bearing_px[0] - pad as f32) * inv_em,
            (bearing_px[1] - pad as f32) * inv_em,
        ],
        size_em: [padded_w as f32 * inv_em, padded_h as f32 * inv_em],
        advance_em: advance_px * inv_em,
    })
}

// ============================================================
//  ユニットテスト（GPU 不要。距離場の性質とサイズ整合を検証する）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::font::sdf::{SDF_EM_PX, SDF_SPREAD_EM};

    /// テスト用ビットマップ幅（16x16 の中央へ 8x8 の塗り潰しを置く）。
    const TEST_W: u32 = 16;
    /// テスト用の塗り潰し領域（[FILL_MIN, FILL_MAX) の正方形）。
    const FILL_MIN: usize = 4;
    const FILL_MAX: usize = 12;
    /// テスト用スプレッド半径。
    const TEST_SPREAD: u32 = 4;

    /// 中央に 8x8 の塗り潰しを持つ 16x16 ビットマップを作る。
    fn filled_square() -> Vec<u8> {
        let mut bmp = vec![0u8; (TEST_W * TEST_W) as usize];
        for y in FILL_MIN..FILL_MAX {
            for x in FILL_MIN..FILL_MAX {
                bmp[y * TEST_W as usize + x] = 255;
            }
        }
        bmp
    }

    /// 距離場を作り、u8 を 0..1 の f32 として読むヘルパー。
    fn sdf_field() -> Vec<f32> {
        generate_sdf(&filled_square(), TEST_W, TEST_W, TEST_SPREAD)
            .iter()
            .map(|v| *v as f32 / 255.0)
            .collect()
    }

    /// 内側 > 0.5、外側 < 0.5、そして中心から外へ向かって単調非増加であること。
    #[test]
    fn sdf_is_inside_high_outside_low_and_monotonic() {
        let f = sdf_field();
        let row = 8usize; // 塗り潰し領域を横断する行
        let at = |x: usize| f[row * TEST_W as usize + x];

        // 内側の中心付近は 0.5 より大きい。
        assert!(at(8) > 0.5, "内側が 0.5 以下: {}", at(8));
        // 明確な外側は 0.5 より小さい。
        assert!(at(15) < 0.5, "外側が 0.5 以上: {}", at(15));

        // 中心から右へ 1 歩ずつ進むと単調非増加。
        for x in 8..(TEST_W as usize - 1) {
            assert!(
                at(x) >= at(x + 1) - 1e-6,
                "x={x} で単調性が崩れた: {} -> {}",
                at(x),
                at(x + 1)
            );
        }
    }

    /// 境界をまたぐ 2 ピクセルが 0.5 を挟むこと（エッジ位置が正しい）。
    #[test]
    fn sdf_brackets_half_at_boundary() {
        let f = sdf_field();
        let row = 8usize;
        let inside_edge = f[row * TEST_W as usize + (FILL_MAX - 1)]; // 内側の最終ピクセル
        let outside_edge = f[row * TEST_W as usize + FILL_MAX]; // 外側の最初のピクセル
        assert!(inside_edge > 0.5, "内側境界: {inside_edge}");
        assert!(outside_edge < 0.5, "外側境界: {outside_edge}");
        // 0.5 の近傍にあること（スプレッド 4 なら ±0.125 刻み）。
        assert!((inside_edge - 0.5).abs() < 0.2);
        assert!((outside_edge - 0.5).abs() < 0.2);
    }

    /// 組み込みフォントを読む（GPU 不要）。
    fn builtin_font() -> FontArc {
        FontArc::try_from_slice(crate::engine::core::font::DEFAULT_FONT_BYTES)
            .expect("組み込みフォントは必ず読める")
    }

    /// 総当たりの SDF（距離変換の検算用。2026-09-28 までの総当たりの探し方を残す）。
    ///
    /// 各画素のまわりの「切り詰めの半径（spread + 1）」の正方形を全部見て、反対側の画素の中心までの二乗距離の最小を探す。
    /// 値への変換は生成と同じ `sdf_value`（2026-10-01 に縁までの距離へ改めた式）を使う＝このテストが確かめるのは距離変換の正しさ。
    fn generate_sdf_brute_force(bitmap: &[u8], width: u32, height: u32, spread: u32) -> Vec<u8> {
        let w = width as usize;
        let h = height as usize;
        let far_sq = sdf_far_distance_sq(spread);
        let radius = spread as usize + 1;
        let mut sdf = vec![0u8; w * h];
        for y in 0..h {
            for x in 0..w {
                let inside = bitmap[y * w + x] >= SDF_INSIDE_THRESHOLD;
                let x_min = x.saturating_sub(radius);
                let x_max = (x + radius + 1).min(w);
                let y_min = y.saturating_sub(radius);
                let y_max = (y + radius + 1).min(h);
                let mut min_dist_sq = far_sq;
                'outer: for sy in y_min..y_max {
                    let dy = sy as i64 - y as i64;
                    for sx in x_min..x_max {
                        let dx = sx as i64 - x as i64;
                        let d_sq = dx * dx + dy * dy;
                        if d_sq >= min_dist_sq {
                            continue;
                        }
                        if (bitmap[sy * w + sx] >= SDF_INSIDE_THRESHOLD) != inside {
                            min_dist_sq = d_sq;
                            if min_dist_sq == 0 {
                                break 'outer;
                            }
                        }
                    }
                }
                sdf[y * w + x] = sdf_value(min_dist_sq, inside, spread);
            }
        }
        sdf
    }

    /// 【縁までの距離の検証（2026-10-01）】縦の境目（左が内側）の SDF は、境目を挟む 2 画素が 0.5 を中心に対称で、
    /// そこから 1 画素ごとに `SDF_VALUE_PER_TEXEL`（spread で割った値）ずつ一定に変わる（縁の前後だけ傾きが 2 倍にならない）。
    #[test]
    fn sdf_measures_distance_to_the_edge_with_constant_slope() {
        use crate::engine::core::font::sdf::SDF_VALUE_PER_TEXEL;
        const W: u32 = 24;
        const EDGE_X: usize = 12; // x < 12 が内側
        const SPREAD: u32 = 8;
        let bitmap: Vec<u8> = (0..W * 3).map(|k| if (k % W) < EDGE_X as u32 { 255 } else { 0 }).collect();
        let sdf = generate_sdf(&bitmap, W, 3, SPREAD);
        let at = |x: usize| f32::from(sdf[W as usize + x]) / SDF_VALUE_MAX;
        // 1/255 の量子化の誤差まで許す
        let tol = 1.0 / SDF_VALUE_MAX;
        // 境目を挟む 2 画素は縁から半画素 → 0.5 ± 半テクセルぶん
        assert!((at(EDGE_X - 1) - (0.5 + 0.5 * SDF_VALUE_PER_TEXEL)).abs() <= tol, "内側の隣 {}", at(EDGE_X - 1));
        assert!((at(EDGE_X) - (0.5 - 0.5 * SDF_VALUE_PER_TEXEL)).abs() <= tol, "外側の隣 {}", at(EDGE_X));
        // spread の手前までは 1 画素ごとに同じ量だけ変わる
        for k in 1..(SPREAD as usize - 1) {
            let step_in = at(EDGE_X - 1 - k) - at(EDGE_X - k);
            let step_out = at(EDGE_X + k - 1) - at(EDGE_X + k);
            assert!((step_in - SDF_VALUE_PER_TEXEL).abs() <= 2.0 * tol, "内側 {k}: {step_in}");
            assert!((step_out - SDF_VALUE_PER_TEXEL).abs() <= 2.0 * tol, "外側 {k}: {step_out}");
        }
        // 境目の前後の傾き（1 画素ぶん）も同じ（以前は 2 倍だった）
        let across = at(EDGE_X - 1) - at(EDGE_X);
        assert!((across - SDF_VALUE_PER_TEXEL).abs() <= 2.0 * tol, "境目をまたぐ傾き {across}");
    }

    /// 【距離変換の正しさの検証】実際のグリフ（rasterize_glyph_sdf と同じ em 64・パディング込み）で、
    /// 距離変換の SDF が総当たりと全画素一致する（英数字・記号・かな・漢字・全角数字）。
    #[test]
    fn distance_transform_sdf_matches_brute_force_on_real_glyphs() {
        use crate::engine::core::font::sdf::SDF_SPREAD_PX;
        let font = builtin_font();
        let text = "AgWm@&%#8.,;:!?()[]{}|/\\~^_ あいうえおがぱゃっーアイウエオ漢字日本語図鑑釣竿魚鳴動０１２３４５６７８９：／";
        let mut checked = 0;
        for ch in text.chars() {
            let Some((bitmap, bw, bh, _, _)) = rasterize_glyph_bitmap(&font, ch, SDF_EM_PX) else {
                continue;
            };
            // rasterize_glyph_sdf と同じパディング
            let pad = SDF_SPREAD_PX;
            let (pw, ph) = (bw + pad * 2, bh + pad * 2);
            let mut padded = vec![0u8; (pw * ph) as usize];
            for row in 0..bh as usize {
                let dst = (row + pad as usize) * pw as usize + pad as usize;
                padded[dst..dst + bw as usize].copy_from_slice(&bitmap[row * bw as usize..(row + 1) * bw as usize]);
            }
            assert_eq!(
                generate_sdf(&padded, pw, ph, pad),
                generate_sdf_brute_force(&padded, pw, ph, pad),
                "'{ch}'（{pw}x{ph}）で総当たりと違う"
            );
            checked += 1;
        }
        assert!(checked > 60, "アウトラインを持つ字を十分に確かめた: {checked}");
    }

    /// 境界の細かい形（1 画素の点・線・市松・端に接する塗り・spread より遠い画素）でも総当たりと全画素一致する。
    #[test]
    fn distance_transform_sdf_matches_brute_force_on_patterns() {
        let (w, h) = (23u32, 19u32);
        let patterns: Vec<Box<dyn Fn(u32, u32) -> bool>> = vec![
            Box::new(|x, y| x == 11 && y == 9),
            Box::new(|x, _| x == 3),
            Box::new(|x, y| (x + y) % 2 == 0),
            Box::new(|x, _| x < 2),
            Box::new(|x, y| x > 18 && y > 15),
            Box::new(|_, _| false),
            Box::new(|_, _| true),
        ];
        for (i, pattern) in patterns.iter().enumerate() {
            let bitmap: Vec<u8> = (0..w * h).map(|k| if pattern(k % w, k / w) { 200 } else { 30 }).collect();
            for spread in [1u32, 4, 8] {
                assert_eq!(
                    generate_sdf(&bitmap, w, h, spread),
                    generate_sdf_brute_force(&bitmap, w, h, spread),
                    "模様 {i}・spread {spread}"
                );
            }
        }
    }

    /// 【見た目サイズ不変の検証】
    /// em 正規化した advance にフォントサイズを掛けた値が、
    /// 従来どおり `as_scaled(font_size).h_advance()` で得られる値と一致すること。
    /// ＝ ab_glyph のメトリクスがスケールに対して線形なので、
    /// 「64px で焼いて後から掛ける」方式でも既存テキストの字送りは変わらない。
    #[test]
    fn em_normalized_advance_matches_scaled_advance() {
        let font = builtin_font();
        for ch in ['A', 'g', '所', '8'] {
            let g = rasterize_glyph_sdf(&font, ch).expect("アウトラインを持つ文字");
            for fs in [12.0f32, 24.0, 160.0] {
                let scaled = font.as_scaled(PxScale::from(fs));
                let expect = scaled.h_advance(font.glyph_id(ch));
                let got = g.advance_em * fs;
                assert!(
                    (got - expect).abs() < 1e-3,
                    "'{ch}' fs={fs}: got={got} expect={expect}"
                );
            }
        }
    }

    /// 【見た目サイズ不変の検証（クアッド高さ）】
    /// パディングを取り除いた「タイトな高さ」が、そのサイズで直接
    /// ラスタライズしたときの px_bounds 高さと ~1px 以内で一致すること。
    #[test]
    fn tight_glyph_height_matches_direct_raster() {
        let font = builtin_font();
        for ch in ['A', 'g', '所'] {
            let g = rasterize_glyph_sdf(&font, ch).expect("アウトラインを持つ文字");
            for fs in [12.0f32, 24.0, 64.0, 160.0] {
                let tight_h = g.size_em[1] * fs - 2.0 * SDF_SPREAD_EM * fs;
                let scale = PxScale::from(fs);
                let glyph = font
                    .glyph_id(ch)
                    .with_scale_and_position(scale, ab_glyph::point(0.0, 0.0));
                let bounds_h = font.outline_glyph(glyph).unwrap().px_bounds().height();
                // 基準 em での 1px 切り上げ誤差が fs 倍に拡大するぶんを許容する。
                let tol = 1.0 + fs / SDF_EM_PX;
                assert!(
                    (tight_h - bounds_h).abs() <= tol,
                    "'{ch}' fs={fs}: tight={tight_h} bounds={bounds_h} tol={tol}"
                );
            }
        }
    }
}
