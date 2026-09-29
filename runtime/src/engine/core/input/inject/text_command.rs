// ============================================================
//  inject/text_command.rs — 文字入力の注入 IPC（INPUT_TEXT:）のパース【純関数・単体テスト付き】
//
//  PC では日本語の IME を自動では打てず、Android の IME の知らせ（状態の写し・アクション・キーボードの高さ）も
//  PC には無いので、入力欄（W2-6。docs/ui_text_input.md §8）の確かめのために、その受け口へ直接入れる命令を用意する。
//  どれも Play 中だけ受ける（INPUT_* と同じ）。応答は INPUT_OK / INPUT_ERROR:{理由}、dump だけ INPUT_TEXT_STATE:{JSON}。
//
//  ワイヤ書式（1 行 1 命令。文字列は最後の引数で、カンマを含んでよい）:
//    INPUT_TEXT:commit,{文字列}                     … 確定（Windows の Ime::Commit と同じ受け口）
//    INPUT_TEXT:preedit,{カーソル},{文字列}          … 変換中の文字列（Ime::Preedit。カーソルは文字列の中の文字の番号・-1 = 末尾。
//                                                     空の文字列 = 変換の取り消し）
//    INPUT_TEXT:key,{キー名}[,ctrl][,shift]          … 編集のキー（Backspace・LeftArrow・Home・A,ctrl など。名前は INPUT_KEY と
//                                                     同じ表〈action_map::key_from_name〉）
//    INPUT_TEXT:action,{名前}                        … 完了などのアクション（done・next・go・search・send・previous・none）
//    INPUT_TEXT:platform_state,{選択の起点},{動く端},{変換の始め},{変換の終わり},{文字列}
//                                                   … Android の IME の状態の写し（添字は UTF-16。変換なしは -1,-1）
//    INPUT_TEXT:keyboard,{高さの画素}                … PC のキーボードの模擬の高さ（0 = 模擬しない）
//    INPUT_TEXT:dump                                 … 今の場の状態を INPUT_TEXT_STATE:{JSON} で返す
//  {文字列} は " で始めると JSON の文字列として読む（IPC の読み取りは行の前後の空白を落とすので、前後の空白・改行を
//  入れたいときは "  田中  " のように書く）。" で始まらなければそのままの文字列。
// ============================================================

use winit::keyboard::KeyCode;

use crate::engine::core::input::action_map::key_from_name;
use crate::engine::core::text_input::{EditModifiers, TextInputAction, Utf16State};

/// 文字入力の注入の命令の接頭辞。
pub const CMD_TEXT: &str = "INPUT_TEXT:";

/// 引数の区切り。
const SEPARATOR: char = ',';
/// 修飾の Ctrl。
const MODIFIER_CTRL: &str = "ctrl";
/// 修飾の Shift。
const MODIFIER_SHIFT: &str = "shift";
/// 「末尾」「無し」を表す添字。
const INDEX_NONE: i64 = -1;

/// 命令の名前。
const OP_COMMIT: &str = "commit";
const OP_PREEDIT: &str = "preedit";
const OP_KEY: &str = "key";
const OP_ACTION: &str = "action";
const OP_PLATFORM_STATE: &str = "platform_state";
const OP_KEYBOARD: &str = "keyboard";
const OP_DUMP: &str = "dump";

/// platform_state の数の引数の数（選択の 2 つ・変換中の区間の 2 つ）。
const PLATFORM_STATE_NUMBERS: usize = 4;

/// 文字入力の注入の命令。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum TextInjectCommand {
    /// 確定。
    Commit(String),
    /// 変換中の文字列（カーソルは文字列の中のバイト位置。None = 末尾）。
    Preedit {
        /// 変換中の文字列。
        text: String,
        /// カーソル（バイト位置）。
        cursor: Option<usize>,
    },
    /// 編集のキー。
    Key {
        /// 物理キー。
        code: KeyCode,
        /// 修飾キー。
        modifiers: EditModifiers,
    },
    /// 完了などのアクション。
    Action(TextInputAction),
    /// Android の IME の状態の写し。
    PlatformState(Utf16State),
    /// PC のキーボードの模擬の高さ（0 以下 = 模擬しない）。
    Keyboard(i32),
    /// 今の場の状態を返す。
    Dump,
}

/// JSON の文字列として読む印（引数の先頭の "）。
const JSON_STRING_QUOTE: char = '"';

/// 文字列の引数を読む（" で始まれば JSON の文字列、そうでなければそのまま）。
fn decode_text(arg: &str) -> Result<String, String> {
    if arg.starts_with(JSON_STRING_QUOTE) {
        serde_json::from_str::<String>(arg).map_err(|_| "bad_json_string".to_string())
    } else {
        Ok(arg.to_string())
    }
}

/// `INPUT_TEXT:` の後ろを読む。
///
/// # 戻り値
/// 命令（読めなければ短い理由の文字列）
pub fn parse_text_command(rest: &str) -> Result<TextInjectCommand, String> {
    let (op, args) = rest.split_once(SEPARATOR).unwrap_or((rest, ""));
    match op.trim() {
        OP_COMMIT => decode_text(args).map(TextInjectCommand::Commit),
        OP_PREEDIT => parse_preedit(args),
        OP_KEY => parse_key(args),
        OP_ACTION => TextInputAction::from_name(args.trim())
            .map(TextInjectCommand::Action)
            .ok_or_else(|| format!("unknown_action:{}", args.trim())),
        OP_PLATFORM_STATE => parse_platform_state(args),
        OP_KEYBOARD => args.trim().parse::<i32>().map(TextInjectCommand::Keyboard).map_err(|_| "bad_number".to_string()),
        OP_DUMP => Ok(TextInjectCommand::Dump),
        other => Err(format!("unknown_text_op:{other}")),
    }
}

/// `preedit,{カーソル},{文字列}`（カーソルは文字の番号 → バイト位置へ）。
fn parse_preedit(args: &str) -> Result<TextInjectCommand, String> {
    let (cursor, raw_text) = args.split_once(SEPARATOR).ok_or_else(|| "bad_args".to_string())?;
    let text = decode_text(raw_text)?;
    let text = text.as_str();
    let cursor: i64 = cursor.trim().parse().map_err(|_| "bad_number".to_string())?;
    let cursor = if cursor == INDEX_NONE {
        None
    } else {
        let chars = usize::try_from(cursor).map_err(|_| "bad_number".to_string())?;
        Some(text.char_indices().nth(chars).map_or(text.len(), |(byte, _)| byte))
    };
    Ok(TextInjectCommand::Preedit { text: text.to_string(), cursor })
}

/// `key,{キー名}[,ctrl][,shift]`。
fn parse_key(args: &str) -> Result<TextInjectCommand, String> {
    let mut parts = args.split(SEPARATOR).map(str::trim);
    let name = parts.next().filter(|name| !name.is_empty()).ok_or_else(|| "bad_args".to_string())?;
    let code = key_from_name(name).ok_or_else(|| format!("unknown_key:{name}"))?;
    let mut modifiers = EditModifiers::default();
    for modifier in parts {
        match modifier {
            MODIFIER_CTRL => modifiers.ctrl = true,
            MODIFIER_SHIFT => modifiers.shift = true,
            other => return Err(format!("unknown_modifier:{other}")),
        }
    }
    Ok(TextInjectCommand::Key { code, modifiers })
}

/// `platform_state,{起点},{動く端},{変換の始め},{変換の終わり},{文字列}`。
fn parse_platform_state(args: &str) -> Result<TextInjectCommand, String> {
    let mut rest = args;
    let mut numbers = [0i64; PLATFORM_STATE_NUMBERS];
    for slot in &mut numbers {
        let (number, tail) = rest.split_once(SEPARATOR).ok_or_else(|| "bad_args".to_string())?;
        *slot = number.trim().parse().map_err(|_| "bad_number".to_string())?;
        rest = tail;
    }
    let [start, end, comp_start, comp_end] = numbers;
    let index = |value: i64| usize::try_from(value).map_err(|_| "bad_number".to_string());
    let composition = if comp_start == INDEX_NONE || comp_end == INDEX_NONE {
        None
    } else {
        Some((index(comp_start)?, index(comp_end)?))
    };
    Ok(TextInjectCommand::PlatformState(Utf16State {
        text: decode_text(rest)?,
        selection_start: index(start)?,
        selection_end: index(end)?,
        composition,
    }))
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 各命令の書式（文字列は最後でカンマを含んでよい）。
    #[test]
    fn parses_each_op() {
        assert_eq!(parse_text_command("commit,a,b"), Ok(TextInjectCommand::Commit("a,b".into())));
        assert_eq!(parse_text_command("commit,\"  田中 \""), Ok(TextInjectCommand::Commit("  田中 ".into())));
        assert_eq!(
            parse_text_command("preedit,-1,かんじ"),
            Ok(TextInjectCommand::Preedit { text: "かんじ".into(), cursor: None })
        );
        assert_eq!(
            parse_text_command("preedit,1,かんじ"),
            Ok(TextInjectCommand::Preedit { text: "かんじ".into(), cursor: Some(3) })
        );
        assert_eq!(
            parse_text_command("key,A,ctrl"),
            Ok(TextInjectCommand::Key { code: KeyCode::KeyA, modifiers: EditModifiers { ctrl: true, shift: false } })
        );
        assert_eq!(
            parse_text_command("key,Home,shift"),
            Ok(TextInjectCommand::Key { code: KeyCode::Home, modifiers: EditModifiers { ctrl: false, shift: true } })
        );
        assert_eq!(parse_text_command("action,next"), Ok(TextInjectCommand::Action(TextInputAction::Next)));
        assert_eq!(parse_text_command("keyboard,900"), Ok(TextInjectCommand::Keyboard(900)));
        assert_eq!(parse_text_command("dump"), Ok(TextInjectCommand::Dump));
        assert_eq!(
            parse_text_command("platform_state,3,3,-1,-1,1,2"),
            Ok(TextInjectCommand::PlatformState(Utf16State {
                text: "1,2".into(),
                selection_start: 3,
                selection_end: 3,
                composition: None
            }))
        );
    }

    /// 読めない命令は理由を返す（黙って捨てない）。
    #[test]
    fn rejects_bad_commands() {
        assert!(parse_text_command("bogus").is_err());
        assert!(parse_text_command("key,NoSuchKey").is_err());
        assert!(parse_text_command("key,KeyA,alt").is_err());
        assert!(parse_text_command("action,jump").is_err());
        assert!(parse_text_command("preedit,x,abc").is_err());
        assert!(parse_text_command("platform_state,1,2,abc").is_err());
        assert!(parse_text_command("keyboard,tall").is_err());
    }
}
