// ============================================================
//  migrate_json.rs — 標準入出力 1 件だけを変換する CLI
//                    （`SEED.exe --migrate-json <kind>`）
//
//  【何のためにあるか】
//  `.anim` / `.inputmap` / `.sprite_mesh` / 地形 JSON / `project_settings.json` は
//  **書き手がエディタ（C#）にしか無い**。C# 側がこれらを読むとき、旧形式のまま
//  解釈してしまうと「ランタイムは変換して読むのに、エディタは変換せずに読む」という
//  食い違いが生まれる（`.inputmap` の二重実装が既にその負債）。
//
//  変換をランタイム 1 か所に保ったまま C# から使えるようにするため、
//  **標準入力の JSON を現行版へ持ち上げて標準出力へ返すだけ**の口を用意する。
//  エディタはファイルを読んだら、解釈する前にこのコマンドへ通す。
//
//  【約束】
//  | 項目 | 内容 |
//  |------|------|
//  | 引数 | `--migrate-json <kind>` または `--migrate-json=<kind>`。`kind` は `FormatKind::label()` |
//  | 入力 | 標準入力に JSON テキスト 1 件（先頭 BOM は許容） |
//  | 出力 | 標準出力に、現行版へ変換した pretty JSON（版の欄も現行版に更新済み） |
//  | 失敗 | 標準エラーへ 1 行 JSON `{"kind":"error","error":"<code>","message":"<日本語>"}` |
//  | 終了 | 0=成功 / 1=未来版 / 2=引数が不正 / 3=解析・変換の失敗 |
//
//  **ウィンドウも GPU も作らない**。`main` の入口で分岐してここで完結する。
//
//  【欄の並びについて】
//  出力は `serde_json::Value` を直列化したものなので、**元ファイルの欄の並びは
//  保たれない**（`serde_json` の既定＝キー名の昇順になる）。
//  読み手はキーを引いて使うので問題にならない。
//  ファイルの整形を保ったまま書き戻したいときは `--upgrade-project` を使うこと。
// ============================================================

use std::io::{Read, Write};

use serde_json::Value;

use super::kind::FormatKind;
use super::{migrate_to_current, MigrationError};

// ── 定数 ─────────────────────────────────────────────────────────

/// 1 件変換を要求するコマンドライン引数。
pub const MIGRATE_JSON_FLAG: &str = "--migrate-json";

/// 正常終了。
const EXIT_OK: i32 = 0;
/// 未来版（このエンジンでは変換できない）。
const EXIT_FUTURE_VERSION: i32 = 1;
/// 引数が不正（形式名が無い・未知の形式）。
const EXIT_BAD_USAGE: i32 = 2;
/// 解析・変換の失敗。
const EXIT_FAILED: i32 = 3;

/// エラー行の `error` に入れる分類（終了コードと 1 対 1 で対応させる）。
const ERROR_CODE_FUTURE_VERSION: &str = "future_version";
const ERROR_CODE_BAD_USAGE: &str = "bad_usage";
const ERROR_CODE_FAILED: &str = "failed";

// ── コマンドラインの入口 ─────────────────────────────────────────

/// 引数に `--migrate-json` があれば 1 件変換を実行し、終了コードを返す。
///
/// 無ければ `None` を返す（通常起動を続ける合図）。
pub fn run_cli_if_requested(args: &[String]) -> Option<i32> {
    let requested = parse_kind_arg(args)?;

    let stdout = std::io::stdout();
    let mut out = stdout.lock();
    let stderr = std::io::stderr();
    let mut err = stderr.lock();

    // ── 形式名を引く ──
    let Some(kind) = FormatKind::from_label(&requested) else {
        write_error(
            &mut err,
            ERROR_CODE_BAD_USAGE,
            &format!(
                "未知の形式です: {:?}（指定できるのは {}）",
                requested,
                FormatKind::all_labels()
            ),
        );
        return Some(EXIT_BAD_USAGE);
    };

    // ── 標準入力を読む ──
    let mut input = String::new();
    if let Err(e) = std::io::stdin().read_to_string(&mut input) {
        write_error(
            &mut err,
            ERROR_CODE_FAILED,
            &format!("標準入力を読めませんでした: {e}"),
        );
        return Some(EXIT_FAILED);
    }

    // ── 変換して書く ──
    match migrate_text(kind, &input) {
        Ok(text) => {
            let _ = writeln!(out, "{text}");
            let _ = out.flush();
            Some(EXIT_OK)
        }
        Err(e) => {
            let code = match e {
                MigrationError::FutureVersion { .. } => ERROR_CODE_FUTURE_VERSION,
                _ => ERROR_CODE_FAILED,
            };
            write_error(&mut err, code, &e.to_string());
            let _ = err.flush();
            Some(match e {
                MigrationError::FutureVersion { .. } => EXIT_FUTURE_VERSION,
                _ => EXIT_FAILED,
            })
        }
    }
}

/// `--migrate-json` の値（形式名）を取り出す。指定が無ければ `None`。
///
/// `--migrate-json=<kind>` と `--migrate-json <kind>` の両方を受け付ける。
/// 値が無い（次の引数もフラグ）場合は空文字を返し、呼び出し側が usage エラーにする。
fn parse_kind_arg(args: &[String]) -> Option<String> {
    let eq_prefix = format!("{MIGRATE_JSON_FLAG}=");
    for (i, arg) in args.iter().enumerate() {
        if let Some(rest) = arg.strip_prefix(&eq_prefix) {
            return Some(rest.to_string());
        }
        if arg == MIGRATE_JSON_FLAG {
            let value = args
                .get(i + 1)
                .filter(|v| !v.starts_with("--"))
                .cloned()
                .unwrap_or_default();
            return Some(value);
        }
    }
    None
}

// ── 変換本体（入出力を伴わない純関数）───────────────────────────

/// JSON テキストを現行版へ変換し、版を刻んだ pretty JSON を返す。
///
/// 既に現行版のテキストでも、版の欄を明示したうえで出力する
/// （呼び出し側が「変換済みかどうか」を出力の有無で判断しなくて済むようにするため）。
///
/// 欄の並びは `serde_json` の既定（キー名の昇順）になる。元の整形は保たれない。
pub fn migrate_text(kind: FormatKind, input: &str) -> Result<String, MigrationError> {
    // 先頭の BOM は許容する（エディタや外部ツールが付けることがある）。
    let json = input.strip_prefix('\u{FEFF}').unwrap_or(input);

    let mut value: Value = serde_json::from_str(json).map_err(|e| MigrationError::Parse {
        kind,
        detail: e.to_string(),
    })?;
    migrate_to_current(kind, &mut value)?;

    // `migrate_to_current` が版の欄を現行版へ更新済みなので、ここでは
    // 版を二重に刻まないよう、値をそのまま pretty 出力する。
    serde_json::to_string_pretty(&value).map_err(|e| MigrationError::Parse {
        kind,
        detail: e.to_string(),
    })
}

/// 失敗を標準エラーへ 1 行 JSON で書く。
fn write_error(out: &mut dyn Write, code: &str, message: &str) {
    let line = serde_json::json!({ "kind": "error", "error": code, "message": message });
    let _ = writeln!(out, "{line}");
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 引数の解釈: `=` 区切りと空白区切りの両方を受け付けること。
    #[test]
    fn parses_kind_argument_in_both_forms() {
        let eq = vec![
            "SEED.exe".to_string(),
            format!("{MIGRATE_JSON_FLAG}=anim"),
        ];
        assert_eq!(parse_kind_arg(&eq).as_deref(), Some("anim"));

        let spaced = vec![
            "SEED.exe".to_string(),
            MIGRATE_JSON_FLAG.to_string(),
            "inputmap".to_string(),
        ];
        assert_eq!(parse_kind_arg(&spaced).as_deref(), Some("inputmap"));

        // 値が無い（次がフラグ）→ 空文字（呼び出し側が usage エラーにする）
        let missing = vec![
            "SEED.exe".to_string(),
            MIGRATE_JSON_FLAG.to_string(),
            "--dry-run".to_string(),
        ];
        assert_eq!(parse_kind_arg(&missing).as_deref(), Some(""));

        // 指定なし
        let none = vec!["SEED.exe".to_string(), "--mode=play".to_string()];
        assert_eq!(parse_kind_arg(&none), None);
    }

    /// 旧形式のテキストが現行版へ持ち上がり、版の欄が付くこと。
    #[test]
    fn migrates_legacy_text_and_stamps_the_version() {
        let input = r#"{"actions":[{"name":"Steer","value_type":1,
            "bindings":[{"platform":"PC","input_type":"WASD","value":"Horizontal"}]}]}"#;
        let out = migrate_text(FormatKind::InputMap, input).expect("変換できること");
        let v: Value = serde_json::from_str(&out).expect("出力が JSON であること");

        assert_eq!(
            v[FormatKind::InputMap.version_key()],
            Value::from(FormatKind::InputMap.current_version())
        );
        assert_eq!(v["actions"][0]["positive"][0]["value"], Value::from("D"));
    }

    /// 既に現行版のテキストも、版の欄を付けたまま素通しできること。
    #[test]
    fn passes_through_current_version_text() {
        let input = format!(
            r#"{{"{}":{},"actions":[]}}"#,
            FormatKind::InputMap.version_key(),
            FormatKind::InputMap.current_version()
        );
        let out = migrate_text(FormatKind::InputMap, &input).expect("変換できること");
        let v: Value = serde_json::from_str(&out).unwrap();
        assert_eq!(
            v[FormatKind::InputMap.version_key()],
            Value::from(FormatKind::InputMap.current_version())
        );
    }

    /// 版の欄を持たない形式でも、版が刻まれた状態で返ること。
    #[test]
    fn stamps_version_for_formats_without_any_step() {
        let out = migrate_text(FormatKind::Anim, r#"{"name":"Swim","tracks":[]}"#).unwrap();
        let v: Value = serde_json::from_str(&out).unwrap();
        assert_eq!(
            v[FormatKind::Anim.version_key()],
            Value::from(FormatKind::Anim.current_version())
        );
    }

    /// 未来版・壊れた JSON がそれぞれ区別できるエラーになること
    /// （終了コードの振り分けはこの型で決まる）。
    #[test]
    fn distinguishes_future_version_from_parse_failure() {
        let future = FormatKind::Anim.current_version() + 1;
        let text = format!(r#"{{"{}":{future}}}"#, FormatKind::Anim.version_key());
        assert!(matches!(
            migrate_text(FormatKind::Anim, &text).unwrap_err(),
            MigrationError::FutureVersion { .. }
        ));

        assert!(matches!(
            migrate_text(FormatKind::Anim, "{not json").unwrap_err(),
            MigrationError::Parse { .. }
        ));

        // トップレベルがオブジェクトでないファイルも失敗として返る
        assert!(matches!(
            migrate_text(FormatKind::Anim, "[1,2,3]").unwrap_err(),
            MigrationError::NotObject { .. }
        ));
    }

    /// BOM 付きのテキストも読めること（エディタが書いたファイルに付くことがある）。
    #[test]
    fn accepts_a_leading_bom() {
        let input = "\u{FEFF}{\"name\":\"Swim\",\"tracks\":[]}";
        assert!(migrate_text(FormatKind::Anim, input).is_ok());
    }

    /// エラー行が約束どおりの JSON になること（エディタが読む形）。
    #[test]
    fn error_line_has_the_expected_shape() {
        let mut buf: Vec<u8> = Vec::new();
        write_error(&mut buf, ERROR_CODE_FUTURE_VERSION, "新しいエンジンです");
        let line = String::from_utf8(buf).unwrap();
        let v: Value = serde_json::from_str(line.trim()).expect("1 行 JSON であること");
        assert_eq!(v["kind"], Value::from("error"));
        assert_eq!(v["error"], Value::from(ERROR_CODE_FUTURE_VERSION));
        assert_eq!(v["message"], Value::from("新しいエンジンです"));
    }
}
