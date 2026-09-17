// ============================================================
//  steps/common.rs — 複数形式で共有する変換の部品
//
//  【なぜ共有するのか】
//  `.scene` と `.actor` は同じ `ActorData` の木を持つため、「アクタ木の中の
//  コンポーネントを直す」変換は 2 形式で中身がまったく同じになる。
//  両方に書き写すと片方だけ直す事故が起きるので、実体はここに 1 本だけ置き、
//  各形式の `vN_to_vM.rs` は「入口の形（シーンの actors 配列か、アクタ 1 本か）」
//  の違いだけを吸収する。
// ============================================================

use serde_json::Value;

use crate::engine::core::migration::json_walk;

// ── v1 → v2: 旧 enum 表記の正規化 ────────────────────────────────
//
//  対象は現在 `#[serde(alias = …)]` で吸収している旧表記。
//  読み込みは alias で通っているが、ファイルの中身が旧表記のままだと
//  「alias をいつ消せるか」が判断できない。変換でファイル側を現行表記へ寄せる。
//
//  | コンポーネント                | 欄             | 旧表記                 | 現行表記      |
//  |------------------------------|----------------|------------------------|---------------|
//  | ParticleEmitterComponent     | `blend`        | `alpha` / `additive`   | `normal`/`add`|
//  | ParticleEmitterComponent     | `shape`        | `point`                | `pixel`       |
//  | CanvasComponent              | `gravity_mode` | `screen_down`          | `world_down`  |
//
//  対応する実装:
//  - `components/particle_emitter_component.rs` の `ParticleBlend` / `ParticleShape`
//  - `components/canvas_component.rs` の `GravityMode`

/// パーティクルエミッタのコンポーネント種別名（`ComponentData` の variant 名）。
const PARTICLE_EMITTER_TYPE: &str = "ParticleEmitterComponent";
/// キャンバスのコンポーネント種別名。
const CANVAS_TYPE: &str = "CanvasComponent";

/// パーティクルの合成モードの欄名。
const KEY_BLEND: &str = "blend";
/// パーティクルの粒形状の欄名。
const KEY_SHAPE: &str = "shape";
/// キャンバス 2D 物理の重力モードの欄名。
const KEY_GRAVITY_MODE: &str = "gravity_mode";

/// `ParticleBlend` の旧表記 → 現行表記。
const BLEND_RENAMES: &[(&str, &str)] = &[("alpha", "normal"), ("additive", "add")];
/// `ParticleShape` の旧表記 → 現行表記（`Model` variant はオブジェクトなので対象外）。
const SHAPE_RENAMES: &[(&str, &str)] = &[("point", "pixel")];
/// `GravityMode` の旧表記 → 現行表記。
const GRAVITY_MODE_RENAMES: &[(&str, &str)] = &[("screen_down", "world_down")];

/// アクタ **1 個**（子孫は見ない）の旧 enum 表記を現行表記へ正規化する。
///
/// 戻り値は書き換えた欄の数（テスト・デバッグ用。0 でも成功）。
/// 対象コンポーネントを持たないアクタでは何も起きない。
pub fn normalize_legacy_enum_names_in_actor(actor: &mut Value) -> usize {
    let mut changed = 0usize;

    json_walk::for_each_component_data_in_actor(actor, PARTICLE_EMITTER_TYPE, &mut |data| {
        changed += normalize_particle_emitter_data(data);
    });
    json_walk::for_each_component_data_in_actor(actor, CANVAS_TYPE, &mut |data| {
        changed += normalize_canvas_data(data);
    });

    changed
}

/// 1 本のアクタ木（アクタ自身と全子孫）の旧 enum 表記を現行表記へ正規化する。
///
/// 戻り値は書き換えた欄の数。`.actor` の変換段が使う
/// （`.scene` 側はシーン直下の走査と組み合わせるので `…_in_actor` を直接使う）。
pub fn normalize_legacy_enum_names_in_actor_tree(actor: &mut Value) -> usize {
    let mut changed = 0usize;
    json_walk::for_each_actor_in_tree(actor, &mut |a| {
        changed += normalize_legacy_enum_names_in_actor(a);
    });
    changed
}

/// パーティクルエミッタのデータ部 1 個を正規化する。戻り値は書き換えた欄の数。
fn normalize_particle_emitter_data(data: &mut Value) -> usize {
    let mut changed = 0usize;
    if json_walk::rename_string_field(data, KEY_BLEND, BLEND_RENAMES) {
        changed += 1;
    }
    if json_walk::rename_string_field(data, KEY_SHAPE, SHAPE_RENAMES) {
        changed += 1;
    }
    changed
}

/// キャンバスのデータ部 1 個を正規化する。戻り値は書き換えた欄の数。
fn normalize_canvas_data(data: &mut Value) -> usize {
    if json_walk::rename_string_field(data, KEY_GRAVITY_MODE, GRAVITY_MODE_RENAMES) {
        1
    } else {
        0
    }
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// 旧表記が現行表記へ直り、既に現行のものは変わらないこと。
    #[test]
    fn renames_legacy_enum_names_and_leaves_current_ones() {
        let mut actor = json!({
            "name": "root",
            "components": [
                { "name": "FX", "component": { "type": "ParticleEmitterComponent",
                  "data": { "blend": "additive", "shape": "point" } } },
                { "name": "UI", "component": { "type": "CanvasComponent",
                  "data": { "gravity_mode": "screen_down" } } }
            ],
            "children": [
                { "name": "child", "components": [
                    { "name": "FX2", "component": { "type": "ParticleEmitterComponent",
                      "data": { "blend": "alpha", "shape": "sphere" } } }
                ], "children": [] }
            ]
        });

        // blend×2 + shape×1 + gravity_mode×1 = 4 件
        assert_eq!(normalize_legacy_enum_names_in_actor_tree(&mut actor), 4);
        assert_eq!(actor["components"][0]["component"]["data"]["blend"], json!("add"));
        assert_eq!(actor["components"][0]["component"]["data"]["shape"], json!("pixel"));
        assert_eq!(
            actor["components"][1]["component"]["data"]["gravity_mode"],
            json!("world_down")
        );
        assert_eq!(
            actor["children"][0]["components"][0]["component"]["data"]["blend"],
            json!("normal")
        );
        // 現行表記のままの値は触らない
        assert_eq!(
            actor["children"][0]["components"][0]["component"]["data"]["shape"],
            json!("sphere")
        );
    }

    /// 対象コンポーネントを持たない木は 1 欄も変わらないこと（冪等性の下地）。
    #[test]
    fn leaves_unrelated_actors_untouched() {
        let original = json!({
            "name": "root",
            "components": [
                { "name": "M", "component": { "type": "ModelComponent",
                  "data": { "model_path": "a.glb", "blend": "additive" } } }
            ],
            "children": []
        });
        let mut actor = original.clone();
        assert_eq!(normalize_legacy_enum_names_in_actor_tree(&mut actor), 0);
        assert_eq!(actor, original);
    }

    /// 2 回流しても結果が変わらないこと（冪等）。
    #[test]
    fn is_idempotent() {
        let mut actor = json!({
            "name": "root",
            "components": [
                { "name": "FX", "component": { "type": "ParticleEmitterComponent",
                  "data": { "blend": "alpha" } } }
            ],
            "children": []
        });
        normalize_legacy_enum_names_in_actor_tree(&mut actor);
        let once = actor.clone();
        assert_eq!(normalize_legacy_enum_names_in_actor_tree(&mut actor), 0);
        assert_eq!(actor, once);
    }
}
