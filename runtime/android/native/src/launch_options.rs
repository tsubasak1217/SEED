// ============================================================
//  launch_options.rs — JNI で受け取った起動オプション（JSON）を android_main まで預かる（段階C-3）
//
//  【流れ】
//    MainActivity.onCreate の最初（super.onCreate より前。ネイティブのスレッドが無いうち）
//      → nativeSetLaunchOptions(byte[] UTF-8 の JSON)（jni_exports.rs）→ receive（ここに預ける）
//    android_main → launch.rs → take（預かったものを読んで LaunchOptions にする。1 回だけ）
//  JSON の書式・シーンの判断は エンジンの engine::platform::launch_options（純粋な処理・単体テスト付き）。
//  受け取りは UI スレッド、読み出しは android_main のスレッドなので Mutex で渡す。
// ============================================================

use std::sync::Mutex;

use seed_engine::engine::platform::launch_options::LaunchOptions;

use crate::logcat;

/// 預かった JSON（受け取っていなければ None）。
static RECEIVED: Mutex<Option<String>> = Mutex::new(None);

/// ログの印（起動オプションの行を logcat で探しやすくする）。
const LOG_PREFIX: &str = "[SEED LAUNCH]";

/// JNI から受け取った UTF-8 の JSON を預かる（読めない・無ければ警告だけ）。
///
/// # 引数
/// * `bytes` - Java の byte[] の中身（null なら None）
pub fn receive(bytes: Option<Vec<u8>>) {
    let Some(bytes) = bytes else {
        logcat::warn(&format!("{LOG_PREFIX} 起動オプションを読めませんでした（既定のまま起動します）"));
        return;
    };
    match String::from_utf8(bytes) {
        Ok(json) => {
            // 接続トークン（ipc_token）の値は logcat へ出さない（段階D-1）
            logcat::info(&format!(
                "{LOG_PREFIX} 起動オプションを受け取りました: {}",
                LaunchOptions::masked_json_for_log(&json)
            ));
            if let Ok(mut slot) = RECEIVED.lock() {
                *slot = Some(json);
            }
        }
        Err(err) => logcat::warn(&format!("{LOG_PREFIX} 起動オプションが UTF-8 ではありません（無視します）: {err}")),
    }
}

/// 預かった起動オプションを取り出す（無い・読めなければ既定値。読めなければ警告を出す）。
pub fn take() -> LaunchOptions {
    let json = RECEIVED.lock().ok().and_then(|mut slot| slot.take());
    let Some(json) = json else {
        return LaunchOptions::default();
    };
    LaunchOptions::from_json(&json).unwrap_or_else(|err| {
        logcat::warn(&format!(
            "{LOG_PREFIX} 起動オプションの JSON を読めません（既定のまま起動します）: {err}  JSON: {}",
            LaunchOptions::masked_json_for_log(&json)
        ));
        LaunchOptions::default()
    })
}
