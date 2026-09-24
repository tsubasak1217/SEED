// ============================================================
//  input_bridge.rs — スクリプト入力 API の ID ⇔ winit 型対応表
//
//  C# 側の SEED.KeyCode / SEED.MouseButton enum の数値と
//  winit の KeyCode / MouseButton を相互変換する。
//
//  【重要】この対応表は C# 側 scripting/src/Api/Input.cs の enum 定義と
//  必ず一致させること（値がずれるとスクリプトの入力判定が壊れる）。
// ============================================================

use winit::event::MouseButton;
use winit::keyboard::KeyCode;

use crate::engine::core::input::touch::TouchPoint;

// ─── 入力判定の種別 ──────────────────────────────────────────

/// 押下状態の判定種別（C# 側 FFI 呼び出しの kind 引数と一致させる）。
pub const INPUT_KIND_PRESS: i32 = 0; // 押されている間
pub const INPUT_KIND_TRIGGER: i32 = 1; // 押された瞬間
pub const INPUT_KIND_RELEASE: i32 = 2; // 離された瞬間

/// マウス状態取得の種別（C# 側 FFI 呼び出しの kind 引数と一致させる）。
pub const MOUSE_STATE_POSITION: i32 = 0; // スクリーン座標（2 要素）
pub const MOUSE_STATE_DELTA: i32 = 1; // 相対移動量（2 要素）
pub const MOUSE_STATE_SCROLL: i32 = 2; // ホイール量（1 要素）
/// キャンバス座標（2 要素）。スクリーンスペースキャンバスの ortho 空間
/// （画面中央が原点・Y 下向き・1 単位 = 1px）で表したカーソル位置。
/// UI のヒットテスト（ポインタイベント）と完全に同じ座標系。
pub const MOUSE_STATE_CANVAS_POSITION: i32 = 3;
/// カーソル座標差分（2 要素）。`CursorMoved` 由来の「前フレームからの移動量」。
/// Raw Input（MOUSE_STATE_DELTA）と違い、エディタ埋め込み Play でも必ず取れる。
pub const MOUSE_STATE_POSITION_DELTA: i32 = 4;

// ── カーソルロック操作種別（C# 側 Input.CursorLocked と一致させる）──
/// 現在のロック状態を取得する。
pub const CURSOR_LOCK_GET: i32 = 0;
/// ロック状態を設定する（フレーム末に App が適用）。
pub const CURSOR_LOCK_SET: i32 = 1;

// ── タッチ（複数指）の問い合わせ種別（C# 側 Input.cs の TouchQuery* と一致させる）──
/// タッチ入力を主に使う端末か（戻り値 1 / 0。out は使わない）。Play 外でも答える。
pub const TOUCH_QUERY_SUPPORTED: i32 = 0;
/// このフレームの指の本数（戻り値 = 本数。out は使わない）。Play 外は 0。
pub const TOUCH_QUERY_COUNT: i32 = 1;
/// index 番目の指（out へ `TOUCH_POINT_FLOATS` 要素。戻り値 = 書いた要素数。範囲外・容量不足・Play 外は 0）。
pub const TOUCH_QUERY_GET: i32 = 2;

// ── 指 1 本を FFI で渡すときの float 配列の並び（C# 側 ScriptHost.InputTouchGet と一致させる）──
/// 要素数。
pub const TOUCH_POINT_FLOATS: usize = 6;
/// 指番号（0 起点の整数を float で。2^24 未満なので無損失）。
pub const TOUCH_FIELD_FINGER_ID: usize = 0;
/// 段階（`TouchPhase::id()` の整数を float で）。
pub const TOUCH_FIELD_PHASE: usize = 1;
/// 位置 x（`Input.MousePos` と同じスクリーン座標系・ピクセル）。
pub const TOUCH_FIELD_POSITION_X: usize = 2;
/// 位置 y。
pub const TOUCH_FIELD_POSITION_Y: usize = 3;
/// 前フレームからの移動量 x。
pub const TOUCH_FIELD_DELTA_X: usize = 4;
/// 前フレームからの移動量 y。
pub const TOUCH_FIELD_DELTA_Y: usize = 5;

/// 指 1 本を FFI の float 配列へ詰める（並びは `TOUCH_FIELD_*`）。
pub fn touch_point_to_floats(t: &TouchPoint) -> [f32; TOUCH_POINT_FLOATS] {
    let mut out = [0.0; TOUCH_POINT_FLOATS];
    out[TOUCH_FIELD_FINGER_ID] = t.finger_id as f32;
    out[TOUCH_FIELD_PHASE] = t.phase.id() as f32;
    out[TOUCH_FIELD_POSITION_X] = t.position.x;
    out[TOUCH_FIELD_POSITION_Y] = t.position.y;
    out[TOUCH_FIELD_DELTA_X] = t.delta.x;
    out[TOUCH_FIELD_DELTA_Y] = t.delta.y;
    out
}

// ─── キーコード対応表 ────────────────────────────────────────

/// C# の SEED.KeyCode（数値 ID）から winit の KeyCode へ変換する。未定義 ID は None。
///
/// ID 割り当て: A-Z=0..25 / 数字 0-9=26..35 / F1-F12=36..47 /
/// 矢印=48..51 / 特殊キー=52..63（C# 側 enum と同一の並び）。
pub fn keycode_from_id(id: u32) -> Option<KeyCode> {
    Some(match id {
        // ── アルファベット（A=0 .. Z=25）──
        0 => KeyCode::KeyA,
        1 => KeyCode::KeyB,
        2 => KeyCode::KeyC,
        3 => KeyCode::KeyD,
        4 => KeyCode::KeyE,
        5 => KeyCode::KeyF,
        6 => KeyCode::KeyG,
        7 => KeyCode::KeyH,
        8 => KeyCode::KeyI,
        9 => KeyCode::KeyJ,
        10 => KeyCode::KeyK,
        11 => KeyCode::KeyL,
        12 => KeyCode::KeyM,
        13 => KeyCode::KeyN,
        14 => KeyCode::KeyO,
        15 => KeyCode::KeyP,
        16 => KeyCode::KeyQ,
        17 => KeyCode::KeyR,
        18 => KeyCode::KeyS,
        19 => KeyCode::KeyT,
        20 => KeyCode::KeyU,
        21 => KeyCode::KeyV,
        22 => KeyCode::KeyW,
        23 => KeyCode::KeyX,
        24 => KeyCode::KeyY,
        25 => KeyCode::KeyZ,
        // ── 数字キー（メインキーボード上段。Alpha0=26 .. Alpha9=35）──
        26 => KeyCode::Digit0,
        27 => KeyCode::Digit1,
        28 => KeyCode::Digit2,
        29 => KeyCode::Digit3,
        30 => KeyCode::Digit4,
        31 => KeyCode::Digit5,
        32 => KeyCode::Digit6,
        33 => KeyCode::Digit7,
        34 => KeyCode::Digit8,
        35 => KeyCode::Digit9,
        // ── ファンクションキー（F1=36 .. F12=47）──
        36 => KeyCode::F1,
        37 => KeyCode::F2,
        38 => KeyCode::F3,
        39 => KeyCode::F4,
        40 => KeyCode::F5,
        41 => KeyCode::F6,
        42 => KeyCode::F7,
        43 => KeyCode::F8,
        44 => KeyCode::F9,
        45 => KeyCode::F10,
        46 => KeyCode::F11,
        47 => KeyCode::F12,
        // ── 矢印キー（48..51）──
        48 => KeyCode::ArrowUp,
        49 => KeyCode::ArrowDown,
        50 => KeyCode::ArrowLeft,
        51 => KeyCode::ArrowRight,
        // ── 特殊キー（52..63）──
        52 => KeyCode::Space,
        53 => KeyCode::Enter,
        54 => KeyCode::Escape,
        55 => KeyCode::Tab,
        56 => KeyCode::Backspace,
        57 => KeyCode::Delete,
        58 => KeyCode::ShiftLeft,
        59 => KeyCode::ShiftRight,
        60 => KeyCode::ControlLeft,
        61 => KeyCode::ControlRight,
        62 => KeyCode::AltLeft,
        63 => KeyCode::AltRight,
        _ => return None,
    })
}

/// C# の SEED.MouseButton（数値 ID）から winit の MouseButton へ変換する。未定義 ID は None。
///
/// ID 割り当て: Left=0 / Right=1 / Middle=2（C# 側 enum と同一）。
pub fn mouse_button_from_id(id: u32) -> Option<MouseButton> {
    Some(match id {
        0 => MouseButton::Left,
        1 => MouseButton::Right,
        2 => MouseButton::Middle,
        _ => return None,
    })
}

// ============================================================
//  テスト（タッチの FFI 配列の並び）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::input::touch::TouchPhase;
    use crate::engine::structs::tensor::Vector2;

    /// 指 1 本の詰め方は C# 側（ScriptHost.InputTouchGet）の読み方と同じ並びであること。
    #[test]
    fn touch_point_layout_matches_csharp_contract() {
        let t = TouchPoint {
            finger_id: 3,
            position: Vector2::new(12.5, 34.0),
            delta: Vector2::new(-1.0, 2.5),
            phase: TouchPhase::Moved,
        };
        assert_eq!(touch_point_to_floats(&t), [3.0, 1.0, 12.5, 34.0, -1.0, 2.5]);
    }
}
