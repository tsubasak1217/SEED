// ============================================================
//  ipc_handler.rs — IPC コマンド受信・ディスパッチ
//
//  process_ipc() はすべての IpcCommand を受け取り、
//  対応する App メソッドへルーティングする。
// ============================================================

use crate::engine::core::app_base::ipc::{IpcCommand, ToolMode};
use crate::engine::core::app_base::scene::{Scene, DebugCameraData};
use crate::engine::core::app_base::undo::{
    UndoHistory, TransformCommand, SceneSnapshotCommand, ActorTreeSnapshotCommand,
    SelectionCommand, ActorDfsSelectionCommand,
};
use crate::engine::components::{ModelComponent, GroupMeta, GROUP_ID_BASE};
use crate::engine::structs::objects::Actor;
use winit::event_loop::ActiveEventLoop;

use super::{
    App,
    find_actor_by_dfs,
    release_window_clamp,
    apply_window_clamp,
    camera_grab_start,
    camera_grab_end,
    collect_entities_for_wl,
    despawn_actor_recursive,
};
use crate::engine::core::app_base::scene::build_actor;

impl App {
    /// IPC コマンドをすべて収集してから順に処理する。
    ///
    /// &self.ipc の不変借用を処理ループ内に持ち込まないことで、
    /// apply_delete 等の &mut self メソッド呼び出しを可能にする。
    pub(super) fn process_ipc(&mut self, event_loop: &ActiveEventLoop) {
        let cmds: Vec<_> = {
            let Some(ipc) = &self.ipc else { return };
            std::iter::from_fn(|| ipc.try_recv()).collect()
        };
        for cmd in cmds {
            match cmd {
                IpcCommand::CtrlDown           => self.ctrl_held = true,
                IpcCommand::CtrlUp             => self.ctrl_held = false,
                IpcCommand::Pause              => self.paused = true,
                IpcCommand::Resume             => self.paused = false,
                IpcCommand::Stop               => event_loop.exit(),
                IpcCommand::CamKeyDown(k)      => self.cam_input.set_key(&k, true),
                IpcCommand::CamKeyUp(k)        => self.cam_input.set_key(&k, false),
                IpcCommand::SetToolMode(m)     => self.tool_mode = m,
                IpcCommand::PlayClamp(v)       => {
                    self.play_clamp = v;
                    if !v { release_window_clamp(); }
                }
                IpcCommand::Undo => {
                    let result = if let Some(scene) = &mut self.scene {
                        self.undo_history.undo(scene)
                    } else { None };
                    if let Some((structural, sel)) = result {
                        // アクターツリー再構築
                        if let Some((wl, actors_data)) = self.undo_history.peek_undone_actor_rebuild() {
                            self.rebuild_actors_for_wl(wl, actors_data);
                        }
                        // コンポーネントスロット再構築
                        if let Some((wl, actor_dfs_id, slots_data)) = self.undo_history.peek_undone_component_rebuild() {
                            self.rebuild_actor_slots(wl, actor_dfs_id, slots_data);
                            self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
                        }
                        // アクタートランスフォーム変更 → インスペクター通知
                        if let Some((_wl, dfs_id)) = self.undo_history.peek_undone_actor_inspect() {
                            self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                        }
                        if let Some(ids) = sel {
                            self.selected_instances = ids;
                            self.send_selected();
                        }
                        // アクター DFS 選択の復元（ActorDfsSelectionCommand）
                        if let Some((dfs_ids, primary)) = self.undo_history.peek_undone_actor_dfs_selection() {
                            self.selected_actor_dfs_ids     = dfs_ids;
                            self.actor_virtual_selected_idx = primary;
                            self.selected_instances.clear();
                            self.send_selected();
                        }
                        if structural {
                            self.sync_anim_seeds();
                            self.send_hierarchy();
                        }
                    }
                }
                IpcCommand::Redo => {
                    let result = if let Some(scene) = &mut self.scene {
                        self.undo_history.redo(scene)
                    } else { None };
                    if let Some((structural, sel)) = result {
                        // アクターツリー再構築
                        if let Some((wl, actors_data)) = self.undo_history.peek_redone_actor_rebuild() {
                            self.rebuild_actors_for_wl(wl, actors_data);
                        }
                        // コンポーネントスロット再構築
                        if let Some((wl, actor_dfs_id, slots_data)) = self.undo_history.peek_redone_component_rebuild() {
                            self.rebuild_actor_slots(wl, actor_dfs_id, slots_data);
                            self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
                        }
                        // アクタートランスフォーム変更 → インスペクター通知
                        if let Some((_wl, dfs_id)) = self.undo_history.peek_redone_actor_inspect() {
                            self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                        }
                        if let Some(ids) = sel {
                            self.selected_instances = ids;
                            self.send_selected();
                        }
                        // アクター DFS 選択の復元（ActorDfsSelectionCommand）
                        if let Some((dfs_ids, primary)) = self.undo_history.peek_redone_actor_dfs_selection() {
                            self.selected_actor_dfs_ids     = dfs_ids;
                            self.actor_virtual_selected_idx = primary;
                            self.selected_instances.clear();
                            self.send_selected();
                        }
                        if structural {
                            self.sync_anim_seeds();
                            self.send_hierarchy();
                        }
                    }
                }
                IpcCommand::Copy  => { self.do_copy(); }
                IpcCommand::Paste => { self.do_paste(); }
                IpcCommand::Delete(ids) => {
                    self.apply_delete(&ids, false);
                }
                IpcCommand::DeleteRecursive(ids) => {
                    self.apply_delete(&ids, true);
                }
                IpcCommand::Select(idx) => {
                    if idx >= 999_000_000 {
                        // アクターツリーノード選択（シーンモード・アクター編集モード共通）
                        let actor_dfs = (idx - 999_000_000) as u32;
                        self.actor_virtual_selected_idx = Some(actor_dfs as usize);
                        self.selected_actor_dfs_ids     = vec![actor_dfs as usize];
                        self.selected_instances.clear();
                    } else {
                        // 後方互換: インスタンスインデックス直接指定（レガシーパス）
                        self.actor_virtual_selected_idx = None;
                        self.actor_virtual_selected_slot_idx = 0;
                        self.selected_actor_dfs_ids.clear();
                        self.selected_instances.clear();
                    }
                    self.send_selected();
                    // 仮想ノード選択時はインスペクタへ即時プッシュ
                    if idx >= 999_000_000 {
                        let actor_dfs = (idx - 999_000_000) as u32;
                        self.send_actor_components(actor_dfs, self.actor_virtual_selected_slot_idx);
                    }
                }
                IpcCommand::SelectMulti(ids) => {
                    // 仮想 ID (999_000_000 + dfs) をパースして selected_actor_dfs_ids を更新する
                    let dfs_ids: Vec<usize> = ids.iter()
                        .filter(|&&id| id >= 999_000_000)
                        .map(|&id| (id - 999_000_000) as usize)
                        .collect();
                    if !dfs_ids.is_empty() {
                        self.actor_virtual_selected_idx = dfs_ids.last().copied();
                        self.selected_actor_dfs_ids     = dfs_ids;
                        self.selected_instances.clear();
                    }
                    self.send_selected();
                    if let Some(idx) = self.actor_virtual_selected_idx {
                        self.send_actor_components(idx as u32, self.actor_virtual_selected_slot_idx);
                    }
                }
                IpcCommand::Reparent { child, new_parent } => {
                    self.handle_reparent_actor(child, new_parent);
                }
                IpcCommand::Rename { idx, name } => {
                    if let Some(scene) = &mut self.scene {
                        if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(self.active_world_line) {
                            if let Some(meta) = mc.instance_meta.get_mut(idx as usize) {
                                meta.name = name;
                            }
                        }
                    }
                    self.send_hierarchy();
                }
                IpcCommand::CreateGroup { name, parent } => {
                    if let Some(scene) = &mut self.scene {
                        if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(self.active_world_line) {
                            let id = mc.next_group_id;
                            mc.next_group_id += 1;
                            mc.group_meta.push(GroupMeta { id, name, parent });
                        }
                    }
                    self.send_hierarchy();
                }
                IpcCommand::CreateGroupWithChildren { name, parent, children } => {
                    if let Some(scene) = &mut self.scene {
                        if let Some(mc) = scene.find_component_in_world_line_mut::<ModelComponent>(self.active_world_line) {
                            let gid = mc.next_group_id;
                            mc.next_group_id += 1;
                            mc.group_meta.push(GroupMeta { id: gid, name, parent });
                            for child in children {
                                if child >= GROUP_ID_BASE {
                                    if let Some(g) = mc.group_meta.iter_mut().find(|g| g.id == child) {
                                        g.parent = Some(gid);
                                    }
                                } else if (child as usize) < mc.instance_meta.len() {
                                    mc.instance_meta[child as usize].parent = Some(gid);
                                }
                            }
                        }
                    }
                    self.send_hierarchy();
                }
                IpcCommand::SaveScene(path) => {
                    if let Some(scene) = &self.scene {
                        let pos = self.camera.base.transform.position;
                        let cam_data = DebugCameraData {
                            position: [pos.x, pos.y, pos.z],
                            yaw:      self.camera.yaw,
                            pitch:    self.camera.pitch,
                            fov_deg:  self.camera.base.projection.fov_y_rad.to_degrees(),
                            far:      self.camera.base.projection.far,
                            speed:    self.camera.move_speed,
                        };
                        match scene.save(std::path::Path::new(&path), &cam_data) {
                            Ok(())   => { if let Some(ipc) = &self.ipc { ipc.send("SAVE_OK"); } }
                            Err(e)   => { if let Some(ipc) = &self.ipc { ipc.send(&format!("SAVE_ERROR:{e}")); } }
                        }
                    }
                }
                IpcCommand::SaveActor(path) => {
                    let wl = self.active_world_line;
                    let result: Result<(), String> = (|| {
                        let scene = self.scene.as_ref().ok_or("シーンが読み込まれていません")?;
                        let actor = scene.actors.iter()
                            .find(|a| a.world_line == wl)
                            .ok_or("アクティブ世界線にアクターがありません")?;
                        let data = actor.to_data(&scene.world);
                        let json = serde_json::to_string_pretty(&data).map_err(|e| e.to_string())?;
                        std::fs::write(&path, json).map_err(|e| e.to_string())?;
                        Ok(())
                    })();
                    if let Some(ipc) = &self.ipc {
                        match result {
                            Ok(())   => ipc.send("SAVE_OK"),
                            Err(e)   => ipc.send(&format!("SAVE_ERROR:{e}")),
                        }
                    }
                }
                IpcCommand::BeginTransformDrag { is_actor, target_id } => {
                    let wl = self.active_world_line;
                    if !is_actor {
                        let old_mat = self.scene.as_ref()
                            .and_then(|s| s.find_component_in_world_line::<ModelComponent>(wl))
                            .and_then(|mc| mc.instance_mats.get(target_id as usize).copied());
                        if let Some(old_mat) = old_mat {
                            self.inspector_transform_drag = Some(super::InspectorTransformDrag::Instance {
                                idx: target_id, old_mat,
                            });
                        }
                    } else {
                        if wl != 0 {
                            let mut c = 0u32;
                            let snapshot = self.scene.as_ref().and_then(|s| {
                                find_actor_by_dfs(&s.actors, wl, target_id, &mut c).map(|actor| {
                                    let old_mats = actor.mc_entity()
                                        .and_then(|e| s.world.get::<ModelComponent>(e))
                                        .map(|mc| mc.instance_mats.clone())
                                        .unwrap_or_default();
                                    let old_tf = s.world.get::<crate::engine::components::Transform>(actor.entity)
                                        .cloned().unwrap_or_default();
                                    let mut child_old_states = Vec::new();
                                    let mut child_dfs = target_id + 1;
                                    super::collect_child_actor_old_states(actor, &s.world, &mut child_dfs, &mut child_old_states);
                                    (old_mats, old_tf, child_old_states)
                                })
                            });
                            if let Some((old_mats, old_tf, child_old_states)) = snapshot {
                                if !old_mats.is_empty() {
                                    self.inspector_transform_drag = Some(super::InspectorTransformDrag::ActorGroup {
                                        dfs_id: target_id, old_mats, old_tf, child_old_states,
                                    });
                                } else if self.canvas_world_lines.contains(&wl) {
                                    // 2D キャンバスアクター: CanvasTransform のスナップショットを記録する
                                    let old_ct = self.scene.as_ref().and_then(|s| {
                                        let mut c2 = 0u32;
                                        find_actor_by_dfs(&s.actors, wl, target_id, &mut c2)
                                            .and_then(|a| s.world.get::<crate::engine::components::CanvasTransform>(a.entity).cloned())
                                    });
                                    if let Some(old_ct) = old_ct {
                                        self.inspector_transform_drag = Some(super::InspectorTransformDrag::CanvasActor {
                                            wl, dfs_id: target_id, old_ct,
                                        });
                                    }
                                } else {
                                    self.inspector_transform_drag = Some(super::InspectorTransformDrag::Actor {
                                        wl, dfs_id: target_id, old_tf,
                                    });
                                }
                            }
                        } else {
                            let old_tf = self.scene.as_ref().and_then(|s| {
                                let mut c = 0u32;
                                find_actor_by_dfs(&s.actors, wl, target_id, &mut c)
                                    .and_then(|a| s.world.get::<crate::engine::components::Transform>(a.entity).cloned())
                            });
                            if let Some(old_tf) = old_tf {
                                self.inspector_transform_drag = Some(super::InspectorTransformDrag::Actor {
                                    wl, dfs_id: target_id, old_tf,
                                });
                            }
                        }
                    }
                }
                IpcCommand::EndTransformDrag => {
                    if let Some(drag) = self.inspector_transform_drag.take() {
                        let wl = self.active_world_line;
                        match drag {
                            super::InspectorTransformDrag::Instance { idx, old_mat } => {
                                if let Some(mc) = self.scene.as_ref()
                                    .and_then(|s| s.find_component_in_world_line::<ModelComponent>(wl))
                                {
                                    if let Some(&new_mat) = mc.instance_mats.get(idx as usize) {
                                        if old_mat != new_mat {
                                            self.undo_history.record(Box::new(TransformCommand {
                                                instance_idx: idx, old_mat, new_mat,
                                            }));
                                        }
                                    }
                                }
                            }
                            super::InspectorTransformDrag::Actor { wl: drag_wl, dfs_id, old_tf } => {
                                {
                                    if let Some(scene) = &self.scene {
                                        let mut c = 0u32;
                                        let new_tf_opt = find_actor_by_dfs(&scene.actors, drag_wl, dfs_id, &mut c)
                                            .and_then(|a| scene.world.get::<crate::engine::components::Transform>(a.entity).cloned());
                                        if let Some(new_tf) = new_tf_opt {
                                            if old_tf != new_tf {
                                                self.undo_history.record(Box::new(crate::engine::core::app_base::undo::ActorTransformCommand {
                                                    world_line: drag_wl, dfs_id,
                                                    old_transform: old_tf, new_transform: new_tf,
                                                }));
                                            }
                                        }
                                    }
                                }
                                self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                            }
                            super::InspectorTransformDrag::ActorGroup { dfs_id, old_mats, old_tf, child_old_states } => {
                                let mut c = 0u32;
                                let transforms: Vec<(u32, [[f32;4];4], [[f32;4];4])>;
                                let new_tf: crate::engine::components::Transform;
                                let child_transforms: Vec<(u32, crate::engine::components::Transform, crate::engine::components::Transform, [[f32;4];4], [[f32;4];4])>;
                                {
                                    let scene_ref = self.scene.as_ref();
                                    let mc_opt = scene_ref.and_then(|s| {
                                        let mut c_ = c;
                                        find_actor_by_dfs(&s.actors, wl, dfs_id, &mut c_)
                                            .and_then(|a| a.mc_entity())
                                            .and_then(|e| s.world.get::<ModelComponent>(e))
                                    });
                                    transforms = mc_opt.map(|mc| {
                                        old_mats.iter().enumerate().filter_map(|(i, &old)| {
                                            mc.instance_mats.get(i).copied()
                                                .filter(|&new| new != old)
                                                .map(|new| (i as u32, old, new))
                                        }).collect()
                                    }).unwrap_or_default();
                                    let mut c2 = 0u32;
                                    new_tf = scene_ref
                                        .and_then(|s| {
                                            find_actor_by_dfs(&s.actors, wl, dfs_id, &mut c2)
                                                .and_then(|a| s.world.get::<crate::engine::components::Transform>(a.entity).cloned())
                                        })
                                        .unwrap_or_default();
                                    child_transforms = child_old_states.iter().filter_map(|(child_dfs, old_child_tf, old_mc_mat)| {
                                        let mut cc = 0u32;
                                        let new_child_tf = scene_ref
                                            .and_then(|s| {
                                                find_actor_by_dfs(&s.actors, wl, *child_dfs, &mut cc)
                                                    .and_then(|a| s.world.get::<crate::engine::components::Transform>(a.entity).cloned())
                                            })?;
                                        let mut cc2 = 0u32;
                                        let new_mc_mat = scene_ref
                                            .and_then(|s| {
                                                find_actor_by_dfs(&s.actors, wl, *child_dfs, &mut cc2)
                                                    .and_then(|a| a.mc_entity())
                                                    .and_then(|e| s.world.get::<ModelComponent>(e))
                                                    .and_then(|mc| mc.instance_mats.first().copied())
                                            })
                                            .unwrap_or(*old_mc_mat);
                                        if old_child_tf != &new_child_tf || old_mc_mat != &new_mc_mat {
                                            Some((*child_dfs, old_child_tf.clone(), new_child_tf, *old_mc_mat, new_mc_mat))
                                        } else {
                                            None
                                        }
                                    }).collect();
                                }
                                if !transforms.is_empty() || old_tf != new_tf || !child_transforms.is_empty() {
                                    self.undo_history.record(Box::new(crate::engine::core::app_base::undo::ActorGroupTransformCommand {
                                        wl, dfs_id, old_tf, new_tf, transforms, child_transforms,
                                        extra_slot_transforms: vec![],
                                    }));
                                }
                                self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                            }
                            super::InspectorTransformDrag::CanvasActor { wl: drag_wl, dfs_id, old_ct } => {
                                // 2D キャンバスアクター: 現在の CanvasTransform と比較して差異があれば記録する
                                let new_ct_opt = self.scene.as_ref().and_then(|s| {
                                    let mut c = 0u32;
                                    find_actor_by_dfs(&s.actors, drag_wl, dfs_id, &mut c)
                                        .and_then(|a| s.world.get::<crate::engine::components::CanvasTransform>(a.entity).cloned())
                                });
                                if let Some(new_ct) = new_ct_opt {
                                    // position/rotation/scale/pivot のいずれかが変化していれば Undo 記録する
                                    let changed = new_ct.position != old_ct.position
                                        || new_ct.rotation != old_ct.rotation
                                        || new_ct.scale    != old_ct.scale
                                        || new_ct.pivot    != old_ct.pivot;
                                    if changed {
                                        self.undo_history.record(Box::new(
                                            crate::engine::core::app_base::undo::CanvasTransformCommand {
                                                world_line: drag_wl, dfs_id, old_ct, new_ct,
                                            }
                                        ));
                                    }
                                }
                                self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                            }
                        }
                    }
                }
                IpcCommand::GetActorData(idx) => {
                    self.send_actor_data(idx);
                }
                IpcCommand::SetTransform { id, px, py, pz, ex, ey, ez, sx, sy, sz } => {
                    self.apply_set_transform(id, px, py, pz, ex, ey, ez, sx, sy, sz);
                }
                IpcCommand::SetCameraFov(deg) => {
                    self.camera.base.projection.fov_y_rad =
                        deg * std::f32::consts::PI / 180.0;
                }
                IpcCommand::SetCameraFar(far) => {
                    self.camera.base.projection.far = far;
                }
                IpcCommand::SetShowGrid(v) => {
                    self.show_grid = v;
                }
                IpcCommand::SetShowAxisGizmo(v) => {
                    self.show_axis_gizmo = v;
                }
                IpcCommand::SetCanvasScreenSpaceOverlay(v) => {
                    // キャンバス表示モード切り替え（false=ワールドスペース、true=スクリーンスペース）
                    self.canvas_screen_space_overlay = v;
                }
                IpcCommand::GetCamState => {
                    let (pos, yaw, pitch, fov, far, spd) = self.cam_state_tuple();
                    if let Some(ipc) = &self.ipc {
                        ipc.send(&format!("CAM_STATE:{pos},{yaw},{pitch},{fov},{far},{spd}"));
                    }
                }
                IpcCommand::SetCameraTransform { px, py, pz, yaw, pitch } => {
                    self.apply_camera_transform(px, py, pz, yaw, pitch);
                }
                IpcCommand::SetCameraSpeed(speed) => {
                    self.camera.move_speed = speed.clamp(0.1, 500.0);
                }
                IpcCommand::LoadScene(path) => {
                    // アクター編集中なら現在のカメラを退避してからシーンモードに切り替える
                    {
                        let pos = self.camera.base.transform.position;
                        self.saved_cameras.insert(self.active_world_line, DebugCameraData {
                            position: [pos.x, pos.y, pos.z],
                            yaw:      self.camera.yaw,
                            pitch:    self.camera.pitch,
                            fov_deg:  self.camera.base.projection.fov_y_rad.to_degrees(),
                            far:      self.camera.base.projection.far,
                            speed:    self.camera.move_speed,
                        });
                    }
                    self.active_world_line = 0;
                    self.saved_cameras.remove(&0);
                    let result = if let Some(ctx) = &self.draw_ctx {
                        Some(Scene::load(
                            std::path::Path::new(&path),
                            ctx,
                            self.scripting_host.as_ref(),
                        ))
                    } else { None };
                    match result {
                        Some(Ok((mut new_scene, cam_data))) => {
                            // world_line=0 が 2D キャンバスモードかどうかを
                            // ロードしたアクター（すべて world_line=0）の種別を走査して判定する。
                            // world_line > 0 のアクターを追加する前にチェックすることで
                            // アクター編集タブの 2D アクターに誤判定しない。
                            fn has_any_2d_actor(actors: &[crate::engine::structs::objects::Actor]) -> bool {
                                actors.iter().any(|a| a.is_2d() || has_any_2d_actor(a.children()))
                            }
                            if has_any_2d_actor(&new_scene.actors) {
                                self.canvas_world_lines.insert(0);
                            } else {
                                self.canvas_world_lines.remove(&0);
                            }
                            // world_line > 0 のアクター（アクター編集タブ）を保持する
                            if let Some(old_scene) = self.scene.take() {
                                for actor in old_scene.actors.into_iter().filter(|a| a.world_line > 0) {
                                    new_scene.actors.push(actor);
                                }
                            }
                            self.scene = Some(new_scene);
                            self.selected_instances.clear();
                            self.actor_virtual_selected_idx = None;
                            self.actor_virtual_selected_slot_idx = 0;
                            self.undo_history = crate::engine::core::app_base::undo::UndoHistory::new();
                            if let Some(cam) = cam_data {
                                self.apply_camera_data(&cam);
                            }
                            self.sync_anim_seeds();
                            self.send_selected();
                            self.send_hierarchy();
                            if let Some(ipc) = &self.ipc {
                                ipc.send("SCENE_LOADED");
                                let (pos, yaw, pitch, fov, far, spd) = self.cam_state_tuple();
                                ipc.send(&format!("CAM_STATE:{pos},{yaw},{pitch},{fov},{far},{spd}"));
                            }
                        }
                        Some(Err(e)) => {
                            if let Some(ipc) = &self.ipc {
                                ipc.send(&format!("LOAD_ERROR:{e}"));
                            }
                        }
                        None => {}
                    }
                }
                IpcCommand::OpenActor { path, world_line } => {
                    if self.draw_ctx.is_some() {
                        // カメラ状態を退避
                        let pos = self.camera.base.transform.position;
                        self.saved_cameras.insert(self.active_world_line, DebugCameraData {
                            position: [pos.x, pos.y, pos.z],
                            yaw:      self.camera.yaw,
                            pitch:    self.camera.pitch,
                            fov_deg:  self.camera.base.projection.fov_y_rad.to_degrees(),
                            far:      self.camera.base.projection.far,
                            speed:    self.camera.move_speed,
                        });

                        // main_scene を確保し、同じ world_line の古いアクターを World ごと除去する
                        let main_scene = self.scene.get_or_insert_with(|| Scene::new("main"));
                        {
                            fn collect_entities(actor: &crate::engine::structs::objects::Actor, out: &mut Vec<crate::engine::ecs::Entity>) {
                                out.push(actor.entity);
                                out.extend(actor.slot_entities());
                                for child in actor.children() { collect_entities(child, out); }
                            }
                            let mut to_despawn = Vec::new();
                            for a in main_scene.actors.iter().filter(|a| a.world_line == world_line) {
                                collect_entities(a, &mut to_despawn);
                            }
                            for e in to_despawn { main_scene.world.despawn(e); }
                            main_scene.actors.retain(|a| a.world_line != world_line);
                        }

                        let result = Scene::load_actor_into(
                            std::path::Path::new(&path),
                            self.draw_ctx.as_ref().unwrap(),
                            &mut main_scene.world,
                            self.scripting_host.as_ref(),
                            world_line,
                        );

                        match result {
                            Ok(actor) => {
                                if actor.is_2d() {
                                    self.canvas_world_lines.insert(world_line);
                                    // アクター編集タブの 2D 世界線として登録（常にスクリーンスペース）
                                    self.actor_edit_canvas_wls.insert(world_line);
                                } else {
                                    self.canvas_world_lines.remove(&world_line);
                                    self.actor_edit_canvas_wls.remove(&world_line);
                                }
                                main_scene.actors.push(actor);
                                self.active_world_line = world_line;
                                let cam = self.saved_cameras.get(&world_line).cloned()
                                    .unwrap_or_else(DebugCameraData::default);
                                self.apply_camera_data(&cam);
                                self.selected_instances.clear();
                                self.actor_virtual_selected_idx = None;
                                self.actor_virtual_selected_slot_idx = 0;
                                self.undo_history = crate::engine::core::app_base::undo::UndoHistory::new();
                                self.sync_anim_seeds();
                                self.send_selected();
                                self.do_send_hierarchy();
                                self.send_world_line_info();
                                if let Some(ipc) = &self.ipc {
                                    ipc.send("ACTOR_EDIT_STARTED");
                                    let (pos, yaw, pitch, fov, far, spd) = self.cam_state_tuple();
                                    ipc.send(&format!("CAM_STATE:{pos},{yaw},{pitch},{fov},{far},{spd}"));
                                }
                            }
                            Err(e) => {
                                if let Some(ipc) = &self.ipc {
                                    ipc.send(&format!("LOAD_ERROR:{e}"));
                                }
                            }
                        }
                    }
                }
                IpcCommand::SetActiveWorldLine(wl) => {
                    // 現在の世界線のカメラを退避
                    let pos = self.camera.base.transform.position;
                    self.saved_cameras.insert(self.active_world_line, DebugCameraData {
                        position: [pos.x, pos.y, pos.z],
                        yaw:      self.camera.yaw,
                        pitch:    self.camera.pitch,
                        fov_deg:  self.camera.base.projection.fov_y_rad.to_degrees(),
                        far:      self.camera.base.projection.far,
                        speed:    self.camera.move_speed,
                    });
                    self.active_world_line = wl;
                    if let Some(cam) = self.saved_cameras.get(&wl).cloned() {
                        self.apply_camera_data(&cam);
                    }
                    self.selected_instances.clear();
                    self.actor_virtual_selected_idx = None;
                    self.actor_virtual_selected_slot_idx = 0;
                    self.undo_history = crate::engine::core::app_base::undo::UndoHistory::new();
                    self.sync_anim_seeds();
                    self.send_selected();
                    self.do_send_hierarchy();
                    self.send_world_line_info();
                    if let Some(ipc) = &self.ipc {
                        if wl == 0 {
                            ipc.send("ACTOR_EDIT_ENDED");
                        } else {
                            ipc.send("ACTOR_EDIT_STARTED");
                        }
                        let (pos, yaw, pitch, fov, far, spd) = self.cam_state_tuple();
                        ipc.send(&format!("CAM_STATE:{pos},{yaw},{pitch},{fov},{far},{spd}"));
                    }
                }
                IpcCommand::RemoveWorldLine(wl) => {
                    if let Some(scene) = &mut self.scene {
                        scene.actors.retain(|a| a.world_line != wl);
                    }
                    self.saved_cameras.remove(&wl);
                    self.canvas_world_lines.remove(&wl);
                    self.actor_edit_canvas_wls.remove(&wl);
                    self.canvas_cameras.remove(&wl);
                }
                IpcCommand::AddComponent { actor_dfs_id, component_type, slot_name, args } => {
                    // Canvas 上に配置するコンポーネント（SpriteComponent 等）は
                    // 自動親子化ロジックを経由して追加する
                    let ct = component_type.clone();
                    let sn = slot_name.clone();
                    let a  = args.clone();
                    match ct.as_str() {
                        "SpriteComponent" => {
                            self.handle_add_canvas_child_component(actor_dfs_id, &ct, &sn, &a);
                        }
                        _ => {
                            self.handle_add_component_to_actor(actor_dfs_id, &ct, &sn, &a);
                        }
                    }
                }
                IpcCommand::GetActorComponents(dfs_id) => {
                    self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                }
                IpcCommand::AddActor { world_line, parent_dfs_id } => {
                    self.handle_add_actor(world_line, parent_dfs_id);
                }
                IpcCommand::AddActor2D { world_line, parent_dfs_id } => {
                    self.handle_add_actor_2d(world_line, parent_dfs_id);
                }
                IpcCommand::RemoveActor(dfs_id) => {
                    self.handle_remove_actor(dfs_id);
                }
                IpcCommand::RenameActor { dfs_id, name } => {
                    let n = name.clone();
                    self.handle_rename_actor(dfs_id, &n);
                }
                IpcCommand::RemoveComponentSlot { actor_dfs_id, slot_idx } => {
                    self.handle_remove_component_slot(actor_dfs_id, slot_idx);
                }
                IpcCommand::RenameComponentSlot { actor_dfs_id, slot_idx, name } => {
                    let n = name.clone();
                    self.handle_rename_component_slot(actor_dfs_id, slot_idx, &n);
                }
                IpcCommand::SetActorTransform { dfs_id, px, py, pz, ex, ey, ez, sx, sy, sz } => {
                    self.handle_set_actor_transform(dfs_id, px, py, pz, ex, ey, ez, sx, sy, sz);
                }
                IpcCommand::SetCanvasTransform { dfs_id, px, py, rotation, sx, sy, pivot_x, pivot_y } => {
                    self.handle_set_canvas_transform(dfs_id, px, py, rotation, sx, sy, pivot_x, pivot_y);
                }
                IpcCommand::SetCanvasSize { actor_dfs_id, slot_idx, width, height } => {
                    self.handle_set_canvas_size(actor_dfs_id, slot_idx, width, height);
                }
                IpcCommand::SetModelPath { actor_dfs_id, slot_idx, path } => {
                    let p = path.clone();
                    self.handle_set_model_path(actor_dfs_id, slot_idx, &p);
                }
                IpcCommand::DuplicateComponent { actor_dfs_id, slot_idx } => {
                    self.handle_duplicate_component(actor_dfs_id, slot_idx);
                }
                IpcCommand::DropActor { path, screen_x, screen_y } => {
                    self.pending_drop       = Some((path, screen_x, screen_y));
                    self.pending_drop_hover = None;
                    self.drop_preview_pos   = None;
                }
                IpcCommand::DragHover { x, y } => {
                    eprintln!("[Hover] DragHover received: ({x},{y})");
                    self.pending_drop_hover = Some((x, y));
                }
                IpcCommand::DragHoverEnd => {
                    eprintln!("[Hover] DragHoverEnd received");
                    self.pending_drop_hover = None;
                    self.drop_preview_pos   = None;
                }
                IpcCommand::SetSpritePath { actor_dfs_id, slot_idx, path } => {
                    let p = path.clone();
                    self.handle_set_sprite_path(actor_dfs_id, slot_idx, &p);
                }
                IpcCommand::SetSpriteColor { actor_dfs_id, slot_idx, r, g, b, a } => {
                    self.handle_set_sprite_color(actor_dfs_id, slot_idx, r, g, b, a);
                }
                IpcCommand::SetSpriteSize { actor_dfs_id, slot_idx, width, height } => {
                    self.handle_set_sprite_size(actor_dfs_id, slot_idx, width, height);
                }
                IpcCommand::SetCanvasAnchor { actor_dfs_id, ax, ay } => {
                    self.handle_set_canvas_anchor(actor_dfs_id, ax, ay);
                }
                IpcCommand::SetCanvasScaleMode { actor_dfs_id, slot_idx, scale_transform, scale_size } => {
                    self.handle_set_canvas_scale_mode(actor_dfs_id, slot_idx, scale_transform, scale_size);
                }
                IpcCommand::SetCanvasAutoScale { actor_dfs_id, slot_idx, auto_scale } => {
                    self.handle_set_canvas_auto_scale(actor_dfs_id, slot_idx, auto_scale);
                }
                IpcCommand::SetInputMapPath { actor_dfs_id, slot_idx, path } => {
                    let p = path.clone();
                    self.handle_set_inputmap_path(actor_dfs_id, slot_idx, &p);
                }
            }
        }
    }
}
