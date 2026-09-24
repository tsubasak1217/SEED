// ============================================================
//  debug_hooks.rs — 検証用フック（adb から切り替える）
//
//  通常起動では何もしない。システムプロパティは次のように端末側で切り替える:
//    adb shell setprop debug.seed.panic_test 1   … 次回起動時に意図的に panic する
//    adb shell setprop debug.seed.panic_test 0   … 元に戻す
//  （debug.* のプロパティは adb shell から書ける。端末の再起動で消える）
// ============================================================

use crate::sysprop;

/// 意図的に panic させるかを決めるシステムプロパティ名。
const PANIC_TEST_PROPERTY: &str = "debug.seed.panic_test";

/// 意図的 panic を有効にする値。
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
