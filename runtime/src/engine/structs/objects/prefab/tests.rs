// ============================================================
//  tests.rs — プレハブオーバーライドの抽出・再適用のユニットテスト
//
//  【方針】
//  テストは実際に `.scene` / `.actor` へ書き出される JSON からデータを組み立てる。
//  構造体リテラルで組むより、保存フォーマットそのもの（serde 経由）を通す方が
//  「保存して壊れないか」という目的に一致し、後方互換の検証も同時に行える。
//
//  再現するのは実際に起きたデータ損失バグの構図:
//   - Player アクタが assets://actors/BrainStem.actor のプレハブインスタンス
//   - シーン側で Collider の freeze_rotation を変更し、Script を追加し、
//     子に Camera を持つアクタを追加した
//   - シーン再オープン時、再展開でそれらが .actor の内容に上書きされて消えていた
// ============================================================

use crate::engine::components::ComponentData;
use crate::engine::structs::objects::actor::ActorData;

use super::apply::{apply_delta_to_subtree, merge_overrides_into};
use super::extract::{compute_reinstantiate_delta, extract_prefab_overrides};
use super::overrides::PrefabOverrides;

// ============================================================
//  テスト用データ
// ============================================================

/// プレハブ本体（`.actor`）の内容。ルート位置＝原点基準で保存されている。
///
/// - ルートに Collider（freeze_rotation は全 false）
/// - 子 "Arm" に Model コンポーネント（インスタンス行列は単位行列）
const PREFAB_JSON: &str = r#"{
  "name": "BrainStem",
  "transform": { "position": [0,0,0], "rotation": [0,0,0], "scale": [1,1,1] },
  "components": [
    {
      "name": "Collider",
      "component": {
        "type": "ColliderComponent",
        "data": {
          "shape": { "type": "Sphere", "radius": 1.0 },
          "offset": [0,0,0],
          "is_trigger": false,
          "physics_layer": 0,
          "layer_mask": 4294967295,
          "freeze_rotation": [false, false, false]
        }
      }
    }
  ],
  "children": [
    {
      "name": "Arm",
      "transform": { "position": [0,1,0], "rotation": [0,0,0], "scale": [1,1,1] },
      "components": [
        {
          "name": "Model",
          "component": {
            "type": "ModelComponent",
            "data": {
              "model_path": "",
              "instances": [[[1,0,0,0],[0,1,0,0],[0,0,1,0],[0,1,0,1]]]
            }
          }
        }
      ],
      "children": []
    }
  ]
}"#;

/// プレハブ本体をパースする。
fn prefab() -> ActorData {
    serde_json::from_str(PREFAB_JSON).expect("プレハブ本体 JSON のパースに失敗")
}

/// シーン上のインスタンス（プレハブと同じ内容・同じ位置）を作る。
/// これを起点に「ユーザーが加えた変更」を足していく。
fn instance_same_as_prefab() -> ActorData {
    let mut d = prefab();
    d.name = "Player".into();
    d.prefab_source = Some("assets://actors/BrainStem.actor".into());
    d
}

/// 指定名のスロットを探して ColliderComponentData の freeze_rotation を返す。
fn freeze_rotation_of(data: &ActorData, slot_name: &str) -> Option<[bool; 3]> {
    data.components.iter()
        .find(|s| s.name == slot_name)
        .and_then(|s| match &s.component {
            ComponentData::ColliderComponent(c) => Some(c.freeze_rotation),
            _ => None,
        })
}

// ============================================================
//  1. 値の上書き（プレハブにも存在するコンポーネント）
// ============================================================

/// Collider の freeze_rotation 変更がオーバーライドとして抽出され、
/// 再展開（プレハブ本体からの再構築）後のマージで復元されることを検証する。
#[test]
fn modified_component_is_extracted_and_restored() {
    // シーン側で freeze_rotation を書き換える
    let mut scene = instance_same_as_prefab();
    if let ComponentData::ColliderComponent(ref mut c) = scene.components[0].component {
        c.freeze_rotation = [true, true, true];
    }

    // 抽出: 値の上書きが 1 件だけ記録される
    let ov = extract_prefab_overrides(&scene, &prefab());
    assert_eq!(ov.modified_components.len(), 1, "Collider の変更が上書きとして抽出されること");
    assert_eq!(ov.added_components.len(), 0);
    assert_eq!(ov.added_children.len(), 0);
    assert_eq!(ov.modified_components[0].key.type_tag, "ColliderComponent");
    assert!(ov.modified_components[0].path.is_empty(), "ルート直下のコンポーネントなのでパスは空");

    // 再適用: プレハブ本体（freeze_rotation 全 false）へマージするとシーンの値が勝つ
    let mut rebuilt = prefab();
    assert_eq!(freeze_rotation_of(&rebuilt, "Collider"), Some([false, false, false]));
    merge_overrides_into(&mut rebuilt, &ov);
    assert_eq!(
        freeze_rotation_of(&rebuilt, "Collider"), Some([true, true, true]),
        "再展開後もシーン側の freeze_rotation が保持されること"
    );
    // スロットが増えていない（上書きであって追加ではない）
    assert_eq!(rebuilt.components.len(), 1);
}

/// 変更が無ければオーバーライドは 1 件も出ない（＝プレハブ本体の編集が素通しで伝播する）。
#[test]
fn no_diff_yields_no_overrides() {
    let scene = instance_same_as_prefab();
    let ov = extract_prefab_overrides(&scene, &prefab());
    assert!(ov.is_empty(), "変更が無ければ差分は空であること");
}

// ============================================================
//  2. コンポーネントの追加（プレハブに存在しない）
// ============================================================

/// プレハブに無い ScriptComponent の追加が抽出され、再適用で復元されることを検証する。
#[test]
fn added_component_is_extracted_and_restored() {
    let mut scene = instance_same_as_prefab();
    // シーン側でスクリプトを追加する
    let script = serde_json::from_str(r#"{
        "name": "PlayerMove",
        "component": { "type": "ScriptComponent", "data": { "type_name": "PlayerMove" } }
    }"#).expect("スクリプトスロット JSON のパースに失敗");
    scene.components.push(script);

    let ov = extract_prefab_overrides(&scene, &prefab());
    assert_eq!(ov.added_components.len(), 1, "Script の追加が抽出されること");
    assert_eq!(ov.modified_components.len(), 0);
    assert_eq!(ov.added_components[0].key.type_tag, "ScriptComponent");

    let mut rebuilt = prefab();
    assert_eq!(rebuilt.components.len(), 1, "プレハブ本体には Script は無い");
    merge_overrides_into(&mut rebuilt, &ov);
    assert_eq!(rebuilt.components.len(), 2, "再展開後も追加した Script が残ること");
    assert!(
        rebuilt.components.iter().any(|s| s.name == "PlayerMove"),
        "追加した Script スロットが名前ごと復元されること"
    );
}

/// 子ノードに追加したコンポーネントもパス付きで抽出・復元されることを検証する。
#[test]
fn added_component_on_child_node_is_restored() {
    let mut scene = instance_same_as_prefab();
    let cam = serde_json::from_str(r#"{
        "name": "Camera", "component": { "type": "CameraComponent", "data": {} }
    }"#).expect("カメラスロット JSON のパースに失敗");
    scene.children[0].components.push(cam);

    let ov = extract_prefab_overrides(&scene, &prefab());
    assert_eq!(ov.added_components.len(), 1);
    // 子 "Arm" を指すパスが 1 段記録される
    assert_eq!(ov.added_components[0].path.len(), 1);
    assert_eq!(ov.added_components[0].path[0].name, "Arm");

    let mut rebuilt = prefab();
    merge_overrides_into(&mut rebuilt, &ov);
    assert!(
        rebuilt.children[0].components.iter().any(|s| s.name == "Camera"),
        "子ノードへ追加したコンポーネントが復元されること"
    );
}

// ============================================================
//  3. 子アクタの追加
// ============================================================

/// シーン側で追加した子アクタ（Camera 付き）が抽出され、再適用で復元されることを検証する。
#[test]
fn added_child_actor_is_extracted_and_restored() {
    let mut scene = instance_same_as_prefab();
    let cam_child: ActorData = serde_json::from_str(r#"{
      "name": "PlayerCamera",
      "transform": { "position": [0,2,-5], "rotation": [0,0,0], "scale": [1,1,1] },
      "components": [
        { "name": "Camera", "component": { "type": "CameraComponent", "data": {} } }
      ],
      "children": []
    }"#).expect("追加子アクタ JSON のパースに失敗");
    scene.children.push(cam_child);

    let ov = extract_prefab_overrides(&scene, &prefab());
    assert_eq!(ov.added_children.len(), 1, "追加した子アクタが抽出されること");
    assert_eq!(ov.added_children[0].actor.name, "PlayerCamera");
    assert!(ov.added_children[0].parent_path.is_empty(), "ルート直下への追加");

    let mut rebuilt = prefab();
    assert_eq!(rebuilt.children.len(), 1, "プレハブ本体の子は Arm のみ");
    merge_overrides_into(&mut rebuilt, &ov);
    assert_eq!(rebuilt.children.len(), 2, "再展開後も追加した子が残ること");
    assert!(rebuilt.children.iter().any(|c| c.name == "PlayerCamera"));
}

/// 3 種類の差分が同時にあっても、すべて抽出・復元されることを検証する
/// （実際に報告されたバグと同じ組み合わせ）。
#[test]
fn all_three_override_kinds_survive_reinstantiation() {
    let mut scene = instance_same_as_prefab();
    // (1) 値の上書き
    if let ComponentData::ColliderComponent(ref mut c) = scene.components[0].component {
        c.freeze_rotation = [true, false, true];
    }
    // (2) コンポーネントの追加
    scene.components.push(serde_json::from_str(r#"{
        "name": "PlayerMove",
        "component": { "type": "ScriptComponent", "data": { "type_name": "PlayerMove" } }
    }"#).unwrap());
    // (3) 子アクタの追加
    scene.children.push(serde_json::from_str(r#"{
      "name": "PlayerCamera",
      "components": [ { "name": "Camera", "component": { "type": "CameraComponent", "data": {} } } ],
      "children": []
    }"#).unwrap());

    let ov = extract_prefab_overrides(&scene, &prefab());
    assert_eq!(ov.len(), 3, "3 種類の差分がすべて抽出されること");

    // `.scene` への保存 → 読み込みを経ても差分が失われないこと（serde 往復）
    let json  = serde_json::to_string(&ov).unwrap();
    let ov: PrefabOverrides = serde_json::from_str(&json).unwrap();

    let mut rebuilt = prefab();
    merge_overrides_into(&mut rebuilt, &ov);
    assert_eq!(freeze_rotation_of(&rebuilt, "Collider"), Some([true, false, true]));
    assert!(rebuilt.components.iter().any(|s| s.name == "PlayerMove"));
    assert!(rebuilt.children.iter().any(|c| c.name == "PlayerCamera"));
}

// ============================================================
//  4. 行列補正（delta）との相互作用
// ============================================================

/// インスタンスを移動しただけでは ModelComponent が差分扱いにならないことを検証する。
///
/// 再展開はプレハブ本体（原点基準）へ delta を適用してシーン位置へ合わせる。
/// 抽出側が同じ delta を考慮しないと、移動しただけのインスタンスで Model が
/// 常に上書き扱いになり、プレハブ本体のモデル変更が伝播しなくなってしまう。
#[test]
fn moved_instance_does_not_pin_model_component() {
    let mut scene = instance_same_as_prefab();
    // ルートを移動する（シーンでの配置）
    scene.transform.as_mut().unwrap().position = [10.0, 0.0, 5.0];
    // 再展開で実際に起きる変換をシーン側データにも反映する
    // （＝保存されている .scene の状態を再現する）
    let delta = compute_reinstantiate_delta(&scene, &prefab()).expect("移動しているので delta が出る");
    apply_delta_to_subtree(&mut scene, delta);

    let ov = extract_prefab_overrides(&scene, &prefab());
    assert!(ov.is_empty(), "移動しただけなら差分は出ないこと（Model が固定化されない）");
}

/// 移動した状態でコンポーネントを変更した場合、その変更だけが差分になることを検証する。
#[test]
fn moved_instance_extracts_only_real_changes() {
    let mut scene = instance_same_as_prefab();
    scene.transform.as_mut().unwrap().position = [10.0, 0.0, 5.0];
    let delta = compute_reinstantiate_delta(&scene, &prefab()).unwrap();
    apply_delta_to_subtree(&mut scene, delta);
    if let ComponentData::ColliderComponent(ref mut c) = scene.components[0].component {
        c.freeze_rotation = [true, true, true];
    }

    let ov = extract_prefab_overrides(&scene, &prefab());
    assert_eq!(ov.len(), 1, "実際に変更した Collider のみが差分になること");
    assert_eq!(ov.modified_components[0].key.type_tag, "ColliderComponent");
}

// ============================================================
//  5. 後方互換（旧 `.scene` が読めること）
// ============================================================

/// `prefab_overrides` フィールドを持たない旧形式のアクタデータが読めること、
/// および差分が空のときは書き出されない（旧シーンとバイト互換）ことを検証する。
#[test]
fn legacy_scene_without_prefab_overrides_loads() {
    // 旧 `.scene` のアクタ 1 件（prefab_overrides フィールドは存在しない）
    let legacy = r#"{
      "name": "Player",
      "transform": { "position": [1,2,3], "rotation": [0,0,0], "scale": [1,1,1] },
      "components": [],
      "children": [],
      "prefab_source": "assets://actors/BrainStem.actor"
    }"#;
    let parsed: ActorData = serde_json::from_str(legacy)
        .expect("prefab_overrides を持たない旧形式が読めること");
    assert!(parsed.prefab_overrides.is_empty(), "省略時は空の差分として扱われること");
    assert_eq!(parsed.prefab_source.as_deref(), Some("assets://actors/BrainStem.actor"));

    // 差分が空なら書き出さない（旧シーンとバイト互換を保つ）
    let json = serde_json::to_string(&parsed).unwrap();
    assert!(!json.contains("prefab_overrides"), "空の差分は書き出さないこと: {json}");
}

/// 差分がある場合は `prefab_overrides` が書き出され、読み戻せることを検証する。
#[test]
fn prefab_overrides_roundtrip_through_scene_json() {
    let mut scene = instance_same_as_prefab();
    if let ComponentData::ColliderComponent(ref mut c) = scene.components[0].component {
        c.freeze_rotation = [true, true, true];
    }
    scene.prefab_overrides = extract_prefab_overrides(&scene, &prefab());

    let json = serde_json::to_string(&scene).unwrap();
    assert!(json.contains("prefab_overrides"), "差分があれば書き出されること");

    let back: ActorData = serde_json::from_str(&json).unwrap();
    assert_eq!(back.prefab_overrides.len(), 1);

    // 読み戻した差分で再展開後の内容を復元できる
    let mut rebuilt = prefab();
    merge_overrides_into(&mut rebuilt, &back.prefab_overrides);
    assert_eq!(freeze_rotation_of(&rebuilt, "Collider"), Some([true, true, true]));
}

// ============================================================
//  6. プレハブ本体が変わったケース（伝播とオーバーライドの両立）
// ============================================================

/// プレハブ本体側の変更は反映され、オーバーライドされた部分だけシーンの値が勝つ。
#[test]
fn prefab_edits_propagate_except_overridden_parts() {
    // シーン側: Collider を変更し、Script を追加した状態
    let mut scene = instance_same_as_prefab();
    if let ComponentData::ColliderComponent(ref mut c) = scene.components[0].component {
        c.freeze_rotation = [true, true, true];
    }
    scene.components.push(serde_json::from_str(r#"{
        "name": "PlayerMove",
        "component": { "type": "ScriptComponent", "data": { "type_name": "PlayerMove" } }
    }"#).unwrap());
    let ov = extract_prefab_overrides(&scene, &prefab());

    // プレハブ本体側で「子 Arm を増やし、Light を追加した」とする
    let mut edited_prefab = prefab();
    edited_prefab.children.push(serde_json::from_str(r#"{
      "name": "Head", "components": [], "children": []
    }"#).unwrap());
    edited_prefab.components.push(serde_json::from_str(r#"{
        "name": "Light", "component": { "type": "LightComponent", "data": {} }
    }"#).unwrap());

    // 再展開＋オーバーライド再適用
    let mut rebuilt = edited_prefab;
    merge_overrides_into(&mut rebuilt, &ov);

    // プレハブ本体の追加は反映される
    assert!(rebuilt.children.iter().any(|c| c.name == "Head"), "本体で増えた子が反映されること");
    assert!(rebuilt.components.iter().any(|s| s.name == "Light"), "本体で増えたコンポーネントが反映されること");
    // シーン側の差分も生きている
    assert_eq!(freeze_rotation_of(&rebuilt, "Collider"), Some([true, true, true]));
    assert!(rebuilt.components.iter().any(|s| s.name == "PlayerMove"));
}

