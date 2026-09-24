// ============================================================
//  logcat/panic_hook.rs — panic を logcat へ確実に残す
//
//  既定の panic フックは標準エラーへ書くが、標準エラーは転送スレッド経由で非同期に
//  logcat へ届くため、panic 直後にプロセスが終わると最後の数行を取りこぼし得る。
//  そこで panic フックを差し替え、liblog へ同期で直接書く（標準エラーへは書かない＝二重に出さない）。
//
//  メインスレッド（android_main）の panic は android-activity が catch_unwind で受け止め、
//  Activity を finish する。その後 MainActivity.onDestroy がプロセスを終了させる。
// ============================================================

use super::liblog::{self, Priority};

/// panic 値が文字列でなかったときに出す文言。
const NON_STRING_PAYLOAD: &str = "(文字列以外の panic 値)";

/// スレッド名が無いときに出す文言。
const UNNAMED_THREAD: &str = "<名前なし>";

/// panic フックを差し替える。
pub fn install() {
    std::panic::set_hook(Box::new(|info| {
        let thread = std::thread::current();
        let thread_name = thread.name().unwrap_or(UNNAMED_THREAD);
        let message = info.payload_as_str().unwrap_or(NON_STRING_PAYLOAD);
        let location = info
            .location()
            .map(|loc| format!("{}:{}:{}", loc.file(), loc.line(), loc.column()))
            .unwrap_or_else(|| "(場所不明)".to_string());
        liblog::write(
            Priority::Error,
            &format!("[SEED PANIC] thread '{thread_name}' panicked at {location}: {message}"),
        );

        // バックトレース。APK へ入る .so は Gradle がシンボルを削るため多くは <unknown> になるが、
        // 未ストリップの jniLibs/<abi>/libSEED.so と llvm-addr2line で後から解決できる。
        let backtrace = std::backtrace::Backtrace::force_capture();
        liblog::write(Priority::Error, &format!("[SEED PANIC] backtrace:\n{backtrace}"));
    }));
}
