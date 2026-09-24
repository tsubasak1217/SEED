// ============================================================
//  dotnet_runtime/script_sources.rs — スクリプトの DLL（SEEDScripting.dll・SEEDUserScripts.dll）の置き場を選ぶ
//
//  【候補（優先順）】データの有無だけで決める（設定フラグは無い）。選び方の純関数はエンジンの
//  core::scripting::script_binaries::choose_binaries（単体テスト付き）。
//    1. 内部アプリ専用フォルダ /data/user/0/<pkg>/files/bin/
//         … build_and_run.ps1 -PushScripts が run-as で送る開発用の置き場（実機でもエミュレータでも読める）
//    2. 外部アプリ専用フォルダ /sdcard/Android/data/<pkg>/files/bin/
//         … 手で adb push した置き場。エミュレータでは読めるが、実機（Android 11 以降）では adb push が作った
//           ファイル・フォルダは shell の所有になりアプリから読めない（docs/android.md §4.5・§10）。読めなければ警告して飛ばす
//    3. APK の assets/seed/bin/
//         … build_and_run.ps1 -ProjectDir（SeedPak --scripts）が APK に入れたもの（配布版と同じ形）
//  「SEEDScripting.dll がある最初の置き場」を使い、SEEDUserScripts.dll と runtimeconfig も同じ置き場から読む。
//  差し替えを消せば（rm -r files/bin）APK の中のものへ戻る。
// ============================================================

use std::path::Path;
use std::sync::Arc;

use seed_engine::engine::core::package_layout;
use seed_engine::engine::core::scripting::SCRIPTING_HOST_DLL_NAME;
use seed_engine::engine::core::scripting::script_binaries::{
    self, DirectoryBinaries, PackageBinaries, ScriptBinarySource,
};
use seed_engine::engine::package_source::PackageSource;
use winit::platform::android::activity::AndroidApp;

use crate::logcat;

/// スクリプトの DLL の置き場を選ぶ。
///
/// # 引数
/// * `app`     - アプリ専用フォルダ（内部・外部）を知るため
/// * `package` - APK の assets/seed/ の読み口（3 番目の候補）
///
/// # 戻り値
/// 選んだ置き場。どこにも SEEDScripting.dll が無ければ None（スクリプト無しで起動する）。
pub fn choose(app: &AndroidApp, package: Arc<dyn PackageSource>) -> Option<Arc<dyn ScriptBinarySource>> {
    let mut candidates: Vec<Arc<dyn ScriptBinarySource>> = Vec::new();
    for data_dir in [app.internal_data_path(), app.external_data_path()].into_iter().flatten() {
        let bin_dir = package_layout::bin_dir(&data_dir);
        warn_if_unreadable(&bin_dir);
        candidates.push(Arc::new(DirectoryBinaries::new(bin_dir)));
    }
    candidates.push(Arc::new(PackageBinaries::new(package)));

    for candidate in &candidates {
        logcat::info(&format!(
            "[SEED DOTNET] スクリプトの置き場の候補: {} — {}",
            candidate.describe(""),
            script_binaries::summarize(candidate.as_ref())
        ));
    }
    match script_binaries::choose_binaries(&candidates) {
        Some(index) => {
            let chosen = candidates.swap_remove(index);
            logcat::info(&format!(
                "[SEED DOTNET] スクリプトの置き場: {}（{} がある最初の候補）",
                chosen.describe(""),
                SCRIPTING_HOST_DLL_NAME
            ));
            Some(chosen)
        }
        None => {
            logcat::warn(&format!(
                "[SEED DOTNET] どの置き場にも {SCRIPTING_HOST_DLL_NAME} がありません（APK を -ProjectDir で作るか、-PushScripts で送ってください）。\
                 スクリプト無しで起動します"
            ));
            None
        }
    }
}

/// フォルダはあるのにアプリから読めない置き場を警告する（実機で外部フォルダへ手で adb push したときの典型）。
///
/// 読めない置き場は `choose_binaries` の対象から自然に外れる（SEEDScripting.dll が「無い」扱い）が、
/// 黙っていると「送ったのに反映されない」理由が分からないため、原因と対処を 1 行残す。
fn warn_if_unreadable(bin_dir: &Path) {
    if !bin_dir.exists() {
        return;
    }
    if let Err(err) = std::fs::read_dir(bin_dir) {
        logcat::warn(&format!(
            "[SEED DOTNET] {} はアプリから読めません（{err}）。実機では adb push で作ったフォルダはアプリの所有にならないため、\
             build_and_run.ps1 -PushScripts（run-as で内部フォルダへ送る）を使ってください",
            bin_dir.display()
        ));
    }
}
