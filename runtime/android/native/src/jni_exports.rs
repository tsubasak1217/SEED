// ============================================================
//  jni_exports.rs — Java（MainActivity）から呼ばれるネイティブ関数（JNI）
//
//  【仕組み】
//  MainActivity は static 初期化子で System.loadLibrary("SEED") している。その Java クラスの
//  `native` メソッドは、JNI の命名規則 `Java_<パッケージ>_<クラス>_<メソッド>` の名前で libSEED.so から
//  書き出された C ABI の関数へ自動で結び付く（RegisterNatives も jni クレートも要らない）。
//  引数の JNIEnv* / jclass は使わないので生ポインタのまま受け取る（依存クレートを増やさない）。
//
//  【nativeFlushSaveData】
//  MainActivity.onDestroy がプロセスを終える（Process.killProcess）直前に UI スレッドから呼ぶ。
//  セーブの未書き出し分を同期でディスクへ書く保険。通常は背面へ回った時点（suspended。
//  エンジンの app/background_lifecycle.rs）で書き出し済みで、ここでは「変更なし」になる。
//  セーブのストアは Mutex で守られているので、UI スレッドから呼んでも android_main のスレッドと競合しない。
//  ログは標準エラーの転送スレッドを経由せず liblog へ直接書く（直後にプロセスが終わっても消えないように）。
// ============================================================

use std::ffi::c_void;

use seed_engine::engine::core::save;

use crate::logcat;

/// `MainActivity.nativeFlushSaveData()`（Java の `private static native void`）の実体。
///
/// # 引数
/// * `_env`   - JNIEnv*（使わない）
/// * `_class` - MainActivity の jclass（static メソッドなのでインスタンスではない。使わない）
#[unsafe(no_mangle)]
pub extern "system" fn Java_com_seedengine_runtime_MainActivity_nativeFlushSaveData(
    _env: *mut c_void,
    _class: *mut c_void,
) {
    // JNI の境界を panic で越えると Java 側ごと未定義の壊れ方をする（Rust では abort）ため、
    // ここで受け止めてログに残し、Java 側はそのままプロセスを終える。
    match std::panic::catch_unwind(save::flush_if_dirty) {
        Ok(outcome) => logcat::info(&format!("[SEED SAVE] onDestroy（プロセス終了前）: {}", outcome.describe())),
        Err(_) => logcat::error("[SEED SAVE] onDestroy（プロセス終了前）: セーブの書き出し中に panic しました"),
    }
}
