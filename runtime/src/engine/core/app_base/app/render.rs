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
    draw_model_indirect, draw_id_pass, draw_canvas_id_items, prepare_canvas_id_bg,
    draw_outline_multi, draw_stencil_mask_multi,
    extract_frustum_planes, IdBuffer, GizmoBatch, draw_gizmo_batch,
    LineBatch, draw_line_batch,
    load_sprite_texture, prepare_sprites_from_mats, draw_sprites, GpuSpriteTexture,
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
    canvas_anchor_offset_for_dfs,
    world_to_screen,
    camera_grab_start, camera_grab_end,
    apply_window_clamp, release_window_clamp,
    camera_scene_gizmo,
    CameraPreviewResources,
    CameraGizmoResources,
};

/// キャンバス座標（ピクセル）→ 3D ワールド座標の変換スケール係数。
/// ワールドスペースモード時に CanvasTransform 座標をワールド空間へスケールするために使う。
/// 例: position=100px → 1.0 ワールドユニット
const CANVAS_WORLD_SCALE: f32 = 1.0 / 100.0;

/// カメラプレビューのテクスチャ幅（ピクセル）。
const CAMERA_PREVIEW_W: u32 = 320;
/// カメラプレビューのテクスチャ高さ（ピクセル）。
const CAMERA_PREVIEW_H: u32 = 180;

impl App {
    /// アセットルートを解決して asset_fs を初期化する。
    ///
    /// - `self.assets_root` が指定されている場合はそれを使う
    /// - 未指定の場合は実行ファイルの隣にある assets/ フォルダを使う
    /// - assets.pak が実行ファイルの隣にあれば PAK モードで初期化する
    fn init_asset_fs(&self) {
        use crate::engine::asset_fs;
        use std::path::PathBuf;

        // アセットルートを決定する
        let assets_root: PathBuf = if let Some(root) = &self.assets_root {
            PathBuf::from(root)
        } else {
            // 実行ファイルの隣の assets/ ディレクトリを使う
            std::env::current_exe()
                .ok()
                .and_then(|p| p.parent().map(|d| d.join("assets")))
                .unwrap_or_else(|| PathBuf::from("assets"))
        };

        // 実行ファイルの隣に assets.pak があれば PAK モードにする
        let pak_path = assets_root.parent()
            .map(|dir| dir.join("assets.pak"))
            .filter(|p| p.exists());

        asset_fs::init(assets_root, pak_path.as_deref());
    }

    /// Playモードでシーンをロードする。
    ///
    /// - `self.scene_path` が指定されている場合: そのシーンを読む（エディタ「現在のシーンでプレイ」）
    /// - 未指定の場合: `assets://project_settings.json` の `start_scene` を読む
    ///
    /// ロードしたシーンの debug_camera データをデバッグカメラの初期位置に適用する。
    /// メインカメラが存在しない場合のフォールバックとして機能する。
    fn load_play_scene(&mut self) {
        use crate::engine::asset_fs;

        // ロードするシーンパスを決定する
        let scene_path_str: String = if let Some(path) = &self.scene_path {
            // エディタから --scene= で指定されたパス
            path.clone()
        } else {
            // project_settings.json の start_scene を読む
            let json = match asset_fs::read_string("assets://project_settings.json") {
                Ok(s) => s,
                Err(_) => return,
            };
            match serde_json::from_str::<serde_json::Value>(&json) {
                Ok(v) => {
                    let s = v["start_scene"].as_str().unwrap_or("").to_string();
                    if s.is_empty() { return; }
                    s
                }
                Err(_) => return,
            }
        };

        // PAK モードで resolve するとファイルシステム読みになるため、
        // 仮想パス (assets://...) のまま Scene::load に渡す。
        let scene_path = std::path::Path::new(&scene_path_str);

        let result = if let Some(ctx) = &self.draw_ctx {
            Some(crate::engine::core::app_base::scene::Scene::load(
                scene_path,
                ctx,
                self.scripting_host.as_ref(),
            ))
        } else {
            None
        };

        match result {
            Some(Ok((new_scene, cam_data))) => {
                // シーンに保存されたデバッグカメラ位置を適用する。
                // メインカメラが存在しない場合のフォールバックとして有効な視点から起動できる。
                if let Some(cam) = cam_data {
                    self.apply_camera_data(&cam);
                }
                self.scene = Some(new_scene);
            }
            Some(Err(_)) => {}
            None => {}
        }
    }
}


impl ApplicationHandler for App {
    /// ウィンドウ・レンダラーを初期化し、IPC へ READY を通知する。
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        let window = Arc::new(create_window(event_loop, &WindowConfig {
            parent_hwnd: self.parent_hwnd,
            ..WindowConfig::default()
        }));

        // スタンドアロンモードでは GPU 初期化前にウィンドウを即表示する。
        // Renderer::new() (wgpu DX12 デバイス作成) は数秒かかることがあり、
        // 初期化完了まで画面に何も出ない問題を防ぐ。
        // 埋め込みモードはエディタ側コンテナが背景を描くため従来どおり後で表示。
        if !self.is_embedded() {
            window.set_visible(true);
        }

        let mut renderer = Renderer::new(window.clone());

        let size = window.inner_size();
        self.camera.set_aspect_ratio(size.width, size.height);

        self.camera.base.transform.position = Vector3::new(0.0, 2.0, -10.0);

        let ctx = DrawContext::new(
            renderer.device(),
            renderer.queue(),
            renderer.surface_format(),
            renderer.depth_format(),
            renderer.pipeline_cache(),
        );

        let scene = crate::engine::core::app_base::scene::Scene::new("Untitled");
        let camera_buf = ctx.create_camera_buffer();
        let id_buffer  = IdBuffer::new(&ctx.device, size.width, size.height);
        let line_model_buf = ctx.create_identity_model_bg_for_unlit();

        if self.is_embedded() {
            // 非表示ウィンドウへの request_redraw は WM_PAINT が配送されず
            // RedrawRequested が発火しないため、常に可視化してから redraw を要求する。
            // 起動中の白フラッシュはエディタ側コンテナの WM_ERASEBKGND 黒塗りで対処する。
            window.set_visible(true);
            window.request_redraw();
        } else {
            // スタンドアロンモードは resumed() 冒頭で set_visible 済み。
            window.set_visible(true); // 冪等（すでに表示中）
        }

        let canvas_overlay_camera_buf = ctx.create_camera_buffer();

        self.draw_ctx      = Some(ctx);
        self.scene         = Some(scene);
        self.camera_buf    = Some(camera_buf);
        self.canvas_overlay_camera_buf = Some(canvas_overlay_camera_buf);
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

        // asset_fs を初期化する（全モード共通）
        self.init_asset_fs();

        let hwnd = self.window_hwnd();
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("READY:{hwnd}"));
        }
        self.send_hierarchy();

        // Playモードでは指定シーン（または start_scene）を自動ロードする
        if self.mode == RuntimeMode::Play {
            self.load_play_scene();
        }
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
                // 埋め込みモードでは Vulkan の currentExtent が親コンテナの
                // 可視領域サイズに固定される。SetWindowLong(WS_CHILD) が生成する
                // 中間的な 816x639 の WM_SIZE を使うと depth と color の不一致が
                // 起こるため、親がいる場合は GetClientRect(parent) を優先する。
                let effective_size = self.get_parent_client_size().unwrap_or(size);
                if let Some(r) = &mut self.renderer { r.resize(effective_size); }
                self.camera.set_aspect_ratio(effective_size.width, effective_size.height);
                if effective_size.width > 0 && effective_size.height > 0 {
                    if let Some(dc) = &self.draw_ctx {
                        self.id_buffer = Some(IdBuffer::new(&dc.device, effective_size.width, effective_size.height));
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
                            if let Some((cx, cy)) = self.last_cursor_pos {
                                if self.actor_edit_canvas_wls.contains(&self.active_world_line) {
                                    // 2D アクター編集タブ: GPU ID パス不要、CPU OBB ピックを即時実行する
                                    self.pick_2d_canvas(cx, cy);
                                } else {
                                    // クリック: GPU ID ピックをスケジュール
                                    self.pending_pick = Some((cx as u32, cy as u32));
                                }
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

                                        // カメラギズモ: アイコン位置をスクリーン投影して矩形内判定する
                                        let cam_gizmo_mats_rect =
                                            camera_scene_gizmo::collect_camera_actor_matrices(
                                                &scene.actors, &scene.world, wl,
                                            );
                                        for (dfs_id, icon_mat) in cam_gizmo_mats_rect {
                                            // アイコン行列の平行移動成分がアクターのワールド位置
                                            let world_pos = [icon_mat[0][3], icon_mat[1][3], icon_mat[2][3]];
                                            let in_rect = world_to_screen(
                                                world_pos, &view.data, &proj.data, vp_w, vp_h,
                                            )
                                            .map(|(sx, sy)| sx >= sx_min && sx <= sx_max && sy >= sy_min && sy <= sy_max)
                                            .unwrap_or(false);
                                            if in_rect && !rect_dfs.contains(&dfs_id) {
                                                rect_dfs.push(dfs_id);
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
                        let in_editor_drag = self.mode == RuntimeMode::Edit || self.paused;
                        let use_ss_drag = self.canvas_screen_space_overlay || !in_editor_drag
                            || self.actor_edit_canvas_wls.contains(&wl_drag);
                        let (ro, rd) = if self.canvas_world_lines.contains(&wl_drag) && use_ss_drag {
                            // スクリーンスペース: 2D ortho レイ
                            let cam_2d = self.canvas_cameras.get(&wl_drag);
                            let pan_x  = cam_2d.map(|c| c.pan_x).unwrap_or(0.0);
                            let pan_y  = cam_2d.map(|c| c.pan_y).unwrap_or(0.0);
                            let half_h = cam_2d.map(|c| c.ortho_half_h).unwrap_or(10.0);
                            let half_w = half_h * (vp_w / vp_h);
                            screen_to_ray_ortho(cx, cy, vp_w, vp_h, pan_x, pan_y, half_w, half_h)
                        } else {
                            // 3D perspective（通常 3D またはワールドスペースキャンバス）
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
                                            // new_mat の平行移動成分はアンカーオフセット込みのgizmo位置を基点とするため、
                                            // CanvasTransform.position（アンカーオフセットなし）に戻すためにオフセットを引く。
                                            let anchor_off = canvas_anchor_offset_for_dfs(
                                                &scene.actors, &scene.world, wl, drag_dfs,
                                            );
                                            if let Some(ct) = scene.world.get_mut::<CanvasTransform>(entity) {
                                                // centroid_mat は単位回転・スケール+平行移動のみなので
                                                // new_mat はツールモードに応じた変化のみを持つ。
                                                // モードごとに変化する成分だけを更新し、他はドラッグ開始値を維持する。
                                                let in_editor_c = self.mode == RuntimeMode::Edit || self.paused;
                                                let use_ss_c = self.canvas_screen_space_overlay || !in_editor_c
                                                    || self.actor_edit_canvas_wls.contains(&wl);
                                                // ワールドスペースでは平行移動をキャンバスピクセルに変換し、
                                                // Y 軸を再反転（レンダリング時に反転済みのため元に戻す）
                                                let pos_inv_scale = if use_ss_c { 1.0 } else { 1.0 / CANVAS_WORLD_SCALE };
                                                let y_inv_sign = if use_ss_c { 1.0f32 } else { -1.0 };
                                                ct.position[0] = new_mat[0][3] * pos_inv_scale - anchor_off[0];
                                                ct.position[1] = new_mat[1][3] * pos_inv_scale * y_inv_sign - anchor_off[1];
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
                                                        // new_mat の各列の長さ = centroid 起点のスケール係数（無次元）
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
                // スクリーンスペースモード:
                //   - チェックボックス ON: スクリーンスペース
                //   - プレイ中: 常にスクリーンスペース
                //   - アクター編集タブの 2D 世界線: 常にスクリーンスペース（編集パネルは従来通り）
                let use_screen_space = self.canvas_screen_space_overlay || !in_editor
                    || self.actor_edit_canvas_wls.contains(&self.active_world_line);

                // アクター編集タブの 2D キャンバスのみ 2D オルソカメラを使用する。
                // シーン上のキャンバスは screenSpace チェック ON でも 3D カメラを維持する。
                let is_actor_edit_2d = self.actor_edit_canvas_wls.contains(&self.active_world_line);
                // シーンのスクリーンスペースキャンバス: 3D メインカメラ + 2D オーバーレイ合成。
                // アクター編集タブは camera_buf 自体が 2D なのでオーバーレイ不要。
                let scene_canvas_ss = is_canvas && use_screen_space && !is_actor_edit_2d;

                if in_editor {
                    if is_actor_edit_2d {
                        // 2D アクター編集タブ: RMB ドラッグで XY パン、スクロールでズーム。
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
                        // 3D モード（ワールドスペースキャンバス含む）: 通常のデバッグカメラ更新
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
                // Outdated ハンドラ用: &mut self.renderer を借用するブロック内では
                // self メソッドが呼べないため、HWND を事前にコピーしておく。
                // Outdated 発生時（SetParent 後）に GetParent(my_hwnd) で
                // 正確なコンテナサイズを取得する。
                let my_hwnd = self.window_hwnd();
                let queue = self.draw_ctx.as_ref().map(|c| c.queue.clone());

                if let (Some(scene), Some(camera_buf), Some(queue)) =
                    (&mut self.scene, &self.camera_buf, queue)
                {
                    // カメラ選択:
                    //   - 2D アクター編集タブ → 2D オルソカメラ
                    //   - Play モード         → シーン内 is_main=true の CameraComponent
                    //                           （見つからなければデバッグカメラにフォールバック）
                    //   - Edit モード         → デバッグカメラ
                    let (view, proj, cam_pos_arr) = if is_actor_edit_2d {
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
                        // Y-down: bottom=+half_h, top=-half_h でワールド Y 正方向 = スクリーン下
                        let p = Mat4x4::orthographic_lh(-half_w, half_w, half_h, -half_h, 0.0, 200.0);
                        (v, p, [cam_2d.pan_x, cam_2d.pan_y, -100.0])
                    } else if self.mode == RuntimeMode::Play && !self.paused {
                        // Play モード（非ポーズ時）: is_main=true の CameraComponent を探す
                        let aspect = window_size.map_or(16.0 / 9.0, |s| {
                            s.width as f32 / s.height as f32
                        });
                        let game_cam = scene.find_main_camera().map(|(tf, cd)| {
                            let [px, py, pz] = tf.position;
                            let [fx, fy, fz] = tf.forward();
                            let [ux, uy, uz] = tf.up();
                            let pos    = Vector3::new(px, py, pz);
                            let target = pos + Vector3::new(fx, fy, fz);
                            let up_vec = Vector3::new(ux, uy, uz);
                            let v = Mat4x4::look_at_lh(pos, target, up_vec);
                            let p = Mat4x4::perspective_lh(
                                cd.fov_y_deg.to_radians(), aspect, cd.near, cd.far,
                            );
                            (v, p, [px, py, pz])
                        });
                        // メインカメラが未配置の場合はデバッグカメラにフォールバック
                        game_cam.unwrap_or_else(|| {
                            let v  = self.camera.view_matrix();
                            let p  = self.camera.projection_matrix();
                            let cp = self.camera.position();
                            (v, p, [cp.x, cp.y, cp.z])
                        })
                    } else {
                        // Edit モード: デバッグカメラ
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

                    // シーンスクリーンスペース専用: 2D オルソオーバーレイカメラを更新する。
                    // 3D メインカメラの上に 2D キャンバス要素を重ねて描画するために使う。
                    // アクター編集タブは camera_buf 自体が 2D なのでここでは更新しない。
                    if scene_canvas_ss {
                        if let Some(canvas_cam_buf) = &self.canvas_overlay_camera_buf {
                            let (vp_w, vp_h) = window_size.map_or(
                                (1280.0f32, 720.0f32),
                                |s| (s.width as f32, s.height as f32),
                            );
                            let half_h = vp_h / 2.0;
                            let half_w = vp_w / 2.0;
                            let eye_c    = Vector3::new(0.0, 0.0, -100.0);
                            let center_c = Vector3::new(0.0, 0.0, 0.0);
                            let up_c     = Vector3::new(0.0, 1.0, 0.0);
                            let cv  = Mat4x4::look_at_lh(eye_c, center_c, up_c);
                            let cp2 = Mat4x4::orthographic_lh(-half_w, half_w, half_h, -half_h, 0.0, 200.0);
                            let cvp = cp2 * cv;
                            canvas_cam_buf.update(&queue, &CameraUniform {
                                view_proj:  cvp.transpose().data,
                                view:       cv.transpose().data,
                                position:   [0.0, 0.0, -100.0],
                                _pad:       0.0,
                                resolution: [vp_w, vp_h],
                                _pad2:      [0.0; 2],
                            });
                        }
                    }

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
                // 2D キャンバスモードの場合、アクターの配置位置は CanvasTransform で固定のため
                // 3D プレビュー球体は表示しない。3D モードのみレイキャストで位置を算出する。
                if let Some((hsx, hsy)) = self.pending_drop_hover.take() {
                    if !is_canvas {
                        // 3D モード: GPU リードバックを使わずレイキャストでワールド座標を算出する。
                        // y=0 平面との交差を優先し、カメラが平行または後方なら DEFAULT_DIST 先。
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
                    // 2D キャンバスモード: pending_drop_hover は消費されるが drop_preview_pos は更新しない
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
                // MC インスタンスの総数（キャンバス ID オフセット計算用）
                // 全 MC の (base + count) の最大値 = 割り当て済み ID の上限
                let mc_total_instances: u32 = wl_mc_pick_infos.iter()
                    .map(|&(base, _, _, count)| base + count as u32)
                    .max()
                    .unwrap_or(0);

                // カメラギズモの (DFS ID, アイコン行列) リスト（3D 編集モードのみ）。
                // ピック情報として mc_total_instances の直後の ID 範囲に割り当てる。
                let cam_gizmo_actor_mats: Vec<(usize, [[f32; 4]; 4])> = if in_editor && !is_canvas {
                    if let Some(scene) = &self.scene {
                        camera_scene_gizmo::collect_camera_actor_matrices(
                            &scene.actors, &scene.world, self.active_world_line,
                        )
                    } else { vec![] }
                } else { vec![] };
                let camera_gizmo_count: u32 = cam_gizmo_actor_mats.len() as u32;
                // キャンバス ID のベースオフセット（MC + カメラギズモの後）
                let canvas_id_offset: u32 = mc_total_instances + camera_gizmo_count;

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

                            // ── カメラシーンギズモ（Edit モード・3D シーンのみ）──────────
                            // カメラアイコン / フラスタム / プレビューは 3D シーン (world_line=0) でのみ表示する。
                            let is_3d_scene = in_editor && !is_canvas;
                            let aspect = window_size.map_or(16.0_f32 / 9.0, |s| {
                                s.width as f32 / s.height as f32
                            });
                            let (vp_w_f, vp_h_f) = window_size.map_or(
                                (1280.0_f32, 720.0_f32),
                                |s| (s.width as f32, s.height as f32),
                            );

                            // カメラギズモモデルのレイジーロードと InstancedModelBatch 更新
                            // assets_root が設定されている場合のみ実行する
                            if is_3d_scene && !cam_gizmo_actor_mats.is_empty() {
                                if let Some(ar) = self.editor_resources.clone() {
                                    let model_path = format!("{}/models/camera.glb", ar);
                                    // CPU モデルをキャッシュから取得するか、なければロードする
                                    let cpu_model_opt: Option<std::sync::Arc<crate::engine::core::loader::model::Model>> = {
                                        let mut cache = draw_ctx.model_cache.borrow_mut();
                                        if !cache.contains_key(&model_path) {
                                            let path = std::path::Path::new(&model_path);
                                            match crate::engine::core::loader::load_model(path) {
                                                Ok(m)  => { cache.insert(model_path.clone(), std::sync::Arc::new(m)); }
                                                Err(e) => { eprintln!("[SEED] camera gizmo model load failed: {e}"); }
                                            }
                                        }
                                        cache.get(&model_path).cloned()
                                    };
                                    if let Some(cpu) = cpu_model_opt {
                                        // バッチ容量が不足している場合は再生成する
                                        let need_reinit = self.camera_gizmo.as_ref()
                                            .map(|g| g.capacity < cam_gizmo_actor_mats.len())
                                            .unwrap_or(true);
                                        if need_reinit {
                                            let cap = (cam_gizmo_actor_mats.len() * 2).max(4);
                                            let gpu_model = draw_ctx.upload_model(&cpu);
                                            let batch     = draw_ctx.create_instanced_batch(&cpu, cap as u32);
                                            self.camera_gizmo = Some(CameraGizmoResources {
                                                cpu_model: cpu, gpu_model, batch, capacity: cap,
                                            });
                                        }
                                    }
                                }
                            }
                            // カメラギズモバッチを毎フレーム更新する（インスタンス変換・視錐台カリング）
                            if is_3d_scene && !cam_gizmo_actor_mats.is_empty() {
                                let cp  = self.camera.position();
                                let v   = self.camera.view_matrix();
                                let p   = self.camera.projection_matrix();
                                let fp  = extract_frustum_planes(&(p * v).data);
                                let cpo = [cp.x, cp.y, cp.z];
                                let transforms: Vec<[[f32; 4]; 4]> =
                                    cam_gizmo_actor_mats.iter().map(|&(_, m)| m).collect();
                                if let Some(gizmo) = &mut self.camera_gizmo {
                                    gizmo.batch.mark_dirty();
                                    gizmo.batch.update(
                                        &draw_ctx.queue,
                                        &gizmo.cpu_model,
                                        &transforms,
                                        &fp,
                                        cpo,
                                        0.0,
                                    );
                                }
                            }

                            // 選択中アクターのカメラデータ取得
                            let selected_cam_data = if is_3d_scene {
                                camera_scene_gizmo::get_selected_camera_data(
                                    &scene.actors, &scene.world,
                                    self.active_world_line,
                                    self.actor_virtual_selected_idx,
                                )
                            } else { None };

                            // フラスタムラインバッチ（選択カメラアクターのみ）
                            let frustum_batch = if let Some(ref cam_data) = selected_cam_data {
                                camera_scene_gizmo::build_camera_frustum_batch(
                                    cam_data, aspect, &draw_ctx.device,
                                )
                            } else { None };

                            // カメラプレビューリソースを初期化・更新する
                            if selected_cam_data.is_some() {
                                // リソースが未初期化の場合は生成する
                                if self.camera_preview.is_none() {
                                    self.camera_preview = Some(CameraPreviewResources::new(
                                        &draw_ctx.device,
                                        &draw_ctx.pipelines.camera_preview_blit,
                                        CAMERA_PREVIEW_W, CAMERA_PREVIEW_H,
                                    ));
                                }
                                // ブリット矩形を更新する
                                if let Some(ref preview) = self.camera_preview {
                                    preview.update_blit_rect(
                                        &draw_ctx.queue, vp_w_f, vp_h_f,
                                        CAMERA_PREVIEW_W as f32, CAMERA_PREVIEW_H as f32,
                                    );
                                }
                            } else {
                                // 選択解除時はリソースを破棄する（メモリ節約）
                                self.camera_preview = None;
                            }

                            // カメラプレビューレンダーパス（選択カメラのビューで全 MC を描画）
                            if let (Some(cam_data), Some(preview)) =
                                (selected_cam_data.as_ref(), self.camera_preview.as_ref())
                            {
                                let res = [CAMERA_PREVIEW_W as f32, CAMERA_PREVIEW_H as f32];
                                let preview_aspect = CAMERA_PREVIEW_W as f32 / CAMERA_PREVIEW_H as f32;
                                let cam_uniform = camera_scene_gizmo::build_camera_uniform(
                                    cam_data, preview_aspect, res,
                                );
                                // プレビュー用一時カメラバッファを生成する
                                let preview_cam_buf = CameraBuffer::new(
                                    &draw_ctx.device,
                                    &draw_ctx.pipelines.unlit_line.camera_bgl,
                                );
                                preview_cam_buf.update(&draw_ctx.queue, &cam_uniform);

                                // メッシュパイプライン用カメラバッファも生成する（PBR mesh 描画用）
                                let preview_mesh_cam_buf = CameraBuffer::new(
                                    &draw_ctx.device,
                                    &draw_ctx.pipelines.mesh.camera_bgl,
                                );
                                preview_mesh_cam_buf.update(&draw_ctx.queue, &cam_uniform);

                                // オフスクリーンプレビューパスで全 MC を描画する
                                let clear_col = {
                                    let [r, g, b, a] = cam_data.clear_color;
                                    wgpu::Color { r: r as f64, g: g as f64, b: b as f64, a: a as f64 }
                                };
                                {
                                    let mut preview_pass = frame.begin_offscreen_pass(
                                        &preview.color_view,
                                        &preview.depth_view,
                                        clear_col,
                                    );
                                    for &(_, _, _, amc) in &all_mcs {
                                        if let Some((gpu, batch)) = amc.rendering_refs() {
                                            draw_model_indirect(
                                                &mut preview_pass, gpu, batch,
                                                &preview_mesh_cam_buf.bind_group,
                                                &draw_ctx.pipelines,
                                            );
                                        }
                                    }
                                }
                            }

                            // ギズモ GPU バッファ（レンダーパスの前に生成）
                            let show_gizmo_pre = self.mode == RuntimeMode::Edit || self.paused;
                            let gizmo_gpu_batch = if show_gizmo_pre
                                && self.tool_mode != ToolMode::Select
                            {
                                gizmo_pos.map(|pos| {
                                    // 2D アクター編集タブ / それ以外でギズモ半径を切り替える
                                    let (radius, cam_pos_arr) = if is_actor_edit_2d {
                                        // 2D スクリーンスペース: ビューポート高さの 15% をギズモ半径とする。
                                        // ortho_half_h = vp_h/2 なので * 0.15 = vp_h * 0.075 px
                                        let cam_2d = self.canvas_cameras.get(&self.active_world_line);
                                        let r = cam_2d.map(|c| c.ortho_half_h * 0.15).unwrap_or(54.0);
                                        (r, [0.0f32, 0.0, -100.0])
                                    } else {
                                        // 3D perspective（通常3D または ワールドスペースキャンバス）: 距離と FOV からギズモ半径を計算する
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
                                    if is_actor_edit_2d || scene_canvas_ss {
                                        // 2D スクリーンスペース（アクター編集タブ・シーンSS共通）:
                                        // 2D ortho でスクリーン座標をキャンバス XY に変換する
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
                            // アクター編集タブの 2D キャンバス: XY 平面グリッド（常時表示）
                            // その他（シーン上のキャンバス含む 3D 系）: XZ 平面グリッド
                            // シーン上に canvas があっても 3D グリッドを維持する（is_actor_edit_canvas で判定）
                            let is_actor_edit_canvas = is_canvas && self.actor_edit_canvas_wls.contains(&self.active_world_line);
                            let grid_gpu_batch = if in_editor && (self.show_grid || (self.active_world_line != 0 && !is_actor_edit_canvas)) {
                                let mut lb = LineBatch::new();
                                // モード別グリッド色
                                // 2D アクター編集: 薄い青系（mine: 薄く, major: 中程度）
                                // 3D アクター編集: 紺背景に映える青系
                                // 3D シーン: ダークグレー
                                let (minor, major): ([f32; 4], [f32; 4]) = if is_actor_edit_canvas {
                                    ([0.22, 0.25, 0.40, 0.20], [0.32, 0.40, 0.60, 0.55])
                                } else if self.active_world_line != 0 {
                                    ([0.22, 0.25, 0.40, 1.0], [0.32, 0.36, 0.55, 1.0])
                                } else {
                                    ([0.18, 0.18, 0.18, 1.0], [0.30, 0.30, 0.30, 1.0])
                                };
                                let ax_x: [f32; 4] = [0.60, 0.15, 0.15, 0.90];

                                if is_actor_edit_canvas {
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
                                    // ワールドスペース時はキャンバス座標をワールドユニットへスケールする
                                    let canvas_scale = if use_screen_space { 1.0f32 } else { CANVAS_WORLD_SCALE };

                                    // スプライト情報収集（再帰的にアクターツリーを走査）
                                    //
                                    // スケールなし累積行列(world_rs)と累積スケール(cumul_scale)を分離管理し、
                                    // CanvasComponent のスケールモードに応じて子への伝播を制御する。
                                    //   scale_transform=false → 子の位置はスケール非依存（絶対座標）
                                    //   scale_size=false      → 子のサイズはスケール非依存（絶対サイズ）
                                    //   回転は常に追従する。
                                    fn collect_sprite_items(
                                        actors:             &[crate::engine::structs::objects::Actor],
                                        world:              &crate::engine::ecs::World,
                                        wl:                 u32,
                                        draw_ctx:           &DrawContext,
                                        // 親アクターの CanvasComponent サイズ（anchor 計算用）
                                        parent_canvas_size: Option<[f32; 2]>,
                                        // 親のワールド行列（スケールなし：回転+平行移動のみ）
                                        parent_world_rs:    [[f32; 4]; 4],
                                        // 親の累積スケール。スケールモードに応じて子に伝播するかを制御する。
                                        parent_cumul_scale: [f32; 2],
                                        // 直前の親 CanvasComponent のスケールモード (scale_transform, scale_size)
                                        parent_scale_mode:  (bool, bool),
                                        // ワールドスペース変換スケール（1.0=スクリーンスペース、CANVAS_WORLD_SCALE=ワールドスペース）
                                        canvas_scale:       f32,
                                        // Y 軸符号（スクリーンスペース=1.0、ワールドスペース=-1.0で Y を反転）
                                        y_sign:             f32,
                                        // シーンスクリーンスペースモード時のビューポートサイズ（ルートアンカー計算用）
                                        // None = アクター編集タブまたはワールドスペース
                                        viewport_size:      Option<[f32; 2]>,
                                        out:                &mut Vec<([[f32; 4]; 4], [f32; 4], Option<std::sync::Arc<GpuSpriteTexture>>)>,
                                    ) {
                                        let (sm_transform, sm_size) = parent_scale_mode;

                                        for actor in actors {
                                            if actor.world_line != wl { continue; }
                                            let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
                                            if let Some(ct) = ct_opt {
                                                // anchor オフセット計算:
                                                // ルートレベル（parent_canvas_size=None）かつシーンSSモードでは
                                                // ビューポートを仮想親として扱い、ortho 原点（画面中央）からの
                                                // オフセットを計算する。それ以外は親キャンバスサイズ基準。
                                                let (anchor_off_x, anchor_off_y) = if parent_canvas_size.is_none() {
                                                    if let Some([vw, vh]) = viewport_size {
                                                        // 画面中央が ortho 原点 → anchor=0,0 で画面左上に寄せるため -vp/2 オフセット
                                                        (vw * ct.anchor[0] - vw / 2.0,
                                                         vh * ct.anchor[1] - vh / 2.0)
                                                    } else {
                                                        (0.0, 0.0)
                                                    }
                                                } else {
                                                    (parent_canvas_size.map_or(0.0, |[pw, _]| pw * ct.anchor[0] * parent_cumul_scale[0]),
                                                     parent_canvas_size.map_or(0.0, |[_, ph]| ph * ct.anchor[1] * parent_cumul_scale[1]))
                                                };

                                                // 有効位置（スケールモードに応じて位置にスケールを乗算）
                                                let eff_pos = if sm_transform {
                                                    [ct.position[0] * parent_cumul_scale[0] + anchor_off_x,
                                                     ct.position[1] * parent_cumul_scale[1] + anchor_off_y]
                                                } else {
                                                    [ct.position[0] + anchor_off_x,
                                                     ct.position[1] + anchor_off_y]
                                                };

                                                // 有効 CanvasTransform（位置を調整済み・scaleは自身のもの・anchorは適用済み）
                                                let eff_ct = CanvasTransform {
                                                    position: eff_pos,
                                                    rotation: ct.rotation,
                                                    scale:    ct.scale,
                                                    pivot:    ct.pivot,
                                                    anchor:   [0.0, 0.0],
                                                };

                                                // 自アクターの CanvasComponent を先に取得する。
                                                // pivot はノーマライズ値（[0,1]）のため、
                                                // self_world_rs の計算に実際のキャンバスサイズが必要。
                                                let my_canvas = actor.slots().iter()
                                                    .filter(|s| s.kind == ComponentKind::Canvas)
                                                    .find_map(|s| world.get::<CanvasComponent>(s.entity));
                                                // sm_size による拡縮を反映した有効キャンバスサイズ
                                                let (my_eff_w, my_eff_h) = my_canvas.map(|cc| (
                                                    cc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 },
                                                    cc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 },
                                                )).unwrap_or((1.0, 1.0));

                                                // 自分のワールド行列（スケールなし）を親 world_rs と合成。
                                                // to_mat4_sized でキャンバスの実サイズを渡し、
                                                // pivot オフセットを正しく計算する。
                                                let self_world_rs = mat4x4_mul(
                                                    parent_world_rs,
                                                    CanvasTransform { scale: [1.0, 1.0], ..eff_ct.clone() }
                                                        .to_mat4_sized(my_eff_w, my_eff_h),
                                                );

                                                for slot in actor.slots() {
                                                    if slot.kind == ComponentKind::Sprite {
                                                        if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                                                            // サイズスケール（scale_size モードに応じて）
                                                            let eff_w = sc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 };
                                                            let eff_h = sc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 };
                                                            // スプライト行優先行列（サイズ付き）を親 world_rs と合成し GPU 列優先に変換する
                                                            // canvas_scale でキャンバス座標→ワールド座標へスケールする（WS=1 ならそのまま）
                                                            let sprite_world = mat4x4_mul(parent_world_rs, eff_ct.to_sprite_mat4(eff_w, eff_h));
                                                            // y_sign でキャンバス Y 軸（下向き）→ ワールド Y 軸（上向き）を反転する
                                                            // スクリーンスペース: y_sign=1.0（反転なし）
                                                            // ワールドスペース:   y_sign=-1.0（Y 反転）
                                                            let csy = canvas_scale * y_sign;
                                                            let gpu_mat = [
                                                                [sprite_world[0][0] * canvas_scale, sprite_world[1][0] * csy, 0.0, 0.0],
                                                                [sprite_world[0][1] * canvas_scale, sprite_world[1][1] * csy, 0.0, 0.0],
                                                                [0.0,                               0.0,                     1.0, 0.0],
                                                                [sprite_world[0][3] * canvas_scale, sprite_world[1][3] * csy, 0.0, 1.0],
                                                            ];
                                                            // テクスチャをキャッシュから取得または新規ロード。
                                                            // キャッシュ値: Some(arc)=成功 / None=失敗済み（毎フレームのリトライ・ログ爆発防止）
                                                            let tex = if sc.texture_path.is_empty() {
                                                                None
                                                            } else {
                                                                let path_str = sc.texture_path.clone();
                                                                let mut cache = draw_ctx.sprite_tex_cache.borrow_mut();
                                                                if !cache.contains_key(&path_str) {
                                                                    // 初回のみロード試行（成否に関わらずキャッシュに記録）
                                                                    let loaded = load_sprite_texture(
                                                                        &draw_ctx.device,
                                                                        &draw_ctx.queue,
                                                                        &path_str,
                                                                        &draw_ctx.pipelines.sprite.tex_bgl,
                                                                        &draw_ctx.pipelines.sprite.sampler,
                                                                    );
                                                                    // None（失敗）もキャッシュに入れて次フレームからスキップ
                                                                    cache.insert(path_str.clone(), loaded);
                                                                }
                                                                // Some(Some(arc))=成功 / Some(None)=失敗 → flatten で None に統一
                                                                cache.get(&sc.texture_path).and_then(|e| e.clone())
                                                            };
                                                            out.push((gpu_mat, sc.color, tex));
                                                        }
                                                    }
                                                }

                                                // my_canvas を再利用して子への CanvasComponent 情報を構築する
                                                let child_info = my_canvas
                                                    .map(|cc| ([cc.width, cc.height], (cc.scale_transform, cc.scale_size), cc.auto_scale));

                                                let child_canvas_size  = child_info.map(|(sz, _, _)| sz);
                                                let child_scale_mode   = child_info.map(|(_, sm, _)| sm).unwrap_or((false, false));
                                                // ルートキャンバスかつ auto_scale=true のとき、ビューポートサイズ/参照サイズ で自動スケールする
                                                let auto_scale_factor = if parent_canvas_size.is_none() {
                                                    if let (Some([vw, vh]), Some((_, _, true))) = (viewport_size, child_info) {
                                                        [vw / my_eff_w, vh / my_eff_h]
                                                    } else {
                                                        [1.0f32, 1.0]
                                                    }
                                                } else {
                                                    [1.0f32, 1.0]
                                                };
                                                // 子への累積スケール（このアクターの scale_transform に応じて自分のスケールを積む）
                                                let child_cumul_scale = if child_scale_mode.0 {
                                                    [parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                                                     parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1]]
                                                } else {
                                                    // スケール伝播なし: auto_scale のみ適用
                                                    [ct.scale[0] * auto_scale_factor[0],
                                                     ct.scale[1] * auto_scale_factor[1]]
                                                };
                                                collect_sprite_items(
                                                    &actor.children, world, wl, draw_ctx,
                                                    child_canvas_size, self_world_rs,
                                                    child_cumul_scale, child_scale_mode,
                                                    canvas_scale, y_sign, viewport_size, out,
                                                );
                                            }
                                        }
                                    }

                                    // 単位行列・初期累積スケール（ルートレベル用）
                                    const IDENTITY: [[f32; 4]; 4] = [
                                        [1.0, 0.0, 0.0, 0.0],
                                        [0.0, 1.0, 0.0, 0.0],
                                        [0.0, 0.0, 1.0, 0.0],
                                        [0.0, 0.0, 0.0, 1.0],
                                    ];
                                    // Y 軸符号とビューポートサイズを決定する
                                    // シーン SS モード: ビューポートを仮想親としてアンカー計算・auto_scale に使う
                                    let y_sign = if use_screen_space { 1.0f32 } else { -1.0 };
                                    let is_scene_ss = use_screen_space && !self.actor_edit_canvas_wls.contains(&wl);
                                    let vp_w = window_size.map_or(1280.0, |s| s.width  as f32);
                                    let vp_h = window_size.map_or(720.0,  |s| s.height as f32);
                                    let viewport_size = if is_scene_ss { Some([vp_w, vp_h]) } else { None };
                                    let mut items = Vec::new();
                                    collect_sprite_items(
                                        &scene.actors, &scene.world, wl, draw_ctx,
                                        None, IDENTITY, [1.0, 1.0], (false, false),
                                        canvas_scale, y_sign, viewport_size, &mut items,
                                    );
                                    prepare_sprites_from_mats(&draw_ctx.device, &draw_ctx.pipelines.sprite, &items)
                                } else { vec![] }
                            } else { vec![] };

                            // CanvasComponent 矩形アウトラインバッチ（エディタモード + 2D キャンバス世界線のみ）
                            // Canvas のアウトラインは常に表示、Sprite のアウトラインは選択時のみ表示する。
                            let canvas_rect_batch = if in_editor && is_canvas {
                                if let Some(scene) = &self.scene {
                                    let wl = self.active_world_line;
                                    let mut lb = LineBatch::new();
                                    let rect_col: [f32; 4] = [0.85, 0.95, 1.0, 0.9];
                                    // ワールドスペース時はキャンバス座標をワールドユニットへスケールする
                                    let canvas_scale_rect = if use_screen_space { 1.0f32 } else { CANVAS_WORLD_SCALE };

                                    // CanvasComponent / Sprite のアウトラインを再帰的に収集する。
                                    // collect_sprite_items と同じスケールモード・累積行列の仕組みを使う。
                                    fn collect_canvas_rects(
                                        actors:             &[crate::engine::structs::objects::Actor],
                                        world:              &crate::engine::ecs::World,
                                        wl:                 u32,
                                        lb:                 &mut LineBatch,
                                        col:                [f32; 4],
                                        selected_dfs_ids:   &[usize],
                                        counter:            &mut u32,
                                        parent_canvas_size: Option<[f32; 2]>,
                                        parent_world_rs:    [[f32; 4]; 4],
                                        parent_cumul_scale: [f32; 2],
                                        parent_scale_mode:  (bool, bool),
                                        // ワールドスペース変換スケール
                                        canvas_scale:       f32,
                                        // Y 軸符号
                                        y_sign:             f32,
                                        // シーン SS モード時のビューポートサイズ
                                        viewport_size:      Option<[f32; 2]>,
                                    ) {
                                        let (sm_transform, sm_size) = parent_scale_mode;

                                        for actor in actors {
                                            if actor.world_line != wl { continue; }
                                            let my_dfs = *counter as usize;
                                            *counter += 1;

                                            let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
                                            if let Some(ct) = ct_opt {
                                                // anchor オフセット計算（collect_sprite_items と同じロジック）
                                                let (anchor_off_x, anchor_off_y) = if parent_canvas_size.is_none() {
                                                    if let Some([vw, vh]) = viewport_size {
                                                        (vw * ct.anchor[0] - vw / 2.0,
                                                         vh * ct.anchor[1] - vh / 2.0)
                                                    } else {
                                                        (0.0, 0.0)
                                                    }
                                                } else {
                                                    (parent_canvas_size.map_or(0.0, |[pw, _]| pw * ct.anchor[0] * parent_cumul_scale[0]),
                                                     parent_canvas_size.map_or(0.0, |[_, ph]| ph * ct.anchor[1] * parent_cumul_scale[1]))
                                                };

                                                // 有効位置（スケールモードに応じて）
                                                let eff_pos = if sm_transform {
                                                    [ct.position[0] * parent_cumul_scale[0] + anchor_off_x,
                                                     ct.position[1] * parent_cumul_scale[1] + anchor_off_y]
                                                } else {
                                                    [ct.position[0] + anchor_off_x,
                                                     ct.position[1] + anchor_off_y]
                                                };
                                                let eff_ct = CanvasTransform {
                                                    position: eff_pos,
                                                    rotation: ct.rotation,
                                                    scale:    ct.scale,
                                                    pivot:    ct.pivot,
                                                    anchor:   [0.0, 0.0],
                                                };

                                                // pivot はノーマライズ値のため実際のキャンバスサイズで補正する
                                                let my_canvas_r = actor.slots().iter()
                                                    .filter(|s| s.kind == ComponentKind::Canvas)
                                                    .find_map(|s| world.get::<CanvasComponent>(s.entity));
                                                let (my_eff_w_r, my_eff_h_r) = my_canvas_r.map(|cc| (
                                                    cc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 },
                                                    cc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 },
                                                )).unwrap_or((1.0, 1.0));

                                                let self_world_rs = mat4x4_mul(
                                                    parent_world_rs,
                                                    CanvasTransform { scale: [1.0, 1.0], ..eff_ct.clone() }
                                                        .to_mat4_sized(my_eff_w_r, my_eff_h_r),
                                                );

                                                for slot in actor.slots() {
                                                    match slot.kind {
                                                        ComponentKind::Canvas => {
                                                            // CanvasComponent: キャンバス領域のアウトラインを常に描画する
                                                            if let Some(cc) = world.get::<CanvasComponent>(slot.entity) {
                                                                let eff_w = cc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 };
                                                                let eff_h = cc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 };
                                                                let m = mat4x4_mul(parent_world_rs, eff_ct.to_mat4_sized(eff_w, eff_h));
                                                                let csy = canvas_scale * y_sign;
                                                                let tp = |lx: f32, ly: f32| -> [f32; 3] {
                                                                    [(m[0][0]*lx + m[0][1]*ly + m[0][3]) * canvas_scale,
                                                                     (m[1][0]*lx + m[1][1]*ly + m[1][3]) * csy,
                                                                     0.0f32]
                                                                };
                                                                let tl = tp(0.0,   0.0  );
                                                                let tr = tp(eff_w, 0.0  );
                                                                let br = tp(eff_w, eff_h);
                                                                let bl = tp(0.0,   eff_h);
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
                                                                    let eff_w = sc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 };
                                                                    let eff_h = sc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 };
                                                                    let sprite_col: [f32; 4] = [1.0, 0.95, 0.6, 0.85];
                                                                    let m = mat4x4_mul(parent_world_rs, eff_ct.to_sprite_mat4(eff_w, eff_h));
                                                                    let csy2 = canvas_scale * y_sign;
                                                                    let tp = |lx: f32, ly: f32| -> [f32; 3] {
                                                                        [(m[0][0]*lx + m[0][1]*ly + m[0][3]) * canvas_scale,
                                                                         (m[1][0]*lx + m[1][1]*ly + m[1][3]) * csy2,
                                                                         0.0f32]
                                                                    };
                                                                    let tl = tp(0.0, 0.0);
                                                                    let tr = tp(1.0, 0.0);
                                                                    let br = tp(1.0, 1.0);
                                                                    let bl = tp(0.0, 1.0);
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

                                                let child_info = my_canvas_r
                                                    .map(|cc| ([cc.width, cc.height], (cc.scale_transform, cc.scale_size), cc.auto_scale));

                                                let child_canvas_size = child_info.map(|(sz, _, _)| sz);
                                                let child_scale_mode  = child_info.map(|(_, sm, _)| sm).unwrap_or((false, false));
                                                // ルートキャンバスかつ auto_scale=true のとき、ビューポートサイズ/参照サイズ で自動スケールする
                                                let auto_scale_factor = if parent_canvas_size.is_none() {
                                                    if let (Some([vw, vh]), Some((_, _, true))) = (viewport_size, child_info) {
                                                        [vw / my_eff_w_r, vh / my_eff_h_r]
                                                    } else {
                                                        [1.0f32, 1.0]
                                                    }
                                                } else {
                                                    [1.0f32, 1.0]
                                                };
                                                let child_cumul_scale = if child_scale_mode.0 {
                                                    [parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                                                     parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1]]
                                                } else {
                                                    [ct.scale[0] * auto_scale_factor[0],
                                                     ct.scale[1] * auto_scale_factor[1]]
                                                };
                                                collect_canvas_rects(
                                                    &actor.children, world, wl, lb, col,
                                                    selected_dfs_ids, counter,
                                                    child_canvas_size, self_world_rs,
                                                    child_cumul_scale, child_scale_mode,
                                                    canvas_scale, y_sign, viewport_size,
                                                );
                                            }
                                        }
                                    }

                                    const IDENTITY_RECT: [[f32; 4]; 4] = [
                                        [1.0, 0.0, 0.0, 0.0],
                                        [0.0, 1.0, 0.0, 0.0],
                                        [0.0, 0.0, 1.0, 0.0],
                                        [0.0, 0.0, 0.0, 1.0],
                                    ];
                                    let mut counter: u32 = 0;
                                    // rect アウトライン用 y_sign と viewport_size
                                    let y_sign_rect = if use_screen_space { 1.0f32 } else { -1.0 };
                                    let is_scene_ss_rect = use_screen_space && !self.actor_edit_canvas_wls.contains(&wl);
                                    let vp_w_r = window_size.map_or(1280.0, |s| s.width  as f32);
                                    let vp_h_r = window_size.map_or(720.0,  |s| s.height as f32);
                                    let viewport_size_rect = if is_scene_ss_rect { Some([vp_w_r, vp_h_r]) } else { None };
                                    collect_canvas_rects(
                                        &scene.actors, &scene.world, wl, &mut lb, rect_col,
                                        &self.selected_actor_dfs_ids, &mut counter,
                                        None, IDENTITY_RECT, [1.0, 1.0], (false, false),
                                        canvas_scale_rect, y_sign_rect, viewport_size_rect,
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
                            // キャンバスモード・3D モード共通: 常に 3D パースペクティブ行列でスクリーン座標を計算する。
                            // キャンバスアクターは MC インスタンスを持たないためアイコンは表示されない。
                            let icon_overlay_batch = if in_editor {
                                let vp_w = window_size.map_or(1280.0, |s| s.width  as f32);
                                let vp_h = window_size.map_or(720.0,  |s| s.height as f32);
                                let (view, proj) = (self.camera.view_matrix(), self.camera.projection_matrix());
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

                                // 矩形選択ビジュアル（scene_canvas_ss はオーバーレイパスで描画）
                                if !scene_canvas_ss {
                                    if let (Some(rect_batch), Some((_, line_bg))) =
                                        (&rect_gpu_batch, &self.line_model_buf)
                                    {
                                        draw_line_batch(
                                            &mut pass, rect_batch,
                                            &camera_buf.bind_group, line_bg,
                                            &draw_ctx.pipelines,
                                        );
                                    }
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

                                // スプライト画像描画（グリッドより前面に描画）
                                // scene_canvas_ss の場合はオーバーレイパスで描画するためスキップ
                                if !scene_canvas_ss && !sprite_prepared.is_empty() {
                                    draw_sprites(
                                        &mut pass,
                                        &draw_ctx.pipelines.sprite,
                                        &camera_buf.bind_group,
                                        &sprite_prepared,
                                    );
                                }

                                // グリッド描画（スプライトより後、Canvas 矩形・アウトラインより前）
                                if let (Some(grid_batch), Some((_, line_bg))) =
                                    (&grid_gpu_batch, &self.line_model_buf)
                                {
                                    draw_line_batch(
                                        &mut pass, grid_batch,
                                        &camera_buf.bind_group, line_bg,
                                        &draw_ctx.pipelines,
                                    );
                                }

                                // CanvasComponent 矩形アウトライン描画（グリッドより前面）
                                // Canvas: 常に表示, Sprite: 選択時のみ表示
                                // scene_canvas_ss の場合はオーバーレイパスで描画するためスキップ
                                if !scene_canvas_ss {
                                    if let (Some(rect_batch), Some((_, line_bg))) =
                                        (&canvas_rect_batch, &self.line_model_buf)
                                    {
                                        draw_line_batch(
                                            &mut pass, rect_batch,
                                            &camera_buf.bind_group, line_bg,
                                            &draw_ctx.pipelines,
                                        );
                                    }
                                }

                                // アウトライン: 全選択アクター（マルチ選択対応）※グリッドより前面に描画
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

                                // カメラフラスタムライン（選択中カメラアクターのみ、3D シーン）
                                if !scene_canvas_ss {
                                    if let (Some(frustum), Some((_, line_bg))) =
                                        (&frustum_batch, &self.line_model_buf)
                                    {
                                        draw_line_batch(
                                            &mut pass, frustum,
                                            &camera_buf.bind_group, line_bg,
                                            &draw_ctx.pipelines,
                                        );
                                    }
                                }

                                // カメラアイコンモデル（全カメラアクター、3D シーン）
                                // camera.glb を InstancedModelBatch で描画する
                                if !scene_canvas_ss && !cam_gizmo_actor_mats.is_empty() {
                                    if let Some(gizmo) = &self.camera_gizmo {
                                        draw_model_indirect(
                                            &mut pass, &gizmo.gpu_model, &gizmo.batch,
                                            &camera_buf.bind_group, &draw_ctx.pipelines,
                                        );
                                    }
                                }

                                // ギズモ（グリッド・アウトラインより前面、アイコンより背面）
                                // scene_canvas_ss の場合はオーバーレイパスで描画するためスキップ
                                if !scene_canvas_ss {
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
                                }

                                // 軸ギズモ（エディタモードのみ）
                                // scene_canvas_ss 時はオーバーレイパスの末尾で最前面描画するためスキップ
                                if !scene_canvas_ss {
                                    if let (Some(batch), Some(ag)) =
                                        (&axis_gizmo_batch, &self.axis_gizmo)
                                    {
                                        ag.draw(batch, &mut pass);
                                    }
                                }

                                // アイコンオーバーレイ（最前面：選択アクター位置マーカー）
                                if let (Some(batch), Some(io)) =
                                    (&icon_overlay_batch, &self.icon_overlay)
                                {
                                    io.draw(batch, &mut pass);
                                }

                            }

                            // ── シーンキャンバスオーバーレイパス（シーンSS専用）──────────────
                            // 3D シーンのカラーを保持しつつ、2D キャンバス要素を最前面に合成する。
                            // アクター編集タブは camera_buf が 2D なのでメインパスで済む。
                            if scene_canvas_ss {
                                if let Some(canvas_cam_buf) = self.canvas_overlay_camera_buf.as_ref() {
                                    let mut overlay_pass = frame.begin_canvas_overlay_pass();

                                    // スプライト画像（アウトラインより前に描画してアウトラインを前面に）
                                    if !sprite_prepared.is_empty() {
                                        draw_sprites(
                                            &mut overlay_pass,
                                            &draw_ctx.pipelines.sprite,
                                            &canvas_cam_buf.bind_group,
                                            &sprite_prepared,
                                        );
                                    }

                                    // CanvasComponent 矩形アウトライン
                                    if let (Some(rect_batch), Some((_, line_bg))) =
                                        (&canvas_rect_batch, &self.line_model_buf)
                                    {
                                        draw_line_batch(
                                            &mut overlay_pass, rect_batch,
                                            &canvas_cam_buf.bind_group, line_bg,
                                            &draw_ctx.pipelines,
                                        );
                                    }

                                    // 矩形選択ビジュアル
                                    if let (Some(rect_batch), Some((_, line_bg))) =
                                        (&rect_gpu_batch, &self.line_model_buf)
                                    {
                                        draw_line_batch(
                                            &mut overlay_pass, rect_batch,
                                            &canvas_cam_buf.bind_group, line_bg,
                                            &draw_ctx.pipelines,
                                        );
                                    }

                                    // ギズモ（スプライト・矩形より前面）
                                    let show_gizmo = in_editor && self.tool_mode != ToolMode::Select;
                                    if show_gizmo {
                                        if let (Some(gpu_batch), Some((_, line_bg))) =
                                            (&gizmo_gpu_batch, &self.line_model_buf)
                                        {
                                            draw_gizmo_batch(
                                                &mut overlay_pass, gpu_batch,
                                                &canvas_cam_buf.bind_group, line_bg,
                                                &draw_ctx.pipelines,
                                            );
                                        }
                                    }

                                    // 軸ギズモ（オーバーレイ最前面）
                                    // scene_canvas_ss 時にメインパスから移動し、常に最前面に表示する
                                    if let (Some(batch), Some(ag)) =
                                        (&axis_gizmo_batch, &self.axis_gizmo)
                                    {
                                        ag.draw(batch, &mut overlay_pass);
                                    }

                                }
                            }

                            // ── カメラプレビューブリット（選択カメラがある場合のみ）──────
                            // メインパスの後に、オフスクリーンプレビューをビューポート右下に貼り付ける。
                            if selected_cam_data.is_some() {
                                if let Some(ref preview) = self.camera_preview {
                                    let mut blit_pass = frame.begin_blit_pass();
                                    blit_pass.set_pipeline(&draw_ctx.pipelines.camera_preview_blit.pipeline);
                                    blit_pass.set_bind_group(0, &preview.blit_rect_bg, &[]);
                                    blit_pass.set_bind_group(1, &preview.blit_tex_bg, &[]);
                                    blit_pass.draw(0..6, 0..1);
                                }
                            }

                            // ── ID パス（Edit/Pause のみ）──────────
                            if in_editor {
                                if let Some(id_buf) = &self.id_buffer {
                                    {
                                        // BindGroup は RenderPass より長く生きる必要があるので先に生成する

                                        // 3D MC ID バインドグループ
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

                                        // カメラギズモ ID バインドグループ（RenderPass より先に生成してライフタイムを確保）
                                        let cam_gizmo_id_base_opt: Option<(wgpu::Buffer, wgpu::BindGroup)> =
                                            if !cam_gizmo_actor_mats.is_empty() && self.camera_gizmo.is_some() {
                                                Some(draw_ctx.create_id_base_bg(mc_total_instances))
                                            } else { None };

                                        // キャンバスアクター ID アイテム収集
                                        // scene canvas モードのみ実行（actor edit 2D タブは CPU picking 専用）
                                        // DFS カウンタは find_actor_by_dfs と同じ規則で全アクターを数える。
                                        let canvas_id_is_ss = scene_canvas_ss;
                                        let canvas_id_raw_items: Vec<(u32, [[f32; 4]; 4], Option<String>)> =
                                            if is_canvas && !is_actor_edit_2d {
                                                if let Some(scene) = &self.scene {
                                                    let wl = self.active_world_line;
                                                    let canvas_scale = if use_screen_space { 1.0f32 } else { CANVAS_WORLD_SCALE };
                                                    let y_sign = if use_screen_space { 1.0f32 } else { -1.0 };
                                                    let vp_w = window_size.map_or(1280.0, |s| s.width  as f32);
                                                    let vp_h = window_size.map_or(720.0,  |s| s.height as f32);
                                                    let viewport_size: Option<[f32; 2]> =
                                                        if scene_canvas_ss { Some([vp_w, vp_h]) } else { None };

                                                    /// キャンバスアクター ID アイテムを DFS 順に収集する。
                                                    ///
                                                    /// DFS カウンタは find_actor_by_dfs と同じ規則で全アクターを数える
                                                    /// （CanvasTransform がないアクターも子を含めてカウント）。
                                                    fn collect_canvas_id_items(
                                                        actors:             &[crate::engine::structs::objects::Actor],
                                                        world:              &crate::engine::ecs::World,
                                                        wl:                 u32,
                                                        counter:            &mut u32,
                                                        parent_canvas_size: Option<[f32; 2]>,
                                                        parent_world_rs:    [[f32; 4]; 4],
                                                        parent_cumul_scale: [f32; 2],
                                                        parent_scale_mode:  (bool, bool),
                                                        canvas_scale:       f32,
                                                        y_sign:             f32,
                                                        viewport_size:      Option<[f32; 2]>,
                                                        mc_total:           u32,
                                                        // out: (raw_id, gpu_mat, sprite_tex_path)
                                                        // sprite_tex_path: Some(path) = スプライトあり → アルファマスク有効
                                                        //                  None       = スプライトなし → 全面選択可能
                                                        out:                &mut Vec<(u32, [[f32; 4]; 4], Option<String>)>,
                                                    ) {
                                                        let (sm_transform, sm_size) = parent_scale_mode;
                                                        for actor in actors {
                                                            if actor.world_line != wl { continue; }
                                                            let my_dfs = *counter;
                                                            *counter += 1;

                                                            let ct_opt = world.get::<CanvasTransform>(actor.entity).cloned();
                                                            let (next_canvas_size, next_cumul_scale, next_scale_mode, next_world_rs) =
                                                                if let Some(ct) = ct_opt {
                                                                    // アンカーオフセット（collect_sprite_items と同じロジック）
                                                                    let (anchor_off_x, anchor_off_y) =
                                                                        if parent_canvas_size.is_none() {
                                                                            if let Some([vw, vh]) = viewport_size {
                                                                                (vw * ct.anchor[0] - vw / 2.0,
                                                                                 vh * ct.anchor[1] - vh / 2.0)
                                                                            } else { (0.0, 0.0) }
                                                                        } else {
                                                                            (parent_canvas_size.map_or(0.0, |[pw, _]| pw * ct.anchor[0] * parent_cumul_scale[0]),
                                                                             parent_canvas_size.map_or(0.0, |[_, ph]| ph * ct.anchor[1] * parent_cumul_scale[1]))
                                                                        };
                                                                    let eff_pos = if sm_transform {
                                                                        [ct.position[0] * parent_cumul_scale[0] + anchor_off_x,
                                                                         ct.position[1] * parent_cumul_scale[1] + anchor_off_y]
                                                                    } else {
                                                                        [ct.position[0] + anchor_off_x,
                                                                         ct.position[1] + anchor_off_y]
                                                                    };
                                                                    let eff_ct = CanvasTransform {
                                                                        position: eff_pos,
                                                                        rotation: ct.rotation,
                                                                        scale:    ct.scale,
                                                                        pivot:    ct.pivot,
                                                                        anchor:   [0.0, 0.0],
                                                                    };

                                                                    // 自アクターの CanvasComponent
                                                                    let my_canvas = actor.slots().iter()
                                                                        .filter(|s| s.kind == ComponentKind::Canvas)
                                                                        .find_map(|s| world.get::<CanvasComponent>(s.entity));
                                                                    let (my_eff_w, my_eff_h) = my_canvas.map(|cc| (
                                                                        cc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 },
                                                                        cc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 },
                                                                    )).unwrap_or((1.0, 1.0));

                                                                    // 子への親ワールド RS 行列
                                                                    let self_world_rs = mat4x4_mul(
                                                                        parent_world_rs,
                                                                        CanvasTransform { scale: [1.0, 1.0], ..eff_ct.clone() }
                                                                            .to_mat4_sized(my_eff_w, my_eff_h),
                                                                    );

                                                                    // ID quad 用 GPU 行列の構築。
                                                                    // テクスチャパスが有効な Sprite のみビューポートからピッキング可能にする。
                                                                    // Sprite なし・テクスチャパス空（単色）のアクターは空白領域とみなし
                                                                    // ビューポートからは選択不可（階層パネルからのみ選択できる）。
                                                                    let csy = canvas_scale * y_sign;
                                                                    let mut gpu_mat_and_path: Option<([[f32; 4]; 4], String)> = None;
                                                                    for slot in actor.slots() {
                                                                        if slot.kind == ComponentKind::Sprite {
                                                                            if let Some(sc) = world.get::<SpriteComponent>(slot.entity) {
                                                                                // テクスチャパスが空 = 単色スプライト = 空白と同様、選択不可
                                                                                if sc.texture_path.is_empty() { break; }
                                                                                let ew = sc.width  * if sm_size { parent_cumul_scale[0] } else { 1.0 };
                                                                                let eh = sc.height * if sm_size { parent_cumul_scale[1] } else { 1.0 };
                                                                                let sw = mat4x4_mul(parent_world_rs, eff_ct.to_sprite_mat4(ew, eh));
                                                                                gpu_mat_and_path = Some(([
                                                                                    [sw[0][0] * canvas_scale, sw[1][0] * csy, 0.0, 0.0],
                                                                                    [sw[0][1] * canvas_scale, sw[1][1] * csy, 0.0, 0.0],
                                                                                    [0.0, 0.0, 1.0, 0.0],
                                                                                    [sw[0][3] * canvas_scale, sw[1][3] * csy, 0.0, 1.0],
                                                                                ], sc.texture_path.clone()));
                                                                                break;
                                                                            }
                                                                        }
                                                                    }

                                                                    if let Some((gpu_mat, tex_path)) = gpu_mat_and_path {
                                                                        // raw_id = mc_total + my_dfs + 1
                                                                        // （0 = 背景、1..mc_total = 3D MC インスタンス）
                                                                        out.push((mc_total + my_dfs + 1, gpu_mat, Some(tex_path)));
                                                                    }

                                                                    // 子への継承情報を計算する（collect_sprite_items と同じ）
                                                                    let child_info = my_canvas.map(|cc| (
                                                                        [cc.width, cc.height],
                                                                        (cc.scale_transform, cc.scale_size),
                                                                        cc.auto_scale,
                                                                    ));
                                                                    let child_canvas_size = child_info.map(|(sz, _, _)| sz);
                                                                    let child_scale_mode  = child_info.map(|(_, sm, _)| sm).unwrap_or((false, false));
                                                                    let auto_scale_factor = if parent_canvas_size.is_none() {
                                                                        if let (Some([vw, vh]), Some((_, _, true))) = (viewport_size, child_info) {
                                                                            [vw / my_eff_w, vh / my_eff_h]
                                                                        } else { [1.0f32, 1.0] }
                                                                    } else { [1.0f32, 1.0] };
                                                                    let child_cumul_scale = if child_scale_mode.0 {
                                                                        [parent_cumul_scale[0] * ct.scale[0] * auto_scale_factor[0],
                                                                         parent_cumul_scale[1] * ct.scale[1] * auto_scale_factor[1]]
                                                                    } else {
                                                                        [ct.scale[0] * auto_scale_factor[0],
                                                                         ct.scale[1] * auto_scale_factor[1]]
                                                                    };
                                                                    (child_canvas_size, child_cumul_scale, child_scale_mode, self_world_rs)
                                                                } else {
                                                                    // CanvasTransform なし: 子は親の情報をそのまま引き継ぐ
                                                                    (parent_canvas_size, parent_cumul_scale, parent_scale_mode, parent_world_rs)
                                                                };

                                                            // 常に子に再帰する（find_actor_by_dfs と DFS カウンタを合わせるため）
                                                            collect_canvas_id_items(
                                                                &actor.children, world, wl, counter,
                                                                next_canvas_size, next_world_rs,
                                                                next_cumul_scale, next_scale_mode,
                                                                canvas_scale, y_sign, viewport_size,
                                                                mc_total, out,
                                                            );
                                                        }
                                                    }

                                                    let mut items = Vec::new();
                                                    let mut ctr   = 0u32;
                                                    const IDENTITY: [[f32; 4]; 4] = [
                                                        [1.0, 0.0, 0.0, 0.0],
                                                        [0.0, 1.0, 0.0, 0.0],
                                                        [0.0, 0.0, 1.0, 0.0],
                                                        [0.0, 0.0, 0.0, 1.0],
                                                    ];
                                                    collect_canvas_id_items(
                                                        &scene.actors, &scene.world, wl,
                                                        &mut ctr, None, IDENTITY,
                                                        [1.0, 1.0], (false, false),
                                                        canvas_scale, y_sign, viewport_size,
                                                        canvas_id_offset, &mut items,
                                                    );
                                                    items
                                                } else { vec![] }
                                            } else { vec![] };

                                        // キャンバス ID GPU バインドグループ（render pass より長く生きる）
                                        let canvas_id_bgs: Vec<(wgpu::Buffer, wgpu::BindGroup)> =
                                            canvas_id_raw_items.iter()
                                                .map(|&(raw_id, gpu_mat, _)| {
                                                    prepare_canvas_id_bg(
                                                        &draw_ctx.device, &draw_ctx.pipelines,
                                                        gpu_mat, raw_id,
                                                    )
                                                })
                                                .collect();

                                        // スプライトテクスチャ Arc を保持してライフタイムを確保する
                                        // （render pass 中に参照するため drop されないようにする）
                                        let canvas_sprite_arcs: Vec<Option<std::sync::Arc<GpuSpriteTexture>>> = {
                                            let cache = draw_ctx.sprite_tex_cache.borrow();
                                            canvas_id_raw_items.iter()
                                                .map(|(_, _, path_opt): &(u32, [[f32;4];4], Option<String>)| {
                                                    // path_opt は常に Some（テクスチャありのみ out に追加される）
                                                    path_opt.as_deref().and_then(|path| {
                                                        cache.get(path).and_then(|opt| opt.clone())
                                                    })
                                                })
                                                .collect()
                                        };

                                        // テクスチャ BG をアイテムごとに生成する
                                        // スプライトありは Arc から view を取得、なしは白テクスチャ（全面選択可能）
                                        let canvas_id_tex_bgs: Vec<wgpu::BindGroup> =
                                            canvas_sprite_arcs.iter()
                                                .map(|arc_opt: &Option<std::sync::Arc<GpuSpriteTexture>>| {
                                                    let view = arc_opt.as_ref()
                                                        .map(|arc| &arc.view)
                                                        .unwrap_or(&draw_ctx.pipelines.canvas_id.white_view);
                                                    draw_ctx.device.create_bind_group(&wgpu::BindGroupDescriptor {
                                                        label:   Some("CanvasId Tex BG"),
                                                        layout:  &draw_ctx.pipelines.canvas_id.tex_bgl,
                                                        entries: &[
                                                            wgpu::BindGroupEntry {
                                                                binding:  0,
                                                                resource: wgpu::BindingResource::TextureView(view),
                                                            },
                                                            wgpu::BindGroupEntry {
                                                                binding:  1,
                                                                resource: wgpu::BindingResource::Sampler(
                                                                    &draw_ctx.pipelines.canvas_id.sampler
                                                                ),
                                                            },
                                                        ],
                                                    })
                                                })
                                                .collect();
                                        let canvas_id_tex_bg_refs: Vec<&wgpu::BindGroup> =
                                            canvas_id_tex_bgs.iter().collect();

                                        let mut id_pass = frame.begin_id_pass(&id_buf.view);

                                        // 3D MC ID 描画
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

                                        // カメラギズモ ID 描画
                                        // base = mc_total_instances で全インスタンスを一括描画する。
                                        // インスタンス local_idx → cam_gizmo_actor_mats[local_idx].0 = dfs_id
                                        if let (Some(gizmo), Some((_, cam_id_bg))) =
                                            (&self.camera_gizmo, &cam_gizmo_id_base_opt)
                                        {
                                            draw_id_pass(
                                                &mut id_pass,
                                                &gizmo.gpu_model, &gizmo.batch,
                                                &camera_buf.bind_group, &draw_ctx.pipelines,
                                                cam_id_bg,
                                            );
                                        }

                                        // キャンバスアクター ID 描画（3D MC より後で常に上書き）
                                        // WS: perspective camera、SS: 2D ortho カメラを使用する
                                        let ss_camera_bg: Option<&wgpu::BindGroup> = if canvas_id_is_ss {
                                            self.canvas_overlay_camera_buf.as_ref().map(|b| &b.bind_group)
                                        } else { None };
                                        if canvas_id_is_ss {
                                            draw_canvas_id_items(
                                                &mut id_pass, &draw_ctx.pipelines,
                                                &camera_buf.bind_group, ss_camera_bg,
                                                &[], &[],
                                                &canvas_id_bgs, &canvas_id_tex_bg_refs,
                                            );
                                        } else {
                                            draw_canvas_id_items(
                                                &mut id_pass, &draw_ctx.pipelines,
                                                &camera_buf.bind_group, None,
                                                &canvas_id_bgs, &canvas_id_tex_bg_refs,
                                                &[], &[],
                                            );
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

                            // FPS 計測: frame.finish() 後に壁時計でカウント
                            // delta_time ではなく present 完了回数/秒 で計測することで
                            // non-blocking な GPU submit でも正確な fps が得られる。
                            {
                                const FPS_WINDOW_SECS: f32 = 0.5;
                                self.fps_frame_count += 1;
                                let elapsed = self.fps_frame_start.elapsed().as_secs_f32();
                                if elapsed >= FPS_WINDOW_SECS {
                                    self.fps_display = self.fps_frame_count as f32 / elapsed;
                                    self.fps_frame_count = 0;
                                    self.fps_frame_start = std::time::Instant::now();
                                    if let Some(ipc) = &self.ipc {
                                        ipc.send(&format!("FPS:{:.1}", self.fps_display));
                                    }
                                }
                            }

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
                        // Lost: GPU がサーフェスを解放した（フルスクリーン切替など）
                        // Outdated: 親ウィンドウ変更・リサイズでサーフェスが無効化された
                        // どちらも surface.configure() をやり直せば回復する。
                        //
                        // C# の EmbedRuntimeWindow は SetParent を最初に呼ぶ。
                        // このため Outdated が発生する時点では必ず SetParent 済みであり、
                        // GetParent(my_hwnd) は正確な埋め込みコンテナを返す。
                        // GetClientRect(parent) = Vulkan の currentExtent と一致するサイズ
                        // で resize することで depth/color attachment の不一致を防ぐ。
                        //
                        // my_hwnd を事前コピーしているのは &mut self.renderer を借用した
                        // ブロック内で self メソッドを呼べない borrow checker の制約のため。
                        Err(wgpu::SurfaceError::Lost | wgpu::SurfaceError::Outdated) => {
                            #[cfg(target_os = "windows")]
                            let resize_to = {
                                use windows_sys::Win32::Foundation::RECT;
                                use windows_sys::Win32::UI::WindowsAndMessaging::{
                                    GetClientRect, GetParent,
                                };
                                let parent = unsafe { GetParent(my_hwnd as _) };
                                let mut sz = None;
                                if !parent.is_null() {
                                    let mut r = RECT { left: 0, top: 0, right: 0, bottom: 0 };
                                    if unsafe { GetClientRect(parent, &mut r) } != 0 {
                                        let w = (r.right  - r.left) as u32;
                                        let h = (r.bottom - r.top)  as u32;
                                        if w > 0 && h > 0 {
                                            sz = Some(winit::dpi::PhysicalSize { width: w, height: h });
                                        }
                                    }
                                }
                                sz.or(window_size)
                            };
                            #[cfg(not(target_os = "windows"))]
                            let resize_to = window_size;
                            // 0x0 は最小化中に発生するケース。サイズ変更不要なのでスキップ。
                            let is_zero = resize_to.map_or(true, |s| s.width == 0 || s.height == 0);
                            if let Some(size) = resize_to {
                                if !is_zero {
                                    renderer.resize(size);
                                }
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
                        } else {
                            let global = raw - 1; // 0 始まりグローバル ID
                            // 3D MC アクター判定（raw_id = base + local + 1 のいずれかの MC 範囲に入るか）
                            let mc_hit = wl_mc_pick_infos.iter()
                                .find(|&&(base, _, _, count)| global >= base && (global - base) < count as u32);

                            if let Some(&(base, dfs_id, slot_i, _)) = mc_hit {
                                // 3D MC アクター選択
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
                            } else if global >= mc_total_instances
                                && global < mc_total_instances + camera_gizmo_count
                                && !cam_gizmo_actor_mats.is_empty()
                            {
                                // カメラギズモアイコン選択
                                // global - mc_total_instances = カメラギズモのローカルインスタンスインデックス
                                let cam_local_idx = (global - mc_total_instances) as usize;
                                if let Some(&(dfs_id, _)) = cam_gizmo_actor_mats.get(cam_local_idx) {
                                    self.actor_virtual_selected_slot_idx = 0;
                                    if self.ctrl_at_press {
                                        if self.selected_actor_dfs_ids.contains(&dfs_id) {
                                            self.selected_actor_dfs_ids.retain(|&x| x != dfs_id);
                                            if self.actor_virtual_selected_idx == Some(dfs_id) {
                                                self.actor_virtual_selected_idx = self.selected_actor_dfs_ids.last().copied();
                                            }
                                        } else {
                                            self.selected_actor_dfs_ids.push(dfs_id);
                                            self.actor_virtual_selected_idx = Some(dfs_id);
                                        }
                                    } else {
                                        self.actor_virtual_selected_idx = Some(dfs_id);
                                        self.selected_actor_dfs_ids     = vec![dfs_id];
                                    }
                                    self.selected_instances.clear();
                                    self.send_actor_components(dfs_id as u32, 0);
                                }
                            } else if global >= canvas_id_offset {
                                // キャンバスアクター選択
                                // raw_id = canvas_id_offset + dfs_id + 1 → canvas_dfs_id = global - canvas_id_offset
                                let canvas_dfs_id  = global - canvas_id_offset;
                                let dfs_usize      = canvas_dfs_id as usize;
                                self.actor_virtual_selected_slot_idx = 0;
                                if self.ctrl_at_press {
                                    // Ctrl+クリック: マルチ選択トグル
                                    if self.selected_actor_dfs_ids.contains(&dfs_usize) {
                                        self.selected_actor_dfs_ids.retain(|&x| x != dfs_usize);
                                        if self.actor_virtual_selected_idx == Some(dfs_usize) {
                                            self.actor_virtual_selected_idx = self.selected_actor_dfs_ids.last().copied();
                                        }
                                    } else {
                                        self.selected_actor_dfs_ids.push(dfs_usize);
                                        self.actor_virtual_selected_idx = Some(dfs_usize);
                                    }
                                } else {
                                    // 通常クリック: 単一選択
                                    self.actor_virtual_selected_idx = Some(dfs_usize);
                                    self.selected_actor_dfs_ids     = vec![dfs_usize];
                                }
                                self.selected_instances.clear();
                                self.send_actor_components(canvas_dfs_id, 0);
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
                        self.send_selected();
                    }
                }

                // ── ドロップ処理（GPU サブミット後）─────────────
                if let Some((path, sx, sy)) = self.pending_drop.take() {
                    let world_pos = if !did_pick {
                        if let (Some(id_buf), Some(draw_ctx)) = (&self.id_buffer, &self.draw_ctx) {
                            let (wpos, _raw_id) = id_buf.read_pixel(&draw_ctx.device);
                            wpos
                        } else { None }
                    } else {
                        // ピック処理でバッファが読み出し済みのため別途取得できない。
                        // pending_drop を再キューイングして次フレームで処理する。
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
