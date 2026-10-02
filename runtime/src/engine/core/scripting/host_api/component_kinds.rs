// ============================================================
//  scripting/host_api/component_kinds.rs — スクリプトの AddComponent<T>() で足せるコンポーネントの表（動的ノード API）
//
//  【1 か所の表】C# のハンドル型の ComponentKindName（host_api.rs の KIND_* / canvas_layout_api の KIND_*）→ ComponentKind。
//  足すときの既定値はエディタの「コンポーネント追加」（app/component_ops.rs::handle_add_component_to_actor）と同じ:
//    - 純データの UI 部品（CanvasStack・Wrap・Grid・LayoutItem・SafeArea・Gesture・Scroll）は canvas_layout_slots の
//      default_data → insert_from_data（エディタと同じ関数）
//    - Text は TextComponent::new_for_editor_add（エディタと同じ）
//    - それ以外は Xxx::default()（エディタと同じ）
//  World へその場で入れる（スロットの目録への登録はフレーム末尾。host_api/nodes.rs・node_pending.rs）。
//
//  【載せていない種別】GPU の資源・ファイルの読み込みが要るもの（Model・Skybox）、エディタ専用の設定が要るもの
//  （Camera・InputMap・Canvas・物理・水・地形・ControlPoint）。足すときは「スクリプトの API の表」（docs/scripting_api.md §7
//  の AddComponent の表）とこの表の両方に 1 行ずつ足す（片方だけだと C# は呼べるのに失敗する）。
// ============================================================

use crate::engine::components::{
    AnimatorComponent, AudioComponent, AudioDictionaryComponent, CanvasClipComponent, ComponentKind,
    LineRendererComponent, ParticleEmitterComponent, SkinnedSpriteComponent, SpriteComponent, TextComponent,
};
use crate::engine::core::scripting::canvas_layout_api::{
    KIND_CANVAS_GESTURE, KIND_CANVAS_GRID, KIND_CANVAS_LAYOUT_ITEM, KIND_CANVAS_SAFE_AREA, KIND_CANVAS_SCROLL,
    KIND_CANVAS_STACK, KIND_CANVAS_WRAP,
};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::actor::{canvas_layout_slots, ComponentSlot};

use super::{
    KIND_ANIMATOR, KIND_AUDIO, KIND_AUDIO_DICTIONARY, KIND_CANVAS_CLIP, KIND_LINE_RENDERER, KIND_PARTICLE,
    KIND_SKINNED_SPRITE, KIND_SPRITE, KIND_TEXT,
};

/// スクリプトから足せるコンポーネントの表（C# の ComponentKindName ⇔ ComponentKind）。
pub(crate) const SCRIPT_ADDABLE_KINDS: [(&str, ComponentKind); 16] = [
    (KIND_SPRITE, ComponentKind::Sprite),
    (KIND_SKINNED_SPRITE, ComponentKind::SkinnedSprite),
    (KIND_TEXT, ComponentKind::Text),
    (KIND_CANVAS_CLIP, ComponentKind::CanvasClip),
    (KIND_CANVAS_STACK, ComponentKind::CanvasStack),
    (KIND_CANVAS_WRAP, ComponentKind::CanvasWrap),
    (KIND_CANVAS_GRID, ComponentKind::CanvasGrid),
    (KIND_CANVAS_LAYOUT_ITEM, ComponentKind::CanvasLayoutItem),
    (KIND_CANVAS_SAFE_AREA, ComponentKind::CanvasSafeArea),
    (KIND_CANVAS_GESTURE, ComponentKind::CanvasGesture),
    (KIND_CANVAS_SCROLL, ComponentKind::CanvasScroll),
    (KIND_AUDIO, ComponentKind::Audio),
    (KIND_AUDIO_DICTIONARY, ComponentKind::AudioDictionary),
    (KIND_ANIMATOR, ComponentKind::Animator),
    (KIND_PARTICLE, ComponentKind::ParticleEmitter),
    (KIND_LINE_RENDERER, ComponentKind::LineRenderer),
];

/// C# の ComponentKindName から、スクリプトで足せる種別を引く（載っていなければ None）。
pub(crate) fn addable_kind_of(script_name: &str) -> Option<ComponentKind> {
    SCRIPT_ADDABLE_KINDS.iter().find(|(name, _)| *name == script_name).map(|(_, kind)| *kind)
}

/// 種別の既定値のコンポーネントをスロット専用のエンティティへ入れ、スロットの目録の 1 行を作る
/// （エディタの「コンポーネント追加」と同じ既定値。表に無い種別なら何もせず None）。
///
/// # 引数
/// * `world`       - コンポーネントの置き場
/// * `slot_entity` - スロット専用のエンティティ（呼び出し側が spawn 済み。None のときは呼び出し側が despawn する）
/// * `kind`        - 種別（`addable_kind_of` の結果）
/// * `name`        - スロット名
pub(crate) fn insert_default_slot(
    world: &mut World,
    slot_entity: Entity,
    kind: ComponentKind,
    name: &str,
) -> Option<ComponentSlot> {
    // 純データの UI 部品はエディタと同じ表（既定値・入れ方）を通す
    if canvas_layout_slots::is_layout_kind(kind) {
        let data = canvas_layout_slots::default_data(kind)?;
        return canvas_layout_slots::insert_from_data(world, slot_entity, name, &data);
    }
    let slot = match kind {
        ComponentKind::Sprite => {
            world.insert(slot_entity, SpriteComponent::default());
            ComponentSlot::new::<SpriteComponent>(name, kind, slot_entity)
        }
        ComponentKind::SkinnedSprite => {
            world.insert(slot_entity, SkinnedSpriteComponent::default());
            ComponentSlot::new::<SkinnedSpriteComponent>(name, kind, slot_entity)
        }
        ComponentKind::Text => {
            world.insert(slot_entity, TextComponent::new_for_editor_add());
            ComponentSlot::new::<TextComponent>(name, kind, slot_entity)
        }
        ComponentKind::CanvasClip => {
            world.insert(slot_entity, CanvasClipComponent::default());
            ComponentSlot::new::<CanvasClipComponent>(name, kind, slot_entity)
        }
        ComponentKind::Audio => {
            world.insert(slot_entity, AudioComponent::default());
            ComponentSlot::new::<AudioComponent>(name, kind, slot_entity)
        }
        ComponentKind::AudioDictionary => {
            world.insert(slot_entity, AudioDictionaryComponent::default());
            ComponentSlot::new::<AudioDictionaryComponent>(name, kind, slot_entity)
        }
        ComponentKind::Animator => {
            world.insert(slot_entity, AnimatorComponent::default());
            ComponentSlot::new::<AnimatorComponent>(name, kind, slot_entity)
        }
        ComponentKind::ParticleEmitter => {
            world.insert(slot_entity, ParticleEmitterComponent::default());
            ComponentSlot::new::<ParticleEmitterComponent>(name, kind, slot_entity)
        }
        ComponentKind::LineRenderer => {
            world.insert(slot_entity, LineRendererComponent::default());
            ComponentSlot::new::<LineRendererComponent>(name, kind, slot_entity)
        }
        _ => return None,
    };
    Some(slot)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 表の名前に重複が無く、どの行も既定値で入れられること（エディタの既定値の経路が通る）。
    #[test]
    fn every_addable_kind_inserts_a_slot_with_the_same_kind() {
        let mut names: Vec<&str> = SCRIPT_ADDABLE_KINDS.iter().map(|(n, _)| *n).collect();
        names.sort_unstable();
        names.dedup();
        assert_eq!(names.len(), SCRIPT_ADDABLE_KINDS.len(), "名前が重複している");

        let mut world = World::new();
        for (name, kind) in SCRIPT_ADDABLE_KINDS {
            assert_eq!(addable_kind_of(name), Some(kind));
            let e = world.spawn();
            let slot = insert_default_slot(&mut world, e, kind, name).unwrap_or_else(|| panic!("{name} を入れられない"));
            assert_eq!(slot.kind, kind, "{name} のスロットの種別");
            assert_eq!(slot.entity, e);
            assert_eq!(slot.name, name);
            assert!(super::super::entity_is_kind(&world, e, name), "{name} を GetComponent の判定で引ける");
        }
    }

    /// 表に無い名前（Transform・Model・Camera・空）は足せない。
    #[test]
    fn unknown_or_root_kinds_are_not_addable() {
        for name in ["Transform", "CanvasTransform", "Model", "Camera", "", "sprite"] {
            assert_eq!(addable_kind_of(name), None, "{name}");
        }
    }
}
