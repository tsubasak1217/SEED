// ============================================================
//  actor_utils.rs — アクターツリー・ユーティリティ関数群
//
//  【含む処理】
//  - ヒエラルキー JSON 生成（collect_actor_nodes / build_hierarchy_json）
//  - DFS 探索（find_actor_by_dfs* / remove_actor_by_dfs* / extract_actor_by_dfs*）
//  - Canvas ユーティリティ（canvas_anchor_offset_for_dfs / collect_canvas_actors_in_rect）
//  - MC 収集・バッチ更新（collect_mcs_in_world_line / update_all_mc_batches_for_wl）
//  - エンティティ管理（collect_entities_for_wl / despawn_actor_recursive）
//  - 座標・選択ユーティリティ（world_to_screen / selection_centroid）
//  - ドラッグ時子アクター収集（collect_child_actor_mc_starts / apply_delta_to_actor_children）
//
//  Windows カーソルロック系 → platform_utils.rs
// ============================================================


use std::collections::{HashMap, HashSet};

use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;
use crate::engine::components::{
    ModelComponent, Transform as ActorTransform, ComponentKind,
    CanvasTransform, CanvasComponent,
};
use crate::engine::structs::tensor::{Vector4, Mat4x4};
use crate::engine::methods::gizmo_interact::mat4x4_mul;

// ============================================================
//  ヒエラルキーユーティリティ
// ============================================================

/// Actor ツリーを DFS 順にフラット化し (id, name, parent_id) を収集する。
pub(super) fn collect_actor_nodes(
    actor:   &Actor,
    parent:  Option<u32>,
    counter: &mut u32,
    out:     &mut Vec<(u32, String, Option<u32>)>,
) {
    let id = *counter;
    *counter += 1;
    out.push((id, actor.name.clone(), parent));
    for child in actor.children() {
        collect_actor_nodes(child, Some(id), counter, out);
    }
}

/// ヒエラルキー JSON 1 ノード分のシリアライズ用構造体。
#[derive(serde::Serialize)]
struct HierarchyNode<'a> {
    id:       u32,
    name:     &'a str,
    parent:   Option<u32>,
    is_group: bool,
}

/// フラットリストから HIERARCHY JSON を生成する。
pub(super) fn build_hierarchy_json(nodes: &[(u32, String, Option<u32>)]) -> String {
    let items: Vec<HierarchyNode<'_>> = nodes
        .iter()
        .map(|(id, name, parent)| HierarchyNode {
            id:       *id,
            name:     name.as_str(),
            parent:   *parent,
            is_group: false,
        })
        .collect();
    serde_json::to_string(&items).unwrap_or_default()
}

// ============================================================
//  DFS 探索（可変参照）
// ============================================================

/// DFS id でアクターへの可変参照を取得する。
pub(super) fn find_actor_by_dfs_mut<'a>(
    actors:  &'a mut Vec<Actor>,
    wl:      u32,
    dfs_id:  u32,
    counter: &mut u32,
) -> Option<&'a mut Actor> {
    for actor in actors.iter_mut() {
        if actor.world_line != wl { continue; }
        if *counter == dfs_id { return Some(actor); }
        *counter += 1;
        if let Some(found) = find_actor_child_by_dfs_mut(actor, dfs_id, counter) {
            return Some(found);
        }
    }
    None
}

/// find_actor_by_dfs_mut の再帰実装（子ノード用）。
fn find_actor_child_by_dfs_mut<'a>(
    actor:   &'a mut Actor,
    dfs_id:  u32,
    counter: &mut u32,
) -> Option<&'a mut Actor> {
    for child in actor.children_mut().iter_mut() {
        if *counter == dfs_id { return Some(child); }
        *counter += 1;
        if let Some(found) = find_actor_child_by_dfs_mut(child, dfs_id, counter) {
            return Some(found);
        }
    }
    None
}

// ============================================================
//  DFS 探索（不変参照）
// ============================================================

/// DFS id でアクターへの不変参照を取得する。
pub(super) fn find_actor_by_dfs<'a>(
    actors:  &'a Vec<Actor>,
    wl:      u32,
    dfs_id:  u32,
    counter: &mut u32,
) -> Option<&'a Actor> {
    for actor in actors.iter() {
        if actor.world_line != wl { continue; }
        if *counter == dfs_id { return Some(actor); }
        *counter += 1;
        if let Some(found) = find_actor_child_by_dfs(actor, dfs_id, counter) {
            return Some(found);
        }
    }
    None
}

/// find_actor_by_dfs の再帰実装（子ノード用）。
fn find_actor_child_by_dfs<'a>(
    actor:   &'a Actor,
    dfs_id:  u32,
    counter: &mut u32,
) -> Option<&'a Actor> {
    for child in actor.children().iter() {
        if *counter == dfs_id { return Some(child); }
        *counter += 1;
        if let Some(found) = find_actor_child_by_dfs(child, dfs_id, counter) {
            return Some(found);
        }
    }
    None
}

// ============================================================
//  インスタンスインデックス → DFS id 変換
// ============================================================

/// インスタンスインデックスからそのインスタンスを持つアクターの DFS インデックスを返す。
#[allow(dead_code)]
pub(super) fn find_actor_dfs_by_instance(
    actors:       &[Actor],
    world:        &World,
    wl:           u32,
    instance_idx: u32,
) -> Option<u32> {
    let mut counter = 0u32;
    for root in actors.iter().filter(|a| a.world_line == wl) {
        if let Some(dfs) = find_actor_dfs_by_instance_in(root, world, instance_idx, &mut counter) {
            return Some(dfs);
        }
    }
    None
}

/// find_actor_dfs_by_instance の再帰実装。
fn find_actor_dfs_by_instance_in(
    actor:        &Actor,
    world:        &World,
    instance_idx: u32,
    counter:      &mut u32,
) -> Option<u32> {
    let my_dfs = *counter;
    *counter += 1;
    // スロット専用 entity の全 MC インスタンス数を合算して判定する
    let total: usize = actor.slots().iter()
        .filter(|s| s.kind == ComponentKind::Model)
        .filter_map(|s| world.get::<ModelComponent>(s.entity))
        .map(|mc| mc.instance_mats.len())
        .sum();
    if (instance_idx as usize) < total {
        return Some(my_dfs);
    }
    for child in actor.children() {
        if let Some(dfs) = find_actor_dfs_by_instance_in(child, world, instance_idx, counter) {
            return Some(dfs);
        }
    }
    None
}

// ============================================================
//  Canvas アンカー・矩形選択ユーティリティ
// ============================================================

/// 2D キャンバスモード用: 指定 DFS ID のアクターに適用すべきアンカーオフセットを返す。
///
/// アンカーオフセット = 親 CanvasComponent サイズ × CanvasTransform.anchor。
/// ルートアクター（親なし）または CanvasComponent を持たない親の場合は [0.0, 0.0] を返す。
/// render.rs の collect_sprite_items / collect_canvas_rects と同じロジックで計算する。
pub(super) fn canvas_anchor_offset_for_dfs(
    actors:     &[Actor],
    world:      &World,
    wl:         u32,
    target_dfs: u32,
) -> [f32; 2] {
    let mut counter = 0u32;
    for actor in actors.iter() {
        if actor.world_line != wl { continue; }
        // ルートアクター自身が target の場合はアンカー適用なし
        if counter == target_dfs { return [0.0, 0.0]; }
        counter += 1;
        // このアクターの CanvasComponent サイズを子アクターへ渡す
        let canvas_size = actor.slots().iter()
            .filter(|s| s.kind == ComponentKind::Canvas)
            .find_map(|s| world.get::<CanvasComponent>(s.entity))
            .map(|cc| [cc.width, cc.height]);
        if let Some(off) = find_canvas_anchor_in_children(actor, world, target_dfs, &mut counter, canvas_size) {
            return off;
        }
    }
    [0.0, 0.0]
}

/// canvas_anchor_offset_for_dfs の再帰実装（子ノード探索）。
fn find_canvas_anchor_in_children(
    parent:             &Actor,
    world:              &World,
    target_dfs:         u32,
    counter:            &mut u32,
    parent_canvas_size: Option<[f32; 2]>,
) -> Option<[f32; 2]> {
    for child in parent.children().iter() {
        if *counter == target_dfs {
            // ターゲットが見つかった。親の Canvas サイズ × anchor でオフセットを計算する。
            let offset = if let Some([pw, ph]) = parent_canvas_size {
                world.get::<CanvasTransform>(child.entity)
                    .map(|ct| [pw * ct.anchor[0], ph * ct.anchor[1]])
                    .unwrap_or([0.0, 0.0])
            } else {
                [0.0, 0.0]
            };
            return Some(offset);
        }
        *counter += 1;
        // 子の CanvasComponent サイズを孫へ渡す
        let child_canvas_size = child.slots().iter()
            .filter(|s| s.kind == ComponentKind::Canvas)
            .find_map(|s| world.get::<CanvasComponent>(s.entity))
            .map(|cc| [cc.width, cc.height]);
        if let Some(off) = find_canvas_anchor_in_children(child, world, target_dfs, counter, child_canvas_size) {
            return Some(off);
        }
    }
    None
}

/// 2D キャンバスモードの矩形選択用: CanvasTransform を持つアクタを DFS 順に走査し、
/// ワールド矩形 [wx_min, wx_max] × [wy_min, wy_max] 内の DFS ID を result に追加する。
pub(super) fn collect_canvas_actors_in_rect(
    actor:   &Actor,
    world:   &World,
    counter: &mut u32,
    wx_min: f32, wx_max: f32,
    wy_min: f32, wy_max: f32,
    result:  &mut Vec<usize>,
) {
    let dfs_id = *counter as usize;
    *counter += 1;
    if let Some(ct) = world.get::<CanvasTransform>(actor.entity) {
        let [px, py] = ct.position;
        if px >= wx_min && px <= wx_max && py >= wy_min && py <= wy_max {
            if !result.contains(&dfs_id) { result.push(dfs_id); }
        }
    }
    for child in actor.children() {
        collect_canvas_actors_in_rect(child, world, counter, wx_min, wx_max, wy_min, wy_max, result);
    }
}

// ============================================================
//  MC 収集・バッチ更新
// ============================================================

/// world_line の全アクターから ModelComponent を DFS 順で収集する（不変参照版）。
///
/// 戻り値: Vec<(id_base, dfs_id, slot_i, &ModelComponent)>
///   id_base … この MC の先頭インスタンス ID（ID パスのピック計算に使う）
///   dfs_id  … アクターの DFS 順番号
///   slot_i  … このアクターの Model スロット内連番（複数 MC の区別に使う）
pub(super) fn collect_mcs_in_world_line<'a>(
    actors: &'a [Actor],
    world:  &'a World,
    wl:     u32,
) -> Vec<(u32, u32, usize, &'a ModelComponent)> {
    let mut result  = Vec::new();
    let mut base    = 0u32;
    let mut counter = 0u32;
    for root in actors.iter().filter(|a| a.world_line == wl) {
        collect_mcs_in_actor(root, world, &mut counter, &mut base, &mut result);
    }
    result
}

/// collect_mcs_in_world_line の再帰実装。
fn collect_mcs_in_actor<'a>(
    actor:   &'a Actor,
    world:   &'a World,
    counter: &mut u32,
    base:    &mut u32,
    result:  &mut Vec<(u32, u32, usize, &'a ModelComponent)>,
) {
    let dfs = *counter;
    *counter += 1;
    // スロット専用 entity から ModelComponent を収集する（複数スロット対応）
    for (slot_i, slot) in actor.slots().iter()
        .filter(|s| s.kind == ComponentKind::Model)
        .enumerate()
    {
        if let Some(mc) = world.get::<ModelComponent>(slot.entity) {
            result.push((*base, dfs, slot_i, mc));
            *base += mc.instance_mats.len() as u32;
        }
    }
    for child in actor.children() {
        collect_mcs_in_actor(child, world, counter, base, result);
    }
}

/// world_line の全アクターの MC バッチを DFS 順で更新する（可変参照版）。
pub(super) fn update_all_mc_batches_for_wl(
    actors:         &mut Vec<Actor>,
    world:          &mut World,
    wl:             u32,
    queue:          &wgpu::Queue,
    frustum_planes: &[[f32; 4]; 6],
    camera_pos:     [f32; 3],
    anim_time:      f32,
) {
    for actor in actors.iter_mut().filter(|a| a.world_line == wl) {
        update_mc_batch_recursive(actor, world, queue, frustum_planes, camera_pos, anim_time);
    }
}

/// update_all_mc_batches_for_wl の再帰実装。
fn update_mc_batch_recursive(
    actor:          &mut Actor,
    world:          &mut World,
    queue:          &wgpu::Queue,
    frustum_planes: &[[f32; 4]; 6],
    camera_pos:     [f32; 3],
    anim_time:      f32,
) {
    // スロット専用 entity の全 ModelComponent バッチを更新する（複数スロット対応）
    let slot_entities: Vec<Entity> = actor.slots().iter()
        .filter(|s| s.kind == ComponentKind::Model)
        .map(|s| s.entity)
        .collect();
    for slot_entity in slot_entities {
        if let Some(mc) = world.get_mut::<ModelComponent>(slot_entity) {
            if let (Some(batch), Some(model)) = (&mut mc.instanced_batch, mc.model.as_deref()) {
                batch.update(queue, model, &mc.instance_mats, frustum_planes, camera_pos, anim_time);
            }
        }
    }
    for child in actor.children_mut().iter_mut() {
        update_mc_batch_recursive(child, world, queue, frustum_planes, camera_pos, anim_time);
    }
}

// ============================================================
//  アクター削除・サイズ計算
// ============================================================

/// DFS id でアクターを削除する。
pub(super) fn remove_actor_by_dfs(
    actors:  &mut Vec<Actor>,
    wl:      u32,
    dfs_id:  u32,
    counter: &mut u32,
) -> bool {
    let mut i = 0;
    while i < actors.len() {
        if actors[i].world_line != wl { i += 1; continue; }
        if *counter == dfs_id { actors.remove(i); return true; }
        *counter += 1;
        if remove_actor_children_by_dfs(&mut actors[i], dfs_id, counter) { return true; }
        i += 1;
    }
    false
}

/// remove_actor_by_dfs の再帰実装（子ノード用）。
fn remove_actor_children_by_dfs(actor: &mut Actor, dfs_id: u32, counter: &mut u32) -> bool {
    let mut i = 0;
    while i < actor.children_mut().len() {
        if *counter == dfs_id { actor.children_mut().remove(i); return true; }
        *counter += 1;
        if remove_actor_children_by_dfs(&mut actor.children_mut()[i], dfs_id, counter) { return true; }
        i += 1;
    }
    false
}

/// アクターとその全子孫を合わせたノード数（自身を含む）を返す。
/// handle_reparent_actor での取り出し後 DFS id 補正に使用する。
pub(super) fn actor_subtree_size(actor: &Actor) -> u32 {
    1 + actor.children().iter().map(|c| actor_subtree_size(c)).sum::<u32>()
}

// ============================================================
//  親情報取得
// ============================================================

/// 指定 DFS id のアクターの親情報を返す。
///
/// 返り値: (親の DFS id, 親が CanvasComponent を持つか)
/// - ルートアクター（親なし）の場合: (None, false)
/// - 対象が見つからない場合: (None, false)
pub(super) fn find_parent_canvas_info(
    actors:     &[Actor],
    wl:         u32,
    target_dfs: u32,
) -> (Option<u32>, bool) {
    let mut counter = 0u32;
    find_parent_canvas_info_root(actors, wl, target_dfs, &mut counter)
        .unwrap_or((None, false))
}

/// find_parent_canvas_info のルートレベル探索実装。
fn find_parent_canvas_info_root(
    actors:     &[Actor],
    wl:         u32,
    target_dfs: u32,
    counter:    &mut u32,
) -> Option<(Option<u32>, bool)> {
    for actor in actors.iter() {
        if actor.world_line != wl { continue; }
        let my_dfs = *counter;
        if my_dfs == target_dfs {
            // このアクターが対象 → 親なし
            return Some((None, false));
        }
        *counter += 1;
        let my_has_canvas = actor.has_kind(ComponentKind::Canvas);
        if let Some(result) = find_parent_canvas_info_children(
            actor.children(), target_dfs, counter, my_dfs, my_has_canvas,
        ) {
            return Some(result);
        }
    }
    None
}

/// find_parent_canvas_info の子孫レベル再帰探索実装。
fn find_parent_canvas_info_children(
    children:          &[Actor],
    target_dfs:        u32,
    counter:           &mut u32,
    parent_dfs:        u32,
    parent_has_canvas: bool,
) -> Option<(Option<u32>, bool)> {
    for child in children.iter() {
        let my_dfs = *counter;
        if my_dfs == target_dfs {
            return Some((Some(parent_dfs), parent_has_canvas));
        }
        *counter += 1;
        let my_has_canvas = child.has_kind(ComponentKind::Canvas);
        if let Some(result) = find_parent_canvas_info_children(
            child.children(), target_dfs, counter, my_dfs, my_has_canvas,
        ) {
            return Some(result);
        }
    }
    None
}

// ============================================================
//  エンティティ収集・despawn
// ============================================================

/// 指定 world_line のアクターとその全子孫のエンティティを収集する（World.despawn 用）。
pub(super) fn collect_entities_for_wl(actors: &[Actor], wl: u32) -> Vec<Entity> {
    let mut result = Vec::new();
    for actor in actors.iter().filter(|a| a.world_line == wl) {
        collect_actor_entities_recursive(actor, &mut result);
    }
    result
}

/// collect_entities_for_wl の再帰実装。
fn collect_actor_entities_recursive(actor: &Actor, result: &mut Vec<Entity>) {
    result.push(actor.entity);
    // スロット専用エンティティも含めて収集する（World.despawn 対象）
    result.extend(actor.slot_entities());
    for child in actor.children() {
        collect_actor_entities_recursive(child, result);
    }
}

/// アクターとその全子孫の World エンティティを再帰的に despawn する。
pub(super) fn despawn_actor_recursive(actor: &Actor, world: &mut World) {
    for slot_entity in actor.slot_entities() {
        world.despawn(slot_entity);
    }
    world.despawn(actor.entity);
    for child in actor.children() {
        despawn_actor_recursive(child, world);
    }
}

// ============================================================
//  DFS ノード数計算・取り出し
// ============================================================

/// アクター 1 本の DFS ノード数（自身 + 全子孫）を count に加算する。
/// do_paste でペースト後の新規 DFS id を求めるために使用する。
pub(super) fn count_actor_dfs_nodes(actor: &Actor, count: &mut usize) {
    *count += 1;
    for child in actor.children() {
        count_actor_dfs_nodes(child, count);
    }
}

/// DFS id でアクターをツリーから取り出して out へ格納する。
pub(super) fn extract_actor_by_dfs(
    actors:  &mut Vec<Actor>,
    wl:      u32,
    dfs_id:  u32,
    counter: &mut u32,
    out:     &mut Option<Actor>,
) -> bool {
    let mut i = 0;
    while i < actors.len() {
        if actors[i].world_line != wl { i += 1; continue; }
        if *counter == dfs_id {
            *out = Some(actors.remove(i));
            return true;
        }
        *counter += 1;
        if extract_actor_child_by_dfs(&mut actors[i], dfs_id, counter, out) {
            return true;
        }
        i += 1;
    }
    false
}

/// extract_actor_by_dfs の再帰実装（子ノード用）。
fn extract_actor_child_by_dfs(
    actor:   &mut Actor,
    dfs_id:  u32,
    counter: &mut u32,
    out:     &mut Option<Actor>,
) -> bool {
    let mut i = 0;
    while i < actor.children_mut().len() {
        if *counter == dfs_id {
            *out = Some(actor.children_mut().remove(i));
            return true;
        }
        *counter += 1;
        if extract_actor_child_by_dfs(&mut actor.children_mut()[i], dfs_id, counter, out) {
            return true;
        }
        i += 1;
    }
    false
}

// ============================================================
//  座標変換・選択ユーティリティ
// ============================================================

/// 選択インスタンスのワールド位置の重心を返す。
pub(super) fn selection_centroid(
    instances: &[u32],
    mats:      &[[[f32; 4]; 4]],
) -> Option<[f32; 3]> {
    if instances.is_empty() { return None; }
    let (mut sx, mut sy, mut sz) = (0.0f32, 0.0, 0.0);
    let mut cnt = 0u32;
    for &i in instances {
        if let Some(m) = mats.get(i as usize) {
            sx += m[0][3]; sy += m[1][3]; sz += m[2][3];
            cnt += 1;
        }
    }
    if cnt == 0 { None } else { Some([sx / cnt as f32, sy / cnt as f32, sz / cnt as f32]) }
}

/// インスタンス削除後の親参照を修正する。
#[allow(dead_code)]
pub(super) fn fix_parent(
    parent:         Option<u32>,
    delete_set:     &HashSet<u32>,
    deleted_parent: &HashMap<u32, Option<u32>>,
    sorted_asc:     &[u32],
    recursive:      bool,
) -> Option<u32> {
    use crate::engine::structs::components::model_component::GROUP_ID_BASE;
    let p = parent?;

    if p >= GROUP_ID_BASE {
        return Some(p); // グループ ID は変化しない
    }

    if delete_set.contains(&p) {
        if recursive {
            return None;
        }
        // 非再帰: 削除チェーンを辿って最初の生存祖先を探す
        let mut cur = deleted_parent.get(&p).copied().flatten();
        loop {
            match cur {
                None => return None,
                Some(c) if c >= GROUP_ID_BASE => return Some(c),
                Some(c) if !delete_set.contains(&c) => {
                    let shift = sorted_asc.partition_point(|&d| d < c) as u32;
                    return Some(c - shift);
                }
                Some(c) => cur = deleted_parent.get(&c).copied().flatten(),
            }
        }
    }

    // 親は生存 → インデックスをシフト
    let shift = sorted_asc.partition_point(|&d| d < p) as u32;
    Some(p - shift)
}

/// ワールド座標をビューポートのスクリーン座標へ投影する。
/// カメラ後方（clip.w ≤ 0）の場合は None を返す。
pub(super) fn world_to_screen(
    world: [f32; 3],
    view:  &[[f32; 4]; 4],
    proj:  &[[f32; 4]; 4],
    vp_w:  f32,
    vp_h:  f32,
) -> Option<(f32, f32)> {
    let [wx, wy, wz] = world;
    // ビュー変換 → プロジェクション変換（列ベクトル規約: v' = M * v）
    let view_pos = Mat4x4 { data: *view } * Vector4::new(wx, wy, wz, 1.0);
    let clip     = Mat4x4 { data: *proj } * view_pos;
    if clip.w <= 0.0 { return None; }
    // NDC → スクリーン座標
    let nx = clip.x / clip.w;
    let ny = clip.y / clip.w;
    Some(((nx + 1.0) * 0.5 * vp_w, (1.0 - ny) * 0.5 * vp_h))
}

// ============================================================
//  ドラッグ開始・終了時の子アクター状態収集ユーティリティ
// ============================================================

/// ドラッグ開始時: 子孫アクターの MC 初期行列を収集する。
/// dfs_counter は選択アクターの DFS + 1 から始める。
pub(super) fn collect_child_actor_mc_starts(
    actor:       &Actor,
    world:       &World,
    dfs_counter: &mut u32,
    result:      &mut Vec<(u32, [[f32; 4]; 4])>,
) {
    for child in actor.children() {
        let child_dfs = *dfs_counter;
        *dfs_counter += 1;
        // スロット entity 経由で MC の最初のインスタンス行列を取得する
        if let Some(mc_e) = child.mc_entity() {
            if let Some(mc) = world.get::<ModelComponent>(mc_e) {
                if let Some(&mat) = mc.instance_mats.first() {
                    result.push((child_dfs, mat));
                }
            }
        }
        collect_child_actor_mc_starts(child, world, dfs_counter, result);
    }
}

/// インスペクタードラッグ開始時: 子孫アクターの (dfs_id, old_tf, old_mc_mat) を収集する。
pub(super) fn collect_child_actor_old_states(
    actor:       &Actor,
    world:       &World,
    dfs_counter: &mut u32,
    result:      &mut Vec<(u32, ActorTransform, [[f32; 4]; 4])>,
) {
    for child in actor.children() {
        let child_dfs = *dfs_counter;
        *dfs_counter += 1;
        let old_tf = world.get::<ActorTransform>(child.entity).cloned().unwrap_or_default();
        // スロット entity 経由で MC の最初のインスタンス行列を取得する
        let old_mc_mat = child.mc_entity()
            .and_then(|e| world.get::<ModelComponent>(e))
            .and_then(|mc| mc.instance_mats.first().copied())
            .unwrap_or([[0.0; 4]; 4]);
        result.push((child_dfs, old_tf, old_mc_mat));
        collect_child_actor_old_states(child, world, dfs_counter, result);
    }
}

/// ギズモドラッグまたはインスペクタードラッグ中: delta を子孫アクター全体に適用し、
/// Undo 用の変更データ (child_dfs, old_tf, new_tf, old_mc_mat, new_mc_mat) を収集する。
pub(super) fn apply_delta_to_actor_children(
    actor:       &mut Actor,
    world:       &mut World,
    delta:       [[f32; 4]; 4],
    dfs_counter: &mut u32,
    result:      &mut Vec<(u32, ActorTransform, ActorTransform, [[f32; 4]; 4], [[f32; 4]; 4])>,
) {
    let identity = super::MAT4_IDENTITY;
    for child in actor.children_mut().iter_mut() {
        let child_dfs = *dfs_counter;
        *dfs_counter += 1;
        let child_entity   = child.entity;
        // スロット entity を Copy で取り出す（child の borrow が続くが Entity は Copy）
        let mc_slot_entity = child.mc_entity();

        // MC の更新: スロット entity 経由でアクセスする
        let (old_mc_mat, new_mc_mat) = if let Some(mc_e) = mc_slot_entity {
            if let Some(mc) = world.get_mut::<ModelComponent>(mc_e) {
                let old = mc.instance_mats.first().copied().unwrap_or(identity);
                if let Some(m) = mc.instance_mats.first_mut() { *m = mat4x4_mul(delta, *m); }
                mc.mark_batch_dirty();
                let new = mc.instance_mats.first().copied().unwrap_or(identity);
                (old, new)
            } else {
                (identity, identity)
            }
        } else {
            (identity, identity)
        };

        // Transform の更新（actor.entity から Transform を参照）
        let old_tf = world.get::<ActorTransform>(child_entity).cloned().unwrap_or_default();
        let new_tf = ActorTransform::from_mat4(&mat4x4_mul(delta, old_tf.to_mat4()));
        if let Some(tf) = world.get_mut::<ActorTransform>(child_entity) { *tf = new_tf.clone(); }

        result.push((child_dfs, old_tf, new_tf, old_mc_mat, new_mc_mat));
        apply_delta_to_actor_children(child, world, delta, dfs_counter, result);
    }
}

