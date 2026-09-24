// ============================================================
//  logcat/stdio_redirect.rs — 標準出力・標準エラーを logcat へ転送する
//
//  【仕組み】（Android ネイティブアプリの定番）
//    1. pipe を 1 本作る
//    2. 標準出力（fd 1）と標準エラー（fd 2）を dup2 で pipe の書き込み側へ付け替える
//    3. 専用スレッドが読み取り側を 1 行ずつ読み、liblog へ INFO で書く
//  エンジンは起動ログ・警告を eprintln! で出しているため、これが無いと logcat に何も出ない。
//
//  標準出力と標準エラーは同じ pipe に合流するので区別はしない（どちらも INFO で出す）。
//  エラーは内容側に [ERROR] 等の印が付いているのでそちらで判別する。
// ============================================================

use std::fs::File;
use std::io::{BufRead, BufReader, ErrorKind};
use std::os::fd::FromRawFd;

use super::liblog::{self, Priority};

/// 転送スレッドの名前（logcat の tid 表示・デバッガでの識別用）。
const STDIO_THREAD_NAME: &str = "seed-stdio-logcat";

/// pipe の fd 配列の要素数（読み取り側・書き込み側）。
const PIPE_FD_COUNT: usize = 2;

/// 標準出力・標準エラーを pipe へ付け替え、転送スレッドを起動する。
///
/// 失敗したときは付け替えを行わずにエラーを返す（元の出力先＝/dev/null のまま）。
pub fn redirect() -> std::io::Result<()> {
    let mut fds = [0 as libc::c_int; PIPE_FD_COUNT];
    // SAFETY: fds は pipe が要求する 2 要素の書き込み可能な配列。
    if unsafe { libc::pipe(fds.as_mut_ptr()) } != 0 {
        return Err(std::io::Error::last_os_error());
    }
    let [read_fd, write_fd] = fds;

    for target_fd in [libc::STDOUT_FILENO, libc::STDERR_FILENO] {
        // SAFETY: write_fd は直前の pipe が返した有効な fd、target_fd は標準の fd 番号。
        if unsafe { libc::dup2(write_fd, target_fd) } < 0 {
            let err = std::io::Error::last_os_error();
            // SAFETY: どちらもこの関数が作った fd で、ほかに所有者はいない。
            unsafe {
                libc::close(read_fd);
                libc::close(write_fd);
            }
            return Err(err);
        }
    }
    // 付け替え後は fd 1 / 2 が pipe を指し続けるので、元の書き込み側 fd は閉じてよい。
    // SAFETY: write_fd はこの関数が作った fd で、ほかに所有者はいない。
    unsafe {
        libc::close(write_fd);
    }

    // SAFETY: read_fd は pipe が返した有効な fd で、以後はこの File だけが所有する。
    let reader = unsafe { File::from_raw_fd(read_fd) };
    std::thread::Builder::new()
        .name(STDIO_THREAD_NAME.to_string())
        .spawn(move || pump_lines(reader))?;
    Ok(())
}

/// pipe の読み取り側を 1 行ずつ読み、logcat へ書き続ける（転送スレッドの本体）。
fn pump_lines(reader: File) {
    let mut reader = BufReader::new(reader);
    let mut line = Vec::new();
    loop {
        line.clear();
        match reader.read_until(b'\n', &mut line) {
            // 書き込み側がすべて閉じた（プロセス終了間際）。
            Ok(0) => break,
            Ok(_) => {
                let text = String::from_utf8_lossy(&line);
                let text = text.trim_end_matches(['\n', '\r']);
                // 空行は logcat では意味を持たないので出さない。
                if !text.is_empty() {
                    liblog::write(Priority::Info, text);
                }
            }
            Err(err) if err.kind() == ErrorKind::Interrupted => continue,
            Err(err) => {
                liblog::write(Priority::Warn, &format!("標準出力の転送を終了します: {err}"));
                break;
            }
        }
    }
}
