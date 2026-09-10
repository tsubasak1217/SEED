// ============================================================
//  log_path.rs — 起動ログの置き場とファイル名、古いログの後始末
//
//  【役割】
//  ・ログフォルダの候補を優先順に並べる（`%LOCALAPPDATA%\{アプリ名}\logs` → exe 隣の `logs`）
//  ・ログファイル名を作る（`seed_YYYYMMDD_HHMMSS.log`）
//  ・世代管理（最新 N 件だけ残す）の「どれを消すか」を決める
//
//  【設計方針】
//  「決める」（純関数・単体テスト対象）と「作る／消す」（ファイル系・不純）を分離する。
//  ファイル系の失敗は一切致命化しない — ログが残せなくてもゲームは起動しなければならない。
// ============================================================

use std::path::{Path, PathBuf};

use super::timestamp::Timestamp;

// ── 命名規則 ────────────────────────────────────────────────
/// ログファイル名の接頭辞。
const LOG_FILE_PREFIX: &str = "seed_";
/// ログファイルの拡張子（ドット込み）。
const LOG_FILE_EXTENSION: &str = ".log";
/// ログを格納するサブフォルダ名。
const LOG_DIR_NAME: &str = "logs";

// ── 世代管理 ────────────────────────────────────────────────
/// 残す起動ログの最大件数。これを超えた古いログは起動時に削除する。
/// 「不具合報告のときに直近数回ぶんが残っていればよい」という基準。
pub const MAX_KEPT_LOG_FILES: usize = 10;

// ── 環境変数 ────────────────────────────────────────────────
/// ユーザーごとのローカルアプリデータフォルダを指す環境変数名。
pub const LOCAL_APP_DATA_ENV: &str = "LOCALAPPDATA";

/// ログフォルダの候補を優先順に返す【純関数】。
///
/// # 優先順
/// 1. `{exe のあるフォルダ}\logs` — **既定**。
///    配布物のフォルダ構成を「exe / assets.pak / DLL をまとめたフォルダ／
///    実行時生成の caches・logs・saved」に統一する方針のため、
///    ログも配布フォルダの中に置く（ユーザーが自分で見つけて送れる）。
/// 2. `%LOCALAPPDATA%\{アプリ名}\logs` — exe の隣に書けないときのフォールバック。
///    Program Files 配下へ展開された・読み取り専用メディアから起動した、
///    といったケースで効く。
///
/// どちらも取れない場合は空の Vec を返し、呼び出し側は「ログを諦める」判断をする。
pub fn log_dir_candidates(
    local_app_data: Option<&Path>,
    exe_dir: Option<&Path>,
    app_name: &str,
) -> Vec<PathBuf> {
    let mut candidates = Vec::new();
    if let Some(dir) = exe_dir {
        candidates.push(dir.join(LOG_DIR_NAME));
    }
    if let Some(local) = local_app_data {
        candidates.push(local.join(app_name).join(LOG_DIR_NAME));
    }
    candidates
}

/// ログファイル名を作る【純関数】。
///
/// 例: `seed_20260910_143059.log`
/// ゼロ埋め固定長なので、フォルダ内で名前順に並べると時系列順になる。
pub fn log_file_name(started_at: &Timestamp) -> String {
    format!(
        "{}{}{}",
        LOG_FILE_PREFIX,
        started_at.format_compact(),
        LOG_FILE_EXTENSION
    )
}

/// 与えられたファイル名が「このモジュールが作った起動ログ」かを判定する【純関数】。
///
/// 世代管理で削除してよいのはこの形式のファイルだけ。
/// 同じフォルダにユーザーが置いた別ファイルを巻き込まないための門番。
pub fn is_log_file_name(name: &str) -> bool {
    name.starts_with(LOG_FILE_PREFIX)
        && name.ends_with(LOG_FILE_EXTENSION)
        // 接頭辞と拡張子だけの `seed_.log` は対象外（日時部分が空 = 別物とみなす）
        && name.len() > LOG_FILE_PREFIX.len() + LOG_FILE_EXTENSION.len()
}

/// 世代管理で削除するログファイル名を決める【純関数】。
///
/// # 引数
/// * `existing` — フォルダ内にある **起動ログのファイル名だけ** を集めたもの（順不同）
/// * `keep` — 残す件数（新しい方から数える）
///
/// # 戻り値
/// 削除対象のファイル名（入力の部分集合）。`keep` 以下しか無ければ空。
///
/// 名前の辞書順＝時系列順（`log_file_name` のゼロ埋め固定長が根拠）なので、
/// 降順に並べて先頭 `keep` 件を残し、残りを削除対象とする。
pub fn logs_to_delete(existing: &[String], keep: usize) -> Vec<String> {
    let mut sorted: Vec<String> = existing.to_vec();
    // 降順（新しい順）に並べる
    sorted.sort_by(|a, b| b.cmp(a));
    sorted.into_iter().skip(keep).collect()
}

/// ログフォルダを実際に用意する（既に存在する場合も成功扱い）。
///
/// 「作れたか」と「そこにファイルを作れるか」は別問題（フォルダはあるが書き込み権限が無い、
/// という配布先がある）なので、成否の最終判定は呼び出し側がログファイルを開いて行う。
pub fn ensure_log_dir(dir: &Path) -> std::io::Result<()> {
    std::fs::create_dir_all(dir)
}

/// ログフォルダ内の起動ログを最新 `keep` 件だけ残して削除する。
///
/// `current_file_name` は**今まさに書き込み中**のログ。名前順では最新なので通常は
/// 削除対象に入らないが、時刻の巻き戻し（時刻同期・サマータイム）で順序が崩れた場合に
/// 備えて明示的に除外する（開いたまま削除しても書き込みは続くが、閉じた瞬間に消えるため）。
///
/// 削除の失敗（他プロセスが開いている等）は握りつぶす — 後始末は best effort であり、
/// ここで起動を止める理由にはならない。
pub fn prune_old_logs(dir: &Path, keep: usize, current_file_name: &str) {
    // フォルダが読めなければ何もしない
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };

    // 起動ログの形式に一致するファイル名だけを集める
    let names: Vec<String> = entries
        .flatten()
        .filter(|e| e.file_type().map(|t| t.is_file()).unwrap_or(false))
        .filter_map(|e| e.file_name().into_string().ok())
        .filter(|name| is_log_file_name(name))
        .collect();

    for name in logs_to_delete(&names, keep) {
        // 実行中のログだけは何があっても消さない
        if name == current_file_name {
            continue;
        }
        let _ = std::fs::remove_file(dir.join(name));
    }
}

// ============================================================
//  単体テスト（純関数のみ）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn candidates_prefer_exe_dir_then_local_app_data() {
        let c = log_dir_candidates(
            Some(Path::new(r"C:\Users\u\AppData\Local")),
            Some(Path::new(r"C:\game")),
            "MyGame",
        );
        assert_eq!(
            c,
            vec![
                PathBuf::from(r"C:\game\logs"),
                PathBuf::from(r"C:\Users\u\AppData\Local\MyGame\logs"),
            ]
        );
    }

    #[test]
    fn candidates_fall_back_to_local_app_data_only() {
        // exe の場所が取れない場合でも %LOCALAPPDATA% があれば書ける
        let c = log_dir_candidates(Some(Path::new(r"C:\Users\u\AppData\Local")), None, "MyGame");
        assert_eq!(c, vec![PathBuf::from(r"C:\Users\u\AppData\Local\MyGame\logs")]);
    }

    #[test]
    fn candidates_use_exe_dir_when_env_missing() {
        let c = log_dir_candidates(None, Some(Path::new(r"C:\game")), "MyGame");
        assert_eq!(c, vec![PathBuf::from(r"C:\game\logs")]);
    }

    #[test]
    fn candidates_empty_when_nothing_known() {
        assert!(log_dir_candidates(None, None, "MyGame").is_empty());
    }

    #[test]
    fn file_name_follows_convention() {
        let ts = Timestamp { year: 2026, month: 9, day: 10, hour: 14, minute: 30, second: 59 };
        assert_eq!(log_file_name(&ts), "seed_20260910_143059.log");
        assert!(is_log_file_name(&log_file_name(&ts)));
    }

    #[test]
    fn is_log_file_name_rejects_foreign_files() {
        assert!(!is_log_file_name("readme.txt"));
        assert!(!is_log_file_name("seed_20260910_143059.txt"));
        assert!(!is_log_file_name("editor_20260910_143059.log"));
        // 接頭辞と拡張子だけ（日時が空）は対象外
        assert!(!is_log_file_name("seed_.log"));
    }

    #[test]
    fn logs_to_delete_keeps_newest() {
        let names: Vec<String> = vec![
            "seed_20260101_000000.log",
            "seed_20260103_000000.log",
            "seed_20260102_000000.log",
        ]
        .into_iter()
        .map(String::from)
        .collect();

        // 2 件残す → 最も古い 1 件だけ消える
        let del = logs_to_delete(&names, 2);
        assert_eq!(del, vec!["seed_20260101_000000.log".to_string()]);
    }

    #[test]
    fn logs_to_delete_returns_empty_when_under_limit() {
        let names: Vec<String> = vec!["seed_20260101_000000.log".to_string()];
        assert!(logs_to_delete(&names, MAX_KEPT_LOG_FILES).is_empty());
        assert!(logs_to_delete(&[], MAX_KEPT_LOG_FILES).is_empty());
    }

    #[test]
    fn logs_to_delete_with_zero_keep_deletes_everything() {
        let names: Vec<String> = vec![
            "seed_20260101_000000.log".to_string(),
            "seed_20260102_000000.log".to_string(),
        ];
        assert_eq!(logs_to_delete(&names, 0).len(), 2);
    }

    #[test]
    fn logs_to_delete_exactly_at_limit_keeps_all() {
        // 上限ちょうど（10 件）のときは 1 件も消さない
        let names: Vec<String> = (0..MAX_KEPT_LOG_FILES)
            .map(|i| format!("seed_2026010{}_000000.log", i))
            .collect();
        assert!(logs_to_delete(&names, MAX_KEPT_LOG_FILES).is_empty());
        // 1 件増えると最古の 1 件が対象になる
        let mut plus = names.clone();
        plus.push("seed_20251231_000000.log".to_string());
        assert_eq!(
            logs_to_delete(&plus, MAX_KEPT_LOG_FILES),
            vec!["seed_20251231_000000.log".to_string()]
        );
    }
}
