// ============================================================
//  pipe.rs — 名前付きパイプの通信路（PC のエディタ。--pipe=<名前>）
//
//  ipc.rs の IpcClient::connect から移した（振る舞いは従来どおり）:
//    - エディタのパイプサーバーへ、最大 20 回・100 ms 間隔で接続を試みる（エディタの準備待ち）
//    - 読み取り用と書き込み用に同じハンドルを複製して、別々のスレッドで使う
//    - 読み取りは PeekNamedPipe で「読めるデータがある」ことを確かめてから ReadFile する（下の PipeReader）
//
//  【PeekNamedPipe で待つ理由（従来の read_loop のコメントから）】
//  try_clone() した複製ハンドルでブロッキングの ReadFile を使うと、同じファイルオブジェクトへの同期 I/O が
//  カーネルで直列化され、書き込みスレッドの WriteFile が待たされる。データがあるときだけ ReadFile すれば止まらない。
//  従来は行の読み始めだけで確かめていたが、読み取りの 1 回ごと（行の途中の ReadFile も含む）に確かめるようにした。
//  パイプが切れて PeekNamedPipe が失敗したら読み取りの失敗として返す（従来は 1 ms ごとに確かめ続けていた）。
// ============================================================

use std::fs::{File, OpenOptions};
use std::io::{self, Read};
use std::thread;
use std::time::Duration;

/// パイプ接続を試みる最大リトライ回数。
const PIPE_CONNECT_RETRIES: u32 = 20;

/// リトライ間隔 (ミリ秒)。エディタ起動後のパイプ準備待ち時間に相当する。
const PIPE_CONNECT_RETRY_MS: u64 = 100;

/// 名前付きパイプのパスの接頭辞（`\\.\pipe\<名前>`）。
const PIPE_PATH_PREFIX: &str = r"\\.\pipe\";

/// 読めるデータが無いときに次に確かめるまでの間隔（ミリ秒。従来の read_loop と同じ）。
#[cfg(windows)]
const PEEK_POLL_INTERVAL_MS: u64 = 1;

/// エディタの名前付きパイプへつなぎ、読み取り口と書き込み口を返す。
///
/// # 引数
/// * `pipe_name` - パイプ名（`\\.\pipe\<名前>` の `<名前>`）
///
/// # 戻り値
/// (読み取り口, 書き込み口)。つながらなければエラー。
pub(crate) fn open(pipe_name: &str) -> io::Result<(PipeReader, File)> {
    let path = format!("{PIPE_PATH_PREFIX}{pipe_name}");
    let file = try_open(&path)?;
    let writer = file.try_clone()?;
    Ok((PipeReader { file }, writer))
}

/// パイプを開く（エディタがパイプを用意するまで、決まった回数だけ待ってやり直す）。
fn try_open(path: &str) -> io::Result<File> {
    for _ in 0..PIPE_CONNECT_RETRIES {
        match OpenOptions::new().read(true).write(true).open(path) {
            Ok(f) => return Ok(f),
            Err(_) => thread::sleep(Duration::from_millis(PIPE_CONNECT_RETRY_MS)),
        }
    }
    OpenOptions::new().read(true).write(true).open(path)
}

/// 名前付きパイプの読み取り口（読む前に PeekNamedPipe でデータがあるのを待つ）。
pub(crate) struct PipeReader {
    /// パイプのハンドル（書き込み口とは複製した別ハンドル）。
    file: File,
}

impl Read for PipeReader {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        // PeekNamedPipe は Windows 専用。エディタとの名前付きパイプは Windows でしか使わない
        // （非 Windows ではコンパイルを通すためだけに、事前の確認を省いてそのまま読む）。
        #[cfg(windows)]
        wait_until_readable(&self.file)?;
        self.file.read(buf)
    }
}

/// 読めるデータが来るまで待つ（パイプが切れていればエラー）。
#[cfg(windows)]
fn wait_until_readable(file: &File) -> io::Result<()> {
    use std::os::windows::io::AsRawHandle;
    loop {
        match peek_available(file.as_raw_handle())? {
            0 => thread::sleep(Duration::from_millis(PEEK_POLL_INTERVAL_MS)),
            _ => return Ok(()),
        }
    }
}

/// パイプに溜まっている読めるバイト数（PeekNamedPipe。読み取り位置は動かさない）。
#[cfg(windows)]
fn peek_available(handle: std::os::windows::raw::HANDLE) -> io::Result<u32> {
    let mut available: u32 = 0;
    // SAFETY: handle は生きている File のハンドル。バッファは渡さず（null・長さ 0）、読めるバイト数だけを受け取る。
    let ok = unsafe {
        windows_sys::Win32::System::Pipes::PeekNamedPipe(
            handle as _,
            std::ptr::null_mut(),
            0,
            std::ptr::null_mut(),
            &mut available,
            std::ptr::null_mut(),
        )
    };
    if ok == 0 {
        // 相手が閉じた（ERROR_BROKEN_PIPE）等。読み取りの失敗として read_loop を終わらせる
        return Err(io::Error::last_os_error());
    }
    Ok(available)
}
