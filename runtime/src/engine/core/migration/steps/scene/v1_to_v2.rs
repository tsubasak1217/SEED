// ============================================================
//  steps/scene/v1_to_v2.rs — `.scene` v1 → v2
//
//  【内容】旧 enum 表記の正規化。
//  いま `#[serde(alias = …)]` で吸収している旧表記を、ファイルの側で現行表記へ寄せる。
//  対応表と実体は `steps/common.rs` にある（`.actor` の同じ段と共用）。
//
//  【この段が変えないもの】
//  トップレベルのメタデータ（name / settings / shading_* / terrain_dir / debug_camera）と
//  アクタ木の構造。対象コンポーネントを持たないシーンは版の欄だけが 2 になる。
// ============================================================

use serde_json::Value;

use crate::engine::core::migration::json_walk;
use crate::engine::core::migration::steps::common;

/// `.scene` を v1 から v2 へ持ち上げる。
///
/// シーン直下の全ルートアクタ（とその子孫）に対して旧 enum 表記の正規化を適用する。
/// 構造が想定と違う（`actors` が無い・配列でない）場合も、正規化すべき対象が
/// 無いだけなので成功として扱う（本体のデシリアライズが別途エラーにする）。
pub fn migrate(value: &mut Value) -> Result<(), String> {
    json_walk::for_each_actor_in_scene(value, &mut |actor| {
        common::normalize_legacy_enum_names_in_actor(actor);
    });
    Ok(())
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// アクタ木の奥にある旧表記まで直ること。
    #[test]
    fn normalizes_legacy_enum_names_across_the_whole_scene() {
        let mut scene = json!({
            "name": "S",
            "actors": [
                { "name": "a", "components": [
                    { "name": "FX", "component": { "type": "ParticleEmitterComponent",
                      "data": { "blend": "additive" } } }
                ], "children": [
                    { "name": "b", "components": [
                        { "name": "UI", "component": { "type": "CanvasComponent",
                          "data": { "gravity_mode": "screen_down" } } }
                    ], "children": [] }
                ] }
            ]
        });
        migrate(&mut scene).unwrap();
        assert_eq!(scene["actors"][0]["components"][0]["component"]["data"]["blend"], json!("add"));
        assert_eq!(
            scene["actors"][0]["children"][0]["components"][0]["component"]["data"]["gravity_mode"],
            json!("world_down")
        );
    }

    /// `actors` が無いシーンでも失敗しないこと（版の欄だけ上がる）。
    #[test]
    fn succeeds_on_scene_without_actors_key() {
        let mut scene = json!({ "name": "empty" });
        assert!(migrate(&mut scene).is_ok());
        assert_eq!(scene, json!({ "name": "empty" }));
    }

    /// 版の欄には触らないこと（版を進めるのは runner の責務）。
    #[test]
    fn does_not_touch_the_version_field() {
        let mut scene = json!({ "format_version": 1, "name": "S", "actors": [] });
        migrate(&mut scene).unwrap();
        assert_eq!(scene["format_version"], json!(1));
    }
}
