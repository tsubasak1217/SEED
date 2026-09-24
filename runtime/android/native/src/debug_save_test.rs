// ============================================================
//  debug_save_test.rs — 検証用フック: セーブが「背面へ回るとき」「プロセスを終える前」に書き出されるかの確認
//
//  スクリプトが Android でまだ動かない（段階B）ため、セーブを書き換える役をこのフックが代わりに行う。
//  システムプロパティ debug.seed.save_test が無い・"1"/"2" 以外なら何もしない（本番の経路には一切触らない。
//  使うのはエンジンの公開 API の save::get_int / set_int / resolve_save_path と background_gate::is_background だけ）。
//
//  【モード 1】adb shell setprop debug.seed.save_test 1
//    起動時にカウンタ（COUNTER_KEY）を 1 増やす。メモリ上だけで、ディスクへはまだ書かない。
//      ホームへ戻す（suspended で書き出されるはず）→ am kill → 再起動 → 増えた値が読めれば成功。
//      比較用: 前面のまま am force-stop → 再起動 → 増える前の値のまま（書き出しは背面へ回るときだけ）。
//  【モード 2】adb shell setprop debug.seed.save_test 2
//    モード 1 に加え、背面へ回るたびに（suspended の書き出しが済んだ後で）LATE_KEY を書き換える。
//    この値がディスクへ届く経路は「次の suspended」か「MainActivity.onDestroy の JNI フラッシュ」だけ。
//      ホームへ戻す → Activity を破棄させる（最近のタスクから消す等）→ 再起動 → LATE_KEY が読めれば
//      onDestroy の JNI フラッシュが効いている。比較用: ホームへ戻す → am kill → LATE_KEY は前回の値のまま。
//  結果はログの `[SEED SAVE TEST]` / `[SEED SAVE]` で見る。確認後は `adb shell setprop debug.seed.save_test 0`。
// ============================================================

use std::time::Duration;

use seed_engine::engine::core::background_gate;
use seed_engine::engine::core::save;

use crate::{logcat, sysprop};

/// フックの有効・モードを決めるシステムプロパティ名。
const SAVE_TEST_PROPERTY: &str = "debug.seed.save_test";

/// モード 1（起動時にカウンタを増やすだけ）を表す値。
const MODE_COUNTER: &str = "1";

/// モード 2（加えて、背面へ回った後に LATE_KEY を書き換える）を表す値。
const MODE_COUNTER_AND_LATE_WRITE: &str = "2";

/// 起動のたびに 1 増やすセーブのキー（ゲームのキーと衝突しない名前）。
const COUNTER_KEY: &str = "__seed_debug_save_test_counter";

/// 背面へ回った後に書き換えるセーブのキー（onDestroy の JNI フラッシュでしかディスクへ届かない値）。
const LATE_KEY: &str = "__seed_debug_save_test_late";

/// 背面・前面の切り替わりを見張る間隔（検証用のスレッドだけが使う）。
const WATCH_INTERVAL: Duration = Duration::from_millis(5);

/// 見張りスレッドの名前（logcat の tid 表示・デバッガでの識別用）。
const WATCH_THREAD_NAME: &str = "seed-save-test";

/// `debug.seed.save_test` が有効なら、起動時のセーブを読んでカウンタを書き換える（モード 2 なら見張りも立てる）。
///
/// エンジンの書き込み先（app_dirs::init）を設定した後に呼ぶこと（セーブの置き場が決まってから読むため）。
pub fn run_if_enabled() {
    let Some(mode) = sysprop::get(SAVE_TEST_PROPERTY) else { return };
    let late_write = match mode.as_str() {
        MODE_COUNTER => false,
        MODE_COUNTER_AND_LATE_WRITE => true,
        _ => return,
    };

    let counter = save::get_int(COUNTER_KEY);
    let late = save::get_int(LATE_KEY);
    logcat::info(&format!(
        "[SEED SAVE TEST] {SAVE_TEST_PROPERTY}={mode}: 起動時に読んだ値 {COUNTER_KEY}={counter:?} {LATE_KEY}={late:?}（{}）",
        save::resolve_save_path().display()
    ));

    let next = counter.unwrap_or(0) + 1;
    save::set_int(COUNTER_KEY, next);
    logcat::info(&format!(
        "[SEED SAVE TEST] {COUNTER_KEY} をメモリ上で {next} に書き換えました（まだディスクへは書いていません。\
         背面へ回ると書き出されるはず。`adb shell setprop {SAVE_TEST_PROPERTY} 0` で無効化）"
    ));

    if late_write {
        spawn_late_writer(next);
    }
}

/// 背面へ回るたびに LATE_KEY を書き換える見張りスレッドを立てる（モード 2）。
///
/// エンジンは suspended でセーブを書き出してから背面の印（background_gate）を立てるので、
/// 印を見てから書いた値は、その suspended の書き出しには含まれない。
fn spawn_late_writer(value: i64) {
    let spawned = std::thread::Builder::new()
        .name(WATCH_THREAD_NAME.to_string())
        .spawn(move || loop {
            wait_until(background_gate::is_background);
            save::set_int(LATE_KEY, value);
            logcat::info(&format!(
                "[SEED SAVE TEST] 背面へ回った後に {LATE_KEY} をメモリ上で {value} に書き換えました\
                 （suspended の書き出しの後なので、ディスクへ届くのは onDestroy の JNI フラッシュか次の suspended だけ）"
            ));
            wait_until(|| !background_gate::is_background());
        });
    if let Err(err) = spawned {
        logcat::warn(&format!("[SEED SAVE TEST] 見張りスレッドを起動できませんでした: {err}"));
    }
}

/// 条件が真になるまで一定間隔で見張る。
fn wait_until(condition: impl Fn() -> bool) {
    while !condition() {
        std::thread::sleep(WATCH_INTERVAL);
    }
}
