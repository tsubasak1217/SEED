// ============================================================
//  hiz.rs — Hi-Z オクルージョンカリングシステム
//
//  ## フレームごとの流れ
//  1. 深度プリパス (application 側で draw_depth_prepass を呼ぶ)
//  2. build_pyramid(encoder, depth_view)  — 深度 → Hi-Z ミップ生成
//  3. dispatch_occlusion(encoder, ...) — インスタンス単位オクルージョンテスト
//  4. schedule_readback(encoder)        — 結果を staging バッファへコピー + map 予約
//  5. (GPU submit & device.poll)
//  6. try_read_results() → Option<Vec<bool>>  — 前フレーム結果の取得
// ============================================================

use wgpu::util::DeviceExt;

// ============================================================
//  ScreenInfo ユニフォーム（オクルージョンシェーダー用）
// ============================================================

#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
struct ScreenInfo {
    width:      f32,
    height:     f32,
    mip_levels: u32,
    _pad:       u32,
}

// ============================================================
//  HiZSystem
// ============================================================

/// Hi-Z ピラミッド生成 + インスタンス単位オクルージョンテストシステム。
///
/// `build_pyramid` → `dispatch_occlusion` → `schedule_readback` の順で呼び出し、
/// GPU submit 後に `try_read_results` で前フレームの結果を取得する（1フレーム遅延）。
pub struct HiZSystem {
    // ── Hi-Z テクスチャ（R32Float, ミップチェーン付き）─────────
    pub hiz_texture:   wgpu::Texture,
    /// ミップレベル 0 からのフルビュー（オクルージョンシェーダーがサンプル）
    pub hiz_full_view: wgpu::TextureView,
    /// ミップレベルごとのストレージビュー（生成シェーダーが書き込み）
    hiz_mip_views:     Vec<wgpu::TextureView>,
    pub hiz_width:     u32,
    pub hiz_height:    u32,
    pub mip_levels:    u32,

    // ── 深度コピーパイプライン（D32Float → R32Float mip0）─────
    copy_pipeline:     wgpu::ComputePipeline,
    copy_bgl:          wgpu::BindGroupLayout,

    // ── ミップ生成パイプライン（mip[n-1] → mip[n]）────────────
    gen_pipeline:      wgpu::ComputePipeline,
    gen_bgl:           wgpu::BindGroupLayout,

    // ── オクルージョンテストパイプライン ─────────────────────
    occ_pipeline:      wgpu::ComputePipeline,
    occ_bgl:           wgpu::BindGroupLayout,

    // ── スクリーン情報ユニフォーム ────────────────────────────
    screen_buf:        wgpu::Buffer,

    // ── 可視性結果バッファ（GPU のみ、将来の GPU カリングフィードバック用）
    result_buf:    wgpu::Buffer,   // STORAGE | COPY_SRC (GPU write)
    num_instances: u32,
}

impl HiZSystem {
    pub fn new(
        device:        &wgpu::Device,
        width:         u32,
        height:        u32,
        num_instances: u32,
    ) -> Self {
        let mip_levels = mip_count(width, height);

        // ── Hi-Z テクスチャ ───────────────────────────────────
        let (hiz_texture, hiz_full_view, hiz_mip_views) =
            create_hiz_texture(device, width, height, mip_levels);

        // ── 深度コピーパイプライン ────────────────────────────
        let (copy_pipeline, copy_bgl) = create_copy_pipeline(device);

        // ── ミップ生成パイプライン ────────────────────────────
        let (gen_pipeline, gen_bgl) = create_gen_pipeline(device);

        // ── オクルージョンテストパイプライン ─────────────────
        let (occ_pipeline, occ_bgl) = create_occ_pipeline(device);

        // ── スクリーン情報バッファ ────────────────────────────
        let screen_info = ScreenInfo {
            width:      width as f32,
            height:     height as f32,
            mip_levels,
            _pad:       0,
        };
        let screen_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("HiZ ScreenInfo Buffer"),
            contents: bytemuck::bytes_of(&screen_info),
            usage:    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });

        // ── 結果バッファ（GPU 側のみ・CPU 読み取りなし）─────────
        let buf_size = ((num_instances.max(1)) as u64 * 4).max(16);
        let result_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label:              Some("HiZ Result Buffer"),
            size:               buf_size,
            usage:              wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });

        Self {
            hiz_texture,
            hiz_full_view,
            hiz_mip_views,
            hiz_width:  width,
            hiz_height: height,
            mip_levels,
            copy_pipeline,
            copy_bgl,
            gen_pipeline,
            gen_bgl,
            occ_pipeline,
            occ_bgl,
            screen_buf,
            result_buf,
            num_instances,
        }
    }

    /// ウィンドウリサイズ時に Hi-Z テクスチャを再生成する。
    pub fn resize(&mut self, device: &wgpu::Device, queue: &wgpu::Queue, width: u32, height: u32) {
        self.hiz_width  = width;
        self.hiz_height = height;
        self.mip_levels = mip_count(width, height);

        let (tex, full_view, mip_views) = create_hiz_texture(device, width, height, self.mip_levels);
        self.hiz_texture  = tex;
        self.hiz_full_view = full_view;
        self.hiz_mip_views = mip_views;

        // スクリーン情報を更新
        let info = ScreenInfo {
            width:      width as f32,
            height:     height as f32,
            mip_levels: self.mip_levels,
            _pad:       0,
        };
        queue.write_buffer(&self.screen_buf, 0, bytemuck::bytes_of(&info));
    }

    // ── ① Hi-Z ピラミッド生成 ─────────────────────────────────

    /// 深度バッファから Hi-Z ミップピラミッドを生成する。
    ///
    /// 深度プリパスの後、メインレンダーパスの前に呼び出すこと。
    pub fn build_pyramid(
        &self,
        encoder:    &mut wgpu::CommandEncoder,
        depth_view: &wgpu::TextureView,
        device:     &wgpu::Device,
    ) {
        let mut cpass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label:            Some("HiZ Build"),
            timestamp_writes: None,
        });

        // パス 1: depth → mip 0
        {
            let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label:  Some("HiZ Copy BG"),
                layout: &self.copy_bgl,
                entries: &[
                    wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(depth_view) },
                    wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::TextureView(&self.hiz_mip_views[0]) },
                ],
            });
            cpass.set_pipeline(&self.copy_pipeline);
            cpass.set_bind_group(0, &bg, &[]);
            let (gx, gy) = dispatch_size(self.hiz_width, self.hiz_height, 8);
            cpass.dispatch_workgroups(gx, gy, 1);
        }

        // パス 2〜N: mip[n-1] → mip[n]
        for mip in 1..self.mip_levels as usize {
            let src_w = (self.hiz_width  >> (mip - 1)).max(1);
            let src_h = (self.hiz_height >> (mip - 1)).max(1);
            let dst_w = (self.hiz_width  >> mip).max(1);
            let dst_h = (self.hiz_height >> mip).max(1);

            // mip[n-1] のサンプリング用ビューを一時生成
            let src_view = self.hiz_texture.create_view(&wgpu::TextureViewDescriptor {
                base_mip_level:   (mip - 1) as u32,
                mip_level_count:  Some(1),
                dimension:        Some(wgpu::TextureViewDimension::D2),
                ..Default::default()
            });

            let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
                label:  Some("HiZ Gen BG"),
                layout: &self.gen_bgl,
                entries: &[
                    wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(&src_view) },
                    wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::TextureView(&self.hiz_mip_views[mip]) },
                ],
            });
            cpass.set_pipeline(&self.gen_pipeline);
            cpass.set_bind_group(0, &bg, &[]);
            let (gx, gy) = dispatch_size(dst_w, dst_h, 8);
            cpass.dispatch_workgroups(gx, gy, 1);

            let _ = (src_w, src_h); // suppress unused warnings
        }
    }

    // ── ② オクルージョンテスト ───────────────────────────────

    /// 全インスタンスに対してオクルージョンテストを GPU コンピュートで実行する。
    ///
    /// `aabbs_buf` は `GpuCullData` 配列（world space AABB）を持つ Storage バッファ。
    /// `camera_buf` は `CameraUniform` を持つ Uniform バッファ。
    pub fn dispatch_occlusion(
        &self,
        encoder:    &mut wgpu::CommandEncoder,
        aabbs_buf:  &wgpu::Buffer,
        camera_buf: &wgpu::Buffer,
        device:     &wgpu::Device,
    ) {
        if self.num_instances == 0 { return; }

        let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:  Some("HiZ Occlusion BG"),
            layout: &self.occ_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: aabbs_buf.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 1, resource: camera_buf.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::TextureView(&self.hiz_full_view) },
                wgpu::BindGroupEntry { binding: 3, resource: self.result_buf.as_entire_binding() },
                wgpu::BindGroupEntry { binding: 4, resource: self.screen_buf.as_entire_binding() },
            ],
        });

        let mut cpass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label:            Some("HiZ Occlusion"),
            timestamp_writes: None,
        });
        cpass.set_pipeline(&self.occ_pipeline);
        cpass.set_bind_group(0, &bg, &[]);
        let groups = (self.num_instances + 63) / 64;
        cpass.dispatch_workgroups(groups, 1, 1);
    }

}

// ============================================================
//  内部ヘルパー
// ============================================================

fn mip_count(width: u32, height: u32) -> u32 {
    let max_dim = width.max(height);
    (u32::BITS - max_dim.leading_zeros()).max(1)
}

fn dispatch_size(width: u32, height: u32, tile: u32) -> (u32, u32) {
    ((width + tile - 1) / tile, (height + tile - 1) / tile)
}

fn create_hiz_texture(
    device:     &wgpu::Device,
    width:      u32,
    height:     u32,
    mip_levels: u32,
) -> (wgpu::Texture, wgpu::TextureView, Vec<wgpu::TextureView>) {
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label:           Some("Hi-Z Texture"),
        size:            wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
        mip_level_count: mip_levels,
        sample_count:    1,
        dimension:       wgpu::TextureDimension::D2,
        format:          wgpu::TextureFormat::R32Float,
        usage:           wgpu::TextureUsages::STORAGE_BINDING
                       | wgpu::TextureUsages::TEXTURE_BINDING,
        view_formats:    &[],
    });

    // 全ミップをサンプリングするフルビュー
    let full_view = texture.create_view(&wgpu::TextureViewDescriptor {
        dimension:       Some(wgpu::TextureViewDimension::D2),
        mip_level_count: Some(mip_levels),
        ..Default::default()
    });

    // ミップごとのストレージビュー（書き込み専用）
    let mip_views: Vec<wgpu::TextureView> = (0..mip_levels)
        .map(|mip| {
            texture.create_view(&wgpu::TextureViewDescriptor {
                base_mip_level:  mip,
                mip_level_count: Some(1),
                dimension:       Some(wgpu::TextureViewDimension::D2),
                ..Default::default()
            })
        })
        .collect();

    (texture, full_view, mip_views)
}

fn create_copy_pipeline(device: &wgpu::Device) -> (wgpu::ComputePipeline, wgpu::BindGroupLayout) {
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label:  Some("HiZ Copy Shader"),
        source: wgpu::ShaderSource::Wgsl(include_str!("hiz_copy_depth.wgsl").into()),
    });

    let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label:   Some("HiZ Copy BGL"),
        entries: &[
            // binding 0: depth texture (texture_depth_2d)
            wgpu::BindGroupLayoutEntry {
                binding:    0,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::Texture {
                    sample_type:    wgpu::TextureSampleType::Depth,
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled:   false,
                },
                count: None,
            },
            // binding 1: dst R32Float storage (write)
            wgpu::BindGroupLayoutEntry {
                binding:    1,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::StorageTexture {
                    access:         wgpu::StorageTextureAccess::WriteOnly,
                    format:         wgpu::TextureFormat::R32Float,
                    view_dimension: wgpu::TextureViewDimension::D2,
                },
                count: None,
            },
        ],
    });

    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label:                Some("HiZ Copy Layout"),
        bind_group_layouts:   &[&bgl],
        push_constant_ranges: &[],
    });
    let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label:               Some("HiZ Copy Pipeline"),
        layout:              Some(&layout),
        module:              &shader,
        entry_point:         Some("cs_main"),
        compilation_options: Default::default(),
        cache:               None,
    });

    (pipeline, bgl)
}

fn create_gen_pipeline(device: &wgpu::Device) -> (wgpu::ComputePipeline, wgpu::BindGroupLayout) {
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label:  Some("HiZ Gen Shader"),
        source: wgpu::ShaderSource::Wgsl(include_str!("hiz_gen_mip.wgsl").into()),
    });

    let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label:   Some("HiZ Gen BGL"),
        entries: &[
            // binding 0: src R32Float sampled
            wgpu::BindGroupLayoutEntry {
                binding:    0,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::Texture {
                    sample_type:    wgpu::TextureSampleType::Float { filterable: false },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled:   false,
                },
                count: None,
            },
            // binding 1: dst R32Float storage (write)
            wgpu::BindGroupLayoutEntry {
                binding:    1,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::StorageTexture {
                    access:         wgpu::StorageTextureAccess::WriteOnly,
                    format:         wgpu::TextureFormat::R32Float,
                    view_dimension: wgpu::TextureViewDimension::D2,
                },
                count: None,
            },
        ],
    });

    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label:                Some("HiZ Gen Layout"),
        bind_group_layouts:   &[&bgl],
        push_constant_ranges: &[],
    });
    let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label:               Some("HiZ Gen Pipeline"),
        layout:              Some(&layout),
        module:              &shader,
        entry_point:         Some("cs_main"),
        compilation_options: Default::default(),
        cache:               None,
    });

    (pipeline, bgl)
}

fn create_occ_pipeline(device: &wgpu::Device) -> (wgpu::ComputePipeline, wgpu::BindGroupLayout) {
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label:  Some("HiZ Occlusion Shader"),
        source: wgpu::ShaderSource::Wgsl(include_str!("hiz_occlusion.wgsl").into()),
    });

    let make_storage_ro = |binding: u32| wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::Buffer {
            ty:                 wgpu::BufferBindingType::Storage { read_only: true },
            has_dynamic_offset: false,
            min_binding_size:   None,
        },
        count: None,
    };
    let make_storage_rw = |binding: u32| wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::Buffer {
            ty:                 wgpu::BufferBindingType::Storage { read_only: false },
            has_dynamic_offset: false,
            min_binding_size:   None,
        },
        count: None,
    };
    let make_uniform = |binding: u32| wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::COMPUTE,
        ty: wgpu::BindingType::Buffer {
            ty:                 wgpu::BufferBindingType::Uniform,
            has_dynamic_offset: false,
            min_binding_size:   None,
        },
        count: None,
    };

    let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label:   Some("HiZ Occlusion BGL"),
        entries: &[
            make_storage_ro(0), // aabbs
            make_uniform(1),    // u_camera
            // binding 2: Hi-Z texture (sampled, R32Float non-filterable)
            wgpu::BindGroupLayoutEntry {
                binding:    2,
                visibility: wgpu::ShaderStages::COMPUTE,
                ty: wgpu::BindingType::Texture {
                    sample_type:    wgpu::TextureSampleType::Float { filterable: false },
                    view_dimension: wgpu::TextureViewDimension::D2,
                    multisampled:   false,
                },
                count: None,
            },
            make_storage_rw(3), // visibility output
            make_uniform(4),    // u_screen
        ],
    });

    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label:                Some("HiZ Occlusion Layout"),
        bind_group_layouts:   &[&bgl],
        push_constant_ranges: &[],
    });
    let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label:               Some("HiZ Occlusion Pipeline"),
        layout:              Some(&layout),
        module:              &shader,
        entry_point:         Some("cs_main"),
        compilation_options: Default::default(),
        cache:               None,
    });

    (pipeline, bgl)
}
