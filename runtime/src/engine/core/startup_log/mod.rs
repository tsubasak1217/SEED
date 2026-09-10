// ============================================================
//  startup_log — 配布パッケージ版の起動ログと panic 通知
//
//  【解決する問題】
//  リリースビルドは `windows_subsystem = "windows"` でコンソールを持たない。
//  ランタイムのログは全て `eprintln!`（C# 側は `Console.Error.WriteLine`）なので、
//  配布物では**すべて捨てられる**。GPU アダプタが取れない・`assets.pak` が壊れている
//  といった起動失敗も、ユーザーからは「ダブルクリックしても何も起きない」としか見えず、
//  原因を問い合わせる手掛かりが一切残らなかった。
//
//  【この機構がすること（パッケージ実行時のみ）】
//  1. 標準出力／標準エラーを `{exe のあるフォルダ}\logs\seed_YYYYMMDD_HHMMSS.log` へ差し替える
//     （`redirect`。Rust の `eprintln!` も、後から起動する CLR の `Console.Error` も同じ 1 本に入る。
//      exe の隣に書けない場合だけ `%LOCALAPPDATA%\{exe名}\logs` へ退避する）
//  2. ログ先頭へ環境情報を 1 回だけ記録する（`env_report`）
//  3. panic をログへ残し、ダイアログで案内する（`panic_hook`）
//
//  エディタ起動（`--assets-root=` / `--pipe=` 付き）と開発ビルドの単体起動では
//  **何も変えない**。標準エラーは従来どおりエディタの Output パネル／コンソールへ流れる
//  （panic フックだけは設置するが、ダイアログは出さずログ出力のみ）。
//
//  【ファイル分割】
//  | ファイル        | 責務 |
//  |-----------------|------|
//  | `launch_kind`   | パッケージ実行かどうかの判定（純関数＋実環境版） |
//  | `exe_info`      | 実行ファイルのパス・置き場・アプリ名 |
//  | `timestamp`     | ローカル日時の取得と書式化 |
//  | `log_path`      | ログの置き場・ファイル名・世代管理 |
//  | `redirect`      | Win32 の標準ハンドル差し替え |
//  | `env_report`    | 起動時の環境情報の収集と整形 |
//  | `panic_hook`    | panic の記録とダイアログ通知 |
//  | `wide`          | Win32 W 系 API 用の UTF-16 変換 |
// ============================================================

/// 起動時の環境情報（実行ファイル・OS・同梱物）の収集と整形。
pub mod env_report;
/// 実行ファイル自身の情報（パス・置き場・アプリ名）。
pub mod exe_info;
/// 起動形態（配布パッケージ実行か、エディタ／開発ビルド実行か）の判定。
pub mod launch_kind;
/// ログの置き場・ファイル名・世代管理。
pub mod log_path;
/// panic の記録とユーザーへの通知。
pub mod panic_hook;
/// 標準出力／標準エラーのログファイルへの差し替え。
pub mod redirect;
/// 起動ログ用のローカル日時。
pub mod timestamp;
/// Win32 W 系 API へ渡す UTF-16 文字列の生成。
pub mod wide;

use std::path::PathBuf;

pub use launch_kind::LaunchKind;

/// 起動ログ機構の初期化結果。
///
/// 呼び出し側（`main`）が「今どういう起動で、ログはどこに出ているか」を
/// 後段へ伝えられるように返すが、現状は `main` 内で完結する。
#[derive(Debug, Clone)]
pub struct StartupLog {
    /// 判定された起動形態。
    pub kind: LaunchKind,
    /// リダイレクト先のログファイル。エディタ実行・リダイレクト失敗時は `None`。
    pub log_path: Option<PathBuf>,
}

/// 起動ログ機構を初期化する。
///
/// # 呼び出し位置
/// `main()` の **最初の 1 行**。ウィンドウもレンダラーも作る前に呼ぶこと。
/// ここより後に出るログを 1 行も落とさないため、また初期化中の panic
/// （GPU 初期化失敗など、最も起きやすい失敗）を捕まえるため。
///
/// # 動作
/// * パッケージ実行 … ログフォルダを用意 → 古いログを整理 → 標準ハンドルを差し替え
///                    → 環境情報を記録 → panic フック（ダイアログ有り）を設置
/// * それ以外 ……… 標準ハンドルには触れず、panic フック（ダイアログ無し）だけ設置
///
/// # 失敗時の方針
/// ログが用意できない場合でも**必ずゲームは起動する**。
/// 失敗理由は標準エラーへ 1 行だけ出して続行する（コンソールが無ければ捨てられるが、
/// それはログが無い状態そのものなので追加の害は無い）。
pub fn init() -> StartupLog {
    let exe_path = exe_info::exe_path();
    let exe_dir = exe_path.as_ref().and_then(|p| p.parent().map(|d| d.to_path_buf()));
    let app_name = exe_info::app_name_from_exe_path(exe_path.as_deref());

    let args: Vec<String> = std::env::args().collect();
    let kind = launch_kind::detect_launch_kind(&args, exe_dir.as_deref());

    // エディタ／開発ビルド実行では標準ハンドルに触れない（Output パネルへの出力を壊さない）。
    let log_path = match kind {
        LaunchKind::Packaged => setup_log_file(&app_name, exe_dir.as_deref()),
        LaunchKind::Editor => None,
    };

    // panic フックは**どちらの起動でも**設置する（エディタ実行でもログには残したい）。
    // ダイアログはパッケージ実行のときだけ。
    panic_hook::install(
        log_path.clone(),
        app_name,
        kind == LaunchKind::Packaged,
    );

    // 環境情報はログファイルへ書けたときだけ記録する。
    // エディタ実行で出すと Output パネルに毎回 12 行増えるだけなので出さない。
    if log_path.is_some() {
        let env = env_report::collect(
            kind,
            exe_path,
            exe_dir.as_deref(),
            log_path.clone(),
            timestamp::now_local(),
        );
        env_report::write_report(&env);
    }

    StartupLog { kind, log_path }
}

/// ログファイルを用意して標準ハンドルを差し替える。
///
/// 候補フォルダ（exe 隣の `logs` → `%LOCALAPPDATA%\{アプリ名}\logs`）を順に試し、
/// **実際にログファイルを開けた**ところで確定する。
/// 「フォルダは作れたがファイルは作れない」（読み取り専用の配布メディア、
/// Program Files 配下の権限など）でも次の候補へ落ちられるように、
/// フォルダ作成だけでなくファイルオープンまでを 1 回の試行として扱う。
///
/// どこにも作れなければ `None`（ログ無しでゲームは起動する）。
fn setup_log_file(app_name: &str, exe_dir: Option<&std::path::Path>) -> Option<PathBuf> {
    // 日時が取れない環境ではファイル名を決められないので諦める
    // （Windows 以外＝この機構が動かない環境のみ該当）。
    let started_at = timestamp::now_local()?;
    let file_name = log_path::log_file_name(&started_at);

    let local_app_data = std::env::var_os(log_path::LOCAL_APP_DATA_ENV).map(PathBuf::from);
    let candidates = log_path::log_dir_candidates(local_app_data.as_deref(), exe_dir, app_name);

    // 最後に起きた失敗理由（全候補が失敗したときだけ報告する）
    let mut last_error: Option<String> = None;

    for dir in &candidates {
        if let Err(e) = log_path::ensure_log_dir(dir) {
            last_error = Some(format!("フォルダを作成できません（{}）: {e}", dir.display()));
            continue;
        }
        let path = dir.join(&file_name);
        match redirect::redirect_std_handles_to_file(&path) {
            Ok(()) => {
                // 差し替え成功後に世代管理を行う。
                // 新しく作ったファイルは名前が最新なので削除対象には入らない。
                log_path::prune_old_logs(dir, log_path::MAX_KEPT_LOG_FILES, &file_name);
                return Some(path);
            }
            Err(e) => last_error = Some(e),
        }
    }

    // ここへ来た時点では標準ハンドルは元のまま（＝コンソールがあればそちらへ出る）。
    if let Some(e) = last_error {
        eprintln!("[SEED LOG] 起動ログを作成できませんでした: {e}");
    }
    None
}
