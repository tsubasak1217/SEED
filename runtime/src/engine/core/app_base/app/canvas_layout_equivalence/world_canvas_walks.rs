// ============================================================
//  canvas_layout_equivalence/world_canvas_walks.rs — 3D ワールドキャンバスの子の走査 2 か所の同値の検査（W2-1b）
//
//  W2-1b で canvas_collect.rs の walk_3d_canvas_children_id（GPU の ID 描画）と collect_3d_canvas_child_outlines
//  （エディタの枠）をレイアウトの表を読む形へ寄せた。旧い走査（legacy_world_canvas.rs の写し）と、
//  ランダムな木（random_tree.rs）・ランダムなワールドキャンバスの大きさと行列で突き合わせる:
//    - CanvasTransform を持たないノード（3D アクター）の下に無いノードは、ビット単位で同じ ID アイテム・同じ枠
//    - その下のノードは、新しい走査では出ない（描画と同じ。旧い走査は素通しでたどって出していた＝意図した違い）
//    - DFS 番号のカウンタの進み方は同じ
// ============================================================

use std::collections::HashSet;
use std::sync::Arc;

use crate::engine::components::{CanvasDrawZone, CanvasTransform};
use crate::engine::core::canvas_layout::{
    AutoScaleDivisor, CanvasLayoutEnv, CanvasLayoutPass, CanvasParentFrame,
};
use crate::engine::core::renderer::sprite_skin::SkinnedSpriteDraw;
use crate::engine::core::renderer::ui_draw_order::UiDrawKind;
use crate::engine::ecs::Entity;

use super::super::canvas_collect::{collect_3d_canvas_child_outlines, walk_3d_canvas_children_id};
use super::legacy_world_canvas::{legacy_collect_3d_canvas_child_outlines, legacy_walk_3d_canvas_children_id};
use super::random_tree::{generate, Rng, TARGET_WORLD_LINE};

/// 試すシーンの数（W2-1a の同値と同じ種の範囲の前半）。
const SCENE_COUNT: u64 = 1500;
/// ID 描画の raw_id のオフセット（3D のインスタンス数の代わり）。
const MC_TOTAL: u32 = 17;
/// 最初の子の DFS 番号（ワールドキャンバス自身の次の番号の代わり）。
const FIRST_CHILD_DFS: u32 = 5;

/// ID 描画のアイテム 1 件（raw_id・行列・テクスチャ・スキン・レイヤー・種別）。
type IdItem = (u32, [[f32; 4]; 4], Option<String>, Option<Arc<SkinnedSpriteDraw>>, i32, UiDrawKind);

/// 行列をビットで比べる。
fn same_mat(a: &[[f32; 4]; 4], b: &[[f32; 4]; 4]) -> bool {
    a.iter().flatten().zip(b.iter().flatten()).all(|(x, y)| x.to_bits() == y.to_bits())
}

/// 4 隅をビットで比べる。
fn same_corners(a: &[[f32; 3]; 4], b: &[[f32; 3]; 4]) -> bool {
    a.iter().flatten().zip(b.iter().flatten()).all(|(x, y)| x.to_bits() == y.to_bits())
}

/// ID アイテムをビットで比べる。
fn same_item(a: &IdItem, b: &IdItem) -> bool {
    a.0 == b.0 && same_mat(&a.1, &b.1) && a.2 == b.2 && a.3.is_none() == b.3.is_none() && a.4 == b.4 && a.5 == b.5
}

/// 新しい表で「2D レイアウト木の外」（CanvasTransform を持たないノードの下）になる DFS 番号の集合。
fn outside_2d_tree(rs: &super::random_tree::RandomScene, size: [f32; 2], ctw: [[f32; 4]; 4]) -> HashSet<u32> {
    let empty = std::collections::HashMap::new();
    let env = CanvasLayoutEnv::without_viewport(&empty, AutoScaleDivisor::Raw);
    let table = CanvasLayoutPass::run(
        &rs.scene.actors,
        &rs.scene.world,
        TARGET_WORLD_LINE,
        CanvasParentFrame::new(Some(size), ctw, [1.0, 1.0], CanvasDrawZone::Foreground),
        &env,
    );
    table
        .nodes
        .iter()
        .enumerate()
        .filter(|(_, n)| !n.flags.in_2d_tree)
        .map(|(i, _)| FIRST_CHILD_DFS + i as u32)
        .collect()
}

/// 旧い走査と新しい走査の ID アイテム・枠を突き合わせる（1 シーン）。
///
/// # 戻り値
/// (一致を確かめた ID アイテムの数, 一致を確かめた枠の数, 意図して出なくなった ID アイテムの数)
fn check_scene(seed: u64) -> (usize, usize, usize) {
    let rs = generate(seed);
    let mut rng = Rng::new(seed.wrapping_mul(131).wrapping_add(3));
    let size = [rng.range(1.0, 2000.0), rng.range(1.0, 2000.0)];
    let ctw = CanvasTransform {
        position: [rng.range(-50.0, 50.0), rng.range(-50.0, 50.0)],
        rotation: rng.range(-180.0, 180.0),
        scale: [rng.range(0.01, 2.0), rng.range(0.01, 2.0)],
        ..CanvasTransform::default()
    }
    .to_mat4_sized(size[0], size[1]);
    let outside = outside_2d_tree(&rs, size, ctw);
    let no_skin = |_: Entity| None;

    // ── ID 描画 ──
    let mut old_items: Vec<IdItem> = Vec::new();
    let mut old_counter = FIRST_CHILD_DFS;
    legacy_walk_3d_canvas_children_id(
        &rs.scene.actors, &rs.scene.world, TARGET_WORLD_LINE, &mut old_counter,
        Some(size), ctw, [1.0, 1.0], MC_TOTAL, &no_skin, &rs.text_bounds, &mut old_items,
    );
    let mut new_items: Vec<IdItem> = Vec::new();
    let mut new_counter = FIRST_CHILD_DFS;
    walk_3d_canvas_children_id(
        &rs.scene.actors, &rs.scene.world, TARGET_WORLD_LINE, &mut new_counter,
        size, ctw, MC_TOTAL, &no_skin, &rs.text_bounds, &mut new_items,
    );
    assert_eq!(old_counter, new_counter, "seed={seed}: DFS 番号の進み方が違う");
    let dfs_of = |raw_id: u32| raw_id - MC_TOTAL - 1;
    let (kept, dropped): (Vec<IdItem>, Vec<IdItem>) =
        old_items.into_iter().partition(|it| !outside.contains(&dfs_of(it.0)));
    assert_eq!(kept.len(), new_items.len(), "seed={seed}: ID アイテムの数が違う");
    for (i, (a, b)) in kept.iter().zip(new_items.iter()).enumerate() {
        assert!(same_item(a, b), "seed={seed}: ID アイテム {i} が違う（raw_id 旧={} 新={}）", a.0, b.0);
    }

    // ── 枠 ──
    // 旧い枠の走査は世界線を見ない（呼び出し側がワールドキャンバスの子＝同じ世界線だけを渡す）ので、
    // ルートがすべて対象の世界線のシーンだけで比べる
    if rs.scene.actors.iter().any(|a| a.world_line != TARGET_WORLD_LINE) {
        return (new_items.len(), 0, dropped.len());
    }
    let roots = &rs.scene.actors;
    let mut old_outlines = Vec::new();
    let mut old_counter = FIRST_CHILD_DFS;
    legacy_collect_3d_canvas_child_outlines(
        roots, &rs.scene.world, TARGET_WORLD_LINE, &mut old_counter, Some(size), ctw, [1.0, 1.0], &mut old_outlines,
    );
    let mut new_outlines = Vec::new();
    let mut new_counter = FIRST_CHILD_DFS;
    collect_3d_canvas_child_outlines(
        roots, &rs.scene.world, TARGET_WORLD_LINE, &mut new_counter, Some(size), ctw, [1.0, 1.0], &mut new_outlines,
    );
    assert_eq!(old_counter, new_counter, "seed={seed}: 枠の DFS 番号の進み方が違う");
    let kept_outlines: Vec<_> = old_outlines.into_iter().filter(|(_, dfs)| !outside.contains(dfs)).collect();
    assert_eq!(kept_outlines.len(), new_outlines.len(), "seed={seed}: 枠の数が違う");
    for (i, (a, b)) in kept_outlines.iter().zip(new_outlines.iter()).enumerate() {
        assert!(a.1 == b.1 && same_corners(&a.0, &b.0), "seed={seed}: 枠 {i} が違う（dfs 旧={} 新={}）", a.1, b.1);
    }
    (new_items.len(), new_outlines.len(), dropped.len())
}

/// 3D ワールドキャンバスの子の走査 2 か所: 表を読む新しい走査は、旧い走査とビット単位で同じ
/// （CanvasTransform を持たないノードの下だけは描画と同じく出さない）。
#[test]
fn world_canvas_child_walks_match_legacy_on_random_trees() {
    let (mut items, mut outlines, mut dropped) = (0usize, 0usize, 0usize);
    for seed in 0..SCENE_COUNT {
        let (i, o, d) = check_scene(seed);
        items += i;
        outlines += o;
        dropped += d;
    }
    eprintln!(
        "[W2-1b 同値] 3D ワールドキャンバスの子: シーン {SCENE_COUNT} 個・ID アイテム {items} 件・枠 {outlines} 本がビット単位で一致\
         （CanvasTransform を持たないノードの下で出なくなった ID アイテム {dropped} 件は描画と同じ規則）"
    );
    assert!(items > 0 && outlines > 0 && dropped > 0, "比べる物・違いの出る木の両方が生成されていること");
}
