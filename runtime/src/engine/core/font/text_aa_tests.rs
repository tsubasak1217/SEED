// ============================================================
//  font/text_aa_tests.rs — 文字の塗り方（text.wgsl）の CPU の写しと試験（テストだけのモジュール）
//
//  【なぜ要るか（2026-10-01。docs/ui_components.md §12）】
//  PC の等倍（1 dp = 1 画素）で 17 px 以下の文字の細い横画が消え、「ー」「−」が消える・「ス」が「メ」に見えた。
//  原因は text.wgsl が平滑化の幅を距離場の値の微分 fwidth(d) から決めていたこと（1 画素より細い横画の尾根を
//  2×2 の画素の組が挟むと fwidth(d) ≒ 0 → 閾値の切り捨てになり、画素の中心の間に落ちた横画がまるごと消える）。
//  シェーダーは GPU でしか動かないので、ここで同じ式を CPU で再現し、実際のグリフの距離場を
//  画素の中心でバイリニアに読んで、縦の位置を 0.05 画素ずつずらしても横画が消えないことを確かめる。
//
//  【MTSDF（2026-10-02）】同じ塗り方（shade）を MTSDF でも使う。中央値と真の SDF の切り替え（mtsdf_true_weight）と、
//  太さを真の SDF で付ける規則（太らせ = 形 ∪ 真の SDF の太らせ）を写し、MTSDF のグリフでも横画が消えないこと・
//  縁の傾きが 1 画素であること・太らせた角が尖って伸びないことを確かめる。
//
//  【定数の出どころ】シェーダーの定数は text.wgsl の本文から読む（写しの式に数を二重に書かない）。
//  距離場の片側の幅（em）は font/sdf.rs・font/msdf/params.rs と一致することを確かめる。
//  式を変えるときは text.wgsl とこのファイルの `shade` を両方直すこと。
// ============================================================

use std::collections::HashMap;

use super::glyph_field::DistanceFieldKind;
use super::msdf::params::{MTSDF_SPREAD_EM, MTSDF_EM_PX};
use super::rasterizer::{GlyphField, rasterize_glyph_sdf};
use super::sdf::{SDF_EDGE_VALUE, SDF_EM_PX, SDF_SPREAD_EM, SDF_SPREAD_PX, SDF_VALUE_PER_TEXEL};

/// シェーダーの本文（pipeline.rs と同じもの）。
const TEXT_WGSL: &str = include_str!("../renderer/shaders/text.wgsl");

/// 副画素の位置を何画素ずつずらして確かめるか。
const SUBPIXEL_STEP_PX: f32 = 0.05;
/// 副画素の位置の数（0.0〜0.95 の 20 通り）。
const SUBPIXEL_STEPS: usize = 20;
/// 横画の真ん中として見る列の範囲（グリフの幅に対する割合）。丸い端を避ける。
const STROKE_MID_COLS: (f32, f32) = (0.3, 0.7);
/// 等倍で試す文字の大きさ（画面の画素。SEED の文字の大きさの単位）。17 は Wake or Pay の本文、0.9 倍の縮小（15.3・18）も含む。
const SMALL_SIZES_PX: [f32; 7] = [12.0, 14.0, 15.3, 16.0, 17.0, 18.0, 20.0];
/// 横画の真ん中の列で「いちばん濃い画素の alpha」が、横画の太さ（画素）のこの割合を下回ってはいけない
/// （太さ t の横画が 2 行にちょうど割れたときの正しい塗りは各行 t/2。直す前は 0 になっていた）。
const MIN_STROKE_PEAK_PER_THICKNESS: f32 = 0.45;
/// 横画の列の alpha の和（塗られた量）が、距離場の上の横画の太さ（画素）に対してこれを下回ってはいけない。
const MIN_STROKE_INK_RATIO: f32 = 0.85;
/// バイリニアで読むときの、テクセルの中心のずれ（テクセルの中心は i + 0.5）。
const TEXEL_CENTER: f32 = 0.5;
/// u8 の距離場の値の最大。
const SDF_BYTE_MAX: f32 = 255.0;
/// 縁の傾きの幅を測るとき「縁の途中」とみなす alpha の範囲（この間の位置の幅を測る）。
const RAMP_ALPHA_RANGE: (f32, f32) = (0.02, 0.98);
/// 縁の傾きの幅（画素）の許す範囲。直線の 1 画素の傾きなら 0.02〜0.98 の幅は 0.96 画素（u8 の量子化の分を許す）。
const RAMP_WIDTH_OK_PX: std::ops::RangeInclusive<f32> = 0.8..=1.1;
/// 縁の傾きを読む細かさ（1 画素を何分割するか）と、縁の前後に読む範囲（画素）。
const RAMP_SCAN_STEPS_PER_PX: usize = 64;
const RAMP_SCAN_SPAN_PX: f32 = 3.0;
/// 縁の傾きを確かめる倍率（1 画素が何テクセルか）: 0.25（4 倍に拡大。150 px の字ほど）〜 5（12.8 px の字ほど）。
const RAMP_TEXELS_PER_PX: [f32; 7] = [0.25, 0.43, 1.0, 1.43, 2.0, 3.76, 5.0];
/// 影の試験のぼかしの幅（値の単位）。
const SHADOW_TEST_SOFTNESS: f32 = 0.1;

// ── シェーダーの定数の読み取り ─────────────────────────────────

/// text.wgsl の `const 名前: f32 = 数;` を表にする（式で書いた定数は入らない）。
fn wgsl_float_consts() -> HashMap<String, f32> {
    let mut out = HashMap::new();
    for line in TEXT_WGSL.lines() {
        let Some(rest) = line.trim().strip_prefix("const ") else { continue };
        let Some((name, rest)) = rest.split_once(':') else { continue };
        let Some((_, rest)) = rest.split_once('=') else { continue };
        let Some((value, _)) = rest.split_once(';') else { continue };
        if let Ok(v) = value.trim().parse::<f32>() {
            out.insert(name.trim().to_string(), v);
        }
    }
    out
}

/// シェーダーの塗り方の定数（text.wgsl から読んだもの）。
struct ShaderConsts {
    edge: f32,
    spread_em: f32,
    aa_half_width_px: f32,
    coverage_at_edge: f32,
    min_aa_width: f32,
    min_texels_per_px: f32,
    dilate_max_px: f32,
    dilate_full_size_px: f32,
    dilate_none_size_px: f32,
    median_full_texels_per_px: f32,
    true_full_texels_per_px: f32,
}

impl ShaderConsts {
    /// text.wgsl から読む（無ければ失敗させる＝名前を変えたらこのテストも直す）。
    fn load() -> Self {
        let c = wgsl_float_consts();
        let get = |name: &str| *c.get(name).unwrap_or_else(|| panic!("text.wgsl に {name} が無い"));
        Self {
            edge: get("TEXT_SDF_EDGE"),
            spread_em: get("TEXT_FIELD_SPREAD_EM"),
            aa_half_width_px: get("TEXT_AA_HALF_WIDTH_PX"),
            coverage_at_edge: get("TEXT_COVERAGE_AT_EDGE"),
            min_aa_width: get("TEXT_MIN_AA_WIDTH"),
            min_texels_per_px: get("TEXT_MIN_TEXELS_PER_PX"),
            dilate_max_px: get("TEXT_SMALL_DILATE_MAX_PX"),
            dilate_full_size_px: get("TEXT_SMALL_DILATE_FULL_SIZE_PX"),
            dilate_none_size_px: get("TEXT_SMALL_DILATE_NONE_SIZE_PX"),
            median_full_texels_per_px: get("TEXT_MTSDF_MEDIAN_FULL_TEXELS_PER_PX"),
            true_full_texels_per_px: get("TEXT_MTSDF_TRUE_FULL_TEXELS_PER_PX"),
        }
    }

    /// 1 テクセル進むと値がいくつ変わるか（解像度 field_em の距離場）。text.wgsl の shade の最初の式。
    fn value_per_texel(&self, field_em: f32) -> f32 {
        self.edge / (self.spread_em * field_em)
    }
}

// ── シェーダーの式の写し（text.wgsl の shade・fs_sdf・fs_mtsdf と同じ順） ──────────

/// smoothstep（WGSL と同じ定義）。
fn smoothstep(e0: f32, e1: f32, x: f32) -> f32 {
    let t = ((x - e0) / (e1 - e0)).clamp(0.0, 1.0);
    t * t * (3.0 - 2.0 * t)
}

/// 小さな文字で縁を外へ寄せる量（画面の画素）。text.wgsl の `small_text_dilation_px`。
fn small_text_dilation_px(c: &ShaderConsts, size_px: f32) -> f32 {
    let t = ((c.dilate_none_size_px - size_px) / (c.dilate_none_size_px - c.dilate_full_size_px)).clamp(0.0, 1.0);
    c.dilate_max_px * t
}

/// 縁の被覆率。text.wgsl の `edge_coverage`（傾きの全体の幅 = 半分の幅の 2 倍）。
fn edge_coverage(c: &ShaderConsts, d: f32, edge: f32, half_w: f32, softness: f32) -> f32 {
    if softness > 0.0 {
        smoothstep(edge - half_w, edge + half_w, d)
    } else {
        ((d - edge) / (2.0 * half_w) + c.coverage_at_edge).clamp(0.0, 1.0)
    }
}

/// MTSDF の形に真の SDF をどれだけ混ぜるか。text.wgsl の `mtsdf_true_weight`。
fn mtsdf_true_weight(c: &ShaderConsts, texels: f32) -> f32 {
    ((texels - c.median_full_texels_per_px) / (c.true_full_texels_per_px - c.median_full_texels_per_px)).clamp(0.0, 1.0)
}

/// 3 つの中央値。
fn median3(a: f32, b: f32, c: f32) -> f32 {
    a.min(b).max(a.max(b).min(c))
}

/// 本体の alpha（縁取り・色は見ない）。text.wgsl の `shade` の本体の部分。
///
/// - `d_shape` / `d_true`: 形の距離と真の SDF（SDF は同じ値）
/// - `texels_per_px`: 画面の 1 画素がアトラスの何テクセルか（UV の微分）
/// - `field_em`: このグリフの解像度（em あたりのテクセル数）
fn fill_alpha(c: &ShaderConsts, d_shape: f32, d_true: f32, texels_per_px: f32, field_em: f32, weight_dist: f32, softness: f32) -> f32 {
    let texels = texels_per_px.max(c.min_texels_per_px);
    let value_per_px = texels * c.value_per_texel(field_em);
    let w = (value_per_px * c.aa_half_width_px).max(c.min_aa_width) + softness;
    let dilation = small_text_dilation_px(c, field_em / texels) * value_per_px;
    let base_edge = c.edge - dilation;
    let edge = base_edge - weight_dist;
    let shape_a = edge_coverage(c, d_shape, base_edge, w, softness);
    let true_a = edge_coverage(c, d_true, edge, w, softness);
    let weighted = if weight_dist > 0.0 { shape_a.max(true_a) } else { shape_a.min(true_a) };
    let body = if weight_dist == 0.0 { shape_a } else { weighted };
    if softness > 0.0 { true_a } else { body }
}

/// fs_sdf の本体の alpha（R8 の値 d）。
fn fill_alpha_sdf(c: &ShaderConsts, d: f32, texels_per_px: f32, weight_dist: f32, softness: f32) -> f32 {
    fill_alpha(c, d, d, texels_per_px, SDF_EM_PX, weight_dist, softness)
}

/// fs_mtsdf の本体の alpha（RGBA の値）。
fn fill_alpha_mtsdf(c: &ShaderConsts, s: [f32; 4], texels_per_px: f32, field_em: f32, weight_dist: f32, softness: f32) -> f32 {
    let texels = texels_per_px.max(c.min_texels_per_px);
    let t = mtsdf_true_weight(c, texels);
    let m = median3(s[0], s[1], s[2]);
    let d_shape = m + (s[3] - m) * t;
    fill_alpha(c, d_shape, s[3], texels_per_px, field_em, weight_dist, softness)
}

// ── グリフを画面の画素で読む ──────────────────────────────────

/// 距離場をテクセル座標（テクセルの中心が i + 0.5）でバイリニアに読む（全チャネル。R8 は 1 チャネルを 4 つに写す）。
/// 範囲の外は 0（アトラスの隙間）。
fn sample_bilinear(g: &GlyphField, tx: f32, ty: f32) -> [f32; 4] {
    let bpt = g.kind.bytes_per_texel();
    let at = |x: i64, y: i64| -> [f32; 4] {
        if x < 0 || y < 0 || x >= i64::from(g.width) || y >= i64::from(g.height) {
            return [0.0; 4];
        }
        let k = ((y as usize) * g.width as usize + x as usize) * bpt;
        if bpt == 1 {
            let v = f32::from(g.data[k]) / SDF_BYTE_MAX;
            [v; 4]
        } else {
            [0, 1, 2, 3].map(|i| f32::from(g.data[k + i]) / SDF_BYTE_MAX)
        }
    };
    let fx = tx - TEXEL_CENTER;
    let fy = ty - TEXEL_CENTER;
    let (x0, y0) = (fx.floor(), fy.floor());
    let (ax, ay) = (fx - x0, fy - y0);
    let (x0, y0) = (x0 as i64, y0 as i64);
    let (a, b, cc, d) = (at(x0, y0), at(x0 + 1, y0), at(x0, y0 + 1), at(x0 + 1, y0 + 1));
    [0, 1, 2, 3].map(|i| a[i] * (1.0 - ax) * (1.0 - ay) + b[i] * ax * (1.0 - ay) + cc[i] * (1.0 - ax) * ay + d[i] * ax * ay)
}

/// グリフの本体の alpha（距離場の種類に合わせて fs_sdf / fs_mtsdf の写しで塗る）。
fn glyph_alpha(c: &ShaderConsts, g: &GlyphField, s: [f32; 4], texels_per_px: f32, weight_dist: f32) -> f32 {
    match g.kind {
        DistanceFieldKind::Sdf => fill_alpha_sdf(c, s[0], texels_per_px, weight_dist, 0.0),
        DistanceFieldKind::Mtsdf => fill_alpha_mtsdf(c, s, texels_per_px, g.em_px, weight_dist, 0.0),
    }
}

/// 等倍でグリフを描いたときの本体の alpha の格子（行 × 列）と、横画の真ん中の列の範囲。
///
/// `size_px` の文字を、クアッドの左上が (0, offset_y) 画素に来るよう置く（x は画素の格子に揃え、y だけ副画素でずらす）。
fn render_glyph(c: &ShaderConsts, g: &GlyphField, size_px: f32, offset_y: f32) -> (Vec<Vec<f32>>, std::ops::Range<usize>) {
    let quad_w = g.size_em[0] * size_px;
    let quad_h = g.size_em[1] * size_px;
    // 画面の 1 画素がアトラスの何テクセルか（等倍・回転なし: 縦横とも同じ）
    let texels_per_px = g.width as f32 / quad_w;
    let cols = quad_w.ceil() as usize + 1;
    let rows = (quad_h + offset_y).ceil() as usize + 1;
    let mut grid = vec![vec![0.0f32; cols]; rows];
    for (py, row) in grid.iter_mut().enumerate() {
        for (px, a) in row.iter_mut().enumerate() {
            let tx = (px as f32 + TEXEL_CENTER) / quad_w * g.width as f32;
            let ty = (py as f32 + TEXEL_CENTER - offset_y) / quad_h * g.height as f32;
            *a = glyph_alpha(c, g, sample_bilinear(g, tx, ty), texels_per_px, 0.0);
        }
    }
    let mid = (cols as f32 * STROKE_MID_COLS.0) as usize..(cols as f32 * STROKE_MID_COLS.1) as usize;
    (grid, mid)
}

/// 距離場の上の横画の太さ（画素）: 真ん中の列で値が縁（0.5）以上のテクセルの数 × 1 テクセルの画素。
/// MTSDF は真の SDF（アルファ）で数える。
fn stroke_thickness_px(g: &GlyphField, size_px: f32) -> f32 {
    let col = g.width as usize / 2;
    let bpt = g.kind.bytes_per_texel();
    let channel = bpt - 1;
    let inside = (0..g.height as usize)
        .filter(|&y| f32::from(g.data[(y * g.width as usize + col) * bpt + channel]) / SDF_BYTE_MAX >= SDF_EDGE_VALUE)
        .count();
    inside as f32 * size_px / g.em_px
}

fn builtin_font() -> ab_glyph::FontArc {
    ab_glyph::FontArc::try_from_slice(super::DEFAULT_FONT_BYTES).expect("組み込みフォントは必ず読める")
}

/// 試験に使う距離場（同じ字を SDF と MTSDF で）。
fn both_fields(font: &ab_glyph::FontArc, ch: char) -> [GlyphField; 2] {
    let sdf = rasterize_glyph_sdf(font, ch).expect("アウトラインを持つ字");
    let (mtsdf, _) = super::msdf::bake_glyph_mtsdf(font, ch, super::msdf::ColoringStrategy::InkTrap).expect("アウトラインを持つ字");
    [sdf, mtsdf]
}

// ── 試験 ──────────────────────────────────────────────────────

/// シェーダーの距離場の定数が Rust と一致する（焼く側と読む側の食い違いの検出）。
#[test]
fn shader_field_constants_match_rust() {
    let c = wgsl_float_consts();
    assert_eq!(c["TEXT_FIELD_SPREAD_EM"], SDF_SPREAD_EM, "text.wgsl の TEXT_FIELD_SPREAD_EM と sdf.rs の SDF_SPREAD_EM");
    assert_eq!(c["TEXT_FIELD_SPREAD_EM"], MTSDF_SPREAD_EM, "text.wgsl の TEXT_FIELD_SPREAD_EM と msdf/params.rs の MTSDF_SPREAD_EM");
    // SDF（em 64・spread 8）の 1 テクセルの値の変化は 2026-10-01 と同じ 0.0625
    assert!((ShaderConsts::load().value_per_texel(SDF_EM_PX) - SDF_VALUE_PER_TEXEL).abs() < 1e-7);
    assert_eq!(SDF_SPREAD_PX as f32, SDF_SPREAD_EM * SDF_EM_PX);
    assert_eq!(c["TEXT_AA_HALF_WIDTH_PX"], 0.5, "平滑化は縁の前後 ±0.5 画素（1 画素の傾き）");
    let sc = ShaderConsts::load();
    assert!(sc.median_full_texels_per_px < sc.true_full_texels_per_px, "中央値だけの範囲 < 真の SDF だけの範囲");
}

/// 【不具合の再発防止】等倍で「ー」「−」の横画を縦に 0.05 画素ずつずらしても、横画が消えない
/// （真ん中の列のいちばん濃い画素の alpha・塗られた量が、どちらも横画の太さに対する下限の割合以上）。SDF と MTSDF の両方。
#[test]
fn thin_horizontal_strokes_never_vanish_at_1x() {
    let c = ShaderConsts::load();
    let font = builtin_font();
    for ch in ['ー', '−'] {
        for g in both_fields(&font, ch) {
            for size in SMALL_SIZES_PX {
                let thickness = stroke_thickness_px(&g, size);
                for step in 0..SUBPIXEL_STEPS {
                    let offset = step as f32 * SUBPIXEL_STEP_PX;
                    let (grid, mid) = render_glyph(&c, &g, size, offset);
                    let n = mid.len() as f32;
                    let peak: f32 = mid.clone().map(|x| grid.iter().map(|r| r[x]).fold(0.0, f32::max)).sum::<f32>() / n;
                    let ink: f32 = mid.clone().map(|x| grid.iter().map(|r| r[x]).sum::<f32>()).sum::<f32>() / n;
                    let kind = g.kind.as_str();
                    assert!(
                        peak >= thickness * MIN_STROKE_PEAK_PER_THICKNESS,
                        "{kind} '{ch}' 大きさ {size} 縦のずれ {offset:.2}: 横画のいちばん濃い alpha {peak:.3} < 太さ {thickness:.3} × {MIN_STROKE_PEAK_PER_THICKNESS}"
                    );
                    assert!(
                        ink >= thickness * MIN_STROKE_INK_RATIO,
                        "{kind} '{ch}' 大きさ {size} 縦のずれ {offset:.2}: 塗られた量 {ink:.3} 画素 < 太さ {thickness:.3} × {MIN_STROKE_INK_RATIO}"
                    );
                }
            }
        }
    }
}

/// 縁の平滑化はどの倍率でも画面の 1 画素の幅（縁の前後 ±0.5 画素）: 縦の境目の SDF を拡大・縮小して読み、
/// 0 と 1 の間の画素の並びの幅を測る（縁までの距離の SDF と UV の微分の組み合わせで決まる。大きい字で硬くならない）。
#[test]
fn edge_ramp_is_one_pixel_wide_at_any_scale() {
    use super::rasterizer::generate_sdf;
    let c = ShaderConsts::load();
    const W: u32 = 40;
    const EDGE_X: u32 = 20; // x < 20 が内側
    let bitmap: Vec<u8> = (0..W).map(|x| if x < EDGE_X { 255 } else { 0 }).collect();
    let sdf = generate_sdf(&bitmap, W, 1, SDF_SPREAD_PX);
    let value = |tx: f32| -> f32 {
        // 1 行の画像を横だけバイリニアに読む
        let fx = tx - TEXEL_CENTER;
        let x0 = fx.floor().clamp(0.0, (W - 1) as f32) as usize;
        let x1 = (x0 + 1).min(W as usize - 1);
        let a = (fx - fx.floor()).clamp(0.0, 1.0);
        (f32::from(sdf[x0]) * (1.0 - a) + f32::from(sdf[x1]) * a) / SDF_BYTE_MAX
    };
    for texels_per_px in RAMP_TEXELS_PER_PX {
        let width = ramp_width(|tx| fill_alpha_sdf(&c, value(tx), texels_per_px, 0.0, 0.0), EDGE_X as f32, texels_per_px);
        // 直線の 1 画素の傾きなら 0.96 画素。量子化の分だけ許す
        assert!(RAMP_WIDTH_OK_PX.contains(&width), "1 画素 = {texels_per_px} テクセル: 縁の傾きの幅 {width:.3} 画素");
    }
}

/// 縁（テクセル座標 `edge_x`）の前後を細かく読み、alpha が縁の途中（RAMP_ALPHA_RANGE の中）にある位置の幅（画素）を測る。
fn ramp_width(alpha_at: impl Fn(f32) -> f32, edge_x: f32, texels_per_px: f32) -> f32 {
    let mut first_partial = None;
    let mut last_partial = None;
    let scan_steps = (RAMP_SCAN_SPAN_PX * 2.0) as usize * RAMP_SCAN_STEPS_PER_PX;
    for k in 0..scan_steps {
        let offset_px = k as f32 / RAMP_SCAN_STEPS_PER_PX as f32 - RAMP_SCAN_SPAN_PX;
        let a = alpha_at(edge_x + offset_px * texels_per_px);
        if a > RAMP_ALPHA_RANGE.0 && a < RAMP_ALPHA_RANGE.1 {
            first_partial.get_or_insert(offset_px);
            last_partial = Some(offset_px);
        }
    }
    last_partial.unwrap() - first_partial.unwrap()
}

/// MTSDF でも縁の傾きはどの倍率でも 1 画素（中央値・真の SDF・その混ぜ合わせのどれで読んでも）。
/// 正方形の MTSDF（render_shape）の左の縦の縁を横切って読む。
#[test]
fn mtsdf_edge_ramp_is_one_pixel_wide_at_any_scale() {
    use super::msdf::bake::{render_shape, BakeStats};
    use super::msdf::geometry::v2;
    use super::msdf::outline::{Contour, Shape};
    use super::msdf::params::FieldScale;
    use super::msdf::segment::EdgeSegment;
    let c = ShaderConsts::load();
    let s = 30.0;
    let mut shape = Shape {
        contours: vec![Contour {
            edges: vec![
                EdgeSegment::line(v2(0.0, 0.0), v2(0.0, s)),
                EdgeSegment::line(v2(0.0, s), v2(s, s)),
                EdgeSegment::line(v2(s, s), v2(s, 0.0)),
                EdgeSegment::line(v2(s, 0.0), v2(0.0, 0.0)),
            ],
        }],
    };
    let scale = FieldScale::BASE;
    let (data, layout) = render_shape(&mut shape, super::msdf::ColoringStrategy::InkTrap, &scale, &mut BakeStats::default()).expect("形");
    let g = GlyphField {
        data,
        width: layout.width as u32,
        height: layout.height as u32,
        kind: DistanceFieldKind::Mtsdf,
        bearing_em: [0.0; 2],
        size_em: [0.0; 2],
        advance_em: 0.0,
        pad_em: 0.0,
        em_px: scale.em_px,
        msdf_fallback: false,
    };
    // 左の縁は格子の x = -left（形の x = 0）、真ん中の行で読む
    let edge_x = (0.0 - layout.left as f64) as f32;
    let row_y = layout.height as f32 * 0.5;
    for texels_per_px in RAMP_TEXELS_PER_PX {
        let width = ramp_width(|tx| fill_alpha_mtsdf(&c, sample_bilinear(&g, tx, row_y), texels_per_px, g.em_px, 0.0, 0.0), edge_x, texels_per_px);
        assert!(RAMP_WIDTH_OK_PX.contains(&width), "MTSDF 1 画素 = {texels_per_px} テクセル: 縁の傾きの幅 {width:.3} 画素");
    }
}

/// 【太さの意味】MTSDF で太らせた（Weight > 0）角は丸い（真の SDF で太らせる）: 正方形の角の斜め外で、角の頂点から
/// 太さより遠い点は塗らない（中央値の疑似距離で太らせると角が尖って伸び、そこまで塗ってしまう）。角の頂点より内側は塗る。
#[test]
fn mtsdf_weight_rounds_convex_corners() {
    use super::msdf::bake::{render_shape, BakeStats};
    use super::msdf::geometry::v2;
    use super::msdf::outline::{Contour, Shape};
    use super::msdf::params::FieldScale;
    use super::msdf::segment::EdgeSegment;
    let c = ShaderConsts::load();
    let s = 30.0;
    let mut shape = Shape {
        contours: vec![Contour {
            edges: vec![
                EdgeSegment::line(v2(0.0, 0.0), v2(0.0, s)),
                EdgeSegment::line(v2(0.0, s), v2(s, s)),
                EdgeSegment::line(v2(s, s), v2(s, 0.0)),
                EdgeSegment::line(v2(s, 0.0), v2(0.0, 0.0)),
            ],
        }],
    };
    let scale = FieldScale::BASE;
    let (data, layout) = render_shape(&mut shape, super::msdf::ColoringStrategy::InkTrap, &scale, &mut BakeStats::default()).expect("形");
    let g = GlyphField {
        data,
        width: layout.width as u32,
        height: layout.height as u32,
        kind: DistanceFieldKind::Mtsdf,
        bearing_em: [0.0; 2],
        size_em: [0.0; 2],
        advance_em: 0.0,
        pad_em: 0.0,
        em_px: scale.em_px,
        msdf_fallback: false,
    };
    // 左上の角の頂点（格子の座標）。大きく拡大（1 画素 = 0.25 テクセル）して読む＝形は中央値だけ
    let corner = ((0.0 - layout.left as f64) as f32, (layout.top as f64 - s) as f32);
    let texels_per_px = 0.25;
    // 太さ 2 テクセル（値の単位）
    let weight_texels = 2.0;
    let weight_dist = weight_texels * scale.value_per_texel();
    // 角の斜め外へ 2.4 テクセル（太さ 2 より遠い・疑似距離の尖った角〈2√2 = 2.83〉より近い）: 塗らない
    let d = 2.4 / std::f32::consts::SQRT_2;
    let outside = sample_bilinear(&g, corner.0 - d, corner.1 - d);
    let a = fill_alpha_mtsdf(&c, outside, texels_per_px, g.em_px, weight_dist, 0.0);
    assert!(a < 0.1, "太らせた角が尖って伸びた（真の距離 2.4 > 太さ 2 の所の alpha {a}）");
    // 角の斜め外へ 1.4 テクセル（太さより近い）: 塗る
    let d = 1.4 / std::f32::consts::SQRT_2;
    let inside = sample_bilinear(&g, corner.0 - d, corner.1 - d);
    let a = fill_alpha_mtsdf(&c, inside, texels_per_px, g.em_px, weight_dist, 0.0);
    assert!(a > 0.9, "太らせた範囲の中が塗られていない（alpha {a}）");
    // 太さ 0 なら角の頂点のすぐ内側まで塗る（中央値で角が立つ）
    let tip = sample_bilinear(&g, corner.0 + 0.15, corner.1 + 0.15);
    let a = fill_alpha_mtsdf(&c, tip, texels_per_px, g.em_px, 0.0, 0.0);
    assert!(a > 0.5, "太さ 0 の角の頂点の内側（alpha {a}）");
}

/// 中央値と真の SDF の切り替え: 拡大（1 画素が少ないテクセル）は中央値だけ、縮小は真の SDF だけ、間は単調に増える。
#[test]
fn mtsdf_switches_from_median_to_true_sdf_by_scale() {
    let c = ShaderConsts::load();
    assert_eq!(mtsdf_true_weight(&c, 0.25), 0.0, "4 倍の拡大（150 px）は中央値だけ");
    assert_eq!(mtsdf_true_weight(&c, MTSDF_EM_PX / 17.0 - 0.5), 0.0, "em 40 の 20 px 前後は中央値だけ");
    assert_eq!(mtsdf_true_weight(&c, MTSDF_EM_PX / 10.0), 1.0, "em 40 の 10 px は真の SDF だけ");
    let mut prev = 0.0;
    for k in 0..=40 {
        let t = mtsdf_true_weight(&c, k as f32 * 0.1);
        assert!(t >= prev, "単調");
        prev = t;
    }
}

/// ぼかし（影）の縁は従来どおり smoothstep の S 字（直線の傾きにしない）で、幅はぼかしのぶん広がる。
#[test]
fn soft_shadow_edges_keep_smoothstep() {
    let c = ShaderConsts::load();
    let texels_per_px = 1.0;
    let softness = SHADOW_TEST_SOFTNESS;
    let vpt = c.value_per_texel(SDF_EM_PX);
    // 縁の上は縁の被覆率（0.5）、ぼかしの幅の外では 0 と 1（幅はシェーダーと同じ式: 下限つきの平滑化の半分の幅 + ぼかし）
    let w = (texels_per_px * vpt * c.aa_half_width_px).max(c.min_aa_width) + softness;
    let edge = c.edge - small_text_dilation_px(&c, SDF_EM_PX / texels_per_px) * texels_per_px * vpt;
    assert!((fill_alpha_sdf(&c, edge, texels_per_px, 0.0, softness) - c.coverage_at_edge).abs() < 1e-5);
    assert_eq!(fill_alpha_sdf(&c, edge - w, texels_per_px, 0.0, softness), 0.0);
    assert_eq!(fill_alpha_sdf(&c, edge + w, texels_per_px, 0.0, softness), 1.0);
    // S 字: 縁と端の間（1/4 の所）は直線（0.25）より小さい
    let quarter = fill_alpha_sdf(&c, edge - w * 0.5, texels_per_px, 0.0, softness);
    assert!(quarter < 0.25, "smoothstep の S 字 {quarter}");
}

/// 大きな文字（実機の 2.625 倍の 17 dp ≒ 44.6 px 以上）は太らせない（見た目を変えない）。
#[test]
fn large_text_is_not_dilated() {
    let c = ShaderConsts::load();
    for size in [c.dilate_none_size_px, 44.6, 64.0, 150.0] {
        assert_eq!(small_text_dilation_px(&c, size), 0.0, "大きさ {size}");
    }
    assert!(small_text_dilation_px(&c, c.dilate_full_size_px) <= c.dilate_max_px);
}
