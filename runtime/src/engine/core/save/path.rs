// ============================================================
//  save/path.rs — セーブファイルの保存先解決
//
//  【役割】
//  「セーブデータをどこへ置くか」だけを決める層。実際の I/O は行わない。
//  判定の中核は純関数 `decide_save_dir` に閉じてあり、環境（実行ファイル位置・
//  アセットルート）をすべて引数で受けるためユニットテストできる。
//
//  【保存先の規約】
//  1. 環境変数 `SEED_SAVE_DIR` があれば最優先（CI・テスト・多重起動の切り分け用）
//  2. プラットフォームがデータフォルダを与えている（Android）: その直下の `save/`
//     → `/data/user/0/<パッケージ名>/files/save/`。起動モード（APK 内 pak のパッケージ実行か、
//        run-as で送った開発用の置き場か）に関係なく同じ場所。Android では実行ファイル
//        （app_process）の隣が /system/bin で書けないため、3 の規則は使えない。
//        名前を `save` にしたのは、段階0 からの開発用の置き場（files/assets の親＝files の save/）と
//        同じ場所にして、既存の端末のセーブをそのまま引き継ぐため（platform::paths）。
//  3. パッケージ実行（assets.pak あり = 配布ビルド）: 実行ファイル隣の `saved/`
//     → 配布物のフォルダ構成（`core::package_layout`）の一部。
//  4. エディタ Play（アセットルートあり = リポジトリ内実行）:
//     アセットルートの親 = `runtime/` 直下の `save/`
//     → Git 追跡外（.gitignore に `runtime/save/` を追加済み）。
//        アセットフォルダの中には**置かない**（パッケージングに巻き込まれ、
//        開発者のセーブが配布物へ同梱されてしまうため）。
//  5. いずれも解決できない場合（単体テスト等）: カレントディレクトリの `save/`
//  デスクトップでは 2 を設定しないので、従来どおり 1 → 3 → 4 → 5 の順になる。
//
//  【なぜ配布版とリポジトリ内でフォルダ名が違うのか】
//  配布物側は `caches` / `logs` / `saved` と名前を揃えて「実行時生成物」だと
//  一目で分かる構成にしている。一方リポジトリ内の `runtime/save/` は
//  .gitignore の記述・既存の開発者環境がその名前に依存しているため据え置く。
//  混同を防ぐため定数も 2 本に分けてある。
// ============================================================

use std::path::{Path, PathBuf};

use crate::engine::core::package_layout;

/// 開発時（エディタ Play・単体テスト）と、プラットフォームのデータフォルダ（Android）の下に作る
/// セーブディレクトリ名。
///
/// デスクトップのパッケージ実行では使わない（そちらは `package_layout::SAVED_DIR_NAME`）。
pub const SAVE_DIR_NAME: &str = "save";

/// セーブファイル名（JSON 1 ファイル）。
pub const SAVE_FILE_NAME: &str = "save.json";

/// 保存先ディレクトリを上書きする環境変数名。
pub const SAVE_DIR_ENV: &str = "SEED_SAVE_DIR";

/// 保存先ディレクトリを決める純関数。
///
/// # 引数
/// - `env_override`     : `SEED_SAVE_DIR` の値（未設定なら `None`）
/// - `platform_data_dir`: プラットフォームが与えるデータフォルダ（Android の files。デスクトップは `None`）
/// - `packaged`         : パッケージ実行か（assets.pak を読み込んでいるか）
/// - `exe_dir`          : 実行ファイルのあるディレクトリ（取得できなければ `None`）
/// - `assets_root`      : アセットルートの絶対パス（未初期化なら `None`）
/// - `cwd`              : カレントディレクトリ（最終フォールバック）
///
/// # 戻り値
/// セーブファイルを置くディレクトリ。ファイル自体は `SAVE_FILE_NAME`。
pub fn decide_save_dir(
    env_override: Option<&str>,
    platform_data_dir: Option<&Path>,
    packaged: bool,
    exe_dir: Option<&Path>,
    assets_root: Option<&Path>,
    cwd: &Path,
) -> PathBuf {
    // 1) 環境変数の明示指定が最優先（空文字は未指定と同じ扱い）
    if let Some(dir) = env_override.filter(|s| !s.trim().is_empty()) {
        return PathBuf::from(dir);
    }

    // 2) プラットフォームのデータフォルダ（Android）: 起動モードに関係なくその直下の save/。
    //    実行ファイルの隣やアセットルートの親が書けるとは限らないプラットフォームのための規則。
    if let Some(dir) = platform_data_dir {
        return dir.join(SAVE_DIR_NAME);
    }

    // 3) パッケージ実行: 実行ファイル隣の saved/
    //    （配布物はユーザーの任意フォルダへ展開されるため、実行ファイル相対が最も予測しやすい）
    //    フォルダ名は配布物の構成の一部なので package_layout を参照する。
    if packaged {
        if let Some(dir) = exe_dir {
            return package_layout::saved_dir(dir);
        }
    }

    // 4) エディタ Play: アセットルートの親（= runtime/）直下の save/
    //    アセットルートそのものではなく親に置くのは、パッケージング対象
    //    （assets/ 配下）へセーブを混入させないため。
    if let Some(root) = assets_root {
        if let Some(parent) = root.parent() {
            return parent.join(SAVE_DIR_NAME);
        }
        return root.join(SAVE_DIR_NAME);
    }

    // 5) パッケージだが実行ファイル位置が取れなかった場合も含む最終フォールバック
    cwd.join(SAVE_DIR_NAME)
}

/// 実際の環境（環境変数・プラットフォームのデータフォルダ・実行ファイル・asset_fs）を読んで
/// 保存先ファイルパスを返す。
///
/// 判定ロジック自体は `decide_save_dir`（純関数）に委譲し、
/// この関数は「環境を集めて渡す」だけに徹する。
pub fn resolve_save_path() -> PathBuf {
    use crate::engine::asset_fs;
    use crate::engine::platform;

    let env_override = std::env::var(SAVE_DIR_ENV).ok();
    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(Path::to_path_buf));
    let assets_root = asset_fs::root().cloned();
    let cwd = std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."));

    let dir = decide_save_dir(
        env_override.as_deref(),
        platform::paths::data_dir(),
        asset_fs::is_packaged(),
        exe_dir.as_deref(),
        assets_root.as_deref(),
        &cwd,
    );
    dir.join(SAVE_FILE_NAME)
}

// ============================================================
//  ユニットテスト（保存先パス解決は純関数なので完全に検証できる）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 環境変数指定は他のすべてに優先する。
    #[test]
    fn env_override_wins() {
        let dir = decide_save_dir(
            Some("D:/custom/slot1"),
            None,
            true,
            Some(Path::new("C:/game")),
            Some(Path::new("C:/repo/runtime/assets")),
            Path::new("C:/cwd"),
        );
        assert_eq!(dir, PathBuf::from("D:/custom/slot1"));
    }

    /// 空文字・空白のみの環境変数は「未指定」として扱う。
    #[test]
    fn blank_env_override_is_ignored() {
        let dir = decide_save_dir(
            Some("   "),
            None,
            true,
            Some(Path::new("C:/game")),
            None,
            Path::new("C:/cwd"),
        );
        assert_eq!(dir, Path::new("C:/game").join(package_layout::SAVED_DIR_NAME));
    }

    /// パッケージ実行では実行ファイル隣の saved/ を使う（アセットルートより優先）。
    #[test]
    fn packaged_uses_exe_dir() {
        let dir = decide_save_dir(
            None,
            None,
            true,
            Some(Path::new("C:/game")),
            Some(Path::new("C:/game/assets")),
            Path::new("C:/cwd"),
        );
        assert_eq!(dir, PathBuf::from("C:/game/saved"));
    }

    /// パッケージ実行のフォルダ名が配布物の構成（package_layout）と一致していること。
    /// 開発時の `save/` と取り違えると、配布物のセーブが別フォルダに散る。
    #[test]
    fn packaged_dir_name_comes_from_package_layout() {
        let dir = decide_save_dir(None, None, true, Some(Path::new("C:/game")), None, Path::new("C:/cwd"));
        assert!(dir.ends_with(package_layout::SAVED_DIR_NAME));
        assert!(!dir.ends_with(SAVE_DIR_NAME), "開発時の save/ を配布版で使っている");
    }

    /// エディタ Play（非パッケージ）ではアセットルートの**親**直下へ置く。
    /// assets/ の中へ置くとパッケージングに巻き込まれるため。
    #[test]
    fn editor_play_uses_assets_root_parent() {
        let dir = decide_save_dir(
            None,
            None,
            false,
            Some(Path::new("C:/repo/target/debug")),
            Some(Path::new("C:/repo/runtime/assets")),
            Path::new("C:/cwd"),
        );
        assert_eq!(dir, PathBuf::from("C:/repo/runtime").join(SAVE_DIR_NAME));
        // assets/ 配下に入っていないこと（パッケージング混入の回帰防止）
        assert!(!dir.starts_with("C:/repo/runtime/assets"));
    }

    /// パッケージだが実行ファイル位置が不明ならカレントへフォールバックする。
    #[test]
    fn packaged_without_exe_dir_falls_back_to_cwd() {
        let dir = decide_save_dir(None, None, true, None, None, Path::new("C:/cwd"));
        assert_eq!(dir, Path::new("C:/cwd").join(SAVE_DIR_NAME));
    }

    /// 何も分からない場合はカレントディレクトリ直下。
    #[test]
    fn no_information_falls_back_to_cwd() {
        let dir = decide_save_dir(None, None, false, None, None, Path::new("."));
        assert_eq!(dir, Path::new(".").join(SAVE_DIR_NAME));
    }

    /// アセットルートが親を持たない（ルート直下）異常系でも panic しない。
    #[test]
    fn assets_root_without_parent_is_safe() {
        let dir = decide_save_dir(None, None, false, None, Some(Path::new("/")), Path::new("."));
        // Path::new("/").parent() は None なので root 自身 + save/
        assert!(dir.ends_with(SAVE_DIR_NAME));
    }

    // ── プラットフォームのデータフォルダ（Android）────────────────────

    /// Android の files フォルダ（テスト用の値）。
    const ANDROID_FILES: &str = "/data/user/0/com.seedengine.runtime/files";

    /// パッケージ実行（APK 内 pak）でもデータフォルダの save/ を使う。
    /// 実行ファイル（app_process）の隣＝/system/bin の saved/ へ行かないこと（A-2 までの不具合の回帰防止）。
    #[test]
    fn platform_data_dir_wins_over_packaged_exe_dir() {
        let dir = decide_save_dir(
            None,
            Some(Path::new(ANDROID_FILES)),
            true,
            Some(Path::new("/system/bin")),
            Some(Path::new("/data/user/0/com.seedengine.runtime/files/assets")),
            Path::new("/"),
        );
        assert_eq!(dir, Path::new(ANDROID_FILES).join(SAVE_DIR_NAME));
        assert!(!dir.starts_with("/system"), "書き込めない /system 配下を選んだ");
    }

    /// 開発用の置き場（run-as で送った files/assets）でも同じ場所になる（起動モードでセーブが分かれない）。
    /// 段階0 からの「アセットルートの親の save/」とも一致する（既存の端末のセーブをそのまま引き継ぐ）。
    #[test]
    fn platform_data_dir_is_same_for_dev_and_packaged() {
        let files = Path::new(ANDROID_FILES);
        let assets = files.join("assets");
        let packaged = decide_save_dir(None, Some(files), true, Some(Path::new("/system/bin")), Some(&assets), Path::new("/"));
        let dev = decide_save_dir(None, Some(files), false, Some(Path::new("/system/bin")), Some(&assets), Path::new("/"));
        assert_eq!(packaged, dev);
        // 段階0 の規則（データフォルダ無し・開発用の置き場）と同じ場所。
        let legacy_dev = decide_save_dir(None, None, false, Some(Path::new("/system/bin")), Some(&assets), Path::new("/"));
        assert_eq!(dev, legacy_dev);
    }

    /// 環境変数の明示指定はプラットフォームのデータフォルダより優先する（規約 1 のまま）。
    #[test]
    fn env_override_beats_platform_data_dir() {
        let dir = decide_save_dir(
            Some("D:/custom/slot1"),
            Some(Path::new(ANDROID_FILES)),
            true,
            None,
            None,
            Path::new("/"),
        );
        assert_eq!(dir, PathBuf::from("D:/custom/slot1"));
    }
}
