// ============================================================
//  ipc_handler.rs — IPC コマンド受信・ディスパッチ
//
//  process_ipc() はすべての IpcCommand を受け取り、
//  対応する App メソッドへルーティングする。
// ============================================================

use crate::engine::core::app_base::ipc::IpcCommand;
use crate::engine::core::app_base::scene::{Scene, DebugCameraData, CanvasCameraData};
use crate::engine::core::app_base::app::RuntimeMode;
use crate::engine::core::app_base::undo::TransformCommand;
use crate::engine::components::{ModelComponent, GroupMeta, GROUP_ID_BASE};
use winit::event_loop::ActiveEventLoop;

use super::{
    App,
    find_actor_by_dfs,
    release_window_clamp,
};

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
                IpcCommand::Pause => {
                    // Play モード中にメインカメラが存在する場合、
                    // デバッグカメラをその視点に同期してから Pause に入る。
                    if self.mode == RuntimeMode::Play {
                        self.sync_debug_camera_to_main_camera();
                    }
                    self.paused = true;
                }
                IpcCommand::Resume             => self.paused = false,
                // AI 実行中レンダリング停止（GPU リソースを LLM に解放するため）
                IpcCommand::PauseRender        => self.render_paused = true,
                // AI 応答完了後にレンダリングを再開する
                IpcCommand::ResumeRender       => self.render_paused = false,
                IpcCommand::Stop               => event_loop.exit(),
                // 内蔵デバッガのアタッチ/デタッチに応じてブレークポイント停止ガードを切り替える。
                IpcCommand::SetDebugGuard(v)   => self.clock.set_debug_guard(v),
                IpcCommand::CamKeyDown(k)      => self.cam_input.set_key(&k, true),
                IpcCommand::CamKeyUp(k)        => self.cam_input.set_key(&k, false),
                // Play 切替等でキー UP が届かずスタックした移動キーを一括リセットする。
                // スタックすると「RMB 押下だけでカメラが滑る」「軸スナップが即キャンセル
                // される（any_move_key が真のため）」という不具合になる。
                IpcCommand::CamKeysClear       => self.cam_input.clear_keys(),
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
                        let mut data = actor.to_data(&scene.world);
                        // 2D アクター: ルート CanvasTransform.position は 0 で保存する
                        // （handle_export_actor と同じ規則。ドロップ配置時に位置を設定し直すため）。
                        // rotation / scale / pivot / anchor は維持する。
                        if let Some(ref mut ct) = data.canvas_transform {
                            ct.position = [0.0, 0.0];
                        }
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
                                let c = 0u32;
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
                IpcCommand::SetEditViewMode { is_2d } => {
                    // Edit ビューモード切替（エディタの 3Dシーン / 2Dシーンタブ）。
                    // Edit モード限定機能のため Play モード中は無視する。
                    if self.mode == RuntimeMode::Edit {
                        let new_mode = if is_2d {
                            super::EditViewMode::View2D
                        } else {
                            super::EditViewMode::View3D
                        };
                        // ビューモードが実際に変わる場合は選択状態をリセットする。
                        // ワールド / ビューポートタブを直接切り替えたとき、旧ビューで
                        // 選択していたアクターのギズモが新ビューに残留するのを防ぐ。
                        if new_mode != self.edit_view_mode {
                            self.selected_actor_dfs_ids.clear();
                            self.selected_instances.clear();
                            self.actor_virtual_selected_idx = None;
                            self.actor_virtual_selected_slot_idx = 0;
                            self.hovered_gizmo_part = None;
                            self.drag.gizmo_drag = None;
                            // エディタのヒエラルキーパネルにも選択解除を通知する
                            if let Some(ipc) = &self.ipc { ipc.send("SELECTED:-1"); }
                        }
                        self.edit_view_mode = new_mode;
                        // ピッキング・ギズモ・ドラッグの各ハンドラは
                        // canvas_screen_space_overlay から use_screen_space を計算するため、
                        // ビューモードとフラグを同期させて座標系の不整合を防ぐ。
                        // （View2D = スクリーンスペース / View3D = SS キャンバス非表示）
                        self.canvas_screen_space_overlay = is_2d;
                        // 2D シーンビューの初回表示時は WYSIWYG（1 キャンバスピクセル = 1 画面
                        // ピクセル・画面中央原点）になるよう 2D カメラを初期化する。
                        // 既にカメラ状態がある場合はユーザーのパン・ズームを維持する。
                        if is_2d && !self.canvas_cameras.contains_key(&0) {
                            // ortho_half_h = ビューポート高さの半分 → キャンバス座標が実ピクセルと一致
                            let half_h = self.window.as_ref()
                                .map(|w| w.inner_size().height as f32 / 2.0)
                                .unwrap_or(360.0);
                            self.canvas_cameras.insert(0, CanvasCameraData {
                                pan_x: 0.0,
                                pan_y: 0.0,
                                ortho_half_h: half_h,
                            });
                        }
                    }
                }
                IpcCommand::SetEditorCameraOrtho(v) => {
                    // エディタのデバッグカメラを正射/透視に切り替える（Edit モード限定）。
                    // 視点は維持し、0.3 秒かけて投影方式を補間する。
                    if self.mode == RuntimeMode::Edit {
                        self.camera.set_ortho(v);
                    }
                }
                IpcCommand::GetCamState => {
                    let (pos, euler_x, euler_y, euler_z, fov, far, spd) = self.cam_state_tuple();
                    if let Some(ipc) = &self.ipc {
                        ipc.send(&format!("CAM_STATE:{pos},{euler_x},{euler_y},{euler_z},{fov},{far},{spd}"));
                    }
                }
                IpcCommand::SetCameraTransform { px, py, pz, euler_x, euler_y, euler_z } => {
                    self.apply_camera_euler(px, py, pz, euler_x, euler_y, euler_z);
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
                            // 物理タイムラインをリセットしてシーンロード後の初期状態に戻す
                            self.reset_physics_timeline();
                            // 有効な物理スレッドをシーン初期状態で再起動する
                            if self.edit_physics_enabled {
                                self.stop_physics();
                                self.start_physics();
                            }
                            if self.edit_physics_2d_enabled {
                                self.stop_physics_2d();
                                self.start_physics_2d();
                            }
                            // いずれかの物理が有効な場合はタイムラインを初期化して物理スレッドを Pause する。
                            // init_physics_timeline を呼ばないと Pause が送られず、シーンロード直後から
                            // 物理演算が走り続けて再生時に落下済み状態になってしまう。
                            // ただし常時押し戻しモード（RigidBody 無効）は Pause せず走らせ続ける。
                            if self.is_edit_physics_pushback_mode() {
                                self.enter_edit_physics_pushback();
                            } else if self.edit_physics_enabled || self.edit_physics_2d_enabled {
                                self.init_physics_timeline();
                            }
                            if let Some(ipc) = &self.ipc {
                                ipc.send("SCENE_LOADED");
                                let (pos, euler_x, euler_y, euler_z, fov, far, spd) = self.cam_state_tuple();
                                ipc.send(&format!("CAM_STATE:{pos},{euler_x},{euler_y},{euler_z},{fov},{far},{spd}"));
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
                            None, // ルートエンティティは新規 spawn
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
                                    let (pos, euler_x, euler_y, euler_z, fov, far, spd) = self.cam_state_tuple();
                                    ipc.send(&format!("CAM_STATE:{pos},{euler_x},{euler_y},{euler_z},{fov},{far},{spd}"));
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
                IpcCommand::EditCanvasBegin { world_line, actor_dfs_id } => {
                    // シーン内キャンバスアクターの隔離編集タブを開始する
                    self.handle_edit_canvas_begin(world_line, actor_dfs_id);
                }
                IpcCommand::EditCanvasEnd(wl) => {
                    // キャンバス編集タブを終了してアクターをシーンへ戻す
                    self.handle_edit_canvas_end(wl);
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
                        let (pos, euler_x, euler_y, euler_z, fov, far, spd) = self.cam_state_tuple();
                        ipc.send(&format!("CAM_STATE:{pos},{euler_x},{euler_y},{euler_z},{fov},{far},{spd}"));
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
                        // Canvas 上に配置するコンポーネント → 親子化ロジック経由
                        "SpriteComponent" | "Collider2dComponent" => {
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
                    if let Some((sx, sy)) = self.context_menu_screen_pos.take() {
                        // コンテキストメニュー経由: D&D と同じ IDバッファ読み取り方式で
                        // スポーン位置を次フレームで確定する
                        self.pending_add_actor = Some((false, world_line, parent_dfs_id, sx, sy));
                    } else {
                        self.handle_add_actor(world_line, parent_dfs_id, None);
                    }
                }
                IpcCommand::AddActor2D { world_line, parent_dfs_id } => {
                    if let Some(_) = self.context_menu_screen_pos.take() {
                        // 2D アクターはスポーン位置を使わないが pending 方式で処理タイミングを統一する
                        self.pending_add_actor = Some((true, world_line, parent_dfs_id, 0, 0));
                    } else {
                        self.handle_add_actor_2d(world_line, parent_dfs_id);
                    }
                }
                IpcCommand::AddActorChild { parent_dfs_id } => {
                    self.handle_add_actor_child(parent_dfs_id);
                }
                IpcCommand::AddActor2dChild { parent_dfs_id } => {
                    self.handle_add_actor_2d_child(parent_dfs_id);
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
                IpcCommand::SetScriptField { actor_dfs_id, slot_idx, field, value } => {
                    self.handle_set_script_field(actor_dfs_id, slot_idx, &field, &value);
                }
                IpcCommand::ReloadScripts => {
                    self.handle_reload_scripts();
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
                    self.pending_drop_hover = Some((x, y));
                }
                IpcCommand::DragHoverEnd => {
                    self.pending_drop_hover = None;
                    self.drop_preview_pos   = None;
                    // 2D シーンビューのキャンバスホバーハイライトも解除する
                    self.drag_hover_canvas_entity = None;
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
                IpcCommand::SetSpriteLayer { actor_dfs_id, slot_idx, layer } => {
                    self.handle_set_sprite_layer(actor_dfs_id, slot_idx, layer);
                }
                IpcCommand::SetAudioField { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_audio_field(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::SetCanvasAnchor { actor_dfs_id, ax, ay } => {
                    self.handle_set_canvas_anchor(actor_dfs_id, ax, ay);
                }
                IpcCommand::SetCanvasTransformScaleMode { actor_dfs_id, scale_transform, scale_size, keep_aspect, axis } => {
                    self.handle_set_canvas_transform_scale_mode(actor_dfs_id, scale_transform, scale_size, keep_aspect, axis);
                }
                IpcCommand::SetCanvasAutoScale { actor_dfs_id, slot_idx, auto_scale } => {
                    self.handle_set_canvas_auto_scale(actor_dfs_id, slot_idx, auto_scale);
                }
                IpcCommand::SetCanvasGravityMode { actor_dfs_id, slot_idx, mode } => {
                    self.handle_set_canvas_gravity_mode(actor_dfs_id, slot_idx, mode);
                }
                IpcCommand::SetCanvasDrawZone { actor_dfs_id, slot_idx, zone } => {
                    let z = zone.clone();
                    self.handle_set_canvas_draw_zone(actor_dfs_id, slot_idx, &z);
                }
                IpcCommand::SetCollider2dAspectRatio { actor_dfs_id, slot_idx, keep, axis } => {
                    let a = axis.clone();
                    self.handle_set_collider2d_aspect_ratio(actor_dfs_id, slot_idx, keep, &a);
                }
                IpcCommand::SetCanvasViewportRefWindow { actor_dfs_id, slot_idx } => {
                    self.handle_set_canvas_viewport_ref_window(actor_dfs_id, slot_idx);
                }
                IpcCommand::SetCanvasViewportRefMainCamera { actor_dfs_id, slot_idx } => {
                    self.handle_set_canvas_viewport_ref_main_camera(actor_dfs_id, slot_idx);
                }
                IpcCommand::SetCanvasViewportRefCamera { actor_dfs_id, slot_idx, actor_name, slot_name } => {
                    let an = actor_name.clone();
                    let sn = slot_name.clone();
                    self.handle_set_canvas_viewport_ref_camera(actor_dfs_id, slot_idx, an, sn);
                }
                IpcCommand::SetCanvas3dPivot { actor_dfs_id, slot_idx, pivot_x, pivot_y } => {
                    self.handle_set_canvas_3d_pivot(actor_dfs_id, slot_idx, pivot_x, pivot_y);
                }
                IpcCommand::SetInputMapPath { actor_dfs_id, slot_idx, path } => {
                    let p = path.clone();
                    self.handle_set_inputmap_path(actor_dfs_id, slot_idx, &p);
                }
                // ── CameraComponent プロパティ設定 ───────────────────────────
                IpcCommand::SetCameraComponentFov { actor_dfs_id, slot_idx, value } => {
                    self.handle_set_camera_fov(actor_dfs_id, slot_idx, value);
                }
                IpcCommand::SetCameraComponentNear { actor_dfs_id, slot_idx, value } => {
                    self.handle_set_camera_near(actor_dfs_id, slot_idx, value);
                }
                IpcCommand::SetCameraComponentFar { actor_dfs_id, slot_idx, value } => {
                    self.handle_set_camera_far(actor_dfs_id, slot_idx, value);
                }
                IpcCommand::SetCameraComponentMain { actor_dfs_id, slot_idx, is_main } => {
                    self.handle_set_camera_main(actor_dfs_id, slot_idx, is_main);
                }
                IpcCommand::SetCameraComponentClearColor { actor_dfs_id, slot_idx, r, g, b, a } => {
                    self.handle_set_camera_clear_color(actor_dfs_id, slot_idx, r, g, b, a);
                }
                IpcCommand::SetCameraComponentScalingMode { actor_dfs_id, slot_idx, mode } => {
                    let m = mode.clone();
                    self.handle_set_camera_scaling_mode(actor_dfs_id, slot_idx, &m);
                }
                IpcCommand::SetCameraComponentTargetSize { actor_dfs_id, slot_idx, width, height } => {
                    self.handle_set_camera_target_size(actor_dfs_id, slot_idx, width, height);
                }
                IpcCommand::SetCameraBarColor { actor_dfs_id, slot_idx, r, g, b, a } => {
                    self.handle_set_camera_bar_color(actor_dfs_id, slot_idx, r, g, b, a);
                }
                IpcCommand::SetCameraComponentProjection { actor_dfs_id, slot_idx, mode } => {
                    let m = mode.clone();
                    self.handle_set_camera_projection(actor_dfs_id, slot_idx, &m);
                }
                IpcCommand::SetCameraComponentOrthoHeight { actor_dfs_id, slot_idx, value } => {
                    self.handle_set_camera_ortho_height(actor_dfs_id, slot_idx, value);
                }
                IpcCommand::SetActorActive { dfs_id, active } => {
                    // アクターのアクティブ切替（Unity の SetActive 相当）。
                    // 子孫への影響（実効アクティブ）は各収集処理が親の active を
                    // 継承して判定するため、自身のフラグのみ書き換えればよい。
                    let wl = self.active_world_line;
                    if let Some(scene) = self.scene.as_mut() {
                        let mut c = 0u32;
                        if let Some(actor) = super::find_actor_by_dfs_mut(&mut scene.actors, wl, dfs_id, &mut c) {
                            actor.active = active;
                            if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                            // ヒエラルキーの淡色表示を更新するため再送する
                            self.send_hierarchy();
                        }
                    }
                }
                IpcCommand::SetSlotEnabled { actor_dfs_id, slot_idx, enabled } => {
                    // コンポーネントスロットの有効切替（Unity の enabled 相当）
                    let wl = self.active_world_line;
                    if let Some(scene) = self.scene.as_mut() {
                        let mut c = 0u32;
                        if let Some(actor) = super::find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs_id, &mut c) {
                            if let Some(slot) = actor.slots_mut().get_mut(slot_idx as usize) {
                                slot.enabled = enabled;
                                if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                            }
                        }
                    }
                }
                // ── 物理コンポーネント ──────────────────────────────────────
                IpcCommand::SetColliderData { actor_dfs_id, slot_idx, json } => {
                    let j = json.clone();
                    self.handle_set_collider_data(actor_dfs_id, slot_idx, &j);
                }
                IpcCommand::SetCollider2dData { actor_dfs_id, slot_idx, json } => {
                    // 2D コライダーコンポーネントデータを JSON でまとめて更新する
                    let j = json.clone();
                    self.handle_set_collider2d_data(actor_dfs_id, slot_idx, &j);
                }
                // ── プラグインコンポーネント ─────────────────────────────────
                IpcCommand::SetPluginField { actor_dfs_id, slot_idx, key, value } => {
                    let k = key.clone();
                    let v = value.clone();
                    self.handle_set_plugin_field(actor_dfs_id, slot_idx, &k, &v);
                }
                IpcCommand::GetPluginList => {
                    self.send_plugin_list();
                }

                // ── AI アシスタント用コマンド ─────────────────────────────────
                IpcCommand::GetSceneInfo => {
                    self.send_scene_info();
                }
                IpcCommand::AiAddActor { name, x, y, z } => {
                    self.handle_ai_add_actor(&name.clone(), x, y, z);
                }
                IpcCommand::AiRemoveActor { actor_dfs_id } => {
                    self.apply_delete(&[actor_dfs_id], true);
                    self.send_hierarchy();
                    if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                }
                IpcCommand::AiMoveActor { actor_dfs_id, x, y, z } => {
                    self.handle_ai_move_actor(actor_dfs_id, x, y, z);
                }
                IpcCommand::AiAddComponent { actor_dfs_id, component_type, params_json } => {
                    let ct = component_type.clone();
                    let pj = params_json.clone();
                    self.handle_ai_add_component(actor_dfs_id, &ct, &pj);
                }
                IpcCommand::AiSetValue { actor_dfs_id, slot_idx, key, value } => {
                    self.handle_set_component_value(actor_dfs_id, slot_idx, &key, &value);
                }
                IpcCommand::ExportActor { dfs_id, path } => {
                    self.handle_export_actor(dfs_id, &path);
                }

                // ── 物理シミュレーション設定 ─────────────────────────────────
                IpcCommand::SetEditPhysics { enabled, with_rigidbody } => {
                    // Play モードでは無視する（Play 起動時の SyncViewportSettings による誤停止を防ぐ）
                    if self.mode != RuntimeMode::Edit { continue; }
                    // 編集時の物理シミュレーション設定を更新する
                    self.edit_physics_enabled        = enabled;
                    self.edit_physics_with_rigidbody = with_rigidbody;
                    if enabled {
                        // 既存スレッドを停止してから新しい設定で再起動する
                        self.stop_physics();
                        self.start_physics();
                        if self.is_edit_physics_pushback_mode() {
                            // 【変更3(a)】RigidBody 無効: 常時押し戻しモード。
                            // タイムラインを使わず、物理を Pause せずに走らせ続ける
                            // （起動直後の paused=false のまま）ことでドラッグ押し戻しを常時有効化する。
                            self.enter_edit_physics_pushback();
                        } else {
                            // タイムラインを初期化する（初期状態をフレーム0として記録・Pause 送信）
                            self.init_physics_timeline();
                        }
                    } else {
                        self.stop_physics();
                        // 無効化時は衝突中 ID セットをクリアして描画色をリセットする
                        self.active_collision_dfs_ids.clear();
                        // タイムラインをリセットする
                        self.reset_physics_timeline();
                    }
                }

                // ── 編集時物理タイムライン操作 ──────────────────────────────
                IpcCommand::EditPhysicsPlayPause => {
                    if self.mode == RuntimeMode::Edit {
                        self.handle_edit_physics_play_pause();
                    }
                }
                IpcCommand::EditPhysicsStep { step } => {
                    if self.mode == RuntimeMode::Edit {
                        self.handle_edit_physics_step(step);
                    }
                }
                IpcCommand::EditPhysicsSeek { frame } => {
                    if self.mode == RuntimeMode::Edit {
                        self.handle_edit_physics_seek(frame);
                    }
                }
                IpcCommand::EditPhysicsApplyFrame => {
                    if self.mode == RuntimeMode::Edit {
                        self.handle_edit_physics_apply_frame();
                    }
                }
                IpcCommand::SetPlayColliderDraw(v) => {
                    // 実行時コライダー描画フラグを更新する
                    self.play_collider_draw = v;
                }

                // ── 2D 物理シミュレーション設定 ──────────────────────────────
                IpcCommand::SetEditPhysics2d { enabled, with_rigidbody } => {
                    // Play モードでは無視する（Play 起動時の SyncViewportSettings による誤停止を防ぐ）
                    if self.mode != RuntimeMode::Edit { continue; }
                    // 編集時の 2D 物理シミュレーション設定を更新する
                    self.edit_physics_2d_enabled        = enabled;
                    self.edit_physics_2d_with_rigidbody = with_rigidbody;
                    if enabled {
                        // 既存 2D スレッドを停止してから新しい設定で再起動する
                        self.stop_physics_2d();
                        self.start_physics_2d();
                        if self.is_edit_physics_pushback_mode() {
                            // 【変更3(a)】RigidBody 無効（3D/2D ともタイムライン非使用）:
                            // 常時押し戻しモード。物理を Pause せず走らせ続ける。
                            self.enter_edit_physics_pushback();
                        } else if self.edit_physics_enabled && self.edit_physics_with_rigidbody {
                            // 3D 物理が「RigidBody 有効＝タイムラインモード」で既に動いている場合のみ、
                            // 既存タイムラインが存在するとみなして Pause のみ送信する。
                            // 【バグ修正】従来は self.edit_physics_enabled のみで判定していたため、
                            // 3D が「常時押し戻しモード」（RigidBody 無効）で有効な状態から
                            // 2D を RigidBody 有効で ON にすると、実際にはタイムラインが
                            // 初期化されていない（reset_physics_timeline のみでスナップショット
                            // 空）にもかかわらずこの分岐に入り、init_physics_timeline が
                            // 呼ばれず・エディタへ状態通知もされずタイムライン UI が機能しなかった。
                            // 2D スレッドにも Pause を送って再生ボタンまで動かさない
                            if let Some(thread) = &self.physics_thread_2d {
                                use crate::engine::physics::PhysicsCommand2d;
                                thread.send(PhysicsCommand2d::Pause);
                            }
                        } else {
                            // 3D がタイムラインモードで動いていない（無効 or 押し戻しモード）・
                            // 2D が RigidBody 有効: タイムラインを新規初期化する
                            self.init_physics_timeline();
                        }
                    } else {
                        self.stop_physics_2d();
                        // 無効化時は衝突中 ID セットをクリアして描画色をリセットする
                        self.active_collision_2d_dfs_ids.clear();
                    }
                }
            }
        }
    }

    // ============================================================
    //  AI フィールド変更ディスパッチャ
    // ============================================================

    /// AI からの AI_SET_VALUE コマンドを、コンポーネント型に応じて各ハンドラへ振り分ける。
    fn handle_set_component_value(&mut self, actor_dfs_id: u32, slot_idx: u32, key: &str, value: &str) {
        use crate::engine::components::ComponentKind;

        let wl = self.active_world_line;
        let kind = {
            let scene = match self.scene.as_ref() { Some(s) => s, None => return };
            let mut c = 0u32;
            let actor = match super::find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c) {
                Some(a) => a,
                None => return,
            };
            actor.slots().get(slot_idx as usize).map(|s| s.kind)
        };

        let Some(kind) = kind else { return };

        match kind {
            // ModelComponent: "model_path"
            ComponentKind::Model if key == "model_path" => {
                self.handle_set_model_path(actor_dfs_id, slot_idx, value);
            }

            // CameraComponent: "fov", "near", "far", "is_main", "clear_color"
            ComponentKind::Camera => {
                match key {
                    "fov" | "fov_y_deg" => {
                        if let Ok(v) = value.parse::<f32>() { self.handle_set_camera_fov(actor_dfs_id, slot_idx, v); }
                    }
                    "near" => {
                        if let Ok(v) = value.parse::<f32>() { self.handle_set_camera_near(actor_dfs_id, slot_idx, v); }
                    }
                    "far" => {
                        if let Ok(v) = value.parse::<f32>() { self.handle_set_camera_far(actor_dfs_id, slot_idx, v); }
                    }
                    "is_main" => {
                        let b = value == "true" || value == "1";
                        self.handle_set_camera_main(actor_dfs_id, slot_idx, b);
                    }
                    "clear_color" => {
                        // "r,g,b,a" 形式を想定
                        let parts: Vec<f32> = value.split(',').filter_map(|s| s.trim().parse().ok()).collect();
                        if parts.len() == 4 {
                            self.handle_set_camera_clear_color(actor_dfs_id, slot_idx, parts[0], parts[1], parts[2], parts[3]);
                        }
                    }
                    _ => {}
                }
            }

            // SpriteComponent: "texture_path", "color", "width", "height"
            ComponentKind::Sprite => {
                match key {
                    "texture_path" | "path" => {
                        self.handle_set_sprite_path(actor_dfs_id, slot_idx, value);
                    }
                    "color" => {
                        let parts: Vec<f32> = value.split(',').filter_map(|s| s.trim().parse().ok()).collect();
                        if parts.len() == 4 {
                            self.handle_set_sprite_color(actor_dfs_id, slot_idx, parts[0], parts[1], parts[2], parts[3]);
                        }
                    }
                    "width" => {
                        if let Ok(v) = value.parse::<f32>() {
                            let h = {
                                let scene = self.scene.as_ref().unwrap();
                                let mut c = 0u32;
                                find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                                    .and_then(|a| a.slots().get(slot_idx as usize))
                                    .and_then(|s| scene.world.get::<crate::engine::components::SpriteComponent>(s.entity))
                                    .map(|s| s.height)
                                    .unwrap_or(100.0)
                            };
                            self.handle_set_sprite_size(actor_dfs_id, slot_idx, v, h);
                        }
                    }
                    "height" => {
                        if let Ok(v) = value.parse::<f32>() {
                            let w = {
                                let scene = self.scene.as_ref().unwrap();
                                let mut c = 0u32;
                                find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                                    .and_then(|a| a.slots().get(slot_idx as usize))
                                    .and_then(|s| scene.world.get::<crate::engine::components::SpriteComponent>(s.entity))
                                    .map(|s| s.width)
                                    .unwrap_or(100.0)
                            };
                            self.handle_set_sprite_size(actor_dfs_id, slot_idx, w, v);
                        }
                    }
                    _ => {}
                }
            }

            // CanvasComponent: "width", "height", "auto_scale"
            // （スケールモードは CanvasTransform 側へ移動。SET_CANVAS_TRANSFORM_SCALE_MODE で設定する）
            ComponentKind::Canvas => {
                match key {
                    "width" => {
                        if let Ok(v) = value.parse::<f32>() {
                            let h = {
                                let scene = self.scene.as_ref().unwrap();
                                let mut c = 0u32;
                                find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                                    .and_then(|a| a.slots().get(slot_idx as usize))
                                    .and_then(|s| scene.world.get::<crate::engine::components::CanvasComponent>(s.entity))
                                    .map(|s| s.height)
                                    .unwrap_or(1080.0)
                            };
                            self.handle_set_canvas_size(actor_dfs_id, slot_idx, v, h);
                        }
                    }
                    "height" => {
                        if let Ok(v) = value.parse::<f32>() {
                            let w = {
                                let scene = self.scene.as_ref().unwrap();
                                let mut c = 0u32;
                                find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                                    .and_then(|a| a.slots().get(slot_idx as usize))
                                    .and_then(|s| scene.world.get::<crate::engine::components::CanvasComponent>(s.entity))
                                    .map(|s| s.width)
                                    .unwrap_or(1920.0)
                            };
                            self.handle_set_canvas_size(actor_dfs_id, slot_idx, w, v);
                        }
                    }
                    "auto_scale" => {
                        let b = value == "true" || value == "1";
                        self.handle_set_canvas_auto_scale(actor_dfs_id, slot_idx, b);
                    }
                    _ => {}
                }
            }

            // InputMapComponent: "asset_path"
            ComponentKind::InputMap if key == "asset_path" || key == "path" => {
                self.handle_set_inputmap_path(actor_dfs_id, slot_idx, value);
            }

            // PluginComponent: 全フィールド
            ComponentKind::Plugin => {
                self.handle_set_plugin_field(actor_dfs_id, slot_idx, key, value);
            }

            _ => {
                // その他のコンポーネントや未知のキーは現状無視（必要に応じて拡張）
            }
        }
    }

    // ============================================================
    //  プラグインフィールド変更処理
    // ============================================================

    /// プラグインコンポーネントのフィールド値を変更し Undo に記録する。
    fn handle_set_plugin_field(&mut self, actor_dfs_id: u32, slot_idx: u32, key: &str, new_value: &str) {
        use crate::engine::components::{PluginComponent, ComponentKind};
        use crate::engine::core::app_base::undo::PluginFieldCommand;

        let wl    = self.active_world_line;
        let scene = match self.scene.as_mut() { Some(s) => s, None => return };

        // スロットエンティティと plugin_name を取得する
        let (slot_entity, plugin_name, old_value) = {
            let mut c = 0u32;
            let actor = match super::find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c) {
                Some(a) => a,
                None    => return,
            };
            let slot = match actor.slots().get(slot_idx as usize) {
                Some(s) if s.kind == ComponentKind::Plugin => s,
                _ => return,
            };
            let entity = slot.entity;
            let (pname, oval) = if let Some(pc) = scene.world.get::<PluginComponent>(entity) {
                let old = pc.fields.get(key).cloned().unwrap_or_default();
                (pc.plugin_name.clone(), old)
            } else { return };
            (entity, pname, oval)
        };

        // 値が変わっていなければ何もしない
        if old_value == new_value { return; }

        // プラグインの on_field_changed を呼び出す（副作用・バリデーション）
        if let Some(lp) = self.plugin_registry.get(&plugin_name) {
            lp.plugin.on_field_changed(key, &old_value, new_value);
        }

        // フィールド値を更新する
        if let Some(pc) = scene.world.get_mut::<PluginComponent>(slot_entity) {
            pc.set_field(key, new_value);
        }

        // Undo コマンドを記録する（slot_entity を直接保持）
        self.undo_history.record(Box::new(PluginFieldCommand {
            slot_entity: slot_entity,
            key:         key.to_string(),
            old_value,
            new_value:   new_value.to_string(),
            world_line:  self.active_world_line,
            actor_dfs_id,
        }));

        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// ロード済みプラグイン一覧をエディタへ送信する。
    fn send_plugin_list(&self) {
        if let Some(ipc) = &self.ipc {
            let json = self.plugin_registry.to_json();
            ipc.send(&format!("PLUGIN_LIST:{json}"));
        }
    }
}
