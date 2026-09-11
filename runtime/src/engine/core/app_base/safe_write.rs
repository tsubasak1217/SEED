// ============================================================
//  safe_write.rs — シーン／アクター等の「壊さない」ファイル書き込み
//
//  【なぜ必要か】
//   .scene / .actor は「そのファイルだけがゲームの正」であり、
//   上書きに失敗したり、間違った内容で上書きされたりすると復旧手段が無い。
//   実際に「別シーンの内容を別のパスへ上書きしてしまう」事故が発生した
//   （詳細は docs/editor_mcp.md のポストモーテム節）。
//
//  【この module が保証すること】
//   1. 原子的置換: 一度 `<file>.tmp` へ書き切ってから rename する。
//      途中でプロセスが死んでも元ファイルは無傷のまま残る。
//   2. 世代バックアップ: 置換の直前に旧ファイルを
//      `<assets>/.backup/<アセットルート相対ディレクトリ>/<名前>.<yyyyMMdd-HHmmss><拡張子>`
//      へ複製し、同一ファイルにつき最新 BACKUP_KEEP 世代だけ残す。
//
//  【復旧手順】
//   `<assets>/.backup/` 配下から目的のファイル名 + 時刻のものを探し、
//   タイムスタンプ部分を取り除いた名前で元の場所へコピーし直す。
// ============================================================

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

// ── 定数 ─────────────────────────────────────────────────────────

/// 同一ファイルにつき保持するバックアップ世代数。
pub const BACKUP_KEEP: usize = 10;

/// バックアップ置き場のディレクトリ名（アセットルート直下）。
pub const BACKUP_DIR_NAME: &str = ".backup";

/// 原子的置換に使う一時ファイルの拡張子（元ファイル名に追記する）。
const TEMP_SUFFIX: &str = ".tmp";

/// タイムスタンプが衝突したとき（同一秒内の連続保存）に付ける連番の上限。
const BACKUP_DEDUP_LIMIT: u32 = 1000;

// ── 公開 API ─────────────────────────────────────────────────────

/// バックアップを取ってから原子的にファイルを置換する。
///
/// * `path`     … 書き込み先の実パス（仮想パスは呼び出し側で解決しておくこと）
/// * `contents` … 書き込む内容
///
/// バックアップの失敗は書き込み自体を止めない（保存できない方が損害が大きい）。
/// 失敗した場合は `Err` ではなく戻り値へ警告メッセージとして入る。
pub fn write_atomic_with_backup(path: &Path, contents: &str) -> io::Result<Option<String>> {
    let backup_warning = match backup_existing(path) {
        Ok(_) => None,
        Err(e) => Some(format!("バックアップ作成に失敗: {e}")),
    };
    write_atomic(path, contents.as_bytes())?;
    Ok(backup_warning)
}

/// `<file>.tmp` へ書いてから rename する原子的置換。
pub fn write_atomic(path: &Path, bytes: &[u8]) -> io::Result<()> {
    if let Some(dir) = path.parent() {
        if !dir.as_os_str().is_empty() {
            fs::create_dir_all(dir)?;
        }
    }
    let tmp = temp_path_for(path);
    fs::write(&tmp, bytes)?;
    // Windows の rename は「置換先が存在すると失敗する」ため、存在時は置換相当にする。
    // std に atomic replace が無いので、失敗したら remove → rename でフォールバックする。
    match fs::rename(&tmp, path) {
        Ok(()) => Ok(()),
        Err(_) => {
            // 置換先を消してから rename する。ここで落ちても .tmp が残るので内容は失われない。
            let _ = fs::remove_file(path);
            let r = fs::rename(&tmp, path);
            if r.is_err() {
                let _ = fs::remove_file(&tmp);
            }
            r
        }
    }
}

/// 原子的置換で使う一時ファイルのパスを返す。
pub fn temp_path_for(path: &Path) -> PathBuf {
    let mut s = path.as_os_str().to_os_string();
    s.push(TEMP_SUFFIX);
    PathBuf::from(s)
}

/// 既存ファイルをバックアップ置き場へ複製し、世代数を `BACKUP_KEEP` に切り詰める。
/// 元ファイルが無い（新規作成）場合は何もしない。
pub fn backup_existing(path: &Path) -> io::Result<Option<PathBuf>> {
    if !path.is_file() {
        return Ok(None);
    }
    let Some(dir) = backup_dir_for(path) else {
        return Ok(None);
    };
    fs::create_dir_all(&dir)?;

    let stem = file_stem_str(path);
    let ext = file_ext_str(path);
    let stamp = now_timestamp();
    let dest = unique_backup_path(&dir, &stem, &ext, &stamp);
    fs::copy(path, &dest)?;
    rotate_backups(&dir, &stem, &ext, BACKUP_KEEP)?;
    Ok(Some(dest))
}

/// あるファイルのバックアップ置き場（ディレクトリ）を返す。
///
/// アセットルートが判っていて、かつ `path` がその配下にあれば
/// `<assets>/.backup/<相対ディレクトリ>` を返す。
/// アセット外（テンポラリ等）なら `<ファイルのあるディレクトリ>/.backup` を返す。
pub fn backup_dir_for(path: &Path) -> Option<PathBuf> {
    let parent = path.parent()?;
    if let Some(root) = crate::engine::asset_fs::root() {
        if let Ok(rel) = parent.strip_prefix(root) {
            return Some(root.join(BACKUP_DIR_NAME).join(rel));
        }
    }
    Some(parent.join(BACKUP_DIR_NAME))
}

/// バックアップの世代を `keep` 件へ切り詰める（古いものから削除）。戻り値は削除件数。
///
/// 対象は `<dir>/<stem>.<タイムスタンプ><ext>` の形をしたファイルだけ。
/// 別ファイルのバックアップや無関係なファイルには触れない。
/// タイムスタンプは固定長・辞書順＝時系列順なので、名前でソートすれば世代順になる。
pub fn rotate_backups(dir: &Path, stem: &str, ext: &str, keep: usize) -> io::Result<usize> {
    let mut names: Vec<String> = Vec::new();
    let Ok(entries) = fs::read_dir(dir) else {
        return Ok(0);
    };
    for entry in entries.flatten() {
        let name = entry.file_name().to_string_lossy().to_string();
        if is_backup_name(&name, stem, ext) {
            names.push(name);
        }
    }
    if names.len() <= keep {
        return Ok(0);
    }
    names.sort(); // 昇順＝古い順
    let remove_count = names.len() - keep;
    let mut removed = 0usize;
    for name in names.into_iter().take(remove_count) {
        if fs::remove_file(dir.join(name)).is_ok() {
            removed += 1;
        }
    }
    Ok(removed)
}

/// ファイル名が `<stem>.<タイムスタンプ><ext>` 形式のバックアップかどうかを判定する。
///
/// 素朴な前方後方一致ではなく「stem と ext の間がタイムスタンプ文字だけ」まで見る。
/// そうしないと `MainGame.scene` の世代整理が `MainGameOld.scene` の分まで巻き込む。
pub fn is_backup_name(name: &str, stem: &str, ext: &str) -> bool {
    let prefix = format!("{stem}.");
    if !name.starts_with(&prefix) || !name.ends_with(ext) {
        return false;
    }
    if name.len() < prefix.len() + ext.len() {
        return false;
    }
    let middle = &name[prefix.len()..name.len() - ext.len()];
    if middle.is_empty() {
        return false;
    }
    // タイムスタンプ（数字・ハイフン）と重複回避の連番（`_` + 数字）だけを許す。
    middle
        .chars()
        .all(|c| c.is_ascii_digit() || c == '-' || c == '_')
}

// ── 内部ヘルパー ─────────────────────────────────────────────────

/// 拡張子を除いたファイル名。
fn file_stem_str(path: &Path) -> String {
    path.file_stem()
        .map(|s| s.to_string_lossy().to_string())
        .unwrap_or_default()
}

/// ドット付きの拡張子（`.scene`）。拡張子が無ければ空文字。
fn file_ext_str(path: &Path) -> String {
    match path.extension() {
        Some(e) => format!(".{}", e.to_string_lossy()),
        None => String::new(),
    }
}

/// 同一秒内の連続保存でも上書きしないよう、空いている名前を探す。
fn unique_backup_path(dir: &Path, stem: &str, ext: &str, stamp: &str) -> PathBuf {
    let first = dir.join(format!("{stem}.{stamp}{ext}"));
    if !first.exists() {
        return first;
    }
    for i in 1..BACKUP_DEDUP_LIMIT {
        let candidate = dir.join(format!("{stem}.{stamp}_{i}{ext}"));
        if !candidate.exists() {
            return candidate;
        }
    }
    first
}

/// 現在時刻（UTC）の `yyyyMMdd-HHmmss` 文字列を作る。
///
/// ランタイムは日付ライブラリに依存していないため、UNIX 秒から自前で変換する。
/// 世代の並べ替えに使うだけなので、タイムゾーン・うるう秒の厳密さは不要。
fn now_timestamp() -> String {
    let secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    format_unix_timestamp(secs)
}

/// UNIX 秒（UTC）を `yyyyMMdd-HHmmss` へ整形する。日付計算のテスト用に切り出してある。
pub fn format_unix_timestamp(secs: u64) -> String {
    // 1 日・1 時間・1 分の秒数（マジックナンバー回避のためのローカル定数）
    const SECS_PER_DAY: u64 = 86_400;
    const SECS_PER_HOUR: u64 = 3_600;
    const SECS_PER_MIN: u64 = 60;

    let days = secs / SECS_PER_DAY;
    let rem = secs % SECS_PER_DAY;
    let (h, m, s) = (
        rem / SECS_PER_HOUR,
        (rem % SECS_PER_HOUR) / SECS_PER_MIN,
        rem % SECS_PER_MIN,
    );
    let (y, mo, d) = civil_from_days(days as i64);
    format!("{y:04}{mo:02}{d:02}-{h:02}{m:02}{s:02}")
}

/// 1970-01-01 からの経過日数を (年, 月, 日) へ変換する（Howard Hinnant の civil_from_days）。
/// 内部の数値はアルゴリズム由来の固定値で、意味のある調整点ではない。
fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = (z - era * 146_097) as u64; // [0, 146096]
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365; // [0, 399]
    let y = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100); // [0, 365]
    let mp = (5 * doy + 2) / 153; // [0, 11]
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32; // [1, 31]
    let m = (if mp < 10 { mp + 3 } else { mp - 9 }) as u32; // [1, 12]
    (if m <= 2 { y + 1 } else { y }, m, d)
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の一時ディレクトリを作る（外部クレートに依存しない）。
    fn temp_dir(tag: &str) -> PathBuf {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("seed_safe_write_{tag}_{nanos}"));
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn write_atomic_creates_and_replaces() {
        let dir = temp_dir("atomic");
        let file = dir.join("a.scene");
        write_atomic(&file, b"one").unwrap();
        assert_eq!(fs::read_to_string(&file).unwrap(), "one");
        write_atomic(&file, b"two").unwrap();
        assert_eq!(fs::read_to_string(&file).unwrap(), "two");
        // 一時ファイルが残っていないこと
        assert!(!temp_path_for(&file).exists());
        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn backup_name_matching_is_exact() {
        assert!(is_backup_name(
            "MainGame.20260907-101112.scene",
            "MainGame",
            ".scene"
        ));
        assert!(is_backup_name(
            "MainGame.20260907-101112_3.scene",
            "MainGame",
            ".scene"
        ));
        // 別ファイルのバックアップを巻き込まない
        assert!(!is_backup_name(
            "MainGameOld.20260907-101112.scene",
            "MainGame",
            ".scene"
        ));
        // 本体ファイルそのものはバックアップではない
        assert!(!is_backup_name("MainGame.scene", "MainGame", ".scene"));
        // 拡張子違いは対象外
        assert!(!is_backup_name(
            "MainGame.20260907-101112.actor",
            "MainGame",
            ".scene"
        ));
    }

    #[test]
    fn rotate_backups_keeps_newest_n() {
        let dir = temp_dir("rotate");
        // 15 世代 + 無関係ファイルを置く
        for i in 0..15 {
            fs::write(dir.join(format!("MainGame.20260901-0000{i:02}.scene")), "x").unwrap();
        }
        fs::write(dir.join("Other.20260901-000000.scene"), "x").unwrap();
        fs::write(dir.join("MainGame.scene"), "x").unwrap();

        let removed = rotate_backups(&dir, "MainGame", ".scene", BACKUP_KEEP).unwrap();
        assert_eq!(removed, 5);

        let mut left: Vec<String> = fs::read_dir(&dir)
            .unwrap()
            .flatten()
            .map(|e| e.file_name().to_string_lossy().to_string())
            .filter(|n| is_backup_name(n, "MainGame", ".scene"))
            .collect();
        left.sort();
        assert_eq!(left.len(), BACKUP_KEEP);
        // 残ったのは新しい方（末尾 10 件）
        assert_eq!(left[0], "MainGame.20260901-000005.scene");
        // 無関係ファイルは消えていない
        assert!(dir.join("Other.20260901-000000.scene").exists());
        assert!(dir.join("MainGame.scene").exists());
        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn rotate_backups_noop_when_under_limit() {
        let dir = temp_dir("rotate_small");
        for i in 0..3 {
            fs::write(dir.join(format!("A.20260901-00000{i}.scene")), "x").unwrap();
        }
        assert_eq!(rotate_backups(&dir, "A", ".scene", BACKUP_KEEP).unwrap(), 0);
        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn backup_existing_copies_previous_content() {
        let dir = temp_dir("backup");
        let file = dir.join("S.scene");
        fs::write(&file, "old").unwrap();
        let dest = backup_existing(&file).unwrap().expect("バックアップが作られるはず");
        assert_eq!(fs::read_to_string(&dest).unwrap(), "old");
        // 新規ファイル（存在しない）ならバックアップ無し
        assert!(backup_existing(&dir.join("none.scene")).unwrap().is_none());
        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn timestamp_format_is_sortable_and_correct() {
        // 2026-09-07 10:11:12 UTC
        assert_eq!(format_unix_timestamp(1_788_775_872), "20260907-101112");
        // 単調増加する入力に対し文字列も昇順になること（世代ソートの前提）
        assert!(format_unix_timestamp(1_788_775_872) < format_unix_timestamp(1_788_775_873));
    }
}
