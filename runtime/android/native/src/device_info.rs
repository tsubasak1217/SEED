// ============================================================
//  device_info.rs — 起動時の端末情報ログ
//
//  「どの端末・どの OS・どの ABI で動いたか」を最初に残しておくと、端末ごとの不具合の
//  切り分けが速い（Windows 版の startup_log の環境情報に相当）。
// ============================================================

use winit::platform::android::activity::AndroidApp;

use crate::{logcat, sysprop};

/// ログへ残すシステムプロパティ（表示名, プロパティ名）。
const DEVICE_PROPERTIES: &[(&str, &str)] = &[
    ("メーカー", "ro.product.manufacturer"),
    ("機種", "ro.product.model"),
    ("Android", "ro.build.version.release"),
    ("ABI", "ro.product.cpu.abi"),
    ("GPU(EGL)", "ro.hardware.egl"),
    ("Vulkan ドライバ", "ro.hardware.vulkan"),
];

/// 端末情報とアプリのデータパスをログへ出す。
pub fn log(app: &AndroidApp) {
    logcat::info(&format!(
        "端末: SDK={} / ビルド ABI={}",
        AndroidApp::sdk_version(),
        std::env::consts::ARCH
    ));
    for (label, property) in DEVICE_PROPERTIES {
        let value = sysprop::get(property).unwrap_or_else(|| "(不明)".to_string());
        logcat::info(&format!("端末: {label}={value}"));
    }
    logcat::info(&format!(
        "データパス: internal={:?} external={:?}",
        app.internal_data_path(),
        app.external_data_path()
    ));
}
