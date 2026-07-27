// ============================================================
//  gbuffer_debug.rs — G-Buffer デバッグ可視化パイプライン（シーンビュー表示モード）
//
//  ## 役割（単一責任）
//  「G-Buffer の指定 1 チャンネルをシーン HDR へ上書きする」フルスクリーンパイプラインと、
//  その入力 BindGroup／パラメータ UBO の生成だけを持つ。
//  実行するかどうかの判断（表示モード）とパスの記録は frame_renderer の責務。
//
//  ## モードごとにパイプラインを作らない
//  可視化チャンネルは uniform（`GBufferDebugParams.channel`）の enum 値で切り替える。
//  シェーダは `shaders/gbuffer_debug.wgsl` の 1 本だけ・パイプラインも 1 本だけで、
//  モードを増やしても GPU 資源は増えない（シェーダの switch に case を足すだけ）。
//
//  ## なぜ TOML ビルダー（RenderPipelineBuilder）を使わないのか
//  本パスは group を 1 つしか使わず、G-Buffer 5 枚＋速度＋UBO という固定構成のため、
//  WGSL リフレクションで BGL を起こす利点がない。velocity_debug.rs と同じ手書き構成に揃える。
// ============================================================

use super::pipeline::get_shader_source;
use super::view_mode::GBufferDebugChannel;

// ─── パラメータ UBO ─────────────────────────────────────────

/// G-Buffer 可視化パラメータ（WGSL `GBufferDebugParams` と #[repr(C)] 一致）。
#[repr(C)]
#[derive(Copy, Clone, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct GBufferDebugParams {
    /// 可視化チャンネル（`GBufferDebugChannel::to_code()`）。
    pub channel:    u32,
    /// カメラの near 平面距離（深度の線形化用）。
    pub near_plane: f32,
    /// カメラの far 平面距離（深度の線形化用）。
    pub far_plane:  f32,
    /// 16 バイト境界合わせのパディング（WGSL 側の `_pad` と対応）。
    pub _pad:       u32,
}

// ─── パイプライン一式 ───────────────────────────────────────

/// G-Buffer デバッグ可視化のフルスクリーンパイプライン一式。
pub struct GBufferDebugPipeline {
    /// フルスクリーン三角形 → チャンネル可視化出力のパイプライン。
    pub pipeline: wgpu::RenderPipeline,
    /// group0: G-Buffer 5 枚 + 速度 + パラメータ UBO のレイアウト
    /// （サンプラー無し＝すべて textureLoad で直読み）。
    pub bgl:      wgpu::BindGroupLayout,
    /// パラメータ UBO（毎フレーム write_buffer で更新する。1 個で使い回す）。
    params_buf:   wgpu::Buffer,
}

impl GBufferDebugPipeline {
    /// 可視化パイプラインを構築する。
    ///
    /// - `out_format`: 出力先（シーン HDR, `Rgba16Float`）。
    ///   深度は持たない（`depth_stencil: None`）。全画素を無条件に上書きするデバッグ表示なので、
    ///   深度テストは不要かつ有害（velocity_debug.rs と同じ理由）。
    pub fn new(
        device:     &wgpu::Device,
        out_format: wgpu::TextureFormat,
        cache:      Option<&wgpu::PipelineCache>,
    ) -> Self {
        const LABEL: &str = "gbuffer_debug";

        // G-Buffer カラー RT（RT0..RT3・速度 RT4）は「フィルタ可能な float」テクスチャ。
        // textureLoad しか使わないためサンプラーは一切要求しない。
        let color_tex_entry = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Texture {
                sample_type:    wgpu::TextureSampleType::Float { filterable: true },
                view_dimension: wgpu::TextureViewDimension::D2,
                multisampled:   false,
            },
            count: None,
        };

        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some(LABEL),
            entries: &[
                color_tex_entry(0), // RT0: base_color + occlusion
                color_tex_entry(1), // RT1: world normal
                color_tex_entry(2), // RT2: metallic / roughness / transmission / user_data
                color_tex_entry(3), // RT3: emissive + surface_id
                // 深度（texture_depth_2d。textureLoad で深度値のみ読む＝比較サンプラー不要）。
                wgpu::BindGroupLayoutEntry {
                    binding:    4,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type:    wgpu::TextureSampleType::Depth,
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled:   false,
                    },
                    count: None,
                },
                color_tex_entry(5), // RT4: 速度（モーションベクタ）
                // パラメータ UBO。
                wgpu::BindGroupLayoutEntry {
                    binding:    6,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Buffer {
                        ty:                 wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size:   None,
                    },
                    count: None,
                },
            ],
        });

        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some(LABEL),
            source: wgpu::ShaderSource::Wgsl(get_shader_source("gbuffer_debug.wgsl").into()),
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some(LABEL),
            bind_group_layouts:   &[&bgl],
            push_constant_ranges: &[],
        });

        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some(LABEL),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module:              &shader,
                entry_point:         Some("vs_fullscreen"),
                buffers:             &[],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module:      &shader,
                entry_point: Some("fs_gbuffer_debug"),
                targets:     &[Some(wgpu::ColorTargetState {
                    format:     out_format,
                    blend:      None,
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive:     wgpu::PrimitiveState::default(),
            depth_stencil: None,
            multisample:   wgpu::MultisampleState::default(),
            multiview:     None,
            cache,
        });

        let params_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label:              Some("GBuffer Debug Params"),
            size:               std::mem::size_of::<GBufferDebugParams>() as u64,
            usage:              wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        Self { pipeline, bgl, params_buf }
    }

    /// 可視化パラメータ（チャンネル・near/far）を UBO へ書き込む。
    /// 描画パスを開く前に呼ぶこと（同一 encoder 内では queue の書き込みが先行適用される）。
    pub fn write_params(
        &self,
        queue:      &wgpu::Queue,
        channel:    GBufferDebugChannel,
        near_plane: f32,
        far_plane:  f32,
    ) {
        let params = GBufferDebugParams {
            channel: channel.to_code(),
            near_plane,
            far_plane,
            _pad: 0,
        };
        queue.write_buffer(&self.params_buf, 0, bytemuck::bytes_of(&params));
    }

    /// G-Buffer 各ビューから group0 の BindGroup を生成する。
    /// G-Buffer はウィンドウリサイズで作り直されるため、毎フレーム呼んでよい。
    #[allow(clippy::too_many_arguments)]
    pub fn create_bind_group(
        &self,
        device:        &wgpu::Device,
        g0:            &wgpu::TextureView,
        g1:            &wgpu::TextureView,
        g2:            &wgpu::TextureView,
        g3:            &wgpu::TextureView,
        depth:         &wgpu::TextureView,
        velocity:      &wgpu::TextureView,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("GBuffer Debug BG"),
            layout:  &self.bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(g0) },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::TextureView(g1) },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::TextureView(g2) },
                wgpu::BindGroupEntry { binding: 3, resource: wgpu::BindingResource::TextureView(g3) },
                wgpu::BindGroupEntry { binding: 4, resource: wgpu::BindingResource::TextureView(depth) },
                wgpu::BindGroupEntry { binding: 5, resource: wgpu::BindingResource::TextureView(velocity) },
                wgpu::BindGroupEntry { binding: 6, resource: self.params_buf.as_entire_binding() },
            ],
        })
    }
}

// ============================================================
//  WGSL 静的検証 / レイアウト検証
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 可視化 WGSL（単体でモジュールになる）を naga で parse + validate する。
    #[test]
    fn gbuffer_debug_shader_parses_and_validates() {
        let src = include_str!("shaders/gbuffer_debug.wgsl");
        let module = naga::front::wgsl::parse_str(src)
            .unwrap_or_else(|e| panic!("[gbuffer_debug] WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("[gbuffer_debug] WGSL validate 失敗: {e:?}"));
    }

    /// リゾルバ登録漏れの検出（pipeline.rs::get_shader_source）。
    #[test]
    fn gbuffer_debug_shader_is_registered_in_resolver() {
        assert_eq!(
            super::get_shader_source("gbuffer_debug.wgsl"),
            include_str!("shaders/gbuffer_debug.wgsl"),
            "pipeline.rs::get_shader_source に gbuffer_debug.wgsl が登録されていない"
        );
    }

    /// UBO のサイズが uniform の最小アライメント（16 バイト）を満たすこと。
    #[test]
    fn gbuffer_debug_params_is_16_bytes() {
        assert_eq!(std::mem::size_of::<GBufferDebugParams>(), 16);
    }

    /// Rust の GBufferDebugChannel と WGSL の GB_DEBUG_* 定数が一致していること。
    /// （片方だけ書き換えると「別チャンネルが表示される」静かな不具合になるため機械検証する）
    #[test]
    fn gbuffer_debug_channel_codes_match_wgsl() {
        // WGSL 側は定数名を桁揃えしているため、空白を全て畳んでから部分文字列照合する。
        let src: String = include_str!("shaders/gbuffer_debug.wgsl")
            .split_whitespace()
            .collect::<Vec<_>>()
            .join(" ");
        let pairs: &[(&str, GBufferDebugChannel)] = &[
            ("GB_DEBUG_BASE_COLOR",   GBufferDebugChannel::BaseColor),
            ("GB_DEBUG_OCCLUSION",    GBufferDebugChannel::Occlusion),
            ("GB_DEBUG_NORMAL",       GBufferDebugChannel::Normal),
            ("GB_DEBUG_ROUGHNESS",    GBufferDebugChannel::Roughness),
            ("GB_DEBUG_METALLIC",     GBufferDebugChannel::Metallic),
            ("GB_DEBUG_TRANSMISSION", GBufferDebugChannel::Transmission),
            ("GB_DEBUG_EMISSIVE",     GBufferDebugChannel::Emissive),
            ("GB_DEBUG_DEPTH",        GBufferDebugChannel::Depth),
            ("GB_DEBUG_VELOCITY",     GBufferDebugChannel::Velocity),
            ("GB_DEBUG_RENDER_TAG",   GBufferDebugChannel::RenderTag),
            ("GB_DEBUG_USER_DATA",    GBufferDebugChannel::UserData),
        ];
        for (name, ch) in pairs {
            let needle = format!("const {name}: u32 = {}u;", ch.to_code());
            // src は空白畳み済みなので、`const NAME: u32 = Nu;` の形で一意に一致する。
            assert!(
                src.contains(&needle),
                "gbuffer_debug.wgsl に `{needle}` が見つからない（Rust 側 GBufferDebugChannel と不一致）"
            );
        }
    }
}
