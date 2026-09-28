// ============================================================
//  platform/bridge/desktop_sim/os_ui_mode.rs — PC の OS の明暗の設定を読む（W2-9。模擬の app.ui_mode の源）
//
//  Windows: 設定の「個人用設定 → 色 → 既定のアプリ モード」＝レジストリ
//    HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize の DWORD AppsUseLightTheme
//    （0 = ダーク・1 = ライト。値が無い古い Windows はライト扱いが OS の既定だが、ここでは取れない＝Unknown にする）。
//  ほかの OS（macOS・Linux のデスクトップ）は今は読まない（Unknown ＝ テーマの明暗のまま）。
//  変化は winit の WindowEvent::ThemeChanged（単体起動の最上位のウィンドウだけに届く）で知る（render.rs → bridge::notify_host_ui_mode_changed）。
// ============================================================

use super::ui_mode_state::SimNight;

/// OS の明暗の設定を読む（読めなければ Unknown）。
#[cfg(windows)]
pub fn read_os_night() -> SimNight {
    use windows_sys::Win32::System::Registry::{RegGetValueW, HKEY_CURRENT_USER, RRF_RT_REG_DWORD};

    /// 設定のキー（NUL 終わりの UTF-16 にして渡す）。
    const PERSONALIZE_KEY: &str = "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";
    /// 値の名前。
    const APPS_USE_LIGHT_THEME: &str = "AppsUseLightTheme";
    /// 成功（ERROR_SUCCESS）。
    const ERROR_SUCCESS: u32 = 0;
    /// ダークの値。
    const DARK_VALUE: u32 = 0;

    let key: Vec<u16> = PERSONALIZE_KEY.encode_utf16().chain(std::iter::once(0)).collect();
    let value: Vec<u16> = APPS_USE_LIGHT_THEME.encode_utf16().chain(std::iter::once(0)).collect();
    let mut data: u32 = 0;
    let mut size = std::mem::size_of::<u32>() as u32;
    // SAFETY: key・value は NUL 終わりの UTF-16、data は DWORD 1 つ分・size はその大きさ。型は RRF_RT_REG_DWORD で DWORD に限る。
    let status = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            key.as_ptr(),
            value.as_ptr(),
            RRF_RT_REG_DWORD,
            std::ptr::null_mut(),
            (&mut data as *mut u32).cast(),
            &mut size,
        )
    };
    if status != ERROR_SUCCESS {
        return SimNight::Unknown;
    }
    if data == DARK_VALUE { SimNight::Yes } else { SimNight::No }
}

/// OS の明暗の設定を読む（Windows 以外は読まない）。
#[cfg(not(windows))]
pub fn read_os_night() -> SimNight {
    SimNight::Unknown
}
