// ============================================================
//  prefab_live_patch/merge.rs — 値の 3 方向の合成（純粋な関数）
//
//  コンポーネント・CanvasTransform の値を JSON（serde の表現＝保存と同じ形）にして、
//  「元の版 base」「新しい版 new」「動いている値 live」から当て直し後の値を作る。
//
//  【規則】
//   - base と new が同じ部分 … ファイルは変えていない → live を残す
//     （スクリプトが Play 中に書いた文字・色・位置を守る。これが「状態を保ったまま」の要）
//   - base と new が違う部分 … ファイルが変えた → new を当てる
//   - オブジェクトは鍵ごとに再帰する（文字の大きさだけ変えたなら、本文は live のまま）
//   - 配列・数値・文字列はまるごと 1 個の値として扱う（要素ごとには混ぜない）
//   - new にだけある鍵 … 当てる。base にあって new に無い鍵 … 消す（serde の既定値へ戻る）
//  base が無い（2 方向）ときはこの関数を使わず new をそのまま当てる（呼び出し側。apply.rs）。
// ============================================================

use serde_json::{Map, Value};

/// 3 方向の合成をする。
///
/// * `base` … 元の版の値
/// * `new`  … 新しい版の値
/// * `live` … 動いている値
///
/// # 戻り値
/// 当て直し後の値。ファイルが何も変えていなければ `live` と同じ値になる。
pub(crate) fn merge3(base: &Value, new: &Value, live: &Value) -> Value {
    // ファイルが変えていない部分は動いている値を残す
    if base == new {
        return live.clone();
    }
    match (base, new, live) {
        // 3 つともオブジェクト: 鍵ごとに合成する
        (Value::Object(b), Value::Object(n), Value::Object(l)) => Value::Object(merge_objects(b, n, l)),
        // それ以外（配列・値・型が変わった）: ファイルの値を当てる
        _ => new.clone(),
    }
}

/// オブジェクトの鍵ごとの合成。
fn merge_objects(base: &Map<String, Value>, new: &Map<String, Value>, live: &Map<String, Value>) -> Map<String, Value> {
    let mut out = live.clone();
    // new の鍵: 変わったものだけ当てる
    for (key, new_value) in new {
        let merged = match (base.get(key), live.get(key)) {
            (Some(b), Some(l)) => merge3(b, new_value, l),
            // 元の版に無い（ファイルが足した）・動いている値に無い（skip_serializing_if で省かれた既定値）
            (Some(b), None) if b == new_value => continue, // ファイルは変えていない。live の省略（既定値）を守る
            _ => new_value.clone(),
        };
        out.insert(key.clone(), merged);
    }
    // base にあって new に無い鍵: ファイルが既定値へ戻した（serde が省いた）→ 消して既定値に任せる
    for key in base.keys() {
        if !new.contains_key(key) {
            out.remove(key);
        }
    }
    out
}

/// 動いている値を JSON のまま残したい欄を、合成結果へ live から書き戻す。
///
/// 3D モデルのインスタンス配列のように「ファイル（原点基準）と Play 中の値（ワールド空間）で
/// 座標系が違う」欄は、ファイルの値を当てると位置が飛ぶ。そうした欄の名前を `keys` に渡す。
///
/// * `merged` … 合成結果（`{"type":..,"data":{..}}` の `data` の中身）
/// * `live`   … 動いている値（同じ形）
pub(crate) fn keep_live_keys(merged: &mut Value, live: &Value, keys: &[&str]) {
    let (Some(out), Some(src)) = (merged.as_object_mut(), live.as_object()) else { return };
    for key in keys {
        match src.get(*key) {
            Some(value) => {
                out.insert((*key).to_string(), value.clone());
            }
            None => {
                out.remove(*key);
            }
        }
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// ファイルが変えていない欄は、実行中に書かれた値を残す（本文を守って大きさだけ当てる）。
    #[test]
    fn untouched_fields_keep_live_values() {
        let base = json!({"text": "00:00", "size": 24, "color": [1, 1, 1, 1]});
        let new = json!({"text": "00:00", "size": 32, "color": [1, 1, 1, 1]});
        let live = json!({"text": "07:30", "size": 24, "color": [1, 0, 0, 1]});
        let merged = merge3(&base, &new, &live);
        assert_eq!(merged, json!({"text": "07:30", "size": 32, "color": [1, 0, 0, 1]}));
    }

    /// ファイルが何も変えていなければ live そのもの。
    #[test]
    fn identical_versions_return_live() {
        let base = json!({"a": 1});
        let live = json!({"a": 5, "b": 2});
        assert_eq!(merge3(&base, &base, &live), live);
    }

    /// 入れ子のオブジェクトは鍵ごとに合成する。
    #[test]
    fn nested_objects_merge_per_key() {
        let base = json!({"padding": {"left": 4, "right": 4}});
        let new = json!({"padding": {"left": 8, "right": 4}});
        let live = json!({"padding": {"left": 4, "right": 12}});
        assert_eq!(merge3(&base, &new, &live), json!({"padding": {"left": 8, "right": 12}}));
    }

    /// 配列はまるごと 1 個の値（要素ごとには混ぜない）。
    #[test]
    fn arrays_are_replaced_whole() {
        let base = json!({"stops": [0, 1]});
        let new = json!({"stops": [0, 0.5, 1]});
        let live = json!({"stops": [0.2, 1]});
        assert_eq!(merge3(&base, &new, &live), json!({"stops": [0, 0.5, 1]}));
    }

    /// ファイルが足した欄は当て、ファイルが消した（既定値へ戻した）欄は消す。
    #[test]
    fn added_and_removed_keys() {
        let base = json!({"old": true, "keep": 1});
        let new = json!({"keep": 1, "added": "x"});
        let live = json!({"old": true, "keep": 9});
        assert_eq!(merge3(&base, &new, &live), json!({"keep": 9, "added": "x"}));
    }

    /// live が省いている欄（既定値）は、ファイルが変えていなければ省いたまま。
    #[test]
    fn live_default_omission_is_respected() {
        let base = json!({"wrap": false, "size": 10});
        let new = json!({"wrap": false, "size": 12});
        let live = json!({"size": 10});
        assert_eq!(merge3(&base, &new, &live), json!({"size": 12}));
    }

    /// 座標系の違う欄は live の値を書き戻せる（無ければ消す）。
    #[test]
    fn keep_live_keys_restores_listed_fields() {
        let mut merged = json!({"instances": [[0]], "visible": false, "meta": []});
        let live = json!({"instances": [[5]], "visible": true});
        keep_live_keys(&mut merged, &live, &["instances", "meta"]);
        assert_eq!(merged, json!({"instances": [[5]], "visible": false}));
    }
}
