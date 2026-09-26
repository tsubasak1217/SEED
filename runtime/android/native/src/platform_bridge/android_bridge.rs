// ============================================================
//  platform_bridge/android_bridge.rs — エンジンの PlatformBridge の Android の実装（W1-1。W1-3 で目覚ましの前処理）
//
//  エンジン（seed_engine::engine::platform::bridge）は OS の違いを trait PlatformBridge で隠す。ここはその Android の中身で、
//    invoke      → java_bridge::invoke（JNI → Java の SeedPlatform.invoke → :seed_platform）
//                  alarm.schedule だけは先に alarm_prep で音源を書き出して sound_path に置き換える（W1-3）
//    poll_events → inbox::take_all（Java の nativeOnPlatformEvent が積んだもの）
//    is_available → Java の登録（nativeRegisterPlatformBridge）が済んでいるか
//  をつなぐだけ。登録は android_main が App を作る前に mod.rs の install で行う。
//  reset_session（Play の区切り）は既定の「何もしない」のまま（実機の予約はエディタの Play と関係ない）。
// ============================================================

use seed_engine::engine::platform::bridge::wire::alarm as alarm_names;
use seed_engine::engine::platform::bridge::{PlatformBridge, PlatformBridgeKind};

use super::alarm_prep::{self, Prepared};
use super::{inbox, java_bridge};

/// Android の PlatformBridge（状態は java_bridge と inbox の static が持つ）。
pub struct AndroidPlatformBridge;

impl PlatformBridge for AndroidPlatformBridge {
    fn kind(&self) -> PlatformBridgeKind {
        PlatformBridgeKind::Device
    }

    fn is_available(&self) -> bool {
        java_bridge::is_registered()
    }

    fn invoke(&self, module: &str, method: &str, json: &str) -> Result<String, String> {
        if module == alarm_names::MODULE && method == alarm_names::METHOD_SCHEDULE {
            // 音源（assets://）を :seed_platform が読める実ファイルへ書き出してから送る
            return match alarm_prep::prepare_schedule(json) {
                Prepared::Send(prepared) => java_bridge::invoke(module, method, &prepared),
                Prepared::Reply(reply) => Ok(reply),
            };
        }
        java_bridge::invoke(module, method, json)
    }

    fn poll_events(&self) -> Vec<String> {
        inbox::take_all()
    }
}
