// ============================================================
//  steps/actor/v1_to_v2.rs — `.actor` / `.actor2d` v1 → v2
//
//  【内容】旧 enum 表記の正規化。`.scene` の同じ段と中身は共通で、
//  違いは入口の形だけ（こちらはトップレベルがアクタ 1 本）。
//  対応表と実体は `steps/common.rs` にある。
// ============================================================

use serde_json::Value;

use crate::engine::core::migration::steps::common;

/// `.actor` / `.actor2d` を v1 から v2 へ持ち上げる。
///
/// トップレベルのアクタとその全子孫に旧 enum 表記の正規化を適用する。
/// 対象コンポーネントを持たないアクタは版の欄だけが 2 になる。
pub fn migrate(value: &mut Value) -> Result<(), String> {
    common::normalize_legacy_enum_names_in_actor_tree(value);
    Ok(())
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// トップレベルのアクタと子孫の両方が直ること。
    #[test]
    fn normalizes_root_and_descendants() {
        let mut actor = json!({
            "name": "Fish",
            "components": [
                { "name": "FX", "component": { "type": "ParticleEmitterComponent",
                  "data": { "shape": "point" } } }
            ],
            "children": [
                { "name": "Bubble", "components": [
                    { "name": "FX", "component": { "type": "ParticleEmitterComponent",
                      "data": { "blend": "alpha" } } }
                ], "children": [] }
            ]
        });
        migrate(&mut actor).unwrap();
        assert_eq!(actor["components"][0]["component"]["data"]["shape"], json!("pixel"));
        assert_eq!(
            actor["children"][0]["components"][0]["component"]["data"]["blend"],
            json!("normal")
        );
    }

    /// コンポーネントを持たないアクタでも失敗せず、中身も変わらないこと。
    #[test]
    fn succeeds_on_actor_without_components() {
        let original = json!({ "name": "Empty", "components": [], "children": [] });
        let mut actor = original.clone();
        assert!(migrate(&mut actor).is_ok());
        assert_eq!(actor, original);
    }
}
