// ============================================================
//  frame_scan_gates.rs — 毎フレームの 2D の全走査を「対象が無ければ走らせない」ための判定（純関数）
//
//  【なぜ要るか（2026-09-28 の実機の計測。docs/app_platform_roadmap.md §3.9 の原因 3）】
//  UI だけの見本（templates/ui）のスクロール中、2D の木を毎フレームたどる処理（2D 物理の同期・ポインタイベント・
//  スクリーン座標・ジェスチャー・スプライト収集・エディタ状態の収集）が release の .so でもフレームの 25% を使っていた。
//  見本には 2D 物理のボディも raycast_target のスプライトも無いのに、2D 物理の同期は全 2D アクタの文脈の表
//  （collect_actor2d_contexts ＝レイアウトの表の作成）を、ポインタイベントはヒットテストの木の走査を毎フレーム行っていた。
//
//  【ここに置く判定の約束】
//  どの判定も「false なら、走査しても結果が空（何も起きない）」ことが**コードから言える**ものだけにする
//  （見込みや経験則で省かない）。判定はコンポーネントの格納（疎集合）を見るだけで、木はたどらない。
//    - 2D のコライダー: 2D 物理のボディは Collider2dComponent からしか作られない（start_physics_2d・
//      physics2d_component_ops の追加）。1 つも無ければ物理ワールドは空で、update_physics_2d が文脈の表を使う処理
//      （書き戻し・キネマティックの送信・再登録・ドラッグの押し戻し）はどれも「コライダーを持つ文脈」か
//      「物理のボディの ID」でしか文脈を引かないので、空の表と同じ結果になる。
//    - ポインタイベントの的: ポインタイベントのヒットテスト（PickFilter2d::POINTER_EVENT）は raycast_target = true の
//      Sprite / SkinnedSprite だけを候補にする（Canvas・Text は候補にしない）。1 つも無ければ候補は必ず空＝当たり無し。
//  判定はフレームごとに行う（スクリプトが途中でコンポーネントを足した・raycast_target を立てたフレームから走る）。
// ============================================================

use crate::engine::components::{Collider2dComponent, SkinnedSpriteComponent, SpriteComponent};
use crate::engine::ecs::World;

/// 2D のコライダー（Collider2dComponent）が 1 つでもあるか。
///
/// false なら 2D 物理のボディは 1 つも無い（ボディは Collider2dComponent からしか作られない）。
/// 格納の先頭を見るだけ（O(1)）。有効・無効（slot の enabled）は問わない（あれば走査する＝従来どおり）。
pub(super) fn world_has_2d_colliders(world: &World) -> bool {
    world.query::<Collider2dComponent>().next().is_some()
}

/// ポインタイベントの当たり判定の対象（raycast_target = true の Sprite / SkinnedSprite）が 1 つでもあるか。
///
/// false ならポインタイベントのヒットテストの候補は必ず空（`walk_pick_candidates_2d` を POINTER_EVENT で
/// 走らせても当たり無し）。格納を 1 回なめるだけで木はたどらない（O(スプライト数)。レイアウトの計算も無い）。
/// 表示・有効・アクティブは問わない（1 つでも的があれば従来どおり木をたどって判定する）。
pub(super) fn world_has_pointer_targets(world: &World) -> bool {
    world.query::<SpriteComponent>().any(|(_, sprite)| sprite.raycast_target)
        || world.query::<SkinnedSpriteComponent>().any(|(_, sprite)| sprite.raycast_target)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 空の World はどちらも false（UI だけの見本に相当: 走査を省く）。
    #[test]
    fn empty_world_has_no_targets() {
        let world = World::new();
        assert!(!world_has_2d_colliders(&world));
        assert!(!world_has_pointer_targets(&world));
    }

    /// raycast_target = false のスプライトだけなら的は無い（見本のボタンの絵・背景）。
    #[test]
    fn sprites_without_raycast_target_are_not_pointer_targets() {
        let mut world = World::new();
        for _ in 0..3 {
            let entity = world.spawn();
            world.insert(entity, SpriteComponent::default());
        }
        assert!(!world_has_pointer_targets(&world));
        assert!(!world_has_2d_colliders(&world), "スプライトはコライダーではない");
    }

    /// raycast_target = true のスプライトが 1 つでもあれば的がある（図鑑のボタンなど。従来どおり木をたどる）。
    #[test]
    fn one_raycast_sprite_is_a_pointer_target() {
        let mut world = World::new();
        let plain = world.spawn();
        world.insert(plain, SpriteComponent::default());
        let button = world.spawn();
        world.insert(button, SpriteComponent { raycast_target: true, ..SpriteComponent::default() });
        assert!(world_has_pointer_targets(&world));
        // 的を外せば（スクリプトが raycast_target を下ろした）次の判定から省く
        world.get_mut::<SpriteComponent>(button).expect("入れた").raycast_target = false;
        assert!(!world_has_pointer_targets(&world));
    }

    /// raycast_target = true のスキンスプライトも的（ヒットテストは変形後メッシュで判定する）。
    #[test]
    fn raycast_skinned_sprite_is_a_pointer_target() {
        let mut world = World::new();
        let entity = world.spawn();
        world.insert(entity, SkinnedSpriteComponent { raycast_target: true, ..SkinnedSpriteComponent::default() });
        assert!(world_has_pointer_targets(&world));
    }

    /// Collider2dComponent が 1 つでもあれば 2D のコライダーがある（無効なものでも従来どおり走査する）。
    #[test]
    fn one_collider_2d_counts() {
        let mut world = World::new();
        let entity = world.spawn();
        world.insert(entity, Collider2dComponent::default());
        assert!(world_has_2d_colliders(&world));
        // 取り除けば（スクリプトの RemoveComponent）次の判定から省く
        assert!(world.remove::<Collider2dComponent>(entity));
        assert!(!world_has_2d_colliders(&world));
    }
}
