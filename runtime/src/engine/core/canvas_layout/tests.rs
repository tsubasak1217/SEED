// ============================================================
//  canvas_layout/tests.rs — レイアウトの純関数・走査・切り抜きの単体テスト
//
//  旧実装（5 か所の複製）との同値の性質テストは、旧実装の出力の型が app 側にあるため
//  app/canvas_layout_equivalence/ に置いている。ここでは表そのものの性質を確かめる。
// ============================================================

use std::collections::HashMap;

use super::clip::{corners_aabb, point_inside_clip_chain, rect_corners, CanvasClipRegion, ClipRectSource};
use super::*;
use crate::engine::components::{
    CanvasClipComponent, CanvasComponent, CanvasDrawZone, CanvasTransform, ComponentKind, SpriteComponent,
};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

/// テスト用のビューポート（横長）。
const VIEWPORT: [f32; 2] = [1280.0, 720.0];

/// キャンバスのノードを作る（CanvasTransform + CanvasComponent）。
fn canvas_node(world: &mut World, name: &str, ct: CanvasTransform, size: [f32; 2]) -> Actor {
    let entity = world.spawn();
    world.insert(entity, ct);
    let slot = world.spawn();
    world.insert(
        slot,
        CanvasComponent { width: size[0], height: size[1], auto_scale: false, ..CanvasComponent::default() },
    );
    let mut actor = Actor::new_2d(entity, name);
    actor.add_slot_typed::<CanvasComponent>("Canvas", ComponentKind::Canvas, slot);
    actor
}

/// スプライトのノードを作る（CanvasTransform + SpriteComponent）。
fn sprite_node(world: &mut World, name: &str, ct: CanvasTransform, size: [f32; 2]) -> Actor {
    let entity = world.spawn();
    world.insert(entity, ct);
    let slot = world.spawn();
    world.insert(slot, SpriteComponent { width: size[0], height: size[1], ..SpriteComponent::default() });
    let mut actor = Actor::new_2d(entity, name);
    actor.add_slot_typed::<SpriteComponent>("Sprite", ComponentKind::Sprite, slot);
    actor
}

/// 表の並びは find_actor_by_dfs と同じ（ルート → 子を深さ優先）で、subtree_end が子孫の範囲を指す。
#[test]
fn table_is_depth_first_with_subtree_ranges() {
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), VIEWPORT);
    let mut panel = canvas_node(&mut world, "Panel", CanvasTransform::default(), [400.0, 300.0]);
    panel.add_child(sprite_node(&mut world, "A", CanvasTransform::default(), [10.0, 10.0]));
    root.add_child(panel);
    root.add_child(sprite_node(&mut world, "B", CanvasTransform::default(), [10.0, 10.0]));
    let roots = vec![root];
    let empty: HashMap<Entity, [f32; 2]> = HashMap::new();
    let env = CanvasLayoutEnv::without_viewport(&empty, AutoScaleDivisor::Raw);
    let table = CanvasLayoutPass::run(&roots, &world, 0, CanvasParentFrame::viewport_root(CanvasDrawZone::Foreground), &env);
    let names: Vec<&str> = table.iter_with_actors(&roots).map(|(_, a)| a.name.as_str()).collect();
    assert_eq!(names, vec!["Root", "Panel", "A", "B"]);
    assert_eq!(table.nodes[0].subtree_end, 4);
    assert_eq!(table.nodes[1].subtree_end, 3);
    assert_eq!(table.nodes[2].parent, Some(1));
    assert_eq!(table.nodes[3].parent, Some(0));
    assert_eq!(table.nodes[2].depth, 2, "ルート 0 → パネル 1 → 子 2");
}

/// 子のアンカーは親のキャンバス領域 × anchor（位置と同じく親の累積スケールが掛かる）。
#[test]
fn child_anchor_uses_parent_canvas_area() {
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), VIEWPORT);
    let child_ct = CanvasTransform { anchor: [0.5, 0.5], position: [10.0, -20.0], ..CanvasTransform::default() };
    root.add_child(sprite_node(&mut world, "C", child_ct, [40.0, 20.0]));
    let roots = vec![root];
    let empty: HashMap<Entity, [f32; 2]> = HashMap::new();
    let env = CanvasLayoutEnv {
        viewport_size: Some(VIEWPORT),
        viewport_overrides: &empty,
        root_auto_sizes: &empty,
        design_space: true,
        auto_scale_divisor: AutoScaleDivisor::Raw,
        screen: CanvasScreenEnv::NONE,
    };
    let table = CanvasLayoutPass::run(&roots, &world, 0, CanvasParentFrame::viewport_root(CanvasDrawZone::Foreground), &env);
    let child = table.nodes[1].placement().expect("子は配置を持つ");
    assert_eq!(child.anchor_offset, [VIEWPORT[0] * 0.5, VIEWPORT[1] * 0.5]);
    assert_eq!(child.eff_transform.position, [VIEWPORT[0] * 0.5 + 10.0, VIEWPORT[1] * 0.5 - 20.0]);
}

/// CanvasTransform を持たないノードの子孫は 2D レイアウト木の外になる（描画・当たり判定の対象外）。
#[test]
fn nodes_below_a_3d_actor_are_outside_the_2d_tree() {
    let mut world = World::new();
    let entity = world.spawn();
    let mut actor3d = Actor::new(entity, "Actor3D");
    actor3d.add_child(sprite_node(&mut world, "S", CanvasTransform::default(), [10.0, 10.0]));
    let roots = vec![actor3d];
    let empty: HashMap<Entity, [f32; 2]> = HashMap::new();
    let env = CanvasLayoutEnv::without_viewport(&empty, AutoScaleDivisor::Raw);
    let table = CanvasLayoutPass::run(&roots, &world, 0, CanvasParentFrame::viewport_root(CanvasDrawZone::Foreground), &env);
    assert!(matches!(table.nodes[0].kind, CanvasNodeKind::NoTransform { .. }));
    assert!(table.nodes[0].flags.in_2d_tree, "3D アクター自身の祖先は木の中");
    assert!(!table.nodes[1].flags.in_2d_tree, "その子は木の外");
    assert!(!table.nodes[1].is_drawn());
}

/// 表とアクター木の並びが食い違ったら反復を打ち切る（別のアクターへ値を当てない）。
#[test]
fn iterator_stops_on_mismatch() {
    let mut world = World::new();
    let a = sprite_node(&mut world, "A", CanvasTransform::default(), [1.0, 1.0]);
    let b = sprite_node(&mut world, "B", CanvasTransform::default(), [1.0, 1.0]);
    let empty: HashMap<Entity, [f32; 2]> = HashMap::new();
    let env = CanvasLayoutEnv::without_viewport(&empty, AutoScaleDivisor::Raw);
    let table = CanvasLayoutPass::run(&[a], &world, 0, CanvasParentFrame::viewport_root(CanvasDrawZone::Foreground), &env);
    assert_eq!(table.iter_with_actors(&[b]).count(), 0);
}

/// 4 隅は (0,0)・(1,0)・(0,1)・(1,1) の並びで、AABB は回転しても 4 隅を囲む。
#[test]
fn rect_corners_and_aabb() {
    let m = CanvasTransform { position: [100.0, 50.0], rotation: 90.0, ..CanvasTransform::default() }
        .to_mat4_sized(20.0, 10.0);
    let corners = rect_corners(&m, [20.0, 10.0]);
    let (min, max) = corners_aabb(&corners);
    assert!((min[0] - 90.0).abs() < 1e-4 && (max[0] - 100.0).abs() < 1e-4, "{min:?} {max:?}");
    assert!((min[1] - 50.0).abs() < 1e-4 && (max[1] - 70.0).abs() < 1e-4, "{min:?} {max:?}");
}

/// 点は切り抜きの鎖（自身と祖先）のすべての領域の内側のときだけ内側。境界上は内側。
#[test]
fn point_inside_nested_clip_chain() {
    let region = |min: [f32; 2], max: [f32; 2], parent: Option<u16>| CanvasClipRegion {
        corners: [[min[0], min[1], 0.0], [max[0], min[1], 0.0], [min[0], max[1], 0.0], [max[0], max[1], 0.0]],
        parent,
        owner: 0,
        source: ClipRectSource::CanvasArea,
        shape: crate::engine::core::renderer::ui_shape::UiClipShape::NONE,
    };
    let regions = vec![region([0.0, 0.0], [100.0, 100.0], None), region([50.0, 50.0], [200.0, 200.0], Some(0))];
    assert!(point_inside_clip_chain(&regions, None, [500.0, 500.0]), "切り抜きの外のノードは常に内側");
    assert!(point_inside_clip_chain(&regions, Some(1), [75.0, 75.0]));
    assert!(point_inside_clip_chain(&regions, Some(1), [100.0, 100.0]), "境界上は内側");
    assert!(!point_inside_clip_chain(&regions, Some(1), [150.0, 150.0]), "内側の領域の中でも外側の領域の外");
    assert!(!point_inside_clip_chain(&regions, Some(1), [25.0, 25.0]), "外側の領域の中でも内側の領域の外");
}


// ─── 切り抜き（CanvasClipComponent）の領域 ────────────────────────────

/// 切り抜きのコンポーネントのスロットを足す（`enabled` はコンポーネントの旗、`slot_enabled` はスロットの旗）。
fn add_clip(world: &mut World, actor: &mut Actor, enabled: bool, slot_enabled: bool) {
    let slot = world.spawn();
    world.insert(slot, CanvasClipComponent { enabled, ..CanvasClipComponent::default() });
    actor.add_slot_typed::<CanvasClipComponent>("CanvasClip", ComponentKind::CanvasClip, slot);
    if !slot_enabled {
        if let Some(last) = actor.slots_mut().last_mut() {
            last.enabled = false;
        }
    }
}

/// 表を作る（ビューポート基準なし・設計空間相当の単純な文脈）。
fn table_of(roots: &[Actor], world: &World) -> CanvasLayoutTable {
    let empty: HashMap<Entity, [f32; 2]> = HashMap::new();
    let env = CanvasLayoutEnv::without_viewport(&empty, AutoScaleDivisor::Raw);
    CanvasLayoutPass::run(roots, world, 0, CanvasParentFrame::viewport_root(CanvasDrawZone::Foreground), &env)
}

/// キャンバス領域を持つノードは、その領域で子孫を切る。ノード自身は切らない。入れ子は外側を親に持つ。
#[test]
fn clip_regions_use_canvas_area_and_nest() {
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), VIEWPORT);
    let mut outer = canvas_node(&mut world, "Outer", CanvasTransform { position: [100.0, 50.0], ..CanvasTransform::default() }, [400.0, 300.0]);
    add_clip(&mut world, &mut outer, true, true);
    let mut inner = sprite_node(&mut world, "Inner", CanvasTransform { position: [300.0, 200.0], ..CanvasTransform::default() }, [200.0, 150.0]);
    add_clip(&mut world, &mut inner, true, true);
    inner.add_child(sprite_node(&mut world, "Leaf", CanvasTransform::default(), [10.0, 10.0]));
    outer.add_child(inner);
    root.add_child(outer);
    let roots = vec![root];
    let table = table_of(&roots, &world);
    assert_eq!(table.clip_regions.len(), 2);
    let [outer_node, inner_node, leaf] = [&table.nodes[1], &table.nodes[2], &table.nodes[3]];
    assert_eq!(outer_node.clip, None, "切り抜きのノード自身は切らない");
    assert_eq!(outer_node.own_clip_region, Some(0));
    assert_eq!(inner_node.clip, Some(0), "子は外側の領域に入る");
    assert_eq!(inner_node.own_clip_region, Some(1));
    assert_eq!(leaf.clip, Some(1), "孫は内側の領域に入る");
    assert_eq!(table.clip_regions[1].parent, Some(0), "入れ子は外側を親に持つ");
    assert_eq!(table.clip_regions[0].source, ClipRectSource::CanvasArea);
    assert_eq!(table.clip_regions[1].source, ClipRectSource::FirstSprite, "キャンバスが無ければ最初のスプライト");
    let (min, max) = corners_aabb(&table.clip_regions[0].corners);
    assert_eq!((min, max), ([100.0, 50.0], [500.0, 350.0]), "外側はキャンバス領域");
    let (min, max) = corners_aabb(&table.clip_regions[1].corners);
    assert_eq!((min, max), ([400.0, 250.0], [600.0, 400.0]), "内側はスプライトの矩形（親の原点 + 位置）");
}

/// スロットかコンポーネントの旗のどちらかが無効なら切らない。矩形が決まらないノードも切らない。
#[test]
fn disabled_or_rectless_clip_makes_no_region() {
    for (enabled, slot_enabled) in [(false, true), (true, false)] {
        let mut world = World::new();
        let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), VIEWPORT);
        add_clip(&mut world, &mut root, enabled, slot_enabled);
        root.add_child(sprite_node(&mut world, "Child", CanvasTransform::default(), [10.0, 10.0]));
        let roots = vec![root];
        let table = table_of(&roots, &world);
        assert!(table.clip_regions.is_empty(), "enabled={enabled} slot_enabled={slot_enabled}");
        assert_eq!(table.nodes[1].clip, None);
    }
    // CanvasComponent も Sprite も無いノード（矩形が決まらない）
    let mut world = World::new();
    let entity = world.spawn();
    world.insert(entity, CanvasTransform::default());
    let mut bare = Actor::new_2d(entity, "Bare");
    add_clip(&mut world, &mut bare, true, true);
    bare.add_child(sprite_node(&mut world, "Child", CanvasTransform::default(), [10.0, 10.0]));
    let roots = vec![bare];
    let table = table_of(&roots, &world);
    assert!(table.clip_regions.is_empty());
    assert_eq!(table.nodes[1].clip, None);
}

/// フォルダを挟んでも子孫は同じ切り抜きに入る（フォルダはレイアウト透明）。
#[test]
fn folder_keeps_the_clip_of_its_parent() {
    let mut world = World::new();
    let mut root = canvas_node(&mut world, "Root", CanvasTransform::default(), VIEWPORT);
    add_clip(&mut world, &mut root, true, true);
    let folder_entity = world.spawn();
    world.insert(folder_entity, CanvasTransform::default());
    let mut folder = Actor::new_folder_2d(folder_entity, "Folder");
    folder.add_child(sprite_node(&mut world, "Child", CanvasTransform::default(), [10.0, 10.0]));
    root.add_child(folder);
    let roots = vec![root];
    let table = table_of(&roots, &world);
    assert_eq!(table.nodes[1].clip, Some(0), "フォルダ自身も領域の中");
    assert_eq!(table.nodes[2].clip, Some(0), "フォルダの子も同じ領域");
}
