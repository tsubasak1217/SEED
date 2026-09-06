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
/// 出力値:
/// - `255 (1.0)` = 内側で `spread` ピクセル以上離れている
/// - `128 (0.5)` = エッジ上
/// - `0   (0.0)` = 外側で `spread` ピクセル以上離れている
///
/// `spread` はサーチ半径（ピクセル）。大きいほど遠くまで勾配が続く。
pub fn generate_sdf(bitmap: &[u8], width: u32, height: u32, spread: u32) -> Vec<u8> {
    let w = width as usize;
    let h = height as usize;
    let spread_f = spread as f32;
    let spread_sq = (spread * spread) as usize;
    let mut sdf = vec![0u8; w * h];

    for y in 0..h {
        for x in 0..w {
            let inside = bitmap[y * w + x] >= 128;

            let x_min = x.saturating_sub(spread as usize);
            let x_max = (x + spread as usize + 1).min(w);
            let y_min = y.saturating_sub(spread as usize);
            let y_max = (y + spread as usize + 1).min(h);

            let mut min_dist_sq = spread_sq + 1;

            'outer: for sy in y_min..y_max {
                let dy = sy as isize - y as isize;
                for sx in x_min..x_max {
                    let dx = sx as isize - x as isize;
                    let d_sq = (dx * dx + dy * dy) as usize;
                    if d_sq >= min_dist_sq {
                        continue;
                    }
                    if (bitmap[sy * w + sx] >= 128) != inside {
                        min_dist_sq = d_sq;
                        if min_dist_sq == 0 {
                            break 'outer;
                        }
                    }
                }
            }

            let dist = (min_dist_sq as f32).sqrt();
            let norm = (dist / spread_f).min(1.0);
            let val = if inside {
                0.5 + 0.5 * norm
            } else {
                0.5 - 0.5 * norm
            };
            sdf[y * w + x] = (val * 255.0).clamp(0.0, 255.0) as u8;
        }
    }

    sdf
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
