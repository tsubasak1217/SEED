// ============================================================
//  wide.rs — Win32 API へ渡す UTF-16 文字列の生成
//
//  【役割】
//  `CreateFileW` / `GetFileAttributesExW` / `MessageBoxW` など、
//  起動ログ機構が使う W 系 API へ渡す「NUL 終端 UTF-16 バッファ」を作るだけの小さな層。
//
//  【なぜ独立したファイルか】
//  同じ変換をリダイレクト・環境情報・panic ダイアログの 3 か所で使うため、
//  各所に `encode_wide().chain(once(0))` を書き散らさないようにまとめている。
// ============================================================

/// Windows の文字列終端に置く NUL 文字（UTF-16 コードユニット）。
const NUL_UTF16: u16 = 0;

/// `OsStr` を NUL 終端の UTF-16 バッファへ変換する（Windows 専用）。
///
/// 戻り値の `Vec<u16>` は呼び出し側が生存させ続ける必要がある
/// （`as_ptr()` だけを API に渡して Vec を落とすとダングリングになる）。
#[cfg(windows)]
pub fn to_wide_null(s: &std::ffi::OsStr) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    s.encode_wide().chain(std::iter::once(NUL_UTF16)).collect()
}

/// `&str` を NUL 終端の UTF-16 バッファへ変換する（Windows 専用）。
///
/// メッセージボックスの本文・タイトルのように、`String` から直接渡す用途で使う。
#[cfg(windows)]
pub fn str_to_wide_null(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(NUL_UTF16)).collect()
}

// ============================================================
//  単体テスト
// ============================================================
#[cfg(all(test, windows))]
mod tests {
    use super::*;

    #[test]
    fn str_conversion_appends_terminator() {
        let w = str_to_wide_null("ab");
        assert_eq!(w, vec![b'a' as u16, b'b' as u16, NUL_UTF16]);
    }

    #[test]
    fn str_conversion_handles_japanese_and_empty() {
        // 空文字でも終端だけは必ず入る（API が読み出す最小要件）
        assert_eq!(str_to_wide_null(""), vec![NUL_UTF16]);
        // 日本語（BMP 内）は 1 コードユニットずつ入る
        let w = str_to_wide_null("あ");
        assert_eq!(w, vec![0x3042, NUL_UTF16]);
    }

    #[test]
    fn os_str_conversion_matches_str_conversion() {
        let path = std::ffi::OsString::from(r"C:\temp\ログ.log");
        assert_eq!(to_wide_null(&path), str_to_wide_null(r"C:\temp\ログ.log"));
    }
}
