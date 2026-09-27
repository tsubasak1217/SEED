// ============================================================
//  redraw_waker.rs — Java から「描く理由」を積み、眠っているイベントループを起こす JNI（W2-10a）
//
//  【なぜ要るか】
//  render_policy の on_demand で描画を止めている間、イベントループは眠っている。Android の文字入力（IME の本文の変化・
//  完了などのアクション・キーボードの表示と高さ）は GameActivity の glue がルーパーを起こすが、winit はそれを
//  WindowEvent にしない（読み捨てる。docs/app_platform_roadmap.md §3.8.1 の I-1・I-10）ので、エンジンは気付けない。
//  MainActivity の IME の受け口（stateChanged など）が Java の RedrawWaker.requestRedraw を呼び、ここへ届く。
//  W2-6a で本文を受け取る JNI を足したら、その受け口もここと同じく起こす（または本文の受け取りの中で raise する）。
//
//  【nativeRequestRedraw(int reason)】（Java の `com.seedengine.runtime.redraw.RedrawWaker` の `private static native void`）
//  番号は engine::core::redraw::reason の EXTERNAL_REASON_*（0 = 文字入力・1 = 画面・2 = その他）。UI スレッドから呼ばれる。
//  原子変数への書き込みと、眠っていれば EventLoopProxy の送信（ALooper_wake）だけなので速い。
//  引数の JNIEnv* / jclass は使わないので生ポインタのまま受け取る（jni_exports.rs と同じ流儀）。
// ============================================================

use std::ffi::c_void;

use seed_engine::engine::core::redraw::wake as redraw_wake;
use seed_engine::engine::core::redraw::{RedrawReason, LOG_PREFIX};

use crate::logcat;

/// `RedrawWaker.nativeRequestRedraw(int)`（Java の `private static native void`）の実体。
///
/// # 引数
/// * `_env` / `_class` - JNIEnv* と RedrawWaker の jclass（使わない）
/// * `reason`          - 理由の番号（RedrawWaker.REASON_*）
#[unsafe(no_mangle)]
pub extern "system" fn Java_com_seedengine_runtime_redraw_RedrawWaker_nativeRequestRedraw(
    _env: *mut c_void,
    _class: *mut c_void,
    reason: i32,
) {
    let Some(reason) = RedrawReason::from_external_code(reason) else {
        // Java とネイティブの版の食い違い。知らない理由では起こさない（次の入力で描かれる）
        logcat::warn(&format!("{LOG_PREFIX} 知らない描く理由の番号を受け取りました（無視します）: {reason}"));
        return;
    };
    // 原子変数と EventLoopProxy の送信だけだが、JNI の境界を panic で越えないよう念のため受け止める
    if std::panic::catch_unwind(|| redraw_wake::raise(reason)).is_err() {
        logcat::error(&format!("{LOG_PREFIX} 描く理由を積む途中で panic しました（{}）", reason.name()));
    }
}
