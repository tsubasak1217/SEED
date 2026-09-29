// ============================================================
//  text_input/inbox.rs — プラットフォーム（Android の IME・IPC の模擬）→ エンジンの文字入力の知らせの箱
//
//  【流れ】（docs/ui_text_input.md §5）
//  Android の MainActivity の受け口（stateChanged・onEditorAction・onSoftwareKeyboardVisibilityChanged・
//  onApplyWindowInsets の IME の高さ）は Java の UI スレッドから JNI（runtime/android/native の text_input）でここへ積む。
//  積んだら描く理由（文字入力）を積んでイベントループを起こす（render_policy の on_demand で止めていても遅れない。W2-10a）。
//  エンジンはフレームの頭（スクリプトの前）で取り出してハブ（hub.rs）へ当てる。
//  箱は上限つき（エンジンが止まっている間に溜まり続けない。超えたら古い知らせから捨てる。状態の写しは後の方が正しい）。
// ============================================================

use std::sync::Mutex;

use crate::engine::core::redraw::{wake as redraw_wake, RedrawReason};

use super::edit_state::Utf16State;

/// プラットフォームからの知らせ。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum PlatformTextMessage {
    /// IME が持つ状態の丸ごとの写し（添字は UTF-16）。
    State(Utf16State),
    /// 完了などのアクション（Android の IME_ACTION_*）。
    Action(i32),
    /// ソフトキーボードの表示が変わった。
    KeyboardVisible(bool),
    /// IME が占める高さ（画面の下端から。画面の画素。0 = 無い）。
    KeyboardHeight(i32),
}

/// 箱に溜める知らせの上限。
pub const MAX_PENDING_MESSAGES: usize = 512;

/// 知らせの箱（どのスレッドからでも積める）。
static INBOX: Mutex<Vec<PlatformTextMessage>> = Mutex::new(Vec::new());

/// 知らせを積んでイベントループを起こす（Android の UI スレッドの JNI から）。
pub fn push(message: PlatformTextMessage) {
    {
        let mut inbox = INBOX.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        if inbox.len() >= MAX_PENDING_MESSAGES {
            inbox.remove(0);
        }
        inbox.push(message);
    }
    redraw_wake::raise(RedrawReason::TextInput);
}

/// 積まれた知らせをすべて取り出す（エンジンのスレッドがフレームの頭で）。
pub fn take_all() -> Vec<PlatformTextMessage> {
    let mut inbox = INBOX.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    std::mem::take(&mut *inbox)
}
