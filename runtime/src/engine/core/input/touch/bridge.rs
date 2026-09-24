// ============================================================
//  touch/bridge.rs — マウス ⇔ タッチの相互変換（どちらの入力源が「指0／左ボタン」を握るかの調停）
//
//  【役割】
//  実入力（winit のタッチ・マウス）を受け取り、TouchState と MouseState の両方へ振り分ける。
//  どちら向きの変換を行うかはプラットフォーム特性（PlatformTraits）から写した方針で決める。
//
//  - touch_drives_mouse（Android）
//      指0（他に触れている指が無い状態で触れ始めた指）が MouseState を駆動する。
//      位置 → カーソル座標、触れている間 → 左ボタン押下。指0 が離れたら左ボタンを離す。
//      他の指はマウスに影響しない。これで既存のキャンバス UI・スクリプトのマウス API が動く。
//  - mouse_simulates_touch（デスクトップ）
//      マウス左ボタンで「指」を 1 本合成する（押下 → Began、押下中の移動 → Moved、
//      動かなければ Stationary、離す → Ended）。PC の Play でも Input.GetTouch を試せる。
//
//  【二重駆動の防止（どちらか一方だけが効く）】
//  - 方針表の段階で 2 つのフラグは排他（platform/mod.rs のテストで固定）。
//  - touch_drives_mouse の端末で、実タッチが触れている／タッチ由来の押下が続いている間は、
//    実マウスのカーソル移動・左ボタンを無視する（同じ操作がマウスとしても届く環境への備え。
//    winit 0.30.13 の Android 実装は MotionEvent をすべて Touch として送り、CursorMoved /
//    MouseInput は出さないことをソースで確認済み。将来の版・別バックエンドのための保険）。
//  - mouse_simulates_touch の端末で実タッチ（タッチパネル付き PC）が触れたら、合成中の指は
//    Canceled で畳み、実タッチが触れている間は新しく合成しない（OS がタッチをマウスへ昇格して
//    送ってくる環境で、同じ操作を 2 本の指として数えないため）。
//
//  【押下と解放を同じフレームに潰さない】
//  タッチ由来の左ボタンは、TouchState が見せる段階（Began で押下・Ended / Canceled で解放）に合わせる。
//  触れ始めたフレームのうちに離れたタップは TouchState 側が Ended を次フレームへ送るので、
//  マウスも「押下フレーム → 次フレームで解放」になる（GetMouseButton で押下中を見るスクリプトも取りこぼさない）。
//  指0 が入れ替わって解放と押下が同じフレームに重なるときは、押下を次フレームへ回す。
// ============================================================

use winit::event::{MouseButton, TouchPhase as RawTouchPhase};

use crate::engine::platform::PlatformTraits;
use crate::engine::structs::tensor::Vector2;

use super::super::mouse::MouseState;
use super::state::TouchState;

/// マウス左ボタンから合成する指の生 ID。
///
/// OS の生 ID と衝突しない予約値（Android のポインタ ID は小さな整数、Windows のタッチ ID は u32）。
pub const MOUSE_FINGER_RAW_ID: u64 = u64::MAX;

/// タッチ由来で駆動する（・合成に使う）マウスボタン。
const POINTER_BUTTON: MouseButton = MouseButton::Left;

// ─── 方針 ──────────────────────────────────────────────────

/// 相互変換の方針（プラットフォーム特性から写す。テストでは任意の組を作れる）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PointerBridgePolicy {
    /// 指0 が MouseState（カーソル座標＋左ボタン）を駆動するか。
    pub touch_drives_mouse: bool,
    /// マウス左ボタンで指を合成するか。
    pub mouse_simulates_touch: bool,
}

impl PointerBridgePolicy {
    /// プラットフォーム特性から方針を作る。
    pub fn from_traits(traits: &PlatformTraits) -> Self {
        Self {
            touch_drives_mouse: traits.touch_drives_mouse,
            mouse_simulates_touch: traits.mouse_simulates_touch,
        }
    }
}

// ─── PointerBridge ─────────────────────────────────────────

/// マウス ⇔ タッチの相互変換と、入力源の調停。
///
/// | 実入力                         | 呼ぶメソッド       |
/// |--------------------------------|--------------------|
/// | `WindowEvent::Touch`           | `on_touch`         |
/// | `WindowEvent::CursorMoved`     | `on_cursor_moved`  |
/// | `WindowEvent::MouseInput`      | `on_mouse_button`  |
/// | フォーカス喪失（安全弁）       | `cancel_real_touches` |
/// | フレーム末（mouse.end_frame の後）| `end_frame`     |
pub struct PointerBridge {
    /// 相互変換の方針。
    policy: PointerBridgePolicy,
    /// タッチ由来で MouseState の左ボタンを押している指（指0 の生 ID）。None = タッチは押していない。
    held_by: Option<u64>,
    /// マウス左ボタンから合成した指が触れている最中か。
    mouse_finger_active: bool,
}

impl PointerBridge {
    /// 方針を指定して作る。
    pub fn new(policy: PointerBridgePolicy) -> Self {
        Self {
            policy,
            held_by: None,
            mouse_finger_active: false,
        }
    }

    /// 現在の方針。
    #[inline]
    pub fn policy(&self) -> PointerBridgePolicy {
        self.policy
    }

    // ─── 実タッチ ──────────────────────────────────────────

    /// 実タッチのイベント 1 件（位置は入力座標系へ写像済み）。
    pub fn on_touch(
        &mut self,
        raw_id: u64,
        kind: RawTouchPhase,
        position: Vector2<f32>,
        mouse: &mut MouseState,
        touch: &mut TouchState,
    ) {
        // 実タッチが触れ始めたら、マウスから合成した指は取り消す（同じ操作を 2 本に数えない）。
        if kind == RawTouchPhase::Started && self.mouse_finger_active {
            touch.apply(MOUSE_FINGER_RAW_ID, RawTouchPhase::Cancelled, mouse.position());
            self.mouse_finger_active = false;
        }
        touch.apply(raw_id, kind, position);
        self.sync_mouse_from_touch(mouse, touch);
    }

    /// フォーカス喪失などの安全弁: 実タッチの指をすべて取り消し、タッチ由来の押下を解く。
    ///
    /// マウスから合成した指は残す（マウスの左ボタン自体はフォーカス喪失でも解除しない従来動作と揃える）。
    pub fn cancel_real_touches(&mut self, mouse: &mut MouseState, touch: &mut TouchState) {
        touch.cancel_matching(|raw_id| raw_id != MOUSE_FINGER_RAW_ID);
        self.sync_mouse_from_touch(mouse, touch);
    }

    // ─── 実マウス ──────────────────────────────────────────

    /// 実マウスのカーソル移動（位置は入力座標系へ写像済み）。
    pub fn on_cursor_moved(
        &mut self,
        position: Vector2<f32>,
        mouse: &mut MouseState,
        touch: &mut TouchState,
    ) {
        if self.touch_owns_pointer(touch) {
            return;
        }
        mouse.process_cursor_moved(position.x, position.y);
        if self.mouse_finger_active {
            touch.apply(MOUSE_FINGER_RAW_ID, RawTouchPhase::Moved, position);
        }
    }

    /// 実マウスのボタン。
    pub fn on_mouse_button(
        &mut self,
        button: MouseButton,
        pressed: bool,
        mouse: &mut MouseState,
        touch: &mut TouchState,
    ) {
        // 右・中ボタンはタッチと無関係なのでそのまま通す。
        if button != POINTER_BUTTON {
            mouse.process_button(button, pressed);
            return;
        }
        if self.touch_owns_pointer(touch) {
            return;
        }
        mouse.process_button(button, pressed);
        if !self.policy.mouse_simulates_touch {
            return;
        }
        if pressed {
            // 実タッチが触れている間は合成しない（タッチの昇格マウスを 2 本目に数えないため）。
            if !self.mouse_finger_active && touch.down_count() == 0 {
                touch.apply(MOUSE_FINGER_RAW_ID, RawTouchPhase::Started, mouse.position());
                self.mouse_finger_active = true;
            }
        } else if self.mouse_finger_active {
            touch.apply(MOUSE_FINGER_RAW_ID, RawTouchPhase::Ended, mouse.position());
            self.mouse_finger_active = false;
        }
    }

    // ─── フレーム末 ────────────────────────────────────────

    /// フレーム末に呼ぶ。タッチの段階を進め、次フレームで見せる解放・押下をマウスへ反映する。
    ///
    /// **`MouseState::end_frame` の後に呼ぶこと**。ここでマウスへ入れた解放・押下は
    /// 「次フレームの瞬間フラグ」として見える必要があるため（先に呼ぶと即座に消される）。
    pub fn end_frame(&mut self, mouse: &mut MouseState, touch: &mut TouchState) {
        touch.end_frame();
        self.sync_mouse_from_touch(mouse, touch);
    }

    // ─── 内部 ──────────────────────────────────────────────

    /// 実マウスの入力を無視すべきか（タッチがポインタを握っている）。
    ///
    /// touch_drives_mouse の端末でだけ真になり得る。そうした端末ではマウスからの合成指が
    /// 存在しない（方針が排他）ので、`down_count` はすべて実タッチの本数になる。
    fn touch_owns_pointer(&self, touch: &TouchState) -> bool {
        self.policy.touch_drives_mouse && (self.held_by.is_some() || touch.down_count() > 0)
    }

    /// 指0 の見えている状態へ MouseState を合わせる（touch_drives_mouse のときだけ）。
    ///
    /// 何度呼んでも同じ結果になる（冪等）。イベントのたびと end_frame のたびに呼ぶ。
    fn sync_mouse_from_touch(&mut self, mouse: &mut MouseState, touch: &TouchState) {
        if !self.policy.touch_drives_mouse {
            return;
        }
        // マウスから合成した指は駆動元にしない（方針が排他なので通常は現れない。自己ループ防止の保険）。
        let primary = touch
            .primary()
            .filter(|p| p.raw_id != MOUSE_FINGER_RAW_ID);

        // 位置: 指0 が一覧にいる間は常にその位置へ（離れたフレームも最後の位置に置く）。
        if let Some(p) = primary {
            mouse.process_cursor_moved(p.position.x, p.position.y);
        }

        // 押下: 指0 が「触れている」段階として見えている間だけ押す。
        let want = primary.filter(|p| p.down).map(|p| p.raw_id);
        if self.held_by.is_some() && self.held_by != want {
            // 指0 が離れた（または別の指0 へ入れ替わった）ので解放する。
            mouse.process_button(POINTER_BUTTON, false);
            self.held_by = None;
        }
        // 同じフレームに解放が起きていたら押下は次フレームへ回す（押下と解放を 1 フレームに潰さない）。
        // 回した押下は end_frame（mouse.end_frame の後）の同期で入る。
        if want.is_some() && self.held_by.is_none() && !mouse.is_release(POINTER_BUTTON) {
            mouse.process_button(POINTER_BUTTON, true);
            self.held_by = want;
        }
    }
}

// ============================================================
//  テスト（タッチ → マウス駆動 / マウス → タッチ合成 / 排他）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::input::touch::TouchPhase;

    /// Android と同じ方針（指0 がマウスを駆動する）。
    const TOUCH_DRIVES: PointerBridgePolicy = PointerBridgePolicy {
        touch_drives_mouse: true,
        mouse_simulates_touch: false,
    };
    /// デスクトップと同じ方針（マウス左ボタンで指を合成する）。
    const MOUSE_SIMULATES: PointerBridgePolicy = PointerBridgePolicy {
        touch_drives_mouse: false,
        mouse_simulates_touch: true,
    };

    /// 1 フレーム分を処理する組（Input::end_frame と同じ順序でフレームを閉じる）。
    struct Rig {
        bridge: PointerBridge,
        mouse: MouseState,
        touch: TouchState,
    }

    impl Rig {
        fn new(policy: PointerBridgePolicy) -> Self {
            Self { bridge: PointerBridge::new(policy), mouse: MouseState::new(), touch: TouchState::new() }
        }
        fn touch(&mut self, raw_id: u64, kind: RawTouchPhase, x: f32, y: f32) {
            self.bridge.on_touch(raw_id, kind, Vector2::new(x, y), &mut self.mouse, &mut self.touch);
        }
        fn cursor(&mut self, x: f32, y: f32) {
            self.bridge.on_cursor_moved(Vector2::new(x, y), &mut self.mouse, &mut self.touch);
        }
        fn button(&mut self, pressed: bool) {
            self.bridge.on_mouse_button(MouseButton::Left, pressed, &mut self.mouse, &mut self.touch);
        }
        /// フレームを閉じる（Input::end_frame と同じく mouse.end_frame の後に bridge.end_frame）。
        fn end_frame(&mut self) {
            self.mouse.end_frame();
            self.bridge.end_frame(&mut self.mouse, &mut self.touch);
        }
        /// 左ボタンの (押下中, 押した瞬間, 離した瞬間)。
        fn left(&self) -> (bool, bool, bool) {
            (
                self.mouse.is_press(MouseButton::Left),
                self.mouse.is_trigger(MouseButton::Left),
                self.mouse.is_release(MouseButton::Left),
            )
        }
        fn mouse_pos(&self) -> (f32, f32) {
            let p = self.mouse.position();
            (p.x, p.y)
        }
        fn phases(&self) -> Vec<TouchPhase> {
            self.touch.iter().map(|t| t.phase).collect()
        }
    }

    // ─── タッチ → マウス ───────────────────────────────────

    /// 指0 の触れ始め → 移動 → 離れがカーソル座標と左ボタンになる。
    #[test]
    fn primary_finger_drives_mouse() {
        let mut r = Rig::new(TOUCH_DRIVES);
        r.touch(0, RawTouchPhase::Started, 100.0, 200.0);
        assert_eq!(r.mouse_pos(), (100.0, 200.0));
        assert_eq!(r.left(), (true, true, false), "触れた瞬間に左ボタン押下");

        r.end_frame();
        r.touch(0, RawTouchPhase::Moved, 120.0, 210.0);
        assert_eq!(r.mouse_pos(), (120.0, 210.0));
        assert_eq!(r.left(), (true, false, false));
        let d = r.mouse.position_delta();
        assert_eq!((d.x, d.y), (20.0, 10.0), "Input.MouseDelta もタッチで動く");

        r.end_frame();
        r.touch(0, RawTouchPhase::Ended, 125.0, 210.0);
        assert_eq!(r.mouse_pos(), (125.0, 210.0));
        assert_eq!(r.left(), (false, false, true), "離れた瞬間に左ボタン解放");
        r.end_frame();
        assert_eq!(r.left(), (false, false, false));
        assert_eq!(r.mouse_pos(), (125.0, 210.0), "離した後も最後の位置に残る");
    }

    /// 同じフレームで触れて離れたタップは「押下フレーム → 次フレームで解放」になる。
    #[test]
    fn quick_tap_presses_then_releases_next_frame() {
        let mut r = Rig::new(TOUCH_DRIVES);
        r.touch(0, RawTouchPhase::Started, 10.0, 10.0);
        r.touch(0, RawTouchPhase::Ended, 10.0, 10.0);
        assert_eq!(r.left(), (true, true, false), "今フレームは押下として見える");
        assert_eq!(r.phases(), vec![TouchPhase::Began]);

        r.end_frame();
        assert_eq!(r.left(), (false, false, true), "次フレームで解放");
        assert_eq!(r.phases(), vec![TouchPhase::Ended]);

        r.end_frame();
        assert_eq!(r.left(), (false, false, false));
        assert!(r.phases().is_empty());
    }

    /// 2 本目以降の指はマウスに影響しない。指0 が離れたら、残った指は指0 を引き継がない。
    #[test]
    fn secondary_fingers_do_not_affect_mouse() {
        let mut r = Rig::new(TOUCH_DRIVES);
        r.touch(0, RawTouchPhase::Started, 10.0, 10.0);
        r.touch(1, RawTouchPhase::Started, 500.0, 500.0);
        assert_eq!(r.mouse_pos(), (10.0, 10.0));
        r.end_frame();
        r.touch(1, RawTouchPhase::Moved, 520.0, 480.0);
        assert_eq!(r.mouse_pos(), (10.0, 10.0), "2 本目の移動はカーソルを動かさない");
        assert_eq!(r.phases(), vec![TouchPhase::Stationary, TouchPhase::Moved]);

        // 指0 が離れる → 左ボタン解放。2 本目は触れたままでもマウスは押されない
        r.end_frame();
        r.touch(0, RawTouchPhase::Ended, 10.0, 10.0);
        assert_eq!(r.left(), (false, false, true));
        r.end_frame();
        r.touch(1, RawTouchPhase::Moved, 600.0, 600.0);
        assert_eq!(r.left(), (false, false, false));
        assert_eq!(r.mouse_pos(), (10.0, 10.0));
        assert_eq!(r.touch.count(), 1, "2 本目はタッチとしては追跡され続ける");
    }

    /// 取り消し（Cancelled）でも左ボタンは解放される。
    #[test]
    fn cancel_releases_mouse() {
        let mut r = Rig::new(TOUCH_DRIVES);
        r.touch(0, RawTouchPhase::Started, 1.0, 1.0);
        r.end_frame();
        r.touch(0, RawTouchPhase::Cancelled, 1.0, 1.0);
        assert_eq!(r.left(), (false, false, true));
        assert_eq!(r.phases(), vec![TouchPhase::Canceled]);
    }

    /// 安全弁（フォーカス喪失）: 実タッチを取り消し、タッチ由来の押下を解く。
    #[test]
    fn cancel_real_touches_releases_mouse() {
        let mut r = Rig::new(TOUCH_DRIVES);
        r.touch(0, RawTouchPhase::Started, 1.0, 1.0);
        r.touch(1, RawTouchPhase::Started, 2.0, 2.0);
        r.end_frame();
        r.bridge.cancel_real_touches(&mut r.mouse, &mut r.touch);
        assert_eq!(r.left(), (false, false, true));
        assert_eq!(r.phases(), vec![TouchPhase::Canceled, TouchPhase::Canceled]);
        r.end_frame();
        assert_eq!(r.touch.count(), 0);
    }

    /// タッチがポインタを握っている間、実マウスのカーソル移動・左ボタンは無視される（二重駆動しない）。
    #[test]
    fn real_mouse_is_ignored_while_touch_owns_pointer() {
        let mut r = Rig::new(TOUCH_DRIVES);
        r.touch(0, RawTouchPhase::Started, 50.0, 50.0);
        r.cursor(900.0, 900.0);
        r.button(false);
        assert_eq!(r.mouse_pos(), (50.0, 50.0), "実マウスの移動は無視");
        assert_eq!(r.left(), (true, true, false), "実マウスの解放も無視");

        // 右ボタンはタッチと無関係なので通る
        r.bridge.on_mouse_button(MouseButton::Right, true, &mut r.mouse, &mut r.touch);
        assert!(r.mouse.is_press(MouseButton::Right));

        // 指が離れて解放が済めば、実マウスは再び効く
        r.end_frame();
        r.touch(0, RawTouchPhase::Ended, 50.0, 50.0);
        r.end_frame();
        r.cursor(300.0, 300.0);
        assert_eq!(r.mouse_pos(), (300.0, 300.0));
        assert!(r.touch.count() == 0, "touch_drives_mouse ではマウスから指を合成しない");
        r.button(true);
        assert!(r.touch.count() == 0);
    }

    /// 指0 が入れ替わるとき、解放と押下は同じフレームに潰さず、押下は次フレームへ回る。
    #[test]
    fn primary_switch_does_not_collapse_release_and_press() {
        let mut r = Rig::new(TOUCH_DRIVES);
        r.touch(0, RawTouchPhase::Started, 1.0, 1.0);
        r.end_frame();
        // 今フレーム: 指0 が離れ、同じフレームで別の指が単独で触れる
        r.touch(0, RawTouchPhase::Ended, 1.0, 1.0);
        r.touch(1, RawTouchPhase::Started, 40.0, 40.0);
        assert_eq!(r.left(), (false, false, true), "今フレームは旧指0 の解放だけ");
        assert_eq!(r.mouse_pos(), (40.0, 40.0), "位置は新しい指0");

        r.end_frame();
        assert_eq!(r.left(), (true, true, false), "新しい指0 の押下は次フレーム");
        assert_eq!(r.phases(), vec![TouchPhase::Stationary], "旧指0 は一覧から消えている");
    }

    // ─── マウス → タッチ ───────────────────────────────────

    /// 左ボタンで指を合成する: 押下 → Began、押下中の移動 → Moved、止まれば Stationary、離す → Ended。
    #[test]
    fn mouse_left_button_simulates_a_finger() {
        let mut r = Rig::new(MOUSE_SIMULATES);
        r.cursor(10.0, 10.0);
        assert_eq!(r.touch.count(), 0, "ボタンを押していない移動では指を作らない");

        r.button(true);
        let t = r.touch.get(0).unwrap();
        assert_eq!((t.finger_id, t.phase), (0, TouchPhase::Began));
        assert_eq!((t.position.x, t.position.y), (10.0, 10.0));
        assert_eq!(r.left(), (true, true, false), "マウス自体の状態は従来どおり");

        r.end_frame();
        r.cursor(30.0, 25.0);
        let t = r.touch.get(0).unwrap();
        assert_eq!(t.phase, TouchPhase::Moved);
        assert_eq!((t.delta.x, t.delta.y), (20.0, 15.0));

        r.end_frame();
        assert_eq!(r.phases(), vec![TouchPhase::Stationary]);

        r.end_frame();
        r.button(false);
        assert_eq!(r.phases(), vec![TouchPhase::Ended]);
        assert_eq!(r.left(), (false, false, true));
        r.end_frame();
        assert_eq!(r.touch.count(), 0);
    }

    /// 右・中ボタンでは指を合成しない。
    #[test]
    fn other_buttons_do_not_simulate_fingers() {
        let mut r = Rig::new(MOUSE_SIMULATES);
        r.bridge.on_mouse_button(MouseButton::Right, true, &mut r.mouse, &mut r.touch);
        r.bridge.on_mouse_button(MouseButton::Middle, true, &mut r.mouse, &mut r.touch);
        assert_eq!(r.touch.count(), 0);
        assert!(r.mouse.is_press(MouseButton::Right));
    }

    /// 実タッチ（タッチパネル付き PC）が触れたら合成中の指は Canceled で畳み、触れている間は合成しない。
    #[test]
    fn real_touch_wins_over_simulated_finger() {
        let mut r = Rig::new(MOUSE_SIMULATES);
        r.cursor(5.0, 5.0);
        r.button(true);
        r.end_frame();
        r.touch(42, RawTouchPhase::Started, 70.0, 70.0);
        assert_eq!(r.phases(), vec![TouchPhase::Canceled, TouchPhase::Began]);
        assert_eq!(r.mouse_pos(), (5.0, 5.0), "デスクトップではタッチがマウスを動かさない");

        // OS がタッチを昇格したマウス押下が届いても 2 本目は作らない
        r.button(false);
        r.button(true);
        r.end_frame();
        assert_eq!(r.touch.count(), 1);
        assert_eq!(r.phases(), vec![TouchPhase::Stationary]);
    }

    /// 合成した指は mouse.end_frame → bridge.end_frame の順でも 1 フレームずつ正しく進む
    /// （同じフレーム内の押下・解放は Began → 次フレーム Ended）。
    #[test]
    fn quick_click_is_seen_as_began_then_ended() {
        let mut r = Rig::new(MOUSE_SIMULATES);
        r.button(true);
        r.button(false);
        assert_eq!(r.phases(), vec![TouchPhase::Began]);
        r.end_frame();
        assert_eq!(r.phases(), vec![TouchPhase::Ended]);
        r.end_frame();
        assert!(r.phases().is_empty());
    }

    /// デスクトップ方針ではマウスの状態は従来と完全に同じ（合成しても MouseState へは書き戻さない）。
    #[test]
    fn desktop_mouse_behaviour_is_unchanged() {
        let mut bridged = Rig::new(MOUSE_SIMULATES);
        let mut plain = MouseState::new();
        let script: [(Option<(f32, f32)>, Option<bool>); 6] = [
            (Some((1.0, 2.0)), None),
            (None, Some(true)),
            (Some((5.0, 6.0)), None),
            (None, Some(false)),
            (Some((7.0, 8.0)), None),
            (None, Some(true)),
        ];
        for (moved, button) in script {
            if let Some((x, y)) = moved {
                bridged.cursor(x, y);
                plain.process_cursor_moved(x, y);
            }
            if let Some(pressed) = button {
                bridged.button(pressed);
                plain.process_button(MouseButton::Left, pressed);
            }
            let a = bridged.left();
            let b = (plain.is_press(MouseButton::Left), plain.is_trigger(MouseButton::Left), plain.is_release(MouseButton::Left));
            assert_eq!(a, b);
            assert_eq!(bridged.mouse_pos(), (plain.position().x, plain.position().y));
            bridged.end_frame();
            plain.end_frame();
        }
    }
}
