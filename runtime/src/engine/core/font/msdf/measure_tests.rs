// ============================================================
//  font/msdf/measure_tests.rs — MTSDF の計測（焼き時間・検査の結果）と目視用の画像（テストだけのモジュール）
//
//  `cargo test --lib engine::core::font::msdf::measure_tests -- --ignored --nocapture` で走らせる（既定では走らない）。
//  - measure_bake_times_and_checks: いろいろな字（英数・かな・漢字・画数の多い漢字）を焼いて、字ごとの大きさ・辺の数・
//    各段の時間・補正したテクセル・検査の食い違い・真の SDF へ落ちたかを表にし、集計を出す（debug と release で比べる）。
//  - 環境変数 SEED_MSDF_DUMP_DIR を指定すると、字ごとに「距離場（RGB・アルファ）・150 px で描いた中央値・アルファ・
//    1 チャネルの SDF・参照のラスタ」を横に並べた PNG を書く（目視の確かめ用）。
// ============================================================

use std::time::{Duration, Instant};

use ab_glyph::{Font, FontArc, PxScale};

use super::bake::bake_glyph_mtsdf;
use super::edge_color::ColoringStrategy;
use super::params::{MTSDF_BYTES_PER_TEXEL, MTSDF_VALUE_PER_TEXEL};
use super::verify::{sample_field, VerifyChannel};
use crate::engine::core::font::rasterizer::rasterize_glyph_sdf;
use crate::engine::core::font::sdf::SDF_VALUE_PER_TEXEL;

/// 計測する字（英数・記号・かな・常用の漢字・大きな文字の例）。
const MEASURE_TEXT: &str = "AgWmR@&05:27ABCSあいうえおがぱゃっーアイウエオスヌーズ上限−時刻起きろ！床成功図鑑釣竿魚鳴動永東京";
/// 画数の多い漢字（安全弁の確かめ。書体に無い字は飛ばす）。
const DENSE_KANJI: &str = "鬱薔薇魑魅魍魎麤龘鑑鑿鬣鸞齲讒纜蠢曠艦鷹鷲襲響驚灘鑽籠竈鬮麟龍鱗爨釁饕餮躑躅黴齷齪鼈驟纏臘蠣罐矚鑵籤顳顬靉靆";
/// 目視の画像で描く大きさ（画面の画素）。
const PREVIEW_SIZE_PX: f32 = 150.0;
/// 目視の画像で距離場を何倍に拡大して並べるか。
const PREVIEW_FIELD_ZOOM: usize = 4;
/// 画像の並びの隙間（画素）。
const PREVIEW_GAP: usize = 6;

fn builtin() -> FontArc {
    FontArc::try_from_slice(crate::engine::core::font::DEFAULT_FONT_BYTES).expect("組み込みフォント")
}

/// 書体にある字か（glyph 0 = .notdef は無い字）。
fn has_glyph(font: &FontArc, ch: char) -> bool {
    font.glyph_id(ch).0 != 0
}

fn ms(d: Duration) -> f64 {
    d.as_secs_f64() * 1000.0
}

/// 距離場（1 または 4 チャネル）を大きさ `size` の画素の格子で塗った被覆率の画像（シェーダーの平滑化と同じ直線の傾き）。
fn render_coverage(sample: &dyn Fn(f32, f32) -> f32, field_w: usize, field_h: usize, size_em: [f32; 2], size: f32, value_per_texel: f32) -> (Vec<f32>, usize, usize) {
    let qw = size_em[0] * size;
    let qh = size_em[1] * size;
    let w = qw.ceil() as usize;
    let h = qh.ceil() as usize;
    let texels_per_px = field_w as f32 / qw;
    let half = 0.5 * texels_per_px * value_per_texel;
    let mut out = vec![0.0f32; w * h];
    for py in 0..h {
        for px in 0..w {
            let gx = (px as f32 + 0.5) / qw * field_w as f32;
            let gy = (py as f32 + 0.5) / qh * field_h as f32;
            let d = sample(gx, gy);
            out[py * w + px] = ((d - 0.5) / (2.0 * half) + 0.5).clamp(0.0, 1.0);
        }
    }
    (out, w, h)
}

/// 1 チャネルの SDF（R8）を格子の座標でバイリニアに読む。
fn sample_r8(data: &[u8], w: usize, h: usize, gx: f32, gy: f32) -> f32 {
    let fx = gx - 0.5;
    let fy = gy - 0.5;
    let (x0, y0) = (fx.floor(), fy.floor());
    let (ax, ay) = (fx - x0, fy - y0);
    let read = |x: i64, y: i64| -> f32 {
        if x < 0 || y < 0 || x as usize >= w || y as usize >= h {
            return 0.0;
        }
        f32::from(data[y as usize * w + x as usize]) / 255.0
    };
    let (x0, y0) = (x0 as i64, y0 as i64);
    let top = read(x0, y0) + (read(x0 + 1, y0) - read(x0, y0)) * ax;
    let bottom = read(x0, y0 + 1) + (read(x0 + 1, y0 + 1) - read(x0, y0 + 1)) * ax;
    top + (bottom - top) * ay
}

/// 灰色の画像を RGB の帯の (x0, y0) へ書く（被覆率 1 = 黒）。
fn blit_gray(canvas: &mut image::RgbImage, img: &[f32], w: usize, h: usize, x0: usize, y0: usize) {
    for y in 0..h {
        for x in 0..w {
            let v = (255.0 * (1.0 - img[y * w + x])).round().clamp(0.0, 255.0) as u8;
            if (x0 + x) < canvas.width() as usize && (y0 + y) < canvas.height() as usize {
                canvas.put_pixel((x0 + x) as u32, (y0 + y) as u32, image::Rgb([v, v, v]));
            }
        }
    }
}

/// 字ごとの目視の画像を書く（距離場の RGB・アルファ・150 px の中央値・アルファ・1 チャネルの SDF・参照）。
fn dump_preview(dir: &str, font: &FontArc, ch: char, data: &[u8], fw: usize, fh: usize, size_em: [f32; 2]) {
    let zoom = PREVIEW_FIELD_ZOOM;
    let median = |gx: f32, gy: f32| sample_field(data, fw, fh, gx, gy, VerifyChannel::Median);
    let alpha = |gx: f32, gy: f32| sample_field(data, fw, fh, gx, gy, VerifyChannel::Alpha);
    let (m_img, rw, rh) = render_coverage(&median, fw, fh, size_em, PREVIEW_SIZE_PX, MTSDF_VALUE_PER_TEXEL);
    let (a_img, _, _) = render_coverage(&alpha, fw, fh, size_em, PREVIEW_SIZE_PX, MTSDF_VALUE_PER_TEXEL);
    let sdf = rasterize_glyph_sdf(font, ch).expect("輪郭がある");
    let (sw, sh) = (sdf.width as usize, sdf.height as usize);
    let sdf_sample = |gx: f32, gy: f32| sample_r8(&sdf.data, sw, sh, gx, gy);
    let (s_img, s_w, s_h) = render_coverage(&sdf_sample, sw, sh, sdf.size_em, PREVIEW_SIZE_PX, SDF_VALUE_PER_TEXEL);
    // 参照のラスタ（ab_glyph の被覆率）
    let glyph = font.glyph_id(ch).with_scale_and_position(PxScale::from(PREVIEW_SIZE_PX), ab_glyph::point(0.0, 0.0));
    let outlined = font.outline_glyph(glyph).expect("輪郭");
    let b = outlined.px_bounds();
    let (bw, bh) = (b.width() as usize, b.height() as usize);
    let mut r_img = vec![0.0f32; bw * bh];
    outlined.draw(|x, y, c| {
        let i = y as usize * bw + x as usize;
        if i < r_img.len() {
            r_img[i] = c;
        }
    });
    let field_w = fw * zoom;
    let field_h = fh * zoom;
    let width = field_w * 2 + rw * 2 + s_w + bw + PREVIEW_GAP * 6;
    let height = field_h.max(rh).max(s_h).max(bh);
    let mut canvas = image::RgbImage::from_pixel(width as u32, height as u32, image::Rgb([255, 255, 255]));
    // 距離場の RGB とアルファ（最近傍で拡大）
    for y in 0..field_h {
        for x in 0..field_w {
            let k = ((y / zoom) * fw + x / zoom) * MTSDF_BYTES_PER_TEXEL;
            canvas.put_pixel(x as u32, y as u32, image::Rgb([data[k], data[k + 1], data[k + 2]]));
            let a = data[k + 3];
            canvas.put_pixel((field_w + PREVIEW_GAP + x) as u32, y as u32, image::Rgb([a, a, a]));
        }
    }
    let mut x0 = field_w * 2 + PREVIEW_GAP * 2;
    blit_gray(&mut canvas, &m_img, rw, rh, x0, 0);
    x0 += rw + PREVIEW_GAP;
    blit_gray(&mut canvas, &a_img, rw, rh, x0, 0);
    x0 += rw + PREVIEW_GAP;
    blit_gray(&mut canvas, &s_img, s_w, s_h, x0, 0);
    x0 += s_w + PREVIEW_GAP;
    blit_gray(&mut canvas, &r_img, bw, bh, x0, 0);
    let path = format!("{dir}/glyph_U{:04X}.png", ch as u32);
    canvas.save(&path).expect("PNG を書く");
}

/// 【計測】字ごとの焼き時間・検査の結果（`--ignored --nocapture` で表を出す）。
#[test]
#[ignore]
fn measure_bake_times_and_checks() {
    let font = builtin();
    let dump_dir = std::env::var("SEED_MSDF_DUMP_DIR").ok();
    if let Some(dir) = &dump_dir {
        std::fs::create_dir_all(dir).expect("出力のフォルダ");
    }
    let coloring = std::env::var("SEED_MSDF_COLORING").ok().and_then(|s| ColoringStrategy::parse(&s)).unwrap_or_default();
    println!("色分け: {}", coloring.as_str());
    for (title, text) in [("一般", MEASURE_TEXT), ("画数の多い漢字", DENSE_KANJI)] {
        let mut count = 0usize;
        let mut total = Duration::ZERO;
        let mut sum = [Duration::ZERO; 4];
        let mut max = (Duration::ZERO, ' ');
        let mut fallbacks = Vec::new();
        let mut missing = Vec::new();
        println!("── {title} ──");
        println!("字 | 大きさ | 輪郭 | 辺 | 合計 ms（輪郭 / 距離 / 補正 / 検査） | 補正 | 検査の食い違い | 真の SDF");
        for ch in text.chars() {
            if !has_glyph(&font, ch) {
                missing.push(ch);
                continue;
            }
            let t = Instant::now();
            let Some((g, s)) = bake_glyph_mtsdf(&font, ch, coloring) else { continue };
            let wall = t.elapsed();
            count += 1;
            total += wall;
            sum[0] += s.time_outline;
            sum[1] += s.time_distance;
            sum[2] += s.time_correct;
            sum[3] += s.time_verify;
            if wall > max.0 {
                max = (wall, ch);
            }
            if s.fallback {
                fallbacks.push((ch, s.verify.artifact_px, s.alpha_verify.map(|a| a.artifact_px).unwrap_or(0)));
            }
            println!(
                "{ch} | {}x{} | {} | {} | {:.2}（{:.2} / {:.2} / {:.2} / {:.2}） | {} | {}/{} | {}",
                s.width,
                s.height,
                s.contours,
                s.edges,
                ms(wall),
                ms(s.time_outline),
                ms(s.time_distance),
                ms(s.time_correct),
                ms(s.time_verify),
                format!("{}（距離の確かめ {}）", s.correction.corrected, s.correction.distance_checks),
                s.verify.artifact_px,
                s.verify.threshold,
                if s.fallback { format!("落ちた（真の SDF {}）", s.alpha_verify.map(|a| a.artifact_px).unwrap_or(0)) } else { "-".into() }
            );
            if let Some(dir) = &dump_dir {
                dump_preview(dir, &font, ch, &g.data, g.width as usize, g.height as usize, g.size_em);
            }
        }
        let n = count.max(1) as f64;
        println!(
            "集計（{title}）: {count} 字 平均 {:.2} ms（輪郭 {:.2} / 距離 {:.2} / 補正 {:.2} / 検査 {:.2}） 最大 {:.2} ms（{}） 真の SDF へ落ちた字 {} {:?} 書体に無い字 {:?}",
            ms(total) / n,
            ms(sum[0]) / n,
            ms(sum[1]) / n,
            ms(sum[2]) / n,
            ms(sum[3]) / n,
            ms(max.0),
            max.1,
            fallbacks.len(),
            fallbacks,
            missing.iter().collect::<String>()
        );
    }
}

/// 【調べもの】字の輪郭の組み直しの結果（輪郭ごとの辺の数・向き・外接矩形）を出す（SEED_MSDF_CHARS で字を指定）。
#[test]
#[ignore]
fn dump_contours() {
    let font = builtin();
    let chars = std::env::var("SEED_MSDF_CHARS").unwrap_or_else(|_| "図".to_string());
    for ch in chars.chars() {
        let id = font.glyph_id(ch);
        let outline = font.outline(id).expect("輪郭");
        println!("'{ch}' 曲線 {}", outline.curves.len());
        let shape = super::outline::Shape::from_outline_curves(&outline.curves, 1.0);
        for (i, c) in shape.contours.iter().enumerate() {
            let mut min = super::geometry::v2(f64::MAX, f64::MAX);
            let mut max = super::geometry::v2(-f64::MAX, -f64::MAX);
            for e in &c.edges {
                e.extend_bounds(&mut min, &mut max);
            }
            println!("  輪郭 {i}: 辺 {} 向き {} 範囲 ({:.0},{:.0})-({:.0},{:.0}) 始点 ({:.0},{:.0})", c.edges.len(), c.winding(), min.x, min.y, max.x, max.y, c.edges[0].start().x, c.edges[0].start().y);
        }
    }
}

/// 【調べもの】字の内外（走査線の巻き数）と参照のラスタを、MTSDF の格子で文字の絵にして並べる（SEED_MSDF_CHARS で字を指定）。
#[test]
#[ignore]
fn dump_fill_ascii() {
    use super::bake::FieldLayout;
    use super::distance::PreparedShape;
    use super::params::{FieldScale, DISTANCE_CUTOFF_PX, FAR_DISTANCE_PX, MTSDF_EM_PX, MTSDF_PAD_PX};
    let _ = FieldScale::BASE;
    use ab_glyph::ScaleFont;
    let font = builtin();
    let chars = std::env::var("SEED_MSDF_CHARS").unwrap_or_else(|_| "図".to_string());
    for ch in chars.chars() {
        let id = font.glyph_id(ch);
        let outline = font.outline(id).expect("輪郭");
        let scale = f64::from(font.as_scaled(PxScale::from(MTSDF_EM_PX)).h_scale_factor());
        let mut shape = super::outline::Shape::from_outline_curves(&outline.curves, scale);
        let (min, max) = shape.bounds().unwrap();
        let layout = FieldLayout::around(min, max, i64::from(MTSDF_PAD_PX));
        super::edge_color::color_edges(&mut shape, ColoringStrategy::InkTrap);
        let prepared = PreparedShape::new(&shape, DISTANCE_CUTOFF_PX, FAR_DISTANCE_PX);
        let field = prepared.generate(&layout.grid);
        // 参照: ab_glyph の 1 倍のラスタ（テクセルの格子と同じ大きさ）
        let glyph = id.with_scale_and_position(PxScale::from(MTSDF_EM_PX), ab_glyph::point(0.0, 0.0));
        let outlined = font.outline_glyph(glyph).unwrap();
        let b = outlined.px_bounds();
        let (bw, bh) = (b.width() as usize, b.height() as usize);
        let mut cov = vec![0.0f32; bw * bh];
        outlined.draw(|x, y, c| {
            let i = y as usize * bw + x as usize;
            if i < cov.len() {
                cov[i] = c;
            }
        });
        println!("'{ch}' 左が巻き数の内外（#）と中央値の内外（*= 内外とも内・o=中央値だけ内・x=内外だけ内）、右が参照");
        for j in 0..layout.height {
            let mut left = String::new();
            let mut right = String::new();
            for i in 0..layout.width {
                let k = j * layout.width + i;
                let fill = field.inside[k];
                let d = field.texels[k];
                let m = super::geometry::median3(d[0], d[1], d[2]) > 0.0;
                left.push(match (fill, m) {
                    (true, true) => '*',
                    (false, true) => 'o',
                    (true, false) => 'x',
                    (false, false) => '.',
                });
                let rx = layout.left as i32 + i as i32 - b.min.x as i32;
                let ry = -(layout.top as i32) + j as i32 - b.min.y as i32;
                let c = if rx >= 0 && ry >= 0 && (rx as usize) < bw && (ry as usize) < bh { cov[ry as usize * bw + rx as usize] } else { 0.0 };
                right.push(if c >= 0.5 { '#' } else { '.' });
            }
            println!("{left}   {right}");
        }
    }
}

/// 参照（ab_glyph の面積の被覆率）と、距離場をシェーダーと同じ直線の傾きで塗った被覆率の差（大きさ `size` の画素の格子で比べる）。
///
/// 返り値: (平均の差〈どちらかが塗られた画素で〉, 差が 0.5 を超えた画素の数, 比べた画素の数)。
fn coverage_error(font: &FontArc, ch: char, size: f32, bearing_em: [f32; 2], size_em: [f32; 2], fw: usize, fh: usize, value_per_texel: f32, sample: &dyn Fn(f32, f32) -> f32) -> (f64, usize, usize) {
    let glyph = font.glyph_id(ch).with_scale_and_position(PxScale::from(size), ab_glyph::point(0.0, 0.0));
    let Some(outlined) = font.outline_glyph(glyph) else { return (0.0, 0, 0) };
    let b = outlined.px_bounds();
    let (rx0, ry0) = (b.min.x as i32, b.min.y as i32);
    let (rw, rh) = (b.width() as usize, b.height() as usize);
    let mut reference = vec![0.0f32; rw * rh];
    outlined.draw(|x, y, c| {
        let i = y as usize * rw + x as usize;
        if i < reference.len() {
            reference[i] = c;
        }
    });
    let qx = bearing_em[0] * size;
    let qy = bearing_em[1] * size;
    let qw = size_em[0] * size;
    let qh = size_em[1] * size;
    let texels_per_px = fw as f32 / qw;
    let half = 0.5 * texels_per_px * value_per_texel;
    let (x0, y0) = (qx.floor() as i32, qy.floor() as i32);
    let (x1, y1) = ((qx + qw).ceil() as i32, (qy + qh).ceil() as i32);
    let mut sum = 0.0f64;
    let mut n = 0usize;
    let mut bad = 0usize;
    for y in y0..y1 {
        for x in x0..x1 {
            let gx = (x as f32 + 0.5 - qx) / qw * fw as f32;
            let gy = (y as f32 + 0.5 - qy) / qh * fh as f32;
            let ours = ((sample(gx, gy) - 0.5) / (2.0 * half) + 0.5).clamp(0.0, 1.0);
            let (ix, iy) = (x - rx0, y - ry0);
            let theirs = if ix >= 0 && iy >= 0 && (ix as usize) < rw && (iy as usize) < rh { reference[iy as usize * rw + ix as usize] } else { 0.0 };
            if ours > 0.0 || theirs > 0.0 {
                let d = (ours - theirs).abs();
                sum += f64::from(d);
                n += 1;
                if d > 0.5 {
                    bad += 1;
                }
            }
        }
    }
    (if n > 0 { sum / n as f64 } else { 0.0 }, bad, n)
}

/// 【計測】大きさごとの描画の誤差（参照の被覆率との平均の差・0.5 を超えて違う画素）を、1 チャネルの SDF（em 64）と
/// MTSDF（em 40 / 48 / 56 / 64 の中央値・アルファ）で比べる（解像度と、中央値と真の SDF の切り替えの決め方の根拠）。
#[test]
#[ignore]
fn measure_coverage_error_by_size() {
    use super::bake::bake_glyph_mtsdf_at;
    use super::params::FieldScale;
    let font = builtin();
    let sizes: [f32; 11] = [12.0, 14.0, 17.0, 20.0, 24.0, 32.0, 48.0, 64.0, 96.0, 150.0, 200.0];
    let sets: [(&str, &str); 3] = [("英数", "05:27AgRSW@&"), ("かな・漢字", "あいうスヌーズ上限起きろ床成功時刻図鑑"), ("画数の多い漢字", "鬱薔薇魑魅魍魎鑑鑿鸞讒蠢艦鷹襲響驚灘麟龍鱗齷齪纏")];
    let ems: [f32; 4] = [40.0, 48.0, 56.0, 64.0];
    for (title, text) in sets {
        let chars: Vec<char> = text.chars().filter(|c| has_glyph(&font, *c)).collect();
        // 字ごとの距離場（SDF と各 em の MTSDF）を先に焼く
        let sdf: Vec<_> = chars.iter().map(|&c| rasterize_glyph_sdf(&font, c).unwrap()).collect();
        let mt: Vec<Vec<_>> = ems.iter().map(|&em| chars.iter().map(|&c| bake_glyph_mtsdf_at(&font, c, ColoringStrategy::InkTrap, &FieldScale::with_em(em)).unwrap().0).collect()).collect();
        println!("── {title}（{} 字）: 平均の差 ×1000 / 0.5 を超えて違う画素（1 字あたり）──", chars.len());
        let mut header = String::from("大きさ | SDF64");
        for em in ems {
            header += &format!(" | 中央値{em} | アルファ{em}");
        }
        println!("{header}");
        for size in sizes {
            let mut line = format!("{size:>5}");
            let mut acc = (0.0f64, 0usize);
            for (k, &c) in chars.iter().enumerate() {
                let g = &sdf[k];
                let (w, h) = (g.width as usize, g.height as usize);
                let s = |gx: f32, gy: f32| sample_r8(&g.data, w, h, gx, gy);
                let (e, b, _) = coverage_error(&font, c, size, g.bearing_em, g.size_em, w, h, SDF_VALUE_PER_TEXEL, &s);
                acc.0 += e;
                acc.1 += b;
            }
            line += &format!(" | {:.1}/{:.1}", acc.0 / chars.len() as f64 * 1000.0, acc.1 as f64 / chars.len() as f64);
            for (ei, &em) in ems.iter().enumerate() {
                let vpt = FieldScale::with_em(em).value_per_texel();
                for channel in [VerifyChannel::Median, VerifyChannel::Alpha] {
                    let mut acc = (0.0f64, 0usize);
                    for (k, &c) in chars.iter().enumerate() {
                        let g = &mt[ei][k];
                        let (w, h) = (g.width as usize, g.height as usize);
                        let s = |gx: f32, gy: f32| sample_field(&g.data, w, h, gx, gy, channel);
                        let (e, b, _) = coverage_error(&font, c, size, g.bearing_em, g.size_em, w, h, vpt, &s);
                        acc.0 += e;
                        acc.1 += b;
                    }
                    line += &format!(" | {:.1}/{:.1}", acc.0 / chars.len() as f64 * 1000.0, acc.1 as f64 / chars.len() as f64);
                }
            }
            println!("{line}");
        }
    }
}

/// 【計測】字ごとの細部の潰れ（検査の detail_px）と 150 px の誤差を em 40 / 64 で比べる（解像度を字ごとに上げる判断の根拠）。
#[test]
#[ignore]
fn measure_detail_per_glyph() {
    use super::bake::bake_glyph_mtsdf_at;
    use super::params::FieldScale;
    let font = builtin();
    let text = format!("{MEASURE_TEXT}{DENSE_KANJI}");
    println!("字 | 辺 | em40 の潰れ(画素) / 食い違い | 150px 誤差 em40 平均×1000/0.5超 | em64 の潰れ | 150px 誤差 em64");
    for ch in text.chars().filter(|c| has_glyph(&font, *c)) {
        let mut line = format!("{ch}");
        for em in [40.0f32, 64.0] {
            let scale = FieldScale::with_em(em);
            let (g, st) = bake_glyph_mtsdf_at(&font, ch, ColoringStrategy::InkTrap, &scale).unwrap();
            let (w, h) = (g.width as usize, g.height as usize);
            let s = |gx: f32, gy: f32| sample_field(&g.data, w, h, gx, gy, VerifyChannel::Median);
            let (e, b, _) = coverage_error(&font, ch, 150.0, g.bearing_em, g.size_em, w, h, scale.value_per_texel(), &s);
            if em == 40.0 {
                line += &format!(" | {}", st.edges);
            }
            line += &format!(" | {} / {} | {:.1}/{}", st.verify.detail_px, st.verify.artifact_px, e * 1000.0, b);
        }
        println!("{line}");
    }
}

/// 【計測】字の輪郭の長さ（em 単位。分割の仕方によらない複雑さの目安）と辺の数（解像度を上げる字の決め方の根拠）。
#[test]
#[ignore]
fn measure_outline_length() {
    use ab_glyph::ScaleFont;
    let font = builtin();
    let text = format!("{MEASURE_TEXT}{DENSE_KANJI}");
    let mut rows = Vec::new();
    for ch in text.chars().filter(|c| has_glyph(&font, *c)) {
        let outline = font.outline(font.glyph_id(ch)).unwrap();
        let scale = f64::from(font.as_scaled(PxScale::from(1.0)).h_scale_factor());
        let shape = super::outline::Shape::from_outline_curves(&outline.curves, scale);
        rows.push((ch, super::bake::outline_length(&shape), shape.edge_count()));
    }
    for (ch, len, edges) in rows {
        println!("{ch} 長さ {len:.2} em 辺 {edges}");
    }
}

/// 【計測】最終の設定（em は輪郭の長さで 40〜64・形は中央値と真の SDF をシェーダーと同じ規則で混ぜる）の誤差を、
/// 1 チャネルの SDF（em 64）と大きさごとに比べる。
#[test]
#[ignore]
fn measure_final_vs_sdf() {
    use super::bake::bake_glyph_mtsdf;
    let font = builtin();
    // シェーダーの切り替えの定数（text.wgsl の本文から読む）
    let wgsl = include_str!("../../renderer/shaders/text.wgsl");
    let read = |name: &str| -> f32 {
        let line = wgsl.lines().find(|l| l.trim().starts_with(&format!("const {name}:"))).expect(name);
        line.split('=').nth(1).unwrap().split(';').next().unwrap().trim().parse().unwrap()
    };
    let (m_full, t_full) = (read("TEXT_MTSDF_MEDIAN_FULL_TEXELS_PER_PX"), read("TEXT_MTSDF_TRUE_FULL_TEXELS_PER_PX"));
    let sizes: [f32; 11] = [12.0, 14.0, 17.0, 20.0, 24.0, 32.0, 48.0, 64.0, 96.0, 150.0, 200.0];
    let sets: [(&str, &str); 3] = [("英数", "05:27AgRSW@&"), ("かな・漢字", "あいうスヌーズ上限起きろ床成功時刻図鑑"), ("画数の多い漢字", "鬱薔薇魑魅魍魎鑑鑿鸞讒蠢艦鷹襲響驚灘麟龍鱗齷齪纏")];
    for (title, text) in sets {
        let chars: Vec<char> = text.chars().filter(|c| has_glyph(&font, *c)).collect();
        let sdf: Vec<_> = chars.iter().map(|&c| rasterize_glyph_sdf(&font, c).unwrap()).collect();
        let mt: Vec<_> = chars.iter().map(|&c| bake_glyph_mtsdf(&font, c, ColoringStrategy::InkTrap).unwrap().0).collect();
        let avg_em: f32 = mt.iter().map(|g| g.em_px).sum::<f32>() / mt.len() as f32;
        println!("── {title}（{} 字・MTSDF の em の平均 {avg_em:.1}）: 平均の差 ×1000 / 0.5 を超えて違う画素（1 字あたり）──", chars.len());
        println!("大きさ | SDF64 | MTSDF（混ぜ）| MTSDF 中央値 | MTSDF アルファ");
        for size in sizes {
            let mut cols = Vec::new();
            let mut acc = (0.0f64, 0usize);
            for (k, &c) in chars.iter().enumerate() {
                let g = &sdf[k];
                let (w, h) = (g.width as usize, g.height as usize);
                let s = |gx: f32, gy: f32| sample_r8(&g.data, w, h, gx, gy);
                let (e, b, _) = coverage_error(&font, c, size, g.bearing_em, g.size_em, w, h, SDF_VALUE_PER_TEXEL, &s);
                acc.0 += e;
                acc.1 += b;
            }
            cols.push(acc);
            for mode in 0..3 {
                let mut acc = (0.0f64, 0usize);
                for (k, &c) in chars.iter().enumerate() {
                    let g = &mt[k];
                    let (w, h) = (g.width as usize, g.height as usize);
                    let texels = w as f32 / (g.size_em[0] * size);
                    let t = ((texels - m_full) / (t_full - m_full)).clamp(0.0, 1.0);
                    let s = |gx: f32, gy: f32| {
                        let m = sample_field(&g.data, w, h, gx, gy, VerifyChannel::Median);
                        let a = sample_field(&g.data, w, h, gx, gy, VerifyChannel::Alpha);
                        match mode {
                            0 => m + (a - m) * t,
                            1 => m,
                            _ => a,
                        }
                    };
                    let vpt = 0.5 / (0.125 * g.em_px);
                    let (e, b, _) = coverage_error(&font, c, size, g.bearing_em, g.size_em, w, h, vpt, &s);
                    acc.0 += e;
                    acc.1 += b;
                }
                cols.push(acc);
            }
            let n = chars.len() as f64;
            let line: Vec<String> = cols.iter().map(|(e, b)| format!("{:.1}/{:.1}", e / n * 1000.0, *b as f64 / n)).collect();
            println!("{size:>5} | {}", line.join(" | "));
        }
    }
}
