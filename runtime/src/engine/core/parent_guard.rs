// ============================================================
//  parent_guard.rs ��� 親プロセス（エディタ）の死亡監視
//
//  エディタから起動された SEED.exe が、エディタプロセスの終了を
//  検知して自動的に自分自身を終了するための仕組み。
//
//  Windows では OpenProcess + WaitForSingleObject(INFINITE) を使い、
//  親プロセスの終了シグナルを待つバックグラウンドスレッドを起動する。
//  親が終了した瞬間に WaitForSingleObject が返り、std::process::exit(0) を呼ぶ���
//  INFINITE 待機なのでポーリングより効率的（CPU を消費しない）。
//
//  非 Windows では何もしない（エディタは Windows 専用のため実質不要）。
// ============================================================

/// 親プロセスの監視を開始する。
///
/// `parent_pid` が指定されている場合、バックグラウンドスレッドを起動して
/// 親プロセスの終了を監視する。親が終了した際は即座に自プロセスを終了する。
///
/// # 引数
/// * `parent_pid` - 監視対象の親プロセス ID（None なら何もしない）
pub fn watch(parent_pid: Option<u32>) {
    let Some(pid) = parent_pid else { return };

    #[cfg(target_os = "windows")]
    {
        use std::thread;

        thread::Builder::new()
            .name("parent-guard".into())
            .spawn(move || {
                if !wait_for_process_exit(pid) {
                    // プロセスを開けなかった場合: すでに存在しない可能性があるため即終了
                    eprintln!(
                        "[parent_guard] 親プロセス (PID={pid}) を開けませんでした。終了します。"
                    );
                    std::process::exit(0);
                }
                // wait_for_process_exit が true を返した = 待機が完了 = 親が終了した
                eprintln!(
                    "[parent_guard] 親プロセス (PID={pid}) が終了しました。SEED.exe を終了します。"
                );
                std::process::exit(0);
            })
            .expect("parent-guard スレッドの起動に失敗しました");
    }

    // 非 Windows では何もしない
    #[cfg(not(target_os = "windows"))]
    {
        let _ = pid; // 未使用警告を抑制
    }
}

/// 指定 PID のプロセスが終了するまで待機する（Windows 専用）。
///
/// 戻り値:
/// - `true`  : プロセスが見つかり、終了を待機し終えた（親が終了した）
/// - `false` : プロセスが見つからなかった（すでに存在しない）
#[cfg(target_os = "windows")]
fn wait_for_process_exit(pid: u32) -> bool {
    use windows_sys::Win32::Foundation::{CloseHandle, WAIT_OBJECT_0};
    use windows_sys::Win32::System::Threading::{
        INFINITE, OpenProcess, PROCESS_SYNCHRONIZE, WaitForSingleObject,
    };

    // SYNCHRONIZE 権限でプロセスハンドルを取得する。
    // このハンドルを WaitForSingleObject に渡すと、プロセス終了時にシグナルされる。
    let handle = unsafe {
        OpenProcess(PROCESS_SYNCHRONIZE, 0 /* inherit=FALSE */, pid)
    };
    if handle.is_null() {
        // プロセスが存在しないか、権限がない
        return false;
    }

    // プロセスが終了するまで無限に待機する（CPU 使用率ゼロ）
    let wait_result = unsafe { WaitForSingleObject(handle, INFINITE) };
    unsafe { CloseHandle(handle) };

    // WAIT_OBJECT_0 = プロセスがシグナル状態（終了）になった
    wait_result == WAIT_OBJECT_0
}
