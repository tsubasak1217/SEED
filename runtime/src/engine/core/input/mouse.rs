use std::collections::HashSet;
use winit::event::MouseButton;

use crate::engine::structs::tensor::Vector2;

/// マウスの入力状態を管理する。
///
/// | イベント                        | 呼ぶメソッド            |
/// |---------------------------------|-------------------------|
/// | `WindowEvent::MouseInput`       | `process_button`        |
/// | `WindowEvent::CursorMoved`      | `process_cursor_moved`  |
/// | `WindowEvent::MouseWheel`       | `process_scroll`        |
/// | `DeviceEvent::MouseMotion`      | `process_motion`        |
/// | フレーム末                      | `end_frame`             |
pub struct MouseState {
    /// 現在押されているボタンの集合
    held: HashSet<MouseButton>,
    /// このフレームで押された瞬間のボタン集合
    just_pressed: HashSet<MouseButton>,
    /// このフレームで離された瞬間のボタン集合
    just_released: HashSet<MouseButton>,

    /// スクリーン上のカーソル座標（ピクセル）
    position: Vector2<f32>,
    prev_position: Vector2<f32>,

    /// フレーム内に累積した生のマウス移動量（DeviceEvent::MouseMotion 由来）
    delta: Vector2<f32>,
    prev_delta: Vector2<f32>,

    /// フレーム内のホイールスクロール量（ライン数または正規化ピクセル量）
    scroll: f32,
    prev_scroll: f32,

    /// カーソルの表示状態（エンジン側の記録用。実際の表示は Window に委ねる）
    cursor_visible: bool,
}

impl MouseState {
    pub fn new() -> Self {
        Self {
            held: HashSet::new(),
            just_pressed: HashSet::new(),
            just_released: HashSet::new(),
            position: Vector2::zero(),
            prev_position: Vector2::zero(),
            delta: Vector2::zero(),
            prev_delta: Vector2::zero(),
            scroll: 0.0,
            prev_scroll: 0.0,
            cursor_visible: true,
        }
    }

    // ─── イベント処理 ──────────────────────────────────────────

    /// `WindowEvent::MouseInput` を処理する。
    pub fn process_button(&mut self, button: MouseButton, pressed: bool) {
        if pressed {
            if self.held.insert(button) {
                self.just_pressed.insert(button);
            }
        } else if self.held.remove(&button) {
            self.just_released.insert(button);
        }
    }

    /// `DeviceEvent::MouseMotion` を処理する（生の相対移動量を累積）。
    pub fn process_motion(&mut self, dx: f64, dy: f64) {
        self.delta.x += dx as f32;
        self.delta.y += dy as f32;
    }

    /// `WindowEvent::CursorMoved` を処理する（スクリーン座標）。
    pub fn process_cursor_moved(&mut self, x: f32, y: f32) {
        self.position = Vector2::new(x, y);
    }

    /// `WindowEvent::MouseWheel` を処理する（ライン数を渡す想定）。
    pub fn process_scroll(&mut self, lines: f32) {
        self.scroll += lines;
    }

    /// フレーム終了時に呼ぶ。瞬間フラグをクリアし、前フレームの値を保存する。
    pub fn end_frame(&mut self) {
        self.just_pressed.clear();
        self.just_released.clear();
        self.prev_position = self.position;
        self.prev_delta = self.delta;
        self.delta = Vector2::zero();
        self.prev_scroll = self.scroll;
        self.scroll = 0.0;
    }

    // ─── ボタン クエリ ─────────────────────────────────────────

    #[inline] pub fn is_press(&self, button: MouseButton) -> bool   { self.held.contains(&button) }
    #[inline] pub fn is_trigger(&self, button: MouseButton) -> bool { self.just_pressed.contains(&button) }
    #[inline] pub fn is_release(&self, button: MouseButton) -> bool { self.just_released.contains(&button) }

    // ─── 座標・移動量 クエリ ───────────────────────────────────

    #[inline] pub fn position(&self) -> Vector2<f32>      { self.position }
    #[inline] pub fn prev_position(&self) -> Vector2<f32> { self.prev_position }
    #[inline] pub fn delta(&self) -> Vector2<f32>         { self.delta }
    #[inline] pub fn prev_delta(&self) -> Vector2<f32>    { self.prev_delta }
    #[inline] pub fn scroll(&self) -> f32                 { self.scroll }
    #[inline] pub fn prev_scroll(&self) -> f32            { self.prev_scroll }

    /// このフレームにマウスが動いたか（delta が 0 でないか）
    #[inline]
    pub fn is_moved(&self) -> bool {
        self.delta.x != 0.0 || self.delta.y != 0.0
    }

    /// 前フレームにマウスが動いたか
    #[inline]
    pub fn is_prev_moved(&self) -> bool {
        self.prev_delta.x != 0.0 || self.prev_delta.y != 0.0
    }

    /// ボタン押下・移動・スクロールのいずれかがあるか
    #[inline]
    pub fn is_any(&self) -> bool {
        !self.held.is_empty() || self.is_moved() || self.scroll != 0.0
    }

    // ─── カーソル ─────────────────────────────────────────────

    #[inline] pub fn cursor_visible(&self) -> bool { self.cursor_visible }
    #[inline] pub fn set_cursor_visible(&mut self, visible: bool) { self.cursor_visible = visible; }
}
