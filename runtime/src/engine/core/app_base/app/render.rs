// ============================================================
//  render.rs — ApplicationHandler 実装（resumed / window_event / device_event）
//
//  winit イベントループへの応答処理全般。
//  メインレンダーループ、ギズモドラッグ、ピック、ドロップ、
//  グリッド・オーバーレイ描画等を含む。
// ============================================================

use std::sync::Arc;

use winit::application::ApplicationHandler;
use winit::event::{DeviceEvent, DeviceId, ElementState, WindowEvent};
use winit::event_loop::ActiveEventLoop;
use winit::keyboard::{KeyCode, PhysicalKey};
use winit::window::WindowId;

use crate::engine::components::{
    ModelComponent, Transform as ActorTransform, ComponentKind, CanvasTransform, CanvasComponent,
    SpriteComponent,
};
use crate::engine::core::app_base::ipc::ToolMode;
use crate::engine::core::app_base::scene::CanvasCameraData;
use crate::engine::core::clock::FrameContext;
use crate::engine::core::renderer::Renderer;
use crate::engine::core::window::{create_window, WindowConfig};
use crate::engine::methods::drawer::{
    DrawContext, CameraBuffer, CameraUniform,
    draw_model_indirect, draw_id_pass,
    draw_outline_multi, draw_stencil_mask_multi,
    extract_frustum_planes, IdBuffer, GizmoBatch, draw_gizmo_batch,
    LineBatch, draw_line_batch,
    load_sprite_texture, prepare_sprites, draw_sprites, GpuSpriteTexture,
};
use crate::engine::core::app_base::undo::{
    SelectionCommand, ActorGroupTransformCommand, MultiTransformCommand,
    ActorDfsSelectionCommand, MultiActorDragTransformCommand, CompositeCommand,
};
use crate::engine::methods::gizmo_interact::{
    screen_to_ray, screen_to_ray_ortho, update_drag, mat4x4_mul, mat4x4_inv,
};
use crate::engine::structs::tensor::{Vector3, Mat4x4};
use crate::engine::structs::utils::Color;

use super::{
    App, RuntimeMode,
    find_actor_by_dfs,
    collect_mcs_in_world_line,
    collect_canvas_actors_in_rect,
    collect_child_actor_mc_starts,
    world_to_screen,
    camera_grab_start, camera_grab_end,
    apply_window_clamp, release_window_clamp,
};

impl ApplicationHandler for App {
    /// ウィンドウ・レンダラーを初期化し、IPC へ READY を通知する。
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        eprintln!("[SEED] resumed() start  parent_hwnd={:?}", self.parent_hwnd);

        let window = Arc::new(create_window(event_loop, &WindowConfig {
            parent_hwnd: self.parent_hwnd,
            ..WindowConfig::default()
        }));
        eprintln!("[SEED] window created");

        let mut renderer = Renderer::new(window.clone());
        eprintln!("[SEED] renderer created");

        let size = window.inner_size();
        self.camera.set_aspect_ratio(size.width, size.height);

        self.camera.base.transform.position = Vector3::new(0.0, 2.0, -10.0);

        let ctx = DrawContext::new(
            renderer.device(),
            renderer.queue(),
            renderer.surface_format(),
            renderer.depth_format(),
        );
        eprintln!("[SEED] DrawContext created");

        let scene = crate::engine::core::app_base::scene::Scene::new("Untitled");
        let camera_buf = ctx.create_camera_buffer();
        let id_buffer  = IdBuffer::new(&ctx.device, size.width, size.height);
        let line_model_buf = ctx.create_identity_model_bg_for_unlit();
        eprintln!("[SEED] scene ready");

        if self.is_embedded() {
            // 非表示ウィンドウへの request_redraw は WM_PAINT が配送されず
            // RedrawRequested が発火しないため、常に可視化してから redraw を要求する。
            // 起動中の白フラッシュはエディタ側コンテナの WM_ERASEBKGND 黒塗りで対処する。
            window.set_visible(true);
            window.request_redraw();
        } else {
            if let Ok(frame) = renderer.begin_frame() { frame.finish(); }
            window.set_visible(true);
        }

        self.draw_ctx      = Some(ctx);
        self.scene         = Some(scene);
        self.camera_buf    = Some(camera_buf);
        self.id_buffer     = Some(id_buffer);
        self.line_model_buf = Some(line_model_buf);

        // 軸ギズモ・アイコンオーバーレイ（エディタモードのみ初期化）
        if self.mode == RuntimeMode::Edit {
            use crate::engine::core::font::axis_gizmo::AxisGizmo;
            use crate::engine::core::font::icon_overlay::IconOverlay;
            let dev = &self.draw_ctx.as_ref().unwrap().device;
            let que = &self.draw_ctx.as_ref().unwrap().queue;
            self.axis_gizmo = Some(AxisGizmo::new(
                dev,
                renderer.surface_format(),
                renderer.depth_format(),
            ));
            self.icon_overlay = Some(IconOverlay::new(
                dev,
                que,
                renderer.surface_format(),
                renderer.depth_format(),
            ));
        }

        self.renderer      = Some(renderer);
        self.window        = Some(window);
        self.clock         = crate::engine::core::clock::Clock::new();

        self.sync_anim_seeds();

        let hwnd = self.window_hwnd();
        eprintln!("[SEED] sending READY:{hwnd}");
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("READY:{hwnd}"));
        }
        self.send_hierarchy();
        eprintln!("[SEED] resumed() done");
    }

    /// ウィンドウイベントを処理する（キー入力・マウス・リサイズ・メインループ）。
    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        _id: WindowId,
        event: WindowEvent,
    ) {
        match event {
            WindowEvent::CloseRequested if !self.is_embedded() => {
                if let Some(ipc) = &self.ipc { ipc.send("STOPPED"); }
                event_loop.exit();
            }

            WindowEvent::Resized(size) => {
                if let Some(r) = &mut self.renderer { r.resize(size); }
                self.camera.set_aspect_ratio(size.width, size.height);
                if size.width > 0 && size.height > 0 {
                    if let Some(dc) = &self.draw_ctx {
                        self.id_buffer = Some(IdBuffer::new(&dc.device, size.width, size.height));
                    }
                }
            }

            WindowEvent::KeyboardInput { event, .. } => {
                let pressed = event.state == ElementState::Pressed;
                if let PhysicalKey::Code(key) = event.physical_key {
                    self.input.process_key(key, pressed);

                    match key {
                        KeyCode::ControlLeft | KeyCode::ControlRight => {
                            self.ctrl_held = pressed;
                        }
                        KeyCode::KeyZ if pressed && self.ctrl_held => {
                            let result = if let Some(scene) = &mut self.scene {
                                self.undo_history.undo(scene)
                            } else { None };
                            if let Some((structural, sel)) = result {
                                if let Some(ids) = sel {
                                    self.selected_instances = ids;
                                    self.send_selected();
                                }
                                if structural {
                                    self.sync_anim_seeds();
                                    self.send_hierarchy();
                                }
                            }
                        }
                        KeyCode::KeyY if pressed && self.ctrl_held => {
                            let result = if let Some(scene) = &mut self.scene {
                                self.undo_history.redo(scene)
                            } else { None };
                            if let Some((structural, sel)) = result {
                                if let Some(ids) = sel {
                                    self.selected_instances = ids;
                                    self.send_selected();
                                }
                                if structural {
                                    self.sync_anim_seeds();
                                    self.send_hierarchy();
                                }
                            }
                        }
                        _ => {}
                    }
                }
            }

            WindowEvent::MouseInput { button, state, .. } => {
                let pressed = state == ElementState::Pressed;
                self.input.process_mouse_button(button, pressed);

                if button == winit::event::MouseButton::Left {
                    if pressed
                        && (self.mode == RuntimeMode::Edit || self.paused)
                        && !self.cam_input.rmb
                    {
                        if let Some((cx, cy)) = self.last_cursor_pos {
                            self.lmb_held      = true;
                            self.lmb_press_pos = Some((cx, cy));
                            self.ctrl_at_press = self.ctrl_held;

                            // ギズモヒットを優先。外れた場合は release 時にピックまたは矩形選択。
                            if let Some(drag) = self.try_gizmo_hit_and_start(cx, cy) {
                                let wl                = self.active_world_line;
                                let selected_dfs      = self.actor_virtual_selected_idx;
                                let selected_slot_i   = self.actor_virtual_selected_slot_idx;
                                self.multi_actor_drag_starts.clear();
                                if let Some(scene) = self.scene.as_ref() {
                                    // 選択スロット entity を取得する
                                    // シーンモード・アクター編集モード共通: selected_dfs で選択アクタを特定する
                                    let mc_entity: Option<crate::engine::ecs::Entity> =
                                        selected_dfs.and_then(|dfs| {
                                            let mut c = 0u32;
                                            find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c)
                                                .and_then(|a| a.mc_entity_at(selected_slot_i))
                                        });
                                    if let Some(mc) = mc_entity.and_then(|e| scene.world.get::<ModelComponent>(e)) {
                                        // selected_instances が空（矩形選択・マルチ選択後）の場合は
                                        // インスタンス 0 をドラッグ対象として扱う
                                        let inst_slice: &[u32] = if self.selected_instances.is_empty() { &[0] } else { &self.selected_instances };
                                        let roots = mc.filter_selection_roots(inst_slice);
                                        self.drag_root_starts = roots.iter()
                                            .filter_map(|&i| mc.instance_mats.get(i as usize).map(|&m| (i, m)))
                                            .collect();
                                        self.drag_child_starts = mc.collect_non_root_descendants(&roots);
                                    }
                                    // 選択スロット以外の MC スロット開始行列を収集する
                                    if let Some(dfs) = selected_dfs {
                                        let mut c = 0u32;
                                        if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c) {
                                            self.actor_extra_mc_drag_starts = actor.slots().iter()
                                                .filter(|s| s.kind == ComponentKind::Model)
                                                .enumerate()
                                                .filter(|&(i, _)| i != selected_slot_i)
                                                .filter_map(|(i, s)| {
                                                    scene.world.get::<ModelComponent>(s.entity)
                                                        .map(|mc| (i, mc.instance_mats.clone()))
                                                })
                                                .collect();
                                        }
                                    }
                                    // 子アクター MC の開始行列を収集する
                                    if let Some(dfs) = selected_dfs {
                                        let mut c = 0u32;
                                        if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c) {
                                            let mut child_dfs_counter = dfs as u32 + 1;
                                            collect_child_actor_mc_starts(actor, &scene.world, &mut child_dfs_counter, &mut self.actor_child_drag_starts);
                                        }
                                    }
                                    // MC なし（または空）のアクターは Transform を直接動かす
                                    if self.drag_root_starts.is_empty() {
                                        if let Some(dfs) = selected_dfs {
                                            let mut c = 0u32;
                                            if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c) {
                                                if self.canvas_world_lines.contains(&wl) {
                                                    // 2D: CanvasTransform のスナップショットを保持する
                                                    let old_ct = scene.world.get::<CanvasTransform>(actor.entity)
                                                        .cloned().unwrap_or_default();
                                                    self.canvas_transform_drag_start = Some((dfs as u32, old_ct));
                                                } else {
                                                    let old_tf = scene.world.get::<ActorTransform>(actor.entity)
                                                        .cloned().unwrap_or_default();
                                                    self.actor_transform_drag_start = Some((dfs as u32, old_tf));
                                                }
                                            }
                                        }
                                    }
                                    // マルチ選択: プライマリ以外の選択アクターの開始行列を収集する
                                    if self.selected_actor_dfs_ids.len() > 1 {
                                        for &other_dfs in &self.selected_actor_dfs_ids {
                                            if Some(other_dfs) == selected_dfs { continue; }
                                            let mut c = 0u32;
                                            if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, other_dfs as u32, &mut c) {
                                                let start_mat = actor.mc_entity()
                                                    .and_then(|e| scene.world.get::<ModelComponent>(e))
                                                    .and_then(|mc| mc.instance_mats.first().copied())
                                                    .unwrap_or_else(|| {
                                                        scene.world.get::<ActorTransform>(actor.entity)
                                                            .map(|tf| tf.to_mat4())
                                                            .unwrap_or_default()
                                                    });
                                                self.multi_actor_drag_starts.push((other_dfs as u32, start_mat));
                                            }
                                        }
                                    }
                                }
                                self.gizmo_drag = Some(drag);
                            }
                        }
                    }

                    if !pressed {
                        self.lmb_held = false;

                        if self.rect_selecting {
                            // 矩形選択終了: SelectionCommand と ActorDfsSelectionCommand を記録してエディタへ通知
                            let before_inst = std::mem::take(&mut self.selection_before_rect);
                            let after_inst  = self.selected_instances.clone();
                            if before_inst != after_inst {
                                self.undo_history.record(Box::new(SelectionCommand { before: before_inst, after: after_inst }));
                            }
                            let before_dfs     = std::mem::take(&mut self.selection_before_rect_dfs);
                            let before_primary = self.selection_before_rect_primary.take();
                            let after_dfs      = self.selected_actor_dfs_ids.clone();
                            let after_primary  = self.actor_virtual_selected_idx;
                            if before_dfs != after_dfs || before_primary != after_primary {
                                self.undo_history.record(Box::new(ActorDfsSelectionCommand {
                                    before_dfs_ids: before_dfs,
                                    after_dfs_ids:  after_dfs,
                                    before_primary,
                                    after_primary,
                                }));
                            }
                            self.send_selected();
                            // インスペクタへプライマリ選択アクターのコンポーネントをプッシュ
                            if let Some(idx) = self.actor_virtual_selected_idx {
                                self.send_actor_components(idx as u32, self.actor_virtual_selected_slot_idx);
                            }
                            self.rect_selecting = false;
                        } else if self.gizmo_drag.is_none() && self.lmb_press_pos.is_some()
                            && (self.mode == RuntimeMode::Edit || self.paused)
                        {
                            // クリック: ID ピックをスケジュール
                            if let Some((cx, cy)) = self.last_cursor_pos {
                                self.pending_pick = Some((cx as u32, cy as u32));
                            }
                        }
                        self.lmb_press_pos = None;

                        // ドラッグで変化があれば Undo 履歴に一括記録
                        // マルチ選択ブロックは gizmo_drag ブロックの外にあるためここで宣言する。
                        let mut primary_recorded = false;
                        if self.gizmo_drag.is_some() {
                            if let Some((dfs_id, old_transform)) = self.actor_transform_drag_start.take() {
                                // アクター編集モード: MC なしアクターの Transform ドラッグ終了
                                let wl = self.active_world_line;
                                let child_drag_starts = std::mem::take(&mut self.actor_child_drag_starts);
                                // entity を取り出してから World で Transform を取得
                                let new_transform_opt = self.scene.as_ref().and_then(|s| {
                                    let mut c = 0u32;
                                    find_actor_by_dfs(&s.actors, wl, dfs_id, &mut c)
                                        .and_then(|a| s.world.get::<ActorTransform>(a.entity).cloned())
                                });
                                if let Some(new_transform) = new_transform_opt {
                                    let delta = mat4x4_mul(new_transform.to_mat4(), mat4x4_inv(old_transform.to_mat4()));
                                    let mut child_transforms: Vec<(u32, ActorTransform, ActorTransform, [[f32;4];4], [[f32;4];4])> = Vec::new();
                                    for (child_dfs, start_mat) in child_drag_starts {
                                        let new_mc_mat = mat4x4_mul(delta, start_mat);
                                        if let Some(scene) = &mut self.scene {
                                            // entity を先に取り出して actors の borrow を解放する
                                            let child_entity = {
                                                let mut c = 0u32;
                                                find_actor_by_dfs(&scene.actors, wl, child_dfs, &mut c)
                                                    .map(|a| a.entity)
                                            };
                                            if let Some(child_entity) = child_entity {
                                                let old_child_tf = scene.world.get::<ActorTransform>(child_entity)
                                                    .cloned().unwrap_or_default();
                                                let new_child_tf = ActorTransform::from_mat4(&new_mc_mat);
                                                if let Some(tf) = scene.world.get_mut::<ActorTransform>(child_entity) {
                                                    *tf = new_child_tf.clone();
                                                }
                                                if let Some(mc) = scene.world.get_mut::<ModelComponent>(child_entity) {
                                                    if let Some(m) = mc.instance_mats.first_mut() { *m = new_mc_mat; }
                                                    mc.mark_batch_dirty();
                                                }
                                                child_transforms.push((child_dfs, old_child_tf, new_child_tf, start_mat, new_mc_mat));
                                            }
                                        }
                                    }
                                    if old_transform != new_transform || !child_transforms.is_empty() {
                                        self.undo_history.record(Box::new(ActorGroupTransformCommand {
                                            wl, dfs_id,
                                            old_tf: old_transform,
                                            new_tf: new_transform,
                                            transforms: vec![],
                                            child_transforms,
                                            extra_slot_transforms: vec![],
                                        }));
                                        primary_recorded = true;
                                        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                                    }
                                }
                                self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                            } else {
                                let mut transforms: Vec<(u32, [[f32;4];4], [[f32;4];4])> = Vec::new();
                                let root_starts       = std::mem::take(&mut self.drag_root_starts);
                                let child_starts      = std::mem::take(&mut self.drag_child_starts);
                                let extra_mc_starts   = std::mem::take(&mut self.actor_extra_mc_drag_starts);
                                let wl_end            = self.active_world_line;
                                let selected_dfs_end  = self.actor_virtual_selected_idx;
                                let selected_slot_i_e = self.actor_virtual_selected_slot_idx;
                                // 選択スロット entity を取得して old→new 変換を収集する
                                let mc_entity: Option<crate::engine::ecs::Entity> = self.scene.as_ref().and_then(|s| {
                                    selected_dfs_end.and_then(|dfs| {
                                        let mut c = 0u32;
                                        find_actor_by_dfs(&s.actors, wl_end, dfs as u32, &mut c)
                                            .and_then(|a| a.mc_entity_at(selected_slot_i_e))
                                    })
                                });
                                if let Some(mc) = mc_entity.and_then(|e| self.scene.as_ref()?.world.get::<ModelComponent>(e)) {
                                    for (idx, old_mat) in root_starts.into_iter().chain(child_starts) {
                                        if let Some(&new_mat) = mc.instance_mats.get(idx as usize) {
                                            if new_mat != old_mat {
                                                transforms.push((idx, old_mat, new_mat));
                                            }
                                        }
                                    }
                                }
                                // 追加 MC スロットの old→new 変換を収集する（Undo 用）
                                let extra_slot_transforms: Vec<(usize, u32, [[f32;4];4], [[f32;4];4])> =
                                    extra_mc_starts.iter().flat_map(|(slot_i, start_mats)| {
                                        let cur_mats: Vec<[[f32;4];4]> = selected_dfs_end.and_then(|dfs| {
                                            let mut c = 0u32;
                                            self.scene.as_ref().and_then(|s| {
                                                find_actor_by_dfs(&s.actors, wl_end, dfs as u32, &mut c)
                                                    .and_then(|a| a.mc_entity_at(*slot_i))
                                                    .and_then(|e| s.world.get::<ModelComponent>(e))
                                                    .map(|mc| mc.instance_mats.clone())
                                            })
                                        }).unwrap_or_default();
                                        start_mats.iter().zip(cur_mats.iter()).enumerate()
                                            .filter_map(|(i, (old, &new))| {
                                                if *old != new { Some((*slot_i, i as u32, *old, new)) } else { None }
                                            })
                                            .collect::<Vec<_>>()
                                    }).collect();
                                let wl = self.active_world_line;
                                if self.actor_virtual_selected_idx.is_some() {
                                    // 仮想ノード選択中: delta を Transform と子アクターに反映して ActorGroupTransformCommand で記録
                                    let dfs_id = self.actor_virtual_selected_idx.unwrap() as u32;
                                    let child_drag_starts = std::mem::take(&mut self.actor_child_drag_starts);
                                    let (old_tf, new_tf, child_transforms) = if let Some(&(_, old_mat, new_mat)) = transforms.first() {
                                        let delta = mat4x4_mul(new_mat, mat4x4_inv(old_mat));
                                        let mut old_v = ActorTransform::default();
                                        let mut new_v = ActorTransform::default();
                                        if let Some(scene) = &mut self.scene {
                                            // entity を先に取り出して actors の borrow を解放する
                                            let entity = {
                                                let mut c = 0u32;
                                                find_actor_by_dfs(&scene.actors, wl, dfs_id, &mut c)
                                                    .map(|a| a.entity)
                                            };
                                            if let Some(entity) = entity {
                                                old_v = scene.world.get::<ActorTransform>(entity).cloned().unwrap_or_default();
                                                new_v = ActorTransform::from_mat4(&mat4x4_mul(delta, old_v.to_mat4()));
                                                if let Some(tf) = scene.world.get_mut::<ActorTransform>(entity) { *tf = new_v.clone(); }
                                            }
                                        }
                                        // 子アクターの Transform を更新し Undo 用データを収集
                                        let mut child_transforms = Vec::new();
                                        for (child_dfs, start_mat) in child_drag_starts {
                                            if let Some(scene) = &mut self.scene {
                                                // actor.entity（Transform 用）とスロット entity（MC 用）を別々に取得する
                                                let (child_entity_opt, mc_slot_entity) = {
                                                    let mut c = 0u32;
                                                    find_actor_by_dfs(&scene.actors, wl, child_dfs, &mut c)
                                                        .map(|a| (Some(a.entity), a.mc_entity()))
                                                        .unwrap_or((None, None))
                                                };
                                                if let Some(child_entity) = child_entity_opt {
                                                    let old_child_tf = scene.world.get::<ActorTransform>(child_entity)
                                                        .cloned().unwrap_or_default();
                                                    let new_child_tf = ActorTransform::from_mat4(
                                                        &mat4x4_mul(delta, old_child_tf.to_mat4()));
                                                    if let Some(tf) = scene.world.get_mut::<ActorTransform>(child_entity) {
                                                        *tf = new_child_tf.clone();
                                                    }
                                                    let new_mc_mat = mc_slot_entity
                                                        .and_then(|e| scene.world.get::<ModelComponent>(e))
                                                        .and_then(|mc| mc.instance_mats.first().copied())
                                                        .unwrap_or(start_mat);
                                                    child_transforms.push((child_dfs, old_child_tf, new_child_tf, start_mat, new_mc_mat));
                                                }
                                            }
                                        }
                                        (old_v, new_v, child_transforms)
                                    } else {
                                        // インスタンス変化なし: 現在の Transform を取得
                                        let tf = self.scene.as_ref().and_then(|s| {
                                            let mut c = 0u32;
                                            find_actor_by_dfs(&s.actors, wl, dfs_id, &mut c)
                                                .and_then(|a| s.world.get::<ActorTransform>(a.entity).cloned())
                                        }).unwrap_or_default();
                                        (tf.clone(), tf, Vec::new())
                                    };
                                    self.send_actor_components(dfs_id, self.actor_virtual_selected_slot_idx);
                                    if !transforms.is_empty() || !extra_slot_transforms.is_empty() {
                                        self.undo_history.record(Box::new(ActorGroupTransformCommand {
                                            wl, dfs_id, old_tf, new_tf, transforms,
                                            child_transforms, extra_slot_transforms,
                                        }));
                                        primary_recorded = true;
                                        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                                    }
                                } else {
                                    if !transforms.is_empty() {
                                        self.undo_history.record(Box::new(MultiTransformCommand { transforms }));
                                        primary_recorded = true;
                                        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                                    }
                                    if self.selected_instances.len() == 1 {
                                        self.send_actor_data(self.selected_instances[0]);
                                    }
                                }
                            }
                        } else {
                            self.actor_transform_drag_start = None;
                            self.drag_root_starts.clear();
                            self.drag_child_starts.clear();
                            self.actor_child_drag_starts.clear();
                            self.actor_extra_mc_drag_starts.clear();
                        }

                        // マルチ選択ドラッグ終了: 非プライマリアクターの Transform を記録する
                        // プライマリのコマンドが直前に記録済みなら CompositeCommand で一括化する
                        if !self.multi_actor_drag_starts.is_empty() {
                            let wl = self.active_world_line;
                            let mut drag_transforms: Vec<(u32, [[f32; 4]; 4], [[f32; 4]; 4])> = Vec::new();
                            if let Some(scene) = self.scene.as_ref() {
                                for &(other_dfs, old_mat) in &self.multi_actor_drag_starts {
                                    let mut c = 0u32;
                                    let new_mat = find_actor_by_dfs(&scene.actors, wl, other_dfs, &mut c)
                                        .and_then(|a| a.mc_entity()
                                            .and_then(|e| scene.world.get::<ModelComponent>(e))
                                            .and_then(|mc| mc.instance_mats.first().copied())
                                            .or_else(|| scene.world.get::<ActorTransform>(a.entity)
                                                .map(|tf| tf.to_mat4())))
                                        .unwrap_or(old_mat);
                                    drag_transforms.push((other_dfs, old_mat, new_mat));
                                }
                            }
                            if !drag_transforms.is_empty() {
                                let multi_cmd: Box<dyn crate::engine::core::app_base::undo::Command> =
                                    Box::new(MultiActorDragTransformCommand { wl, transforms: drag_transforms });
                                if primary_recorded {
                                    // プライマリのコマンドを取り出して CompositeCommand に統合する
                                    if let Some(primary_cmd) = self.undo_history.pop_last() {
                                        self.undo_history.record(Box::new(CompositeCommand {
                                            commands: vec![primary_cmd, multi_cmd],
                                        }));
                                    } else {
                                        self.undo_history.record(multi_cmd);
                                    }
                                } else {
                                    self.undo_history.record(multi_cmd);
                                }
                                if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
                            }
                            self.multi_actor_drag_starts.clear();
                        }
                        self.gizmo_drag = None;
                        // ドラッグ終了後はホバーを再評価する
                        self.hovered_gizmo_part = self.last_cursor_pos
                            .and_then(|(cx, cy)| self.compute_gizmo_hover(cx, cy));
                    }
                }

                if button == winit::event::MouseButton::Right {
                    self.cam_input.rmb = pressed;
                    // カメラ grab は Edit / Pause モードのみ。
                    // Play モードでは editor 側が ClipCursor を管理するため
                    // ここで ClipCursor(null) を呼ばないようにする。
                    if self.mode == RuntimeMode::Edit || self.paused {
                        if let Some(window) = &self.window {
                            if pressed {
                                self.rmb_press_pos = self.last_cursor_pos;
                                self.rmb_moved     = false;
                                self.cam_grab_screen_pos =
                                    camera_grab_start(self.window_hwnd());
                                window.set_cursor_visible(false);
                            } else {
                                window.set_cursor_visible(true);
                                if let Some((x, y)) = self.cam_grab_screen_pos.take() {
                                    camera_grab_end(x, y);
                                    // 短押し（カーソル移動なし）→ コンテキストメニュー
                                    if !self.rmb_moved {
                                        if let Some(ipc) = &self.ipc {
                                            ipc.send("CONTEXT_MENU");
                                        }
                                    }
                                } else {
                                    // RMB_DOWN が処理されていない（コンテキストメニューの
                                    // ポップアップが WM_RBUTTONDOWN を横取りした等）。
                                    // ClipCursor のみ解除し SetCursorPos(0,0) は呼ばない。
                                    release_window_clamp();
                                }
                                self.rmb_press_pos = None;
                                self.rmb_moved     = false;
                            }
                        }
                    }
                }
            }

            WindowEvent::CursorMoved { position, .. } => {
                let cx = position.x as f32;
                let cy = position.y as f32;
                self.input.process_cursor_moved(cx, cy);
                self.last_cursor_pos = Some((cx, cy));

                // RMB 押下中に移動量が閾値を超えたらカメラ grab とみなす
                if self.cam_input.rmb && !self.rmb_moved {
                    if let Some((px, py)) = self.rmb_press_pos {
                        let dx = cx - px;
                        let dy = cy - py;
                        if dx * dx + dy * dy > 25.0 {
                            self.rmb_moved = true;
                        }
                    }
                }

                // 矩形選択の更新（LMB 押下中かつギズモドラッグなし）
                if self.lmb_held && self.gizmo_drag.is_none() {
                    if let Some((px, py)) = self.lmb_press_pos {
                        let dx = cx - px;
                        let dy = cy - py;
                        if !self.rect_selecting && dx * dx + dy * dy > 25.0 {
                            self.rect_selecting = true;
                            self.selection_before_rect         = self.selected_instances.clone();
                            self.selection_before_rect_dfs     = self.selected_actor_dfs_ids.clone();
                            self.selection_before_rect_primary = self.actor_virtual_selected_idx;
                        }
                        if self.rect_selecting {
                            let sx_min = px.min(cx);
                            let sx_max = px.max(cx);
                            let sy_min = py.min(cy);
                            let sy_max = py.max(cy);
                            if let Some(scene) = &self.scene {
                                if let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) {
                                    let wl   = self.active_world_line;
                                    let vp_w = ws.width as f32;
                                    let vp_h = ws.height as f32;
                                    let mut rect_dfs: Vec<usize> = Vec::new();

                                    if self.canvas_world_lines.contains(&wl) {
                                        // 2D キャンバス: スクリーン矩形をワールド矩形に変換して
                                        // CanvasTransform.position が範囲内かで判定する
                                        let cam_2d = self.canvas_cameras.get(&wl);
                                        let pan_x  = cam_2d.map(|c| c.pan_x).unwrap_or(0.0);
                                        let pan_y  = cam_2d.map(|c| c.pan_y).unwrap_or(0.0);
                                        let half_h = cam_2d.map(|c| c.ortho_half_h).unwrap_or(10.0);
                                        let half_w = half_h * (vp_w / vp_h);
                                        // Y-down 規則（bottom=+half_h）でスクリーン→ワールド変換
                                        let wx_min = pan_x + (2.0 * sx_min / vp_w - 1.0) * half_w;
                                        let wx_max = pan_x + (2.0 * sx_max / vp_w - 1.0) * half_w;
                                        let wy_min = pan_y + (2.0 * sy_min / vp_h - 1.0) * half_h;
                                        let wy_max = pan_y + (2.0 * sy_max / vp_h - 1.0) * half_h;
                                        let mut dfs_counter = 0u32;
                                        for actor in scene.actors.iter().filter(|a| a.world_line == wl) {
                                            collect_canvas_actors_in_rect(
                                                actor, &scene.world, &mut dfs_counter,
                                                wx_min, wx_max, wy_min, wy_max, &mut rect_dfs,
                                            );
                                        }
                                    } else {
                                        // 3D: MC の instance_mats をスクリーン投影して判定する
                                        let view    = self.camera.view_matrix();
                                        let proj    = self.camera.projection_matrix();
                                        let all_mcs = collect_mcs_in_world_line(&scene.actors, &scene.world, wl);
                                        for (_base, dfs_id, _slot_i, mc) in all_mcs {
                                            let in_rect = mc.instance_mats.iter().any(|m| {
                                                let world = [m[0][3], m[1][3], m[2][3]];
                                                world_to_screen(world, &view.data, &proj.data, vp_w, vp_h)
                                                    .map(|(sx, sy)| sx >= sx_min && sx <= sx_max && sy >= sy_min && sy <= sy_max)
                                                    .unwrap_or(false)
                                            });
                                            if in_rect && !rect_dfs.contains(&(dfs_id as usize)) {
                                                rect_dfs.push(dfs_id as usize);
                                            }
                                        }
                                    }

                                    self.selected_actor_dfs_ids     = rect_dfs.clone();
                                    self.actor_virtual_selected_idx = rect_dfs.last().copied();
                                    self.selected_instances.clear();
                                }
                            }
                        }
                    }
                }

                // ホバーパーツを更新（ドラッグ中はドラッグパーツを維持）
                self.hovered_gizmo_part = if let Some(drag) = &self.gizmo_drag {
                    Some(drag.part)
                } else {
                    self.compute_gizmo_hover(cx, cy)
                };

                // ギズモドラッグ中: 新しい変換行列を計算してインスタンスに適用する
                let new_mat_opt = if let Some(drag) = &self.gizmo_drag {
                    if let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) {
                        let vp_w = ws.width  as f32;
                        let vp_h = ws.height as f32;
                        let wl_drag = self.active_world_line;
                        let (ro, rd) = if self.canvas_world_lines.contains(&wl_drag) {
                            // 2D ortho: スクリーン座標を直接ワールド XY に変換するレイ
                            let cam_2d = self.canvas_cameras.get(&wl_drag);
                            let pan_x  = cam_2d.map(|c| c.pan_x).unwrap_or(0.0);
                            let pan_y  = cam_2d.map(|c| c.pan_y).unwrap_or(0.0);
                            let half_h = cam_2d.map(|c| c.ortho_half_h).unwrap_or(10.0);
                            let half_w = half_h * (vp_w / vp_h);
                            screen_to_ray_ortho(cx, cy, vp_w, vp_h, pan_x, pan_y, half_w, half_h)
                        } else {
                            let cam_v = self.camera.position();
                            let cam   = [cam_v.x, cam_v.y, cam_v.z];
                            let view  = self.camera.view_matrix();
                            let proj  = self.camera.projection_matrix();
                            screen_to_ray(cx, cy, vp_w, vp_h, &view.data, &proj.data, cam)
                        };
                        Some(update_drag(drag, ro, rd))
                    } else { None }
                } else { None };

                if let Some(new_mat) = new_mat_opt {
                    if let Some(drag) = &self.gizmo_drag {
                        let delta        = mat4x4_mul(new_mat, mat4x4_inv(drag.start_mat));
                        let wl           = self.active_world_line;
                        let selected_dfs = self.actor_virtual_selected_idx;

                        if let Some(scene) = &mut self.scene {
                            // 選択スロット entity を取得して MC 行列にデルタを適用する
                            let selected_slot_i = self.actor_virtual_selected_slot_idx;
                            let mc_entity: Option<crate::engine::ecs::Entity> =
                                if let Some(dfs) = selected_dfs {
                                    let mut c = 0u32;
                                    find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c)
                                        .and_then(|a| a.mc_entity_at(selected_slot_i))
                                } else { None };
                            if let Some(mc) = mc_entity.and_then(|e| scene.world.get_mut::<ModelComponent>(e)) {
                                for &(idx, ref start) in &self.drag_root_starts {
                                    if let Some(m) = mc.instance_mats.get_mut(idx as usize) {
                                        *m = mat4x4_mul(delta, *start);
                                    }
                                }
                                for &(idx, ref start) in &self.drag_child_starts {
                                    if let Some(m) = mc.instance_mats.get_mut(idx as usize) {
                                        *m = mat4x4_mul(delta, *start);
                                    }
                                }
                                mc.mark_batch_dirty();
                            }
                            // 追加 MC スロット（選択スロット以外）にも同デルタを適用する
                            if let Some(dfs) = selected_dfs {
                                let extra_starts = self.actor_extra_mc_drag_starts.clone();
                                for (slot_i, start_mats) in &extra_starts {
                                    let mut c = 0u32;
                                    let slot_entity = find_actor_by_dfs(&scene.actors, wl, dfs as u32, &mut c)
                                        .and_then(|a| a.mc_entity_at(*slot_i));
                                    if let Some(entity) = slot_entity {
                                        if let Some(mc) = scene.world.get_mut::<ModelComponent>(entity) {
                                            for (m, start) in mc.instance_mats.iter_mut().zip(start_mats.iter()) {
                                                *m = mat4x4_mul(delta, *start);
                                            }
                                            mc.mark_batch_dirty();
                                        }
                                    }
                                }
                            }
                            // MC なし（インスタンス空含む）のアクターは Transform を直接ドラッグする
                            if self.drag_root_starts.is_empty() {
                                if self.canvas_world_lines.contains(&wl) {
                                    // 2D: CanvasTransform の XY 位置・Z 回転・XY スケールを更新する
                                    if let Some((drag_dfs, ref start_ct)) = self.canvas_transform_drag_start.clone() {
                                        let entity = {
                                            let mut c = 0u32;
                                            find_actor_by_dfs(&scene.actors, wl, drag_dfs, &mut c)
                                                .map(|a| a.entity)
                                        };
                                        if let Some(entity) = entity {
                                            if let Some(ct) = scene.world.get_mut::<CanvasTransform>(entity) {
                                                // centroid_mat は単位回転・スケール+平行移動のみなので
                                                // new_mat はツールモードに応じた変化のみを持つ。
                                                // モードごとに変化する成分だけを更新し、他はドラッグ開始値を維持する。
                                                ct.position[0] = new_mat[0][3];
                                                ct.position[1] = new_mat[1][3];
                                                match self.tool_mode {
                                                    ToolMode::Rotate => {
                                                        // new_mat = Rz(delta) * T(pos) なので col0 の XY 角度がデルタ回転
                                                        // ドラッグ開始角に加算して最終回転を得る
                                                        let delta_angle = new_mat[1][0].atan2(new_mat[0][0]).to_degrees();
                                                        ct.rotation = start_ct.rotation + delta_angle;
                                                        // スケールは変化なし
                                                        ct.scale = start_ct.scale;
                                                    }
                                                    ToolMode::Scale => {
                                                        // new_mat の各列の長さ = centroid 起点のスケール係数
                                                        // start_scale × factor で新しい絶対スケールを得る
                                                        let sx = (new_mat[0][0]*new_mat[0][0] + new_mat[1][0]*new_mat[1][0]).sqrt();
                                                        let sy = (new_mat[0][1]*new_mat[0][1] + new_mat[1][1]*new_mat[1][1]).sqrt();
                                                        if sx > 0.001 { ct.scale[0] = start_ct.scale[0] * sx; }
                                                        if sy > 0.001 { ct.scale[1] = start_ct.scale[1] * sy; }
                                                        // 回転は変化なし
                                                        ct.rotation = start_ct.rotation;
                                                    }
                                                    _ => {
                                                        // Move: 位置のみ変化（回転・スケールはドラッグ開始値を維持）
                                                        ct.rotation = start_ct.rotation;
                                                        ct.scale    = start_ct.scale;
                                                    }
                                                }
                                                // ピボットはドラッグ中変化なし
                                                ct.pivot = start_ct.pivot;
                                            }
                                        }
                                    }
                                } else if let Some((drag_dfs, ref start_tf)) = self.actor_transform_drag_start.clone() {
                                    let new_mat = mat4x4_mul(delta, start_tf.to_mat4());
                                    let entity = {
                                        let mut c = 0u32;
                                        find_actor_by_dfs(&scene.actors, wl, drag_dfs, &mut c)
                                            .map(|a| a.entity)
                                    };
                                    if let Some(entity) = entity {
                                        if let Some(tf) = scene.world.get_mut::<ActorTransform>(entity) {
                                            *tf = ActorTransform::from_mat4(&new_mat);
                                        }
                                    }
                                }
                            }
                            // 子アクター MC にも同デルタを適用する
                            {
                                let child_starts = self.actor_child_drag_starts.clone();
                                for (child_dfs, start_mat) in &child_starts {
                                    // スロット entity 経由で子アクターの MC を更新する
                                    let mc_slot_entity = {
                                        let mut c = 0u32;
                                        find_actor_by_dfs(&scene.actors, wl, *child_dfs, &mut c)
                                            .and_then(|a| a.mc_entity())
                                    };
                                    if let Some(entity) = mc_slot_entity {
                                        if let Some(mc) = scene.world.get_mut::<ModelComponent>(entity) {
                                            if let Some(m) = mc.instance_mats.first_mut() {
                                                *m = mat4x4_mul(delta, *start_mat);
                                            }
                                            mc.mark_batch_dirty();
                                        }
                                    }
                                }
                            }
                            // マルチ選択: プライマリ以外の全選択アクターにも同デルタを適用する
                            if !self.multi_actor_drag_starts.is_empty() {
                                let multi_starts = self.multi_actor_drag_starts.clone();
                                for (other_dfs, start_mat) in &multi_starts {
                                    let mut c = 0u32;
                                    if let Some(actor) = find_actor_by_dfs(&scene.actors, wl, *other_dfs, &mut c) {
                                        let new_mat = mat4x4_mul(delta, *start_mat);
                                        let actor_entity = actor.entity;
                                        let mc_entity = actor.mc_entity();
                                        // MC の instance_mats を更新する（GPU 描画位置）
                                        if let Some(me) = mc_entity {
                                            if let Some(mc) = scene.world.get_mut::<ModelComponent>(me) {
                                                if let Some(m) = mc.instance_mats.first_mut() {
                                                    *m = new_mat;
                                                }
                                                mc.mark_batch_dirty();
                                            }
                                        }
                                        // ActorTransform も更新する（Inspector 反映用）
                                        if let Some(tf) = scene.world.get_mut::<ActorTransform>(actor_entity) {
                                            *tf = ActorTransform::from_mat4(&new_mat);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    // ドラッグ中のリアルタイム IPC 送信は廃止（ドラッグ終了時に送信）。
                }
            }

            WindowEvent::MouseWheel { delta, .. } => {
                self.input.process_scroll(&delta);
                let lines = match delta {
                    winit::event::MouseScrollDelta::LineDelta(_, y) => y,
                    winit::event::MouseScrollDelta::PixelDelta(p) => p.y as f32 / 20.0,
                };
                self.cam_input.scroll += lines;
            }

            // ─── メインループ ─────────────────────────────────
            WindowEvent::RedrawRequested => {
                self.process_ipc(event_loop);

                // ヒエラルキー遅延フラッシュ（スロットリングで保留されていた送信）
                if self.hierarchy_dirty {
                    let now = std::time::Instant::now();
                    let ready = self.last_hierarchy_send
                        .map(|t| now.duration_since(t).as_millis() >= 100)
                        .unwrap_or(true);
                    if ready {
                        self.hierarchy_dirty = false;
                        self.last_hierarchy_send = Some(now);
                        self.do_send_hierarchy();
                    }
                }

                // Play クランプが有効な間は毎フレーム ClipCursor を再適用する。
                if self.play_clamp {
                    apply_window_clamp(self.window_hwnd());
                }

                // ── 時間 ──────────────────────────────────────
                let time_running = self.mode == RuntimeMode::Play && !self.paused;
                let ctx: FrameContext = self.clock.tick(time_running);
                let in_editor = self.mode == RuntimeMode::Edit || self.paused;
                // 現在の世界線が 2D キャンバスモードかどうか
                let is_canvas = self.canvas_world_lines.contains(&self.active_world_line);

                if in_editor {
                    if is_canvas {
                        // 2D キャンバスモード: RMB ドラッグで XY パン、スクロールでズーム
                        if self.cam_input.rmb {
                            let ws = self.window.as_ref().map(|w| {
                                let s = w.inner_size();
                                [s.width as f32, s.height as f32]
                            }).unwrap_or([1280.0, 720.0]);
                            let cam_2d = self.canvas_cameras
                                .entry(self.active_world_line)
                                .or_insert_with(CanvasCameraData::default);
                            // ビューポート高さあたりのワールドユニット（スケール係数）
                            let scale = (2.0 * cam_2d.ortho_half_h) / ws[1];
                            // raw delta: right/up が正。X は符号反転、Y は Y-down 座標系のため同符号
                            cam_2d.pan_x -= self.cam_input.mouse_dx * scale;
                            cam_2d.pan_y -= self.cam_input.mouse_dy * scale;
                        }
                        if self.cam_input.scroll != 0.0 {
                            let cam_2d = self.canvas_cameras
                                .entry(self.active_world_line)
                                .or_insert_with(CanvasCameraData::default);
                            // scroll 正 = ホイール上 = ズームイン（half_h を小さくする）
                            cam_2d.ortho_half_h = (cam_2d.ortho_half_h
                                * 0.9_f32.powf(self.cam_input.scroll))
                                .clamp(0.5, 1000.0);
                        }
                    } else {
                        // 3D モード: 通常のデバッグカメラ更新
                        self.camera.update(&self.cam_input, ctx.delta_time);
                    }
                }

                // ─ 1-6. ゲームロジック（Play 時のみ）─────────
                if time_running {
                    if let Some(scene) = &mut self.scene { scene.begin_frame(&ctx); }
                    if let Some(scene) = &mut self.scene { scene.early_update(&ctx); }
                    if let Some(scene) = &mut self.scene { scene.update(&ctx); }
                    for fixed_ctx in self.clock.drain_fixed() {
                        if let Some(scene) = &mut self.scene { scene.constant_update(&fixed_ctx); }
                    }
                    if let Some(scene) = &mut self.scene { scene.late_update(&ctx); }
                    if let Some(scene) = &mut self.scene { scene.render(&ctx); }
                }

                // ── GPU カメラ・インスタンスバッファ更新 ──────
                let window_size = self.window.as_ref().map(|w| w.inner_size());
                let queue = self.draw_ctx.as_ref().map(|c| c.queue.clone());

                if let (Some(scene), Some(camera_buf), Some(queue)) =
                    (&mut self.scene, &self.camera_buf, queue)
                {
                    // 2D キャンバスモードと 3D モードでビュー行列・射影行列を切り替える
                    let (view, proj, cam_pos_arr) = if is_canvas {
                        let cam_2d = self.canvas_cameras
                            .entry(self.active_world_line)
                            .or_insert_with(CanvasCameraData::default);
                        let aspect = window_size.map_or(16.0 / 9.0, |s| {
                            s.width as f32 / s.height as f32
                        });
                        let half_h = cam_2d.ortho_half_h;
                        let half_w = half_h * aspect;
                        // カメラは Z=-100 から XY 平面（Z=0）を見下ろす（LH forward = +Z）
                        let eye    = Vector3::new(cam_2d.pan_x, cam_2d.pan_y, -100.0);
                        let center = Vector3::new(cam_2d.pan_x, cam_2d.pan_y, 0.0);
                        let up     = Vector3::new(0.0, 1.0, 0.0);
                        let v = Mat4x4::look_at_lh(eye, center, up);
                        // Y-down 座標系: bottom=+half_h, top=-half_h にすることで
                        // ワールド Y の正方向がスクリーン下向きになる（UI/キャンバス標準規則）
                        let p = Mat4x4::orthographic_lh(-half_w, half_w, half_h, -half_h, 0.0, 200.0);
                        (v, p, [cam_2d.pan_x, cam_2d.pan_y, -100.0])
                    } else {
                        let v  = self.camera.view_matrix();
                        let p  = self.camera.projection_matrix();
                        let cp = self.camera.position();
                        (v, p, [cp.x, cp.y, cp.z])
                    };

                    let view_proj = proj * view;

                    let res = window_size.map_or([1280.0, 720.0], |s| {
                        [s.width as f32, s.height as f32]
                    });
                    camera_buf.update(&queue, &CameraUniform {
                        view_proj:  view_proj.transpose().data,
                        view:       view.transpose().data,
                        position:   cam_pos_arr,
                        _pad:       0.0,
                        resolution: res,
                        _pad2:      [0.0; 2],
                    });

                    let frustum_planes = extract_frustum_planes(&view_proj.data);
                    let camera_pos     = cam_pos_arr;

                    // シーンモード・アクター編集モード共通: world_line 全 MC を DFS で更新する
                    let (actors, world) = (&mut scene.actors, &mut scene.world);
                    super::update_all_mc_batches_for_wl(
                        actors, world, self.active_world_line,
                        &queue, &frustum_planes, camera_pos, self.clock.anim_time(),
                    );
                }

                // ── ギズモ位置：全選択アクターの重心（マルチ選択対応） ──
                let gizmo_pos = self.selected_actors_centroid()
                    .or_else(|| self.actor_virtual_world_pos());

                // アクター仮想選択のワールド位置（レンダラー借用外で取得）
                let actor_virtual_pos: Option<[f32; 3]> = if self.actor_virtual_selected_idx.is_some() {
                    self.actor_virtual_world_pos()
                } else { None };

                // ── ドラッグホバープレビュー位置の更新（レンダー前）──────────
                // GPU リードバックを使わずレイキャストで直接ワールド座標を算出する。
                // y=0 平面との交差を優先し、カメラが平行または後方なら DEFAULT_DIST 先。
                if let Some((hsx, hsy)) = self.pending_drop_hover.take() {
                    const DEFAULT_DIST: f32 = 10.0;
                    if let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) {
                        let cam_v = self.camera.position();
                        let cam   = [cam_v.x, cam_v.y, cam_v.z];
                        let view  = self.camera.view_matrix();
                        let proj  = self.camera.projection_matrix();
                        let (_ro, rd) = screen_to_ray(
                            hsx as f32, hsy as f32,
                            ws.width as f32, ws.height as f32,
                            &view.data, &proj.data, cam,
                        );
                        // y=0 平面との交差を試みる（地面への自然な配置）
                        let pos = if rd[1].abs() > 0.001 {
                            let t = -cam[1] / rd[1];
                            if t > 0.5 {
                                [cam[0]+rd[0]*t, 0.0, cam[2]+rd[2]*t]
                            } else {
                                [cam[0]+rd[0]*DEFAULT_DIST, cam[1]+rd[1]*DEFAULT_DIST, cam[2]+rd[2]*DEFAULT_DIST]
                            }
                        } else {
                            [cam[0]+rd[0]*DEFAULT_DIST, cam[1]+rd[1]*DEFAULT_DIST, cam[2]+rd[2]*DEFAULT_DIST]
                        };
                        self.drop_preview_pos = Some(pos);
                    }
                }

                // ピック要求を取り出す（描画ブロック内で使用）
                let pick_pos = self.pending_pick.take();
                let mut did_pick = false;

                // ピック結果デコード用 MC 情報 (base, dfs_id, slot_i, instance_count)
                let wl_mc_pick_infos: Vec<(u32, u32, usize, usize)> = {
                    if let Some(scene) = &self.scene {
                        collect_mcs_in_world_line(&scene.actors, &scene.world, self.active_world_line)
                            .into_iter()
                            .map(|(base, dfs, slot_i, mc)| (base, dfs, slot_i, mc.instance_mats.len()))
                            .collect()
                    } else { vec![] }
                };

                if let (Some(renderer), Some(scene), Some(camera_buf), Some(draw_ctx)) =
                    (&mut self.renderer, &self.scene, &self.camera_buf, &self.draw_ctx)
                {
                    match renderer.begin_frame() {
                        Ok(mut frame) => {
                            // シーンモード・アクター編集モード共通: world_line の全 MC を収集する
                            // タプル: (id_base, dfs_id, slot_i, &ModelComponent)
                            let all_mcs: Vec<(u32, u32, usize, &ModelComponent)> =
                                collect_mcs_in_world_line(&scene.actors, &scene.world, self.active_world_line);
                            // 後方互換: 単一 MC として使う箇所用（シーン編集モード or 先頭 MC）
                            let _mc = all_mcs.first().map(|&(_, _, _, mc)| mc);
                            // 選択中アクターの MC（アウトライン・アイコン用）
                            let selected_slot_i = self.actor_virtual_selected_slot_idx;
                            let selected_mc: Option<&ModelComponent> =
                                self.actor_virtual_selected_idx
                                    .and_then(|dfs| all_mcs.iter()
                                        .find(|&&(_, d, si, _)| d == dfs as u32 && si == selected_slot_i)
                                        .map(|&(_, _, _, mc)| mc));

                            // スキンメッシュコンピュート: 全 MC に対して実行
                            for &(_, _, _, amc) in &all_mcs {
                                if let Some(batch) = amc.instanced_batch.as_ref() {
                                    batch.dispatch_skin(
                                        frame.encoder_mut(),
                                        &draw_ctx.pipelines.skin_compute,
                                    );
                                }
                            }

                            // ギズモ GPU バッファ（レンダーパスの前に生成）
                            let show_gizmo_pre = self.mode == RuntimeMode::Edit || self.paused;
                            let gizmo_gpu_batch = if show_gizmo_pre
                                && self.tool_mode != ToolMode::Select
                            {
                                gizmo_pos.map(|pos| {
                                    // 2D/3D でギズモ半径とカメラ位置を切り替える
                                    let (radius, cam_pos_arr) = if is_canvas {
                                        // 2D ortho: 表示高さの 15% をギズモ半径とする
                                        let cam_2d = self.canvas_cameras.get(&self.active_world_line);
                                        let r = cam_2d.map(|c| c.ortho_half_h * 0.15).unwrap_or(54.0);
                                        let cp = [
                                            cam_2d.map(|c| c.pan_x).unwrap_or(0.0),
                                            cam_2d.map(|c| c.pan_y).unwrap_or(0.0),
                                            -100.0f32,
                                        ];
                                        (r, cp)
                                    } else {
                                        // 3D perspective: 距離と FOV からギズモ半径を計算する
                                        let cam_pos = self.camera.position();
                                        let d = [pos[0]-cam_pos.x, pos[1]-cam_pos.y, pos[2]-cam_pos.z];
                                        let dist = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(0.01);
                                        let half_fov = self.camera.base.projection.fov_y_rad * 0.5;
                                        let r = dist * half_fov.tan() * 0.233;
                                        (r, [cam_pos.x, cam_pos.y, cam_pos.z])
                                    };

                                    let hov  = self.hovered_gizmo_part;
                                    let drag_part = self.gizmo_drag.as_ref().map(|d| d.part);
                                    let mut batch = GizmoBatch::new();
                                    if is_canvas {
                                        // 2D: Z 軸・平面ハンドル不要、Rotate は完全円
                                        match self.tool_mode {
                                            ToolMode::Move   => batch.add_gizmo_translate_2d(pos, radius, hov),
                                            ToolMode::Rotate => batch.add_gizmo_rotate_2d(pos, radius, 64, hov),
                                            ToolMode::Scale  => batch.add_gizmo_scale_2d(pos, radius, hov),
                                            ToolMode::Select => {}
                                        }
                                    } else {
                                        // 3D: 全軸・平面ハンドル、Rotate は半円
                                        match self.tool_mode {
                                            ToolMode::Move   => batch.add_gizmo_translate(pos, radius, hov),
                                            ToolMode::Rotate => batch.add_gizmo_rotate(pos, radius, 64, cam_pos_arr, hov, drag_part),
                                            ToolMode::Scale  => batch.add_gizmo_scale(pos, radius, hov),
                                            ToolMode::Select => {}
                                        }
                                    }
                                    batch.build(&draw_ctx.device)
                                })
                            } else { None };

                            // 矩形選択ビジュアル（レンダーパスの前に GPU バッファを生成）
                            let rect_gpu_batch = if in_editor && self.rect_selecting {
                                if let (Some((px, py)), Some((cx, cy))) =
                                    (self.lmb_press_pos, self.last_cursor_pos)
                                {
                                    let vp_w = window_size.map_or(1280.0, |s| s.width  as f32);
                                    let vp_h = window_size.map_or(720.0,  |s| s.height as f32);
                                    let sc = [
                                        (px.min(cx), py.min(cy)), // TL
                                        (px.max(cx), py.min(cy)), // TR
                                        (px.max(cx), py.max(cy)), // BR
                                        (px.min(cx), py.max(cy)), // BL
                                    ];
                                    let mut wp = [[0.0f32; 3]; 4];
                                    if is_canvas {
                                        // 2D ortho: スクリーン座標をワールド XY に直接変換する
                                        // Y-down 規則（bottom=+half_h, top=-half_h）に合わせる
                                        let cam_2d = self.canvas_cameras.get(&self.active_world_line);
                                        let pan_x  = cam_2d.map(|c| c.pan_x).unwrap_or(0.0);
                                        let pan_y  = cam_2d.map(|c| c.pan_y).unwrap_or(0.0);
                                        let half_h = cam_2d.map(|c| c.ortho_half_h).unwrap_or(360.0);
                                        let half_w = half_h * (vp_w / vp_h);
                                        for (i, &(sx, sy)) in sc.iter().enumerate() {
                                            let nx = 2.0 * sx / vp_w - 1.0;
                                            let ny = 2.0 * sy / vp_h - 1.0; // Y-down
                                            wp[i] = [pan_x + nx * half_w, pan_y + ny * half_h, 0.0];
                                        }
                                    } else {
                                        // 3D perspective: near plane の手前にワールド座標を投影する
                                        let view    = self.camera.view_matrix();
                                        let proj    = self.camera.projection_matrix();
                                        let cam_pv  = self.camera.position();
                                        let cam_pos = [cam_pv.x, cam_pv.y, cam_pv.z];
                                        let near_vs = self.camera.base.projection.near * 1.05;
                                        let p = &proj.data;
                                        let v = &view.data;
                                        for (i, &(sx, sy)) in sc.iter().enumerate() {
                                            let nx  = 2.0 * sx / vp_w - 1.0;
                                            let ny  = 1.0 - 2.0 * sy / vp_h;
                                            let vpx = (nx / p[0][0]) * near_vs;
                                            let vpy = (ny / p[1][1]) * near_vs;
                                            let vpz = near_vs;
                                            wp[i] = [
                                                cam_pos[0] + v[0][0]*vpx + v[1][0]*vpy + v[2][0]*vpz,
                                                cam_pos[1] + v[0][1]*vpx + v[1][1]*vpy + v[2][1]*vpz,
                                                cam_pos[2] + v[0][2]*vpx + v[1][2]*vpy + v[2][2]*vpz,
                                            ];
                                        }
                                    }
                                    let color = [0.3, 0.7, 1.0, 1.0f32];
                                    let mut lb = LineBatch::new();
                                    lb.add_line(wp[0], wp[1], color);
                                    lb.add_line(wp[1], wp[2], color);
                                    lb.add_line(wp[2], wp[3], color);
                                    lb.add_line(wp[3], wp[0], color);
                                    Some(lb.build(&draw_ctx.device))
                                } else { None }
                            } else { None };

                            // ドロッププレビュー球体バッチ（ドラッグ中のみ）
                            const PREVIEW_SPHERE_RADIUS: f32 = 0.5;
                            let drop_preview_batch = if let Some(pos) = self.drop_preview_pos {
                                let mut gb = GizmoBatch::new();
                                gb.add_solid_sphere(
                                    pos,
                                    PREVIEW_SPHERE_RADIUS,
                                    16,  // stacks
                                    24,  // slices
                                    Color::new(0.3, 0.8, 1.0, 0.85),
                                );
                                Some(gb.build(&draw_ctx.device))
                            } else { None };

                            // グリッド描画バッチ（エディタモード + show_grid のみ）
                            // 3D アクター編集モードはグリッドを常時表示。
                            // 2D キャンバスモードは show_grid フラグに従う。
                            // 2D キャンバスモードは XY 平面グリッド（Z=0）、3D は XZ 平面グリッド（Y=0）
                            let grid_gpu_batch = if in_editor && (self.show_grid || (self.active_world_line != 0 && !is_canvas)) {
                                let mut lb = LineBatch::new();
                                // 2D/3D モードで色を分ける
                                // 2D: minor を非常に薄く、major を中程度の明度で明確に区別する
                                // 3D 編集: 紺背景に映える青系
                                // 3D シーン: ダークグレー
                                let (minor, major): ([f32; 4], [f32; 4]) = if is_canvas {
                                    ([0.22, 0.25, 0.40, 0.20], [0.32, 0.40, 0.60, 0.55])
                                } else if self.active_world_line != 0 {
                                    ([0.22, 0.25, 0.40, 1.0], [0.32, 0.36, 0.55, 1.0])
                                } else {
                                    ([0.18, 0.18, 0.18, 1.0], [0.30, 0.30, 0.30, 1.0])
                                };
                                let ax_x: [f32; 4] = [0.60, 0.15, 0.15, 0.90];

                                if is_canvas {
                                    // 2D モード: XY 平面グリッド（Z=0）
                                    // カメラ追従 + 可視範囲に応じたステップ自動選択（Y-down 座標系）
                                    // Y 軸（X=0 の縦線）: 緑、X 軸（Y=0 の横線）: 赤
                                    let ax_y: [f32; 4] = [0.10, 0.55, 0.10, 0.90];

                                    // 2D カメラの状態を取得（view/proj 計算で entry 生成済みのため get で OK）
                                    let cam_2d = self.canvas_cameras
                                        .get(&self.active_world_line)
                                        .cloned()
                                        .unwrap_or_default();
                                    let aspect = window_size.map_or(16.0 / 9.0, |s| {
                                        s.width as f32 / s.height as f32
                                    });
                                    let half_h_2d = cam_2d.ortho_half_h;
                                    let half_w_2d = half_h_2d * aspect;

                                    // 可視高さを TARGET_DIVS 分割するステップを 1/2/5 × 10^n の「良い数」に丸める
                                    const TARGET_DIVS: f32 = 8.0;
                                    let raw_step = (2.0 * half_h_2d) / TARGET_DIVS;
                                    let exp = raw_step.log10().floor();
                                    let base = 10.0f32.powf(exp);
                                    let step = {
                                        let m = raw_step / base;
                                        if m < 1.5 { base } else if m < 3.5 { base * 2.0 } else { base * 5.0 }
                                    };

                                    // major ライン周期（minor 5本ごとに major 1本 → step の 5 倍ごと）
                                    const MAJOR_PERIOD: i64 = 5;

                                    // 可視範囲 + 1 セル分マージン（グリッドが画面端で途切れないよう）
                                    let vis_x0 = cam_2d.pan_x - half_w_2d - step;
                                    let vis_x1 = cam_2d.pan_x + half_w_2d + step;
                                    let vis_y0 = cam_2d.pan_y - half_h_2d - step;
                                    let vis_y1 = cam_2d.pan_y + half_h_2d + step;

                                    // 可視範囲に含まれるグリッドインデックス（step 単位）
                                    let ix_start = (vis_x0 / step).floor() as i64;
                                    let ix_end   = (vis_x1 / step).ceil()  as i64;
                                    let iy_start = (vis_y0 / step).floor() as i64;
                                    let iy_end   = (vis_y1 / step).ceil()  as i64;

                                    // 縦ライン（X = 定数, Y 方向に伸ばす）
                                    for ix in ix_start..=ix_end {
                                        let world_x = ix as f32 * step;
                                        let is_axis  = ix == 0;
                                        let is_major = !is_axis && (ix.rem_euclid(MAJOR_PERIOD) == 0);
                                        let col = if is_axis { ax_x } else if is_major { major } else { minor };
                                        lb.add_line([world_x, vis_y0, 0.0], [world_x, vis_y1, 0.0], col);
                                    }

                                    // 横ライン（Y = 定数, X 方向に伸ばす）
                                    for iy in iy_start..=iy_end {
                                        let world_y = iy as f32 * step;
                                        let is_axis  = iy == 0;
                                        let is_major = !is_axis && (iy.rem_euclid(MAJOR_PERIOD) == 0);
                                        let col = if is_axis { ax_y } else if is_major { major } else { minor };
                                        lb.add_line([vis_x0, world_y, 0.0], [vis_x1, world_y, 0.0], col);
                                    }
                                } else {
                                    // 3D モード: XZ 平面グリッド（Y=0）
                                    // カメラ追従＋深度フェード＋スケール段階切り替え
                                    let cam_pos = self.camera.base.transform.position;
                                    let cam_y   = cam_pos[1].abs();
                                    let cam_far = self.camera.base.projection.far;
                                    let ax_z: [f32; 4] = [0.10, 0.10, 0.50, 1.0];

                                    // グリッドスケール選択: (step, thick_period, tier_y_start, tier_y_end)
                                    let (step, thick_period, tier_start, tier_end): (f32, i64, f32, f32) =
                                        if cam_y < 1.0 {
                                            (0.1,  10, 0.0,  1.0)
                                        } else if cam_y < 10.0 {
                                            (1.0,   5, 1.0, 10.0)
                                        } else if cam_y < 30.0 {
                                            (5.0,   2, 10.0, 30.0)
                                        } else {
                                            (10.0,  5, 30.0, f32::INFINITY)
                                        };

                                    // minor ライン alpha: 区間始端=1.0 → 区間終端≈0.0
                                    let minor_alpha_base: f32 = if tier_end.is_finite() {
                                        (1.0 - (cam_y - tier_start) / (tier_end - tier_start)).max(0.0)
                                    } else {
                                        1.0
                                    };

                                    // n_half を cam_far / step まで伸ばしてグリッドが far 距離まで描画される
                                    let max_lines: i32 = if step < 0.5 { 300 } else { 2000 };
                                    let n_half: i32 = ((cam_far / step) as i32).min(max_lines);
                                    let ext    = n_half as f32 * step;
                                    let snap_x = (cam_pos[0] / step).floor() * step;
                                    let snap_z = (cam_pos[2] / step).floor() * step;

                                    // XZ 距離ベースフェード: 各ラインをカメラの XZ 投影点でスプリット
                                    // して「端=0 → スプリット点=peak → 端=0」の2セグメント描画
                                    let fade_xz = |d: f32| -> f32 {
                                        (1.0 - (d / ext).powi(2)).clamp(0.0, 1.0)
                                    };
                                    let split_x = cam_pos[0].clamp(snap_x - ext, snap_x + ext);
                                    let split_z = cam_pos[2].clamp(snap_z - ext, snap_z + ext);

                                    for i in -n_half..=n_half {
                                        // Z 方向ライン（X = world_x で固定）
                                        let world_x  = snap_x + i as f32 * step;
                                        let perp_dx  = (world_x - cam_pos[0]).abs();
                                        if perp_dx < ext {
                                            let idx_x    = (world_x / step).round() as i64;
                                            let is_axis  = idx_x == 0;
                                            let is_major = !is_axis && idx_x % thick_period == 0;
                                            let base_a = if is_axis {
                                                ax_z[3]
                                            } else if is_major {
                                                major[3]
                                            } else {
                                                minor[3] * minor_alpha_base
                                            };
                                            if base_a > 0.005 {
                                                let peak_a = fade_xz(perp_dx) * base_a;
                                                let rgb = if is_axis { [ax_z[0], ax_z[1], ax_z[2]] }
                                                    else if is_major  { [major[0], major[1], major[2]] }
                                                    else               { [minor[0], minor[1], minor[2]] };
                                                lb.add_line_grad(
                                                    [world_x, 0.0, snap_z - ext],
                                                    [world_x, 0.0, split_z],
                                                    [rgb[0], rgb[1], rgb[2], 0.0],
                                                    [rgb[0], rgb[1], rgb[2], peak_a],
                                                );
                                                lb.add_line_grad(
                                                    [world_x, 0.0, split_z],
                                                    [world_x, 0.0, snap_z + ext],
                                                    [rgb[0], rgb[1], rgb[2], peak_a],
                                                    [rgb[0], rgb[1], rgb[2], 0.0],
                                                );
                                            }
                                        }

                                        // X 方向ライン（Z = world_z で固定）
                                        let world_z  = snap_z + i as f32 * step;
                                        let perp_dz  = (world_z - cam_pos[2]).abs();
                                        if perp_dz < ext {
                                            let idx_z    = (world_z / step).round() as i64;
                                            let is_axis  = idx_z == 0;
                                            let is_major = !is_axis && idx_z % thick_period == 0;
                                            let base_a = if is_axis {
                                                ax_x[3]
                                            } else if is_major {
                                                major[3]
                                            } else {
                                                minor[3] * minor_alpha_base
                                            };
                                            if base_a > 0.005 {
                                                let peak_a = fade_xz(perp_dz) * base_a;
                                                let rgb = if is_axis { [ax_x[0], ax_x[1], ax_x[2]] }
                                                    else if is_major  { [major[0], major[1], major[2]] }
                                                    else               { [minor[0], minor[1], minor[2]] };
                                                lb.add_line_grad(
                                                    [snap_x - ext, 0.0, world_z],
                                                    [split_x,      0.0, world_z],
                                                    [rgb[0], rgb[1], rgb[2], 0.0],
                                                    [rgb[0], rgb[1], rgb[2], peak_a],
                                                );
                                                lb.add_line_grad(
                                                    [split_x,      0.0, world_z],
                                                    [snap_x + ext, 0.0, world_z],
                                                    [rgb[0], rgb[1], rgb[2], peak_a],
                                                    [rgb[0], rgb[1], rgb[2], 0.0],
                                                );
                                            }
                                        }
                                    }
                                }

                                Some(lb.build(&draw_ctx.device))
                            } else { None };

                            // スプライト描画リソース収集（render pass 前に GPU バッファを準備する）
                            // CanvasTransform + SpriteComponent を持つアクターを列挙し、
                            // テクスチャをキャッシュから取得または新規ロードして SpritePrepared を生成する。
                            let sprite_prepared = if in_editor && is_canvas {
                                if let Some(scene) = &self.scene {
                                    let wl = self.active_world_line;

                                    // スプライト情報収集（再帰的にアクターツリーを走査）
                                    fn collect_sprite_items(
                                        actors:   &[crate::engine::structs::objects::Actor],
                                        world:    &crate::engine::ecs::World,
                                        wl:       u32,
                                        draw_ctx: &DrawContext,
                                        out:      &mut Vec<(CanvasTransform, f32, f32, [f32; 4], Option<std::sync::Arc<GpuSpriteTexture>>)>,
                                    ) {
                                        for actor in actors {
                                            if actor.world_line != wl { continue; }
                                            let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
                                            if let Some(ct) = ct_opt {
                                                for slot in actor.slots() {
                                                    if slot.kind == ComponentKind::Sprite {
                                                        if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                                                            // テクスチャをキャッシュから取得または新規ロード
                                                            let tex = if sc.texture_path.is_empty() {
                                                                None
                                                            } else {
                                                                let mut cache = draw_ctx.sprite_tex_cache.borrow_mut();
                                                                if !cache.contains_key(&sc.texture_path) {
                                                                    let loaded = load_sprite_texture(
                                                                        &draw_ctx.device,
                                                                        &draw_ctx.queue,
                                                                        &sc.texture_path,
                                                                        &draw_ctx.pipelines.sprite.tex_bgl,
                                                                        &draw_ctx.pipelines.sprite.sampler,
                                                                    );
                                                                    if let Some(t) = loaded {
                                                                        cache.insert(sc.texture_path.clone(), t);
                                                                    }
                                                                }
                                                                cache.get(&sc.texture_path).cloned()
                                                            };
                                                            out.push((ct.clone(), sc.width, sc.height, sc.color, tex));
                                                        }
                                                    }
                                                }
                                            }
                                            collect_sprite_items(&actor.children, world, wl, draw_ctx, out);
                                        }
                                    }

                                    let mut items = Vec::new();
                                    collect_sprite_items(&scene.actors, &scene.world, wl, draw_ctx, &mut items);
                                    prepare_sprites(&draw_ctx.device, &draw_ctx.pipelines.sprite, &items)
                                } else { vec![] }
                            } else { vec![] };

                            // CanvasComponent 矩形アウトラインバッチ（エディタモード + 2D キャンバス世界線のみ）
                            // Canvas のアウトラインは常に表示、Sprite のアウトラインは選択時のみ表示する。
                            let canvas_rect_batch = if in_editor && is_canvas {
                                if let Some(scene) = &self.scene {
                                    let wl = self.active_world_line;
                                    let mut lb = LineBatch::new();
                                    let rect_col: [f32; 4] = [0.85, 0.95, 1.0, 0.9];

                                    fn collect_canvas_rects(
                                        actors:           &[crate::engine::structs::objects::Actor],
                                        world:            &crate::engine::ecs::World,
                                        wl:               u32,
                                        lb:               &mut LineBatch,
                                        col:              [f32; 4],
                                        selected_dfs_ids: &[usize],
                                        counter:          &mut u32,
                                    ) {
                                        for actor in actors {
                                            if actor.world_line != wl { continue; }
                                            // DFS ID を確定し、カウンターを進める
                                            let my_dfs = *counter as usize;
                                            *counter += 1;

                                            // CanvasTransform を clone して borrow を解放してから
                                            // CanvasComponent / SpriteComponent を別途 borrow する（同時 borrow 回避）
                                            let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
                                            if let Some(ct) = ct_opt {
                                                for slot in actor.slots() {
                                                    match slot.kind {
                                                        ComponentKind::Canvas => {
                                                            // CanvasComponent: キャンバス領域のアウトラインを常に描画する
                                                            if let Some(cc) = world.get::<CanvasComponent>(slot.entity) {
                                                                let w = cc.width;
                                                                let h = cc.height;
                                                                let m = ct.to_mat4_sized(w, h);
                                                                let tp = |lx: f32, ly: f32| -> [f32; 3] {
                                                                    [m[0][0]*lx + m[0][1]*ly + m[0][3],
                                                                     m[1][0]*lx + m[1][1]*ly + m[1][3],
                                                                     0.0f32]
                                                                };
                                                                let tl = tp(0.0, 0.0);
                                                                let tr = tp(w,   0.0);
                                                                let br = tp(w,   h  );
                                                                let bl = tp(0.0, h  );
                                                                lb.add_line(tl, tr, col);
                                                                lb.add_line(tr, br, col);
                                                                lb.add_line(br, bl, col);
                                                                lb.add_line(bl, tl, col);
                                                            }
                                                        }
                                                        ComponentKind::Sprite => {
                                                            // SpriteComponent: 選択時のみアウトラインを描画する
                                                            if selected_dfs_ids.contains(&my_dfs) {
                                                                if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                                                                    let w = sc.width;
                                                                    let h = sc.height;
                                                                    let sprite_col: [f32; 4] = [1.0, 0.95, 0.6, 0.85];
                                                                    let m = ct.to_mat4_sized(w, h);
                                                                    let tp = |lx: f32, ly: f32| -> [f32; 3] {
                                                                        [m[0][0]*lx + m[0][1]*ly + m[0][3],
                                                                         m[1][0]*lx + m[1][1]*ly + m[1][3],
                                                                         0.0f32]
                                                                    };
                                                                    let tl = tp(0.0, 0.0);
                                                                    let tr = tp(w,   0.0);
                                                                    let br = tp(w,   h  );
                                                                    let bl = tp(0.0, h  );
                                                                    lb.add_line(tl, tr, sprite_col);
                                                                    lb.add_line(tr, br, sprite_col);
                                                                    lb.add_line(br, bl, sprite_col);
                                                                    lb.add_line(bl, tl, sprite_col);
                                                                }
                                                            }
                                                        }
                                                        _ => {}
                                                    }
                                                }
                                            }
                                            collect_canvas_rects(
                                                &actor.children, world, wl, lb, col,
                                                selected_dfs_ids, counter,
                                            );
                                        }
                                    }

                                    let mut counter: u32 = 0;
                                    collect_canvas_rects(
                                        &scene.actors, &scene.world, wl, &mut lb, rect_col,
                                        &self.selected_actor_dfs_ids, &mut counter,
                                    );
                                    if lb.is_empty() { None } else { Some(lb.build(&draw_ctx.device)) }
                                } else { None }
                            } else { None };

                            // 軸ギズモバッチ（エディタモード + show_axis_gizmo のみ）
                            let axis_gizmo_batch = if in_editor && self.show_axis_gizmo {
                                let sw  = window_size.map_or(1280.0, |s| s.width  as f32);
                                let sh  = window_size.map_or(720.0,  |s| s.height as f32);
                                let rot = self.camera.base.transform.rotation;
                                self.axis_gizmo.as_mut().map(|ag| {
                                    ag.build(rot, sw, sh, &draw_ctx.device, &draw_ctx.queue)
                                })
                            } else { None };

                            // アイコンオーバーレイバッチ（エディタモードのみ）
                            let icon_overlay_batch = if in_editor {
                                let vp_w = window_size.map_or(1280.0, |s| s.width  as f32);
                                let vp_h = window_size.map_or(720.0,  |s| s.height as f32);
                                // 2D キャンバスモードと 3D モードでビュー/プロジェクション行列を切り替える
                                // （GPU側の描画と同じ行列を使わないとアイコン座標がずれる）
                                let (view, proj) = if is_canvas {
                                    let cam_2d = self.canvas_cameras
                                        .entry(self.active_world_line)
                                        .or_insert_with(CanvasCameraData::default);
                                    let half_h = cam_2d.ortho_half_h;
                                    let half_w = half_h * (vp_w / vp_h);
                                    let eye    = Vector3::new(cam_2d.pan_x, cam_2d.pan_y, -100.0);
                                    let center = Vector3::new(cam_2d.pan_x, cam_2d.pan_y, 0.0);
                                    let up     = Vector3::new(0.0, 1.0, 0.0);
                                    let v = Mat4x4::look_at_lh(eye, center, up);
                                    let p = Mat4x4::orthographic_lh(-half_w, half_w, half_h, -half_h, 0.0, 200.0);
                                    (v, p)
                                } else {
                                    (self.camera.view_matrix(), self.camera.projection_matrix())
                                };
                                let positions: Vec<(f32, f32)> = if !self.selected_instances.is_empty() {
                                    selected_mc
                                        .map(|mc| {
                                            self.selected_instances.iter()
                                                .filter_map(|&i| {
                                                    let mat = mc.instance_mats.get(i as usize)?;
                                                    let world = [mat[0][3], mat[1][3], mat[2][3]];
                                                    world_to_screen(world, &view.data, &proj.data, vp_w, vp_h)
                                                })
                                                .collect()
                                        })
                                        .unwrap_or_default()
                                } else if self.actor_virtual_selected_idx.is_some() && self.active_world_line != 0 {
                                    let pos = actor_virtual_pos.unwrap_or([0.0, 0.0, 0.0]);
                                    world_to_screen(pos, &view.data, &proj.data, vp_w, vp_h)
                                        .map(|p| vec![p])
                                        .unwrap_or_default()
                                } else {
                                    Vec::new()
                                };
                                crate::engine::core::font::icon_overlay::IconOverlay::build(
                                    &positions, vp_w, vp_h, &draw_ctx.device,
                                )
                            } else { None };

                            // ── メインレンダーパス ────────────────
                            {
                                // アクター編集モード: 紺色背景、通常: ダークグレー
                                let clear_color = if self.active_world_line != 0 {
                                    wgpu::Color { r: 0.05, g: 0.08, b: 0.18, a: 1.0 }
                                } else {
                                    wgpu::Color { r: 0.1,  g: 0.1,  b: 0.1,  a: 1.0 }
                                };
                                let mut pass = frame.begin_render_pass(clear_color);
                                // 全 MC を描画（子アクターの MC も含む）
                                for &(_, _, _, amc) in &all_mcs {
                                    if let Some((gpu, batch)) = amc.rendering_refs() {
                                        draw_model_indirect(
                                            &mut pass, gpu, batch,
                                            &camera_buf.bind_group, &draw_ctx.pipelines,
                                        );
                                    }
                                }
                                // アウトライン: 全選択アクター（マルチ選択対応）
                                if in_editor {
                                    if !self.selected_actor_dfs_ids.is_empty() {
                                        // Phase 1: 全選択アクターのステンシルマスクを書き込む
                                        for &dfs_id in &self.selected_actor_dfs_ids {
                                            if let Some(&(_, _, _, mc)) = all_mcs.iter()
                                                .find(|&&(_, d, si, _)| d == dfs_id as u32 && si == 0)
                                            {
                                                if let Some((gpu, batch)) = mc.rendering_refs() {
                                                    draw_stencil_mask_multi(
                                                        &mut pass, gpu, batch,
                                                        &camera_buf.bind_group, &draw_ctx.pipelines,
                                                        &[0],
                                                    );
                                                }
                                            }
                                        }
                                        // Phase 2: 全選択アクターのアウトラインを描画
                                        for &dfs_id in &self.selected_actor_dfs_ids {
                                            if let Some(&(_, _, _, mc)) = all_mcs.iter()
                                                .find(|&&(_, d, si, _)| d == dfs_id as u32 && si == 0)
                                            {
                                                if let Some((gpu, batch)) = mc.rendering_refs() {
                                                    draw_outline_multi(
                                                        &mut pass, gpu, batch,
                                                        &camera_buf.bind_group, &draw_ctx.pipelines,
                                                        &[0],
                                                    );
                                                }
                                            }
                                        }
                                    } else if !self.selected_instances.is_empty() {
                                        // レガシー: インスタンス直接選択（後方互換）
                                        if let Some(sel_mc) = selected_mc {
                                            if let Some((gpu, batch)) = sel_mc.rendering_refs() {
                                                draw_stencil_mask_multi(
                                                    &mut pass, gpu, batch,
                                                    &camera_buf.bind_group, &draw_ctx.pipelines,
                                                    &self.selected_instances,
                                                );
                                                draw_outline_multi(
                                                    &mut pass, gpu, batch,
                                                    &camera_buf.bind_group, &draw_ctx.pipelines,
                                                    &self.selected_instances,
                                                );
                                            }
                                        }
                                    }
                                }

                                // 矩形選択ビジュアル
                                if let (Some(rect_batch), Some((_, line_bg))) =
                                    (&rect_gpu_batch, &self.line_model_buf)
                                {
                                    draw_line_batch(
                                        &mut pass, rect_batch,
                                        &camera_buf.bind_group, line_bg,
                                        &draw_ctx.pipelines,
                                    );
                                }

                                // ドロッププレビュー球体描画（ドラッグ中のみ）
                                if let (Some(preview_batch), Some((_, line_bg))) =
                                    (&drop_preview_batch, &self.line_model_buf)
                                {
                                    draw_gizmo_batch(
                                        &mut pass, preview_batch,
                                        &camera_buf.bind_group, line_bg,
                                        &draw_ctx.pipelines,
                                    );
                                }

                                // グリッド描画（最背面：ギズモ・アイコンより奥）
                                if let (Some(grid_batch), Some((_, line_bg))) =
                                    (&grid_gpu_batch, &self.line_model_buf)
                                {
                                    draw_line_batch(
                                        &mut pass, grid_batch,
                                        &camera_buf.bind_group, line_bg,
                                        &draw_ctx.pipelines,
                                    );
                                }

                                // スプライト画像描画（Canvas アウトラインより前に描画してアウトラインを前面に）
                                if !sprite_prepared.is_empty() {
                                    draw_sprites(
                                        &mut pass,
                                        &draw_ctx.pipelines.sprite,
                                        &camera_buf.bind_group,
                                        &sprite_prepared,
                                    );
                                }

                                // CanvasComponent 矩形アウトライン描画（2D キャンバスモードのみ）
                                // Canvas: 常に表示, Sprite: 選択時のみ表示
                                if let (Some(rect_batch), Some((_, line_bg))) =
                                    (&canvas_rect_batch, &self.line_model_buf)
                                {
                                    draw_line_batch(
                                        &mut pass, rect_batch,
                                        &camera_buf.bind_group, line_bg,
                                        &draw_ctx.pipelines,
                                    );
                                }

                                // ギズモ（グリッドより前面、アイコンより背面）
                                let show_gizmo = in_editor && self.tool_mode != ToolMode::Select;
                                if show_gizmo {
                                    if let (Some(gpu_batch), Some((_, line_bg))) =
                                        (&gizmo_gpu_batch, &self.line_model_buf)
                                    {
                                        draw_gizmo_batch(
                                            &mut pass, gpu_batch,
                                            &camera_buf.bind_group, line_bg,
                                            &draw_ctx.pipelines,
                                        );
                                    }
                                }

                                // 軸ギズモ（エディタモードのみ）
                                if let (Some(batch), Some(ag)) =
                                    (&axis_gizmo_batch, &self.axis_gizmo)
                                {
                                    ag.draw(batch, &mut pass);
                                }

                                // アイコンオーバーレイ（最前面：選択アクター位置マーカー）
                                if let (Some(batch), Some(io)) =
                                    (&icon_overlay_batch, &self.icon_overlay)
                                {
                                    io.draw(batch, &mut pass);
                                }
                            }

                            // ── ID パス（Edit/Pause のみ）──────────
                            if in_editor {
                                if let Some(id_buf) = &self.id_buffer {
                                    {
                                        // BindGroup は RenderPass より長く生きる必要があるので先に生成する
                                        let id_base_bgs: Vec<Option<(wgpu::Buffer, wgpu::BindGroup)>> =
                                            all_mcs.iter()
                                                .map(|&(base, _, _, amc)| {
                                                    if amc.rendering_refs().is_some() {
                                                        Some(draw_ctx.create_id_base_bg(base))
                                                    } else {
                                                        None
                                                    }
                                                })
                                                .collect();

                                        let mut id_pass = frame.begin_id_pass(&id_buf.view);
                                        for (&(_, _, _, amc), bg_opt) in all_mcs.iter().zip(id_base_bgs.iter()) {
                                            if let (Some((gpu, batch)), Some((_, id_base_bg))) =
                                                (amc.rendering_refs(), bg_opt.as_ref())
                                            {
                                                draw_id_pass(
                                                    &mut id_pass, gpu, batch,
                                                    &camera_buf.bind_group, &draw_ctx.pipelines,
                                                    id_base_bg,
                                                );
                                            }
                                        }
                                    }
                                    // readback 優先度: drop > pick
                                    let drop_pos = self.pending_drop
                                        .as_ref()
                                        .map(|&(_, sx, sy)| (sx, sy));
                                    let readback_pos = drop_pos.or(pick_pos);
                                    if let Some((px, py)) = readback_pos {
                                        let px = px.min(id_buf.width.saturating_sub(1));
                                        let py = py.min(id_buf.height.saturating_sub(1));
                                        frame.schedule_id_copy(
                                            &id_buf.texture, px, py, &id_buf.readback_buf,
                                        );
                                        // readback が drop ではなく pick のためかを記録するフラグ
                                        did_pick = drop_pos.is_none() && pick_pos.is_some();
                                    }
                                }
                            }

                            frame.finish();

                            // 実際にポリゴンが描画された最初のフレームでエディタへ通知する
                            // （デバッグビルド・埋め込みモード限定）。
                            #[cfg(debug_assertions)]
                            if !self.first_frame_sent && self.parent_hwnd.is_some() {
                                self.first_frame_sent = true;
                                if let Some(ipc) = &self.ipc {
                                    ipc.send("FIRST_FRAME");
                                }
                            }
                        }
                        Err(wgpu::SurfaceError::Lost) => {
                            if let Some(size) = window_size {
                                renderer.resize(size);
                            }
                        }
                        Err(wgpu::SurfaceError::OutOfMemory) => event_loop.exit(),
                        Err(e) => eprintln!("Render error: {e:?}"),
                    }
                }

                // ── ピック結果の読み出し（GPU サブミット後）─────
                if did_pick {
                    if let (Some(id_buf), Some(draw_ctx)) = (&self.id_buffer, &self.draw_ctx) {
                        let (_world_pos, raw) = id_buf.read_pixel(&draw_ctx.device);
                        // 選択変更前の状態を保存する（Undo 記録用）
                        let before_inst       = self.selected_instances.clone();
                        let before_dfs_ids    = self.selected_actor_dfs_ids.clone();
                        let before_primary    = self.actor_virtual_selected_idx;

                        if raw == 0 {
                            // 空クリック: 選択解除（Ctrl 押下時は何もしない）
                            if !self.ctrl_at_press {
                                self.actor_virtual_selected_idx      = None;
                                self.actor_virtual_selected_slot_idx = 0;
                                self.selected_actor_dfs_ids.clear();
                                self.selected_instances.clear();
                            }
                        } else if !wl_mc_pick_infos.is_empty() {
                            // global ID から所有 MC を特定し、アクターと MC スロットを仮想選択として設定する
                            let global = raw - 1; // global instance ID (0-based)
                            if let Some(&(base, dfs_id, slot_i, _)) = wl_mc_pick_infos.iter()
                                .find(|&&(base, _, _, count)| global >= base && (global - base) < count as u32)
                            {
                                let dfs_usize = dfs_id as usize;
                                let local_idx = global - base;
                                self.actor_virtual_selected_slot_idx = slot_i;
                                if self.ctrl_at_press {
                                    // Ctrl+クリック: アクターをマルチ選択リストでトグルする
                                    if self.selected_actor_dfs_ids.contains(&dfs_usize) {
                                        self.selected_actor_dfs_ids.retain(|&x| x != dfs_usize);
                                        if self.actor_virtual_selected_idx == Some(dfs_usize) {
                                            self.actor_virtual_selected_idx = self.selected_actor_dfs_ids.last().copied();
                                        }
                                    } else {
                                        self.selected_actor_dfs_ids.push(dfs_usize);
                                        self.actor_virtual_selected_idx = Some(dfs_usize);
                                    }
                                    self.selected_instances = vec![local_idx];
                                } else {
                                    // 通常クリック: 単一選択
                                    self.actor_virtual_selected_idx = Some(dfs_usize);
                                    self.selected_actor_dfs_ids     = vec![dfs_usize];
                                    self.selected_instances          = vec![local_idx];
                                }
                                self.send_actor_components(dfs_id, slot_i);
                            }
                        }

                        // MC インスタンス選択の Undo 記録
                        let after_inst = self.selected_instances.clone();
                        if before_inst != after_inst {
                            self.undo_history.record(Box::new(SelectionCommand {
                                before: before_inst,
                                after:  after_inst,
                            }));
                        }
                        // アクター DFS 選択の Undo 記録
                        let after_dfs_ids = self.selected_actor_dfs_ids.clone();
                        let after_primary = self.actor_virtual_selected_idx;
                        if before_dfs_ids != after_dfs_ids || before_primary != after_primary {
                            self.undo_history.record(Box::new(ActorDfsSelectionCommand {
                                before_dfs_ids,
                                after_dfs_ids,
                                before_primary,
                                after_primary,
                            }));
                        }
                        eprintln!("[SEED] pick: raw={raw}, selected={:?}", self.selected_instances);
                        self.send_selected();
                    }
                }

                // ── ドロップ処理（GPU サブミット後）─────────────
                if let Some((path, sx, sy)) = self.pending_drop.take() {
                    eprintln!("[Drop] Processing pending_drop pos=({sx},{sy}) did_pick={did_pick}");
                    let world_pos = if !did_pick {
                        if let (Some(id_buf), Some(draw_ctx)) = (&self.id_buffer, &self.draw_ctx) {
                            let (wpos, raw_id) = id_buf.read_pixel(&draw_ctx.device);
                            eprintln!("[Drop] read_pixel => world_pos={wpos:?} raw_id={raw_id}");
                            wpos
                        } else { None }
                    } else {
                        // ピック処理でバッファが読み出し済みのため別途取得できない。
                        // pending_drop を再キューイングして次フレームで処理する。
                        eprintln!("[Drop] did_pick=true, re-queuing for next frame");
                        self.pending_drop = Some((path.clone(), sx, sy));
                        None
                    };

                    // world_pos が取れた場合はその場所に、なければカーソルレイ方向へ DEFAULT_DIST 進んだ位置に配置する
                    const DEFAULT_DIST: f32 = 10.0;
                    let spawn_pos = world_pos.unwrap_or_else(|| {
                        let cam_v = self.camera.position();
                        let cam   = [cam_v.x, cam_v.y, cam_v.z];
                        if let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) {
                            let view = self.camera.view_matrix();
                            let proj = self.camera.projection_matrix();
                            let (_ro, rd) = screen_to_ray(
                                sx as f32, sy as f32,
                                ws.width as f32, ws.height as f32,
                                &view.data, &proj.data, cam,
                            );
                            [
                                cam[0] + rd[0] * DEFAULT_DIST,
                                cam[1] + rd[1] * DEFAULT_DIST,
                                cam[2] + rd[2] * DEFAULT_DIST,
                            ]
                        } else {
                            // フォールバック: カメラ前方
                            let yaw   = self.camera.yaw.to_radians();
                            let pitch = self.camera.pitch.to_radians();
                            [
                                cam[0] + yaw.sin() * pitch.cos() * DEFAULT_DIST,
                                cam[1] + pitch.sin()              * DEFAULT_DIST,
                                cam[2] + yaw.cos() * pitch.cos() * DEFAULT_DIST,
                            ]
                        }
                    });

                    if world_pos.is_some() || !did_pick {
                        self.handle_drop_actor(&path, spawn_pos);
                    }
                }

                // ─ 7. EndFrame（Play 時のみ）─────────────────
                if time_running {
                    if let Some(scene) = &mut self.scene { scene.end_frame(&ctx); }
                }

                self.input.end_frame();
                self.cam_input.end_frame();

                if let Some(window) = &self.window { window.request_redraw(); }
            }

            _ => {}
        }
    }

    /// デバイスイベントを処理する（マウス移動 → カメラ入力）。
    fn device_event(
        &mut self,
        _event_loop: &ActiveEventLoop,
        _device_id: DeviceId,
        event: DeviceEvent,
    ) {
        if let DeviceEvent::MouseMotion { delta: (dx, dy) } = event {
            self.input.process_mouse_motion(dx, dy);
            self.cam_input.mouse_dx += dx as f32;
            self.cam_input.mouse_dy += dy as f32;
        }
    }
}
