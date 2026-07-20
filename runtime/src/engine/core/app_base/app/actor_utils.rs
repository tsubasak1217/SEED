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

/// Actor ツリーを DFS 順にフラット化し
/// (id, name, parent_id, is_2d, is_vp, active, has_canvas) を収集する。
///
/// `root_is_vp` はこのサブツリーのトップレベル（ルート）アクターが Actor2D
/// （= スクリーンスペースキャンバス系）かどうかのフラグ。
/// トップレベル呼び出しでは `actor.is_2d()` を渡し、再帰では同じ値を伝播させる
/// （「ビューポート所属」= ルートが Actor2D のサブツリー全体、という分類のため）。
///
/// 最後の `has_canvas` は「このアクター自身が CanvasComponent を持つか」。
/// エディタ側で「2D アクターの新しい親として、Canvas を持たない 3D アクターを禁止する」
/// ドロップ制限（3D Canvas アクターは 2D の親として許可）に使用する。
pub(super) fn collect_actor_nodes(
    actor:         &Actor,
    parent:        Option<u32>,
    counter:       &mut u32,
    root_is_vp:    bool,
    parent_active: bool,
    out:           &mut Vec<(u32, String, Option<u32>, bool, bool, bool, bool, bool, bool)>,
) {
    let id = *counter;
    *counter += 1;
    // active は「実効アクティブ」（自身と全祖先が active）。エディタの淡色表示に使う。
    let active = parent_active && actor.active;
    // has_canvas: このアクターが CanvasComponent スロットを持つか（3D Canvas 判定に使う）
    let has_canvas = actor.has_kind(ComponentKind::Canvas);
    // is_prefab: このアクターがプレハブ参照リンクを持つか（= プレハブインスタンスのルート）。
    // インスタンスのルートのみ Some を持つため、子ノードは自然と false になる。
    // エディタのヒエラルキーでプレハブアイコン表示に使用する（C# パースは次ウェーブ）。
    let is_prefab = actor.prefab_source.is_some();
    // is_folder: 整理専用のフォルダノードか（Transform 非保持・透過）。
    // エディタでフォルダアイコン表示＋Inspector で Transform を出さない判定に使う。
    let is_folder = actor.is_folder();
    out.push((id, actor.name.clone(), parent, actor.is_2d(), root_is_vp, active, has_canvas, is_prefab, is_folder));
    for child in actor.children() {
        // ルートのビューポート所属フラグを子孫全体へそのまま伝播する
        collect_actor_nodes(child, Some(id), counter, root_is_vp, active, out);
    }
}

/// ヒエラルキー JSON 1 ノード分のシリアライズ用構造体。
#[derive(serde::Serialize)]
struct HierarchyNode<'a> {
    id:       u32,
    name:     &'a str,
    parent:   Option<u32>,
    is_group: bool,
    /// 2D アクター（CanvasTransform）か否かを示すフラグ。エディタのアイコン色分けに使用する。
    is_2d:    bool,
    /// ビューポート所属フラグ。サブツリーのトップレベルルートが Actor2D
    /// （スクリーンスペースキャンバス）のとき true。
    /// エディタのシーンタブ（ワールド/ビューポート）自動切替に使用する。
    /// 3D ワールドキャンバス配下の 2D スプライトは is_2d=true でも is_vp=false になる。
    is_vp:    bool,
    /// 実効アクティブフラグ（自身と全祖先の active が true）。
    /// false のノードはエディタのヒエラルキーで淡色表示する。
    active:   bool,
    /// このアクター自身が CanvasComponent を持つか。
    /// エディタの 2D ドロップ制限（Canvas を持たない 3D アクターへの 2D 子付け禁止）に使用する。
    has_canvas: bool,
    /// このアクターがプレハブインスタンスのルート（prefab_source を持つ）か。
    /// エディタのプレハブアイコン表示・右クリック「リンク解除」メニュー活性化に使用する。
    is_prefab: bool,
    /// このアクターが整理専用のフォルダノード（Transform 非保持・透過）か。
    /// エディタのヒエラルキーでフォルダアイコン表示、Inspector で Transform を
    /// 出さない（名前変更のみ）判定に使用する。地形ルート／チャンクの器などに付く。
    is_folder: bool,
}

/// フラットリストから HIERARCHY JSON を生成する。
pub(super) fn build_hierarchy_json(nodes: &[(u32, String, Option<u32>, bool, bool, bool, bool, bool, bool)]) -> String {
    let items: Vec<HierarchyNode<'_>> = nodes
        .iter()
        .map(|(id, name, parent, is_2d, is_vp, active, has_canvas, is_prefab, is_folder)| HierarchyNode {
            id:       *id,
            name:     name.as_str(),
            parent:   *parent,
            // フォルダノードはグループ同様「器」なので is_group も true にして、
            // 既存エディタのグループ系ロジック（選択種別判定のスキップ等）と整合させる。
            is_group: *is_folder,
            is_2d:    *is_2d,
            is_vp:    *is_vp,
            active:   *active,
            has_canvas: *has_canvas,
            is_prefab: *is_prefab,
            is_folder: *is_folder,
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
        collect_mcs_in_actor(root, world, &mut counter, &mut base, &mut result, true);
    }
    result
}

/// collect_mcs_in_world_line の再帰実装。
///
/// `parent_active` は祖先のアクティブ状態。非アクティブなアクター（自身または祖先が
/// active=false）および enabled=false のスロットの MC は収集しない（描画・ピック対象外）。
/// DFS カウントは選択系と整合させるため、スキップ時も必ず進める。
fn collect_mcs_in_actor<'a>(
    actor:         &'a Actor,
    world:         &'a World,
    counter:       &mut u32,
    base:          &mut u32,
    result:        &mut Vec<(u32, u32, usize, &'a ModelComponent)>,
    parent_active: bool,
) {
    let dfs = *counter;
    *counter += 1;
    let active = parent_active && actor.active;
    // スロット専用 entity から ModelComponent を収集する（複数スロット対応）
    // slot_i は「Model スロット内の連番」なので、無効スロットも含めて数える
    // （mc_entity_at と整合させるため enumerate を filter の後に置かない）
    if active {
        let mut slot_i = 0usize;
        for slot in actor.slots().iter().filter(|s| s.kind == ComponentKind::Model) {
            if slot.enabled {
                if let Some(mc) = world.get::<ModelComponent>(slot.entity) {
                    result.push((*base, dfs, slot_i, mc));
                    *base += mc.instance_mats.len() as u32;
                }
            }
            slot_i += 1;
        }
    }
    for child in actor.children() {
        collect_mcs_in_actor(child, world, counter, base, result, active);
    }
}

/// world_line の全アクターの MC バッチを DFS 順で更新する（可変参照版）。
///
/// オブジェクト単位の視錐台カリングは廃止した（誤棄却によるポッピング/チャンク消え防止）。
/// 全インスタンスを常に可視扱いで LOD 振り分け・アップロードする。
pub(super) fn update_all_mc_batches_for_wl(
    actors:         &mut Vec<Actor>,
    world:          &mut World,
    wl:             u32,
    queue:          &wgpu::Queue,
    camera_pos:     [f32; 3],
) {
    for actor in actors.iter_mut().filter(|a| a.world_line == wl) {
        update_mc_batch_recursive(actor, world, queue, camera_pos);
    }
}

/// update_all_mc_batches_for_wl の再帰実装。
fn update_mc_batch_recursive(
    actor:          &mut Actor,
    world:          &mut World,
    queue:          &wgpu::Queue,
    camera_pos:     [f32; 3],
) {
    // スロット専用 entity の全 ModelComponent バッチを更新する（複数スロット対応）
    let slot_entities: Vec<Entity> = actor.slots().iter()
        .filter(|s| s.kind == ComponentKind::Model)
        .map(|s| s.entity)
        .collect();
    for slot_entity in slot_entities {
        if let Some(mc) = world.get_mut::<ModelComponent>(slot_entity) {
            if let (Some(batch), Some(model)) = (&mut mc.instanced_batch, mc.model.as_deref()) {
                batch.update(queue, model, &mc.instance_mats, camera_pos);
            }
        }
    }
    for child in actor.children_mut().iter_mut() {
        update_mc_batch_recursive(child, world, queue, camera_pos);
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

/// 指定 DFS ID のアクターについて (トップレベルルートか, サブツリールートが Actor2D か) を返す。
///
/// DFS カウント規則は find_actor_by_dfs と同一
/// （トップレベルのみ world_line でフィルタし、子孫は全カウント）。
/// 戻り値の第 2 要素はヒエラルキーの is_vp（ビューポート所属）と同じ意味を持つ。
/// 対象が見つからない場合は None を返す。
pub(super) fn find_actor_root_info(
    actors: &[Actor],
    wl:     u32,
    dfs_id: u32,
) -> Option<(bool, bool)> {
    let mut counter = 0u32;
    for root in actors.iter().filter(|a| a.world_line == wl) {
        let start = counter;
        let size  = actor_subtree_size(root);
        if dfs_id == start {
            // トップレベルルート自身
            return Some((true, root.is_2d()));
        }
        if dfs_id < start + size {
            // このルートのサブツリー内の子孫
            return Some((false, root.is_2d()));
        }
        counter += size;
    }
    None
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

/// ルートエンティティでアクターをツリーから取り出す（DFS 順の最初の一致）。
///
/// スクリプトの Destroy（エンティティハンドル指定）用。DFS id と異なり
/// エンティティは世代付きで安定なため、フレームをまたいだ破棄要求にも安全。
pub(super) fn extract_actor_by_entity(
    actors: &mut Vec<Actor>,
    entity: crate::engine::ecs::Entity,
) -> Option<Actor> {
    let mut i = 0;
    while i < actors.len() {
        if actors[i].entity == entity {
            return Some(actors.remove(i));
        }
        if let Some(found) = extract_actor_child_by_entity(&mut actors[i], entity) {
            return Some(found);
        }
        i += 1;
    }
    None
}

/// extract_actor_by_entity の再帰実装（子ノード用）。
fn extract_actor_child_by_entity(
    actor:  &mut Actor,
    entity: crate::engine::ecs::Entity,
) -> Option<Actor> {
    let mut i = 0;
    while i < actor.children_mut().len() {
        if actor.children_mut()[i].entity == entity {
            return Some(actor.children_mut().remove(i));
        }
        if let Some(found) = extract_actor_child_by_entity(&mut actor.children_mut()[i], entity) {
            return Some(found);
        }
        i += 1;
    }
    None
}

/// DFS id でアクターをツリーから取り出し、復帰用の出自情報も返す。
///
/// キャンバス編集タブ（EDIT_CANVAS_BEGIN）用。取り出したアクターを編集終了時に
/// 元の位置へ戻せるよう、以下のタプルを返す:
/// - `Actor`         … 取り出したアクター（サブツリーごと）
/// - `Option<Entity>`… 親アクターのエンティティ（None = シーン直下のトップレベル）
/// - `usize`         … 挿入位置（トップレベルなら scene.actors の index、
///                      子なら親の children 内 index）
pub(super) fn extract_actor_by_dfs_with_origin(
    actors: &mut Vec<Actor>,
    wl:     u32,
    dfs_id: u32,
) -> Option<(Actor, Option<Entity>, usize)> {
    let mut counter = 0u32;
    let mut i = 0;
    while i < actors.len() {
        if actors[i].world_line != wl { i += 1; continue; }
        if counter == dfs_id {
            // トップレベルで一致: scene.actors の実 index を出自として返す
            return Some((actors.remove(i), None, i));
        }
        counter += 1;
        let parent_entity = actors[i].entity;
        if let Some(found) =
            extract_actor_child_by_dfs_with_origin(&mut actors[i], dfs_id, &mut counter, parent_entity)
        {
            return Some(found);
        }
        i += 1;
    }
    None
}

/// extract_actor_by_dfs_with_origin の再帰実装（子ノード用）。
fn extract_actor_child_by_dfs_with_origin(
    actor:   &mut Actor,
    dfs_id:  u32,
    counter: &mut u32,
    parent_entity: Entity,
) -> Option<(Actor, Option<Entity>, usize)> {
    let mut i = 0;
    while i < actor.children_mut().len() {
        if *counter == dfs_id {
            // 親エンティティと children 内 index を出自として返す
            return Some((actor.children_mut().remove(i), Some(parent_entity), i));
        }
        *counter += 1;
        let my_entity = actor.children_mut()[i].entity;
        if let Some(found) =
            extract_actor_child_by_dfs_with_origin(&mut actor.children_mut()[i], dfs_id, counter, my_entity)
        {
            return Some(found);
        }
        i += 1;
    }
    None
}

/// ルートエンティティでアクターへの可変参照を取得する（world_line 不問・DFS 順の最初の一致）。
///
/// キャンバス編集タブの終了時にサブツリーを元の親へ再挿入するために使用する。
/// エンティティは世代付きで安定なため、編集セッションをまたいでも安全に親を特定できる。
pub(super) fn find_actor_by_entity_mut<'a>(
    actors: &'a mut [Actor],
    entity: Entity,
) -> Option<&'a mut Actor> {
    for actor in actors.iter_mut() {
        if actor.entity == entity {
            return Some(actor);
        }
        if let Some(found) = find_actor_by_entity_mut(actor.children_mut(), entity) {
            return Some(found);
        }
    }
    None
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

/// ModelComponent を持たない 3D アクターの Transform 位置を矩形内でスクリーン投影し、
/// DFS ID を result に追加する。MC 選択・カメラギズモ選択と重複しないよう already を参照する。
/// 2D キャンバスアクターはスキップする（キャンバス専用の選択ロジックで処理する）。
pub(super) fn collect_transform_only_in_rect(
    actors:  &[Actor],
    world:   &World,
    wl:      u32,
    view:    &[[f32; 4]; 4],
    proj:    &[[f32; 4]; 4],
    vp_w:    f32,
    vp_h:    f32,
    sx_min:  f32, sx_max: f32,
    sy_min:  f32, sy_max: f32,
    already: &[usize],
    result:  &mut Vec<usize>,
) {
    let mut counter = 0u32;
    for actor in actors.iter().filter(|a| a.world_line == wl) {
        collect_transform_only_recursive(
            actor, world, view, proj, vp_w, vp_h,
            sx_min, sx_max, sy_min, sy_max,
            &mut counter, already, result,
        );
    }
}

fn collect_transform_only_recursive(
    actor:   &Actor,
    world:   &World,
    view:    &[[f32; 4]; 4],
    proj:    &[[f32; 4]; 4],
    vp_w:    f32,
    vp_h:    f32,
    sx_min:  f32, sx_max: f32,
    sy_min:  f32, sy_max: f32,
    counter: &mut u32,
    already: &[usize],
    result:  &mut Vec<usize>,
) {
    let dfs_id = *counter as usize;
    *counter += 1;

    // 2D キャンバスアクターは対象外
    let has_model = actor.slots().iter().any(|s| s.kind == ComponentKind::Model);
    if !has_model && !actor.is_2d()
        && !already.contains(&dfs_id)
        && !result.contains(&dfs_id)
    {
        if let Some(tf) = world.get::<ActorTransform>(actor.entity) {
            let pos = tf.position;
            if let Some((sx, sy)) = world_to_screen(pos, view, proj, vp_w, vp_h) {
                if sx >= sx_min && sx <= sx_max && sy >= sy_min && sy <= sy_max {
                    result.push(dfs_id);
                }
            }
        }
    }

    for child in actor.children() {
        collect_transform_only_recursive(
            child, world, view, proj, vp_w, vp_h,
            sx_min, sx_max, sy_min, sy_max,
            counter, already, result,
        );
    }
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

/// アクターのサブツリー全体へワールド空間デルタ行列を適用する（Undo 収集なしの軽量版）。
///
/// 適用対象:
///  - 自身と全子孫の ModelComponent の instance_mats（全インスタンス。ワールド行列）
///  - 子孫アクターの Transform（ワールド空間で保持されるため delta を直接適用）
/// ルート自身の Transform は呼び出し側が設定する（ここでは触らない）。
/// CanvasTransform のみの 2D 子アクターは描画時に親キャンバス階層から再計算されるため
/// 対象外（Transform を持つ場合のみ更新する）。
///
/// ドロップ配置・プレハブ再展開で「ルート位置＝原点」基準のアクタファイル内容を
/// シーン上の配置行列へ変換するために使用する。
pub(super) fn apply_delta_to_actor_subtree(
    actor: &mut Actor,
    world: &mut World,
    delta: [[f32; 4]; 4],
) {
    // 自身の全 Model スロットの instance_mats に delta を左乗算する
    let slot_entities: Vec<Entity> = actor.slots().iter()
        .filter(|s| s.kind == ComponentKind::Model)
        .map(|s| s.entity)
        .collect();
    for slot_entity in slot_entities {
        if let Some(mc) = world.get_mut::<ModelComponent>(slot_entity) {
            for m in mc.instance_mats.iter_mut() {
                *m = mat4x4_mul(delta, *m);
            }
            mc.mark_batch_dirty();
        }
    }
    // 子孫の Transform（ワールド空間）と MC へ再帰的に適用する
    for child in actor.children_mut().iter_mut() {
        let child_entity = child.entity;
        if let Some(tf) = world.get_mut::<ActorTransform>(child_entity) {
            *tf = ActorTransform::from_mat4(&mat4x4_mul(delta, tf.to_mat4()));
        }
        apply_delta_to_actor_subtree(child, world, delta);
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

// ============================================================
//  3D Canvas 座標変換ユーティリティ
// ============================================================

/// DFS ID のアクターの直接親アクターを返す。
///
/// アクターツリーを DFS 順で走査し、target_dfs の直前の親を返す。
/// トップレベルアクター（親なし）の場合は None を返す。
pub(super) fn find_parent_actor_of_dfs<'a>(
    actors:  &'a [Actor],
    wl:      u32,
    target:  u32,
    counter: &mut u32,
    parent:  Option<&'a Actor>,
) -> Option<&'a Actor> {
    for actor in actors {
        if actor.world_line != wl { continue; }
        let my_dfs = *counter;
        *counter += 1;
        if my_dfs == target { return parent; }
        if let Some(found) =
            find_parent_actor_of_dfs(actor.children(), wl, target, counter, Some(actor))
        {
            return Some(found);
        }
    }
    None
}

/// アクターが Actor3D + CanvasComponent を持つ場合、canvas_to_world 変換行列を返す。
///
/// canvas_to_world = actor_3d_mat * local_mat
/// local_mat は pivot・Y 反転を含む 3D Canvas 固有のローカル行列。
/// 3D Canvas 親でない（is_2d() / CanvasComponent なし）場合は None を返す。
pub(super) fn get_3d_canvas_world_mat(
    actor: &Actor,
    world: &World,
) -> Option<[[f32; 4]; 4]> {
    if actor.is_2d() { return None; }
    let canvas_slot = actor.slots().iter().find(|s| s.kind == ComponentKind::Canvas)?;
    let cc = world.get::<CanvasComponent>(canvas_slot.entity)?;
    let tf = world.get::<ActorTransform>(actor.entity)?;
    const CWS: f32 = 1.0 / 100.0;
    let (piv_x, piv_y, w, h) = (cc.pivot[0], cc.pivot[1], cc.width, cc.height);
    // キャンバス Y+（下）→ ワールド Y-（Y-UP カメラで screen 下）、pivot オフセット適用
    let local_mat = [
        [ CWS,  0.0, 0.0, -piv_x * w * CWS],
        [ 0.0, -CWS, 0.0,  piv_y * h * CWS],
        [ 0.0,  0.0, 1.0,  0.0            ],
        [ 0.0,  0.0, 0.0,  1.0            ],
    ];
    Some(mat4x4_mul(tf.to_mat4(), local_mat))
}

