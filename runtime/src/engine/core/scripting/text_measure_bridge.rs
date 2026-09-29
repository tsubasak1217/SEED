// ============================================================
//  text_measure_bridge.rs — 1 行の文字の寸法の API（SEED.TextMeasure）の FFI（W2-6b。W2-6c の Text.Measure の芽）
//
//  【役割】入力欄（SEED.UI.TextField）のカーソルの位置・タップの位置からの添字・はみ出しのスクロールに、描画と同じ送り幅
//  （font::text_layout::advance_em × 大きさ。カーニングなし＝描画の measure_range_width と同じ式）を使う。
//  記法（[icon:…]・{…}）は解かない（入力欄の本文は記法を逃がして描くので、利用者の打った文字そのものを測る）。
//  複数行・折り返し・枠の大きさ（Text.Measure）は W2-6c。
//
//  【ffi_text_measure(op, font, fontLen, size, text, textLen, out, outCap) → i32】（op は C# 側 ScriptHost.TextMeasureOp* と一致）
//    op 0 = LineWidth   … out[0] = 本文の 1 行の幅（キャンバスの単位＝大きさと同じ単位）。1
//    op 1 = CaretStops  … out[i] = UTF-16 の添字 i の前の端の x（i = 0..=長さ。サロゲートの間は前の文字の頭と同じ）。
//                         必要な数（長さ + 1）を返す（outCap が足りなければ書かない）
//    op 2 = Metrics     … out = [アセント, ディセント]（どちらも正の値。大きさと同じ単位）。2
//    それ以外・大きさが正でない … -1
//  font は assets:// のパス（空 = 組み込みの書体。読めなければ組み込みへ落ちる＝描画と同じ規則）。
// ============================================================

use crate::engine::core::font::layout_fonts::font_for;
use crate::engine::core::font::text_layout::{advance_em, ascent_em, descent_em};

/// 1 行の幅。
pub const MEASURE_OP_LINE_WIDTH: i32 = 0;
/// カーソルの位置の並び。
pub const MEASURE_OP_CARET_STOPS: i32 = 1;
/// 書体の縦の寸法。
pub const MEASURE_OP_METRICS: i32 = 2;
/// 読めない値・知らない op（C# 側 ScriptHost.TextMeasureInvalid と一致させる）。
pub const MEASURE_RESULT_INVALID: i32 = -1;

/// LineWidth の書く数。
const LINE_WIDTH_LEN: usize = 1;
/// Metrics の書く数。
const METRICS_LEN: usize = 2;

/// ポインタと長さから文字列を読む（null・負の長さ・不正な UTF-8 は空文字）。
///
/// # Safety
/// `ptr` は null か、`len` バイト以上を読める領域を指していること。
unsafe fn text_from<'a>(ptr: *const u8, len: i32) -> &'a str {
    match usize::try_from(len) {
        Ok(n) if n > 0 && !ptr.is_null() => {
            std::str::from_utf8(unsafe { std::slice::from_raw_parts(ptr, n) }).unwrap_or("")
        }
        _ => "",
    }
}

/// 文字の寸法（SEED.TextMeasure の入口）。
///
/// # Safety
/// `font`・`text` は null か長さの分だけ読める領域、`out` は null か `out_cap` 個の f32 を書ける領域を指していること。
#[allow(clippy::too_many_arguments)] // C# の関数ポインタの形そのまま
pub(super) unsafe extern "system" fn ffi_text_measure(
    op: i32,
    font: *const u8,
    font_len: i32,
    size: f32,
    text: *const u8,
    text_len: i32,
    out: *mut f32,
    out_cap: i32,
) -> i32 {
    if !(size.is_finite() && size > 0.0) {
        return MEASURE_RESULT_INVALID;
    }
    // SAFETY: 呼び出し側（C#）が長さつきで渡した領域だけを読む
    let (font_path, text) = unsafe { (text_from(font, font_len), text_from(text, text_len)) };
    let out: &mut [f32] = match usize::try_from(out_cap) {
        // SAFETY: 呼び出し側が out_cap 個を書ける領域を渡した
        Ok(n) if n > 0 && !out.is_null() => unsafe { std::slice::from_raw_parts_mut(out, n) },
        _ => &mut [],
    };
    measure(op, font_path, size, text, out)
}

/// op ごとの計算（純粋な部分。単体テストから直接呼ぶ）。
fn measure(op: i32, font_path: &str, size: f32, text: &str, out: &mut [f32]) -> i32 {
    let font = font_for(font_path);
    match op {
        MEASURE_OP_LINE_WIDTH => {
            let width: f32 = text.chars().map(|ch| advance_em(&font, ch) * size).sum();
            match out.get_mut(..LINE_WIDTH_LEN) {
                Some(slot) => {
                    slot[0] = width;
                    LINE_WIDTH_LEN as i32
                }
                None => MEASURE_RESULT_INVALID,
            }
        }
        MEASURE_OP_CARET_STOPS => {
            let needed = text.encode_utf16().count() + 1;
            if out.len() >= needed {
                let mut x = 0.0f32;
                let mut index = 0usize;
                out[0] = 0.0;
                for ch in text.chars() {
                    let advance = advance_em(&font, ch) * size;
                    // サロゲートの組（UTF-16 で 2 単位）の間は文字の頭のまま（添字が文字の途中を指しても頭に置く）
                    for extra in 1..ch.len_utf16() {
                        out[index + extra] = x;
                    }
                    x += advance;
                    index += ch.len_utf16();
                    out[index] = x;
                }
            }
            i32::try_from(needed).unwrap_or(i32::MAX)
        }
        MEASURE_OP_METRICS => match out.get_mut(..METRICS_LEN) {
            Some(slot) => {
                slot[0] = ascent_em(&font) * size;
                slot[1] = descent_em(&font) * size;
                METRICS_LEN as i32
            }
            None => MEASURE_RESULT_INVALID,
        },
        _ => MEASURE_RESULT_INVALID,
    }
}

// ============================================================
//  テスト（組み込みの書体で。GPU は要らない）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// カーソルの位置は単調に増え、最後は 1 行の幅と同じ。サロゲートの間は文字の頭。
    #[test]
    fn caret_stops_match_line_width() {
        let text = "aあ😀b";
        let mut stops = vec![0.0f32; 16];
        let needed = measure(MEASURE_OP_CARET_STOPS, "", 20.0, text, &mut stops);
        assert_eq!(needed, 6, "UTF-16 の長さ 5 + 1");
        let mut width = [0.0f32; 1];
        assert_eq!(measure(MEASURE_OP_LINE_WIDTH, "", 20.0, text, &mut width), 1);
        assert!((stops[5] - width[0]).abs() < 1e-3, "{stops:?} {width:?}");
        assert!(stops[1] > stops[0] && stops[2] > stops[1]);
        assert_eq!(stops[3], stops[2], "サロゲートの間は文字の頭");
        assert!(stops[4] > stops[3]);
    }

    /// 入れ物が足りなければ必要な数だけ返して書かない。大きさが正でなければ -1。
    #[test]
    fn small_buffer_and_bad_size() {
        let mut out = [7.0f32; 2];
        assert_eq!(measure(MEASURE_OP_CARET_STOPS, "", 10.0, "abc", &mut out), 4);
        assert_eq!(out, [7.0, 7.0]);
        let mut metrics = [0.0f32; 2];
        assert_eq!(measure(MEASURE_OP_METRICS, "", 10.0, "", &mut metrics), 2);
        assert!(metrics[0] > 0.0 && metrics[1] > 0.0);
        assert_eq!(unsafe { ffi_text_measure(MEASURE_OP_LINE_WIDTH, std::ptr::null(), 0, 0.0, std::ptr::null(), 0, std::ptr::null_mut(), 0) }, -1);
        assert_eq!(measure(99, "", 10.0, "a", &mut metrics), MEASURE_RESULT_INVALID);
    }
}
