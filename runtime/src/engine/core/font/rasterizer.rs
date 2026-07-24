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

// ── ダウンサンプル（ボックスフィルタ）────────────────────────

/// src を縦横 `factor` 倍縮小する（平均ボックスフィルタ）。
///
/// src_w / factor, src_h / factor が割り切れることを前提とする。
pub fn downsample(src: &[u8], src_w: u32, src_h: u32, factor: u32) -> (Vec<u8>, u32, u32) {
    let dst_w = src_w / factor;
    let dst_h = src_h / factor;
    let factor_sq = (factor * factor) as u32;
    let mut dst = vec![0u8; (dst_w * dst_h) as usize];

    for dy in 0..dst_h as usize {
        for dx in 0..dst_w as usize {
            let mut sum = 0u32;
            for fy in 0..factor as usize {
                for fx in 0..factor as usize {
                    let sx = dx * factor as usize + fx;
                    let sy = dy * factor as usize + fy;
                    sum += src[sy * src_w as usize + sx] as u32;
                }
            }
            dst[dy * dst_w as usize + dx] = (sum / factor_sq) as u8;
        }
    }

    (dst, dst_w, dst_h)
}
