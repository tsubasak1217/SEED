// ============================================================
//  platform_utils.rs — Windows プラットフォームユーティリティ
//
//  【含む処理】
//  - camera_grab_start: カメラ操作（中／右）開始時のカーソルロック（ClipCursor）
//  - camera_grab_end:   カーソルを元のスクリーン座標に戻す
//  - apply_window_clamp: Play モード中のカーソルをウィンドウ内に固定
//  - release_window_clamp: ClipCursor を解除する
// ============================================================

// ============================================================
//  カーソルロック / 復元（Windows のみ）
// ============================================================

/// カメラ操作（中ボタン／右ボタン）開始時: カーソルをビューポート内に
/// ClipCursor で閉じ込め、押下前のスクリーン座標を返す。
///
/// ビューポート外にカーソルが出ると親ウィンドウ（WPF）がカーソルを再表示するため、
/// 中ボタンのパン中も同じく閉じ込める（中・右で実装を分けない）。
pub(super) fn camera_grab_start(hwnd: isize) -> Option<(i32, i32)> {
    #[cfg(target_os = "windows")]
    unsafe {
        use windows_sys::Win32::Foundation::{POINT, RECT};
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            ClipCursor, GetCursorPos, GetWindowRect,
        };

        let mut pt = POINT { x: 0, y: 0 };
        if GetCursorPos(&mut pt) == 0 {
            return None;
        }

        let mut rect = RECT {
            left: 0,
            top: 0,
            right: 0,
            bottom: 0,
        };
        GetWindowRect(hwnd as _, &mut rect);
        ClipCursor(&rect);

        return Some((pt.x, pt.y));
    }
    #[cfg(not(target_os = "windows"))]
    let _ = hwnd;
    None
}

// 【MMB 専用ラッパを廃止した理由】
// 以前は mmb_grab_start / mmb_grab_end という RMB 版への単純な別名があり、
// 「どちらのボタンが担当か」をボタンごとに持つ実装を助長していた。
// 中＋右（オービット）の解放順によってはどちらの復元処理も走らず、
// カーソルが非表示のまま残るバグの温床だったため、
// 中・右ともに camera_grab_start / camera_grab_end を直接使う形へ統一した。
// 呼び出し側の入口・出口は App::begin_camera_grab / end_camera_grab の 1 対のみ。

/// カメラ操作終了時（中・右の**両方**が離れたとき）: ClipCursor を解除して
/// カーソルを開始時の座標へ戻す。
pub(super) fn camera_grab_end(x: i32, y: i32) {
    #[cfg(target_os = "windows")]
    unsafe {
        use windows_sys::Win32::UI::WindowsAndMessaging::{ClipCursor, SetCursorPos};
        ClipCursor(core::ptr::null());
        SetCursorPos(x, y);
    }
    #[cfg(not(target_os = "windows"))]
    let _ = (x, y);
}

/// Play クランプ: 毎フレーム呼び出し、ウィンドウ矩形へ ClipCursor を再適用する。
pub(super) fn apply_window_clamp(hwnd: isize) {
    #[cfg(target_os = "windows")]
    unsafe {
        use windows_sys::Win32::Foundation::RECT;
        use windows_sys::Win32::UI::WindowsAndMessaging::{ClipCursor, GetWindowRect};
        let mut rect = RECT {
            left: 0,
            top: 0,
            right: 0,
            bottom: 0,
        };
        GetWindowRect(hwnd as _, &mut rect);
        ClipCursor(&rect);
    }
    #[cfg(not(target_os = "windows"))]
    let _ = hwnd;
}

/// Play クランプ解除。
pub(super) fn release_window_clamp() {
    #[cfg(target_os = "windows")]
    unsafe {
        windows_sys::Win32::UI::WindowsAndMessaging::ClipCursor(core::ptr::null());
    }
}

/// Pause モード用カーソルワープ。
///
/// ウィンドウローカル座標 (lx, ly) へカーソルを移動する。
/// GetWindowRect でウィンドウ左上のスクリーン座標を取得し SetCursorPos で移動する。
/// 非クライアント領域を持たない WS_CHILD ウィンドウ（埋め込み Runtime）向け実装。
pub(super) fn warp_cursor_to_local(hwnd: isize, lx: i32, ly: i32) {
    #[cfg(target_os = "windows")]
    unsafe {
        use windows_sys::Win32::Foundation::RECT;
        use windows_sys::Win32::UI::WindowsAndMessaging::{GetWindowRect, SetCursorPos};
        let mut rect = RECT {
            left: 0,
            top: 0,
            right: 0,
            bottom: 0,
        };
        if GetWindowRect(hwnd as _, &mut rect) != 0 {
            SetCursorPos(rect.left + lx, rect.top + ly);
        }
    }
    #[cfg(not(target_os = "windows"))]
    let _ = (hwnd, lx, ly);
}
