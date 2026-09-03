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

    /// カーソルロック（相対マウスモード）中か。
    ///
    /// ロック中は毎フレーム末にカーソルをビューポート中央へワープさせるため、
    /// `position_delta()` は「前フレーム位置との差」ではなく「中央との差」で求める。
    cursor_locked: bool,
    /// ロック中にカーソルを戻す基準点（クライアント座標のビューポート中央）。
    lock_center: Vector2<f32>,
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
            cursor_locked: false,
            lock_center: Vector2::zero(),
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

    #[inline]
    pub fn is_press(&self, button: MouseButton) -> bool {
        self.held.contains(&button)
    }
    #[inline]
    pub fn is_trigger(&self, button: MouseButton) -> bool {
        self.just_pressed.contains(&button)
    }
    #[inline]
    pub fn is_release(&self, button: MouseButton) -> bool {
        self.just_released.contains(&button)
    }

    // ─── 座標・移動量 クエリ ───────────────────────────────────

    #[inline]
    pub fn position(&self) -> Vector2<f32> {
        self.position
    }
    #[inline]
    pub fn prev_position(&self) -> Vector2<f32> {
        self.prev_position
    }
    #[inline]
    pub fn delta(&self) -> Vector2<f32> {
        self.delta
    }
    #[inline]
    pub fn prev_delta(&self) -> Vector2<f32> {
        self.prev_delta
    }
    #[inline]
    pub fn scroll(&self) -> f32 {
        self.scroll
    }
    #[inline]
    pub fn prev_scroll(&self) -> f32 {
        self.prev_scroll
    }

    /// カーソル座標の前フレーム比の差分（`CursorMoved` 由来）。
    ///
    /// `delta()`（`DeviceEvent::MouseMotion` = Raw Input）との違いが重要:
    /// Raw Input はランタイムウィンドウがエディタへ WS_CHILD で埋め込まれていると
    /// フォアグラウンドプロセス側に横取りされて**届かない**。
    /// こちらは `WindowEvent::CursorMoved` 由来なので埋め込み Play でも必ず動く。
    /// 代わりにカーソルがウィンドウ端でクランプされると 0 になる（Raw Input は動き続ける）。
    /// カーソルロック中は「中央からの差」を返す（毎フレーム中央へ戻しているため、
    /// 前フレーム位置との差だと 1 フレームおきに戻り分の逆ベクトルが混ざる）。
    #[inline]
    pub fn position_delta(&self) -> Vector2<f32> {
        let base = if self.cursor_locked { self.lock_center } else { self.prev_position };
        Vector2::new(self.position.x - base.x, self.position.y - base.y)
    }

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

    #[inline]
    pub fn cursor_visible(&self) -> bool {
        self.cursor_visible
    }
    #[inline]
    pub fn set_cursor_visible(&mut self, visible: bool) {
        self.cursor_visible = visible;
    }

    // ─── カーソルロック（相対マウスモード）────────────────────

    /// カーソルロック中か。
    #[inline]
    pub fn cursor_locked(&self) -> bool {
        self.cursor_locked
    }

    /// ロック中の基準点（ビューポート中央）。
    #[inline]
    pub fn lock_center(&self) -> Vector2<f32> {
        self.lock_center
    }

    /// カーソルロックの ON/OFF を設定する。
    ///
    /// `center` はビューポート中央（クライアント座標）。ON にした瞬間は
    /// position / prev_position をともに中央へ揃えるので、ロック直後のフレームの
    /// `position_delta()` は必ず 0 になる（ロック前の座標が巨大な差分として
    /// 1 フレームだけ吹き出すのを防ぐ）。
    pub fn set_cursor_lock(&mut self, locked: bool, center: Vector2<f32>) {
        if locked {
            if !self.cursor_locked {
                self.cursor_locked = true;
                self.position = center;
                self.prev_position = center;
            }
            self.lock_center = center;
        } else {
            self.cursor_locked = false;
        }
    }

    /// カーソルを中央へワープさせたことを入力状態へ反映する。
    ///
    /// 実際の OS 呼び出し（`Window::set_cursor_position`）が成功したときだけ呼ぶこと。
    /// ワープ由来の `CursorMoved` は非同期に届くが、その値も中央なので
    /// ここで先に中央を入れておけば二重反映にはならず、次フレームの差分は
    /// 「実際に動いた分」だけになる。
    pub fn notify_warped_to_center(&mut self, center: Vector2<f32>) {
        self.lock_center = center;
        self.position = center;
        self.prev_position = center;
    }
}

// ============================================================
//  テスト（カーソルロック時の position_delta）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// ロック直後のフレームは差分 0（ロック前の座標を持ち越さない）。
    #[test]
    fn enabling_lock_yields_zero_delta() {
        let mut m = MouseState::new();
        m.process_cursor_moved(800.0, 600.0); // ロック前にどこかにいる
        let center = Vector2::new(320.0, 240.0);
        m.set_cursor_lock(true, center);
        let d = m.position_delta();
        assert_eq!((d.x, d.y), (0.0, 0.0), "ロックした瞬間の差分は 0 であるべき");
    }

    /// 中央から (+5, -3) 動いたら差分も (5, -3)。
    #[test]
    fn movement_from_center_is_reported() {
        let mut m = MouseState::new();
        let center = Vector2::new(320.0, 240.0);
        m.set_cursor_lock(true, center);
        m.process_cursor_moved(center.x + 5.0, center.y - 3.0);
        let d = m.position_delta();
        assert_eq!((d.x, d.y), (5.0, -3.0));
    }

    /// 中央へワープし直した直後は差分 0（戻し分が逆ベクトルとして出ない）。
    #[test]
    fn warp_back_to_center_yields_zero_delta() {
        let mut m = MouseState::new();
        let center = Vector2::new(320.0, 240.0);
        m.set_cursor_lock(true, center);
        m.process_cursor_moved(center.x + 5.0, center.y - 3.0);
        m.end_frame();
        m.notify_warped_to_center(center);
        assert_eq!((m.position_delta().x, m.position_delta().y), (0.0, 0.0));
        // ワープ由来の CursorMoved（中央）が遅れて届いても 0 のまま。
        m.process_cursor_moved(center.x, center.y);
        assert_eq!((m.position_delta().x, m.position_delta().y), (0.0, 0.0));
    }

    /// ロック解除後は従来どおり「前フレーム位置との差」に戻る。
    #[test]
    fn unlocked_uses_prev_position() {
        let mut m = MouseState::new();
        m.process_cursor_moved(100.0, 100.0);
        m.end_frame();
        m.process_cursor_moved(110.0, 90.0);
        let d = m.position_delta();
        assert_eq!((d.x, d.y), (10.0, -10.0));
    }
}
