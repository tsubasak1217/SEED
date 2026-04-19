use std::sync::Arc;
use std::path::Path;

use winit::application::ApplicationHandler;
use winit::event::{DeviceEvent, DeviceId, ElementState, WindowEvent};
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop};
use winit::keyboard::PhysicalKey;
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
    extract_frustum_planes, IdBuffer, LineBatch, draw_gizmo_batch,
};
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
            }
        }
    }

    /// デモシーンを構築する。
    /// 将来的にはシーンファイルのロードに置き換える。
    fn build_demo_scene(ctx: &DrawContext, scripting_host: Option<&Arc<ScriptingHost>>) -> Scene {
        let mut scene = Scene::new("Demo");

        let model_path = Path::new("assets/models/BrainStem.glb");
        let model      = load_model(model_path)
            .unwrap_or_else(|e| panic!("BrainStem.glb のロード失敗: {e}"));
        let gpu_model       = ctx.upload_model(&model);
        let total           = INSTANCE_DIM.pow(3);
        let instanced_batch = ctx.create_instanced_batch(&model, total as u32);

        let mut instance_mats = Vec::with_capacity(total);
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
                if let PhysicalKey::Code(key) = event.physical_key {
                    self.input.process_key(key, event.state == ElementState::Pressed);
                }
            }
            WindowEvent::MouseInput { button, state, .. } => {
                let pressed = state == ElementState::Pressed;
                self.input.process_mouse_button(button, pressed);

                // LMB: Edit/Pause モードでインスタンス選択（RMB カメラ操作中は無効）
                if button == winit::event::MouseButton::Left && pressed
                    && (self.mode == RuntimeMode::Edit || self.paused)
                    && !self.cam_input.rmb
                {
                    if let Some((cx, cy)) = self.last_cursor_pos {
                        self.pending_pick = Some((cx as u32, cy as u32));
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
                self.input.process_cursor_moved(position.x as f32, position.y as f32);
                self.last_cursor_pos = Some((position.x as f32, position.y as f32));
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
                let queue = self.draw_ctx.as_ref().map(|c| c.queue.clone());

                if let (Some(scene), Some(camera_buf), Some(queue)) =
                    (&mut self.scene, &self.camera_buf, queue)
                {
                    let view      = self.camera.view_matrix();
                    let proj      = self.camera.projection_matrix();
                    let view_proj = proj * view;
                    let pos       = self.camera.position();

                    camera_buf.update(&queue, &CameraUniform {
                        view_proj: view_proj.transpose().data,
                        view:      view.transpose().data,
                        position:  [pos.x, pos.y, pos.z],
                        _pad:      0.0,
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

                // ── GPU 描画 ──────────────────────────────────
                let window_size = self.window.as_ref().map(|w| w.inner_size());

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
                                        let mut batch = LineBatch::new();
                                        match self.tool_mode {
                                            ToolMode::Move   => batch.add_gizmo_translate(pos, 1.5),
                                            ToolMode::Rotate => batch.add_gizmo_rotate(pos, 1.2, 32),
                                            ToolMode::Scale  => batch.add_gizmo_scale(pos, 1.5),
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
