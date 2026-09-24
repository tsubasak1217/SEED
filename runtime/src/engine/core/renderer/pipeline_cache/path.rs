// ============================================================
//  pipeline_cache/path.rs — パイプラインキャッシュの置き場とファイル名【純関数】
//
//  【置き場】`package_layout::decide_cache_dir` の規則に、開発時の置き場（実行ファイルの隣）を渡したもの:
//    1. プラットフォームのキャッシュフォルダ（Android: /data/user/0/<pkg>/cache。起動モードに関係なく）
//    2. パッケージ実行: `{exe のフォルダ}/caches/`（配布物の実行時生成フォルダ）
//    3. 開発 / エディタ実行: 実行ファイルの隣（target/debug 等。掃除は cargo clean に任せられる）
//  デスクトップは 1 が無いので従来どおり 2 → 3。
//
//  【ファイル名】`wgpu::util::pipeline_cache_key`（バックエンド・アダプタのベンダー ID・デバイス ID）＋ `.bin`
//  （例: wgpu_pipeline_cache_vulkan_4318_9860.bin）。
//  - GPU が 2 つある PC（Optimus 等）でもアダプタごとに別ファイルになり、交互に上書きして無効にし合わない。
//  - wgpu は読み込んだデータのアダプタが違うと、fallback を指定していても「避けられた誤り」として
//    検証エラーにする（wgpu-core の PipelineCacheValidationError::DeviceMismatch）。アダプタごとに
//    名前を分ければ、別アダプタのデータを読むこと自体が起きない。
//  - キーが無いバックエンド（Vulkan 以外）はキャッシュを持てない（wgpu 25 のパイプラインキャッシュは Vulkan のみ）。
//
//  【旧ファイル名】2026-09 以前は同じ置き場の `pipeline_cache.bin` 1 本だった。新しい名前のファイルが
//  無いときだけ読み込み元として使う（1 回目のシェーダの作り直しを避けるため）。保存は新しい名前にだけ行う。
//  旧ファイルが別アダプタのものでも、読み込み側（mod.rs）が検証エラーを捕まえて空のキャッシュで作り直す。
// ============================================================

use std::path::{Path, PathBuf};

use crate::engine::core::package_layout;

/// キャッシュファイルの拡張子。
pub const CACHE_FILE_EXTENSION: &str = "bin";

/// 書き込み途中のファイルの拡張子（`<名前>.bin.tmp` へ書いてから置き換える）。
pub const TEMP_FILE_EXTENSION: &str = "bin.tmp";

/// 2026-09 以前の単一ファイル名（読み込みだけに使う。保存はしない）。
pub const LEGACY_CACHE_FILE_NAME: &str = "pipeline_cache.bin";

/// パイプラインキャッシュのファイルの場所。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CacheFiles {
    /// 読み書きするファイル（アダプタごと）。
    pub path: PathBuf,
    /// 旧ファイル名（`path` が無いときだけ読む）。
    pub legacy_path: PathBuf,
}

impl CacheFiles {
    /// 書き込み途中のファイル（書き終えてから `path` へ置き換える）。
    pub fn temp_path(&self) -> PathBuf {
        self.path.with_extension(TEMP_FILE_EXTENSION)
    }
}

/// アダプタのキーからファイル名を作る【純関数】（`<キー>.bin`）。
pub fn cache_file_name(adapter_key: &str) -> String {
    format!("{adapter_key}.{CACHE_FILE_EXTENSION}")
}

/// パイプラインキャッシュのファイルの場所を決める【純関数】。
///
/// # 引数
/// * `platform_cache_dir` - プラットフォームのキャッシュフォルダ（Android。デスクトップは None）
/// * `packaged`           - パッケージ実行か（`asset_fs::is_packaged()`）
/// * `exe_dir`            - 実行ファイルのあるフォルダ（取れなければ None）
/// * `adapter_key`        - `wgpu::util::pipeline_cache_key` の値（キャッシュを持てないバックエンドは None）
///
/// # 戻り値
/// 置き場かキーが決まらなければ None（キャッシュはメモリ上だけで使い、保存しない）。
pub fn decide_cache_files(
    platform_cache_dir: Option<&Path>,
    packaged: bool,
    exe_dir: Option<&Path>,
    adapter_key: Option<&str>,
) -> Option<CacheFiles> {
    let key = adapter_key?;
    // 開発時の置き場は実行ファイルの隣（従来と同じ）。
    let dir = package_layout::decide_cache_dir(
        platform_cache_dir,
        packaged,
        exe_dir,
        exe_dir.map(Path::to_path_buf),
    )?;
    Some(CacheFiles {
        path: dir.join(cache_file_name(key)),
        legacy_path: dir.join(LEGACY_CACHE_FILE_NAME),
    })
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用のアダプタキー（wgpu の書式: wgpu_pipeline_cache_vulkan_<vendor>_<device>）。
    const KEY: &str = "wgpu_pipeline_cache_vulkan_5045_2449580802";

    /// Android: プラットフォームのキャッシュフォルダへ、アダプタごとの名前で置く（パッケージ実行でも）。
    #[test]
    fn android_uses_platform_cache_dir() {
        let cache = Path::new("/data/user/0/com.seedengine.runtime/cache");
        let files = decide_cache_files(Some(cache), true, Some(Path::new("/system/bin")), Some(KEY)).unwrap();
        assert_eq!(files.path, cache.join(format!("{KEY}.bin")));
        assert!(!files.path.starts_with("/system"), "書き込めない /system 配下を選んだ");
    }

    /// デスクトップの開発実行: 実行ファイルの隣（従来の置き場）。
    #[test]
    fn desktop_dev_uses_exe_dir() {
        let exe = Path::new("C:/repo/runtime/target/debug");
        let files = decide_cache_files(None, false, Some(exe), Some(KEY)).unwrap();
        assert_eq!(files.path, exe.join(format!("{KEY}.bin")));
        assert_eq!(files.legacy_path, exe.join(LEGACY_CACHE_FILE_NAME));
    }

    /// デスクトップのパッケージ実行: 配布物の caches/（従来の置き場）。
    #[test]
    fn desktop_packaged_uses_caches_dir() {
        let exe = Path::new("D:/Games/MyGame");
        let files = decide_cache_files(None, true, Some(exe), Some(KEY)).unwrap();
        assert_eq!(files.path, package_layout::caches_dir(exe).join(format!("{KEY}.bin")));
    }

    /// アダプタが違えばファイルも違う（交互に上書きして無効にし合わない）。
    #[test]
    fn different_adapters_get_different_files() {
        let exe = Path::new("C:/game");
        let a = decide_cache_files(None, false, Some(exe), Some("wgpu_pipeline_cache_vulkan_4318_9860")).unwrap();
        let b = decide_cache_files(None, false, Some(exe), Some("wgpu_pipeline_cache_vulkan_32902_18086")).unwrap();
        assert_ne!(a.path, b.path);
        // 旧ファイル名は共通（新しい名前が無いときの読み込み元）。
        assert_eq!(a.legacy_path, b.legacy_path);
    }

    /// キーが無い（Vulkan 以外）・置き場が決まらないときは None（保存しない）。
    #[test]
    fn missing_key_or_dir_yields_none() {
        assert_eq!(decide_cache_files(None, false, Some(Path::new("C:/game")), None), None);
        assert_eq!(decide_cache_files(None, false, None, Some(KEY)), None);
    }

    /// 書き込み途中のファイルは同じフォルダ（rename が同じボリューム内で済む）。
    #[test]
    fn temp_file_is_next_to_cache_file() {
        let files = decide_cache_files(None, false, Some(Path::new("C:/game")), Some(KEY)).unwrap();
        let temp = files.temp_path();
        assert_eq!(temp.parent(), files.path.parent());
        assert_ne!(temp, files.path);
    }
}
