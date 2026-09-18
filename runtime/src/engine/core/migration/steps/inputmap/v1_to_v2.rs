// ============================================================
//  steps/inputmap/v1_to_v2.rs — `.inputmap` v1 → v2
//
//  【何を直すか】
//  v1 は 1 アクションのバインドをすべて `bindings` へ平らに並べ、`WASD` という
//  合成軸の入力種別で「左右」「上下」を 1 件で表していた。
//  v2 では軸アクションが正負のグループを直接持つ:
//
//    Axis1D : positive[] / negative[]
//    Axis2D : x{positive[],negative[]} / y{positive[],negative[]}
//
//  この段は v1 の `bindings` に残る合成軸と素のキーを、対応するグループへ展開する。
//
//  【変換の規則（v1 の読み込み挙動をそのまま写したもの）】
//  | アクション種別 | bindings の中身 | 行き先 |
//  |----------------|-----------------|--------|
//  | Bool           | すべて          | そのまま（v2 も `bindings` を使う） |
//  | Axis1D         | `WASD`          | Horizontal → positive:[D,→] / negative:[A,←]<br>Vertical → positive:[W,↑] / negative:[S,↓] |
//  | Axis1D         | `Key`           | positive（v1 の「キー押下で +1」挙動を保つ） |
//  | Axis2D         | `WASD`          | Horizontal → x、Vertical → y（正負は上と同じ） |
//
//  **移し終えた要素だけを `bindings` から取り除く**。`GamepadButton` など
//  v1 の読み込みでも無視されていた要素はそのまま残す。
//  一括アップグレードはこの結果をファイルへ書き戻すため、ここで捨てると
//  ユーザーが登録したバインドが**ディスク上から消える**。読み込み時に無視されるのと
//  ファイルから消えるのとでは被害がまるで違うので、消さずに残す。
//
//  【冪等性】
//  2 回流しても結果は変わらない。1 回目で `WASD` / `Key` は `bindings` から
//  取り除かれるため、2 回目は移すものが無い。
// ============================================================

use serde_json::{Map, Value};

// ── JSON のキー名（実装との対応。ここ以外に文字列を散らさない）──────────

/// ファイル直下のアクション配列のキー。
const KEY_ACTIONS: &str = "actions";
/// アクションの値の型のキー（0=Bool / 1=Axis1D / 2=Axis2D）。
const KEY_VALUE_TYPE: &str = "value_type";
/// v1 の平らなバインド配列のキー（v2 では Bool 専用）。
const KEY_BINDINGS: &str = "bindings";
/// Axis1D / 各軸の正バインド配列のキー。
const KEY_POSITIVE: &str = "positive";
/// Axis1D / 各軸の負バインド配列のキー。
const KEY_NEGATIVE: &str = "negative";
/// Axis2D の X 軸オブジェクトのキー。
const KEY_X: &str = "x";
/// Axis2D の Y 軸オブジェクトのキー。
const KEY_Y: &str = "y";
/// バインドのプラットフォームのキー。
const KEY_PLATFORM: &str = "platform";
/// バインドの入力種別のキー。
const KEY_INPUT_TYPE: &str = "input_type";
/// バインドの値（キー名・ボタン名・合成軸名）のキー。
const KEY_VALUE: &str = "value";

// ── 値（`action_map.rs` の定数と一致させること）──────────────────────

/// 評価対象のプラットフォーム。これ以外のバインドは v1 でも無視されていた。
const PLATFORM_PC: &str = "PC";
/// 入力種別: 物理キー。
const INPUT_TYPE_KEY: &str = "Key";
/// 入力種別: WASD 合成軸（v1 のみ）。
const INPUT_TYPE_WASD: &str = "WASD";
/// WASD 合成軸の値: 水平。
const WASD_HORIZONTAL: &str = "Horizontal";
/// WASD 合成軸の値: 垂直。
const WASD_VERTICAL: &str = "Vertical";

/// アクション値の型（エディタ `ActionValueType` と数値一致）。
const VALUE_TYPE_BOOL: i64 = 0;
const VALUE_TYPE_AXIS1D: i64 = 1;
const VALUE_TYPE_AXIS2D: i64 = 2;

/// WASD 水平軸を展開したときの正側キー名（エディタのキー名表記）。
const HORIZONTAL_POSITIVE_KEYS: &[&str] = &["D", "RightArrow"];
/// WASD 水平軸を展開したときの負側キー名。
const HORIZONTAL_NEGATIVE_KEYS: &[&str] = &["A", "LeftArrow"];
/// WASD 垂直軸を展開したときの正側キー名。
const VERTICAL_POSITIVE_KEYS: &[&str] = &["W", "UpArrow"];
/// WASD 垂直軸を展開したときの負側キー名。
const VERTICAL_NEGATIVE_KEYS: &[&str] = &["S", "DownArrow"];

// ── 変換本体 ─────────────────────────────────────────────────────

/// `.inputmap` を v1 → v2 へ 1 段だけ持ち上げる。
///
/// トップレベルがオブジェクトでない・`actions` が配列でない場合は何もしない
/// （構造の検証は本体のデシリアライズの責務。ここは「直せるものだけ直す」）。
pub fn migrate(value: &mut Value) -> Result<(), String> {
    let Some(actions) = value.get_mut(KEY_ACTIONS).and_then(Value::as_array_mut) else {
        return Ok(());
    };
    for action in actions.iter_mut() {
        migrate_action(action);
    }
    Ok(())
}

/// アクション 1 件を v2 の形へ直す。
fn migrate_action(action: &mut Value) {
    let value_type = action
        .get(KEY_VALUE_TYPE)
        .and_then(Value::as_i64)
        .unwrap_or(VALUE_TYPE_BOOL);

    match value_type {
        VALUE_TYPE_AXIS1D => migrate_axis1d(action),
        VALUE_TYPE_AXIS2D => migrate_axis2d(action),
        // Bool（および未知の値）は v2 でも `bindings` をそのまま使うので手を触れない。
        _ => {}
    }
}

/// Axis1D アクションの `bindings` を `positive` / `negative` へ展開する。
fn migrate_axis1d(action: &mut Value) {
    // 移す対象（WASD / 素のキー）だけを取り出す。残りは `bindings` に残す。
    let moved = take_convertible_bindings(action, /* include_plain_keys = */ true);
    if moved.is_empty() {
        return;
    }
    let (mut positive, mut negative) = (Vec::new(), Vec::new());
    for binding in &moved {
        expand_binding(binding, &mut positive, &mut negative);
    }
    append_bindings(action, KEY_POSITIVE, positive);
    append_bindings(action, KEY_NEGATIVE, negative);
}

/// Axis2D アクションの `bindings`（WASD のみ）を `x` / `y` の正負へ展開する。
fn migrate_axis2d(action: &mut Value) {
    // v1 の読み込みは Axis2D の素のキーを無視していたので、WASD だけを移す。
    let moved = take_convertible_bindings(action, /* include_plain_keys = */ false);
    if moved.is_empty() {
        return;
    }
    let (mut x_pos, mut x_neg) = (Vec::new(), Vec::new());
    let (mut y_pos, mut y_neg) = (Vec::new(), Vec::new());
    for binding in &moved {
        match binding.get(KEY_VALUE).and_then(Value::as_str).unwrap_or("") {
            WASD_HORIZONTAL => expand_binding(binding, &mut x_pos, &mut x_neg),
            WASD_VERTICAL => expand_binding(binding, &mut y_pos, &mut y_neg),
            _ => {}
        }
    }
    append_axis_bindings(action, KEY_X, x_pos, x_neg);
    append_axis_bindings(action, KEY_Y, y_pos, y_neg);
}

// ── ヘルパー ─────────────────────────────────────────────────────

/// `bindings` から「v2 のグループへ移せる要素」だけを抜き取って返す。
///
/// * `include_plain_keys` … 素のキー（`input_type == "Key"`）も移すか
///   （Axis1D は移す＝v1 の「押下で +1」挙動、Axis2D は v1 でも無視されていたので移さない）
///
/// 抜き取った後に `bindings` が空になったらキーごと削除する
/// （v2 の軸アクションは `bindings` を使わないため、空配列を残す意味が無い）。
fn take_convertible_bindings(action: &mut Value, include_plain_keys: bool) -> Vec<Value> {
    let Some(bindings) = action.get_mut(KEY_BINDINGS).and_then(Value::as_array_mut) else {
        return Vec::new();
    };

    let mut moved = Vec::new();
    let mut kept = Vec::new();
    for binding in bindings.drain(..) {
        if is_convertible(&binding, include_plain_keys) {
            moved.push(binding);
        } else {
            kept.push(binding);
        }
    }
    *bindings = kept;

    if bindings.is_empty() {
        if let Some(obj) = action.as_object_mut() {
            obj.remove(KEY_BINDINGS);
        }
    }
    moved
}

/// このバインドを v2 のグループへ移せるか（＝v1 の読み込みが解釈していたか）。
fn is_convertible(binding: &Value, include_plain_keys: bool) -> bool {
    if binding.get(KEY_PLATFORM).and_then(Value::as_str) != Some(PLATFORM_PC) {
        return false;
    }
    match binding.get(KEY_INPUT_TYPE).and_then(Value::as_str) {
        Some(INPUT_TYPE_WASD) => true,
        Some(INPUT_TYPE_KEY) => include_plain_keys,
        _ => false,
    }
}

/// バインド 1 件を正負のグループへ展開する。
///
/// - `WASD` … 合成軸を 4 つ（左右 2 本ずつ）のキーバインドへ割る
/// - `Key`  … そのまま正側へ置く（v1 の挙動）
fn expand_binding(binding: &Value, positive: &mut Vec<Value>, negative: &mut Vec<Value>) {
    match binding.get(KEY_INPUT_TYPE).and_then(Value::as_str) {
        Some(INPUT_TYPE_WASD) => {
            let (pos_keys, neg_keys) =
                match binding.get(KEY_VALUE).and_then(Value::as_str).unwrap_or("") {
                    WASD_HORIZONTAL => (HORIZONTAL_POSITIVE_KEYS, HORIZONTAL_NEGATIVE_KEYS),
                    WASD_VERTICAL => (VERTICAL_POSITIVE_KEYS, VERTICAL_NEGATIVE_KEYS),
                    // 未知の合成軸は展開できない（v1 の読み込みでも無反応だった）。
                    _ => return,
                };
            positive.extend(pos_keys.iter().map(|k| key_binding(k)));
            negative.extend(neg_keys.iter().map(|k| key_binding(k)));
        }
        Some(INPUT_TYPE_KEY) => positive.push(binding.clone()),
        _ => {}
    }
}

/// キー 1 個ぶんの v2 バインドオブジェクトを作る。
fn key_binding(key_name: &str) -> Value {
    let mut obj = Map::new();
    obj.insert(KEY_PLATFORM.to_string(), Value::from(PLATFORM_PC));
    obj.insert(KEY_INPUT_TYPE.to_string(), Value::from(INPUT_TYPE_KEY));
    obj.insert(KEY_VALUE.to_string(), Value::from(key_name));
    Value::Object(obj)
}

/// アクション直下の配列（`positive` / `negative`）へ要素を足す。
///
/// 既にある要素は残し、末尾へ追加する（v1 でも「既存の正負 ＋ bindings の展開」だった）。
fn append_bindings(target: &mut Value, key: &str, items: Vec<Value>) {
    if items.is_empty() {
        return;
    }
    let Some(obj) = target.as_object_mut() else {
        return;
    };
    let entry = obj.entry(key.to_string()).or_insert_with(|| Value::Array(Vec::new()));
    // 既存の値が配列でなければ（壊れたファイル）作り直す。
    if !entry.is_array() {
        *entry = Value::Array(Vec::new());
    }
    if let Some(arr) = entry.as_array_mut() {
        arr.extend(items);
    }
}

/// Axis2D の軸オブジェクト（`x` / `y`）の正負へ要素を足す。無ければ軸オブジェクトを作る。
fn append_axis_bindings(action: &mut Value, axis_key: &str, positive: Vec<Value>, negative: Vec<Value>) {
    if positive.is_empty() && negative.is_empty() {
        return;
    }
    let Some(obj) = action.as_object_mut() else {
        return;
    };
    let axis = obj
        .entry(axis_key.to_string())
        .or_insert_with(|| Value::Object(Map::new()));
    if !axis.is_object() {
        *axis = Value::Object(Map::new());
    }
    append_bindings(axis, KEY_POSITIVE, positive);
    append_bindings(axis, KEY_NEGATIVE, negative);
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// Axis1D の WASD 合成軸が正負のキーへ展開され、`bindings` から消えること。
    #[test]
    fn axis1d_wasd_expands_into_positive_and_negative() {
        let mut v = json!({
            "actions": [
                { "name": "Steer", "value_type": 1, "bindings": [
                    { "platform": "PC", "input_type": "WASD", "value": "Horizontal" }
                ] }
            ]
        });
        migrate(&mut v).unwrap();

        let action = &v["actions"][0];
        let pos: Vec<&str> = action["positive"]
            .as_array()
            .unwrap()
            .iter()
            .map(|b| b["value"].as_str().unwrap())
            .collect();
        let neg: Vec<&str> = action["negative"]
            .as_array()
            .unwrap()
            .iter()
            .map(|b| b["value"].as_str().unwrap())
            .collect();
        assert_eq!(pos, vec!["D", "RightArrow"]);
        assert_eq!(neg, vec!["A", "LeftArrow"]);
        assert!(action.get("bindings").is_none(), "空の bindings が残っている: {action}");
    }

    /// Axis1D の素のキーは正側へ移ること（v1 の「押下で +1」挙動）。
    #[test]
    fn axis1d_plain_key_moves_to_positive() {
        let mut v = json!({
            "actions": [
                { "name": "Throttle", "value_type": 1, "bindings": [
                    { "platform": "PC", "input_type": "Key", "value": "Space" }
                ] }
            ]
        });
        migrate(&mut v).unwrap();
        assert_eq!(v["actions"][0]["positive"][0]["value"], json!("Space"));
        assert!(v["actions"][0].get("bindings").is_none());
    }

    /// 既存の正負グループがある場合は末尾へ足されること（上書きしない）。
    #[test]
    fn existing_groups_are_kept_and_appended_to() {
        let mut v = json!({
            "actions": [
                { "name": "Steer", "value_type": 1,
                  "positive": [ { "platform": "PC", "input_type": "GamepadAxis", "value": "LeftStickX" } ],
                  "bindings": [ { "platform": "PC", "input_type": "WASD", "value": "Horizontal" } ] }
            ]
        });
        migrate(&mut v).unwrap();
        let pos = v["actions"][0]["positive"].as_array().unwrap();
        assert_eq!(pos.len(), 3, "既存 1 件 ＋ 展開 2 件: {pos:?}");
        assert_eq!(pos[0]["input_type"], json!("GamepadAxis"));
        assert_eq!(pos[1]["value"], json!("D"));
    }

    /// Axis2D の WASD は x / y へ振り分けられること。
    #[test]
    fn axis2d_wasd_splits_into_x_and_y() {
        let mut v = json!({
            "actions": [
                { "name": "Move", "value_type": 2, "bindings": [
                    { "platform": "PC", "input_type": "WASD", "value": "Horizontal" },
                    { "platform": "PC", "input_type": "WASD", "value": "Vertical" }
                ] }
            ]
        });
        migrate(&mut v).unwrap();
        let action = &v["actions"][0];
        assert_eq!(action["x"]["positive"][0]["value"], json!("D"));
        assert_eq!(action["x"]["negative"][1]["value"], json!("LeftArrow"));
        assert_eq!(action["y"]["positive"][0]["value"], json!("W"));
        assert_eq!(action["y"]["negative"][1]["value"], json!("DownArrow"));
        assert!(action.get("bindings").is_none());
    }

    /// Axis2D の素のキーは v1 でも無視されていたので、`bindings` に残ること
    /// （＝ファイルから消えないこと）。
    #[test]
    fn axis2d_plain_key_stays_in_bindings() {
        let mut v = json!({
            "actions": [
                { "name": "Move", "value_type": 2, "bindings": [
                    { "platform": "PC", "input_type": "Key", "value": "Space" }
                ] }
            ]
        });
        migrate(&mut v).unwrap();
        let bindings = v["actions"][0]["bindings"].as_array().unwrap();
        assert_eq!(bindings.len(), 1, "素のキーが消えている");
        assert!(v["actions"][0].get("x").is_none());
    }

    /// 移せないバインド（ゲームパッド・PC 以外）は `bindings` に残ること。
    /// 読み込みでは無視されるが、**ファイルからは消さない**。
    #[test]
    fn unconvertible_bindings_are_left_in_place() {
        let mut v = json!({
            "actions": [
                { "name": "Steer", "value_type": 1, "bindings": [
                    { "platform": "PC",     "input_type": "GamepadAxis", "value": "LeftStickX" },
                    { "platform": "Switch", "input_type": "WASD",        "value": "Horizontal" },
                    { "platform": "PC",     "input_type": "WASD",        "value": "Horizontal" }
                ] }
            ]
        });
        migrate(&mut v).unwrap();
        let bindings = v["actions"][0]["bindings"].as_array().unwrap();
        assert_eq!(bindings.len(), 2, "残すべきバインドが消えている: {bindings:?}");
        assert_eq!(bindings[0]["input_type"], json!("GamepadAxis"));
        assert_eq!(bindings[1]["platform"], json!("Switch"));
        // PC の WASD だけが展開されている
        assert_eq!(v["actions"][0]["positive"].as_array().unwrap().len(), 2);
    }

    /// Bool アクションには一切手を触れないこと。
    #[test]
    fn bool_actions_are_untouched() {
        let original = json!({
            "actions": [
                { "name": "Fire", "value_type": 0, "condition": "Press", "bindings": [
                    { "platform": "PC", "input_type": "Key", "value": "Space" }
                ] }
            ]
        });
        let mut v = original.clone();
        migrate(&mut v).unwrap();
        assert_eq!(v, original);
    }

    /// 2 回流しても結果が変わらないこと（冪等）。
    #[test]
    fn migrating_twice_is_idempotent() {
        let mut v = json!({
            "actions": [
                { "name": "Move", "value_type": 2, "bindings": [
                    { "platform": "PC", "input_type": "WASD", "value": "Horizontal" }
                ] },
                { "name": "Steer", "value_type": 1, "bindings": [
                    { "platform": "PC", "input_type": "Key", "value": "E" }
                ] }
            ]
        });
        migrate(&mut v).unwrap();
        let once = v.clone();
        migrate(&mut v).unwrap();
        assert_eq!(v, once);
    }

    /// 壊れた形（actions が無い・配列でない・アクションがオブジェクトでない）でも
    /// panic せず、エラーにもしないこと。
    #[test]
    fn tolerates_broken_shapes() {
        let mut empty = json!({});
        migrate(&mut empty).unwrap();

        let mut not_array = json!({ "actions": "nope" });
        migrate(&mut not_array).unwrap();

        let mut not_object = json!({ "actions": [1, "two", null] });
        migrate(&mut not_object).unwrap();
    }
}
