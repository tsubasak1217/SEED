// ============================================================
//  debug_hooks.rs — 検証用フック（adb から切り替える）
//
//  通常起動では何もしない。システムプロパティは次のように端末側で切り替える:
//    adb shell setprop debug.seed.panic_test 1   … 次回起動時に意図的に panic する
//    adb shell setprop debug.seed.panic_test 0   … 元に戻す
//    adb shell setprop debug.seed.touch_test 1   … 次回起動時に複数指の合成タッチ列を 1 回流す
//    adb shell setprop debug.seed.touch_test 0   … 元に戻す
//  （debug.* のプロパティは adb shell から書ける。端末の再起動で消える）
// ============================================================

use crate::{logcat, sysprop};

/// 意図的に panic させるかを決めるシステムプロパティ名。
const PANIC_TEST_PROPERTY: &str = "debug.seed.panic_test";

/// 複数指の合成タッチ列を流すかを決めるシステムプロパティ名。
const TOUCH_TEST_PROPERTY: &str = "debug.seed.touch_test";

/// フックを有効にする値（各プロパティ共通）。
const ENABLED_VALUE: &str = "1";

/// `debug.seed.panic_test` が有効なら意図的に panic する（panic が logcat に残ることの確認用）。
pub fn panic_if_requested() {
    if sysprop::get(PANIC_TEST_PROPERTY).as_deref() == Some(ENABLED_VALUE) {
        panic!(
            "{PANIC_TEST_PROPERTY}={ENABLED_VALUE} のため意図的に panic しました（検証用。\
             `adb shell setprop {PANIC_TEST_PROPERTY} 0` で無効化）"
        );
    }
}

/// `debug.seed.touch_test` が有効なら、エンジンへ複数指の合成タッチ列の再生を要求する。
///
/// adb から複数指を注入できない実機（非 root では sendevent が SELinux で拒否される）で、
/// 複数指の追跡とタッチ由来のマウス状態を確かめるため。列の中身と再生の仕組みはエンジン側の
/// `engine/core/input/touch/test_sequence.rs`。結果はログの `[SEED TOUCH TEST]` / `[SEED TOUCH FRAME]` で見る。
pub fn request_touch_test_if_enabled() {
    if sysprop::get(TOUCH_TEST_PROPERTY).as_deref() == Some(ENABLED_VALUE) {
        logcat::info(&format!(
            "{TOUCH_TEST_PROPERTY}={ENABLED_VALUE} のため、最初のフレームの 2 秒後に合成タッチ列を 1 回流します\
             （検証用。`adb shell setprop {TOUCH_TEST_PROPERTY} 0` で無効化）"
        ));
        seed_engine::engine::core::input::touch::test_sequence::request();
    }
}
