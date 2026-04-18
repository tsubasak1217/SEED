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
///   binding 0 = u_instances    (ノードごとのワールド行列配列, Storage)
///   binding 1 = u_visible_list (可視インスタンスインデックス列, Storage)
fn model_instance_bgl(device: &wgpu::Device) -> wgpu::BindGroupLayout {
    let storage_entry = |binding: u32| wgpu::BindGroupLayoutEntry {
        binding,
        visibility: wgpu::ShaderStages::VERTEX,
        ty: wgpu::BindingType::Buffer {
            ty:                 wgpu::BufferBindingType::Storage { read_only: true },
            has_dynamic_offset: false,
            min_binding_size:   None,
        },
        count: None,
    };
    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label:   Some("Model Instance BGL"),
        entries: &[storage_entry(0), storage_entry(1)],
    })
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
        let joint_bgl    = uniform_bgl(device, "Joint BGL",            0, wgpu::ShaderStages::VERTEX);

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
    pub pipeline:   wgpu::RenderPipeline,
    pub camera_bgl: wgpu::BindGroupLayout,
    pub model_bgl:  wgpu::BindGroupLayout,
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

        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some("Unlit Line Pipeline"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module:      &shader,
                entry_point: Some("vs_main"),
                buffers: &[wgpu::VertexBufferLayout {
                    array_stride: 28,   // [f32;3] + [f32;4]
                    step_mode:    wgpu::VertexStepMode::Vertex,
                    attributes:   COLOR_VERTEX_ATTRS,
                }],
                compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module:      &shader,
                entry_point: Some("fs_main"),
                targets: &[Some(wgpu::ColorTargetState {
                    format:     surface_format,
                    blend:      Some(wgpu::BlendState::ALPHA_BLENDING),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
                compilation_options: Default::default(),
            }),
            primitive: wgpu::PrimitiveState {
                topology:  wgpu::PrimitiveTopology::LineList,
                ..Default::default()
            },
            depth_stencil: Some(wgpu::DepthStencilState {
                format:              depth_format,
                // デバッグオーバーレイとして深度書き込みしない
                depth_write_enabled: false,
                depth_compare:       wgpu::CompareFunction::LessEqual,
                stencil:             wgpu::StencilState::default(),
                bias:                wgpu::DepthBiasState::default(),
            }),
            multisample: wgpu::MultisampleState::default(),
            multiview:   None,
            cache:       None,
        });

        Self { pipeline, camera_bgl, model_bgl }
    }
}

// ============================================================
//  DrawPipelines — 全パイプラインをまとめた型
// ============================================================

pub struct DrawPipelines {
    pub mesh:         MeshPipeline,
    pub skinned_mesh: SkinnedMeshPipeline,
    pub unlit_line:   UnlitPipeline,
}

impl DrawPipelines {
    pub fn new(
        device:         &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
    ) -> Self {
        Self {
            mesh:         MeshPipeline::new(device, surface_format, depth_format),
            skinned_mesh: SkinnedMeshPipeline::new(device, surface_format, depth_format),
            unlit_line:   UnlitPipeline::new(device, surface_format, depth_format),
        }
    }
}
