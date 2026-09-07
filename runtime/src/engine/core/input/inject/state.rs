// ============================================================
//  state.rs — 注入された入力の保持
//
//  実入力（KeyboardState / MouseState）と**同じ構造**を意図的に採っている:
//  「押されている集合」「このフレームで押された集合」「このフレームで離された集合」。
//  こうしておくと、`Input` 側は実入力の判定結果と OR を取るだけで
//  GetKeyDown / GetKeyUp のエッジが注入でも 1 フレームだけ正しく立つ。
//
//  このファイルは**状態の保管だけ**を行う（時間の概念も IPC の概念も持たない）。
// ============================================================

use std::collections::HashSet;

use winit::event::MouseButton;
use winit::keyboard::KeyCode;

use crate::engine::structs::tensor::Vector2;

use super::command::InjectAction;

// ============================================================
//  InjectedInputState
// ============================================================

/// 外部から注入された入力の現在値。
///
/// 押下（キー・マウスボタン）は「明示の up / RELEASE_ALL / Play 停止」まで保持される。
/// 相対移動量とスクロールは実入力と同じく **1 フレーム限りの累積値**で、
/// `end_frame` で前フレーム値へ畳まれる。
/// 絶対座標だけは例外で、一度注入したら解放されるまで実カーソルより優先される
/// （AI が「この座標を指している」と宣言した状態を保つため）。
pub struct InjectedInputState {
    /// 注入によって押されているキー。
    keys_held: HashSet<KeyCode>,
    /// このフレームに注入で押されたキー（`end_frame` でクリア）。
    keys_pressed: HashSet<KeyCode>,
    /// このフレームに注入で離されたキー（`end_frame` でクリア）。
    keys_released: HashSet<KeyCode>,

    /// 注入によって押されているマウスボタン。
    buttons_held: HashSet<MouseButton>,
    /// このフレームに注入で押されたマウスボタン。
    buttons_pressed: HashSet<MouseButton>,
    /// このフレームに注入で離されたマウスボタン。
    buttons_released: HashSet<MouseButton>,

    /// このフレームに注入された相対移動量の累積。
    move_delta: Vector2<f32>,
    /// 前フレームの相対移動量。
    prev_move_delta: Vector2<f32>,

    /// このフレームに注入されたスクロール量の累積（ライン数）。
    scroll: f32,
    /// 前フレームのスクロール量。
    prev_scroll: f32,

    /// 注入された絶対座標（None = 注入されていない＝実カーソルを使う）。
    position: Option<Vector2<f32>>,
    /// 前フレームの注入絶対座標（差分計算用）。
    prev_position: Option<Vector2<f32>>,
}

impl InjectedInputState {
    /// 何も注入されていない状態を作る。
    pub fn new() -> Self {
        Self {
            keys_held: HashSet::new(),
            keys_pressed: HashSet::new(),
            keys_released: HashSet::new(),
            buttons_held: HashSet::new(),
            buttons_pressed: HashSet::new(),
            buttons_released: HashSet::new(),
            move_delta: Vector2::zero(),
            prev_move_delta: Vector2::zero(),
            scroll: 0.0,
            prev_scroll: 0.0,
            position: None,
            prev_position: None,
        }
    }

    // ─── 適用 ──────────────────────────────────────────────────

    /// 注入アクション 1 件を状態へ反映する。
    pub fn apply(&mut self, action: InjectAction) {
        match action {
            InjectAction::Key { key, down } => {
                if down {
                    // 既に押されているキーの再 down はエッジを立てない（実入力と同じ）。
                    if self.keys_held.insert(key) {
                        self.keys_pressed.insert(key);
                    }
                } else if self.keys_held.remove(&key) {
                    self.keys_released.insert(key);
                }
            }
            InjectAction::MouseButton { button, down } => {
                if down {
                    if self.buttons_held.insert(button) {
                        self.buttons_pressed.insert(button);
                    }
                } else if self.buttons_held.remove(&button) {
                    self.buttons_released.insert(button);
                }
            }
            InjectAction::MouseMove { dx, dy } => {
                self.move_delta.x += dx;
                self.move_delta.y += dy;
            }
            InjectAction::MousePos { x, y } => {
                self.position = Some(Vector2::new(x, y));
            }
            InjectAction::Scroll { amount } => {
                self.scroll += amount;
            }
        }
    }

    /// 注入中の押下をすべて解放し、累積値と絶対座標の上書きも捨てる。
    ///
    /// 解放されたキー・ボタンは `keys_released` / `buttons_released` へ入るので、
    /// このフレームのスクリプトは GetKeyUp を 1 度だけ観測できる
    /// （押しっぱなしのまま消えると、離した扱いにならず状態機械が壊れるため）。
    pub fn release_all(&mut self) {
        for key in self.keys_held.drain() {
            self.keys_released.insert(key);
        }
        for button in self.buttons_held.drain() {
            self.buttons_released.insert(button);
        }
        self.move_delta = Vector2::zero();
        self.scroll = 0.0;
        self.position = None;
        self.prev_position = None;
    }

    /// フレーム末に呼ぶ。エッジ集合をクリアし、1 フレーム限りの累積を畳む。
    pub fn end_frame(&mut self) {
        self.keys_pressed.clear();
        self.keys_released.clear();
        self.buttons_pressed.clear();
        self.buttons_released.clear();

        self.prev_move_delta = self.move_delta;
        self.move_delta = Vector2::zero();

        self.prev_scroll = self.scroll;
        self.scroll = 0.0;

        // 絶対座標は「状態」なので消さない。前フレーム値だけ更新する。
        self.prev_position = self.position;
    }

    // ─── クエリ（キー）─────────────────────────────────────────

    #[inline]
    pub fn is_key_held(&self, key: KeyCode) -> bool {
        self.keys_held.contains(&key)
    }
    #[inline]
    pub fn is_key_pressed(&self, key: KeyCode) -> bool {
        self.keys_pressed.contains(&key)
    }
    #[inline]
    pub fn is_key_released(&self, key: KeyCode) -> bool {
        self.keys_released.contains(&key)
    }
    #[inline]
    pub fn any_key_held(&self) -> bool {
        !self.keys_held.is_empty()
    }
    #[inline]
    pub fn any_key_pressed(&self) -> bool {
        !self.keys_pressed.is_empty()
    }
    #[inline]
    pub fn any_key_released(&self) -> bool {
        !self.keys_released.is_empty()
    }

    // ─── クエリ（マウスボタン）─────────────────────────────────

    #[inline]
    pub fn is_button_held(&self, button: MouseButton) -> bool {
        self.buttons_held.contains(&button)
    }
    #[inline]
    pub fn is_button_pressed(&self, button: MouseButton) -> bool {
        self.buttons_pressed.contains(&button)
    }
    #[inline]
    pub fn is_button_released(&self, button: MouseButton) -> bool {
        self.buttons_released.contains(&button)
    }

    // ─── クエリ（座標・移動量・スクロール）─────────────────────

    #[inline]
    pub fn move_delta(&self) -> Vector2<f32> {
        self.move_delta
    }
    #[inline]
    pub fn prev_move_delta(&self) -> Vector2<f32> {
        self.prev_move_delta
    }
    #[inline]
    pub fn scroll(&self) -> f32 {
        self.scroll
    }
    #[inline]
    pub fn prev_scroll(&self) -> f32 {
        self.prev_scroll
    }
    #[inline]
    pub fn position(&self) -> Option<Vector2<f32>> {
        self.position
    }
    #[inline]
    pub fn prev_position(&self) -> Option<Vector2<f32>> {
        self.prev_position
    }

    /// 注入由来の座標差分（絶対座標の変化 + 相対移動量）。
    ///
    /// 絶対座標を注入した最初のフレームは 0 を返す（前フレーム値が無いため、
    /// 「どこかから飛んできた巨大な差分」を作らない）。
    pub fn position_delta(&self) -> Vector2<f32> {
        let from_abs = match (self.position, self.prev_position) {
            (Some(cur), Some(prev)) => Vector2::new(cur.x - prev.x, cur.y - prev.y),
            _ => Vector2::zero(),
        };
        Vector2::new(from_abs.x + self.move_delta.x, from_abs.y + self.move_delta.y)
    }

    /// 何らかの注入入力があるか（`is_mouse_input_any` の合成に使う）。
    #[inline]
    pub fn has_mouse_input(&self) -> bool {
        !self.buttons_held.is_empty()
            || self.move_delta.x != 0.0
            || self.move_delta.y != 0.0
            || self.scroll != 0.0
    }
}

impl Default for InjectedInputState {
    fn default() -> Self {
        Self::new()
    }
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 押下は明示の up まで保持され、エッジは 1 フレームだけ立つ。
    #[test]
    fn key_edges_last_exactly_one_frame() {
        let mut s = InjectedInputState::new();
        s.apply(InjectAction::Key { key: KeyCode::KeyW, down: true });
        assert!(s.is_key_held(KeyCode::KeyW));
        assert!(s.is_key_pressed(KeyCode::KeyW), "押した瞬間は pressed");
        assert!(!s.is_key_released(KeyCode::KeyW));

        s.end_frame();
        assert!(s.is_key_held(KeyCode::KeyW), "保持は続く");
        assert!(!s.is_key_pressed(KeyCode::KeyW), "pressed は 1 フレームだけ");

        s.apply(InjectAction::Key { key: KeyCode::KeyW, down: false });
        assert!(!s.is_key_held(KeyCode::KeyW));
        assert!(s.is_key_released(KeyCode::KeyW));
        s.end_frame();
        assert!(!s.is_key_released(KeyCode::KeyW));
    }

    /// 同じキーの二重 down はエッジを 1 回しか立てない。
    #[test]
    fn repeated_down_does_not_retrigger() {
        let mut s = InjectedInputState::new();
        s.apply(InjectAction::Key { key: KeyCode::Space, down: true });
        s.end_frame();
        s.apply(InjectAction::Key { key: KeyCode::Space, down: true });
        assert!(!s.is_key_pressed(KeyCode::Space), "押しっぱなしで再トリガしないこと");
        assert!(s.is_key_held(KeyCode::Space));
    }

    /// RELEASE_ALL は保持を消しつつ、離しエッジを残す。
    #[test]
    fn release_all_emits_release_edges() {
        let mut s = InjectedInputState::new();
        s.apply(InjectAction::Key { key: KeyCode::KeyA, down: true });
        s.apply(InjectAction::MouseButton { button: MouseButton::Left, down: true });
        s.end_frame();

        s.release_all();
        assert!(!s.is_key_held(KeyCode::KeyA));
        assert!(s.is_key_released(KeyCode::KeyA), "解放も GetKeyUp として観測できること");
        assert!(!s.is_button_held(MouseButton::Left));
        assert!(s.is_button_released(MouseButton::Left));
    }

    /// 相対移動量はフレーム内で累積し、フレーム末に前フレーム値へ畳まれる。
    #[test]
    fn mouse_move_accumulates_per_frame() {
        let mut s = InjectedInputState::new();
        s.apply(InjectAction::MouseMove { dx: 10.0, dy: -2.0 });
        s.apply(InjectAction::MouseMove { dx: 5.0, dy: 1.0 });
        assert_eq!((s.move_delta().x, s.move_delta().y), (15.0, -1.0));

        s.end_frame();
        assert_eq!((s.move_delta().x, s.move_delta().y), (0.0, 0.0));
        assert_eq!((s.prev_move_delta().x, s.prev_move_delta().y), (15.0, -1.0));
    }

    /// 絶対座標は解放まで保持され、差分は 2 フレーム目から出る。
    #[test]
    fn absolute_position_is_sticky_and_yields_delta() {
        let mut s = InjectedInputState::new();
        s.apply(InjectAction::MousePos { x: 100.0, y: 50.0 });
        assert_eq!(s.position().map(|p| (p.x, p.y)), Some((100.0, 50.0)));
        // 初回フレームは差分 0（前フレーム値が無い）。
        assert_eq!((s.position_delta().x, s.position_delta().y), (0.0, 0.0));

        s.end_frame();
        s.apply(InjectAction::MousePos { x: 120.0, y: 40.0 });
        assert_eq!((s.position_delta().x, s.position_delta().y), (20.0, -10.0));

        s.end_frame();
        // 何も注入しなくても座標は保持される（差分は 0 に戻る）。
        assert_eq!(s.position().map(|p| (p.x, p.y)), Some((120.0, 40.0)));
        assert_eq!((s.position_delta().x, s.position_delta().y), (0.0, 0.0));

        s.release_all();
        assert!(s.position().is_none(), "解放で実カーソルへ戻ること");
    }

    /// スクロールも 1 フレーム限りの累積値。
    #[test]
    fn scroll_accumulates_per_frame() {
        let mut s = InjectedInputState::new();
        s.apply(InjectAction::Scroll { amount: 1.5 });
        s.apply(InjectAction::Scroll { amount: -0.5 });
        assert_eq!(s.scroll(), 1.0);
        s.end_frame();
        assert_eq!(s.scroll(), 0.0);
        assert_eq!(s.prev_scroll(), 1.0);
    }
}
