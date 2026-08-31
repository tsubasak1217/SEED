// ============================================================
//  save/store.rs — セーブデータのキー・バリューストア本体
//
//  【役割】
//  キー（文字列）→ 値（整数 / 浮動小数 / 文字列）のマップを保持し、
//  JSON テキストとの相互変換とファイル入出力を担う。
//
//  【JSON 形式】
//  素直な 1 階層のオブジェクト（人が読める・手で書き換えられる）:
//    {
//      "money": 1200,
//      "rod_level": 3,
//      "best_size_bass": 41.5,
//      "player_name": "kani"
//    }
//  JSON の数値はそのまま整数 / 実数として読み分ける（小数点や指数が無く
//  i64 に収まるものを整数、それ以外を実数とみなす）。
//  真偽値・null・配列・オブジェクトはこのストアの型に無いため**読み飛ばす**
//  （手書きで壊れた値が混ざってもロード全体を失敗させない）。
//
//  【型不一致時の方針】
//  - 整数 ⇄ 実数 は相互変換する（`SetFloat(2.0)` → `GetInt` = 2、切り捨て）。
//  - 文字列 → 数値、数値 → 文字列 は**変換しない**（`None` = 既定値を返す）。
//    暗黙のパースは「保存し忘れ」と「本当に文字列だった」を見分けられなくし、
//    バグを既定値で覆い隠すため。
// ============================================================

use std::collections::BTreeMap;
use std::io;
use std::path::{Path, PathBuf};

use serde_json::{Map as JsonMap, Number as JsonNumber, Value as JsonValue};

// ─── SaveValue ────────────────────────────────────────────────

/// セーブデータが保持できる値の型。
#[derive(Debug, Clone, PartialEq)]
pub enum SaveValue {
    /// 整数（資金・レベル・カウント）。
    Int(i64),
    /// 実数（記録サイズ・進捗率）。内部は f64 で保持し、FFI では f32 に丸める。
    Float(f64),
    /// 文字列（プレイヤー名・最後に釣った魚の ID）。
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

    /// JSON 値へ変換する。
    fn to_json(&self) -> JsonValue {
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
    fn from_json(v: &JsonValue) -> Option<SaveValue> {
        match v {
            JsonValue::Number(n) => {
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

// ─── SaveStore ────────────────────────────────────────────────

/// セーブデータのキー・バリューストア。
///
/// キーは `BTreeMap` で保持する（書き出しが常に同じ順序になり、
/// セーブファイルの差分が読める＝手動デバッグしやすいため）。
#[derive(Debug)]
pub struct SaveStore {
    /// 保存先ファイルの絶対パス。
    path: PathBuf,
    /// キー → 値。
    values: BTreeMap<String, SaveValue>,
    /// 最後のフラッシュ以降に変更があったか。
    dirty: bool,
}

impl SaveStore {
    /// 空のストアを作る（ファイルは読まない。テスト用）。
    pub fn new_empty(path: PathBuf) -> Self {
        Self {
            path,
            values: BTreeMap::new(),
            dirty: false,
        }
    }

    /// ファイルからロードする。存在しない / 壊れている場合は空のストアを返す。
    ///
    /// 壊れたファイルでゲームが起動しなくなるほうが害が大きいので、
    /// パース失敗はログを出して空扱いにする（上書き保存で自動的に直る）。
    pub fn load_or_empty(path: PathBuf) -> Self {
        let mut store = Self::new_empty(path);
        match std::fs::read_to_string(&store.path) {
            Ok(text) => {
                let (values, skipped) = parse_json(&text);
                if skipped > 0 {
                    eprintln!(
                        "[SEED SAVE] {} 件の非対応な値を読み飛ばしました ({})",
                        skipped,
                        store.path.display()
                    );
                }
                store.values = values;
            }
            Err(e) if e.kind() == io::ErrorKind::NotFound => {
                // 初回起動: セーブが無いのは正常。ログも出さない。
            }
            Err(e) => {
                eprintln!(
                    "[SEED SAVE] 読み込みに失敗しました ({}): {e}",
                    store.path.display()
                );
            }
        }
        store.dirty = false;
        store
    }

    /// 保存先パス。
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// 最後のフラッシュ以降に変更があったか。
    pub fn is_dirty(&self) -> bool {
        self.dirty
    }

    /// 保持しているキー数（テスト・診断用）。
    #[allow(dead_code)] // 現状はユニットテストからのみ使う診断アクセサ
    pub fn len(&self) -> usize {
        self.values.len()
    }

    /// キーが 1 つも無いか。
    #[allow(dead_code)] // 現状はユニットテストからのみ使う診断アクセサ
    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }

    // ── 読み取り ─────────────────────────────────────────────

    /// キーが存在するか（型は問わない）。
    pub fn has(&self, key: &str) -> bool {
        self.values.contains_key(key)
    }

    /// 整数として読む。
    pub fn get_int(&self, key: &str) -> Option<i64> {
        self.values.get(key).and_then(SaveValue::as_int)
    }

    /// 実数として読む。
    pub fn get_float(&self, key: &str) -> Option<f32> {
        self.values.get(key).and_then(SaveValue::as_float)
    }

    /// 文字列として読む。
    pub fn get_string(&self, key: &str) -> Option<String> {
        self.values
            .get(key)
            .and_then(SaveValue::as_str)
            .map(str::to_string)
    }

    // ── 書き込み ─────────────────────────────────────────────

    /// 値を書く（同じキーの既存値は型ごと置き換える）。
    ///
    /// 空キーは受け付けない（JSON のキーとしては合法だが、
    /// スクリプト側の変数未初期化バグを黙って通す入り口になるため）。
    pub fn set(&mut self, key: &str, value: SaveValue) {
        if key.is_empty() {
            return;
        }
        // 同値の再代入では dirty を立てない（毎フレーム Set する UI 由来の
        // 無駄なフラッシュを避ける）。
        if self.values.get(key) == Some(&value) {
            return;
        }
        self.values.insert(key.to_string(), value);
        self.dirty = true;
    }

    /// キーを削除する。削除した=true / 元から無かった=false。
    pub fn delete_key(&mut self, key: &str) -> bool {
        let removed = self.values.remove(key).is_some();
        if removed {
            self.dirty = true;
        }
        removed
    }

    /// 全キーを削除する。
    pub fn delete_all(&mut self) {
        if !self.values.is_empty() {
            self.values.clear();
            self.dirty = true;
        }
    }

    // ── 永続化 ───────────────────────────────────────────────

    /// JSON テキストへシリアライズする（改行・インデント付き）。
    pub fn to_json_string(&self) -> String {
        let mut map = JsonMap::new();
        for (k, v) in &self.values {
            map.insert(k.clone(), v.to_json());
        }
        // pretty で書き出す（セーブファイルは手で覗いて直せることに価値がある）
        serde_json::to_string_pretty(&JsonValue::Object(map))
            .unwrap_or_else(|_| String::from("{}"))
    }

    /// ディスクへ書き出す（親ディレクトリが無ければ作る）。
    ///
    /// 書き込みは一時ファイル → リネームの 2 段で行う。
    /// 直接上書きすると、書き込み中の異常終了でセーブが半端な状態になり
    /// 次回起動時にパースできなくなる（＝進行が全損する）。
    pub fn flush(&mut self) -> io::Result<()> {
        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let text = self.to_json_string();

        // 一時ファイル名は保存先と同じディレクトリに置く
        // （別ドライブだと rename がコピーになり原子性が失われるため）。
        let tmp = self.path.with_extension("json.tmp");
        std::fs::write(&tmp, text.as_bytes())?;
        // Windows の rename は上書き不可なので、既存を消してから置き換える。
        // 消してから rename までの間に落ちると save.json が消えるが、
        // .tmp が残るため手動復旧は可能。
        if self.path.exists() {
            std::fs::remove_file(&self.path)?;
        }
        std::fs::rename(&tmp, &self.path)?;

        self.dirty = false;
        Ok(())
    }
}

// ─── JSON パース ──────────────────────────────────────────────

/// JSON テキストをキー・バリューへ変換する。
///
/// 戻り値: (読めた値のマップ, 読み飛ばした件数)。
/// トップレベルがオブジェクトでない・パース不能なら空のマップを返す。
fn parse_json(text: &str) -> (BTreeMap<String, SaveValue>, usize) {
    let mut out = BTreeMap::new();
    let mut skipped = 0usize;

    let Ok(JsonValue::Object(map)) = serde_json::from_str::<JsonValue>(text) else {
        return (out, 0);
    };
    for (k, v) in map {
        match SaveValue::from_json(&v) {
            Some(sv) => {
                out.insert(k, sv);
            }
            None => skipped += 1,
        }
    }
    (out, skipped)
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の空ストア（パスは実在しなくてよい）。
    fn empty() -> SaveStore {
        SaveStore::new_empty(PathBuf::from("__test__/save.json"))
    }

    /// 未設定キーの読み取りはすべて None（＝ C# 側が既定値を返す）。
    #[test]
    fn missing_key_returns_none() {
        let s = empty();
        assert_eq!(s.get_int("money"), None);
        assert_eq!(s.get_float("money"), None);
        assert_eq!(s.get_string("money"), None);
        assert!(!s.has("money"));
    }

    /// 各型の書き込み → 読み取り往復。
    #[test]
    fn set_get_roundtrip_each_type() {
        let mut s = empty();
        s.set("money", SaveValue::Int(1200));
        s.set("best", SaveValue::Float(41.5));
        s.set("name", SaveValue::Str("kani".into()));

        assert_eq!(s.get_int("money"), Some(1200));
        assert_eq!(s.get_float("best"), Some(41.5));
        assert_eq!(s.get_string("name"), Some("kani".to_string()));
        assert!(s.has("money") && s.has("best") && s.has("name"));
    }

    /// 整数 ⇄ 実数は相互変換する（実数→整数は切り捨て）。
    #[test]
    fn numeric_types_convert_both_ways() {
        let mut s = empty();
        s.set("a", SaveValue::Float(41.9));
        s.set("b", SaveValue::Int(7));
        assert_eq!(s.get_int("a"), Some(41)); // 切り捨て
        assert_eq!(s.get_float("b"), Some(7.0));
        // 負の実数も 0 方向へ切り捨てる
        s.set("c", SaveValue::Float(-2.7));
        assert_eq!(s.get_int("c"), Some(-2));
    }

    /// 文字列と数値は相互変換しない（既定値へ落ちる）。
    #[test]
    fn string_and_number_do_not_convert() {
        let mut s = empty();
        s.set("name", SaveValue::Str("123".into()));
        assert_eq!(s.get_int("name"), None);
        assert_eq!(s.get_float("name"), None);

        s.set("money", SaveValue::Int(100));
        assert_eq!(s.get_string("money"), None);
    }

    /// 非有限な実数は整数へ変換できない（既定値へ落ちる）。
    #[test]
    fn non_finite_float_is_not_convertible_to_int() {
        let mut s = empty();
        s.set("nan", SaveValue::Float(f64::NAN));
        s.set("inf", SaveValue::Float(f64::INFINITY));
        assert_eq!(s.get_int("nan"), None);
        assert_eq!(s.get_int("inf"), None);
    }

    /// 同じキーへ別の型を書くと型ごと置き換わる。
    #[test]
    fn set_overwrites_type() {
        let mut s = empty();
        s.set("v", SaveValue::Int(1));
        s.set("v", SaveValue::Str("one".into()));
        assert_eq!(s.get_string("v"), Some("one".to_string()));
        assert_eq!(s.get_int("v"), None);
    }

    /// 空キーは無視する。
    #[test]
    fn empty_key_is_rejected() {
        let mut s = empty();
        s.set("", SaveValue::Int(1));
        assert!(s.is_empty());
        assert!(!s.is_dirty());
    }

    /// 削除の戻り値と dirty フラグ。
    #[test]
    fn delete_key_and_delete_all() {
        let mut s = empty();
        s.set("a", SaveValue::Int(1));
        s.set("b", SaveValue::Int(2));
        assert!(s.delete_key("a"));
        assert!(!s.delete_key("a")); // 2 回目は false
        assert_eq!(s.len(), 1);
        s.delete_all();
        assert!(s.is_empty());
    }

    /// 同値の再代入では dirty を立てない（無駄なフラッシュ防止）。
    #[test]
    fn setting_same_value_does_not_dirty() {
        let mut s = empty();
        s.set("a", SaveValue::Int(1));
        assert!(s.is_dirty());
        // dirty を落とした状態から同値を書いても立たない
        let mut s2 = SaveStore::new_empty(PathBuf::from("x/save.json"));
        s2.values.insert("a".into(), SaveValue::Int(1));
        s2.set("a", SaveValue::Int(1));
        assert!(!s2.is_dirty());
    }

    /// JSON 直列化 → パースの往復で値が保たれる。
    #[test]
    fn json_roundtrip_preserves_values() {
        let mut s = empty();
        s.set("money", SaveValue::Int(1200));
        s.set("best", SaveValue::Float(41.5));
        s.set("name", SaveValue::Str("鯉".into())); // 非 ASCII も往復すること
        let text = s.to_json_string();

        let (map, skipped) = parse_json(&text);
        assert_eq!(skipped, 0);
        assert_eq!(map.get("money"), Some(&SaveValue::Int(1200)));
        assert_eq!(map.get("best"), Some(&SaveValue::Float(41.5)));
        assert_eq!(map.get("name"), Some(&SaveValue::Str("鯉".into())));
    }

    /// 壊れた JSON は空として扱う（起動不能にしない）。
    #[test]
    fn broken_json_loads_as_empty() {
        let (map, skipped) = parse_json("{ this is not json");
        assert!(map.is_empty());
        assert_eq!(skipped, 0);
    }

    /// トップレベルが配列でも空として扱う。
    #[test]
    fn non_object_json_loads_as_empty() {
        let (map, _) = parse_json("[1, 2, 3]");
        assert!(map.is_empty());
    }

    /// 非対応な型（bool / null / 配列 / オブジェクト）は読み飛ばし、
    /// 他のキーは生き残る。
    #[test]
    fn unsupported_values_are_skipped_but_others_survive() {
        let text = r#"{ "ok": 5, "flag": true, "nil": null, "arr": [1], "obj": {"x":1} }"#;
        let (map, skipped) = parse_json(text);
        assert_eq!(map.len(), 1);
        assert_eq!(map.get("ok"), Some(&SaveValue::Int(5)));
        assert_eq!(skipped, 4);
    }

    /// JSON の整数リテラルは Int、小数リテラルは Float として読み分ける。
    #[test]
    fn json_number_kind_is_detected() {
        let (map, _) = parse_json(r#"{ "i": 42, "f": 42.0, "neg": -7 }"#);
        assert_eq!(map.get("i"), Some(&SaveValue::Int(42)));
        assert_eq!(map.get("f"), Some(&SaveValue::Float(42.0)));
        assert_eq!(map.get("neg"), Some(&SaveValue::Int(-7)));
    }

    /// 非有限な実数は JSON で表現できないため 0 として書き出される
    /// （書き出し全体が失敗しないこと）。
    #[test]
    fn non_finite_float_serializes_as_zero() {
        let mut s = empty();
        s.set("nan", SaveValue::Float(f64::NAN));
        let text = s.to_json_string();
        let (map, _) = parse_json(&text);
        assert_eq!(map.get("nan"), Some(&SaveValue::Int(0)));
    }

    /// 実ファイルへの書き出し → 読み込み往復（一時ディレクトリを使う）。
    #[test]
    fn file_flush_and_load_roundtrip() {
        let dir = std::env::temp_dir().join(format!(
            "seed_save_test_{}_{}",
            std::process::id(),
            "flush"
        ));
        let _ = std::fs::remove_dir_all(&dir);
        let path = dir.join("save.json");

        let mut s = SaveStore::new_empty(path.clone());
        s.set("money", SaveValue::Int(999));
        s.set("name", SaveValue::Str("angler".into()));
        s.flush().expect("flush should succeed");
        assert!(!s.is_dirty(), "flush 後は dirty が下りる");
        assert!(path.exists(), "親ディレクトリごと作られる");

        let loaded = SaveStore::load_or_empty(path.clone());
        assert_eq!(loaded.get_int("money"), Some(999));
        assert_eq!(loaded.get_string("name"), Some("angler".to_string()));
        assert!(!loaded.is_dirty(), "ロード直後は dirty ではない");

        // 上書き保存（既存ファイルがある状態での rename 経路）
        let mut s2 = SaveStore::load_or_empty(path.clone());
        s2.set("money", SaveValue::Int(1));
        s2.flush().expect("overwrite flush should succeed");
        let reloaded = SaveStore::load_or_empty(path.clone());
        assert_eq!(reloaded.get_int("money"), Some(1));

        let _ = std::fs::remove_dir_all(&dir);
    }

    /// 存在しないファイルのロードは空ストア（エラーにしない）。
    #[test]
    fn load_missing_file_is_empty() {
        let path = std::env::temp_dir().join("seed_save_test_definitely_missing/save.json");
        let s = SaveStore::load_or_empty(path);
        assert!(s.is_empty());
        assert!(!s.is_dirty());
    }
}
