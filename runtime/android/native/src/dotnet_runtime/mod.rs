// ============================================================
//  dotnet_runtime/mod.rs — APK に同梱した .NET を使えるようにし、エンジンへ渡す起動材料を作る（段階B）
//
//  【流れ】（android_main から App を作る前に 1 回。docs/android.md §17。番号は役割の番号で、3 は 2 より先に行う）
//    1. 自分の ABI の目録（APK の assets/seed/dotnet/<ABI>/bundle.json）を読む
//         無ければ「.NET を入れていない APK」としてスクリプト無しで起動する
//    2. 目録どおりに files/dotnet/<種類>-<版>-<content_id>/ へ並べる（初回だけ。2 回目以降は印と asset を確かめて使い回し、
//       .so だけ確かめて直す）
//         BCL の DLL・deps.json … APK の assets から複製
//         .so（hostfxr・hostpolicy・coreclr・clrjit・System.*.Native）… nativeLibraryDir へのシンボリックリンク（既定）か複製
//         （engine::core::scripting::embedded_runtime::install。単体テスト付き。置き方の比較は docs/android.md §17.4）
//    3. スクリプトの DLL の置き場を選ぶ（files/bin → 外部の files/bin → APK の bin/。script_sources.rs）
//    4. その置き場の SEEDScripting.runtimeconfig.json を展開先の app/ へ写す（hostfxr にはファイルのパスが要る）
//    5. 起動材料（EmbeddedClrHost）を返す → LaunchArgs.embedded_clr → App が CLR を起動する
//  どの段階で失敗しても理由をログに残して None を返す（エンジンはスクリプト無しで起動を続ける）。
//
//  ヒープポインタのタグ付けの無効化（実機 arm64 の必須対策）は、マニフェストの属性と、CLR の起動直前の
//  mallopt（エンジンの clr_host/heap_tagging.rs）の 2 段で行う。
// ============================================================

mod abi;
mod native_library_dir;
mod script_sources;

use std::sync::Arc;
use std::time::Instant;

use seed_engine::engine::core::scripting::EmbeddedClrHost;
use seed_engine::engine::core::scripting::embedded_runtime::{
    self, INSTALL_ROOT_DIR_NAME,
    install::{self, InstallRequest, InstalledRuntime},
    manifest::BundleManifest,
};
use seed_engine::engine::core::scripting::script_binaries::SCRIPTING_HOST_RUNTIME_CONFIG_NAME;
use seed_engine::engine::package_source::PackageSource;
use winit::platform::android::activity::AndroidApp;

use crate::apk_package::ApkPackageSource;
use crate::logcat;

/// 秒 → ミリ秒（ログ用）。
const MILLIS_PER_SECOND: f64 = 1000.0;

/// バイト → MiB（ログ用）。
const BYTES_PER_MIB: f64 = 1024.0 * 1024.0;

/// 同梱 .NET を使えるようにして、CLR の起動材料を返す。
///
/// # 引数
/// * `app` - APK（AssetManager）・アプリ専用フォルダを知るため
///
/// # 戻り値
/// 起動材料。APK に .NET が無い・展開に失敗した・スクリプトの DLL が無いときは None。
pub fn prepare(app: &AndroidApp) -> Option<EmbeddedClrHost> {
    let started = Instant::now();
    let package: Arc<ApkPackageSource> = Arc::new(ApkPackageSource::new(app.asset_manager()));

    // ── 1. 目録 ──
    let abi = abi::current_abi().or_else(|| {
        logcat::warn(&format!(
            "[SEED DOTNET] このアーキテクチャ（{}）の同梱 .NET はありません。スクリプト無しで起動します",
            std::env::consts::ARCH
        ));
        None
    })?;
    let manifest = read_manifest(package.as_ref(), abi)?;

    // ── 3. スクリプトの DLL の置き場（展開より先に決める。どこにも無ければ .NET を展開する意味が無いので何もしない）──
    let binaries = script_sources::choose(app, package.clone())?;

    // ── 2. 展開 ──
    let installed = install_runtime(app, package.as_ref(), abi, &manifest)?;

    // ── 4. runtimeconfig を展開先の app/ へ写す ──
    let config_bytes = match binaries.read(SCRIPTING_HOST_RUNTIME_CONFIG_NAME) {
        Ok(bytes) => bytes,
        Err(err) => {
            logcat::error(&format!(
                "[SEED DOTNET] {} を読めません（{err}）。スクリプト無しで起動します",
                binaries.describe(SCRIPTING_HOST_RUNTIME_CONFIG_NAME)
            ));
            return None;
        }
    };
    let runtime_config_path = match install::write_host_runtime_config(
        &installed.dotnet_root,
        SCRIPTING_HOST_RUNTIME_CONFIG_NAME,
        &config_bytes,
    ) {
        Ok(path) => path,
        Err(err) => {
            logcat::error(&format!(
                "[SEED DOTNET] runtimeconfig を {} へ書けません（{err}）。スクリプト無しで起動します",
                installed.dotnet_root.display()
            ));
            return None;
        }
    };

    logcat::info(&format!(
        "[SEED DOTNET] 同梱 .NET の準備ができました: {}（{:.1} ms）",
        manifest.describe(),
        started.elapsed().as_secs_f64() * MILLIS_PER_SECOND
    ));
    Some(EmbeddedClrHost {
        hostfxr_path: installed.hostfxr_path,
        dotnet_root: installed.dotnet_root,
        runtime_config_path,
        runtime_properties: manifest.runtime_properties.clone().into_iter().collect(),
        binaries,
        label: manifest.describe(),
    })
}

/// 自分の ABI の目録を APK から読む。
///
/// 無ければ（.NET を入れていない APK）情報として 1 行残して None。壊れていればエラーとして残して None。
fn read_manifest(package: &ApkPackageSource, abi: &str) -> Option<BundleManifest> {
    let path = embedded_runtime::manifest_path_for_abi(abi);
    let bytes = match package.read_all(&path) {
        Ok(bytes) => bytes,
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => {
            logcat::info(&format!(
                "[SEED DOTNET] APK に同梱 .NET（{}）がありません。スクリプト無しで起動します（build_and_run.ps1 が APK に入れる。docs/android.md §17）",
                package.describe(&path)
            ));
            return None;
        }
        Err(err) => {
            logcat::error(&format!("[SEED DOTNET] {} を読めません: {err}", package.describe(&path)));
            return None;
        }
    };
    match BundleManifest::parse(&bytes) {
        Ok(manifest) => {
            if manifest.abi != abi {
                logcat::warn(&format!(
                    "[SEED DOTNET] 目録の ABI（{}）がこのプロセス（{abi}）と違います。そのまま使います",
                    manifest.abi
                ));
            }
            Some(manifest)
        }
        Err(err) => {
            logcat::error(&format!("[SEED DOTNET] {}: {err}。スクリプト無しで起動します", package.describe(&path)));
            None
        }
    }
}

/// 目録どおりに files/dotnet/ へ展開する（済んでいれば何もしない）。結果をログに残す。
fn install_runtime(
    app: &AndroidApp,
    package: &ApkPackageSource,
    abi: &str,
    manifest: &BundleManifest,
) -> Option<InstalledRuntime> {
    let Some(files_dir) = app.internal_data_path() else {
        logcat::error("[SEED DOTNET] 内部データフォルダ（internal_data_path）が取得できません。スクリプト無しで起動します");
        return None;
    };
    let native_dir = match native_library_dir::find() {
        Ok(dir) => Some(dir),
        Err(reason) => {
            // .so を含まない目録なら無くても展開できるので、ここでは警告に留める（要るなら install が失敗を返す）。
            logcat::warn(&format!("[SEED DOTNET] nativeLibraryDir: {reason}"));
            None
        }
    };
    let install_root = files_dir.join(INSTALL_ROOT_DIR_NAME);
    let bundle_dir = embedded_runtime::bundle_dir_for_abi(abi);
    let started = Instant::now();
    let request = InstallRequest {
        manifest,
        install_root: &install_root,
        package: package as &dyn PackageSource,
        bundle_dir: &bundle_dir,
        native_library_dir: native_dir.as_deref(),
    };
    let installed = match install::install(&request) {
        Ok(installed) => installed,
        Err(err) => {
            logcat::error(&format!(
                "[SEED DOTNET] 同梱 .NET（{}）を {} へ展開できません: {err}。スクリプト無しで起動します",
                manifest.describe(),
                install_root.display()
            ));
            return None;
        }
    };
    let elapsed_ms = started.elapsed().as_secs_f64() * MILLIS_PER_SECOND;
    if installed.reused {
        logcat::info(&format!(
            "[SEED DOTNET] 展開済みの .NET を使います: {}（確認 {elapsed_ms:.1} ms・{} ファイル・置き直した .so {} 個）",
            installed.dotnet_root.display(),
            manifest.files.len(),
            installed.repaired_native_libraries
        ));
    } else {
        logcat::info(&format!(
            "[SEED DOTNET] .NET を展開しました: {}（{} ファイル・BCL {:.1} MiB・.so の置き方 {:?}・nativeLibraryDir={}・{elapsed_ms:.1} ms）",
            installed.dotnet_root.display(),
            installed.written_files,
            installed.written_asset_bytes as f64 / BYTES_PER_MIB,
            manifest.native_library_mode,
            native_dir.as_ref().map(|dir| dir.display().to_string()).unwrap_or_default(),
        ));
    }
    for removed in &installed.removed_stale {
        logcat::info(&format!("[SEED DOTNET] 古い展開を消しました: {}", removed.display()));
    }
    for error in &installed.stale_errors {
        logcat::warn(&format!("[SEED DOTNET] 古い展開を消せませんでした（起動は続けます）: {error}"));
    }
    Some(installed)
}
