// ============================================================
//  water/bindings.rs — 水面シェーダパラメータの `@ref` バインド適用（Phase W8.3）
//
//  ## 役割（単一責任）
//  `WaterVolumeComponent::bindings`（パラメータ名 → `"アクタ名|スロット名|変数名"`）を
//  シーンから毎フレーム解決し、**解決できたものだけ**をパラメータ値マップへ差し込む。
//  汎用の解決機構は `engine::binding` が持ち、ここはその「水向けの接続部」である:
//    ・アセットの `@ref` 宣言を引いて、要求する型（f32 / vec3）を決める
//    ・解決結果を `shader_params` と同じ形（vec4 マップ）に落とす
//
//  ## シーン保存値は書き換えない
//  差し込むのは **`ResolvedWaterVolume`（毎フレーム作られる中間表現）の複製**だけで、
//  `WaterVolumeComponent` 側の保存値には一切触れない。
//  したがってバインドを外せば、その瞬間から元の保存値へ戻る。
//
//  ## 解決失敗は「何もしない」
//  アクタ／スロット／変数が消えた、型が変わった、スクリプトが `[Bindable]` を外した ──
//  いずれの場合も差し込まないだけで、値は保存値／アセット既定値のまま描かれる。
//  ユーザーへの通知はインスペクタの ⚠ が担当する（毎フレーム走る経路なのでログは出さない）。
//
//  ## `@ref` の付いていないパラメータは対象外
//  バインドを設定した後にアセットから `@ref` を外した場合、そのバインドは
//  **孤児として無視**される（`shader_params` の孤児と同じ扱い）。
//  アセットに `@ref` を書き戻せばバインドも復活する。
// ============================================================

use std::collections::BTreeMap;

use crate::engine::binding::catalog::{BindableValueType, BINDING_VALUE_COMPONENTS};
use crate::engine::binding::resolve::{parse_binding, resolve_binding};
use crate::engine::core::renderer::water::shade_params::{
    WaterShadeParamDecl, WaterShadeParamKind,
};
use crate::engine::core::renderer::water::shading_asset;
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;

/// 水面パラメータの UI 種別から、バインドが要求する値の型を決める。
///
/// **色（`vec3<f32>`）は `Vec3`、それ以外（`f32`）は `F32`** という 1 対 1 対応。
/// WGSL 側の型がこの 2 種類しかないので分岐もここだけで閉じる。
fn required_value_type(kind: WaterShadeParamKind) -> BindableValueType {
    match kind {
        WaterShadeParamKind::Color => BindableValueType::Vec3,
        _                          => BindableValueType::F32,
    }
}

/// バインドを解決して「パラメータ名 → 実値」のマップを作る。
///
/// - `decls`    : アセットの宣言（`@ref` の有無と型の正典）
/// - `bindings` : 保存されているバインド（パラメータ名 → バインド先文字列）
///
/// 戻り値に入るのは**解決できたバインドだけ**である。
/// 空マップ＝「1 本も解決できなかった」＝すべて保存値／既定値へフォールバック。
pub fn resolve_bindings(
    actors:     &[Actor],
    world:      &World,
    world_line: u32,
    decls:      &[WaterShadeParamDecl],
    bindings:   &BTreeMap<String, String>,
) -> BTreeMap<String, [f32; BINDING_VALUE_COMPONENTS]> {
    let mut out = BTreeMap::new();
    for (param_name, binding_text) in bindings {
        // ① アセット側が `@ref` を宣言しているパラメータだけを対象にする。
        let Some(decl) = decls.iter().find(|d| &d.name == param_name) else { continue };
        if !decl.bindable { continue; }
        // ② バインド先文字列を割る（壊れていれば解決失敗）。
        let Some(target) = parse_binding(binding_text) else { continue };
        // ③ シーンから実値を読む（型は厳密一致）。
        let want = required_value_type(decl.kind);
        if let Some(v) = resolve_binding(actors, world, world_line, &target, want) {
            out.insert(param_name.clone(), v);
        }
    }
    out
}

/// アセットの宣言をキャッシュから引いたうえでバインドを解決する。
///
/// 収集経路（毎フレーム）から使う入口。アセットパスが空、またはバインドが
/// 1 本も無いときは**ファイルにも触らず**空マップを返す（無駄な mtime 参照を避ける）。
pub fn resolve_bindings_for_asset(
    actors:      &[Actor],
    world:       &World,
    world_line:  u32,
    asset_path:  &str,
    bindings:    &BTreeMap<String, String>,
) -> BTreeMap<String, [f32; BINDING_VALUE_COMPONENTS]> {
    if bindings.is_empty() || asset_path.trim().is_empty() { return BTreeMap::new(); }
    shading_asset::with_cached_declarations(asset_path, |decls| {
        resolve_bindings(actors, world, world_line, decls, bindings)
    })
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::light_component::LightComponent;
    use crate::engine::components::{ComponentKind, Transform};
    use crate::engine::core::renderer::water::shade_params::parse_params;

    /// 光源 1 個を持つテストシーンを組む。
    fn scene_with_light(intensity: f32, color: [f32; 3]) -> (World, Vec<Actor>) {
        let mut world = World::new();
        let entity = world.spawn();
        world.insert(entity, Transform::default());
        let mut actor = Actor::new(entity, "Sun");
        let slot_entity = world.spawn();
        let mut l = LightComponent::default();
        l.intensity = intensity;
        l.color     = color;
        world.insert(slot_entity, l);
        actor.add_slot_typed::<LightComponent>("MainLight", ComponentKind::Light, slot_entity);
        (world, vec![actor])
    }

    /// `@ref` 付きスカラーがライトの強度で解決されること。
    #[test]
    fn resolves_ref_scalar_from_light() {
        let (world, actors) = scene_with_light(3.25, [1.0, 1.0, 1.0]);
        let set = parse_params("@ref override glow: f32 = 1.0;\n");
        assert!(set.warnings.is_empty(), "{:?}", set.warnings);
        assert!(set.params[0].bindable, "@ref が効いていること");

        let bindings = BTreeMap::from([
            ("glow".to_string(), "Sun|MainLight|intensity".to_string())]);
        let live = resolve_bindings(&actors, &world, 0, &set.params, &bindings);
        assert_eq!(live.get("glow"), Some(&[3.25, 0.0, 0.0, 0.0]));
    }

    /// `@ref` 付き色がライトの色で解決されること（vec3 の厳密一致）。
    #[test]
    fn resolves_ref_color_from_light() {
        let (world, actors) = scene_with_light(1.0, [0.2, 0.4, 0.6]);
        let set = parse_params("@ref override tint: vec3<f32> = vec3(1.0);\n");
        let bindings = BTreeMap::from([
            ("tint".to_string(), "Sun|MainLight|color".to_string())]);
        let live = resolve_bindings(&actors, &world, 0, &set.params, &bindings);
        assert_eq!(live.get("tint"), Some(&[0.2, 0.4, 0.6, 0.0]));
    }

    /// 型が食い違うバインドは解決されないこと（色のパラメータへ強度を繋いだ場合）。
    #[test]
    fn type_mismatch_is_not_resolved() {
        let (world, actors) = scene_with_light(3.0, [1.0, 1.0, 1.0]);
        let set = parse_params("@ref override tint: vec3<f32> = vec3(1.0);\n");
        let bindings = BTreeMap::from([
            ("tint".to_string(), "Sun|MainLight|intensity".to_string())]);
        assert!(resolve_bindings(&actors, &world, 0, &set.params, &bindings).is_empty());
    }

    /// `@ref` の無いパラメータへのバインドは無視されること（孤児バインド）。
    #[test]
    fn binding_on_non_ref_param_is_ignored() {
        let (world, actors) = scene_with_light(3.0, [1.0, 1.0, 1.0]);
        let set = parse_params("override glow: f32 = 1.0;\n");
        assert!(!set.params[0].bindable);
        let bindings = BTreeMap::from([
            ("glow".to_string(), "Sun|MainLight|intensity".to_string())]);
        assert!(resolve_bindings(&actors, &world, 0, &set.params, &bindings).is_empty());
    }

    /// 参照先が消えたバインドは解決されないこと（フォールバックの前提）。
    #[test]
    fn broken_binding_is_not_resolved() {
        let (world, actors) = scene_with_light(3.0, [1.0, 1.0, 1.0]);
        let set = parse_params("@ref override glow: f32 = 1.0;\n");
        for text in ["Gone|MainLight|intensity", "Sun|Gone|intensity", "Sun|MainLight|gone", "壊れた"] {
            let bindings = BTreeMap::from([("glow".to_string(), text.to_string())]);
            assert!(resolve_bindings(&actors, &world, 0, &set.params, &bindings).is_empty(),
                "{text} が解決できてはならない");
        }
    }

    /// バインドが空・アセットが空ならファイルへ触れずに空マップを返すこと。
    #[test]
    fn empty_inputs_short_circuit() {
        let (world, actors) = scene_with_light(3.0, [1.0, 1.0, 1.0]);
        assert!(resolve_bindings_for_asset(&actors, &world, 0, "", &BTreeMap::new()).is_empty());
        assert!(resolve_bindings_for_asset(
            &actors, &world, 0, "assets://nope.wgsl", &BTreeMap::new()).is_empty());
    }

    /// UI 種別と要求型の対応（色だけが vec3）。
    #[test]
    fn required_value_type_maps_color_to_vec3() {
        assert_eq!(required_value_type(WaterShadeParamKind::Color), BindableValueType::Vec3);
        assert_eq!(required_value_type(WaterShadeParamKind::Float), BindableValueType::F32);
        assert_eq!(
            required_value_type(WaterShadeParamKind::Range { min: 0.0, max: 1.0 }),
            BindableValueType::F32);
    }
}
