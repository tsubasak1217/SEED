// ============================================================
//  text_input/edit_state.rs — 入力欄の状態（本文・選択・変換中の区間）と編集の操作【純ロジック・単体テスト付き】
//
//  【1 つの形】（E-06 の決定。docs/ui_text_input.md §3）
//  プラットフォームの差（Windows は変換中の文字列と確定の知らせ、Android は IME が持つ状態の丸ごとの写し）を
//  受け口で吸収し、エンジンはこの形 1 つで持つ。添字はすべて本文の UTF-8 のバイト位置（文字の境界）。
//    - 選択は起点（anchor）と動く端（focus＝カーソル）。同じならカーソルだけ（選択なし）
//    - 変換中の区間（composition）は本文の一部（未確定の文字も本文に入っている＝Android の Editable・Flutter の
//      TextEditingValue と同じ考え方）。確定すると区間の印だけが消える
//
//  【操作の意味】（Windows の winit の Ime と PC のキーから使う。Android は状態の丸ごとの置き換え）
//    - set_preedit … 変換中の文字列を差し替える（区間が無ければ選択を消して、そこへ置く）。空なら変換の取り消し
//    - commit      … 確定の文字列で変換中の区間（無ければ選択）を置き換え、カーソルを後ろへ
//    - delete_backward / delete_forward … 選択があれば選択を消す。無ければ前・後ろの書記素 1 つ
//    - move_* … 左右・行頭・行末（extend で選択を伸ばす）
// ============================================================

use std::ops::Range;

use super::indices::{
    byte_to_utf16, floor_char_boundary, next_grapheme_boundary, prev_grapheme_boundary, utf16_to_byte,
};

/// UTF-16 の添字で表した状態（Android の IME・スクリプトとの受け渡しの形）。
/// 変換中の区間が無いときは `composition` が None（Java の -1..-1）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Utf16State {
    /// 本文。
    pub text: String,
    /// 選択の起点（UTF-16 の単位）。
    pub selection_start: usize,
    /// 選択の動く端＝カーソル（UTF-16 の単位。起点より前でもよい）。
    pub selection_end: usize,
    /// 変換中の区間（UTF-16 の単位）。
    pub composition: Option<(usize, usize)>,
}

/// 入力欄の状態（添字は UTF-8 のバイト位置）。
#[derive(Clone, Debug, PartialEq, Eq, Default)]
pub struct TextEditState {
    /// 本文（変換中の文字も含む）。
    pub text: String,
    /// 選択の起点。
    pub anchor: usize,
    /// 選択の動く端（カーソル）。
    pub focus: usize,
    /// 変換中の区間（無ければ None。空の区間は持たない）。
    pub composition: Option<Range<usize>>,
}

impl TextEditState {
    /// 本文の末尾にカーソルを置いた状態。
    pub fn with_caret_at_end(text: impl Into<String>) -> Self {
        let text = text.into();
        let end = text.len();
        Self { text, anchor: end, focus: end, composition: None }
    }

    /// 本文と選択（バイト位置）から作る（境界へ丸める）。
    pub fn new(text: impl Into<String>, anchor: usize, focus: usize) -> Self {
        let mut state = Self { text: text.into(), anchor, focus, composition: None };
        state.normalize();
        state
    }

    /// UTF-16 の添字の状態から作る（範囲の外・サロゲートの途中は丸める）。
    pub fn from_utf16(state: &Utf16State) -> Self {
        let text = state.text.clone();
        let anchor = utf16_to_byte(&text, state.selection_start);
        let focus = utf16_to_byte(&text, state.selection_end);
        let composition = state.composition.map(|(start, end)| {
            let (a, b) = (utf16_to_byte(&text, start), utf16_to_byte(&text, end));
            a.min(b)..a.max(b)
        });
        let mut result = Self { text, anchor, focus, composition };
        result.normalize();
        result
    }

    /// UTF-16 の添字の状態へ写す。
    pub fn to_utf16(&self) -> Utf16State {
        Utf16State {
            text: self.text.clone(),
            selection_start: byte_to_utf16(&self.text, self.anchor),
            selection_end: byte_to_utf16(&self.text, self.focus),
            composition: self
                .composition
                .as_ref()
                .map(|range| (byte_to_utf16(&self.text, range.start), byte_to_utf16(&self.text, range.end))),
        }
    }

    /// 添字を文字の境界・本文の長さの内側へ丸め、空の変換中の区間を消す。
    pub fn normalize(&mut self) {
        self.anchor = floor_char_boundary(&self.text, self.anchor);
        self.focus = floor_char_boundary(&self.text, self.focus);
        if let Some(range) = self.composition.take() {
            let start = floor_char_boundary(&self.text, range.start.min(range.end));
            let end = floor_char_boundary(&self.text, range.start.max(range.end));
            if start < end {
                self.composition = Some(start..end);
            }
        }
    }

    /// 選択の範囲（前 → 後ろ）。
    pub fn selection_range(&self) -> Range<usize> {
        self.anchor.min(self.focus)..self.anchor.max(self.focus)
    }

    /// 選択が無い（カーソルだけ）か。
    pub fn is_collapsed(&self) -> bool {
        self.anchor == self.focus
    }

    /// 選択している文字列（無ければ空）。
    pub fn selected_text(&self) -> &str {
        &self.text[self.selection_range()]
    }

    /// 変換中か。
    pub fn is_composing(&self) -> bool {
        self.composition.is_some()
    }

    /// カーソルを置く（選択を消す）。
    pub fn set_caret(&mut self, byte: usize) {
        let at = floor_char_boundary(&self.text, byte);
        self.anchor = at;
        self.focus = at;
    }

    /// 選択を置く（バイト位置。丸める）。
    pub fn set_selection(&mut self, anchor: usize, focus: usize) {
        self.anchor = floor_char_boundary(&self.text, anchor);
        self.focus = floor_char_boundary(&self.text, focus);
    }

    /// 範囲を文字列で置き換え、カーソルを入れた文字列の後ろへ置く（変換中の区間は消す）。
    fn replace_range(&mut self, range: Range<usize>, insert: &str) {
        self.text.replace_range(range.clone(), insert);
        self.set_caret(range.start + insert.len());
        self.composition = None;
    }

    /// 書き換えの対象の範囲（変換中なら変換中の区間、無ければ選択）。
    fn edit_target(&self) -> Range<usize> {
        self.composition.clone().unwrap_or_else(|| self.selection_range())
    }

    /// 確定の文字列を入れる（変換中の区間、無ければ選択を置き換える。カーソルは後ろ）。
    pub fn commit(&mut self, text: &str) {
        let target = self.edit_target();
        self.replace_range(target, text);
    }

    /// 変換中の文字列を差し替える（Windows の Ime::Preedit）。
    ///
    /// # 引数
    /// * `preedit` - 変換中の文字列（空 = 変換の取り消し・確定の直前の片付け。区間の文字を消す）
    /// * `cursor`  - 変換中の文字列の中のカーソル（バイト位置の組。None = 末尾）
    pub fn set_preedit(&mut self, preedit: &str, cursor: Option<(usize, usize)>) {
        let target = self.edit_target();
        let start = target.start;
        self.text.replace_range(target, preedit);
        if preedit.is_empty() {
            self.composition = None;
            self.set_caret(start);
            return;
        }
        self.composition = Some(start..start + preedit.len());
        match cursor {
            Some((a, b)) => {
                let a = floor_char_boundary(preedit, a);
                let b = floor_char_boundary(preedit, b);
                self.anchor = start + a;
                self.focus = start + b;
            }
            None => self.set_caret(start + preedit.len()),
        }
    }

    /// 変換中の区間の印を消す（文字は残す＝そのまま確定。フォーカスが外れたとき）。
    pub fn finish_composition(&mut self) {
        self.composition = None;
    }

    /// 前の書記素（選択があれば選択）を消す。消したら true。
    pub fn delete_backward(&mut self) -> bool {
        if !self.is_collapsed() {
            self.replace_range(self.selection_range(), "");
            return true;
        }
        if self.focus == 0 {
            return false;
        }
        let start = prev_grapheme_boundary(&self.text, self.focus);
        self.replace_range(start..self.focus, "");
        true
    }

    /// 後ろの書記素（選択があれば選択）を消す。消したら true。
    pub fn delete_forward(&mut self) -> bool {
        if !self.is_collapsed() {
            self.replace_range(self.selection_range(), "");
            return true;
        }
        if self.focus >= self.text.len() {
            return false;
        }
        let end = next_grapheme_boundary(&self.text, self.focus);
        self.replace_range(self.focus..end, "");
        true
    }

    /// カーソルを左へ（extend で選択を伸ばす。選択があって伸ばさないなら選択の前の端へ畳む）。
    pub fn move_left(&mut self, extend: bool) {
        if !extend && !self.is_collapsed() {
            let start = self.selection_range().start;
            self.set_caret(start);
            return;
        }
        self.focus = prev_grapheme_boundary(&self.text, self.focus);
        if !extend {
            self.anchor = self.focus;
        }
    }

    /// カーソルを右へ（extend で選択を伸ばす。選択があって伸ばさないなら選択の後ろの端へ畳む）。
    pub fn move_right(&mut self, extend: bool) {
        if !extend && !self.is_collapsed() {
            let end = self.selection_range().end;
            self.set_caret(end);
            return;
        }
        self.focus = next_grapheme_boundary(&self.text, self.focus);
        if !extend {
            self.anchor = self.focus;
        }
    }

    /// カーソルを先頭へ（1 行の入力欄なので本文の先頭）。
    pub fn move_home(&mut self, extend: bool) {
        self.focus = 0;
        if !extend {
            self.anchor = 0;
        }
    }

    /// カーソルを末尾へ。
    pub fn move_end(&mut self, extend: bool) {
        self.focus = self.text.len();
        if !extend {
            self.anchor = self.focus;
        }
    }

    /// すべてを選ぶ。
    pub fn select_all(&mut self) {
        self.anchor = 0;
        self.focus = self.text.len();
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// Windows の日本語の変換: 変換中 → 確定の直前の空の Preedit → Commit で本文に 1 回だけ入る。
    #[test]
    fn preedit_then_commit_like_winit() {
        let mut state = TextEditState::with_caret_at_end("名前:");
        state.set_preedit("か", None);
        state.set_preedit("かん", None);
        state.set_preedit("かんじ", Some((9, 9)));
        assert_eq!(state.text, "名前:かんじ");
        assert_eq!(state.composition, Some(7..16));
        assert_eq!(state.focus, 16);
        // winit は確定の直前に空の Preedit を送る → 区間の文字を消してから Commit
        state.set_preedit("", None);
        assert_eq!(state.text, "名前:");
        assert!(!state.is_composing());
        state.commit("漢字");
        assert_eq!(state.text, "名前:漢字");
        assert_eq!(state.focus, state.text.len());
        assert!(state.is_collapsed());
    }

    /// 選択があるときの変換の開始は選択を置き換える。
    #[test]
    fn preedit_replaces_selection() {
        let mut state = TextEditState::new("abcdef", 1, 4);
        state.set_preedit("あ", None);
        assert_eq!(state.text, "aあef");
        assert_eq!(state.composition, Some(1..4));
    }

    /// 確定は変換中の区間があればそこを、無ければ選択を置き換える。
    #[test]
    fn commit_replaces_selection_without_composition() {
        let mut state = TextEditState::new("hello", 0, 5);
        state.commit("bye");
        assert_eq!(state.text, "bye");
        assert_eq!((state.anchor, state.focus), (3, 3));
    }

    /// 削除は書記素ごと（合字の絵文字・濁点つきの文字を 1 回で消す）。選択があれば選択を消す。
    #[test]
    fn delete_by_grapheme_and_selection() {
        let mut state = TextEditState::with_caret_at_end("a👨‍👩‍👧か\u{3099}");
        assert!(state.delete_backward());
        assert_eq!(state.text, "a👨‍👩‍👧");
        assert!(state.delete_backward());
        assert_eq!(state.text, "a");
        state.set_caret(0);
        assert!(!state.delete_backward(), "先頭では何もしない");
        assert!(state.delete_forward());
        assert_eq!(state.text, "");
        assert!(!state.delete_forward(), "空では何もしない");

        let mut selected = TextEditState::new("abcdef", 4, 1);
        assert!(selected.delete_forward());
        assert_eq!(selected.text, "aef");
        assert_eq!(selected.focus, 1);
    }

    /// 左右・先頭・末尾の移動と、選択の伸ばし・畳み方。
    #[test]
    fn caret_moves_and_selection() {
        let mut state = TextEditState::with_caret_at_end("あいう");
        state.move_left(false);
        assert_eq!(state.focus, 6);
        state.move_left(true);
        assert_eq!((state.anchor, state.focus), (6, 3));
        assert_eq!(state.selected_text(), "い");
        state.move_right(false);
        assert_eq!((state.anchor, state.focus), (6, 6), "伸ばさない右は選択の後ろの端へ畳む");
        state.move_home(true);
        assert_eq!(state.selected_text(), "あい");
        state.move_end(false);
        assert!(state.is_collapsed());
        state.select_all();
        assert_eq!(state.selected_text(), "あいう");
        state.move_left(false);
        assert_eq!(state.focus, 0, "伸ばさない左は選択の前の端へ畳む");
    }

    /// UTF-16 との往復（Android の IME の添字）。変換中の区間も往復する。
    #[test]
    fn utf16_round_trip() {
        let android = Utf16State {
            text: "あ😀い".to_string(),
            selection_start: 3,
            selection_end: 3,
            composition: Some((3, 4)),
        };
        let state = TextEditState::from_utf16(&android);
        assert_eq!(state.focus, "あ😀".len());
        assert_eq!(state.composition, Some(7..10));
        assert_eq!(state.to_utf16(), android);
    }

    /// 範囲の外・逆向き・空の変換中の区間は丸める（IME の食い違いで panic しない）。
    #[test]
    fn from_utf16_clamps_bad_indices() {
        let state = TextEditState::from_utf16(&Utf16State {
            text: "ab".to_string(),
            selection_start: 10,
            selection_end: 1,
            composition: Some((2, 2)),
        });
        assert_eq!((state.anchor, state.focus), (2, 1));
        assert_eq!(state.composition, None);
    }
}
