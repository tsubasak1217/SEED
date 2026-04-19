use std::sync::Arc;
use winit::dpi::PhysicalSize;
use winit::window::Window;

// ============================================================
//  深度テクスチャ
// ============================================================

// Depth24PlusStencil8: ステンシルバッファを使用するため結合フォーマットを選択。
// Hi-Z 深度サンプリング用には DepthOnly ビューを別途作成する。
pub const DEPTH_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Depth24PlusStencil8;

struct DepthTexture {
    #[allow(dead_code)]
    texture:         wgpu::Texture,
    /// レンダーアタッチメント用（All aspect: depth+stencil 両方）
    view:            wgpu::TextureView,
    /// Hi-Z テクスチャサンプリング用（DepthOnly aspect）
    depth_only_view: wgpu::TextureView,
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
        // All aspect: レンダーパスで depth+stencil を同時に操作するために使用
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        // DepthOnly aspect: Hi-Z コンピュートシェーダでの texture_depth_2d サンプリング用
        let depth_only_view = texture.create_view(&wgpu::TextureViewDescriptor {
            aspect: wgpu::TextureAspect::DepthOnly,
            ..Default::default()
        });
        Self { texture, view, depth_only_view }
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

        // Windows は DX12 を使用する。
        // NvOptimusEnablement シンボルエクスポートにより Optimus 環境でも
        // DX12 アダプター列挙に dGPU が現れる。
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

        let adapter = Self::select_adapter(&instance, backends, &surface);
        eprintln!("[SEED] GPU adapter: {} ({:?})",
            adapter.get_info().name, adapter.get_info().device_type);

        let (device, queue) = pollster::block_on(adapter.request_device(
            &wgpu::DeviceDescriptor {
                label:             None,
                required_features: wgpu::Features::MULTI_DRAW_INDIRECT
                                 | wgpu::Features::INDIRECT_FIRST_INSTANCE,
                required_limits:   wgpu::Limits {
                    max_storage_buffers_per_shader_stage: 12,
                    ..wgpu::Limits::default()
                },
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

    // ── アダプター選択 ──────────────────────────────────────────

    /// 利用可能な GPU アダプターを列挙し、最適なものを選ぶ。
    ///
    /// 優先順位: DiscreteGpu > IntegratedGpu > その他
    /// サーフェスと互換性のないアダプターは除外する。
    fn select_adapter(
        instance: &wgpu::Instance,
        backends: wgpu::Backends,
        surface:  &wgpu::Surface<'_>,
    ) -> wgpu::Adapter {
        use wgpu::DeviceType;

        // DiscreteGpu を最優先にスコアリングする。
        fn adapter_score(info: &wgpu::AdapterInfo) -> u8 {
            match info.device_type {
                DeviceType::DiscreteGpu   => 3,
                DeviceType::IntegratedGpu => 2,
                DeviceType::VirtualGpu    => 1,
                _                         => 0,
            }
        }

        let mut adapters: Vec<wgpu::Adapter> = instance.enumerate_adapters(backends);

        eprintln!("[SEED] --- GPU adapter list ({} found) ---", adapters.len());
        for a in &adapters {
            let i = a.get_info();
            eprintln!("[SEED]   {:?} | {:?} | {} | surface_ok={}",
                i.backend, i.device_type, i.name, a.is_surface_supported(surface));
        }

        adapters.sort_by_key(|a| core::cmp::Reverse(adapter_score(&a.get_info())));


        adapters.into_iter().next().unwrap_or_else(|| {
            eprintln!("[SEED] enumerate_adapters returned empty, falling back to request_adapter");
            pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions {
                power_preference:       wgpu::PowerPreference::HighPerformance,
                compatible_surface:     Some(surface),
                force_fallback_adapter: false,
            }))
            .expect("Failed to find a suitable GPU adapter")
        })
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
    /// 深度テクスチャビューへの参照を返す（Hi-Z ピラミッド生成用、DepthOnly aspect）。
    pub fn depth_view(&self) -> &wgpu::TextureView { &self.depth_texture.depth_only_view }

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
            depth_view:      &self.depth_texture.view,
            depth_only_view: &self.depth_texture.depth_only_view,
            queue:           &self.queue,
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
    output:          wgpu::SurfaceTexture,
    encoder:         wgpu::CommandEncoder,
    color_view:      wgpu::TextureView,
    /// All aspect: レンダーアタッチメント（depth+stencil 操作）用
    depth_view:      &'r wgpu::TextureView,
    /// DepthOnly aspect: Hi-Z テクスチャサンプリング用
    depth_only_view: &'r wgpu::TextureView,
    queue:           &'r wgpu::Queue,
}

impl<'r> RenderFrame<'r> {
    /// コマンドエンコーダへの可変参照を返す（Hi-Z compute pass 等に使用）。
    pub fn encoder_mut(&mut self) -> &mut wgpu::CommandEncoder { &mut self.encoder }

    /// 深度バッファビューへの参照を返す（Hi-Z ピラミッド生成用、DepthOnly aspect）。
    pub fn depth_view(&self) -> &wgpu::TextureView { self.depth_only_view }

    // ── 深度プリパス（カラー出力なし、深度クリアあり）────────────

    /// 深度のみ書き込むプリパスを開始する。
    ///
    /// Hi-Z ピラミッド生成の前に呼び出し、終了後に `encoder_mut()` 経由で
    /// `build_pyramid` を呼ぶこと。
    pub fn begin_depth_prepass<'f>(&'f mut self) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label:             Some("Depth Prepass"),
            color_attachments: &[],  // カラー出力なし
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(1.0),
                    store: wgpu::StoreOp::Store,
                }),
                // 深度プリパスではステンシルを使わない
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(0),
                    store: wgpu::StoreOp::Discard,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    // ── メインレンダーパス（深度プリパスの結果を保持）────────────

    /// 深度プリパスの結果を保持したままメインレンダーパスを開始する。
    ///
    /// 深度バッファは `LoadOp::Load`（プリパスの深度を引き継ぎ）。
    /// Early-Z により、深度プリパスで隠れたフラグメントはシェーダー実行をスキップする。
    pub fn begin_render_pass_with_prepass<'f>(&'f mut self) -> wgpu::RenderPass<'f>
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
                    load:  wgpu::LoadOp::Load,   // プリパスの深度を引き継ぐ
                    store: wgpu::StoreOp::Store,
                }),
                // ステンシルをクリアして、このパスで描画された全ピクセルに 1 を書き込む
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(0),
                    store: wgpu::StoreOp::Store,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

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
                // ステンシルを 0 にクリア。draw_model_indirect が 1 を書き込み、
                // draw_outline が 0 の箇所（シルエット外側）のみ描画する。
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(0),
                    store: wgpu::StoreOp::Store,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// フレーム先頭でバッファ全体を 0 クリアする（draw_count リセット用）。
    pub fn clear_buffer(&mut self, buf: &wgpu::Buffer) {
        self.encoder.clear_buffer(buf, 0, None);
    }

    /// コンピュートパスを開始する。
    ///
    /// 返値のパスをドロップしてから次の操作（レンダーパス等）を行うこと。
    pub fn begin_compute_pass<'f>(&'f mut self) -> wgpu::ComputePass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label:            Some("Cull Compute Pass"),
            timestamp_writes: None,
        })
    }

    /// ID バッファパスを開始する（メインレンダーパスの後に呼ぶ）。
    ///
    /// - `id_view`  : R32Uint テクスチャビュー（クリアして書き込む）
    /// - 深度は Load して参照するのみ（write なし）
    pub fn begin_id_pass<'f>(
        &'f mut self,
        id_view: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("ID Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           id_view,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Discard,
                }),
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Discard,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// 1 ピクセル分を `readback_buf` へコピーするコマンドを積む。
    ///
    /// `frame.finish()` → GPU サブミット後に `map_async` + `poll(Wait)` で読み出す。
    pub fn schedule_id_copy(
        &mut self,
        src_texture: &wgpu::Texture,
        x:           u32,
        y:           u32,
        readback_buf: &wgpu::Buffer,
    ) {
        self.encoder.copy_texture_to_buffer(
            wgpu::ImageCopyTexture {
                texture:   src_texture,
                mip_level: 0,
                origin:    wgpu::Origin3d { x, y, z: 0 },
                aspect:    wgpu::TextureAspect::All,
            },
            wgpu::ImageCopyBuffer {
                buffer: readback_buf,
                layout: wgpu::ImageDataLayout {
                    offset:         0,
                    bytes_per_row:  Some(256),  // COPY_BYTES_PER_ROW_ALIGNMENT
                    rows_per_image: None,
                },
            },
            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
        );
    }

    /// コマンドを GPU にサブミットしてフレームを表示する。
    pub fn finish(self) {
        self.queue.submit(std::iter::once(self.encoder.finish()));
        self.output.present();
    }
}
