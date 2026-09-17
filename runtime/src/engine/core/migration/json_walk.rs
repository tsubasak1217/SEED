// ============================================================
//  json_walk.rs — アクタ木とコンポーネントを辿る共通ヘルパ
//
//  【なぜ必要か】
//  シーンもアクタも「コンポーネントの入れ物」であり、変換の大半は
//  「ある種類のコンポーネントのデータを直す」形になる。その走査を各変換段で
//  書き直すと、子アクタの辿り忘れ・スロットの構造の取り違えが必ず起きる。
//  ここに 1 本だけ置き、`.scene` と `.actor` の変換段が共用する。
//
//  【前提にしている JSON の形】
//  実装（`app_base/scene.rs` の `SceneData` / `SceneDataRef`、
//  `structs/objects/actor/mod.rs` の `ActorData` / `ComponentSlotData`、
//  `components/mod.rs` の `ComponentData`）に合わせてある。
//
//    シーン: { "name": …, "actors": [ <アクタ>, … ] }
//    アクタ: {
//      "name": …,
//      "components": [ { "name": …, "component": { "type": "XxxComponent",
//                                                  "data": <コンポーネントのデータ> },
//                        "enabled": true }, … ],
//      "children": [ <アクタ>, … ]
//    }
//
//  `ComponentData` は `#[serde(tag = "type", content = "data")]` なので
//  種別名は `component.type`、実データは `component.data` に入る。
//
//  【安全側の設計】
//  欠けているキー・型違いは「そこには何も無い」として黙って読み飛ばす。
//  変換段は「直せるものだけ直す」責務であり、構造の検証は本体のデシリアライズが行う。
// ============================================================

use serde_json::Value;

// ── JSON のキー名（実装との対応。ここ以外に文字列を散らさない）──────────

/// シーンのアクタ配列のキー。
const KEY_ACTORS: &str = "actors";
/// アクタの子アクタ配列のキー。
const KEY_CHILDREN: &str = "children";
/// アクタのコンポーネントスロット配列のキー。
const KEY_COMPONENTS: &str = "components";
/// スロットが持つコンポーネント本体のキー。
const KEY_COMPONENT: &str = "component";
/// コンポーネントの種別名のキー（`ComponentData` の `tag`）。
const KEY_TYPE: &str = "type";
/// コンポーネントの実データのキー（`ComponentData` の `content`）。
const KEY_DATA: &str = "data";

// ── アクタ木の走査 ───────────────────────────────────────────────

/// シーン JSON 直下の全ルートアクタ（とその子孫）へ `f` を適用する。
///
/// `actors` が無い・配列でない場合は何もしない。
pub fn for_each_actor_in_scene<F>(scene: &mut Value, f: &mut F)
where
    F: FnMut(&mut Value),
{
    let Some(actors) = scene.get_mut(KEY_ACTORS).and_then(Value::as_array_mut) else {
        return;
    };
    for actor in actors.iter_mut() {
        for_each_actor_in_tree(actor, f);
    }
}

/// 1 本のアクタ（`.actor` のトップレベル）とその全子孫へ `f` を適用する。
///
/// 適用順は自身 → 子（DFS 前順）。`f` はアクタのオブジェクトそのものを受け取る。
pub fn for_each_actor_in_tree<F>(actor: &mut Value, f: &mut F)
where
    F: FnMut(&mut Value),
{
    if !actor.is_object() {
        return;
    }
    f(actor);
    if let Some(children) = actor.get_mut(KEY_CHILDREN).and_then(Value::as_array_mut) {
        for child in children.iter_mut() {
            for_each_actor_in_tree(child, f);
        }
    }
}

// ── コンポーネントの走査 ─────────────────────────────────────────

/// アクタ **1 個**（子孫は見ない）の中から、指定種別のコンポーネントの
/// **データ部**（`component.data`）をすべて辿って `f` を適用する。
///
/// * `type_name` … `ComponentData` の variant 名（例 `"ParticleEmitterComponent"`）
///
/// 同じ種別のコンポーネントを複数スロット持てる（`ComponentSlot` の設計）ため、
/// 最初の 1 件で打ち切らずすべてに適用する。
/// データ部が無いコンポーネント（ユニット variant）は対象にならない。
///
/// 木全体へ流したいときは `for_each_actor_in_tree` / `for_each_actor_in_scene` と
/// 組み合わせる（走査と対象選別の責務を分けてある）。
pub fn for_each_component_data_in_actor<F>(actor: &mut Value, type_name: &str, f: &mut F)
where
    F: FnMut(&mut Value),
{
    let Some(slots) = actor.get_mut(KEY_COMPONENTS).and_then(Value::as_array_mut) else {
        return;
    };
    for slot in slots.iter_mut() {
        let Some(component) = slot.get_mut(KEY_COMPONENT) else {
            continue;
        };
        let is_target = component
            .get(KEY_TYPE)
            .and_then(Value::as_str)
            .is_some_and(|t| t == type_name);
        if !is_target {
            continue;
        }
        if let Some(data) = component.get_mut(KEY_DATA) {
            f(data);
        }
    }
}

// ── 文字列値の付け替え（enum の旧表記の正規化に使う）────────────────

/// オブジェクトの `key` が**文字列**で、旧表記の表に載っていれば現行表記へ書き換える。
///
/// * `table` … `(旧表記, 現行表記)` の並び
///
/// 戻り値は書き換えたかどうか（変換段が「実際に直した件数」を数えるのに使う）。
/// 値がオブジェクト（例: `shape: {"model": {...}}`）や欠損のときは何もしない。
pub fn rename_string_field(target: &mut Value, key: &str, table: &[(&str, &str)]) -> bool {
    let Some(current) = target.get(key).and_then(Value::as_str) else {
        return false;
    };
    let Some((_, new_name)) = table.iter().find(|(old, _)| *old == current) else {
        return false;
    };
    let new_value = Value::String((*new_name).to_string());
    if let Some(obj) = target.as_object_mut() {
        obj.insert(key.to_string(), new_value);
        return true;
    }
    false
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// テスト用: コンポーネント 1 個を持つアクタ JSON を作る。
    fn actor_with(name: &str, comp_type: &str, data: Value) -> Value {
        json!({
            "name": name,
            "components": [
                { "name": "Slot", "component": { "type": comp_type, "data": data } }
            ],
            "children": []
        })
    }

    /// シーン配下のアクタを、子孫も含めて DFS 前順で全て辿ること。
    #[test]
    fn walks_every_actor_in_scene_depth_first() {
        let mut scene = json!({
            "name": "S",
            "actors": [
                {
                    "name": "root",
                    "components": [],
                    "children": [
                        { "name": "childA", "components": [], "children": [
                            { "name": "grandchild", "components": [], "children": [] }
                        ] },
                        { "name": "childB", "components": [], "children": [] }
                    ]
                },
                { "name": "root2", "components": [], "children": [] }
            ]
        });

        let mut visited: Vec<String> = Vec::new();
        for_each_actor_in_scene(&mut scene, &mut |a| {
            visited.push(a["name"].as_str().unwrap_or_default().to_string());
        });
        assert_eq!(
            visited,
            vec!["root", "childA", "grandchild", "childB", "root2"]
        );
    }

    /// 対象種別のコンポーネントだけへ、同一アクタの複数スロットも漏らさず適用すること。
    #[test]
    fn applies_only_to_matching_component_type_including_multiple_slots() {
        let mut actor = json!({
            "name": "root",
            "components": [
                { "name": "P1", "component": { "type": "ParticleEmitterComponent", "data": { "blend": "alpha" } } },
                { "name": "M",  "component": { "type": "ModelComponent",           "data": { "blend": "alpha" } } },
                { "name": "P2", "component": { "type": "ParticleEmitterComponent", "data": { "blend": "additive" } } }
            ],
            "children": [
                actor_with("child", "ParticleEmitterComponent", json!({ "blend": "alpha" }))
            ]
        });

        let mut hits = 0usize;
        for_each_actor_in_tree(&mut actor, &mut |a| {
            for_each_component_data_in_actor(a, "ParticleEmitterComponent", &mut |d| {
                hits += 1;
                d["touched"] = json!(true);
            });
        });
        assert_eq!(hits, 3, "同一アクタの複数スロットと子アクタの分が拾えていない");
        // 対象外のコンポーネントには触っていないこと
        assert!(actor["components"][1]["component"]["data"]
            .get("touched")
            .is_none());
    }

    /// 壊れた形（actors が配列でない・components が無い・data が無い）でも panic しないこと。
    #[test]
    fn tolerates_missing_or_wrongly_typed_keys() {
        /// テスト用: シーン配下の全アクタから対象コンポーネントのデータ部を辿る。
        fn walk_scene(scene: &mut Value, type_name: &str, f: &mut impl FnMut(&mut Value)) {
            for_each_actor_in_scene(scene, &mut |a| {
                for_each_component_data_in_actor(a, type_name, f);
            });
        }

        let mut broken = json!({ "actors": "not-an-array" });
        walk_scene(&mut broken, "AnyComponent", &mut |_| {
            panic!("呼ばれてはいけない");
        });

        let mut no_components = json!({ "actors": [ { "name": "a" } ] });
        walk_scene(&mut no_components, "AnyComponent", &mut |_| {
            panic!("呼ばれてはいけない");
        });

        let mut no_data = json!({
            "actors": [ { "name": "a", "components": [ { "component": { "type": "X" } } ] } ]
        });
        walk_scene(&mut no_data, "X", &mut |_| panic!("呼ばれてはいけない"));
    }

    /// 文字列の旧表記だけを書き換え、オブジェクト値・表に無い値・欠損には触らないこと。
    #[test]
    fn rename_string_field_only_touches_listed_string_values() {
        const TABLE: &[(&str, &str)] = &[("point", "pixel")];

        let mut v = json!({ "shape": "point" });
        assert!(rename_string_field(&mut v, "shape", TABLE));
        assert_eq!(v["shape"], json!("pixel"));

        // 既に現行表記 → 何もしない
        let mut v = json!({ "shape": "pixel" });
        assert!(!rename_string_field(&mut v, "shape", TABLE));
        assert_eq!(v["shape"], json!("pixel"));

        // オブジェクト値（externally tagged の Model variant）→ 何もしない
        let mut v = json!({ "shape": { "model": { "path": "a.glb" } } });
        assert!(!rename_string_field(&mut v, "shape", TABLE));
        assert_eq!(v["shape"]["model"]["path"], json!("a.glb"));

        // 欠損 → 何もしない
        let mut v = json!({ "other": 1 });
        assert!(!rename_string_field(&mut v, "shape", TABLE));
    }
}
