// ============================================================
//  terrain_forward.rs — 地形の前方描画パイプライン（deferred=false のフレーム用）
//
//  ## 役割（単一責任）
//  「前方描画（G-Buffer を使わない）のメインパスで、地形メッシュをレイヤブレンドして
//   ライティングまで行う」パイプライン一式を作ることだけを持つ。
//  レイヤ定義の GPU リソース（`TerrainLayerResources`）と group3 のレイアウトは
//  G-Buffer 版（terrain_gbuffer.rs）が持っているものをそのまま借りる（二重に作らない）。
//  描画の振り分け（どのプリミティブをこのパイプラインで描くか）は
//  methods/drawer/model_drawer.rs の `draw_model_indirect` が担う。
//
//  ## なぜ要るのか（直した不具合・段階D の実機確認で発覚）
//  描画品質プリセット `mobile`（Android の既定）は前方描画（`deferred=false`）。以前は
//  前方描画の地形を汎用メッシュのシェーダで描いていたため、地形の頂点カラー（＝レイヤの重み）が
//  そのまま色として塗られ、島が赤・緑のベタ塗りになっていた。デファードは地形専用の
//  G-Buffer 書き込みでレイヤをブレンドしていたので正常だった。
//
//  ## シェーダの連結（連結順の二重管理をしない）
//  フォワードの不透明メッシュ（pipelines/mesh.toml・RT 影の変種は mesh_rt.toml）の
//  `shader_sources` を**その場で読み**、マテリアル採取（surface_gather.wgsl）を外して、
//  エントリ（shader_fragment.wgsl）を「レイヤブレンド本体 ＋ 地形のエントリ」に差し替える。
//  ライティング側（クラスタ・影・GI・シェーディング契約）の連結はメッシュと常に一致する。
//
//  ## バインドグループ（レイアウトは既存の BGL から明示的に組む）
//    group0 = camera / group1 = model / group2 = material … MeshPipeline の BGL
//    group3 = 地形レイヤ定義                           … TerrainGBufferPipelines::layer_bgl
//    group4 = ライト＋シャドウ（RT 変種は ＋TLAS）       … MeshPipeline / RtMeshPipelines の lights_bgl
//  既存の BGL オブジェクトを使うので、メインパスが持っているカメラ・ライトのバインドグループと、
//  G-Buffer 版と同じパレット別のバインドグループがそのまま挿さる。
// ============================================================

use crate::engine::core::loader::model::{CullFace, CULL_FACE_VARIANTS};

use super::pipeline::{get_shader_source, CullPipelineSet, MeshPipeline};
use super::pipeline_config::PipelineConfig;

// ─── 連結の差し替え規則（名前はシェーダソースのリゾルバと一致させる）──────────────

/// フォワードの不透明メッシュの TOML（非 RT 影）。連結順の正典。
const MESH_TOML: &str = include_str!("pipelines/mesh.toml");
/// フォワードの不透明メッシュの TOML（RT 影の変種）。連結順の正典。
const MESH_RT_TOML: &str = include_str!("pipelines/mesh_rt.toml");

/// メッシュの連結から外すソース（マテリアルのテクスチャ採取。地形はレイヤ配列を読むので使わない）。
const MESH_GATHER_SOURCE: &str = "surface_gather.wgsl";
/// メッシュの連結で差し替えるソース（汎用メッシュのフラグメントエントリ）。
const MESH_ENTRY_SOURCE: &str = "shader_fragment.wgsl";
/// 差し替え先 1: レイヤブレンド本体（G-Buffer 版と共有）。
const TERRAIN_BLEND_SOURCE: &str = "terrain_layer_blend.wgsl";
/// 差し替え先 2: 地形の前方描画エントリ。
const TERRAIN_FORWARD_ENTRY_SOURCE: &str = "terrain_forward.wgsl";

/// 頂点シェーダのエントリ（フォワードと共有の shader_static_vertex.wgsl）。
const VERTEX_ENTRY: &str = "vs_main";
/// フラグメントシェーダのエントリ（terrain_forward.wgsl）。
const FRAGMENT_ENTRY: &str = "fs_terrain_forward";
/// 頂点バッファのスロット名（通常の mesh_vertex。頂点カラーにスプラット重みが載る）。
const VERTEX_SLOT: &str = "mesh_vertex";

/// メッシュの TOML の連結から、地形の前方描画の連結を作る【純関数】。
///
/// `surface_gather.wgsl` を外し、`shader_fragment.wgsl` の位置へ
/// `terrain_layer_blend.wgsl` と `terrain_forward.wgsl` を入れる。
/// それ以外（ライティング・影・クラスタ・GI・契約）はメッシュと同じ並びのまま。
///
/// # Panics
/// 埋め込みの TOML が壊れているとき（ビルドに同梱した定数なので、テストで必ず検出される）。
pub fn terrain_forward_shader_sources(rt_shadows: bool) -> Vec<String> {
    let toml_src = if rt_shadows { MESH_RT_TOML } else { MESH_TOML };
    let cfg: PipelineConfig = toml::from_str(toml_src)
        .expect("mesh.toml / mesh_rt.toml のパースに失敗（埋め込みの TOML が壊れている）");
    let mut out = Vec::with_capacity(cfg.shader_sources.len() + 1);
    for name in cfg.shader_sources {
        match name.as_str() {
            MESH_GATHER_SOURCE => continue,
            MESH_ENTRY_SOURCE => {
                out.push(TERRAIN_BLEND_SOURCE.to_string());
                out.push(TERRAIN_FORWARD_ENTRY_SOURCE.to_string());
            }
            _ => out.push(name),
        }
    }
    out
}

// ============================================================
//  TerrainForwardPipelines
// ============================================================

/// 地形の前方描画パイプライン一式（カリング面 3 種。RT 影の変種は RT 対応 GPU だけ）。
pub struct TerrainForwardPipelines {
    /// 非 RT 影の変種（添字 = `CullFace::index()`）。group4 は MeshPipeline の lights_bgl。
    pub pipes: CullPipelineSet,
    /// RT 影の変種（添字 = `CullFace::index()`）。group4 は RtMeshPipelines の lights_bgl（TLAS 入り）。
    /// RT 非対応 GPU では None（呼び出し側は RT 影を使わないので要らない）。
    pub rt: Option<CullPipelineSet>,
}

impl TerrainForwardPipelines {
    /// パイプラインを構築する。
    ///
    /// - `mesh`          : group0〜2 と非 RT の group4 の BGL を借りる元
    /// - `rt_lights_bgl` : RT 影の group4（`RtMeshPipelines::lights_bgl`）。None なら RT 変種を作らない
    /// - `layer_bgl`     : group3（`TerrainGBufferPipelines::layer_bgl`）
    /// - `sf` / `df`     : シーン HDR のカラーフォーマット・深度フォーマット（メッシュと同じ）
    pub fn new(
        device:        &wgpu::Device,
        mesh:          &MeshPipeline,
        rt_lights_bgl: Option<&wgpu::BindGroupLayout>,
        layer_bgl:     &wgpu::BindGroupLayout,
        sf:            wgpu::TextureFormat,
        df:            wgpu::TextureFormat,
        cache:         Option<&wgpu::PipelineCache>,
    ) -> Self {
        let pipes = build_variant(
            device, mesh, &mesh.lights_bgl, layer_bgl, false, sf, df, cache,
        );
        let rt = rt_lights_bgl.map(|lights| {
            build_variant(device, mesh, lights, layer_bgl, true, sf, df, cache)
        });
        Self { pipes, rt }
    }

    /// 影の方式に合うパイプライン一式を返す（RT 変種が無ければ None＝汎用メッシュで描く）。
    pub fn select(&self, rt_shadows: bool) -> Option<&CullPipelineSet> {
        if rt_shadows { self.rt.as_ref() } else { Some(&self.pipes) }
    }
}

/// 1 変種（RT 影の有無）ぶんのカリング面 3 種を作る。シェーダモジュールは 3 種で共有する。
#[allow(clippy::too_many_arguments)]
fn build_variant(
    device:     &wgpu::Device,
    mesh:       &MeshPipeline,
    lights_bgl: &wgpu::BindGroupLayout,
    layer_bgl:  &wgpu::BindGroupLayout,
    rt_shadows: bool,
    sf:         wgpu::TextureFormat,
    df:         wgpu::TextureFormat,
    cache:      Option<&wgpu::PipelineCache>,
) -> CullPipelineSet {
    let variant = if rt_shadows { "terrain_forward_rt" } else { "terrain_forward" };

    // ── シェーダモジュール（メッシュの連結からエントリだけ差し替えたもの）──
    let combined: String = terrain_forward_shader_sources(rt_shadows)
        .iter()
        .map(|n| get_shader_source(n))
        .collect::<Vec<_>>()
        .join("\n");
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label:  Some(variant),
        source: wgpu::ShaderSource::Wgsl(combined.into()),
    });

    // ── パイプラインレイアウト（既存の BGL を並べる。group3 だけ地形のレイヤ定義）──
    let bgls: [&wgpu::BindGroupLayout; 5] = [
        &mesh.camera_bgl,
        &mesh.model_bgl,
        &mesh.material_bgl,
        layer_bgl,
        lights_bgl,
    ];
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label:                Some(variant),
        bind_group_layouts:   &bgls,
        push_constant_ranges: &[],
    });

    std::array::from_fn(|i| {
        build_pipeline(device, &shader, &layout, variant, CULL_FACE_VARIANTS[i], sf, df, cache)
    })
}

/// カリング面 1 種ぶんのパイプラインを作る（色・深度の設定はフォワードの不透明メッシュと同じ）。
#[allow(clippy::too_many_arguments)]
fn build_pipeline(
    device:    &wgpu::Device,
    shader:    &wgpu::ShaderModule,
    layout:    &wgpu::PipelineLayout,
    variant:   &str,
    cull_face: CullFace,
    sf:        wgpu::TextureFormat,
    df:        wgpu::TextureFormat,
    cache:     Option<&wgpu::PipelineCache>,
) -> wgpu::RenderPipeline {
    let label = format!("{variant}_cull_{}", cull_face.as_str());
    let vbuffers = [super::pipeline_config::vertex_buffer_layout(VERTEX_SLOT)];
    // 色: HDR シーンへ上書き（mesh.toml の blend = "Replace" / write_mask = "all" と同じ）。
    let targets = [Some(wgpu::ColorTargetState {
        format:     sf,
        blend:      Some(wgpu::BlendState::REPLACE),
        write_mask: wgpu::ColorWrites::ALL,
    })];
    // 深度: mesh.toml と同じ（Less・書き込みあり・ステンシルは触らない）。
    let depth_stencil = wgpu::DepthStencilState {
        format:              df,
        depth_write_enabled: true,
        depth_compare:       wgpu::CompareFunction::Less,
        stencil:             wgpu::StencilState::default(),
        bias:                wgpu::DepthBiasState::default(),
    };
    device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label:  Some(label.as_str()),
        layout: Some(layout),
        vertex: wgpu::VertexState {
            module:              shader,
            entry_point:         Some(VERTEX_ENTRY),
            buffers:             &vbuffers,
            compilation_options: Default::default(),
        },
        fragment: Some(wgpu::FragmentState {
            module:              shader,
            entry_point:         Some(FRAGMENT_ENTRY),
            targets:             &targets,
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
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 連結した WGSL を naga で parse + validate する（RT 変種はレイクエリを許可して検証する）。
    fn validate(rt_shadows: bool) {
        let name = if rt_shadows { "terrain_forward_rt" } else { "terrain_forward" };
        let src = terrain_forward_shader_sources(rt_shadows)
            .iter()
            .map(|n| get_shader_source(n))
            .collect::<Vec<_>>()
            .join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[{name}] WGSL parse 失敗: {e:?}"));
        let caps = if rt_shadows {
            naga::valid::Capabilities::RAY_QUERY
        } else {
            naga::valid::Capabilities::empty()
        };
        naga::valid::Validator::new(naga::valid::ValidationFlags::all(), caps)
            .validate(&module)
            .unwrap_or_else(|e| panic!("[{name}] WGSL validate 失敗: {e:?}"));
        // エントリポイントが揃っていること（連結の差し替えでエントリが消えていないこと）。
        assert!(module.entry_points.iter().any(|e| e.name == VERTEX_ENTRY), "[{name}] 頂点エントリが無い");
        assert!(module.entry_points.iter().any(|e| e.name == FRAGMENT_ENTRY), "[{name}] 地形のフラグメントエントリが無い");
    }

    /// 非 RT 影の変種が naga の検証を通ること。
    #[test]
    fn terrain_forward_shader_parses_and_validates() {
        validate(false);
    }

    /// RT 影の変種が naga の検証を通ること。
    #[test]
    fn terrain_forward_rt_shader_parses_and_validates() {
        validate(true);
    }

    /// 連結はメッシュの TOML から「採取を外し、エントリを差し替えた」ものであること。
    /// （ライティングの連結がメッシュとずれると、前方描画の地形だけ光り方が変わるため）
    #[test]
    fn sources_follow_mesh_toml_with_terrain_entry() {
        for (rt, toml_src) in [(false, MESH_TOML), (true, MESH_RT_TOML)] {
            let cfg: PipelineConfig = toml::from_str(toml_src).unwrap();
            let sources = terrain_forward_shader_sources(rt);
            // 汎用メッシュの採取・エントリは含まない。
            assert!(!sources.iter().any(|s| s == MESH_GATHER_SOURCE));
            assert!(!sources.iter().any(|s| s == MESH_ENTRY_SOURCE));
            // 地形のブレンド本体 → エントリの順で 1 回ずつ入る。
            let blend = sources.iter().position(|s| s == TERRAIN_BLEND_SOURCE).expect("ブレンド本体が無い");
            let entry = sources.iter().position(|s| s == TERRAIN_FORWARD_ENTRY_SOURCE).expect("エントリが無い");
            assert_eq!(blend + 1, entry, "ブレンド本体の直後にエントリを置く");
            // それ以外はメッシュと同じ並び。
            let rest: Vec<&String> = sources.iter()
                .filter(|s| *s != TERRAIN_BLEND_SOURCE && *s != TERRAIN_FORWARD_ENTRY_SOURCE)
                .collect();
            let mesh_rest: Vec<&String> = cfg.shader_sources.iter()
                .filter(|s| *s != MESH_GATHER_SOURCE && *s != MESH_ENTRY_SOURCE)
                .collect();
            assert_eq!(rest, mesh_rest, "ライティング側の連結がメッシュとずれている（rt={rt}）");
        }
    }
}
