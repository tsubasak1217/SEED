// ============================================================
//  text_input/hub.rs — 文字入力のハブ（今の入力欄の場・キーボードの状態・プラットフォームとの食い違いの管理）【単体テスト付き】
//
//  【役割】（docs/ui_text_input.md §3・§5）
//  プロセスに 1 つ（`hub()` の Mutex）。スクリプトの FFI（scripting/text_input_bridge.rs）・App のキーと IME の受け口
//  （app/text_input_hooks.rs）・IPC の注入が、エンジンのスレッドから触る（Android の JNI は inbox.rs に積むだけで、ここは触らない）。
//    - 場（TextSession）: フォーカスのある入力欄の本文・選択・変換中の区間。同時に 1 つ（Begin で前の場は終わる）
//    - キーボード: 表示と高さ（画面の画素。Android は IME の知らせ、PC は模擬）。変わったら場へ知らせを積む
//    - プラットフォームとの食い違い: 要求（出す・隠す）と「最後に当てた状態」から、フレームの末尾に命令の並びを作る
//      （`take_platform_commands`。欄から欄へ移るときの「隠す → 出す」を畳む）
//    - Android の古い写しの見張り（EchoBarrier）: 場を始めて IME の本文を差し替えた直後（ECHO_BARRIER_TIMEOUT の間）は、
//      **前の欄の最後の本文と同じ本文の写しだけ**を捨てる（前の欄の遅れた写し〈古い InputConnection の finishComposingText など〉で
//      新しい欄の本文を上書きしない）。それ以外の写し（新しい欄での最初の打鍵）はすぐ受けて見張りを外す。差し替えた状態と同じ写し（返り）も見張りを外す。
//      2026-09-30 の実機（Simeji）: キーボードが隠れている間の差し替えには返りが来なかった（1 回目の欄の開始 12 回・2 回目の 7 回のすべて。
//      返りが来たのはキーボードを出したまま欄を移ったときだけ）。以前の「返りが来るまで全部捨てる」作りは、返りが来ないので 500 ms 以内の
//      最初の打鍵を捨てる作りだった（実機で抜けた記録は無い。コードからの推論。docs/ui_text_input.md §5・§13.1）。
// ============================================================

use std::sync::{Mutex, MutexGuard};
use std::time::{Duration, Instant};

use super::config::{TextInputAction, TextInputConfig, TextInputKind};
use super::edit_state::{TextEditState, Utf16State};
use super::inbox::PlatformTextMessage;
use super::platform::PlatformCommand;
use super::session::{TextInputEvent, TextSession};

/// ログの頭。
pub const LOG_PREFIX: &str = "[SEED TEXT INPUT]";

/// IME の本文を差し替えた後、前の欄の遅れた写しを捨てる最長の時間（過ぎたら古い写しの見張りを外す）。
pub const ECHO_BARRIER_TIMEOUT: Duration = Duration::from_millis(500);

/// キーボードの状態（画面の画素）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub struct KeyboardState {
    /// 見えているか（プラットフォームの知らせ）。
    pub visible: bool,
    /// 高さ（画面の下端から。画面の画素）。
    pub height_px: i32,
}

impl KeyboardState {
    /// スクリプトへ見せる高さ（見えていなければ 0）。
    pub fn effective_height(&self) -> i32 {
        if self.visible { self.height_px.max(0) } else { 0 }
    }
}

/// 次のフレームの末尾で行う要求。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
struct PendingRequests {
    /// キーボードを出す。
    show: bool,
    /// キーボードを隠す。
    hide: bool,
}

/// 最後にプラットフォームへ当てた状態（食い違いだけを命令にするため）。
#[derive(Clone, Debug, PartialEq, Eq, Default)]
struct AppliedState {
    /// 当てた場の番号。
    session: Option<u32>,
    /// 当てた入力の種類とアクション。
    editor: Option<(TextInputKind, TextInputAction)>,
    /// PC の IME を許可しているか。
    ime_allowed: bool,
    /// 当てた候補窓の矩形。
    caret: Option<[i32; 4]>,
}

/// Android の古い写しの見張り。
#[derive(Clone, Debug, PartialEq, Eq)]
struct EchoBarrier {
    /// 差し替えた状態（同じ写しが返ってきたら見張りを外す）。
    expected: Utf16State,
    /// 差し替える前に IME が持っていた本文（前の欄の最後の本文。この本文の写しだけを捨てる。分からなければ None＝何も捨てない）。
    stale_text: Option<String>,
    /// 差し替えた時刻。
    since: Instant,
}

/// Android の写しを見張りに照らした結果。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum BarrierVerdict {
    /// 見張りが無い・時間切れ・新しい欄の写し: 受ける（見張りは外す）。
    Accept,
    /// 差し替えた状態の返り: 当てても変わらないので捨て、見張りを外す。
    Echo,
    /// 前の欄の遅れた写し: 捨てる（見張りは残す）。
    Stale,
}

impl EchoBarrier {
    /// 写しを照らす。
    fn judge(&self, state: &Utf16State, now: Instant) -> BarrierVerdict {
        if self.expected == *state {
            return BarrierVerdict::Echo;
        }
        let within = now.duration_since(self.since) < ECHO_BARRIER_TIMEOUT;
        let same_as_previous_field = self.stale_text.as_deref() == Some(state.text.as_str());
        // 新しい欄の本文と同じ本文なら前の欄の写しと区別できないが、当てても本文は変わらないので受ける
        if within && same_as_previous_field && state.text != self.expected.text {
            BarrierVerdict::Stale
        } else {
            BarrierVerdict::Accept
        }
    }
}

/// 文字入力のハブ。
#[derive(Debug, Default)]
pub struct TextInputHub {
    /// 次に配る場の番号。
    next_id: u32,
    /// 今の場。
    session: Option<TextSession>,
    /// キーボードの状態。
    keyboard: KeyboardState,
    /// 候補窓が避ける矩形（画面の画素。スクリプトがカーソルの位置から決める）。
    caret_rect: Option<[i32; 4]>,
    /// 次のフレームの末尾で行う要求。
    requests: PendingRequests,
    /// 最後に当てた状態。
    applied: AppliedState,
    /// Android の古い写しの見張り。
    echo_barrier: Option<EchoBarrier>,
    /// IME が今持っているはずの本文（最後に受けた写し・最後に差し替えた本文。Android だけ。前の欄の遅れた写しの見分けに使う）。
    last_ime_text: Option<String>,
    /// PC のキーボードの模擬の高さ（画面の画素。None = 模擬しない）。
    simulated_keyboard_px: Option<i32>,
}

/// プロセスに 1 つのハブ。
static HUB: Mutex<Option<TextInputHub>> = Mutex::new(None);

/// ハブを借りる（初めて借りるときに作る。毒されても続ける）。
pub fn with_hub<R>(f: impl FnOnce(&mut TextInputHub) -> R) -> R {
    let mut guard: MutexGuard<'_, Option<TextInputHub>> = HUB.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    f(guard.get_or_insert_with(TextInputHub::default))
}

impl TextInputHub {
    // ─── 場 ──────────────────────────────────────────────

    /// 場を始める（前の場は終わる）。キーボードを出す要求を積む。
    ///
    /// # 戻り値
    /// 新しい場の番号（1 から）
    pub fn begin(&mut self, config: TextInputConfig, initial: TextEditState) -> u32 {
        self.next_id = self.next_id.wrapping_add(1).max(1);
        let id = self.next_id;
        self.session = Some(TextSession::new(id, config, initial));
        self.requests = PendingRequests { show: true, hide: false };
        self.caret_rect = None;
        id
    }

    /// 場を終える（番号が今の場のときだけ。変換中の文字は確定扱いで残す）。キーボードを隠す要求を積む。
    pub fn end(&mut self, id: u32) -> bool {
        if self.active_id() != Some(id) {
            return false;
        }
        if let Some(session) = self.session.as_mut() {
            session.finish_composition();
        }
        self.session = None;
        self.requests = PendingRequests { show: false, hide: true };
        self.caret_rect = None;
        true
    }

    /// Play の区切り（Play を抜けた・シーンの作り直し）: 場を捨ててキーボードを隠す。
    pub fn reset(&mut self) {
        if self.session.take().is_some() || self.applied.session.is_some() {
            self.requests = PendingRequests { show: false, hide: true };
        }
        self.caret_rect = None;
        self.echo_barrier = None;
    }

    /// 今の場の番号（無ければ None）。
    pub fn active_id(&self) -> Option<u32> {
        self.session.as_ref().map(TextSession::id)
    }

    /// 今の場（無ければ None）。
    pub fn active(&self) -> Option<&TextSession> {
        self.session.as_ref()
    }

    /// 番号の場を借りる（今の場でなければ None）。
    pub fn session_mut(&mut self, id: u32) -> Option<&mut TextSession> {
        self.session.as_mut().filter(|session| session.id() == id)
    }

    /// 今の場を借りる（PC のキー・IME・IPC の注入の受け口）。
    pub fn active_mut(&mut self) -> Option<&mut TextSession> {
        self.session.as_mut()
    }

    /// キーボードを出す要求（今の場のときだけ。利用者が IME を閉じた後に欄をもう一度タップしたとき）。
    pub fn request_show(&mut self, id: u32) -> bool {
        if self.active_id() != Some(id) {
            return false;
        }
        self.requests = PendingRequests { show: true, hide: false };
        true
    }

    /// キーボードを隠す要求（今の場のときだけ。フォーカスは残す）。
    pub fn request_hide(&mut self, id: u32) -> bool {
        if self.active_id() != Some(id) {
            return false;
        }
        self.requests = PendingRequests { show: false, hide: true };
        true
    }

    /// 候補窓が避ける矩形を置く（今の場のときだけ。画面の画素）。
    pub fn set_caret_rect(&mut self, id: u32, rect: [i32; 4]) -> bool {
        if self.active_id() != Some(id) {
            return false;
        }
        self.caret_rect = Some(rect);
        true
    }

    // ─── キーボード ──────────────────────────────────────

    /// キーボードの状態。
    pub fn keyboard(&self) -> KeyboardState {
        self.keyboard
    }

    /// キーボードの状態を変える（見えている高さが変わったら場へ知らせる）。
    pub fn set_keyboard(&mut self, keyboard: KeyboardState) {
        let before = self.keyboard.effective_height();
        self.keyboard = keyboard;
        let after = self.keyboard.effective_height();
        if before == after {
            return;
        }
        if let Some(session) = self.session.as_mut() {
            session.push_event(if after > 0 {
                TextInputEvent::KeyboardShown { height_px: after }
            } else {
                TextInputEvent::KeyboardHidden
            });
        }
    }

    /// PC のキーボードの模擬の高さ（None = 模擬しない）。
    pub fn simulated_keyboard_px(&self) -> Option<i32> {
        self.simulated_keyboard_px
    }

    /// PC のキーボードの模擬の高さを置く（環境変数・IPC。出ている模擬のキーボードの高さもすぐ変える）。
    pub fn set_simulated_keyboard_px(&mut self, height_px: Option<i32>) {
        self.simulated_keyboard_px = height_px.filter(|&h| h > 0);
        if self.keyboard.visible {
            match self.simulated_keyboard_px {
                Some(height_px) => self.set_keyboard(KeyboardState { visible: true, height_px }),
                None => self.set_keyboard(KeyboardState::default()),
            }
        }
    }

    // ─── プラットフォームからの知らせ ────────────────────

    /// Android の IME・IPC の模擬からの知らせを当てる（フレームの頭で inbox から取り出したもの）。
    pub fn apply_platform_messages(&mut self, messages: Vec<PlatformTextMessage>, now: Instant) {
        for message in messages {
            match message {
                PlatformTextMessage::State(state) => self.apply_platform_state(&state, now),
                PlatformTextMessage::Action(code) => {
                    if let (Some(session), Some(action)) = (self.session.as_mut(), TextInputAction::from_code(code)) {
                        session.notify_action(action);
                    }
                }
                PlatformTextMessage::KeyboardVisible(visible) => {
                    let keyboard = KeyboardState { visible, ..self.keyboard };
                    self.set_keyboard(keyboard);
                }
                PlatformTextMessage::KeyboardHeight(height_px) => {
                    let keyboard = KeyboardState { height_px: height_px.max(0), ..self.keyboard };
                    self.set_keyboard(keyboard);
                }
            }
        }
    }

    /// Android の IME の状態の写しを当てる（古い写しの見張りの間は、前の欄の最後の本文と同じ本文の写しだけを捨てる）。
    fn apply_platform_state(&mut self, state: &Utf16State, now: Instant) {
        // IME が今持っている本文を覚える（場が無い間の写し〈前の欄の後片付け〉も、次の欄の見張りの手がかりになる）
        self.last_ime_text = Some(state.text.clone());
        if let Some(barrier) = &self.echo_barrier {
            match barrier.judge(state, now) {
                BarrierVerdict::Echo => {
                    // 差し替えた状態の返り（当てても変わらない）
                    self.echo_barrier = None;
                    return;
                }
                BarrierVerdict::Stale => {
                    // 前の欄の遅れた写し: 捨てる（本文は出さず、長さだけ）
                    eprintln!(
                        "{LOG_PREFIX} 前の欄の遅れた写しを捨てました（長さ {}・UTF-16）",
                        state.text.encode_utf16().count()
                    );
                    return;
                }
                // 新しい欄での打鍵・時間切れ: 受けて見張りを外す
                BarrierVerdict::Accept => self.echo_barrier = None,
            }
        }
        if let Some(session) = self.session.as_mut() {
            session.apply_platform_state(state);
        }
    }

    // ─── プラットフォームへの命令 ────────────────────────

    /// 今のフレームの要求と食い違いから命令の並びを作る（フレームの末尾に 1 回。要求は消える）。
    ///
    /// # 引数
    /// * `now`          - 今の時刻（Android の古い写しの見張りの始まり）
    /// * `expects_echo` - IME の本文を差し替えると同じ状態の写しが返ってくるプラットフォームか（Android = true。
    ///                    true のときだけ古い写しの見張りを張る。PC は返りが無いので張らない）
    pub fn take_platform_commands(&mut self, now: Instant, expects_echo: bool) -> Vec<PlatformCommand> {
        let mut commands = Vec::new();
        let requests = std::mem::take(&mut self.requests);
        let caret_rect = self.caret_rect;
        match self.session.as_mut() {
            Some(session) => {
                let id = session.id();
                let editor = (session.config().kind, session.config().action);
                let new_session = self.applied.session != Some(id);
                if new_session || self.applied.editor != Some(editor) {
                    commands.push(PlatformCommand::SetEditorInfo { kind: editor.0, action: editor.1 });
                    let state = session.state().to_utf16();
                    commands.push(PlatformCommand::SetState(state.clone()));
                    session.mark_ime_synced();
                    // 見張り: 差し替える前に IME が持っていた本文（前の欄の最後の本文）の写しだけを、しばらく捨てる
                    let stale_text = self.last_ime_text.take();
                    self.last_ime_text = Some(state.text.clone());
                    self.echo_barrier = expects_echo.then_some(EchoBarrier { expected: state, stale_text, since: now });
                    self.applied.editor = Some(editor);
                } else if session.ime_out_of_sync() {
                    let state = session.state().to_utf16();
                    self.last_ime_text = Some(state.text.clone());
                    commands.push(PlatformCommand::SetState(state));
                    session.mark_ime_synced();
                }
                // PC: 文字の欄だけ IME を許可する（数字の欄は直接の文字で入る）
                let allow_ime = editor.0 == TextInputKind::Text;
                if self.applied.ime_allowed != allow_ime {
                    commands.push(PlatformCommand::AllowIme(allow_ime));
                    self.applied.ime_allowed = allow_ime;
                }
                if caret_rect != self.applied.caret {
                    if let Some(rect) = caret_rect {
                        commands.push(PlatformCommand::SetCaretArea(rect));
                    }
                    self.applied.caret = caret_rect;
                }
                if requests.show {
                    commands.push(PlatformCommand::ShowKeyboard);
                } else if requests.hide {
                    commands.push(PlatformCommand::HideKeyboard);
                }
                self.applied.session = Some(id);
            }
            None => {
                if requests.hide || self.applied.session.is_some() {
                    commands.push(PlatformCommand::HideKeyboard);
                }
                if self.applied.ime_allowed {
                    commands.push(PlatformCommand::AllowIme(false));
                }
                self.applied = AppliedState::default();
                self.echo_barrier = None;
            }
        }
        commands
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    fn text_config() -> TextInputConfig {
        TextInputConfig::default()
    }

    /// Android と同じく「本文の差し替えの返りがある」プラットフォームとして命令を取り出す。
    fn flush(hub: &mut TextInputHub, now: Instant) -> Vec<PlatformCommand> {
        hub.take_platform_commands(now, true)
    }

    /// PC（返りが無い）では古い写しの見張りを張らない（IPC の platform_state の模擬をすぐ受ける）。
    #[test]
    fn desktop_does_not_wait_for_echo() {
        let mut hub = TextInputHub::default();
        let now = Instant::now();
        hub.begin(text_config(), TextEditState::with_caret_at_end("a"));
        hub.take_platform_commands(now, false);
        let typed = Utf16State { text: "ab".into(), selection_start: 2, selection_end: 2, composition: None };
        hub.apply_platform_messages(vec![PlatformTextMessage::State(typed)], now);
        assert_eq!(hub.active().unwrap().state().text, "ab");
    }

    /// 場を始めると、EditorInfo → 本文 → IME の許可 → 表示の順に命令になる。次のフレームは何も出さない。
    #[test]
    fn begin_emits_editor_state_and_show_once() {
        let mut hub = TextInputHub::default();
        let now = Instant::now();
        let id = hub.begin(text_config(), TextEditState::with_caret_at_end("ab"));
        let commands = flush(&mut hub, now);
        assert_eq!(commands.len(), 4, "{commands:?}");
        assert!(matches!(commands[0], PlatformCommand::SetEditorInfo { kind: TextInputKind::Text, .. }));
        assert!(matches!(&commands[1], PlatformCommand::SetState(state) if state.text == "ab"));
        assert_eq!(commands[2], PlatformCommand::AllowIme(true));
        assert_eq!(commands[3], PlatformCommand::ShowKeyboard);
        assert!(flush(&mut hub, now).is_empty());
        assert_eq!(hub.active_id(), Some(id));
    }

    /// 欄から欄へ同じフレームで移ると「隠す」は畳まれ、新しい欄の EditorInfo と表示だけになる。
    #[test]
    fn switching_fields_does_not_hide() {
        let mut hub = TextInputHub::default();
        let now = Instant::now();
        let first = hub.begin(text_config(), TextEditState::default());
        flush(&mut hub, now);
        assert!(hub.end(first));
        let number = TextInputConfig { kind: TextInputKind::Number, ..TextInputConfig::default() };
        hub.begin(number, TextEditState::with_caret_at_end("12"));
        let commands = flush(&mut hub, now);
        assert!(!commands.contains(&PlatformCommand::HideKeyboard), "{commands:?}");
        assert!(commands.contains(&PlatformCommand::ShowKeyboard));
        assert!(commands.contains(&PlatformCommand::AllowIme(false)), "数字の欄は IME を許可しない");
    }

    /// 場を終えると隠す・IME の許可を外す。終えた番号・違う番号では何もしない。
    #[test]
    fn end_hides_and_disallows_ime() {
        let mut hub = TextInputHub::default();
        let now = Instant::now();
        let id = hub.begin(text_config(), TextEditState::default());
        flush(&mut hub, now);
        assert!(!hub.end(id + 1));
        assert!(hub.end(id));
        assert!(!hub.end(id));
        let commands = flush(&mut hub, now);
        assert_eq!(commands, vec![PlatformCommand::HideKeyboard, PlatformCommand::AllowIme(false)]);
    }

    /// 写しを作る（選択は末尾・変換なし）。
    fn typed(text: &str) -> Utf16State {
        let end = text.encode_utf16().count();
        Utf16State { text: text.into(), selection_start: end, selection_end: end, composition: None }
    }

    /// 前の欄 A で本文 `a_text` を打ち終えてから、新しい欄 B（`b_text`）を始めて命令を出した状態のハブを作る。
    fn switched_hub(a_text: &str, b_text: &str, now: Instant) -> TextInputHub {
        let mut hub = TextInputHub::default();
        hub.begin(text_config(), TextEditState::default());
        flush(&mut hub, now);
        hub.apply_platform_messages(vec![PlatformTextMessage::State(typed(a_text))], now);
        hub.begin(text_config(), TextEditState::with_caret_at_end(b_text));
        flush(&mut hub, now);
        hub
    }

    /// Android: 新しい欄を始めた直後は、前の欄の最後の本文と同じ本文の写し（遅れた写し）だけを捨てる。返りの後の打鍵は受ける。
    #[test]
    fn echo_barrier_drops_only_previous_field_states() {
        let start = Instant::now();
        let mut hub = switched_hub("old!", "new", start);
        // 前の欄の遅れた写し（古い InputConnection の finishComposingText など）は捨てる
        hub.apply_platform_messages(vec![PlatformTextMessage::State(typed("old!"))], start);
        assert_eq!(hub.active().unwrap().state().text, "new", "前の欄の遅れた写しは捨てる");
        // 返り（差し替えた状態）と、その後の打鍵は受ける
        hub.apply_platform_messages(
            vec![PlatformTextMessage::State(typed("new")), PlatformTextMessage::State(typed("news"))],
            start,
        );
        assert_eq!(hub.active().unwrap().state().text, "news", "返りの後の写しは受ける");
    }

    /// Android（2026-09-30 の実機のログからの推論。返りを送らない IME）: 返りが来なくても、新しい欄での最初の打鍵は時間を待たずに受ける。
    #[test]
    fn first_keystroke_right_after_focus_is_kept() {
        let start = Instant::now();
        let mut hub = switched_hub("19", "", start);
        let soon = start + Duration::from_millis(10);
        hub.apply_platform_messages(vec![PlatformTextMessage::State(typed("お"))], soon);
        assert_eq!(hub.active().unwrap().state().text, "お", "フォーカスの直後の最初の打鍵は抜けない");
        // 見張りは外れたので、その後に前の欄と同じ本文が来ても（利用者が打ち直した）受ける
        hub.apply_platform_messages(vec![PlatformTextMessage::State(typed("19"))], soon);
        assert_eq!(hub.active().unwrap().state().text, "19");
    }

    /// 見張りの時間が過ぎたら、前の欄と同じ本文の写しも受ける。新しい欄の本文と同じ本文なら捨てない（当てても変わらない）。
    #[test]
    fn echo_barrier_times_out_and_ignores_same_text() {
        let start = Instant::now();
        let mut hub = switched_hub("old!", "x", start);
        let later = start + ECHO_BARRIER_TIMEOUT + Duration::from_millis(1);
        hub.apply_platform_messages(vec![PlatformTextMessage::State(typed("old!"))], later);
        assert_eq!(hub.active().unwrap().state().text, "old!", "時間切れの後は受ける");

        let mut same = switched_hub("abc", "abc", start);
        let caret_at_start = Utf16State { text: "abc".into(), selection_start: 0, selection_end: 0, composition: None };
        same.apply_platform_messages(vec![PlatformTextMessage::State(caret_at_start)], start);
        assert_eq!(same.active().unwrap().state().focus, 0, "同じ本文は前の欄の写しと区別できないが、受けてよい");
    }

    /// キーボードの高さの変化は場へ知らせる（見えていないときの高さは 0 に見せる）。模擬の高さもすぐ変わる。
    #[test]
    fn keyboard_changes_become_events() {
        let mut hub = TextInputHub::default();
        let id = hub.begin(text_config(), TextEditState::default());
        hub.apply_platform_messages(
            vec![PlatformTextMessage::KeyboardHeight(979), PlatformTextMessage::KeyboardVisible(true)],
            Instant::now(),
        );
        assert_eq!(hub.keyboard().effective_height(), 979);
        hub.set_simulated_keyboard_px(Some(600));
        hub.apply_platform_messages(vec![PlatformTextMessage::KeyboardVisible(false)], Instant::now());
        let events = hub.session_mut(id).unwrap().take_events(usize::MAX);
        assert_eq!(
            events,
            vec![
                TextInputEvent::KeyboardShown { height_px: 979 },
                TextInputEvent::KeyboardShown { height_px: 600 },
                TextInputEvent::KeyboardHidden
            ]
        );
    }

    /// 候補窓の矩形は変わったときだけ命令にする。アクションの知らせは場へ積む。
    #[test]
    fn caret_rect_and_actions() {
        let mut hub = TextInputHub::default();
        let now = Instant::now();
        let id = hub.begin(text_config(), TextEditState::default());
        flush(&mut hub, now);
        assert!(hub.set_caret_rect(id, [10, 20, 2, 30]));
        assert_eq!(flush(&mut hub, now), vec![PlatformCommand::SetCaretArea([10, 20, 2, 30])]);
        assert!(hub.set_caret_rect(id, [10, 20, 2, 30]));
        assert!(flush(&mut hub, now).is_empty());
        hub.apply_platform_messages(vec![PlatformTextMessage::Action(TextInputAction::Done.code())], now);
        assert_eq!(
            hub.session_mut(id).unwrap().take_events(usize::MAX),
            vec![TextInputEvent::Action(TextInputAction::Done)]
        );
    }

    /// Play の区切り: 場を捨ててキーボードを隠す。
    #[test]
    fn reset_hides_keyboard() {
        let mut hub = TextInputHub::default();
        let now = Instant::now();
        hub.begin(text_config(), TextEditState::default());
        flush(&mut hub, now);
        hub.reset();
        assert_eq!(hub.active_id(), None);
        assert_eq!(flush(&mut hub, now), vec![PlatformCommand::HideKeyboard, PlatformCommand::AllowIme(false)]);
    }
}
