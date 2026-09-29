// ============================================================
//  touch_timeline.rs — Java（input/TouchTimeline）から MotionEvent の時刻と履歴の控えを受け取る JNI（2026-09-29）
//
//  【なぜ要るか】（docs/input_gestures.md §5）
//  winit 0.30.13 は MotionEvent の時刻（eventTime）と履歴（historical）を捨てて WindowEvent::Touch にするので、エンジンは
//  受け取った時刻でジェスチャーを記録していた（1 回の入力のまとまりの中の標本が µs 差になり、離す瞬間の速度が膨らんだ）。
//  MainActivity.processMotionEvent（GameActivity の glue へ渡す前）が同じ MotionEvent から控えを作ってここへ送り、
//  エンジンの箱（seed_engine の engine::core::input::touch::os_timing）へ積む。エンジンが winit の Touch と突き合わせる。
//
//  【nativeOnMotionTimeline(int phase, int pointerCount, int historySize, int[] ids, float[] coords, long[] timesNs)】
//  （Java の `com.seedengine.runtime.input.TouchTimeline` の `private static native void`。UI スレッドから MotionEvent 1 つにつき 1 回）
//  並びの形は os_timing/motion_record.rs。配列は Java が使い回すので、要る長さだけを読む（長い分は前の MotionEvent の残り）。
//  時刻は CLOCK_MONOTONIC の ns（MotionEvent の eventTime と同じ時計）。受け取った瞬間に CLOCK_MONOTONIC と Instant を続けて読み
//  （os_timing/clock.rs の ClockAnchor::capture）、`Instant = 今の Instant − (今の ns − 控えの ns)` で換算する（未来は今へ丸める）。
//  jni クレート 0.22 の型（EnvUnowned・JIntArray・JFloatArray・JLongArray）で受け、with_env で中の panic を受け止める
//  （JNI の境界を panic で越えない。platform_bridge/jni_exports.rs と同じ流儀）。失敗はログだけで、Java へ例外を残さない。
//
//  【install_clock】android_main（entry.rs）が起動時に 1 回呼び、CLOCK_MONOTONIC の読み口をエンジンへ入れる
//  （エンジンは libc に依存しないので、診断ログ `[SEED TOUCH TIME] rec_ns=` の換算に使う）。
// ============================================================

use std::sync::atomic::{AtomicBool, Ordering};

use jni::objects::{JClass, JFloatArray, JIntArray, JLongArray};
use jni::sys::jint;
use jni::{EnvUnowned, Outcome};

use seed_engine::engine::core::input::touch::os_timing::motion_record::{MAX_HISTORY_SAMPLES, MAX_POINTERS_PER_EVENT};
use seed_engine::engine::core::input::touch::os_timing::{self, ClockAnchor, OsMotionRecord};

use crate::logcat;

/// ログの頭。
const LOG_PREFIX: &str = "[SEED TOUCH TIME]";

/// 1 秒の ns 数（timespec を ns へ直す）。
const NANOS_PER_SEC: i64 = 1_000_000_000;

/// 1 つの MotionEvent で読む履歴の標本の数の上限（Java が送る数がこれを超えたら、新しい方のこの数だけを読む）。
/// エンジンが残す数（MAX_HISTORY_SAMPLES）と同じ。UI スレッドが長く止まって履歴が溜まっても、1 回の読み取りを重くしない。
const MAX_READ_HISTORY: usize = MAX_HISTORY_SAMPLES;

/// x, y の 2 つ。
const COORDS_PER_POINT: usize = 2;

/// 受け取りの失敗を一度ログに出したか（UI スレッドから毎回出さない）。
static FAILURE_LOGGED: AtomicBool = AtomicBool::new(false);

/// CLOCK_MONOTONIC を ns で読む（MotionEvent の eventTime・Rust の Instant と同じ時計）。読めなければ 0。
fn monotonic_now_ns() -> i64 {
    let mut ts = libc::timespec { tv_sec: 0, tv_nsec: 0 };
    // SAFETY: ts は有効な timespec への排他の参照。CLOCK_MONOTONIC は Android で必ずある時計。
    let result = unsafe { libc::clock_gettime(libc::CLOCK_MONOTONIC, &mut ts) };
    if result != 0 {
        return 0;
    }
    i64::from(ts.tv_sec).saturating_mul(NANOS_PER_SEC).saturating_add(i64::from(ts.tv_nsec))
}

/// CLOCK_MONOTONIC の読み口をエンジンへ入れる（android_main が起動時に 1 回。診断ログの ns の換算に使う）。
pub fn install_clock() {
    os_timing::install_monotonic_clock(monotonic_now_ns);
}

/// Java の配列から読んだ 1 つの MotionEvent の並び（エンジンの OsMotionRecord へ渡す持ち主）。
struct MotionArrays {
    /// 段階の番号。
    phase: i32,
    /// 指の数。
    pointer_count: i32,
    /// 読んだ履歴の標本の数（上限で切ったもの）。
    history_size: i32,
    /// 指の ID。
    ids: Vec<i32>,
    /// 位置。
    coords: Vec<f32>,
    /// 時刻（ns）。
    times_ns: Vec<i64>,
}

/// `TouchTimeline.nativeOnMotionTimeline(int, int, int, int[], float[], long[])`（Java の `private static native void`）の実体。
///
/// # 引数
/// * `unowned_env`   - JNIEnv（jni クレートの FFI 安全な包み）
/// * `_class`        - TouchTimeline のクラス（使わない）
/// * `phase`         - 段階の番号（TouchTimeline.PHASE_* = os_timing::stamp::OS_PHASE_*）
/// * `pointer_count` - 指の数（1〜8）
/// * `history_size`  - 履歴の標本の数（Moved だけ。他は 0）
/// * `ids`           - 指の ID（先頭の pointer_count 個を読む）
/// * `coords`        - 位置（(history_size + 1) × pointer_count × 2 個を読む）
/// * `times_ns`      - 時刻（CLOCK_MONOTONIC の ns。history_size + 1 個を読む）
#[unsafe(no_mangle)]
#[allow(clippy::too_many_arguments)] // Java 側の引数の並びそのまま（JNI の関数形は変えられない）
pub extern "system" fn Java_com_seedengine_runtime_input_TouchTimeline_nativeOnMotionTimeline<'caller>(
    mut unowned_env: EnvUnowned<'caller>,
    _class: JClass<'caller>,
    phase: jint,
    pointer_count: jint,
    history_size: jint,
    ids: JIntArray<'caller>,
    coords: JFloatArray<'caller>,
    times_ns: JLongArray<'caller>,
) {
    // 受け取った瞬間の時計の対応（配列を読むより先に。読む時間の分だけ時刻がずれないように）
    let anchor = ClockAnchor::capture(monotonic_now_ns);
    let outcome = unowned_env.with_env(|env| -> Result<Option<MotionArrays>, jni::errors::Error> {
        let result = read_arrays(env, phase, pointer_count, history_size, &ids, &coords, &times_ns);
        if result.is_err() && env.exception_check() {
            // 読み取りの途中の Java の例外を Java（processMotionEvent）へ持ち越さない
            env.exception_clear();
        }
        result
    });
    match outcome.into_outcome() {
        Outcome::Ok(Some(arrays)) => {
            let record = OsMotionRecord {
                phase_code: arrays.phase,
                pointer_count: arrays.pointer_count,
                history_size: arrays.history_size,
                ids: &arrays.ids,
                coords: &arrays.coords,
                times_ns: &arrays.times_ns,
            };
            // 箱へ積むだけ（Mutex の出し入れ）。念のため panic をここで止める
            match std::panic::catch_unwind(|| os_timing::receive_motion(&record, &anchor)) {
                Ok(Ok(_)) => {}
                Ok(Err(err)) => log_failure_once(&format!("並びが読めないので捨てます: {err:?}")),
                Err(_) => log_failure_once("控えを積む途中で panic しました（捨てます）"),
            }
        }
        Outcome::Ok(None) => log_failure_once(&format!(
            "並びの数が範囲の外なので捨てます: phase={phase} pointers={pointer_count} history={history_size}"
        )),
        Outcome::Err(err) => log_failure_once(&format!("配列を読めませんでした（捨てます）: {err}")),
        Outcome::Panic(_) => log_failure_once("受け取りの途中で panic しました（捨てます）"),
    }
}

/// 配列から要る長さだけを読む（数が範囲の外なら None）。履歴が上限を超えていれば、新しい方の上限の数だけを読む。
fn read_arrays(
    env: &jni::Env<'_>,
    phase: i32,
    pointer_count: i32,
    history_size: i32,
    ids: &JIntArray<'_>,
    coords: &JFloatArray<'_>,
    times_ns: &JLongArray<'_>,
) -> Result<Option<MotionArrays>, jni::errors::Error> {
    let Some(count) = usize::try_from(pointer_count).ok().filter(|n| (1..=MAX_POINTERS_PER_EVENT).contains(n)) else {
        return Ok(None);
    };
    let Ok(sent_history) = usize::try_from(history_size) else { return Ok(None) };
    // 読む履歴は新しい方の上限まで（古い方を飛ばす標本の数）
    let history = sent_history.min(MAX_READ_HISTORY);
    let skipped = sent_history - history;
    let samples = history + 1;
    // 読み始めの添字（桁あふれしない形。i32 に収まらなければ並びが壊れている）
    let first_coord = skipped.checked_mul(count * COORDS_PER_POINT).and_then(|v| i32::try_from(v).ok());
    let (Ok(first_sample), Some(first_coord), Ok(count_i)) = (i32::try_from(skipped), first_coord, i32::try_from(count)) else {
        return Ok(None);
    };
    let mut id_buf = vec![0i32; count];
    ids.get_region(env, 0, &mut id_buf)?;
    let mut coord_buf = vec![0f32; samples * count * COORDS_PER_POINT];
    coords.get_region(env, first_coord, &mut coord_buf)?;
    let mut time_buf = vec![0i64; samples];
    times_ns.get_region(env, first_sample, &mut time_buf)?;
    Ok(Some(MotionArrays {
        phase,
        pointer_count: count_i,
        history_size: i32::try_from(history).unwrap_or(0),
        ids: id_buf,
        coords: coord_buf,
        times_ns: time_buf,
    }))
}

/// 受け取りの失敗を最初の 1 回だけログへ出す（以後は黙って捨てる。そのイベントは受け取った時刻に戻るだけ）。
fn log_failure_once(message: &str) {
    if !FAILURE_LOGGED.swap(true, Ordering::Relaxed) {
        logcat::warn(&format!("{LOG_PREFIX} {message}（以後の同じ失敗は出しません）"));
    }
}
