// ============================================================
//  lifecycle_diag.rs — ライフサイクル診断ログ（Android 段階0 の実機検証用）
//
//  【目的】
//  端末の回転（Resized）・フォーカス・タッチ・キーが実際に届いているかを
//  logcat（標準エラー経由）で確認できるようにする。エンジンの入力処理には一切関与せず、
//  観測してログを出すだけ。タッチはここでは winit から届いた生イベントを出し、
//  入力状態（Input.TouchCount / GetTouch・タッチ由来のマウス）への反映結果は
//  touch_diag.rs がフレーム単位で出す。
//
//  【出す条件】platform::CURRENT.lifecycle_diag_log が真のときだけ（Android）。
//  デスクトップでは何も出さない（エディタの Output パネルを埋めないため）。
//
//  状態（タッチ移動の件数）は play_diag と同じくモジュール内の静的変数で持つ。
//  診断専用の値を App 本体の状態に混ぜないため。
// ============================================================

use std::sync::atomic::{AtomicU64, Ordering};

use winit::event::{ElementState, Touch, TouchPhase, WindowEvent};

use crate::engine::platform;

/// 直近のタッチ開始以降に届いた Moved イベントの件数。
///
/// Moved は 1 秒に数十〜数百件届くため 1 件ずつは出さず、指が離れたときに件数だけまとめて出す。
static TOUCH_MOVES_SINCE_START: AtomicU64 = AtomicU64::new(0);

/// WindowEvent を観測し、診断対象ならログを出す（window_event の先頭から毎回呼ぶ）。
pub(super) fn observe_window_event(event: &WindowEvent) {
    if !platform::CURRENT.lifecycle_diag_log {
        return;
    }
    match event {
        WindowEvent::Resized(size) => {
            eprintln!("[SEED LIFECYCLE] Resized {}x{}", size.width, size.height);
        }
        WindowEvent::ScaleFactorChanged { scale_factor, .. } => {
            eprintln!("[SEED LIFECYCLE] ScaleFactorChanged scale_factor={scale_factor}");
        }
        WindowEvent::Focused(focused) => {
            eprintln!("[SEED LIFECYCLE] Focused {focused}");
        }
        WindowEvent::Occluded(occluded) => {
            eprintln!("[SEED LIFECYCLE] Occluded {occluded}");
        }
        WindowEvent::CloseRequested => {
            eprintln!("[SEED LIFECYCLE] CloseRequested");
        }
        WindowEvent::Touch(touch) => log_touch(touch),
        WindowEvent::KeyboardInput { event, .. } if event.state == ElementState::Pressed => {
            // 戻るキー等が実際に届くかの確認用（押下のみ。離しまで出すと件数が倍になるだけ）。
            eprintln!(
                "[SEED KEY] pressed logical={:?} physical={:?}",
                event.logical_key, event.physical_key
            );
        }
        _ => {}
    }
}

/// タッチイベント 1 件をログへ出す（Moved は件数だけ数える）。
fn log_touch(touch: &Touch) {
    match touch.phase {
        TouchPhase::Moved => {
            TOUCH_MOVES_SINCE_START.fetch_add(1, Ordering::Relaxed);
        }
        TouchPhase::Started => {
            TOUCH_MOVES_SINCE_START.store(0, Ordering::Relaxed);
            eprintln!(
                "[SEED TOUCH] Started id={} pos=({:.1}, {:.1}) force={:?}",
                touch.id, touch.location.x, touch.location.y, touch.force
            );
        }
        TouchPhase::Ended | TouchPhase::Cancelled => {
            let moves = TOUCH_MOVES_SINCE_START.swap(0, Ordering::Relaxed);
            eprintln!(
                "[SEED TOUCH] {:?} id={} pos=({:.1}, {:.1}) moves={moves}",
                touch.phase, touch.id, touch.location.x, touch.location.y
            );
        }
    }
}
