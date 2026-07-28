// ============================================================
//  interaction/collect.rs — シーンからのインタラクションソース収集（Phase I1）
//
//  Actor ツリーを DFS で走査し、有効な `InteractionSourceComponent` を
//  ワールド空間の `ResolvedInteractionSource` へ解決して集める。
//
//  スキップ規則は他の収集処理（water/collect.rs の collect_water_volumes /
//  app/actor_utils.rs の collect_mcs_in_world_line）と完全に揃える:
//    ・world_line が一致しないルートは対象外
//    ・active=false のアクターはサブツリーごとスキップ（祖先の非アクティブも伝播）
//    ・enabled=false のスロットはスキップ
//    ・コンポーネント側の enabled=false もスキップ
//
//  DFS 連番は「収集対象外のアクタでも必ず加算する」。これは
//  ソースキー（前フレーム位置の突き合わせ）を、アクタの出現順ではなく
//  **シーン内の位置**で安定させるため（他の収集処理の採番規則とも一致する）。
// ============================================================

use crate::engine::components::interaction_source_component::InteractionSourceComponent;
use crate::engine::components::{ComponentKind, Transform};
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;

use super::resolved::{source_key, ResolvedInteractionSource};

/// Transform を持たないアクター（フォルダノード等）の既定位置。
const FALLBACK_ACTOR_POSITION: [f32; 3] = [0.0, 0.0, 0.0];

/// シーンの全アクタを再帰走査し、有効なインタラクションソースをワールド解決して集める。
///
/// `world_line` はアクター編集タブとの分離用（0 = 通常シーン）。
pub fn collect_interaction_sources(
    actors:     &[Actor],
    world:      &World,
    world_line: u32,
) -> Vec<ResolvedInteractionSource> {
    let mut out = Vec::new();
    let mut dfs_counter = 0u32;
    for root in actors.iter().filter(|a| a.world_line == world_line) {
        collect_in_actor(root, world, &mut out, &mut dfs_counter, true);
    }
    out
}

/// `collect_interaction_sources` の再帰実装。
///
/// `parent_active` は祖先のアクティブ状態。自身または祖先が active=false の
/// アクターはソースを収集しない（ただし DFS 連番は必ず進める）。
fn collect_in_actor(
    actor:         &Actor,
    world:         &World,
    out:           &mut Vec<ResolvedInteractionSource>,
    dfs_counter:   &mut u32,
    parent_active: bool,
) {
    let dfs_id = *dfs_counter;
    *dfs_counter += 1;
    let active = parent_active && actor.active;

    if active {
        // ソースのワールド位置（Transform はワールド空間）。
        let pos = world.get::<Transform>(actor.entity)
            .map(|t| t.position)
            .unwrap_or(FALLBACK_ACTOR_POSITION);

        for (slot_index, slot) in actor.slots().iter().enumerate() {
            // 無効スロットは対象外（スロットの enabled はエディタのチェックボックス）。
            if slot.kind != ComponentKind::InteractionSource || !slot.enabled { continue; }
            let Some(src) = world.get::<InteractionSourceComponent>(slot.entity) else { continue };
            // コンポーネント側の有効フラグ（ゲームロジックからの一時停止）。
            if !src.enabled { continue; }
            // 半径 0 以下・強さ 0 以下は場に何も書けないので、GPU へ送る前に落とす
            // （ゼロ除算の回避と、無意味なソースでディスパッチ内ループを消費しないため）。
            if src.radius <= 0.0 || src.strength <= 0.0 { continue; }
            out.push(ResolvedInteractionSource {
                key:       source_key(dfs_id, slot_index as u32),
                world_pos: pos,
                radius:    src.radius,
                strength:  src.strength,
            });
        }
    }

    // 子孫へは**常に**再帰する（非アクティブでも DFS 連番は進める）。
    for child in actor.children() {
        collect_in_actor(child, world, out, dfs_counter, active);
    }
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用シーン（World ＋ ルートアクタ列）。
    struct TestScene {
        world:  World,
        actors: Vec<Actor>,
    }

    impl TestScene {
        fn new() -> Self {
            Self { world: World::new(), actors: Vec::new() }
        }

        /// 指定位置に InteractionSource スロットを 1 つ持つアクターを作って返す。
        fn make_source_actor(&mut self, pos: [f32; 3]) -> Actor {
            let entity = self.world.spawn();
            let mut tf = Transform::default();
            tf.position = pos;
            self.world.insert(entity, tf);

            let mut actor = Actor::new(entity, "source");
            let slot_entity = self.world.spawn();
            self.world.insert(slot_entity, InteractionSourceComponent::default());
            actor.add_slot_typed::<InteractionSourceComponent>(
                "InteractionSourceComponent", ComponentKind::InteractionSource, slot_entity);
            actor
        }

        fn collect(&self) -> Vec<ResolvedInteractionSource> {
            collect_interaction_sources(&self.actors, &self.world, 0)
        }
    }

    /// 通常のアクター 1 個から 1 ソースが収集され、位置・半径・強さが載ること。
    #[test]
    fn collects_active_enabled_source() {
        let mut s = TestScene::new();
        let a = s.make_source_actor([1.0, 2.0, 3.0]);
        s.actors.push(a);

        let srcs = s.collect();
        assert_eq!(srcs.len(), 1);
        assert_eq!(srcs[0].world_pos, [1.0, 2.0, 3.0]);
        assert_eq!(srcs[0].radius, 1.0);
        assert_eq!(srcs[0].strength, 1.0);
    }

    /// active=false のアクターはスキップされる。
    #[test]
    fn skips_inactive_actor() {
        let mut s = TestScene::new();
        let mut a = s.make_source_actor([0.0, 0.0, 0.0]);
        a.active = false;
        s.actors.push(a);
        assert!(s.collect().is_empty());
    }

    /// 祖先が非アクティブなら子のソースもスキップされる（サブツリー伝播）。
    #[test]
    fn skips_subtree_under_inactive_ancestor() {
        let mut s = TestScene::new();
        let child = s.make_source_actor([0.0, 1.0, 0.0]);
        let parent_entity = s.world.spawn();
        s.world.insert(parent_entity, Transform::default());
        let mut parent = Actor::new(parent_entity, "parent");
        parent.active = false;
        parent.add_child(child);
        s.actors.push(parent);
        assert!(s.collect().is_empty());
    }

    /// スロットの enabled=false はスキップされる。
    #[test]
    fn skips_disabled_slot() {
        let mut s = TestScene::new();
        let mut a = s.make_source_actor([0.0, 0.0, 0.0]);
        a.slots_mut()[0].enabled = false;
        s.actors.push(a);
        assert!(s.collect().is_empty());
    }

    /// コンポーネント側 enabled=false もスキップされる（スロット有効でも書かない）。
    #[test]
    fn skips_component_disabled() {
        let mut s = TestScene::new();
        let a = s.make_source_actor([0.0, 0.0, 0.0]);
        let slot_entity = a.slots()[0].entity;
        s.world.get_mut::<InteractionSourceComponent>(slot_entity).unwrap().enabled = false;
        s.actors.push(a);
        assert!(s.collect().is_empty());
    }

    /// 半径 0 / 強さ 0 のソースは GPU へ送らない（場に何も書けないため）。
    #[test]
    fn skips_zero_radius_or_strength() {
        let mut s = TestScene::new();
        let a = s.make_source_actor([0.0, 0.0, 0.0]);
        let slot_entity = a.slots()[0].entity;
        s.world.get_mut::<InteractionSourceComponent>(slot_entity).unwrap().radius = 0.0;
        s.actors.push(a);
        assert!(s.collect().is_empty());

        let mut s2 = TestScene::new();
        let b = s2.make_source_actor([0.0, 0.0, 0.0]);
        let slot_entity2 = b.slots()[0].entity;
        s2.world.get_mut::<InteractionSourceComponent>(slot_entity2).unwrap().strength = 0.0;
        s2.actors.push(b);
        assert!(s2.collect().is_empty());
    }

    /// world_line が一致しないルートは対象外。
    #[test]
    fn skips_other_world_line() {
        let mut s = TestScene::new();
        let mut a = s.make_source_actor([0.0, 0.0, 0.0]);
        a.world_line = 7;
        s.actors.push(a);
        assert!(collect_interaction_sources(&s.actors, &s.world, 0).is_empty());
        assert_eq!(collect_interaction_sources(&s.actors, &s.world, 7).len(), 1);
    }

    /// キーは DFS 連番から作られ、ソースを持たないアクタも 1 つとして数える
    /// （前フレーム位置の突き合わせを安定させるため）。
    #[test]
    fn key_uses_dfs_index_counting_all_actors() {
        let mut s = TestScene::new();
        // ルート0: ソースなし親（dfs 0）＋ ソースを持つ子（dfs 1）
        let child = s.make_source_actor([0.0, 1.0, 0.0]);
        let parent_entity = s.world.spawn();
        s.world.insert(parent_entity, Transform::default());
        let mut parent = Actor::new(parent_entity, "parent");
        parent.add_child(child);
        s.actors.push(parent);
        // ルート1: ソースを持つアクタ（dfs 2）
        let second = s.make_source_actor([0.0, 2.0, 0.0]);
        s.actors.push(second);

        let srcs = s.collect();
        assert_eq!(srcs.len(), 2);
        assert_eq!(srcs[0].key, source_key(1, 0));
        assert_eq!(srcs[1].key, source_key(2, 0));
    }
}
