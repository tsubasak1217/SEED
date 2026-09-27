// ============================================================
//  platform_bridge/inbox.rs — Java から届いたプラットフォームのイベントの箱（W1-1）
//
//  【流れ】
//    Java の PlatformConnection（背面のスレッド SEEDPlatform）→ SeedPlatform.deliverEvent → nativeOnPlatformEvent（jni_exports.rs）
//      → receive（形を確かめてここへ積む）
//    エンジンがフレームの頭で AndroidPlatformBridge::poll_events → take_all（積まれた順に取り出す）
//  積むのは Java のスレッド、取り出すのはエンジンのスレッドなので、エンジンの PlatformEventQueue（Mutex・上限つき）を使う。
//  形の検査（オブジェクトで、name が platform. で始まる）はエンジンの wire::validate_event_json（単体テスト付き）に任せる。
// ============================================================

use seed_engine::engine::core::redraw::wake as redraw_wake;
use seed_engine::engine::core::redraw::RedrawReason;
use seed_engine::engine::platform::bridge::{wire, PlatformEventQueue, PushOutcome, DEFAULT_EVENT_QUEUE_CAPACITY, LOG_PREFIX};

use crate::logcat;

/// Java から届いたイベントの箱（プロセスで 1 つ）。
static INBOX: PlatformEventQueue = PlatformEventQueue::new(DEFAULT_EVENT_QUEUE_CAPACITY);

/// Java から届いたイベント（UTF-8 の JSON）を確かめて積む。形が違うものはログを残して捨てる。
///
/// # 引数
/// * `bytes` - Java の byte[] の中身
pub fn receive(bytes: Vec<u8>) {
    let text = match String::from_utf8(bytes) {
        Ok(text) => text,
        Err(err) => {
            logcat::warn(&format!("{LOG_PREFIX} UTF-8 でないイベントを捨てました: {err}"));
            return;
        }
    };
    let name = match wire::validate_event_json(&text) {
        Ok(name) => name,
        Err(reason) => {
            logcat::warn(&format!("{LOG_PREFIX} 形の違うイベントを捨てました（{reason}）: {text}"));
            return;
        }
    };
    if INBOX.push(text) == PushOutcome::DroppedOldest {
        logcat::warn(&format!(
            "{LOG_PREFIX} エンジンが取り出さないイベントが上限（{DEFAULT_EVENT_QUEUE_CAPACITY} 件）を超えたので古いものを捨てました（累計 {} 件）",
            INBOX.dropped_count()
        ));
    }
    logcat::info(&format!("{LOG_PREFIX} イベントを受け取りました: {name}"));
    // 描画を止めている間（render_policy の on_demand。W2-10a）でも次のフレームでスクリプトへ届くよう、
    // イベントループを起こす（起きていれば理由を積むだけ）。積んだ後に起こすので、起きたフレームは必ずこのイベントを取り出す。
    redraw_wake::raise(RedrawReason::PlatformEvent);
}

/// 積まれたイベントを積まれた順にすべて取り出す（エンジンのスレッドから）。
pub fn take_all() -> Vec<String> {
    INBOX.drain()
}
