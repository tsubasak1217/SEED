// ============================================================
//  pipeline.rs — PBR メッシュ / スキンメッシュ / アンライトパイプライン
// ============================================================

use wgpu::VertexFormat as VF;
use wgpu::VertexAttribute as VA;

// Vertex 属性オフセット (repr(C), 72 bytes)
//  position [f32;3]  offset 0
//  normal   [f32;3]  offset 12
//  tangent  [f32;4]  offset 24
//  uv0      [f32;2]  offset 40
//  uv1      [f32;2]  offset 48
//  color    [f32;4]  offset 56
const MESH_VERTEX_ATTRS: &[VA] = &[
    VA { format: VF::Float32x3, offset: 0,  shader_location: 0 },
    VA { format: VF::Float32x3, offset: 12, shader_location: 1 },
    VA { format: VF::Float32x4, offset: 24, shader_location: 2 },
    VA { format: VF::Float32x2, offset: 40, shader_location: 3 },
    VA { format: VF::Float32x2, offset: 48, shader_location: 4 },
    VA { format: VF::Float32x4, offset: 56, shader_location: 5 },
];

// SkinVertex 属性オフセット (repr(C), 24 bytes)
//  joints  [u16;4]  offset 0
//  weights [f32;4]  offset 8
const SKIN_VERTEX_ATTRS: &[VA] = &[
    VA { format: VF::Uint16x4,  offset: 0, shader_location: 6 },
    VA { format: VF::Float32x4, offset: 8, shader_location: 7 },
];

// ColorVertex 属性オフセット (repr(C), 28 bytes)
//  position [f32;3]  offset 0
//  color    [f32;4]  offset 12
const COLOR_VERTEX_ATTRS: &[VA] = &[
    VA { format: VF::Float32x3, offset: 0,  shader_location: 0 },
    VA { format: VF::Float32x4, offset: 12, shader_location: 1 },
];

// SmoothNormal 属性オフセット (12 bytes)
//  smooth_normal [f32;3]  offset 0   location 8
const SMOOTH_NORMAL_ATTRS: &[VA] = &[
    VA { format: VF::Float32x3, offset: 0, shader_location: 8 },
];

// ============================================================
//  バインドグループレイアウト生成ヘルパー
// ============================================================

fn uniform_bgl(device: &wgpu::Device, label: &str, binding: u32, visibility: wgpu::ShaderStages) -> wgpu::BindGroupLayout {
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some(label),
        entries: &[wgpu::BindGroupLayoutEntry {
            binding,
            visibility,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Uniform,
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        }],
    })
}

fn storage_bgl(device: &wgpu::Device, label: &str, binding: u32, visibility: wgpu::ShaderStages) -> wgpu::BindGroupLayout {
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some(label),
        entries: &[wgpu::BindGroupLayoutEntry {
            binding,
            visibility,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Storage { read_only: true },
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        }],
    })
}

/// モデルインスタンス用 BGL:
///   binding 0 = u_instances (ノードごとのワールド行列配列, Storage read-only)
fn model_instance_bgl(device: &wgpu::Device) -> wgpu::BindGroupLayout {
    storage_bgl(device, "Model Instance BGL", 0, wgpu::ShaderStages::VERTEX)
}

fn texture_entry(binding: u32) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Texture {
            sample_type:    wgpu::TextureSampleType::Float { filterable: true },
            view_dimension: wgpu::TextureViewDimension::D2,
            multisampled:   false,
        },
        count: None,
    }
}

fn sampler_entry(binding: u32) -> wgpu::BindGroupLayoutEntry {
    wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
        count: None,
    }
}

/// マテリアル BGL:
///  0  = MaterialUniform
///  1  = t_base_color        2  = s_base_color
///  3  = t_normal            4  = s_normal
///  5  = t_metallic_rough    6  = s_metallic_rough
///  7  = t_occlusion         8  = s_occlusion
///  9  = t_emissive          10 = s_emissive
fn material_bgl(device: &wgpu::Device) -> wgpu::BindGroupLayout {
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label: Some("Material BGL"),
        entries: &[
            wgpu::BindGroupLayoutEntry {
                binding:    0,
                visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Buffer {
                    ty:                 wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size:   None,
                },
                count: None,
            },
            texture_entry(1),  sampler_entry(2),   // base_color
            texture_entry(3),  sampler_entry(4),   // normal
            texture_entry(5),  sampler_entry(6),   // metallic_roughness
            texture_entry(7),  sampler_entry(8),   // occlusion
            texture_entry(9),  sampler_entry(10),  // emissive
        ],
    })
}

// ============================================================
//  MeshPipeline — PBR スタティックメッシュ
// ============================================================

pub struct MeshPipeline {
    pub pipeline:     wgpu::RenderPipeline,
    pub camera_bgl:   wgpu::BindGroupLayout,
    pub model_bgl:    wgpu::BindGroupLayout,
    pub material_bgl: wgpu::BindGroupLayout,
}

impl MeshPipeline {
    fn new(
        device:         &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
    ) -> Self {
        let common   = include_str!("shader_common.wgsl");
        let vertex   = include_str!("shader_static_vertex.wgsl");
        let fragment = include_str!("shader_fragment.wgsl");
        let src      = format!("{common}{vertex}{fragment}");

        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Mesh Shader"),
            source: wgpu::ShaderSource::Wgsl(src.into()),
        });

        let camera_bgl   = uniform_bgl(device, "Camera BGL", 0, wgpu::ShaderStages::VERTEX | wgpu::ShaderStages::FRAGMENT);
        let model_bgl    = model_instance_bgl(device);
        let material_bgl = material_bgl(device);

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Mesh Pipeline Layout"),
            bind_group_layouts:   &[&camera_bgl, &model_bgl, &material_bgl],
            push_constant_ranges: &[],
        });

        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some("Mesh Pipeline"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module:              &shader,
                entry_point:         Some("vs_main"),
                buffers:             &[wgpu::VertexBufferLayout {
                    array_stride: 72,
                    step_mode:    wgpu::VertexStepMode::Vertex,
                    attributes:   MESH_VERTEX_ATTRS,
                }],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module:      &shader,
                entry_point: Some("fs_main"),
                targets: &[Some(wgpu::ColorTargetState {
                    format:     surface_format,
                    blend:      Some(wgpu::BlendState::REPLACE),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology:           wgpu::PrimitiveTopology::TriangleList,
                front_face:         wgpu::FrontFace::Ccw,   // glTF 準拠
                cull_mode:          Some(wgpu::Face::Back),
                ..Default::default()
            },
            depth_stencil: Some(wgpu::DepthStencilState {
                format:              depth_format,
                depth_write_enabled: true,
                depth_compare:       wgpu::CompareFunction::Less,
                stencil:             wgpu::StencilState::default(),
                bias:                wgpu::DepthBiasState::default(),
            }),
            multisample: wgpu::MultisampleState::default(),
            multiview:   None,
            cache:       None,
        });

        Self { pipeline, camera_bgl, model_bgl, material_bgl }
    }
}

// ============================================================
//  SkinnedMeshPipeline — PBR スキンメッシュ
// ============================================================

pub struct SkinnedMeshPipeline {
    pub pipeline:     wgpu::RenderPipeline,
    pub camera_bgl:   wgpu::BindGroupLayout,
    pub model_bgl:    wgpu::BindGroupLayout,
    pub material_bgl: wgpu::BindGroupLayout,
    pub joint_bgl:    wgpu::BindGroupLayout,
}

impl SkinnedMeshPipeline {
    fn new(
        device:         &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
    ) -> Self {
        let common   = include_str!("shader_common.wgsl");
        let vertex   = include_str!("shader_skinned_vertex.wgsl");
        let fragment = include_str!("shader_fragment.wgsl");
        let src      = format!("{common}{vertex}{fragment}");

        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Skinned Mesh Shader"),
            source: wgpu::ShaderSource::Wgsl(src.into()),
        });

        let camera_bgl   = uniform_bgl(device, "Skinned Camera BGL", 0, wgpu::ShaderStages::VERTEX | wgpu::ShaderStages::FRAGMENT);
        let model_bgl    = model_instance_bgl(device);
        let material_bgl = material_bgl(device);
        // joint_bgl は storage（コンピュートシェーダが書き込むストレージバッファを vertex shader が読む）
        let joint_bgl    = storage_bgl(device, "Joint Storage BGL", 0, wgpu::ShaderStages::VERTEX);

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Skinned Mesh Pipeline Layout"),
            bind_group_layouts:   &[&camera_bgl, &model_bgl, &material_bgl, &joint_bgl],
            push_constant_ranges: &[],
        });

        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some("Skinned Mesh Pipeline"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module:      &shader,
                entry_point: Some("vs_main"),
                buffers: &[
                    wgpu::VertexBufferLayout {       // slot 0: Vertex
                        array_stride: 72,
                        step_mode:    wgpu::VertexStepMode::Vertex,
                        attributes:   MESH_VERTEX_ATTRS,
                    },
                    wgpu::VertexBufferLayout {       // slot 1: SkinVertex
                        array_stride: 24,
                        step_mode:    wgpu::VertexStepMode::Vertex,
                        attributes:   SKIN_VERTEX_ATTRS,
                    },
                ],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module:      &shader,
                entry_point: Some("fs_main"),
                targets: &[Some(wgpu::ColorTargetState {
                    format:     surface_format,
                    blend:      Some(wgpu::BlendState::REPLACE),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology:  wgpu::PrimitiveTopology::TriangleList,
                front_face: wgpu::FrontFace::Ccw,
                cull_mode: Some(wgpu::Face::Back),
                ..Default::default()
            },
            depth_stencil: Some(wgpu::DepthStencilState {
                format:              depth_format,
                depth_write_enabled: true,
                depth_compare:       wgpu::CompareFunction::Less,
                stencil:             wgpu::StencilState::default(),
                bias:                wgpu::DepthBiasState::default(),
            }),
            multisample: wgpu::MultisampleState::default(),
            multiview:   None,
            cache:       None,
        });

        Self { pipeline, camera_bgl, model_bgl, material_bgl, joint_bgl }
    }
}

// ============================================================
//  UnlitPipeline — デバッグ描画（LineList）
// ============================================================

pub struct UnlitPipeline {
    /// 深度テストあり（LessEqual）— デバッグライン用
    pub pipeline:       wgpu::RenderPipeline,
    /// 深度無視（Always）— ギズモ前面描画用
    pub gizmo_pipeline: wgpu::RenderPipeline,
    pub camera_bgl:     wgpu::BindGroupLayout,
    pub model_bgl:      wgpu::BindGroupLayout,
}

impl UnlitPipeline {
    fn new(
        device:         &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
    ) -> Self {
        let src    = include_str!("unlit.wgsl");
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Unlit Shader"),
            source: wgpu::ShaderSource::Wgsl(src.into()),
        });

        // Camera BGL は mesh パイプラインと同じ visibility にする（同じ BindGroup を共用するため）
        let camera_bgl = uniform_bgl(device, "Unlit Camera BGL", 0, wgpu::ShaderStages::VERTEX | wgpu::ShaderStages::FRAGMENT);
        let model_bgl  = uniform_bgl(device, "Unlit Model BGL",  0, wgpu::ShaderStages::VERTEX);

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Unlit Pipeline Layout"),
            bind_group_layouts:   &[&camera_bgl, &model_bgl],
            push_constant_ranges: &[],
        });

        let vertex_layout = wgpu::VertexBufferLayout {
            array_stride: 28,
            step_mode:    wgpu::VertexStepMode::Vertex,
            attributes:   COLOR_VERTEX_ATTRS,
        };
        let color_target = wgpu::ColorTargetState {
            format:     surface_format,
            blend:      Some(wgpu::BlendState::ALPHA_BLENDING),
            write_mask: wgpu::ColorWrites::ALL,
        };

        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some("Unlit Line Pipeline"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module:      &shader,
                entry_point: Some("vs_main"),
                buffers:     &[vertex_layout.clone()],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module:      &shader,
                entry_point: Some("fs_main"),
                targets:     &[Some(color_target.clone())],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology: wgpu::PrimitiveTopology::LineList,
                ..Default::default()
            },
            depth_stencil: Some(wgpu::DepthStencilState {
                format:              depth_format,
                depth_write_enabled: false,
                depth_compare:       wgpu::CompareFunction::LessEqual,
                stencil:             wgpu::StencilState::default(),
                bias:                wgpu::DepthBiasState::default(),
            }),
            multisample: wgpu::MultisampleState::default(),
            multiview:   None,
            cache:       None,
        });

        // ギズモ用：深度無視で常に前面描画
        let gizmo_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some("Gizmo Line Pipeline"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module:      &shader,
                entry_point: Some("vs_main"),
                buffers:     &[vertex_layout],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module:      &shader,
                entry_point: Some("fs_main"),
                targets:     &[Some(color_target)],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology: wgpu::PrimitiveTopology::LineList,
                ..Default::default()
            },
            depth_stencil: Some(wgpu::DepthStencilState {
                format:              depth_format,
                depth_write_enabled: false,
                depth_compare:       wgpu::CompareFunction::Always,
                stencil:             wgpu::StencilState::default(),
                bias:                wgpu::DepthBiasState::default(),
            }),
            multisample: wgpu::MultisampleState::default(),
            multiview:   None,
            cache:       None,
        });

        Self { pipeline, gizmo_pipeline, camera_bgl, model_bgl }
    }
}

// ============================================================
//  CullPipeline — GPU 視錐台カリング コンピュートパイプライン
// ============================================================

/// GPU コンピュートカリングパイプライン。
///
/// Group 0 BGL:
///   0 = cull_data        (array<GpuCullData>,          Storage RO)
///   1 = u_frustum        (FrustumUniform,               Uniform)
///   2 = draw_cmds        (array<DrawIndexedIndirect>,   Storage RW)
///   3 = prim_index_counts(array<u32>,                   Storage RO)
pub struct CullPipeline {
    pub pipeline: wgpu::ComputePipeline,
    pub bgl:      wgpu::BindGroupLayout,
}

impl CullPipeline {
    fn new(device: &wgpu::Device) -> Self {
        let src    = include_str!("cull.wgsl");
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Cull Shader"),
            source: wgpu::ShaderSource::Wgsl(src.into()),
        });

        let make_storage = |binding: u32, read_only: bool| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Storage { read_only },
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
            label:   Some("Cull BGL"),
            entries: &[
                make_storage(0, true),   // cull_data
                make_uniform(1),          // u_frustum
                make_storage(2, false),  // draw_cmds
                make_storage(3, true),   // prim_index_counts
            ],
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Cull Pipeline Layout"),
            bind_group_layouts:   &[&bgl],
            push_constant_ranges: &[],
        });

        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label:               Some("Cull Compute Pipeline"),
            layout:              Some(&layout),
            module:              &shader,
            entry_point:         Some("cs_main"),
            compilation_options: Default::default(),
            cache:               None,
        });

        Self { pipeline, bgl }
    }
}

// ============================================================
//  DepthPrepassPipelines — 深度プリパス専用パイプライン
// ============================================================

/// 深度のみを書き込む軽量パイプライン（カラー出力なし）。
///
/// `MeshPipeline` / `SkinnedMeshPipeline` と同じ BGL を使用するため、
/// 同一の bind group をそのまま流用できる。
pub struct DepthPrepassPipelines {
    pub mesh:    wgpu::RenderPipeline,
    pub skinned: wgpu::RenderPipeline,
}

impl DepthPrepassPipelines {
    fn new(
        device:       &wgpu::Device,
        depth_format: wgpu::TextureFormat,
        // 既存パイプラインの BGL を共用（同じ bind group を流用するため）
        camera_bgl:   &wgpu::BindGroupLayout,
        model_bgl:    &wgpu::BindGroupLayout,
        joint_bgl:    &wgpu::BindGroupLayout,
    ) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Depth Prepass Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("depth_prepass.wgsl").into()),
        });

        let depth_stencil = wgpu::DepthStencilState {
            format:              depth_format,
            depth_write_enabled: true,
            depth_compare:       wgpu::CompareFunction::Less,
            stencil:             wgpu::StencilState::default(),
            bias:                wgpu::DepthBiasState::default(),
        };

        // ── 非スキンメッシュ（BGL: camera + model）──────────────
        let mesh_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Depth Prepass Mesh Layout"),
            bind_group_layouts:   &[camera_bgl, model_bgl],
            push_constant_ranges: &[],
        });
        let mesh = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some("Depth Prepass Mesh"),
            layout: Some(&mesh_layout),
            vertex: wgpu::VertexState {
                module:              &shader,
                entry_point:         Some("vs_mesh"),
                buffers:             &[wgpu::VertexBufferLayout {
                    array_stride: 72,
                    step_mode:    wgpu::VertexStepMode::Vertex,
                    attributes:   MESH_VERTEX_ATTRS,
                }],
                compilation_options: Default::default(),
            },
            fragment: None,  // カラー出力なし
            primitive: wgpu::PrimitiveState {
                topology:  wgpu::PrimitiveTopology::TriangleList,
                front_face: wgpu::FrontFace::Ccw,
                cull_mode: Some(wgpu::Face::Back),
                ..Default::default()
            },
            depth_stencil:  Some(depth_stencil.clone()),
            multisample:    wgpu::MultisampleState::default(),
            multiview:      None,
            cache:          None,
        });

        // ── スキンメッシュ（BGL: camera + model + dummy_material + joint）
        // joint は group 3 に配置するため material 相当のダミー BGL が必要。
        // ここでは model_bgl と同じ layout で空のグループを作る。
        let dummy_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Depth Prepass Dummy BGL"),
            entries: &[],
        });
        let skinned_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Depth Prepass Skinned Layout"),
            bind_group_layouts:   &[camera_bgl, model_bgl, &dummy_bgl, joint_bgl],
            push_constant_ranges: &[],
        });
        let skinned = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some("Depth Prepass Skinned"),
            layout: Some(&skinned_layout),
            vertex: wgpu::VertexState {
                module:      &shader,
                entry_point: Some("vs_skinned"),
                buffers: &[
                    wgpu::VertexBufferLayout {
                        array_stride: 72,
                        step_mode:    wgpu::VertexStepMode::Vertex,
                        attributes:   MESH_VERTEX_ATTRS,
                    },
                    wgpu::VertexBufferLayout {
                        array_stride: 24,
                        step_mode:    wgpu::VertexStepMode::Vertex,
                        attributes:   SKIN_VERTEX_ATTRS,
                    },
                ],
                compilation_options: Default::default(),
            },
            fragment: None,
            primitive: wgpu::PrimitiveState {
                topology:  wgpu::PrimitiveTopology::TriangleList,
                front_face: wgpu::FrontFace::Ccw,
                cull_mode: Some(wgpu::Face::Back),
                ..Default::default()
            },
            depth_stencil:  Some(depth_stencil),
            multisample:    wgpu::MultisampleState::default(),
            multiview:      None,
            cache:          None,
        });

        Self { mesh, skinned }
    }
}

// ============================================================
//  SkinComputePipeline — GPU スキニング コンピュートパイプライン
// ============================================================

/// GPU スキニング用コンピュートパイプライン。
///
/// Group 0: フレームごと（anim_times + SkinParams）
/// Group 1: 静的アニメーションデータ（ロード時 1 回）
/// Group 2: 出力（ジョイント行列バッファ）
pub struct SkinComputePipeline {
    pub pipeline:    wgpu::ComputePipeline,
    pub per_frame_bgl: wgpu::BindGroupLayout,  // group 0
    pub static_bgl:    wgpu::BindGroupLayout,  // group 1
    pub output_bgl:    wgpu::BindGroupLayout,  // group 2
}

impl SkinComputePipeline {
    fn new(device: &wgpu::Device) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Skin Compute Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("skin_compute.wgsl").into()),
        });

        let ro_storage = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Storage { read_only: true },
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        };
        let rw_storage = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Storage { read_only: false },
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        };
        let uniform_entry = |binding: u32| wgpu::BindGroupLayoutEntry {
            binding,
            visibility: wgpu::ShaderStages::COMPUTE,
            ty: wgpu::BindingType::Buffer {
                ty:                 wgpu::BufferBindingType::Uniform,
                has_dynamic_offset: false,
                min_binding_size:   None,
            },
            count: None,
        };

        let per_frame_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Skin PerFrame BGL"),
            entries: &[ro_storage(0), uniform_entry(1)],
        });

        let static_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Skin Static BGL"),
            entries: &[
                ro_storage(0),   // bind_t
                ro_storage(1),   // bind_r
                ro_storage(2),   // bind_s
                ro_storage(3),   // channels
                ro_storage(4),   // timestamps
                ro_storage(5),   // trans_vals
                ro_storage(6),   // rot_vals
                ro_storage(7),   // scale_vals
                ro_storage(8),   // bfs_order
                ro_storage(9),   // parents
                ro_storage(10),  // joint_nodes
                ro_storage(11),  // ibm
            ],
        });

        let output_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Skin Output BGL"),
            entries: &[rw_storage(0)],
        });

        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Skin Compute Layout"),
            bind_group_layouts:   &[&per_frame_bgl, &static_bgl, &output_bgl],
            push_constant_ranges: &[],
        });

        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label:               Some("Skin Compute Pipeline"),
            layout:              Some(&layout),
            module:              &shader,
            entry_point:         Some("cs_main"),
            compilation_options: Default::default(),
            cache:               None,
        });

        Self { pipeline, per_frame_bgl, static_bgl, output_bgl }
    }
}

// ============================================================
//  IdPassPipeline — Actor ID 書き込みパス（R32Uint テクスチャ）
// ============================================================

/// Actor 選択用 ID バッファレンダーパイプライン。
///
/// BGL:
///   group 0 = camera  (uniform, VS+FS)
///   group 1 = model instances (storage read, VS)
///   group 2 = compact instance IDs (storage read, VS)  ← このパイプライン固有
///   group 3 = joint matrices (storage read, VS) — スキンメッシュのみ
pub struct IdPassPipeline {
    pub mesh_pipeline:    wgpu::RenderPipeline,
    pub skinned_pipeline: wgpu::RenderPipeline,
    pub camera_bgl:       wgpu::BindGroupLayout,
    pub model_bgl:        wgpu::BindGroupLayout,
    pub id_data_bgl:      wgpu::BindGroupLayout,
    pub joint_bgl:        wgpu::BindGroupLayout,
}

impl IdPassPipeline {
    fn new(device: &wgpu::Device, depth_format: wgpu::TextureFormat) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("ID Pass Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("id_pass.wgsl").into()),
        });

        let camera_bgl  = uniform_bgl(device, "ID Camera BGL", 0,
                                      wgpu::ShaderStages::VERTEX | wgpu::ShaderStages::FRAGMENT);
        let model_bgl   = model_instance_bgl(device);
        let id_data_bgl = storage_bgl(device, "ID Data BGL", 0, wgpu::ShaderStages::VERTEX);
        let joint_bgl   = storage_bgl(device, "ID Joint BGL", 0, wgpu::ShaderStages::VERTEX);

        let depth_stencil = wgpu::DepthStencilState {
            format:              depth_format,
            depth_write_enabled: false,
            depth_compare:       wgpu::CompareFunction::LessEqual,
            stencil:             wgpu::StencilState::default(),
            bias:                wgpu::DepthBiasState::default(),
        };

        let mesh_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("ID Pass Mesh Layout"),
            bind_group_layouts:   &[&camera_bgl, &model_bgl, &id_data_bgl],
            push_constant_ranges: &[],
        });
        let mesh_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some("ID Pass Mesh"),
            layout: Some(&mesh_layout),
            vertex: wgpu::VertexState {
                module:      &shader,
                entry_point: Some("vs_mesh"),
                buffers: &[wgpu::VertexBufferLayout {
                    array_stride: 72,
                    step_mode:    wgpu::VertexStepMode::Vertex,
                    attributes:   MESH_VERTEX_ATTRS,
                }],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module:      &shader,
                entry_point: Some("fs_main"),
                targets: &[Some(wgpu::ColorTargetState {
                    format:     wgpu::TextureFormat::R32Uint,
                    blend:      None,
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology:   wgpu::PrimitiveTopology::TriangleList,
                front_face: wgpu::FrontFace::Ccw,
                cull_mode:  Some(wgpu::Face::Back),
                ..Default::default()
            },
            depth_stencil: Some(depth_stencil.clone()),
            multisample:   wgpu::MultisampleState::default(),
            multiview:     None,
            cache:         None,
        });

        let skinned_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("ID Pass Skinned Layout"),
            bind_group_layouts:   &[&camera_bgl, &model_bgl, &id_data_bgl, &joint_bgl],
            push_constant_ranges: &[],
        });
        let skinned_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some("ID Pass Skinned"),
            layout: Some(&skinned_layout),
            vertex: wgpu::VertexState {
                module:      &shader,
                entry_point: Some("vs_skinned"),
                buffers: &[
                    wgpu::VertexBufferLayout {
                        array_stride: 72,
                        step_mode:    wgpu::VertexStepMode::Vertex,
                        attributes:   MESH_VERTEX_ATTRS,
                    },
                    wgpu::VertexBufferLayout {
                        array_stride: 24,
                        step_mode:    wgpu::VertexStepMode::Vertex,
                        attributes:   SKIN_VERTEX_ATTRS,
                    },
                ],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module:      &shader,
                entry_point: Some("fs_main"),
                targets: &[Some(wgpu::ColorTargetState {
                    format:     wgpu::TextureFormat::R32Uint,
                    blend:      None,
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology:   wgpu::PrimitiveTopology::TriangleList,
                front_face: wgpu::FrontFace::Ccw,
                cull_mode:  Some(wgpu::Face::Back),
                ..Default::default()
            },
            depth_stencil: Some(depth_stencil),
            multisample:   wgpu::MultisampleState::default(),
            multiview:     None,
            cache:         None,
        });

        Self { mesh_pipeline, skinned_pipeline, camera_bgl, model_bgl, id_data_bgl, joint_bgl }
    }
}

// ============================================================
//  OutlinePipeline — バックフェース膨張法アウトライン
// ============================================================

/// 選択オブジェクトのオレンジアウトライン描画パイプライン。
///
/// BGL:
///   group 0 = camera  (uniform, VS)
///   group 1 = model instances (storage read, VS)
///   group 2 = joint matrices (storage read, VS) — スキンメッシュのみ
pub struct OutlinePipeline {
    /// バックフェース膨張アウトライン（cull_mode=Front, stencil Equal(0)）
    pub mesh_pipeline:    wgpu::RenderPipeline,
    pub skinned_pipeline: wgpu::RenderPipeline,
    /// 選択インスタンスの前面をステンシル=1 で塗るマスクパス（カラー出力なし）
    pub mesh_stencil_pipeline:    wgpu::RenderPipeline,
    pub skinned_stencil_pipeline: wgpu::RenderPipeline,
}

impl OutlinePipeline {
    fn new(
        device:         &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
        camera_bgl:     &wgpu::BindGroupLayout,
        model_bgl:      &wgpu::BindGroupLayout,
        joint_bgl:      &wgpu::BindGroupLayout,
    ) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Outline Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("outline.wgsl").into()),
        });

        // depth_compare: Less + 正のバイアスで背面を遠方へずらす（③ 深度バイアス）。
        // stencil Equal(0): draw_model_indirect が 1 を書いたキャラ内側には描画しない（② ステンシル）。
        // cull_mode: Front + クリップ空間法線膨張: 背面法（① スムーズ法線は vs_mesh/vs_skinned で使用）。
        let stencil_read_only = wgpu::StencilState {
            front: wgpu::StencilFaceState {
                compare:       wgpu::CompareFunction::Equal,
                fail_op:       wgpu::StencilOperation::Keep,
                depth_fail_op: wgpu::StencilOperation::Keep,
                pass_op:       wgpu::StencilOperation::Keep,
            },
            back: wgpu::StencilFaceState {
                compare:       wgpu::CompareFunction::Equal,
                fail_op:       wgpu::StencilOperation::Keep,
                depth_fail_op: wgpu::StencilOperation::Keep,
                pass_op:       wgpu::StencilOperation::Keep,
            },
            read_mask:  0xFF,
            write_mask: 0x00,
        };
        let depth_stencil = wgpu::DepthStencilState {
            format:              depth_format,
            depth_write_enabled: false,
            depth_compare:       wgpu::CompareFunction::LessEqual,
            stencil:             stencil_read_only,
            bias:                wgpu::DepthBiasState::default(),
        };

        // ── 非スキン（group 0=camera, 1=model）──────────────────
        let mesh_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Outline Mesh Layout"),
            bind_group_layouts:   &[camera_bgl, model_bgl],
            push_constant_ranges: &[],
        });
        let mesh_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some("Outline Mesh"),
            layout: Some(&mesh_layout),
            vertex: wgpu::VertexState {
                module:      &shader,
                entry_point: Some("vs_mesh"),
                buffers: &[
                    wgpu::VertexBufferLayout {   // slot 0: 通常頂点バッファ (position など)
                        array_stride: 72,
                        step_mode:    wgpu::VertexStepMode::Vertex,
                        attributes:   MESH_VERTEX_ATTRS,
                    },
                    wgpu::VertexBufferLayout {   // slot 1: スムーズ法線バッファ (location 8)
                        array_stride: 12,
                        step_mode:    wgpu::VertexStepMode::Vertex,
                        attributes:   SMOOTH_NORMAL_ATTRS,
                    },
                ],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module:      &shader,
                entry_point: Some("fs_main"),
                targets: &[Some(wgpu::ColorTargetState {
                    format:     surface_format,
                    blend:      Some(wgpu::BlendState::REPLACE),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology:   wgpu::PrimitiveTopology::TriangleList,
                front_face: wgpu::FrontFace::Ccw,
                cull_mode:  Some(wgpu::Face::Front), // バックフェース膨張：前面をカリング
                ..Default::default()
            },
            depth_stencil:  Some(depth_stencil.clone()),
            multisample:    wgpu::MultisampleState::default(),
            multiview:      None,
            cache:          None,
        });

        // ── スキン（group 0=camera, 1=model, 2=joints）──────────
        let skinned_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Outline Skinned Layout"),
            bind_group_layouts:   &[camera_bgl, model_bgl, joint_bgl],
            push_constant_ranges: &[],
        });
        let skinned_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some("Outline Skinned"),
            layout: Some(&skinned_layout),
            vertex: wgpu::VertexState {
                module:      &shader,
                entry_point: Some("vs_skinned"),
                buffers: &[
                    wgpu::VertexBufferLayout {   // slot 0: 通常頂点バッファ
                        array_stride: 72,
                        step_mode:    wgpu::VertexStepMode::Vertex,
                        attributes:   MESH_VERTEX_ATTRS,
                    },
                    wgpu::VertexBufferLayout {   // slot 1: スキン頂点バッファ
                        array_stride: 24,
                        step_mode:    wgpu::VertexStepMode::Vertex,
                        attributes:   SKIN_VERTEX_ATTRS,
                    },
                    wgpu::VertexBufferLayout {   // slot 2: スムーズ法線バッファ (location 8)
                        array_stride: 12,
                        step_mode:    wgpu::VertexStepMode::Vertex,
                        attributes:   SMOOTH_NORMAL_ATTRS,
                    },
                ],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module:      &shader,
                entry_point: Some("fs_main"),
                targets: &[Some(wgpu::ColorTargetState {
                    format:     surface_format,
                    blend:      Some(wgpu::BlendState::REPLACE),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology:   wgpu::PrimitiveTopology::TriangleList,
                front_face: wgpu::FrontFace::Ccw,
                cull_mode:  Some(wgpu::Face::Front),
                ..Default::default()
            },
            depth_stencil:  Some(depth_stencil),
            multisample:    wgpu::MultisampleState::default(),
            multiview:      None,
            cache:          None,
        });

        // ── ステンシルマスクパイプライン ────────────────────────────
        // 選択インスタンスの可視ピクセルだけにステンシル=1 を書き込む。
        // カラー出力なし、depth_write=false、depth_compare=LessEqual。
        let stencil_write = wgpu::DepthStencilState {
            format:              depth_format,
            depth_write_enabled: false,
            depth_compare:       wgpu::CompareFunction::Always,
            stencil: wgpu::StencilState {
                front: wgpu::StencilFaceState {
                    compare:       wgpu::CompareFunction::Always,
                    fail_op:       wgpu::StencilOperation::Keep,
                    depth_fail_op: wgpu::StencilOperation::Keep,
                    pass_op:       wgpu::StencilOperation::Replace,
                },
                back: wgpu::StencilFaceState {
                    compare:       wgpu::CompareFunction::Always,
                    fail_op:       wgpu::StencilOperation::Keep,
                    depth_fail_op: wgpu::StencilOperation::Keep,
                    pass_op:       wgpu::StencilOperation::Replace,
                },
                read_mask:  0xFF,
                write_mask: 0xFF,
            },
            bias: wgpu::DepthBiasState::default(),
        };

        // fragment: None はカラーアタッチメントありのパスで使えないため、
        // write_mask = empty() で実質カラー非出力のフラグメントシェーダーを使用する。
        let stencil_color_target = wgpu::ColorTargetState {
            format:     surface_format,
            blend:      None,
            write_mask: wgpu::ColorWrites::empty(),
        };

        let mesh_stencil_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some("Outline Mesh Stencil"),
            layout: Some(&mesh_layout),
            vertex: wgpu::VertexState {
                module:      &shader,
                entry_point: Some("vs_stencil_mesh"),
                buffers: &[wgpu::VertexBufferLayout {
                    array_stride: 72,
                    step_mode:    wgpu::VertexStepMode::Vertex,
                    attributes:   &[VA { format: VF::Float32x3, offset: 0, shader_location: 0 }],
                }],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module:      &shader,
                entry_point: Some("fs_main"),
                targets:     &[Some(stencil_color_target.clone())],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology:   wgpu::PrimitiveTopology::TriangleList,
                front_face: wgpu::FrontFace::Ccw,
                cull_mode:  Some(wgpu::Face::Back),
                ..Default::default()
            },
            depth_stencil:  Some(stencil_write.clone()),
            multisample:    wgpu::MultisampleState::default(),
            multiview:      None,
            cache:          None,
        });

        let skinned_stencil_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some("Outline Skinned Stencil"),
            layout: Some(&skinned_layout),
            vertex: wgpu::VertexState {
                module:      &shader,
                entry_point: Some("vs_stencil_skinned"),
                buffers: &[
                    wgpu::VertexBufferLayout {
                        array_stride: 72,
                        step_mode:    wgpu::VertexStepMode::Vertex,
                        attributes:   &[VA { format: VF::Float32x3, offset: 0, shader_location: 0 }],
                    },
                    wgpu::VertexBufferLayout {
                        array_stride: 24,
                        step_mode:    wgpu::VertexStepMode::Vertex,
                        attributes:   SKIN_VERTEX_ATTRS,
                    },
                ],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module:      &shader,
                entry_point: Some("fs_main"),
                targets:     &[Some(stencil_color_target)],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology:   wgpu::PrimitiveTopology::TriangleList,
                front_face: wgpu::FrontFace::Ccw,
                cull_mode:  Some(wgpu::Face::Back),
                ..Default::default()
            },
            depth_stencil:  Some(stencil_write),
            multisample:    wgpu::MultisampleState::default(),
            multiview:      None,
            cache:          None,
        });

        Self { mesh_pipeline, skinned_pipeline, mesh_stencil_pipeline, skinned_stencil_pipeline }
    }
}

// ============================================================
//  DrawPipelines — 全パイプラインをまとめた型
// ============================================================

pub struct DrawPipelines {
    pub mesh:         MeshPipeline,
    pub skinned_mesh: SkinnedMeshPipeline,
    pub unlit_line:   UnlitPipeline,
    pub cull:         CullPipeline,
    pub skin_compute: SkinComputePipeline,
    pub id_pass:      IdPassPipeline,
    pub outline:      OutlinePipeline,
}

impl DrawPipelines {
    pub fn new(
        device:         &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
    ) -> Self {
        let mesh         = MeshPipeline::new(device, surface_format, depth_format);
        let skinned_mesh = SkinnedMeshPipeline::new(device, surface_format, depth_format);
        let unlit_line   = UnlitPipeline::new(device, surface_format, depth_format);
        let cull         = CullPipeline::new(device);
        let skin_compute = SkinComputePipeline::new(device);
        let id_pass      = IdPassPipeline::new(device, depth_format);
        let outline      = OutlinePipeline::new(
            device, surface_format, depth_format,
            &mesh.camera_bgl, &mesh.model_bgl, &skinned_mesh.joint_bgl,
        );
        Self { mesh, skinned_mesh, unlit_line, cull, skin_compute, id_pass, outline }
    }
}
