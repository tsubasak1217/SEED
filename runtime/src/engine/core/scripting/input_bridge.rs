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

// ─── 入力判定の種別 ──────────────────────────────────────────

/// 押下状態の判定種別（C# 側 FFI 呼び出しの kind 引数と一致させる）。
pub const INPUT_KIND_PRESS: i32 = 0; // 押されている間
pub const INPUT_KIND_TRIGGER: i32 = 1; // 押された瞬間
pub const INPUT_KIND_RELEASE: i32 = 2; // 離された瞬間

/// マウス状態取得の種別（C# 側 FFI 呼び出しの kind 引数と一致させる）。
pub const MOUSE_STATE_POSITION: i32 = 0; // スクリーン座標（2 要素）
pub const MOUSE_STATE_DELTA: i32 = 1; // 相対移動量（2 要素）
pub const MOUSE_STATE_SCROLL: i32 = 2; // ホイール量（1 要素）

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
