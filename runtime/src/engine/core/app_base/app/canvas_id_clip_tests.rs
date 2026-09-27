// ============================================================
//  canvas_id_clip_tests.rs — エディタの GPU の ID 描画の切り抜き（W2-1b）のテスト
//
//  ID 描画（3D ビューでのキャンバスの選択）は、W2-1a まで切り抜き（CanvasClipComponent）を見ず、
//  切り抜かれて見えない子もクリックで選べた。W2-1b でアイテムに切り抜きの番号を持たせ、描画と同じ規則で
//  scissor を張るようにした。GPU を使わずに確かめられる部分（アイテムの番号・画素の scissor の計算）をここで確かめる:
//    - 切り抜きの中の子のアイテムは番号を持ち、scissor は切り抜きの矩形の画素（オーバーレイのカメラで写したもの）
//    - 切り抜きのノード自身・切り抜きの外のアイテムは番号を持たない（scissor なし）
//  scissor を実際に張るのは methods/drawer/id_pass.rs の draw_canvas_id_items（描画の ui_draw_pass と同じ API）。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::{
    CanvasClipComponent, CanvasComponent, CanvasDrawZone, CanvasTransform, ComponentKind, SpriteComponent,
};
use crate::engine::core::canvas_layout::clip::to_render_region;
use crate::engine::core::canvas_layout::{
    AutoScaleDivisor, CanvasLayoutEnv, CanvasLayoutPass, CanvasParentFrame, CanvasScreenEnv,
};
use crate::engine::core::renderer::ui_clip::ScissorRect;
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;
use crate::engine::structs::tensor::{Mat4x4, Vector3};

use super::canvas_collect::{canvas_id_scissors, collect_canvas_id_items};
use super::canvas_text_bounds::TextBoundsMap;

/// 描画先（Play のウィンドウと同じ横長）。
const TARGET: [u32; 2] = [1280, 720];
/// ID の raw_id のオフセット（3D のインスタンス数の代わり）。
const MC_TOTAL: u32 = 7;

/// スプライトのノード。
fn sprite(world: &mut World, name: &str, position: [f32; 2], size: [f32; 2]) -> Actor {
    let entity = world.spawn();
    world.insert(entity, CanvasTransform { position, ..CanvasTransform::default() });
    let mut actor = Actor::new_2d(entity, name);
    let slot = world.spawn();
    world.insert(slot, SpriteComponent { width: size[0], height: size[1], ..SpriteComponent::default() });
    actor.add_slot_typed::<SpriteComponent>("Sprite", ComponentKind::Sprite, slot);
    actor
}

/// キャンバスのノード。
fn canvas(world: &mut World, name: &str, position: [f32; 2], size: [f32; 2]) -> Actor {
    let entity = world.spawn();
    world.insert(entity, CanvasTransform { position, ..CanvasTransform::default() });
    let mut actor = Actor::new_2d(entity, name);
    let slot = world.spawn();
    world.insert(slot, CanvasComponent { width: size[0], height: size[1], auto_scale: false, ..CanvasComponent::default() });
    actor.add_slot_typed::<CanvasComponent>("Canvas", ComponentKind::Canvas, slot);
    actor
}

/// frame_renderer のシーンのオーバーレイのカメラと同じビュー射影（中央原点・Y 下向き）。
fn overlay_view_proj() -> [[f32; 4]; 4] {
    let (half_w, half_h) = (TARGET[0] as f32 / 2.0, TARGET[1] as f32 / 2.0);
    let view = Mat4x4::look_at_lh(Vector3::new(0.0, 0.0, -100.0), Vector3::new(0.0, 0.0, 0.0), Vector3::new(0.0, 1.0, 0.0));
    let proj = Mat4x4::orthographic_lh(-half_w, half_w, half_h, -half_h, 0.0, 200.0);
    (proj * view).data
}

/// 切り抜きの中の子の ID は切り抜きの矩形の scissor を持ち、外と切り抜きのノード自身は持たない。
#[test]
fn id_items_inside_a_clip_get_the_clip_scissor() {
    let mut world = World::new();
    let mut root = canvas(&mut world, "Root", [0.0, 0.0], [1280.0, 720.0]);
    // 画面の (540, 310) から 200×100 の切り抜きの枠（背景の板のスプライト付き）
    let mut panel = canvas(&mut world, "Panel", [540.0, 310.0], [200.0, 100.0]);
    let bg = world.spawn();
    world.insert(bg, SpriteComponent { width: 200.0, height: 100.0, ..SpriteComponent::default() });
    panel.add_slot_typed::<SpriteComponent>("Bg", ComponentKind::Sprite, bg);
    let clip = world.spawn();
    world.insert(clip, CanvasClipComponent { enabled: true, ..CanvasClipComponent::default() });
    panel.add_slot_typed::<CanvasClipComponent>("Clip", ComponentKind::CanvasClip, clip);
    // 右へはみ出す子（400 幅）
    panel.add_child(sprite(&mut world, "Wide", [0.0, 0.0], [400.0, 40.0]));
    root.add_child(panel);
    root.add_child(sprite(&mut world, "Outside", [10.0, 10.0], [50.0, 50.0]));
    let roots = vec![root];

    let empty: HashMap<Entity, [f32; 2]> = HashMap::new();
    let env = CanvasLayoutEnv {
        viewport_size: Some([TARGET[0] as f32, TARGET[1] as f32]),
        viewport_overrides: &empty,
        root_auto_sizes: &empty,
        design_space: false,
        auto_scale_divisor: AutoScaleDivisor::Raw,
        screen: CanvasScreenEnv::NONE,
    };
    let table = CanvasLayoutPass::run(&roots, &world, 0, CanvasParentFrame::viewport_root(CanvasDrawZone::Foreground), &env);
    let mut items = Vec::new();
    collect_canvas_id_items(&table, &roots, &world, 0, 1.0, 1.0, MC_TOTAL, &|_| None, &TextBoundsMap::new(), &mut items);
    let regions: Vec<_> = table.clip_regions.iter().map(|r| to_render_region(r, 1.0, 1.0)).collect();
    let scissors = canvas_id_scissors(&items, &regions, &overlay_view_proj(), TARGET);

    // DFS: Root 0・Panel 1・Wide 2・Outside 3（raw_id = MC_TOTAL + dfs + 1）
    let by_dfs = |dfs: u32| items.iter().position(|it| it.raw_id == MC_TOTAL + dfs + 1).expect("ID アイテムがある");
    let panel_i = by_dfs(1);
    let wide_i = by_dfs(2);
    let outside_i = by_dfs(3);
    assert_eq!(items[panel_i].clip, None, "切り抜きのノード自身（背景の板）は切らない");
    assert_eq!(scissors[panel_i], None);
    assert_eq!(items[wide_i].clip, Some(0));
    assert_eq!(scissors[wide_i], Some(ScissorRect { x: 540, y: 310, width: 200, height: 100 }), "切り抜きの矩形の画素");
    assert_eq!(scissors[outside_i], None, "切り抜きの外は切らない");
}
