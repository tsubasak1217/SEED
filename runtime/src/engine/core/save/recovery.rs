// ============================================================
//  save/recovery.rs — セーブの読み込みと、本体が無い・壊れているときの 1 世代前への切り替え
//
//  【役割】
//  起動して最初にセーブへ触れたとき（store.rs の SaveStore::load）に、どのファイルから読むかを決めて読む。
//  書き出し（durable_file.rs）・本文の変換（codec.rs）は持たない。
//
//  【読む順】（W1-S。docs/app_platform_roadmap.md §2.7）
//    1. 本体（save.json）。読めればそれを使う（普段どおり）。
//    2. 本体が無い・壊れていれば 1 世代前（save.json.bak）。書き出しの 2 と 3 の間で落ちると
//       「本体が無く .bak がある」瞬間ができる（durable_file.rs の表）ので、無いときも .bak を見る。
//    3. どちらも無ければ空（初めての起動）。どちらかが有ったのに読めなければ空（復旧できなかった）。
//  書き出しの途中の一時ファイル（save.json.tmp）は読まない（半端かもしれず、書き出しが済んでいない世代のため）。
//  どこから読んだかは `LoadSource` で返し、スクリプトは `SaveData.RecoveredFrom` で知れる。
//
//  【壊れた本体】
//  読めなかった本体は上書きせずに `save.json.corrupt-<時刻>` へ rename で退避する（中身は手で調べられる）。
//  退避ファイルは 1 つだけ残す（新しいものを残し、古いものは消す。増え続けない）。
//  退避できなかったとき（ファイルがロックされている等）は `primary_untrusted` を立て、次の書き出しで
//  もう一度退避を試す（それでもだめなら .bak へ回さずに置き換える。壊れた本体で正しい .bak を消さないため）。
//
//  【ログ】
//  1 世代前から読んだ・退避した・空で始めたときは `[SEED SAVE] 警告:` の行を標準エラーへ出す
//  （Android は logcat へ転送される）。
// ============================================================

use std::collections::BTreeMap;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::time::Duration;

use super::codec::{self, Decoded};
use super::file_set::SaveFileSet;
use super::value::SaveValue;
use crate::engine::core::app_base::safe_write;

/// `SaveData.RecoveredFrom` の番号: 普段どおり本体を読めた、または初めての起動（C# `SaveRecovery.None`）。
pub const RECOVERY_CODE_NONE: i32 = 0;

/// `SaveData.RecoveredFrom` の番号: 1 世代前（.bak）から読んだ（C# `SaveRecovery.Backup`）。
pub const RECOVERY_CODE_BACKUP: i32 = 1;

/// `SaveData.RecoveredFrom` の番号: 本体か .bak が有ったのに読めず、空で始めた（C# `SaveRecovery.Lost`）。
pub const RECOVERY_CODE_LOST: i32 = 2;

/// 読み込みの I/O エラー（見つからない以外）を何回まで試すか。
///
/// Windows でウイルス対策などが書き出し直後のファイルを一瞬掴んでいるときの読み損ねで、
/// 最新の本体を「壊れている」と退避してしまわないための再試行。壊れた中身（JSON として読めない）は再試行しない。
const READ_ATTEMPTS: u32 = 3;

/// 読み込みの再試行の間隔。
const READ_RETRY_DELAY: Duration = Duration::from_millis(50);

/// ログの行頭（セーブの他のログと同じ印）。
const LOG_TAG: &str = "[SEED SAVE]";

/// ロードのときにどこから読んだか。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LoadSource {
    /// 本体を読めた（普段どおり）。
    Primary,
    /// 本体も 1 世代前も無い（初めての起動）。空で始める。
    Fresh,
    /// 本体が無い・壊れていたので、1 世代前（.bak）から読んだ。
    Backup,
    /// 本体か 1 世代前が有ったのに、どれも読めなかった。空で始める。
    Unrecoverable,
}

impl LoadSource {
    /// スクリプトへ見せる番号（`SaveData.RecoveredFrom`。C# の `SaveRecovery` と一致させる）。
    pub fn recovery_code(self) -> i32 {
        match self {
            LoadSource::Primary | LoadSource::Fresh => RECOVERY_CODE_NONE,
            LoadSource::Backup => RECOVERY_CODE_BACKUP,
            LoadSource::Unrecoverable => RECOVERY_CODE_LOST,
        }
    }

    /// 復旧の読み込みだったか（本体を普段どおり読めた・初めての起動ではない）。
    ///
    /// 復旧したときは、次の書き出しで本体を作り直すために未書き出しの印を立てる（store.rs）。
    pub fn is_recovery(self) -> bool {
        matches!(self, LoadSource::Backup | LoadSource::Unrecoverable)
    }
}

/// 読み込みの結果。
#[derive(Debug)]
pub struct LoadOutcome {
    /// 読めたキー・バリュー（空で始めるときは空）。
    pub values: BTreeMap<String, SaveValue>,
    /// どこから読んだか。
    pub source: LoadSource,
    /// 本体の場所に、退避できなかった壊れた本体が残っているか（次の書き出しで .bak へ回してはいけない）。
    pub primary_untrusted: bool,
}

/// 1 つのファイルを読んだ結果。
enum Candidate {
    /// ファイルが無い。
    Missing,
    /// 読めた。
    Valid(Decoded),
    /// 有るのに読めない（理由付き）。
    Broken(String),
}

/// セーブを読む（ファイル先頭の「読む順」）。どの場合も panic せず、読めなければ空で返す。
pub fn load(files: &SaveFileSet) -> LoadOutcome {
    match read_candidate(files.primary()) {
        Candidate::Valid(decoded) => {
            report_skipped(&decoded, files.primary());
            LoadOutcome { values: decoded.values, source: LoadSource::Primary, primary_untrusted: false }
        }
        Candidate::Missing => load_backup(files, false),
        Candidate::Broken(reason) => {
            eprintln!("{LOG_TAG} 警告: 本体（{}）を読めません: {reason}", files.primary().display());
            // 壊れた本体は上書きせずに退避する（退避できなければ、次の書き出しで .bak へ回さない印を立てる）
            let primary_untrusted = match quarantine_primary(files) {
                Ok(kept) => {
                    eprintln!("{LOG_TAG} 警告: 壊れた本体を {} へ退避しました（中身は手で調べられます）", kept.display());
                    false
                }
                Err(e) => {
                    eprintln!("{LOG_TAG} 警告: 壊れた本体を退避できませんでした（次の書き出しでもう一度試します）: {e}");
                    true
                }
            };
            let mut outcome = load_backup(files, true);
            outcome.primary_untrusted = primary_untrusted;
            outcome
        }
    }
}

/// 壊れた本体を `<本体>.corrupt-<時刻>` へ rename で退避し、他の退避ファイルを消す（1 つだけ残す）。
///
/// # 戻り値
/// 退避先のパス。rename できなければ `Err`（本体はそのまま）。古い退避ファイルを消せないのは失敗にしない。
pub fn quarantine_primary(files: &SaveFileSet) -> io::Result<PathBuf> {
    let target = files.corrupt_path(&safe_write::format_unix_timestamp(now_unix_seconds()));
    fs::rename(files.primary(), &target)?;
    for old in files.existing_corrupt_files() {
        if old == target {
            continue;
        }
        if let Err(e) = fs::remove_file(&old) {
            eprintln!("{LOG_TAG} 警告: 古い退避ファイル {} を消せませんでした: {e}", old.display());
        }
    }
    Ok(target)
}

/// 本体を使えないときに 1 世代前（.bak）から読む。
///
/// # 引数
/// * `primary_existed` - 本体のファイルが有ったか（有ったのに読めなかった）。`false` なら本体は無かった。
fn load_backup(files: &SaveFileSet, primary_existed: bool) -> LoadOutcome {
    let (values, source) = match read_candidate(files.backup()) {
        Candidate::Valid(decoded) => {
            report_skipped(&decoded, files.backup());
            let temp_note = if files.temp().exists() {
                "（書き出しの途中の一時ファイルが残っていました。書き出しの最中に終了した跡です）"
            } else {
                ""
            };
            eprintln!(
                "{LOG_TAG} 警告: 本体が{}ので、1 世代前（{}）から読みました{temp_note}。直前の保存が失われている可能性があります（SaveData.RecoveredFrom = Backup）",
                if primary_existed { "壊れていた" } else { "無かった" },
                files.backup().display()
            );
            (decoded.values, LoadSource::Backup)
        }
        // 本体も 1 世代前も無い＝初めての起動（ログは従来どおり出さない）
        Candidate::Missing if !primary_existed => (BTreeMap::new(), LoadSource::Fresh),
        Candidate::Missing => {
            eprintln!(
                "{LOG_TAG} 警告: 本体が壊れていて 1 世代前（{}）も無いため、空のセーブで始めます（SaveData.RecoveredFrom = Lost）",
                files.backup().display()
            );
            (BTreeMap::new(), LoadSource::Unrecoverable)
        }
        Candidate::Broken(reason) => {
            eprintln!(
                "{LOG_TAG} 警告: 1 世代前（{}）も読めないため、空のセーブで始めます（SaveData.RecoveredFrom = Lost）: {reason}",
                files.backup().display()
            );
            (BTreeMap::new(), LoadSource::Unrecoverable)
        }
    };
    LoadOutcome { values, source, primary_untrusted: false }
}

/// ファイルを 1 つ読んで、無い・読めた・壊れているに分ける。
///
/// 見つからない以外の I/O エラーは `READ_ATTEMPTS` 回まで試す（一時的なロックで最新の本体を退避しないため）。
fn read_candidate(path: &Path) -> Candidate {
    let mut last_error: Option<io::Error> = None;
    for attempt in 0..READ_ATTEMPTS {
        match fs::read(path) {
            Ok(bytes) => return classify(bytes),
            Err(e) if e.kind() == io::ErrorKind::NotFound => return Candidate::Missing,
            Err(e) => {
                last_error = Some(e);
                if attempt + 1 < READ_ATTEMPTS {
                    std::thread::sleep(READ_RETRY_DELAY);
                }
            }
        }
    }
    let detail = last_error.map(|e| e.to_string()).unwrap_or_default();
    Candidate::Broken(format!("ファイルを読めません（{READ_ATTEMPTS} 回試しました）: {detail}"))
}

/// 読んだバイト列を本文として解釈する。UTF-8 でない・JSON として読めないものは壊れている。
fn classify(bytes: Vec<u8>) -> Candidate {
    let text = match String::from_utf8(bytes) {
        Ok(text) => text,
        Err(e) => return Candidate::Broken(format!("UTF-8 の文字列ではありません（{e}）")),
    };
    match codec::decode(&text) {
        Ok(decoded) => Candidate::Valid(decoded),
        Err(e) => Candidate::Broken(e.to_string()),
    }
}

/// 読み飛ばした値があればログに残す（従来どおりの 1 行）。
fn report_skipped(decoded: &Decoded, path: &Path) {
    if decoded.skipped > 0 {
        eprintln!("{LOG_TAG} {} 件の非対応な値を読み飛ばしました ({})", decoded.skipped, path.display());
    }
}

/// 今の時刻（UNIX 秒）。時計が 1970 年より前を指す異常時は 0。
fn now_unix_seconds() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

// ============================================================
//  ユニットテスト（ストアを通した壊れ方ごとの試験は store.rs）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// スクリプトへ見せる番号は、普段・初回が 0、.bak が 1、読めなかったが 2（C# の SaveRecovery と同じ）。
    #[test]
    fn recovery_codes_match_script_enum() {
        assert_eq!(LoadSource::Primary.recovery_code(), 0);
        assert_eq!(LoadSource::Fresh.recovery_code(), 0);
        assert_eq!(LoadSource::Backup.recovery_code(), 1);
        assert_eq!(LoadSource::Unrecoverable.recovery_code(), 2);
    }

    /// 普段の読み込み・初回は復旧ではない。
    #[test]
    fn only_backup_and_unrecoverable_are_recoveries() {
        assert!(!LoadSource::Primary.is_recovery());
        assert!(!LoadSource::Fresh.is_recovery());
        assert!(LoadSource::Backup.is_recovery());
        assert!(LoadSource::Unrecoverable.is_recovery());
    }

    /// UTF-8 でないバイト列は壊れている。
    #[test]
    fn non_utf8_bytes_are_broken() {
        assert!(matches!(classify(vec![0xFF, 0xFE, 0x00]), Candidate::Broken(_)));
    }
}
