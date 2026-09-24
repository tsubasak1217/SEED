// ============================================================
//  embedded_runtime/mod.rs — アプリに同梱した .NET ランタイム（Android の CoreCLR / Mono）
//
//  【構成】
//    manifest … APK に入れた同梱 .NET の目録（bundle.json）の読み取りと検査
//    install  … 目録どおりに端末のファイルとして並べる（files/dotnet/<種類>-<版>-<content_id>/）
//  並べた後の CLR の起動は clr_host/embedded.rs、スクリプトの DLL の置き場は script_binaries.rs。
//
//  【配布物の中の置き場（APK の assets/seed/ から見た相対）】
//    dotnet/<ABI>/bundle.json          … 目録
//    dotnet/<ABI>/<dotnet-root 内の相対パス> … BCL の DLL・deps.json・runtimeconfig（asset）
//  .so（hostfxr・hostpolicy・coreclr・clrjit・System.*.Native）は APK の lib/<ABI>/ に入る（native_library）。
//  置き場を変えるときは runtime/android/build_and_run.ps1 の $DotnetBundleDirName / $DotnetBundleManifestName も直す。
//
//  全体像は docs/android.md §17。
// ============================================================

pub mod install;
pub mod manifest;

/// 配布物のルート直下の、同梱 .NET のフォルダ名（`dotnet/<ABI>/`）。
pub const BUNDLE_DIR_NAME: &str = "dotnet";

/// 同梱 .NET の目録のファイル名（`dotnet/<ABI>/bundle.json`）。
pub const MANIFEST_FILE_NAME: &str = "bundle.json";

/// 端末のアプリ専用フォルダ（files）の中の、展開先の親フォルダ名（`files/dotnet/`）。
pub const INSTALL_ROOT_DIR_NAME: &str = "dotnet";

/// 配布物のルートから見た、その ABI の同梱 .NET のフォルダ（`dotnet/<ABI>`）【純関数】。
///
/// # 引数
/// * `abi` - Android の ABI 名（`arm64-v8a` / `x86_64`）
pub fn bundle_dir_for_abi(abi: &str) -> String {
    format!("{BUNDLE_DIR_NAME}/{abi}")
}

/// 配布物のルートから見た、その ABI の目録のパス（`dotnet/<ABI>/bundle.json`）【純関数】。
///
/// # 引数
/// * `abi` - Android の ABI 名
pub fn manifest_path_for_abi(abi: &str) -> String {
    format!("{}/{MANIFEST_FILE_NAME}", bundle_dir_for_abi(abi))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 置き場の名前は build_and_run.ps1 の書き出し先と一致させる（ずれると APK の .NET が見つからない）。
    #[test]
    fn bundle_paths_match_build_script_contract() {
        assert_eq!(bundle_dir_for_abi("arm64-v8a"), "dotnet/arm64-v8a");
        assert_eq!(manifest_path_for_abi("x86_64"), "dotnet/x86_64/bundle.json");
        assert_eq!(INSTALL_ROOT_DIR_NAME, "dotnet");
    }
}
