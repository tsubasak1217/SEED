// ============================================================
//  font/msdf/bake.rs — 1 グリフの MTSDF を焼く（輪郭 → 色分け → 距離 → 符号の直し → 誤差の補正 → 量子化 → 検査）
//
//  【流れ】
//    1. ab_glyph の輪郭を、SEED の文字の大きさ MTSDF_EM_PX テクセルの形（y 上向き）へ組み直す（outline.rs）
//    2. 外接矩形（曲線の極値まで）を整数のテクセルへ広げ、四方に MTSDF_PAD_PX の余白を足して距離場の大きさを決める
//    3. 辺の色分け（edge_color.rs。既定 ink trap）
//    4. テクセルごとの距離（distance.rs。行ごとに並列）と、走査線の内外での符号の直し
//    5. 正規化（0.5 + 距離 ÷ (2 × 片側の幅)）→ 補間の誤りの補正（error_correction.rs）→ RGBA8 へ四捨五入
//    6. 字形ごとの検査（verify.rs。安全弁 `apply_safety_valve`）。落ちたら RGB にアルファ（真の SDF）を写す（その字は 1 チャネルの
//       SDF と同じ描かれ方）。アルファも落ちたら、ラスタからの真の SDF（raster_fallback.rs）で作り直す（最後の落ち先。形は必ず
//       ラスタと一致する。2026-10-03。レビュー #4）
//    7. 線の幅ごと消えた細部が多い字（verify.rs の `needs_finer_field`。1 テクセルより細い線）は、em を刻みで上限
//       `MTSDF_MAX_EM_PX` まで上げて焼き直し、消えた細部のいちばん少ない em を使う（`bake_glyph_mtsdf`。2026-10-03。レビュー #5）
//  各段の時間を `BakeStats` に残す（ログ・計測用）。
// ============================================================

use std::time::{Duration, Instant};

use ab_glyph::{Font, FontArc, PxScale, ScaleFont};

use super::distance::{FieldGrid, PreparedShape};
use super::edge_color::{color_edges, ColoringStrategy};
use super::error_correction::{correct_errors, distance_to_value, CorrectionStats, Texel};
use super::geometry::v2;
use super::outline::Shape;
use super::params::{em_for_outline_length, thin_stroke_em_candidates, FieldScale, MTSDF_BYTES_PER_TEXEL};
use super::raster_fallback::raster_true_sdf_rgba;
use super::verify::{verify_glyph, FieldPlacement, VerifyChannel, VerifyReport};
use ab_glyph::GlyphId;
use crate::engine::core::font::glyph_field::{DistanceFieldKind, GlyphField};

/// u8 の値の最大。
const BYTE_MAX: f32 = 255.0;
/// アルファのチャネルの位置（RGBA の 4 番目）。
const ALPHA_CHANNEL: usize = 3;

/// 焼いた 1 グリフの計測と結果（ログ・試験用）。
#[derive(Clone, Copy, Debug, Default)]
pub struct BakeStats {
    /// 距離場の幅・高さ（テクセル）。
    pub width: usize,
    pub height: usize,
    /// 輪郭・辺の数（色分けの後）。
    pub contours: usize,
    pub edges: usize,
    /// 各段の時間: 輪郭の組み直しと色分け・距離・誤差の補正・検査。
    pub time_outline: Duration,
    pub time_distance: Duration,
    pub time_correct: Duration,
    pub time_verify: Duration,
    /// 誤差の補正の結果。
    pub correction: CorrectionStats,
    /// MSDF（中央値）の検査の結果。
    pub verify: VerifyReport,
    /// 検査に落ちたときの、真の SDF（アルファ）の検査の結果（ログ用。通っていれば None）。
    pub alpha_verify: Option<VerifyReport>,
    /// 検査に落ちて真の SDF で描く字か。
    pub fallback: bool,
    /// アルファも検査に落ちて、ラスタからの真の SDF（最後の落ち先）で作り直した字か（2026-10-03）。
    pub raster_fallback: bool,
    /// ラスタからの真の SDF の検査の結果（ログ用。作り直したときだけ Some）。
    pub raster_verify: Option<VerifyReport>,
    /// 焼いた em（テクセル。最後に使った大きさ）。
    pub em_px: f32,
    /// 細い線が消えるので em を上げて焼き直した字なら、上げる前の em（2026-10-03。レビュー #5）。
    pub thin_raised_from_em: Option<f32>,
    /// em を上げたことで増えた距離場の面積（テクセル²。アトラスの容量への影響の計測用。上げていなければ 0）。
    pub thin_raised_extra_texels: usize,
}

impl BakeStats {
    /// 全体の時間。
    pub fn total(&self) -> Duration {
        self.time_outline + self.time_distance + self.time_correct + self.time_verify
    }
}

/// 距離場の置き場（テクセルの単位）。
#[derive(Clone, Copy, Debug)]
pub struct FieldLayout {
    /// 距離場の左端の x・上端の y（y 上向きの形の空間。テクセル）。
    pub left: i64,
    pub top: i64,
    /// 幅・高さ（テクセル）。
    pub width: usize,
    pub height: usize,
    /// テクセルの中心の格子。
    pub grid: FieldGrid,
}

impl FieldLayout {
    /// 形の外接矩形を整数のテクセルへ広げ、四方に余白 `pad` を足した置き場。
    pub fn around(min: super::geometry::Vec2, max: super::geometry::Vec2, pad: i64) -> Self {
        let left = min.x.floor() as i64 - pad;
        let right = max.x.ceil() as i64 + pad;
        let bottom = min.y.floor() as i64 - pad;
        let top = max.y.ceil() as i64 + pad;
        let width = (right - left) as usize;
        let height = (top - bottom) as usize;
        // テクセル (0, 0)（左上）の中心 = (left + 0.5, top − 0.5)（y 上向き）
        let grid = FieldGrid { width, height, origin: v2(left as f64 + 0.5, top as f64 - 0.5) };
        Self { left, top, width, height, grid }
    }

    /// 検査（verify.rs）に渡す置き場（y 下向き）。
    pub fn placement(&self) -> FieldPlacement {
        FieldPlacement { left: self.left as i32, top: -(self.top as i32), width: self.width, height: self.height }
    }
}

/// テクセルの単位の形から RGBA8 の MTSDF を作る（色分け → 距離 → 符号の直し → 補正 → 量子化）。形が空なら None。
///
/// `scale` は焼く大きさの組（形はすでに `scale.em_px` の大きさのテクセルの単位であること）。
pub fn render_shape(shape: &mut Shape, coloring: ColoringStrategy, scale: &FieldScale, stats: &mut BakeStats) -> Option<(Vec<u8>, FieldLayout)> {
    let t0 = Instant::now();
    let (min, max) = shape.bounds()?;
    let layout = FieldLayout::around(min, max, i64::from(scale.pad_px));
    color_edges(shape, coloring);
    stats.contours = shape.contours.len();
    stats.edges = shape.edge_count();
    let prepared = PreparedShape::new(shape, scale.cutoff_px(), scale.far_px());
    stats.time_outline += t0.elapsed();

    // 距離（符号の直し込み。行ごとに並列）
    let t1 = Instant::now();
    let field = prepared.generate(&layout.grid);
    stats.time_distance = t1.elapsed();

    // 正規化 → 誤差の補正 → 量子化
    let t2 = Instant::now();
    let mut values: Vec<Texel> = field
        .texels
        .iter()
        .map(|d| {
            let s = scale.spread_px;
            [distance_to_value(d[0], s), distance_to_value(d[1], s), distance_to_value(d[2], s), distance_to_value(d[3], s)]
        })
        .collect();
    stats.correction = correct_errors(&mut values, &layout.grid, shape, &prepared, scale);
    let mut data = vec![0u8; layout.width * layout.height * MTSDF_BYTES_PER_TEXEL];
    for (texel, out) in values.iter().zip(data.chunks_exact_mut(MTSDF_BYTES_PER_TEXEL)) {
        for (v, o) in texel.iter().zip(out.iter_mut()) {
            *o = quantize(*v);
        }
    }
    stats.time_correct = t2.elapsed();
    stats.width = layout.width;
    stats.height = layout.height;
    Some((data, layout))
}

/// グリフの MTSDF を焼く（em は輪郭の長さで決める: ふつうの字は既定の 40、画数の多い字は大きく。`em_for_outline_length`）。
/// 輪郭の無い字（スペースなど）は None。
///
/// 焼いた字の検査で線の幅ごと消えた細部が多ければ（`VerifyReport::needs_finer_field`。1 テクセルより細い線）、
/// em を刻みで上限まで上げて焼き直し（`thin_stroke_em_candidates`）、消えた細部（em² あたり）がいちばん少ない em を使う
/// （同じなら小さい em。消えなくなった em で打ち切る）。上げた字は `BakeStats::thin_raised_from_em` に上げる前の em が残る。
pub fn bake_glyph_mtsdf(font: &FontArc, codepoint: char, coloring: ColoringStrategy) -> Option<(GlyphField, BakeStats)> {
    let outline = font.outline(font.glyph_id(codepoint))?;
    // 輪郭の長さ（em 単位）。em 1 テクセルの形で測る
    let unit = f64::from(font.as_scaled(PxScale::from(1.0)).h_scale_factor());
    let length_em = outline_length(&Shape::from_outline_curves(&outline.curves, unit)) as f32;
    let scale = FieldScale::with_em(em_for_outline_length(length_em));
    let (first, first_stats) = bake_glyph_mtsdf_at(font, codepoint, coloring, &scale)?;
    if !first_stats.verify.needs_finer_field() {
        return Some((first, first_stats));
    }
    // ── 細い線が消える字: em を上げて焼き直し、消えた細部のいちばん少ない em を選ぶ ──
    let first_area = first.width as usize * first.height as usize;
    let mut total_time = first_stats.total();
    let mut best_score = first_stats.verify.vanished_area_em2(scale.em_px);
    let mut best = (first, first_stats);
    for em in thin_stroke_em_candidates(scale.em_px) {
        let (glyph, stats) = bake_glyph_mtsdf_at(font, codepoint, coloring, &FieldScale::with_em(em))?;
        total_time += stats.total();
        let score = stats.verify.vanished_area_em2(em);
        let good_enough = !stats.verify.needs_finer_field();
        if score < best_score {
            best_score = score;
            best = (glyph, stats);
        }
        if good_enough {
            break;
        }
    }
    let (glyph, mut stats) = best;
    if glyph.em_px != scale.em_px {
        stats.thin_raised_from_em = Some(scale.em_px);
        stats.thin_raised_extra_texels = (glyph.width as usize * glyph.height as usize).saturating_sub(first_area);
    }
    // 時間は試した全部の合計を検査の段に足す（初めて出る画面の詰まりの計測に効く）
    stats.time_verify += total_time.saturating_sub(stats.total());
    Some((glyph, stats))
}

/// グリフの MTSDF を大きさ `scale` で焼く。
pub fn bake_glyph_mtsdf_at(font: &FontArc, codepoint: char, coloring: ColoringStrategy, scale: &FieldScale) -> Option<(GlyphField, BakeStats)> {
    let mut stats = BakeStats { em_px: scale.em_px, ..Default::default() };
    let t0 = Instant::now();

    // ── 輪郭を組み直す（フォントの単位 → テクセル。ab_glyph の PxScale と同じ倍率）──
    let glyph_id = font.glyph_id(codepoint);
    let outline = font.outline(glyph_id)?;
    let units_to_texels = f64::from(font.as_scaled(PxScale::from(scale.em_px)).h_scale_factor());
    let mut shape = Shape::from_outline_curves(&outline.curves, units_to_texels);
    stats.time_outline = t0.elapsed();

    // ── 距離場を作る ──
    let (mut data, layout) = render_shape(&mut shape, coloring, scale, &mut stats)?;

    // ── 検査（安全弁）。落ちたら RGB へアルファを写して真の SDF で描く（アルファも落ちたらラスタからの真の SDF）──
    let t3 = Instant::now();
    apply_safety_valve(font, glyph_id, scale, layout.placement(), &mut data, &mut stats);
    stats.time_verify = t3.elapsed();

    // ── メトリクス（em 単位。送り幅の定義は text_layout に一本化＝描画とピックで同じ値）──
    let inv_em = 1.0 / scale.em_px;
    let glyph = GlyphField {
        data,
        width: layout.width as u32,
        height: layout.height as u32,
        kind: DistanceFieldKind::Mtsdf,
        bearing_em: [layout.left as f32 * inv_em, -(layout.top as f32) * inv_em],
        size_em: [layout.width as f32 * inv_em, layout.height as f32 * inv_em],
        advance_em: crate::engine::core::font::text_layout::advance_em(font, codepoint),
        pad_em: scale.pad_px as f32 * inv_em,
        em_px: scale.em_px,
        msdf_fallback: stats.fallback,
    };
    Some((glyph, stats))
}

/// 字形ごとの検査（安全弁）。検査に通るまで、次の順に落とす（結果は `stats` の verify・alpha_verify・raster_verify と旗）:
///   1. MSDF（中央値）のまま
///   2. RGB へアルファ（輪郭からの真の SDF）を写す（その字は 1 チャネルの SDF と同じ描かれ方）
///   3. ラスタからの真の SDF で作り直す（輪郭の合成を使わない最後の落ち先。形はラスタと必ず一致する。raster_fallback.rs）
/// 3 は 2 が落ちたら必ず使う（ラスタからの作り直しの検査の結果はログに残すだけ）。
pub fn apply_safety_valve(font: &FontArc, glyph_id: GlyphId, scale: &FieldScale, place: FieldPlacement, data: &mut Vec<u8>, stats: &mut BakeStats) {
    // ── 1. 中央値 ──
    stats.verify = verify_glyph(font, glyph_id, scale.em_px, data, place, VerifyChannel::Median);
    if stats.verify.passed() {
        return;
    }
    // ── 2. アルファを RGB へ写す ──
    stats.fallback = true;
    copy_alpha_to_rgb(data);
    let alpha = verify_glyph(font, glyph_id, scale.em_px, data, place, VerifyChannel::Alpha);
    stats.alpha_verify = Some(alpha);
    if alpha.passed() {
        return;
    }
    // ── 3. ラスタからの真の SDF（輪郭の無い字はここへ来ない＝作れなければアルファのまま）──
    if let Some(raster) = raster_true_sdf_rgba(font, glyph_id, scale, place) {
        stats.raster_verify = Some(verify_glyph(font, glyph_id, scale.em_px, &raster, place, VerifyChannel::Median));
        stats.raster_fallback = true;
        *data = raster;
    }
}

/// 形の輪郭の全長（形の単位。曲線は `EDGE_LENGTH_STEPS` 本の折れ線で測る）。字の細かさの目安（解像度を上げる字の判定）。
pub fn outline_length(shape: &Shape) -> f64 {
    /// 曲線 1 本を何本の折れ線で測るか。
    const EDGE_LENGTH_STEPS: usize = 8;
    let mut total = 0.0;
    for e in shape.contours.iter().flat_map(|c| c.edges.iter()) {
        let mut prev = e.point(0.0);
        for k in 1..=EDGE_LENGTH_STEPS {
            let cur = e.point(k as f64 / EDGE_LENGTH_STEPS as f64);
            total += (cur - prev).length();
            prev = cur;
        }
    }
    total
}

/// 正規化した値（0..1 の外もありうる）→ u8（四捨五入。0..1 に収める）。
#[inline]
fn quantize(v: f32) -> u8 {
    (v.clamp(0.0, 1.0) * BYTE_MAX).round() as u8
}

/// RGB の 3 チャネルへアルファ（真の SDF）を写す（中央値 = アルファ になる＝その字は真の SDF で描かれる）。
fn copy_alpha_to_rgb(data: &mut [u8]) {
    for texel in data.chunks_exact_mut(MTSDF_BYTES_PER_TEXEL) {
        let a = texel[ALPHA_CHANNEL];
        texel[0] = a;
        texel[1] = a;
        texel[2] = a;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::font::msdf::verify::sample_field;

    fn builtin() -> FontArc {
        FontArc::try_from_slice(crate::engine::core::font::DEFAULT_FONT_BYTES).expect("組み込みフォント")
    }

    /// 画数の多い字は大きな em で焼き、ふつうの字は既定の em（輪郭の長さで決める）。
    #[test]
    fn dense_glyphs_get_larger_em() {
        let font = builtin();
        let (a, _) = bake_glyph_mtsdf(&font, 'A', ColoringStrategy::InkTrap).unwrap();
        let (tokei, _) = bake_glyph_mtsdf(&font, '時', ColoringStrategy::InkTrap).unwrap();
        let (utsu, _) = bake_glyph_mtsdf(&font, '鬱', ColoringStrategy::InkTrap).unwrap();
        assert_eq!(a.em_px, FieldScale::BASE.em_px);
        assert_eq!(tokei.em_px, FieldScale::BASE.em_px, "ふつうの漢字は既定の em");
        assert!(utsu.em_px > 50.0, "画数の多い漢字は大きな em: {}", utsu.em_px);
        // em が変わっても em 単位のメトリクス（送り幅・余白）は同じ意味
        assert!((utsu.pad_em - 0.125).abs() < 0.02);
        assert_eq!(utsu.advance_em, crate::engine::core::font::text_layout::advance_em(&font, '鬱'));
    }

    /// 輪郭の無い字（スペース）は None、ある字は余白込みの大きさ・RGBA8 のデータ・em 単位のメトリクスを返す。
    #[test]
    fn bakes_glyph_with_metrics() {
        let font = builtin();
        assert!(bake_glyph_mtsdf(&font, ' ', ColoringStrategy::InkTrap).is_none());
        let (g, stats) = bake_glyph_mtsdf(&font, 'A', ColoringStrategy::InkTrap).expect("'A' は輪郭がある");
        assert_eq!(g.kind, DistanceFieldKind::Mtsdf);
        assert_eq!(g.data.len(), (g.width * g.height) as usize * MTSDF_BYTES_PER_TEXEL);
        let base = FieldScale::BASE;
        assert!((g.pad_em - base.pad_px as f32 / base.em_px).abs() < 1e-7);
        assert!((g.size_em[0] - g.width as f32 / base.em_px).abs() < 1e-7);
        // 送り幅は text_layout と同じ値（描画とピックの一致）
        let expect = crate::engine::core::font::text_layout::advance_em(&font, 'A');
        assert_eq!(g.advance_em, expect);
        assert!(stats.verify.passed(), "'A' は検査に通る: {:?}", stats.verify);
        assert!(!g.msdf_fallback);
    }

    /// 【安全弁】MSDF（中央値）に偽の点（余白の 3×3 テクセルを内側に）を入れると検査に落ち、真の SDF（アルファ）は通る。
    /// RGB へアルファを写すと（bake の落ちたときの扱い）中央値の検査も通る。
    #[test]
    fn safety_valve_detects_broken_median_and_alpha_fallback_passes() {
        let font = builtin();
        let (g, stats) = bake_glyph_mtsdf(&font, 'A', ColoringStrategy::InkTrap).expect("輪郭がある");
        assert!(stats.verify.passed(), "壊す前は通る: {:?}", stats.verify);
        let (w, h) = (g.width as usize, g.height as usize);
        let em = g.em_px;
        // 置き場（y 下向きのテクセル。bearing_em = [左, 上] ÷ em）
        let place = FieldPlacement { left: (g.bearing_em[0] * em).round() as i32, top: (g.bearing_em[1] * em).round() as i32, width: w, height: h };
        let id = font.glyph_id('A');
        let mut data = g.data.clone();
        // 余白の左上（字から 2 テクセル以上離れた所）の 3×3 テクセルの RGB だけを「内側」にする
        for y in 1..4 {
            for x in 1..4 {
                let k = (y * w + x) * MTSDF_BYTES_PER_TEXEL;
                data[k] = 255;
                data[k + 1] = 255;
                data[k + 2] = 255;
            }
        }
        let broken = verify_glyph(&font, id, em, &data, place, VerifyChannel::Median);
        assert!(!broken.passed(), "偽の点を見つける: {broken:?}");
        let alpha = verify_glyph(&font, id, em, &data, place, VerifyChannel::Alpha);
        assert!(alpha.passed(), "真の SDF は壊していないので通る: {alpha:?}");
        copy_alpha_to_rgb(&mut data);
        let fixed = verify_glyph(&font, id, em, &data, place, VerifyChannel::Median);
        assert!(fixed.passed(), "RGB へアルファを写すと中央値も通る: {fixed:?}");
    }

    /// 【最後の落ち先（レビュー #4）】中央値もアルファも壊れた字（余白の 3×3 テクセルを RGBA の全部で内側にする）は、
    /// ラスタからの真の SDF で作り直され、検査に通る（中央値・アルファの両方で）。壊れていない字は作り直さない。
    #[test]
    fn safety_valve_falls_back_to_raster_when_alpha_is_broken_too() {
        let font = builtin();
        let (g, stats) = bake_glyph_mtsdf(&font, 'A', ColoringStrategy::InkTrap).expect("輪郭がある");
        assert!(!stats.raster_fallback && stats.raster_verify.is_none(), "ふつうの字は作り直さない");
        let (w, h) = (g.width as usize, g.height as usize);
        let em = g.em_px;
        let place = FieldPlacement { left: (g.bearing_em[0] * em).round() as i32, top: (g.bearing_em[1] * em).round() as i32, width: w, height: h };
        let id = font.glyph_id('A');
        let scale = FieldScale::with_em(em);
        // アルファまで壊す（合成の選び間違いでアルファも同じく崩れた場合の写し）
        let mut data = g.data.clone();
        for y in 1..4 {
            for x in 1..4 {
                let k = (y * w + x) * MTSDF_BYTES_PER_TEXEL;
                data[k..k + MTSDF_BYTES_PER_TEXEL].fill(255);
            }
        }
        let mut st = BakeStats::default();
        apply_safety_valve(&font, id, &scale, place, &mut data, &mut st);
        assert!(st.fallback && !st.verify.passed(), "中央値は落ちる: {:?}", st.verify);
        assert!(!st.alpha_verify.expect("アルファも確かめる").passed(), "アルファも落ちる");
        assert!(st.raster_fallback, "ラスタからの真の SDF で作り直す");
        assert!(st.raster_verify.expect("作り直しの検査").passed(), "作り直した字は通る: {:?}", st.raster_verify);
        for channel in [VerifyChannel::Median, VerifyChannel::Alpha] {
            let r = verify_glyph(&font, id, em, &data, place, channel);
            assert!(r.passed(), "作り直した後の {channel:?}: {r:?}");
        }
        // アルファだけ壊れていない字は従来どおりアルファで止まる（作り直さない）
        let mut data2 = g.data.clone();
        for y in 1..4 {
            for x in 1..4 {
                let k = (y * w + x) * MTSDF_BYTES_PER_TEXEL;
                data2[k..k + 3].fill(255);
            }
        }
        let mut st2 = BakeStats::default();
        apply_safety_valve(&font, id, &scale, place, &mut data2, &mut st2);
        assert!(st2.fallback && st2.alpha_verify.expect("アルファ").passed() && !st2.raster_fallback);
    }

    /// 【細い線（レビュー #5）】組み込みの書体の Thin（M PLUS Rounded 1c Thin。横画の太さ 20/1000 em ≒ 0.02 em。em 40 で 0.57 テクセル）の
    /// 横画の字は、em 40 だと線が消える（検査の vanished_px が多い）ので em 64 へ上げて焼き直され、大きな表示での線の濃さが戻る。
    /// Regular（0.06 em 以上）の同じ字は上げない。
    #[test]
    fn thin_strokes_raise_em_and_keep_ink() {
        let thin = FontArc::try_from_slice(include_bytes!("../../../engine_resources/fonts/M_PLUS_Rounded_1c/MPLUSRounded1c-Thin.ttf"))
            .expect("Thin の書体");
        let regular = builtin();
        // 描いた形（中央値 ≥ 0.5）の内側の画素の数（距離場の 4 倍の細かさで数える。線が消えると減る）
        let drawn_px = |g: &GlyphField| -> f32 {
            let k = crate::engine::core::font::msdf::params::VERIFY_OVERSAMPLE as usize;
            let (hw, hh) = (g.width as usize * k, g.height as usize * k);
            let mut drawn = 0usize;
            for hy in 0..hh {
                for hx in 0..hw {
                    let (gx, gy) = ((hx as f32 + 0.5) / k as f32, (hy as f32 + 0.5) / k as f32);
                    if sample_field(&g.data, g.width as usize, g.height as usize, gx, gy, VerifyChannel::Median) >= 0.5 {
                        drawn += 1;
                    }
                }
            }
            drawn as f32
        };
        let mut raised_any = false;
        for ch in ['一', 'ー', '二', '三'] {
            let base = FieldScale::with_em(crate::engine::core::font::msdf::params::MTSDF_EM_PX);
            let (at40, st40) = bake_glyph_mtsdf_at(&thin, ch, ColoringStrategy::InkTrap, &base).expect("輪郭がある");
            let (g, st) = bake_glyph_mtsdf(&thin, ch, ColoringStrategy::InkTrap).expect("輪郭がある");
            // 4 倍の画素の数 ÷ (em × 4)² = em² の単位の面積
            let px_per_em = |em: f32| (em * crate::engine::core::font::msdf::params::VERIFY_OVERSAMPLE as f32).powi(2);
            let area40 = drawn_px(&at40) / px_per_em(base.em_px);
            let area = drawn_px(&g) / px_per_em(g.em_px);
            eprintln!(
                "{ch} em40: 消えた {} 画素（{:.5} em²）描いた {area40:.5} em² → em {}: 消えた {} 画素（{:.5} em²）描いた {area:.5} em²",
                st40.verify.vanished_px,
                st40.verify.vanished_area_em2(base.em_px),
                g.em_px,
                st.verify.vanished_px,
                st.verify.vanished_area_em2(g.em_px)
            );
            if st40.verify.needs_finer_field() {
                raised_any = true;
                assert!(g.em_px > base.em_px, "{ch} は em を上げる: {:?}", st40.verify);
                assert_eq!(st.thin_raised_from_em, Some(base.em_px));
                assert!(st.thin_raised_extra_texels > 0);
                // 消えた細部（em² あたり）が減り、描いた線の面積が em 40 より増える＝消えていた線が戻る
                assert!(st.verify.vanished_area_em2(g.em_px) < st40.verify.vanished_area_em2(base.em_px), "{ch} 消えた細部が減らない");
                assert!(area > area40, "{ch} 描いた線の面積 em40 {area40} → em {} {area}", g.em_px);
            } else {
                assert_eq!(st.thin_raised_from_em, None);
            }
            // Regular は線が太いので上げない
            let (gr, sr) = bake_glyph_mtsdf(&regular, ch, ColoringStrategy::InkTrap).expect("輪郭がある");
            assert_eq!(sr.thin_raised_from_em, None, "Regular の '{ch}' は上げない: {:?}", sr.verify);
            assert_eq!(gr.em_px, base.em_px);
        }
        assert!(raised_any, "Thin の横画の字のどれかは em 40 で線が消えて上げられるはず");
    }

    /// 余白の外周（クアッドの端）は外側（値 < 0.5）、字の中央付近は内側（「口」の枠の線の上）。
    #[test]
    fn field_border_is_outside_and_strokes_inside() {
        let font = builtin();
        let (g, _) = bake_glyph_mtsdf(&font, '口', ColoringStrategy::InkTrap).expect("輪郭がある");
        let (w, h) = (g.width as usize, g.height as usize);
        for x in 0..w {
            for y in [0, h - 1] {
                let v = sample_field(&g.data, w, h, x as f32 + 0.5, y as f32 + 0.5, VerifyChannel::Median);
                assert!(v < 0.5, "外周 ({x},{y}) が内側: {v}");
            }
        }
        // 「口」の真ん中は穴（外側）
        let center = sample_field(&g.data, w, h, w as f32 * 0.5, h as f32 * 0.5, VerifyChannel::Median);
        assert!(center < 0.5, "「口」の穴が内側になった: {center}");
    }

    /// 【大きな文字の角】MTSDF の中央値で描いた正方形の角は尖る: 角の頂点へ向かう対角線の上で、中央値の 0.5 の等値線は
    /// 真の SDF（アルファ）の等値線より頂点に近い（アルファはバイリニアの補間で角が丸まる）。組み込みの書体は角の丸い書体なので、
    /// 角の尖った四角を直接作って確かめる。
    #[test]
    fn median_keeps_corners_sharper_than_alpha() {
        use crate::engine::core::font::msdf::outline::Contour;
        use crate::engine::core::font::msdf::segment::EdgeSegment;
        // 20 × 20 テクセルの正方形（y 上向きで時計回り）。頂点は格子の中途半端な位置に置く
        let (x0, y0, s) = (0.3, 0.7, 20.0);
        let mut shape = Shape {
            contours: vec![Contour {
                edges: vec![
                    EdgeSegment::line(v2(x0, y0), v2(x0, y0 + s)),
                    EdgeSegment::line(v2(x0, y0 + s), v2(x0 + s, y0 + s)),
                    EdgeSegment::line(v2(x0 + s, y0 + s), v2(x0 + s, y0)),
                    EdgeSegment::line(v2(x0 + s, y0), v2(x0, y0)),
                ],
            }],
        };
        let mut stats = BakeStats::default();
        let (data, layout) = render_shape(&mut shape, ColoringStrategy::InkTrap, &FieldScale::BASE, &mut stats).expect("形がある");
        let (w, h) = (layout.width, layout.height);
        // 左上の頂点（格子の座標）
        let corner = ((x0 - layout.left as f64) as f32, (layout.top as f64 - (y0 + s)) as f32);
        let inner = (corner.0 + 5.0, corner.1 + 5.0);
        // 内側の点から頂点へ進み、0.5 を下回る直前までの距離（頂点まで届けば対角線の長さ）
        let reach = |channel: VerifyChannel| -> f32 {
            let steps = 2000;
            let mut last = 0.0;
            for k in 0..=steps {
                let t = k as f32 / steps as f32 * 1.2;
                let p = (inner.0 + (corner.0 - inner.0) * t, inner.1 + (corner.1 - inner.1) * t);
                if sample_field(&data, w, h, p.0, p.1, channel) < 0.5 {
                    return last;
                }
                last = t;
            }
            last
        };
        let median_reach = reach(VerifyChannel::Median);
        let alpha_reach = reach(VerifyChannel::Alpha);
        assert!((median_reach - 1.0).abs() < 0.02, "中央値の角は頂点まで届く: {median_reach}");
        assert!(median_reach > alpha_reach + 0.02, "中央値の角 {median_reach} がアルファの角 {alpha_reach} より尖っていない");
    }
}
