// pipeline_rs — パイプライン定義
//
// レンダーパイプラインは TOML 設定 + WGSL リフレクションで自動構築。
// コンピュートパイプライン（CullPipeline, SkinComputePipeline）は手動定義。

use super::pipeline_config::{RenderPipelineBuilder, parse_compare};

// ============================================================
//  シェーダーソースリゾルバ
// ============================================================

fn get_shader_source(name: &str) -> &'static str {
    match name {
        "shader_common.wgsl"         => include_str!("shader_common.wgsl"),
        "shader_static_vertex.wgsl"  => include_str!("shader_static_vertex.wgsl"),
        "shader_skinned_vertex.wgsl" => include_str!("shader_skinned_vertex.wgsl"),
        "shader_fragment.wgsl"       => include_str!("shader_fragment.wgsl"),
        "unlit.wgsl"                 => include_str!("unlit.wgsl"),
        "depth_prepass.wgsl"         => include_str!("depth_prepass.wgsl"),
        "id_pass.wgsl"               => include_str!("id_pass.wgsl"),
        "outline.wgsl"               => include_str!("outline.wgsl"),
        other => panic!("unknown shader source: {other}"),
    }
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
    fn new(device: &wgpu::Device, sf: wgpu::TextureFormat, df: wgpu::TextureFormat) -> Self {
        let (pipeline, mut bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/mesh.toml"), sf, df)
                .build(get_shader_source);
        let material_bgl = bgls.pop().unwrap();
        let model_bgl    = bgls.pop().unwrap();
        let camera_bgl   = bgls.pop().unwrap();
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
    fn new(device: &wgpu::Device, sf: wgpu::TextureFormat, df: wgpu::TextureFormat) -> Self {
        let (pipeline, mut bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/skinned_mesh.toml"), sf, df)
                .build(get_shader_source);
        let joint_bgl    = bgls.pop().unwrap();
        let material_bgl = bgls.pop().unwrap();
        let model_bgl    = bgls.pop().unwrap();
        let camera_bgl   = bgls.pop().unwrap();
        Self { pipeline, camera_bgl, model_bgl, material_bgl, joint_bgl }
    }
}

// ============================================================
//  UnlitPipeline — デバッグ描画（LineList）
// ============================================================

pub struct UnlitPipeline {
    pub pipeline:       wgpu::RenderPipeline,
    pub gizmo_pipeline: wgpu::RenderPipeline,
    pub camera_bgl:     wgpu::BindGroupLayout,
    pub model_bgl:      wgpu::BindGroupLayout,
}

impl UnlitPipeline {
    fn new(device: &wgpu::Device, sf: wgpu::TextureFormat, df: wgpu::TextureFormat) -> Self {
        let build = |toml: &str| {
            RenderPipelineBuilder::new(device, toml, sf, df).build(get_shader_source)
        };

        let (pipeline, mut bgls) = build(include_str!("pipelines/unlit.toml"));
        let model_bgl  = bgls.pop().unwrap();
        let camera_bgl = bgls.pop().unwrap();

        let (gizmo_pipeline, _) = build(include_str!("pipelines/unlit_gizmo.toml"));

        Self { pipeline, gizmo_pipeline, camera_bgl, model_bgl }
    }
}

// ============================================================
//  DepthPrepassPipelines — 深度プリパス専用パイプライン
// ============================================================

/// 深度のみを書き込む軽量パイプライン（カラー出力なし）。
///
/// MeshPipeline / SkinnedMeshPipeline と同じ BGL レイアウトを独立して生成するが、
/// 同一レイアウトのため既存 BindGroup とそのまま互換。
pub struct DepthPrepassPipelines {
    pub mesh:    wgpu::RenderPipeline,
    pub skinned: wgpu::RenderPipeline,
}

impl DepthPrepassPipelines {
    pub fn new(device: &wgpu::Device, df: wgpu::TextureFormat) -> Self {
        // color_format = "none" なので surface_format は使用されない
        let sf_unused = wgpu::TextureFormat::Bgra8UnormSrgb;

        let (mesh, _) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/depth_prepass_mesh.toml"), sf_unused, df)
                .build(get_shader_source);
        let (skinned, _) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/depth_prepass_skinned.toml"), sf_unused, df)
                .build(get_shader_source);

        Self { mesh, skinned }
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
                make_storage(0, true),
                make_uniform(1),
                make_storage(2, false),
                make_storage(3, true),
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
//  SkinComputePipeline — GPU スキニング コンピュートパイプライン
// ============================================================

pub struct SkinComputePipeline {
    pub pipeline:      wgpu::ComputePipeline,
    pub per_frame_bgl: wgpu::BindGroupLayout,
    pub static_bgl:    wgpu::BindGroupLayout,
    pub output_bgl:    wgpu::BindGroupLayout,
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
                ro_storage(0), ro_storage(1), ro_storage(2), ro_storage(3),
                ro_storage(4), ro_storage(5), ro_storage(6), ro_storage(7),
                ro_storage(8), ro_storage(9), ro_storage(10), ro_storage(11),
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
//  IdPassPipeline — Actor ID 書き込みパス
// ============================================================

pub struct IdPassPipeline {
    pub mesh_pipeline:    wgpu::RenderPipeline,
    pub skinned_pipeline: wgpu::RenderPipeline,
    pub camera_bgl:       wgpu::BindGroupLayout,
    pub model_bgl:        wgpu::BindGroupLayout,
    pub id_data_bgl:      wgpu::BindGroupLayout,
    pub joint_bgl:        wgpu::BindGroupLayout,
}

impl IdPassPipeline {
    fn new(device: &wgpu::Device, sf: wgpu::TextureFormat, df: wgpu::TextureFormat) -> Self {
        let build = |toml: &str| {
            RenderPipelineBuilder::new(device, toml, sf, df).build(get_shader_source)
        };

        let (mesh_pipeline, mut bgls_m) = build(include_str!("pipelines/id_pass_mesh.toml"));
        let id_data_bgl = bgls_m.pop().unwrap();
        let model_bgl   = bgls_m.pop().unwrap();
        let camera_bgl  = bgls_m.pop().unwrap();

        let (skinned_pipeline, mut bgls_s) = build(include_str!("pipelines/id_pass_skinned.toml"));
        let joint_bgl = bgls_s.pop().unwrap();
        // id_data_bgl, model_bgl, camera_bgl は mesh 側と同じレイアウトなので破棄
        let _ = bgls_s;

        Self { mesh_pipeline, skinned_pipeline, camera_bgl, model_bgl, id_data_bgl, joint_bgl }
    }
}

// ============================================================
//  OutlinePipeline — バックフェース膨張法アウトライン
// ============================================================

pub struct OutlinePipeline {
    pub mesh_pipeline:            wgpu::RenderPipeline,
    pub skinned_pipeline:         wgpu::RenderPipeline,
    pub mesh_stencil_pipeline:    wgpu::RenderPipeline,
    pub skinned_stencil_pipeline: wgpu::RenderPipeline,
}

impl OutlinePipeline {
    fn new(device: &wgpu::Device, sf: wgpu::TextureFormat, df: wgpu::TextureFormat) -> Self {
        // stencil Equal(0): アウトライン内側への描画を抑制
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

        // stencil Replace: 選択インスタンス前面にステンシル=1 を書き込む
        let stencil_write = wgpu::StencilState {
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
        };

        let build = |toml: &str, stencil: wgpu::StencilState| {
            RenderPipelineBuilder::new(device, toml, sf, df)
                .with_stencil(stencil)
                .build(get_shader_source)
                .0
        };

        let mesh_pipeline = build(
            include_str!("pipelines/outline_mesh.toml"),
            stencil_read_only.clone(),
        );
        let skinned_pipeline = build(
            include_str!("pipelines/outline_skinned.toml"),
            stencil_read_only,
        );
        let mesh_stencil_pipeline = build(
            include_str!("pipelines/outline_stencil_mesh.toml"),
            stencil_write.clone(),
        );
        let skinned_stencil_pipeline = build(
            include_str!("pipelines/outline_stencil_skinned.toml"),
            stencil_write,
        );

        Self { mesh_pipeline, skinned_pipeline, mesh_stencil_pipeline, skinned_stencil_pipeline }
    }
}

// ============================================================
//  DrawPipelines — 全パイプラインをまとめた型
// ============================================================

pub struct DrawPipelines {
    pub mesh:          MeshPipeline,
    pub skinned_mesh:  SkinnedMeshPipeline,
    pub unlit_line:    UnlitPipeline,
    pub cull:          CullPipeline,
    pub skin_compute:  SkinComputePipeline,
    pub depth_prepass: DepthPrepassPipelines,
    pub id_pass:       IdPassPipeline,
    pub outline:       OutlinePipeline,
}

impl DrawPipelines {
    pub fn new(
        device:         &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
    ) -> Self {
        let mesh          = MeshPipeline::new(device, surface_format, depth_format);
        let skinned_mesh  = SkinnedMeshPipeline::new(device, surface_format, depth_format);
        let unlit_line    = UnlitPipeline::new(device, surface_format, depth_format);
        let cull          = CullPipeline::new(device);
        let skin_compute  = SkinComputePipeline::new(device);
        let depth_prepass = DepthPrepassPipelines::new(device, depth_format);
        let id_pass       = IdPassPipeline::new(device, surface_format, depth_format);
        let outline       = OutlinePipeline::new(device, surface_format, depth_format);
        Self { mesh, skinned_mesh, unlit_line, cull, skin_compute, depth_prepass, id_pass, outline }
    }
}
