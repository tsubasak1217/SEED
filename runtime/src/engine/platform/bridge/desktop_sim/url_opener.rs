// ============================================================
//  platform/bridge/desktop_sim/url_opener.rs — 模擬の app.open_url が URL を PC の既定のアプリへ渡す係（W1-6）
//
//  【何を開くか】URL の規則（bridge::app::check_open_url。Android と同じ）を通った URL のうち、DESKTOP_OPENABLE_SCHEMES
//  （http・https・mailto）だけを PC で開く。独自の scheme・tel などは Android でだけ開く（PC では判定だけ）。
//  Windows のドライブ文字（C:\…）は規則では 1 文字の scheme として通るが、ここで開かないのでファイルは実行されない。
//  【開き方】シェルを通さない（cmd /c start は & % ^ などを解釈するので URL の中身でコマンドが走りうる。open クレート 5.4.4 も
//  cmd /c start を insecure の機能の裏へ下げている〈~/.cargo/registry の実ソースを読んだ〉）:
//    Windows … 専用のスレッドで COM を初期化（アパートメント・OLE1 DDE なし。ShellExecute の説明が求める形）し、
//              ShellExecuteW(open) に URL を 1 つの文字列として渡す（戻り値が 32 を超えれば成功）
//    macOS   … open <URL>（引数 1 つ。std::process::Command はシェルを通さない）
//    その他の Unix … xdg-open <URL>
//  どれも呼び出し元（スクリプトのフレーム）を待たせない（専用のスレッドで開き、結果はログだけ）。URL は scheme が英字で始まるので
//  「-」で始まらず、コマンドのオプションに取り違えられない。
//  【開かない】単体テスト（cfg(test)）と、環境変数 SEED_PLATFORM_SIM_NO_OPEN=1 のときは DryRunUrlOpener（判定とログだけ）。
// ============================================================

use std::fmt::Debug;
use std::sync::Arc;

use crate::engine::platform::bridge::LOG_PREFIX;

/// PC で実際に開く scheme（小文字。ブラウザ・メール）。
pub const DESKTOP_OPENABLE_SCHEMES: [&str; 3] = ["http", "https", "mailto"];

/// 外部のアプリを開かない（判定だけにする）ための環境変数（確かめ・自動の試験で PC にブラウザを出さない）。
pub const NO_OPEN_ENV: &str = "SEED_PLATFORM_SIM_NO_OPEN";

/// NO_OPEN_ENV を有効とみなす値。
const NO_OPEN_ENV_ON: &str = "1";

/// URL を開く専用のスレッドの名前。
const OPEN_THREAD_NAME: &str = "SEEDOpenUrl";

/// URL を PC の既定のアプリへ渡す係。
pub trait UrlOpener: Send + Sync + Debug {
    /// URL を渡す（呼び出し元を待たせない。開けたかどうかはログ）。
    ///
    /// # 戻り値
    /// 渡したら true。渡さなかった（判定だけの係・スレッドを作れなかった）ら false
    fn open(&self, url: &str) -> bool;
}

/// 本物: OS の既定のアプリで開く。
#[derive(Debug, Default)]
pub struct SystemUrlOpener;

impl UrlOpener for SystemUrlOpener {
    fn open(&self, url: &str) -> bool {
        let owned = url.to_string();
        let spawned = std::thread::Builder::new().name(OPEN_THREAD_NAME.to_string()).spawn(move || match open_with_os(&owned) {
            Ok(()) => eprintln!("{LOG_PREFIX} 模擬: PC の既定のアプリで開きました: {owned}"),
            Err(err) => eprintln!("{LOG_PREFIX} 模擬: PC で開けませんでした: {owned}（{err}）"),
        });
        if let Err(err) = &spawned {
            eprintln!("{LOG_PREFIX} 模擬: URL を開くスレッドを作れませんでした（{err}）");
        }
        spawned.is_ok()
    }
}

/// 判定だけ: 開かずにログを出す（単体テスト・SEED_PLATFORM_SIM_NO_OPEN=1）。
#[derive(Debug, Default)]
pub struct DryRunUrlOpener;

impl UrlOpener for DryRunUrlOpener {
    fn open(&self, url: &str) -> bool {
        eprintln!("{LOG_PREFIX} 模擬: 開きません（判定だけ。{NO_OPEN_ENV}=1 か単体テスト）: {url}");
        false
    }
}

/// 模擬が使う係（単体テストと SEED_PLATFORM_SIM_NO_OPEN=1 は判定だけ、それ以外は本物）。
pub fn default_url_opener() -> Arc<dyn UrlOpener> {
    if cfg!(test) {
        return Arc::new(DryRunUrlOpener);
    }
    if no_open_requested() {
        eprintln!("{LOG_PREFIX} {NO_OPEN_ENV}=1: app.open_url は判定だけにし、PC の既定のアプリを開きません");
        return Arc::new(DryRunUrlOpener);
    }
    Arc::new(SystemUrlOpener)
}

/// 環境変数で「開かない」が指定されているか。
fn no_open_requested() -> bool {
    std::env::var(NO_OPEN_ENV).is_ok_and(|value| value.trim() == NO_OPEN_ENV_ON)
}

/// Windows: ShellExecuteW の動詞（既定のアプリで開く）。
#[cfg(target_os = "windows")]
const SHELL_OPEN_VERB: &str = "open";

/// Windows: ShellExecuteW の戻り値がこれより大きければ成功（32 以下はエラーの番号。ShellExecuteW の説明）。
#[cfg(target_os = "windows")]
const SHELL_EXECUTE_MAX_ERROR: isize = 32;

/// Windows: 文字列を ShellExecuteW へ渡す UTF-16 の 0 終わりにする。
#[cfg(target_os = "windows")]
fn to_wide(text: &str) -> Vec<u16> {
    const NUL: u16 = 0;
    text.encode_utf16().chain(std::iter::once(NUL)).collect()
}

/// Windows: この（専用の）スレッドで COM を初期化し、ShellExecuteW で URL を既定のアプリへ渡す。
#[cfg(target_os = "windows")]
fn open_with_os(url: &str) -> Result<(), String> {
    use windows_sys::Win32::System::Com::{CoInitializeEx, CoUninitialize, COINIT_APARTMENTTHREADED, COINIT_DISABLE_OLE1DDE};
    use windows_sys::Win32::UI::Shell::ShellExecuteW;
    use windows_sys::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

    let verb = to_wide(SHELL_OPEN_VERB);
    let target = to_wide(url);
    // SAFETY: 予約の引数は null（約束どおり）。初期化に成功したとき（S_OK / S_FALSE ＝ 0 以上）だけ同じスレッドで CoUninitialize する
    let initialized = unsafe { CoInitializeEx(std::ptr::null(), (COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE) as u32) } >= 0;
    // SAFETY: verb・target は呼び出しの間生きている 0 終わりの UTF-16。窓・引数・作業フォルダは null（既定）
    let result = unsafe {
        ShellExecuteW(std::ptr::null_mut(), verb.as_ptr(), target.as_ptr(), std::ptr::null(), std::ptr::null(), SW_SHOWNORMAL)
    };
    if initialized {
        // SAFETY: 上で初期化に成功した同じスレッド
        unsafe { CoUninitialize() };
    }
    let code = result as isize;
    if code > SHELL_EXECUTE_MAX_ERROR {
        Ok(())
    } else {
        Err(format!("ShellExecuteW の戻り値 {code}"))
    }
}

/// macOS: open コマンドへ URL を 1 つの引数として渡す（シェルを通さない）。
#[cfg(target_os = "macos")]
fn open_with_os(url: &str) -> Result<(), String> {
    run_opener_command("open", url)
}

/// その他の Unix（Linux など。Android は模擬を使わない）: xdg-open へ URL を 1 つの引数として渡す。
#[cfg(all(unix, not(target_os = "macos"), not(target_os = "android")))]
fn open_with_os(url: &str) -> Result<(), String> {
    run_opener_command("xdg-open", url)
}

/// Android など: 模擬は使わない（呼ばれない）。
#[cfg(not(any(target_os = "windows", target_os = "macos", all(unix, not(target_os = "android")))))]
fn open_with_os(_url: &str) -> Result<(), String> {
    Err("この OS では PC の既定のアプリで開く手段がありません".to_string())
}

/// コマンドを 1 つの引数で動かし、終わるのを待つ（専用のスレッドから。子のプロセスを残さない）。
#[cfg(all(unix, not(target_os = "android")))]
fn run_opener_command(program: &str, url: &str) -> Result<(), String> {
    let status = std::process::Command::new(program).arg(url).status().map_err(|err| format!("{program} を起動できません: {err}"))?;
    if status.success() {
        Ok(())
    } else {
        Err(format!("{program} の終了コード {status}"))
    }
}

// ============================================================
//  ユニットテスト（外部のアプリは開かない）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 単体テストでは必ず判定だけの係になる（ブラウザを開かない）。判定だけの係は「渡していない」を返す。
    #[test]
    fn tests_never_use_the_system_opener() {
        let opener = default_url_opener();
        assert!(format!("{opener:?}").contains("DryRun"), "{opener:?}");
        assert!(!opener.open("https://example.com"));
    }

    /// PC で開く scheme は http / https / mailto だけ（独自の scheme・tel・ドライブ文字は開かない）。
    #[test]
    fn desktop_openable_schemes_are_web_and_mail_only() {
        assert_eq!(DESKTOP_OPENABLE_SCHEMES, ["http", "https", "mailto"]);
        for scheme in ["tel", "wakeorpay", "c", "file", "javascript", "ms-settings"] {
            assert!(!DESKTOP_OPENABLE_SCHEMES.contains(&scheme), "{scheme}");
        }
    }
}
