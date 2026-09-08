// ============================================================
//  save_data_ops.rs — セーブデータ（SEED.SaveData）の IPC 操作
//
//  【役割】
//  エディタ（＝ MCP 経由の AI）から、実行中ランタイムのセーブデータを
//  読み書きするための IPC ハンドラ。
//
//  【なぜ必要か】
//  ゲームの検証では「所持金 1000 の状態」「この魚を釣った状態」といった
//  進行状況を作ってから Play したい。従来はセーブ JSON ファイルを手で書く
//  しかなく、①保存先（`SEED_SAVE_DIR` の有無・実行モード）を人間が推測する、
//  ②Play 中はランタイムがメモリ上の値を正としてファイルを上書きする、
//  という 2 つの落とし穴があった。ランタイム自身のストアを触れば
//  どちらも起きない（保存先の決定は `save::path` が一元管理している）。
//
//  【ワイヤ書式】
//    要求: `SAVE_DATA:{json}`（1 行。json は下記のオブジェクト）
//      {"op":"get",    "key":"money"}
//      {"op":"set",    "key":"money", "type":"int",    "value":1200}
//      {"op":"set",    "key":"best",  "type":"float",  "value":32.5}
//      {"op":"set",    "key":"name",  "type":"string", "value":"kani"}
//      {"op":"delete", "key":"money"}
//      {"op":"save"}                              … ディスクへフラッシュ
//    応答: `SAVE_DATA_OK:{json}` / `SAVE_DATA_ERROR:{message}`
//
//  `type` を省略した `set` は value の JSON 型から推論する
//  （整数 → int / 実数 → float / 文字列 → string）。
//
//  【安全性】
//  セーブデータはゲームの進行データであってシーンではないため、
//  シーン保存のような多重ガードは掛けていない。ただし AI 経路としては
//  「変更系コマンド」として扱い、読み取り専用インスタンス
//  （利用者が普通に開いたエディタ）では拒否される。
// ============================================================

use serde_json::{json, Value};

use crate::engine::core::save;

use super::App;

/// 応答（成功）の接頭辞。エディタ側 `RuntimeManager` の定数と一致させること。
const REPLY_OK_PREFIX: &str = "SAVE_DATA_OK:";

/// 応答（失敗）の接頭辞。エディタ側 `RuntimeManager` の定数と一致させること。
const REPLY_ERROR_PREFIX: &str = "SAVE_DATA_ERROR:";

/// 値の型指定: 整数。
const TYPE_INT: &str = "int";
/// 値の型指定: 実数。
const TYPE_FLOAT: &str = "float";
/// 値の型指定: 文字列。
const TYPE_STRING: &str = "string";

impl App {
    /// `SAVE_DATA:{json}` を処理して 1 行の応答を返す。
    ///
    /// 実処理は純関数 [`apply_save_data_command`] にあり、ここは
    /// 「IPC で受けて IPC で返す」入出力だけを担う（テスト容易性のため）。
    pub(super) fn handle_save_data(&mut self, payload: String) {
        let reply = match apply_save_data_command(&payload) {
            Ok(v) => format!("{REPLY_OK_PREFIX}{v}"),
            Err(e) => format!("{REPLY_ERROR_PREFIX}{e}"),
        };
        if let Some(ipc) = &self.ipc {
            ipc.send(&reply);
        }
    }
}

/// セーブデータ操作 1 件を実行し、応答 JSON（文字列）を返す。
///
/// 成功時の JSON は次の形:
///   get    … `{"op":"get","key":"…","found":true,"type":"int","value":1200}`
///   set    … `{"op":"set","key":"…","type":"int","value":1200}`
///   delete … `{"op":"delete","key":"…","deleted":true}`
///   save   … `{"op":"save","saved":true}`
/// 失敗時は `Err(メッセージ)`。
pub fn apply_save_data_command(payload: &str) -> Result<String, String> {
    let root: Value = serde_json::from_str(payload)
        .map_err(|e| format!("JSON として解釈できません: {e}"))?;

    let op = root.get("op").and_then(Value::as_str)
        .ok_or_else(|| "\"op\" が必要です（get / set / delete / save）".to_string())?;

    match op {
        "get"    => op_get(&root),
        "set"    => op_set(&root),
        "delete" => op_delete(&root),
        "save"   => Ok(json!({ "op": "save", "saved": save::save() }).to_string()),
        other    => Err(format!("不明な op '{other}'（get / set / delete / save）")),
    }
}

/// 要求からキー名を取り出す（空文字は不正）。
fn key_of(root: &Value) -> Result<&str, String> {
    let key = root.get("key").and_then(Value::as_str)
        .ok_or_else(|| "\"key\" が必要です".to_string())?;
    if key.is_empty() {
        return Err("\"key\" が空です".to_string());
    }
    Ok(key)
}

/// `get`: 型を問わず値を読み、見つかった型で返す。
///
/// ストアは「その値が何型で入っているか」を直接返す API を持たないため、
/// 文字列 → 整数 → 実数 の順に試す（文字列と数値は相互変換されないので、
/// この順で最初に成功したものが実際の型と一致する）。
fn op_get(root: &Value) -> Result<String, String> {
    let key = key_of(root)?;
    if !save::has(key) {
        return Ok(json!({ "op": "get", "key": key, "found": false }).to_string());
    }
    if let Some(s) = save::get_string(key) {
        return Ok(json!({ "op":"get","key":key,"found":true,"type":TYPE_STRING,"value":s }).to_string());
    }
    if let Some(i) = save::get_int(key) {
        // 実数として保持されている値も get_int で取れてしまうため、
        // 「実数へ戻して同じ値か」で整数／実数を見分ける。
        let f = save::get_float(key);
        if f.map(|f| f == i as f32).unwrap_or(false) {
            return Ok(json!({ "op":"get","key":key,"found":true,"type":TYPE_INT,"value":i }).to_string());
        }
    }
    if let Some(f) = save::get_float(key) {
        return Ok(json!({ "op":"get","key":key,"found":true,"type":TYPE_FLOAT,"value":f }).to_string());
    }
    Ok(json!({ "op": "get", "key": key, "found": false }).to_string())
}

/// `set`: 型指定（省略時は value の JSON 型から推論）に従って書き込む。
fn op_set(root: &Value) -> Result<String, String> {
    let key = key_of(root)?.to_string();
    let value = root.get("value")
        .ok_or_else(|| "\"value\" が必要です".to_string())?;

    // 型指定が無ければ value の JSON 型から決める
    let ty = match root.get("type").and_then(Value::as_str) {
        Some(t) => t.to_string(),
        None => match value {
            Value::String(_) => TYPE_STRING.to_string(),
            Value::Number(n) if n.is_i64() => TYPE_INT.to_string(),
            Value::Number(_) => TYPE_FLOAT.to_string(),
            _ => return Err("\"value\" は数値か文字列で指定してください".to_string()),
        },
    };

    match ty.as_str() {
        TYPE_INT => {
            let v = value.as_i64()
                .or_else(|| value.as_f64().map(|f| f.trunc() as i64))
                .ok_or_else(|| "\"value\" を整数として解釈できません".to_string())?;
            save::set_int(&key, v);
            Ok(json!({ "op":"set","key":key,"type":TYPE_INT,"value":v }).to_string())
        }
        TYPE_FLOAT => {
            let v = value.as_f64()
                .ok_or_else(|| "\"value\" を実数として解釈できません".to_string())? as f32;
            save::set_float(&key, v);
            Ok(json!({ "op":"set","key":key,"type":TYPE_FLOAT,"value":v }).to_string())
        }
        TYPE_STRING => {
            let v = value.as_str()
                .ok_or_else(|| "\"value\" を文字列として解釈できません".to_string())?;
            save::set_string(&key, v);
            Ok(json!({ "op":"set","key":key,"type":TYPE_STRING,"value":v }).to_string())
        }
        other => Err(format!("不明な type '{other}'（int / float / string）")),
    }
}

/// `delete`: キーを 1 つ削除する。
fn op_delete(root: &Value) -> Result<String, String> {
    let key = key_of(root)?.to_string();
    let deleted = save::delete_key(&key);
    Ok(json!({ "op":"delete","key":key,"deleted":deleted }).to_string())
}

// ============================================================
//  ユニットテスト（引数解釈のエラー経路。ストア本体は save::store のテストが担当）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 壊れた JSON・必須キー欠落・不明な op はすべてエラーメッセージになる。
    #[test]
    fn rejects_malformed_requests() {
        assert!(apply_save_data_command("not json").is_err());
        assert!(apply_save_data_command("{}").is_err());
        assert!(apply_save_data_command(r#"{"op":"nope"}"#).is_err());
        assert!(apply_save_data_command(r#"{"op":"get"}"#).is_err());
        assert!(apply_save_data_command(r#"{"op":"get","key":""}"#).is_err());
        assert!(apply_save_data_command(r#"{"op":"set","key":"a"}"#).is_err());
        assert!(apply_save_data_command(r#"{"op":"set","key":"a","value":true}"#).is_err());
        assert!(apply_save_data_command(r#"{"op":"set","key":"a","type":"bool","value":1}"#).is_err());
    }

    /// set → get → delete の往復ができる（プロセス共有ストアを使う）。
    ///
    /// キー名はこのテスト専用に十分珍しいものを使い、他テストと衝突させない。
    #[test]
    fn set_get_delete_roundtrip() {
        let k = "unit_test_save_data_ops_key";

        let set = apply_save_data_command(
            &format!(r#"{{"op":"set","key":"{k}","type":"string","value":"kani"}}"#)).unwrap();
        assert!(set.contains("\"kani\""), "{set}");

        let got = apply_save_data_command(&format!(r#"{{"op":"get","key":"{k}"}}"#)).unwrap();
        assert!(got.contains("\"found\":true"), "{got}");
        assert!(got.contains("\"string\""), "{got}");

        let del = apply_save_data_command(&format!(r#"{{"op":"delete","key":"{k}"}}"#)).unwrap();
        assert!(del.contains("\"deleted\":true"), "{del}");

        let got2 = apply_save_data_command(&format!(r#"{{"op":"get","key":"{k}"}}"#)).unwrap();
        assert!(got2.contains("\"found\":false"), "{got2}");
    }

    /// type 省略時は value の JSON 型から推論する（整数 / 実数 / 文字列）。
    #[test]
    fn infers_type_from_value() {
        let k = "unit_test_save_data_ops_infer";

        let i = apply_save_data_command(
            &format!(r#"{{"op":"set","key":"{k}","value":7}}"#)).unwrap();
        assert!(i.contains("\"int\""), "{i}");

        let f = apply_save_data_command(
            &format!(r#"{{"op":"set","key":"{k}","value":7.5}}"#)).unwrap();
        assert!(f.contains("\"float\""), "{f}");

        let s = apply_save_data_command(
            &format!(r#"{{"op":"set","key":"{k}","value":"x"}}"#)).unwrap();
        assert!(s.contains("\"string\""), "{s}");

        apply_save_data_command(&format!(r#"{{"op":"delete","key":"{k}"}}"#)).unwrap();
    }
}
