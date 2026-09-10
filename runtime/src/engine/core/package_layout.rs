// ============================================================
//  package_layout.rs — 配布パッケージのフォルダ構成【正典】
//
//  【役割】
//  配布物（{ゲーム名}/ フォルダ）の中に置くサブフォルダの名前と、
//  実行ファイルの位置からそれぞれの絶対パスを求める純関数だけを持つ。
//  ファイル I/O は一切行わない（作る・消すは各機能の責務）。
//
//  【なぜ 1 モジュールに集めるのか】
//  以前は「スクリプトホスト DLL は exe の隣」「セーブは exe の隣の save/」
//  「モデルキャッシュは assets の隣の cache/」と、置き場の知識が
//  機能ごとに散らばっていた。その結果 exe と同じ階層に DLL が数十個
//  散らかり、どれが配布物でどれが実行時生成物かを利用者が判別できなかった。
//  ここを唯一の定義箇所にして、フォルダ名を変えるときの変更点を 1 つにする。
//
//  【配布物の構成】
//  ```text
//  {ゲーム名}/
//    {ゲーム名}.exe          … 実行ファイル
//    assets.pak              … アセット（PAK）
//    bin/                    … 実行に必要な副次ファイル（配布時に同梱する）
//      SEEDScripting.dll / SEEDScripting.runtimeconfig.json / SEEDScripting.deps.json
//      Microsoft.CodeAnalysis*.dll / SEEDUserScripts.dll
//      dotnet/               … 同梱 .NET ランタイム（self-contained 配布）
//    caches/                 … 実行時生成（モデル派生キャッシュ *.smdl / pipeline_cache.bin）
//    logs/                   … 実行時生成（起動ログ seed_*.log）
//    saved/                  … 実行時生成（セーブデータ save.json）
//  ```
//
//  【空フォルダを作らない方針】
//  `caches` / `logs` / `saved` は **パッケージ化では作らない**。
//  空フォルダは zip 化・展開で落ちることが多く、「あるはず」を前提にすると
//  配布先でだけ壊れる。必要になった時点でランタイムが `create_dir_all` する。
//
//  【エディタ側の対応物】
//  `editor/src/Packaging/PackageLayout.cs` が同じ名前の定数を持つ。
//  どちらかを変えたら必ず両方直すこと（名前がずれると配布物だけが壊れる）。
// ============================================================

use std::path::{Path, PathBuf};

// ============================================================
//  フォルダ名（配布物の中に作るサブフォルダ）
// ============================================================

/// 実行に必要な副次ファイル（スクリプトホスト DLL・同梱 .NET）を入れるフォルダ名。
///
/// エディタ側 `PackageLayout.BinDirName` と一致必須。
pub const BIN_DIR_NAME: &str = "bin";

/// 実行時に生成する派生データキャッシュのフォルダ名。
///
/// 中身は「消しても再生成できるもの」だけに限る（消せることが利用者への契約）。
/// エディタ側 `PackageLayout.CachesDirName` と一致必須。
pub const CACHES_DIR_NAME: &str = "caches";

/// 起動ログを入れるフォルダ名。
///
/// 実際に書き出すのは `core::startup_log`（候補の決定は `startup_log::log_path`）。
/// エディタ側 `PackageLayout.LogsDirName` と一致必須。
pub const LOGS_DIR_NAME: &str = "logs";

/// セーブデータを入れるフォルダ名（パッケージ実行のみ）。
///
/// エディタ Play は配布物ではないので別の場所（`runtime/save/`）を使う。
/// 判定は `core::save::path::decide_save_dir`。
/// エディタ側 `PackageLayout.SavedDirName` と一致必須。
pub const SAVED_DIR_NAME: &str = "saved";

// ============================================================
//  パス組み立て（純関数）
// ============================================================

/// 副次ファイルフォルダ `{exe のフォルダ}/bin` を返す【純関数】。
///
/// # 引数
/// * `exe_dir` - 実行ファイルのあるフォルダ
pub fn bin_dir(exe_dir: &Path) -> PathBuf {
    exe_dir.join(BIN_DIR_NAME)
}

/// キャッシュフォルダ `{exe のフォルダ}/caches` を返す【純関数】。
///
/// # 引数
/// * `exe_dir` - 実行ファイルのあるフォルダ
pub fn caches_dir(exe_dir: &Path) -> PathBuf {
    exe_dir.join(CACHES_DIR_NAME)
}

/// ログフォルダ `{exe のフォルダ}/logs` を返す【純関数】。
///
/// # 引数
/// * `exe_dir` - 実行ファイルのあるフォルダ
pub fn logs_dir(exe_dir: &Path) -> PathBuf {
    exe_dir.join(LOGS_DIR_NAME)
}

/// セーブフォルダ `{exe のフォルダ}/saved` を返す【純関数】。
///
/// # 引数
/// * `exe_dir` - 実行ファイルのあるフォルダ
pub fn saved_dir(exe_dir: &Path) -> PathBuf {
    exe_dir.join(SAVED_DIR_NAME)
}

/// 実行時生成キャッシュの置き場を決める【純関数】。
///
/// パッケージ実行かどうかで置き場が変わる、という判断だけをここに集める。
/// 「開発時の置き場」は用途ごとに違う（モデルキャッシュは `{assets}/../cache`、
/// パイプラインキャッシュは exe の隣）ため、呼び出し側が `dev_dir` で渡す。
///
/// # 引数
/// * `packaged` - パッケージ実行か（`asset_fs::is_packaged()`）
/// * `exe_dir`  - 実行ファイルのあるフォルダ（取得できなければ `None`）
/// * `dev_dir`  - パッケージ実行でないときに使う置き場（決められなければ `None`）
///
/// # 戻り値
/// キャッシュを置くフォルダ。決められない場合は `None`
/// （呼び出し側はキャッシュを諦める＝機能は落とさない）。
pub fn decide_cache_dir(
    packaged: bool,
    exe_dir: Option<&Path>,
    dev_dir: Option<PathBuf>,
) -> Option<PathBuf> {
    // パッケージ実行では配布フォルダの中の caches/ に集約する。
    // 実行ファイルの位置が取れないときだけ開発時の置き場へ落ちる
    // （ゲームを起動できなくするほどの事情ではない）。
    if packaged {
        if let Some(dir) = exe_dir {
            return Some(caches_dir(dir));
        }
    }
    dev_dir
}

// ============================================================
//  ユニットテスト（すべて純関数なので完全に検証できる）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 各フォルダが exe の直下に 1 段だけ作られること。
    #[test]
    fn directories_are_direct_children_of_exe_dir() {
        let exe = Path::new("D:/Games/MyGame");
        assert_eq!(bin_dir(exe), Path::new("D:/Games/MyGame/bin"));
        assert_eq!(caches_dir(exe), Path::new("D:/Games/MyGame/caches"));
        assert_eq!(logs_dir(exe), Path::new("D:/Games/MyGame/logs"));
        assert_eq!(saved_dir(exe), Path::new("D:/Games/MyGame/saved"));
    }

    /// フォルダ名がエディタ側（PackageLayout.cs）の規約と一致していること。
    /// 名前がずれるとビルドは通るのに配布物だけが壊れるため、文字列で固定する。
    #[test]
    fn dir_names_match_editor_contract() {
        assert_eq!(BIN_DIR_NAME, "bin");
        assert_eq!(CACHES_DIR_NAME, "caches");
        assert_eq!(LOGS_DIR_NAME, "logs");
        assert_eq!(SAVED_DIR_NAME, "saved");
    }

    /// パッケージ実行では exe の隣の caches/ を使う（開発時の置き場より優先）。
    #[test]
    fn packaged_cache_dir_uses_exe_caches() {
        let dir = decide_cache_dir(
            true,
            Some(Path::new("D:/Games/MyGame")),
            Some(PathBuf::from("C:/repo/runtime/cache")),
        );
        assert_eq!(dir, Some(PathBuf::from("D:/Games/MyGame/caches")));
    }

    /// 非パッケージ実行では開発時の置き場をそのまま使う。
    #[test]
    fn dev_cache_dir_is_passed_through() {
        let dir = decide_cache_dir(
            false,
            Some(Path::new("C:/repo/runtime/target/debug")),
            Some(PathBuf::from("C:/repo/runtime/cache")),
        );
        assert_eq!(dir, Some(PathBuf::from("C:/repo/runtime/cache")));
    }

    /// パッケージ実行でも exe の位置が取れなければ開発時の置き場へ落ちる。
    #[test]
    fn packaged_without_exe_dir_falls_back_to_dev_dir() {
        let dir = decide_cache_dir(true, None, Some(PathBuf::from("C:/fallback/cache")));
        assert_eq!(dir, Some(PathBuf::from("C:/fallback/cache")));
    }

    /// どちらも決められないときは None（キャッシュ無しで動く）。
    #[test]
    fn no_information_yields_none() {
        assert_eq!(decide_cache_dir(true, None, None), None);
        assert_eq!(decide_cache_dir(false, Some(Path::new("C:/game")), None), None);
    }
}
