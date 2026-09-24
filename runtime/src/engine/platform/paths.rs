// ============================================================
//  platform/paths.rs — プラットフォームが与える書き込み先（起動時に 1 回だけ設定する値）
//
//  【役割】
//  エンジンが書き込むファイル（セーブ・モデルの派生キャッシュ・パイプラインキャッシュ）の置き場のうち、
//  OS が実行時にだけ教えてくれるもの（Android の Context.getFilesDir() / getCacheDir() に当たるフォルダ）を
//  プロセス全体で 1 か所に保持する。特性表（mod.rs の PlatformTraits）はコンパイル時定数だが、
//  こちらは実行時にしか分からないため「起動時に 1 回だけ設定する値」として持つ。
//
//  【設定する側・しない側】
//  - Android: 糊（runtime/android/native の android_main → app_dirs.rs）が AndroidApp の
//    internal_data_path() から組み立てて `init` する。実行ファイル（app_process）の隣は /system/bin で
//    書き込めないため、従来の「実行ファイル基準」の置き場は Android では使えない。
//  - デスクトップ: 設定しない（`get()` が None）。セーブ・キャッシュは従来どおり実行ファイル／
//    アセットルート基準で決まる（振る舞いは一切変わらない）。
//
//  【使う側の優先順】
//  設定されていれば、起動モード（APK 内 pak のパッケージ実行か、run-as で送った開発用の置き場か）に
//  関係なく最優先で使う（Android ではアプリ専用フォルダが唯一の書き込み先で、どちらのモードでも
//  同じ場所に書けば「起動の仕方でセーブが分かれる」ことも無い）。
//  判定そのものは各機能の純関数が引数で受ける:
//    - セーブ           … core::save::path::decide_save_dir（`data_dir` 直下の save/）
//    - 派生キャッシュ   … core::package_layout::decide_cache_dir（`cache_dir` そのもの）
//    - パイプラインキャッシュ … core::renderer::pipeline_cache::path（同上）
//
//  全体像は docs/android.md §14。
// ============================================================

use std::path::{Path, PathBuf};
use std::sync::OnceLock;

/// Android のアプリ専用フォルダのうち、キャッシュのフォルダ名（`/data/user/0/<pkg>/cache`）。
///
/// Context.getCacheDir() と同じ場所（`getFilesDir()` と同じ親の下）。OS は端末の空き容量が
/// 少ないときにここのファイルを消すことがある。消しても再生成できるものだけを置く、という
/// エンジンのキャッシュの契約（package_layout の caches/ と同じ）と一致する。
pub const ANDROID_CACHE_DIR_NAME: &str = "cache";

/// プラットフォームが与える書き込み先。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlatformPaths {
    /// 永続データ（セーブ）のルート。アプリを消すまで残る。
    ///
    /// Android: `/data/user/0/<パッケージ名>/files`（AndroidApp::internal_data_path）。
    pub data_dir: PathBuf,
    /// 派生データ（モデルの .smdl・パイプラインキャッシュ）の置き場。消えても再生成される。
    ///
    /// Android: `/data/user/0/<パッケージ名>/cache`（`android_cache_dir_for_files_dir` で求める）。
    pub cache_dir: PathBuf,
}

/// 起動時に設定された書き込み先（未設定＝デスクトップ）。
///
/// `OnceLock` なので最初の 1 回だけが効く（途中で置き場が変わると、同じプロセスの中で
/// セーブの読み書き先が食い違うため、上書きはさせない）。
static APP_PATHS: OnceLock<PlatformPaths> = OnceLock::new();

/// 書き込み先を設定する。起動時に 1 回だけ呼ぶ（エンジンを動かし始める前）。
///
/// # 戻り値
/// 設定できた＝true。既に設定済みなら何もせず false（最初の値を保つ）。
pub fn init(paths: PlatformPaths) -> bool {
    APP_PATHS.set(paths).is_ok()
}

/// 設定された書き込み先。デスクトップ（設定しない）では None。
pub fn get() -> Option<&'static PlatformPaths> {
    APP_PATHS.get()
}

/// 設定されたデータフォルダ（セーブのルート）。未設定なら None。
pub fn data_dir() -> Option<&'static Path> {
    get().map(|paths| paths.data_dir.as_path())
}

/// 設定されたキャッシュフォルダ。未設定なら None。
pub fn cache_dir() -> Option<&'static Path> {
    get().map(|paths| paths.cache_dir.as_path())
}

/// Android の files フォルダから、同じアプリのキャッシュフォルダを求める【純関数】。
///
/// `/data/user/0/<pkg>/files` → `/data/user/0/<pkg>/cache`。Android の ContextImpl は
/// getFilesDir() と getCacheDir() を同じデータフォルダ（getDataDir()）の直下に作るため、
/// files の親の `cache` がそのままキャッシュフォルダになる（JNI で問い合わせずに済む）。
///
/// # 引数
/// * `files_dir` - AndroidApp::internal_data_path() の値（絶対パス）
///
/// # 戻り値
/// 親フォルダが無い・空（ルートや相対の 1 段だけのパス）なら None（置き場を決められない）。
pub fn android_cache_dir_for_files_dir(files_dir: &Path) -> Option<PathBuf> {
    let parent = files_dir.parent()?;
    // "files" のような 1 段だけの相対パスでは親が空文字になる。そこへ cache を足すと
    // カレントディレクトリ相対の置き場になってしまうため、決められない扱いにする。
    if parent.as_os_str().is_empty() {
        return None;
    }
    Some(parent.join(ANDROID_CACHE_DIR_NAME))
}

// ============================================================
//  ユニットテスト（純関数だけ。グローバルの APP_PATHS はテストから設定しない:
//  同じプロセスで並行に走る他のテスト（セーブの保存先の解決など）の置き場まで変わってしまうため）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// files の兄弟の cache になること（Android の実際の配置）。
    #[test]
    fn android_cache_dir_is_sibling_of_files_dir() {
        let cache = android_cache_dir_for_files_dir(Path::new("/data/user/0/com.seedengine.runtime/files"));
        assert_eq!(cache, Some(PathBuf::from("/data/user/0/com.seedengine.runtime/cache")));
    }

    /// 末尾に区切りが付いていても同じ結果になること。
    #[test]
    fn android_cache_dir_ignores_trailing_separator() {
        let cache = android_cache_dir_for_files_dir(Path::new("/data/user/0/com.seedengine.runtime/files/"));
        assert_eq!(cache, Some(PathBuf::from("/data/user/0/com.seedengine.runtime/cache")));
    }

    /// 親を持たないパスでは決められない（None）。カレント相対の置き場を作らないこと。
    #[test]
    fn android_cache_dir_needs_a_parent() {
        assert_eq!(android_cache_dir_for_files_dir(Path::new("/")), None);
        assert_eq!(android_cache_dir_for_files_dir(Path::new("files")), None);
    }

    /// フォルダ名は Android の Context.getCacheDir() と同じ "cache"（名前がずれると OS の掃除対象から外れる）。
    #[test]
    fn android_cache_dir_name_matches_android_context() {
        assert_eq!(ANDROID_CACHE_DIR_NAME, "cache");
    }
}
