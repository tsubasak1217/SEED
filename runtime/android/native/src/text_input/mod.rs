// ============================================================
//  text_input/ — 文字入力（GameActivity の GameTextInput）の Android の糊（W2-6a。E-06 の決定。docs/ui_text_input.md §6）
//
//  【E-06 の形】（docs/app_platform_roadmap.md §3.8.4）
//    - 操作（エンジン → IME）: android_main が受け取った AndroidApp を複製して持ち（winit へは元を渡す。同じ Activity を指す）、
//      エンジンの命令（engine::core::text_input::PlatformCommand）を android-activity の API で実行する（platform.rs）。
//      set_ime_editor_info → set_text_input_state → show_soft_input の順（どれも GameActivity の UI スレッドの作業の列に
//      順に積まれる。I-4）。hide_soft_input で隠す
//    - 知らせ（IME → エンジン）: MainActivity の上書き（stateChanged・onEditorAction・onSoftwareKeyboardVisibilityChanged・
//      onApplyWindowInsets の IME の高さ）が Java の input/TextInputBridge を通してここの JNI を呼び（jni_receivers.rs）、
//      エンジンの箱（engine::core::text_input::inbox）へ積む（積むとイベントループを起こす）
//    - winit が読み捨てる TextEvent・TextAction には頼らない（I-1）。ネイティブから text_input_state() を読むこともしない
//      （I-7・I-12: UI スレッド以外で読むと途中の本文を読みうる・一度も本文が入っていないと落ちる）
//  W2-0 の試作（ui_spike/ime_probe.rs・spike/ImeSpikeLog.java）はこれに置き換えて外した。
// ============================================================

mod jni_receivers;
mod platform;

use seed_engine::engine::core::text_input::{self, LOG_PREFIX};
use winit::platform::android::activity::AndroidApp;

use crate::logcat;

/// エンジンへ Android の文字入力の実装を登録する（android_main が EventLoop を作る前に 1 回）。
///
/// # 引数
/// * `app` - android_main が受け取った AndroidApp（複製して持つ。winit へ渡す元はそのまま）
pub fn install(app: &AndroidApp) {
    if text_input::register_platform(Box::new(platform::AndroidTextInput::new(app.clone()))) {
        logcat::info(&format!("{LOG_PREFIX} エンジンへ Android の文字入力（GameTextInput）を登録しました"));
    } else {
        logcat::warn(&format!("{LOG_PREFIX} 文字入力の実装は既に登録されていました（そのまま使います）"));
    }
}
