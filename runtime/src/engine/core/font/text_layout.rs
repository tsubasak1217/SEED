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

use super::sdf::SDF_EM_PX;
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

/// 1 文字の送り幅を em 単位（フォントサイズ 1.0 相当）で返す。
///
/// グリフの SDF 化（`rasterize_glyph_sdf`）が記録する `advance_em` と同じ値になるよう、
/// 同一の基準 em サイズ `SDF_EM_PX` でスケールしてから正規化する。
pub fn advance_em(font: &FontArc, ch: char) -> f32 {
    let scaled = font.as_scaled(PxScale::from(SDF_EM_PX));
    scaled.h_advance(font.glyph_id(ch)) / SDF_EM_PX
}

/// 1 行の幅（px）を測る。
pub fn measure_line_width(font: &FontArc, line: &str, font_size: f32) -> f32 {
    line.chars().map(|ch| advance_em(font, ch) * font_size).sum()
}

/// テキストブロックのローカル境界矩形を返す。
///
/// レイアウト規則は `canvas_text.rs::append_item` と同一:
/// - 行送り = `font_size * line_spacing`、ブロック高さ = 行送り × 行数
/// - 垂直: Top = 上端が原点 / Middle = 中央 / Bottom = 下端
/// - 水平: 行ごとに Left = 左端が原点 / Center = 中央 / Right = 右端
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

    let line_step = font_size * line_spacing;
    let block_height = line_step * widths.len() as f32;
    // 垂直方向の基準（キャンバス Y は下向き）
    let base_y = match vertical_align {
        TextVerticalAlign::Top => 0.0,
        TextVerticalAlign::Middle => -block_height * 0.5,
        TextVerticalAlign::Bottom => -block_height,
    };

    // 水平方向は行ごとに基準位置が変わるため、全行の最小/最大を取る。
    let mut min_x = f32::MAX;
    let mut max_x = f32::MIN;
    for w in &widths {
        let base_x = match align {
            TextAlign::Left => 0.0,
            TextAlign::Center => -w * 0.5,
            TextAlign::Right => -w,
        };
        min_x = min_x.min(base_x);
        max_x = max_x.max(base_x + w);
    }

    Some(TextLocalBox {
        min: [min_x, base_y],
        max: [max_x, base_y + block_height],
    })
}

// ============================================================
//  単体テスト（GPU 不要。組み込みフォントで寸法規則を検証する）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用フォント（組み込みフォント）。
    fn builtin() -> FontArc {
        FontArc::try_from_slice(super::super::DEFAULT_FONT_BYTES).expect("組み込みフォントを読める")
    }

    /// 空文字・サイズ 0 は枠を持たない。
    #[test]
    fn empty_text_has_no_box() {
        let f = builtin();
        assert!(
            measure_text_box(&f, "", 24.0, 1.2, TextAlign::Left, TextVerticalAlign::Top).is_none()
        );
        assert!(
            measure_text_box(&f, "A", 0.0, 1.2, TextAlign::Left, TextVerticalAlign::Top).is_none()
        );
    }

    /// 左上揃え: 原点が枠の左上になる。高さは 1 行ぶんの行送り。
    #[test]
    fn left_top_box_starts_at_origin() {
        let f = builtin();
        let font_size = 24.0;
        let line_spacing = 1.2;
        let b = measure_text_box(
            &f,
            "Ab",
            font_size,
            line_spacing,
            TextAlign::Left,
            TextVerticalAlign::Top,
        )
        .expect("枠が得られる");
        assert!((b.min[0]).abs() < 1e-4);
        assert!((b.min[1]).abs() < 1e-4);
        assert!(((b.max[1] - b.min[1]) - font_size * line_spacing).abs() < 1e-4);
        assert!((b.max[0] - b.min[0]) > 0.0, "文字幅は正");
    }

    /// 中央揃えは原点を中心に左右対称、Middle は上下対称になる。
    #[test]
    fn center_middle_box_is_symmetric() {
        let f = builtin();
        let b = measure_text_box(
            &f,
            "Ab",
            24.0,
            1.2,
            TextAlign::Center,
            TextVerticalAlign::Middle,
        )
        .expect("枠が得られる");
        assert!((b.min[0] + b.max[0]).abs() < 1e-3, "左右対称");
        assert!((b.min[1] + b.max[1]).abs() < 1e-3, "上下対称");
    }

    /// 複数行はブロック高さが行数ぶんになり、幅は最長行に一致する。
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
        )
        .unwrap();
        let two = measure_text_box(
            &f,
            "A\nAAAA",
            font_size,
            line_spacing,
            TextAlign::Left,
            TextVerticalAlign::Top,
        )
        .unwrap();
        assert!(((two.max[1] - two.min[1]) - font_size * line_spacing * 2.0).abs() < 1e-4);
        assert!(((two.max[0] - two.min[0]) - (one.max[0] - one.min[0])).abs() < 1e-3, "幅は最長行");
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
