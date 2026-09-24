// ============================================================
//  app_dirs.rs — アプリ専用フォルダをエンジンの書き込み先として設定する（起動時に 1 回）
//
//  【何をするか】
//  AndroidApp::internal_data_path()（/data/user/0/<パッケージ名>/files）と、その兄弟のキャッシュフォルダ
//  （/data/user/0/<パッケージ名>/cache。無ければ作る）を engine::platform::paths へ設定する。
//    → セーブは files/save/save.json、モデルの派生キャッシュ（.smdl）とパイプラインキャッシュは cache/ に
//      書かれる（APK 内 pak のパッケージ実行でも、run-as で送った開発用の置き場でも同じ）。
//  以前は「実行ファイル（app_process）の隣」を基準にしていたため、パッケージ実行では /system/bin/saved・
//  /system/bin/caches を指して書けず、パイプラインキャッシュは起動モードに関係なく保存されなかった。
//
//  【環境変数 TMPDIR / HOME】
//  アプリプロセスには元々どちらも無い（zygote から受け継ぐ環境に無い。API 35 のエミュレータで確認）。
//  段階B の .NET（Path.GetTempPath() が TMPDIR を、ユーザーフォルダ系の API が HOME を使う）のために、
//  Java 側（MainActivity.onCreate の最初。ネイティブのスレッドが 1 本も無いうち）で
//  TMPDIR＝キャッシュフォルダ・HOME＝files に向けている。Rust 2024 では std::env::set_var が
//  unsafe（他スレッドの getenv と競合する）なので、スレッドが立つ前に Java の Os.setenv で行う。
//  ここでは値がこのフォルダを指しているかを確かめてログに残すだけ（読み取りは安全）。
// ============================================================

use std::path::{Path, PathBuf};

use seed_engine::engine::platform::paths::{self, PlatformPaths};
use winit::platform::android::activity::AndroidApp;

use crate::logcat;

/// Java 側（MainActivity）がキャッシュフォルダへ向ける環境変数（.NET の Path.GetTempPath・Rust の temp_dir が読む）。
const TEMP_DIR_ENV: &str = "TMPDIR";

/// Java 側（MainActivity）がデータフォルダ（files）へ向ける環境変数（.NET のユーザーフォルダ系 API が読む）。
const HOME_DIR_ENV: &str = "HOME";

/// アプリ専用フォルダをエンジンの書き込み先として設定する（android_main の最初に 1 回）。
///
/// 内部データフォルダが取れない（通常は起きない）ときは設定しない。そのときエンジンは
/// 従来の実行ファイル基準の置き場を使う（Android では書けないが落ちはしない）。
pub fn init(app: &AndroidApp) {
    let Some(files_dir) = app.internal_data_path() else {
        logcat::warn("内部データフォルダ（internal_data_path）が取得できません。セーブ・キャッシュは保存されません");
        return;
    };
    let Some(cache_dir) = paths::android_cache_dir_for_files_dir(&files_dir) else {
        logcat::warn(&format!(
            "キャッシュフォルダを決められません（files={}）。セーブ・キャッシュは保存されません",
            files_dir.display()
        ));
        return;
    };
    ensure_dir(&cache_dir);

    log_env_points_to(TEMP_DIR_ENV, &cache_dir);
    log_env_points_to(HOME_DIR_ENV, &files_dir);

    let platform_paths = PlatformPaths {
        data_dir: files_dir,
        cache_dir,
    };
    logcat::info(&format!(
        "書き込み先: データ（セーブ）={} / キャッシュ={}",
        platform_paths.data_dir.display(),
        platform_paths.cache_dir.display()
    ));
    if !paths::init(platform_paths) {
        // android_main は 1 プロセス 1 回だが、万一 2 回目が来ても最初の値を保つ（途中で置き場を変えない）。
        logcat::warn("書き込み先は設定済みのため、最初の値のまま使います");
    }
}

/// フォルダが無ければ作る（キャッシュフォルダは Android が作っているのが普通だが、消されていても困らないように）。
///
/// 作れなくても起動は止めない（キャッシュが無いだけで動く）。ログだけ残す。
fn ensure_dir(dir: &Path) {
    if dir.is_dir() {
        return;
    }
    match std::fs::create_dir_all(dir) {
        Ok(()) => logcat::info(&format!("キャッシュフォルダが無かったため作成しました: {}", dir.display())),
        Err(err) => logcat::warn(&format!("キャッシュフォルダを作成できませんでした: {} — {err}", dir.display())),
    }
}

/// 環境変数が期待のフォルダを指しているかをログに残す（Java 側の設定の確認用。値は変えない）。
fn log_env_points_to(name: &str, expected: &Path) {
    match std::env::var_os(name).map(PathBuf::from) {
        Some(value) if value == expected => logcat::info(&format!("環境変数 {name}={}", value.display())),
        Some(value) => logcat::warn(&format!(
            "環境変数 {name}={} がアプリのフォルダ（{}）を指していません（MainActivity.onCreate の設定を確認してください）",
            value.display(),
            expected.display()
        )),
        None => logcat::warn(&format!(
            "環境変数 {name} が未設定です（MainActivity.onCreate で {} へ向けるはず）",
            expected.display()
        )),
    }
}
