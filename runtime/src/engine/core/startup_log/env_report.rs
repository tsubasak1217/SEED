// ============================================================
//  env_report.rs — 起動時の環境情報をログ先頭へ 1 回だけ記録する
//
//  【役割】
//  「動かない」という報告を受け取ったとき、ログの先頭だけ見れば
//  ビルド・実行場所・配布フォルダの中身・OS が分かる状態にする。
//
//  【同梱物の見せ方：名前決め打ちをしない】
//  以前の設計では `dotnet/` や `SEEDScripting.dll` といった特定の名前の有無を
//  個別に判定していたが、配布フォルダの構成（DLL をまとめるフォルダ名など）は
//  今後変わる。判定を名前に固定すると、構成変更のたびにここを直す必要があり、
//  しかも直し忘れると「無し」と誤報する。
//  そこで **exe フォルダ直下と、その 1 階層下のサブフォルダの一覧**（サイズ付き）を
//  そのまま出す方式にした。何が置かれているかは読む人が判断できる。
//
//  【設計方針】
//  ・環境を集める部分（`collect`／不純）と、行に組み立てる部分（`format_report_lines`／純関数）
//    を分ける。書式は単体テストで固定し、項目の抜けを検出できるようにする。
//  ・一覧は必ず上限で打ち切る（`caches` のように何千件も入るフォルダがあるため）。
// ============================================================

use std::path::{Path, PathBuf};

use super::launch_kind::LaunchKind;
use super::timestamp::Timestamp;

// ── ログ行の接頭辞 ──────────────────────────────────────────
/// 環境情報の行につける接頭辞（他のランタイムログと混ざっても拾えるように）。
const LOG_TAG: &str = "[SEED ENV]";

// ── フォルダ一覧の上限 ─────────────────────────────────────
/// 1 つのフォルダについて一覧に載せるエントリ数の上限。
/// 超えた分は「他 N 件を省略」の 1 行にまとめる。
const MAX_ENTRIES_PER_DIR: usize = 40;
/// 一覧全体（exe フォルダ＋その 1 階層下）に載せるエントリ数の上限。
/// 起動ログが実行ファイル一覧で埋まるのを防ぐ最終的な歯止め。
const MAX_TOTAL_ENTRIES: usize = 300;
/// サブフォルダを辿る深さ（1 = exe フォルダ直下のサブフォルダの中身まで）。
const SUBDIR_SCAN_DEPTH: usize = 1;

/// バイト数を MiB 表記へ換算するための除数。
const BYTES_PER_MIB: f64 = 1024.0 * 1024.0;

/// ビルド種別の表示名（デバッグビルド）。
const BUILD_PROFILE_DEBUG: &str = "debug";
/// ビルド種別の表示名（リリースビルド）。
const BUILD_PROFILE_RELEASE: &str = "release";

/// OS のバージョン（`RtlGetVersion` の生値）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct OsVersion {
    /// メジャーバージョン（Windows 10 / 11 はいずれも 10）
    pub major: u32,
    /// マイナーバージョン
    pub minor: u32,
    /// ビルド番号（Windows 11 は 22000 以上）
    pub build: u32,
}

/// フォルダ一覧の 1 エントリの種別。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum EntryKind {
    /// ファイル（サイズ付き）
    File {
        /// ファイルサイズ（バイト）
        size_bytes: u64,
    },
    /// フォルダ（サイズは測らない — 走査コストが起動時間に乗るため）
    Dir,
    /// 上限で打ち切った残り件数を表す擬似エントリ
    Truncated {
        /// 省略した件数
        remaining: usize,
    },
}

/// exe フォルダ配下の 1 エントリ。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DirEntryInfo {
    /// exe フォルダからの相対表記（例: `assets.pak`、`bin/SEEDScripting.dll`）
    pub relative: String,
    /// 種別（ファイル／フォルダ／打ち切り）
    pub kind: EntryKind,
}

/// 起動時の環境スナップショット（ログへ書くための素材）。
#[derive(Debug, Clone)]
pub struct StartupEnvironment {
    /// エンジンのバージョン（Cargo.toml の version）
    pub engine_version: &'static str,
    /// ビルド種別（debug / release）
    pub build_profile: &'static str,
    /// 起動形態（パッケージ実行かどうか）
    pub launch_kind: LaunchKind,
    /// 起動時刻（ローカル）
    pub started_at: Option<Timestamp>,
    /// 実行ファイルのフルパス
    pub exe_path: Option<PathBuf>,
    /// 実行ファイルの更新日時（ビルド日時の目安）
    pub exe_modified: Option<Timestamp>,
    /// 起動引数（実行ファイルパスを含む生の argv）
    pub args: Vec<String>,
    /// カレントディレクトリ
    pub current_dir: Option<PathBuf>,
    /// OS バージョン
    pub os_version: Option<OsVersion>,
    /// exe フォルダ（一覧の基準）
    pub exe_dir: Option<PathBuf>,
    /// exe フォルダ配下の一覧（直下＋1 階層下）
    pub dir_entries: Vec<DirEntryInfo>,
    /// このログ自身のパス（リダイレクトが効いているときのみ）
    pub log_path: Option<PathBuf>,
}

/// 環境情報を実環境から集める。
///
/// ファイル系の失敗はすべて「不明（None）／空」に落とす。ここで起動を止めない。
pub fn collect(
    launch_kind: LaunchKind,
    exe_path: Option<PathBuf>,
    exe_dir: Option<&Path>,
    log_path: Option<PathBuf>,
    started_at: Option<Timestamp>,
) -> StartupEnvironment {
    StartupEnvironment {
        engine_version: env!("CARGO_PKG_VERSION"),
        build_profile: if cfg!(debug_assertions) {
            BUILD_PROFILE_DEBUG
        } else {
            BUILD_PROFILE_RELEASE
        },
        launch_kind,
        started_at,
        exe_modified: exe_path
            .as_deref()
            .and_then(super::timestamp::file_modified_local),
        exe_path,
        args: std::env::args().collect(),
        current_dir: std::env::current_dir().ok(),
        os_version: os_version(),
        dir_entries: collect_dir_entries(exe_dir),
        exe_dir: exe_dir.map(Path::to_path_buf),
        log_path,
    }
}

/// exe フォルダ直下と、その 1 階層下のサブフォルダの中身を一覧する。
///
/// 並び順は「直下のエントリを名前順に並べ、フォルダならその直後にその中身を置く」。
/// 上限（`MAX_ENTRIES_PER_DIR` / `MAX_TOTAL_ENTRIES`）に達した分は
/// `EntryKind::Truncated` の擬似エントリ 1 件にまとめる。
fn collect_dir_entries(exe_dir: Option<&Path>) -> Vec<DirEntryInfo> {
    let Some(root) = exe_dir else {
        return Vec::new();
    };
    let mut out = Vec::new();
    scan_dir(root, "", SUBDIR_SCAN_DEPTH, &mut out);
    out
}

/// 1 フォルダを走査して `out` へ積む（`depth` が残っていればサブフォルダも辿る）。
fn scan_dir(dir: &Path, prefix: &str, depth: usize, out: &mut Vec<DirEntryInfo>) {
    // 読めないフォルダは黙って飛ばす（権限・未接続ドライブなど）
    let Ok(read) = std::fs::read_dir(dir) else {
        return;
    };

    // 名前順に安定させる（起動ごとに並びが変わるとログの差分が取りづらい）
    let mut names: Vec<(String, bool, Option<u64>)> = read
        .flatten()
        .filter_map(|e| {
            let name = e.file_name().into_string().ok()?;
            let meta = e.metadata().ok();
            let is_dir = meta.as_ref().map(|m| m.is_dir()).unwrap_or(false);
            let size = meta.filter(|m| m.is_file()).map(|m| m.len());
            Some((name, is_dir, size))
        })
        .collect();
    names.sort_by(|a, b| a.0.cmp(&b.0));

    // このフォルダで載せる件数の上限（全体上限にも従う）
    let room_left = MAX_TOTAL_ENTRIES.saturating_sub(out.len());
    let limit = MAX_ENTRIES_PER_DIR.min(room_left);
    let total = names.len();

    for (name, is_dir, size) in names.into_iter().take(limit) {
        let relative = if prefix.is_empty() {
            name.clone()
        } else {
            format!("{prefix}/{name}")
        };
        out.push(DirEntryInfo {
            relative: relative.clone(),
            kind: if is_dir {
                EntryKind::Dir
            } else {
                EntryKind::File { size_bytes: size.unwrap_or(0) }
            },
        });
        // フォルダなら 1 階層だけ中身も載せる
        if is_dir && depth > 0 {
            scan_dir(&dir.join(&name), &relative, depth - 1, out);
        }
    }

    // 打ち切った件数を 1 行で記録する（「載っていない＝無い」と誤読させないため）
    if total > limit {
        let label = if prefix.is_empty() { "." } else { prefix };
        out.push(DirEntryInfo {
            relative: label.to_string(),
            kind: EntryKind::Truncated { remaining: total - limit },
        });
    }
}

/// OS のバージョンを取得する。
///
/// `GetVersionExW` はアプリケーションマニフェスト次第で古い値を返す（互換シム）ため、
/// 実際のバージョンを返す `RtlGetVersion`（ntdll）を使う。
#[cfg(windows)]
fn os_version() -> Option<OsVersion> {
    use windows_sys::Wdk::System::SystemServices::RtlGetVersion;
    use windows_sys::Win32::System::SystemInformation::OSVERSIONINFOW;

    /// NTSTATUS の成功値。
    const STATUS_SUCCESS: i32 = 0;

    let mut info: OSVERSIONINFOW = unsafe { core::mem::zeroed() };
    // dwOSVersionInfoSize は呼び出し側が必ず埋める規約。
    info.dwOSVersionInfoSize = core::mem::size_of::<OSVERSIONINFOW>() as u32;
    if unsafe { RtlGetVersion(&mut info) } != STATUS_SUCCESS {
        return None;
    }
    Some(OsVersion {
        major: info.dwMajorVersion,
        minor: info.dwMinorVersion,
        build: info.dwBuildNumber,
    })
}

/// Windows 以外向けのスタブ。
#[cfg(not(windows))]
fn os_version() -> Option<OsVersion> {
    None
}

/// 環境情報をログ行の並びへ組み立てる【純関数】。
///
/// 1 行 1 項目。値が取れなかった項目は `不明` と明記する（項目自体は消さない）。
pub fn format_report_lines(env: &StartupEnvironment) -> Vec<String> {
    /// 値が取れなかったときの表示。
    const UNKNOWN: &str = "不明";

    let mut lines = Vec::new();

    lines.push(format!(
        "{LOG_TAG} ===== SEED runtime 起動 v{} ({}) =====",
        env.engine_version, env.build_profile
    ));
    lines.push(format!(
        "{LOG_TAG} 起動日時: {}",
        env.started_at
            .map(|t| t.format_readable())
            .unwrap_or_else(|| UNKNOWN.to_string())
    ));
    lines.push(format!(
        "{LOG_TAG} 起動形態: {}",
        match env.launch_kind {
            LaunchKind::Packaged => "パッケージ実行（配布物）",
            LaunchKind::Editor => "エディタ／開発ビルド実行",
        }
    ));
    lines.push(format!(
        "{LOG_TAG} 実行ファイル: {}",
        env.exe_path
            .as_ref()
            .map(|p| p.display().to_string())
            .unwrap_or_else(|| UNKNOWN.to_string())
    ));
    lines.push(format!(
        "{LOG_TAG} 実行ファイル更新日時（ビルド日時の目安）: {}",
        env.exe_modified
            .map(|t| t.format_readable())
            .unwrap_or_else(|| UNKNOWN.to_string())
    ));
    lines.push(format!("{LOG_TAG} 起動引数: {}", format_args_list(&env.args)));
    lines.push(format!(
        "{LOG_TAG} カレントディレクトリ: {}",
        env.current_dir
            .as_ref()
            .map(|p| p.display().to_string())
            .unwrap_or_else(|| UNKNOWN.to_string())
    ));
    lines.push(format!(
        "{LOG_TAG} OS: {}",
        env.os_version
            .map(|v| format!("Windows {}.{} (build {})", v.major, v.minor, v.build))
            .unwrap_or_else(|| UNKNOWN.to_string())
    ));
    lines.push(format!(
        "{LOG_TAG} ログファイル: {}",
        env.log_path
            .as_ref()
            .map(|p| p.display().to_string())
            .unwrap_or_else(|| UNKNOWN.to_string())
    ));

    // ── exe フォルダの中身（直下＋1 階層下）─────────────────────
    lines.push(format!(
        "{LOG_TAG} exe フォルダの内容（直下＋1 階層下）: {}",
        env.exe_dir
            .as_ref()
            .map(|p| p.display().to_string())
            .unwrap_or_else(|| UNKNOWN.to_string())
    ));
    for entry in &env.dir_entries {
        lines.push(format!("{LOG_TAG}   {}", format_dir_entry(entry)));
    }

    lines.push(format!("{LOG_TAG} ===== 環境情報ここまで ====="));

    lines
}

/// 起動引数を 1 行に並べる【純関数】。
///
/// 先頭（実行ファイルパス）は「実行ファイル」の行で既に出しているため省き、
/// 残りを空白区切りで並べる。引数が無い場合は `（なし）`。
fn format_args_list(args: &[String]) -> String {
    let rest: Vec<&str> = args.iter().skip(1).map(String::as_str).collect();
    if rest.is_empty() {
        "（なし）".to_string()
    } else {
        rest.join(" ")
    }
}

/// フォルダ一覧の 1 エントリを 1 行へ組み立てる【純関数】。
fn format_dir_entry(entry: &DirEntryInfo) -> String {
    match &entry.kind {
        EntryKind::File { size_bytes } => format!(
            "{}  ({} バイト / {:.1} MiB)",
            entry.relative,
            size_bytes,
            *size_bytes as f64 / BYTES_PER_MIB
        ),
        EntryKind::Dir => format!("{}/  (フォルダ)", entry.relative),
        EntryKind::Truncated { remaining } => {
            format!("{}  … 他 {} 件を省略", entry.relative, remaining)
        }
    }
}

/// 環境情報を標準エラーへ書き出す。
///
/// リダイレクトが成功していればそのままログファイルの先頭に載る。
pub fn write_report(env: &StartupEnvironment) {
    use std::io::Write;
    let mut err = std::io::stderr();
    for line in format_report_lines(env) {
        // ここでの書き込み失敗は無視する（ログが書けないだけでゲームは動く）。
        let _ = writeln!(err, "{line}");
    }
}

// ============================================================
//  単体テスト（純関数のみ）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の最小限の環境スナップショットを作る。
    fn sample_env() -> StartupEnvironment {
        StartupEnvironment {
            engine_version: "0.1.0",
            build_profile: BUILD_PROFILE_RELEASE,
            launch_kind: LaunchKind::Packaged,
            started_at: Some(Timestamp {
                year: 2026,
                month: 9,
                day: 10,
                hour: 14,
                minute: 30,
                second: 59,
            }),
            exe_path: Some(PathBuf::from(r"C:\dist\MyGame\MyGame.exe")),
            exe_modified: Some(Timestamp {
                year: 2026,
                month: 9,
                day: 9,
                hour: 21,
                minute: 0,
                second: 0,
            }),
            args: vec![r"C:\dist\MyGame\MyGame.exe".to_string()],
            current_dir: Some(PathBuf::from(r"C:\Windows\System32")),
            os_version: Some(OsVersion { major: 10, minor: 0, build: 26200 }),
            exe_dir: Some(PathBuf::from(r"C:\dist\MyGame")),
            dir_entries: vec![
                DirEntryInfo {
                    relative: "assets.pak".to_string(),
                    kind: EntryKind::File { size_bytes: 2 * 1024 * 1024 },
                },
                DirEntryInfo { relative: "bin".to_string(), kind: EntryKind::Dir },
                DirEntryInfo {
                    relative: "bin/SEEDScripting.dll".to_string(),
                    kind: EntryKind::File { size_bytes: 167936 },
                },
                DirEntryInfo {
                    relative: "caches".to_string(),
                    kind: EntryKind::Truncated { remaining: 1234 },
                },
            ],
            log_path: Some(PathBuf::from(r"C:\dist\MyGame\logs\seed_20260910_143059.log")),
        }
    }

    #[test]
    fn report_contains_every_required_field() {
        let text = format_report_lines(&sample_env()).join("\n");
        assert!(text.contains("v0.1.0 (release)"), "{text}");
        assert!(text.contains("2026-09-10 14:30:59"), "{text}");
        assert!(text.contains("パッケージ実行"), "{text}");
        assert!(text.contains(r"C:\dist\MyGame\MyGame.exe"), "{text}");
        assert!(text.contains("2026-09-09 21:00:00"), "{text}");
        assert!(text.contains(r"C:\Windows\System32"), "{text}");
        assert!(text.contains("Windows 10.0 (build 26200)"), "{text}");
        assert!(text.contains(r"logs\seed_20260910_143059.log"), "{text}");
    }

    #[test]
    fn dir_listing_shows_files_folders_and_truncation() {
        let text = format_report_lines(&sample_env()).join("\n");
        assert!(text.contains("assets.pak  (2097152 バイト / 2.0 MiB)"), "{text}");
        assert!(text.contains("bin/  (フォルダ)"), "{text}");
        // 1 階層下は相対表記で並ぶ（DLL の置き場所が変わっても名前決め打ちにならない）
        assert!(text.contains("bin/SEEDScripting.dll  (167936 バイト"), "{text}");
        assert!(text.contains("caches  … 他 1234 件を省略"), "{text}");
    }

    #[test]
    fn args_line_is_none_marker_when_only_exe_path() {
        assert_eq!(format_args_list(&[r"C:\dist\MyGame.exe".to_string()]), "（なし）");
        assert_eq!(format_args_list(&[]), "（なし）");
    }

    #[test]
    fn args_line_lists_arguments_without_exe_path() {
        let args = vec![
            r"C:\dist\MyGame.exe".to_string(),
            "--mode=play".to_string(),
            "--play-collider-draw=1".to_string(),
        ];
        assert_eq!(format_args_list(&args), "--mode=play --play-collider-draw=1");
    }

    #[test]
    fn unknown_values_are_marked_not_omitted() {
        let mut env = sample_env();
        env.started_at = None;
        env.exe_path = None;
        env.exe_modified = None;
        env.current_dir = None;
        env.os_version = None;
        env.log_path = None;
        env.exe_dir = None;
        env.dir_entries = Vec::new();
        let lines = format_report_lines(&env);
        let text = lines.join("\n");
        // 見出し 2 行（開始・終了）+ 項目 9 行。一覧が空でも項目は消えない。
        assert_eq!(lines.len(), 2 + 9, "{text}");
        // 起動日時 / 実行ファイル / 更新日時 / カレントディレクトリ / OS / ログファイル / exe フォルダ = 7 件
        assert_eq!(text.matches("不明").count(), 7, "{text}");
    }

    #[test]
    fn dir_scan_lists_one_level_of_subdirectories() {
        // 実フォルダを作って走査結果を検証する（`scan_dir` はファイル系だが、
        // 「1 階層だけ辿る」「名前順」という契約はここでしか固定できない）
        let base = std::env::temp_dir().join(format!(
            "seed_startup_log_scan_test_{}",
            std::process::id()
        ));
        let _ = std::fs::remove_dir_all(&base);
        std::fs::create_dir_all(base.join("bin").join("deep")).unwrap();
        std::fs::write(base.join("assets.pak"), b"12345").unwrap();
        std::fs::write(base.join("bin").join("a.dll"), b"12").unwrap();
        std::fs::write(base.join("bin").join("deep").join("hidden.txt"), b"x").unwrap();

        let entries = collect_dir_entries(Some(&base));
        let names: Vec<&str> = entries.iter().map(|e| e.relative.as_str()).collect();
        assert_eq!(names, vec!["assets.pak", "bin", "bin/a.dll", "bin/deep"]);
        // 2 階層下（bin/deep/hidden.txt）は載らない
        assert!(!names.iter().any(|n| n.contains("hidden.txt")));
        // サイズが載る
        assert_eq!(entries[0].kind, EntryKind::File { size_bytes: 5 });
        assert_eq!(entries[1].kind, EntryKind::Dir);

        let _ = std::fs::remove_dir_all(&base);
    }

    #[test]
    fn dir_scan_returns_empty_when_exe_dir_unknown() {
        assert!(collect_dir_entries(None).is_empty());
    }
}
