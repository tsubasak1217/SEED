// ============================================================
//  text_input/indices.rs — 文字列の添字の変換（UTF-8 のバイト・UTF-16 の単位・文字・書記素）【純関数・単体テスト付き】
//
//  【なぜ要るか】（docs/ui_text_input.md §3・docs/app_platform_roadmap.md §3.8.1 の I-6）
//  入力欄の状態は 3 つの世界の添字を行き来する:
//    - エンジン（Rust の String）… UTF-8 のバイト位置（本文・選択・変換中の区間はこれで持つ）
//    - Android の IME（GameTextInput）… Java の String の添字＝UTF-16 の単位（stateChanged・set_text_input_state）
//    - スクリプト（C# の string）… UTF-16 の単位（string.Length・Substring の添字）
//    - Windows の IME（winit の Ime::Preedit）… 変換中の文字列の中の UTF-8 のバイト位置
//  ここはその変換と、境界への丸め（文字の途中・サロゲートの途中を指した添字を前の境界へ寄せる）だけを持つ。
//  範囲の外を指した添字は末尾へ丸める（呼び出し側の添字の誤りで panic しない）。
//
//  【書記素（grapheme cluster）】削除（Backspace・Delete）とカーソルの移動は、見た目の 1 文字（書記素）ごとに行う
//  （絵文字の合字・結合文字の濁点を途中で切らない）。境界は unicode-segmentation の拡張書記素クラスタ。
// ============================================================

use unicode_segmentation::UnicodeSegmentation;

/// UTF-16 の単位で数えた長さ。
pub fn utf16_len(text: &str) -> usize {
    text.encode_utf16().count()
}

/// バイト位置を文字の境界へ丸める（文字の途中なら前の境界。範囲の外なら末尾）。
pub fn floor_char_boundary(text: &str, byte: usize) -> usize {
    if byte >= text.len() {
        return text.len();
    }
    let mut index = byte;
    // UTF-8 の後続バイトの途中なら前へ戻る（最大 3 バイト）
    while index > 0 && !text.is_char_boundary(index) {
        index -= 1;
    }
    index
}

/// UTF-16 の位置 → UTF-8 のバイト位置（サロゲートの途中は前の境界へ、範囲の外は末尾へ）。
pub fn utf16_to_byte(text: &str, utf16_index: usize) -> usize {
    let mut units = 0usize;
    for (byte, ch) in text.char_indices() {
        let width = ch.len_utf16();
        if units + width > utf16_index {
            // この文字の中（か手前）を指している: 文字の先頭へ寄せる
            return byte;
        }
        units += width;
    }
    text.len()
}

/// UTF-8 のバイト位置 → UTF-16 の位置（文字の途中のバイトは前の境界へ、範囲の外は末尾へ）。
pub fn byte_to_utf16(text: &str, byte: usize) -> usize {
    let end = floor_char_boundary(text, byte);
    text[..end].encode_utf16().count()
}

/// 文字（Unicode のスカラー値）の番号 → バイト位置（範囲の外は末尾へ）。
pub fn char_to_byte(text: &str, char_index: usize) -> usize {
    text.char_indices().nth(char_index).map_or(text.len(), |(byte, _)| byte)
}

/// バイト位置 → 文字の番号（文字の途中のバイトは前の境界へ）。
pub fn byte_to_char(text: &str, byte: usize) -> usize {
    let end = floor_char_boundary(text, byte);
    text[..end].chars().count()
}

/// 書記素の数（見た目の文字の数。最大の長さの数え方）。
pub fn grapheme_count(text: &str) -> usize {
    text.graphemes(true).count()
}

/// 先頭から `count` 個の書記素が占めるバイト数（書記素が足りなければ全体の長さ）。
pub fn grapheme_prefix_bytes(text: &str, count: usize) -> usize {
    text.grapheme_indices(true).nth(count).map_or(text.len(), |(byte, _)| byte)
}

/// `byte` の直前の書記素の境界（先頭なら 0）。`byte` は境界へ丸めてから見る。
pub fn prev_grapheme_boundary(text: &str, byte: usize) -> usize {
    let at = floor_char_boundary(text, byte);
    let mut previous = 0;
    for (start, _) in text.grapheme_indices(true) {
        if start >= at {
            break;
        }
        previous = start;
    }
    previous
}

/// `byte` の直後の書記素の境界（末尾なら末尾）。`byte` は境界へ丸めてから見る。
pub fn next_grapheme_boundary(text: &str, byte: usize) -> usize {
    let at = floor_char_boundary(text, byte);
    for (start, grapheme) in text.grapheme_indices(true) {
        let end = start + grapheme.len();
        if end > at {
            return end;
        }
    }
    text.len()
}

/// バイト位置を書記素の境界へ丸める（書記素の途中なら前の境界）。
pub fn floor_grapheme_boundary(text: &str, byte: usize) -> usize {
    let at = floor_char_boundary(text, byte);
    let mut boundary = 0;
    for (start, grapheme) in text.grapheme_indices(true) {
        if start > at {
            break;
        }
        boundary = start;
        if start + grapheme.len() == at {
            return at;
        }
    }
    if at == text.len() { at } else { boundary }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 「あいう」は UTF-8 で 9 バイト・UTF-16 で 3（実機の R-4 と同じ数え方）。
    #[test]
    fn japanese_utf16_and_bytes() {
        let text = "あいう";
        assert_eq!(text.len(), 9);
        assert_eq!(utf16_len(text), 3);
        assert_eq!(utf16_to_byte(text, 0), 0);
        assert_eq!(utf16_to_byte(text, 1), 3);
        assert_eq!(utf16_to_byte(text, 3), 9);
        assert_eq!(byte_to_utf16(text, 6), 2);
        assert_eq!(byte_to_utf16(text, 9), 3);
    }

    /// サロゲートの組（絵文字）の途中の UTF-16 の位置は文字の先頭へ寄せる。
    #[test]
    fn surrogate_middle_rounds_down() {
        let text = "a😀b"; // 😀 は UTF-16 で 2 単位・UTF-8 で 4 バイト
        assert_eq!(utf16_len(text), 4);
        assert_eq!(utf16_to_byte(text, 1), 1, "😀 の先頭");
        assert_eq!(utf16_to_byte(text, 2), 1, "サロゲートの途中は前の境界");
        assert_eq!(utf16_to_byte(text, 3), 5, "b の先頭");
        assert_eq!(byte_to_utf16(text, 5), 3);
    }

    /// 文字の途中のバイト・範囲の外は丸める（panic しない）。
    #[test]
    fn out_of_range_and_mid_char_bytes_are_clamped() {
        let text = "あい";
        assert_eq!(floor_char_boundary(text, 1), 0);
        assert_eq!(floor_char_boundary(text, 4), 3);
        assert_eq!(floor_char_boundary(text, 100), 6);
        assert_eq!(byte_to_utf16(text, 100), 2);
        assert_eq!(utf16_to_byte(text, 100), 6);
        assert_eq!(char_to_byte(text, 5), 6);
        assert_eq!(byte_to_char(text, 4), 1);
    }

    /// 書記素: 家族の絵文字（ZWJ の合字）と結合文字の濁点は 1 つに数え、途中で切らない。
    #[test]
    fn grapheme_boundaries_keep_clusters() {
        let family = "👨‍👩‍👧"; // 5 つのスカラー値・1 書記素
        let text = format!("a{family}か\u{3099}");
        assert_eq!(grapheme_count(&text), 3);
        let family_end = 1 + family.len();
        assert_eq!(next_grapheme_boundary(&text, 1), family_end);
        assert_eq!(prev_grapheme_boundary(&text, family_end), 1);
        assert_eq!(prev_grapheme_boundary(&text, text.len()), family_end, "濁点つきの「か」を 1 つで戻る");
        assert_eq!(floor_grapheme_boundary(&text, 5), 1, "合字の途中は合字の先頭へ");
        assert_eq!(floor_grapheme_boundary(&text, text.len()), text.len());
        assert_eq!(grapheme_prefix_bytes(&text, 2), family_end);
        assert_eq!(grapheme_prefix_bytes(&text, 10), text.len());
    }

    /// 空の文字列でも落ちない。
    #[test]
    fn empty_text_is_safe() {
        assert_eq!(utf16_to_byte("", 3), 0);
        assert_eq!(byte_to_utf16("", 3), 0);
        assert_eq!(prev_grapheme_boundary("", 0), 0);
        assert_eq!(next_grapheme_boundary("", 0), 0);
        assert_eq!(grapheme_count(""), 0);
    }
}
