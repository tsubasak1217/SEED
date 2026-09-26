// ============================================================
//  platform_bridge/jni_exports.rs — Java の SeedPlatform から呼ばれるネイティブ関数（JNI。W1-1）
//
//  既存の 4 関数（crate 直下の jni_exports.rs。生ポインタで受ける流儀）とは別に、プラットフォーム機能の 2 本はここに置く。
//  こちらは jni クレート 0.22 の型（EnvUnowned・JClass・JByteArray。どれも JNI の生の値と同じ並びの FFI 安全な包み）で受け、
//  EnvUnowned::with_env で Env を得る（with_env は中の panic を受け止める。JNI の境界を panic で越えない）。
//  失敗はログだけで続け、Java へ例外を残さない（残っていれば消す）。名前は JNI の命名規則
//  Java_com_seedengine_runtime_platform_SeedPlatform_<メソッド> で Java の private static native に結び付く。
//
//  【nativeRegisterPlatformBridge(Class)】MainActivity.onCreate（super.onCreate の前）→ SeedPlatform.init から 1 回。
//  【nativeOnPlatformEvent(byte[])】Java の背面のスレッド（SEEDPlatform）から、イベント 1 件ごと。箱へ積むだけ。
// ============================================================

use jni::objects::{JByteArray, JClass};
use jni::{EnvUnowned, Outcome};

use seed_engine::engine::platform::bridge::LOG_PREFIX;

use super::{inbox, java_bridge};
use crate::logcat;

/// `SeedPlatform.nativeRegisterPlatformBridge(Class<?> cls)`（Java の `private static native void`）の実体。
///
/// # 引数
/// * `unowned_env`  - JNIEnv（jni クレートの FFI 安全な包み）
/// * `_class`       - 呼び出し元のクラス（static メソッドなので SeedPlatform 自身。使わない）
/// * `bridge_class` - Java が渡す SeedPlatform.class（invoke を持つクラス。明示して受け取る）
#[unsafe(no_mangle)]
pub extern "system" fn Java_com_seedengine_runtime_platform_SeedPlatform_nativeRegisterPlatformBridge<'caller>(
    mut unowned_env: EnvUnowned<'caller>,
    _class: JClass<'caller>,
    bridge_class: JClass<'caller>,
) {
    let outcome = unowned_env.with_env(|env| -> Result<java_bridge::RegisterOutcome, jni::errors::Error> {
        let result = java_bridge::register(env, &bridge_class);
        if result.is_err() && env.exception_check() {
            // 失敗の途中の Java の例外を Java（SeedPlatform.init）へ持ち越さない
            env.exception_clear();
        }
        result
    });
    match outcome.into_outcome() {
        Outcome::Ok(java_bridge::RegisterOutcome::Registered) => {
            logcat::info(&format!("{LOG_PREFIX} Java の SeedPlatform を登録しました（SEED.Platform が使えます）"));
        }
        Outcome::Ok(java_bridge::RegisterOutcome::AlreadyRegistered) => {
            logcat::info(&format!("{LOG_PREFIX} Java の SeedPlatform は登録済みです（最初の登録のまま）"));
        }
        Outcome::Err(err) => {
            logcat::error(&format!("{LOG_PREFIX} Java の SeedPlatform を登録できませんでした（SEED.Platform は使えません）: {err}"));
        }
        Outcome::Panic(_) => {
            logcat::error(&format!("{LOG_PREFIX} Java の SeedPlatform の登録の途中で panic しました（SEED.Platform は使えません）"));
        }
    }
}

/// `SeedPlatform.nativeOnPlatformEvent(byte[] jsonUtf8)`（Java の `private static native void`）の実体。
///
/// # 引数
/// * `unowned_env` - JNIEnv（jni クレートの FFI 安全な包み）
/// * `_class`      - SeedPlatform のクラス（使わない）
/// * `json_utf8`   - イベントの JSON（UTF-8 の byte[]。null なら捨てる）
#[unsafe(no_mangle)]
pub extern "system" fn Java_com_seedengine_runtime_platform_SeedPlatform_nativeOnPlatformEvent<'caller>(
    mut unowned_env: EnvUnowned<'caller>,
    _class: JClass<'caller>,
    json_utf8: JByteArray<'caller>,
) {
    let outcome = unowned_env.with_env(|env| -> Result<Vec<u8>, jni::errors::Error> {
        // null は convert_byte_array が Err にする（例外は投げない API）
        env.convert_byte_array(&json_utf8)
    });
    match outcome.into_outcome() {
        Outcome::Ok(bytes) => {
            // 箱へ積むだけ（Mutex の出し入れ）。念のため panic をここで止める
            if std::panic::catch_unwind(move || inbox::receive(bytes)).is_err() {
                logcat::error(&format!("{LOG_PREFIX} イベントを積む途中で panic しました（捨てます）"));
            }
        }
        Outcome::Err(err) => {
            logcat::warn(&format!("{LOG_PREFIX} イベントの byte[] を読めませんでした（捨てます）: {err}"));
        }
        Outcome::Panic(_) => {
            logcat::error(&format!("{LOG_PREFIX} イベントの受け取りの途中で panic しました（捨てます）"));
        }
    }
}
