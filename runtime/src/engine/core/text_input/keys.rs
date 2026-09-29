// ============================================================
//  text_input/keys.rs — PC のキー（winit の KeyEvent・IPC の注入のキー）→ 入力欄の編集の操作【純関数・単体テスト付き】
//
//  入力欄にフォーカスがある間（Play 中）、PC のキーの押下は先にここで読み、編集の操作（EditKey）か
//  入れる文字（KeyEvent.text）になるものは入力欄が受ける（app/text_input_hooks.rs）。
//
//  【変換中のキー】Windows の日本語の IME が変換している間のキーは、winit では論理キーが NamedKey::Process
//  （VK_PROCESSKEY）になり、IME が受けている（確定の Enter・変換中の Backspace を入力欄の操作にしない）。
//  物理キーは押した場所のまま（Enter など）なので、論理キーを見て捨てる。
//  【Ctrl の組み合わせ】Ctrl+A/C/V/X は物理キーの位置で読む（英字の配列の位置。AZERTY などの配列では位置が違う。
//  docs/backlog.md）。Windows の慣習の Shift+Insert（貼り付け）・Ctrl+Insert（コピー）・Shift+Delete（切り取り）も読む。
// ============================================================

use winit::keyboard::{Key, KeyCode, NamedKey};

/// 入力欄の編集の操作。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EditKey {
    /// 前を消す。
    Backspace,
    /// 後ろを消す。
    Delete,
    /// 左へ（extend で選択を伸ばす）。
    Left {
        /// Shift で選択を伸ばすか。
        extend: bool,
    },
    /// 右へ。
    Right {
        /// Shift で選択を伸ばすか。
        extend: bool,
    },
    /// 先頭へ。
    Home {
        /// Shift で選択を伸ばすか。
        extend: bool,
    },
    /// 末尾へ。
    End {
        /// Shift で選択を伸ばすか。
        extend: bool,
    },
    /// Enter（1 行の入力欄では完了などのアクション）。
    Enter,
    /// すべてを選ぶ（Ctrl+A）。
    SelectAll,
    /// コピー（Ctrl+C・Ctrl+Insert）。
    Copy,
    /// 切り取り（Ctrl+X・Shift+Delete）。
    Cut,
    /// 貼り付け（Ctrl+V・Shift+Insert）。
    Paste,
}

/// 押している修飾キー。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub struct EditModifiers {
    /// Shift。
    pub shift: bool,
    /// Ctrl。
    pub ctrl: bool,
}

/// 物理キーと修飾キーから編集の操作を決める（IPC の注入のキーはこれだけで決める）。
///
/// # 戻り値
/// 編集の操作（入力欄が受けるキーでなければ None）
pub fn edit_key_from_code(code: KeyCode, modifiers: EditModifiers) -> Option<EditKey> {
    let extend = modifiers.shift;
    let key = match code {
        KeyCode::Backspace => EditKey::Backspace,
        KeyCode::Delete if modifiers.shift => EditKey::Cut,
        KeyCode::Delete => EditKey::Delete,
        KeyCode::ArrowLeft => EditKey::Left { extend },
        KeyCode::ArrowRight => EditKey::Right { extend },
        KeyCode::Home => EditKey::Home { extend },
        KeyCode::End => EditKey::End { extend },
        KeyCode::Enter | KeyCode::NumpadEnter => EditKey::Enter,
        KeyCode::Insert if modifiers.shift => EditKey::Paste,
        KeyCode::Insert if modifiers.ctrl => EditKey::Copy,
        KeyCode::KeyA if modifiers.ctrl => EditKey::SelectAll,
        KeyCode::KeyC if modifiers.ctrl => EditKey::Copy,
        KeyCode::KeyX if modifiers.ctrl => EditKey::Cut,
        KeyCode::KeyV if modifiers.ctrl => EditKey::Paste,
        _ => return None,
    };
    Some(key)
}

/// winit のキーの押下から編集の操作を決める（変換中〈論理キーが Process〉は None＝IME が受けている）。
pub fn edit_key_from_winit(logical: &Key, physical: KeyCode, modifiers: EditModifiers) -> Option<EditKey> {
    if is_ime_processing(logical) {
        return None;
    }
    edit_key_from_code(physical, modifiers)
}

/// 論理キーが「IME が処理中」（Windows の VK_PROCESSKEY）か。
pub fn is_ime_processing(logical: &Key) -> bool {
    matches!(logical, Key::Named(NamedKey::Process))
}

/// キーの押下が入れる文字（winit の KeyEvent.text）を入力欄へ入れてよい文字列にする。
/// 制御文字（Backspace の \u{8}・Enter の \r・Ctrl+A の \u{1} など）を含むものは入れない（None）。
pub fn insertable_text(text: Option<&str>) -> Option<&str> {
    let text = text?;
    if text.is_empty() || text.chars().any(char::is_control) {
        return None;
    }
    Some(text)
}

/// 貼り付ける文字列を 1 行にする（改行は空白へ、復帰は捨てる）。
pub fn single_line(text: &str) -> String {
    text.chars()
        .filter(|&ch| ch != '\r')
        .map(|ch| if ch == '\n' { ' ' } else { ch })
        .collect()
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    const NONE: EditModifiers = EditModifiers { shift: false, ctrl: false };
    const SHIFT: EditModifiers = EditModifiers { shift: true, ctrl: false };
    const CTRL: EditModifiers = EditModifiers { shift: false, ctrl: true };

    /// 編集のキーと Ctrl の組み合わせ。英字だけの押下は編集の操作ではない（文字として KeyEvent.text で入る）。
    #[test]
    fn maps_edit_keys() {
        assert_eq!(edit_key_from_code(KeyCode::Backspace, NONE), Some(EditKey::Backspace));
        assert_eq!(edit_key_from_code(KeyCode::ArrowLeft, SHIFT), Some(EditKey::Left { extend: true }));
        assert_eq!(edit_key_from_code(KeyCode::End, NONE), Some(EditKey::End { extend: false }));
        assert_eq!(edit_key_from_code(KeyCode::KeyA, CTRL), Some(EditKey::SelectAll));
        assert_eq!(edit_key_from_code(KeyCode::KeyV, CTRL), Some(EditKey::Paste));
        assert_eq!(edit_key_from_code(KeyCode::Insert, SHIFT), Some(EditKey::Paste));
        assert_eq!(edit_key_from_code(KeyCode::Delete, SHIFT), Some(EditKey::Cut));
        assert_eq!(edit_key_from_code(KeyCode::KeyA, NONE), None);
        assert_eq!(edit_key_from_code(KeyCode::Escape, NONE), None, "Escape は戻るの段（BackDispatcher）が受ける");
    }

    /// 変換中（論理キーが Process）の押下は IME が受けているので入力欄の操作にしない。
    #[test]
    fn ime_processing_keys_are_ignored() {
        let process = Key::Named(NamedKey::Process);
        assert_eq!(edit_key_from_winit(&process, KeyCode::Enter, NONE), None);
        let enter = Key::Named(NamedKey::Enter);
        assert_eq!(edit_key_from_winit(&enter, KeyCode::Enter, NONE), Some(EditKey::Enter));
    }

    /// 入れてよい文字（制御文字は入れない）と、貼り付けの 1 行化。
    #[test]
    fn insertable_text_and_single_line() {
        assert_eq!(insertable_text(Some("a")), Some("a"));
        assert_eq!(insertable_text(Some("あ")), Some("あ"));
        assert_eq!(insertable_text(Some("\u{8}")), None);
        assert_eq!(insertable_text(Some("\r")), None);
        assert_eq!(insertable_text(None), None);
        assert_eq!(single_line("a\r\nb\nc"), "a b c");
    }
}
