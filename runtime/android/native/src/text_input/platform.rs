// ============================================================
//  text_input/platform.rs — エンジンの文字入力の命令を android-activity の API で実行する（W2-6a）
//
//  エンジン（seed_engine の engine::core::text_input::hub）がフレームの末尾に作った命令の並びを、複製した AndroidApp で
//  実行する（エンジンのスレッドから呼ばれる。どの API も GameActivity の UI スレッドの作業の列へ積まれるだけなので速い。I-4）。
//    SetEditorInfo → set_ime_editor_info（入力の種類・アクション・全画面の入力欄を出さない。次の表示で IME が読み直す）
//    SetState      → set_text_input_state（添字は UTF-16 のまま。I-6。IME から同じ状態の stateChanged が返ってくる）
//    ShowKeyboard  → show_soft_input(false)（明示の操作として出す）
//    HideKeyboard  → hide_soft_input(false)
//    AllowIme・SetCaretArea は PC だけの命令（Android は何もしない）
// ============================================================

use std::sync::Mutex;

use seed_engine::engine::core::text_input::{
    PlatformCommand, TextInputAction as EngineAction, TextInputKind, TextInputPlatform, Utf16State, LOG_PREFIX,
};
use winit::platform::android::activity::input::{ImeOptions, InputType, TextInputAction, TextInputState, TextSpan};
use winit::platform::android::activity::AndroidApp;

use crate::logcat;

/// Android の文字入力（複製した AndroidApp を持つ）。
pub struct AndroidTextInput {
    /// 複製した AndroidApp（winit が握っているものと同じ Activity を指す）。
    app: Mutex<AndroidApp>,
}

impl AndroidTextInput {
    /// 作る。
    pub fn new(app: AndroidApp) -> Self {
        Self { app: Mutex::new(app) }
    }
}

impl TextInputPlatform for AndroidTextInput {
    fn execute(&self, commands: &[PlatformCommand]) {
        let app = self.app.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        for command in commands {
            // 確かめ用のログ（本文は出さない）
            logcat::info(&format!("{LOG_PREFIX} 命令: {}", describe(command)));
            match command {
                PlatformCommand::SetEditorInfo { kind, action } => {
                    let action = android_action(*action);
                    // 全画面の入力欄（横画面で出る抽出の UI）を出さない。アクションは imeOptions の下位ビットにも入れる
                    // （IME が表示するボタンは imeOptions のアクション、ハードウェアの Enter は actionId を使う。W2-0 と同じ）
                    let mut options = ImeOptions::IME_FLAG_NO_FULLSCREEN | ImeOptions::IMG_FLAG_NO_EXTRACT_UI;
                    options.set_action(action);
                    app.set_ime_editor_info(input_type(*kind), action, options);
                }
                PlatformCommand::SetState(state) => app.set_text_input_state(text_input_state(state)),
                // 明示の操作として出す・隠す（SHOW_IMPLICIT・HIDE_IMPLICIT_ONLY を付けない）
                PlatformCommand::ShowKeyboard => app.show_soft_input(false),
                PlatformCommand::HideKeyboard => app.hide_soft_input(false),
                PlatformCommand::AllowIme(_) | PlatformCommand::SetCaretArea(_) => {}
            }
        }
    }
}

/// 命令を本文を伏せた短い文字にする（logcat 用）。
fn describe(command: &PlatformCommand) -> String {
    match command {
        PlatformCommand::SetEditorInfo { kind, action } => format!("SetEditorInfo({}, {})", kind.name(), action.name()),
        PlatformCommand::SetState(state) => format!(
            "SetState(長さ {}・選択 {}..{}・変換 {:?})",
            state.text.encode_utf16().count(),
            state.selection_start,
            state.selection_end,
            state.composition
        ),
        other => format!("{other:?}"),
    }
}

/// 入力の種類 → EditorInfo.inputType。
fn input_type(kind: TextInputKind) -> InputType {
    match kind {
        TextInputKind::Text => InputType::TYPE_CLASS_TEXT | InputType::TYPE_TEXT_VARIATION_NORMAL,
        TextInputKind::Number => InputType::TYPE_CLASS_NUMBER | InputType::TYPE_NUMBER_VARIATION_NORMAL,
    }
}

/// エンジンのアクション → android-activity のアクション（どちらも EditorInfo.IME_ACTION_* と同じ値）。
fn android_action(action: EngineAction) -> TextInputAction {
    match action {
        EngineAction::Unspecified => TextInputAction::Unspecified,
        EngineAction::None => TextInputAction::None,
        EngineAction::Go => TextInputAction::Go,
        EngineAction::Search => TextInputAction::Search,
        EngineAction::Send => TextInputAction::Send,
        EngineAction::Next => TextInputAction::Next,
        EngineAction::Done => TextInputAction::Done,
        EngineAction::Previous => TextInputAction::Previous,
    }
}

/// エンジンの状態（UTF-16 の添字）→ android-activity の状態（添字はそのまま Java の String の添字として渡る。I-6）。
fn text_input_state(state: &Utf16State) -> TextInputState {
    TextInputState {
        text: state.text.clone(),
        selection: TextSpan { start: state.selection_start, end: state.selection_end },
        compose_region: state.composition.map(|(start, end)| TextSpan { start, end }),
    }
}
