use std::sync::Arc;
use std::path::Path;

use winit::application::ApplicationHandler;
use winit::event::{DeviceEvent, DeviceId, ElementState, WindowEvent};
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop};
use winit::keyboard::{KeyCode, PhysicalKey};
use winit::window::{Window, WindowId};

use crate::engine::core::clock::{Clock, FrameContext};
use crate::engine::core::input::Input;
use crate::engine::core::loader::load_model;
use crate::engine::core::renderer::Renderer;
use crate::engine::core::window::{create_window, WindowConfig};
use crate::engine::core::app_base::ipc::{IpcClient, IpcCommand, ToolMode};
use crate::engine::core::app_base::scene::Scene;
use crate::engine::methods::drawer::{
    DrawContext, CameraBuffer, CameraUniform,
    draw_model_indirect, draw_id_pass, draw_outline, draw_stencil_mask,
    extract_frustum_planes, IdBuffer, GizmoBatch, draw_gizmo_batch,
};
use crate::engine::methods::gizmo_interact::{
    GizmoDrag, GizmoPart, screen_to_ray, hit_test_gizmo, start_drag, update_drag,
    mat4x4_mul, mat4x4_inv,
};
use crate::engine::core::app_base::undo::{UndoHistory, TransformCommand};
use crate::engine::core::scripting::{ScriptingHost, ScriptComponent};
use crate::engine::structs::components::ModelComponent;
use crate::engine::structs::objects::{Actor, DebugCamera};
use crate::engine::structs::objects::camera::debug_camera::CameraInput;
use crate::engine::structs::tensor::Vector3;

// ============================================================
//  起動設定
// ============================================================

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum RuntimeMode {
    /// デバッグカメラ・エディタ埋め込み
    Edit,
    /// 通常ゲームプレイ・独立ウィンドウ
    Play,
}

pub struct LaunchArgs {
    pub parent_hwnd: Option<isize>,
    pub mode:        RuntimeMode,
    pub pipe_name:   Option<String>,
}

// ============================================================
//  定数（デモシーン用）
// ============================================================

const INSTANCE_DIM:     usize = 10;
const INSTANCE_SPACING: f32   = 3.0;

// ============================================================
//  App
// ============================================================

pub struct App {
    window:         Option<Arc<Window>>,
    renderer:       Option<Renderer>,
    input:          Input,
    cam_input:      CameraInput,
    camera:         DebugCamera,
    clock:          Clock,
    draw_ctx:       Option<DrawContext>,
    scene:          Option<Scene>,
    camera_buf:     Option<CameraBuffer>,
    scripting_host: Option<Arc<ScriptingHost>>,

    parent_hwnd: Option<isize>,
    mode:        RuntimeMode,
    ipc:         Option<IpcClient>,
    paused:      bool,

    /// RMB 押下時のスクリーン座標。カーソルロック解除後に復元する。
    cam_grab_screen_pos: Option<(i32, i32)>,

    /// エディタから PLAY_CLAMP:1 を受け取ったとき true。
    /// 毎フレーム ClipCursor を貼り直してカーソルをウィンドウ内に閉じ込める。
    play_clamp: bool,

    // ── ピッキング / ギズモ ──────────────────────────────────
    /// Actor 選択用 ID バッファ（Edit/Pause モードのみ使用）。
    id_buffer:         Option<IdBuffer>,
    /// 現在選択中のインスタンスインデックス（0-based）。
    selected_instance: Option<u32>,
    /// LMB クリック時のビューポートピクセル座標（次フレームで処理）。
    pending_pick:      Option<(u32, u32)>,
    /// 直前フレームのカーソル座標（ビューポートローカル）。
    last_cursor_pos:   Option<(f32, f32)>,
    /// ギズモ描画用の単位行列モデルバッファ。
    line_model_buf:    Option<(wgpu::Buffer, wgpu::BindGroup)>,
    /// 現在のエディタツールモード。
    tool_mode:         ToolMode,
    /// 進行中のギズモドラッグ状態。
    gizmo_drag:        Option<GizmoDrag>,
    /// マウスホバー中のギズモパーツ（ハイライト表示用）。
    hovered_gizmo_part: Option<GizmoPart>,
    /// Undo/Redo 履歴。
    undo_history:       UndoHistory,
    /// Ctrl キーが押されているか。
    ctrl_held:          bool,
    /// ギズモドラッグ開始時の子孫インスタンスの初期行列（index, start_mat）。
    drag_child_starts:  Vec<(u32, [[f32; 4]; 4])>,
}

impl App {
    pub fn new(args: LaunchArgs) -> Self {
        let ipc = args.pipe_name.as_deref()
            .and_then(|name| IpcClient::connect(name).ok());

        let scripting_host = match ScriptingHost::load(&ScriptingHost::resolve_dll_path()) {
            Ok(host) => {
                eprintln!("[SEED] ScriptingHost loaded");
                Some(host)
            }
            Err(e) => {
                eprintln!("[SEED] ScriptingHost load failed (scripting disabled): {e}");
                None
            }
        };

        Self {
            window:         None,
            renderer:       None,
            input:          Input::new(),
            cam_input:      CameraInput::default(),
            camera:         DebugCamera::default(),
            clock:          Clock::new(),
            draw_ctx:       None,
            scene:          None,
            camera_buf:     None,
            scripting_host,
            parent_hwnd: args.parent_hwnd,
            mode:        args.mode,
            ipc,
            paused: false,
            cam_grab_screen_pos: None,
            play_clamp: false,
            id_buffer:         None,
            selected_instance: None,
            pending_pick:      None,
            last_cursor_pos:   None,
            line_model_buf:    None,
            tool_mode:         ToolMode::Select,
            gizmo_drag:        None,
            hovered_gizmo_part: None,
            undo_history:       UndoHistory::new(),
            ctrl_held:          false,
            drag_child_starts:  Vec::new(),
        }
    }

    /// エントリポイント。EventLoop を生成して実行する。
    pub fn run(args: LaunchArgs) {
        let event_loop: EventLoop<()> =
            EventLoop::new().expect("Failed to create event loop");
        event_loop.set_control_flow(ControlFlow::Poll);
        let mut app = App::new(args);
        event_loop.run_app(&mut app).expect("Failed to run app");
    }

    fn is_embedded(&self) -> bool { self.parent_hwnd.is_some() }

    fn window_hwnd(&self) -> isize {
        #[cfg(target_os = "windows")]
        {
            use raw_window_handle::{HasWindowHandle, RawWindowHandle};
            if let Some(win) = &self.window {
                if let Ok(handle) = win.window_handle() {
                    if let RawWindowHandle::Win32(h) = handle.as_raw() {
                        return h.hwnd.get();
                    }
                }
            }
        }
        0
    }

    fn process_ipc(&mut self, event_loop: &ActiveEventLoop) {
        let Some(ipc) = &self.ipc else { return };
        while let Some(cmd) = ipc.try_recv() {
            match cmd {
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
                    if let Some(scene) = &mut self.scene {
                        self.undo_history.undo(scene);
                    }
                }
                IpcCommand::Redo => {
                    if let Some(scene) = &mut self.scene {
                        self.undo_history.redo(scene);
                    }
                }
                IpcCommand::Select(idx) => {
                    self.selected_instance = Some(idx);
                    // エディタからの選択はそのまま反映（SELECTED の送り返しは不要）
                }
                IpcCommand::Reparent { child, new_parent } => {
                    if let Some(scene) = &mut self.scene {
                        if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
                            if (child as usize) < mc.instance_meta.len() {
                                mc.instance_meta[child as usize].parent = new_parent;
                            }
                        }
                    }
                    self.send_hierarchy();
                }
                IpcCommand::Rename { idx, name } => {
                    if let Some(scene) = &mut self.scene {
                        if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
                            if let Some(meta) = mc.instance_meta.get_mut(idx as usize) {
                                meta.name = name;
                            }
                        }
                    }
                    self.send_hierarchy();
                }
            }
        }
    }

    /// ヒエラルキーを JSON にシリアライズしてエディタへ送信する。
    fn send_hierarchy(&self) {
        let Some(ipc)   = &self.ipc   else { return };
        let Some(scene) = &self.scene else { return };
        let Some(mc)    = scene.find_component::<ModelComponent>() else { return };

        let mut json = String::from("[");
        for (i, meta) in mc.instance_meta.iter().enumerate() {
            if i > 0 { json.push(','); }
            let parent_str = match meta.parent {
                Some(p) => p.to_string(),
                None    => "null".to_string(),
            };
            json.push_str(&format!(
                r#"{{"id":{},"name":{},"parent":{}}}"#,
                i,
                serde_json::to_string(&meta.name).unwrap_or_default(),
                parent_str,
            ));
        }
        json.push(']');
        ipc.send(&format!("HIERARCHY:{json}"));
    }

    /// 現在の選択インスタンスをエディタへ通知する。
    fn send_selected(&self) {
        let Some(ipc) = &self.ipc else { return };
        match self.selected_instance {
            Some(idx) => ipc.send(&format!("SELECTED:{idx}")),
            None      => ipc.send("SELECTED:-1"),
        }
    }

    /// デモシーンを構築する。
    /// 将来的にはシーンファイルのロードに置き換える。
    /// カーソル座標でギズモのヒットテストを行い、当たったパーツを返す。
    fn compute_gizmo_hover(&self, cx: f32, cy: f32) -> Option<GizmoPart> {
        if self.tool_mode == ToolMode::Select { return None; }
        let sel = self.selected_instance?;
        let mc  = self.scene.as_ref()?.find_component::<ModelComponent>()?;
        let mat = mc.instance_mats.get(sel as usize)?;
        let gizmo_pos = [mat[0][3], mat[1][3], mat[2][3]];

        let window_size = self.window.as_ref()?.inner_size();
        let cam_pos_v = self.camera.position();
        let cam_pos   = [cam_pos_v.x, cam_pos_v.y, cam_pos_v.z];

        let d    = [gizmo_pos[0]-cam_pos[0], gizmo_pos[1]-cam_pos[1], gizmo_pos[2]-cam_pos[2]];
        let dist = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(0.01);
        let half_fov = self.camera.base.projection.fov_y_rad * 0.5;
        let radius   = dist * half_fov.tan() * 0.233;

        let view = self.camera.view_matrix();
        let proj = self.camera.projection_matrix();
        let (ray_o, ray_d) = screen_to_ray(
            cx, cy,
            window_size.width as f32, window_size.height as f32,
            &view.data, &proj.data, cam_pos,
        );
        hit_test_gizmo(ray_o, ray_d, gizmo_pos, radius, self.tool_mode)
    }

    /// カーソル座標でギズモのヒットテストを行い、当たった場合は GizmoDrag を返す。
    fn try_gizmo_hit_and_start(&self, cx: f32, cy: f32) -> Option<GizmoDrag> {
        if self.tool_mode == ToolMode::Select { return None; }
        let sel = self.selected_instance?;
        let mc  = self.scene.as_ref()?.find_component::<ModelComponent>()?;
        let mat = *mc.instance_mats.get(sel as usize)?;
        let gizmo_pos = [mat[0][3], mat[1][3], mat[2][3]];

        let window_size = self.window.as_ref()?.inner_size();
        let vp_w = window_size.width  as f32;
        let vp_h = window_size.height as f32;

        let cam_pos_v = self.camera.position();
        let cam_pos   = [cam_pos_v.x, cam_pos_v.y, cam_pos_v.z];

        let d    = [gizmo_pos[0]-cam_pos[0], gizmo_pos[1]-cam_pos[1], gizmo_pos[2]-cam_pos[2]];
        let dist = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(0.01);
        let half_fov = self.camera.base.projection.fov_y_rad * 0.5;
        let radius   = dist * half_fov.tan() * 0.233;

        let view = self.camera.view_matrix();
        let proj = self.camera.projection_matrix();
        let (ray_o, ray_d) = screen_to_ray(cx, cy, vp_w, vp_h, &view.data, &proj.data, cam_pos);

        let part = hit_test_gizmo(ray_o, ray_d, gizmo_pos, radius, self.tool_mode)?;
        Some(start_drag(part, self.tool_mode, ray_o, ray_d, gizmo_pos, radius, mat))
    }

    fn build_demo_scene(ctx: &DrawContext, scripting_host: Option<&Arc<ScriptingHost>>) -> Scene {
        let mut scene = Scene::new("Demo");

        let model_path = Path::new("assets/models/BrainStem.glb");
        let model      = load_model(model_path)
            .unwrap_or_else(|e| panic!("BrainStem.glb のロード失敗: {e}"));
        let gpu_model       = ctx.upload_model(&model);
        let total           = INSTANCE_DIM.pow(3);
        let instanced_batch = ctx.create_instanced_batch(&model, total as u32);

        let mut instance_mats = Vec::with_capacity(total);
        let mut instance_meta = Vec::with_capacity(total);
        let mut idx = 0usize;
        for z in 0..INSTANCE_DIM {
            for y in 0..INSTANCE_DIM {
                for x in 0..INSTANCE_DIM {
                    let (tx, ty, tz) = (
                        x as f32 * INSTANCE_SPACING,
                        y as f32 * INSTANCE_SPACING,
                        z as f32 * INSTANCE_SPACING,
                    );
                    instance_mats.push([
                        [1.0, 0.0, 0.0, tx],
                        [0.0, 1.0, 0.0, ty],
                        [0.0, 0.0, 1.0, tz],
                        [0.0, 0.0, 0.0, 1.0f32],
                    ]);
                    instance_meta.push(crate::engine::structs::components::model_component::InstanceMeta::new(
                        format!("BrainStem_{idx}")
                    ));
                    idx += 1;
                }
            }
        }

        let mut actor = Actor::with_name("BrainStem");
        actor.add_component(ModelComponent {
            source_path: model_path.to_string_lossy().into_owned(),
            model,
            gpu_model,
            instanced_batch,
            instance_mats,
            instance_meta,
        });
        scene.add_actor(actor);

        if let Some(host) = scripting_host {
            if let Some(script) = ScriptComponent::new(Arc::clone(host), "TestRotator") {
                let mut script_actor = Actor::with_name("TestRotator");
                script_actor.add_component(script);
                scene.add_actor(script_actor);
                eprintln!("[SEED] TestRotator script component created");
            } else {
                eprintln!("[SEED] TestRotator: CreateComponent returned null");
            }
        }

        scene
    }
}

// ============================================================
//  ApplicationHandler
// ============================================================

impl ApplicationHandler for App {
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

        let center = (INSTANCE_DIM as f32 - 1.0) * INSTANCE_SPACING * 0.5;
        self.camera.base.transform.position = Vector3::new(center, center, -10.0);

        let ctx = DrawContext::new(
            renderer.device(),
            renderer.queue(),
            renderer.surface_format(),
            renderer.depth_format(),
        );
        eprintln!("[SEED] DrawContext created");

        let scene      = Self::build_demo_scene(&ctx, self.scripting_host.as_ref());
        let camera_buf = ctx.create_camera_buffer();
        let id_buffer  = IdBuffer::new(&ctx.device, size.width, size.height);
        let line_model_buf = ctx.create_identity_model_bg_for_unlit();
        eprintln!("[SEED] scene ready");

        if self.is_embedded() {
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
        self.renderer      = Some(renderer);
        self.window        = Some(window);
        self.clock         = Clock::new();

        let hwnd = self.window_hwnd();
        eprintln!("[SEED] sending READY:{hwnd}");
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("READY:{hwnd}"));
        }
        self.send_hierarchy();
        eprintln!("[SEED] resumed() done");
    }

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
                            if let Some(scene) = &mut self.scene {
                                self.undo_history.undo(scene);
                            }
                        }
                        KeyCode::KeyY if pressed && self.ctrl_held => {
                            if let Some(scene) = &mut self.scene {
                                self.undo_history.redo(scene);
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
                            // ギズモヒットを優先。外れた場合のみ ID ピック。
                            if let Some(drag) = self.try_gizmo_hit_and_start(cx, cy) {
                                // ドラッグ開始: 子孫インスタンスの初期行列を保存
                                if let Some(sel) = self.selected_instance {
                                    self.drag_child_starts = self.scene.as_ref()
                                        .and_then(|s| s.find_component::<ModelComponent>())
                                        .map(|mc| {
                                            mc.all_descendants(sel).into_iter()
                                                .filter_map(|c| mc.instance_mats.get(c as usize).map(|&m| (c, m)))
                                                .collect()
                                        })
                                        .unwrap_or_default();
                                }
                                self.gizmo_drag = Some(drag);
                            } else {
                                self.pending_pick = Some((cx as u32, cy as u32));
                            }
                        }
                    }
                    if !pressed {
                        // ドラッグで変化があれば Undo 履歴に積む
                        if let (Some(drag), Some(sel)) =
                            (&self.gizmo_drag, self.selected_instance)
                        {
                            let old_mat = drag.start_mat;
                            let new_mat = self.scene.as_ref()
                                .and_then(|s| s.find_component::<ModelComponent>())
                                .and_then(|mc| mc.instance_mats.get(sel as usize))
                                .copied();
                            if let Some(new_mat) = new_mat {
                                if new_mat != old_mat {
                                    self.undo_history.record(Box::new(TransformCommand {
                                        instance_idx: sel,
                                        old_mat,
                                        new_mat,
                                    }));
                                }
                            }
                            // 子孫インスタンスの変化も記録
                            let child_starts = std::mem::take(&mut self.drag_child_starts);
                            for (child_idx, child_start) in child_starts {
                                let child_new = self.scene.as_ref()
                                    .and_then(|s| s.find_component::<ModelComponent>())
                                    .and_then(|mc| mc.instance_mats.get(child_idx as usize))
                                    .copied();
                                if let Some(child_new_mat) = child_new {
                                    if child_new_mat != child_start {
                                        self.undo_history.record(Box::new(TransformCommand {
                                            instance_idx: child_idx,
                                            old_mat:      child_start,
                                            new_mat:      child_new_mat,
                                        }));
                                    }
                                }
                            }
                        } else {
                            self.drag_child_starts.clear();
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
                                self.cam_grab_screen_pos =
                                    camera_grab_start(self.window_hwnd());
                                window.set_cursor_visible(false);
                            } else {
                                window.set_cursor_visible(true);
                                if let Some((x, y)) = self.cam_grab_screen_pos.take() {
                                    camera_grab_end(x, y);
                                } else {
                                    camera_grab_end(0, 0);
                                }
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

                // ホバーパーツを更新（ドラッグ中はドラッグパーツを維持）
                self.hovered_gizmo_part = if let Some(drag) = &self.gizmo_drag {
                    Some(drag.part)
                } else {
                    self.compute_gizmo_hover(cx, cy)
                };

                // ギズモドラッグ中: 新しい変換行列を計算してインスタンスに適用する
                let new_mat_opt = if let Some(drag) = &self.gizmo_drag {
                    if let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) {
                        let cam_v  = self.camera.position();
                        let cam    = [cam_v.x, cam_v.y, cam_v.z];
                        let view   = self.camera.view_matrix();
                        let proj   = self.camera.projection_matrix();
                        let (ro, rd) = screen_to_ray(
                            cx, cy,
                            ws.width as f32, ws.height as f32,
                            &view.data, &proj.data, cam,
                        );
                        Some(update_drag(drag, ro, rd))
                    } else { None }
                } else { None };

                if let Some(new_mat) = new_mat_opt {
                    if let Some(sel) = self.selected_instance {
                        if let Some(scene) = &mut self.scene {
                            if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
                                // 選択インスタンスを更新
                                if let Some(m) = mc.instance_mats.get_mut(sel as usize) {
                                    *m = new_mat;
                                }
                                // 子孫を delta 伝播で追随させる
                                if !self.drag_child_starts.is_empty() {
                                    if let Some(drag) = &self.gizmo_drag {
                                        let delta = mat4x4_mul(new_mat, mat4x4_inv(drag.start_mat));
                                        for &(child_idx, child_start) in &self.drag_child_starts {
                                            if let Some(cm) = mc.instance_mats.get_mut(child_idx as usize) {
                                                *cm = mat4x4_mul(delta, child_start);
                                            }
                                        }
                                    }
                                }
                                mc.instanced_batch.mark_dirty();
                            }
                        }
                    }
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

                // Play クランプが有効な間は毎フレーム ClipCursor を再適用する。
                if self.play_clamp {
                    apply_window_clamp(self.window_hwnd());
                }

                // ── 時間 ──────────────────────────────────────
                let time_running = self.mode == RuntimeMode::Play && !self.paused;
                let ctx: FrameContext = self.clock.tick(time_running);
                let in_editor = self.mode == RuntimeMode::Edit || self.paused;

                if in_editor {
                    self.camera.update(&self.cam_input, ctx.delta_time);
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
                    let view      = self.camera.view_matrix();
                    let proj      = self.camera.projection_matrix();
                    let view_proj = proj * view;
                    let pos       = self.camera.position();

                    let res = window_size.map_or([1280.0, 720.0], |s| {
                        [s.width as f32, s.height as f32]
                    });
                    camera_buf.update(&queue, &CameraUniform {
                        view_proj:  view_proj.transpose().data,
                        view:       view.transpose().data,
                        position:   [pos.x, pos.y, pos.z],
                        _pad:       0.0,
                        resolution: res,
                        _pad2:      [0.0; 2],
                    });

                    let frustum_planes = extract_frustum_planes(&view_proj.data);
                    let camera_pos     = [pos.x, pos.y, pos.z];

                    if let Some(mc) = scene.find_component_mut::<ModelComponent>() {
                        mc.instanced_batch.update(
                            &queue, &mc.model, &mc.instance_mats,
                            &frustum_planes, camera_pos, self.clock.anim_time(),
                        );
                    }
                }

                // ── 選択インスタンスのワールド座標（ギズモ用）──
                let gizmo_pos = self.selected_instance.and_then(|sel| {
                    self.scene.as_ref()?
                        .find_component::<ModelComponent>()?
                        .instance_mats.get(sel as usize)
                        .map(|mat| [mat[0][3], mat[1][3], mat[2][3]])
                });

                // ピック要求を取り出す（描画ブロック内で使用）
                let pick_pos = self.pending_pick.take();
                let mut did_pick = false;

                if let (Some(renderer), Some(scene), Some(camera_buf), Some(draw_ctx)) =
                    (&mut self.renderer, &self.scene, &self.camera_buf, &self.draw_ctx)
                {
                    if let Some(mc) = scene.find_component::<ModelComponent>() {
                        match renderer.begin_frame() {
                            Ok(mut frame) => {
                                mc.instanced_batch.dispatch_skin(
                                    frame.encoder_mut(),
                                    &draw_ctx.pipelines.skin_compute,
                                );

                                // ギズモ GPU バッファ（レンダーパスの前に生成）
                                let show_gizmo_pre = self.mode == RuntimeMode::Edit || self.paused;
                                let gizmo_gpu_batch = if show_gizmo_pre
                                    && self.tool_mode != ToolMode::Select
                                {
                                    gizmo_pos.map(|pos| {
                                        // カメラ距離に比例したスクリーン固定サイズ
                                        // radius = dist * tan(fov_y/2) * NDC_fraction
                                        let cam_pos  = self.camera.position();
                                        let d = [pos[0]-cam_pos.x, pos[1]-cam_pos.y, pos[2]-cam_pos.z];
                                        let dist = (d[0]*d[0]+d[1]*d[1]+d[2]*d[2]).sqrt().max(0.01);
                                        let half_fov = self.camera.base.projection.fov_y_rad * 0.5;
                                        let radius = dist * half_fov.tan() * 0.233;

                                        let cam_pos_arr = [cam_pos.x, cam_pos.y, cam_pos.z];
                                        let hov  = self.hovered_gizmo_part;
                                        let drag_part = self.gizmo_drag.as_ref().map(|d| d.part);
                                        let mut batch = GizmoBatch::new();
                                        match self.tool_mode {
                                            ToolMode::Move   => batch.add_gizmo_translate(pos, radius, hov),
                                            ToolMode::Rotate => batch.add_gizmo_rotate(pos, radius, 64, cam_pos_arr, hov, drag_part),
                                            ToolMode::Scale  => batch.add_gizmo_scale(pos, radius, hov),
                                            ToolMode::Select => {}
                                        }
                                        batch.build(&draw_ctx.device)
                                    })
                                } else { None };

                                // ── メインレンダーパス ────────────────
                                {
                                    let mut pass = frame.begin_render_pass();
                                    draw_model_indirect(
                                        &mut pass, &mc.gpu_model, &mc.instanced_batch,
                                        &camera_buf.bind_group, &draw_ctx.pipelines,
                                    );

                                    // アウトライン（Edit/Pause + 選択中のみ）
                                    // 順序: stencil_mask(選択インスタンスに1を書く) → outline(0の箇所のみ描画)
                                    if in_editor {
                                        if let Some(sel) = self.selected_instance {
                                            draw_stencil_mask(
                                                &mut pass, &mc.gpu_model, &mc.instanced_batch,
                                                &camera_buf.bind_group, &draw_ctx.pipelines, sel,
                                            );
                                            draw_outline(
                                                &mut pass, &mc.gpu_model, &mc.instanced_batch,
                                                &camera_buf.bind_group, &draw_ctx.pipelines, sel,
                                            );
                                        }
                                    }

                                    // ギズモ（Edit/Pause + Move/Rotate/Scale モードのみ）
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

                                // ── ID パス（Edit/Pause のみ）──────────
                                if in_editor {
                                    if let Some(id_buf) = &self.id_buffer {
                                        {
                                            let mut id_pass = frame.begin_id_pass(&id_buf.view);
                                            draw_id_pass(
                                                &mut id_pass, &mc.gpu_model, &mc.instanced_batch,
                                                &camera_buf.bind_group, &draw_ctx.pipelines,
                                            );
                                        }
                                        if let Some((px, py)) = pick_pos {
                                            let px = px.min(id_buf.width.saturating_sub(1));
                                            let py = py.min(id_buf.height.saturating_sub(1));
                                            frame.schedule_id_copy(
                                                &id_buf.texture, px, py, &id_buf.readback_buf,
                                            );
                                            did_pick = true;
                                        }
                                    }
                                }

                                frame.finish();
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
                }

                // ── ピック結果の読み出し（GPU サブミット後）─────
                if did_pick {
                    if let (Some(id_buf), Some(draw_ctx)) = (&self.id_buffer, &self.draw_ctx) {
                        let raw = id_buf.read_pixel(&draw_ctx.device);
                        self.selected_instance = if raw > 0 { Some(raw - 1) } else { None };
                        eprintln!("[SEED] pick: raw={raw}, selected={:?}", self.selected_instance);
                        self.send_selected();
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

// ============================================================
//  カーソルロック / 復元（Windows のみ）
// ============================================================

/// RMB 押下時: カーソルをビューポート内に ClipCursor で閉じ込め、
/// 押下前のスクリーン座標を返す。
fn camera_grab_start(hwnd: isize) -> Option<(i32, i32)> {
    #[cfg(target_os = "windows")]
    unsafe {
        use windows_sys::Win32::Foundation::{POINT, RECT};
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            ClipCursor, GetCursorPos, GetWindowRect,
        };

        let mut pt = POINT { x: 0, y: 0 };
        if GetCursorPos(&mut pt) == 0 { return None; }

        // ビューポートウィンドウの画面矩形を取得して ClipCursor で閉じ込める。
        // これによりカーソルがビューポート外に出なくなり、
        // set_cursor_visible(false) が常に有効になる。
        let mut rect = RECT { left: 0, top: 0, right: 0, bottom: 0 };
        GetWindowRect(hwnd as _, &mut rect);
        ClipCursor(&rect);

        return Some((pt.x, pt.y));
    }
    #[cfg(not(target_os = "windows"))]
    let _ = hwnd;
    None
}

/// RMB リリース時: ClipCursor を解除してカーソルを元の座標へ戻す。
fn camera_grab_end(x: i32, y: i32) {
    #[cfg(target_os = "windows")]
    unsafe {
        use windows_sys::Win32::UI::WindowsAndMessaging::{ClipCursor, SetCursorPos};
        ClipCursor(core::ptr::null()); // クリップ解除
        SetCursorPos(x, y);
    }
    #[cfg(not(target_os = "windows"))]
    let _ = (x, y);
}

/// Play クランプ: 毎フレーム呼び出し、ウィンドウ矩形へ ClipCursor を再適用する。
fn apply_window_clamp(hwnd: isize) {
    #[cfg(target_os = "windows")]
    unsafe {
        use windows_sys::Win32::Foundation::RECT;
        use windows_sys::Win32::UI::WindowsAndMessaging::{ClipCursor, GetWindowRect};
        let mut rect = RECT { left: 0, top: 0, right: 0, bottom: 0 };
        GetWindowRect(hwnd as _, &mut rect);
        ClipCursor(&rect);
    }
    #[cfg(not(target_os = "windows"))]
    let _ = hwnd;
}

/// Play クランプ解除。
fn release_window_clamp() {
    #[cfg(target_os = "windows")]
    unsafe {
        windows_sys::Win32::UI::WindowsAndMessaging::ClipCursor(core::ptr::null());
    }
}
