// ============================================================
//  actor_utils.rs — アクターツリー・ユーティリティ関数群
//
//  【含む処理】
//  - ヒエラルキー JSON 生成（collect_actor_nodes / build_hierarchy_json）
//  - DFS 探索（find_actor_by_dfs* / remove_actor_by_dfs* / extract_actor_by_dfs*）
//  - Canvas ユーティリティ（canvas_anchor_offset_for_dfs / collect_canvas_actors_in_rect）
//  - MC 収集・バッチ更新（collect_mcs_in_world_line / update_all_mc_batches_for_wl）
//  - エンティティ管理（collect_entities_for_wl / despawn_actor_recursive）
//  - 親子付け替え（validate_reparent_kind / reparent_actor_by_entity / attach_actor_under）
//  - 座標・選択ユーティリティ（world_to_screen / selection_centroid）
//  - ドラッグ時子アクター収集（collect_child_actor_drag_starts / apply_delta_to_actor_children）
//
//  Windows カーソルロック系 → platform_utils.rs
// ============================================================


use std::collections::{HashMap, HashSet};

use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;
use super::drag_state::ChildDragStart;
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
/// undo.rs（app_base 直下）の ActorActiveCommand からも使うため pub(crate)。
pub(crate) fn find_actor_by_dfs_mut<'a>(
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

/// 指定エンティティ群の **DFS id** を一括で解決する。
///
/// `find_actor_by_dfs` と**同じ走査順**（世界線一致のルートを順に、各ルートは
/// 自身 → 子を深さ優先）で番号を振るので、ここで得た id は
/// `SELECTED_MULTI` やインスペクタの参照にそのまま使える。
///
/// 1 体ずつ `find_actor_by_dfs` を呼ぶと生成数 N に対して O(N × ツリー全体) に
/// なるため、**1 回の走査でまとめて**引く（数百体の一括生成が実用速度で終わる）。
/// 戻り値は入力 `entities` と同じ並び。見つからなかった要素は `None`。
pub(super) fn dfs_ids_for_entities(
    actors:   &[Actor],
    wl:       u32,
    entities: &[Entity],
) -> Vec<Option<u32>> {
    let mut found: HashMap<Entity, u32> = HashMap::new();
    let mut counter = 0u32;
    for actor in actors.iter() {
        if actor.world_line != wl { continue; }
        collect_dfs_ids(actor, &mut counter, entities, &mut found);
    }
    entities.iter().map(|e| found.get(e).copied()).collect()
}

/// `dfs_ids_for_entities` の再帰実装（自身に番号を振ってから子へ降りる）。
fn collect_dfs_ids(
    actor:    &Actor,
    counter:  &mut u32,
    wanted:   &[Entity],
    found:    &mut HashMap<Entity, u32>,
) {
    if wanted.contains(&actor.entity) {
        found.insert(actor.entity, *counter);
    }
    *counter += 1;
    for child in actor.children().iter() {
        collect_dfs_ids(child, counter, wanted, found);
    }
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

// ── 旧: update_all_mc_batches_for_wl / update_mc_batch_recursive（削除）──────────
// per-MC `ModelComponent::instanced_batch` を毎フレーム `update()` していた関数群。
// Phase R7 のバッチ統合以降、描画は shared_model_batches（統合バッチ）だけを通り、
// per-MC の instanced_batch はどの描画経路からも参照されない（唯一の描画アクセサ
// rendering_refs() は呼び出し 0 件）。毎フレームの update は完全な死荷重（16×16×3層の
// 地形で実測 batch≈4ms/フレーム）だったため削除した。統合バッチの更新は frame_renderer の
// merge 更新ループが担う。

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
//  DFS 挿入・取り出し
// ============================================================

/// DFS id で指定したアンカーアクターの「兄弟としてすぐ後ろ」へ、新しいアクター群をまとめて挿入する。
///
/// ペースト（＝複製）を「複製元の直下（兄弟として一つ下）」へ入れるために使用する。
/// アンカーがトップレベルなら `actors` へ、子アクターなら親の `children` へ挿入する。
///
/// # 引数
/// - `actors`     … シーンのトップレベルアクター配列（複数世界線が混在する）
/// - `wl`         … 対象世界線（トップレベルのみこのフィルタが効く。DFS 規則は find_actor_by_dfs と同一）
/// - `anchor_dfs` … 挿入基準となるアクターの DFS id
/// - `new_actors` … 挿入するアクター群。**挿入に成功したときのみ** drain して空になる
///
/// # 戻り値
/// 挿入された先頭アクターの DFS id。アンカーが見つからなければ `None`（`new_actors` は無変更）。
/// アンカーのサブツリー直後に入るため `anchor_dfs + アンカーのサブツリーノード数` に等しい。
pub(super) fn insert_actors_after_dfs(
    actors:     &mut Vec<Actor>,
    wl:         u32,
    anchor_dfs: u32,
    new_actors: &mut Vec<Actor>,
) -> Option<u32> {
    let mut counter = 0u32;
    let mut i = 0;
    while i < actors.len() {
        if actors[i].world_line != wl { i += 1; continue; }
        if counter == anchor_dfs {
            // トップレベルで一致: アンカーのサブツリー直後の DFS id が新規先頭になる
            let base  = anchor_dfs + actor_subtree_size(&actors[i]);
            let items = new_actors.drain(..).collect::<Vec<_>>();
            actors.splice(i + 1 .. i + 1, items);
            return Some(base);
        }
        counter += 1;
        if let Some(base) =
            insert_actors_after_dfs_children(&mut actors[i], anchor_dfs, &mut counter, new_actors)
        {
            return Some(base);
        }
        i += 1;
    }
    None
}

/// insert_actors_after_dfs の再帰実装（子ノード用）。
fn insert_actors_after_dfs_children(
    parent:     &mut Actor,
    anchor_dfs: u32,
    counter:    &mut u32,
    new_actors: &mut Vec<Actor>,
) -> Option<u32> {
    let mut i = 0;
    while i < parent.children().len() {
        if *counter == anchor_dfs {
            // 子として一致: 同じ親の children 内でアンカーの直後へ挿入する（＝兄弟として一つ下）
            let base  = anchor_dfs + actor_subtree_size(&parent.children()[i]);
            let items = new_actors.drain(..).collect::<Vec<_>>();
            parent.children_mut().splice(i + 1 .. i + 1, items);
            return Some(base);
        }
        *counter += 1;
        if let Some(base) = insert_actors_after_dfs_children(
            &mut parent.children_mut()[i], anchor_dfs, counter, new_actors,
        ) {
            return Some(base);
        }
        i += 1;
    }
    None
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

/// ドラッグ開始時: 子孫アクターのドラッグ開始スナップショットを収集する。
/// dfs_counter は選択アクターの DFS + 1 から始める。
///
/// 【重要】**Model の有無にかかわらず全ての子孫を収集する**。
/// 以前は ModelComponent と instance_mats を持つ子だけを対象にしていたため、
/// モデルを持たない子アクタ（カメラ・空アクタなど）がギズモドラッグの伝播から
/// 漏れて親に追従しなかった。Transform は Model の有無と無関係に存在するため、
/// MC 行列（無ければ None）と Transform 行列を独立に記録する。
pub(super) fn collect_child_actor_drag_starts(
    actor:       &Actor,
    world:       &World,
    dfs_counter: &mut u32,
    result:      &mut Vec<ChildDragStart>,
) {
    for child in actor.children() {
        let child_dfs = *dfs_counter;
        *dfs_counter += 1;
        // スロット entity 経由で MC の最初のインスタンス行列を取得する（無ければ None）
        let mc_start = child.mc_entity()
            .and_then(|e| world.get::<ModelComponent>(e))
            .and_then(|mc| mc.instance_mats.first().copied());
        // Transform（ワールド空間）の開始行列。Transform を持たないノード（フォルダ・2D）は
        // 単位行列を記録するが、適用側は Transform が存在する場合のみ書き戻すため無害。
        let tf_start = world.get::<ActorTransform>(child.entity)
            .map(|tf| tf.to_mat4())
            .unwrap_or(super::MAT4_IDENTITY);
        result.push(ChildDragStart { dfs_id: child_dfs, mc_start, tf_start });
        collect_child_actor_drag_starts(child, world, dfs_counter, result);
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
///
/// 実体は集約モジュール `engine::core::transform_sync` にある
/// （Transform 伝播ロジックを 1 か所に集約し、経路ごとの取りこぼしを防ぐため）。
/// ここは既存呼び出し元の互換ラッパー。
pub(super) fn apply_delta_to_actor_children(
    actor:       &Actor,
    world:       &mut World,
    delta:       [[f32; 4]; 4],
    dfs_counter: &mut u32,
    result:      &mut Vec<(u32, ActorTransform, ActorTransform, [[f32; 4]; 4], [[f32; 4]; 4])>,
) {
    crate::engine::core::transform_sync::propagate_delta_to_children(
        actor, world, delta, dfs_counter, result,
    );
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

// ============================================================
//  グループ（フォルダ）アクタ生成
// ============================================================

/// 生成済みのグループアクタをツリーへ挿入し、指定した子アクタ群をその配下へ移す。
///
/// ヒエラルキー表示の正典は **アクタツリー**（`Scene::actors` → `collect_actor_nodes`）
/// であり、ModelComponent 側の `group_meta` は旧実装の遺物で表示に使われない。
/// そのため「グループフォルダを作成」は実アクタ（フォルダノード or 2D アクタ）を
/// 1 体生やすことで実現する。
///
/// # 引数
/// - `actors`:       対象ツリー（`Scene::actors`）
/// - `wl`:           対象世界線（DFS id の数え上げはこの世界線のアクタのみが対象）
/// - `group`:        呼び出し側で生成済みのグループアクタ（entity / 種別設定済み）
/// - `parent_dfs`:   グループの親アクタ DFS id（None ならルート直下）
/// - `children_dfs`: グループ配下へ移す子アクタの DFS id 列（空でも可）
///
/// # 実装上の注意
/// 子の取り出しは **DFS id ではなく Entity 基準**で行う。DFS id は 1 体取り出す
/// たびにズレるため、先に Entity へ解決してから取り出すことで補正計算を不要にする。
/// 既に取り出したサブツリーへ含まれていた子（＝祖先ごと移動済み）は Entity 解決に
/// 失敗するため自然にスキップされる。
///
/// # 戻り値
/// 挿入できたら true（`group` を消費するため、現状は常に true）。
pub(super) fn insert_group_actor(
    actors:       &mut Vec<Actor>,
    wl:           u32,
    mut group:    Actor,
    parent_dfs:   Option<u32>,
    children_dfs: &[u32],
) -> bool {
    // ── 1. DFS id を Entity へ解決する（ツリーを触る前に済ませる） ──
    let parent_entity = parent_dfs.and_then(|pid| {
        let mut c = 0u32;
        find_actor_by_dfs(actors, wl, pid, &mut c).map(|a| a.entity)
    });
    let mut child_entities: Vec<Entity> = Vec::new();
    for &cdfs in children_dfs {
        let mut c = 0u32;
        if let Some(a) = find_actor_by_dfs(actors, wl, cdfs, &mut c) {
            if !child_entities.contains(&a.entity) {
                child_entities.push(a.entity);
            }
        }
    }

    // ── 2. 子サブツリーを取り出してグループ配下へ移す ──
    group.world_line = wl;
    for ce in child_entities {
        // 挿入先の親自身を子として取り込むと親子関係が壊れるので除外する
        if Some(ce) == parent_entity { continue; }
        if let Some(child) = extract_actor_by_entity(actors, ce) {
            group.children_mut().push(child);
        }
    }

    // ── 3. グループ本体を親（またはルート）へ挿入する ──
    if let Some(pe) = parent_entity {
        if let Some(parent) = find_actor_by_entity_mut(actors, pe) {
            parent.children_mut().push(group);
            return true;
        }
        // 親が見つからない（取り出しで消えた等）場合はルートへフォールバックする
        actors.push(group);
        return true;
    }
    actors.push(group);
    true
}


// ============================================================
//  親子付け替え（Reparent）— エディタ / スクリプト共用ロジック
//
//  エディタの handle_reparent_actor（DFS id 基準）と、スクリプトの
//  GameObject.SetParent / Instantiate(path, parent)（Entity 基準）の
//  両方から使う共通部分をここへ集約する。
//  「どのツリー操作を許すか」の判断が 2 か所へ分裂すると、片方だけ緩い
//  経路から不整合なツリー（3D の子を持つ 2D 親など）が生まれるため。
// ============================================================

/// 親子付け替えの種別整合ルール（2D / 3D）を検証する。
///
/// - `child_is_2d`  … 移動する子アクターが Actor2D か
/// - `new_parent`   … 新しい親の (is_2d, has_canvas)。`None` はルート（親なし）
///
/// ルール:
/// 1. 3D の子を 2D の親の下へは入れられない。
/// 2. 2D の子は「2D アクター」または「Canvas を持つ 3D アクター」の下にしか入れられない。
/// 3. ルート（親なし）へはどちらの種別も置ける。
///
/// 戻り値の `Err` は利用者向けメッセージ（エディタは IPC の LOAD_ERROR、
/// スクリプトは `[Script]` 警告としてそのまま出す）。
pub(super) fn validate_reparent_kind(
    child_is_2d: bool,
    new_parent:  Option<(bool, bool)>,
) -> Result<(), &'static str> {
    // ① 3D 子 → 2D 親を禁止
    if new_parent.map(|(is_2d, _)| is_2d).unwrap_or(false) && !child_is_2d {
        return Err("3Dアクターは2Dアクターの子にできません");
    }
    // ② 2D 子 → Canvas を持たない 3D 親を禁止
    if child_is_2d {
        if let Some((parent_is_2d, parent_has_canvas)) = new_parent {
            if !parent_is_2d && !parent_has_canvas {
                return Err("2DアクターはCanvasを持たない3Dアクターの子にできません");
            }
        }
    }
    Ok(())
}

/// ルートエンティティでアクターへの不変参照を取得する（world_line 不問・DFS 順の最初の一致）。
///
/// `find_actor_by_entity_mut` の読み取り専用版。種別判定（is_2d / has_canvas）や
/// 祖先判定のように「探すだけ」の用途で使う。
pub(super) fn find_actor_by_entity<'a>(
    actors: &'a [Actor],
    entity: Entity,
) -> Option<&'a Actor> {
    for actor in actors.iter() {
        if actor.entity == entity {
            return Some(actor);
        }
        if let Some(found) = find_actor_by_entity(actor.children(), entity) {
            return Some(found);
        }
    }
    None
}

/// `actor` 自身またはその子孫に `entity` が含まれるかを判定する。
///
/// 親子付け替えで「自分自身または自分の子孫を新しい親に指定する」＝
/// ツリーが循環（サブツリーの喪失）になるケースを弾くために使う。
pub(super) fn actor_subtree_contains_entity(actor: &Actor, entity: Entity) -> bool {
    if actor.entity == entity {
        return true;
    }
    actor.children().iter().any(|c| actor_subtree_contains_entity(c, entity))
}

/// アクターの種別情報 (is_2d, has_canvas) を取り出す（親候補の検証用）。
fn actor_kind_info(actor: &Actor) -> (bool, bool) {
    (actor.is_2d(), actor.has_kind(ComponentKind::Canvas))
}

/// 親子付け替えの拒否理由。呼び出し側がログ／IPC メッセージを組み立てる。
#[derive(Debug, PartialEq, Eq)]
pub(super) enum ReparentRejection {
    /// 対象の子アクターが現在のツリーに存在しない（破棄済み等）
    ChildNotFound,
    /// 指定された新しい親が現在のツリーに存在しない（破棄済み等）
    ParentNotFound,
    /// 新しい親が自分自身または自分の子孫（＝ツリーが循環する）
    Cycle,
    /// 2D / 3D の種別が不整合（メッセージは validate_reparent_kind 由来）
    KindMismatch(&'static str),
}

impl ReparentRejection {
    /// ログ表示用の理由文字列。
    pub(super) fn message(&self) -> &'static str {
        match self {
            ReparentRejection::ChildNotFound  => "対象アクターがシーンに存在しません",
            ReparentRejection::ParentNotFound => "指定した親アクターがシーンに存在しません",
            ReparentRejection::Cycle          => "自分自身または子孫を親にはできません",
            ReparentRejection::KindMismatch(m) => m,
        }
    }
}

/// Entity 基準でアクターの親を付け替える（新しい親の **末尾の子** になる）。
///
/// スクリプトの `GameObject.SetParent` 用。エディタ経路（DFS id + アンカー兄弟指定）と
/// 違い、挿入位置は常に末尾で、Undo 記録も行わない純粋なツリー操作。
///
/// # 変換について
/// - 3D: 子アクターの `Transform` は **ワールド空間** で保持されるため、
///   ツリー上の位置を変えても値を触る必要がない（ワールド変換が自動的に保たれる）。
/// - 2D: `CanvasTransform` は **親相対** のため、値をそのまま保つと画面上の位置は
///   新しい親を基準に変わる（ローカル値の維持を仕様とする）。
///
/// # 検証
/// - 子・親がツリーに存在すること
/// - 新しい親が子自身／子の子孫でないこと（循環防止）
/// - 2D / 3D の種別整合（`validate_reparent_kind`）
///
/// 検証に通らない場合はツリーを一切変更せず `Err` を返す。
pub(super) fn reparent_actor_by_entity(
    actors:     &mut Vec<Actor>,
    child:      Entity,
    new_parent: Option<Entity>,
) -> Result<(), ReparentRejection> {
    // ── 1. 子の存在確認と種別取得（ツリーを触る前に検証を済ませる）──
    let child_actor = find_actor_by_entity(actors, child)
        .ok_or(ReparentRejection::ChildNotFound)?;
    let child_is_2d = child_actor.is_2d();

    // ── 2. 親の存在確認・循環判定・種別整合 ──
    let parent_info = match new_parent {
        Some(pe) => {
            // 自分自身または子孫を親にすると、取り出したサブツリーごと行方不明になる
            if actor_subtree_contains_entity(child_actor, pe) {
                return Err(ReparentRejection::Cycle);
            }
            let parent_actor = find_actor_by_entity(actors, pe)
                .ok_or(ReparentRejection::ParentNotFound)?;
            Some(actor_kind_info(parent_actor))
        }
        None => None,
    };
    validate_reparent_kind(child_is_2d, parent_info)
        .map_err(ReparentRejection::KindMismatch)?;

    // ── 3. 取り出して挿入する（ここから先は失敗しない）──
    let Some(mut extracted) = extract_actor_by_entity(actors, child) else {
        return Err(ReparentRejection::ChildNotFound);
    };
    match new_parent {
        Some(pe) => {
            // 親の world_line へ合わせてサブツリー全体を移す
            //（エディタ経路が active_world_line へ揃えるのと同じ意図）
            let parent_wl = find_actor_by_entity(actors, pe).map(|a| a.world_line);
            if let Some(wl) = parent_wl {
                extracted.set_world_line_recursive(wl);
            }
            match find_actor_by_entity_mut(actors, pe) {
                Some(parent) => parent.children_mut().push(extracted),
                // 直前に存在を確認済みなので通常は到達しない（保険としてルートへ戻す）
                None => actors.push(extracted),
            }
        }
        None => actors.push(extracted),
    }
    Ok(())
}

/// 生成済みアクターを指定親の **末尾の子** として取り付ける。
///
/// スクリプトの `GameObject.Instantiate(path, parent)` 用。
/// 親が指定されていない／見つからない／種別が不整合な場合はルート（`actors` 直下）へ
/// 追加し、`false` を返す（呼び出し側が `[Script]` 警告を出す）。
/// 成功（親の下へ入った）なら `true`。
pub(super) fn attach_actor_under(
    actors: &mut Vec<Actor>,
    parent: Option<Entity>,
    actor:  Actor,
) -> bool {
    let Some(pe) = parent else {
        actors.push(actor);
        return true; // 親指定なし＝ルートが期待どおりの配置
    };
    // 種別整合を先に判定する（不整合ならルートへフォールバック）
    let ok_kind = find_actor_by_entity(actors, pe)
        .map(|p| validate_reparent_kind(actor.is_2d(), Some(actor_kind_info(p))).is_ok())
        .unwrap_or(false);
    if !ok_kind {
        actors.push(actor);
        return false;
    }
    let mut actor = actor;
    if let Some(wl) = find_actor_by_entity(actors, pe).map(|a| a.world_line) {
        actor.set_world_line_recursive(wl);
    }
    match find_actor_by_entity_mut(actors, pe) {
        Some(p) => { p.children_mut().push(actor); true }
        None    => { actors.push(actor); false }
    }
}

// ============================================================
//  テスト — グループ（フォルダ）アクタ生成のツリー操作
// ============================================================
#[cfg(test)]
mod group_actor_tests {
    use super::*;
    use crate::engine::ecs::World;

    /// world_line=wl のルートアクタを名前付きで作る（テスト用ヘルパ）。
    fn root(world: &mut World, wl: u32, name: &str) -> Actor {
        let mut a = Actor::new(world.spawn(), name);
        a.world_line = wl;
        a
    }

    /// グループ用フォルダアクタを作る（テスト用ヘルパ）。
    fn folder(world: &mut World, wl: u32, name: &str) -> Actor {
        let mut g = Actor::new_folder(world.spawn(), name);
        g.world_line = wl;
        g
    }

    /// 2D グループ用フォルダアクタを作る（テスト用ヘルパ）。
    /// `handle_create_group` の 2D 分岐と同じ手順（単位 CanvasTransform + new_folder_2d）。
    fn folder_2d(world: &mut World, wl: u32, name: &str) -> Actor {
        let e = world.spawn();
        world.insert(e, crate::engine::components::CanvasTransform::default());
        let mut g = Actor::new_folder_2d(e, name);
        g.world_line = wl;
        g
    }

    /// 2D アクタ（ルート）を作る（テスト用ヘルパ）。
    fn root_2d(world: &mut World, wl: u32, name: &str) -> Actor {
        let e = world.spawn();
        world.insert(e, crate::engine::components::CanvasTransform::default());
        let mut a = Actor::new_2d(e, name);
        a.world_line = wl;
        a
    }

    /// ModelComponent が 1 つも無いツリー（＝旧実装で何も起きなかったケース）でも
    /// グループアクタが生えることを検証する。
    #[test]
    fn create_group_on_tree_without_model_component() {
        let mut world  = World::new();
        let mut actors = vec![root(&mut world, 0, "Camera")];
        let group      = folder(&mut world, 0, "Group");

        assert!(insert_group_actor(&mut actors, 0, group, None, &[]));

        assert_eq!(actors.len(), 2);
        assert_eq!(actors[1].name, "Group");
        assert!(actors[1].is_folder());
    }

    /// 選択した子（兄弟複数）をまとめてグループ配下へ移せることを検証する。
    /// DFS id は取り出しのたびにズレるため、Entity 解決が効いているかの確認でもある。
    #[test]
    fn create_group_with_multiple_children() {
        let mut world = World::new();
        let mut actors = vec![
            root(&mut world, 0, "A"),
            root(&mut world, 0, "B"),
            root(&mut world, 0, "C"),
        ];
        let group = folder(&mut world, 0, "Group");

        // DFS id: A=0 / B=1 / C=2。B と C をグループへ入れる。
        assert!(insert_group_actor(&mut actors, 0, group, None, &[1, 2]));

        // ルートは A と Group の 2 体、Group の下に B・C が選択順で入る
        assert_eq!(actors.len(), 2);
        assert_eq!(actors[0].name, "A");
        assert_eq!(actors[1].name, "Group");
        let kids: Vec<&str> = actors[1].children().iter().map(|c| c.name.as_str()).collect();
        assert_eq!(kids, vec!["B", "C"]);
    }

    /// 親子関係にある 2 体（親 P と子 C）を同時に選んでも壊れないことを検証する。
    /// P を取り出した時点で C は P のサブツリーに含まれるため、C は二重に移動しない。
    #[test]
    fn create_group_with_ancestor_and_descendant_selected() {
        let mut world = World::new();
        let mut p = root(&mut world, 0, "P");
        let mut c = Actor::new(world.spawn(), "C");
        c.world_line = 0;
        p.children_mut().push(c);
        let mut actors = vec![p];
        let group = folder(&mut world, 0, "Group");

        // DFS id: P=0 / C=1
        assert!(insert_group_actor(&mut actors, 0, group, None, &[0, 1]));

        assert_eq!(actors.len(), 1);
        assert_eq!(actors[0].name, "Group");
        assert_eq!(actors[0].children().len(), 1);
        assert_eq!(actors[0].children()[0].name, "P");
        assert_eq!(actors[0].children()[0].children()[0].name, "C");
    }

    /// 親 DFS id を指定した場合、そのアクタの子としてグループが生えることを検証する。
    #[test]
    fn create_group_under_parent() {
        let mut world  = World::new();
        let mut actors = vec![root(&mut world, 0, "P"), root(&mut world, 0, "Q")];
        let group      = folder(&mut world, 0, "Group");

        // DFS id: P=0 / Q=1。P の子としてグループを作る。
        assert!(insert_group_actor(&mut actors, 0, group, Some(0), &[]));

        assert_eq!(actors.len(), 2);
        assert_eq!(actors[0].children().len(), 1);
        assert_eq!(actors[0].children()[0].name, "Group");
    }

    /// 別世界線のアクタは DFS 数え上げの対象外であることを検証する
    /// （アクター編集タブで作ったグループがシーン側のアクタを巻き込まない）。
    #[test]
    fn create_group_ignores_other_world_lines() {
        let mut world = World::new();
        let mut actors = vec![
            root(&mut world, 0, "SceneA"),
            root(&mut world, 1, "EditRoot"),
            root(&mut world, 1, "EditChild"),
        ];
        let group = folder(&mut world, 1, "Group");

        // wl=1 内の DFS id: EditRoot=0 / EditChild=1。EditChild だけを入れる。
        assert!(insert_group_actor(&mut actors, 1, group, None, &[1]));

        assert_eq!(actors.len(), 3);
        assert_eq!(actors[0].name, "SceneA");
        assert_eq!(actors[1].name, "EditRoot");
        assert_eq!(actors[2].name, "Group");
        assert_eq!(actors[2].world_line, 1);
        assert_eq!(actors[2].children()[0].name, "EditChild");
    }
    /// 2D アクタをまとめたグループが「2D フォルダノード」になることを検証する。
    ///
    /// 旧実装は通常の 2D アクタ（is_folder=false）を作っていたため、ヒエラルキーで
    /// フォルダとして扱われず Inspector にも Transform が出ていた。現在は
    /// is_folder=true / Actor2D / 単位 CanvasTransform 保持が不変条件。
    /// CanvasTransform を持つことは必須（キャンバス系の走査が CanvasTransform 無しの
    /// アクタでサブツリーを打ち切り、配下のスプライトが描画対象から外れるため）。
    #[test]
    fn create_group_for_2d_children_makes_2d_folder() {
        use crate::engine::components::CanvasTransform;
        let mut world = World::new();
        let mut actors = vec![
            root_2d(&mut world, 0, "SpriteA"),
            root_2d(&mut world, 0, "SpriteB"),
        ];
        let group        = folder_2d(&mut world, 0, "UIGroup");
        let group_entity = group.entity;

        // DFS id: SpriteA=0 / SpriteB=1。両方をグループへ入れる。
        assert!(insert_group_actor(&mut actors, 0, group, None, &[0, 1]));

        assert_eq!(actors.len(), 1, "2 体とも Group 配下へ移動しているはず");
        let g = &actors[0];
        assert_eq!(g.name, "UIGroup");
        assert!(g.is_folder(), "2D グループはフォルダノードでなければならない");
        assert!(g.is_2d(), "2D グループの種別は Actor2D でなければならない");
        assert!(world.contains::<CanvasTransform>(group_entity),
                "2D フォルダは単位 CanvasTransform を持たねばならない（走査の打ち切り防止）");
        let kids: Vec<&str> = g.children().iter().map(|c| c.name.as_str()).collect();
        assert_eq!(kids, vec!["SpriteA", "SpriteB"]);
    }

}


// ============================================================
//  テスト — 親子付け替え（Reparent）共用ロジック
//
//  スクリプトの GameObject.Instantiate(path, parent) / SetParent が使う
//  ツリー操作の適用ロジックを、App（GPU/シーン）非依存の純粋関数として検証する。
// ============================================================
#[cfg(test)]
mod reparent_tests {
    use super::*;
    use crate::engine::ecs::World;

    /// 3D アクタ（Transform 保持）を作る。
    fn actor3d(world: &mut World, name: &str) -> Actor {
        let e = world.spawn();
        world.insert(e, ActorTransform::default());
        Actor::new(e, name)
    }

    /// 2D アクタ（CanvasTransform 保持）を作る。
    fn actor2d(world: &mut World, name: &str) -> Actor {
        let e = world.spawn();
        world.insert(e, CanvasTransform::default());
        Actor::new_2d(e, name)
    }

    /// CanvasComponent を持つ 3D アクタ（3D 空間キャンバス）を作る。
    /// 2D アクタの親として許可される唯一の 3D 形態。
    fn actor3d_canvas(world: &mut World, name: &str) -> Actor {
        let mut a = actor3d(world, name);
        let slot  = world.spawn();
        world.insert(slot, CanvasComponent::default());
        a.add_slot_typed::<CanvasComponent>("Canvas", ComponentKind::Canvas, slot);
        a
    }

    /// 子アクタ名の一覧を取り出す（順序検証用）。
    fn child_names(actor: &Actor) -> Vec<&str> {
        actor.children().iter().map(|c| c.name.as_str()).collect()
    }

    // ── Instantiate(path, parent) 相当 ─────────────────────

    /// 親指定で生成したアクタが「末尾の子」として追加されること。
    #[test]
    fn attach_appends_as_last_child() {
        let mut world = World::new();
        let mut parent = actor3d(&mut world, "Parent");
        parent.add_child(actor3d(&mut world, "First"));
        let parent_e = parent.entity;
        let mut actors = vec![parent];

        let spawned = actor3d(&mut world, "Spawned");
        assert!(attach_actor_under(&mut actors, Some(parent_e), spawned));

        assert_eq!(actors.len(), 1, "ルートには親 1 体だけが残る");
        assert_eq!(child_names(&actors[0]), vec!["First", "Spawned"], "末尾へ追加される");
    }

    /// 親を指定しなければルート直下へ追加され、成功扱いになること。
    #[test]
    fn attach_without_parent_goes_to_root() {
        let mut world  = World::new();
        let mut actors = vec![actor3d(&mut world, "Existing")];
        let spawned    = actor3d(&mut world, "Spawned");

        assert!(attach_actor_under(&mut actors, None, spawned));
        assert_eq!(actors.len(), 2);
        assert_eq!(actors[1].name, "Spawned");
    }

    /// 親がシーンに存在しない（破棄済み）ときはルートへフォールバックし false を返すこと。
    #[test]
    fn attach_with_dead_parent_falls_back_to_root() {
        let mut world  = World::new();
        let mut actors = vec![actor3d(&mut world, "Existing")];
        let ghost      = world.spawn(); // ツリーに存在しないエンティティ
        let spawned    = actor3d(&mut world, "Spawned");

        assert!(!attach_actor_under(&mut actors, Some(ghost), spawned),
                "親が見つからない場合は false（呼び出し側が警告を出す）");
        assert_eq!(actors.len(), 2, "生成自体は成功しルート直下へ入る");
        assert_eq!(actors[1].name, "Spawned");
    }

    /// 種別不整合（3D の子 → 2D の親）はルートへフォールバックすること。
    #[test]
    fn attach_kind_mismatch_falls_back_to_root() {
        let mut world  = World::new();
        let parent     = actor2d(&mut world, "UIParent");
        let parent_e   = parent.entity;
        let mut actors = vec![parent];
        let spawned    = actor3d(&mut world, "Spawned3D");

        assert!(!attach_actor_under(&mut actors, Some(parent_e), spawned));
        assert_eq!(actors.len(), 2, "2D 親の下ではなくルートへ入る");
        assert!(actors[0].children().is_empty());
    }

    // ── SetParent 相当 ─────────────────────────────────────

    /// ルート直下のアクタを別のアクタの末尾の子へ移動できること。
    #[test]
    fn reparent_moves_to_new_parent_as_last_child() {
        let mut world = World::new();
        let mut parent = actor3d(&mut world, "Parent");
        parent.add_child(actor3d(&mut world, "First"));
        let parent_e = parent.entity;
        let child    = actor3d(&mut world, "Child");
        let child_e  = child.entity;
        let mut actors = vec![parent, child];

        assert_eq!(reparent_actor_by_entity(&mut actors, child_e, Some(parent_e)), Ok(()));
        assert_eq!(actors.len(), 1);
        assert_eq!(child_names(&actors[0]), vec!["First", "Child"]);
    }

    /// 子アクタをルート（親なし）へ戻せること。
    #[test]
    fn reparent_to_root_moves_out_of_parent() {
        let mut world = World::new();
        let mut parent = actor3d(&mut world, "Parent");
        let child      = actor3d(&mut world, "Child");
        let child_e    = child.entity;
        parent.add_child(child);
        let mut actors = vec![parent];

        assert_eq!(reparent_actor_by_entity(&mut actors, child_e, None), Ok(()));
        assert_eq!(actors.len(), 2, "ルート直下へ出る");
        assert_eq!(actors[1].name, "Child");
        assert!(actors[0].children().is_empty());
    }

    /// 自分の子孫を親に指定した場合は拒否され、ツリーが変化しないこと（循環防止）。
    #[test]
    fn reparent_rejects_descendant_as_parent() {
        let mut world  = World::new();
        let mut parent = actor3d(&mut world, "Parent");
        let mut mid    = actor3d(&mut world, "Mid");
        let leaf       = actor3d(&mut world, "Leaf");
        let leaf_e     = leaf.entity;
        mid.add_child(leaf);
        parent.add_child(mid);
        let parent_e   = parent.entity;
        let mut actors = vec![parent];

        assert_eq!(
            reparent_actor_by_entity(&mut actors, parent_e, Some(leaf_e)),
            Err(ReparentRejection::Cycle)
        );
        // ツリーは無変更（Parent > Mid > Leaf のまま）
        assert_eq!(actors.len(), 1);
        assert_eq!(child_names(&actors[0]), vec!["Mid"]);
        assert_eq!(child_names(&actors[0].children()[0]), vec!["Leaf"]);
    }

    /// 自分自身を親に指定した場合も循環として拒否されること。
    #[test]
    fn reparent_rejects_self_as_parent() {
        let mut world  = World::new();
        let a          = actor3d(&mut world, "A");
        let a_e        = a.entity;
        let mut actors = vec![a];

        assert_eq!(
            reparent_actor_by_entity(&mut actors, a_e, Some(a_e)),
            Err(ReparentRejection::Cycle)
        );
        assert_eq!(actors.len(), 1);
    }

    /// 3D アクタを 2D アクタの子にはできないこと。
    #[test]
    fn reparent_rejects_3d_child_under_2d_parent() {
        let mut world  = World::new();
        let ui         = actor2d(&mut world, "UI");
        let ui_e       = ui.entity;
        let obj        = actor3d(&mut world, "Obj3D");
        let obj_e      = obj.entity;
        let mut actors = vec![ui, obj];

        let err = reparent_actor_by_entity(&mut actors, obj_e, Some(ui_e)).unwrap_err();
        assert_eq!(err, ReparentRejection::KindMismatch("3Dアクターは2Dアクターの子にできません"));
        assert_eq!(actors.len(), 2, "ツリーは変化しない");
    }

    /// 2D アクタは Canvas を持たない 3D アクタの子にできないこと。
    #[test]
    fn reparent_rejects_2d_child_under_plain_3d_parent() {
        let mut world  = World::new();
        let obj        = actor3d(&mut world, "Obj3D");
        let obj_e      = obj.entity;
        let ui         = actor2d(&mut world, "UI");
        let ui_e       = ui.entity;
        let mut actors = vec![obj, ui];

        let err = reparent_actor_by_entity(&mut actors, ui_e, Some(obj_e)).unwrap_err();
        assert_eq!(
            err,
            ReparentRejection::KindMismatch("2DアクターはCanvasを持たない3Dアクターの子にできません")
        );
        assert_eq!(actors.len(), 2);
    }

    /// 2D アクタは Canvas を持つ 3D アクタの子にはできること（3D 空間 UI）。
    #[test]
    fn reparent_allows_2d_child_under_canvas_3d_parent() {
        let mut world  = World::new();
        let canvas     = actor3d_canvas(&mut world, "WorldCanvas");
        let canvas_e   = canvas.entity;
        let ui         = actor2d(&mut world, "UI");
        let ui_e       = ui.entity;
        let mut actors = vec![canvas, ui];

        assert_eq!(reparent_actor_by_entity(&mut actors, ui_e, Some(canvas_e)), Ok(()));
        assert_eq!(actors.len(), 1);
        assert_eq!(child_names(&actors[0]), vec!["UI"]);
    }

    /// 対象・親がシーンに存在しない場合はそれぞれの理由で拒否されること。
    #[test]
    fn reparent_rejects_missing_actors() {
        let mut world  = World::new();
        let a          = actor3d(&mut world, "A");
        let a_e        = a.entity;
        let mut actors = vec![a];
        let ghost      = world.spawn();

        assert_eq!(
            reparent_actor_by_entity(&mut actors, ghost, None),
            Err(ReparentRejection::ChildNotFound)
        );
        assert_eq!(
            reparent_actor_by_entity(&mut actors, a_e, Some(ghost)),
            Err(ReparentRejection::ParentNotFound)
        );
        assert_eq!(actors.len(), 1);
    }

    /// 付け替えても子孫サブツリーは丸ごと保たれること（末尾の子として移動する）。
    #[test]
    fn reparent_keeps_subtree_intact() {
        let mut world  = World::new();
        let mut child  = actor3d(&mut world, "Child");
        child.add_child(actor3d(&mut world, "GrandChild"));
        let child_e    = child.entity;
        let parent     = actor3d(&mut world, "NewParent");
        let parent_e   = parent.entity;
        let mut actors = vec![parent, child];

        assert_eq!(reparent_actor_by_entity(&mut actors, child_e, Some(parent_e)), Ok(()));
        let moved = &actors[0].children()[0];
        assert_eq!(moved.name, "Child");
        assert_eq!(child_names(moved), vec!["GrandChild"]);
    }

    /// 種別整合ルール（validate_reparent_kind）の真理値表。
    /// ルートへの移動はどちらの種別でも常に許可される。
    #[test]
    fn validate_reparent_kind_truth_table() {
        assert!(validate_reparent_kind(false, None).is_ok(), "3D → ルート");
        assert!(validate_reparent_kind(true,  None).is_ok(), "2D → ルート");
        assert!(validate_reparent_kind(false, Some((false, false))).is_ok(), "3D → 素の 3D");
        assert!(validate_reparent_kind(false, Some((true,  false))).is_err(), "3D → 2D は不可");
        assert!(validate_reparent_kind(true,  Some((true,  false))).is_ok(), "2D → 2D");
        assert!(validate_reparent_kind(true,  Some((false, true ))).is_ok(), "2D → Canvas 付き 3D");
        assert!(validate_reparent_kind(true,  Some((false, false))).is_err(), "2D → 素の 3D は不可");
    }
}
