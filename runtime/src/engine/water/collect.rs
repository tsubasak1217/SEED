// ============================================================
//  water/collect.rs — シーンからの水ボリューム収集
//
//  Actor ツリーを DFS で走査し、有効な WaterVolumeComponent を
//  ワールド空間の ResolvedWaterVolume へ解決して集める。
//
//  スキップ規則は他の収集処理（app/actor_utils.rs の collect_mcs_in_world_line /
//  app/audio_ops.rs の collect_audio_sources）と揃える:
//    ・world_line が一致しないルートは対象外
//    ・active=false のアクターはサブツリーごとスキップ（祖先の非アクティブも伝播）
//    ・enabled=false のスロットはスキップ
//    ・Spline（W4 未実装）はスキップ
//
//  【W1 の制限】アクタのワールド行列は Transform.position のみ使う
//  （Transform はワールド空間）。**回転は無視する ＝ Region は軸平行 AABB。**
//  回転した水塊への対応は W4 以降。
// ============================================================

use crate::engine::components::water_volume_component::{
    WaterVolumeComponent, WaterVolumeKind,
};
use crate::engine::components::{ComponentKind, Transform};
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;

use super::resolved::ResolvedWaterVolume;

/// Transform を持たないアクター（フォルダノード・2D アクター等）の既定位置。
const FALLBACK_ACTOR_POSITION: [f32; 3] = [0.0, 0.0, 0.0];

/// シーンの全アクタを再帰走査し、有効な WaterVolume をワールド空間へ解決して集める。
/// active でないアクタ・enabled でないスロット・Spline(未実装) はスキップする。
///
/// `world_line` はアクター編集タブとの分離用（0=通常シーン）。
pub fn collect_water_volumes(
    actors:     &[Actor],
    world:      &World,
    world_line: u32,
) -> Vec<ResolvedWaterVolume> {
    let mut out = Vec::new();
    // ルートは world_line が一致するものだけを対象にする
    //（collect_mcs_in_world_line と同じフィルタ条件）
    for root in actors.iter().filter(|a| a.world_line == world_line) {
        collect_in_actor(root, world, &mut out, true);
    }
    out
}

/// collect_water_volumes の再帰実装。
///
/// `parent_active` は祖先のアクティブ状態。自身または祖先が active=false の
/// アクターは、そのサブツリーごと収集対象から外す。
fn collect_in_actor(
    actor:         &Actor,
    world:         &World,
    out:           &mut Vec<ResolvedWaterVolume>,
    parent_active: bool,
) {
    let active = parent_active && actor.active;

    if active {
        // アクターのワールド位置（Transform はワールド空間。回転は W1 では無視）
        let pos = world.get::<Transform>(actor.entity)
            .map(|t| t.position)
            .unwrap_or(FALLBACK_ACTOR_POSITION);

        for slot in actor.slots() {
            // 無効スロットは描画・問い合わせともに対象外
            if slot.kind != ComponentKind::WaterVolume || !slot.enabled { continue; }
            let Some(wv) = world.get::<WaterVolumeComponent>(slot.entity) else { continue };
            // Spline は W4 で実装。それまでは収集しない（下流が誤って参照しないように）。
            if wv.kind == WaterVolumeKind::Spline { continue; }
            out.push(ResolvedWaterVolume::from_component(wv, pos));
        }
    }

    // 非アクティブなアクターは子孫も収集しない（サブツリーごとスキップ）
    if active {
        for child in actor.children() {
            collect_in_actor(child, world, out, active);
        }
    }
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用シーンビルダの戻り値: (World, ルートアクター列)
    struct TestScene {
        world:  World,
        actors: Vec<Actor>,
    }

    impl TestScene {
        fn new() -> Self {
            Self { world: World::new(), actors: Vec::new() }
        }

        /// 指定位置に WaterVolume スロットを 1 つ持つアクターを作って返す
        /// （まだツリーへは追加しない）。
        fn make_water_actor(
            &mut self,
            pos:  [f32; 3],
            kind: WaterVolumeKind,
        ) -> Actor {
            let entity = self.world.spawn();
            let mut tf = Transform::default();
            tf.position = pos;
            self.world.insert(entity, tf);

            let mut actor = Actor::new(entity, "water");
            let slot_entity = self.world.spawn();
            let mut wv = WaterVolumeComponent::default();
            wv.kind = kind;
            self.world.insert(slot_entity, wv);
            actor.add_slot_typed::<WaterVolumeComponent>(
                "WaterVolumeComponent", ComponentKind::WaterVolume, slot_entity);
            actor
        }

        /// 収集を実行する（world_line 0）。
        fn collect(&self) -> Vec<ResolvedWaterVolume> {
            collect_water_volumes(&self.actors, &self.world, 0)
        }
    }

    /// 通常のアクター 1 個から 1 ボリュームが収集される。
    #[test]
    fn collects_active_enabled_volume() {
        let mut s = TestScene::new();
        let a = s.make_water_actor([0.0, 4.0, 0.0], WaterVolumeKind::Region);
        s.actors.push(a);

        let vols = s.collect();
        assert_eq!(vols.len(), 1);
        // Region の水面 = アクタ Y + surface_height(既定 0.0)
        assert_eq!(vols[0].surface_y, 4.0);
        assert_eq!(vols[0].center, [0.0, 4.0, 0.0]);
    }

    /// active=false のアクターはスキップされる。
    #[test]
    fn skips_inactive_actor() {
        let mut s = TestScene::new();
        let mut a = s.make_water_actor([0.0, 0.0, 0.0], WaterVolumeKind::Region);
        a.active = false;
        s.actors.push(a);

        assert!(s.collect().is_empty());
    }

    /// 祖先が非アクティブなら子の水もスキップされる（サブツリー伝播）。
    #[test]
    fn skips_subtree_under_inactive_ancestor() {
        let mut s = TestScene::new();
        let child = s.make_water_actor([0.0, 1.0, 0.0], WaterVolumeKind::Region);
        let parent_entity = s.world.spawn();
        s.world.insert(parent_entity, Transform::default());
        let mut parent = Actor::new(parent_entity, "parent");
        parent.active = false;
        parent.add_child(child);
        s.actors.push(parent);

        assert!(s.collect().is_empty());
    }

    /// アクティブな親の下の子アクターの水は収集される。
    #[test]
    fn collects_child_under_active_parent() {
        let mut s = TestScene::new();
        let child = s.make_water_actor([0.0, 1.0, 0.0], WaterVolumeKind::Region);
        let parent_entity = s.world.spawn();
        s.world.insert(parent_entity, Transform::default());
        let mut parent = Actor::new(parent_entity, "parent");
        parent.add_child(child);
        s.actors.push(parent);

        assert_eq!(s.collect().len(), 1);
    }

    /// enabled=false のスロットはスキップされる。
    #[test]
    fn skips_disabled_slot() {
        let mut s = TestScene::new();
        let mut a = s.make_water_actor([0.0, 0.0, 0.0], WaterVolumeKind::Region);
        a.slots_mut()[0].enabled = false;
        s.actors.push(a);

        assert!(s.collect().is_empty());
    }

    /// Spline（W4 未実装）は収集されない。
    #[test]
    fn skips_spline_kind() {
        let mut s = TestScene::new();
        let a = s.make_water_actor([0.0, 0.0, 0.0], WaterVolumeKind::Spline);
        s.actors.push(a);

        assert!(s.collect().is_empty());
    }

    /// world_line が一致しないルートは対象外。
    #[test]
    fn skips_other_world_line() {
        let mut s = TestScene::new();
        let mut a = s.make_water_actor([0.0, 0.0, 0.0], WaterVolumeKind::Region);
        a.world_line = 7;
        s.actors.push(a);

        // world_line 0 では収集されない
        assert!(collect_water_volumes(&s.actors, &s.world, 0).is_empty());
        // world_line 7 では収集される
        assert_eq!(collect_water_volumes(&s.actors, &s.world, 7).len(), 1);
    }

    /// Ocean はアクタ位置に依存せず、surface_height をワールド Y として使う。
    #[test]
    fn ocean_ignores_actor_position() {
        let mut s = TestScene::new();
        let a = s.make_water_actor([10.0, 99.0, -5.0], WaterVolumeKind::Ocean);
        s.actors.push(a);

        let vols = s.collect();
        assert_eq!(vols.len(), 1);
        // surface_height 既定 0.0 がそのままワールド水面 Y になる
        assert_eq!(vols[0].surface_y, 0.0);
    }
}
