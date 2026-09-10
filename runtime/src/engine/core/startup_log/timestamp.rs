// ============================================================
//  timestamp.rs — 起動ログ用のローカル日時
//
//  【役割】
//  ログファイル名（`seed_YYYYMMDD_HHMMSS.log`）とログ本文の見出しに使う
//  「人が読む日時」だけを扱う。日時ライブラリ（chrono 等）は依存に無いため、
//  Windows API（`GetLocalTime` / `FileTimeToLocalFileTime` + `FileTimeToSystemTime`）
//  から直接 SYSTEMTIME を貰い、書式化だけを純関数として切り出す。
//
//  【設計方針】
//  ・OS から値を取る部分（不純）と、書式化する部分（純関数）を必ず分ける。
//    書式は単体テストで固定する（ゼロ埋め・桁数の崩れはログの並び順を壊すため）。
//  ・この機構は Windows 専用（起動ログのリダイレクト自体が Win32 前提）なので、
//    Windows 以外では `now_local()` が None を返し、呼び出し側が機能ごと諦める。
// ============================================================

/// 年月日・時分秒だけを持つ日時（ローカルタイム）。
///
/// タイムゾーン情報もミリ秒も持たない。ログの見出しとファイル名にしか使わないため、
/// 「人が読めて、辞書順に並べるとおおむね時系列になる」ことだけを保証する。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Timestamp {
    /// 西暦年（例: 2026）
    pub year: u16,
    /// 月（1〜12）
    pub month: u8,
    /// 日（1〜31）
    pub day: u8,
    /// 時（0〜23）
    pub hour: u8,
    /// 分（0〜59）
    pub minute: u8,
    /// 秒（0〜59）
    pub second: u8,
}

impl Timestamp {
    /// ファイル名に埋め込む形式（`YYYYMMDD_HHMMSS`）へ書式化する【純関数】。
    ///
    /// ゼロ埋め固定長にしてあるので、この文字列を含むファイル名は
    /// **辞書順ソート＝時系列ソート**になる（古いログの選別がこの性質に依存する）。
    pub fn format_compact(&self) -> String {
        format!(
            "{:04}{:02}{:02}_{:02}{:02}{:02}",
            self.year, self.month, self.day, self.hour, self.minute, self.second
        )
    }

    /// ログ本文に書く読みやすい形式（`YYYY-MM-DD HH:MM:SS`）へ書式化する【純関数】。
    pub fn format_readable(&self) -> String {
        format!(
            "{:04}-{:02}-{:02} {:02}:{:02}:{:02}",
            self.year, self.month, self.day, self.hour, self.minute, self.second
        )
    }
}

/// 現在のローカル日時を取得する。
///
/// Windows では `GetLocalTime`（OS のタイムゾーン設定を反映した現地時刻）を使う。
/// Windows 以外では起動ログ機構そのものが無効なので `None` を返す。
pub fn now_local() -> Option<Timestamp> {
    #[cfg(windows)]
    {
        use windows_sys::Win32::Foundation::SYSTEMTIME;
        use windows_sys::Win32::System::SystemInformation::GetLocalTime;

        // SYSTEMTIME は全フィールドが u16 の POD。GetLocalTime が必ず全て埋めるため
        // ゼロ初期化してから渡す（失敗を返さない API なので戻り値の検査は無い）。
        let mut st: SYSTEMTIME = unsafe { core::mem::zeroed() };
        unsafe { GetLocalTime(&mut st) };
        Some(from_systemtime(&st))
    }
    #[cfg(not(windows))]
    {
        None
    }
}

/// ファイルの最終更新日時をローカル日時で取得する。
///
/// 実行ファイルの更新日時を「ビルド日時の目安」としてログへ出すために使う
/// （ビルド時刻を埋め込む build.rs は持たないため、これが最も近い代替になる）。
///
/// 取得できない場合（ファイルが無い・API 失敗）は `None`。
#[cfg(windows)]
pub fn file_modified_local(path: &std::path::Path) -> Option<Timestamp> {
    use windows_sys::Win32::Foundation::{FILETIME, SYSTEMTIME};
    use windows_sys::Win32::Storage::FileSystem::{
        FileTimeToLocalFileTime, GetFileAttributesExW, GetFileExInfoStandard,
        WIN32_FILE_ATTRIBUTE_DATA,
    };
    use windows_sys::Win32::System::Time::FileTimeToSystemTime;

    /// Win32 の BOOL の成功値（0 が失敗）。
    const WIN32_TRUE: i32 = 1;

    let wide = super::wide::to_wide_null(path.as_os_str());

    // ファイル属性（更新日時を含む）を 1 回の API 呼び出しで取得する。
    let mut data: WIN32_FILE_ATTRIBUTE_DATA = unsafe { core::mem::zeroed() };
    let ok = unsafe {
        GetFileAttributesExW(
            wide.as_ptr(),
            GetFileExInfoStandard,
            (&mut data as *mut WIN32_FILE_ATTRIBUTE_DATA).cast(),
        )
    };
    if ok != WIN32_TRUE {
        return None;
    }

    // UTC の FILETIME → ローカルの FILETIME → SYSTEMTIME の順に変換する。
    let mut local: FILETIME = unsafe { core::mem::zeroed() };
    if unsafe { FileTimeToLocalFileTime(&data.ftLastWriteTime, &mut local) } != WIN32_TRUE {
        return None;
    }
    let mut st: SYSTEMTIME = unsafe { core::mem::zeroed() };
    if unsafe { FileTimeToSystemTime(&local, &mut st) } != WIN32_TRUE {
        return None;
    }
    Some(from_systemtime(&st))
}

/// Windows 以外向けのスタブ（起動ログ機構が無効なので常に `None`）。
#[cfg(not(windows))]
pub fn file_modified_local(_path: &std::path::Path) -> Option<Timestamp> {
    None
}

/// Win32 の `SYSTEMTIME` を `Timestamp` へ写す。
///
/// SYSTEMTIME の各フィールドは u16 だが、月日時分秒はいずれも u8 に収まる範囲
/// （OS が保証する）なので、そのまま切り詰める。
#[cfg(windows)]
fn from_systemtime(st: &windows_sys::Win32::Foundation::SYSTEMTIME) -> Timestamp {
    Timestamp {
        year: st.wYear,
        month: st.wMonth as u8,
        day: st.wDay as u8,
        hour: st.wHour as u8,
        minute: st.wMinute as u8,
        second: st.wSecond as u8,
    }
}

// ============================================================
//  単体テスト（純関数の書式のみ。OS 依存部分はテストしない）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用に日時を組み立てる補助。
    fn ts(year: u16, month: u8, day: u8, hour: u8, minute: u8, second: u8) -> Timestamp {
        Timestamp { year, month, day, hour, minute, second }
    }

    #[test]
    fn compact_format_is_zero_padded_fixed_width() {
        // 1 桁の月日時分秒がすべてゼロ埋めされること（辞書順＝時系列の前提）
        assert_eq!(ts(2026, 1, 2, 3, 4, 5).format_compact(), "20260102_030405");
        assert_eq!(ts(2026, 12, 31, 23, 59, 59).format_compact(), "20261231_235959");
    }

    #[test]
    fn readable_format_uses_hyphen_and_colon() {
        assert_eq!(
            ts(2026, 9, 10, 7, 8, 9).format_readable(),
            "2026-09-10 07:08:09"
        );
    }

    #[test]
    fn compact_format_sorts_chronologically_as_text() {
        // 「辞書順ソート＝時系列ソート」が成り立つこと（古いログ選別がこれに依存する）
        let mut names = vec![
            ts(2026, 9, 10, 10, 0, 0).format_compact(),
            ts(2026, 9, 10, 9, 59, 59).format_compact(),
            ts(2026, 9, 9, 23, 0, 0).format_compact(),
            ts(2027, 1, 1, 0, 0, 0).format_compact(),
        ];
        names.sort();
        assert_eq!(
            names,
            vec![
                "20260909_230000".to_string(),
                "20260910_095959".to_string(),
                "20260910_100000".to_string(),
                "20270101_000000".to_string(),
            ]
        );
    }
}
