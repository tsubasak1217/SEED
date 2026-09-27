// ============================================================
//  save/file_set.rs — セーブのフォルダに置くファイルの名前（本体・一時・1 世代前・壊れた本体）
//
//  【役割】
//  本体（save.json）の場所から、同じフォルダに置く仲間のファイルの名前を決めるだけの層。
//  I/O はフォルダの一覧（壊れた本体の退避ファイルを探す）だけで、読み書きはしない。
//
//  【ファイル】（すべて本体と同じフォルダ。別フォルダだと rename がコピーになり原子性が失われる）
//    save.json                     本体（今の世代）
//    save.json.tmp                 書き出しの途中の一時ファイル（書き終えて sync してから本体へ rename）
//    save.json.bak                 1 つ前の世代（本体を置き換える直前に rename で回したもの）
//    save.json.corrupt-<時刻>      読めなかった本体を上書きせずに退避したもの（1 つだけ残す）
//  `.tmp` の名前は W1-S より前の実装（`with_extension("json.tmp")`）と同じ。
// ============================================================

use std::fs;
use std::path::{Path, PathBuf};

/// 一時ファイルに付ける接尾辞（本体のファイル名の後ろに足す）。
pub const TEMP_SUFFIX: &str = ".tmp";

/// 1 つ前の世代に付ける接尾辞（本体のファイル名の後ろに足す）。
pub const BACKUP_SUFFIX: &str = ".bak";

/// 壊れた本体の退避ファイルに付ける印（本体のファイル名の後ろに足し、その後ろに時刻を付ける）。
pub const CORRUPT_MARKER: &str = ".corrupt-";

/// セーブの本体と、その仲間のファイルのパス一式。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SaveFileSet {
    /// 本体（今の世代）。
    primary: PathBuf,
    /// 書き出しの途中の一時ファイル。
    temp: PathBuf,
    /// 1 つ前の世代。
    backup: PathBuf,
}

impl SaveFileSet {
    /// 本体のパスから仲間のファイルのパスを決める。
    pub fn new(primary: PathBuf) -> Self {
        let temp = with_suffix(&primary, TEMP_SUFFIX);
        let backup = with_suffix(&primary, BACKUP_SUFFIX);
        Self { primary, temp, backup }
    }

    /// 本体（今の世代）のパス。
    pub fn primary(&self) -> &Path {
        &self.primary
    }

    /// 書き出しの途中の一時ファイルのパス。
    pub fn temp(&self) -> &Path {
        &self.temp
    }

    /// 1 つ前の世代のパス。
    pub fn backup(&self) -> &Path {
        &self.backup
    }

    /// ファイルを置くフォルダ。本体のパスがファイル名だけ（相対でフォルダ無し）ならカレント（`.`）。
    pub fn dir(&self) -> PathBuf {
        match self.primary.parent() {
            Some(parent) if !parent.as_os_str().is_empty() => parent.to_path_buf(),
            _ => PathBuf::from("."),
        }
    }

    /// 壊れた本体の退避先（`<本体>.corrupt-<時刻>`）。
    ///
    /// # 引数
    /// * `stamp` - 時刻の文字列（`safe_write::format_unix_timestamp` の `yyyyMMdd-HHmmss`）
    pub fn corrupt_path(&self, stamp: &str) -> PathBuf {
        with_suffix(&self.primary, &format!("{CORRUPT_MARKER}{stamp}"))
    }

    /// フォルダにある、壊れた本体の退避ファイルの一覧（順不同）。フォルダが読めなければ空。
    pub fn existing_corrupt_files(&self) -> Vec<PathBuf> {
        let Some(prefix) = self.corrupt_name_prefix() else { return Vec::new() };
        let Ok(entries) = fs::read_dir(self.dir()) else { return Vec::new() };
        entries
            .flatten()
            .filter(|entry| entry.file_name().to_string_lossy().starts_with(&prefix))
            .map(|entry| entry.path())
            .collect()
    }

    /// 退避ファイルの名前の頭（`save.json.corrupt-`）。本体のファイル名が無ければ `None`。
    fn corrupt_name_prefix(&self) -> Option<String> {
        let name = self.primary.file_name()?.to_string_lossy();
        Some(format!("{name}{CORRUPT_MARKER}"))
    }
}

/// パスのファイル名の後ろに接尾辞を足す（`save.json` → `save.json.bak`）。
///
/// `Path::with_extension` だと既存の拡張子（`.json`）を置き換えてしまうので、文字列として後ろに足す。
fn with_suffix(path: &Path, suffix: &str) -> PathBuf {
    let mut s = path.as_os_str().to_os_string();
    s.push(suffix);
    PathBuf::from(s)
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 仲間のファイルは本体の名前の後ろに接尾辞を足した名前で、同じフォルダに置かれる。
    #[test]
    fn sibling_names_append_suffix_in_same_dir() {
        let files = SaveFileSet::new(PathBuf::from("dir/save.json"));
        assert_eq!(files.temp(), Path::new("dir/save.json.tmp"));
        assert_eq!(files.backup(), Path::new("dir/save.json.bak"));
        assert_eq!(files.corrupt_path("20260927-101530"), PathBuf::from("dir/save.json.corrupt-20260927-101530"));
        assert_eq!(files.dir(), PathBuf::from("dir"));
    }

    /// 一時ファイルの名前は W1-S より前の実装（with_extension("json.tmp")）と同じ。
    #[test]
    fn temp_name_matches_previous_implementation() {
        let primary = PathBuf::from("dir/save.json");
        assert_eq!(SaveFileSet::new(primary.clone()).temp(), primary.with_extension("json.tmp"));
    }

    /// フォルダの無い相対パスではカレントを使う（sync・一覧で空のパスを開かない）。
    #[test]
    fn bare_file_name_uses_current_dir() {
        let files = SaveFileSet::new(PathBuf::from("save.json"));
        assert_eq!(files.dir(), PathBuf::from("."));
    }
}
