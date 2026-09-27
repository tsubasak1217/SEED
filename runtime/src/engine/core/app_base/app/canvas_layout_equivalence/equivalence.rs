// ============================================================
//  canvas_layout_equivalence/equivalence.rs — 旧 5 か所 == 新しい表（ビット単位）の性質テスト
//
//  ランダムなシーンごとに次を突き合わせる（旧実装は legacy.rs の写し、新しい実装は本番のコード）:
//    1. 描画（collect_sprite_items）… 各ノードで描画が読むレイアウトの値（親の行列・有効トランスフォーム・
//       サイズ倍率・ゾーン）と、フォルダの座標空間。2D キャンバスと 3D ワールドキャンバスの子の 2 つの文脈
//    2. キャンバス枠（collect_canvas_rects）… LineBatch の頂点（スクリーンスペースとワールドスペース）
//    3. ID 描画（collect_canvas_id_items）… raw_id・行列・テクスチャ・ゾーン・レイヤー・種別
//    4. 当たり判定（walk_pick_candidates_2d）… 探りの点ごとの候補（エディタの選択とポインタイベント）
//    5. 2D 物理（collect_actor2d_contexts）… 全フィールド
//  浮動小数は to_bits で比べる（-0.0 と 0.0 も区別する）。1 か所でも違えば種とノードを出して落ちる。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::{CanvasDrawZone, CanvasTransform, ComponentKind, SpriteComponent};
use crate::engine::core::canvas_layout::{
    AutoScaleDivisor, CanvasLayoutEnv, CanvasLayoutPass, CanvasLayoutTable, CanvasParentFrame,
    IDENTITY_MAT4,
};
use crate::engine::ecs::Entity;
use crate::engine::methods::drawer::LineBatch;
use crate::engine::methods::gizmo_interact::mat4x4_mul;

use super::super::canvas_collect::{
    collect_canvas_id_items, collect_canvas_rects, drawn_nodes, CanvasIdItem, DrawnNode,
};
use super::super::physics2d_ops::{collect_actor2d_contexts, Actor2dPhysicsCtx};
use super::super::pick_2d::{walk_pick_candidates_2d, PickCand2d, PickFilter2d, PickKind2d};
use super::legacy::{
    legacy_collect_actor2d_contexts, legacy_collect_canvas_id_items, legacy_collect_canvas_rects,
    legacy_sprite_trace, legacy_walk_pick_candidates_2d, SpriteTraceEntry,
};
use super::random_tree::{generate, RandomScene, Rng, TARGET_WORLD_LINE};

/// 試すシーンの数（種 0..SCENE_COUNT）。
const SCENE_COUNT: u64 = 3000;
/// 1 シーンあたりの乱数の探りの点の数（スプライトの中心・隅の近くの点とは別）。
const RANDOM_PROBES_PER_SCENE: usize = 12;
/// 探りの点を散らす範囲（キャンバス px。ビューポート中心原点・左上原点の両方を覆う）。
const PROBE_RANGE: (f32, f32) = (-1500.0, 2600.0);
/// スプライトの隅から内外へずらす量（px。境界の内外の判定を通す）。
const EDGE_NUDGE: f32 = 0.5;
/// ワールドスペースのキャンバス px → ワールドの倍率（frame_renderer の CANVAS_WORLD_SCALE と同じ）。
const WORLD_SPACE_SCALE: f32 = 1.0 / 100.0;
/// キャンバス枠の色（何でもよい。旧実装と同じ値を渡すだけ）。
const OUTLINE_COL: [f32; 4] = [0.85, 0.95, 1.0, 0.9];
/// キャンバス枠のリング間隔。
const OUTLINE_STEP: f32 = 1.0;
/// ID 描画の raw_id のオフセット（3D のインスタンス数の代わり）。
const MC_TOTAL: u32 = 17;

/// f32 をビットで比べる。
fn same_f32(a: f32, b: f32) -> bool {
    a.to_bits() == b.to_bits()
}

/// [f32; N] をビットで比べる。
fn same_arr<const N: usize>(a: &[f32; N], b: &[f32; N]) -> bool {
    a.iter().zip(b.iter()).all(|(x, y)| same_f32(*x, *y))
}

/// 4×4 行列をビットで比べる。
fn same_mat(a: &[[f32; 4]; 4], b: &[[f32; 4]; 4]) -> bool {
    a.iter().zip(b.iter()).all(|(x, y)| same_arr(x, y))
}

/// CanvasTransform をビットで比べる。
fn same_ct(a: &CanvasTransform, b: &CanvasTransform) -> bool {
    same_arr(&a.position, &b.position)
        && same_f32(a.rotation, b.rotation)
        && same_arr(&a.scale, &b.scale)
        && same_arr(&a.pivot, &b.pivot)
        && same_arr(&a.anchor, &b.anchor)
        && a.scale_transform == b.scale_transform
        && a.scale_size == b.scale_size
        && a.keep_aspect_ratio == b.keep_aspect_ratio
        && a.aspect_ratio_axis == b.aspect_ratio_axis
}

/// 描画の記録 1 件をビットで比べる。
fn same_trace(a: &SpriteTraceEntry, b: &SpriteTraceEntry) -> bool {
    match (a, b) {
        (
            SpriteTraceEntry::Folder { entity: ea, parent_world_rs: ma, zone: za },
            SpriteTraceEntry::Folder { entity: eb, parent_world_rs: mb, zone: zb },
        ) => ea == eb && same_mat(ma, mb) && za == zb,
        (
            SpriteTraceEntry::Placed { entity: ea, parent_world_rs: ma, eff_ct: ca, size_scale: sa, zone: za },
            SpriteTraceEntry::Placed { entity: eb, parent_world_rs: mb, eff_ct: cb, size_scale: sb, zone: zb },
        ) => ea == eb && same_mat(ma, mb) && same_ct(ca, cb) && same_arr(sa, sb) && za == zb,
        _ => false,
    }
}

/// 新しい実装の描画が読む値を、旧実装の記録と同じ形にする（本番の `drawn_nodes` を通す）。
fn new_sprite_trace(table: &CanvasLayoutTable, rs: &RandomScene) -> Vec<SpriteTraceEntry> {
    drawn_nodes(table, &rs.scene.actors)
        .map(|drawn| match drawn {
            DrawnNode::Folder { actor, frame, .. } => SpriteTraceEntry::Folder {
                entity: actor.entity,
                parent_world_rs: frame.world_rs,
                zone: frame.zone,
            },
            DrawnNode::Placed { actor, frame, placement, .. } => SpriteTraceEntry::Placed {
                entity: actor.entity,
                parent_world_rs: frame.world_rs,
                eff_ct: placement.eff_transform.clone(),
                size_scale: placement.size_scale,
                zone: placement.zone,
            },
        })
        .collect()
}

/// シーンの文脈で表を作る（描画・枠・ID 描画は Raw、当たり判定・物理は GuardEpsilon）。
fn build_table(rs: &RandomScene, frame: CanvasParentFrame, divisor: AutoScaleDivisor) -> CanvasLayoutTable {
    let env = CanvasLayoutEnv {
        viewport_size: rs.viewport,
        viewport_overrides: &rs.overrides,
        root_auto_sizes: &rs.root_auto,
        design_space: rs.design_space,
        auto_scale_divisor: divisor,
        // W2-1a の同値は画面の情報を使わない文脈で確かめる（dp・安全領域の部品を使わない木では結果に効かない）
        screen: crate::engine::core::canvas_layout::CanvasScreenEnv::NONE,
    };
    CanvasLayoutPass::run(&rs.scene.actors, &rs.scene.world, TARGET_WORLD_LINE, frame, &env)
}

/// 描画の突き合わせ（2D キャンバスの最上位の文脈）。
fn check_render(seed: u64, rs: &RandomScene) -> usize {
    let mut legacy = Vec::new();
    legacy_sprite_trace(
        &rs.scene.actors,
        &rs.scene.world,
        TARGET_WORLD_LINE,
        None,
        IDENTITY_MAT4,
        [1.0, 1.0],
        rs.viewport,
        &rs.overrides,
        &rs.root_auto,
        CanvasDrawZone::Foreground,
        rs.design_space,
        &mut legacy,
    );
    let table = build_table(rs, CanvasParentFrame::viewport_root(CanvasDrawZone::Foreground), AutoScaleDivisor::Raw);
    let new = new_sprite_trace(&table, rs);
    assert_eq!(legacy.len(), new.len(), "seed={seed}: 描画するノードの数が違う");
    for (i, (a, b)) in legacy.iter().zip(new.iter()).enumerate() {
        assert!(same_trace(a, b), "seed={seed}: 描画の {i} 番目が違う\n旧={a:?}\n新={b:?}");
    }
    legacy.len()
}

/// 描画の突き合わせ（3D ワールドキャンバスの子の文脈: 親の大きさが Some・親の行列が任意）。
fn check_render_world_canvas(seed: u64, rs: &RandomScene) -> usize {
    let mut rng = Rng::new(seed.wrapping_mul(31).wrapping_add(7));
    let size = [rng.range(1.0, 2000.0), rng.range(1.0, 2000.0)];
    let parent = CanvasTransform {
        position: [rng.range(-50.0, 50.0), rng.range(-50.0, 50.0)],
        rotation: rng.range(-180.0, 180.0),
        scale: [rng.range(0.01, 2.0), rng.range(0.01, 2.0)],
        ..CanvasTransform::default()
    }
    .to_mat4_sized(size[0], size[1]);
    let mut legacy = Vec::new();
    let empty: HashMap<Entity, [f32; 2]> = HashMap::new();
    legacy_sprite_trace(
        &rs.scene.actors,
        &rs.scene.world,
        TARGET_WORLD_LINE,
        Some(size),
        parent,
        [1.0, 1.0],
        None,
        &empty,
        &empty,
        CanvasDrawZone::Foreground,
        false,
        &mut legacy,
    );
    let env = CanvasLayoutEnv::without_viewport(&empty, AutoScaleDivisor::Raw);
    let table = CanvasLayoutPass::run(
        &rs.scene.actors,
        &rs.scene.world,
        TARGET_WORLD_LINE,
        CanvasParentFrame::new(Some(size), parent, [1.0, 1.0], CanvasDrawZone::Foreground),
        &env,
    );
    let new = new_sprite_trace(&table, rs);
    assert_eq!(legacy.len(), new.len(), "seed={seed}: ワールドキャンバスの描画ノードの数が違う");
    for (i, (a, b)) in legacy.iter().zip(new.iter()).enumerate() {
        assert!(same_trace(a, b), "seed={seed}: ワールドキャンバスの描画の {i} 番目が違う\n旧={a:?}\n新={b:?}");
    }
    legacy.len()
}

/// キャンバス枠の突き合わせ（スクリーンスペースとワールドスペース）。
fn check_rects(seed: u64, rs: &RandomScene) -> usize {
    let table = build_table(rs, CanvasParentFrame::viewport_root(CanvasDrawZone::Foreground), AutoScaleDivisor::Raw);
    let mut lines = 0;
    for (canvas_scale, y_sign) in [(1.0f32, 1.0f32), (WORLD_SPACE_SCALE, -1.0)] {
        let mut lb_old = LineBatch::new();
        let mut counter = 0u32;
        legacy_collect_canvas_rects(
            &rs.scene.actors,
            &rs.scene.world,
            TARGET_WORLD_LINE,
            &mut lb_old,
            OUTLINE_COL,
            &rs.selected,
            &mut counter,
            None,
            IDENTITY_MAT4,
            [1.0, 1.0],
            canvas_scale,
            y_sign,
            rs.viewport,
            &rs.overrides,
            &rs.root_auto,
            rs.design_space,
            OUTLINE_STEP,
            None,
            &rs.text_bounds,
        );
        let mut lb_new = LineBatch::new();
        collect_canvas_rects(
            &table,
            &rs.scene.actors,
            &rs.scene.world,
            &mut lb_new,
            OUTLINE_COL,
            &rs.selected,
            0,
            canvas_scale,
            y_sign,
            OUTLINE_STEP,
            None,
            &rs.text_bounds,
        );
        let (a, b) = (lb_old.vertices(), lb_new.vertices());
        assert_eq!(a.len(), b.len(), "seed={seed}: 枠の頂点の数が違う（scale={canvas_scale}）");
        for (i, (va, vb)) in a.iter().zip(b.iter()).enumerate() {
            assert!(
                same_arr(&va.position, &vb.position) && same_arr(&va.color, &vb.color),
                "seed={seed}: 枠の頂点 {i} が違う 旧={va:?} 新={vb:?}"
            );
        }
        lines += a.len() / 2;
    }
    lines
}

/// ID 描画のアイテムをビットで比べる。
fn same_id_item(a: &CanvasIdItem, b: &CanvasIdItem) -> bool {
    a.raw_id == b.raw_id
        && same_mat(&a.model, &b.model)
        && a.tex_path == b.tex_path
        && a.mesh.is_none() == b.mesh.is_none()
        && a.zone == b.zone
        && a.layer == b.layer
        && a.kind == b.kind
}

/// ID 描画の突き合わせ（スクリーンスペースとワールドスペース）。
fn check_id_items(seed: u64, rs: &RandomScene) -> usize {
    let table = build_table(rs, CanvasParentFrame::viewport_root(CanvasDrawZone::Foreground), AutoScaleDivisor::Raw);
    let no_skin = |_: Entity| None;
    let mut count = 0;
    for (canvas_scale, y_sign) in [(1.0f32, 1.0f32), (WORLD_SPACE_SCALE, -1.0)] {
        let mut old = Vec::new();
        let mut counter = 0u32;
        legacy_collect_canvas_id_items(
            &rs.scene.actors,
            &rs.scene.world,
            TARGET_WORLD_LINE,
            &mut counter,
            None,
            IDENTITY_MAT4,
            [1.0, 1.0],
            canvas_scale,
            y_sign,
            rs.viewport,
            &rs.overrides,
            &rs.root_auto,
            MC_TOTAL,
            CanvasDrawZone::Foreground,
            true,
            rs.design_space,
            &no_skin,
            &rs.text_bounds,
            &mut old,
        );
        let mut new = Vec::new();
        collect_canvas_id_items(
            &table,
            &rs.scene.actors,
            &rs.scene.world,
            0,
            canvas_scale,
            y_sign,
            MC_TOTAL,
            &no_skin,
            &rs.text_bounds,
            &mut new,
        );
        assert_eq!(old.len(), new.len(), "seed={seed}: ID アイテムの数が違う");
        for (i, (a, b)) in old.iter().zip(new.iter()).enumerate() {
            assert!(same_id_item(a, b), "seed={seed}: ID アイテム {i} が違う（raw_id 旧={} 新={}）", a.raw_id, b.raw_id);
        }
        count += old.len();
    }
    count
}

/// 当たり判定の候補をビットで比べる（候補は整数・列挙だけ）。
fn same_cand(a: &PickCand2d, b: &PickCand2d) -> bool {
    a.dfs == b.dfs
        && a.entity == b.entity
        && a.kind == b.kind
        && a.zone == b.zone
        && a.depth == b.depth
        && a.layer == b.layer
}

/// 探りの点（スプライトの中心・隅の内外と、乱数の点）。
fn probe_points(seed: u64, rs: &RandomScene) -> Vec<[f32; 2]> {
    let mut points = Vec::new();
    let mut trace = Vec::new();
    legacy_sprite_trace(
        &rs.scene.actors,
        &rs.scene.world,
        TARGET_WORLD_LINE,
        None,
        IDENTITY_MAT4,
        [1.0, 1.0],
        rs.viewport,
        &rs.overrides,
        &rs.root_auto,
        CanvasDrawZone::Foreground,
        rs.design_space,
        &mut trace,
    );
    let actors_by_entity = collect_actors(&rs.scene.actors);
    for entry in &trace {
        let SpriteTraceEntry::Placed { entity, parent_world_rs, eff_ct, size_scale, .. } = entry else {
            continue;
        };
        let Some(actor) = actors_by_entity.get(entity) else { continue };
        for slot in actor.slots() {
            if slot.kind != ComponentKind::Sprite {
                continue;
            }
            let Some(sc) = rs.scene.world.get::<SpriteComponent>(slot.entity) else { continue };
            let (w, h) = (sc.width * size_scale[0], sc.height * size_scale[1]);
            let m = mat4x4_mul(*parent_world_rs, eff_ct.to_mat4_sized(w, h));
            let at = |lx: f32, ly: f32| [m[0][0] * lx + m[0][1] * ly + m[0][3], m[1][0] * lx + m[1][1] * ly + m[1][3]];
            points.push(at(w * 0.5, h * 0.5));
            for (lx, ly) in [(0.0, 0.0), (w, 0.0), (0.0, h), (w, h)] {
                let c = at(lx, ly);
                points.push([c[0] + EDGE_NUDGE, c[1] + EDGE_NUDGE]);
                points.push([c[0] - EDGE_NUDGE, c[1] - EDGE_NUDGE]);
            }
        }
    }
    let mut rng = Rng::new(seed ^ 0xABCD_EF01);
    for _ in 0..RANDOM_PROBES_PER_SCENE {
        points.push([rng.range(PROBE_RANGE.0, PROBE_RANGE.1), rng.range(PROBE_RANGE.0, PROBE_RANGE.1)]);
    }
    points
}

/// entity → アクター（子孫を含む）。
fn collect_actors(
    actors: &[crate::engine::structs::objects::Actor],
) -> HashMap<Entity, &crate::engine::structs::objects::Actor> {
    let mut map = HashMap::new();
    let mut stack: Vec<&crate::engine::structs::objects::Actor> = actors.iter().collect();
    while let Some(a) = stack.pop() {
        map.insert(a.entity, a);
        stack.extend(a.children.iter());
    }
    map
}

/// 当たり判定の突き合わせ（エディタの選択とポインタイベント）。
///
/// # 戻り値
/// (探った点の数, 候補の総数)
fn check_pick(seed: u64, rs: &RandomScene) -> (usize, usize) {
    let mesh_of = |_: &str| None;
    let points = probe_points(seed, rs);
    let mut hits = 0;
    for point in &points {
        for filter in [PickFilter2d::EDITOR_SELECT, PickFilter2d::POINTER_EVENT] {
            let mut old = Vec::new();
            let mut counter_old = 0u32;
            legacy_walk_pick_candidates_2d(
                &rs.scene.actors,
                &rs.scene.world,
                TARGET_WORLD_LINE,
                point[0],
                point[1],
                &mut counter_old,
                IDENTITY_MAT4,
                [1.0, 1.0],
                None,
                0,
                CanvasDrawZone::Foreground,
                rs.viewport,
                &rs.overrides,
                &rs.root_auto,
                rs.design_space,
                &mesh_of,
                &rs.text_bounds,
                filter,
                &mut old,
            );
            let mut new = Vec::new();
            let mut counter_new = 0u32;
            walk_pick_candidates_2d(
                &rs.scene.actors,
                &rs.scene.world,
                TARGET_WORLD_LINE,
                point[0],
                point[1],
                &mut counter_new,
                IDENTITY_MAT4,
                [1.0, 1.0],
                None,
                0,
                CanvasDrawZone::Foreground,
                rs.viewport,
                &rs.overrides,
                &rs.root_auto,
                rs.design_space,
                &mesh_of,
                &rs.text_bounds,
                filter,
                &mut new,
            );
            assert_eq!(old.len(), new.len(), "seed={seed}: 点 {point:?} の候補の数が違う");
            for (a, b) in old.iter().zip(new.iter()) {
                assert!(
                    same_cand(a, b),
                    "seed={seed}: 点 {point:?} の候補が違う 旧=(dfs {} depth {} layer {}) 新=(dfs {} depth {} layer {})",
                    a.dfs, a.depth, a.layer, b.dfs, b.depth, b.layer
                );
                debug_assert!(matches!(a.kind, PickKind2d::Sprite | PickKind2d::Canvas));
            }
            assert_eq!(counter_old, counter_new, "seed={seed}: DFS の数え上げが違う");
            hits += old.len();
        }
    }
    (points.len(), hits)
}

/// 物理のコンテキストをビットで比べる。
fn same_physics(a: &Actor2dPhysicsCtx, b: &Actor2dPhysicsCtx) -> bool {
    a.dfs_id == b.dfs_id
        && a.actor_entity == b.actor_entity
        && same_arr(&a.body_pos_px, &b.body_pos_px)
        && same_arr(&a.pivot_world_px, &b.pivot_world_px)
        && same_f32(a.rot_rad, b.rot_rad)
        && same_arr(&a.scale, &b.scale)
        && same_arr(&a.anchor_off, &b.anchor_off)
        && a.sm_transform == b.sm_transform
        && same_arr(&a.cumul_scale, &b.cumul_scale)
        && same_arr(&a.pivot_corr_local, &b.pivot_corr_local)
        && same_f32(a.size_sx, b.size_sx)
        && same_f32(a.size_sy, b.size_sy)
        && a.collider_slot_entity == b.collider_slot_entity
        && same_arr(&a.parent_canvas_origin, &b.parent_canvas_origin)
        && same_f32(a.parent_world_rot, b.parent_world_rot)
}

/// 2D 物理の突き合わせ。
fn check_physics(seed: u64, rs: &RandomScene) -> usize {
    let old = legacy_collect_actor2d_contexts(
        &rs.scene,
        TARGET_WORLD_LINE,
        rs.viewport,
        &rs.overrides,
        &rs.root_auto,
        rs.design_space,
    );
    let new = collect_actor2d_contexts(
        &rs.scene,
        TARGET_WORLD_LINE,
        rs.viewport,
        &rs.overrides,
        &rs.root_auto,
        rs.design_space,
    );
    assert_eq!(old.len(), new.len(), "seed={seed}: 物理コンテキストの数が違う");
    for (i, (a, b)) in old.iter().zip(new.iter()).enumerate() {
        assert!(
            same_physics(a, b),
            "seed={seed}: 物理コンテキスト {i}（dfs {}）が違う 旧 body={:?} pivot={:?} anchor={:?} 新 body={:?} pivot={:?} anchor={:?}",
            a.dfs_id, a.body_pos_px, a.pivot_world_px, a.anchor_off, b.body_pos_px, b.pivot_world_px, b.anchor_off
        );
    }
    old.len()
}

/// 旧 5 か所と新しい表が、ランダムな木でビット単位に一致する（W2-1a の同値の保証）。
#[test]
fn legacy_walks_equal_layout_table_on_random_trees() {
    let (mut nodes, mut drawn, mut drawn_world, mut rect_lines, mut ids, mut probes, mut hits, mut phys) =
        (0usize, 0usize, 0usize, 0usize, 0usize, 0usize, 0usize, 0usize);
    for seed in 0..SCENE_COUNT {
        let rs = generate(seed);
        nodes += count(&rs.scene.actors);
        drawn += check_render(seed, &rs);
        drawn_world += check_render_world_canvas(seed, &rs);
        rect_lines += check_rects(seed, &rs);
        ids += check_id_items(seed, &rs);
        let (p, h) = check_pick(seed, &rs);
        probes += p;
        hits += h;
        phys += check_physics(seed, &rs);
    }
    eprintln!(
        "[W2-1a 同値] シーン {SCENE_COUNT} 個・ノード {nodes} 個: 描画のノード {drawn}（ワールドキャンバスの文脈 {drawn_world}）・\
         枠の線 {rect_lines} 本・ID アイテム {ids} 件・当たり判定の探り {probes} 点×2 経路（候補 {hits} 件）・物理コンテキスト {phys} 件 — すべてビット単位で一致"
    );
    // 生成が偏って「何も比べていない」にならないことの確認
    assert!(drawn > 0 && rect_lines > 0 && ids > 0 && hits > 0 && phys > 0);
}

/// ノードの数（子は世界線を問わず数える）。
fn count(actors: &[crate::engine::structs::objects::Actor]) -> usize {
    actors.iter().map(|a| 1 + count(&a.children)).sum()
}
