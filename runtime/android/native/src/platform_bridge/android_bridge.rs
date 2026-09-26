// ============================================================
//  platform_bridge/android_bridge.rs — エンジンの PlatformBridge の Android の実装（W1-1）
//
//  エンジン（seed_engine::engine::platform::bridge）は OS の違いを trait PlatformBridge で隠す。ここはその Android の中身で、
//    invoke      → java_bridge::invoke（JNI → Java の SeedPlatform.invoke → :seed_platform）
//    poll_events → inbox::take_all（Java の nativeOnPlatformEvent が積んだもの）
//    is_available → Java の登録（nativeRegisterPlatformBridge）が済んでいるか
//  をつなぐだけ。登録は android_main が App を作る前に mod.rs の install で行う。
// ============================================================

use seed_engine::engine::platform::bridge::{PlatformBridge, PlatformBridgeKind};

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
        java_bridge::invoke(module, method, json)
    }

    fn poll_events(&self) -> Vec<String> {
        inbox::take_all()
    }
}
