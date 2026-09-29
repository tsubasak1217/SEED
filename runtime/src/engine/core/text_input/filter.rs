// ============================================================
//  text_input/filter.rs — 入力欄の決まり（数字だけ・最大の長さ・貼り付けの禁止）を新しい状態へ当てる【純関数・単体テスト付き】
//
//  【いつ当てるか】（docs/ui_text_input.md §4）
//  どのプラットフォームからの変化（Windows の確定・変換中・キー、Android の IME の状態の写し、スクリプトの SetText、
//  IPC の注入）も、いったん「変化の後の状態の案」にしてからここを通す。決まりで案を変えたら、呼び出し側（session.rs）が
//  Android の IME へ状態を送り返す（IME の本文もエンジンと同じにする。数字のキーボードでも IME の側でかなへ切り替えられるので、
//  数字だけの欄はエンジンの側でも数字以外を捨てる。§3.8.4 の注意）。
//
//  【決まり】
//    - 数字だけ（TextInputKind::Number）: 全角の数字（０〜９）は半角へ直し、ほかの文字は捨てる。変換中の区間は持たない
//      （捨てた・直した文字があれば区間ごと確定扱い）。選択・区間の添字は残った文字へ付け替える
//    - 最大の長さ（書記素の数）: 変換中は超えてよい（Flutter の truncateAfterCompositionEnds）。変換中でない案が超えたら、
//      前の状態が既に最大なら変化を取り消し（前の状態のまま）、そうでなければ先頭から最大の長さまでに切り詰める
//      （Flutter の LengthLimitingTextInputFormatter と同じ）
//    - 貼り付けの禁止（Android の IME の状態の写しだけ）: 変換中の区間に触れない一度の挿入が
//      `PASTE_MIN_GRAPHEMES` 書記素以上なら貼り付けとみなして取り消す（IME のクリップボードの候補・音声入力・
//      次の単語の予測もこれに当たる。GameActivity には長押しの貼り付けのメニューが無いので、IME の commitText が唯一の経路）
// ============================================================

use super::config::{TextInputConfig, TextInputKind};
use super::edit_state::TextEditState;
use super::indices::{floor_char_boundary, grapheme_count, grapheme_prefix_bytes};

/// 貼り付けとみなす一度の挿入の最小の書記素の数（変換中の区間に触れない挿入で数える）。
/// 1 文字ずつの確定（数字・記号・英字の直接の入力）は通し、2 文字以上を一度に入れたら貼り付けとみなす。
pub const PASTE_MIN_GRAPHEMES: usize = 2;

/// 全角の数字の最初（'０' = U+FF10）。
const FULLWIDTH_DIGIT_ZERO: u32 = 0xFF10;
/// 全角の数字の最後（'９' = U+FF19）。
const FULLWIDTH_DIGIT_NINE: u32 = 0xFF19;
/// 半角の数字の最初（'0'）。
const ASCII_DIGIT_ZERO: u32 = '0' as u32;

/// 変化がどこから来たか（貼り付けの見分けに使う）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EditOrigin {
    /// Android の IME が持つ状態の丸ごとの写し（差分から挿入を見分ける）。
    PlatformState,
    /// それ以外（Windows の確定・変換中・キー、スクリプト、IPC の注入）。貼り付けはキーの側で止める。
    Direct,
}

/// 決まりを当てた結果。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FilterOutcome {
    /// 当てた後の状態。
    pub state: TextEditState,
    /// 案から変えたか（変えたら Android の IME へ送り返す）。
    pub adjusted: bool,
    /// 貼り付けとみなして取り消したか（スクリプトへ知らせる）。
    pub paste_blocked: bool,
}

/// 決まりを当てる。
///
/// # 引数
/// * `config`   - 入力欄の設定
/// * `previous` - 変化の前の状態（決まりを満たしている）
/// * `proposed` - 変化の後の状態の案
/// * `origin`   - 変化の出どころ
pub fn apply_rules(
    config: &TextInputConfig,
    previous: &TextEditState,
    proposed: TextEditState,
    origin: EditOrigin,
) -> FilterOutcome {
    // 貼り付けの禁止（IME の状態の写しだけ。取り消すなら前の状態のまま）
    if !config.allow_paste && origin == EditOrigin::PlatformState && looks_like_paste(previous, &proposed) {
        return FilterOutcome { state: previous.clone(), adjusted: true, paste_blocked: true };
    }
    let mut state = proposed;
    let mut adjusted = false;
    if config.kind == TextInputKind::Number {
        adjusted |= keep_digits_only(&mut state);
    }
    if let Some(max) = config.max_length {
        match limit_length(previous, &mut state, max) {
            LengthOutcome::Unchanged => {}
            LengthOutcome::Truncated => adjusted = true,
            LengthOutcome::Rejected => {
                return FilterOutcome { state: previous.clone(), adjusted: true, paste_blocked: false };
            }
        }
    }
    FilterOutcome { state, adjusted, paste_blocked: false }
}

/// 数字だけにする（全角の数字は半角へ。ほかは捨てる）。変えたら true。変換中の区間は常に外す。
fn keep_digits_only(state: &mut TextEditState) -> bool {
    let had_composition = state.composition.take().is_some();
    let mut filtered = String::with_capacity(state.text.len());
    // 元のバイト位置 → 新しいバイト位置（選択の付け替え用。元の各文字の先頭と末尾）
    let mut map_anchor = None;
    let mut map_focus = None;
    for (byte, ch) in state.text.char_indices() {
        if map_anchor.is_none() && state.anchor <= byte {
            map_anchor = Some(filtered.len());
        }
        if map_focus.is_none() && state.focus <= byte {
            map_focus = Some(filtered.len());
        }
        if let Some(digit) = ascii_digit(ch) {
            filtered.push(digit);
        }
    }
    let changed = filtered != state.text;
    let end = filtered.len();
    state.anchor = map_anchor.unwrap_or(end);
    state.focus = map_focus.unwrap_or(end);
    state.text = filtered;
    changed || had_composition
}

/// 数字なら半角の数字を返す（全角の数字は半角へ直す）。数字でなければ None。
fn ascii_digit(ch: char) -> Option<char> {
    if ch.is_ascii_digit() {
        return Some(ch);
    }
    let code = ch as u32;
    if (FULLWIDTH_DIGIT_ZERO..=FULLWIDTH_DIGIT_NINE).contains(&code) {
        return char::from_u32(ASCII_DIGIT_ZERO + (code - FULLWIDTH_DIGIT_ZERO));
    }
    None
}

/// 最大の長さの当て方の結果。
enum LengthOutcome {
    /// 収まっている（または変換中なので見ない）。
    Unchanged,
    /// 切り詰めた。
    Truncated,
    /// 前の状態が既に最大なので変化を取り消す。
    Rejected,
}

/// 最大の長さを当てる（変換中は見ない）。
fn limit_length(previous: &TextEditState, state: &mut TextEditState, max: usize) -> LengthOutcome {
    if state.is_composing() || grapheme_count(&state.text) <= max {
        return LengthOutcome::Unchanged;
    }
    if !previous.is_composing() && grapheme_count(&previous.text) == max {
        return LengthOutcome::Rejected;
    }
    let keep = grapheme_prefix_bytes(&state.text, max);
    state.text.truncate(keep);
    state.anchor = floor_char_boundary(&state.text, state.anchor.min(keep));
    state.focus = floor_char_boundary(&state.text, state.focus.min(keep));
    LengthOutcome::Truncated
}

/// 前と後ろの状態の差分が「変換中の区間に触れない、一度の大きな挿入」か（貼り付けの見分け）。
fn looks_like_paste(previous: &TextEditState, proposed: &TextEditState) -> bool {
    let old = previous.text.as_str();
    let new = proposed.text.as_str();
    if new.len() <= old.len() {
        // 増えていなければ挿入ではない（削除・置き換えで短くなった）
        return false;
    }
    // 共通の先頭と末尾（文字の境界）を除いた部分が、置き換えられた範囲と入った文字列
    let prefix = common_prefix_len(old, new);
    let suffix = common_suffix_len(&old[prefix..], &new[prefix..]);
    let replaced = prefix..old.len() - suffix;
    let inserted = &new[prefix..new.len() - suffix];
    // 変換中の区間に触れる（区間の中・境）変化は変換の続き・確定なので貼り付けではない
    if let Some(composition) = &previous.composition {
        if replaced.start <= composition.end && composition.start <= replaced.end {
            return false;
        }
    }
    if proposed.is_composing() {
        // 新しく変換を始めた（区間の中の文字）は貼り付けではない
        if let Some(composition) = &proposed.composition {
            if composition.start <= prefix && new.len() - suffix <= composition.end {
                return false;
            }
        }
    }
    grapheme_count(inserted) >= PASTE_MIN_GRAPHEMES
}

/// 共通の先頭のバイト数（文字の境界）。
fn common_prefix_len(a: &str, b: &str) -> usize {
    let mut len = 0;
    for (ca, cb) in a.chars().zip(b.chars()) {
        if ca != cb {
            break;
        }
        len += ca.len_utf8();
    }
    len
}

/// 共通の末尾のバイト数（文字の境界）。
fn common_suffix_len(a: &str, b: &str) -> usize {
    let mut len = 0;
    for (ca, cb) in a.chars().rev().zip(b.chars().rev()) {
        if ca != cb {
            break;
        }
        len += ca.len_utf8();
    }
    len
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::text_input::config::TextInputAction;

    fn config(kind: TextInputKind, max_length: Option<usize>, allow_paste: bool) -> TextInputConfig {
        TextInputConfig { kind, action: TextInputAction::Done, max_length, allow_paste, allow_copy: true }
    }

    /// 数字だけ: 全角は半角へ、ほかは捨て、カーソルは残った文字へ付け替える。変換中の区間は外す。
    #[test]
    fn number_keeps_ascii_digits() {
        let rules = config(TextInputKind::Number, None, true);
        let previous = TextEditState::with_caret_at_end("1");
        let mut proposed = TextEditState::with_caret_at_end("1a２b3");
        proposed.composition = Some(1..2);
        let outcome = apply_rules(&rules, &previous, proposed, EditOrigin::Direct);
        assert_eq!(outcome.state.text, "123");
        assert_eq!(outcome.state.focus, 3);
        assert_eq!(outcome.state.composition, None);
        assert!(outcome.adjusted);

        // 数字だけの案は変えない
        let ok = apply_rules(&rules, &previous, TextEditState::with_caret_at_end("42"), EditOrigin::Direct);
        assert!(!ok.adjusted);
        assert_eq!(ok.state.text, "42");
    }

    /// カーソルが途中のときの付け替え（捨てた文字の後ろのカーソルは前へ詰める）。
    #[test]
    fn number_remaps_caret_in_middle() {
        let rules = config(TextInputKind::Number, None, true);
        let previous = TextEditState::with_caret_at_end("12");
        let proposed = TextEditState::new("1あ2", 4, 4); // 「あ」の後ろ
        let outcome = apply_rules(&rules, &previous, proposed, EditOrigin::Direct);
        assert_eq!(outcome.state.text, "12");
        assert_eq!(outcome.state.focus, 1);
    }

    /// 最大の長さ: 変換中は超えてよく、確定した案は切り詰める。前が既に最大なら取り消す。
    #[test]
    fn max_length_truncates_after_composition() {
        let rules = config(TextInputKind::Text, Some(3), true);
        let previous = TextEditState::with_caret_at_end("ab");
        let mut composing = TextEditState::with_caret_at_end("abかんじ");
        composing.composition = Some(2..11);
        let during = apply_rules(&rules, &previous, composing.clone(), EditOrigin::Direct);
        assert!(!during.adjusted, "変換中は超えてよい");

        let committed = TextEditState::with_caret_at_end("ab漢字");
        let after = apply_rules(&rules, &during.state, committed, EditOrigin::Direct);
        assert_eq!(after.state.text, "ab漢");
        assert_eq!(after.state.focus, "ab漢".len());
        assert!(after.adjusted);

        let full = TextEditState::with_caret_at_end("abc");
        let rejected = apply_rules(&rules, &full, TextEditState::with_caret_at_end("abcd"), EditOrigin::Direct);
        assert_eq!(rejected.state, full, "最大のときの挿入は取り消す");
        assert!(rejected.adjusted);
    }

    /// 最大の長さは書記素で数える（合字の絵文字は 1 つ）。
    #[test]
    fn max_length_counts_graphemes() {
        let rules = config(TextInputKind::Text, Some(2), true);
        let previous = TextEditState::with_caret_at_end("a");
        let outcome = apply_rules(&rules, &previous, TextEditState::with_caret_at_end("a👨‍👩‍👧"), EditOrigin::Direct);
        assert!(!outcome.adjusted);
    }

    /// 貼り付けの禁止: 変換中の区間に触れない 2 文字以上の挿入は取り消す。1 文字・変換の確定・変換中は通す。
    #[test]
    fn paste_heuristic_on_platform_state() {
        let rules = config(TextInputKind::Text, None, false);
        let previous = TextEditState::with_caret_at_end("おは");
        // クリップボードの候補から「ようございます」を一度に入れた
        let pasted = TextEditState::with_caret_at_end("おはようございます");
        let blocked = apply_rules(&rules, &previous, pasted, EditOrigin::PlatformState);
        assert!(blocked.paste_blocked);
        assert_eq!(blocked.state, previous);

        // 1 文字の確定は通す
        let one = apply_rules(&rules, &previous, TextEditState::with_caret_at_end("おはよ"), EditOrigin::PlatformState);
        assert!(!one.paste_blocked);

        // 変換中の区間の差し替え（かんじ → 漢字の確定）は通す
        let mut composing = TextEditState::with_caret_at_end("おはかんじ");
        composing.composition = Some(6..15);
        let committed = TextEditState::with_caret_at_end("おは漢字です");
        let ok = apply_rules(&rules, &composing, committed, EditOrigin::PlatformState);
        assert!(!ok.paste_blocked);

        // 新しく変換を始めた（区間の中の 3 文字）は通す
        let mut started = TextEditState::with_caret_at_end("おはかんじ");
        started.composition = Some(6..15);
        let ok = apply_rules(&rules, &previous, started, EditOrigin::PlatformState);
        assert!(!ok.paste_blocked);

        // 許可している欄・キーの経路（Direct）では見ない
        let allowed = config(TextInputKind::Text, None, true);
        let pasted = TextEditState::with_caret_at_end("おはようございます");
        assert!(!apply_rules(&allowed, &previous, pasted.clone(), EditOrigin::PlatformState).paste_blocked);
        assert!(!apply_rules(&rules, &previous, pasted, EditOrigin::Direct).paste_blocked);
    }
}
