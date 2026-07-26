// ============================================================
//  font/icon_overlay.rs — アイコンオーバーレイレンダラー
//
//  選択アクターのワールド座標に location.png を表示する。
//  ピンの先端（底辺中央）が選択位置に対応し、アイコンは上方向に展開する。
// ============================================================

use wgpu::util::DeviceExt;

const ICON_SIZE_PX: f32 = 32.0;

static ICON_BYTES: &[u8] =
    include_bytes!("../../../../../editor/resources/icons/viewport/location.png");

// ── 頂点型 ────────────────────────────────────────────────────

/// アイコンオーバーレイのクワッド描画に使う頂点（NDC座標とUV）。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct IconOverlayVertex {
    pub position: [f32; 2],
    pub uv: [f32; 2],
}

// ── GpuIconOverlayBatch ──────────────────────────────────────

/// `IconOverlay::build` が構築する、GPU に転送済みのアイコンオーバーレイ描画データ。
pub struct GpuIconOverlayBatch {
    pub vertex_buf: wgpu::Buffer,
    pub vertex_count: u32,
}

// ── IconOverlay ───────────────────────────────────────────────

/// 選択アクターのワールド座標に location.png アイコンを表示するレンダラー。
/// ピンの先端（底辺中央）が選択位置に対応する。
pub struct IconOverlay {
    pipeline: wgpu::RenderPipeline,
    bind_group: wgpu::BindGroup,
}

impl IconOverlay {
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        surface_format: wgpu::TextureFormat,
        depth_format: wgpu::TextureFormat,
    ) -> Self {
        // PNG デコード
        let img = image::load_from_memory(ICON_BYTES)
            .expect("icon_overlay: failed to load location.png")
            .to_rgba8();
        let (img_w, img_h) = img.dimensions();
        let rgba = img.into_raw();

        // テクスチャ作成
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Icon Overlay Texture"),
            size: wgpu::Extent3d {
                width: img_w,
                height: img_h,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8UnormSrgb,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        queue.write_texture(
            texture.as_image_copy(),
            &rgba,
            wgpu::ImageDataLayout {
                offset: 0,
                bytes_per_row: Some(4 * img_w),
                rows_per_image: Some(img_h),
            },
            wgpu::Extent3d {
                width: img_w,
                height: img_h,
                depth_or_array_layers: 1,
            },
        );
        let texture_view = texture.create_view(&Default::default());

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("Icon Overlay Sampler"),
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            mipmap_filter: wgpu::FilterMode::Nearest,
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            ..Default::default()
        });

        // バインドグループレイアウト
        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Icon Overlay BGL"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        multisampled: false,
                        view_dimension: wgpu::TextureViewDimension::D2,
                        sample_type: wgpu::TextureSampleType::Float { filterable: true },
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
            ],
        });

        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Icon Overlay BG"),
            layout: &bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(&texture_view),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Sampler(&sampler),
                },
            ],
        });

        // シェーダー
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("Icon Overlay Shader"),
            source: wgpu::ShaderSource::Wgsl(
                include_str!("../renderer/shaders/icon_overlay.wgsl").into(),
            ),
        });

        // パイプラインレイアウト
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("Icon Overlay Layout"),
            bind_group_layouts: &[&bgl],
            push_constant_ranges: &[],
        });

        let stride = std::mem::size_of::<IconOverlayVertex>() as u64;
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("Icon Overlay Pipeline"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                buffers: &[wgpu::VertexBufferLayout {
                    array_stride: stride,
                    step_mode: wgpu::VertexStepMode::Vertex,
                    attributes: &[
                        wgpu::VertexAttribute {
                            format: wgpu::VertexFormat::Float32x2,
                            offset: 0,
                            shader_location: 0,
                        },
                        wgpu::VertexAttribute {
                            format: wgpu::VertexFormat::Float32x2,
                            offset: 8,
                            shader_location: 1,
                        },
                    ],
                }],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("fs_main"),
                targets: &[Some(wgpu::ColorTargetState {
                    format: surface_format,
                    blend: Some(wgpu::BlendState::ALPHA_BLENDING),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology: wgpu::PrimitiveTopology::TriangleList,
                cull_mode: None,
                ..Default::default()
            },
            // 深度書き込みなし・テストなし（常に最前面に表示）
            depth_stencil: Some(wgpu::DepthStencilState {
                format: depth_format,
                depth_write_enabled: false,
                depth_compare: wgpu::CompareFunction::Always,
                stencil: wgpu::StencilState::default(),
                bias: wgpu::DepthBiasState::default(),
            }),
            multisample: wgpu::MultisampleState::default(),
            multiview: None,
            cache: None,
        });

        Self {
            pipeline,
            bind_group,
        }
    }

    /// 選択インスタンスのスクリーン座標からバッチを構築する。
    ///
    /// `positions_screen`: `world_to_screen` で得たピクセル座標のリスト。
    /// ピンの先端（底辺中央）を各座標に合わせ、アイコンは上方向に展開する。
    pub fn build(
        positions_screen: &[(f32, f32)],
        vp_w: f32,
        vp_h: f32,
        device: &wgpu::Device,
    ) -> Option<GpuIconOverlayBatch> {
        if positions_screen.is_empty() {
            return None;
        }

        // NDC でのアイコン半幅・高さ
        let hw = (ICON_SIZE_PX * 0.5) / vp_w * 2.0;
        let hh = ICON_SIZE_PX / vp_h * 2.0;

        let mut verts: Vec<IconOverlayVertex> = Vec::with_capacity(positions_screen.len() * 6);

        for &(sx, sy) in positions_screen {
            // スクリーン座標 → NDC（ピン先端 = 底辺中央）
            let cx = sx / vp_w * 2.0 - 1.0;
            let cy = 1.0 - sy / vp_h * 2.0;

            let tl = [cx - hw, cy + hh];
            let tr = [cx + hw, cy + hh];
            let bl = [cx - hw, cy];
            let br = [cx + hw, cy];

            // 三角形 1: TL → TR → BR
            verts.push(IconOverlayVertex {
                position: tl,
                uv: [0.0, 0.0],
            });
            verts.push(IconOverlayVertex {
                position: tr,
                uv: [1.0, 0.0],
            });
            verts.push(IconOverlayVertex {
                position: br,
                uv: [1.0, 1.0],
            });
            // 三角形 2: TL → BR → BL
            verts.push(IconOverlayVertex {
                position: tl,
                uv: [0.0, 0.0],
            });
            verts.push(IconOverlayVertex {
                position: br,
                uv: [1.0, 1.0],
            });
            verts.push(IconOverlayVertex {
                position: bl,
                uv: [0.0, 1.0],
            });
        }

        let vertex_count = verts.len() as u32;
        let vertex_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("Icon Overlay Vertex Buffer"),
            contents: bytemuck::cast_slice(&verts),
            usage: wgpu::BufferUsages::VERTEX,
        });

        Some(GpuIconOverlayBatch {
            vertex_buf,
            vertex_count,
        })
    }

    /// メインレンダーパスに描画する（深度テストなし）。
    pub fn draw<'pass>(
        &'pass self,
        batch: &'pass GpuIconOverlayBatch,
        pass: &mut wgpu::RenderPass<'pass>,
    ) {
        if batch.vertex_count == 0 {
            return;
        }
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, &self.bind_group, &[]);
        pass.set_vertex_buffer(0, batch.vertex_buf.slice(..));
        pass.draw(0..batch.vertex_count, 0..1);
    }
}
