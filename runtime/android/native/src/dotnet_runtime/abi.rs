// ============================================================
//  dotnet_runtime/abi.rs — 動いているプロセスの ABI 名（APK の中のフォルダ名）
//
//  APK の lib/<ABI>/（.so）と assets/seed/dotnet/<ABI>/（同梱 .NET の目録と BCL）は Android の ABI 名で並ぶ。
//  2 ABI 入りの APK（開発用）でも、自分の ABI の同梱 .NET だけを展開する。
//  SeedAndroid の ABI の表（editor/src/Android/Common/AndroidAbi.cs。arm64-v8a / x86_64）と
//  runtime/android/dotnet_runtime.json の abis と一致させる。
// ============================================================

/// Rust のアーキテクチャ名（std::env::consts::ARCH）→ Android の ABI 名。
const ABI_TABLE: &[(&str, &str)] = &[
    // 実機（配布対象）
    ("aarch64", "arm64-v8a"),
    // PC のエミュレータ（開発用）
    ("x86_64", "x86_64"),
];

/// このプロセスの ABI 名。表に無いアーキテクチャ（同梱 .NET を作っていない）なら None。
pub fn current_abi() -> Option<&'static str> {
    abi_for_arch(std::env::consts::ARCH)
}

/// アーキテクチャ名から ABI 名を引く【純関数】。
///
/// # 引数
/// * `arch` - Rust のアーキテクチャ名（`aarch64` / `x86_64` 等）
fn abi_for_arch(arch: &str) -> Option<&'static str> {
    ABI_TABLE.iter().find(|(name, _)| *name == arch).map(|(_, abi)| *abi)
}
