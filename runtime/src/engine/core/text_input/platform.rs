// ============================================================
//  text_input/platform.rs — エンジン → プラットフォームの文字入力の操作（命令の形と、Android の実装の登録）
//
//  【流れ】（docs/ui_text_input.md §5）
//  ハブ（hub.rs）はフレームの間に積まれた要求（キーボードを出す・隠す・場の開始と終わり・候補窓の位置）と
//  状態の食い違いから、フレームの末尾に命令の並び（PlatformCommand）を 1 度だけ作る（同じフレームの「隠す → 出す」は
//  畳むので、欄から欄へフォーカスを移してもキーボードがちらつかない）。命令を実行するのは:
//    - Android: runtime/android/native が起動時に登録する実装（TextInputPlatform。複製した AndroidApp の
//      set_ime_editor_info → set_text_input_state → show_soft_input・hide_soft_input。E-06 の決定。どのスレッドから
//      呼んでもよい〈GameActivity の UI スレッドの作業の列に順に積まれる〉。I-4）
//    - PC: 登録が無いので App（app/text_input_hooks.rs）がウィンドウで実行する（set_ime_allowed・set_ime_cursor_area と
//      キーボードの模擬）
// ============================================================

use std::sync::OnceLock;

use super::config::{TextInputAction, TextInputKind};
use super::edit_state::Utf16State;

/// プラットフォームへの文字入力の命令。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum PlatformCommand {
    /// 入力の種類と完了のアクションを差し替える（Android の EditorInfo。次の表示で IME が読み直す）。
    SetEditorInfo {
        /// 入力の種類。
        kind: TextInputKind,
        /// 完了などのアクション。
        action: TextInputAction,
    },
    /// IME の本文・選択・変換中の区間を差し替える（Android の set_text_input_state。添字は UTF-16）。
    SetState(Utf16State),
    /// ソフトキーボードを出す（Android の show_soft_input。PC は模擬の高さがあれば模擬のキーボードを出す）。
    ShowKeyboard,
    /// ソフトキーボードを隠す。
    HideKeyboard,
    /// PC: ウィンドウに IME を許可する・しない（winit の set_ime_allowed。数字の欄では許可しない＝直接の文字で入る）。
    AllowIme(bool),
    /// PC: IME の候補窓が避ける矩形（画面の画素。x・y・幅・高さ。winit の set_ime_cursor_area）。
    SetCaretArea([i32; 4]),
}

/// 命令を実行するプラットフォーム（Android の糊が登録する）。
pub trait TextInputPlatform: Send + Sync {
    /// 命令の並びを順に実行する（エンジンのスレッドからフレームの末尾に呼ばれる）。
    fn execute(&self, commands: &[PlatformCommand]);
}

/// 登録されたプラットフォーム（プロセスで 1 つ。PC は登録しない）。
static PLATFORM: OnceLock<Box<dyn TextInputPlatform>> = OnceLock::new();

/// プラットフォームを登録する（Android の android_main が App を作る前に 1 回）。既に登録済みなら false。
pub fn register_platform(platform: Box<dyn TextInputPlatform>) -> bool {
    PLATFORM.set(platform).is_ok()
}

/// 登録されたプラットフォーム（PC は None＝App がウィンドウで実行する）。
pub fn registered_platform() -> Option<&'static dyn TextInputPlatform> {
    PLATFORM.get().map(|platform| platform.as_ref())
}
