// ============================================================
//  command.rs — 入力注入 IPC コマンドのパース
//
//  「文字列 in / 型 out」の純粋関数だけを置く（副作用なし＝ユニットテスト可能）。
//  ランタイム状態にもパイプにも触れないので、名前付きパイプ無しで全書式を検証できる。
//
//  ワイヤ書式（1 行 1 コマンド。既存 IPC と同じテキストプロトコル）:
//    INPUT_KEY:{keyName},{down|up}
//    INPUT_MOUSE_BUTTON:{left|right|middle},{down|up}
//    INPUT_MOUSE_MOVE:{dx},{dy}
//    INPUT_MOUSE_POS:{x},{y}
//    INPUT_SCROLL:{amount}
//    INPUT_SEQUENCE:{json}
//    INPUT_RELEASE_ALL
// ============================================================

use winit::event::MouseButton;
use winit::keyboard::KeyCode;

use crate::engine::core::input::action_map::key_from_name;

use super::sequence::{parse_sequence_events, InputSequencePlayer};

// ============================================================
//  ワイヤ書式の定数（マジック文字列の集約）
// ============================================================

/// 入力注入コマンド共通の接頭辞。ipc.rs の振り分けと本ファイルで共有する。
pub const INJECT_COMMAND_PREFIX: &str = "INPUT_";

/// 引数の区切り文字。
const ARG_SEPARATOR: char = ',';

/// キー押下/解放コマンド。
const CMD_KEY: &str = "INPUT_KEY:";
/// マウスボタン押下/解放コマンド。
const CMD_MOUSE_BUTTON: &str = "INPUT_MOUSE_BUTTON:";
/// マウス相対移動コマンド。
const CMD_MOUSE_MOVE: &str = "INPUT_MOUSE_MOVE:";
/// マウス絶対座標コマンド。
const CMD_MOUSE_POS: &str = "INPUT_MOUSE_POS:";
/// ホイールコマンド。
const CMD_SCROLL: &str = "INPUT_SCROLL:";
/// 時間軸付きシーケンスコマンド。
const CMD_SEQUENCE: &str = "INPUT_SEQUENCE:";
/// 全解放コマンド（引数なし）。
const CMD_RELEASE_ALL: &str = "INPUT_RELEASE_ALL";

/// 押下を表す引数。
const ARG_DOWN: &str = "down";
/// 解放を表す引数。
const ARG_UP: &str = "up";

/// 左ボタンの引数。
const ARG_MOUSE_LEFT: &str = "left";
/// 右ボタンの引数。
const ARG_MOUSE_RIGHT: &str = "right";
/// 中ボタンの引数。
const ARG_MOUSE_MIDDLE: &str = "middle";

/// 応答の `INPUT_ERROR:{reason}` に載せる理由文字列の最大長。
/// serde のエラーメッセージがそのまま長大になるのを防ぐ（IPC は 1 行 1 コマンド）。
const MAX_REASON_LEN: usize = 200;

/// 理由が空になったときの代替文字列。
const REASON_FALLBACK: &str = "invalid";

// ============================================================
//  InjectAction — 注入される 1 操作
// ============================================================

/// 注入する入力操作 1 件。単発コマンドとシーケンス内イベントで共有する。
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum InjectAction {
    /// キーの押下 / 解放。
    Key { key: KeyCode, down: bool },
    /// マウスボタンの押下 / 解放。
    MouseButton { button: MouseButton, down: bool },
    /// マウスの相対移動（ピクセル）。
    MouseMove { dx: f32, dy: f32 },
    /// マウスの絶対座標（ゲームビューポート左上原点 px）。
    MousePos { x: f32, y: f32 },
    /// ホイール回転量（ライン数。実入力の正規化と同じ単位）。
    Scroll { amount: f32 },
}

// ============================================================
//  InjectCommand — IPC 1 行が表す指示
// ============================================================

/// 入力注入 IPC コマンド 1 件。
///
/// **不正な入力も `Invalid` として型に載せる**のが要点。IPC の受信スレッドには
/// 応答を返す手段が無いため、「解釈できなかった」という事実をアプリ側まで
/// 運んで `INPUT_ERROR:{reason}` を返させる（黙って捨てない）。
#[derive(Debug, Clone)]
pub enum InjectCommand {
    /// 単発の注入操作。
    Action(InjectAction),
    /// 時間軸付きシーケンスの再生開始。
    Sequence(InputSequencePlayer),
    /// 注入中の押下をすべて解放する。
    ReleaseAll,
    /// 解釈できなかった（reason は `INPUT_ERROR:` に載せる短い識別子）。
    Invalid(String),
}

// ============================================================
//  パース
// ============================================================

/// `INPUT_*` の 1 行を [`InjectCommand`] へ変換する。
///
/// 接頭辞 `INPUT_` を持たない行だけ `None`（＝他のコマンド解析へ譲る）。
/// 接頭辞を持つ限り、引数が壊れていても `Invalid` を返して必ず応答させる。
pub fn parse_inject_command(line: &str) -> Option<InjectCommand> {
    let line = line.trim();
    if !line.starts_with(INJECT_COMMAND_PREFIX) {
        return None;
    }

    // 引数なしコマンドを先に確定させる。
    if line == CMD_RELEASE_ALL {
        return Some(InjectCommand::ReleaseAll);
    }

    let cmd = if let Some(rest) = line.strip_prefix(CMD_KEY) {
        parse_key(rest)
    } else if let Some(rest) = line.strip_prefix(CMD_MOUSE_BUTTON) {
        parse_mouse_button(rest)
    } else if let Some(rest) = line.strip_prefix(CMD_MOUSE_MOVE) {
        match parse_two_floats(rest) {
            Ok((dx, dy)) => InjectCommand::Action(InjectAction::MouseMove { dx, dy }),
            Err(reason) => InjectCommand::Invalid(reason),
        }
    } else if let Some(rest) = line.strip_prefix(CMD_MOUSE_POS) {
        match parse_two_floats(rest) {
            Ok((x, y)) => InjectCommand::Action(InjectAction::MousePos { x, y }),
            Err(reason) => InjectCommand::Invalid(reason),
        }
    } else if let Some(rest) = line.strip_prefix(CMD_SCROLL) {
        parse_scroll(rest)
    } else if let Some(rest) = line.strip_prefix(CMD_SEQUENCE) {
        parse_sequence(rest)
    } else {
        InjectCommand::Invalid(sanitize_reason("unknown_command"))
    };

    Some(cmd)
}

/// `INPUT_KEY:{keyName},{down|up}`
///
/// keyName は InputMap と**同じ表**（`action_map::key_from_name`）で解決する。
/// 表を二重に持たないことで、エディタのキー一覧・InputMap・注入の 3 者が必ず一致する。
fn parse_key(rest: &str) -> InjectCommand {
    let Some((name, state)) = split_two(rest) else {
        return InjectCommand::Invalid(sanitize_reason("bad_args"));
    };
    let Some(key) = key_from_name(name) else {
        return InjectCommand::Invalid(sanitize_reason(&format!("unknown_key:{name}")));
    };
    match parse_down_up(state) {
        Some(down) => InjectCommand::Action(InjectAction::Key { key, down }),
        None => InjectCommand::Invalid(sanitize_reason(&format!("bad_key_state:{state}"))),
    }
}

/// `INPUT_MOUSE_BUTTON:{left|right|middle},{down|up}`
fn parse_mouse_button(rest: &str) -> InjectCommand {
    let Some((name, state)) = split_two(rest) else {
        return InjectCommand::Invalid(sanitize_reason("bad_args"));
    };
    let Some(button) = mouse_button_name_to_code(name) else {
        return InjectCommand::Invalid(sanitize_reason(&format!("unknown_mouse_button:{name}")));
    };
    match parse_down_up(state) {
        Some(down) => InjectCommand::Action(InjectAction::MouseButton { button, down }),
        None => InjectCommand::Invalid(sanitize_reason(&format!("bad_key_state:{state}"))),
    }
}

/// `INPUT_SCROLL:{amount}`
fn parse_scroll(rest: &str) -> InjectCommand {
    match rest.trim().parse::<f32>() {
        Ok(amount) if amount.is_finite() => InjectCommand::Action(InjectAction::Scroll { amount }),
        _ => InjectCommand::Invalid(sanitize_reason("bad_number")),
    }
}

/// `INPUT_SEQUENCE:{json}`
fn parse_sequence(rest: &str) -> InjectCommand {
    match parse_sequence_events(rest) {
        Ok(events) => InjectCommand::Sequence(InputSequencePlayer::new(events)),
        Err(reason) => InjectCommand::Invalid(sanitize_reason(&reason)),
    }
}

// ============================================================
//  小さな共有ヘルパー
// ============================================================

/// カンマ区切りのちょうど 2 引数へ分割する（余分な引数があれば拒否）。
fn split_two(rest: &str) -> Option<(&str, &str)> {
    let mut it = rest.split(ARG_SEPARATOR);
    let a = it.next()?.trim();
    let b = it.next()?.trim();
    if it.next().is_some() || a.is_empty() || b.is_empty() {
        return None;
    }
    Some((a, b))
}

/// カンマ区切りの 2 実数を読む（`INPUT_MOUSE_MOVE` / `INPUT_MOUSE_POS` 共通）。
///
/// NaN / 無限大は拒否する。そのまま状態へ入れると座標や差分が汚染され、
/// 以後どの入力でも回復しなくなるため（比較が常に false になる）。
fn parse_two_floats(rest: &str) -> Result<(f32, f32), String> {
    let Some((a, b)) = split_two(rest) else {
        return Err(sanitize_reason("bad_args"));
    };
    match (a.parse::<f32>(), b.parse::<f32>()) {
        (Ok(x), Ok(y)) if x.is_finite() && y.is_finite() => Ok((x, y)),
        _ => Err(sanitize_reason("bad_number")),
    }
}

/// `down` / `up` を bool へ（大文字小文字は問わない）。
fn parse_down_up(s: &str) -> Option<bool> {
    match s.to_ascii_lowercase().as_str() {
        ARG_DOWN => Some(true),
        ARG_UP => Some(false),
        _ => None,
    }
}

/// キー名を winit の `KeyCode` へ（InputMap と同じ表を使う）。
///
/// シーケンス解釈（sequence.rs）からも使うため、モジュール内へ公開する薄い窓口。
/// 表そのものは `action_map::key_from_name` が正典で、ここには複製を作らない。
pub(super) fn key_name_to_code(name: &str) -> Option<KeyCode> {
    key_from_name(name)
}

/// マウスボタン名を winit の `MouseButton` へ（大文字小文字は問わない）。
pub(super) fn mouse_button_name_to_code(name: &str) -> Option<MouseButton> {
    match name.to_ascii_lowercase().as_str() {
        ARG_MOUSE_LEFT => Some(MouseButton::Left),
        ARG_MOUSE_RIGHT => Some(MouseButton::Right),
        ARG_MOUSE_MIDDLE => Some(MouseButton::Middle),
        _ => None,
    }
}

/// 理由文字列を 1 行の短い識別子へ整える。
///
/// IPC は 1 行 1 コマンドなので、改行を含むメッセージ（serde のエラー等）を
/// そのまま流すとプロトコルが壊れる。制御文字を空白へ潰し、長さも切り詰める。
pub(crate) fn sanitize_reason(reason: &str) -> String {
    let mut s: String = reason
        .chars()
        .map(|c| if c.is_control() { ' ' } else { c })
        .collect();
    s = s.trim().to_string();
    if s.chars().count() > MAX_REASON_LEN {
        s = s.chars().take(MAX_REASON_LEN).collect();
    }
    if s.is_empty() {
        REASON_FALLBACK.to_string()
    } else {
        s
    }
}

// ============================================================
//  ユニットテスト（全書式・不正値）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト補助: 単発アクションを取り出す（違う種別なら panic）。
    fn action(line: &str) -> InjectAction {
        match parse_inject_command(line) {
            Some(InjectCommand::Action(a)) => a,
            other => panic!("単発アクションを期待した: {other:?}"),
        }
    }

    /// テスト補助: 不正コマンドの理由を取り出す。
    fn invalid_reason(line: &str) -> String {
        match parse_inject_command(line) {
            Some(InjectCommand::Invalid(r)) => r,
            other => panic!("Invalid を期待した: {other:?}"),
        }
    }

    /// 接頭辞を持たない行は他コマンドへ譲る。
    #[test]
    fn non_input_lines_are_not_claimed() {
        assert!(parse_inject_command("SCREENSHOT:game,C:/a.png").is_none());
        assert!(parse_inject_command("PAUSE").is_none());
    }

    /// キーの押下・解放。
    #[test]
    fn parses_key_commands() {
        assert_eq!(
            action("INPUT_KEY:W,down"),
            InjectAction::Key { key: KeyCode::KeyW, down: true }
        );
        assert_eq!(
            action("INPUT_KEY:LeftShift,up"),
            InjectAction::Key { key: KeyCode::ShiftLeft, down: false }
        );
        assert_eq!(
            action("INPUT_KEY:Space,DOWN"),
            InjectAction::Key { key: KeyCode::Space, down: true },
        );
        assert_eq!(
            action("INPUT_KEY: Enter , up "),
            InjectAction::Key { key: KeyCode::Enter, down: false },
        );
    }

    /// キー名・状態の不正はそれぞれ別の理由で弾く。
    #[test]
    fn rejects_bad_key_commands() {
        assert!(invalid_reason("INPUT_KEY:NoSuchKey,down").starts_with("unknown_key:"));
        assert!(invalid_reason("INPUT_KEY:W,sideways").starts_with("bad_key_state:"));
        assert_eq!(invalid_reason("INPUT_KEY:W"), "bad_args");
        assert_eq!(invalid_reason("INPUT_KEY:W,down,extra"), "bad_args");
        assert_eq!(invalid_reason("INPUT_KEY:,down"), "bad_args");
    }

    /// マウスボタン。
    #[test]
    fn parses_mouse_button_commands() {
        assert_eq!(
            action("INPUT_MOUSE_BUTTON:left,down"),
            InjectAction::MouseButton { button: MouseButton::Left, down: true }
        );
        assert_eq!(
            action("INPUT_MOUSE_BUTTON:Right,up"),
            InjectAction::MouseButton { button: MouseButton::Right, down: false }
        );
        assert_eq!(
            action("INPUT_MOUSE_BUTTON:middle,down"),
            InjectAction::MouseButton { button: MouseButton::Middle, down: true }
        );
        assert!(invalid_reason("INPUT_MOUSE_BUTTON:x1,down").starts_with("unknown_mouse_button:"));
    }

    /// 相対移動・絶対座標・ホイール。
    #[test]
    fn parses_motion_commands() {
        assert_eq!(
            action("INPUT_MOUSE_MOVE:120,-4.5"),
            InjectAction::MouseMove { dx: 120.0, dy: -4.5 }
        );
        assert_eq!(
            action("INPUT_MOUSE_POS:640,360"),
            InjectAction::MousePos { x: 640.0, y: 360.0 }
        );
        assert_eq!(action("INPUT_SCROLL:-2"), InjectAction::Scroll { amount: -2.0 });
    }

    /// 数値として読めない・有限でない値は拒否する。
    #[test]
    fn rejects_non_finite_and_malformed_numbers() {
        assert_eq!(invalid_reason("INPUT_MOUSE_MOVE:abc,0"), "bad_number");
        assert_eq!(invalid_reason("INPUT_MOUSE_MOVE:NaN,0"), "bad_number");
        assert_eq!(invalid_reason("INPUT_MOUSE_POS:inf,0"), "bad_number");
        assert_eq!(invalid_reason("INPUT_MOUSE_POS:1"), "bad_args");
        assert_eq!(invalid_reason("INPUT_SCROLL:"), "bad_number");
        assert_eq!(invalid_reason("INPUT_SCROLL:NaN"), "bad_number");
    }

    /// 全解放と未知コマンド。
    #[test]
    fn parses_release_all_and_rejects_unknown() {
        assert!(matches!(
            parse_inject_command("INPUT_RELEASE_ALL"),
            Some(InjectCommand::ReleaseAll)
        ));
        assert_eq!(invalid_reason("INPUT_TELEPORT:1,2"), "unknown_command");
    }

    /// シーケンスは JSON を解釈してプレイヤーを作る。
    #[test]
    fn parses_sequence_command() {
        let line = r#"INPUT_SEQUENCE:[{"t":0.0,"key":"W","down":true},{"t":0.5,"mouse_move":[120,0]}]"#;
        match parse_inject_command(line) {
            Some(InjectCommand::Sequence(p)) => assert_eq!(p.remaining(), 2),
            other => panic!("Sequence を期待した: {other:?}"),
        }
        assert!(invalid_reason("INPUT_SEQUENCE:{not json").starts_with("bad_json"));
        assert_eq!(invalid_reason("INPUT_SEQUENCE:[]"), "empty_sequence");
    }

    /// 理由文字列に改行が混ざっても 1 行を保つ。
    #[test]
    fn reason_is_single_line_and_bounded() {
        let r = sanitize_reason("bad\njson: line 1\ncolumn 2");
        assert!(!r.contains('\n'));
        let long = sanitize_reason(&"x".repeat(MAX_REASON_LEN * 2));
        assert_eq!(long.chars().count(), MAX_REASON_LEN);
    }
}
