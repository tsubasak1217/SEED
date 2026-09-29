// ============================================================
//  platform_bridge.rs — スクリプトのプラットフォーム機能 API（SEED.Platform）の FFI（W1-1）
//
//  【役割】
//  C# の `SEED.Platform`（scripting/src/Api/Platform/）が呼ぶ FFI 関数 2 本と、スクリプトへ見せるイベントの箱を持つ。
//  中身の処理（どの OS の実装へ送るか・模擬）はエンジン側の engine/platform/bridge/ が持ち、ここは
//  「文字列の受け渡しの約束」と「フレームの中で配る時機」だけを受け持つ。
//
//  【ffi_platform_invoke（op で分ける）】（C# 側 ScriptHost.PlatformOp* と一致させる）
//    op 0 = 呼び出し … module・method・JSON（UTF-8）を送り、返答の JSON を out へ書く。
//                      返り値は返答のバイト数。out に収まらなければ書かずに返答を預かり、C# は
//                      返り値の大きさの入れ物で op 1 を呼んで受け取る（呼び出しをやり直すと命令が 2 回走るので、
//                      既存の「長さを返して呼び直す」約束を「預かった返答を取りに来る」形にした）
//    op 1 = 返答の受け取り … 預かっている返答を out へ書く（収まらなければ書かずに長さだけ）。無ければ -1
//    op 2 = 状態       … 0 = 使えない / 1 = 実機 / 2 = 模擬（IPC も JNI も通らない）
//    それ以外          … -1
//  返答は届かなかったとき（基盤が無い・名前の誤り・panic）も {"ok":false,"error":…} の JSON にして返す。
//
//  【ffi_platform_poll_events】
//  スクリプトへ見せる箱の先頭のイベント（JSON）を out へ書いて取り出す。返り値はそのバイト数、空なら -1。
//  収まらなければ取り出さずに必要な長さだけ返す（デバッグコマンドの script_debug_take と同じ約束。取りこぼさない）。
//
//  【配る時機】
//  エンジン（app の frame_renderer）がフレームの頭（スクリプトのフェーズより前）で publish_platform_events を 1 回呼び、
//  基盤に届いていたイベントをこの箱へ移す。C# の PlatformEvents.Poll（ScriptBridge.BeginFrame からフレームに 1 回）が
//  箱から 1 件ずつ取り出して配る。箱と預かった返答はスクリプトのスレッド（メインスレッド）だけが触るので thread_local。
//
//  【スクリプトの準備の前に届いたイベント（W2 の手直し P1-2・2026-09-29）】
//  起動の直後（Android の起動し直しの最初の onResume など）に届いたイベントを、最初のシーンのスクリプトが OnStart で
//  受け手（PlatformEvents.OnEvent・this.On）を登録する前に配ると、受け手がいないまま消える（実機の M7 で起きた。
//  スクリプトのシステムはスクリプトごとに「OnStart → BeginFrame」を順に呼ぶので、先に回ったスクリプトの BeginFrame の
//  Poll が、後のスクリプトの OnStart より前に配ってしまう）。
//  そこで「スクリプトの準備ができた」印（SCRIPTS_READY）が立つまでは基盤からイベントを取り出さず、基盤の箱
//  （Android の受け取りの箱・デスクトップの模擬の箱。どちらも上限 DEFAULT_EVENT_QUEUE_CAPACITY 件・古いものから捨てる）に残す。
//  印はスクリプトのシステムが「スクリプトのある BeginFrame」を回し終えたとき（＝その時点で有効なスクリプトの OnStart が
//  すべて済んだとき）に立て（mark_scripts_ready）、次のフレームの頭で保持していた分をまとめて移す（＝OnStart の次のフレームの
//  BeginFrame で配る。1 フレーム遅れ）。印は Play の区切り（clear_platform_events）でだけ倒すので、Play の中のシーンの
//  切り替えでは保持しない（従来どおり届いたフレームで配る）。
// ============================================================

use std::cell::{Cell, RefCell};

use crate::engine::core::redraw::{self, RedrawReason};

use crate::engine::platform::bridge::{
    self, wire, FrontTake, PlatformBridgeStatus, PlatformEventQueue, PushOutcome, DEFAULT_EVENT_QUEUE_CAPACITY,
};

// ── op（C# 側 ScriptHost.PlatformOp* と一致させる）──
/// 命令を送って返答を受け取る。
pub const PLATFORM_OP_INVOKE: i32 = 0;
/// 預かっている返答を受け取る（op 0 の返答が入れ物に収まらなかったとき）。
pub const PLATFORM_OP_TAKE_REPLY: i32 = 1;
/// 基盤の状態を問い合わせる。
pub const PLATFORM_OP_STATUS: i32 = 2;

// ── 状態（C# 側 ScriptHost.PlatformStatus* と一致させる）──
/// 使えない。
pub const PLATFORM_STATUS_UNAVAILABLE: i32 = 0;
/// 実機（Android）につながっている。
pub const PLATFORM_STATUS_DEVICE: i32 = 1;
/// デスクトップの模擬。
pub const PLATFORM_STATUS_SIMULATED: i32 = 2;

/// 「何も無い」の返り値（預かった返答が無い・箱が空・知らない op）。
pub const PLATFORM_RESULT_NONE: i32 = -1;

/// 返答が FFI の長さ（i32）で表せないほど大きいときの理由の名前。
const ERROR_REPLY_TOO_LARGE: &str = "reply_too_large";

thread_local! {
    /// 入れ物に収まらず預かっている返答（op 1 で受け取る。次の op 0 で置き換わる）。
    static PENDING_REPLY: RefCell<Option<String>> = const { RefCell::new(None) };
    /// スクリプトへ見せるイベントの箱（フレームの頭で移し、C# が 1 件ずつ取り出す）。
    static SCRIPT_EVENTS: PlatformEventQueue = const { PlatformEventQueue::new(DEFAULT_EVENT_QUEUE_CAPACITY) };
    /// スクリプトの準備ができたか（ファイル冒頭の「スクリプトの準備の前に届いたイベント」）。
    /// 立つまでは publish_platform_events が基盤からイベントを取り出さない（基盤の箱に保持する）。
    static SCRIPTS_READY: Cell<bool> = const { Cell::new(false) };
}

/// 基盤に届いていたイベントをスクリプトへ見せる箱へ移す（App がフレームの頭で 1 回呼ぶ）。
///
/// スクリプトの準備の印（`mark_scripts_ready`）が立つまでは何もしない＝基盤の箱に保持し、印が立った後の最初の呼び出しで
/// まとめて移す。スクリプトが取り出さないまま上限を超えたら古いものから捨て、1 行だけ知らせる。
pub fn publish_platform_events() {
    // 準備の前に移すと、OnStart で受け手を登録する前のスクリプトへ配って取りこぼす（保持は基盤の箱に任せる）
    if !SCRIPTS_READY.with(Cell::get) {
        return;
    }
    let events = bridge::poll_events();
    if events.is_empty() {
        return;
    }
    let dropped = SCRIPT_EVENTS.with(|queue| {
        events
            .into_iter()
            .map(|event| queue.push(event))
            .filter(|outcome| *outcome == PushOutcome::DroppedOldest)
            .count()
    });
    if dropped > 0 {
        eprintln!(
            "{} スクリプトが取り出さないイベントが上限（{DEFAULT_EVENT_QUEUE_CAPACITY} 件）を超えたので、古いものを {dropped} 件捨てました",
            bridge::LOG_PREFIX
        );
    }
}

/// スクリプトの準備ができたことを知らせる（スクリプトのシステムが、スクリプトのある BeginFrame を回し終えるたびに呼ぶ）。
///
/// 呼ばれた時点で、そのフレームに有効なスクリプトの OnStart はすべて済んでいる（OnStart は各スクリプトの BeginFrame の直前）。
/// 初めて立ったときは、保持していたイベントを次のフレームの頭で移して配れるよう、描画を止めていても次のフレームを起こす
/// （`render_policy: on_demand` で止まったまま保持し続けないため。起こすのは Play の回ごとに 1 回だけ）。
pub fn mark_scripts_ready() {
    let was_ready = SCRIPTS_READY.with(|ready| ready.replace(true));
    if !was_ready {
        redraw::wake::raise(RedrawReason::PlatformEvent);
    }
}

/// スクリプトの準備の印が立っているか（試験・診断用）。
pub fn scripts_ready() -> bool {
    SCRIPTS_READY.with(Cell::get)
}

/// 前の回のイベントと預かった返答を捨てる（エディタの Play の開始・停止で持ち越さないため）。
///
/// 基盤の側の前の回の状態（デスクトップの模擬の目覚ましの予約表と箱。W1-3）も捨て、残っていれば取り出して捨てる。
/// スクリプトの準備の印も倒す（次の Play の最初のシーンの OnStart まで、また基盤の箱に保持する）。
pub fn clear_platform_events() {
    bridge::reset_session();
    let _ = bridge::poll_events();
    SCRIPT_EVENTS.with(|queue| queue.clear());
    PENDING_REPLY.with(|slot| slot.borrow_mut().take());
    SCRIPTS_READY.with(|ready| ready.set(false));
}

/// 状態の番号（FFI の約束）。
fn status_code(status: PlatformBridgeStatus) -> i32 {
    match status {
        PlatformBridgeStatus::Unavailable => PLATFORM_STATUS_UNAVAILABLE,
        PlatformBridgeStatus::Device => PLATFORM_STATUS_DEVICE,
        PlatformBridgeStatus::Simulated => PLATFORM_STATUS_SIMULATED,
    }
}

/// FFI の引数の UTF-8 を &str にする（長さ 0 は空文字。null で長さがある・UTF-8 でない・負の長さは None）。
///
/// # Safety
/// `ptr` は null か、`len` バイト以上読める領域を指していること（C# が fixed で渡す）。
unsafe fn utf8_arg<'a>(ptr: *const u8, len: i32) -> Option<&'a str> {
    let len = usize::try_from(len).ok()?;
    if len == 0 {
        return Some("");
    }
    if ptr.is_null() {
        return None;
    }
    // SAFETY: 呼び出し側の約束により ptr から len バイト読める。
    let bytes = unsafe { std::slice::from_raw_parts(ptr, len) };
    std::str::from_utf8(bytes).ok()
}

/// 入れ物（out・cap）に `len` バイトが収まるか。
fn fits(out: *mut u8, cap: i32, len: usize) -> bool {
    !out.is_null() && usize::try_from(cap).is_ok_and(|capacity| capacity >= len)
}

/// `bytes` を out へ写す（長さ 0 なら何もしない）。
///
/// # Safety
/// `fits(out, cap, bytes.len())` を確かめた後であること。
unsafe fn copy_out(bytes: &[u8], out: *mut u8) {
    if bytes.is_empty() {
        return;
    }
    // SAFETY: 呼び出し側が fits で out の容量を確かめている。
    unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), out, bytes.len()) };
}

/// 返答を入れ物へ書く（収まらなければ預かる）。返り値は返答のバイト数。
///
/// # Safety
/// `out` は null か、`cap` バイト以上書ける領域を指していること。
unsafe fn deliver_reply(reply: String, out: *mut u8, cap: i32) -> i32 {
    // FFI の長さ（i32）で表せない返答は、表せる大きさの失敗の返答に置き換える（現実には起きない大きさ）。
    let reply = if i32::try_from(reply.len()).is_ok() { reply } else { wire::error_reply(ERROR_REPLY_TOO_LARGE) };
    let len = reply.len();
    if fits(out, cap, len) {
        // SAFETY: fits で容量を確かめた。
        unsafe { copy_out(reply.as_bytes(), out) };
        PENDING_REPLY.with(|slot| slot.borrow_mut().take());
    } else {
        PENDING_REPLY.with(|slot| *slot.borrow_mut() = Some(reply));
    }
    len as i32
}

/// 預かっている返答を入れ物へ書く（収まらなければ預かったまま長さだけ返す）。無ければ -1。
///
/// # Safety
/// `out` は null か、`cap` バイト以上書ける領域を指していること。
unsafe fn take_pending_reply(out: *mut u8, cap: i32) -> i32 {
    PENDING_REPLY.with(|slot| {
        let mut slot = slot.borrow_mut();
        let Some(reply) = slot.as_ref() else {
            return PLATFORM_RESULT_NONE;
        };
        let len = reply.len();
        if fits(out, cap, len) {
            // SAFETY: fits で容量を確かめた。
            unsafe { copy_out(reply.as_bytes(), out) };
            slot.take();
        }
        len as i32
    })
}

/// 命令を送り、その返答を入れ物へ書く（op 0 の中身）。
///
/// # Safety
/// 各ポインタは null か、対応する長さの領域を指していること。
#[allow(clippy::too_many_arguments)] // FFI の引数の並びそのまま
unsafe fn invoke_and_deliver(
    module: *const u8, module_len: i32,
    method: *const u8, method_len: i32,
    json: *const u8, json_len: i32,
    out: *mut u8, cap: i32,
) -> i32 {
    // SAFETY: 呼び出し側（C#）の約束どおり、各ポインタは対応する長さの領域を指す。
    let (module, method, json) =
        unsafe { (utf8_arg(module, module_len), utf8_arg(method, method_len), utf8_arg(json, json_len)) };
    let reply = match (module, method, json) {
        (Some(module), Some(method), Some(json)) => match bridge::invoke(module, method, json) {
            Ok(reply) => reply,
            Err(reason) => wire::error_reply(&reason),
        },
        (Some(_), Some(_), None) => wire::error_reply(wire::ERROR_INVALID_JSON),
        _ => wire::error_reply(wire::ERROR_INVALID_NAME),
    };
    // SAFETY: out と cap は呼び出し側の約束どおり。
    unsafe { deliver_reply(reply, out, cap) }
}

/// プラットフォーム機能の呼び出し・返答の受け取り・状態の問い合わせ（SEED.Platform の入口）。
///
/// # 引数
/// - `op` … `PLATFORM_OP_*`
/// - `module` / `method` / `json` と各長さ … op 0 の命令（UTF-8。op 1・2 では使わない）
/// - `out` / `cap` … 返答の書き込み先と容量（バイト。op 0・1）
///
/// # 戻り値
/// op 0・1: 返答のバイト数（cap を超えていれば書いていない）。op 1 で預かった返答が無ければ -1。
/// op 2: 状態の番号（`PLATFORM_STATUS_*`）。知らない op は -1。
///
/// # Safety
/// 各ポインタは null か、対応する長さ（out は cap バイト）の領域を指していること（C# 側が fixed で渡す）。
#[allow(clippy::too_many_arguments)] // C# の関数ポインタの並びそのまま
pub(super) unsafe extern "system" fn ffi_platform_invoke(
    op: i32,
    module: *const u8, module_len: i32,
    method: *const u8, method_len: i32,
    json: *const u8, json_len: i32,
    out: *mut u8, cap: i32,
) -> i32 {
    match op {
        // SAFETY: 引数の約束は呼び出し側（C#）が守る。
        PLATFORM_OP_INVOKE => unsafe {
            invoke_and_deliver(module, module_len, method, method_len, json, json_len, out, cap)
        },
        // SAFETY: 同上。
        PLATFORM_OP_TAKE_REPLY => unsafe { take_pending_reply(out, cap) },
        PLATFORM_OP_STATUS => status_code(bridge::status()),
        _ => PLATFORM_RESULT_NONE,
    }
}

/// スクリプトへ見せる箱の先頭のイベント（JSON）を取り出す（SEED.Platform.PlatformEvents.Poll の供給源）。
///
/// # 戻り値
/// 空なら -1。そうでなければ先頭のバイト数（cap を超えていれば書かず、取り出してもいない）。
///
/// # Safety
/// `out` は null か、`cap` バイト以上書ける領域を指していること。
pub(super) unsafe extern "system" fn ffi_platform_poll_events(out: *mut u8, cap: i32) -> i32 {
    // null の入れ物は容量 0 とみなす（長さだけを知りたい呼び出し）
    let capacity = if out.is_null() { 0 } else { usize::try_from(cap).unwrap_or(0) };
    match SCRIPT_EVENTS.with(|queue| queue.take_front_if_fits(capacity)) {
        FrontTake::Empty => PLATFORM_RESULT_NONE,
        FrontTake::TooLarge(needed) => i32::try_from(needed).unwrap_or(i32::MAX),
        FrontTake::Taken(event) => {
            // SAFETY: take_front_if_fits が capacity（= cap。out は null でない）以下であることを確かめた。
            if !out.is_null() {
                unsafe { copy_out(event.as_bytes(), out) };
            }
            event.len() as i32
        }
    }
}

// ============================================================
//  テスト（箱と預かった返答はスレッドローカルなので、各テストのスレッドの中だけで完結する）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::Value;

    /// op 0 を Rust の文字列で呼ぶ（テスト用）。
    fn invoke(module: &str, method: &str, json: &str, out: &mut [u8]) -> i32 {
        unsafe {
            ffi_platform_invoke(
                PLATFORM_OP_INVOKE,
                module.as_ptr(), module.len() as i32,
                method.as_ptr(), method.len() as i32,
                json.as_ptr(), json.len() as i32,
                out.as_mut_ptr(), out.len() as i32,
            )
        }
    }

    /// op 1 を呼ぶ（テスト用）。
    fn take_reply(out: &mut [u8]) -> i32 {
        unsafe {
            ffi_platform_invoke(
                PLATFORM_OP_TAKE_REPLY,
                std::ptr::null(), 0, std::ptr::null(), 0, std::ptr::null(), 0,
                out.as_mut_ptr(), out.len() as i32,
            )
        }
    }

    /// 返答の先頭 len バイトを JSON として読む（テスト用）。
    fn parse(out: &[u8], len: i32) -> Value {
        serde_json::from_slice(&out[..len as usize]).expect("返答が JSON でない")
    }

    /// 状態: デスクトップは模擬（2）。知らない op は -1。
    #[cfg(not(target_os = "android"))]
    #[test]
    fn status_and_unknown_op() {
        let status = unsafe {
            ffi_platform_invoke(PLATFORM_OP_STATUS, std::ptr::null(), 0, std::ptr::null(), 0, std::ptr::null(), 0, std::ptr::null_mut(), 0)
        };
        assert_eq!(status, PLATFORM_STATUS_SIMULATED);
        let unknown = unsafe {
            ffi_platform_invoke(99, std::ptr::null(), 0, std::ptr::null(), 0, std::ptr::null(), 0, std::ptr::null_mut(), 0)
        };
        assert_eq!(unknown, PLATFORM_RESULT_NONE);
    }

    /// 入れ物が十分なら返答がそのまま書かれ、預かりは残らない。
    #[cfg(not(target_os = "android"))]
    #[test]
    fn invoke_writes_reply_when_it_fits() {
        let mut out = vec![0u8; 4096];
        let len = invoke(wire::MODULE_PLATFORM, wire::METHOD_PING, r#"{"nonce":"a"}"#, &mut out);
        assert!(len > 0);
        let reply = parse(&out, len);
        assert_eq!(reply[wire::KEY_OK], Value::Bool(true));
        assert_eq!(reply["echo"]["nonce"], Value::from("a"));
        assert_eq!(take_reply(&mut out), PLATFORM_RESULT_NONE, "収まったのに返答を預かっている");
    }

    /// 入れ物が小さいと書かずに長さを返して預かり、その長さの入れ物で op 1 を呼ぶと受け取れる（命令は 1 回だけ走る）。
    #[cfg(not(target_os = "android"))]
    #[test]
    fn small_buffer_keeps_reply_for_take() {
        let mut tiny = vec![0u8; 4];
        let needed = invoke(wire::MODULE_PLATFORM, wire::METHOD_PING, r#"{"nonce":"長い返答にする"}"#, &mut tiny);
        assert!(needed as usize > tiny.len());
        assert_eq!(tiny, vec![0u8; 4], "収まらないのに書いた");
        // まだ小さい入れ物: 長さだけ返り、預かったまま
        assert_eq!(take_reply(&mut tiny), needed);
        let mut exact = vec![0u8; needed as usize];
        assert_eq!(take_reply(&mut exact), needed);
        assert_eq!(parse(&exact, needed)["echo"]["nonce"], Value::from("長い返答にする"));
        assert_eq!(take_reply(&mut exact), PLATFORM_RESULT_NONE, "受け取った後も残っている");
    }

    /// 名前の誤りは送らずに invalid_name の返答（ok=false）。
    #[test]
    fn invalid_name_becomes_error_reply() {
        let mut out = vec![0u8; 256];
        let len = invoke("Platform", wire::METHOD_PING, "{}", &mut out);
        let reply = parse(&out, len);
        assert_eq!(reply[wire::KEY_OK], Value::Bool(false));
        assert_eq!(reply[wire::KEY_ERROR], Value::from(wire::ERROR_INVALID_NAME));
    }

    /// イベントの取り出し: 空は -1、小さい入れ物は取り出さずに長さ、十分なら取り出す。
    #[test]
    fn poll_events_two_step_protocol() {
        let mut out = vec![0u8; 256];
        SCRIPT_EVENTS.with(|queue| queue.clear());
        assert_eq!(unsafe { ffi_platform_poll_events(out.as_mut_ptr(), out.len() as i32) }, PLATFORM_RESULT_NONE);

        let event = wire::event_json(wire::TEST_EVENT_NAME, 1, 0, serde_json::json!({ "message": "こんにちは" }));
        SCRIPT_EVENTS.with(|queue| queue.push(event.clone()));
        let mut tiny = vec![0u8; 2];
        let needed = unsafe { ffi_platform_poll_events(tiny.as_mut_ptr(), tiny.len() as i32) };
        assert_eq!(needed as usize, event.len());
        // null の入れ物も長さだけ
        assert_eq!(unsafe { ffi_platform_poll_events(std::ptr::null_mut(), 0) }, needed);
        let taken = unsafe { ffi_platform_poll_events(out.as_mut_ptr(), out.len() as i32) };
        assert_eq!(taken, needed);
        assert_eq!(std::str::from_utf8(&out[..taken as usize]).unwrap(), event);
        assert_eq!(unsafe { ffi_platform_poll_events(out.as_mut_ptr(), out.len() as i32) }, PLATFORM_RESULT_NONE);
    }

    /// 模擬の試験イベントが、フレームの頭の公開を経てスクリプトの箱から取り出せる（端から端まで）。
    /// Play の区切りの片付けで箱が空になる。
    #[cfg(not(target_os = "android"))]
    #[test]
    fn simulated_test_event_reaches_script_queue() {
        let _guard = SIM_EVENTS_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let mut out = vec![0u8; 4096];
        clear_platform_events();
        // スクリプトの準備ができた後の流れ（準備の前の保持は下の held_until_scripts_ready で確かめる）
        mark_scripts_ready();
        let len = invoke(wire::MODULE_PLATFORM, wire::METHOD_EMIT_TEST_EVENT, r#"{"message":"端から端"}"#, &mut out);
        assert_eq!(parse(&out, len)[wire::KEY_OK], Value::Bool(true));
        // 公開前はスクリプトから見えない
        assert_eq!(unsafe { ffi_platform_poll_events(out.as_mut_ptr(), out.len() as i32) }, PLATFORM_RESULT_NONE);
        publish_platform_events();
        let taken = unsafe { ffi_platform_poll_events(out.as_mut_ptr(), out.len() as i32) };
        assert!(taken > 0, "公開したイベントが取り出せない");
        let event = parse(&out, taken);
        assert_eq!(event[wire::KEY_NAME], Value::from(wire::TEST_EVENT_NAME));
        assert_eq!(event[wire::KEY_DATA]["message"], Value::from("端から端"));

        // 片付け: 模擬に溜まった分とスクリプトの箱の両方が空になる
        invoke(wire::MODULE_PLATFORM, wire::METHOD_EMIT_TEST_EVENT, "{}", &mut out);
        publish_platform_events();
        invoke(wire::MODULE_PLATFORM, wire::METHOD_EMIT_TEST_EVENT, "{}", &mut out);
        clear_platform_events();
        publish_platform_events();
        assert_eq!(unsafe { ffi_platform_poll_events(out.as_mut_ptr(), out.len() as i32) }, PLATFORM_RESULT_NONE);
    }

    /// 模擬の箱（プロセスで 1 つ）を使うテストどうしを直列にする錠（箱はスレッドローカルでないため、並行に走ると
    /// 片方の片付け〈clear_platform_events〉がもう片方の積んだイベントを捨てる）。
    #[cfg(not(target_os = "android"))]
    static SIM_EVENTS_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

    /// 公開して取り出した試験イベントの message を順に集める（テスト用）。
    #[cfg(not(target_os = "android"))]
    fn take_test_messages(out: &mut [u8]) -> Vec<String> {
        let mut messages = Vec::new();
        loop {
            let taken = unsafe { ffi_platform_poll_events(out.as_mut_ptr(), out.len() as i32) };
            if taken <= 0 {
                return messages;
            }
            let event = parse(out, taken);
            messages.push(event[wire::KEY_DATA]["message"].as_str().unwrap_or_default().to_string());
        }
    }

    /// スクリプトの準備の前に届いたイベントは、何フレーム公開を呼んでも基盤の箱に残り（取りこぼさない）、
    /// 準備の印が立った後の最初の公開で、届いた順のまま 1 回だけスクリプトの箱へ移る（W2 の手直し P1-2。実機の M7）。
    #[cfg(not(target_os = "android"))]
    #[test]
    fn held_until_scripts_ready() {
        let _guard = SIM_EVENTS_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let mut out = vec![0u8; 4096];
        clear_platform_events();
        assert!(!scripts_ready(), "Play の区切りの直後は準備の前");
        invoke(wire::MODULE_PLATFORM, wire::METHOD_EMIT_TEST_EVENT, r#"{"message":"起動の直後 1"}"#, &mut out);
        invoke(wire::MODULE_PLATFORM, wire::METHOD_EMIT_TEST_EVENT, r#"{"message":"起動の直後 2"}"#, &mut out);
        // 準備の前のフレーム（スクリプトの OnStart がまだ）: 何度公開しても見えない
        const FRAMES_BEFORE_READY: usize = 3;
        for _ in 0..FRAMES_BEFORE_READY {
            publish_platform_events();
            assert!(take_test_messages(&mut out).is_empty(), "準備の前にスクリプトの箱へ移った");
        }
        // OnStart を配り終えたフレームの後: 次の公開で届いた順に移る
        mark_scripts_ready();
        assert!(scripts_ready());
        publish_platform_events();
        assert_eq!(take_test_messages(&mut out), vec!["起動の直後 1".to_string(), "起動の直後 2".to_string()]);
        // 2 度目の公開で重ねて移らない（重複して配らない）
        publish_platform_events();
        assert!(take_test_messages(&mut out).is_empty(), "保持していた分が 2 回移った");
        // 準備の後に届いた分は従来どおり次の公開で移る
        invoke(wire::MODULE_PLATFORM, wire::METHOD_EMIT_TEST_EVENT, r#"{"message":"準備の後"}"#, &mut out);
        publish_platform_events();
        assert_eq!(take_test_messages(&mut out), vec!["準備の後".to_string()]);
        clear_platform_events();
    }

    /// Play の区切り（clear_platform_events）は保持していた分を捨て、準備の印も倒す（前の回の分を次の回へ持ち越さない）。
    #[cfg(not(target_os = "android"))]
    #[test]
    fn play_boundary_drops_held_events_and_resets_ready() {
        let _guard = SIM_EVENTS_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let mut out = vec![0u8; 4096];
        clear_platform_events();
        mark_scripts_ready();
        // 前の回: 準備の後に届いて、まだ公開していない分
        invoke(wire::MODULE_PLATFORM, wire::METHOD_EMIT_TEST_EVENT, r#"{"message":"前の回"}"#, &mut out);
        // Play の区切り
        clear_platform_events();
        assert!(!scripts_ready(), "区切りで準備の印が倒れていない");
        // 次の回: 準備の前に届いた分は保持され、前の回の分は捨てられている
        invoke(wire::MODULE_PLATFORM, wire::METHOD_EMIT_TEST_EVENT, r#"{"message":"次の回"}"#, &mut out);
        publish_platform_events();
        assert!(take_test_messages(&mut out).is_empty(), "次の回の準備の前に移った");
        mark_scripts_ready();
        publish_platform_events();
        assert_eq!(take_test_messages(&mut out), vec!["次の回".to_string()], "前の回の分が残っているか、次の回の分が消えた");
        clear_platform_events();
    }
}
