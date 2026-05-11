// pipeline_config.rs — WGSL リフレクション + レンダーパイプライン自動構築
//
// ## 使い方
// 1. TOML ファイルで設定を記述する（pipelines/*.toml）
// 2. RenderPipelineBuilder::new(device, toml_str, sf, df).build(get_shader_source)
//    を呼ぶと (RenderPipeline, Vec<BindGroupLayout>) が返る
// 3. 返り値の Vec は bind group 番号順（0, 1, 2, ...）で、pop() で末尾から取り出せる

use std::collections::BTreeMap;

// ============================================================
//  PipelineConfig — TOML 設定構造体
// ============================================================

#[derive(serde::Deserialize)]
pub struct PipelineConfig {
    /// 連結するシェーダーファイル名のリスト（順番に join）
    pub shader_sources:  Vec<String>,
    /// 頂点シェーダーエントリポイント名
    pub vertex_entry:    String,
    /// フラグメントシェーダーエントリポイント名（None → fragment: None）
    pub fragment_entry:  Option<String>,
    /// 頂点バッファスロット名のリスト（pipeline_config::vertex_buffer_layout 参照）
    pub vertex_slots:    Vec<String>,
    /// 生成する BindGroupLayout のスロット数。None → 全リフレクション結果から自動決定。
    /// 値より大きい group 番号は無視され、宣言されていない group は空 BGL で埋める。
    pub num_bind_groups: Option<u32>,

    #[serde(default = "default_topology")]
    pub topology:   String,  // "TriangleList" | "LineList" | "TriangleStrip"
    #[serde(default = "default_cull")]
    pub cull_mode:  String,  // "Back" | "Front" | "None"
    #[serde(default = "default_front_face")]
    pub front_face: String,  // "Ccw" | "Cw"

    #[serde(default = "default_true")]
    pub depth_write:   bool,
    #[serde(default = "default_depth_compare")]
    pub depth_compare: String,  // "Less" | "LessEqual" | "Always" | ...

    /// "surface" → surface_format、"R32Uint" → R32Uint、"none" → fragment: None
    #[serde(default = "default_color_format")]
    pub color_format: String,
    /// "Replace" | "AlphaBlending" | "None"
    #[serde(default = "default_blend")]
    pub blend:        String,
    /// "all" | "none"
    #[serde(default = "default_write_mask")]
    pub write_mask:   String,

    #[serde(default)]
    pub depth_bias_constant: i32,
}

fn default_topology()      -> String { "TriangleList".into() }
fn default_cull()          -> String { "Back".into() }
fn default_front_face()    -> String { "Ccw".into() }
fn default_true()          -> bool   { true }
fn default_depth_compare() -> String { "Less".into() }
fn default_color_format()  -> String { "surface".into() }
fn default_blend()         -> String { "Replace".into() }
fn default_write_mask()    -> String { "all".into() }

// ============================================================
//  RenderPipelineBuilder
// ============================================================

pub struct RenderPipelineBuilder<'d> {
    device:         &'d wgpu::Device,
    cfg:            PipelineConfig,
    surface_format: wgpu::TextureFormat,
    depth_format:   wgpu::TextureFormat,
    stencil:        Option<wgpu::StencilState>,
}

impl<'d> RenderPipelineBuilder<'d> {
    /// TOML 文字列から Builder を初期化する。
    pub fn new(
        device:         &'d wgpu::Device,
        toml_src:       &str,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
    ) -> Self {
        let cfg: PipelineConfig = toml::from_str(toml_src)
            .expect("invalid pipeline TOML");
        Self { device, cfg, surface_format, depth_format, stencil: None }
    }

    /// ステンシルステートを上書き設定する（アウトライン等の特殊用途）。
    pub fn with_stencil(mut self, s: wgpu::StencilState) -> Self {
        self.stencil = Some(s);
        self
    }

    /// パイプラインを構築して返す。
    ///
    /// `resolve` は shader_sources 中のファイル名を `&'static str` ソースに変換する関数。
    /// 返り値の `Vec<BindGroupLayout>` は group 番号順（0, 1, 2, ...）。
    /// pop() で末尾（最大グループ）から順に取り出せる。
    pub fn build<F>(self, resolve: F) -> (wgpu::RenderPipeline, Vec<wgpu::BindGroupLayout>)
    where
        F: Fn(&str) -> &'static str,
    {
        let Self { device, cfg, surface_format, depth_format, stencil } = self;

        // ── 1. シェーダーソースを連結 ──────────────────────────
        let combined: String = cfg.shader_sources.iter()
            .map(|name| resolve(name))
            .collect::<Vec<_>>()
            .join("\n");

        // ── 2. シェーダーモジュール ────────────────────────────
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some(cfg.vertex_entry.as_str()),
            source: wgpu::ShaderSource::Wgsl(combined.clone().into()),
        });

        // ── 3. WGSL リフレクション → BGL マップ ─────────────────
        let mut reflected = reflect_bgls(device, &combined);

        let num_groups = cfg.num_bind_groups.unwrap_or_else(|| {
            reflected.keys().max().copied().map_or(0, |m| m + 1)
        });

        // ── 4. 空 BGL で gap を埋めた Vec<BGL> を構築 ─────────
        let all_bgls: Vec<wgpu::BindGroupLayout> = (0..num_groups)
            .map(|g| {
                reflected.remove(&g).unwrap_or_else(|| {
                    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                        label:   Some("Gap BGL"),
                        entries: &[],
                    })
                })
            })
            .collect();

        // ── 5. パイプラインレイアウト ──────────────────────────
        let bgl_refs: Vec<&wgpu::BindGroupLayout> = all_bgls.iter().collect();
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some(cfg.vertex_entry.as_str()),
            bind_group_layouts:   &bgl_refs,
            push_constant_ranges: &[],
        });

        // ── 6. 頂点バッファレイアウト ──────────────────────────
        let vertex_buffers: Vec<wgpu::VertexBufferLayout<'static>> = cfg.vertex_slots.iter()
            .map(|name| vertex_buffer_layout(name))
            .collect();

        // ── 7. カラーターゲット ────────────────────────────────
        let color_target: Option<wgpu::ColorTargetState> = if cfg.color_format == "none" {
            None
        } else {
            let format = match cfg.color_format.as_str() {
                "R32Uint" => wgpu::TextureFormat::R32Uint,
                _         => surface_format,
            };
            let blend = match cfg.blend.as_str() {
                "AlphaBlending" => Some(wgpu::BlendState::ALPHA_BLENDING),
                "None"          => None,
                _               => Some(wgpu::BlendState::REPLACE),
            };
            let write_mask = if cfg.write_mask == "none" {
                wgpu::ColorWrites::empty()
            } else {
                wgpu::ColorWrites::ALL
            };
            Some(wgpu::ColorTargetState { format, blend, write_mask })
        };

        // ── 8. フラグメントステート ────────────────────────────
        let color_targets = color_target.as_ref().map(|ct| vec![Some(ct.clone())]);
        let fragment: Option<wgpu::FragmentState<'_>> =
            match (&cfg.fragment_entry, &color_targets) {
                (Some(entry), Some(targets)) => Some(wgpu::FragmentState {
                    module:              &shader,
                    entry_point:         Some(entry.as_str()),
                    targets:             targets.as_slice(),
                    compilation_options: Default::default(),
                }),
                _ => None,
            };

        // ── 9. 深度ステンシルステート ──────────────────────────
        let depth_compare  = parse_compare(&cfg.depth_compare);
        let depth_stencil  = Some(wgpu::DepthStencilState {
            format:              depth_format,
            depth_write_enabled: cfg.depth_write,
            depth_compare,
            stencil: stencil.unwrap_or_default(),
            bias: wgpu::DepthBiasState {
                constant: cfg.depth_bias_constant,
                ..Default::default()
            },
        });

        // ── 10. プリミティブステート ───────────────────────────
        let topology = match cfg.topology.as_str() {
            "LineList"      => wgpu::PrimitiveTopology::LineList,
            "TriangleStrip" => wgpu::PrimitiveTopology::TriangleStrip,
            _               => wgpu::PrimitiveTopology::TriangleList,
        };
        let cull_mode = match cfg.cull_mode.as_str() {
            "None"  => None,
            "Front" => Some(wgpu::Face::Front),
            _       => Some(wgpu::Face::Back),
        };
        let front_face = if cfg.front_face == "Cw" { wgpu::FrontFace::Cw } else { wgpu::FrontFace::Ccw };

        // ── 11. パイプライン生成 ───────────────────────────────
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  None,
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module:              &shader,
                entry_point:         Some(cfg.vertex_entry.as_str()),
                buffers:             &vertex_buffers,
                compilation_options: Default::default(),
            },
            fragment,
            primitive: wgpu::PrimitiveState {
                topology,
                front_face,
                cull_mode,
                ..Default::default()
            },
            depth_stencil,
            multisample: wgpu::MultisampleState::default(),
            multiview:   None,
            cache:       None,
        });

        (pipeline, all_bgls)
    }
}

// ============================================================
//  WGSL リフレクション
// ============================================================

/// WGSL ソースを naga で解析し、各 bind group のレイアウトを返す。
///
/// バッファ類は `VERTEX_FRAGMENT` 可視、テクスチャ・サンプラーは `FRAGMENT` 可視とする。
fn reflect_bgls(
    device: &wgpu::Device,
    src:    &str,
) -> BTreeMap<u32, wgpu::BindGroupLayout> {
    let module = naga::front::wgsl::parse_str(src)
        .unwrap_or_else(|e| panic!("WGSL parse failed: {e:?}"));

    let mut groups: BTreeMap<u32, Vec<wgpu::BindGroupLayoutEntry>> = BTreeMap::new();

    for (_, var) in module.global_variables.iter() {
        let Some(rb) = &var.binding else { continue };

        let ty = &module.types[var.ty];

        let Some((wgpu_ty, visibility)) = to_binding_type(&ty.inner, var.space)
            else { continue };

        groups.entry(rb.group).or_default().push(wgpu::BindGroupLayoutEntry {
            binding:    rb.binding,
            visibility,
            ty:         wgpu_ty,
            count:      None,
        });
    }

    // 各グループ内のエントリを binding 番号でソート
    for entries in groups.values_mut() {
        entries.sort_by_key(|e| e.binding);
    }

    groups.into_iter().map(|(group, entries)| {
        let label = format!("Auto BGL group={group}");
        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some(&label),
            entries: &entries,
        });
        (group, bgl)
    }).collect()
}

fn to_binding_type(
    ty:    &naga::TypeInner,
    space: naga::AddressSpace,
) -> Option<(wgpu::BindingType, wgpu::ShaderStages)> {
    use naga::{ImageClass, ScalarKind, StorageAccess};

    match ty {
        // テクスチャ
        naga::TypeInner::Image { class, dim, arrayed } => {
            let view_dim = naga_dim_to_wgpu(*dim, *arrayed);
            let binding_ty = match class {
                ImageClass::Sampled { kind, .. } => {
                    let st = match kind {
                        ScalarKind::Float => wgpu::TextureSampleType::Float { filterable: true },
                        ScalarKind::Sint  => wgpu::TextureSampleType::Sint,
                        ScalarKind::Uint  => wgpu::TextureSampleType::Uint,
                        _                 => wgpu::TextureSampleType::Float { filterable: true },
                    };
                    wgpu::BindingType::Texture {
                        sample_type:  st,
                        view_dimension: view_dim,
                        multisampled: false,
                    }
                },
                ImageClass::Depth { .. } => {
                    wgpu::BindingType::Texture {
                        sample_type:    wgpu::TextureSampleType::Depth,
                        view_dimension: view_dim,
                        multisampled:   false,
                    }
                },
                ImageClass::Storage { format, access } => {
                    let acc = match (
                        access.contains(StorageAccess::LOAD),
                        access.contains(StorageAccess::STORE),
                    ) {
                        (true, true)  => wgpu::StorageTextureAccess::ReadWrite,
                        (_, true)     => wgpu::StorageTextureAccess::WriteOnly,
                        _             => wgpu::StorageTextureAccess::ReadOnly,
                    };
                    wgpu::BindingType::StorageTexture {
                        access:         acc,
                        format:         naga_storage_fmt(*format),
                        view_dimension: view_dim,
                    }
                },
            };
            Some((binding_ty, wgpu::ShaderStages::FRAGMENT))
        },
        // サンプラー
        naga::TypeInner::Sampler { comparison } => {
            let st = if *comparison {
                wgpu::SamplerBindingType::Comparison
            } else {
                wgpu::SamplerBindingType::Filtering
            };
            Some((wgpu::BindingType::Sampler(st), wgpu::ShaderStages::FRAGMENT))
        },
        // バッファ（Uniform / Storage）
        _ => {
            let bt = match space {
                naga::AddressSpace::Uniform => wgpu::BindingType::Buffer {
                    ty:                 wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size:   None,
                },
                naga::AddressSpace::Storage { access } => wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Storage {
                        read_only: !access.contains(naga::StorageAccess::STORE),
                    },
                    has_dynamic_offset: false,
                    min_binding_size:   None,
                },
                _ => return None,
            };
            Some((bt, wgpu::ShaderStages::VERTEX_FRAGMENT))
        },
    }
}

fn naga_dim_to_wgpu(dim: naga::ImageDimension, arrayed: bool) -> wgpu::TextureViewDimension {
    match (dim, arrayed) {
        (naga::ImageDimension::D1,   _    ) => wgpu::TextureViewDimension::D1,
        (naga::ImageDimension::D2,   false) => wgpu::TextureViewDimension::D2,
        (naga::ImageDimension::D2,   true ) => wgpu::TextureViewDimension::D2Array,
        (naga::ImageDimension::D3,   _    ) => wgpu::TextureViewDimension::D3,
        (naga::ImageDimension::Cube, false) => wgpu::TextureViewDimension::Cube,
        (naga::ImageDimension::Cube, true ) => wgpu::TextureViewDimension::CubeArray,
    }
}

fn naga_storage_fmt(fmt: naga::StorageFormat) -> wgpu::TextureFormat {
    match fmt {
        naga::StorageFormat::R32Float     => wgpu::TextureFormat::R32Float,
        naga::StorageFormat::Rgba8Unorm   => wgpu::TextureFormat::Rgba8Unorm,
        naga::StorageFormat::Rgba16Float  => wgpu::TextureFormat::Rgba16Float,
        naga::StorageFormat::Rgba32Float  => wgpu::TextureFormat::Rgba32Float,
        naga::StorageFormat::R32Uint      => wgpu::TextureFormat::R32Uint,
        naga::StorageFormat::R32Sint      => wgpu::TextureFormat::R32Sint,
        other => panic!("unsupported storage format: {other:?}"),
    }
}

// ============================================================
//  頂点バッファレイアウトプリセット
// ============================================================

/// スロット名から頂点バッファレイアウトを返す。
///
/// 定義済みスロット名:
/// - `"mesh_vertex"`   — Vertex 構造体 (72 bytes, location 0-5)
/// - `"skin_vertex"`   — SkinVertex (24 bytes, location 6-7)
/// - `"smooth_normal"` — smooth normal (12 bytes, location 8)
/// - `"color_vertex"`  — ColorVertex (28 bytes, location 0-1)
pub fn vertex_buffer_layout(name: &str) -> wgpu::VertexBufferLayout<'static> {
    use wgpu::{VertexAttribute as VA, VertexFormat as VF, VertexStepMode, VertexBufferLayout};

    static MESH_ATTRS: &[VA] = &[
        VA { format: VF::Float32x3, offset: 0,  shader_location: 0 },
        VA { format: VF::Float32x3, offset: 12, shader_location: 1 },
        VA { format: VF::Float32x4, offset: 24, shader_location: 2 },
        VA { format: VF::Float32x2, offset: 40, shader_location: 3 },
        VA { format: VF::Float32x2, offset: 48, shader_location: 4 },
        VA { format: VF::Float32x4, offset: 56, shader_location: 5 },
    ];
    static SKIN_ATTRS: &[VA] = &[
        VA { format: VF::Uint16x4,  offset: 0, shader_location: 6 },
        VA { format: VF::Float32x4, offset: 8, shader_location: 7 },
    ];
    static SMOOTH_NORMAL_ATTRS: &[VA] = &[
        VA { format: VF::Float32x3, offset: 0, shader_location: 8 },
    ];
    static COLOR_ATTRS: &[VA] = &[
        VA { format: VF::Float32x3, offset: 0,  shader_location: 0 },
        VA { format: VF::Float32x4, offset: 12, shader_location: 1 },
    ];
    // GizmoVertex: pos_a(0-11), t(12-15), pos_b(16-27), side(28-31), color(32-47)
    static GIZMO_ATTRS: &[VA] = &[
        VA { format: VF::Float32x3, offset: 0,  shader_location: 0 },
        VA { format: VF::Float32,   offset: 12, shader_location: 1 },
        VA { format: VF::Float32x3, offset: 16, shader_location: 2 },
        VA { format: VF::Float32,   offset: 28, shader_location: 3 },
        VA { format: VF::Float32x4, offset: 32, shader_location: 4 },
    ];
    // SpriteVertex: position(0-7, vec2) + uv(8-15, vec2) = 16 bytes
    static SPRITE_ATTRS: &[VA] = &[
        VA { format: VF::Float32x2, offset: 0, shader_location: 0 },
        VA { format: VF::Float32x2, offset: 8, shader_location: 1 },
    ];

    match name {
        "mesh_vertex"   => VertexBufferLayout { array_stride: 72, step_mode: VertexStepMode::Vertex, attributes: MESH_ATTRS },
        "skin_vertex"   => VertexBufferLayout { array_stride: 24, step_mode: VertexStepMode::Vertex, attributes: SKIN_ATTRS },
        "smooth_normal" => VertexBufferLayout { array_stride: 12, step_mode: VertexStepMode::Vertex, attributes: SMOOTH_NORMAL_ATTRS },
        "color_vertex"  => VertexBufferLayout { array_stride: 28, step_mode: VertexStepMode::Vertex, attributes: COLOR_ATTRS },
        "gizmo_vertex"  => VertexBufferLayout { array_stride: 48, step_mode: VertexStepMode::Vertex, attributes: GIZMO_ATTRS },
        "sprite_vertex" => VertexBufferLayout { array_stride: 16, step_mode: VertexStepMode::Vertex, attributes: SPRITE_ATTRS },
        other => panic!("unknown vertex slot preset: {other}"),
    }
}

// ============================================================
//  ヘルパー
// ============================================================

pub fn parse_compare(s: &str) -> wgpu::CompareFunction {
    match s {
        "Never"        => wgpu::CompareFunction::Never,
        "Less"         => wgpu::CompareFunction::Less,
        "Equal"        => wgpu::CompareFunction::Equal,
        "LessEqual"    => wgpu::CompareFunction::LessEqual,
        "Greater"      => wgpu::CompareFunction::Greater,
        "NotEqual"     => wgpu::CompareFunction::NotEqual,
        "GreaterEqual" => wgpu::CompareFunction::GreaterEqual,
        "Always"       => wgpu::CompareFunction::Always,
        other          => panic!("unknown compare function: {other}"),
    }
}
