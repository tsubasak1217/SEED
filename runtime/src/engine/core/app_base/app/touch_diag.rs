// ============================================================
//  touch_diag.rs — タッチ状態のフレーム単位診断ログ（Android 段階A の実機検証用）
//
//  【目的】
//  winit から届いた生のタッチ（lifecycle_diag.rs の [SEED TOUCH]）が、Input のタッチ状態
//  （スクリプトの Input.TouchCount / GetTouch が返す一覧）と、タッチ由来のマウス状態
//  （Input.MousePos・左ボタン）へどう反映されたかを、スクリプトが読むのと同じ
//  「フレーム末（Input::end_frame の直前）」の値で標準エラー（Android では logcat）へ出す。
//  入力処理には一切関与せず、読んでログを出すだけ。
//
//  【出す条件】platform::CURRENT.lifecycle_diag_log が真（Android）で、かつそのフレームに変化
//  （Stationary 以外の段階の指、または左ボタンの押した瞬間・離した瞬間）があるときだけ。
//  触れたまま静止しているフレームは出さない（長押しでログが埋まらないように）。
//
//  【書式】1 フレーム 1 行:
//    [SEED TOUCH FRAME] f=<通番> n=<本数> #<指番号>:<段階>(x,y)d(dx,dy) ... | mouse=(x,y) L=<押下中><押した瞬間><離した瞬間>
//  L は 3 文字で、押下中=P / 押した瞬間=D / 離した瞬間=U、該当しなければ '-'（例: "PD-" = 押した瞬間）。
// ============================================================

use std::fmt::Write as _;
use std::sync::atomic::{AtomicU64, Ordering};

use winit::event::MouseButton;

use crate::engine::core::input::touch::TouchPhase;
use crate::engine::core::input::{Input, InputState};
use crate::engine::platform;

/// 診断ログの行頭（logcat で grep する印）。
const LINE_TAG: &str = "[SEED TOUCH FRAME]";

/// 左ボタンの状態を表す文字（押下中 / 押した瞬間 / 離した瞬間 / 該当なし）。
const MARK_HELD: char = 'P';
const MARK_DOWN: char = 'D';
const MARK_UP: char = 'U';
const MARK_NONE: char = '-';

/// 観測したフレームの通番（ログの行どうしの前後関係を見るため。描画フレーム数とは別物）。
static OBSERVED_FRAMES: AtomicU64 = AtomicU64::new(0);

/// フレーム末に 1 回呼ぶ（Input::end_frame の直前）。変化のあったフレームだけ 1 行出す。
pub(super) fn observe_frame(input: &Input) {
    if !platform::CURRENT.lifecycle_diag_log {
        return;
    }
    let frame = OBSERVED_FRAMES.fetch_add(1, Ordering::Relaxed);
    if !frame_has_change(input) {
        return;
    }
    eprintln!("{}", format_frame_line(frame, input));
}

/// このフレームにログへ出すべき変化があるか。
fn frame_has_change(input: &Input) -> bool {
    let left_edge = input.is_trigger_mouse(MouseButton::Left)
        || input.is_release_mouse(MouseButton::Left);
    left_edge || input.touches().any(|t| t.phase != TouchPhase::Stationary)
}

/// 1 フレーム分のログ行を組み立てる。
fn format_frame_line(frame: u64, input: &Input) -> String {
    let mut line = format!("{LINE_TAG} f={frame} n={}", input.touch_count());
    for t in input.touches() {
        // String への書き込みは失敗しない。
        let _ = write!(
            line,
            " #{}:{}({:.1},{:.1})d({:.1},{:.1})",
            t.finger_id,
            t.phase.label(),
            t.position.x,
            t.position.y,
            t.delta.x,
            t.delta.y,
        );
    }
    let mouse = input.mouse_position(InputState::Current);
    let mark = |on: bool, c: char| if on { c } else { MARK_NONE };
    let _ = write!(
        line,
        " | mouse=({:.1},{:.1}) L={}{}{}",
        mouse.x,
        mouse.y,
        mark(input.is_press_mouse(MouseButton::Left), MARK_HELD),
        mark(input.is_trigger_mouse(MouseButton::Left), MARK_DOWN),
        mark(input.is_release_mouse(MouseButton::Left), MARK_UP),
    );
    line
}

// ============================================================
//  テスト（書式。実機検証の手順が grep する前提を固定する）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::input::touch::PointerBridgePolicy;
    use winit::event::TouchPhase as RawTouchPhase;

    /// Android と同じ方針の Input で、指 2 本・左ボタン押下の瞬間が 1 行に出る。
    #[test]
    fn line_contains_fingers_and_mouse_state() {
        let mut input = Input::with_pointer_policy(PointerBridgePolicy {
            touch_drives_mouse: true,
            mouse_simulates_touch: false,
        });
        input.process_touch(0, RawTouchPhase::Started, 10.0, 20.0);
        input.process_touch(1, RawTouchPhase::Started, 30.0, 40.0);
        assert!(frame_has_change(&input));
        let line = format_frame_line(7, &input);
        assert_eq!(
            line,
            "[SEED TOUCH FRAME] f=7 n=2 #0:Began(10.0,20.0)d(0.0,0.0) #1:Began(30.0,40.0)d(0.0,0.0) \
             | mouse=(10.0,20.0) L=PD-"
        );

        // 触れたまま静止しているだけのフレームは変化なし
        input.end_frame();
        assert!(!frame_has_change(&input));
    }
}
