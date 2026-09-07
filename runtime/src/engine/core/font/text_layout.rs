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
use super::text_wrap::{WrappedLine, normalize_newlines, wrap_lines};
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
    // 枠なし（box_width = 0）の `resolve_layout` は従来式とビット互換である。
    // 定義を 2 本持たないため、ここは薄い委譲に留める。
    resolve_layout(
        font,
        text,
        &TextLayoutSpec {
            font_size,
            line_spacing,
            align,
            vertical_align,
            outline_width,
            ..TextLayoutSpec::default()
        },
    )
    .map(|r| r.bounds)
}

// ============================================================
//  枠つきレイアウト（TextLayoutSpec / ResolvedLayout）
//
//  「枠なし」と「枠あり」でレイアウト規則が変わるため、両方を 1 つの
//  純関数へ集約する。描画（canvas_text）・ピック（pick_2d）・選択枠
//  （canvas_collect）はすべてこの結果を使い、式を各所で再実装しない。
// ============================================================

/// テキストのレイアウト条件（コンポーネントの値をそのまま写したもの）。
///
/// `box_width <= 0` は **枠なし**を意味し、従来どおり
/// 「align / vertical_align はアクター原点に対するブロック配置」になる。
/// `box_width > 0` は **枠あり**で、align / vertical_align は枠内での配置になる。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TextLayoutSpec {
    /// フォントサイズ（キャンバス px）。
    pub font_size: f32,
    /// 行送り倍率（フォントサイズに対する倍率）。
    pub line_spacing: f32,
    /// 水平方向の基準位置。
    pub align: TextAlign,
    /// 垂直方向の基準位置。
    pub vertical_align: TextVerticalAlign,
    /// 縁取りの太さ（px。境界矩形の四方パディングになる）。
    pub outline_width: f32,
    /// 枠の幅（px）。0 = 枠なし（従来経路）。
    pub box_width: f32,
    /// 枠の最小高さ（px）。実際の高さは `max(box_height, 内容高さ)`。
    pub box_height: f32,
    /// 自動折り返しを行うか（`box_width > 0` のときのみ意味を持つ）。
    pub wrap: bool,
}

impl Default for TextLayoutSpec {
    /// 枠なし・折り返しなしの既定条件（`measure_text_box` の従来経路）。
    fn default() -> Self {
        Self {
            font_size: 0.0,
            line_spacing: 1.0,
            align: TextAlign::Left,
            vertical_align: TextVerticalAlign::Top,
            outline_width: 0.0,
            box_width: 0.0,
            box_height: 0.0,
            wrap: false,
        }
    }
}

impl TextLayoutSpec {
    /// 枠を持つか（＝枠基準のレイアウト・pivot が有効か）。
    ///
    /// 判定はこの 1 か所だけを使う（各所で `> 0.0` を書くと規則がぶれる）。
    #[inline]
    pub fn has_box(&self) -> bool {
        self.box_width > 0.0
    }

    /// 折り返しに使う最大幅（px）。0 = 折り返さない。
    #[inline]
    fn wrap_width(&self) -> f32 {
        if self.has_box() && self.wrap {
            self.box_width
        } else {
            0.0
        }
    }
}

/// レイアウト解決結果（描画・計測が共有する唯一の中間表現）。
pub struct ResolvedLayout {
    /// 描画上限で切り詰めた後の文字列。`lines` の範囲はこの文字列に対するもの。
    pub text: String,
    /// 行分割の結果（範囲と幅）。
    pub lines: Vec<WrappedLine>,
    /// 行送り（px）= font_size * line_spacing。
    pub line_step: f32,
    /// 1 行目の**ベースライン** Y（px。Y は下向き）。
    pub first_baseline_y: f32,
    /// 行ごとのペン開始 X（px。`lines` と同じ長さ・同じ順）。
    pub base_x: Vec<f32>,
    /// 枠の矩形（枠なしのときは `None`）。
    pub frame: Option<TextLocalBox>,
    /// pivot を掛ける基準サイズ（px）。枠なしは `[0, 0]`（＝ pivot 無効）。
    pub pivot_size: [f32; 2],
    /// 枠と字面を合わせた境界矩形（ピック・選択枠が使う）。
    pub bounds: TextLocalBox,
}

impl ResolvedLayout {
    /// 正規化 pivot からローカル平行移動量（px）を求める。
    ///
    /// 枠ありのテキストは、行列側ではなく**グリフ座標側**で pivot を適用する
    /// （行列が「フォントを測らないと組めない」状態になるのを避けるため）。
    /// 枠なしは `pivot_size = [0, 0]` なので常に `[0, 0]` を返す＝従来どおり pivot 無効。
    #[inline]
    pub fn pivot_offset(&self, pivot: [f32; 2]) -> [f32; 2] {
        [
            -pivot[0] * self.pivot_size[0],
            -pivot[1] * self.pivot_size[1],
        ]
    }
}

/// レイアウトを解決する（**描画・計測の唯一の定義**）。
///
/// # 枠なし（`spec.box_width <= 0`）
/// 従来の `layout_origin` / `line_base_x` と完全に同じ結果を返す。
/// 折り返しは行わず、明示改行だけで行が分かれる。`pivot_size = [0, 0]`。
///
/// # 枠あり（`spec.box_width > 0`）
/// - 枠のローカル矩形は `[0, W] × [0, H]`（左上原点。Sprite と同じ規約）
/// - `W = box_width`、`H = max(box_height, 行送り × 行数)`
/// - 行 i のベースライン = `v_off + i * line_step + half_leading + ascent`
///   （`half_leading = (line_step − (ascent + descent)) / 2`。行送りの余白を
///   行の上下へ均等に配る＝一般的なテキストレイアウトと同じ規則）
/// - `v_off` は Top:0 / Middle:(H − 内容高さ)/2 / Bottom:H − 内容高さ
/// - 行の開始 X は Left:0 / Center:(W − 行幅)/2 / Right:W − 行幅
///
/// 描画されない入力（空文字・サイズ 0）は `None` を返す。
pub fn resolve_layout(font: &FontArc, text: &str, spec: &TextLayoutSpec) -> Option<ResolvedLayout> {
    if text.is_empty() || spec.font_size <= 0.0 {
        return None;
    }

    // 改行表記を \n に正規化してから切り詰める（正規化は normalize_newlines
    // 1 関数に集約。詳細は text_wrap.rs のコメントを参照）。
    //
    // 【正規化を先に行う理由】
    // ここで正規化した文字列を `layout.text`（ResolvedLayout::text）として
    // 保持し、そのまま `wrap_lines` にも渡す。`wrap_lines` は自分でも同じ
    // 関数で正規化するが、入力が既に正規化済みなら \r を含まないため
    // 何もしない（Cow::Borrowed）。こうして「layout.text の内容」と
    // 「wrap_lines が返す WrappedLine::range の基準文字列」を必ず一致させる
    // （どちらかだけ正規化すると、CRLF が \r\n → \n で 1 バイト縮む分だけ
    //  range がズレて canvas_text.rs のスライスが破綻する）。
    let normalized = normalize_newlines(text);
    // 描画側と同じ上限で切り詰める（表示されない文字を枠に含めない）。
    let truncated: String = if normalized.chars().count() > MAX_TEXT_CHARS {
        normalized.chars().take(MAX_TEXT_CHARS).collect()
    } else {
        normalized.into_owned()
    };

    // 行分割（枠なし・折り返し無効なら明示改行での分割と同一）。
    let lines = wrap_lines(font, &truncated, spec.font_size, spec.wrap_width());
    if lines.is_empty() {
        return None;
    }

    let line_step = spec.font_size * spec.line_spacing;
    let ascent = ascent_em(font) * spec.font_size;
    let descent = descent_em(font) * spec.font_size;
    let pad = outline_pad_px(spec.outline_width, spec.font_size);
    let content_h = line_step * lines.len() as f32;

    // 枠の有無でベースライン・行頭 X の規則が変わる。
    let (first_baseline_y, base_x, frame, pivot_size) = if spec.has_box() {
        let w = spec.box_width;
        // 指定高さと内容高さの大きいほうが実際の枠の高さ（自動伸縮）。
        let h = spec.box_height.max(content_h);
        // 枠内でのブロック全体の縦オフセット。
        let v_off = match spec.vertical_align {
            TextVerticalAlign::Top => 0.0,
            TextVerticalAlign::Middle => (h - content_h) * 0.5,
            TextVerticalAlign::Bottom => h - content_h,
        };
        // 行送りの余りを行の上下へ均等配分した半分（行の上側の余白）。
        let half_leading = (line_step - (ascent + descent)) * 0.5;
        let base_x: Vec<f32> = lines
            .iter()
            .map(|l| match spec.align {
                TextAlign::Left => 0.0,
                TextAlign::Center => (w - l.width) * 0.5,
                TextAlign::Right => w - l.width,
            })
            .collect();
        (
            v_off + half_leading + ascent,
            base_x,
            Some(TextLocalBox {
                min: [0.0, 0.0],
                max: [w, h],
            }),
            [w, h],
        )
    } else {
        // 従来経路（アクター原点に対するブロック配置。pivot は効かない）。
        let origin = layout_origin(
            spec.font_size,
            spec.line_spacing,
            lines.len(),
            spec.vertical_align,
        );
        let base_x: Vec<f32> = lines
            .iter()
            .map(|l| line_base_x(spec.align, l.width))
            .collect();
        (origin.first_baseline_y, base_x, None, [0.0, 0.0])
    };

    // ── 字面の境界（縁取りぶんを四方へ足す）──
    let mut min_x = f32::MAX;
    let mut max_x = f32::MIN;
    for (line, bx) in lines.iter().zip(base_x.iter()) {
        min_x = min_x.min(*bx);
        max_x = max_x.max(bx + line.width);
    }
    let last_baseline_y = first_baseline_y + line_step * (lines.len() as f32 - 1.0);
    let mut bounds = TextLocalBox {
        min: [min_x - pad, first_baseline_y - ascent - pad],
        max: [max_x + pad, last_baseline_y + descent + pad],
    };
    // 枠がある場合は枠との和集合にする（空白だけの行でも枠全体を掴めるように）。
    if let Some(f) = frame {
        bounds.min[0] = bounds.min[0].min(f.min[0]);
        bounds.min[1] = bounds.min[1].min(f.min[1]);
        bounds.max[0] = bounds.max[0].max(f.max[0]);
        bounds.max[1] = bounds.max[1].max(f.max[1]);
    }

    Some(ResolvedLayout {
        text: truncated,
        lines,
        line_step,
        first_baseline_y,
        base_x,
        frame,
        pivot_size,
        bounds,
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

    // ── 枠つきレイアウト（resolve_layout）────────────────────

    /// 枠つきレイアウトの共通スペックを作る。
    fn box_spec(w: f32, h: f32, wrap: bool) -> TextLayoutSpec {
        TextLayoutSpec {
            font_size: 20.0,
            line_spacing: 1.5,
            box_width: w,
            box_height: h,
            wrap,
            ..TextLayoutSpec::default()
        }
    }

    /// 枠なしの resolve_layout は従来の枠計算と一致し、pivot も無効（サイズ 0）。
    #[test]
    fn frameless_layout_matches_legacy_box() {
        let f = builtin();
        let spec = TextLayoutSpec {
            font_size: 24.0,
            line_spacing: 1.2,
            align: TextAlign::Center,
            vertical_align: TextVerticalAlign::Middle,
            ..TextLayoutSpec::default()
        };
        let r = resolve_layout(&f, "Ab\nCd", &spec).expect("枠が得られる");
        let legacy = measure_text_box(
            &f,
            "Ab\nCd",
            24.0,
            1.2,
            TextAlign::Center,
            TextVerticalAlign::Middle,
            0.0,
        )
        .unwrap();
        assert_eq!(r.bounds, legacy);
        assert!(r.frame.is_none());
        assert_eq!(r.pivot_size, [0.0, 0.0]);
        assert_eq!(r.pivot_offset([0.5, 0.5]), [0.0, 0.0], "枠なしは pivot 無効");
    }

    /// 枠の高さは「指定値と内容高さの大きいほう」になる（自動伸縮）。
    #[test]
    fn box_height_is_max_of_specified_and_content() {
        let f = builtin();
        // 1 行 = 20 * 1.5 = 30px。指定 10px なら内容 30px が勝つ。
        let grown = resolve_layout(&f, "A", &box_spec(200.0, 10.0, false)).unwrap();
        assert!((grown.frame.unwrap().max[1] - 30.0).abs() < 1e-4);
        // 指定 100px なら指定が勝つ。
        let fixed = resolve_layout(&f, "A", &box_spec(200.0, 100.0, false)).unwrap();
        assert!((fixed.frame.unwrap().max[1] - 100.0).abs() < 1e-4);
        // pivot 基準サイズは枠そのもの。
        assert_eq!(fixed.pivot_size, [200.0, 100.0]);
    }

    /// 枠内では align が「枠内の水平配置」になる（原点基準ではない）。
    #[test]
    fn box_align_places_line_inside_frame() {
        let f = builtin();
        let width = 200.0;
        let left = resolve_layout(&f, "Ab", &box_spec(width, 0.0, false)).unwrap();
        assert!((left.base_x[0]).abs() < 1e-4, "左揃えは枠の左端");
        let mut spec = box_spec(width, 0.0, false);
        spec.align = TextAlign::Center;
        let center = resolve_layout(&f, "Ab", &spec).unwrap();
        let line_w = center.lines[0].width;
        assert!((center.base_x[0] - (width - line_w) * 0.5).abs() < 1e-4);
        spec.align = TextAlign::Right;
        let right = resolve_layout(&f, "Ab", &spec).unwrap();
        assert!((right.base_x[0] - (width - line_w)).abs() < 1e-4);
    }

    /// 枠内では vertical_align が「枠内の垂直配置」になる。
    #[test]
    fn box_vertical_align_places_block_inside_frame() {
        let f = builtin();
        let h = 200.0;
        let content = 20.0 * 1.5; // 1 行ぶんの行送り
        let mut spec = box_spec(100.0, h, false);
        let top = resolve_layout(&f, "A", &spec).unwrap();
        spec.vertical_align = TextVerticalAlign::Middle;
        let mid = resolve_layout(&f, "A", &spec).unwrap();
        spec.vertical_align = TextVerticalAlign::Bottom;
        let bottom = resolve_layout(&f, "A", &spec).unwrap();
        assert!((mid.first_baseline_y - top.first_baseline_y - (h - content) * 0.5).abs() < 1e-4);
        assert!((bottom.first_baseline_y - top.first_baseline_y - (h - content)).abs() < 1e-4);
        // 上端揃えの 1 行目は「枠上端 + ハーフレディング + アセント」に載る。
        let ascent = ascent_em(&f) * 20.0;
        let descent = descent_em(&f) * 20.0;
        let half_leading = (content - (ascent + descent)) * 0.5;
        assert!((top.first_baseline_y - (half_leading + ascent)).abs() < 1e-4);
    }

    /// 枠 + 折り返し有効なら、長い文字列が複数行になり枠の高さが伸びる。
    #[test]
    fn box_wrap_grows_height() {
        let f = builtin();
        let src = "あいうえおかきくけこ";
        let five = measure_line_width(&f, "あいうえお", 20.0) + 0.01;
        let r = resolve_layout(&f, src, &box_spec(five, 0.0, true)).unwrap();
        assert_eq!(r.lines.len(), 2, "5 文字幅なら 2 行");
        assert!((r.frame.unwrap().max[1] - 20.0 * 1.5 * 2.0).abs() < 1e-4);
        // 折り返し無効なら 1 行のまま（枠からはみ出す）。
        let no_wrap = resolve_layout(&f, src, &box_spec(five, 0.0, false)).unwrap();
        assert_eq!(no_wrap.lines.len(), 1);
    }

    /// pivot は枠サイズに対する正規化値としてローカル平行移動になる。
    #[test]
    fn pivot_offset_scales_with_frame() {
        let f = builtin();
        let r = resolve_layout(&f, "A", &box_spec(200.0, 100.0, false)).unwrap();
        assert_eq!(r.pivot_offset([0.5, 1.0]), [-100.0, -100.0]);
    }

    /// 枠ありの境界矩形は枠と字面の和集合になる（空白行でも枠全体を含む）。
    #[test]
    fn box_bounds_include_frame() {
        let f = builtin();
        let r = resolve_layout(&f, " ", &box_spec(200.0, 100.0, false)).unwrap();
        assert!(r.bounds.min[0] <= 0.0 && r.bounds.min[1] <= 0.0);
        assert!(r.bounds.max[0] >= 200.0 && r.bounds.max[1] >= 100.0);
    }

    /// CRLF（WPF TextBox が Enter で挿入する改行）を含む文字列でも、
    /// LF だけの同内容と完全に同じ枠になる（本不具合の回帰テスト）。
    ///
    /// 正規化前は `\r` が 1 文字ぶんの幅を持つ未定義グリフとして描かれ、
    /// 行の幅・枠の右端が余分に広がっていた。
    #[test]
    fn measure_text_box_ignores_carriage_return() {
        let f = builtin();
        let font_size = 24.0;
        let line_spacing = 1.2;
        let lf = measure_text_box(
            &f, "a\nb", font_size, line_spacing,
            TextAlign::Left, TextVerticalAlign::Top, 0.0,
        )
        .unwrap();
        let crlf = measure_text_box(
            &f, "a\r\nb", font_size, line_spacing,
            TextAlign::Left, TextVerticalAlign::Top, 0.0,
        )
        .unwrap();
        let cr = measure_text_box(
            &f, "a\rb", font_size, line_spacing,
            TextAlign::Left, TextVerticalAlign::Top, 0.0,
        )
        .unwrap();
        assert_eq!(crlf, lf, "CRLF は LF と完全に同じ枠になる（\r が幅に混入しない）");
        assert_eq!(cr, lf, "単独 CR も LF と完全に同じ枠になる");
    }

    /// 枠あり（box_width 指定）レイアウトでも CRLF は行数・枠高さに影響しない。
    #[test]
    fn resolve_layout_box_ignores_carriage_return() {
        let f = builtin();
        let lf = resolve_layout(&f, "A\nB", &box_spec(200.0, 0.0, false)).unwrap();
        let crlf = resolve_layout(&f, "A\r\nB", &box_spec(200.0, 0.0, false)).unwrap();
        assert_eq!(lf.lines.len(), crlf.lines.len(), "行数が変わらない");
        assert_eq!(lf.bounds, crlf.bounds, "枠が変わらない");
        // layout.text（wrap_lines の range が対応する文字列）にも \r が残らない。
        assert!(!crlf.text.contains('\r'), "正規化済みの text に \r が残っていない");
    }
}
