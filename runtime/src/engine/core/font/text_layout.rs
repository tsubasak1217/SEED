// ============================================================
//  font/text_layout.rs — テキストの寸法計算（GPU 非依存の純関数）
//
//  【役割】
//  `TextComponent` の文字列が「キャンバスローカル px でどこからどこまでを占めるか」を
//  求める。描画（`canvas_text.rs`）とピック・選択枠（`pick_2d.rs` /
//  `canvas_collect.rs`）が**同じ寸法**を使うための唯一の定義。
//
//  【なぜ独立モジュールか】
//  描画側の `CanvasTextRenderer` は wgpu を要求する（グリフアトラス）。
//  一方ピックはデバイス無しで走るため、送り幅（advance）だけを ab_glyph から
//  直接引く純関数として切り出す。`FontSystem::advance_em` もここへ委譲するので、
//  「描画で使う送り幅」と「ピックで使う送り幅」が食い違うことは構造的に起きない。
//
//  【座標系】
//  キャンバスローカル px（原点 = アクター位置、X 右・Y 下）。
//  `align` / `vertical_align` は canvas_text.rs の append_item と同一規則。
// ============================================================

use ab_glyph::{Font, FontArc, PxScale, ScaleFont};

use super::sdf::{SDF_EM_PX, SDF_SPREAD_EM};
use crate::engine::components::{MAX_TEXT_CHARS, TextAlign, TextVerticalAlign};

/// テキストブロックのローカル境界矩形（キャンバス px）。
///
/// `min` = 左上、`max` = 右下（Y は下向き）。
/// ピックのヒット矩形・選択アウトラインの両方がこの矩形を使う。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TextLocalBox {
    pub min: [f32; 2],
    pub max: [f32; 2],
}

/// テキストブロックのレイアウト原点（描画とピックが共有する唯一の定義）。
///
/// **重要な規約**: `first_baseline_y` は 1 行目の**ベースライン**の Y である
/// （行の上端ではない）。グリフの `bearing_em[1]` はベースラインからの
/// オフセット（上方向は負）なので、描画側はこの値にそのまま足せる。
/// 枠を測る側は「上端 = ベースライン − アセント」で換算する必要がある。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TextLayoutOrigin {
    /// 1 行目のベースライン Y（キャンバス px。Y は下向き）。
    pub first_baseline_y: f32,
    /// 行送り（px）= font_size * line_spacing。
    pub line_step: f32,
    /// ブロック全体の高さ（px）= 行送り × 行数。
    pub block_height: f32,
}

/// 1 文字の送り幅を em 単位（フォントサイズ 1.0 相当）で返す。
///
/// グリフの SDF 化（`rasterize_glyph_sdf`）が記録する `advance_em` と同じ値になるよう、
/// 同一の基準 em サイズ `SDF_EM_PX` でスケールしてから正規化する。
pub fn advance_em(font: &FontArc, ch: char) -> f32 {
    let scaled = font.as_scaled(PxScale::from(SDF_EM_PX));
    scaled.h_advance(font.glyph_id(ch)) / SDF_EM_PX
}

/// ベースラインから上方向への伸び（アセント）を em 単位・**正の値**で返す。
///
/// グリフ量子化と同じ基準 em サイズで引いて正規化するため、
/// `GlyphInfo::bearing_px` と同じスケール上で比較できる。
pub fn ascent_em(font: &FontArc) -> f32 {
    font.as_scaled(PxScale::from(SDF_EM_PX)).ascent() / SDF_EM_PX
}

/// ベースラインから下方向への伸び（ディセント）を em 単位・**正の値**で返す。
///
/// ab_glyph の `descent()` はベースラインより下を負で返すため符号を反転する
/// （呼び出し側で符号を間違えないよう、ここで «下向きの正の距離» に統一する）。
pub fn descent_em(font: &FontArc) -> f32 {
    -font.as_scaled(PxScale::from(SDF_EM_PX)).descent() / SDF_EM_PX
}

/// 縁取りがグリフ矩形の外へはみ出す量（px）を返す。
///
/// SDF は四方に `SDF_SPREAD_EM` ぶんしか焼かれていないため、
/// それ以上太い指定をしても実際には広がらない（`outline_px_to_sdf` のクランプと同じ上限）。
/// 枠にもこの「実際に塗られる量」だけを足す。
pub fn outline_pad_px(outline_width_px: f32, font_size: f32) -> f32 {
    if outline_width_px <= 0.0 || font_size <= 0.0 {
        return 0.0;
    }
    outline_width_px.min(SDF_SPREAD_EM * font_size)
}

/// 1 行の幅（px）を測る。
pub fn measure_line_width(font: &FontArc, line: &str, font_size: f32) -> f32 {
    line.chars().map(|ch| advance_em(font, ch) * font_size).sum()
}

/// テキストブロックのレイアウト原点を求める（**描画・計測の唯一の定義**）。
///
/// 描画（`canvas_text.rs::append_item`）と計測（`measure_text_box`）が
/// 別々にこの式を持つと必ず食い違うため、両者ともここを呼ぶこと。
///
/// - `line_count`: 行数（`split('\n')` の要素数。0 は 1 行として扱う）
/// - 戻り値の `first_baseline_y` は 1 行目の**ベースライン** Y。
pub fn layout_origin(
    font_size: f32,
    line_spacing: f32,
    line_count: usize,
    vertical_align: TextVerticalAlign,
) -> TextLayoutOrigin {
    let line_step = font_size * line_spacing;
    let block_height = line_step * line_count.max(1) as f32;
    // 垂直方向の基準（キャンバス Y は下向き）。
    let first_baseline_y = match vertical_align {
        TextVerticalAlign::Top => 0.0,
        TextVerticalAlign::Middle => -block_height * 0.5,
        TextVerticalAlign::Bottom => -block_height,
    };
    TextLayoutOrigin {
        first_baseline_y,
        line_step,
        block_height,
    }
}

/// 1 行のペン開始 X（**描画・計測の唯一の定義**）。
///
/// 行ごとに幅が違うため行単位で計算する。
pub fn line_base_x(align: TextAlign, line_width: f32) -> f32 {
    match align {
        TextAlign::Left => 0.0,
        TextAlign::Center => -line_width * 0.5,
        TextAlign::Right => -line_width,
    }
}

/// テキストブロックのローカル境界矩形を返す。
///
/// レイアウト規則は `canvas_text.rs::append_item` と**同じ関数**
/// （`layout_origin` / `line_base_x`）から導く。したがって
/// 「描いた位置」と「測った枠」は構造的にズレない。
///
/// # 縦方向の規約（ここが要）
/// `layout_origin` が返すのは行の上端ではなく**ベースライン**である。
/// したがって枠の上端は `1 行目のベースライン − アセント`、
/// 下端は `最終行のベースライン + ディセント` になる。
/// （以前はベースラインを行の上端とみなしていたため、枠が
///  アセントぶん = 約 0.77em だけ下へズレていた。）
///
/// # 縁取り
/// 縁取りはグリフのエッジから外側へ広がるため、実際に塗られる量
/// （`outline_pad_px`）を四方に足す。
///
/// 描画されない入力（空文字・サイズ 0）は `None` を返す
/// （＝ピックもアウトラインも出さない。見えないものは掴めない）。
pub fn measure_text_box(
    font: &FontArc,
    text: &str,
    font_size: f32,
    line_spacing: f32,
    align: TextAlign,
    vertical_align: TextVerticalAlign,
    outline_width: f32,
) -> Option<TextLocalBox> {
    if text.is_empty() || font_size <= 0.0 {
        return None;
    }
    // 描画側と同じ上限で切り詰める（表示されない文字を枠に含めない）。
    let truncated: String = if text.chars().count() > MAX_TEXT_CHARS {
        text.chars().take(MAX_TEXT_CHARS).collect()
    } else {
        text.to_string()
    };

    let widths: Vec<f32> = truncated
        .split('\n')
        .map(|line| measure_line_width(font, line, font_size))
        .collect();
    if widths.is_empty() {
        return None;
    }

    // 描画とまったく同じ原点計算を使う（定義は 1 箇所だけ）。
    let origin = layout_origin(font_size, line_spacing, widths.len(), vertical_align);

    // 水平方向は行ごとに基準位置が変わるため、全行の最小/最大を取る。
    let mut min_x = f32::MAX;
    let mut max_x = f32::MIN;
    for w in &widths {
        let base_x = line_base_x(align, *w);
        min_x = min_x.min(base_x);
        max_x = max_x.max(base_x + w);
    }

    // 縦は「ベースライン基準」で実際の字面の上下端へ換算する。
    let ascent = ascent_em(font) * font_size;
    let descent = descent_em(font) * font_size;
    let last_baseline_y =
        origin.first_baseline_y + origin.line_step * (widths.len() as f32 - 1.0);
    // 縁取りぶんの余白（四方）。
    let pad = outline_pad_px(outline_width, font_size);

    Some(TextLocalBox {
        min: [min_x - pad, origin.first_baseline_y - ascent - pad],
        max: [max_x + pad, last_baseline_y + descent + pad],
    })
}

// ============================================================
//  単体テスト（GPU 不要。組み込みフォントで寸法規則を検証する）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::font::rasterizer::rasterize_glyph_sdf;

    /// テスト用フォント（組み込みフォント）。
    fn builtin() -> FontArc {
        FontArc::try_from_slice(super::super::DEFAULT_FONT_BYTES).expect("組み込みフォントを読める")
    }

    /// 空文字・サイズ 0 は枠を持たない。
    #[test]
    fn empty_text_has_no_box() {
        let f = builtin();
        assert!(
            measure_text_box(&f, "", 24.0, 1.2, TextAlign::Left, TextVerticalAlign::Top, 0.0)
                .is_none()
        );
        assert!(
            measure_text_box(&f, "A", 0.0, 1.2, TextAlign::Left, TextVerticalAlign::Top, 0.0)
                .is_none()
        );
    }

    /// 左上揃え: 原点は 1 行目の**ベースライン**なので、枠の上端は
    /// アセントぶん上（負の Y）になる。左端は原点に一致する。
    #[test]
    fn left_top_box_starts_at_baseline_minus_ascent() {
        let f = builtin();
        let font_size = 24.0;
        let b = measure_text_box(
            &f,
            "Ab",
            font_size,
            1.2,
            TextAlign::Left,
            TextVerticalAlign::Top,
            0.0,
        )
        .expect("枠が得られる");
        assert!((b.min[0]).abs() < 1e-4, "左端は原点");
        let ascent = ascent_em(&f) * font_size;
        assert!(ascent > 0.0, "アセントは正");
        assert!((b.min[1] + ascent).abs() < 1e-4, "上端 = ベースライン − アセント");
        let descent = descent_em(&f) * font_size;
        assert!((b.max[1] - descent).abs() < 1e-4, "下端 = ベースライン + ディセント");
        assert!((b.max[0] - b.min[0]) > 0.0, "文字幅は正");
    }

    /// **回帰テストの本体**: 描画側が使うペン原点から求めた 1 文字目の
    /// クアッド上端が、計測した枠の上端の内側に収まること。
    ///
    /// 以前は `layout_origin` の Y を「行の上端」と誤解していたため、
    /// グリフが枠よりアセントぶん（約 0.77em）上へはみ出していた。
    #[test]
    fn first_glyph_quad_is_inside_measured_box() {
        let f = builtin();
        let font_size = 40.0;
        let line_spacing = 1.2;
        let g = rasterize_glyph_sdf(&f, 'A').expect("'A' はアウトラインを持つ");

        for valign in [
            TextVerticalAlign::Top,
            TextVerticalAlign::Middle,
            TextVerticalAlign::Bottom,
        ] {
            let origin = layout_origin(font_size, line_spacing, 1, valign);
            // 描画側（append_item）とまったく同じ式でクアッド上下端を求める。
            let quad_top = origin.first_baseline_y + g.bearing_em[1] * font_size;
            let quad_bottom = quad_top + g.size_em[1] * font_size;
            // 同じく X（1 文字目・左揃えなので base_x = 0）。
            let quad_left = line_base_x(TextAlign::Left, 0.0) + g.bearing_em[0] * font_size;

            let b = measure_text_box(
                &f,
                "A",
                font_size,
                line_spacing,
                TextAlign::Left,
                valign,
                0.0,
            )
            .expect("枠が得られる");

            // SDF スプレッドのパディング（0.125em）ぶんは枠外へ出てよいが、
            // 「アセントぶん丸ごとズレる」ような大きな逸脱は許さない。
            let slack = SDF_SPREAD_EM * font_size + 1e-3;
            assert!(
                quad_top >= b.min[1] - slack,
                "{valign:?}: グリフ上端 {quad_top} が枠上端 {} より上へ出すぎ",
                b.min[1]
            );
            assert!(
                quad_bottom <= b.max[1] + slack,
                "{valign:?}: グリフ下端 {quad_bottom} が枠下端 {} より下へ出すぎ",
                b.max[1]
            );
            assert!(
                quad_left >= b.min[0] - slack,
                "{valign:?}: グリフ左端 {quad_left} が枠左端 {} より左へ出すぎ",
                b.min[0]
            );
        }
    }

    /// Top 揃えでは 1 行目のベースラインが原点、Middle ではブロックが原点中心。
    #[test]
    fn layout_origin_matches_vertical_align() {
        let font_size = 30.0;
        let line_spacing = 1.5;
        let step = font_size * line_spacing;
        let top = layout_origin(font_size, line_spacing, 2, TextVerticalAlign::Top);
        assert!((top.first_baseline_y).abs() < 1e-6);
        assert!((top.line_step - step).abs() < 1e-6);
        assert!((top.block_height - step * 2.0).abs() < 1e-6);
        let mid = layout_origin(font_size, line_spacing, 2, TextVerticalAlign::Middle);
        assert!((mid.first_baseline_y + step).abs() < 1e-6);
        let bottom = layout_origin(font_size, line_spacing, 2, TextVerticalAlign::Bottom);
        assert!((bottom.first_baseline_y + step * 2.0).abs() < 1e-6);
    }

    /// 中央揃えは原点を中心に左右対称になる。
    #[test]
    fn center_box_is_horizontally_symmetric() {
        let f = builtin();
        let b = measure_text_box(
            &f,
            "Ab",
            24.0,
            1.2,
            TextAlign::Center,
            TextVerticalAlign::Middle,
            0.0,
        )
        .expect("枠が得られる");
        assert!((b.min[0] + b.max[0]).abs() < 1e-3, "左右対称");
    }

    /// 複数行は「行送り×(行数-1) + アセント + ディセント」の高さになり、
    /// 幅は最長行に一致する。
    #[test]
    fn multiline_box_covers_all_lines() {
        let f = builtin();
        let font_size = 20.0;
        let line_spacing = 1.5;
        let one = measure_text_box(
            &f,
            "AAAA",
            font_size,
            line_spacing,
            TextAlign::Left,
            TextVerticalAlign::Top,
            0.0,
        )
        .unwrap();
        let two = measure_text_box(
            &f,
            "A\nAAAA",
            font_size,
            line_spacing,
            TextAlign::Left,
            TextVerticalAlign::Top,
            0.0,
        )
        .unwrap();
        let expect_h = font_size * line_spacing
            + (ascent_em(&f) + descent_em(&f)) * font_size;
        assert!(((two.max[1] - two.min[1]) - expect_h).abs() < 1e-3);
        assert!(
            ((two.max[0] - two.min[0]) - (one.max[0] - one.min[0])).abs() < 1e-3,
            "幅は最長行"
        );
    }

    /// 縁取りぶん枠が四方へ広がる（ただしスプレッド上限で頭打ち）。
    #[test]
    fn outline_expands_box_up_to_spread_limit() {
        let f = builtin();
        let font_size = 40.0;
        let plain = measure_text_box(
            &f,
            "A",
            font_size,
            1.2,
            TextAlign::Left,
            TextVerticalAlign::Top,
            0.0,
        )
        .unwrap();
        let outlined = measure_text_box(
            &f,
            "A",
            font_size,
            1.2,
            TextAlign::Left,
            TextVerticalAlign::Top,
            3.0,
        )
        .unwrap();
        assert!((plain.min[0] - outlined.min[0] - 3.0).abs() < 1e-4);
        assert!((outlined.max[1] - plain.max[1] - 3.0).abs() < 1e-4);
        // 焼いていない太さは効かない（0.125em = 5px で頭打ち）
        let huge = measure_text_box(
            &f,
            "A",
            font_size,
            1.2,
            TextAlign::Left,
            TextVerticalAlign::Top,
            1000.0,
        )
        .unwrap();
        let cap = SDF_SPREAD_EM * font_size;
        assert!((plain.min[0] - huge.min[0] - cap).abs() < 1e-4);
    }

    /// 送り幅はフォントサイズに線形（em 正規化が効いている）。
    #[test]
    fn width_scales_with_font_size() {
        let f = builtin();
        let w1 = measure_line_width(&f, "Test", 10.0);
        let w2 = measure_line_width(&f, "Test", 20.0);
        assert!((w2 - w1 * 2.0).abs() < 1e-3);
    }
}
