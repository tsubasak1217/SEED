pub mod action_map;
pub mod gamepad;
pub mod inject;
pub mod keyboard;
pub mod mouse;
pub mod raw_input;

pub use raw_input::RawInput;
pub use inject::{InjectAction, InjectCommand, InjectTickOutcome, InputInjection, InputSequencePlayer};

use gamepad::GamepadState;

use std::time::Instant;

use winit::dpi::PhysicalPosition;
use winit::event::{MouseButton, MouseScrollDelta};
use winit::keyboard::KeyCode;
use winit::window::Window;

use crate::engine::structs::tensor::Vector2;
use keyboard::KeyboardState;
use mouse::MouseState;

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
    /// ゲームパッド状態（gilrs バックエンド）。毎フレーム `update_gamepad` でポンプする。
    gamepad: GamepadState,
    /// 外部（エディタ／MCP 経由の AI）から注入された入力。
    ///
    /// 実入力とは**別に**持ち、各クエリで OR 合成する。混ぜずに分けて持つのは
    /// 「実入力の挙動をひとつも変えない」ことと「注入だけを一括解放できる」ことを
    /// 同時に満たすため（詳細は inject モジュール）。
    injection: InputInjection,
    is_active: bool,
}

impl Input {
    pub fn new() -> Self {
        Self {
            keyboard: KeyboardState::new(),
            mouse: MouseState::new(),
            gamepad: GamepadState::new(),
            injection: InputInjection::new(),
            is_active: true,
        }
    }

    /// ゲームパッド状態への参照（action_map の PadQuery 評価用）。
    pub fn gamepad(&self) -> &GamepadState {
        &self.gamepad
    }

    /// 毎フレーム先頭で呼ぶ。gilrs イベントをポンプしてパッド状態を更新する。
    /// キーボード/マウスは winit イベント駆動だが、パッドはここで能動ポンプする。
    pub fn update_gamepad(&mut self) {
        if self.is_active {
            self.gamepad.update();
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
        if !self.is_active {
            return;
        }
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
        self.gamepad.end_frame();
        // 注入側も実入力とまったく同じタイミングで畳む。
        // これにより注入の GetKeyDown / GetKeyUp も 1 フレームだけ立つ。
        self.injection.end_frame();
    }

    // ─── キーボード API ────────────────────────────────────────

    /// キーが押されている間 true（C++: IsPressKey）
    ///
    /// 実入力と注入入力の **OR**。以下のキー・マウス判定もすべて同じ規約で、
    /// 実入力側の式には一切手を加えていない（注入が無ければ従来と完全に同一）。
    pub fn is_press_key(&self, key: KeyCode) -> bool {
        self.is_active && (self.keyboard.is_press(key) || self.injection.state().is_key_held(key))
    }

    /// キーが押された瞬間のみ true（C++: IsTriggerKey）
    pub fn is_trigger_key(&self, key: KeyCode) -> bool {
        self.is_active
            && (self.keyboard.is_trigger(key) || self.injection.state().is_key_pressed(key))
    }

    /// キーが離された瞬間のみ true（C++: IsReleaseKey）
    pub fn is_release_key(&self, key: KeyCode) -> bool {
        self.is_active
            && (self.keyboard.is_release(key) || self.injection.state().is_key_released(key))
    }

    /// いずれかのキーが押されている（C++: IsPressAnyKey）
    pub fn is_press_any_key(&self) -> bool {
        self.is_active && (self.keyboard.is_press_any() || self.injection.state().any_key_held())
    }

    /// いずれかのキーが押された瞬間（C++: IsTriggerAnyKey）
    pub fn is_trigger_any_key(&self) -> bool {
        self.is_active
            && (self.keyboard.is_trigger_any() || self.injection.state().any_key_pressed())
    }

    /// いずれかのキーが離された瞬間（C++: IsReleaseAnyKey）
    pub fn is_release_any_key(&self) -> bool {
        self.is_active
            && (self.keyboard.is_release_any() || self.injection.state().any_key_released())
    }

    // ─── マウス API ────────────────────────────────────────────

    /// マウスボタンが押されている間 true（C++: IsPressMouse）
    pub fn is_press_mouse(&self, button: MouseButton) -> bool {
        self.is_active
            && (self.mouse.is_press(button) || self.injection.state().is_button_held(button))
    }

    /// マウスボタンが押された瞬間のみ true（C++: IsTriggerMouse）
    pub fn is_trigger_mouse(&self, button: MouseButton) -> bool {
        self.is_active
            && (self.mouse.is_trigger(button) || self.injection.state().is_button_pressed(button))
    }

    /// マウスボタンが離された瞬間のみ true（C++: IsReleaseMouse）
    pub fn is_release_mouse(&self, button: MouseButton) -> bool {
        self.is_active
            && (self.mouse.is_release(button) || self.injection.state().is_button_released(button))
    }

    /// マウスの移動ベクトル（生の相対量）を返す（C++: GetMouseVector）
    ///
    /// 注入された相対移動量（`INPUT_MOUSE_MOVE`）を**加算**する。
    /// ボタンと違って移動量は「どちらか」ではなく合成量が意味を持つため。
    pub fn mouse_vector(&self, state: InputState) -> Vector2<f32> {
        let (real, injected) = match state {
            InputState::Current => (self.mouse.delta(), self.injection.state().move_delta()),
            InputState::Previous => {
                (self.mouse.prev_delta(), self.injection.state().prev_move_delta())
            }
        };
        Vector2::new(real.x + injected.x, real.y + injected.y)
    }

    /// カーソル座標の前フレーム比の差分を返す（`CursorMoved` 由来）。
    ///
    /// `mouse_vector`（Raw Input）は埋め込み Play で届かないことがあるため、
    /// 「必ず取れるフレーム移動量」が要る用途（マウスジェスチャ判定）はこちらを使う。
    ///
    /// 注入がある場合の合成規約:
    /// - 絶対座標（`INPUT_MOUSE_POS`）が注入されている間は、実カーソルの差分ではなく
    ///   **注入座標の差分**を使う（実カーソルは AI 操作中には意味を持たないため）。
    /// - 相対移動（`INPUT_MOUSE_MOVE`）は常に加算する（カーソルロック中の
    ///   「振り」判定はこの経路に乗る）。
    pub fn mouse_position_delta(&self) -> Vector2<f32> {
        let injected = self.injection.state();
        let base = if injected.position().is_some() {
            Vector2::zero()
        } else {
            self.mouse.position_delta()
        };
        let add = injected.position_delta();
        Vector2::new(base.x + add.x, base.y + add.y)
    }

    /// マウスの移動方向（正規化済み）を返す（C++: GetMouseDirection）
    pub fn mouse_direction(&self, state: InputState) -> Vector2<f32> {
        self.mouse_vector(state).normalize()
    }

    /// マウスのスクリーン座標を返す（C++: GetMousePosition）
    ///
    /// 絶対座標が注入されている間はそちらを返す（実カーソルより優先）。
    /// この値はスクリプトの `Input.MousePos` と、そこから導かれる
    /// `Input.MousePositionCanvas` / UI のポインタ判定の基準を兼ねる。
    pub fn mouse_position(&self, state: InputState) -> Vector2<f32> {
        let injected = self.injection.state();
        match state {
            InputState::Current => injected.position().unwrap_or_else(|| self.mouse.position()),
            InputState::Previous => injected
                .prev_position()
                .unwrap_or_else(|| self.mouse.prev_position()),
        }
    }

    /// マウスホイールのスクロール量を返す（C++: GetMouseWheel）
    ///
    /// 注入されたホイール量（`INPUT_SCROLL`）を加算する。
    pub fn mouse_scroll(&self, state: InputState) -> f32 {
        match state {
            InputState::Current => self.mouse.scroll() + self.injection.state().scroll(),
            InputState::Previous => {
                self.mouse.prev_scroll() + self.injection.state().prev_scroll()
            }
        }
    }

    /// マウスが動いたか（C++: IsMouseMoved）
    ///
    /// 判定は `mouse_vector`（実入力 + 注入）そのものを見る。
    /// 注入で動かしたのに「動いていない」と返る不整合を作らないため。
    pub fn is_mouse_moved(&self, state: InputState) -> bool {
        let v = self.mouse_vector(state);
        v.x != 0.0 || v.y != 0.0
    }

    /// マウスに何らかの入力があるか（C++: IsMouseInputAny）
    pub fn is_mouse_input_any(&self) -> bool {
        self.is_active && (self.mouse.is_any() || self.injection.state().has_mouse_input())
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

        if pos.x < min.x {
            new_pos.x = max.x;
            moved = true;
        } else if pos.x > max.x {
            new_pos.x = min.x;
            moved = true;
        }
        if pos.y < min.y {
            new_pos.y = max.y;
            moved = true;
        } else if pos.y > max.y {
            new_pos.y = min.y;
            moved = true;
        }

        if moved {
            let _ = window.set_cursor_position(PhysicalPosition::new(new_pos.x, new_pos.y));
        }
    }

    // ─── カーソルロック（相対マウスモード）──────────────────────

    /// カーソルロック中か（スクリプト API `SEED.Input.CursorLocked` の getter）。
    #[inline]
    pub fn is_cursor_locked(&self) -> bool {
        self.mouse.cursor_locked()
    }

    /// カーソルロックの ON/OFF。
    ///
    /// ON の間はカーソルを隠し、毎フレーム末に `update_cursor_lock` でビューポート
    /// 中央へ戻す。これにより「ウィンドウ端でカーソルが止まって `position_delta()` が
    /// 0 になる」問題（エディタ埋め込み Play の ClipCursor 下で顕著）を回避できる。
    ///
    /// ロック中は `mouse_position` は中央付近に張り付くため意味を持たない。
    pub fn set_cursor_lock(&mut self, locked: bool, window: &Window) {
        let was = self.mouse.cursor_locked();
        let center = viewport_center(window);
        self.mouse.set_cursor_lock(locked, center);

        // 可視状態は OS へも反映する（ロック中は隠す / 解除で必ず戻す）。
        self.mouse.set_cursor_visible(!locked);
        window.set_cursor_visible(!locked);

        // ロックし始めたフレームで一度中央へ寄せておく（以降は update_cursor_lock）。
        if locked && !was {
            self.warp_to_lock_center(window, center);
        }
    }

    /// フレーム末に呼ぶ。ロック中ならカーソルをビューポート中央へ戻す。
    ///
    /// 「スクリプトが今フレームの差分を読み終えたあと」に呼ぶこと。
    /// 呼ぶ順序を誤ると、戻した直後の中央座標を差分計算に使ってしまう。
    pub fn update_cursor_lock(&mut self, window: &Window) {
        if !self.mouse.cursor_locked() {
            return;
        }
        let center = viewport_center(window);
        self.warp_to_lock_center(window, center);
    }

    /// カーソルを中央へワープさせ、成功したときだけ入力状態へ反映する。
    ///
    /// 失敗（プラットフォーム未対応など）しても機能を落とすだけなので握り潰すが、
    /// 原因調査のために初回だけ警告を出す。
    fn warp_to_lock_center(&mut self, window: &Window, center: Vector2<f32>) {
        match window.set_cursor_position(PhysicalPosition::new(center.x, center.y)) {
            Ok(()) => self.mouse.notify_warped_to_center(center),
            Err(e) => {
                static WARNED: std::sync::Once = std::sync::Once::new();
                WARNED.call_once(|| {
                    eprintln!("[SEED input] カーソルロックのワープに失敗（以降は無視）: {e}");
                });
            }
        }
    }

    // ─── 入力注入（エディタ／MCP 経由の AI 操作）────────────────

    /// 注入状態への参照（診断・テスト用）。
    #[inline]
    pub fn injection(&self) -> &InputInjection {
        &self.injection
    }

    /// 単発の注入操作を適用する。
    ///
    /// 押下は明示の解放（up / `release_injected_input`）まで保持される。
    pub fn apply_injected_action(&mut self, action: InjectAction) {
        self.injection.apply_action(action);
    }

    /// 時間軸付きシーケンスの再生を開始する。
    ///
    /// 既に再生中なら `false`（呼び出し側が `INPUT_ERROR:sequence_busy` を返す）。
    pub fn start_injected_sequence(&mut self, player: InputSequencePlayer) -> bool {
        self.injection.start_sequence(player)
    }

    /// 注入中の押下・移動量・再生中シーケンスをすべて破棄する。
    pub fn release_injected_input(&mut self) {
        self.injection.release_all();
    }

    /// 毎フレーム 1 回、IPC 処理の直後に呼ぶ。シーケンスを実時間で進める。
    ///
    /// `playing` が false へ落ちた瞬間に注入中の押下を自動解放する
    /// （Play を止めたのに押しっぱなしが残らないようにする安全弁）。
    /// `paused` の間はシーケンスの時計だけを止め、押下はそのまま保持する。
    pub fn tick_injection(&mut self, playing: bool, paused: bool) -> InjectTickOutcome {
        self.injection.tick(playing, paused, Instant::now())
    }

    // ─── アクティブフラグ ──────────────────────────────────────

    /// 入力受付の有効/無効を切り替える（C++: SetIsActive）
    pub fn set_active(&mut self, active: bool) {
        self.is_active = active;
    }
    /// 入力受付が有効かどうかを返す（C++: GetIsActive）
    pub fn is_active(&self) -> bool {
        self.is_active
    }
}

/// ビューポート（ウィンドウのクライアント領域）中央の物理座標を求める。
///
/// `CursorMoved` が返す座標系と同じ「クライアント左上原点の物理ピクセル」なので、
/// カーソルロックの基準点としてそのまま使える。
fn viewport_center(window: &Window) -> Vector2<f32> {
    let size = window.inner_size();
    Vector2::new(size.width as f32 / 2.0, size.height as f32 / 2.0)
}

// ============================================================
//  ユニットテスト（実入力と注入入力の合成）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 注入だけで GetKeyDown 相当のエッジが 1 フレームだけ立つ。
    #[test]
    fn injected_key_edge_lasts_one_frame() {
        let mut input = Input::new();
        input.apply_injected_action(InjectAction::Key { key: KeyCode::KeyW, down: true });

        assert!(input.is_press_key(KeyCode::KeyW));
        assert!(input.is_trigger_key(KeyCode::KeyW), "注入でもトリガが立つこと");
        assert!(input.is_press_any_key());
        assert!(input.is_trigger_any_key());

        input.end_frame();
        assert!(input.is_press_key(KeyCode::KeyW), "押下は保持される");
        assert!(!input.is_trigger_key(KeyCode::KeyW), "トリガは 1 フレームだけ");

        input.apply_injected_action(InjectAction::Key { key: KeyCode::KeyW, down: false });
        assert!(!input.is_press_key(KeyCode::KeyW));
        assert!(input.is_release_key(KeyCode::KeyW));
        input.end_frame();
        assert!(!input.is_release_key(KeyCode::KeyW));
    }

    /// 実入力と注入は OR される（どちらか一方でも押していれば押下）。
    #[test]
    fn real_and_injected_are_ored() {
        let mut input = Input::new();
        // 実入力で A を押す。
        input.process_key(KeyCode::KeyA, true);
        // 注入で B を押す。
        input.apply_injected_action(InjectAction::Key { key: KeyCode::KeyB, down: true });

        assert!(input.is_press_key(KeyCode::KeyA));
        assert!(input.is_press_key(KeyCode::KeyB));

        // 注入を全解放しても実入力の押下は残る。
        input.end_frame();
        input.release_injected_input();
        assert!(input.is_press_key(KeyCode::KeyA), "実入力は解放されないこと");
        assert!(!input.is_press_key(KeyCode::KeyB));
        assert!(input.is_release_key(KeyCode::KeyB), "解放は GetKeyUp として観測できること");
    }

    /// 注入が無ければ実入力の挙動は従来どおり（回帰確認）。
    #[test]
    fn real_input_behaviour_is_unchanged_without_injection() {
        let mut input = Input::new();
        input.process_key(KeyCode::Space, true);
        assert!(input.is_press_key(KeyCode::Space));
        assert!(input.is_trigger_key(KeyCode::Space));
        input.end_frame();
        assert!(input.is_press_key(KeyCode::Space));
        assert!(!input.is_trigger_key(KeyCode::Space));
        input.process_key(KeyCode::Space, false);
        assert!(input.is_release_key(KeyCode::Space));
        assert!(!input.is_press_key(KeyCode::Space));
    }

    /// マウスボタン・ホイール・相対移動の合成。
    #[test]
    fn mouse_state_is_composed() {
        let mut input = Input::new();
        input.process_mouse_motion(3.0, 4.0);
        input.process_scroll(&MouseScrollDelta::LineDelta(0.0, 1.0));
        input.apply_injected_action(InjectAction::MouseMove { dx: 10.0, dy: -1.0 });
        input.apply_injected_action(InjectAction::Scroll { amount: 0.5 });
        input.apply_injected_action(InjectAction::MouseButton {
            button: MouseButton::Left,
            down: true,
        });

        let v = input.mouse_vector(InputState::Current);
        assert_eq!((v.x, v.y), (13.0, 3.0), "相対移動は加算されること");
        assert_eq!(input.mouse_scroll(InputState::Current), 1.5);
        assert!(input.is_press_mouse(MouseButton::Left));
        assert!(input.is_trigger_mouse(MouseButton::Left));
        assert!(input.is_mouse_moved(InputState::Current));
        assert!(input.is_mouse_input_any());
    }

    /// 絶対座標の注入は実カーソルより優先され、差分も注入側から出る。
    #[test]
    fn injected_position_overrides_real_cursor() {
        let mut input = Input::new();
        input.process_cursor_moved(10.0, 10.0);
        input.end_frame();
        input.process_cursor_moved(12.0, 10.0);

        // 注入前は実カーソルの値。
        let p = input.mouse_position(InputState::Current);
        assert_eq!((p.x, p.y), (12.0, 10.0));

        input.apply_injected_action(InjectAction::MousePos { x: 640.0, y: 360.0 });
        let p = input.mouse_position(InputState::Current);
        assert_eq!((p.x, p.y), (640.0, 360.0), "注入座標が優先されること");
        // 初回フレームは差分 0（実カーソル差分も混ざらない）。
        let d = input.mouse_position_delta();
        assert_eq!((d.x, d.y), (0.0, 0.0));

        input.end_frame();
        input.apply_injected_action(InjectAction::MousePos { x: 660.0, y: 350.0 });
        let d = input.mouse_position_delta();
        assert_eq!((d.x, d.y), (20.0, -10.0));

        // 解放すると実カーソルへ戻る。
        input.release_injected_input();
        let p = input.mouse_position(InputState::Current);
        assert_eq!((p.x, p.y), (12.0, 10.0));
    }
}
