// ============================================================
//  logcat/liblog.rs — Android の liblog へ直接書く最小ラッパー
//
//  log クレートを経由しない出力（標準出力の転送・panic・グルー自身のログ）はここを通す。
//  log の最大レベル設定やロガーの初期化状態に左右されず、呼んだその場で同期的に書かれるため、
//  panic 直後にプロセスが終わるような場面でも取りこぼさない。
// ============================================================

use std::ffi::{CStr, CString, c_char, c_int};

/// logcat のタグ（`adb logcat -s SEED` で絞り込める）。android_logger にも同じ値を渡す。
pub const TAG_TEXT: &str = "SEED";

/// liblog へ渡す NUL 終端のタグ（TAG_TEXT と同じ文字列）。
const TAG: &CStr = c"SEED";

/// liblog が 1 件で受け付ける本文の上限の目安（これを超えると切り詰められるので分割して書く）。
/// liblog の実際の上限（LOGGER_ENTRY_MAX_PAYLOAD ≒ 4068 バイト）からタグ等の余白を引いた値。
const MAX_MESSAGE_BYTES: usize = 4000;

/// logcat の優先度。値は android/log.h の android_LogPriority と同じ（ABI の一部）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Priority {
    /// ANDROID_LOG_INFO
    Info = 4,
    /// ANDROID_LOG_WARN
    Warn = 5,
    /// ANDROID_LOG_ERROR
    Error = 6,
}

#[link(name = "log")]
unsafe extern "C" {
    /// android/log.h の __android_log_write。戻り値（書いたバイト数・負値はエラー）は使わない。
    fn __android_log_write(prio: c_int, tag: *const c_char, text: *const c_char) -> c_int;
}

/// 1 件のテキストを logcat へ書く。長いテキストは上限ごとに分割して複数件にする。
pub fn write(priority: Priority, text: &str) {
    for chunk in split_utf8_chunks(text, MAX_MESSAGE_BYTES) {
        // NUL を含むと C 文字列にできないので置換文字へ置き換える（ログとしては十分）。
        let Ok(message) = CString::new(chunk.replace('\0', "\u{FFFD}")) else {
            continue;
        };
        // SAFETY: tag・message はどちらも NUL 終端済みの有効な C 文字列で、呼び出し中は生存する。
        unsafe {
            __android_log_write(priority as c_int, TAG.as_ptr(), message.as_ptr());
        }
    }
}

/// UTF-8 文字列を、文字の途中で切らずに最大 `max_bytes` バイトずつへ分割する【純関数】。
///
/// 空文字列は 1 件の空文字列を返す（空行もそのまま 1 件として出すため）。
fn split_utf8_chunks(text: &str, max_bytes: usize) -> Vec<&str> {
    let mut chunks = Vec::new();
    let mut rest = text;
    while rest.len() > max_bytes {
        // max_bytes 以下で最も後ろの文字境界で切る（1 文字が max_bytes を超えることは無い）。
        let mut cut = max_bytes;
        while !rest.is_char_boundary(cut) {
            cut -= 1;
        }
        let (head, tail) = rest.split_at(cut);
        chunks.push(head);
        rest = tail;
    }
    chunks.push(rest);
    chunks
}
