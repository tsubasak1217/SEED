// ============================================================
//  font/text_aa_tests.rs — 文字の塗り方（text.wgsl）の CPU の写しと試験（テストだけのモジュール）
//
//  【なぜ要るか（2026-10-01。docs/ui_components.md §12）】
//  PC の等倍（1 dp = 1 画素）で 17 px 以下の文字の細い横画が消え、「ー」「−」が消える・「ス」が「メ」に見えた。
//  原因は text.wgsl が平滑化の幅を距離場の値の微分 fwidth(d) から決めていたこと（1 画素より細い横画の尾根を
//  2×2 の画素の組が挟むと fwidth(d) ≒ 0 → 閾値の切り捨てになり、画素の中心の間に落ちた横画がまるごと消える）。
//  シェーダーは GPU でしか動かないので、ここで同じ式を CPU で再現し、実際のグリフの SDF（rasterize_glyph_sdf）を
//  画素の中心でバイリニアに読んで、縦の位置を 0.05 画素ずつずらしても横画が消えないことを確かめる。
//
//  【定数の出どころ】シェーダーの定数は text.wgsl の本文から読む（写しの式に数を二重に書かない）。
//  SDF の焼き方の定数（em・spread・1 テクセルあたりの値の変化）は font/sdf.rs と一致することを確かめる。
//  式を変えるときは text.wgsl とこのファイルの `fill_alpha` を両方直すこと。
// ============================================================

use std::collections::HashMap;

use super::rasterizer::{GlyphSdf, rasterize_glyph_sdf};
use super::sdf::{SDF_EDGE_VALUE, SDF_EM_PX, SDF_SPREAD_PX, SDF_VALUE_PER_TEXEL};

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
/// 横画の列の alpha の和（塗られた量）が、SDF の上の横画の太さ（画素）に対してこれを下回ってはいけない。
const MIN_STROKE_INK_RATIO: f32 = 0.85;
/// バイリニアで読むときの、テクセルの中心のずれ（テクセルの中心は i + 0.5）。
const TEXEL_CENTER: f32 = 0.5;
/// u8 の SDF の値の最大。
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
/// 影の試験のぼかしの幅（SDF の値の単位）。
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
    em_px: f32,
    value_per_texel: f32,
    aa_half_width_px: f32,
    coverage_at_edge: f32,
    min_aa_width: f32,
    min_texels_per_px: f32,
    dilate_max_px: f32,
    dilate_full_size_px: f32,
    dilate_none_size_px: f32,
}

impl ShaderConsts {
    /// text.wgsl から読む（無ければ失敗させる＝名前を変えたらこのテストも直す）。
    fn load() -> Self {
        let c = wgsl_float_consts();
        let get = |name: &str| *c.get(name).unwrap_or_else(|| panic!("text.wgsl に {name} が無い"));
        let spread = get("TEXT_SDF_SPREAD_TEXELS");
        Self {
            edge: get("TEXT_SDF_EDGE"),
            em_px: get("TEXT_SDF_EM_PX"),
            // シェーダーでは式（TEXT_SDF_EDGE / TEXT_SDF_SPREAD_TEXELS）で書いている
            value_per_texel: get("TEXT_SDF_EDGE") / spread,
            aa_half_width_px: get("TEXT_AA_HALF_WIDTH_PX"),
            coverage_at_edge: get("TEXT_COVERAGE_AT_EDGE"),
            min_aa_width: get("TEXT_MIN_AA_WIDTH"),
            min_texels_per_px: get("TEXT_MIN_TEXELS_PER_PX"),
            dilate_max_px: get("TEXT_SMALL_DILATE_MAX_PX"),
            dilate_full_size_px: get("TEXT_SMALL_DILATE_FULL_SIZE_PX"),
            dilate_none_size_px: get("TEXT_SMALL_DILATE_NONE_SIZE_PX"),
        }
    }
}

// ── シェーダーの式の写し（text.wgsl の fs_main と同じ順） ──────────

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

/// 本体の alpha（縁取り・色は見ない）。`texels_per_px` は画面の 1 画素がアトラスの何テクセルか（UV の微分）。
fn fill_alpha(c: &ShaderConsts, d: f32, texels_per_px: f32, weight_dist: f32, softness: f32) -> f32 {
    let texels = texels_per_px.max(c.min_texels_per_px);
    let value_per_px = texels * c.value_per_texel;
    let w = (value_per_px * c.aa_half_width_px).max(c.min_aa_width) + softness;
    let dilation = small_text_dilation_px(c, c.em_px / texels) * value_per_px;
    let edge = c.edge - weight_dist - dilation;
    edge_coverage(c, d, edge, w, softness)
}

// ── グリフを画面の画素で読む ──────────────────────────────────

/// グリフの SDF をテクセル座標（テクセルの中心が i + 0.5）でバイリニアに読む。範囲の外は 0（アトラスの隙間）。
fn sample_bilinear(g: &GlyphSdf, tx: f32, ty: f32) -> f32 {
    let at = |x: i64, y: i64| -> f32 {
        if x < 0 || y < 0 || x >= i64::from(g.width) || y >= i64::from(g.height) {
            return 0.0;
        }
        f32::from(g.data[(y as usize) * g.width as usize + x as usize]) / SDF_BYTE_MAX
    };
    let fx = tx - TEXEL_CENTER;
    let fy = ty - TEXEL_CENTER;
    let (x0, y0) = (fx.floor(), fy.floor());
    let (ax, ay) = (fx - x0, fy - y0);
    let (x0, y0) = (x0 as i64, y0 as i64);
    at(x0, y0) * (1.0 - ax) * (1.0 - ay)
        + at(x0 + 1, y0) * ax * (1.0 - ay)
        + at(x0, y0 + 1) * (1.0 - ax) * ay
        + at(x0 + 1, y0 + 1) * ax * ay
}

/// 等倍でグリフを描いたときの本体の alpha の格子（行 × 列）と、横画の真ん中の列の範囲。
///
/// `size_px` の文字を、クアッドの左上が (0, offset_y) 画素に来るよう置く（x は画素の格子に揃え、y だけ副画素でずらす）。
fn render_glyph(c: &ShaderConsts, g: &GlyphSdf, size_px: f32, offset_y: f32) -> (Vec<Vec<f32>>, std::ops::Range<usize>) {
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
            let d = sample_bilinear(g, tx, ty);
            *a = fill_alpha(c, d, texels_per_px, 0.0, 0.0);
        }
    }
    let mid = (cols as f32 * STROKE_MID_COLS.0) as usize..(cols as f32 * STROKE_MID_COLS.1) as usize;
    (grid, mid)
}

/// SDF の上の横画の太さ（画素）: 真ん中の列で値が縁（0.5）以上のテクセルの数 × 1 テクセルの画素。
fn stroke_thickness_px(g: &GlyphSdf, size_px: f32) -> f32 {
    let col = g.width as usize / 2;
    let inside = (0..g.height as usize).filter(|&y| f32::from(g.data[y * g.width as usize + col]) / SDF_BYTE_MAX >= SDF_EDGE_VALUE).count();
    inside as f32 * size_px / SDF_EM_PX
}

fn builtin_font() -> ab_glyph::FontArc {
    ab_glyph::FontArc::try_from_slice(super::DEFAULT_FONT_BYTES).expect("組み込みフォントは必ず読める")
}

// ── 試験 ──────────────────────────────────────────────────────

/// シェーダーの SDF の定数が font/sdf.rs と一致する（焼く側と読む側の食い違いの検出）。
#[test]
fn shader_sdf_constants_match_rust() {
    let c = wgsl_float_consts();
    assert_eq!(c["TEXT_SDF_EM_PX"], SDF_EM_PX, "text.wgsl の TEXT_SDF_EM_PX と sdf.rs の SDF_EM_PX");
    assert_eq!(c["TEXT_SDF_SPREAD_TEXELS"], SDF_SPREAD_PX as f32, "text.wgsl の TEXT_SDF_SPREAD_TEXELS と sdf.rs の SDF_SPREAD_PX");
    assert!(
        TEXT_WGSL.contains("const TEXT_SDF_VALUE_PER_TEXEL: f32 = TEXT_SDF_EDGE / TEXT_SDF_SPREAD_TEXELS;"),
        "1 テクセルあたりの値の変化の式"
    );
    assert!((ShaderConsts::load().value_per_texel - SDF_VALUE_PER_TEXEL).abs() < 1e-7);
    assert_eq!(c["TEXT_AA_HALF_WIDTH_PX"], 0.5, "平滑化は縁の前後 ±0.5 画素（1 画素の傾き）");
}

/// 【不具合の再発防止】等倍で「ー」「−」の横画を縦に 0.05 画素ずつずらしても、横画が消えない
/// （真ん中の列のいちばん濃い画素の alpha・塗られた量が、どちらも横画の太さに対する下限の割合以上）。
#[test]
fn thin_horizontal_strokes_never_vanish_at_1x() {
    let c = ShaderConsts::load();
    let font = builtin_font();
    for ch in ['ー', '−'] {
        let g = rasterize_glyph_sdf(&font, ch).expect("アウトラインを持つ字");
        for size in SMALL_SIZES_PX {
            let thickness = stroke_thickness_px(&g, size);
            for step in 0..SUBPIXEL_STEPS {
                let offset = step as f32 * SUBPIXEL_STEP_PX;
                let (grid, mid) = render_glyph(&c, &g, size, offset);
                let n = mid.len() as f32;
                let peak: f32 = mid.clone().map(|x| grid.iter().map(|r| r[x]).fold(0.0, f32::max)).sum::<f32>() / n;
                let ink: f32 = mid.clone().map(|x| grid.iter().map(|r| r[x]).sum::<f32>()).sum::<f32>() / n;
                assert!(
                    peak >= thickness * MIN_STROKE_PEAK_PER_THICKNESS,
                    "'{ch}' 大きさ {size} 縦のずれ {offset:.2}: 横画のいちばん濃い alpha {peak:.3} < 太さ {thickness:.3} × {MIN_STROKE_PEAK_PER_THICKNESS}"
                );
                assert!(
                    ink >= thickness * MIN_STROKE_INK_RATIO,
                    "'{ch}' 大きさ {size} 縦のずれ {offset:.2}: 塗られた量 {ink:.3} 画素 < 太さ {thickness:.3} × {MIN_STROKE_INK_RATIO}"
                );
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
        // 縁（テクセル座標 EDGE_X）の前後を細かく読み、alpha が縁の途中（RAMP_ALPHA_RANGE の中）にある位置の幅を測る
        let mut first_partial = None;
        let mut last_partial = None;
        let scan_steps = (RAMP_SCAN_SPAN_PX * 2.0) as usize * RAMP_SCAN_STEPS_PER_PX;
        for k in 0..scan_steps {
            let offset_px = k as f32 / RAMP_SCAN_STEPS_PER_PX as f32 - RAMP_SCAN_SPAN_PX;
            let tx = EDGE_X as f32 + offset_px * texels_per_px;
            let a = fill_alpha(&c, value(tx), texels_per_px, 0.0, 0.0);
            if a > RAMP_ALPHA_RANGE.0 && a < RAMP_ALPHA_RANGE.1 {
                first_partial.get_or_insert(offset_px);
                last_partial = Some(offset_px);
            }
        }
        let width = last_partial.unwrap() - first_partial.unwrap();
        // 直線の 1 画素の傾きなら 0.96 画素。量子化の分だけ許す
        assert!(RAMP_WIDTH_OK_PX.contains(&width), "1 画素 = {texels_per_px} テクセル: 縁の傾きの幅 {width:.3} 画素");
    }
}

/// ぼかし（影）の縁は従来どおり smoothstep の S 字（直線の傾きにしない）で、幅はぼかしのぶん広がる。
#[test]
fn soft_shadow_edges_keep_smoothstep() {
    let c = ShaderConsts::load();
    let texels_per_px = 1.0;
    let softness = SHADOW_TEST_SOFTNESS;
    // 縁の上は縁の被覆率（0.5）、ぼかしの幅の外では 0 と 1（幅はシェーダーと同じ式: 下限つきの平滑化の半分の幅 + ぼかし）
    let w = (texels_per_px * c.value_per_texel * c.aa_half_width_px).max(c.min_aa_width) + softness;
    let edge = c.edge - small_text_dilation_px(&c, c.em_px / texels_per_px) * texels_per_px * c.value_per_texel;
    assert!((fill_alpha(&c, edge, texels_per_px, 0.0, softness) - c.coverage_at_edge).abs() < 1e-5);
    assert_eq!(fill_alpha(&c, edge - w, texels_per_px, 0.0, softness), 0.0);
    assert_eq!(fill_alpha(&c, edge + w, texels_per_px, 0.0, softness), 1.0);
    // S 字: 縁と端の間（1/4 の所）は直線（0.25）より小さい
    let quarter = fill_alpha(&c, edge - w * 0.5, texels_per_px, 0.0, softness);
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
