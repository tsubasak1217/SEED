// ============================================================
//  font/inline/placement.rs — インライン画像の配置矩形を求める（純関数）
//
//  【役割】
//  解決済みレイアウト（`text_layout::ResolvedLayout`）から、
//  「どの画像を、キャンバスローカル px のどの矩形へ描くか」を並べる。
//
//  【なぜ描画側と分けるか】
//  画像は文字（SDF テキストパイプライン）ではなく**スプライト経路**で描く。
//  そのため矩形を求める側（ここ）と描く側（スプライト収集）が別のファイルになる。
//  ペンの進み方はグリフ描画（`canvas_text::emit_glyph_quads`）と同じ規則
//  （行頭 X = `base_x[row]`、1 文字ごとに送り幅を加算）を使うため、
//  文字と画像の間隔がズレることはない。
//
//  【座標系】
//  キャンバスローカル px（原点 = アクター位置、X 右・Y 下）。
//  `offset` には pivot ぶんの平行移動（と影を描くならそのオフセット）を渡す。
// ============================================================

use ab_glyph::FontArc;

use super::doc::IMAGE_PLACEHOLDER;
use crate::engine::core::font::text_layout::{
    ResolvedLayout, advance_em, inline_image_top_offset, x_height_em,
};

/// 描画する画像 1 件の配置結果。
#[derive(Clone, Debug, PartialEq)]
pub struct InlineImageRect {
    /// 画像の assets:// パス（必ず非空＝解決済みのものだけを返す）。
    pub path: String,
    /// 矩形の左上（キャンバスローカル px）。
    pub min: [f32; 2],
    /// 矩形の右下（キャンバスローカル px）。
    pub max: [f32; 2],
}

impl InlineImageRect {
    /// 幅（px）。
    #[inline]
    pub fn width(&self) -> f32 {
        self.max[0] - self.min[0]
    }

    /// 高さ（px）。
    #[inline]
    pub fn height(&self) -> f32 {
        self.max[1] - self.min[1]
    }
}

/// 解決済みレイアウトから、描画する画像の矩形を並べる。
///
/// - `font`      : レイアウトに使ったフォント（送り幅・x ハイトの取得に使う）
/// - `font_size` : フォントサイズ（px）
/// - `offset`    : すべての矩形へ一様に足す平行移動（pivot ぶん・影ぶん）
///
/// 未解決の画像（パスが空・高さ 0）は返さない（場所だけ取って描かない）。
pub fn collect_image_rects(
    layout: &ResolvedLayout,
    font: &FontArc,
    font_size: f32,
    offset: [f32; 2],
) -> Vec<InlineImageRect> {
    // 画像が 1 つも無い（大多数の）テキストでは何もしない。
    if layout.images.is_empty() {
        return Vec::new();
    }
    let x_height = x_height_em(font) * font_size;
    let mut out: Vec<InlineImageRect> = Vec::with_capacity(layout.images.len());

    for (row, line) in layout.lines.iter().enumerate() {
        // 行頭 X とベースライン Y はグリフ描画とまったく同じ規則で求める。
        let mut pen_x = layout.base_x[row] + offset[0];
        let baseline_y = layout.first_baseline_y + layout.line_step * row as f32 + offset[1];

        let start = line.range.start;
        for (rel, ch) in layout.text[line.range.clone()].char_indices() {
            let abs = start + rel;
            // 画像の代替文字なら矩形を作り、送り幅も画像のものを使う。
            if ch == IMAGE_PLACEHOLDER {
                if let Some(img) = layout.images.get(abs) {
                    let advance = img.advance_px(font_size);
                    if img.is_drawable() {
                        let h = img.height_px(font_size);
                        let top = baseline_y + inline_image_top_offset(h, x_height);
                        out.push(InlineImageRect {
                            path: img.path.clone(),
                            min: [pen_x, top],
                            max: [pen_x + advance, top + h],
                        });
                    }
                    pen_x += advance;
                    continue;
                }
            }
            pen_x += advance_em(font, ch) * font_size;
        }
    }
    out
}

// ============================================================
//  単体テスト（GPU 不要。組み込みフォントで配置規則を検証する）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::{TextAlign, TextVerticalAlign};
    use crate::engine::core::font::DEFAULT_FONT_BYTES;
    use crate::engine::core::font::inline::doc::{IMAGE_PLACEHOLDER, InlineImage, InlineImages};
    use crate::engine::core::font::text_layout::{
        TextLayoutSpec, measure_line_width, resolve_layout_with_images,
    };

    /// テスト用フォント（組み込みフォント）。
    fn builtin() -> FontArc {
        FontArc::try_from_slice(DEFAULT_FONT_BYTES).expect("組み込みフォントを読める")
    }

    /// 枠なし・左上そろえの基本条件。
    fn spec(font_size: f32) -> TextLayoutSpec {
        TextLayoutSpec {
            font_size,
            line_spacing: 1.2,
            align: TextAlign::Left,
            vertical_align: TextVerticalAlign::Top,
            ..TextLayoutSpec::default()
        }
    }

    /// 画像は「直前の文字の送り幅ぶんだけ右」に、画像の送り幅ぶんの
    /// 幅を持って置かれる（＝ペンの進み方がグリフ描画と一致する）。
    #[test]
    fn image_rect_follows_pen_advance() {
        let f = builtin();
        let font_size = 20.0;
        let text = format!("あ{IMAGE_PLACEHOLDER}");
        let advance_em = 2.0;
        let images = InlineImages::from_entries(vec![(
            "あ".len(),
            InlineImage {
                path: "assets://dummy.png".to_string(),
                advance_em,
                height_em: 1.0,
            },
        )]);
        let layout = resolve_layout_with_images(&f, &text, &spec(font_size), &images)
            .expect("レイアウトできる");

        let rects = collect_image_rects(&layout, &f, font_size, [0.0, 0.0]);
        assert_eq!(rects.len(), 1);
        let r = &rects[0];
        let a_w = measure_line_width(&f, "あ", font_size);
        assert!((r.min[0] - a_w).abs() < 1e-3, "画像の左端が「あ」の直後にない");
        assert!(
            (r.width() - advance_em * font_size).abs() < 1e-3,
            "画像の幅が送り幅と一致しない"
        );
        assert!((r.height() - font_size).abs() < 1e-3, "画像の高さが 1em でない");
    }

    /// `offset`（pivot や影のぶん）は全矩形へ一様に効く。
    #[test]
    fn offset_shifts_all_rects() {
        let f = builtin();
        let font_size = 20.0;
        let text = format!("{IMAGE_PLACEHOLDER}");
        let images = InlineImages::from_entries(vec![(
            0,
            InlineImage {
                path: "assets://dummy.png".to_string(),
                advance_em: 1.0,
                height_em: 1.0,
            },
        )]);
        let layout = resolve_layout_with_images(&f, &text, &spec(font_size), &images)
            .expect("レイアウトできる");
        let base = collect_image_rects(&layout, &f, font_size, [0.0, 0.0]);
        let moved = collect_image_rects(&layout, &f, font_size, [3.0, -4.0]);
        assert_eq!(base.len(), 1);
        assert!((moved[0].min[0] - base[0].min[0] - 3.0).abs() < 1e-4);
        assert!((moved[0].min[1] - base[0].min[1] + 4.0).abs() < 1e-4);
    }

    /// 未解決（描かない）画像は矩形を返さない。
    #[test]
    fn unresolved_image_yields_no_rect() {
        let f = builtin();
        let font_size = 20.0;
        let text = format!("あ{IMAGE_PLACEHOLDER}い");
        let images = InlineImages::from_entries(vec![(
            "あ".len(),
            InlineImage {
                path: String::new(),
                advance_em: 1.0,
                height_em: 0.0,
            },
        )]);
        let layout = resolve_layout_with_images(&f, &text, &spec(font_size), &images)
            .expect("レイアウトできる");
        assert!(collect_image_rects(&layout, &f, font_size, [0.0, 0.0]).is_empty());
    }

    /// 2 行目の画像は行送りぶん下へ置かれる（行またぎでも位置がズレない）。
    #[test]
    fn image_on_second_line_is_shifted_down() {
        let f = builtin();
        let font_size = 20.0;
        let text = format!("あ\n{IMAGE_PLACEHOLDER}");
        let off = text.find(IMAGE_PLACEHOLDER).unwrap();
        let images = InlineImages::from_entries(vec![(
            off,
            InlineImage {
                path: "assets://dummy.png".to_string(),
                advance_em: 1.0,
                height_em: 1.0,
            },
        )]);
        let layout = resolve_layout_with_images(&f, &text, &spec(font_size), &images)
            .expect("レイアウトできる");
        let rects = collect_image_rects(&layout, &f, font_size, [0.0, 0.0]);
        assert_eq!(rects.len(), 1);
        // 行頭なので左端は 0、Y は 1 行ぶん（line_step）下のベースライン基準。
        assert!(rects[0].min[0].abs() < 1e-4, "2 行目の行頭にない");
        let expect_top = layout.first_baseline_y
            + layout.line_step
            + crate::engine::core::font::text_layout::inline_image_top_offset(
                font_size,
                crate::engine::core::font::text_layout::x_height_em(&f) * font_size,
            );
        assert!((rects[0].min[1] - expect_top).abs() < 1e-3);
    }
}
