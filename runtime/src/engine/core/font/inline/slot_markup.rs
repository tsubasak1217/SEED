// ============================================================
//  font/inline/slot_markup.rs — プレースホルダ記法パーサ（純関数）
//
//  【役割】
//  `TextComponent.content` に書かれた「差し込み口」の記法を、
//  トークン列（通常テキスト／スロット／色区間の終端）へ分解する。
//  **アセットもスロットデータも一切読まない**ので、そのまま単体テストできる。
//
//  【記法（確定仕様）】
//    {image}        … 画像を 1 文字ぶん差し込む
//    {image h=1.4}  … 高さ倍率つき（既存の [icon:] と同じ意味・同じ値域）
//    {color}        … 以降の文字色をスロットの色へ切り替える
//    {/color}       … 色区間の終わり（省略時は行末 = 次の改行の直前まで）
//    {string}       … 文字列を差し込む
//    {num}          … 数値を整数（四捨五入）で差し込む
//    {num.3}        … 数値を小数 3 桁で差し込む
//    {image:1}      … 番号指定（既にあるスロット 1 番を参照する）
//    {num:2.3}      … 番号 + 書式（スロット 2 番を小数 3 桁で）
//    バックスラッシュ + {  … 「{」そのものを描くエスケープ
//
//  【番号の決まり】
//  通し番号カウンタは**記法が出るたびに必ず 1 進む**（明示番号を書いても進む）。
//  番号を書かなければそのときのカウンタ値が添字になる。
//  スロット配列の長さは max(記法の出現数, 明示番号の最大 + 1)。
//  カウンタを常に進めるのは、「途中に明示番号を挟んでも、後続の自動番号が
//  ずれない」ことを保証するため（挟んだ瞬間に以降が総入れ替えになると、
//  本文を少し直しただけで全スロットの値が壊れる）。
//
//  【既知キーワード以外は通常文字（互換の要）】
//  `{` で始まっても image / color / /color / string / num のいずれでもなければ、
//  波括弧ごと**そのまま描く**。既存の本文（プログラム的な `{}` を含む説明文など）
//  の見た目を 1 文字も変えないための規約であり、未閉じ `{` も同じ扱いにする。
//
//  【既存の [icon:] / [img:] 記法との関係】
//  本パーサは波括弧だけを見る。角括弧の記法（`markup.rs`）は、ここで切り出した
//  **通常テキスト区間に対してのみ**後段（`doc.rs`）が適用する。
//  スロットが差し込んだ文字列は再パースしない（値に `[` が入っても壊れない）。
// ============================================================

use crate::engine::components::text_slots::TextSlotKind;

use super::markup::{MAX_HEIGHT_SCALE, MIN_HEIGHT_SCALE};

// ─── 記法の定数（マジックナンバー・マジックストリング禁止）──────

/// トークンの開始文字。
pub const SLOT_OPEN: char = '{';

/// トークンの終了文字。
pub const SLOT_CLOSE: char = '}';

/// エスケープ文字（バックスラッシュ）。直後が `{` のときだけ意味を持つ。
pub const SLOT_ESCAPE: char = '\\';

/// 閉じタグの印（`{/color}` の `/`）。
pub const CLOSE_TAG_MARK: char = '/';

/// 番号指定の区切り（`{image:1}`）。
pub const INDEX_SEPARATOR: char = ':';

/// 小数桁指定の区切り（`{num.3}`）。
pub const DECIMALS_SEPARATOR: char = '.';

/// 高さ倍率オプションの区切り（本体との間に必ず空白を置く。`markup.rs` と同流儀）。
pub const HEIGHT_OPTION_SEPARATOR: &str = " h=";

/// 画像スロットのキーワード。
pub const KEYWORD_IMAGE: &str = "image";
/// 色スロットのキーワード。
pub const KEYWORD_COLOR: &str = "color";
/// 文字列スロットのキーワード。
pub const KEYWORD_STRING: &str = "string";
/// 数値スロットのキーワード。
pub const KEYWORD_NUM: &str = "num";

/// 小数桁の既定値（0 = 整数へ四捨五入）。
pub const DEFAULT_DECIMALS: u32 = 0;

/// 小数桁の上限（f32 の有効桁を超える指定を弾く）。
pub const MAX_DECIMALS: u32 = 9;

/// スロット添字の上限（誤入力で巨大配列を作らないための安全弁）。
pub const MAX_SLOT_INDEX: usize = 255;

// ─── トークン ─────────────────────────────────────────────────

/// 1 つのプレースホルダが要求する「スロットの使い方」。
///
/// 添字・種類・書式はすべて**本文が正典**であり、スロットデータ側は
/// 値だけを持つ（`components::text_slots::TextSlotData`）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SlotSpec {
    /// このプレースホルダが参照するスロットの添字（0 始まり）。
    pub index: usize,
    /// スロットの種類。
    pub kind: TextSlotKind,
    /// 数値スロットの小数桁（それ以外の種類では `DEFAULT_DECIMALS`）。
    pub decimals: u32,
    /// 画像スロットの高さ倍率。None = 既定（アイコンセットの値 → 全体既定）。
    pub height_scale: Option<f32>,
}

/// 本文を分解した 1 トークン。
#[derive(Clone, Debug, PartialEq)]
pub enum SlotToken {
    /// 通常の文字列（`{` のエスケープだけ解除済み。角括弧記法は未処理のまま）。
    Text(String),
    /// プレースホルダ 1 つ。
    Slot(SlotSpec),
    /// 色区間の明示的な終端（`{/color}`）。
    ColorEnd,
}

// ─── パース本体 ────────────────────────────────────────────────

/// 本文にプレースホルダ記法が含まれ得るか（含まないなら解析を丸ごと省く）。
///
/// 入口は `{` とエスケープ用バックスラッシュだけなので、
/// そのどちらも無ければトークン化の結果は「本文そのもの」で確定する。
pub fn may_contain_slot_markup(text: &str) -> bool {
    text.contains(SLOT_OPEN) || text.contains(SLOT_ESCAPE)
}

/// 本文をトークン列へ分解する（純関数）。
///
/// 連続する通常文字は 1 つの `Text` トークンへまとめる
/// （後段が文字列を組み立てるときの再確保を減らすため）。
pub fn parse_slot_markup(text: &str) -> Vec<SlotToken> {
    let mut out: Vec<SlotToken> = Vec::new();
    let mut buf = String::new();
    // 通し番号カウンタ。プレースホルダが出るたびに必ず 1 進む。
    let mut counter: usize = 0;
    let bytes_len = text.len();
    let mut i = 0usize;

    while i < bytes_len {
        let rest = &text[i..];
        let ch = rest.chars().next().expect("i は必ず文字境界を指す");

        // ── エスケープ: バックスラッシュ + `{` は「{」そのもの ──
        if ch == SLOT_ESCAPE {
            let after = &rest[ch.len_utf8()..];
            if let Some(next) = after.chars().next()
                && next == SLOT_OPEN
            {
                buf.push(SLOT_OPEN);
                i += ch.len_utf8() + next.len_utf8();
                continue;
            }
            // `{` 以外が続くバックスラッシュは特別扱いしない
            // （角括弧記法のエスケープ `\[` を壊さないため、そのまま残す）。
            buf.push(ch);
            i += ch.len_utf8();
            continue;
        }

        // ── トークン開始 ──
        if ch == SLOT_OPEN {
            match take_token_body(text, i) {
                Some((body, next_i)) => {
                    match classify_body(body, &mut counter) {
                        Some(token) => {
                            flush_text(&mut buf, &mut out);
                            out.push(token);
                        }
                        // 既知キーワードでない `{...}` は波括弧ごと通常文字。
                        None => buf.push_str(&text[i..next_i]),
                    }
                    i = next_i;
                    continue;
                }
                // 未閉じ `{` → 通常文字。
                None => {
                    buf.push(ch);
                    i += ch.len_utf8();
                    continue;
                }
            }
        }

        // ── それ以外（単独の `}` を含む）は通常文字 ──
        buf.push(ch);
        i += ch.len_utf8();
    }

    flush_text(&mut buf, &mut out);
    out
}

/// 本文からプレースホルダの仕様だけを抜き出す（スロット配列の再マッピング用）。
pub fn slot_specs(text: &str) -> Vec<SlotSpec> {
    if !may_contain_slot_markup(text) {
        return Vec::new();
    }
    parse_slot_markup(text)
        .into_iter()
        .filter_map(|t| match t {
            SlotToken::Slot(spec) => Some(spec),
            _ => None,
        })
        .collect()
}

/// 記法列が必要とするスロット配列の長さ。
///
/// = max(記法の出現数, 明示番号の最大 + 1)。
/// 出現数を下限に入れているのは、通し番号カウンタが常に進む規約
/// （＝明示番号を挟んでも自動番号がずれない）と辻褄を合わせるため。
pub fn required_slot_count(specs: &[SlotSpec]) -> usize {
    let max_index = specs.iter().map(|s| s.index + 1).max().unwrap_or(0);
    specs.len().max(max_index)
}

/// 貯め込んだ通常文字を `Text` トークンとして確定させる（空なら何もしない）。
fn flush_text(buf: &mut String, out: &mut Vec<SlotToken>) {
    if !buf.is_empty() {
        out.push(SlotToken::Text(std::mem::take(buf)));
    }
}

/// `open_at`（`{` の位置）から対応する `}` までの中身を取り出す。
///
/// 戻り値は「中身」と「`}` の次のバイトオフセット」。`}` が無ければ None。
/// 入れ子は考えない（プレースホルダの中身に `}` は現れない）。
fn take_token_body(text: &str, open_at: usize) -> Option<(&str, usize)> {
    let body_start = open_at + SLOT_OPEN.len_utf8();
    let rel_close = text[body_start..].find(SLOT_CLOSE)?;
    let close_at = body_start + rel_close;
    Some((&text[body_start..close_at], close_at + SLOT_CLOSE.len_utf8()))
}

/// `{...}` の中身を記法として解釈する。記法でなければ None（＝通常文字）。
///
/// `counter` はプレースホルダを 1 つ確定させるたびに 1 進める
/// （`{/color}` は値を持たないので進めない）。
fn classify_body(body: &str, counter: &mut usize) -> Option<SlotToken> {
    let body = body.trim();
    if body.is_empty() {
        return None;
    }

    // ── 閉じタグ（`{/color}` のみ）──
    if let Some(rest) = body.strip_prefix(CLOSE_TAG_MARK) {
        return (rest.trim() == KEYWORD_COLOR).then_some(SlotToken::ColorEnd);
    }

    // ── 高さ倍率オプションを切り離す（画像スロット専用）──
    let (head, height_scale) = split_height_option(body);

    // ── キーワード（先頭の英字並び）と、その後ろの修飾子へ割る ──
    let keyword_len = head
        .char_indices()
        .find(|(_, c)| !c.is_ascii_alphabetic())
        .map(|(i, _)| i)
        .unwrap_or(head.len());
    let (keyword, suffix) = head.split_at(keyword_len);
    let kind = match keyword {
        KEYWORD_IMAGE => TextSlotKind::Image,
        KEYWORD_COLOR => TextSlotKind::Color,
        KEYWORD_STRING => TextSlotKind::String,
        KEYWORD_NUM => TextSlotKind::Num,
        // 既知キーワード以外は記法ではない（＝通常文字として描く）。
        _ => return None,
    };

    // ── 修飾子（`:番号` と `.小数桁`）を読む ──
    let (index, decimals) = parse_suffix(suffix)?;

    // ── 種類ごとの妥当性（書けない修飾子は「記法でない」＝通常文字）──
    // 誤記をこっそり無視するより、そのまま画面に出したほうが原因が分かる。
    if decimals.is_some() && kind != TextSlotKind::Num {
        return None;
    }
    if height_scale.is_some() && kind != TextSlotKind::Image {
        return None;
    }

    // 添字は「明示指定 > 通し番号」。カウンタは**常に**進める。
    let resolved_index = index.unwrap_or(*counter);
    if resolved_index > MAX_SLOT_INDEX {
        return None;
    }
    *counter += 1;

    Some(SlotToken::Slot(SlotSpec {
        index: resolved_index,
        kind,
        decimals: decimals.unwrap_or(DEFAULT_DECIMALS),
        height_scale,
    }))
}

/// キーワードの後ろに続く修飾子を読む。
///
/// 受け付ける形は 4 通りだけ:
///   ""（無し） / ":番号" / ".小数桁" / ":番号.小数桁"
/// それ以外（余計な文字・空の数字・桁上限超え）は None を返し、
/// 呼び出し側が「記法ではない」と判断する。
fn parse_suffix(suffix: &str) -> Option<(Option<usize>, Option<u32>)> {
    if suffix.is_empty() {
        return Some((None, None));
    }
    // 番号部と小数桁部へ割る。
    let (index_part, decimals_part) = match suffix.strip_prefix(INDEX_SEPARATOR) {
        Some(rest) => match rest.split_once(DECIMALS_SEPARATOR) {
            Some((idx, dec)) => (Some(idx), Some(dec)),
            None => (Some(rest), None),
        },
        // `:` が無いなら残りは小数桁指定のみ（`.3`）でなければならない。
        None => match suffix.strip_prefix(DECIMALS_SEPARATOR) {
            Some(dec) => (None, Some(dec)),
            None => return None,
        },
    };

    let index = match index_part {
        Some(s) if !s.is_empty() && s.chars().all(|c| c.is_ascii_digit()) => {
            Some(s.parse::<usize>().ok()?)
        }
        Some(_) => return None,
        None => None,
    };
    let decimals = match decimals_part {
        Some(s) if !s.is_empty() && s.chars().all(|c| c.is_ascii_digit()) => {
            let d = s.parse::<u32>().ok()?;
            if d > MAX_DECIMALS {
                return None;
            }
            Some(d)
        }
        Some(_) => return None,
        None => None,
    };
    Some((index, decimals))
}

/// 本体と高さ倍率オプションを切り分ける（`markup.rs::split_height_option` と同規則）。
///
/// 区切りは**最後の** " h=" を使い、数値としてパースできなければ
/// オプションとみなさず全体を本体として扱う。
/// 倍率は MIN_HEIGHT_SCALE..MAX_HEIGHT_SCALE へ丸める。
fn split_height_option(body: &str) -> (&str, Option<f32>) {
    if let Some(sep) = body.rfind(HEIGHT_OPTION_SEPARATOR) {
        let value_str = body[sep + HEIGHT_OPTION_SEPARATOR.len()..].trim();
        if let Ok(v) = value_str.parse::<f32>()
            && v.is_finite()
        {
            return (
                body[..sep].trim(),
                Some(v.clamp(MIN_HEIGHT_SCALE, MAX_HEIGHT_SCALE)),
            );
        }
    }
    (body.trim(), None)
}

// ============================================================
//  単体テスト（アセット不要の純関数）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 文字列トークンだけを連結して取り出すヘルパ。
    fn text_of(tokens: &[SlotToken]) -> String {
        tokens
            .iter()
            .filter_map(|t| match t {
                SlotToken::Text(s) => Some(s.as_str()),
                _ => None,
            })
            .collect()
    }

    /// 期待するスロット仕様を短く書くためのヘルパ。
    fn spec(index: usize, kind: TextSlotKind, decimals: u32, h: Option<f32>) -> SlotSpec {
        SlotSpec { index, kind, decimals, height_scale: h }
    }

    /// 記法が無い本文はそのまま 1 つの Text トークンになる。
    #[test]
    fn plain_text_is_single_token() {
        let t = parse_slot_markup("こんにちは world");
        assert_eq!(t, vec![SlotToken::Text("こんにちは world".into())]);
        assert!(slot_specs("こんにちは world").is_empty());
    }

    /// 4 種類のキーワードがそれぞれの種類になり、通し番号が順に振られる。
    #[test]
    fn four_keywords_are_numbered_in_order() {
        let s = slot_specs("{image}{color}{string}{num}");
        assert_eq!(
            s,
            vec![
                spec(0, TextSlotKind::Image, DEFAULT_DECIMALS, None),
                spec(1, TextSlotKind::Color, DEFAULT_DECIMALS, None),
                spec(2, TextSlotKind::String, DEFAULT_DECIMALS, None),
                spec(3, TextSlotKind::Num, DEFAULT_DECIMALS, None),
            ]
        );
    }

    /// 小数桁指定が読める（数値スロットのみ）。
    #[test]
    fn decimals_are_parsed() {
        assert_eq!(
            slot_specs("{num.3}"),
            vec![spec(0, TextSlotKind::Num, 3, None)]
        );
        // 数値以外に小数桁を付けたら記法ではない（そのまま描く）。
        assert!(slot_specs("{string.3}").is_empty());
        assert_eq!(text_of(&parse_slot_markup("{string.3}")), "{string.3}");
        // 桁数の上限を超えたら記法ではない。
        assert!(slot_specs("{num.99}").is_empty());
    }

    /// 高さ倍率が読める（画像スロットのみ）。値域も丸められる。
    #[test]
    fn height_option_is_parsed_and_clamped() {
        assert_eq!(
            slot_specs("{image h=1.4}"),
            vec![spec(0, TextSlotKind::Image, DEFAULT_DECIMALS, Some(1.4))]
        );
        assert_eq!(
            slot_specs("{image h=999}"),
            vec![spec(0, TextSlotKind::Image, DEFAULT_DECIMALS, Some(MAX_HEIGHT_SCALE))]
        );
        // 画像以外に h= を付けたら記法ではない。
        assert!(slot_specs("{color h=2}").is_empty());
    }

    /// 番号指定が読め、通し番号カウンタは明示番号でも進む。
    #[test]
    fn explicit_index_still_advances_the_counter() {
        // 1 つ目は明示 5、2 つ目は自動なのでカウンタ値 1 になる。
        let s = slot_specs("{num:5}{num}");
        assert_eq!(s[0].index, 5);
        assert_eq!(s[1].index, 1, "明示番号を挟んでも自動番号はずれない");
        // 配列長は max(出現数 2, 明示最大 5 + 1) = 6。
        assert_eq!(required_slot_count(&s), 6);
    }

    /// 番号と小数桁の同時指定が読める。
    #[test]
    fn index_and_decimals_together() {
        assert_eq!(
            slot_specs("{num:2.3}"),
            vec![spec(2, TextSlotKind::Num, 3, None)]
        );
    }

    /// 既知キーワード以外の波括弧は通常文字として残る（既存本文の互換）。
    #[test]
    fn unknown_keyword_is_plain_text() {
        let src = "JSON は {\"a\": 1} の形、{note} も {} も普通の文字";
        let t = parse_slot_markup(src);
        assert_eq!(text_of(&t), src);
        assert_eq!(t.len(), 1, "スロットは 1 つも出ない");
    }

    /// 未閉じの `{` は通常文字（以降が消えない）。
    #[test]
    fn unclosed_brace_is_plain_text() {
        let t = parse_slot_markup("残り{num");
        assert_eq!(text_of(&t), "残り{num");
        assert!(slot_specs("残り{num").is_empty());
    }

    /// エスケープ（バックスラッシュ + `{`）は「{」そのものになる。
    #[test]
    fn escaped_open_brace_is_literal() {
        let t = parse_slot_markup(r"\{num}");
        assert_eq!(text_of(&t), "{num}");
        assert!(slot_specs(r"\{num}").is_empty());
    }

    /// 角括弧記法のエスケープ（バックスラッシュ + `[`）は壊さずに残す。
    #[test]
    fn bracket_escape_is_preserved() {
        let t = parse_slot_markup(r"\[icon:a]");
        assert_eq!(text_of(&t), r"\[icon:a]");
    }

    /// 閉じタグは `{/color}` だけが有効。
    #[test]
    fn close_tag_only_for_color() {
        assert_eq!(
            parse_slot_markup("{/color}"),
            vec![SlotToken::ColorEnd]
        );
        assert_eq!(text_of(&parse_slot_markup("{/num}")), "{/num}");
    }

    /// 前後の空白は取り除かれる。
    #[test]
    fn body_is_trimmed() {
        assert_eq!(
            slot_specs("{ num.2 }"),
            vec![spec(0, TextSlotKind::Num, 2, None)]
        );
    }

    /// 壊れた修飾子は記法ではない（通常文字として描く）。
    #[test]
    fn malformed_suffix_is_plain_text() {
        for src in ["{num:}", "{num.}", "{num:x}", "{numx}", "{num:1:2}"] {
            assert!(slot_specs(src).is_empty(), "{src} は記法ではない");
            assert_eq!(text_of(&parse_slot_markup(src)), src);
        }
    }

    /// 添字の上限を超えた指定は記法ではない（巨大配列を作らせない）。
    #[test]
    fn index_beyond_limit_is_plain_text() {
        let src = format!("{{num:{}}}", MAX_SLOT_INDEX + 1);
        assert!(slot_specs(&src).is_empty());
    }

    /// 本文とスロットが混ざっても、通常文字は欠けずに残る。
    #[test]
    fn text_around_slots_is_preserved() {
        let t = parse_slot_markup("所持金 {num} 円");
        assert_eq!(text_of(&t), "所持金  円");
        assert_eq!(t.len(), 3);
    }
}
