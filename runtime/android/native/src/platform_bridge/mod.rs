// ============================================================
//  platform_bridge/mod.rs — アプリのプラットフォーム機能（SEED.Platform）の Android の糊（W1-1 橋渡し）
//
//  【全体の流れ】（docs/android.md §25・docs/app_platform_roadmap.md §2.2）
//    C# SEED.Platform → エンジンの host_api（ffi_platform_invoke）→ engine::platform::bridge::invoke
//      → AndroidPlatformBridge（android_bridge.rs）→ java_bridge::invoke（JNI。jni クレート 0.22）
//      → Java の SeedPlatform.invoke → PlatformConnection（ContentProviderClient を持ち続けて call）→ :seed_platform の PlatformProvider
//    :seed_platform の記録 → Binder の呼び鈴 → Java の PlatformConnection が未読を取り出す
//      → SeedPlatform.nativeOnPlatformEvent（jni_exports.rs）→ inbox（箱）→ エンジンがフレームの頭で取り出してスクリプトへ
//
//  【構成】
//    android_bridge … エンジンの trait PlatformBridge の Android の実装（下をつなぐだけ）
//    alarm_prep     … alarm.schedule を送る前の前処理（音源〈assets://〉を :seed_platform が読める実ファイルへ書き出す。W1-3）
//    java_bridge    … native → Java（SeedPlatform のクラスの GlobalRef・invoke のメソッド ID・スレッドの attach）
//    inbox          … Java → native のイベントの箱（形の検査つき）
//    jni_exports    … Java から呼ばれる 2 本（nativeRegisterPlatformBridge・nativeOnPlatformEvent）
//
//  jni クレートはこのクレート（Android の糊）だけが使い、エンジン本体（seed_engine）には入れない。
// ============================================================

mod alarm_prep;
mod android_bridge;
mod inbox;
mod java_bridge;
mod jni_exports;

use seed_engine::engine::platform::bridge::{self, LOG_PREFIX};

use crate::logcat;

/// エンジンへ Android の PlatformBridge を登録する（android_main が App を作る前に 1 回）。
///
/// Java 側の登録（nativeRegisterPlatformBridge）は MainActivity.onCreate が先に済ませている。済んでいなくても
/// 登録はしておき、スクリプトからは `IsSupported == false`（呼び出しは platform_unavailable）に見える。
pub fn install() {
    if bridge::register_bridge(Box::new(android_bridge::AndroidPlatformBridge)) {
        logcat::info(&format!(
            "{LOG_PREFIX} エンジンへ Android の橋渡しを登録しました（Java の登録: {}）",
            if java_bridge::is_registered() { "済み" } else { "まだ" }
        ));
    } else {
        logcat::warn(&format!("{LOG_PREFIX} エンジンには既に別の橋渡しが登録されていました（そのまま使います）"));
    }
}
