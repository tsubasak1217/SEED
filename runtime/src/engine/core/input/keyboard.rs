use std::collections::HashSet;
use winit::keyboard::KeyCode;

/// キーボードの入力状態を管理する。
///
/// winit の `WindowEvent::KeyboardInput` から `process_key` を呼び、
/// フレーム末に `end_frame` を呼ぶことで 1 フレーム分の状態が確定する。
pub struct KeyboardState {
    /// 現在押されているキーの集合
    held: HashSet<KeyCode>,
    /// このフレームで押された瞬間のキー集合（次の `end_frame` でクリア）
    just_pressed: HashSet<KeyCode>,
    /// このフレームで離された瞬間のキー集合（次の `end_frame` でクリア）
    just_released: HashSet<KeyCode>,
}

impl KeyboardState {
    pub fn new() -> Self {
        Self {
            held: HashSet::new(),
            just_pressed: HashSet::new(),
            just_released: HashSet::new(),
        }
    }

    /// winit の `KeyboardInput` イベントを処理する。
    ///
    /// - `pressed = true`  → キーを `held` に追加し、新規なら `just_pressed` にも追加
    /// - `pressed = false` → キーを `held` から除去し、`just_released` に追加
    pub fn process_key(&mut self, key: KeyCode, pressed: bool) {
        if pressed {
            if self.held.insert(key) {
                self.just_pressed.insert(key);
            }
        } else if self.held.remove(&key) {
            self.just_released.insert(key);
        }
    }

    /// フレーム終了時に呼ぶ。`just_pressed` と `just_released` をクリアする。
    pub fn end_frame(&mut self) {
        self.just_pressed.clear();
        self.just_released.clear();
    }

    // ─── クエリ ────────────────────────────────────────────────

    /// キーが押されている間 true（C++: IsPressKey）
    #[inline] pub fn is_press(&self, key: KeyCode) -> bool   { self.held.contains(&key) }
    /// キーが押された瞬間のみ true（C++: IsTriggerKey）
    #[inline] pub fn is_trigger(&self, key: KeyCode) -> bool { self.just_pressed.contains(&key) }
    /// キーが離された瞬間のみ true（C++: IsReleaseKey）
    #[inline] pub fn is_release(&self, key: KeyCode) -> bool { self.just_released.contains(&key) }

    /// いずれかのキーが押されている（C++: IsPressAnyKey）
    #[inline] pub fn is_press_any(&self) -> bool   { !self.held.is_empty() }
    /// いずれかのキーが押された瞬間（C++: IsTriggerAnyKey）
    #[inline] pub fn is_trigger_any(&self) -> bool { !self.just_pressed.is_empty() }
    /// いずれかのキーが離された瞬間（C++: IsReleaseAnyKey）
    #[inline] pub fn is_release_any(&self) -> bool { !self.just_released.is_empty() }
}
