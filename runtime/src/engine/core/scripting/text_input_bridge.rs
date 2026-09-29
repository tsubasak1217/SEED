// ============================================================
//  text_input_bridge.rs — スクリプトの文字入力 API（SEED.TextInput）の FFI（W2-6a。正典は docs/ui_text_input.md §7）
//
//  【役割】
//  C# の `SEED.TextInput`（scripting/src/Api/TextInput.cs）が呼ぶ FFI 関数 1 本（op で分ける）。中身（場・状態・キーボード）は
//  エンジンの engine/core/text_input（hub.rs）が持ち、ここは「数値と文字列の受け渡しの約束」だけを受け持つ。
//  添字はスクリプトの string と同じ UTF-16 の単位（エンジンの中の UTF-8 のバイトとの変換はここで行う）。
//
//  【ffi_text_input(op, session, ints, intsLen, text, textLen, outInts, outIntsCap, outText, outTextCap) → i32】
//  （op の番号は C# 側 ScriptHost.TextInputOp* と一致させる。-1 = 知らない op・場が今の場でない・読めない値）
//    op 0 = Begin        … ints [種類, アクション, 最大の長さ(0 以下=なし), 旗, 選択の起点, 動く端]（選択は -1 で末尾）、text = 初めの本文。
//                          新しい場の番号（1 以上）
//    op 1 = End          … 場を終える。0
//    op 2 = GetState     … outInts [選択の起点, 動く端, 変換の始め, 変換の終わり(無ければ -1,-1), 版(下位 31 ビット)]、
//                          outText = 本文（UTF-8）。戻り値は本文のバイト数（outTextCap より大きければ本文は書かない＝入れ物を広げて呼び直す）
//    op 3 = SetText      … ints [選択の起点, 動く端]（-1 で末尾）、text = 本文。0
//    op 4 = SetSelection … ints [起点, 動く端]。0
//    op 5 = TakeEvents   … outInts に [番号, 添え] の組を古い順に書く（outIntsCap / 2 組まで）。書いた組の数
//    op 6 = ShowKeyboard / op 7 = HideKeyboard … 0
//    op 8 = SetCaretRect … ints [x, y, 幅, 高さ]（画面の画素。PC の IME の候補窓が避ける）。0
//    op 9 = Keyboard     … outInts [見えているか(1/0), 高さ(画面の画素。見えていなければ 0)]。0（session は使わない）
//    op 10 = Active      … 今の場の番号（無ければ 0。session は使わない）
//  エンジンのスレッド（スクリプトのフェーズ）から呼ぶ。
// ============================================================

use crate::engine::core::text_input::indices::utf16_to_byte;
use crate::engine::core::text_input::{self, TextEditState, TextInputConfig, TextInputHub};

// ── op（C# 側 ScriptHost.TextInputOp* と一致させる）──
/// 場を始める。
pub const TEXT_OP_BEGIN: i32 = 0;
/// 場を終える。
pub const TEXT_OP_END: i32 = 1;
/// 状態を読む。
pub const TEXT_OP_GET_STATE: i32 = 2;
/// 本文と選択を置く。
pub const TEXT_OP_SET_TEXT: i32 = 3;
/// 選択を置く。
pub const TEXT_OP_SET_SELECTION: i32 = 4;
/// 出来事を取り出す。
pub const TEXT_OP_TAKE_EVENTS: i32 = 5;
/// キーボードを出す。
pub const TEXT_OP_SHOW_KEYBOARD: i32 = 6;
/// キーボードを隠す。
pub const TEXT_OP_HIDE_KEYBOARD: i32 = 7;
/// 候補窓が避ける矩形を置く。
pub const TEXT_OP_SET_CARET_RECT: i32 = 8;
/// キーボードの状態を読む。
pub const TEXT_OP_KEYBOARD: i32 = 9;
/// 今の場の番号。
pub const TEXT_OP_ACTIVE: i32 = 10;

/// 受け付けた（C# 側 ScriptHost.TextInputResultOk と一致させる）。
pub const TEXT_RESULT_OK: i32 = 0;
/// 知らない op・今の場でない・読めない値（C# 側 ScriptHost.TextInputResultInvalid と一致させる）。
pub const TEXT_RESULT_INVALID: i32 = -1;
/// 場が無い（Active の答え）。
pub const TEXT_NO_SESSION: i32 = 0;
/// 添字の「末尾」「無い」。
pub const TEXT_INDEX_NONE: i32 = -1;

// ── ints の並び ──
/// Begin の ints の要素の数（種類・アクション・最大の長さ・旗・選択の起点・動く端）。
const BEGIN_INTS: usize = 6;
/// Begin の ints の番号: 種類。
const BEGIN_KIND: usize = 0;
/// Begin の ints の番号: アクション。
const BEGIN_ACTION: usize = 1;
/// Begin の ints の番号: 最大の長さ。
const BEGIN_MAX_LENGTH: usize = 2;
/// Begin の ints の番号: 旗。
const BEGIN_FLAGS: usize = 3;
/// Begin の ints の番号: 選択の起点。
const BEGIN_SELECTION_START: usize = 4;
/// Begin の ints の番号: 選択の動く端。
const BEGIN_SELECTION_END: usize = 5;
/// 選択の ints の要素の数（起点・動く端）。
const SELECTION_INTS: usize = 2;
/// GetState の outInts の要素の数（選択 2・変換中の区間 2・版 1）。
pub const STATE_OUT_INTS: usize = 5;
/// 候補窓の矩形の要素の数。
const RECT_INTS: usize = 4;
/// Keyboard の outInts の要素の数。
pub const KEYBOARD_OUT_INTS: usize = 2;
/// 出来事 1 つの outInts の要素の数（番号・添え）。
pub const EVENT_INTS: usize = 2;
/// 版を i32 で返すときに残すビット（負にしない）。
const REVISION_MASK: u64 = i32::MAX as u64;

/// ポインタと長さから i32 の並びを読む（null・負の長さは空）。
///
/// # Safety
/// `ptr` は null か、`len` 個以上の i32 を読める領域を指していること。
unsafe fn ints_from<'a>(ptr: *const i32, len: i32) -> &'a [i32] {
    match usize::try_from(len) {
        Ok(n) if n > 0 && !ptr.is_null() => unsafe { std::slice::from_raw_parts(ptr, n) },
        _ => &[],
    }
}

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

/// 書き出し先（null・負の容量は容量 0）。
///
/// # Safety
/// `ptr` は null か、`cap` 個以上を書ける領域を指していること。
unsafe fn out_slice<'a, T>(ptr: *mut T, cap: i32) -> &'a mut [T] {
    match usize::try_from(cap) {
        Ok(n) if n > 0 && !ptr.is_null() => unsafe { std::slice::from_raw_parts_mut(ptr, n) },
        _ => &mut [],
    }
}

/// スクリプトの UTF-16 の添字（-1 = 末尾）を本文のバイト位置へ。
fn byte_index(text: &str, utf16_index: i32) -> usize {
    match usize::try_from(utf16_index) {
        Ok(index) => utf16_to_byte(text, index),
        Err(_) => text.len(),
    }
}

/// スクリプトの文字入力（SEED.TextInput の入口）。
///
/// # Safety
/// ポインタはそれぞれ null か、組の長さ・容量の分だけ読める（書ける）領域を指していること。
#[allow(clippy::too_many_arguments)] // C# の関数ポインタの形そのまま（op で分ける 1 本の入口）
pub(super) unsafe extern "system" fn ffi_text_input(
    op: i32,
    session: i32,
    ints: *const i32,
    ints_len: i32,
    text: *const u8,
    text_len: i32,
    out_ints: *mut i32,
    out_ints_cap: i32,
    out_text: *mut u8,
    out_text_cap: i32,
) -> i32 {
    // SAFETY: 呼び出し側（C#）が長さ・容量つきで渡した領域だけを読む・書く
    let (ints, text, out_ints, out_text) = unsafe {
        (
            ints_from(ints, ints_len),
            text_from(text, text_len),
            out_slice(out_ints, out_ints_cap),
            out_slice(out_text, out_text_cap),
        )
    };
    let id = u32::try_from(session).unwrap_or(0);
    text_input::with_hub(|hub| dispatch(hub, op, id, ints, text, out_ints, out_text))
}

/// op ごとの処理（ハブを借りた中で）。
fn dispatch(hub: &mut TextInputHub, op: i32, id: u32, ints: &[i32], text: &str, out_ints: &mut [i32], out_text: &mut [u8]) -> i32 {
    match op {
        TEXT_OP_BEGIN => begin(hub, ints, text),
        TEXT_OP_END => ok_or_invalid(hub.end(id)),
        TEXT_OP_GET_STATE => get_state(hub, id, out_ints, out_text),
        TEXT_OP_SET_TEXT => {
            let Some(session) = hub.session_mut(id) else { return TEXT_RESULT_INVALID };
            let [start, end] = selection_ints(ints);
            session.set_text(text, byte_index(text, start), byte_index(text, end));
            TEXT_RESULT_OK
        }
        TEXT_OP_SET_SELECTION => {
            let Some(session) = hub.session_mut(id) else { return TEXT_RESULT_INVALID };
            let [start, end] = selection_ints(ints);
            let current = session.state().text.clone();
            session.set_selection(byte_index(&current, start), byte_index(&current, end));
            TEXT_RESULT_OK
        }
        TEXT_OP_TAKE_EVENTS => {
            let Some(session) = hub.session_mut(id) else { return TEXT_RESULT_INVALID };
            let events = session.take_events(out_ints.len() / EVENT_INTS);
            for (slot, event) in out_ints.chunks_exact_mut(EVENT_INTS).zip(&events) {
                slot[0] = event.code();
                slot[1] = event.argument();
            }
            events.len() as i32
        }
        TEXT_OP_SHOW_KEYBOARD => ok_or_invalid(hub.request_show(id)),
        TEXT_OP_HIDE_KEYBOARD => ok_or_invalid(hub.request_hide(id)),
        TEXT_OP_SET_CARET_RECT => match ints.get(..RECT_INTS) {
            Some(&[x, y, width, height]) => ok_or_invalid(hub.set_caret_rect(id, [x, y, width, height])),
            _ => TEXT_RESULT_INVALID,
        },
        TEXT_OP_KEYBOARD => {
            let keyboard = hub.keyboard();
            if let Some(slot) = out_ints.get_mut(..KEYBOARD_OUT_INTS) {
                slot[0] = i32::from(keyboard.effective_height() > 0);
                slot[1] = keyboard.effective_height();
            }
            TEXT_RESULT_OK
        }
        TEXT_OP_ACTIVE => hub.active_id().map_or(TEXT_NO_SESSION, |active| active as i32),
        _ => TEXT_RESULT_INVALID,
    }
}

/// Begin: 設定と初めの本文から場を始める。
fn begin(hub: &mut TextInputHub, ints: &[i32], text: &str) -> i32 {
    let Some(values) = ints.get(..BEGIN_INTS) else { return TEXT_RESULT_INVALID };
    let config = TextInputConfig::from_ffi(
        values[BEGIN_KIND],
        values[BEGIN_ACTION],
        values[BEGIN_MAX_LENGTH],
        values[BEGIN_FLAGS],
    );
    let initial = TextEditState::new(
        text,
        byte_index(text, values[BEGIN_SELECTION_START]),
        byte_index(text, values[BEGIN_SELECTION_END]),
    );
    i32::try_from(hub.begin(config, initial)).unwrap_or(TEXT_RESULT_INVALID)
}

/// GetState: 選択・変換中の区間・版と本文を書く。
fn get_state(hub: &mut TextInputHub, id: u32, out_ints: &mut [i32], out_text: &mut [u8]) -> i32 {
    let Some(session) = hub.session_mut(id) else { return TEXT_RESULT_INVALID };
    let state = session.state().to_utf16();
    if let Some(slot) = out_ints.get_mut(..STATE_OUT_INTS) {
        let (comp_start, comp_end) =
            state.composition.map_or((TEXT_INDEX_NONE, TEXT_INDEX_NONE), |(s, e)| (clamp_i32(s), clamp_i32(e)));
        slot.copy_from_slice(&[
            clamp_i32(state.selection_start),
            clamp_i32(state.selection_end),
            comp_start,
            comp_end,
            (session.revision() & REVISION_MASK) as i32,
        ]);
    }
    let bytes = state.text.as_bytes();
    if let Some(slot) = out_text.get_mut(..bytes.len()) {
        slot.copy_from_slice(bytes);
    }
    i32::try_from(bytes.len()).unwrap_or(i32::MAX)
}

/// 選択の ints（足りなければ末尾）。
fn selection_ints(ints: &[i32]) -> [i32; SELECTION_INTS] {
    [
        ints.first().copied().unwrap_or(TEXT_INDEX_NONE),
        ints.get(1).copied().unwrap_or(TEXT_INDEX_NONE),
    ]
}

/// 真偽を結果の番号へ。
fn ok_or_invalid(ok: bool) -> i32 {
    if ok { TEXT_RESULT_OK } else { TEXT_RESULT_INVALID }
}

/// usize を i32 へ（大きすぎれば上限）。
fn clamp_i32(value: usize) -> i32 {
    i32::try_from(value).unwrap_or(i32::MAX)
}

// ============================================================
//  テスト（ハブはプロセスに 1 つなので、ここでは dispatch へ自前のハブを渡して確かめる）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::text_input::session::{EVENT_ACTION, EVENT_TEXT_CHANGED};
    use crate::engine::core::text_input::TextInputAction;

    fn call(hub: &mut TextInputHub, op: i32, id: u32, ints: &[i32], text: &str) -> (i32, Vec<i32>, String) {
        let mut out_ints = vec![0; 16];
        let mut out_text = vec![0u8; 64];
        let result = dispatch(hub, op, id, ints, text, &mut out_ints, &mut out_text);
        let text_out = if op == TEXT_OP_GET_STATE && result >= 0 {
            String::from_utf8(out_text[..result as usize].to_vec()).unwrap()
        } else {
            String::new()
        };
        (result, out_ints, text_out)
    }

    /// Begin → GetState（添字は UTF-16）→ SetSelection → TakeEvents → End の往復。
    #[test]
    fn round_trip_in_utf16() {
        let mut hub = TextInputHub::default();
        let (id, _, _) = call(&mut hub, TEXT_OP_BEGIN, 0, &[0, 6, 0, 0, TEXT_INDEX_NONE, TEXT_INDEX_NONE], "あ😀い");
        assert!(id > 0);
        let id = id as u32;
        let (len, ints, text) = call(&mut hub, TEXT_OP_GET_STATE, id, &[], "");
        assert_eq!(text, "あ😀い");
        assert_eq!(len as usize, "あ😀い".len());
        assert_eq!(&ints[..4], &[4, 4, -1, -1], "末尾は UTF-16 で 4（絵文字は 2 単位）");
        assert_eq!(call(&mut hub, TEXT_OP_SET_SELECTION, id, &[1, 3], "").0, TEXT_RESULT_OK);
        let (_, ints, _) = call(&mut hub, TEXT_OP_GET_STATE, id, &[], "");
        assert_eq!(&ints[..2], &[1, 3]);
        assert_eq!(call(&mut hub, TEXT_OP_SET_TEXT, id, &[TEXT_INDEX_NONE, TEXT_INDEX_NONE], "abc").0, TEXT_RESULT_OK);
        hub.session_mut(id).unwrap().notify_action(TextInputAction::Done);
        let (count, events, _) = call(&mut hub, TEXT_OP_TAKE_EVENTS, id, &[], "");
        assert!(count >= 2);
        let pairs: Vec<(i32, i32)> = events.chunks_exact(EVENT_INTS).take(count as usize).map(|p| (p[0], p[1])).collect();
        assert!(pairs.contains(&(EVENT_TEXT_CHANGED, 0)));
        assert!(pairs.contains(&(EVENT_ACTION, TextInputAction::Done.code())));
        assert_eq!(call(&mut hub, TEXT_OP_ACTIVE, 0, &[], "").0, id as i32);
        assert_eq!(call(&mut hub, TEXT_OP_END, id, &[], "").0, TEXT_RESULT_OK);
        assert_eq!(call(&mut hub, TEXT_OP_ACTIVE, 0, &[], "").0, TEXT_NO_SESSION);
    }

    /// 今の場でない番号・知らない op・足りない ints は -1。本文が入れ物より長ければ長さだけ返す。
    #[test]
    fn invalid_calls_and_small_buffer() {
        let mut hub = TextInputHub::default();
        assert_eq!(call(&mut hub, TEXT_OP_BEGIN, 0, &[0, 6], "x").0, TEXT_RESULT_INVALID);
        let (id, _, _) = call(&mut hub, TEXT_OP_BEGIN, 0, &[0, 6, 0, 0, -1, -1], "0123456789");
        assert_eq!(call(&mut hub, TEXT_OP_END, id as u32 + 1, &[], "").0, TEXT_RESULT_INVALID);
        assert_eq!(call(&mut hub, 99, id as u32, &[], "").0, TEXT_RESULT_INVALID);
        let mut small = [0u8; 4];
        let mut out_ints = [0i32; STATE_OUT_INTS];
        let len = dispatch(&mut hub, TEXT_OP_GET_STATE, id as u32, &[], "", &mut out_ints, &mut small);
        assert_eq!(len, 10);
        assert_eq!(small, [0u8; 4], "入れ物に収まらなければ本文は書かない");
    }
}
