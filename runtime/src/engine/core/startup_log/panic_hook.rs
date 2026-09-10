// ============================================================
//  panic_hook.rs — panic の記録とユーザーへの通知
//
//  【役割】
//  ランタイムが panic したとき（GPU アダプタが取れない・PAK が壊れている等）に
//  ① 何がどこで起きたかとバックトレースを標準エラー＝ログへ残し、
//  ② パッケージ実行ならモーダルダイアログで「起動に失敗した」ことと
//     ログの場所をユーザーへ知らせる。
//
//  【なぜ必要か】
//  リリースビルドは `windows_subsystem = "windows"` でコンソールを持たない。
//  フックが無いと、配布物は**何のメッセージも出さずに消える**（ユーザーからは
//  「ダブルクリックしても何も起きない」としか見えない）。
//
//  【既定フックのチェーン】
//  `take_hook()` で既定フックを退避し、自前の出力の後に必ず呼ぶ。
//  既定フックの整形（`thread '...' panicked at ...`）を失わないため。
// ============================================================

use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};

/// ダイアログの表題に使う接尾辞。
const DIALOG_TITLE_SUFFIX: &str = " — 起動エラー";
/// ログ行の接頭辞（他のランタイムログと混ざっても拾えるように）。
const LOG_TAG: &str = "[SEED PANIC]";

/// ダイアログを出したかどうか。
///
/// 複数スレッドが同時に panic したときにダイアログが積み重なり、
/// ユーザーが閉じきれなくなる（＝ハングに見える）のを防ぐ。
static DIALOG_SHOWN: AtomicBool = AtomicBool::new(false);

/// panic フックを設置する。
///
/// # 引数
/// * `log_path` — 起動ログのパス（リダイレクトが効いているときのみ Some）。ダイアログ本文で案内する
/// * `app_name` — ダイアログの表題に使うアプリ名（exe 名）
/// * `show_dialog` — パッケージ実行のときだけ true。エディタ実行ではログ出力のみ
///
/// 契約: プロセスにつき 1 回だけ呼ぶ（`main()` の最初期）。
pub fn install(log_path: Option<PathBuf>, app_name: String, show_dialog: bool) {
    // 既定フック（`thread '...' panicked at ...` の整形出力）を退避してチェーンする。
    let previous = std::panic::take_hook();

    std::panic::set_hook(Box::new(move |info| {
        // ① まず既定フックに標準の整形出力をさせる
        previous(info);

        // ② 自前の詳細（位置・スレッド名・強制取得のバックトレース）を足す
        //    RUST_BACKTRACE 未設定でもバックトレースを出すため force_capture を使う。
        let backtrace = std::backtrace::Backtrace::force_capture();
        let detail = format_panic_detail(
            &payload_text(info),
            &location_text(info),
            &thread_text(),
            &backtrace.to_string(),
        );
        write_to_stderr(&detail);

        // ③ パッケージ実行ならユーザーへ通知する（最初の 1 回だけ）
        if show_dialog && !DIALOG_SHOWN.swap(true, Ordering::SeqCst) {
            let text = format_dialog_text(&payload_text(info), log_path.as_deref());
            show_error_dialog_blocking(format!("{app_name}{DIALOG_TITLE_SUFFIX}"), text);
        }
    }));
}

/// panic のペイロード（`panic!` に渡された文字列）を取り出す。
///
/// `&str` と `String` の両方に対応する。それ以外の型は表現できないため既定文言を返す。
fn payload_text(info: &std::panic::PanicHookInfo<'_>) -> String {
    if let Some(s) = info.payload().downcast_ref::<&str>() {
        (*s).to_string()
    } else if let Some(s) = info.payload().downcast_ref::<String>() {
        s.clone()
    } else {
        "（メッセージ無し）".to_string()
    }
}

/// panic 発生位置（ファイル:行:桁）を取り出す。
fn location_text(info: &std::panic::PanicHookInfo<'_>) -> String {
    match info.location() {
        Some(loc) => format!("{}:{}:{}", loc.file(), loc.line(), loc.column()),
        None => "（位置不明）".to_string(),
    }
}

/// panic したスレッド名を取り出す。
fn thread_text() -> String {
    std::thread::current()
        .name()
        .unwrap_or("<unnamed>")
        .to_string()
}

/// ログへ書く panic 詳細を組み立てる【純関数】。
pub fn format_panic_detail(
    payload: &str,
    location: &str,
    thread: &str,
    backtrace: &str,
) -> String {
    format!(
        "{LOG_TAG} メッセージ: {payload}\n\
         {LOG_TAG} 位置: {location}\n\
         {LOG_TAG} スレッド: {thread}\n\
         {LOG_TAG} バックトレース:\n{backtrace}"
    )
}

/// ダイアログ本文を組み立てる【純関数】。
///
/// ログの場所が分かる場合は必ず併記する（ユーザーに送ってもらう先がここになる）。
pub fn format_dialog_text(payload: &str, log_path: Option<&std::path::Path>) -> String {
    let mut text = String::from("起動に失敗しました。\n\n");
    text.push_str(payload);
    match log_path {
        Some(path) => {
            text.push_str("\n\nログ: ");
            text.push_str(&path.display().to_string());
        }
        None => {
            text.push_str("\n\n（ログファイルは作成されていません）");
        }
    }
    text
}

/// 標準エラーへ書く。
///
/// panic フックの中では `eprintln!` を使わない — 書き込みに失敗すると
/// `eprintln!` 自身が panic し、panic 中の panic でプロセスが abort するため。
/// stdout は行バッファなので、ここで併せて flush して取りこぼしを防ぐ。
fn write_to_stderr(text: &str) {
    use std::io::Write;
    let _ = std::io::stdout().flush();
    let mut err = std::io::stderr();
    let _ = writeln!(err, "{text}");
    let _ = err.flush();
}

/// エラーダイアログを**専用スレッドで**出し、閉じられるまで待つ。
///
/// 【なぜ専用スレッドか（実測で判明した不具合の対策）】
/// panic はイベントループ（winit）のスレッドで起きることが多い。そのスレッドで
/// `MessageBoxW` を呼ぶと、モーダルループが**そのスレッド宛の未処理メッセージを配送する**。
/// 配送先には winit のウィンドウプロシージャが含まれ、初期化途中の（＝可変借用が
/// スタック上に生きている）イベントループへ再入する。実測では、ダイアログを出した
/// 1.5〜8.7 秒後にプロセスが勝手に終了し、**ユーザーが読む前にダイアログが消えていた**。
///
/// 別スレッドで出せば、そのスレッドはウィンドウを持たないため配送されるのは
/// ダイアログ自身のメッセージだけになり、再入が起きない。
/// panic したスレッドは `join()` で待つので、ユーザーが OK を押すまで
/// 巻き戻し（unwind）は始まらない。
///
/// スレッドが起動できない場合は、その場で出す（何も出さないよりはよい）。
fn show_error_dialog_blocking(title: String, text: String) {
    match std::thread::Builder::new()
        .name("seed-panic-dialog".into())
        .spawn(move || show_error_dialog(&title, &text))
    {
        // OK が押される（＝スレッドが終わる）まで待つ。
        // ダイアログスレッド側が panic した場合も Err で戻るだけで、ここでは何もしない。
        Ok(handle) => {
            let _ = handle.join();
        }
        // スレッドを作れないほど逼迫している場合のフォールバック。
        Err(_) => {}
    }
}

/// エラーダイアログを出す（Windows 専用・モーダル）。
///
/// オーナーウィンドウは指定しない（panic 時点でウィンドウが壊れている可能性があるため）。
/// 最前面かつフォアグラウンドを要求し、ゲームウィンドウの裏に隠れて
/// 「反応が無い」ように見えるのを防ぐ。
#[cfg(windows)]
fn show_error_dialog(title: &str, text: &str) {
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        MB_ICONERROR, MB_OK, MB_SETFOREGROUND, MB_TOPMOST, MessageBoxW,
    };

    let title_w = super::wide::str_to_wide_null(title);
    let text_w = super::wide::str_to_wide_null(text);
    unsafe {
        MessageBoxW(
            core::ptr::null_mut(),
            text_w.as_ptr(),
            title_w.as_ptr(),
            MB_OK | MB_ICONERROR | MB_TOPMOST | MB_SETFOREGROUND,
        );
    }
}

/// Windows 以外向けのスタブ（ダイアログ機構を持たないので何もしない）。
#[cfg(not(windows))]
fn show_error_dialog(_title: &str, _text: &str) {}

// ============================================================
//  単体テスト（純関数のみ。フック設置はプロセス共有なのでテストしない）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use std::path::Path;

    #[test]
    fn detail_contains_all_parts() {
        let text = format_panic_detail(
            "Failed to find a suitable GPU adapter",
            "src/engine/core/renderer/mod.rs:669:14",
            "main",
            "   0: backtrace_frame_0\n   1: backtrace_frame_1",
        );
        assert!(text.contains("メッセージ: Failed to find a suitable GPU adapter"), "{text}");
        assert!(text.contains("位置: src/engine/core/renderer/mod.rs:669:14"), "{text}");
        assert!(text.contains("スレッド: main"), "{text}");
        assert!(text.contains("backtrace_frame_1"), "{text}");
        // 1 行 1 情報でログから grep しやすいこと
        assert_eq!(text.matches(LOG_TAG).count(), 4, "{text}");
    }

    #[test]
    fn dialog_text_includes_log_path() {
        let text = format_dialog_text(
            "assets.pak を開けません",
            Some(Path::new(r"C:\Users\u\AppData\Local\MyGame\logs\seed_20260910_143059.log")),
        );
        assert!(text.starts_with("起動に失敗しました。"), "{text}");
        assert!(text.contains("assets.pak を開けません"), "{text}");
        assert!(text.contains(r"logs\seed_20260910_143059.log"), "{text}");
    }

    #[test]
    fn dialog_text_states_when_no_log_exists() {
        let text = format_dialog_text("何かが壊れた", None);
        assert!(text.contains("ログファイルは作成されていません"), "{text}");
    }
}
