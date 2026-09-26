// ============================================================
//  platform/bridge/wire.rs — プラットフォーム機能（SEED.Platform）の JSON の約束（W1-1）
//
//  【役割】
//  エンジン・Java（メインプロセスの SeedPlatform と :seed_platform の PlatformProvider）・C#（SEED.Platform）の
//  3 者が同じ形で読み書きする JSON の「名前・形・エラーの理由」をこのファイル 1 か所に集める（Rust 側の正典）。
//  Java 側の対になる定数は runtime/android/app/src/main/java/com/seedengine/runtime/platform/PlatformContract.java、
//  C# 側は scripting/src/Api/Platform/。値を変えるときは 3 か所を必ず揃える。
//
//  【形】
//    命令     : invoke(module, method, json)。module・method は小文字英数字と _ の名前（is_valid_name）
//    返答     : {"ok": true, ...}  /  {"ok": false, "error": "<理由の名前>"}
//    イベント : {"name": "platform.<名前>", "seq": <番号>, "time_ms": <UTC の epoch ミリ秒>, "data": {...}}
//               seq は :seed_platform の記録（EventJournal）の通し番号（1 から）。メインプロセスの中で作る
//               接続の知らせ（platform.connected など）とデスクトップの模擬の試験イベントは LOCAL_EVENT_SEQ（0）か
//               模擬の中の通し番号
//
//  全体像は docs/android.md §25・docs/app_platform_roadmap.md §2.2。
// ============================================================

use serde_json::{json, Map, Value};

/// プロトコルの版（platform.version の返答の protocol）。Java の PlatformContract.PROTOCOL_VERSION と一致させる。
///
/// JSON の形・メソッドの意味を互換のない形で変えたら 1 つ上げる（APK の Java と C# の組み合わせの食い違いを見分けるため）。
pub const PROTOCOL_VERSION: u32 = 1;

// ── キーの名前（Java の PlatformContract.KEY_* と一致させる）──

/// 返答: 成功したか（bool）。
pub const KEY_OK: &str = "ok";
/// 返答: 失敗の理由の名前（文字列。ERROR_* のどれか、または Java 側の理由）。
pub const KEY_ERROR: &str = "error";
/// イベント: 名前（`platform.` で始まる）。
pub const KEY_NAME: &str = "name";
/// イベント: 通し番号。
pub const KEY_SEQ: &str = "seq";
/// イベント: 起きた時刻（UTC の epoch ミリ秒）。
pub const KEY_TIME_MS: &str = "time_ms";
/// イベント: 中身（オブジェクト）。
pub const KEY_DATA: &str = "data";

/// イベントの名前の接頭辞（スクリプトの SEED.Events にもこの名前で流れる）。
pub const EVENT_NAME_PREFIX: &str = "platform.";

/// メインプロセスの中で作ったイベント（接続の知らせ）の seq（記録を通らないので番号を持たない）。
pub const LOCAL_EVENT_SEQ: u64 = 0;

// ── モジュールとメソッドの名前（W1-1 で実装するもの）──

/// 基盤そのもののモジュール。
pub const MODULE_PLATFORM: &str = "platform";
/// 往復の計測（受け取った JSON をそのまま echo に入れて返す＋pid・起動からの ms）。
pub const METHOD_PING: &str = "ping";
/// プロトコルの版と使えるモジュール。
pub const METHOD_VERSION: &str = "version";
/// 試験イベントを 1 つ流す（デバッグ用）。
pub const METHOD_EMIT_TEST_EVENT: &str = "emit_test_event";
/// 試験イベントの名前。
pub const TEST_EVENT_NAME: &str = "platform.test_event";

// ── エラーの理由（Java の PlatformContract.ERROR_* と一致させる。C# の Platform.LastError に入る）──

/// 基盤が無い（Android で Java 側がまだ登録していない・デスクトップ以外で模擬も無い）。
pub const ERROR_UNAVAILABLE: &str = "platform_unavailable";
/// module / method の名前が約束（is_valid_name）に合わない。
pub const ERROR_INVALID_NAME: &str = "invalid_name";
/// 引数の JSON が読めない。
pub const ERROR_INVALID_JSON: &str = "invalid_json";
/// 知らないメソッド（模擬・PlatformProvider とも同じ理由）。
pub const ERROR_UNKNOWN_METHOD: &str = "unknown_method";
/// エンジンの中で panic した（呼び出しは続けられる）。
pub const ERROR_INTERNAL_PANIC: &str = "internal_panic";

/// module / method の名前の最大の長さ（バイト）。
pub const MAX_NAME_LEN: usize = 64;

/// module / method の名前が約束どおりか（1〜MAX_NAME_LEN 文字の小文字英数字と `_`）。
///
/// Java 側は `<module>.<method>` を ContentProvider の call の method に使うので、`.` や空白を含む名前は通さない。
pub fn is_valid_name(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= MAX_NAME_LEN
        && name.bytes().all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'_')
}

/// 失敗の返答 `{"ok": false, "error": <理由>}` を作る。
pub fn error_reply(reason: &str) -> String {
    json!({ KEY_OK: false, KEY_ERROR: reason }).to_string()
}

/// 成功の返答を作る（`fields` の先頭に `"ok": true` を足す）。
///
/// # 引数
/// * `fields` - 返答の中身（オブジェクト）。`ok` のキーがあれば true で上書きする
pub fn ok_reply(fields: Map<String, Value>) -> String {
    let mut reply = Map::new();
    reply.insert(KEY_OK.to_string(), Value::Bool(true));
    for (key, value) in fields {
        if key != KEY_OK {
            reply.insert(key, value);
        }
    }
    Value::Object(reply).to_string()
}

/// イベントの JSON を作る（`{"name", "seq", "time_ms", "data"}`）。
///
/// # 引数
/// * `name`    - イベントの名前（`platform.` で始めること）
/// * `seq`     - 通し番号
/// * `time_ms` - 起きた時刻（UTC の epoch ミリ秒）
/// * `data`    - 中身（オブジェクト）
pub fn event_json(name: &str, seq: u64, time_ms: u64, data: Value) -> String {
    json!({ KEY_NAME: name, KEY_SEQ: seq, KEY_TIME_MS: time_ms, KEY_DATA: data }).to_string()
}

/// 外（Java）から届いたイベントの JSON が約束の形か確かめる（オブジェクトで、`name` が `platform.` で始まる文字列）。
///
/// # 戻り値
/// 形が正しければ Ok(名前)。正しくなければ Err(理由の説明。ログ用)
pub fn validate_event_json(text: &str) -> Result<String, String> {
    let value: Value = serde_json::from_str(text).map_err(|err| format!("JSON として読めません: {err}"))?;
    let Some(name) = value.get(KEY_NAME).and_then(Value::as_str) else {
        return Err(format!("\"{KEY_NAME}\"（文字列）がありません"));
    };
    if !name.starts_with(EVENT_NAME_PREFIX) {
        return Err(format!("名前が \"{EVENT_NAME_PREFIX}\" で始まっていません: {name}"));
    }
    Ok(name.to_string())
}

/// 引数の JSON を読む（空なら空のオブジェクト）。
///
/// # 戻り値
/// 読めた値。読めなければ Err(ERROR_INVALID_JSON)
pub fn parse_request(json_text: &str) -> Result<Value, &'static str> {
    if json_text.trim().is_empty() {
        return Ok(Value::Object(Map::new()));
    }
    serde_json::from_str(json_text).map_err(|_| ERROR_INVALID_JSON)
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 名前の約束: 小文字英数字と _ だけ・空と長すぎは不可・`.` を含む名前は不可（Java で `<module>.<method>` に使うため）。
    #[test]
    fn name_rules() {
        assert!(is_valid_name("platform"));
        assert!(is_valid_name("emit_test_event"));
        assert!(is_valid_name("alarm2"));
        assert!(!is_valid_name(""));
        assert!(!is_valid_name("Platform"), "大文字は不可");
        assert!(!is_valid_name("platform.ping"), "区切りの . を含む名前は不可");
        assert!(!is_valid_name("ping pong"));
        assert!(!is_valid_name(&"a".repeat(MAX_NAME_LEN + 1)));
        assert!(is_valid_name(&"a".repeat(MAX_NAME_LEN)));
    }

    /// 失敗の返答は ok=false と理由を持つ。
    #[test]
    fn error_reply_shape() {
        let value: Value = serde_json::from_str(&error_reply(ERROR_UNKNOWN_METHOD)).unwrap();
        assert_eq!(value[KEY_OK], Value::Bool(false));
        assert_eq!(value[KEY_ERROR], Value::from(ERROR_UNKNOWN_METHOD));
    }

    /// 成功の返答は ok=true が先頭に入り、呼び出し側の ok は上書きされる。
    #[test]
    fn ok_reply_forces_ok_true() {
        let mut fields = Map::new();
        fields.insert(KEY_OK.to_string(), Value::Bool(false));
        fields.insert("pid".to_string(), Value::from(42));
        let text = ok_reply(fields);
        let value: Value = serde_json::from_str(&text).unwrap();
        assert_eq!(value[KEY_OK], Value::Bool(true));
        assert_eq!(value["pid"], Value::from(42));
        assert!(text.starts_with("{\"ok\":true"), "ok が先頭にない: {text}");
    }

    /// イベントの JSON は検査を通り、名前を返す。接頭辞の無い名前・名前の無いもの・壊れた JSON は通さない。
    #[test]
    fn event_json_round_trips_through_validation() {
        let text = event_json(TEST_EVENT_NAME, 3, 1_700_000_000_000, json!({ "message": "こんにちは" }));
        assert_eq!(validate_event_json(&text).unwrap(), TEST_EVENT_NAME);
        assert!(validate_event_json(r#"{"name":"alarm.started"}"#).is_err());
        assert!(validate_event_json(r#"{"seq":1}"#).is_err());
        assert!(validate_event_json("{not json").is_err());
    }

    /// 引数の JSON: 空は空のオブジェクト、壊れたものは ERROR_INVALID_JSON。
    #[test]
    fn parse_request_rules() {
        assert_eq!(parse_request("").unwrap(), json!({}));
        assert_eq!(parse_request("  ").unwrap(), json!({}));
        assert_eq!(parse_request(r#"{"a":1}"#).unwrap(), json!({ "a": 1 }));
        assert_eq!(parse_request("{").unwrap_err(), ERROR_INVALID_JSON);
    }
}
