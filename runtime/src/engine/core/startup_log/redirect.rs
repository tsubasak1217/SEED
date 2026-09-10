// ============================================================
//  redirect.rs — 標準出力／標準エラーをログファイルへ差し替える
//
//  【役割】
//  パッケージ実行（コンソールを持たない `windows_subsystem = "windows"` ビルド）で、
//  `println!` / `eprintln!` と C# 側の `Console.Error.WriteLine` の行き先を
//  1 本のログファイルに束ねる。
//
//  【方式：SetStdHandle】
//  `CreateFileW` で開いたファイルハンドルをプロセスの標準ハンドル
//  （STD_OUTPUT_HANDLE / STD_ERROR_HANDLE）に差し替える。これで、
//  ・Rust 側 … `std::io::stderr()` は **書き込みのたびに `GetStdHandle` を引く**ため、
//              差し替え以降の `eprintln!` はそのままファイルへ流れる。
//              独自のグローバルシンクやマクロ置換は不要。
//  ・C# 側 …… CLR（hostfxr / System.Console）は初回利用時に `GetStdHandle` を引く。
//              CLR の起動はこの差し替えより後（`App::new` のスクリプトホスト初期化）なので、
//              `Console.Out` / `Console.Error` も同じファイルへ出る。
//  ・プラグイン DLL … 同じプロセスの標準ハンドルを使うため同様にログへ入る。
//
//  【バッファリング】
//  差し替え先は OS のファイルハンドルであり、Rust の stderr は無バッファ（書くたびに
//  WriteFile）。さらに `FILE_FLAG_WRITE_THROUGH` を付けてキャッシュ経由の遅延書き込みを
//  避けているため、panic で異常終了しても直前の行までログに残る。
//  （stdout は Rust 側で行バッファされるので、panic フックで明示的に flush する。）
//
//  【共有モード】
//  読み取りと削除だけ許可し、**書き込み共有は許さない**。
//  同じ秒に 2 つ目のインスタンスが起動しても同名ファイルを奪い合って
//  ログが混線することはなく、2 つ目は「ログ無し」で静かに起動を続ける。
// ============================================================

use std::path::Path;

/// リダイレクトの失敗理由（呼び出し側はログを諦めるだけなので文字列で十分）。
pub type RedirectError = String;

/// 標準出力・標準エラーを `path` のファイルへ差し替える（Windows 専用）。
///
/// 成功した場合、ファイルハンドルは**意図的にリークさせる**。
/// プロセスが終わるまで標準ハンドルとして使い続けるため、閉じる主体が存在しないのが正しい
/// （閉じてしまうと以降の `eprintln!` が無効ハンドルへの書き込みになる）。
#[cfg(windows)]
pub fn redirect_std_handles_to_file(path: &Path) -> Result<(), RedirectError> {
    use windows_sys::Win32::Foundation::{GENERIC_WRITE, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::Storage::FileSystem::{
        CREATE_ALWAYS, CreateFileW, FILE_ATTRIBUTE_NORMAL, FILE_FLAG_WRITE_THROUGH,
        FILE_SHARE_DELETE, FILE_SHARE_READ,
    };
    use windows_sys::Win32::System::Console::{
        STD_ERROR_HANDLE, STD_OUTPUT_HANDLE, SetStdHandle,
    };

    /// Win32 の BOOL の成功値（0 が失敗）。
    const WIN32_TRUE: i32 = 1;

    let wide = super::wide::to_wide_null(path.as_os_str());

    // ログファイルを作る（既存なら切り詰める＝起動ごとに新規ファイルなので実質新規作成）。
    let handle = unsafe {
        CreateFileW(
            wide.as_ptr(),
            GENERIC_WRITE,
            // 実行中でもメモ帳等で開ける（READ）／世代管理で消せる（DELETE）。
            // 書き込み共有は許可しない（多重起動時のログ混線を防ぐ）。
            FILE_SHARE_READ | FILE_SHARE_DELETE,
            core::ptr::null(),
            CREATE_ALWAYS,
            // WRITE_THROUGH: クラッシュしても直前の行までディスクへ届いていることを優先する。
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH,
            core::ptr::null_mut(),
        )
    };
    if handle == INVALID_HANDLE_VALUE {
        let code = unsafe { windows_sys::Win32::Foundation::GetLastError() };
        return Err(format!(
            "CreateFileW に失敗しました（GetLastError={code}）: {}",
            path.display()
        ));
    }

    // 標準出力・標準エラーの両方を同じハンドルへ向ける。
    // 同一ハンドルなのでファイルポインタも共有され、Rust と CLR の出力が
    // 上書きし合わず順番に追記されていく。
    let out_ok = unsafe { SetStdHandle(STD_OUTPUT_HANDLE, handle) };
    let err_ok = unsafe { SetStdHandle(STD_ERROR_HANDLE, handle) };
    if out_ok != WIN32_TRUE || err_ok != WIN32_TRUE {
        let code = unsafe { windows_sys::Win32::Foundation::GetLastError() };
        return Err(format!("SetStdHandle に失敗しました（GetLastError={code}）"));
    }

    // 先頭に UTF-8 BOM を書く。ログ本文には日本語が多く、メモ帳など
    // BOM 無しだと文字化けし得るビューアで開かれる想定のため
    // （このプロジェクトのドキュメントも BOM 付き UTF-8 で統一している）。
    // ここが最初の `eprint!` なので、差し替えが効いているかの実質的な検証も兼ねる。
    eprint!("\u{feff}");

    Ok(())
}

/// Windows 以外向けのスタブ（この機構は Win32 の標準ハンドル差し替えに依存する）。
#[cfg(not(windows))]
pub fn redirect_std_handles_to_file(_path: &Path) -> Result<(), RedirectError> {
    Err("起動ログのリダイレクトは Windows でのみ対応しています".to_string())
}
