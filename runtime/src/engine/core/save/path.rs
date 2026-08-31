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
//  2. パッケージ実行（assets.pak あり = 配布ビルド）: 実行ファイル隣の `save/`
//  3. エディタ Play（アセットルートあり = リポジトリ内実行）:
//     アセットルートの親 = `runtime/` 直下の `save/`
//     → Git 追跡外（.gitignore に `runtime/save/` を追加済み）。
//        アセットフォルダの中には**置かない**（パッケージングに巻き込まれ、
//        開発者のセーブが配布物へ同梱されてしまうため）。
//  4. いずれも解決できない場合（単体テスト等）: カレントディレクトリの `save/`
// ============================================================

use std::path::{Path, PathBuf};

/// セーブディレクトリ名（上記いずれの経路でも共通）。
pub const SAVE_DIR_NAME: &str = "save";

/// セーブファイル名（JSON 1 ファイル）。
pub const SAVE_FILE_NAME: &str = "save.json";

/// 保存先ディレクトリを上書きする環境変数名。
pub const SAVE_DIR_ENV: &str = "SEED_SAVE_DIR";

/// 保存先ディレクトリを決める純関数。
///
/// # 引数
/// - `env_override`: `SEED_SAVE_DIR` の値（未設定なら `None`）
/// - `packaged`    : パッケージ実行か（assets.pak を読み込んでいるか）
/// - `exe_dir`     : 実行ファイルのあるディレクトリ（取得できなければ `None`）
/// - `assets_root` : アセットルートの絶対パス（未初期化なら `None`）
/// - `cwd`         : カレントディレクトリ（最終フォールバック）
///
/// # 戻り値
/// セーブファイルを置くディレクトリ。ファイル自体は `SAVE_FILE_NAME`。
pub fn decide_save_dir(
    env_override: Option<&str>,
    packaged: bool,
    exe_dir: Option<&Path>,
    assets_root: Option<&Path>,
    cwd: &Path,
) -> PathBuf {
    // 1) 環境変数の明示指定が最優先（空文字は未指定と同じ扱い）
    if let Some(dir) = env_override.filter(|s| !s.trim().is_empty()) {
        return PathBuf::from(dir);
    }

    // 2) パッケージ実行: 実行ファイル隣の save/
    //    （配布物はユーザーの任意フォルダへ展開されるため、実行ファイル相対が最も予測しやすい）
    if packaged {
        if let Some(dir) = exe_dir {
            return dir.join(SAVE_DIR_NAME);
        }
    }

    // 3) エディタ Play: アセットルートの親（= runtime/）直下の save/
    //    アセットルートそのものではなく親に置くのは、パッケージング対象
    //    （assets/ 配下）へセーブを混入させないため。
    if let Some(root) = assets_root {
        if let Some(parent) = root.parent() {
            return parent.join(SAVE_DIR_NAME);
        }
        return root.join(SAVE_DIR_NAME);
    }

    // 4) パッケージだが実行ファイル位置が取れなかった場合も含む最終フォールバック
    cwd.join(SAVE_DIR_NAME)
}

/// 実際の環境（環境変数・実行ファイル・asset_fs）を読んで保存先ファイルパスを返す。
///
/// 判定ロジック自体は `decide_save_dir`（純関数）に委譲し、
/// この関数は「環境を集めて渡す」だけに徹する。
pub fn resolve_save_path() -> PathBuf {
    use crate::engine::asset_fs;

    let env_override = std::env::var(SAVE_DIR_ENV).ok();
    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(Path::to_path_buf));
    let assets_root = asset_fs::root().cloned();
    let cwd = std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."));

    let dir = decide_save_dir(
        env_override.as_deref(),
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
            true,
            Some(Path::new("C:/game")),
            None,
            Path::new("C:/cwd"),
        );
        assert_eq!(dir, Path::new("C:/game").join(SAVE_DIR_NAME));
    }

    /// パッケージ実行では実行ファイル隣の save/ を使う（アセットルートより優先）。
    #[test]
    fn packaged_uses_exe_dir() {
        let dir = decide_save_dir(
            None,
            true,
            Some(Path::new("C:/game")),
            Some(Path::new("C:/game/assets")),
            Path::new("C:/cwd"),
        );
        assert_eq!(dir, Path::new("C:/game").join(SAVE_DIR_NAME));
    }

    /// エディタ Play（非パッケージ）ではアセットルートの**親**直下へ置く。
    /// assets/ の中へ置くとパッケージングに巻き込まれるため。
    #[test]
    fn editor_play_uses_assets_root_parent() {
        let dir = decide_save_dir(
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
        let dir = decide_save_dir(None, true, None, None, Path::new("C:/cwd"));
        assert_eq!(dir, Path::new("C:/cwd").join(SAVE_DIR_NAME));
    }

    /// 何も分からない場合はカレントディレクトリ直下。
    #[test]
    fn no_information_falls_back_to_cwd() {
        let dir = decide_save_dir(None, false, None, None, Path::new("."));
        assert_eq!(dir, Path::new(".").join(SAVE_DIR_NAME));
    }

    /// アセットルートが親を持たない（ルート直下）異常系でも panic しない。
    #[test]
    fn assets_root_without_parent_is_safe() {
        let dir = decide_save_dir(None, false, None, Some(Path::new("/")), Path::new("."));
        // Path::new("/").parent() は None なので root 自身 + save/
        assert!(dir.ends_with(SAVE_DIR_NAME));
    }
}
