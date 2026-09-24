// ============================================================
//  key_diag.rs — 置き換えたキーのフレーム単位診断ログ（Android の戻るキー → Escape の確認用）
//
//  【目的】
//  winit から届いた生のキー（lifecycle_diag.rs の [SEED KEY]）が、プラットフォームの置き換え表
//  （platform::CURRENT.key_remap。Android は戻るキー → Escape）を通って入力状態へ入ったかを、
//  スクリプトの Input.GetKeyDown / GetKeyUp が読むのと同じ「フレーム末（Input::end_frame の直前）」の
//  値で標準エラー（Android では logcat）へ出す。入力処理には一切関与せず、読んでログを出すだけ。
//
//  【出す条件】platform::CURRENT.lifecycle_diag_log が真（Android）で、かつそのフレームに置き換え先の
//  キーの「押した瞬間」か「離した瞬間」があるときだけ。デスクトップでは何も出さない。
//
//  【書式】1 フレーム 1 行:
//    [SEED KEY FRAME] f=<通番> <キー>:<down|up|down+up> ...
//  down = GetKeyDown が true、up = GetKeyUp が true（同じフレームに押して離すと down+up）。
// ============================================================

use std::fmt::Write as _;
use std::sync::atomic::{AtomicU64, Ordering};

use crate::engine::core::input::Input;
use crate::engine::platform;

/// 診断ログの行頭（logcat で grep する印）。
const LINE_TAG: &str = "[SEED KEY FRAME]";

/// 観測したフレームの通番（ログの行どうしの前後関係を見るため。描画フレーム数とは別物）。
static OBSERVED_FRAMES: AtomicU64 = AtomicU64::new(0);

/// フレーム末に 1 回呼ぶ（Input::end_frame の直前）。置き換え先のキーに変化があったフレームだけ 1 行出す。
pub(super) fn observe_frame(input: &Input) {
    let traits = platform::CURRENT;
    if !traits.lifecycle_diag_log || traits.key_remap.is_empty() {
        return;
    }
    let frame = OBSERVED_FRAMES.fetch_add(1, Ordering::Relaxed);

    let mut line = String::new();
    for rule in traits.key_remap {
        let down = input.is_trigger_key(rule.to);
        let up = input.is_release_key(rule.to);
        let edge = match (down, up) {
            (true, true) => "down+up",
            (true, false) => "down",
            (false, true) => "up",
            (false, false) => continue,
        };
        // String への書き込みは失敗しない。
        let _ = write!(line, " {:?}:{edge}", rule.to);
    }
    if !line.is_empty() {
        eprintln!("{LINE_TAG} f={frame}{line}");
    }
}
