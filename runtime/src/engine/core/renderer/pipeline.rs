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
        "shader_common.wgsl"         => include_str!("shaders/shader_common.wgsl"),
        "shader_static_vertex.wgsl"  => include_str!("shaders/shader_static_vertex.wgsl"),
        "shader_skinned_vertex.wgsl" => include_str!("shaders/shader_skinned_vertex.wgsl"),
        "shader_fragment.wgsl"       => include_str!("shaders/shader_fragment.wgsl"),
        "shadow.wgsl"                => include_str!("shaders/shadow.wgsl"),
        "unlit.wgsl"                 => include_str!("shaders/unlit.wgsl"),
        "gizmo_line.wgsl"            => include_str!("shaders/gizmo_line.wgsl"),
        "depth_prepass.wgsl"         => include_str!("shaders/depth_prepass.wgsl"),
        "id_pass.wgsl"               => include_str!("shaders/id_pass.wgsl"),
        "outline.wgsl"               => include_str!("shaders/outline.wgsl"),
        "sprite.wgsl"                => include_str!("shaders/sprite.wgsl"),
        "sprite_outline.wgsl"        => include_str!("shaders/sprite_outline.wgsl"),
        "canvas_id.wgsl"             => include_str!("shaders/canvas_id.wgsl"),
        "camera_preview_blit.wgsl"   => include_str!("shaders/camera_preview_blit.wgsl"),
        "bar_fill.wgsl"              => include_str!("shaders/bar_fill.wgsl"),
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
    /// group 4: ライト（storage 配列 + メタ uniform）。LightBuffer の bind group 生成に使う。
    pub lights_bgl:   wgpu::BindGroupLayout,
    /// group 5: シャドウ（深度配列 ×2 + 比較サンプラー + 行列 UBO）。
    /// ShadowResources の group 5 bind group 生成に使う（skinned とレイアウト互換）。
    pub shadow_bgl:   wgpu::BindGroupLayout,
    /// group 3 用の空 BindGroup。
    ///
    /// mesh パイプラインは fragment が group 4（ライト）を参照する都合で
    /// レイアウトが 5 グループになり、group 3 は「空の gap BGL」になる。
    /// wgpu はレイアウトに存在する全 group 番号への BindGroup 設定を要求する
    /// （空レイアウトでも未設定のまま draw すると検証エラー）ため、
    /// 非スキン描画では必ずこの空 BG を slot 3 へセットする。
    /// 生成は起動時 1 回のみで毎フレーム使い回す。
    pub empty_bg3:    wgpu::BindGroup,
}

impl MeshPipeline {
    fn new(device: &wgpu::Device, sf: wgpu::TextureFormat, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        let (pipeline, bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/mesh.toml"), sf, df)
                .with_label("mesh_pbr")
                .with_cache(cache)
                .build(get_shader_source);
        // group 番号順 (0, 1, 2, 3=gap, 4=lights, 5=shadow) にイテレートして取り出す。
        // fragment の group 4/5 参照によりレイアウトは 6 グループになり、
        // group 3 はスキンなしメッシュでは空の gap BGL になる。
        let mut it = bgls.into_iter();
        let camera_bgl   = it.next().unwrap(); // group 0
        let model_bgl    = it.next().unwrap(); // group 1
        let material_bgl = it.next().unwrap(); // group 2
        let gap_bgl      = it.next().unwrap(); // group 3（空レイアウト）
        let lights_bgl   = it.next().unwrap(); // group 4
        let shadow_bgl   = it.next().unwrap(); // group 5

        // group 3 の空 BindGroup を 1 個だけ生成して保持する（draw 時の必須セット用）。
        let empty_bg3 = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Mesh Empty BG (group 3)"),
            layout:  &gap_bgl,
            entries: &[],
        });

        Self { pipeline, camera_bgl, model_bgl, material_bgl, lights_bgl, shadow_bgl, empty_bg3 }
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
    fn new(device: &wgpu::Device, sf: wgpu::TextureFormat, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        let (pipeline, bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/skinned_mesh.toml"), sf, df)
                .with_label("skinned_mesh_pbr")
                .with_cache(cache)
                .build(get_shader_source);
        // group 番号順 (0, 1, 2, 3=joints, 4=lights, 5=shadow) にイテレートして取り出す
        let mut it = bgls.into_iter();
        let camera_bgl   = it.next().unwrap(); // group 0
        let model_bgl    = it.next().unwrap(); // group 1
        let material_bgl = it.next().unwrap(); // group 2
        let joint_bgl    = it.next().unwrap(); // group 3
        let _lights_bgl  = it.next().unwrap(); // group 4（LightBuffer は mesh 側 BGL で生成し共用）
        let _shadow_bgl  = it.next().unwrap(); // group 5（ShadowResources は mesh 側 BGL で生成し共用）
        Self { pipeline, camera_bgl, model_bgl, material_bgl, joint_bgl }
    }
}

// ============================================================
//  UnlitPipeline — デバッグ描画（LineList）
// ============================================================

pub struct UnlitPipeline {
    pub pipeline:           wgpu::RenderPipeline,  // LineList, ColorVertex
    pub gizmo_line_pipeline: wgpu::RenderPipeline, // TriangleList, GizmoVertex (太線, depth=Always)
    /// 選択強調用の太線パイプライン。gizmo_line と同じ GizmoVertex quad 展開シェーダを使うが、
    /// 深度は 1px ライン（unlit）と同じ LessEqual にして遮蔽の見え方を揃える
    /// （ギズモの depth=Always とは異なり、可視物の背後では隠れる）。
    pub thick_line_pipeline: wgpu::RenderPipeline, // TriangleList, GizmoVertex (太線, depth=LessEqual)
    pub gizmo_tri_pipeline:  wgpu::RenderPipeline, // TriangleList, ColorVertex (ソリッド)
    pub camera_bgl:          wgpu::BindGroupLayout,
    pub model_bgl:           wgpu::BindGroupLayout,
}

impl UnlitPipeline {
    fn new(device: &wgpu::Device, sf: wgpu::TextureFormat, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        let build = |toml: &str| {
            RenderPipelineBuilder::new(device, toml, sf, df).with_cache(cache).build(get_shader_source)
        };

        let (pipeline, bgls) = build(include_str!("pipelines/unlit.toml"));
        // group 番号順 (0, 1) にイテレートして取り出す
        let mut it = bgls.into_iter();
        let camera_bgl = it.next().unwrap(); // group 0
        let model_bgl  = it.next().unwrap(); // group 1

        let (gizmo_line_pipeline, _) = build(include_str!("pipelines/gizmo_line.toml"));
        let (thick_line_pipeline, _) = build(include_str!("pipelines/unlit_thick_line.toml"));
        let (gizmo_tri_pipeline, _)  = build(include_str!("pipelines/gizmo_tri.toml"));

        Self { pipeline, gizmo_line_pipeline, thick_line_pipeline, gizmo_tri_pipeline, camera_bgl, model_bgl }
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
    pub fn new(device: &wgpu::Device, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        // color_format = "none" なので surface_format は使用されない
        let sf_unused = wgpu::TextureFormat::Bgra8UnormSrgb;

        let (mesh, _) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/depth_prepass_mesh.toml"), sf_unused, df)
                .with_cache(cache)
                .build(get_shader_source);
        let (skinned, _) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/depth_prepass_skinned.toml"), sf_unused, df)
                .with_cache(cache)
                .build(get_shader_source);

        Self { mesh, skinned }
    }
}

// ============================================================
//  ShadowDepthPipelines — シャドウマップ深度専用パイプライン（Phase R2）
// ============================================================

/// シャドウマップへ深度のみ書き込むパイプライン（カラー出力なし）。
///
/// depth_prepass.wgsl を流用し、深度フォーマットは SHADOW_DEPTH_FORMAT。
/// group 0 = シャドウカメラ（light view-proj）, group 1 = モデル行列。
/// skinned は group 3 = joints、group 2 は空 gap（`empty_bg2` を必ずセットする）。
pub struct ShadowDepthPipelines {
    pub mesh:      wgpu::RenderPipeline,
    pub skinned:   wgpu::RenderPipeline,
    /// skinned パイプラインの group 2（空 gap）用 BindGroup。描画時に必須セット。
    pub empty_bg2: wgpu::BindGroup,
}

impl ShadowDepthPipelines {
    pub fn new(device: &wgpu::Device, shadow_df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        // color_format = "none" なので surface_format は使用されない
        let sf_unused = wgpu::TextureFormat::Bgra8UnormSrgb;

        let (mesh, _) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/shadow_depth_mesh.toml"), sf_unused, shadow_df)
                .with_label("shadow_depth_mesh")
                .with_cache(cache)
                .build(get_shader_source);

        let (skinned, bgls_s) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/shadow_depth_skinned.toml"), sf_unused, shadow_df)
                .with_label("shadow_depth_skinned")
                .with_cache(cache)
                .build(get_shader_source);
        // skinned のレイアウトは (0=camera, 1=instances, 2=gap, 3=joints)。
        // group 2 の空 BindGroup を 1 個だけ生成して描画時に使い回す。
        let gap2_bgl = &bgls_s[2];
        let empty_bg2 = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Shadow Depth Empty BG (group 2)"),
            layout:  gap2_bgl,
            entries: &[],
        });

        Self { mesh, skinned, empty_bg2 }
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
    fn new(device: &wgpu::Device, cache: Option<&wgpu::PipelineCache>) -> Self {
        let src    = include_str!("shaders/cull.wgsl");
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
            cache,
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
    fn new(device: &wgpu::Device, cache: Option<&wgpu::PipelineCache>) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Skin Compute Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("shaders/skin_compute.wgsl").into()),
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
            cache,
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
    pub id_base_bgl:      wgpu::BindGroupLayout,
}

impl IdPassPipeline {
    fn new(device: &wgpu::Device, _sf: wgpu::TextureFormat, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        // ID パスはオフスクリーン Rgba32Float テクスチャへ描画するため、
        // サーフェスフォーマット (_sf) ではなく固定フォーマットを使う。
        let build = |toml: &str| {
            RenderPipelineBuilder::new(device, toml, wgpu::TextureFormat::Rgba32Float, df)
                .with_cache(cache)
                .build(get_shader_source)
        };

        // mesh: num_bind_groups=5 → bgls[0..4] = [camera, model, id_data, joint, id_base]
        let (mesh_pipeline, mut bgls_m) = build(include_str!("pipelines/id_pass_mesh.toml"));
        let id_base_bgl  = bgls_m.pop().unwrap(); // group 4
        let _joint_bgl_m = bgls_m.pop().unwrap(); // group 3 (mesh では未使用だが layout は存在)
        let id_data_bgl  = bgls_m.pop().unwrap(); // group 2
        let model_bgl    = bgls_m.pop().unwrap(); // group 1
        let camera_bgl   = bgls_m.pop().unwrap(); // group 0

        // skinned: num_bind_groups=5 → 同様。group 3 の joint_bgl だけ取り出す
        let (skinned_pipeline, mut bgls_s) = build(include_str!("pipelines/id_pass_skinned.toml"));
        let _id_base_bgl_s = bgls_s.pop().unwrap(); // group 4 (mesh 側と同レイアウト)
        let joint_bgl      = bgls_s.pop().unwrap(); // group 3
        let _ = bgls_s;

        Self { mesh_pipeline, skinned_pipeline, camera_bgl, model_bgl, id_data_bgl, joint_bgl, id_base_bgl }
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
    fn new(device: &wgpu::Device, sf: wgpu::TextureFormat, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
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
                .with_cache(cache)
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
//  SpritePipeline — 2D スプライトテクスチャ描画
// ============================================================

/// ワールド空間テクスチャクワッドパイプライン。
///
/// Group 0: CameraUniform（mesh パイプラインと同一レイアウト → camera_buf.bind_group と互換）
/// Group 1: SpriteUniform（モデル行列 + カラー）
/// Group 2: テクスチャ + サンプラー
pub struct SpritePipeline {
    pub pipeline:           wgpu::RenderPipeline,
    /// Group 1: SpriteUniform バインドグループレイアウト
    pub sprite_uniform_bgl: wgpu::BindGroupLayout,
    /// Group 2: テクスチャ＋サンプラー バインドグループレイアウト
    pub tex_bgl:            wgpu::BindGroupLayout,
    /// リニアフィルタリングサンプラー（テクスチャ BG 構築に使用）
    pub sampler:            wgpu::Sampler,
    /// テクスチャ未設定時のフォールバック（白 1×1）
    pub white_fallback_bg:  wgpu::BindGroup,
    /// ユニットクワッド頂点バッファ ([0,1]×[0,1], 2 三角形 = 6 頂点)
    pub unit_quad_vbuf:     wgpu::Buffer,
}

impl SpritePipeline {
    fn new(
        device: &wgpu::Device,
        queue:  &wgpu::Queue,
        sf:     wgpu::TextureFormat,
        df:     wgpu::TextureFormat,
        cache:  Option<&wgpu::PipelineCache>,
    ) -> Self {
        let (pipeline, bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/sprite.toml"), sf, df)
                .with_cache(cache)
                .build(get_shader_source);
        // group 番号順 (0, 1, 2) にイテレートして取り出す
        let mut it = bgls.into_iter();
        let _camera_bgl_compat = it.next(); // group 0: camera_buf.bind_group と互換のため不使用
        let sprite_uniform_bgl = it.next().unwrap(); // group 1
        let tex_bgl            = it.next().unwrap(); // group 2

        // リニアフィルタリングサンプラー
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Sprite Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Linear,
            min_filter:     wgpu::FilterMode::Linear,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        // 白 1×1 フォールバックテクスチャ（テクスチャ未設定時に使用）
        let white_tex = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Sprite White Fallback Tex"),
            size:            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          wgpu::TextureFormat::Rgba8UnormSrgb,
            usage:           wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats:    &[],
        });
        queue.write_texture(
            white_tex.as_image_copy(),
            &[255u8, 255, 255, 255],
            wgpu::ImageDataLayout {
                offset:         0,
                bytes_per_row:  Some(4),
                rows_per_image: Some(1),
            },
            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
        );
        let white_view = white_tex.create_view(&Default::default());
        let white_fallback_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Sprite White Fallback BG"),
            layout:  &tex_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding:  0,
                    resource: wgpu::BindingResource::TextureView(&white_view),
                },
                wgpu::BindGroupEntry {
                    binding:  1,
                    resource: wgpu::BindingResource::Sampler(&sampler),
                },
            ],
        });

        // ユニットクワッド: [0,1]×[0,1], 2 三角形（6頂点）
        // レイアウト: position(vec2) + uv(vec2) = 16 bytes / 頂点
        let quad_verts: &[f32] = &[
            0.0, 0.0,  0.0, 0.0,   // tri1 v0: 左上
            1.0, 0.0,  1.0, 0.0,   // tri1 v1: 右上
            1.0, 1.0,  1.0, 1.0,   // tri1 v2: 右下
            0.0, 0.0,  0.0, 0.0,   // tri2 v0: 左上
            1.0, 1.0,  1.0, 1.0,   // tri2 v1: 右下
            0.0, 1.0,  0.0, 1.0,   // tri2 v2: 左下
        ];
        use wgpu::util::DeviceExt;
        let unit_quad_vbuf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Sprite Unit Quad VBuf"),
            contents: bytemuck::cast_slice(quad_verts),
            usage:    wgpu::BufferUsages::VERTEX,
        });

        Self { pipeline, sprite_uniform_bgl, tex_bgl, sampler, white_fallback_bg, unit_quad_vbuf }
    }
}

// ============================================================
//  CanvasIdPipeline — 2D キャンバスアクター ID 書き込みパス
// ============================================================

/// キャンバスアクター ID パスで GPU に送るユニフォーム（80 bytes）。
///
/// model は GPU 列優先（WGSL mat4x4<f32> レイアウト）で格納する。
/// actor_id は「bitcast<f32>(u32)」として A チャンネルに書き込まれる。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct CanvasIdUniform {
    /// GPU 列優先モデル行列（64 bytes）
    pub model:    [[f32; 4]; 4],
    /// raw アクター ID（0 = 背景、1 以上 = アクター）
    pub actor_id: u32,
    /// 16 バイトアライメント用パディング（12 bytes）
    pub _pad:     [u32; 3],
}

/// キャンバスアクター ID パスパイプライン。
///
/// スプライトと同じユニットクワッド頂点レイアウトを使用し、
/// Rgba32Float テクスチャの A チャンネルにアクター ID を書き込む。
/// スプライトテクスチャのアルファが 0.1 未満のピクセルは discard するため、
/// 透明領域クリックではアクターが選択されない。
pub struct CanvasIdPipeline {
    pub pipeline:         wgpu::RenderPipeline,
    /// コライダーピック面用の深度対応バリアント（collider_pick.toml）。
    /// シェーダー・バインドグループレイアウトは `pipeline` と同一で、
    /// depth_compare = LessEqual / depth_write = true のみ異なる。
    /// メインパスのシーン深度に対してテストしつつ自身も深度を書くため、
    /// コライダー面同士・可視物との重なりが「カメラに近い方優先」で解決される。
    pub pick_depth_pipeline: wgpu::RenderPipeline,
    /// Group 1 BGL: CanvasIdUniform（モデル行列 + アクター ID）
    pub canvas_id_bgl:    wgpu::BindGroupLayout,
    /// Group 2 BGL: テクスチャ + サンプラー（アルファマスク用）
    pub tex_bgl:          wgpu::BindGroupLayout,
    /// スプライトなし時のフォールバック（白 1×1, alpha=1）
    pub white_view:       wgpu::TextureView,
    /// リニアフィルタリングサンプラー
    pub sampler:          wgpu::Sampler,
}

impl CanvasIdPipeline {
    fn new(device: &wgpu::Device, queue: &wgpu::Queue, df: wgpu::TextureFormat, cache: Option<&wgpu::PipelineCache>) -> Self {
        // color_format = "Rgba32Float" のため surface_format は使用しない
        let sf_unused = wgpu::TextureFormat::Bgra8UnormSrgb;
        let (pipeline, bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/canvas_id.toml"), sf_unused, df)
                .with_cache(cache)
                .build(get_shader_source);
        // group 番号順 (0, 1, 2) にイテレートして取り出す
        let mut it = bgls.into_iter();
        let _camera_bgl_compat = it.next(); // group 0: camera_buf.bind_group と互換のため不使用
        let canvas_id_bgl      = it.next().unwrap(); // group 1
        let tex_bgl            = it.next().unwrap(); // group 2

        // コライダーピック面用の深度対応バリアント。
        // 同一シェーダー（canvas_id.wgsl）から生成されるため BGL は上記と等価であり、
        // canvas_id 側の BGL で作った BindGroup をそのまま使用できる
        // （group 0 の camera_buf 互換と同じく wgpu の BGL 等価性に依拠する既存慣例）。
        let (pick_depth_pipeline, _bgls_depth) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/collider_pick.toml"), sf_unused, df)
                .with_cache(cache)
                .build(get_shader_source);

        // 白 1×1 テクスチャ（alpha=1）を作成してフォールバックビューとする
        let white_tex = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("CanvasId White Fallback Tex"),
            size:            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          wgpu::TextureFormat::Rgba8UnormSrgb,
            usage:           wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats:    &[],
        });
        queue.write_texture(
            white_tex.as_image_copy(),
            &[255u8, 255, 255, 255],
            wgpu::ImageDataLayout { offset: 0, bytes_per_row: Some(4), rows_per_image: Some(1) },
            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
        );
        let white_view = white_tex.create_view(&Default::default());

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:      Some("CanvasId Sampler"),
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            ..Default::default()
        });

        Self { pipeline, pick_depth_pipeline, canvas_id_bgl, tex_bgl, white_view, sampler }
    }
}

// ============================================================
//  DrawPipelines — 全パイプラインをまとめた型
// ============================================================

// ============================================================
//  CameraPreviewBlitPipeline — カメラプレビューブリットパイプライン
// ============================================================

/// オフスクリーンのカメラプレビューテクスチャをスクリーン矩形に貼り付けるパイプライン。
///
/// Group 0: BlitRect ユニフォーム (NDC 矩形座標)
/// Group 1: プレビューテクスチャ + サンプラー
pub struct CameraPreviewBlitPipeline {
    /// ブリット描画パイプライン（頂点バッファなし、6 頂点の三角形 2 枚）
    pub pipeline: wgpu::RenderPipeline,
    /// Group 0 レイアウト（BlitRect ユニフォームバッファ）
    pub rect_bgl: wgpu::BindGroupLayout,
    /// Group 1 レイアウト（テクスチャ + サンプラー）
    pub tex_bgl:  wgpu::BindGroupLayout,
}

impl CameraPreviewBlitPipeline {
    fn new(
        device: &wgpu::Device,
        sf:     wgpu::TextureFormat,
        df:     wgpu::TextureFormat,
        cache:  Option<&wgpu::PipelineCache>,
    ) -> Self {
        let (pipeline, bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/camera_preview_blit.toml"), sf, df)
                .with_cache(cache)
                .build(get_shader_source);
        // group 番号順 (0, 1) にイテレートして取り出す
        let mut it = bgls.into_iter();
        let rect_bgl = it.next().unwrap(); // group 0
        let tex_bgl  = it.next().unwrap(); // group 1
        Self { pipeline, rect_bgl, tex_bgl }
    }
}

// ============================================================
//  BarFillPipeline — 帯塗りつぶしパイプライン
// ============================================================

/// 帯塗りつぶし用 GPU ユニフォーム（32 bytes）。
///
/// NDC 座標系 (X: -1=左/+1=右、Y: -1=下/+1=上) で矩形を指定する。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct BarFillUniform {
    /// 塗りカラー（RGBA リニア）
    pub color: [f32; 4],
    /// NDC 左端
    pub x0:    f32,
    /// NDC 下端
    pub y0:    f32,
    /// NDC 右端
    pub x1:    f32,
    /// NDC 上端
    pub y1:    f32,
}

/// LetterBox / PillarBox の帯エリアを単色で塗りつぶすパイプライン。
///
/// 頂点バッファなし（@builtin(vertex_index) で 6 頂点を生成）。
/// depth_write = false / depth_compare = Always のため深度に干渉しない。
pub struct BarFillPipeline {
    /// 帯描画パイプライン
    pub pipeline:     wgpu::RenderPipeline,
    /// Group 0 レイアウト（BarFillUniform バッファ）
    pub uniform_bgl:  wgpu::BindGroupLayout,
}

impl BarFillPipeline {
    fn new(
        device: &wgpu::Device,
        sf:     wgpu::TextureFormat,
        df:     wgpu::TextureFormat,
        cache:  Option<&wgpu::PipelineCache>,
    ) -> Self {
        let (pipeline, bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/bar_fill.toml"), sf, df)
                .with_cache(cache)
                .build(get_shader_source);
        let mut it = bgls.into_iter();
        let uniform_bgl = it.next().unwrap(); // group 0
        Self { pipeline, uniform_bgl }
    }

    /// 指定した NDC 矩形を単色で塗りつぶす。
    ///
    /// - `ndc_x0/y0`: 矩形の左端・下端（NDC）
    /// - `ndc_x1/y1`: 矩形の右端・上端（NDC）
    pub fn draw(
        &self,
        pass:    &mut wgpu::RenderPass<'_>,
        device:  &wgpu::Device,
        color:   [f32; 4],
        ndc_x0:  f32,
        ndc_y0:  f32,
        ndc_x1:  f32,
        ndc_y1:  f32,
    ) {
        use wgpu::util::DeviceExt;
        let uniform = BarFillUniform { color, x0: ndc_x0, y0: ndc_y0, x1: ndc_x1, y1: ndc_y1 };
        // フレームごとに小さなユニフォームバッファを生成して即時描画する
        let buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("BarFill Uniform Buf"),
            contents: bytemuck::bytes_of(&uniform),
            usage:    wgpu::BufferUsages::UNIFORM,
        });
        let bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("BarFill BG"),
            layout:  &self.uniform_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding:  0,
                resource: buf.as_entire_binding(),
            }],
        });
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, &bg, &[]);
        // 6 頂点（2 三角形）を 1 インスタンス描画
        pass.draw(0..6, 0..1);
    }
}

// ============================================================
//  SpriteOutlinePipeline — 選択スプライトのスクリーンスペース均一アウトライン
// ============================================================

/// スプライト選択時のオレンジアウトラインパイプライン。
///
/// sprite_outline.wgsl によりクリップ空間でコーナーを押し出し、
/// 3D モデルアウトラインと同一の OUTLINE_THICKNESS を実現する。
/// テクスチャ不要のためバインドグループは Group 0（Camera）+ Group 1（SpriteUniform）のみ。
pub struct SpriteOutlinePipeline {
    pub pipeline: wgpu::RenderPipeline,
}

impl SpriteOutlinePipeline {
    fn new(
        device: &wgpu::Device,
        sf:     wgpu::TextureFormat,
        df:     wgpu::TextureFormat,
        cache:  Option<&wgpu::PipelineCache>,
    ) -> Self {
        let (pipeline, _bgls) =
            RenderPipelineBuilder::new(device, include_str!("pipelines/sprite_outline.toml"), sf, df)
                .with_cache(cache)
                .build(get_shader_source);
        Self { pipeline }
    }
}

pub struct DrawPipelines {
    pub mesh:                 MeshPipeline,
    pub skinned_mesh:         SkinnedMeshPipeline,
    pub unlit_line:           UnlitPipeline,
    pub cull:                 CullPipeline,
    pub skin_compute:         SkinComputePipeline,
    pub depth_prepass:        DepthPrepassPipelines,
    pub shadow_depth:         ShadowDepthPipelines,
    pub id_pass:              IdPassPipeline,
    pub outline:              OutlinePipeline,
    pub sprite:               SpritePipeline,
    pub sprite_outline:       SpriteOutlinePipeline,
    pub canvas_id:            CanvasIdPipeline,
    pub camera_preview_blit:  CameraPreviewBlitPipeline,
    pub bar_fill:             BarFillPipeline,
}

impl DrawPipelines {
    pub fn new(
        device:         &wgpu::Device,
        queue:          &wgpu::Queue,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
        cache:          Option<&wgpu::PipelineCache>,
    ) -> Self {
        let sf = surface_format;
        let df = depth_format;
        // cache を全パイプラインに渡す。
        // 初回は通常コンパイル（15 秒程度）してキャッシュに記録し、
        // 2 回目以降はキャッシュから復元するためコンパイルをスキップする。
        let mesh                = MeshPipeline::new(device, sf, df, cache);
        let skinned_mesh        = SkinnedMeshPipeline::new(device, sf, df, cache);
        let unlit_line          = UnlitPipeline::new(device, sf, df, cache);
        let cull                = CullPipeline::new(device, cache);
        let skin_compute        = SkinComputePipeline::new(device, cache);
        let depth_prepass       = DepthPrepassPipelines::new(device, df, cache);
        let shadow_depth        = ShadowDepthPipelines::new(device, super::shadow::SHADOW_DEPTH_FORMAT, cache);
        let id_pass             = IdPassPipeline::new(device, sf, df, cache);
        let outline             = OutlinePipeline::new(device, sf, df, cache);
        let sprite              = SpritePipeline::new(device, queue, sf, df, cache);
        let sprite_outline      = SpriteOutlinePipeline::new(device, sf, df, cache);
        let canvas_id           = CanvasIdPipeline::new(device, queue, df, cache);
        let camera_preview_blit = CameraPreviewBlitPipeline::new(device, sf, df, cache);
        let bar_fill            = BarFillPipeline::new(device, sf, df, cache);
        Self { mesh, skinned_mesh, unlit_line, cull, skin_compute, depth_prepass, shadow_depth, id_pass, outline, sprite, sprite_outline, canvas_id, camera_preview_blit, bar_fill }
    }
}
