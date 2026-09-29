// ============================================================
//  text_input/session.rs — フォーカスのある入力欄 1 つぶんの編集の場（設定・状態・スクリプトへの知らせ）【純ロジック・単体テスト付き】
//
//  【役割】（docs/ui_text_input.md §3〜§5）
//  スクリプトが入力欄にフォーカスを当てる（TextInput.Begin）と、ハブ（hub.rs）がこの場を 1 つ作る。
//  どのプラットフォームからの変化もここの入口（commit・set_preedit・apply_key・apply_platform_state・set_text …）で
//  「変化の後の状態の案」にし、決まり（filter.rs）を通して当てる。当てた結果で
//    - 本文が変わった → TextChanged、選択・変換中の区間だけが変わった → SelectionChanged を積む（スクリプトが毎フレーム取り出す）
//    - Android の IME の本文と食い違う（決まりで案を変えた・エンジンの側で変えた）→ `ime_out_of_sync` を立てる
//      （ハブがフレームの末尾に set_text_input_state で送り返す）
//  を決める。場は同時に 1 つ（フォーカスは 1 つ）。
// ============================================================

use super::clipboard::ClipboardAccess;
use super::config::TextInputConfig;
use super::edit_state::{TextEditState, Utf16State};
use super::filter::{apply_rules, EditOrigin};
use super::keys::{single_line, EditKey};

/// スクリプトへ知らせる出来事。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TextInputEvent {
    /// 本文が変わった（変換中の文字の変化も含む）。
    TextChanged,
    /// 本文は同じで、選択・変換中の区間だけが変わった。
    SelectionChanged,
    /// 完了などのアクション（IME のアクションのボタン・PC の Enter）。
    Action(super::config::TextInputAction),
    /// 貼り付けを禁止した欄で貼り付けを止めた。
    PasteBlocked,
    /// ソフトキーボードが出た・高さが変わった（高さは画面の画素）。
    KeyboardShown {
        /// キーボードの高さ（画面の画素）。
        height_px: i32,
    },
    /// ソフトキーボードが隠れた。
    KeyboardHidden,
}

impl TextInputEvent {
    /// スクリプトとの約束の番号（C# の SEED.TextInputEventKind と一致させる）。
    pub fn code(self) -> i32 {
        match self {
            Self::TextChanged => EVENT_TEXT_CHANGED,
            Self::SelectionChanged => EVENT_SELECTION_CHANGED,
            Self::Action(_) => EVENT_ACTION,
            Self::PasteBlocked => EVENT_PASTE_BLOCKED,
            Self::KeyboardShown { .. } => EVENT_KEYBOARD_SHOWN,
            Self::KeyboardHidden => EVENT_KEYBOARD_HIDDEN,
        }
    }

    /// 出来事の添えの値（アクションの番号・キーボードの高さ。無ければ 0）。
    pub fn argument(self) -> i32 {
        match self {
            Self::Action(action) => action.code(),
            Self::KeyboardShown { height_px } => height_px,
            _ => 0,
        }
    }
}

/// 出来事の番号: 本文が変わった。
pub const EVENT_TEXT_CHANGED: i32 = 1;
/// 出来事の番号: 選択・変換中の区間が変わった。
pub const EVENT_SELECTION_CHANGED: i32 = 2;
/// 出来事の番号: 完了などのアクション（添え = アクションの番号）。
pub const EVENT_ACTION: i32 = 3;
/// 出来事の番号: 貼り付けを止めた。
pub const EVENT_PASTE_BLOCKED: i32 = 4;
/// 出来事の番号: キーボードが出た（添え = 高さの画素）。
pub const EVENT_KEYBOARD_SHOWN: i32 = 5;
/// 出来事の番号: キーボードが隠れた。
pub const EVENT_KEYBOARD_HIDDEN: i32 = 6;

/// 積んでおく出来事の上限（スクリプトが取り出さないまま溜まり続けないように。超えたら古いものから捨てる）。
pub const MAX_PENDING_EVENTS: usize = 256;

/// キーを受けた結果。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum KeyOutcome {
    /// 入力欄が受けた（ゲームのショートカット・エディタの操作へ回さない）。
    Consumed,
    /// 入力欄の操作ではない。
    Ignored,
}

/// フォーカスのある入力欄 1 つぶんの編集の場。
#[derive(Debug)]
pub struct TextSession {
    /// 場の番号（1 から。スクリプトが持つ）。
    id: u32,
    /// 設定。
    config: TextInputConfig,
    /// 今の状態（決まりを満たしている）。
    state: TextEditState,
    /// 状態が変わるたびに増える版。
    revision: u64,
    /// スクリプトが取り出していない出来事。
    events: Vec<TextInputEvent>,
    /// Android の IME の本文へ送り返す必要があるか。
    ime_out_of_sync: bool,
}

impl TextSession {
    /// 場を作る（初めの本文にも決まりを当てる。IME へは初めの状態を送る）。
    pub fn new(id: u32, config: TextInputConfig, initial: TextEditState) -> Self {
        let previous = TextEditState::default();
        let outcome = apply_rules(&config, &previous, initial, EditOrigin::Direct);
        Self { id, config, state: outcome.state, revision: 0, events: Vec::new(), ime_out_of_sync: true }
    }

    /// 場の番号。
    pub fn id(&self) -> u32 {
        self.id
    }

    /// 設定。
    pub fn config(&self) -> &TextInputConfig {
        &self.config
    }

    /// 今の状態。
    pub fn state(&self) -> &TextEditState {
        &self.state
    }

    /// 版（状態が変わるたびに増える）。
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// IME へ送り返す必要があるか。
    pub fn ime_out_of_sync(&self) -> bool {
        self.ime_out_of_sync
    }

    /// IME へ送り返した（ハブがフレームの末尾に呼ぶ）。
    pub fn mark_ime_synced(&mut self) {
        self.ime_out_of_sync = false;
    }

    /// 出来事を積む（上限を超えたら古いものから捨てる）。
    pub fn push_event(&mut self, event: TextInputEvent) {
        if self.events.len() >= MAX_PENDING_EVENTS {
            self.events.remove(0);
        }
        self.events.push(event);
    }

    /// 出来事を最大 `max` 件取り出す（古い順。残りは次に取り出す）。
    pub fn take_events(&mut self, max: usize) -> Vec<TextInputEvent> {
        let count = max.min(self.events.len());
        self.events.drain(..count).collect()
    }

    /// 変化の案を決まりに通して当てる。
    ///
    /// # 引数
    /// * `proposed` - 変化の後の状態の案
    /// * `origin`   - 変化の出どころ（IME の状態の写しなら、決まりで変えたときだけ IME へ送り返す。
    ///                それ以外はエンジンの側の変化なので、変わったら IME へ送り返す）
    fn apply(&mut self, proposed: TextEditState, origin: EditOrigin) {
        let outcome = apply_rules(&self.config, &self.state, proposed, origin);
        if outcome.paste_blocked {
            self.push_event(TextInputEvent::PasteBlocked);
        }
        let changed_text = outcome.state.text != self.state.text;
        let changed_marks = !changed_text
            && (outcome.state.anchor != self.state.anchor
                || outcome.state.focus != self.state.focus
                || outcome.state.composition != self.state.composition);
        let changed = changed_text || changed_marks;
        match origin {
            EditOrigin::PlatformState => self.ime_out_of_sync |= outcome.adjusted,
            EditOrigin::Direct => self.ime_out_of_sync |= changed || outcome.adjusted,
        }
        if !changed {
            return;
        }
        self.state = outcome.state;
        self.revision += 1;
        self.push_event(if changed_text { TextInputEvent::TextChanged } else { TextInputEvent::SelectionChanged });
    }

    /// 確定の文字列を入れる（Windows の Ime::Commit・PC の文字のキー・IPC の注入）。
    pub fn commit(&mut self, text: &str) {
        let mut proposed = self.state.clone();
        proposed.commit(text);
        self.apply(proposed, EditOrigin::Direct);
    }

    /// 変換中の文字列を差し替える（Windows の Ime::Preedit・IPC の注入）。
    pub fn set_preedit(&mut self, text: &str, cursor: Option<(usize, usize)>) {
        let mut proposed = self.state.clone();
        proposed.set_preedit(text, cursor);
        self.apply(proposed, EditOrigin::Direct);
    }

    /// 変換中の区間の印を外す（文字は残す。フォーカスが外れる前・Windows の IME が無効になった）。
    pub fn finish_composition(&mut self) {
        if !self.state.is_composing() {
            return;
        }
        let mut proposed = self.state.clone();
        proposed.finish_composition();
        self.apply(proposed, EditOrigin::Direct);
    }

    /// Android の IME が持つ状態の丸ごとの写しを受ける（添字は UTF-16）。
    pub fn apply_platform_state(&mut self, state: &Utf16State) {
        self.apply(TextEditState::from_utf16(state), EditOrigin::PlatformState);
    }

    /// スクリプトから本文と選択を置く（変換中の区間は外す。決まりも当てる）。
    pub fn set_text(&mut self, text: &str, anchor: usize, focus: usize) {
        self.apply(TextEditState::new(text, anchor, focus), EditOrigin::Direct);
    }

    /// スクリプトから選択を置く（本文と変換中の区間はそのまま）。
    pub fn set_selection(&mut self, anchor: usize, focus: usize) {
        let mut proposed = self.state.clone();
        proposed.set_selection(anchor, focus);
        self.apply(proposed, EditOrigin::Direct);
    }

    /// 完了などのアクションを知らせる（IME のアクションのボタン・IPC の注入）。
    pub fn notify_action(&mut self, action: super::config::TextInputAction) {
        self.push_event(TextInputEvent::Action(action));
    }

    /// PC の編集のキーを受ける。
    ///
    /// # 引数
    /// * `key`       - 編集の操作
    /// * `clipboard` - コピー・貼り付けに使うクリップボード
    pub fn apply_key(&mut self, key: EditKey, clipboard: &mut dyn ClipboardAccess) -> KeyOutcome {
        if self.state.is_composing() {
            // 変換中のキーは IME が受ける（ここへ来るのは IME の外から注入したときだけ）。本文は触らない
            return KeyOutcome::Consumed;
        }
        let mut proposed = self.state.clone();
        match key {
            EditKey::Backspace => {
                proposed.delete_backward();
            }
            EditKey::Delete => {
                proposed.delete_forward();
            }
            EditKey::Left { extend } => proposed.move_left(extend),
            EditKey::Right { extend } => proposed.move_right(extend),
            EditKey::Home { extend } => proposed.move_home(extend),
            EditKey::End { extend } => proposed.move_end(extend),
            EditKey::SelectAll => proposed.select_all(),
            EditKey::Enter => {
                if self.config.action.fires_on_enter() {
                    self.notify_action(self.config.action);
                }
                return KeyOutcome::Consumed;
            }
            EditKey::Copy => {
                if self.config.allow_copy && !self.state.is_collapsed() {
                    clipboard.write_text(self.state.selected_text());
                }
                return KeyOutcome::Consumed;
            }
            EditKey::Cut => {
                if !self.config.allow_copy || self.state.is_collapsed() {
                    return KeyOutcome::Consumed;
                }
                if !clipboard.write_text(self.state.selected_text()) {
                    return KeyOutcome::Consumed;
                }
                proposed.delete_backward();
            }
            EditKey::Paste => {
                if !self.config.allow_paste {
                    self.push_event(TextInputEvent::PasteBlocked);
                    return KeyOutcome::Consumed;
                }
                let Some(text) = clipboard.read_text() else { return KeyOutcome::Consumed };
                proposed.commit(&single_line(&text));
            }
        }
        self.apply(proposed, EditOrigin::Direct);
        KeyOutcome::Consumed
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::text_input::clipboard::MemoryClipboard;
    use crate::engine::core::text_input::config::{TextInputAction, TextInputKind};

    fn session(config: TextInputConfig, text: &str) -> TextSession {
        TextSession::new(1, config, TextEditState::with_caret_at_end(text))
    }

    /// 本文の変化は TextChanged、カーソルだけの変化は SelectionChanged。変わらなければ積まない。
    #[test]
    fn events_distinguish_text_and_selection() {
        let mut s = session(TextInputConfig::default(), "ab");
        s.commit("c");
        s.apply_key(EditKey::Left { extend: false }, &mut MemoryClipboard::default());
        s.apply_key(EditKey::Home { extend: false }, &mut MemoryClipboard::default());
        s.apply_key(EditKey::Home { extend: false }, &mut MemoryClipboard::default());
        let events = s.take_events(usize::MAX);
        assert_eq!(
            events,
            vec![TextInputEvent::TextChanged, TextInputEvent::SelectionChanged, TextInputEvent::SelectionChanged]
        );
        assert_eq!(s.revision(), 3);
    }

    /// Enter は設定のアクションを知らせる（None なら何もしない）。本文は変えない。
    #[test]
    fn enter_fires_configured_action() {
        let mut s = session(TextInputConfig { action: TextInputAction::Next, ..TextInputConfig::default() }, "x");
        assert_eq!(s.apply_key(EditKey::Enter, &mut MemoryClipboard::default()), KeyOutcome::Consumed);
        assert_eq!(s.take_events(usize::MAX), vec![TextInputEvent::Action(TextInputAction::Next)]);

        let mut none = session(TextInputConfig { action: TextInputAction::None, ..TextInputConfig::default() }, "x");
        none.apply_key(EditKey::Enter, &mut MemoryClipboard::default());
        assert!(none.take_events(usize::MAX).is_empty());
    }

    /// コピー・切り取り・貼り付け（禁止した欄では何もしない。貼り付けは PasteBlocked を知らせる）。
    #[test]
    fn clipboard_respects_flags() {
        let mut clipboard = MemoryClipboard::default();
        let mut s = session(TextInputConfig::default(), "hello");
        s.apply_key(EditKey::SelectAll, &mut clipboard);
        s.apply_key(EditKey::Cut, &mut clipboard);
        assert_eq!(clipboard.text.as_deref(), Some("hello"));
        assert_eq!(s.state().text, "");
        s.apply_key(EditKey::Paste, &mut clipboard);
        assert_eq!(s.state().text, "hello");

        let locked = TextInputConfig { allow_paste: false, allow_copy: false, ..TextInputConfig::default() };
        let mut guarded = session(locked, "secret");
        let mut other = MemoryClipboard { text: Some("pasted".to_string()) };
        guarded.apply_key(EditKey::SelectAll, &mut other);
        guarded.apply_key(EditKey::Copy, &mut other);
        assert_eq!(other.text.as_deref(), Some("pasted"), "コピーを禁止した欄は書かない");
        guarded.apply_key(EditKey::Cut, &mut other);
        assert_eq!(guarded.state().text, "secret", "切り取りも禁止");
        guarded.take_events(usize::MAX);
        guarded.apply_key(EditKey::Paste, &mut other);
        assert_eq!(guarded.state().text, "secret");
        assert_eq!(guarded.take_events(usize::MAX), vec![TextInputEvent::PasteBlocked]);
    }

    /// Android の状態の写し: 決まりで変えたときだけ IME へ送り返す。数字だけの欄はかなを捨てる。
    #[test]
    fn platform_state_resync_only_when_adjusted() {
        let mut s = session(TextInputConfig { kind: TextInputKind::Number, ..TextInputConfig::default() }, "");
        s.mark_ime_synced();
        s.apply_platform_state(&Utf16State {
            text: "12".to_string(),
            selection_start: 2,
            selection_end: 2,
            composition: None,
        });
        assert_eq!(s.state().text, "12");
        assert!(!s.ime_out_of_sync(), "そのまま受けたなら送り返さない");
        s.apply_platform_state(&Utf16State {
            text: "12あ".to_string(),
            selection_start: 3,
            selection_end: 3,
            composition: Some((2, 3)),
        });
        assert_eq!(s.state().text, "12");
        assert!(s.ime_out_of_sync(), "かなを捨てたので IME へ送り返す");
    }

    /// スクリプトの SetText は IME へ送り返す。初めの本文にも決まりを当てる。
    #[test]
    fn script_changes_mark_out_of_sync() {
        let mut s = session(TextInputConfig { kind: TextInputKind::Number, ..TextInputConfig::default() }, "4a2");
        assert_eq!(s.state().text, "42", "初めの本文にも決まり");
        s.mark_ime_synced();
        s.set_text("7", 1, 1);
        assert!(s.ime_out_of_sync());
    }

    /// 変換中の区間を外すと、文字は残って SelectionChanged。
    #[test]
    fn finish_composition_keeps_text() {
        let mut s = session(TextInputConfig::default(), "");
        s.set_preedit("かな", None);
        s.take_events(usize::MAX);
        s.finish_composition();
        assert_eq!(s.state().text, "かな");
        assert!(!s.state().is_composing());
        assert_eq!(s.take_events(usize::MAX), vec![TextInputEvent::SelectionChanged]);
    }

    /// 出来事は上限を超えたら古いものから捨てる。取り出しは件数で区切れる。
    #[test]
    fn event_queue_is_bounded() {
        let mut s = session(TextInputConfig::default(), "");
        for _ in 0..(MAX_PENDING_EVENTS + 5) {
            s.push_event(TextInputEvent::PasteBlocked);
        }
        assert_eq!(s.take_events(3).len(), 3);
        assert_eq!(s.take_events(usize::MAX).len(), MAX_PENDING_EVENTS - 3);
    }
}
