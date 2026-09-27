// ============================================================
//  gesture_scene.rs — レイアウトの表からジェスチャーの当たり判定の材料を作る（W2-2）
//
//  【何をするか】
//  Play のポインタイベント（pointer_events.rs）と同じ文脈（画面の大きさ・Camera 参照の上書き・自動解像度・
//  画面の情報）で 2D キャンバスのレイアウトの表（canvas_layout）を作り、CanvasGestureComponent を持つノードだけを
//  `GestureHitNode`（ジェスチャーの当たり判定の材料。engine/core/input/gesture/hit_slop.rs）へ写す。
//  アリーナはこの材料だけを読む（World に触れない）。
//
//  【写し方】
//    - 対象: 当たり判定の対象（祖先まで visible・active・世界線・2D レイアウト木の中）で、有効なスロットの
//      CanvasGestureComponent を持ち、そのコンポーネントも有効なノード
//    - 見た目の矩形: CanvasComponent があればキャンバス領域、無ければ最初の有効な Sprite の矩形（切り抜きの矩形と同じ
//      選び方。レイアウトが伸ばした軸は矩形の大きさ）。どちらも無いノードは参加しない
//    - 行列: 親のワールド行列 × 有効トランスフォーム（ローカルの [0,w]×[0,h] → キャンバスの画素。pick_2d と同じ）
//    - 切り抜き: ノードの祖先の切り抜きの領域の AABB（点がすべての内側のときだけ当たる）
//    - 祖先: 表の親をたどり、ジェスチャーを受けるノードだけを近い順に
//    - 手前・奥: 描画ゾーン → 最初の有効な Sprite のレイヤー（無ければ 0）→ 表の並び（DFS）
//  ジェスチャーを受けるノードが無い木では、表を作っても空の材料になる（呼び出し側は指が無ければ作らない）。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::{
    CanvasDrawZone, CanvasGestureComponent, ComponentKind, SpriteComponent,
};
use crate::engine::core::canvas_layout::clip::corners_aabb;
use crate::engine::core::canvas_layout::{
    AutoScaleDivisor, CanvasLayoutEnv, CanvasLayoutPass, CanvasLayoutTable, CanvasNodeKind, CanvasParentFrame,
};
use crate::engine::core::input::gesture::{ClipAabb, GestureHitNode, GestureHitScene, PaintOrder};
use crate::engine::ecs::{Entity, World};
use crate::engine::methods::gizmo_interact::mat4x4_mul;
use crate::engine::structs::objects::Actor;

/// 描画ゾーンの手前の順（前景が手前）。
fn zone_front(zone: CanvasDrawZone) -> u8 {
    match zone {
        CanvasDrawZone::Foreground => 1,
        CanvasDrawZone::Background => 0,
    }
}

/// ノードのジェスチャーの設定（有効なスロットの最初の CanvasGesture で、コンポーネントも有効なもの）。
pub(super) fn gesture_of<'w>(actor: &Actor, world: &'w World) -> Option<&'w CanvasGestureComponent> {
    actor
        .slots()
        .iter()
        .filter(|s| s.kind == ComponentKind::CanvasGesture && s.enabled)
        .find_map(|s| world.get::<CanvasGestureComponent>(s.entity))
        .filter(|c| c.enabled)
}

/// ノードの最初の有効な Sprite。
fn first_sprite<'w>(actor: &Actor, world: &'w World) -> Option<&'w SpriteComponent> {
    actor
        .slots()
        .iter()
        .filter(|s| s.kind == ComponentKind::Sprite && s.enabled)
        .find_map(|s| world.get::<SpriteComponent>(s.entity))
}

/// レイアウトの表からジェスチャーの当たり判定の材料を作る【純関数】。
///
/// # 引数
/// * `table`  - レイアウトの表（`actors` から作ったもの）
/// * `actors` - 表を作ったのと同じルートの並び
/// * `world`  - コンポーネントの置き場
pub(super) fn gesture_nodes_from_table(table: &CanvasLayoutTable, actors: &[Actor], world: &World) -> Vec<GestureHitNode> {
    let mut nodes: Vec<GestureHitNode> = Vec::new();
    // 表の行 → 材料の添字（祖先を引くため。祖先は必ず子より先に訪ねる）
    let mut by_row: HashMap<u32, usize> = HashMap::new();
    for (row, (node, actor)) in table.iter_with_actors(actors).enumerate() {
        // Play のポインタイベントと同じ対象（非アクティブ・非表示・世界線の外・2D 木の外は除く）
        if !node.is_pickable(true) {
            continue;
        }
        let CanvasNodeKind::Placed(placement) = &node.kind else { continue };
        let Some(settings) = gesture_of(actor, world) else { continue };
        let sprite = first_sprite(actor, world);
        // 見た目の矩形（ローカルの大きさ）
        let size = if placement.canvas_base.is_some() {
            placement.eff_size
        } else if let Some(sc) = sprite {
            placement.sprite_size(sc.width, sc.height)
        } else {
            continue;
        };
        let matrix = mat4x4_mul(node.frame.world_rs, placement.eff_transform.to_mat4_sized(size[0], size[1]));
        // 祖先の切り抜きの鎖（親は必ず子より番号が小さいので、鎖は表の長さ以内で終わる）
        let mut clip_aabbs: Vec<ClipAabb> = Vec::new();
        let mut current = node.clip;
        for _ in 0..table.clip_regions.len() {
            let Some(id) = current else { break };
            let Some(region) = table.clip_regions.get(id as usize) else { break };
            clip_aabbs.push(corners_aabb(&region.corners));
            current = region.parent;
        }
        // 祖先のうちジェスチャーを受けるノード（近い順）
        let mut ancestors = Vec::new();
        let mut parent = node.parent;
        while let Some(p) = parent {
            if let Some(&g) = by_row.get(&p) {
                ancestors.push(g);
            }
            parent = table.nodes.get(p as usize).and_then(|n| n.parent);
        }
        let paint = PaintOrder {
            zone_front: zone_front(placement.zone),
            layer: sprite.map_or(0, |s| s.layer),
            dfs: row,
        };
        by_row.insert(row as u32, nodes.len());
        nodes.push(GestureHitNode {
            key: actor.entity,
            matrix,
            size,
            clip_aabbs,
            ancestors,
            paint,
            settings: settings.clone(),
            unit_scale: placement.size_scale,
        });
    }
    nodes
}

/// Play の文脈（ポインタイベントと同じ引数）でレイアウトの表を作り、ジェスチャーの当たり判定の材料にする。
///
/// # 引数
/// * `viewport`  - 画面の大きさ（画素。compute_viewport_size_2d）
/// * `overrides` / `root_auto` - build_ss_layout_maps の結果（Camera 参照の上書き・自動解像度）
/// * `dp_scale`  - 1 dp の画素数（最小のヒット領域の換算）
pub(super) fn build_gesture_hit_scene(
    actors: &[Actor],
    world: &World,
    wl: u32,
    viewport: [f32; 2],
    overrides: &HashMap<Entity, [f32; 2]>,
    root_auto: &HashMap<Entity, [f32; 2]>,
    dp_scale: f32,
) -> GestureHitScene {
    let env = CanvasLayoutEnv {
        viewport_size: Some(viewport),
        viewport_overrides: overrides,
        root_auto_sizes: root_auto,
        // Play の実合成は常に「画面中央原点」（設計空間表示は Edit 専用。pointer_events と同じ）
        design_space: false,
        auto_scale_divisor: AutoScaleDivisor::GuardEpsilon,
        screen: super::canvas_screen_env::screen_env_for(Some(viewport), false),
    };
    let table = CanvasLayoutPass::run(
        actors,
        world,
        wl,
        CanvasParentFrame::viewport_root(CanvasDrawZone::Foreground),
        &env,
    );
    GestureHitScene::new(gesture_nodes_from_table(&table, actors, world), dp_scale)
}

// ============================================================
//  単体テスト（実際のレイアウトの走査を通す）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::{CanvasClipComponent, CanvasComponent, CanvasTransform};
    use crate::engine::core::input::gesture::GestureScene;

    /// 画面（ビューポート）の大きさ。
    const VIEWPORT: [f32; 2] = [800.0, 600.0];

    /// ルートキャンバス（画面いっぱい）の下に、左上 (x, y)・大きさ (w, h) の Sprite の子を足していくための道具。
    struct SceneBuilder {
        world: World,
        root: Actor,
    }

    impl SceneBuilder {
        /// ルートキャンバスを作る。
        fn new() -> Self {
            let mut world = World::new();
            let root_entity = world.spawn();
            world.insert(root_entity, CanvasTransform::default());
            let canvas_slot = world.spawn();
            world.insert(canvas_slot, CanvasComponent { width: VIEWPORT[0], height: VIEWPORT[1], ..CanvasComponent::default() });
            let mut root = Actor::new_2d(root_entity, "Root");
            root.world_line = 0;
            root.add_slot_typed::<CanvasComponent>("Canvas", ComponentKind::Canvas, canvas_slot);
            Self { world, root }
        }

        /// Sprite（と任意でジェスチャー・切り抜き）を持つノードを作る。
        fn sprite_node(&mut self, name: &str, rect: [f32; 4], gesture: Option<CanvasGestureComponent>, clip: bool) -> Actor {
            let e = self.world.spawn();
            self.world.insert(e, CanvasTransform { position: [rect[0], rect[1]], ..CanvasTransform::default() });
            let mut a = Actor::new_2d(e, name);
            a.world_line = 0;
            let s = self.world.spawn();
            self.world.insert(s, SpriteComponent { width: rect[2], height: rect[3], layer: 3, ..SpriteComponent::default() });
            a.add_slot_typed::<SpriteComponent>("Sprite", ComponentKind::Sprite, s);
            if let Some(g) = gesture {
                let gs = self.world.spawn();
                self.world.insert(gs, g);
                a.add_slot_typed::<CanvasGestureComponent>("Gesture", ComponentKind::CanvasGesture, gs);
            }
            if clip {
                let cs = self.world.spawn();
                self.world.insert(cs, CanvasClipComponent::default());
                a.add_slot_typed::<CanvasClipComponent>("Clip", ComponentKind::CanvasClip, cs);
            }
            a
        }

        /// 材料を作る。
        fn build(self) -> (GestureHitScene, Vec<Actor>) {
            let actors = vec![self.root];
            let empty = HashMap::new();
            let scene = build_gesture_hit_scene(&actors, &self.world, 0, VIEWPORT, &empty, &empty, 1.0);
            (scene, actors)
        }
    }

    /// ジェスチャーを付けたノードだけが材料になり、矩形・祖先・レイヤーが表の値と一致する。
    /// 付けていないノード（Sprite だけ）は参加しない。
    #[test]
    fn only_gesture_nodes_join_with_layout_rects() {
        let mut b = SceneBuilder::new();
        let mut list = b.sprite_node("List", [0.0, 0.0, 400.0, 300.0], Some(CanvasGestureComponent { tap: false, drag: true, ..Default::default() }), true);
        let button = b.sprite_node("Button", [10.0, 20.0, 100.0, 50.0], Some(CanvasGestureComponent::default()), false);
        let plain = b.sprite_node("Plain", [500.0, 0.0, 100.0, 100.0], None, false);
        list.children_mut().push(button);
        b.root.children_mut().push(list);
        b.root.children_mut().push(plain);
        let (scene, _) = b.build();
        assert_eq!(scene.nodes.len(), 2, "List と Button だけ");
        let (l, btn) = (&scene.nodes[0], &scene.nodes[1]);
        assert_eq!(btn.ancestors, vec![0], "Button の祖先は List");
        assert_eq!(btn.size, [100.0, 50.0]);
        assert_eq!(btn.paint.layer, 3);
        // キャンバスの画素（画面の中央が原点）: ルートの左上は (-400, -300)
        assert_eq!(scene.hit_path([-400.0 + 60.0, -300.0 + 45.0]).iter().map(|(e, _)| *e).collect::<Vec<_>>(), vec![btn.key, l.key]);
        // List の切り抜き（List の Sprite の矩形）の外は、Button の広げた領域でも当たらない
        assert!(!btn.clip_aabbs.is_empty());
        assert_eq!(scene.press_region_contains(btn.key, [-400.0 + 60.0, -300.0 + 45.0], 0.0), Some(true));
        assert!(scene.hit_path([-400.0 + 550.0, -300.0 + 50.0]).is_empty(), "ジェスチャーの無いノードの上");
    }

    /// 無効なスロット・無効なコンポーネント・非アクティブなノードは参加しない。
    #[test]
    fn disabled_and_inactive_nodes_do_not_join() {
        let mut b = SceneBuilder::new();
        let mut off_slot = b.sprite_node("OffSlot", [0.0, 0.0, 100.0, 100.0], Some(CanvasGestureComponent::default()), false);
        off_slot.slots_mut()[1].enabled = false;
        let off_comp = b.sprite_node("OffComp", [100.0, 0.0, 100.0, 100.0], Some(CanvasGestureComponent { enabled: false, ..Default::default() }), false);
        let mut inactive = b.sprite_node("Inactive", [200.0, 0.0, 100.0, 100.0], Some(CanvasGestureComponent::default()), false);
        inactive.active = false;
        let on = b.sprite_node("On", [300.0, 0.0, 100.0, 100.0], Some(CanvasGestureComponent::default()), false);
        for a in [off_slot, off_comp, inactive, on] {
            b.root.children_mut().push(a);
        }
        let (scene, _) = b.build();
        assert_eq!(scene.nodes.len(), 1, "On だけ");
    }
}
