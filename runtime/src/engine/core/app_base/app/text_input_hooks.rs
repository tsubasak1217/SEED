// ============================================================
//  text_input_hooks.rs — 文字入力（W2-6a。engine/core/text_input）を App へつなぐ所（正典は docs/ui_text_input.md）
//
//  【受け持ち】
//    - PC の IME（winit の WindowEvent::Ime）→ 今の入力欄の場（on_text_input_ime）
//    - PC のキーの押下 → 編集の操作・入れる文字（route_key_to_text_input。入力欄が受けたらエディタのショートカットへ回さない）
//    - IPC の注入（INPUT_TEXT: と INPUT_KEY の押下）→ 同じ受け口（handle_text_inject・route_injected_key_to_text_input）
//    - フレームの頭: Android の JNI が積んだ知らせ（inbox）をハブへ当てる（pump_text_input_messages）
//    - フレームの末尾: ハブが作った命令をプラットフォームで実行する（flush_text_input_platform。Android は登録された実装、
//      PC はウィンドウの set_ime_allowed・set_ime_cursor_area とキーボードの模擬）
//    - Play の区切りで場を捨てる（reset_text_input_for_play_session）
//  入力欄の場が無いとき（ゲーム・入力欄の無い画面）は、どの受け口も何もしない（従来どおり）。
//
//  【PC のキーボードの模擬】環境変数 SEED_SIM_KEYBOARD_HEIGHT（画面の画素）か IPC の INPUT_TEXT:keyboard,<画素> で、
//  入力欄にフォーカスがある間だけ「その高さのソフトキーボードが出ている」とスクリプトへ見せる（キーボードを避ける確かめ用。
//  PC の画面には何も描かない）。
// ============================================================

use std::time::Instant;

use winit::event::{ElementState, Ime, KeyEvent};
use winit::keyboard::{KeyCode, PhysicalKey};

use crate::engine::core::app_base::app::RuntimeMode;
use crate::engine::core::input::inject::{TextInjectCommand, INJECT_REPLY_ERROR_PREFIX, INJECT_REPLY_OK};
use crate::engine::core::redraw::{wake as redraw_wake, RedrawReason};
use crate::engine::core::text_input::{
    self, inbox, keys, EditModifiers, KeyOutcome, KeyboardState, PlatformCommand, PlatformTextMessage, SystemClipboard,
    TextInputHub, LOG_PREFIX,
};

use super::App;

/// PC のキーボードの模擬の高さの環境変数（画面の画素。無い・0 以下 = 模擬しない）。
pub const ENV_SIM_KEYBOARD_HEIGHT: &str = "SEED_SIM_KEYBOARD_HEIGHT";

/// 診断ログの環境変数（"1" でプラットフォームへの命令の種類をログへ出す。本文は出さない）。
pub const ENV_TEXT_INPUT_LOG: &str = "SEED_TEXT_INPUT_LOG";

/// 診断ログを有効にする値。
const TEXT_INPUT_LOG_ON: &str = "1";

/// 診断ログを出すか（起動時に 1 回読む）。
static TEXT_INPUT_LOG: std::sync::OnceLock<bool> = std::sync::OnceLock::new();

/// 診断ログを出すか。
fn text_input_log_enabled() -> bool {
    *TEXT_INPUT_LOG.get_or_init(|| std::env::var(ENV_TEXT_INPUT_LOG).is_ok_and(|v| v.trim() == TEXT_INPUT_LOG_ON))
}

/// 命令を本文を伏せた短い文字にする（診断ログ用）。
fn describe_command(command: &PlatformCommand) -> String {
    match command {
        PlatformCommand::SetEditorInfo { kind, action } => format!("SetEditorInfo({}, {})", kind.name(), action.name()),
        PlatformCommand::SetState(state) => format!(
            "SetState(長さ {} ・選択 {}..{} ・変換 {:?})",
            state.text.encode_utf16().count(),
            state.selection_start,
            state.selection_end,
            state.composition
        ),
        PlatformCommand::ShowKeyboard => "ShowKeyboard".to_string(),
        PlatformCommand::HideKeyboard => "HideKeyboard".to_string(),
        PlatformCommand::AllowIme(allowed) => format!("AllowIme({allowed})"),
        PlatformCommand::SetCaretArea(rect) => format!("SetCaretArea({rect:?})"),
    }
}

/// IPC の応答: 今の場の状態（INPUT_TEXT:dump）。
const REPLY_TEXT_STATE: &str = "INPUT_TEXT_STATE:";

/// IPC の失敗の理由: 入力欄の場が無い。
const ERROR_NO_SESSION: &str = "no_text_session";

/// 候補窓の矩形の要素の番号（x・y・幅・高さ）。
const RECT_X: usize = 0;
const RECT_Y: usize = 1;
const RECT_WIDTH: usize = 2;
const RECT_HEIGHT: usize = 3;

impl App {
    /// 起動時に 1 回: PC のキーボードの模擬の高さを環境変数から読む（Android では読まない）。
    pub(super) fn init_text_input_from_env(&self) {
        if cfg!(target_os = "android") {
            return;
        }
        let Some(height) = std::env::var(ENV_SIM_KEYBOARD_HEIGHT).ok().and_then(|v| v.trim().parse::<i32>().ok()) else {
            return;
        };
        text_input::with_hub(|hub| hub.set_simulated_keyboard_px(Some(height)));
        eprintln!("{LOG_PREFIX} PC のキーボードの模擬: 高さ {height} px（{ENV_SIM_KEYBOARD_HEIGHT}）");
    }

    /// 入力欄がキーを受けてよい状態か（Play 中で、一時停止〈デバッグカメラ〉でない）。
    fn text_input_accepts_keys(&self) -> bool {
        self.mode == RuntimeMode::Play && !self.is_simulation_paused()
    }

    /// 押している修飾キー（実キーと注入の両方）。
    fn text_input_modifiers(&self) -> EditModifiers {
        EditModifiers {
            shift: self.shift_held
                || self.input.is_press_key(KeyCode::ShiftLeft)
                || self.input.is_press_key(KeyCode::ShiftRight),
            ctrl: self.ctrl_held
                || self.input.is_press_key(KeyCode::ControlLeft)
                || self.input.is_press_key(KeyCode::ControlRight),
        }
    }

    // ─── PC の IME とキー ────────────────────────────────

    /// PC の IME の知らせ（winit の WindowEvent::Ime）を今の入力欄の場へ当てる。
    pub(super) fn on_text_input_ime(&mut self, ime: &Ime) {
        if !self.text_input_accepts_keys() {
            return;
        }
        text_input::with_hub(|hub| {
            let Some(session) = hub.active_mut() else { return };
            match ime {
                // 許可した窓で変換が始まった（何もしない。変換中の文字は Preedit で届く）
                Ime::Enabled => {}
                // 変換中の文字列（カーソルは変換中の文字列の中のバイト位置。空 = 取り消し・確定の直前の片付け）
                Ime::Preedit(text, cursor) => session.set_preedit(text, *cursor),
                Ime::Commit(text) => session.commit(text),
                // IME が無効になった: 残った変換中の区間の印を外す（文字は残す）
                Ime::Disabled => session.finish_composition(),
            }
        });
    }

    /// PC のキーの押下を入力欄へ回す（入力欄が受けたら true。呼び出し側はエディタのショートカットへ回さない）。
    ///
    /// 変換中のキー（論理キーが Process）は IME が受けているので、入力欄の操作にはせず受けたことにする
    /// （変換の確定の Enter でエディタの操作が動かないように）。離す操作は受けない（ゲームの入力へそのまま）。
    pub(super) fn route_key_to_text_input(&mut self, event: &KeyEvent) -> bool {
        if event.state != ElementState::Pressed || !self.text_input_accepts_keys() {
            return false;
        }
        let PhysicalKey::Code(code) = event.physical_key else { return false };
        let modifiers = self.text_input_modifiers();
        text_input::with_hub(|hub| {
            let Some(session) = hub.active_mut() else { return false };
            if keys::is_ime_processing(&event.logical_key) {
                return true;
            }
            if let Some(key) = keys::edit_key_from_winit(&event.logical_key, code, modifiers) {
                return session.apply_key(key, &mut SystemClipboard::default()) == KeyOutcome::Consumed;
            }
            if modifiers.ctrl {
                // 入力中の Ctrl の組み合わせ（Ctrl+Z など）はエディタのショートカットへ回さない（入力欄の操作でなくても受けたことにする）
                return true;
            }
            if let Some(text) = keys::insertable_text(event.text.as_deref()) {
                session.commit(text);
                return true;
            }
            false
        })
    }

    /// IPC の注入のキーの押下（INPUT_KEY:{名前},down）を入力欄の編集の操作へ回す（文字は入らない。INPUT_TEXT:commit で入れる）。
    pub(super) fn route_injected_key_to_text_input(&mut self, code: KeyCode) {
        if !self.text_input_accepts_keys() {
            return;
        }
        let modifiers = self.text_input_modifiers();
        text_input::with_hub(|hub| {
            let Some(session) = hub.active_mut() else { return };
            if let Some(key) = keys::edit_key_from_code(code, modifiers) {
                session.apply_key(key, &mut SystemClipboard::default());
            }
        });
    }

    // ─── フレームの頭と末尾 ──────────────────────────────

    /// フレームの頭（スクリプトの前）: Android の JNI が積んだ知らせをハブへ当てる。
    pub(super) fn pump_text_input_messages(&mut self) {
        let messages = inbox::take_all();
        if messages.is_empty() {
            return;
        }
        let now = Instant::now();
        text_input::with_hub(|hub| hub.apply_platform_messages(messages, now));
    }

    /// フレームの末尾: ハブの命令をプラットフォームで実行する（Android は登録された実装、PC はウィンドウ）。
    pub(super) fn flush_text_input_platform(&mut self) {
        let platform = text_input::registered_platform();
        let now = Instant::now();
        let commands = text_input::with_hub(|hub| hub.take_platform_commands(now, platform.is_some()));
        if commands.is_empty() {
            return;
        }
        if text_input_log_enabled() {
            let list: Vec<String> = commands.iter().map(describe_command).collect();
            eprintln!("{LOG_PREFIX} 命令: {}", list.join(" → "));
        }
        match platform {
            Some(platform) => platform.execute(&commands),
            None => self.execute_desktop_text_commands(&commands),
        }
    }

    /// PC で命令を実行する（IME の許可・候補窓の位置はウィンドウへ、キーボードの表示は模擬へ）。
    fn execute_desktop_text_commands(&mut self, commands: &[PlatformCommand]) {
        let mut keyboard_changed = false;
        for command in commands {
            match command {
                PlatformCommand::AllowIme(allowed) => {
                    if let Some(window) = &self.window {
                        window.set_ime_allowed(*allowed);
                    }
                }
                PlatformCommand::SetCaretArea(rect) => {
                    if let Some(window) = &self.window {
                        window.set_ime_cursor_area(
                            winit::dpi::PhysicalPosition::new(rect[RECT_X], rect[RECT_Y]),
                            winit::dpi::PhysicalSize::new(rect[RECT_WIDTH].max(0) as u32, rect[RECT_HEIGHT].max(0) as u32),
                        );
                    }
                }
                PlatformCommand::ShowKeyboard => {
                    keyboard_changed |= text_input::with_hub(|hub| show_simulated_keyboard(hub));
                }
                PlatformCommand::HideKeyboard => {
                    keyboard_changed |= text_input::with_hub(|hub| {
                        let was_visible = hub.keyboard().visible;
                        hub.set_keyboard(KeyboardState::default());
                        was_visible
                    });
                }
                // PC には IME の本文・EditorInfo が無い（本文はエンジンが持つ）
                PlatformCommand::SetEditorInfo { .. } | PlatformCommand::SetState(_) => {}
            }
        }
        if keyboard_changed {
            // 模擬のキーボードの知らせをスクリプトが次のフレームで読めるよう、描画を止めていても次のフレームを回す
            redraw_wake::raise(RedrawReason::TextInput);
        }
    }

    /// Play の区切り（開始・終了）: 入力欄の場を捨てる（キーボードを隠す命令は次のフレームの末尾で出る）。
    /// 積まれた Android の知らせも捨てる（前の Play の打鍵を次の Play の欄へ入れない）。
    pub(super) fn reset_text_input_for_play_session(&mut self) {
        let _ = inbox::take_all();
        text_input::with_hub(TextInputHub::reset);
    }

    // ─── IPC の注入（INPUT_TEXT:）────────────────────────

    /// `INPUT_TEXT:` の命令を実行して応答する（Play 中だけ。呼び出し側 input_inject_ops.rs が確かめ済み）。
    pub(super) fn handle_text_inject(&mut self, command: TextInjectCommand) {
        let reply = match command {
            TextInjectCommand::Keyboard(height) => {
                text_input::with_hub(|hub| hub.set_simulated_keyboard_px(Some(height)));
                redraw_wake::raise(RedrawReason::TextInput);
                Ok(())
            }
            TextInjectCommand::Dump => {
                let json = text_input::with_hub(|hub| dump_json(hub));
                self.send_ipc_line(&format!("{REPLY_TEXT_STATE}{json}"));
                return;
            }
            TextInjectCommand::Key { code, modifiers } => self.with_active_session(|session| {
                if let Some(key) = keys::edit_key_from_code(code, modifiers) {
                    session.apply_key(key, &mut SystemClipboard::default());
                }
            }),
            TextInjectCommand::Commit(text) => self.with_active_session(|session| session.commit(&text)),
            TextInjectCommand::Preedit { text, cursor } => {
                self.with_active_session(|session| session.set_preedit(&text, cursor.map(|c| (c, c))))
            }
            TextInjectCommand::Action(action) => self.with_active_session(|session| session.notify_action(action)),
            TextInjectCommand::PlatformState(state) => {
                let active = text_input::with_hub(|hub| {
                    if hub.active_id().is_none() {
                        return false;
                    }
                    hub.apply_platform_messages(vec![PlatformTextMessage::State(state)], Instant::now());
                    true
                });
                if active { Ok(()) } else { Err(ERROR_NO_SESSION) }
            }
        };
        match reply {
            Ok(()) => self.send_ipc_line(INJECT_REPLY_OK),
            Err(reason) => self.send_ipc_line(&format!("{INJECT_REPLY_ERROR_PREFIX}{reason}")),
        }
    }

    /// 今の場があれば f を当てる（無ければ理由を返す）。
    fn with_active_session(&mut self, f: impl FnOnce(&mut text_input::TextSession)) -> Result<(), &'static str> {
        text_input::with_hub(|hub| match hub.active_mut() {
            Some(session) => {
                f(session);
                Ok(())
            }
            None => Err(ERROR_NO_SESSION),
        })
    }

    /// IPC へ 1 行送る（つながっていなければ何もしない）。
    fn send_ipc_line(&self, line: &str) {
        if let Some(ipc) = &self.ipc {
            ipc.send(line);
        }
    }
}

/// PC の模擬のキーボードを出す（模擬の高さがあれば）。見えるようになったら true。
fn show_simulated_keyboard(hub: &mut TextInputHub) -> bool {
    let Some(height_px) = hub.simulated_keyboard_px() else { return false };
    let was_visible = hub.keyboard().visible;
    hub.set_keyboard(KeyboardState { visible: true, height_px });
    !was_visible
}

/// 今の場の状態を JSON にする（IPC の INPUT_TEXT:dump の応答。添字は UTF-16＝スクリプトと同じ）。
fn dump_json(hub: &TextInputHub) -> String {
    let keyboard = hub.keyboard();
    let session = hub.active().map(|session| {
        let state = session.state().to_utf16();
        serde_json::json!({
            "id": session.id(),
            "kind": session.config().kind.name(),
            "action": session.config().action.name(),
            "max_length": session.config().max_length,
            "allow_paste": session.config().allow_paste,
            "allow_copy": session.config().allow_copy,
            "text": state.text,
            "selection": [state.selection_start, state.selection_end],
            "composition": state.composition.map(|(start, end)| [start, end]),
            "revision": session.revision(),
        })
    });
    serde_json::json!({
        "active": session.is_some(),
        "session": session,
        "keyboard": { "visible": keyboard.visible, "height_px": keyboard.height_px, "effective_px": keyboard.effective_height() },
        "simulated_keyboard_px": hub.simulated_keyboard_px(),
    })
    .to_string()
}
