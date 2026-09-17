// =============================================================================
// SEED アカウント発行窓口 : ファイルの原子的置換
// =============================================================================
// `accounts.json` / `issuer_key.json` / `jwks.json` はいずれも
// 「壊れた中身が残ると復旧できない」種類のファイルなので、
// 書き込みは必ず「一時ファイルへ書く → sync → rename で置換」で行う。
// 同一ディレクトリ内の rename は OS が原子的に扱うため、
// 書き込み中にプロセスが落ちても半端な JSON は残らない。
//
// 同じ流儀を `src/lock_store_file.rs` でも使っている。
// あちらはロックストアに閉じた実装なので、こちらは窓口側の共通実装とする
// （将来どちらかに寄せるなら、この 2 つを 1 つのユーティリティにまとめること）。
// =============================================================================

use std::fs;
use std::io::Write;
use std::path::Path;
use std::path::PathBuf;

/// 原子的置換に使う一時ファイルの拡張子。
/// 本体と同じディレクトリに作ることで、rename が同一ボリューム内で完結する。
const TEMP_FILE_SUFFIX: &str = ".tmp";

/// 文字列をファイルへ原子的に書き込む。
///
/// 親ディレクトリが無ければ作る。
///
/// # Errors
/// ディレクトリ作成・書き込み・同期・rename のいずれかに失敗したとき、
/// 何が起きたか分かる日本語のメッセージを返す。
pub fn write_atomic(path: &Path, contents: &str) -> Result<(), String> {
    if let Some(parent) = path.parent()
        && !parent.as_os_str().is_empty()
    {
        fs::create_dir_all(parent)
            .map_err(|e| format!("ディレクトリを作成できません {parent:?}: {e}"))?;
    }

    let mut temp_path = path.to_path_buf().into_os_string();
    temp_path.push(TEMP_FILE_SUFFIX);
    let temp_path = PathBuf::from(temp_path);

    {
        let mut file = fs::File::create(&temp_path)
            .map_err(|e| format!("一時ファイルを作成できません {temp_path:?}: {e}"))?;
        file.write_all(contents.as_bytes())
            .map_err(|e| format!("一時ファイルへ書き込めません {temp_path:?}: {e}"))?;
        file.sync_all()
            .map_err(|e| format!("一時ファイルを同期できません {temp_path:?}: {e}"))?;
    }

    // Windows の std::fs::rename は MoveFileEx(MOVEFILE_REPLACE_EXISTING) 相当で、
    // 既存ファイルがあっても置換する。事前削除は不要。
    fs::rename(&temp_path, path)
        .map_err(|e| format!("ファイルを置換できません {temp_path:?} -> {path:?}: {e}"))?;

    Ok(())
}

/// ファイルを文字列として読む。存在しなければ `None`。
///
/// 「初回起動でファイルがまだ無い」と「読めない」を呼び出し側で
/// 区別できるようにするための薄いラッパー。
///
/// # Errors
/// ファイルが存在するのに読めないとき。
pub fn read_if_exists(path: &Path) -> Result<Option<String>, String> {
    match fs::read_to_string(path) {
        Ok(text) => Ok(Some(text)),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(format!("ファイルを読めません {path:?}: {e}")),
    }
}

// =============================================================================
// 単体テスト
// =============================================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 書いたものが読み戻せること、親ディレクトリが作られること。
    #[test]
    fn writes_and_creates_parent_directory() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("nested").join("deeper").join("data.json");

        write_atomic(&path, "{\"a\":1}").unwrap();
        assert_eq!(read_if_exists(&path).unwrap().unwrap(), "{\"a\":1}");
    }

    /// 既存ファイルを上書きできること、一時ファイルが残らないこと。
    #[test]
    fn replaces_existing_file_and_leaves_no_temp() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("data.json");

        write_atomic(&path, "old").unwrap();
        write_atomic(&path, "new").unwrap();
        assert_eq!(read_if_exists(&path).unwrap().unwrap(), "new");

        let mut temp = path.clone().into_os_string();
        temp.push(TEMP_FILE_SUFFIX);
        assert!(
            !PathBuf::from(temp).exists(),
            "一時ファイルが残っている（rename に失敗している）"
        );
    }

    /// 存在しないファイルは None（エラーではない）。
    #[test]
    fn missing_file_is_none() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("absent.json");
        assert!(read_if_exists(&path).unwrap().is_none());
    }

    /// 一時ファイルの置き場をディレクトリで塞ぐと、書き込みが失敗すること
    /// （失敗を黙って握り潰していないことの確認）。
    #[test]
    fn write_failure_is_reported() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("blocked.json");

        let mut temp = path.clone().into_os_string();
        temp.push(TEMP_FILE_SUFFIX);
        fs::create_dir(PathBuf::from(temp)).unwrap();

        assert!(write_atomic(&path, "x").is_err());
    }
}
