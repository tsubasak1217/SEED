// ============================================================
//  redraw_bridge.rs — スクリプトの描画の要求 API（SEED.Redraw）の FFI（W2-10a）
//
//  【役割】
//  C# の `SEED.Redraw`（scripting/src/Api/Redraw.cs）が呼ぶ FFI 関数 1 本（op で分ける）。
//  中身（要求の状態・起こし方）はエンジンの engine/core/redraw/script_requests.rs が持ち、ここは
//  「数値の受け渡しの約束」だけを受け持つ（正典は docs/redraw_policy.md §4）。
//
//  【ffi_redraw(op, value) → i32】（op の番号は C# 側 ScriptHost.RedrawOp* と一致させる）
//    op 0 = Request           … 次の 1 フレームを描く（value は使わない）。0
//    op 1 = RequestAfter      … value 秒後に 1 フレームを描く。0（value が NaN・負なら -1 で何もしない）
//    op 2 = KeepAlive         … value 秒の間は描き続ける。0（value が NaN・負なら -1 で何もしない）
//    op 3 = SetContinuous     … value が 0 でなければ常に描く・0 なら外す。0
//    op 4 = IsContinuous      … 常に描く要求の中なら 1、でなければ 0
//    op 5 = SetPolicy         … value を方針の番号（0 = continuous・1 = on_demand）として上書き、-1 なら上書きを外す。
//                               0（知らない番号なら -1 で何もしない）
//    op 6 = GetPolicy         … 今の方針の番号（上書き → プロジェクト設定の順）
//    それ以外                 … -1
//  どのスレッドから呼んでもよい（状態は Mutex と原子変数。Request は止めていれば起こす）。
// ============================================================

use crate::engine::core::redraw::policy::RenderPolicy;
use crate::engine::core::redraw::script_requests;

// ── op（C# 側 ScriptHost.RedrawOp* と一致させる）──
/// 次の 1 フレームを描く。
pub const REDRAW_OP_REQUEST: i32 = 0;
/// value 秒後に 1 フレームを描く。
pub const REDRAW_OP_REQUEST_AFTER: i32 = 1;
/// value 秒の間は描き続ける。
pub const REDRAW_OP_KEEP_ALIVE: i32 = 2;
/// 常に描く（value が 0 以外）・外す（0）。
pub const REDRAW_OP_SET_CONTINUOUS: i32 = 3;
/// 常に描く要求の中か（1 / 0）。
pub const REDRAW_OP_IS_CONTINUOUS: i32 = 4;
/// 方針の上書き（value = 方針の番号・-1 で外す）。
pub const REDRAW_OP_SET_POLICY: i32 = 5;
/// 今の方針の番号。
pub const REDRAW_OP_GET_POLICY: i32 = 6;

/// 受け付けた（C# 側 ScriptHost.RedrawResultOk と一致させる）。
pub const REDRAW_RESULT_OK: i32 = 0;
/// 知らない op・読めない値（C# 側 ScriptHost.RedrawResultInvalid と一致させる）。
pub const REDRAW_RESULT_INVALID: i32 = -1;
/// SetPolicy の「上書きを外す」の番号（C# 側 ScriptHost.RedrawPolicyClearOverride と一致させる）。
pub const REDRAW_POLICY_CLEAR_OVERRIDE: i32 = -1;

/// 秒の値が使えるか（NaN・負は使えない。+∞ は上限の長さとして受け付ける）。
fn is_usable_seconds(value: f32) -> bool {
    !value.is_nan() && value >= 0.0
}

/// bool を FFI の 1 / 0 にする。
fn flag(value: bool) -> i32 {
    i32::from(value)
}

/// スクリプトの描画の要求（SEED.Redraw の入口）。
///
/// # 引数
/// - `op`    … `REDRAW_OP_*`
/// - `value` … 秒・旗・方針の番号（op による）
///
/// # 戻り値
/// op ごとの値（ファイル冒頭の表）。知らない op・読めない値は -1。
pub(super) extern "system" fn ffi_redraw(op: i32, value: f32) -> i32 {
    match op {
        REDRAW_OP_REQUEST => {
            script_requests::request();
            REDRAW_RESULT_OK
        }
        REDRAW_OP_REQUEST_AFTER if is_usable_seconds(value) => {
            script_requests::request_after(f64::from(value));
            REDRAW_RESULT_OK
        }
        REDRAW_OP_KEEP_ALIVE if is_usable_seconds(value) => {
            script_requests::keep_alive(f64::from(value));
            REDRAW_RESULT_OK
        }
        REDRAW_OP_SET_CONTINUOUS => {
            script_requests::set_continuous(value != 0.0);
            REDRAW_RESULT_OK
        }
        REDRAW_OP_IS_CONTINUOUS => flag(script_requests::continuous()),
        REDRAW_OP_SET_POLICY => set_policy(value),
        REDRAW_OP_GET_POLICY => script_requests::effective_policy().to_code(),
        _ => REDRAW_RESULT_INVALID,
    }
}

/// SetPolicy の中身（番号を読んで上書きする・外す）。
fn set_policy(value: f32) -> i32 {
    // 番号は小さな整数（-1・0・1）。整数でない値は読めない値として断る
    if value.fract() != 0.0 || !value.is_finite() {
        return REDRAW_RESULT_INVALID;
    }
    let code = value as i32;
    if code == REDRAW_POLICY_CLEAR_OVERRIDE {
        script_requests::set_policy_override(None);
        return REDRAW_RESULT_OK;
    }
    match RenderPolicy::from_code(code) {
        Some(policy) => {
            script_requests::set_policy_override(Some(policy));
            REDRAW_RESULT_OK
        }
        None => REDRAW_RESULT_INVALID,
    }
}

// ============================================================
//  テスト（値の検査と知らない op だけ。状態を変える op はプロセスで 1 つの状態を触るので
//  engine/core/redraw/script_requests.rs の純粋な値の試験で確かめる）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 知らない op・読めない秒・読めない方針の番号は -1 で、何もしない。
    #[test]
    fn invalid_ops_and_values_are_rejected() {
        assert_eq!(ffi_redraw(99, 0.0), REDRAW_RESULT_INVALID);
        assert_eq!(ffi_redraw(-1, 0.0), REDRAW_RESULT_INVALID);
        assert_eq!(ffi_redraw(REDRAW_OP_REQUEST_AFTER, f32::NAN), REDRAW_RESULT_INVALID);
        assert_eq!(ffi_redraw(REDRAW_OP_KEEP_ALIVE, -1.0), REDRAW_RESULT_INVALID);
        assert_eq!(ffi_redraw(REDRAW_OP_SET_POLICY, 2.0), REDRAW_RESULT_INVALID);
        assert_eq!(ffi_redraw(REDRAW_OP_SET_POLICY, 0.5), REDRAW_RESULT_INVALID);
        assert_eq!(ffi_redraw(REDRAW_OP_SET_POLICY, f32::INFINITY), REDRAW_RESULT_INVALID);
    }

    /// 秒の値の検査（0 と +∞ は受け付ける）。
    #[test]
    fn seconds_validation() {
        assert!(is_usable_seconds(0.0));
        assert!(is_usable_seconds(1.5));
        assert!(is_usable_seconds(f32::INFINITY));
        assert!(!is_usable_seconds(-0.001));
        assert!(!is_usable_seconds(f32::NAN));
    }
}
