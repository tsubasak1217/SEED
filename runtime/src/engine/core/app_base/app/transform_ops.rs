// ============================================================
//  transform_ops.rs — トランスフォーム適用・設定操作
//
//  apply_set_transform / handle_set_actor_transform /
//  handle_set_canvas_transform / handle_set_canvas_size /
//  actor_virtual_world_pos
// ============================================================

use crate::engine::components::{ModelComponent, Transform as ActorTransform, ComponentKind, CanvasTransform, CanvasComponent, CanvasViewportRef};
use crate::engine::core::app_base::undo::{TransformCommand, ActorGroupTransformCommand};
use crate::engine::structs::tensor::Vector3;
use crate::engine::structs::transforms::Transform;

use crate::engine::methods::gizmo_interact::{mat4x4_mul, mat4x4_inv};

use super::{
    App,
    find_actor_by_dfs, find_actor_by_dfs_mut,
    apply_delta_to_actor_children,
    canvas_anchor_offset_for_dfs,
    RuntimeMode,
};

/// キャンバス座標（ピクセル）→ 3D ワールド座標の変換スケール係数。
/// ワールドスペースモード時に CanvasTransform 座標をワールド空間にスケールするために使う。
const CANVAS_WORLD_SCALE: f32 = 1.0 / 100.0;

impl App {
    /// MC インスタンスのトランスフォームを行列形式で適用する（旧スタイル SET_TRANSFORM 用）。
    ///
    /// id: インスタンス ID、px/py/pz: 位置、ex/ey/ez: オイラー角（度）、sx/sy/sz: スケール。
    /// アクター編集モードでは ActorTransform と子アクター MC への伝播も行う。
    pub(super) fn apply_set_transform(&mut self, id: u32, px: f32, py: f32, pz: f32,
                           ex: f32, ey: f32, ez: f32, sx: f32, sy: f32, sz: f32) {
        let i = id as usize;
        let wl           = self.active_world_line;
        let selected_dfs = self.actor_virtual_selected_idx;

        const RAD: f32 = std::f32::consts::PI / 180.0;
        let q = Vector3::new(ex * RAD, ey * RAD, ez * RAD).to_quaternion();
        let transform = Transform {
            position: Vector3::new(px, py, pz),
            rotation: q,
            scale:    Vector3::new(sx, sy, sz),
        };
        let new_mat = transform.to_matrix().data;

        // Phase 1: 選択 MC のインスタンスを更新してデルタを計算
        let (old_mat, delta) = {
            let Some(scene) = &mut self.scene else { return };
            // Entity を先に取得（actors の不変借用を解放してから world を可変借用）
            let entity_opt = if wl != 0 {
                let dfs = match selected_dfs { Some(d) => d as u32, None => return };
                let mut c = 0u32;
                find_actor_by_dfs(&scene.actors, wl, dfs, &mut c).map(|a| a.entity)
            } else {
                scene.actors.iter().find(|a| a.world_line == wl).map(|a| a.entity)
            };
            let entity = match entity_opt { Some(e) => e, None => return };
            let mc = scene.world.get_mut::<ModelComponent>(entity);
            let Some(mc) = mc else { return };
            if i >= mc.instance_mats.len() { return; }
            let old_mat = mc.instance_mats[i];
            let delta   = mat4x4_mul(new_mat, mat4x4_inv(old_mat));
            mc.instance_mats[i] = new_mat;
            let descendants = mc.all_descendants(id);
            for d in descendants {
                if let Some(m) = mc.instance_mats.get_mut(d as usize) {
                    *m = mat4x4_mul(delta, *m);
                }
            }
            mc.mark_batch_dirty();
            (old_mat, delta)
        };

        // Phase 2: アクター編集モードでは actor.transform と子アクター MC を伝播する
        let (selected_old_tf, selected_new_tf, child_changes) = if wl != 0 {
            let dfs = match selected_dfs { Some(d) => d as u32, None => return };
            let Some(scene) = &mut self.scene else { return };
            // entity を先取得（actors の不変借用を解放）
            let entity = {
                let mut c = 0u32;
                match find_actor_by_dfs(&scene.actors, wl, dfs, &mut c).map(|a| a.entity) {
                    Some(e) => e,
                    None => return,
                }
            };
            let old_tf = scene.world.get::<ActorTransform>(entity).cloned().unwrap_or_default();
            let new_tf = ActorTransform::from_mat4(&mat4x4_mul(delta, old_tf.to_mat4()));
            if let Some(t) = scene.world.get_mut::<ActorTransform>(entity) { *t = new_tf.clone(); }
            let mut child_dfs_counter = dfs + 1;
            let mut child_changes = Vec::new();
            {
                // actors と world を別フィールドとして同時可変借用する
                let (actors, world) = (&mut scene.actors, &mut scene.world);
                let mut c2 = 0u32;
                if let Some(actor) = find_actor_by_dfs_mut(actors, wl, dfs, &mut c2) {
                    apply_delta_to_actor_children(actor, world, delta, &mut child_dfs_counter, &mut child_changes);
                }
            }
            (old_tf, new_tf, child_changes)
        } else {
            (ActorTransform::default(), ActorTransform::default(), Vec::new())
        };

        // Phase 3: ドラッグ中は EndTransformDrag でまとめて記録するためここでは記録しない
        if self.inspector_transform_drag.is_none() {
            if wl != 0 {
                let dfs = selected_dfs.unwrap() as u32;
                self.undo_history.record(Box::new(ActorGroupTransformCommand {
                    wl,
                    dfs_id: dfs,
                    old_tf: selected_old_tf,
                    new_tf: selected_new_tf,
                    transforms: vec![(id, old_mat, new_mat)],
                    child_transforms: child_changes,
                    extra_slot_transforms: vec![],
                }));
            } else {
                self.undo_history.record(Box::new(TransformCommand {
                    instance_idx: id,
                    old_mat,
                    new_mat,
                }));
            }
        }
    }

    /// 3D Actor の ActorTransform を更新する。
    ///
    /// DFS id で特定したアクターの Transform を設定し、
    /// MC インスタンスと子アクター MC へデルタを伝播する。
    pub(super) fn handle_set_actor_transform(
        &mut self,
        dfs_id: u32,
        px: f32, py: f32, pz: f32,
        ex: f32, ey: f32, ez: f32,
        sx: f32, sy: f32, sz: f32,
    ) {
        let wl = self.active_world_line;
        let new_tf = ActorTransform { position: [px, py, pz], rotation: [ex, ey, ez], scale: [sx, sy, sz] };
        // entity を先に取得して borrow を解放
        let entity = {
            let Some(scene) = &mut self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c).map(|a| a.entity)
        };
        let Some(entity) = entity else {
            if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
            return;
        };
        let old_tf = {
            let Some(scene) = &mut self.scene else { return };
            let old = scene.world.get::<ActorTransform>(entity).cloned().unwrap_or_default();
            if let Some(t) = scene.world.get_mut::<ActorTransform>(entity) { *t = new_tf.clone(); }
            old
        };

        // シーンモード・アクター編集モード共通: delta を選択アクターの MC と子アクターに適用する
        let delta = mat4x4_mul(new_tf.to_mat4(), mat4x4_inv(old_tf.to_mat4()));
        // Phase A: 選択アクターの全 ModelComponent スロットに delta を適用する。
        // ModelComponent は actor.entity ではなく各スロット専用の entity に格納されているため、
        // スロット entity を先に収集してから world を可変借用する。
        let mc_transforms = if let Some(scene) = &mut self.scene {
            let slot_entities: Vec<crate::engine::ecs::Entity> = {
                let mut c = 0u32;
                find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c)
                    .map(|a| a.slots().iter()
                        .filter(|s| s.kind == ComponentKind::Model)
                        .map(|s| s.entity)
                        .collect())
                    .unwrap_or_default()
            };
            let mut all_changes: Vec<(u32, [[f32;4];4], [[f32;4];4])> = Vec::new();
            for slot_entity in slot_entities {
                if let Some(mc) = scene.world.get_mut::<ModelComponent>(slot_entity) {
                    let old_mats = mc.instance_mats.clone();
                    for m in &mut mc.instance_mats { *m = mat4x4_mul(delta, *m); }
                    mc.mark_batch_dirty();
                    for (i, &old) in old_mats.iter().enumerate() {
                        if let Some(new) = mc.instance_mats.get(i).copied() {
                            if new != old { all_changes.push((i as u32, old, new)); }
                        }
                    }
                }
            }
            all_changes
        } else { Vec::new() };
        // Phase B: 子アクターに delta を伝播
        let mut child_changes = Vec::new();
        if let Some(scene) = &mut self.scene {
            let (actors, world) = (&mut scene.actors, &mut scene.world);
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(actors, wl, dfs_id, &mut c) {
                let mut child_dfs_counter = dfs_id + 1;
                apply_delta_to_actor_children(actor, world, delta, &mut child_dfs_counter, &mut child_changes);
            }
        }
        let (mc_transforms, child_changes) = (mc_transforms, child_changes);

        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
        // ドラッグ中は EndTransformDrag でまとめて1コマンド記録するためここでは記録しない
        // シーンモード・アクター編集モード共通: MC 変化または Transform 変化があれば記録する
        if self.inspector_transform_drag.is_none() {
            if !mc_transforms.is_empty() || !child_changes.is_empty() || old_tf != new_tf {
                self.undo_history.record(Box::new(ActorGroupTransformCommand {
                    wl, dfs_id,
                    old_tf: old_tf.clone(), new_tf: new_tf.clone(),
                    transforms: mc_transforms,
                    child_transforms: child_changes,
                    extra_slot_transforms: vec![],
                }));
            }
            self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
        }
    }

    /// 2D Actor の CanvasTransform を更新する。
    ///
    /// 3D の handle_set_actor_transform と異なり、MC への delta 適用は行わない。
    /// CanvasTransform は位置・回転・スケール・ピボットを独立して保持するためデルタ計算不要。
    pub(super) fn handle_set_canvas_transform(
        &mut self,
        dfs_id: u32,
        px: f32, py: f32, rotation: f32, sx: f32, sy: f32,
        pivot_x: f32, pivot_y: f32,
    ) {
        let wl = self.active_world_line;

        // entity を先に取得して borrow を解放
        let entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c).map(|a| a.entity)
        };
        let Some(entity) = entity else { return };

        // CanvasTransform を更新する（位置・回転・スケール・ピボット）
        let old_ct = if let Some(scene) = &mut self.scene {
            let old = scene.world.get::<CanvasTransform>(entity).cloned().unwrap_or_default();
            if let Some(ct) = scene.world.get_mut::<CanvasTransform>(entity) {
                ct.position = [px, py];
                ct.rotation = rotation;
                ct.scale    = [sx, sy];
                ct.pivot    = [pivot_x, pivot_y];
            }
            old
        } else {
            return;
        };

        // ドラッグ中でなければ即時 Undo 記録する（ドラッグ中は EndTransformDrag でまとめて記録）
        if self.inspector_transform_drag.is_none() {
            let new_ct = CanvasTransform {
                position: [px, py],
                rotation,
                scale: [sx, sy],
                pivot:  [pivot_x, pivot_y],
                // anchor は handle_set_canvas_transform では変更しないため旧値を引き継ぐ
                anchor: old_ct.anchor,
            };
            let changed = new_ct.position != old_ct.position
                || new_ct.rotation != old_ct.rotation
                || new_ct.scale    != old_ct.scale
                || new_ct.pivot    != old_ct.pivot;
            if changed {
                self.undo_history.record(Box::new(
                    crate::engine::core::app_base::undo::CanvasTransformCommand {
                        world_line: wl, dfs_id, old_ct, new_ct,
                    }
                ));
            }
        }

        // インスペクターを最新値で更新し、シーン変更を通知する
        self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// CanvasComponent のスケールモードを更新する。
    ///
    /// scale_transform: 子UIの位置にスケールを適用するか
    /// scale_size:      子UIのサイズにスケールを適用するか
    pub(super) fn handle_set_canvas_scale_mode(
        &mut self,
        actor_dfs_id:    u32,
        slot_idx:        u32,
        scale_transform: bool,
        scale_size:      bool,
    ) {
        let wl = self.active_world_line;

        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .map(|s| s.entity)
        };
        let Some(slot_entity) = slot_entity else { return };

        if let Some(scene) = &mut self.scene {
            if let Some(cc) = scene.world.get_mut::<CanvasComponent>(slot_entity) {
                cc.scale_transform = scale_transform;
                cc.scale_size      = scale_size;
            }
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// CanvasComponent のサイズを更新する。
    pub(super) fn handle_set_canvas_size(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        width:        f32,
        height:       f32,
    ) {
        let wl = self.active_world_line;

        // スロットの entity を取得する
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .map(|s| s.entity)
        };
        let Some(slot_entity) = slot_entity else { return };

        // CanvasComponent を更新する
        if let Some(scene) = &mut self.scene {
            if let Some(cc) = scene.world.get_mut::<CanvasComponent>(slot_entity) {
                cc.width  = width;
                cc.height = height;
            }
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 仮想選択中のアクターのワールド座標を返す。
    /// シーンモード（wl==0）・アクター編集モード（wl!=0）共通で動作する。
    /// 2D キャンバスモードでは CanvasTransform.position を [px, py, 0.0] として返す。
    pub(super) fn actor_virtual_world_pos(&self) -> Option<[f32; 3]> {
        let dfs_id = self.actor_virtual_selected_idx? as u32;
        let wl = self.active_world_line;
        let scene = self.scene.as_ref()?;
        let mut c = 0u32;
        let actor = find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c)?;
        // canvas_world_lines（シーン全体の 2D フラグ）ではなくアクター個別の種別で判定する。
        // 3D/2D 混在シーンで 3D アクターを選んでも正しく ActorTransform を参照できる。
        if actor.is_2d() {
            let ct = scene.world.get::<CanvasTransform>(actor.entity)?;
            let off = canvas_anchor_offset_for_dfs(&scene.actors, &scene.world, wl, dfs_id);
            // ワールドスペースモード（エディタでスクリーンスペース OFF かつ 3D シーン世界線）では
            // 座標をスケールして 3D 空間へ変換し、Y 軸（下向き→上向き）を反転する
            let in_editor = self.mode == RuntimeMode::Edit || self.paused;
            let use_ss = self.canvas_screen_space_overlay || !in_editor
                || self.actor_edit_canvas_wls.contains(&wl);
            let ws     = if !use_ss { CANVAS_WORLD_SCALE } else { 1.0 };
            let y_sign = if !use_ss { -1.0f32 } else { 1.0 };
            Some([(ct.position[0] + off[0]) * ws,
                  (ct.position[1] + off[1]) * ws * y_sign,
                  0.0])
        } else {
            Some(scene.world.get::<ActorTransform>(actor.entity)?.position)
        }
    }

    /// CanvasComponent の画面サイズ自動スケールフラグを更新する。
    pub(super) fn handle_set_canvas_auto_scale(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        auto_scale:   bool,
    ) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .map(|s| s.entity)
        };
        let Some(slot_entity) = slot_entity else { return };
        if let Some(scene) = &mut self.scene {
            if let Some(cc) = scene.world.get_mut::<CanvasComponent>(slot_entity) {
                cc.auto_scale = auto_scale;
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// キャンバスのビューポート参照をウィンドウ（デフォルト）に戻す。
    pub(super) fn handle_set_canvas_viewport_ref_window(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
    ) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Canvas)
                .map(|s| s.entity)
        };
        let Some(slot_entity) = slot_entity else { return };
        if let Some(scene) = &mut self.scene {
            if let Some(cc) = scene.world.get_mut::<CanvasComponent>(slot_entity) {
                cc.viewport_ref = CanvasViewportRef::Window;
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// キャンバスのビューポート参照を指定カメラに設定する。
    ///
    /// `cam_actor_name` / `cam_slot_name` で参照カメラを特定する。
    /// 実際に該当カメラが存在するかのバリデーションはここでは行わない
    /// （シーンリロード後に参照が無効になることがある）。
    pub(super) fn handle_set_canvas_viewport_ref_camera(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        cam_actor_name: String,
        cam_slot_name:  String,
    ) {
        let wl = self.active_world_line;
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::Canvas)
                .map(|s| s.entity)
        };
        let Some(slot_entity) = slot_entity else { return };
        if let Some(scene) = &mut self.scene {
            if let Some(cc) = scene.world.get_mut::<CanvasComponent>(slot_entity) {
                cc.viewport_ref = CanvasViewportRef::Camera {
                    actor_name: cam_actor_name,
                    slot_name:  cam_slot_name,
                };
            }
        }
        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }
}
