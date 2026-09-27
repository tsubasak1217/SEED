pub mod action_map;
pub mod cursor_visibility;
pub mod gamepad;
/// ジェスチャーアリーナ（タップ・長押し・ドラッグ・フリック・押下の取り消し・指ごとの捕捉。W2-2）
pub mod gesture;
pub mod inject;
/// OS 固有のキー（Android の戻るキー等）をエンジンの KeyCode へ置き換える表（platform::PlatformTraits::key_remap が選ぶ）。
pub mod key_remap;
pub mod keyboard;
pub mod mouse;
pub mod raw_input;
pub mod touch;

pub use raw_input::RawInput;
pub use inject::{InjectAction, InjectCommand, InjectTickOutcome, InputInjection, InputSequencePlayer};

use gamepad::GamepadState;

use std::time::Instant;

use winit::dpi::PhysicalPosition;
use winit::event::{MouseButton, MouseScrollDelta, TouchPhase as RawTouchPhase};
use winit::keyboard::KeyCode;
use winit::window::Window;

use crate::engine::platform;
use crate::engine::structs::tensor::Vector2;
use gesture::pointer_log::{
    pointer_clock_now, pointer_clock_secs, InjectedPointerTracker, PointerEventLog, PointerLogEntry, PointerPhase,
    INJECTED_POINTER_KEY,
};
use keyboard::KeyboardState;
use mouse::MouseState;
use touch::{PointerBridge, PointerBridgePolicy, TouchPoint, TouchState};

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

// ─── ViewMap ───────────────────────────────────────────────────────────────

/// ウィンドウ実サイズ → 描画解像度 の写像パラメータ。
///
/// 内部解像度固定（`RenderResolutionMode::Fixed`）のときだけ `Input` に設定される。
/// これがあると `CursorMoved` のウィンドウ座標は、描画と同じレターボックス写像を
/// 通してから `MouseState` へ入る。結果として `Input::mouse_position` は
/// **常に描画ターゲット座標系**を返す（UI のヒット判定・スクリプトの
/// `Input.MousePos` が見た目と一致する）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ViewMap {
    /// ウィンドウ（クライアント領域）の実ピクセルサイズ。
    pub window: (u32, u32),
    /// 描画に使う内部解像度。
    pub internal: (u32, u32),
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
    /// 複数指のタッチ状態（`touch/state.rs`）。スクリプトの `Input.TouchCount` / `GetTouch` の源。
    touch: TouchState,
    /// マウス ⇔ タッチの相互変換（`touch/bridge.rs`）。実マウス・実タッチのイベントは必ずここを通し、
    /// どちらの入力源が「左ボタン＝指0」を握るかをここだけで決める（二重駆動の防止）。
    pointer_bridge: PointerBridge,
    /// ゲームパッド状態（gilrs バックエンド）。毎フレーム `update_gamepad` でポンプする。
    gamepad: GamepadState,
    /// 外部（エディタ／MCP 経由の AI）から注入された入力。
    ///
    /// 実入力とは**別に**持ち、各クエリで OR 合成する。混ぜずに分けて持つのは
    /// 「実入力の挙動をひとつも変えない」ことと「注入だけを一括解放できる」ことを
    /// 同時に満たすため（詳細は inject モジュール）。
    injection: InputInjection,
    /// 時刻つきの指のイベントの記録（W2-2 のジェスチャーアリーナが読む。gesture/pointer_log.rs）。
    ///
    /// 実タッチ・マウスの合成の指（PointerBridge の控え）・注入の指・全部の取り消しを、受け取った時刻つきで積む。
    /// ジェスチャーの処理（App の update_gestures）が取り出し、取り出されなかった分は end_frame で捨てる。
    /// TouchState・MouseState・注入の状態には一切触れない（従来の入力の読み手の値は変わらない）。
    pointer_log: PointerEventLog,
    /// 注入のマウスの左ボタンと座標を 1 本の指のイベントへ直す追跡（W2-2）。
    injected_pointer: InjectedPointerTracker,
    /// ウィンドウ実サイズ → 描画解像度 の写像。`None` = 等倍（従来動作）。
    ///
    /// 写像の**唯一の所有者**がここであることが重要。App 側にも同じ情報を持たせると、
    /// 片方だけ更新し忘れた瞬間に「見た目とクリック位置がズレる」不具合になる。
    view_map: Option<ViewMap>,
    is_active: bool,
}

impl Input {
    /// このビルドのプラットフォーム特性（`platform::CURRENT`）に従って作る。
    pub fn new() -> Self {
        Self::with_pointer_policy(PointerBridgePolicy::from_traits(&platform::CURRENT))
    }

    /// マウス ⇔ タッチの相互変換方針を指定して作る（本番は `new`。テストで Android の方針を試すため）。
    pub fn with_pointer_policy(policy: PointerBridgePolicy) -> Self {
        Self {
            keyboard: KeyboardState::new(),
            mouse: MouseState::new(),
            touch: TouchState::new(),
            pointer_bridge: PointerBridge::new(policy),
            gamepad: GamepadState::new(),
            injection: InputInjection::new(),
            pointer_log: PointerEventLog::new(),
            injected_pointer: InjectedPointerTracker::default(),
            view_map: None,
            is_active: true,
        }
    }

    // ─── 座標系の写像（内部解像度固定モード）──────────────────

    /// ウィンドウ実サイズ → 描画解像度 の写像を設定する。
    ///
    /// `None` で従来動作（等倍）。ウィンドウサイズが変わるたびに設定し直すこと
    /// （App 側は `sync_input_view_map` が唯一の呼び出し口）。
    pub fn set_view_map(&mut self, map: Option<ViewMap>) {
        self.view_map = map;
    }

    /// ウィンドウ座標を入力座標（＝描画ターゲット座標）へ写す。
    ///
    /// 戻り値 `.1` は「レターボックスの映像部分の内側か」。写像が無いときは常に true。
    /// 座標はクランプしない（黒帯の上は枠外の値のまま返る）。
    fn window_pos_to_input(&self, pos: [f32; 2]) -> ([f32; 2], bool) {
        match self.view_map {
            Some(m) => {
                crate::engine::core::renderer::letterbox::window_to_internal(
                    pos, m.window, m.internal,
                )
            }
            None => (pos, true),
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
    ///
    /// 相互変換（`touch/bridge.rs`）を通す。デスクトップではマウスの状態は従来と同一で、
    /// 左ボタンで指を 1 本合成するだけ。Android（指0 がマウスを駆動する端末）では、
    /// タッチがポインタを握っている間の実マウスの左ボタンは無視される（二重駆動の防止）。
    pub fn process_mouse_button(&mut self, button: MouseButton, pressed: bool) {
        if self.is_active {
            self.pointer_bridge
                .on_mouse_button(button, pressed, &mut self.mouse, &mut self.touch);
            self.journal_finger_events();
        }
    }

    /// `DeviceEvent::MouseMotion` を処理する（生の相対移動量）。
    pub fn process_mouse_motion(&mut self, dx: f64, dy: f64) {
        if self.is_active {
            self.mouse.process_motion(dx, dy);
        }
    }

    /// `WindowEvent::CursorMoved` を処理する（ウィンドウのクライアント座標）。
    ///
    /// 内部解像度固定モード（`view_map` が Some）では、ここで描画解像度座標へ写す。
    /// **この 1 か所で変換を済ませる**ことで、`MouseState` 以降（`mouse_position` /
    /// `position_delta` / UI ヒット判定 / スクリプト API）はすべて描画ターゲット座標系で
    /// 統一される。`view_map` が None のときは恒等写像なので従来と完全に同一。
    pub fn process_cursor_moved(&mut self, x: f32, y: f32) {
        if self.is_active {
            let ([mx, my], _inside) = self.window_pos_to_input([x, y]);
            // 相互変換を通す（押下中なら合成した指も動かす。Android でタッチが握っている間は無視）。
            self.pointer_bridge.on_cursor_moved(
                Vector2::new(mx, my),
                &mut self.mouse,
                &mut self.touch,
            );
            self.journal_finger_events();
        }
    }

    /// `WindowEvent::Touch` を処理する（ウィンドウのクライアント座標・物理ピクセル）。
    ///
    /// 座標は `process_cursor_moved` と同じ写像（内部解像度固定モードのレターボックス）を通すので、
    /// タッチの位置は `mouse_position` と同じ単位・原点（描画ターゲットの左上原点ピクセル）になる。
    /// 指0 がマウスを駆動する端末（Android）では、ここで MouseState も更新される。
    ///
    /// # 引数
    /// - `raw_id` … OS が付けた指の ID（winit の `Touch::id`）
    /// - `phase`  … winit のタッチイベント種別（Started / Moved / Ended / Cancelled）
    /// - `x`, `y` … ウィンドウのクライアント座標（物理ピクセル）
    pub fn process_touch(&mut self, raw_id: u64, phase: RawTouchPhase, x: f32, y: f32) {
        if self.is_active {
            let ([tx, ty], _inside) = self.window_pos_to_input([x, y]);
            self.pointer_bridge.on_touch(
                raw_id,
                phase,
                Vector2::new(tx, ty),
                &mut self.mouse,
                &mut self.touch,
            );
            self.journal_finger_events();
        }
    }

    /// 実タッチの指をすべて取り消す（フォーカス喪失時の安全弁）。
    ///
    /// OS の離れ・取り消しが届かないまま指（とタッチ由来の左ボタン押下）が残り続けるのを防ぐ。
    /// マウスから合成した指は残す（マウスの左ボタン自体もフォーカス喪失では解除しない従来動作に揃える）。
    pub fn cancel_touches(&mut self) {
        self.pointer_bridge
            .cancel_real_touches(&mut self.mouse, &mut self.touch);
        // ジェスチャーはマウスの合成の指も含めてすべて取り消す（押下の見た目を戻す。W2-2）
        self.pointer_log.record_cancel_all(pointer_clock_now());
    }

    /// ジェスチャーの指だけをすべて取り消す（アプリが背面へ回った。W2-2）。
    ///
    /// TouchState・MouseState には触れない（従来の振る舞いを変えない）。記録に「全部の取り消し」を積むだけで、
    /// 次に動いたフレーム（前面へ戻った最初のフレーム）でアリーナが PressCancel・DragEnd（取り消し）を配る。
    pub fn cancel_gesture_pointers(&mut self) {
        self.pointer_log.record_cancel_all(pointer_clock_now());
    }

    // ─── ジェスチャーの記録（W2-2）──────────────────────────

    /// 積んだ時刻つきの指のイベントを時刻の順に取り出す（ジェスチャーの処理がフレームに 1 回呼ぶ）。
    pub fn take_pointer_events(&mut self) -> Vec<PointerLogEntry> {
        self.pointer_log.take()
    }

    /// PointerBridge が TouchState へ入れた指の単位のイベントを、今の時刻で記録へ移す。
    fn journal_finger_events(&mut self) {
        let events = self.pointer_bridge.take_journal();
        if events.is_empty() {
            return;
        }
        let time = pointer_clock_now();
        for e in events {
            let phase = match e.kind {
                RawTouchPhase::Started => PointerPhase::Down,
                RawTouchPhase::Moved => PointerPhase::Move,
                RawTouchPhase::Ended => PointerPhase::Up,
                RawTouchPhase::Cancelled => PointerPhase::Cancel,
            };
            self.pointer_log.record(e.raw_id, phase, [e.position.x, e.position.y], time);
        }
    }

    /// 注入の操作を当てた直後の状態から、注入の指のイベントを記録へ積む（位置は注入の座標、無ければ実カーソル）。
    fn journal_injected_pointer(&mut self) {
        for snap in self.injection.take_pointer_snapshots() {
            let p = snap.position.unwrap_or_else(|| self.mouse.position());
            if let Some((phase, at)) = self.injected_pointer.observe(snap.left_held, [p.x, p.y]) {
                self.pointer_log.record(INJECTED_POINTER_KEY, phase, at, pointer_clock_secs(snap.at));
            }
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
        // タッチの段階を進める。**mouse.end_frame の後**に呼ぶこと: 次フレームで見せる
        // タッチ由来の左ボタンの解放・押下（素早いタップの解放など）をここで入れるため。
        self.pointer_bridge
            .end_frame(&mut self.mouse, &mut self.touch);
        // 控えは各イベントの直後に記録へ移しているので通常は空（念のため空にする。TouchState の end_frame が
        // 次フレームへ回したイベントを入れ直すのは控えを通らない＝既に記録済みの指を二重に記録しない）。
        // 取り出されなかったジェスチャーの記録（Edit・一時停止のフレーム）も捨てる（W2-2）
        self.pointer_bridge.take_journal();
        self.pointer_log.clear();
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

    /// マウスのボタン・指のどれかを押している（触れている）か、注入でキー・ボタンを押しているか。
    ///
    /// 描画を止める判定（render_policy の on_demand。W2-10a）の「押している間は描き続ける」に使う。
    /// 押しっぱなしを毎フレーム読むスクリプト（長押しでためる等）が、指を動かさない間も Update を受けられるようにする。
    /// フォーカスが無い間（`is_active` が false）は押していない扱い（スクリプトの判定と同じ）。
    ///
    /// **実キーボードのキーは数えない**: Windows の日本語キーボードの「半角/全角」キーは押下だけが届いて離しが届かず、
    /// winit の Backquote が押されたまま残る（2026-09-28 に PC で確かめた。触っていないのに keys={Backquote}）。
    /// 数えると on_demand が永久に止まらない。キーを押している間は OS の自動の繰り返し（KeyboardInput）が届くので、
    /// それが入力の理由として描画を起こす（最初の繰り返しまでの約 0.5 秒は止まりうる）。
    pub fn is_any_input_held(&self) -> bool {
        self.is_active
            && (self.mouse.is_press_any()
                || self.touch.down_count() > 0
                || self.injection.state().any_key_held()
                || self.injection.state().any_button_held())
    }

    /// 押している入力の内訳（検証用のログ。W2-10a の SEED_REDRAW_LOG で「押している」が理由になったときに出す）。
    pub fn held_input_summary(&self) -> String {
        format!(
            "keys={} mouse={} touches_down={} injected_keys={} injected_buttons={}",
            self.keyboard.held_debug(),
            self.mouse.held_debug(),
            self.touch.down_count(),
            self.injection.state().any_key_held(),
            self.injection.state().any_button_held(),
        )
    }

    /// 注入の入力の列（`INPUT_SEQUENCE`）を再生中か（予定の時刻の操作を落とさないために描き続ける判定。W2-10a）。
    pub fn is_injected_sequence_playing(&self) -> bool {
        self.injection.is_sequence_playing()
    }

    // ─── タッチ API ────────────────────────────────────────────

    /// このフレームの指の本数（スクリプトの `Input.TouchCount`）。
    ///
    /// このフレームで離れた指（Ended / Canceled）も含む。デスクトップでは左ボタン押下中に 1 になる
    /// （マウスから合成した指）。
    pub fn touch_count(&self) -> usize {
        self.touch.count()
    }

    /// このフレームの `index` 番目の指（触れ始めた順。スクリプトの `Input.GetTouch`）。範囲外は None。
    pub fn touch(&self, index: usize) -> Option<TouchPoint> {
        self.touch.get(index)
    }

    /// このフレームの指を触れ始めた順に列挙する（診断ログ用）。
    pub fn touches(&self) -> impl Iterator<Item = TouchPoint> + '_ {
        self.touch.iter()
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
    ///
    /// 【非表示の掛け方】
    /// winit の `set_cursor_visible` だけでは、カーソルがクライアント領域外だと
    /// winit 自身が `ShowCursor(TRUE)` を呼んで表示へ戻してしまう（`cursor_visibility`
    /// のコメント参照）。そこで winit へフラグを伝えたうえで、OS の表示カウンタも
    /// `cursor_visibility` で直接「確実に負」へ押し下げる。
    /// 解除時は必ずカウンタを 0 以上へ戻す（隠れたまま操作不能になるのを防ぐ）。
    pub fn set_cursor_lock(&mut self, locked: bool, window: &Window) {
        let was = self.mouse.cursor_locked();
        let (win_center, input_center) = self.lock_centers(window);
        self.mouse.set_cursor_lock(locked, input_center);

        // 可視状態は OS へも反映する（ロック中は隠す / 解除で必ず戻す）。
        self.mouse.set_cursor_visible(!locked);
        window.set_cursor_visible(!locked);

        if locked {
            // ロックし始めたフレームで一度中央へ寄せておく（以降は update_cursor_lock）。
            if !was {
                self.warp_to_lock_center(window, win_center, input_center);
            }
            cursor_visibility::force_hidden();
        } else {
            // 解除は「ロック中だったか」に関わらず必ず表示へ戻す。
            // 直前の状態を信用して分岐すると、フラグの取りこぼしで
            // カーソルが消えたまま残る事故（＝操作不能）になるため。
            cursor_visibility::force_shown();
        }
    }

    /// カーソルロックの基準点を「ウィンドウ座標」と「入力座標」の 2 系統で返す。
    ///
    /// 【なぜ 2 つの座標系が要るのか】
    /// - `Window::set_cursor_position` は **OS のウィンドウ座標**（クライアント領域の
    ///   実ピクセル）を要求する。ここへ内部解像度の値を渡すと、実際のカーソルが
    ///   画面の意図しない位置へ飛ぶ。
    /// - 一方 `MouseState` の `lock_center` は `position_delta()`（ロック中は
    ///   `position - lock_center`）の基準であり、`position` は
    ///   `process_cursor_moved` が写像済み＝**入力座標（描画解像度）**で入っている。
    ///   ここへウィンドウ座標を渡すと 2 項が別座標系になり、fixed モードで
    ///   カーソルロック中の視点回転が定常的にドリフトする。
    ///
    /// 3 か所（`set_cursor_lock` / `update_cursor_lock` / `warp_to_lock_center`）で
    /// 基準がズレないよう、導出はこのヘルパー 1 つに集約する。
    /// window モード（`view_map` = None）では両者は完全に同じ値になる。
    fn lock_centers(&self, window: &Window) -> (Vector2<f32>, Vector2<f32>) {
        let win_center = viewport_center(window);
        let ([ix, iy], _inside) = self.window_pos_to_input([win_center.x, win_center.y]);
        (win_center, Vector2::new(ix, iy))
    }

    /// フレーム末に呼ぶ。ロック中ならカーソルを隠し直し、ビューポート中央へ戻す。
    ///
    /// 「スクリプトが今フレームの差分を読み終えたあと」に呼ぶこと。
    /// 呼ぶ順序を誤ると、戻した直後の中央座標を差分計算に使ってしまう。
    ///
    /// 【毎フレーム掛け直す理由】
    /// カーソルの表示状態は winit / DefWindowProc / 他プロセスなど複数の主体が触る
    /// 共有リソースで、「1 回隠したら隠れたまま」という前提が成り立たない
    /// （フォーカス変化・WM_SETCURSOR・領域外判定などで表示へ戻される）。
    /// ロック中は状態を観測せず毎フレーム無条件に掛け直し、収束させる。
    /// 冪等なので追加コストは実質ゼロ（すでに隠れていれば OS 呼び出しは発生しない）。
    pub fn update_cursor_lock(&mut self, window: &Window) {
        if !self.mouse.cursor_locked() {
            return;
        }
        // winit の内部フラグも毎フレーム押し直す（先に winit → 後から OS カウンタ）。
        // 逆順にすると、winit が領域外判定で表示へ戻した結果が最後に残ってしまう。
        window.set_cursor_visible(false);
        cursor_visibility::force_hidden();

        let (win_center, input_center) = self.lock_centers(window);
        self.warp_to_lock_center(window, win_center, input_center);
    }

    /// カーソルを中央へワープさせ、成功したときだけ入力状態へ反映する。
    ///
    /// `win_center` は OS へ渡すウィンドウ座標、`input_center` は入力状態へ記録する
    /// 入力座標（＝描画解像度座標）。2 つに分ける理由は `lock_centers` の説明を参照。
    ///
    /// 失敗（プラットフォーム未対応など）しても機能を落とすだけなので握り潰すが、
    /// 原因調査のために初回だけ警告を出す。
    fn warp_to_lock_center(
        &mut self,
        window: &Window,
        win_center: Vector2<f32>,
        input_center: Vector2<f32>,
    ) {
        match window.set_cursor_position(PhysicalPosition::new(win_center.x, win_center.y)) {
            Ok(()) => self.mouse.notify_warped_to_center(input_center),
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
        self.journal_injected_pointer();
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
        self.journal_injected_pointer();
    }

    /// 毎フレーム 1 回、IPC 処理の直後に呼ぶ。シーケンスを実時間で進める。
    ///
    /// `playing` が false へ落ちた瞬間に注入中の押下を自動解放する
    /// （Play を止めたのに押しっぱなしが残らないようにする安全弁）。
    /// `paused` の間はシーケンスの時計だけを止め、押下はそのまま保持する。
    pub fn tick_injection(&mut self, playing: bool, paused: bool) -> InjectTickOutcome {
        let outcome = self.injection.tick(playing, paused, Instant::now());
        self.journal_injected_pointer();
        outcome
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

    // ─── タッチ（input/touch との結線）─────────────────────────

    use touch::TouchPhase;

    /// Android と同じ方針（指0 がマウスを駆動する）。
    const ANDROID_POLICY: PointerBridgePolicy = PointerBridgePolicy {
        touch_drives_mouse: true,
        mouse_simulates_touch: false,
    };

    /// タッチの座標はカーソルと同じ写像（内部解像度固定のレターボックス）を通る。
    #[test]
    fn touch_position_uses_the_same_mapping_as_cursor() {
        let mut input = Input::new();
        // 横長ウィンドウに正方形の内部解像度 → 左右に黒帯が付く（非自明な写像）。
        input.set_view_map(Some(ViewMap { window: (800, 400), internal: (400, 400) }));
        input.process_cursor_moved(300.0, 100.0);
        let m = input.mouse_position(InputState::Current);

        input.process_touch(5, RawTouchPhase::Started, 300.0, 100.0);
        let t = input.touch(0).expect("指が 1 本載る");
        assert_eq!((t.position.x, t.position.y), (m.x, m.y), "マウスと同じ単位・原点");
        assert_ne!((t.position.x, t.position.y), (300.0, 100.0), "写像が掛かっている");
    }

    /// デスクトップ（テストを走らせるホスト）ではマウス左ボタンが指を 1 本合成する。
    #[test]
    fn desktop_mouse_left_button_simulates_touch() {
        let mut input = Input::new();
        input.process_cursor_moved(10.0, 20.0);
        assert_eq!(input.touch_count(), 0);
        input.process_mouse_button(MouseButton::Left, true);
        assert_eq!(input.touch_count(), 1);
        assert_eq!(input.touch(0).unwrap().phase, TouchPhase::Began);
        assert!(input.is_press_mouse(MouseButton::Left), "マウスの状態は従来どおり");

        input.end_frame();
        input.process_cursor_moved(15.0, 20.0);
        let t = input.touch(0).unwrap();
        assert_eq!(t.phase, TouchPhase::Moved);
        assert_eq!((t.delta.x, t.delta.y), (5.0, 0.0));

        input.end_frame();
        input.process_mouse_button(MouseButton::Left, false);
        assert_eq!(input.touch(0).unwrap().phase, TouchPhase::Ended);
        input.end_frame();
        assert_eq!(input.touch_count(), 0);
        assert!(input.touch(0).is_none(), "範囲外は None");
    }

    /// Android の方針: 指0 がマウスの座標・左ボタンを駆動し、素早いタップは 2 フレームに分かれる
    /// （Input::end_frame が mouse → touch の順で畳むことの確認を兼ねる）。
    #[test]
    fn android_policy_touch_drives_mouse_through_input() {
        let mut input = Input::with_pointer_policy(ANDROID_POLICY);
        input.process_touch(0, RawTouchPhase::Started, 64.0, 32.0);
        input.process_touch(0, RawTouchPhase::Ended, 64.0, 32.0);
        let m = input.mouse_position(InputState::Current);
        assert_eq!((m.x, m.y), (64.0, 32.0));
        assert!(input.is_trigger_mouse(MouseButton::Left));
        assert!(input.is_press_mouse(MouseButton::Left));
        assert!(!input.is_release_mouse(MouseButton::Left));
        assert_eq!(input.touch(0).unwrap().phase, TouchPhase::Began);

        input.end_frame();
        assert!(input.is_release_mouse(MouseButton::Left), "解放は次フレーム");
        assert!(!input.is_press_mouse(MouseButton::Left));
        assert_eq!(input.touch(0).unwrap().phase, TouchPhase::Ended);

        input.end_frame();
        assert!(!input.is_release_mouse(MouseButton::Left));
        assert_eq!(input.touch_count(), 0);
    }

    /// フォーカス喪失の安全弁: 実タッチは取り消され、タッチ由来の押下も解ける。
    #[test]
    fn cancel_touches_releases_touch_driven_mouse() {
        let mut input = Input::with_pointer_policy(ANDROID_POLICY);
        input.process_touch(3, RawTouchPhase::Started, 1.0, 1.0);
        input.end_frame();
        input.cancel_touches();
        assert!(input.is_release_mouse(MouseButton::Left));
        assert_eq!(input.touch(0).unwrap().phase, TouchPhase::Canceled);
    }

    // ─── ジェスチャーの記録（W2-2）───────────────────────────

    use gesture::pointer_log::PointerInputEvent;

    /// 記録から指のイベントだけを (鍵, 段階, 位置) にする（全部の取り消しは鍵 0・Cancel）。
    fn log_summary(entries: &[PointerLogEntry]) -> Vec<(u64, PointerPhase, [f32; 2])> {
        entries
            .iter()
            .map(|e| match e {
                PointerLogEntry::Pointer(PointerInputEvent { pointer, phase, position, .. }) => (*pointer, *phase, *position),
                PointerLogEntry::CancelAll { .. } => (0, PointerPhase::Cancel, [0.0, 0.0]),
            })
            .collect()
    }

    /// デスクトップ: マウスの左ボタンの合成の指が、触れた → 動いた → 離れたとして記録される。
    /// 左ボタンを押していない移動・右ボタンは記録しない。フレームの終わりで捨てる。
    #[test]
    fn desktop_mouse_finger_is_logged_for_gestures() {
        use touch::MOUSE_FINGER_RAW_ID;
        let mut input = Input::new();
        input.process_cursor_moved(10.0, 20.0);
        input.process_mouse_button(MouseButton::Right, true);
        input.process_mouse_button(MouseButton::Left, true);
        input.process_cursor_moved(15.0, 20.0);
        input.process_mouse_button(MouseButton::Left, false);
        let log = input.take_pointer_events();
        assert_eq!(
            log_summary(&log),
            vec![
                (MOUSE_FINGER_RAW_ID, PointerPhase::Down, [10.0, 20.0]),
                (MOUSE_FINGER_RAW_ID, PointerPhase::Move, [15.0, 20.0]),
                (MOUSE_FINGER_RAW_ID, PointerPhase::Up, [15.0, 20.0]),
            ]
        );
        assert!(log.windows(2).all(|w| w[0].time() <= w[1].time()), "時刻は受け取った順");
        input.process_mouse_button(MouseButton::Left, true);
        input.end_frame();
        assert!(input.take_pointer_events().is_empty(), "取り出されなかった記録はフレームの終わりで捨てる");
    }

    /// Android の方針: 実タッチの指がそのまま記録され、フォーカスを失うと全部の取り消しが積まれる。
    #[test]
    fn android_touches_and_focus_loss_are_logged() {
        let mut input = Input::with_pointer_policy(ANDROID_POLICY);
        input.process_touch(4, RawTouchPhase::Started, 1.0, 2.0);
        input.process_touch(4, RawTouchPhase::Moved, 3.0, 2.0);
        input.cancel_touches();
        input.cancel_gesture_pointers();
        assert_eq!(
            log_summary(&input.take_pointer_events()),
            vec![
                (4, PointerPhase::Down, [1.0, 2.0]),
                (4, PointerPhase::Move, [3.0, 2.0]),
                (0, PointerPhase::Cancel, [0.0, 0.0]),
                (0, PointerPhase::Cancel, [0.0, 0.0]),
            ]
        );
    }

    /// 注入: 座標と左ボタンが 1 本の指（INJECTED_POINTER_KEY）になる。解放（RELEASE_ALL）で離れる。
    #[test]
    fn injected_mouse_becomes_a_gesture_pointer() {
        let mut input = Input::new();
        input.apply_injected_action(InjectAction::MousePos { x: 100.0, y: 50.0 });
        input.apply_injected_action(InjectAction::MouseButton { button: MouseButton::Left, down: true });
        input.apply_injected_action(InjectAction::MousePos { x: 120.0, y: 50.0 });
        input.apply_injected_action(InjectAction::Key { key: KeyCode::KeyA, down: true });
        input.release_injected_input();
        assert_eq!(
            log_summary(&input.take_pointer_events()),
            vec![
                (INJECTED_POINTER_KEY, PointerPhase::Down, [100.0, 50.0]),
                (INJECTED_POINTER_KEY, PointerPhase::Move, [120.0, 50.0]),
                (INJECTED_POINTER_KEY, PointerPhase::Up, [120.0, 50.0]),
            ]
        );
        assert_eq!(input.touch_count(), 0, "注入は TouchState を変えない（従来どおり）");
    }

    /// 注入のシーケンス: 1 回の tick でまとめて当たった操作も、予定の時刻（t）の間隔で記録される。
    #[test]
    fn injected_sequence_uses_scheduled_times() {
        let json = r#"[{"t":0.0,"mouse_pos":[10,10]},{"t":0.0,"mouse_button":"left","down":true},
                       {"t":0.1,"mouse_pos":[30,10]},{"t":0.25,"mouse_button":"left","down":false}]"#;
        let mut input = Input::new();
        assert!(input.start_injected_sequence(InputSequencePlayer::from_json(json).unwrap()));
        input.tick_injection(true, false);
        // 0.3 秒遅れて次の tick（1 フレームが大きく遅れた）→ 残りの 2 件が同じ tick で当たる
        std::thread::sleep(std::time::Duration::from_millis(300));
        input.tick_injection(true, false);
        let log = input.take_pointer_events();
        assert_eq!(log.len(), 3, "{log:?}");
        let (t0, t1, t2) = (log[0].time(), log[1].time(), log[2].time());
        assert!(((t1 - t0) - 0.1).abs() < 0.02, "動いたのは押してから 0.1 秒後（予定の時刻）: {}", t1 - t0);
        assert!(((t2 - t0) - 0.25).abs() < 0.02, "離したのは 0.25 秒後: {}", t2 - t0);
    }
}
