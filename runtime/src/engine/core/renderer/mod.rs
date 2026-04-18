use std::sync::Arc;
use winit::dpi::PhysicalSize;
use winit::window::Window;

// ============================================================
//  深度テクスチャ
// ============================================================

pub const DEPTH_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Depth32Float;

struct DepthTexture {
    #[allow(dead_code)]
    texture: wgpu::Texture,
    view:    wgpu::TextureView,
}

impl DepthTexture {
    fn new(device: &wgpu::Device, width: u32, height: u32) -> Self {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Depth Texture"),
            size: wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          DEPTH_FORMAT,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                 | wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        Self { texture, view }
    }
}

// ============================================================
//  Renderer
// ============================================================

/// wgpu 描画コンテキスト一式。
///
/// `device` / `queue` は `Arc` で共有し、`DrawContext` と所有権なしに共用できる。
pub struct Renderer {
    surface:       wgpu::Surface<'static>,
    device:        Arc<wgpu::Device>,
    queue:         Arc<wgpu::Queue>,
    config:        wgpu::SurfaceConfiguration,
    size:          PhysicalSize<u32>,
    depth_texture: DepthTexture,
}

impl Renderer {
    pub fn new(window: Arc<Window>) -> Self {
        let size = window.inner_size();

        // Windows では Vulkan バックエンドのスワップチェーン後処理にバグがあるため
        // DX12 を優先する。他プラットフォームでは PRIMARY（Metal / Vulkan）を使う。
        let backends = if cfg!(target_os = "windows") {
            wgpu::Backends::DX12
        } else {
            wgpu::Backends::PRIMARY
        };
        let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor {
            backends,
            ..Default::default()
        });

        let surface = instance
            .create_surface(window.clone())
            .expect("Failed to create surface");

        let adapter = pollster::block_on(instance.request_adapter(
            &wgpu::RequestAdapterOptions {
                power_preference:       wgpu::PowerPreference::default(),
                compatible_surface:     Some(&surface),
                force_fallback_adapter: false,
            },
        ))
        .expect("Failed to find a suitable adapter");

        let (device, queue) = pollster::block_on(adapter.request_device(
            &wgpu::DeviceDescriptor {
                label:             None,
                required_features: wgpu::Features::empty(),
                required_limits:   wgpu::Limits::default(),
                memory_hints:      wgpu::MemoryHints::default(),
                ..Default::default()
            },
        ))
        .expect("Failed to create device");

        let device = Arc::new(device);
        let queue  = Arc::new(queue);

        let surface_caps   = surface.get_capabilities(&adapter);
        let surface_format = surface_caps
            .formats
            .iter()
            .copied()
            .find(|f| f.is_srgb())
            .unwrap_or(surface_caps.formats[0]);

        let config = wgpu::SurfaceConfiguration {
            usage:                        wgpu::TextureUsages::RENDER_ATTACHMENT,
            format:                       surface_format,
            width:                        size.width,
            height:                       size.height,
            present_mode:                 surface_caps.present_modes[0],
            alpha_mode:                   surface_caps.alpha_modes[0],
            view_formats:                 vec![],
            desired_maximum_frame_latency: 2,
        };
        surface.configure(&device, &config);

        let depth_texture = DepthTexture::new(&device, size.width, size.height);

        Self { surface, device, queue, config, size, depth_texture }
    }

    // ── アクセサ ────────────────────────────────────────────────

    /// `wgpu::Device` の Arc クローンを返す。
    pub fn device(&self) -> Arc<wgpu::Device> { Arc::clone(&self.device) }

    /// `wgpu::Queue` の Arc クローンを返す。
    pub fn queue(&self) -> Arc<wgpu::Queue> { Arc::clone(&self.queue) }

    /// スワップチェーンのピクセルフォーマットを返す。
    pub fn surface_format(&self) -> wgpu::TextureFormat { self.config.format }

    /// 深度バッファのフォーマットを返す。
    pub fn depth_format(&self) -> wgpu::TextureFormat { DEPTH_FORMAT }

    // ── リサイズ ────────────────────────────────────────────────

    pub fn resize(&mut self, new_size: PhysicalSize<u32>) {
        if new_size.width > 0 && new_size.height > 0 {
            self.size = new_size;
            self.config.width  = new_size.width;
            self.config.height = new_size.height;
            self.surface.configure(&self.device, &self.config);
            self.depth_texture = DepthTexture::new(&self.device, new_size.width, new_size.height);
        }
    }

    // ── 描画 ────────────────────────────────────────────────────

    /// フレームの描画を開始し、`RenderFrame` を返す。
    ///
    /// 呼び出し元は `RenderFrame::begin_render_pass()` でレンダーパスを取得し、
    /// 描画コマンドを記録したあと `RenderFrame::finish()` で GPU にサブミットする。
    ///
    /// # 戻り値
    /// - `Ok(RenderFrame)`: 成功
    /// - `Err(SurfaceError)`: スワップチェーン取得失敗
    ///
    /// # 使用例
    /// ```rust
    /// let mut frame = renderer.begin_frame()?;
    /// {
    ///     let mut pass = frame.begin_render_pass();
    ///     draw_model(&mut pass, &gpu_model, &model, ...);
    /// }
    /// frame.finish();
    /// ```
    pub fn begin_frame(&mut self) -> Result<RenderFrame<'_>, wgpu::SurfaceError> {
        let output     = self.surface.get_current_texture()?;
        let color_view = output.texture.create_view(&wgpu::TextureViewDescriptor::default());
        let encoder    = self.device.create_command_encoder(
            &wgpu::CommandEncoderDescriptor { label: Some("Render Encoder") },
        );
        Ok(RenderFrame {
            output,
            encoder,
            color_view,
            depth_view: &self.depth_texture.view,
            queue:      &self.queue,
        })
    }
}

// ============================================================
//  RenderFrame — 1 フレーム分の描画リソース
// ============================================================

/// `Renderer::begin_frame()` が返すフレームハンドル。
///
/// `begin_render_pass()` でレンダーパスを開き、描画コマンドを積んだあと、
/// スコープを外れてレンダーパスを閉じてから `finish()` でサブミットする。
pub struct RenderFrame<'r> {
    output:     wgpu::SurfaceTexture,
    encoder:    wgpu::CommandEncoder,
    color_view: wgpu::TextureView,
    depth_view: &'r wgpu::TextureView,
    queue:      &'r wgpu::Queue,
}

impl<'r> RenderFrame<'r> {
    /// クリア付きのメインレンダーパスを開始する。
    ///
    /// 返値のレンダーパスをドロップしてから `finish()` を呼ぶこと。
    pub fn begin_render_pass<'f>(&'f mut self) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Main Render Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           &self.color_view,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(wgpu::Color { r: 0.1, g: 0.1, b: 0.1, a: 1.0 }),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(1.0),
                    store: wgpu::StoreOp::Store,
                }),
                stencil_ops: None,
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// コマンドを GPU にサブミットしてフレームを表示する。
    pub fn finish(self) {
        self.queue.submit(std::iter::once(self.encoder.finish()));
        self.output.present();
    }
}
