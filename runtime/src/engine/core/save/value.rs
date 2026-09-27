// ============================================================
//  save/value.rs — セーブデータが保持できる値（SaveValue）と型の読み替え
//
//  【役割】
//  1 つのキーに入る値の型（整数・実数・文字列）と、読み出すときの型の読み替え規則、
//  JSON の値との相互変換だけを持つ。ストア全体の管理（store.rs）・本文の組み立て（codec.rs）・
//  ファイル I/O（durable_file.rs / recovery.rs）は持たない。
//
//  【型不一致時の方針】
//  - 整数 ⇄ 実数 は相互変換する（`SetFloat(2.0)` → `GetInt` = 2、切り捨て）。
//  - 文字列 → 数値、数値 → 文字列 は**変換しない**（`None` = 既定値を返す）。
//    暗黙のパースは「保存し忘れ」と「本当に文字列だった」を見分けられなくし、
//    バグを既定値で覆い隠すため。
// ============================================================

use serde_json::{Number as JsonNumber, Value as JsonValue};

/// セーブデータが保持できる値の型。
#[derive(Debug, Clone, PartialEq)]
pub enum SaveValue {
    /// 整数（資金・レベル・カウント）。
    Int(i64),
    /// 実数（記録サイズ・進捗率）。内部は f64 で保持し、FFI では f32 に丸める。
    Float(f64),
    /// 文字列（プレイヤー名・最後に釣った魚の ID・1 文書をまとめた JSON）。
    Str(String),
}

impl SaveValue {
    /// 整数として解釈する。実数は切り捨て、文字列は変換しない。
    pub fn as_int(&self) -> Option<i64> {
        match self {
            SaveValue::Int(v) => Some(*v),
            // 実数 → 整数は切り捨て（trunc）。NaN / 範囲外は変換不能として None。
            SaveValue::Float(v) => {
                if v.is_finite() && *v >= i64::MIN as f64 && *v <= i64::MAX as f64 {
                    Some(v.trunc() as i64)
                } else {
                    None
                }
            }
            SaveValue::Str(_) => None,
        }
    }

    /// 実数として解釈する。整数は昇格、文字列は変換しない。
    pub fn as_float(&self) -> Option<f32> {
        match self {
            SaveValue::Int(v) => Some(*v as f32),
            SaveValue::Float(v) => Some(*v as f32),
            SaveValue::Str(_) => None,
        }
    }

    /// 文字列として解釈する。数値は変換しない（意図しない既定値化を防ぐ）。
    pub fn as_str(&self) -> Option<&str> {
        match self {
            SaveValue::Str(s) => Some(s.as_str()),
            _ => None,
        }
    }

    /// JSON 値へ変換する（本文の組み立ては codec.rs）。
    pub(super) fn to_json(&self) -> JsonValue {
        match self {
            SaveValue::Int(v) => JsonValue::Number(JsonNumber::from(*v)),
            // 非有限（NaN / ∞）は JSON で表現できないため 0 として書く
            // （書き出し全体を失敗させるより、値 1 つを潰すほうが被害が小さい）。
            SaveValue::Float(v) => JsonNumber::from_f64(*v)
                .map(JsonValue::Number)
                .unwrap_or_else(|| JsonValue::Number(JsonNumber::from(0))),
            SaveValue::Str(s) => JsonValue::String(s.clone()),
        }
    }

    /// JSON 値から変換する。対応しない型（bool / null / 配列 / オブジェクト）は `None`。
    pub(super) fn from_json(v: &JsonValue) -> Option<SaveValue> {
        match v {
            JsonValue::Number(n) => {
                // 小数点・指数が無く i64 に収まるものを整数、それ以外を実数とみなす
                if let Some(i) = n.as_i64() {
                    Some(SaveValue::Int(i))
                } else {
                    n.as_f64().map(SaveValue::Float)
                }
            }
            JsonValue::String(s) => Some(SaveValue::Str(s.clone())),
            _ => None,
        }
    }
}

// ============================================================
//  ユニットテスト（値の読み替え規則。ストア経由の往復は store.rs のテスト）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 実数 → 整数は 0 方向へ切り捨てる（負の値も）。
    #[test]
    fn float_to_int_truncates_toward_zero() {
        assert_eq!(SaveValue::Float(41.9).as_int(), Some(41));
        assert_eq!(SaveValue::Float(-2.7).as_int(), Some(-2));
    }

    /// 非有限な実数は整数へ変換できない（既定値へ落ちる）。
    #[test]
    fn non_finite_float_is_not_convertible_to_int() {
        assert_eq!(SaveValue::Float(f64::NAN).as_int(), None);
        assert_eq!(SaveValue::Float(f64::INFINITY).as_int(), None);
    }

    /// 文字列と数値は相互変換しない。
    #[test]
    fn string_and_number_do_not_convert() {
        assert_eq!(SaveValue::Str("123".into()).as_int(), None);
        assert_eq!(SaveValue::Str("1.5".into()).as_float(), None);
        assert_eq!(SaveValue::Int(100).as_str(), None);
    }
}
