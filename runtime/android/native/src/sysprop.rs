// ============================================================
//  sysprop.rs — Android システムプロパティの読み取り
//
//  端末情報（ro.product.model 等）と、adb から切り替えられるデバッグ用フラグ
//  （debug.seed.* 。`adb shell setprop` で書ける）を読むための薄いラッパー。
// ============================================================

use std::ffi::{CStr, CString};

/// プロパティを読み、値の文字列を返す。未定義・空・名前が不正なときは None。
pub fn get(name: &str) -> Option<String> {
    let name = CString::new(name).ok()?;
    // 値の最大長は PROP_VALUE_MAX（終端 NUL を含む）。bionic がこの長さまでしか書かないことを保証する。
    let mut value = [0 as libc::c_char; libc::PROP_VALUE_MAX as usize];
    // SAFETY: name は NUL 終端済み、value は PROP_VALUE_MAX バイトの書き込み可能なバッファ。
    let len = unsafe { libc::__system_property_get(name.as_ptr(), value.as_mut_ptr()) };
    if len <= 0 {
        return None;
    }
    // SAFETY: __system_property_get は value を NUL 終端する。
    let text = unsafe { CStr::from_ptr(value.as_ptr()) };
    Some(text.to_string_lossy().into_owned())
}
