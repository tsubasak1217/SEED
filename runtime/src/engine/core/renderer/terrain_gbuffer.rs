// ============================================================
//  terrain_gbuffer.rs — 地形レイヤブレンド用 G-Buffer パイプライン（Terrain T2）
//
//  ## 役割（単一責任）
//  「地形メッシュを、レイヤブレンド（スプラット × triplanar）した結果で G-Buffer へ焼く」
//  ためのパイプラインと、レイヤ定義（layers.json）を GPU リソース化する処理だけを持つ。
//  地形の密度・メッシュ化・ペイントは engine/terrain/ の責務であり、本ファイルは触らない。
//
//  ## 既存 deferred 経路への乗せ方（設計判断）
//  新しいライティングを一切書かず、**G-Buffer 書き込み段だけ**を差し替える。
//  合成済みの base_color / normal / roughness / metallic を通常の G-Buffer レイアウトへ
//  出すため、ライティング・シャドウ・SSAO・RT 反射・SSGI は既存パスがそのまま効く。
//
//  ## バインドグループ
//    group0 = camera / group1 = model / group2 = material  … MeshPipeline から借りる
//    group3 = 地形レイヤ定義（本ファイルが定義する専用レイアウト）
//  group2 のマテリアルは地形では参照されないが、shader_common.wgsl を連結する以上
//  レイアウトには残る（バインドは draw 側が既存のマテリアル BG をそのまま行う）。
//  gbuffer.rs のスキンメッシュが group3 を joints に使うのに対し、地形は非スキン確定なので
//  group3 が空いている。ここにレイヤ定義を差すのが最も差分の小さい配置になる。
// ============================================================

use bytemuck::{Pod, Zeroable};
use wgpu::util::DeviceExt;

use crate::engine::core::loader::model::{CullFace, CULL_FACE_VARIANTS};
use crate::engine::terrain::layers::{TerrainLayerSet, TERRAIN_LAYER_COUNT};

use super::pipeline::{get_shader_source, CullPipelineSet, MeshPipeline};

// ─── 調整用定数（マジックナンバー禁止）──────────────────────────────────────

/// triplanar ブレンドの既定の鋭さ。大きいほど 3 平面の切り替わりが硬くなる。
/// 4.0 は地形テクスチャリングで一般的な値（継ぎ目のぼけと引き伸ばしのバランス）。
const DEFAULT_TRIPLANAR_SHARPNESS: f32 = 4.0;

/// レイヤにベースカラーテクスチャが「有る」ことを表す uniform 値。
const LAYER_HAS_TEXTURE: f32 = 1.0;
/// レイヤにベースカラーテクスチャが「無い」（単色レイヤ）ことを表す uniform 値。
const LAYER_NO_TEXTURE: f32 = 0.0;

/// テクスチャ未指定レイヤに割り当てるダミーテクスチャの 1 辺（ピクセル）。
const DUMMY_TEXTURE_SIZE: u32 = 1;
/// ダミーテクスチャの色（白・不透明）。単色レイヤでは base_color がそのまま出る。
const DUMMY_TEXTURE_PIXEL: [u8; 4] = [255, 255, 255, 255];

/// group3（地形レイヤ）のバインディング番号。WGSL 側の @binding と一致必須。
const BINDING_UNIFORM: u32 = 0;
const BINDING_SAMPLER: u32 = 1;
/// レイヤテクスチャの先頭バインディング番号（以降 +1 ずつ 4 層ぶん）。
const BINDING_TEXTURE_BASE: u32 = 2;

// ============================================================
//  GPU uniform レイアウト（WGSL TerrainLayerUniform と一致必須）
// ============================================================

/// レイヤ 1 枚ぶんの GPU パラメータ。
///
/// WGSL の `TerrainLayerParams` と同一レイアウト（vec4 × 2 = 32 バイト）。
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
struct TerrainLayerParamsGpu {
    /// rgb = ベースカラー係数（リニア）, a = テクスチャの有無（0 or 1）
    base_color: [f32; 4],
    /// r = metallic, g = roughness, b = triplanar UV スケール, a = 予約
    surface: [f32; 4],
}

/// レイヤ定義一式の GPU uniform。
///
/// WGSL の `TerrainLayerUniform` と同一レイアウト。
/// `array<TerrainLayerParams, 4>` の要素ストライドは 32 バイト（vec4 アライン 16 の倍数）
/// なので、Rust 側の `[TerrainLayerParamsGpu; 4]` とバイト単位で一致する。
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
struct TerrainLayerUniformGpu {
    layers: [TerrainLayerParamsGpu; TERRAIN_LAYER_COUNT],
    /// x = triplanar ブレンドの鋭さ, yzw = 予約
    params: [f32; 4],
}

impl TerrainLayerUniformGpu {
    /// レイヤ定義（CPU 側）から uniform を組み立てる。
    ///
    /// 定義数が TERRAIN_LAYER_COUNT に満たない場合、余りの層は
    /// 「重み 0 でしか参照されない黒・テクスチャ無し」で埋める（未定義値を残さない）。
    fn from_layer_set(set: &TerrainLayerSet) -> Self {
        let mut layers = [TerrainLayerParamsGpu {
            base_color: [0.0, 0.0, 0.0, LAYER_NO_TEXTURE],
            surface:    [0.0, 1.0, 1.0, 0.0],
        }; TERRAIN_LAYER_COUNT];

        for (i, l) in set.layers.iter().take(TERRAIN_LAYER_COUNT).enumerate() {
            let has_tex = if l.base_color_texture.is_some() {
                LAYER_HAS_TEXTURE
            } else {
                LAYER_NO_TEXTURE
            };
            layers[i] = TerrainLayerParamsGpu {
                base_color: [l.base_color[0], l.base_color[1], l.base_color[2], has_tex],
                surface:    [l.metallic, l.roughness, l.uv_scale, 0.0],
            };
        }

        Self {
            layers,
            params: [DEFAULT_TRIPLANAR_SHARPNESS, 0.0, 0.0, 0.0],
        }
    }
}

// ============================================================
//  TerrainGBufferPipelines — 地形 G-Buffer パイプライン一式
// ============================================================

/// 地形メッシュを G-Buffer へ焼くパイプライン一式（カリング面 3 種）。
///
/// `layer_bgl` は group3（レイヤ定義）のレイアウト。App 側がこのレイアウトから
/// レイヤバインドグループを作り、描画時に渡す（レイヤ定義はアセット読み込みを伴い、
/// パイプライン構築時点ではまだ手元に無いため分離している）。
pub struct TerrainGBufferPipelines {
    /// カリング面 3 種のパイプライン（添字 = `CullFace::index()`）。
    pub pipes: CullPipelineSet,
    /// group3（地形レイヤ定義）のバインドグループレイアウト。
    pub layer_bgl: wgpu::BindGroupLayout,
}

/// 地形 G-Buffer 書き込み用の連結ソースを返す。
///
/// surface.wgsl / surface_gather.wgsl は連結しない（Surface を経由せず直接 MRT を作る）。
/// naga 検証テストはこの並びと一致させること。
pub fn terrain_gbuffer_shader_sources() -> [&'static str; 3] {
    [
        "shader_common.wgsl",
        "shader_static_vertex.wgsl",
        "terrain_gbuffer_write.wgsl",
    ]
}

impl TerrainGBufferPipelines {
    /// 地形 G-Buffer パイプラインを構築する。
    ///
    /// group0〜2 は `mesh_pipeline` の BGL を借りる（gbuffer.rs と同じ方針）。
    /// group3 だけを本ファイルで新規に定義する。
    pub fn new(
        device:        &wgpu::Device,
        mesh_pipeline: &MeshPipeline,
        df:            wgpu::TextureFormat,
        cache:         Option<&wgpu::PipelineCache>,
        color_targets: &[Option<wgpu::ColorTargetState>],
    ) -> Self {
        let layer_bgl = create_layer_bind_group_layout(device);

        let bgls: Vec<&wgpu::BindGroupLayout> = vec![
            &mesh_pipeline.camera_bgl,
            &mesh_pipeline.model_bgl,
            &mesh_pipeline.material_bgl,
            &layer_bgl,
        ];

        let pipes: CullPipelineSet = std::array::from_fn(|i| {
            build_terrain_gbuffer_pipeline(
                device, df, cache, &bgls, color_targets, CULL_FACE_VARIANTS[i],
            )
        });

        Self { pipes, layer_bgl }
    }
}

/// group3（地形レイヤ定義）のバインドグループレイアウトを作る。
///
/// 内訳: uniform 1 本 + サンプラ 1 本 + レイヤテクスチャ 4 枚。
/// テクスチャ配列（binding_array）ではなく個別バインディングにしたのは、
/// レイヤごとに解像度・フォーマットが違ってよいようにするため（2D 配列テクスチャは
/// 全レイヤで同一サイズ・同一フォーマットを強制する）。層数は 4 固定なので
/// 個別バインディングでも管理コストは増えない。
fn create_layer_bind_group_layout(device: &wgpu::Device) -> wgpu::BindGroupLayout {
    let mut entries: Vec<wgpu::BindGroupLayoutEntry> = Vec::with_capacity(2 + TERRAIN_LAYER_COUNT);

    // ─── uniform（レイヤパラメータ）───
    entries.push(wgpu::BindGroupLayoutEntry {
        binding:    BINDING_UNIFORM,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty: wgpu::BindingType::Buffer {
            ty:                 wgpu::BufferBindingType::Uniform,
            has_dynamic_offset: false,
            min_binding_size:   None,
        },
        count: None,
    });

    // ─── サンプラ（全レイヤ共通・リピート＋線形フィルタ）───
    entries.push(wgpu::BindGroupLayoutEntry {
        binding:    BINDING_SAMPLER,
        visibility: wgpu::ShaderStages::FRAGMENT,
        ty:         wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
        count:      None,
    });

    // ─── レイヤテクスチャ 4 枚 ───
    for i in 0..TERRAIN_LAYER_COUNT {
        entries.push(wgpu::BindGroupLayoutEntry {
            binding:    BINDING_TEXTURE_BASE + i as u32,
            visibility: wgpu::ShaderStages::FRAGMENT,
            ty: wgpu::BindingType::Texture {
                sample_type:    wgpu::TextureSampleType::Float { filterable: true },
                view_dimension: wgpu::TextureViewDimension::D2,
                multisampled:   false,
            },
            count: None,
        });
    }

    device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
        label:   Some("terrain_layer_bgl"),
        entries: &entries,
    })
}

/// 地形 G-Buffer パイプラインを 1 本構築する（手動 MRT。gbuffer.rs と同じ手法）。
fn build_terrain_gbuffer_pipeline(
    device:        &wgpu::Device,
    df:            wgpu::TextureFormat,
    cache:         Option<&wgpu::PipelineCache>,
    bgls:          &[&wgpu::BindGroupLayout],
    color_targets: &[Option<wgpu::ColorTargetState>],
    cull_face:     CullFace,
) -> wgpu::RenderPipeline {
    let label = format!("terrain_gbuffer_cull_{}", cull_face.as_str());
    let label = label.as_str();

    // ── シェーダモジュール（ソース連結）──
    let combined: String = terrain_gbuffer_shader_sources()
        .iter()
        .map(|n| get_shader_source(n))
        .collect::<Vec<_>>()
        .join("\n");
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label:  Some(label),
        source: wgpu::ShaderSource::Wgsl(combined.into()),
    });

    // ── パイプラインレイアウト（group0〜2 は借用・group3 は自前）──
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label:                Some(label),
        bind_group_layouts:   bgls,
        push_constant_ranges: &[],
    });

    // ── 頂点バッファレイアウト（通常の mesh_vertex。頂点カラーにスプラットが載る）──
    let vbuffers = [super::pipeline_config::vertex_buffer_layout("mesh_vertex")];

    // ── 深度: 通常の G-Buffer パスと同一（Less・書き込みあり）──
    let depth_stencil = wgpu::DepthStencilState {
        format:              df,
        depth_write_enabled: true,
        depth_compare:       wgpu::CompareFunction::Less,
        stencil:             wgpu::StencilState::default(),
        bias:                wgpu::DepthBiasState::default(),
    };

    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label:  Some(label),
        layout: Some(&layout),
        vertex: wgpu::VertexState {
            module:              &shader,
            entry_point:         Some("vs_main"),
            buffers:             &vbuffers,
            compilation_options: Default::default(),
        },
        fragment: Some(wgpu::FragmentState {
            module:              &shader,
            entry_point:         Some("fs_terrain_gbuffer"),
            targets:             color_targets,
            compilation_options: Default::default(),
        }),
        primitive: wgpu::PrimitiveState {
            topology:   wgpu::PrimitiveTopology::TriangleList,
            front_face: wgpu::FrontFace::Ccw,
            cull_mode:  match cull_face {
                CullFace::Back  => Some(wgpu::Face::Back),
                CullFace::Front => Some(wgpu::Face::Front),
                CullFace::None  => None,
            },
            ..Default::default()
        },
        depth_stencil: Some(depth_stencil),
        multisample:   wgpu::MultisampleState::default(),
        multiview:     None,
        cache,
    })
}

// ============================================================
//  レイヤバインドグループの生成（アセット読み込みを伴う）
// ============================================================

/// レイヤ定義（layers.json 由来）から group3 のバインドグループを作る。
///
/// レイヤのベースカラーテクスチャは `asset_fs` 経由で読み込む（PAK / ファイルシステム
/// のどちらでも動く）。パス未指定・読み込み失敗のレイヤには 1×1 白テクスチャを
/// 割り当て、`base_color` の単色レイヤとして機能させる（アセットが揃っていない
/// 段階でもブレンドが目視できる＝データドリブンな試作を止めない）。
pub fn create_layer_bind_group(
    device: &wgpu::Device,
    queue:  &wgpu::Queue,
    layout: &wgpu::BindGroupLayout,
    set:    &TerrainLayerSet,
) -> wgpu::BindGroup {
    // ─── uniform バッファ ───
    let uniform = TerrainLayerUniformGpu::from_layer_set(set);
    let ubuf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label:    Some("terrain_layer_uniform"),
        contents: bytemuck::bytes_of(&uniform),
        usage:    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
    });

    // ─── サンプラ（ワールド座標由来 UV をタイリングするため Repeat）───
    let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
        label:          Some("terrain_layer_sampler"),
        address_mode_u: wgpu::AddressMode::Repeat,
        address_mode_v: wgpu::AddressMode::Repeat,
        address_mode_w: wgpu::AddressMode::Repeat,
        mag_filter:     wgpu::FilterMode::Linear,
        min_filter:     wgpu::FilterMode::Linear,
        mipmap_filter:  wgpu::FilterMode::Linear,
        ..Default::default()
    });

    // ─── レイヤテクスチャ（未指定は 1×1 白）───
    let mut views: Vec<wgpu::TextureView> = Vec::with_capacity(TERRAIN_LAYER_COUNT);
    for i in 0..TERRAIN_LAYER_COUNT {
        let path = set.layers.get(i).and_then(|l| l.base_color_texture.as_deref());
        views.push(create_layer_texture_view(device, queue, path, i));
    }

    // ─── バインドグループを組む ───
    let mut entries: Vec<wgpu::BindGroupEntry> = Vec::with_capacity(2 + TERRAIN_LAYER_COUNT);
    entries.push(wgpu::BindGroupEntry {
        binding:  BINDING_UNIFORM,
        resource: ubuf.as_entire_binding(),
    });
    entries.push(wgpu::BindGroupEntry {
        binding:  BINDING_SAMPLER,
        resource: wgpu::BindingResource::Sampler(&sampler),
    });
    for (i, v) in views.iter().enumerate() {
        entries.push(wgpu::BindGroupEntry {
            binding:  BINDING_TEXTURE_BASE + i as u32,
            resource: wgpu::BindingResource::TextureView(v),
        });
    }

    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label:   Some("terrain_layer_bg"),
        layout,
        entries: &entries,
    })
}

/// 1 レイヤぶんのベースカラーテクスチャビューを作る。
///
/// `path` が None のとき（またはアセットが読めないとき）は 1×1 白を返す。
fn create_layer_texture_view(
    device: &wgpu::Device,
    queue:  &wgpu::Queue,
    path:   Option<&str>,
    index:  usize,
) -> wgpu::TextureView {
    // ─── ピクセルデータを用意する（未指定なら 1×1 白）───
    let (pixels, width, height) = match path {
        Some(p) => {
            let img = crate::engine::asset_fs::read_image(p);
            let (w, h) = img.dimensions();
            (img.into_raw(), w, h)
        }
        None => (
            DUMMY_TEXTURE_PIXEL.to_vec(),
            DUMMY_TEXTURE_SIZE,
            DUMMY_TEXTURE_SIZE,
        ),
    };

    let size = wgpu::Extent3d {
        width:                 width.max(1),
        height:                height.max(1),
        depth_or_array_layers: 1,
    };
    let label = format!("terrain_layer_tex{index}");
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label:           Some(&label),
        size,
        mip_level_count: 1,
        sample_count:    1,
        dimension:       wgpu::TextureDimension::D2,
        // ベースカラーは sRGB。サンプル時に自動でリニアへ展開される
        // （レイヤ uniform の base_color はリニア係数なので単位系が揃う）。
        format:          wgpu::TextureFormat::Rgba8UnormSrgb,
        usage:           wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
        view_formats:    &[],
    });
    queue.write_texture(
        wgpu::TexelCopyTextureInfo {
            texture:   &texture,
            mip_level: 0,
            origin:    wgpu::Origin3d::ZERO,
            aspect:    wgpu::TextureAspect::All,
        },
        &pixels,
        wgpu::TexelCopyBufferLayout {
            offset:         0,
            bytes_per_row:  Some(4 * size.width),
            rows_per_image: Some(size.height),
        },
        size,
    );

    texture.create_view(&wgpu::TextureViewDescriptor::default())
}

// ============================================================
//  WGSL 静的検証（naga parse + validate）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 地形 G-Buffer 連結 WGSL を naga で parse + validate する。
    /// 連結順は terrain_gbuffer_shader_sources() と一致させること。
    #[test]
    fn terrain_gbuffer_shader_parses_and_validates() {
        let common   = include_str!("shaders/shader_common.wgsl");
        let static_v = include_str!("shaders/shader_static_vertex.wgsl");
        let terrain  = include_str!("shaders/terrain_gbuffer_write.wgsl");

        let src = [common, static_v, terrain].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[terrain_gbuffer] WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator
            .validate(&module)
            .unwrap_or_else(|e| panic!("[terrain_gbuffer] WGSL validate 失敗: {e:?}"));
    }

    /// WGSL の TERRAIN_LAYER_COUNT が Rust 側定数と一致することを検証する。
    /// 不一致だと頂点カラー 4 成分とレイヤ配列長がズレて描画が壊れる。
    #[test]
    fn terrain_layer_count_matches_shader() {
        let src = include_str!("shaders/terrain_gbuffer_write.wgsl");
        let expected = format!("const TERRAIN_LAYER_COUNT: u32 = {TERRAIN_LAYER_COUNT}u;");
        assert!(
            src.contains(&expected),
            "terrain_gbuffer_write.wgsl の TERRAIN_LAYER_COUNT が Rust 側（{TERRAIN_LAYER_COUNT}）と不一致"
        );
    }

    /// uniform のバイトサイズが WGSL 側レイアウト（vec4×2 ×4 層 + vec4）と一致することを検証する。
    #[test]
    fn terrain_layer_uniform_size_matches_wgsl_layout() {
        // TerrainLayerParams = vec4 + vec4 = 32 バイト、4 層 = 128 バイト、params vec4 = 16 バイト。
        const EXPECTED_BYTES: usize = 32 * TERRAIN_LAYER_COUNT + 16;
        assert_eq!(std::mem::size_of::<TerrainLayerUniformGpu>(), EXPECTED_BYTES);
    }
}
