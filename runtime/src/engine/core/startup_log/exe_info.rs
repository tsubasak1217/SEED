// ============================================================
//  exe_info.rs — 実行ファイル自身の情報
//
//  【役割】
//  起動ログ機構のあちこちで必要になる「実行ファイルのパス / 置き場 / 名前」を 1 か所に集める。
//  ・置き場（exe_dir）      … `assets.pak` や `dotnet/` の有無を調べる基準
//  ・名前（exe_stem）       … ログフォルダ名（`%LOCALAPPDATA%\{exe名}\logs`）とダイアログ表題
//  ・更新日時               … 「どのビルドを実行しているか」の目安（build.rs を持たないため）
//
//  【なぜ exe 名をアプリ名に使うか】
//  パッケージ化では `SEED.exe` が `{ゲーム名}.exe` へリネームされる（docs/packaging.md §1）。
//  ゲーム名の正典は `project_settings.json` の `game_name` だが、それは `assets.pak` の中に
//  あり、main() の時点（asset_fs 初期化前）には読めない。exe 名はリネーム後の名前そのもの
//  なので、ログの置き場を決めるにはこれで十分かつ確実。
// ============================================================

use std::path::{Path, PathBuf};

/// 実行ファイル名が取得できなかったときに使うフォールバック名。
const FALLBACK_APP_NAME: &str = "SEED";

/// 実行ファイルのフルパス。取得できなければ `None`。
pub fn exe_path() -> Option<PathBuf> {
    std::env::current_exe().ok()
}

/// 実行ファイルが置かれているフォルダ。取得できなければ `None`。
pub fn exe_dir() -> Option<PathBuf> {
    exe_path().and_then(|p| p.parent().map(Path::to_path_buf))
}

/// 実行ファイル名から拡張子を除いた「アプリ名」を返す【純関数】。
///
/// 例: `C:\game\わらしべフィッシング.exe` → `わらしべフィッシング`
/// 取得できない・空になる場合は `FALLBACK_APP_NAME`。
pub fn app_name_from_exe_path(exe_path: Option<&Path>) -> String {
    exe_path
        .and_then(|p| p.file_stem())
        .and_then(|s| s.to_str())
        .filter(|s| !s.trim().is_empty())
        .unwrap_or(FALLBACK_APP_NAME)
        .to_string()
}

/// 実環境の実行ファイル名から「アプリ名」を返す。
pub fn app_name() -> String {
    app_name_from_exe_path(exe_path().as_deref())
}

// ============================================================
//  単体テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn app_name_strips_extension() {
        assert_eq!(
            app_name_from_exe_path(Some(Path::new(r"C:\game\SEED.exe"))),
            "SEED"
        );
    }

    #[test]
    fn app_name_keeps_japanese_and_underscores() {
        // パッケージ化では `{ゲーム名}.exe` にリネームされるため、日本語がそのまま来る
        assert_eq!(
            app_name_from_exe_path(Some(Path::new(r"D:\dist\4008_わらしべフィッシング.exe"))),
            "4008_わらしべフィッシング"
        );
    }

    #[test]
    fn app_name_falls_back_when_unavailable() {
        assert_eq!(app_name_from_exe_path(None), FALLBACK_APP_NAME);
    }

    #[test]
    fn app_name_falls_back_for_dot_only_name() {
        // ファイル名が取れない/空になる病的なケースでも既定名へ落ちる
        assert_eq!(
            app_name_from_exe_path(Some(Path::new(r"C:\game\"))),
            "game"
        );
        assert_eq!(app_name_from_exe_path(Some(Path::new("/"))), FALLBACK_APP_NAME);
    }
}
