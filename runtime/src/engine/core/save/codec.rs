// ============================================================
//  save/codec.rs — セーブファイルの本文（JSON）とキー・バリューの相互変換
//
//  【役割】
//  メモリ上のキー・バリュー（BTreeMap<String, SaveValue>）と、ファイルに書く本文（JSON テキスト）を
//  相互に変換するだけの純関数群。ファイルを読む・書く・壊れていたらどうするかは持たない
//  （書き出しは durable_file.rs、読み込みと 1 世代前への切り替えは recovery.rs）。
//
//  【JSON 形式】（W1-S でも変えていない。旧版が書いた save.json をそのまま読める）
//  素直な 1 階層のオブジェクト（人が読める・手で書き換えられる）:
//    {
//      "money": 1200,
//      "rod_level": 3,
//      "best_size_bass": 41.5,
//      "player_name": "kani"
//    }
//  JSON の数値はそのまま整数 / 実数として読み分ける（小数点や指数が無く
//  i64 に収まるものを整数、それ以外を実数とみなす）。
//  キーは BTreeMap の順（辞書順）で書く。書き出しが常に同じ順序になり、差分が読める。
//
//  【壊れた本文の扱い】
//  JSON として読めない・トップレベルがオブジェクトでない本文は `DecodeError`（壊れている）として返す。
//  W1-S より前は「空として読んで、次の保存で上書き」していたが、それでは 1 つ前の世代へ戻れないため、
//  判定だけをここで行い、扱い（1 世代前から読む・退避する）は recovery.rs に任せる。
//  真偽値・null・配列・オブジェクトの「値」は壊れているとはみなさず読み飛ばす
//  （手書きで型の違う値が混ざってもロード全体を失敗させない。従来どおり）。
// ============================================================

use std::collections::BTreeMap;
use std::fmt;
use std::io;

use serde_json::{Map as JsonMap, Value as JsonValue};

use super::value::SaveValue;

/// 本文の先頭に付くことがある UTF-8 の BOM（手で編集したときにエディタが付けることがある）。
///
/// serde_json は BOM を受け付けないので、壊れているとみなす前に取り除く。
const UTF8_BOM: char = '\u{FEFF}';

/// 本文を読んだ結果。
#[derive(Debug, Default)]
pub struct Decoded {
    /// 読めたキー・バリュー。
    pub values: BTreeMap<String, SaveValue>,
    /// ストアの型に無い値（真偽値・null・配列・オブジェクト）で読み飛ばした件数。
    pub skipped: usize,
}

/// 本文が壊れている理由。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DecodeError {
    /// JSON として読めない（途中で切れた・0 バイト・別の中身で上書きされた）。
    InvalidJson(String),
    /// JSON だがトップレベルがオブジェクトではない（配列・数値など）。
    NotAnObject,
}

impl fmt::Display for DecodeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            DecodeError::InvalidJson(detail) => write!(f, "JSON として読めません（{detail}）"),
            DecodeError::NotAnObject => write!(f, "トップレベルがオブジェクトではありません"),
        }
    }
}

/// キー・バリューを本文（整形した JSON テキスト）にする。
///
/// # 戻り値
/// 本文。組み立てに失敗したら `Err`（呼び出し元は書き出しをやめる）。
/// 以前は失敗時に `{}` を返していたが、それを書くと保存が空で上書きされてしまうため、失敗として返す。
pub fn encode(values: &BTreeMap<String, SaveValue>) -> io::Result<String> {
    let mut map = JsonMap::new();
    for (k, v) in values {
        map.insert(k.clone(), v.to_json());
    }
    // pretty で書き出す（セーブファイルは手で覗いて直せることに価値がある）
    serde_json::to_string_pretty(&JsonValue::Object(map))
        .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, format!("セーブの本文を組み立てられません: {e}")))
}

/// 本文を読んでキー・バリューにする。
///
/// # 戻り値
/// 読めた値と読み飛ばした件数。本文が壊れていれば `DecodeError`。
pub fn decode(text: &str) -> Result<Decoded, DecodeError> {
    // 先頭の BOM は壊れている印ではない（手で編集したファイル）。取り除いてから読む。
    let body = text.strip_prefix(UTF8_BOM).unwrap_or(text);
    let root: JsonValue =
        serde_json::from_str(body).map_err(|e| DecodeError::InvalidJson(e.to_string()))?;
    let JsonValue::Object(map) = root else {
        return Err(DecodeError::NotAnObject);
    };

    let mut decoded = Decoded::default();
    for (k, v) in map {
        match SaveValue::from_json(&v) {
            Some(sv) => {
                decoded.values.insert(k, sv);
            }
            None => decoded.skipped += 1,
        }
    }
    Ok(decoded)
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用のキー・バリューを組み立てる。
    fn sample() -> BTreeMap<String, SaveValue> {
        let mut values = BTreeMap::new();
        values.insert("money".to_string(), SaveValue::Int(1200));
        values.insert("best".to_string(), SaveValue::Float(41.5));
        values.insert("name".to_string(), SaveValue::Str("鯉".into())); // 非 ASCII も往復すること
        values
    }

    /// JSON 直列化 → パースの往復で値が保たれる。
    #[test]
    fn json_roundtrip_preserves_values() {
        let text = encode(&sample()).expect("組み立てに失敗した");
        let decoded = decode(&text).expect("読めなかった");
        assert_eq!(decoded.skipped, 0);
        assert_eq!(decoded.values, sample());
    }

    /// 途中で切れた JSON は壊れている（空として読まない）。
    #[test]
    fn broken_json_is_an_error() {
        assert!(matches!(decode("{ this is not json"), Err(DecodeError::InvalidJson(_))));
        // 書き出しの途中で切れた形（閉じ括弧が無い）
        assert!(matches!(decode("{\n  \"money\": 12"), Err(DecodeError::InvalidJson(_))));
        // 0 バイト（電源断で中身が届かなかったファイル）
        assert!(matches!(decode(""), Err(DecodeError::InvalidJson(_))));
    }

    /// トップレベルが配列・数値なら壊れている。
    #[test]
    fn non_object_json_is_an_error() {
        assert_eq!(decode("[1, 2, 3]").unwrap_err(), DecodeError::NotAnObject);
        assert_eq!(decode("42").unwrap_err(), DecodeError::NotAnObject);
    }

    /// 空のオブジェクトは正しい本文（DeleteAll → Save の結果）。壊れているとはみなさない。
    #[test]
    fn empty_object_is_valid() {
        let decoded = decode("{}").expect("空のオブジェクトを壊れているとみなした");
        assert!(decoded.values.is_empty());
    }

    /// 非対応な型（bool / null / 配列 / オブジェクト）は読み飛ばし、他のキーは生き残る。
    #[test]
    fn unsupported_values_are_skipped_but_others_survive() {
        let text = r#"{ "ok": 5, "flag": true, "nil": null, "arr": [1], "obj": {"x":1} }"#;
        let decoded = decode(text).unwrap();
        assert_eq!(decoded.values.len(), 1);
        assert_eq!(decoded.values.get("ok"), Some(&SaveValue::Int(5)));
        assert_eq!(decoded.skipped, 4);
    }

    /// JSON の整数リテラルは Int、小数リテラルは Float として読み分ける。
    #[test]
    fn json_number_kind_is_detected() {
        let decoded = decode(r#"{ "i": 42, "f": 42.0, "neg": -7 }"#).unwrap();
        assert_eq!(decoded.values.get("i"), Some(&SaveValue::Int(42)));
        assert_eq!(decoded.values.get("f"), Some(&SaveValue::Float(42.0)));
        assert_eq!(decoded.values.get("neg"), Some(&SaveValue::Int(-7)));
    }

    /// 非有限な実数は JSON で表現できないため 0 として書き出される（書き出し全体が失敗しないこと）。
    #[test]
    fn non_finite_float_serializes_as_zero() {
        let mut values = BTreeMap::new();
        values.insert("nan".to_string(), SaveValue::Float(f64::NAN));
        let decoded = decode(&encode(&values).unwrap()).unwrap();
        assert_eq!(decoded.values.get("nan"), Some(&SaveValue::Int(0)));
    }

    /// 先頭に BOM が付いた本文（手で編集したファイル）も読める。
    #[test]
    fn bom_prefixed_text_is_read() {
        let decoded = decode("\u{FEFF}{\"money\": 7}").expect("BOM 付きを壊れているとみなした");
        assert_eq!(decoded.values.get("money"), Some(&SaveValue::Int(7)));
    }
}
