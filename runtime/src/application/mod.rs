use std::sync::Arc;
use std::time::Instant;
use std::path::Path;
use winit::application::ApplicationHandler;
use winit::event::{DeviceEvent, DeviceId, ElementState, WindowEvent};
use winit::event_loop::ActiveEventLoop;
use winit::keyboard::PhysicalKey;
use winit::window::{Window, WindowId};

use crate::engine::core::input::Input;
use crate::engine::core::loader::load_model;
use crate::engine::core::renderer::Renderer;
use crate::engine::core::window::{create_window, WindowConfig};
use crate::engine::structs::objects::DebugCamera;
use crate::engine::structs::tensor::Vector3;
use crate::engine::methods::drawer::{
    DrawContext, GpuModel, InstancedModelBatch, CameraBuffer,
    CameraUniform,
    draw_model_indirect,
    extract_frustum_planes,
};
use crate::engine::core::loader::model::Model;

// ============================================================
//  定数
// ============================================================

/// インスタンス数（各次元 10、合計 10³ = 1000）
const INSTANCE_DIM: usize = 10;
/// インスタンス間隔（ユニット）
const INSTANCE_SPACING: f32 = 3.0;

// ============================================================
//  シーンリソース（GPU 初期化後に生成）
// ============================================================

struct GpuScene {
    model:           Model,
    gpu_model:       GpuModel,
    /// 1000 インスタンス分を 1 つのバッチにまとめたストレージバッファ群
    instanced_batch: InstancedModelBatch,
    /// 各インスタンスのルート変換行列（行優先、列ベクトル規約）
    instance_mats:   Vec<[[f32; 4]; 4]>,
    camera_buf:      CameraBuffer,
}

// ============================================================
//  App
// ============================================================

pub struct App {
    window:      Option<Arc<Window>>,
    renderer:    Option<Renderer>,
    input:       Input,
    camera:      DebugCamera,
    last_frame:  Instant,
    parent_hwnd: Option<isize>,
    draw_ctx:    Option<DrawContext>,
    scene:       Option<GpuScene>,
    /// グローバルアニメーション経過時間（秒）
    anim_time:   f32,
}

impl App {
    pub fn new(parent_hwnd: Option<isize>) -> Self {
        Self {
            window:      None,
            renderer:    None,
            input:       Input::new(),
            camera:      DebugCamera::default(),
            last_frame:  Instant::now(),
            parent_hwnd,
            draw_ctx:    None,
            scene:       None,
            anim_time:   0.0,
        }
    }

    fn is_embedded(&self) -> bool { self.parent_hwnd.is_some() }
}

// ============================================================
//  ApplicationHandler
// ============================================================

impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        let config = WindowConfig {
            parent_hwnd: self.parent_hwnd,
            ..WindowConfig::default()
        };
        let window       = Arc::new(create_window(event_loop, &config));
        let mut renderer = Renderer::new(window.clone());

        let size = window.inner_size();
        self.camera.set_aspect_ratio(size.width, size.height);

        // カメラを全インスタンスが見渡せる位置に配置
        // グリッド中心は ((DIM-1)/2 * spacing) ≈ 13.5
        let center = (INSTANCE_DIM as f32 - 1.0) * INSTANCE_SPACING * 0.5;
        self.camera.base.transform.position = Vector3::new(center, center, -10.0);

        // ── DrawContext ─────────────────────────────────────
        let ctx = DrawContext::new(
            renderer.device(),
            renderer.queue(),
            renderer.surface_format(),
            renderer.depth_format(),
        );

        // ── モデルロード ──────────────────────────────────────
        let model_path = Path::new("assets/models/BrainStem.glb");
        let model = load_model(model_path)
            .unwrap_or_else(|e| panic!("BrainStem.glb のロード失敗: {e}"));

        let gpu_model  = ctx.upload_model(&model);
        let camera_buf = ctx.create_camera_buffer();

        // ── 1000 インスタンスのルート変換行列を生成（10×10×10 グリッド）──
        let total = INSTANCE_DIM.pow(3);
        let mut instance_mats = Vec::with_capacity(total);

        for z in 0..INSTANCE_DIM {
            for y in 0..INSTANCE_DIM {
                for x in 0..INSTANCE_DIM {
                    let tx = x as f32 * INSTANCE_SPACING;
                    let ty = y as f32 * INSTANCE_SPACING;
                    let tz = z as f32 * INSTANCE_SPACING;

                    // 平行移動行列（行優先、列ベクトル規約）
                    let mat: [[f32; 4]; 4] = [
                        [1.0, 0.0, 0.0, tx],
                        [0.0, 1.0, 0.0, ty],
                        [0.0, 0.0, 1.0, tz],
                        [0.0, 0.0, 0.0, 1.0],
                    ];
                    instance_mats.push(mat);
                }
            }
        }

        // ── インスタンスバッチを 1 つ生成（ノードごとのストレージバッファ）──
        let instanced_batch = ctx.create_instanced_batch(&model, total as u32);

        let scene = GpuScene {
            model,
            gpu_model,
            instanced_batch,
            instance_mats,
            camera_buf,
        };

        if self.is_embedded() {
            window.set_visible(true);
            window.request_redraw();
        } else {
            if let Ok(frame) = renderer.begin_frame() { frame.finish(); }
            window.set_visible(true);
        }

        self.draw_ctx   = Some(ctx);
        self.scene      = Some(scene);
        self.renderer   = Some(renderer);
        self.window     = Some(window);
        self.last_frame = Instant::now();
    }

    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        _id: WindowId,
        event: WindowEvent,
    ) {
        match event {
            WindowEvent::CloseRequested if !self.is_embedded() => {
                event_loop.exit();
            }

            WindowEvent::Resized(new_size) => {
                if let Some(renderer) = &mut self.renderer {
                    renderer.resize(new_size);
                }
                self.camera.set_aspect_ratio(new_size.width, new_size.height);
            }

            WindowEvent::KeyboardInput { event, .. } => {
                if let PhysicalKey::Code(key_code) = event.physical_key {
                    self.input.process_key(key_code, event.state == ElementState::Pressed);
                }
            }

            WindowEvent::MouseInput { button, state, .. } => {
                self.input.process_mouse_button(button, state == ElementState::Pressed);
            }

            WindowEvent::CursorMoved { position, .. } => {
                self.input.process_cursor_moved(position.x as f32, position.y as f32);
            }

            WindowEvent::MouseWheel { delta, .. } => {
                self.input.process_scroll(&delta);
            }

            // ─── 描画 ─────────────────────────────────────────────
            WindowEvent::RedrawRequested => {
                let now        = Instant::now();
                let delta_time = now.duration_since(self.last_frame).as_secs_f32();
                self.last_frame = now;
                self.anim_time += delta_time;

                self.camera.update(&self.input, delta_time);

                // ── GPU バッファ更新（レンダーパスの外で行う）──────
                let queue = self.draw_ctx.as_ref().map(|c| c.queue.clone());

                if let (Some(scene), Some(queue)) = (self.scene.as_mut(), queue) {
                    // カメラ uniform（行優先 → 列優先変換のため転置）
                    let view      = self.camera.view_matrix();
                    let proj      = self.camera.projection_matrix();
                    let view_proj = proj * view;
                    let pos       = self.camera.position();
                    scene.camera_buf.update(&queue, &CameraUniform {
                        view_proj: view_proj.transpose().data,
                        view:      view.transpose().data,
                        position:  [pos.x, pos.y, pos.z],
                        _pad:      0.0,
                    });

                    // 視錐台平面を VP 行列から抽出
                    let frustum_planes = extract_frustum_planes(&view_proj.data);
                    let camera_pos = [pos.x, pos.y, pos.z];

                    // モデル行列更新 + スキン anim_times 更新
                    scene.instanced_batch.update(
                        &queue,
                        &scene.model,
                        &scene.instance_mats,
                        &frustum_planes,
                        camera_pos,
                        self.anim_time,
                    );
                }

                // ── レンダリング ──────────────────────────────────
                let window_size = self.window.as_ref().map(|w| w.inner_size());

                if let (Some(renderer), Some(scene), Some(ctx)) =
                    (&mut self.renderer, &self.scene, &self.draw_ctx)
                {
                    match renderer.begin_frame() {
                        Ok(mut frame) => {
                            // ── GPU スキニング コンピュートシェーダ ──
                            // レンダーパスより前にエンコーダに積む
                            scene.instanced_batch.dispatch_skin(
                                frame.encoder_mut(),
                                &ctx.pipelines.skin_compute,
                            );

                            // ── メインレンダーパス ──────────────────
                            {
                                let mut pass = frame.begin_render_pass();
                                draw_model_indirect(
                                    &mut pass,
                                    &scene.gpu_model,
                                    &scene.instanced_batch,
                                    &scene.camera_buf.bind_group,
                                    &ctx.pipelines,
                                );
                            }

                            frame.finish();
                        }
                        Err(wgpu::SurfaceError::Lost) => {
                            if let Some(size) = window_size {
                                renderer.resize(size);
                            }
                        }
                        Err(wgpu::SurfaceError::OutOfMemory) => {
                            event_loop.exit();
                        }
                        Err(e) => eprintln!("Render error: {:?}", e),
                    }
                }

                self.input.end_frame();

                if let Some(window) = &self.window {
                    window.request_redraw();
                }
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
        }
    }
}
