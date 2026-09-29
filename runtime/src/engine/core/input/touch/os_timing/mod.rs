// ============================================================
//  touch/os_timing/ — OS のタッチの時刻の控え（Android の MotionEvent の eventTime と履歴。2026-09-29）
//
//  【なぜ要るか】
//  winit 0.30.13 の Android 実装は MotionEvent の時刻（eventTime）と履歴（historical）を捨て、各ポインタを
//  WindowEvent::Touch（ID・段階・位置だけ）にする。エンジンは受け取った時刻でジェスチャーを記録していたので、
//  1 回の入力のまとまり（vsync ごと）に入った MotionEvent どうしが µs しか違わない時刻になり、速度の推定が膨らんだ
//  （離す瞬間の 1.5〜4.2 dp の飛びで 5,800〜7,700 dp/秒のフリック。docs/input_gestures.md §5・§11）。
//  winit の入力のバッファは読むと消えるので winit の外から二重に読めない。そこで Java（MainActivity の processMotionEvent。
//  GameActivity の glue へ渡す前）で同じ MotionEvent から時刻と履歴を控え、JNI（runtime/android/native の touch_timeline.rs）で
//  ここの箱へ積み、Input::process_touch が winit の Touch と突き合わせる。
//
//  【構成】（1 ファイル 1 責務。PC では誰も積まないので何もしない）
//    stamp.rs         … 控え 1 件（指・段階・今の標本・履歴）と段階の番号（Java・native と一致）
//    clock.rs         … CLOCK_MONOTONIC の ns ↔ Instant の換算（同じ時計）と、native が入れる時計の読み口
//    motion_record.rs … Java から届いた MotionEvent 1 つ分の配列を指ごとの控えへほどく（純関数）
//    timeline.rs      … 控えの箱と突き合わせ（純ロジック。上限・前の控えを捨てる・見つからなければそのまま）
//    diag.rs          … 実機の確かめ用のログ `[SEED TOUCH TIME]`（lifecycle_diag_log のときだけ）
//    mod.rs（ここ）   … プロセスで 1 つの箱（native が積み、Input が取る）
//
//  【使い方】native: `install_monotonic_clock`（起動時）・`receive_motion`（JNI）。エンジン: `take_matching`（Input::process_touch）。
// ============================================================

pub mod clock;
pub mod diag;
pub mod motion_record;
pub mod stamp;
pub mod timeline;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, MutexGuard};

use winit::event::TouchPhase;

pub use clock::{install_monotonic_clock, instant_to_monotonic_ns, ClockAnchor, MonotonicClock};
pub use motion_record::{MotionRecordError, OsMotionRecord};
pub use stamp::{OsTouchSample, OsTouchStamp};
pub use timeline::OsTouchTimeline;

/// プロセスで 1 つの控えの箱（Java の UI スレッドが JNI で積み、エンジンのスレッドが取る）。
static TIMELINE: Mutex<OsTouchTimeline> = Mutex::new(OsTouchTimeline::new());

/// 控えが一度でも積まれたか（PC では誰も積まないので、`take_matching` はロックも取らずに None を返す）。
static ANY_RECEIVED: AtomicBool = AtomicBool::new(false);

/// 箱を借りる（panic で毒された Mutex もそのまま使う。中身は控えの列だけで、壊れても突き合わせが外れるだけ）。
fn timeline() -> MutexGuard<'static, OsTouchTimeline> {
    TIMELINE.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// Java から届いた MotionEvent 1 つ分の控えを箱へ積む（native の JNI が UI スレッドから呼ぶ）。
///
/// # 引数
/// * `record` - 配列（motion_record.rs の形）
/// * `anchor` - 時刻の換算の対応（受け取った瞬間に読んだもの）
///
/// # 戻り値
/// 積んだ控えの数。並びが壊れていれば積まずに Err。
pub fn receive_motion(record: &OsMotionRecord<'_>, anchor: &ClockAnchor) -> Result<usize, MotionRecordError> {
    let stamps = record.to_stamps(anchor)?;
    let count = stamps.len();
    {
        let mut boxed = timeline();
        for stamp in stamps {
            boxed.push(stamp);
        }
    }
    ANY_RECEIVED.store(true, Ordering::Release);
    Ok(count)
}

/// winit の Touch に一致する控えを取り出す（Input::process_touch が呼ぶ）。
///
/// PC（誰も積まない）ではロックも取らずに None。一致が無ければ箱はそのままで None（受け取った時刻に戻る）。
pub fn take_matching(pointer_id: u64, phase: TouchPhase, x: f32, y: f32) -> Option<OsTouchStamp> {
    if !ANY_RECEIVED.load(Ordering::Acquire) {
        return None;
    }
    timeline().take_matching(pointer_id, phase, x, y)
}
