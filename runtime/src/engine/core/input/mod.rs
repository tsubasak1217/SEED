pub mod keyboard;
pub mod mouse;

use winit::dpi::PhysicalPosition;
use winit::event::{MouseButton, MouseScrollDelta};
use winit::keyboard::KeyCode;
use winit::window::Window;

use keyboard::KeyboardState;
use mouse::MouseState;
use crate::engine::structs::tensor::Vector2;

// ─── InputState ────────────────────────────────────────────────────────────

/// マウス座標・移動量・スクロール量を取得するときに現在フレームか前フレームかを指定する。
///
/// C++: `INPUT_STATE::CURRENT` / `INPUT_STATE::PREVIOUS`
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InputState {
    /// 今フレームの値
    Current,
    /// 前フレームの値
    Previous,
}

// ─── Input ─────────────────────────────────────────────────────────────────

/// キーボード・マウス入力を一元管理するラッパー。
///
/// # 使い方
/// 1. `App` 構造体にフィールドとして保持する。
/// 2. `ApplicationHandler` の各コールバックで `process_*` メソッドを呼ぶ。
/// 3. フレーム描画後に `end_frame()` を呼ぶ。
///
/// # C++ 対応表
/// | C++                          | Rust                                |
/// |------------------------------|-------------------------------------|
/// | `IsPressKey(key)`            | `is_press_key(key)`                 |
/// | `IsTriggerKey(key)`          | `is_trigger_key(key)`               |
/// | `IsReleaseKey(key)`          | `is_release_key(key)`               |
/// | `IsPressAnyKey()`            | `is_press_any_key()`                |
/// | `IsTriggerAnyKey()`          | `is_trigger_any_key()`              |
/// | `IsReleaseAnyKey()`          | `is_release_any_key()`              |
/// | `IsPressMouse(button)`       | `is_press_mouse(button)`            |
/// | `IsTriggerMouse(button)`     | `is_trigger_mouse(button)`          |
/// | `IsReleaseMouse(button)`     | `is_release_mouse(button)`          |
/// | `GetMouseVector(state)`      | `mouse_vector(state)`               |
/// | `GetMouseDirection(state)`   | `mouse_direction(state)`            |
/// | `GetMousePosition(state)`    | `mouse_position(state)`             |
/// | `GetMouseWheel(state)`       | `mouse_scroll(state)`               |
/// | `IsMouseMoved(state)`        | `is_mouse_moved(state)`             |
/// | `IsMouseInputAny()`          | `is_mouse_input_any()`              |
/// | `SetMouseCursorVisible(v)`   | `set_cursor_visible(v, window)`     |
/// | `ToggleMouseCursorVisible()` | `toggle_cursor_visible(window)`     |
/// | `SetMouseCursorPos(pos)`     | `set_cursor_pos(pos, window)`       |
/// | `RepeatCursor(range)`        | `repeat_cursor(window, min, max)`   |
/// | `SetIsActive(v)`             | `set_active(v)`                     |
/// | `GetIsActive()`              | `is_active()`                       |
pub struct Input {
    keyboard: KeyboardState,
    mouse: MouseState,
    is_active: bool,
}

impl Input {
    pub fn new() -> Self {
        Self {
            keyboard: KeyboardState::new(),
            mouse: MouseState::new(),
            is_active: true,
        }
    }

    // ─── イベント処理 ──────────────────────────────────────────

    /// `WindowEvent::KeyboardInput` を処理する。
    pub fn process_key(&mut self, key: KeyCode, pressed: bool) {
        if self.is_active {
            self.keyboard.process_key(key, pressed);
        }
    }

    /// `WindowEvent::MouseInput` を処理する。
    pub fn process_mouse_button(&mut self, button: MouseButton, pressed: bool) {
        if self.is_active {
            self.mouse.process_button(button, pressed);
        }
    }

    /// `DeviceEvent::MouseMotion` を処理する（生の相対移動量）。
    pub fn process_mouse_motion(&mut self, dx: f64, dy: f64) {
        if self.is_active {
            self.mouse.process_motion(dx, dy);
        }
    }

    /// `WindowEvent::CursorMoved` を処理する（スクリーン座標）。
    pub fn process_cursor_moved(&mut self, x: f32, y: f32) {
        if self.is_active {
            self.mouse.process_cursor_moved(x, y);
        }
    }

    /// `WindowEvent::MouseWheel` を処理する。
    ///
    /// `LineDelta` はそのまま y 値、`PixelDelta` は 1 ライン = 20px として正規化する。
    pub fn process_scroll(&mut self, delta: &MouseScrollDelta) {
        if !self.is_active { return; }
        let lines = match *delta {
            MouseScrollDelta::LineDelta(_, y) => y,
            MouseScrollDelta::PixelDelta(pos) => pos.y as f32 / 20.0,
        };
        self.mouse.process_scroll(lines);
    }

    /// フレーム描画後に呼ぶ。瞬間フラグをクリアし前フレームの値を保存する。
    pub fn end_frame(&mut self) {
        self.keyboard.end_frame();
        self.mouse.end_frame();
    }

    // ─── キーボード API ────────────────────────────────────────

    /// キーが押されている間 true（C++: IsPressKey）
    pub fn is_press_key(&self, key: KeyCode) -> bool {
        self.is_active && self.keyboard.is_press(key)
    }

    /// キーが押された瞬間のみ true（C++: IsTriggerKey）
    pub fn is_trigger_key(&self, key: KeyCode) -> bool {
        self.is_active && self.keyboard.is_trigger(key)
    }

    /// キーが離された瞬間のみ true（C++: IsReleaseKey）
    pub fn is_release_key(&self, key: KeyCode) -> bool {
        self.is_active && self.keyboard.is_release(key)
    }

    /// いずれかのキーが押されている（C++: IsPressAnyKey）
    pub fn is_press_any_key(&self) -> bool {
        self.is_active && self.keyboard.is_press_any()
    }

    /// いずれかのキーが押された瞬間（C++: IsTriggerAnyKey）
    pub fn is_trigger_any_key(&self) -> bool {
        self.is_active && self.keyboard.is_trigger_any()
    }

    /// いずれかのキーが離された瞬間（C++: IsReleaseAnyKey）
    pub fn is_release_any_key(&self) -> bool {
        self.is_active && self.keyboard.is_release_any()
    }

    // ─── マウス API ────────────────────────────────────────────

    /// マウスボタンが押されている間 true（C++: IsPressMouse）
    pub fn is_press_mouse(&self, button: MouseButton) -> bool {
        self.is_active && self.mouse.is_press(button)
    }

    /// マウスボタンが押された瞬間のみ true（C++: IsTriggerMouse）
    pub fn is_trigger_mouse(&self, button: MouseButton) -> bool {
        self.is_active && self.mouse.is_trigger(button)
    }

    /// マウスボタンが離された瞬間のみ true（C++: IsReleaseMouse）
    pub fn is_release_mouse(&self, button: MouseButton) -> bool {
        self.is_active && self.mouse.is_release(button)
    }

    /// マウスの移動ベクトル（生の相対量）を返す（C++: GetMouseVector）
    pub fn mouse_vector(&self, state: InputState) -> Vector2<f32> {
        match state {
            InputState::Current  => self.mouse.delta(),
            InputState::Previous => self.mouse.prev_delta(),
        }
    }

    /// マウスの移動方向（正規化済み）を返す（C++: GetMouseDirection）
    pub fn mouse_direction(&self, state: InputState) -> Vector2<f32> {
        self.mouse_vector(state).normalize()
    }

    /// マウスのスクリーン座標を返す（C++: GetMousePosition）
    pub fn mouse_position(&self, state: InputState) -> Vector2<f32> {
        match state {
            InputState::Current  => self.mouse.position(),
            InputState::Previous => self.mouse.prev_position(),
        }
    }

    /// マウスホイールのスクロール量を返す（C++: GetMouseWheel）
    pub fn mouse_scroll(&self, state: InputState) -> f32 {
        match state {
            InputState::Current  => self.mouse.scroll(),
            InputState::Previous => self.mouse.prev_scroll(),
        }
    }

    /// マウスが動いたか（C++: IsMouseMoved）
    pub fn is_mouse_moved(&self, state: InputState) -> bool {
        match state {
            InputState::Current  => self.mouse.is_moved(),
            InputState::Previous => self.mouse.is_prev_moved(),
        }
    }

    /// マウスに何らかの入力があるか（C++: IsMouseInputAny）
    pub fn is_mouse_input_any(&self) -> bool {
        self.is_active && self.mouse.is_any()
    }

    // ─── カーソル制御 ──────────────────────────────────────────

    /// カーソルの表示/非表示を設定する（C++: SetMouseCursorVisible）
    pub fn set_cursor_visible(&mut self, visible: bool, window: &Window) {
        self.mouse.set_cursor_visible(visible);
        window.set_cursor_visible(visible);
    }

    /// カーソルの表示状態をトグルする（C++: ToggleMouseCursorVisible）
    pub fn toggle_cursor_visible(&mut self, window: &Window) {
        let visible = !self.mouse.cursor_visible();
        self.set_cursor_visible(visible, window);
    }

    /// カーソル位置をスクリーン座標で設定する（C++: SetMouseCursorPos）
    pub fn set_cursor_pos(&self, pos: Vector2<f32>, window: &Window) {
        let _ = window.set_cursor_position(PhysicalPosition::new(pos.x, pos.y));
    }

    /// カーソルが指定の矩形外に出たら反対側に折り返す（C++: RepeatCursor）
    ///
    /// `min` / `max` はスクリーン座標（ピクセル）で指定する。
    pub fn repeat_cursor(&self, window: &Window, min: Vector2<f32>, max: Vector2<f32>) {
        let pos = self.mouse.position();
        let mut new_pos = pos;
        let mut moved = false;

        if pos.x < min.x       { new_pos.x = max.x; moved = true; }
        else if pos.x > max.x  { new_pos.x = min.x; moved = true; }
        if pos.y < min.y       { new_pos.y = max.y; moved = true; }
        else if pos.y > max.y  { new_pos.y = min.y; moved = true; }

        if moved {
            let _ = window.set_cursor_position(PhysicalPosition::new(new_pos.x, new_pos.y));
        }
    }

    // ─── アクティブフラグ ──────────────────────────────────────

    /// 入力受付の有効/無効を切り替える（C++: SetIsActive）
    pub fn set_active(&mut self, active: bool) { self.is_active = active; }
    /// 入力受付が有効かどうかを返す（C++: GetIsActive）
    pub fn is_active(&self) -> bool { self.is_active }
}
