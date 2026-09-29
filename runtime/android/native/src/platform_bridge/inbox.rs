// ============================================================
//  platform_bridge/inbox.rs — Java から届いたプラットフォームのイベントの箱（W1-1）
//
//  【流れ】
//    Java の PlatformConnection（背面のスレッド SEEDPlatform）→ SeedPlatform.deliverEvent → nativeOnPlatformEvent（jni_exports.rs）
//      → receive（形を確かめてここへ積む）
//    エンジンがフレームの頭で AndroidPlatformBridge::poll_events → take_all（積まれた順に取り出す）
//  積むのは Java のスレッド、取り出すのはエンジンのスレッドなので、エンジンの PlatformEventQueue（Mutex・上限つき）を使う。
//  形の検査（オブジェクトで、name が platform. で始まる）はエンジンの wire::validate_event_json（単体テスト付き）に任せる。
//
//  【ログ】1 件ごとに「受け取りました」を info で出す。ただし毎フレーム届くもの（wire::is_high_frequency_event。予測型の戻るの
//  platform.back_progressed。W2 の手直し P1-3）は出さずに数え、次のほかのイベント（手ぶりの終わりの back_cancelled・back_invoked）の
//  行に「間の N 件は省いた」と添える。上限を超えて捨てた知らせは、捨てた累計が 2 の累乗のときだけ出す（receive の中のコメント）。
// ============================================================

use std::sync::atomic::{AtomicU64, Ordering};

use seed_engine::engine::core::redraw::wake as redraw_wake;
use seed_engine::engine::core::redraw::RedrawReason;
use seed_engine::engine::platform::bridge::{wire, PlatformEventQueue, PushOutcome, DEFAULT_EVENT_QUEUE_CAPACITY, LOG_PREFIX};

use crate::logcat;

/// Java から届いたイベントの箱（プロセスで 1 つ）。
static INBOX: PlatformEventQueue = PlatformEventQueue::new(DEFAULT_EVENT_QUEUE_CAPACITY);

/// ログを省いた毎フレームのイベントの件数（次のほかのイベントのログに添えて 0 に戻す）。
static QUIET_EVENTS_SINCE_LOG: AtomicU64 = AtomicU64::new(0);

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
        // 捨てた累計が 2 の累乗（1・2・4・8…）のときだけ出す（W2 の手直し P1-2・P1-3）。エンジンはスクリプトの準備ができるまで
        // 箱から取り出さない（scripting/platform_bridge.rs の mark_scripts_ready）ので、スクリプトの無いシーンで予測型の戻るの
        // 毎フレームのイベントが届き続けると、捨てるたびに 1 行ずつ出てログが埋まるため。
        let dropped = INBOX.dropped_count();
        if dropped.is_power_of_two() {
            logcat::warn(&format!(
                "{LOG_PREFIX} エンジンが取り出さないイベントが上限（{DEFAULT_EVENT_QUEUE_CAPACITY} 件）を超えたので古いものを捨てました（累計 {dropped} 件。以後は累計が 2 倍になるたびに出します）"
            ));
        }
    }
    log_received(&name);
    // 描画を止めている間（render_policy の on_demand。W2-10a）でも次のフレームでスクリプトへ届くよう、
    // イベントループを起こす（起きていれば理由を積むだけ）。積んだ後に起こすので、起きたフレームは必ずこのイベントを取り出す。
    redraw_wake::raise(RedrawReason::PlatformEvent);
}

/// 積まれたイベントを積まれた順にすべて取り出す（エンジンのスレッドから）。
pub fn take_all() -> Vec<String> {
    INBOX.drain()
}

/// 受け取ったイベントの 1 行のログ（毎フレーム届くものは数えるだけ。ファイル冒頭の【ログ】）。
///
/// # 引数
/// * `name` - 形を確かめたイベントの名前
fn log_received(name: &str) {
    if wire::is_high_frequency_event(name) {
        QUIET_EVENTS_SINCE_LOG.fetch_add(1, Ordering::Relaxed);
        return;
    }
    let quiet = QUIET_EVENTS_SINCE_LOG.swap(0, Ordering::Relaxed);
    if quiet > 0 {
        logcat::info(&format!("{LOG_PREFIX} イベントを受け取りました: {name}（その前の毎フレームのイベント {quiet} 件はログを省きました）"));
    } else {
        logcat::info(&format!("{LOG_PREFIX} イベントを受け取りました: {name}"));
    }
}
