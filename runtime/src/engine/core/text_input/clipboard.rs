// ============================================================
//  text_input/clipboard.rs — 入力欄のコピー・切り取り・貼り付けのクリップボード（PC の Ctrl+C/X/V）
//
//  【実装】
//    - Windows: OS のクリップボード（CF_UNICODETEXT。OpenClipboard → GetClipboardData / EmptyClipboard → SetClipboardData）。
//      開けないとき（他のアプリが開いている）は失敗として扱う（読み取りは None・書き込みは false）
//    - それ以外（Android・単体テスト）: プロセスの中だけの控え。Android の貼り付けは IME の commitText の経路で、
//      この控えは使わない（GameActivity には長押しの貼り付けのメニューが無い）
//  入力欄の設定で禁止された欄では、呼び出し側（session.rs）がここを呼ばない。
// ============================================================

/// クリップボードの読み書き（単体テストでは控えの実装を渡す）。
pub trait ClipboardAccess {
    /// 文字列を読む（無い・読めなければ None）。
    fn read_text(&mut self) -> Option<String>;
    /// 文字列を書く（書けたら true）。
    fn write_text(&mut self, text: &str) -> bool;
}

/// プロセスの中だけの控え（Windows 以外・単体テスト）。
#[derive(Debug, Default)]
pub struct MemoryClipboard {
    /// 控えの文字列。
    pub text: Option<String>,
}

impl ClipboardAccess for MemoryClipboard {
    fn read_text(&mut self) -> Option<String> {
        self.text.clone()
    }

    fn write_text(&mut self, text: &str) -> bool {
        self.text = Some(text.to_string());
        true
    }
}

/// OS のクリップボード（Windows）。Windows 以外ではプロセスの中の控えへ落とす。
#[derive(Debug, Default)]
pub struct SystemClipboard {
    /// Windows 以外の控え。
    #[cfg(not(windows))]
    fallback: MemoryClipboard,
}

#[cfg(windows)]
mod windows_impl {
    //! Windows のクリップボードの読み書き（CF_UNICODETEXT）。

    use std::ptr;

    use windows_sys::Win32::Foundation::{GlobalFree, HANDLE};
    use windows_sys::Win32::System::DataExchange::{
        CloseClipboard, EmptyClipboard, GetClipboardData, OpenClipboard, SetClipboardData,
    };
    use windows_sys::Win32::System::Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE};
    use windows_sys::Win32::System::Ole::CF_UNICODETEXT;

    /// UTF-16 の終端（NUL）。
    const NUL: u16 = 0;

    /// UTF-16 の 1 単位のバイト数。
    const UTF16_UNIT_BYTES: usize = 2;

    /// クリップボードを開いている間の見張り（落ちても閉じる）。
    struct OpenGuard;

    impl OpenGuard {
        /// 開く（持ち主の窓なし。開けなければ None）。
        fn open() -> Option<Self> {
            // SAFETY: 持ち主の窓を指定しない（null）で開く。開けたら Drop で必ず閉じる。
            let opened = unsafe { OpenClipboard(ptr::null_mut()) };
            (opened != 0).then_some(Self)
        }
    }

    impl Drop for OpenGuard {
        fn drop(&mut self) {
            // SAFETY: open で開いたクリップボードを閉じる。
            unsafe { CloseClipboard() };
        }
    }

    /// 文字列を読む。
    pub fn read_text() -> Option<String> {
        let _guard = OpenGuard::open()?;
        // SAFETY: 開いている間に CF_UNICODETEXT の手当てを引く（クリップボードが持ち主。解放しない）。
        let handle: HANDLE = unsafe { GetClipboardData(u32::from(CF_UNICODETEXT)) };
        if handle.is_null() {
            return None;
        }
        // SAFETY: 引いた手当てを固定して UTF-16 の NUL 終端の列として読む（読み終えたら固定を外す）。
        unsafe {
            let data = GlobalLock(handle) as *const u16;
            if data.is_null() {
                return None;
            }
            let mut len = 0usize;
            while *data.add(len) != NUL {
                len += 1;
            }
            let text = String::from_utf16_lossy(std::slice::from_raw_parts(data, len));
            GlobalUnlock(handle);
            Some(text)
        }
    }

    /// 文字列を書く。
    pub fn write_text(text: &str) -> bool {
        let Some(_guard) = OpenGuard::open() else { return false };
        let mut units: Vec<u16> = text.encode_utf16().collect();
        units.push(NUL);
        let bytes = units.len() * UTF16_UNIT_BYTES;
        // SAFETY: 動かせるメモリを取り、UTF-16 を写してからクリップボードへ渡す（渡せたら持ち主はクリップボード。
        // 渡せなければ自分で解放する）。
        unsafe {
            if EmptyClipboard() == 0 {
                return false;
            }
            let memory = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if memory.is_null() {
                return false;
            }
            let target = GlobalLock(memory) as *mut u16;
            if target.is_null() {
                GlobalFree(memory);
                return false;
            }
            ptr::copy_nonoverlapping(units.as_ptr(), target, units.len());
            GlobalUnlock(memory);
            if SetClipboardData(u32::from(CF_UNICODETEXT), memory).is_null() {
                GlobalFree(memory);
                return false;
            }
        }
        true
    }
}

impl ClipboardAccess for SystemClipboard {
    fn read_text(&mut self) -> Option<String> {
        #[cfg(windows)]
        {
            windows_impl::read_text()
        }
        #[cfg(not(windows))]
        {
            self.fallback.read_text()
        }
    }

    fn write_text(&mut self, text: &str) -> bool {
        #[cfg(windows)]
        {
            windows_impl::write_text(text)
        }
        #[cfg(not(windows))]
        {
            self.fallback.write_text(text)
        }
    }
}
