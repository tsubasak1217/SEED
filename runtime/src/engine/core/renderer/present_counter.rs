// ============================================================
//  renderer/present_counter.rs — 提示（present）したフレーム数の計数
//
//  【用途】
//  「描画ループが実際に回ってスワップチェーンへ提示できているか」を、描画スレッドの外
//  （Android の生存確認ログスレッド runtime/android/native の heartbeat など）から
//  読めるようにする。fps（frame_pacing）は描画を省いた早期 return のフレームでも進むため、
//  「画面に出たフレーム」の証拠にはならない。こちらは present した回数だけを数える。
//
//  計数はアトミック加算 1 回だけなので、デスクトップでも常時有効にしてある（コストは無視できる）。
// ============================================================

use std::sync::atomic::{AtomicU64, Ordering};

/// プロセス起動からの提示フレーム数（スワップチェーンへ present した回数）。
static PRESENTED_FRAMES: AtomicU64 = AtomicU64::new(0);

/// 1 フレーム提示したことを記録する（RenderFrame::finish の present 直後から呼ぶ）。
pub(crate) fn record_present() {
    PRESENTED_FRAMES.fetch_add(1, Ordering::Relaxed);
}

/// プロセス起動から現在までに提示したフレーム数を返す。
pub fn presented_frame_count() -> u64 {
    PRESENTED_FRAMES.load(Ordering::Relaxed)
}
