// ============================================================
//  transparency.rs — 透明描画の整備（Phase R5）
//
//  不透明パス（draw_model_indirect）が Blend マテリアルをスキップした分を、
//  ここで 2 方式のいずれかで描画する:
//    - DistanceSort: 半透明プリミティブを「インスタンス単位」で背面→前面に
//      ソートし、通常フラグメント（fs_main）をアルファブレンド・深度書込なしで描く。
//      少数の半透明物・凸形状で自然。半透明同士の相互交差は苦手。
//    - Wboit: Weighted Blended OIT。順序独立で 2 枚の MRT（accum/reveal）へ蓄積し、
//      フルスクリーン合成でシーン HDR へ重ねる。大量・交差する半透明に強い。
//
//  分類は GpuModel::primitive_alpha_mode（唯一の真実source）で行う。
//  透明パイプラインは mesh/skinned と BGL レイアウトが構造的に同一のため、
//  既存の camera/model/material/joint/lights BindGroup をそのまま流用する
//  （canvas_id/collider_pick 等と同じく wgpu の BGL 等価性に依拠する既存慣例）。
//
//  透明はシャドウマップ影のみ受ける（非 RT ライト BG・非 RT パイプライン）。
//  RT 影の受光は範囲外（TODO）。
// ============================================================

use super::gpu_resources::{GpuModel, InstancedModelBatch, NUM_LODS};
use super::pipeline::CullPipelineSet;
use super::pipeline_config::{vertex_buffer_layout, parse_compare};
use super::post::PostPipeline;
use crate::engine::core::loader::model::{AlphaMode, CullFace, CULL_FACE_VARIANTS};

// ============================================================
//  透明方式・RtPool ターゲット名・フォーマット定数
// ============================================================

/// 透明描画の方式（プロジェクト設定 / IPC で切り替え）。
#[derive(Copy, Clone, PartialEq, Eq, Debug)]
pub enum TransparencyMode {
    /// インスタンス単位の距離ソート（背面→前面）アルファブレンド。既定。
    DistanceSort,
    /// Weighted Blended OIT（順序独立）。
    Wboit,
}

impl Default for TransparencyMode {
    fn default() -> Self { TransparencyMode::DistanceSort }
}

impl TransparencyMode {
    /// 設定文字列 → モード。"wboit" のみ Wboit、その他（"sort" 含む）は DistanceSort。
    pub fn from_str(s: &str) -> Self {
        match s {
            "wboit" => TransparencyMode::Wboit,
            _       => TransparencyMode::DistanceSort,
        }
    }
    /// モード → 設定文字列（保存・ログ用）。
    pub fn as_str(&self) -> &'static str {
        match self {
            TransparencyMode::Wboit        => "wboit",
            TransparencyMode::DistanceSort => "sort",
        }
    }
}

// 屈折の背景は refract_pyramid::RefractPyramid（ミップチェーン付き）が専有する（ガラス表現）。
// 旧 RtPool 単一 RT（RT_REFRACT_BG）は撤去（すりガラスのミップ生成に STORAGE が要り RtPool 非対応）。

/// WBOIT の重み付き色蓄積ターゲット名（RtPool）。
pub const RT_WBOIT_ACCUM:  &str = "wboit_accum";
/// WBOIT の透過率蓄積ターゲット名（RtPool）。
pub const RT_WBOIT_REVEAL: &str = "wboit_reveal";

/// accum ターゲットのフォーマット（HDR 色 × 重み）。
pub const WBOIT_ACCUM_FORMAT:  wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;
/// reveal ターゲットのフォーマット（透過率の積、単チャンネル）。
pub const WBOIT_REVEAL_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::R16Float;

/// accum のクリア値（蓄積前は 0）。加算合成の初期値。
const WBOIT_ACCUM_CLEAR:  wgpu::Color = wgpu::Color { r: 0.0, g: 0.0, b: 0.0, a: 0.0 };
/// reveal のクリア値（透過率 1 = 何も遮っていない）。(Zero, OneMinusSrc) 合成の初期値。
const WBOIT_REVEAL_CLEAR: wgpu::Color = wgpu::Color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 };

// ============================================================
//  TransparentItem — ソート／描画対象の 1 単位
// ============================================================

/// 透明描画 1 件（プリミティブ × インスタンス）。
struct TransparentItem {
    /// `models` スライスへのインデックス。
    model_idx:   usize,
    /// `batch.lod_node_data[lod][node_idx]` 参照用。
    node_idx:    usize,
    /// `gpu.meshes[mesh_idx]` 参照用。
    mesh_idx:    usize,
    /// `mesh.primitives[prim_idx]` 参照用。
    prim_idx:    usize,
    /// スキンメッシュか（パイプライン・頂点バッファ選択）。
    is_skinned:  bool,
    /// LOD レベル。
    lod:         usize,
    /// この LOD のコンパクトインスタンス番号（first_instance に渡す）。
    compact_idx: u32,
    /// カメラからの距離二乗（背面→前面ソートのキー）。
    dist_sq:     f32,
}

/// 描画対象の (GpuModel, Batch) ペアのスライス型エイリアス。
pub type TransparentModels<'a> = [(&'a GpuModel, &'a InstancedModelBatch)];

// ============================================================
//  TransparentPipelines — 透明描画パイプライン一式（起動時 1 回生成）
// ============================================================

/// 距離ソート / WBOIT 両方式のパイプラインと合成資源。
///
/// 各パイプラインはマテリアルのカリング面（Back / Front / None）ごとの 3 バリアントを持つ
/// （添字 = `CullFace::index()`）。半透明でも両面マテリアル（カーテン・葉など）は両面描画が要る。
pub struct TransparentPipelines {
    /// 距離ソート用（不透明と同一シェーダ・BGL、ブレンド有効・深度書込なし）。
    sorted_mesh:    CullPipelineSet,
    sorted_skinned: CullPipelineSet,
    /// WBOIT 用（デュアル MRT、fs_wboit）。
    wboit_mesh:     CullPipelineSet,
    wboit_skinned:  CullPipelineSet,
    /// group 3 の空 gap BindGroup（非スキン描画で必須セット）。
    mesh_empty_bg3: wgpu::BindGroup,
    /// WBOIT 合成パイプライン（accum/reveal → シーン HDR）。
    wboit_composite: PostPipeline,
    /// 合成入力サンプラー（accum/reveal 共通）。
    composite_sampler: wgpu::Sampler,
    /// 透明の group4 レイアウト（ライト＋屈折背景 15/16 の superset）。ソート／WBOIT 共通。
    /// frame_renderer が `LightBuffer::create_transparent_bind_group` でこのレイアウトの
    /// 透明用 group4 BindGroup を毎フレーム生成するために公開する（Phase RT-Translucency）。
    pub lights_bgl: wgpu::BindGroupLayout,
    /// 屈折背景サンプラー（線形・ClampToEdge）。透明 group4 の binding16 に差す。
    pub refract_sampler: wgpu::Sampler,
    /// 屈折オフ時にバインドするダミー背景テクスチャ（1x1）。屈折オンでも背景が未確保のフレームは
    /// これを差すことで、透明パイプラインの group4 レイアウトが常に満たされ描画が壊れない。
    dummy_refract_view: wgpu::TextureView,
}

/// 透明用シェーダソースを解決する（本モジュール自己完結）。
fn resolve_shader(name: &str) -> &'static str {
    match name {
        // Clustered Lighting の共有定義（定数・構造体・索引関数）。shader_common.wgsl の
        // group 4 binding 7〜9 がこの構造体を参照するため、必ず先に連結する（Phase C1）。
        "cluster_common.wgsl"        => include_str!("shaders/cluster_common.wgsl"),
        "pbr_common.wgsl"            => include_str!("shaders/pbr_common.wgsl"),
        "shader_common.wgsl"         => include_str!("shaders/shader_common.wgsl"),
        // ライト（GpuLight/LightMeta）＋クラスタ参照（Phase D3 Phase A で shader_common から分離）。
        "ddgi_common.wgsl"           => include_str!("shaders/ddgi_common.wgsl"),
        "light_common.wgsl"          => include_str!("shaders/light_common.wgsl"),
        "shadow.wgsl"                => include_str!("shaders/shadow.wgsl"),
        "rt_shadow_off.wgsl"         => include_str!("shaders/rt_shadow_off.wgsl"),
        "shader_static_vertex.wgsl"  => include_str!("shaders/shader_static_vertex.wgsl"),
        "shader_skinned_vertex.wgsl" => include_str!("shaders/shader_skinned_vertex.wgsl"),
        // PBR シェーディングの 3 段分割（Surface 定義／マテリアル採取／ライト評価）。
        // 半透明パスも不透明パスと同一のライト評価（lighting_eval.wgsl）を共有する。
        "surface.wgsl"               => include_str!("shaders/surface.wgsl"),
        "surface_gather.wgsl"        => include_str!("shaders/surface_gather.wgsl"),
        "lighting_eval.wgsl"         => include_str!("shaders/lighting_eval.wgsl"),
        "shader_fragment.wgsl"       => include_str!("shaders/shader_fragment.wgsl"),
        "shader_wboit.wgsl"          => include_str!("shaders/shader_wboit.wgsl"),
        // RT-Translucency（Phase RT-Translucency）: 屈折の共有ヘルパ（group4 binding15/16）と
        // 距離ソート専用フラグメント。半透明パスにのみ連結する。
        "refract_common.wgsl"        => include_str!("shaders/refract_common.wgsl"),
        "shader_transparent.wgsl"    => include_str!("shaders/shader_transparent.wgsl"),
        "fullscreen.wgsl"            => include_str!("shaders/fullscreen.wgsl"),
        "post_wboit_composite.wgsl"  => include_str!("shaders/post_wboit_composite.wgsl"),
        other => panic!("unknown transparency shader source: {other}"),
    }
}

/// WBOIT MRT のブレンド定義を組み立てる。
///   accum : One/One 加算（premultiplied color × weight を積む）。
///   reveal: (Zero, OneMinusSrc) — dst *= (1 - src) で透過率を積む。
fn wboit_color_targets() -> [Option<wgpu::ColorTargetState>; 2] {
    // accum: 加算合成。
    let accum = wgpu::ColorTargetState {
        format: WBOIT_ACCUM_FORMAT,
        blend: Some(wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::One,
                dst_factor: wgpu::BlendFactor::One,
                operation:  wgpu::BlendOperation::Add,
            },
            alpha: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::One,
                dst_factor: wgpu::BlendFactor::One,
                operation:  wgpu::BlendOperation::Add,
            },
        }),
        write_mask: wgpu::ColorWrites::ALL,
    };
    // reveal: dst *= (1 - src)。src 係数 Zero、dst 係数 OneMinusSrc。
    let reveal = wgpu::ColorTargetState {
        format: WBOIT_REVEAL_FORMAT,
        blend: Some(wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::Zero,
                dst_factor: wgpu::BlendFactor::OneMinusSrc,
                operation:  wgpu::BlendOperation::Add,
            },
            alpha: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::Zero,
                dst_factor: wgpu::BlendFactor::OneMinusSrc,
                operation:  wgpu::BlendOperation::Add,
            },
        }),
        write_mask: wgpu::ColorWrites::ALL,
    };
    [Some(accum), Some(reveal)]
}

impl TransparentPipelines {
    /// 透明描画パイプライン一式を生成する。
    ///
    /// - `sf` : シーン HDR フォーマット（Rgba16Float）。ソート出力・合成出力に使う。
    /// - `df` : 深度フォーマット（メインパスと共有する Depth24PlusStencil8）。
    pub fn new(
        device: &wgpu::Device,
        sf:     wgpu::TextureFormat,
        df:     wgpu::TextureFormat,
        cache:  Option<&wgpu::PipelineCache>,
    ) -> Self {
        use super::pipeline_config::RenderPipelineBuilder;

        // ── 距離ソート用パイプライン（TOML + リフレクション）─────────
        // 返り値 BGL は group 順 [camera, model, material, gap|joint, lights]。
        // WBOIT の手動レイアウトでも同じ BGL を再利用する。
        // カリング面 3 種のバリアントを、TOML の cull_mode を上書きして 1 ファイルから生成する。
        let build_sorted = |toml_src: &str, label_base: &str| -> (CullPipelineSet, Vec<wgpu::BindGroupLayout>) {
            let mut bgls: Option<Vec<wgpu::BindGroupLayout>> = None;
            let pipes: CullPipelineSet = std::array::from_fn(|i| {
                let face  = CULL_FACE_VARIANTS[i];
                let label = format!("{label_base}_cull_{}", face.as_str());
                let (p, b) = RenderPipelineBuilder::new(device, toml_src, sf, df)
                    .with_label(&label)
                    .with_cull_mode(face.as_str())
                    .with_cache(cache)
                    .build(resolve_shader);
                if bgls.is_none() { bgls = Some(b); }
                p
            });
            (pipes, bgls.expect("transparent: BGL が生成されていない"))
        };
        let (sorted_mesh, mesh_bgls) =
            build_sorted(include_str!("pipelines/transparent_mesh.toml"), "transparent_mesh");
        let (sorted_skinned, skin_bgls) =
            build_sorted(include_str!("pipelines/transparent_skinned.toml"), "transparent_skinned");

        // group 3 の空 gap BindGroup（mesh 用）。非スキン描画で必ず slot 3 にセットする。
        let mesh_empty_bg3 = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Transparent Empty BG (group 3)"),
            layout:  &mesh_bgls[3],
            entries: &[],
        });

        // ── WBOIT パイプライン（デュアル MRT・手動構築）──────────────
        // TOML ビルダーは 1 カラーターゲットのみ対応のため手動で組む。
        // BGL は上のソート用ビルドの結果を再利用（構造同一のため既存 BG が使える）。
        // カリング面 3 種ぶん構築する（添字 = CullFace::index()）。
        // 連結末尾に refract_common.wgsl（屈折ヘルパ＋group4 binding15/16）を足す。
        // これで WBOIT の group4 BGL も距離ソートと同じ「lights + refract」の superset になり、
        // 両者で同一の透明用 group4 BindGroup（create_transparent_bind_group）を使い回せる。
        let wboit_mesh: CullPipelineSet = std::array::from_fn(|i| build_wboit_pipeline(
            device, df, cache, &mesh_bgls,
            &["cluster_common.wgsl", "pbr_common.wgsl", "shader_common.wgsl", "ddgi_common.wgsl", "light_common.wgsl", "shadow.wgsl", "rt_shadow_off.wgsl",
              "shader_static_vertex.wgsl", "surface.wgsl", "surface_gather.wgsl",
              "lighting_eval.wgsl", "shader_fragment.wgsl", "refract_common.wgsl", "shader_wboit.wgsl"],
            &["mesh_vertex"],
            "wboit_mesh",
            CULL_FACE_VARIANTS[i],
        ));
        let wboit_skinned: CullPipelineSet = std::array::from_fn(|i| build_wboit_pipeline(
            device, df, cache, &skin_bgls,
            &["cluster_common.wgsl", "pbr_common.wgsl", "shader_common.wgsl", "ddgi_common.wgsl", "light_common.wgsl", "shadow.wgsl", "rt_shadow_off.wgsl",
              "shader_skinned_vertex.wgsl", "surface.wgsl", "surface_gather.wgsl",
              "lighting_eval.wgsl", "shader_fragment.wgsl", "refract_common.wgsl", "shader_wboit.wgsl"],
            &["mesh_vertex", "skin_vertex"],
            "wboit_skinned",
            CULL_FACE_VARIANTS[i],
        ));

        // ── WBOIT 合成パイプライン（accum/reveal → HDR, AlphaBlending）──
        let wboit_composite = PostPipeline::from_toml(
            device, include_str!("pipelines/post_wboit_composite.toml"), sf, cache, resolve_shader,
        );

        let composite_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("WBOIT Composite Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Nearest,
            min_filter:     wgpu::FilterMode::Nearest,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        // ── 屈折背景（Phase RT-Translucency）の共有資源 ─────────────
        // group4 レイアウト（binding15/16 を含む superset）はソート用ビルドの結果を使う
        // （WBOIT も同じ連結＝同一 BGL 構造。wgpu の BGL 等価性で使い回せる）。
        let lights_bgl = mesh_bgls[4].clone();
        // 屈折背景サンプラー（線形・トライリニア・ClampToEdge。画面外は端色でクランプ）。
        // すりガラスのミップチェーン（refract_pyramid）を roughness 連動でサンプルするため、
        // mipmap_filter=Linear にしてミップ間をトライリニア補間する（ミップ境界のジャンプを防ぐ）。
        let refract_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Refraction Background Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Linear,
            min_filter:     wgpu::FilterMode::Linear,
            mipmap_filter:  wgpu::FilterMode::Linear,
            ..Default::default()
        });
        // 屈折オフ時に差すダミー背景（1x1）。透明パイプラインの group4 を常に満たすため。
        let dummy_refract_view = create_dummy_refract_view(device);

        Self {
            sorted_mesh, sorted_skinned,
            wboit_mesh, wboit_skinned,
            mesh_empty_bg3,
            wboit_composite,
            composite_sampler,
            lights_bgl,
            refract_sampler,
            dummy_refract_view,
        }
    }

    /// 屈折オフ時のダミー背景ビュー（1x1）への参照。屈折用 RT が未確保のフレームで使う。
    pub fn dummy_refract_view(&self) -> &wgpu::TextureView { &self.dummy_refract_view }

    /// WBOIT 合成: accum/reveal を読み、シーン HDR（`hdr_view`）へアルファブレンドで重ねる。
    /// 出力は LoadOp::Load（既存 HDR を保持）。ブルーム／トーンマップより前に呼ぶこと。
    pub fn composite_wboit(
        &self,
        device:      &wgpu::Device,
        encoder:     &mut wgpu::CommandEncoder,
        hdr_view:    &wgpu::TextureView,
        accum_view:  &wgpu::TextureView,
        reveal_view: &wgpu::TextureView,
    ) {
        // group 0: accum + サンプラー、group 1: reveal + サンプラー。
        let bg0 = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("WBOIT Composite BG0 accum"),
            layout:  &self.wboit_composite.bgls[0],
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(accum_view) },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(&self.composite_sampler) },
            ],
        });
        let bg1 = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("WBOIT Composite BG1 reveal"),
            layout:  &self.wboit_composite.bgls[1],
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(reveal_view) },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(&self.composite_sampler) },
            ],
        });

        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("WBOIT Composite"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           hdr_view,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Load, // 既存シーン HDR を保持して重ねる。
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: None,
            occlusion_query_set:      None,
            timestamp_writes:         None,
        });
        pass.set_pipeline(&self.wboit_composite.pipeline);
        pass.set_bind_group(0, &bg0, &[]);
        pass.set_bind_group(1, &bg1, &[]);
        pass.draw(0..3, 0..1);
    }
}

/// WBOIT の手動レンダーパイプラインを構築する（デュアル MRT）。
///
/// `bgls` は距離ソート用ビルドで得た group 順 BGL（構造同一のため再利用）。
/// レイアウトは [camera, model, material, gap|joint, lights] の 5 group。
/// `cull_face` はこのバリアントのカリング面（Back / Front / None）。
#[allow(clippy::too_many_arguments)]
fn build_wboit_pipeline(
    device:         &wgpu::Device,
    df:             wgpu::TextureFormat,
    cache:          Option<&wgpu::PipelineCache>,
    bgls:           &[wgpu::BindGroupLayout],
    shader_sources: &[&str],
    vertex_slots:   &[&str],
    label_base:     &str,
    cull_face:      CullFace,
) -> wgpu::RenderPipeline {
    // バリアントを検証エラーで識別できるようラベルへカリング面を含める。
    let label = format!("{label_base}_cull_{}", cull_face.as_str());
    let label = label.as_str();

    // ── シェーダモジュール（ソース連結）─────────────────────────
    let combined: String = shader_sources.iter()
        .map(|n| resolve_shader(n))
        .collect::<Vec<_>>()
        .join("\n");
    let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label:  Some(label),
        source: wgpu::ShaderSource::Wgsl(combined.into()),
    });

    // ── パイプラインレイアウト（BGL を再利用）───────────────────
    let bgl_refs: Vec<&wgpu::BindGroupLayout> = bgls.iter().collect();
    let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label:                Some(label),
        bind_group_layouts:   &bgl_refs,
        push_constant_ranges: &[],
    });

    // ── 頂点バッファレイアウト ──────────────────────────────────
    let vbuffers: Vec<wgpu::VertexBufferLayout<'static>> =
        vertex_slots.iter().map(|n| vertex_buffer_layout(n)).collect();

    // ── デュアルカラーターゲット（accum + reveal）───────────────
    let targets = wboit_color_targets();

    // ── 深度: LessEqual・書込なし（メインパスの不透明深度でテスト）──
    let depth_stencil = wgpu::DepthStencilState {
        format:              df,
        depth_write_enabled: false,
        depth_compare:       parse_compare("LessEqual"),
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
            entry_point:         Some("fs_wboit"),
            targets:             &targets,
            compilation_options: Default::default(),
        }),
        primitive: wgpu::PrimitiveState {
            topology:   wgpu::PrimitiveTopology::TriangleList,
            front_face: wgpu::FrontFace::Ccw,
            // マテリアルのカリング面バリアント（None = 両面描画）。
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
//  収集・判定・描画
// ============================================================

/// このフレームに描画すべき半透明プリミティブが 1 件でもあるか（安価判定）。
///
/// 可視インスタンス（lod_visible_counts>0）を持ち、その LOD にノードデータのある
/// Blend プリミティブが 1 つでもあれば true。false のとき呼び出し側は透明処理を
/// 一切行わない（gather / パス / RT 確保すべてスキップ）= 全 Opaque シーンで
/// 追加コストゼロを保証する。
pub fn has_transparent(models: &TransparentModels) -> bool {
    for (gpu, batch) in models {
        for lod in 0..NUM_LODS {
            if batch.lod_visible_counts[lod] == 0 { continue; }
            for draw in &batch.node_prim_list {
                if gpu.primitive_alpha_mode(draw.material_idx) != AlphaMode::Blend { continue; }
                if batch.lod_node_data[lod][draw.node_idx].is_some() {
                    return true;
                }
            }
        }
    }
    false
}

/// 全モデルの可視な半透明 (プリミティブ×インスタンス) を収集する。
fn gather_items(models: &TransparentModels, camera_pos: [f32; 3]) -> Vec<TransparentItem> {
    let mut items: Vec<TransparentItem> = Vec::new();

    for (model_idx, (gpu, batch)) in models.iter().enumerate() {
        for lod in 0..NUM_LODS {
            let visible = batch.lod_visible_counts[lod];
            if visible == 0 { continue; }

            // この LOD のコンパクト→元インスタンス対応と距離を事前計算。
            let compacts = &batch.lod_compact_insts[lod];
            for draw in &batch.node_prim_list {
                // Blend 以外は不透明パスが担当済み。
                if gpu.primitive_alpha_mode(draw.material_idx) != AlphaMode::Blend { continue; }
                // この LOD にノードデータが無ければスキップ。
                if batch.lod_node_data[lod][draw.node_idx].is_none() { continue; }

                for compact_idx in 0..visible as usize {
                    let orig = compacts[compact_idx];
                    // インスタンス重心とカメラの距離二乗（ソートキー）。
                    let dist_sq = batch.instance_centroid(orig)
                        .map(|c| {
                            let dx = c[0] - camera_pos[0];
                            let dy = c[1] - camera_pos[1];
                            let dz = c[2] - camera_pos[2];
                            dx * dx + dy * dy + dz * dz
                        })
                        .unwrap_or(0.0);
                    items.push(TransparentItem {
                        model_idx,
                        node_idx:    draw.node_idx,
                        mesh_idx:    draw.mesh_idx,
                        prim_idx:    draw.prim_idx,
                        is_skinned:  draw.is_skinned,
                        lod,
                        compact_idx: compact_idx as u32,
                        dist_sq,
                    });
                }
            }
        }
    }
    items
}

/// 1 アイテムを描画する（ソート／WBOIT 共通）。パイプライン**方式**は呼び出し側が選び、
/// その中の**カリング面バリアント**は本関数がマテリアルから選ぶ。
///
/// 距離ソートは描画順が距離で決まるためカリング面でまとめられない（＝アイテムごとに
/// set_pipeline する従来どおりの挙動）。バリアント選択が増えても set_pipeline 回数は変わらない。
#[allow(clippy::too_many_arguments)]
fn draw_one<'p>(
    pass:       &mut wgpu::RenderPass<'p>,
    item:       &TransparentItem,
    models:     &'p TransparentModels<'p>,
    camera_bg:  &'p wgpu::BindGroup,
    lights_bg:  &'p wgpu::BindGroup,
    mesh_pipes: &'p CullPipelineSet,
    skin_pipes: &'p CullPipelineSet,
    empty_bg3:  &'p wgpu::BindGroup,
) {
    let (gpu, batch) = &models[item.model_idx];
    let Some((_, model_bg)) = batch.lod_node_data[item.lod][item.node_idx].as_ref() else { return };
    let prim = &gpu.meshes[item.mesh_idx].primitives[item.prim_idx];

    // マテリアル BG（プリミティブの material_index、無ければ default）。
    let mat_bg: &wgpu::BindGroup = prim.material_index
        .and_then(|mi| gpu.materials.get(mi))
        .map(|m| &m.bind_group)
        .unwrap_or(&gpu.default_material.bind_group);

    // カリング面バリアントを選ぶ（両面マテリアルの半透明を裏面ごと描くため）。
    let cull_idx   = gpu.primitive_cull_face(prim.material_index).index();
    let mesh_pipe  = &mesh_pipes[cull_idx];
    let skin_pipe  = &skin_pipes[cull_idx];

    if item.is_skinned {
        pass.set_pipeline(skin_pipe);
        pass.set_bind_group(0, camera_bg, &[]);
        pass.set_bind_group(1, model_bg, &[]);
        pass.set_bind_group(2, mat_bg, &[]);
        // group 3: GPU スキニングが書き込んだジョイント BG。
        let jbg = batch.joint_vs_bg(item.lod).unwrap_or(&gpu.identity_joints_bg);
        pass.set_bind_group(3, jbg, &[]);
    } else {
        pass.set_pipeline(mesh_pipe);
        pass.set_bind_group(0, camera_bg, &[]);
        pass.set_bind_group(1, model_bg, &[]);
        pass.set_bind_group(2, mat_bg, &[]);
        pass.set_bind_group(3, empty_bg3, &[]); // 空 gap（必須セット）
    }
    pass.set_bind_group(4, lights_bg, &[]);

    pass.set_vertex_buffer(0, prim.vertex_buffer.slice(..));
    if item.is_skinned {
        pass.set_vertex_buffer(1, prim.skin_vertex_buffer.as_ref().unwrap().slice(..));
    }

    let (idx_buf, idx_count) = prim.get_lod_index_buffer(item.lod);
    pass.set_index_buffer(idx_buf.slice(..), wgpu::IndexFormat::Uint32);
    // first_instance=compact_idx（INDIRECT_FIRST_INSTANCE 有効・outline 等で実証済み）。
    // 頂点シェーダの @builtin(instance_index) がこの値になり u_instances[compact_idx] を読む。
    pass.draw_indexed(0..idx_count, 0, item.compact_idx..item.compact_idx + 1);
}

/// 距離ソート方式で半透明を描画する（メインパス内、不透明描画の直後）。
/// アイテムを背面→前面（dist_sq 降順）にソートして 1 インスタンスずつ描く。
pub fn draw_sorted<'p>(
    pass:       &mut wgpu::RenderPass<'p>,
    models:     &'p TransparentModels<'p>,
    camera_bg:  &'p wgpu::BindGroup,
    lights_bg:  &'p wgpu::BindGroup,
    tp:         &'p TransparentPipelines,
    camera_pos: [f32; 3],
) {
    let mut items = gather_items(models, camera_pos);
    if items.is_empty() { return; }
    // 背面→前面（遠い順）。アルファブレンドは描画順に依存するため。
    items.sort_by(|a, b| b.dist_sq.partial_cmp(&a.dist_sq).unwrap_or(std::cmp::Ordering::Equal));

    for item in &items {
        draw_one(
            pass, item, models, camera_bg, lights_bg,
            &tp.sorted_mesh, &tp.sorted_skinned, &tp.mesh_empty_bg3,
        );
    }
}

/// WBOIT 方式で半透明を描画する（accum/reveal パス内）。順序非依存のためソート不要。
pub fn draw_wboit<'p>(
    pass:       &mut wgpu::RenderPass<'p>,
    models:     &'p TransparentModels<'p>,
    camera_bg:  &'p wgpu::BindGroup,
    lights_bg:  &'p wgpu::BindGroup,
    tp:         &'p TransparentPipelines,
    camera_pos: [f32; 3],
) {
    let items = gather_items(models, camera_pos);
    for item in &items {
        draw_one(
            pass, item, models, camera_bg, lights_bg,
            &tp.wboit_mesh, &tp.wboit_skinned, &tp.mesh_empty_bg3,
        );
    }
}

/// 屈折オフ時に差すダミー背景テクスチャ（1x1, HDR フォーマット）を作りビューを返す。
/// 中身は不定（屈折オフのフラグメントはサンプルしない）。透明パイプラインの group4 binding15 を
/// 常に満たすためだけに存在する。
fn create_dummy_refract_view(device: &wgpu::Device) -> wgpu::TextureView {
    let tex = device.create_texture(&wgpu::TextureDescriptor {
        label:           Some("Refraction Dummy 1x1"),
        size:            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
        mip_level_count: 1,
        sample_count:    1,
        dimension:       wgpu::TextureDimension::D2,
        // scene_hdr と同じ Rgba16Float（filterable float）。屈折用 RT のコピー元と揃える。
        format:          WBOIT_ACCUM_FORMAT,
        usage:           wgpu::TextureUsages::TEXTURE_BINDING,
        view_formats:    &[],
    });
    tex.create_view(&wgpu::TextureViewDescriptor::default())
}

// ============================================================
//  WGSL 静的検証（naga parse + validate）
// ============================================================
#[cfg(test)]
mod tests {
    /// 透明系の連結 WGSL（WBOIT mesh/skinned・合成）を naga で parse + validate する。
    /// 連結順は transparency.rs のリゾルバ・TOML と一致させること。
    #[test]
    fn transparency_shaders_parse_and_validate() {
        // Clustered Lighting の共有定義（shader_common.wgsl の group4 binding7〜9 が
        // ClusterCell / ClusterParams を参照するため、必ず先に連結する）。
        let cluster    = include_str!("shaders/cluster_common.wgsl");
        let common     = include_str!("shaders/shader_common.wgsl");
        let pbr_c      = include_str!("shaders/pbr_common.wgsl");
        let light_c    = include_str!("shaders/light_common.wgsl");
        let ddgi_c     = include_str!("shaders/ddgi_common.wgsl");
        let shadow     = include_str!("shaders/shadow.wgsl");
        let rt_off     = include_str!("shaders/rt_shadow_off.wgsl");
        let static_v   = include_str!("shaders/shader_static_vertex.wgsl");
        let skin_v     = include_str!("shaders/shader_skinned_vertex.wgsl");
        // PBR シェーディングの 3 段分割（Surface / 採取 / ライト評価）。
        let surf       = include_str!("shaders/surface.wgsl");
        let gather     = include_str!("shaders/surface_gather.wgsl");
        let light_eval = include_str!("shaders/lighting_eval.wgsl");
        let frag       = include_str!("shaders/shader_fragment.wgsl");
        let wboit      = include_str!("shaders/shader_wboit.wgsl");
        // RT-Translucency: 屈折ヘルパ（group4 binding15/16）＋距離ソート専用フラグメント。
        let refract    = include_str!("shaders/refract_common.wgsl");
        let transp     = include_str!("shaders/shader_transparent.wgsl");
        let fullscreen = include_str!("shaders/fullscreen.wgsl");
        let composite  = include_str!("shaders/post_wboit_composite.wgsl");

        // 連結順は transparency.rs のリゾルバ・TOML と一致させること。
        // 距離ソート（fs_transparent_sorted）と WBOIT（fs_wboit）はいずれも refract_common を含み、
        // group4 に屈折背景（binding15/16）を宣言する superset レイアウトになる。
        let variants: [(&str, Vec<&str>); 4] = [
            ("sorted_mesh",           vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_off, static_v, surf, gather, light_eval, frag, refract, transp]),
            ("wboit_mesh",            vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_off, static_v, surf, gather, light_eval, frag, refract, wboit]),
            ("wboit_skinned",         vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_off, skin_v,   surf, gather, light_eval, frag, refract, wboit]),
            ("post_wboit_composite",  vec![fullscreen, composite]),
        ];

        for (name, parts) in variants {
            let src = parts.join("\n");
            let module = naga::front::wgsl::parse_str(&src)
                .unwrap_or_else(|e| panic!("[{name}] WGSL parse 失敗: {e:?}"));
            let mut validator = naga::valid::Validator::new(
                naga::valid::ValidationFlags::all(),
                naga::valid::Capabilities::empty(),
            );
            validator
                .validate(&module)
                .unwrap_or_else(|e| panic!("[{name}] WGSL validate 失敗: {e:?}"));
        }
    }

    /// grab-pass（屈折の背景置き換え合成）ゲートの判定表を検証する回帰テスト。
    ///
    /// refract_common.wgsl の glass_composite が grab-pass に入る条件は
    ///   material_refracts = (ior > 1.0 + IOR_EPSILON) || (transmission > 0.0)
    /// であり、素の Blend（ior==1.0 かつ transmission==0）は必ず通常アルファブレンド経路
    /// （!refract_on）へ落ちる。ここでの狙いは「素の Blend が屈折 grab-pass に漏れて
    /// 背景コピーで dst を上書きし a_eff=1 になる＝描画順が壊れる」回帰の再発防止。
    ///
    /// WGSL は cargo からは実行できないため、(1) 同一定数・同一述語を Rust でミラーして
    /// 判定表を固定し、(2) その述語がシェーダ本文に実在することをソース照合で担保する
    /// （ミラーがシェーダから静かに乖離しないようにする）二段構えで検証する。
    #[test]
    fn grab_pass_gate_truth_table() {
        // refract_common.wgsl の IOR_EPSILON と一致させること（下のソース照合で担保）。
        const IOR_EPSILON: f32 = 1.0e-4;
        // シェーダ本文の material_refracts と同一の述語（屈折/透過するマテリアルか）。
        let material_refracts = |ior: f32, transmission: f32| -> bool {
            ior > 1.0 + IOR_EPSILON || transmission > 0.0
        };

        // 素の Blend（ior==1, transmission==0）→ 屈折しない＝通常アルファブレンド経路。
        assert!(!material_refracts(1.0, 0.0), "素の Blend は grab-pass に入らないこと");
        // 浮動小数の丸め（1.0 直近）も非屈折側へ吸収されること。
        assert!(!material_refracts(1.0 + IOR_EPSILON * 0.5, 0.0), "ior≈1.0 は非屈折側");
        // ガラス（ior>1）→ 屈折 grab-pass 経路。
        assert!(material_refracts(1.5, 0.0), "ior>1 は屈折経路");
        assert!(material_refracts(1.33, 0.0), "水(ior=1.33)も屈折経路");
        // 透過率>0（色付きガラス越し）→ ior==1 でも grab-pass 経路。
        assert!(material_refracts(1.0, 0.5), "transmission>0 は屈折経路(ior=1でも)");
        assert!(material_refracts(1.5, 1.0), "ガラス＋全透過も屈折経路");

        // ミラーがシェーダ実体と乖離していないことを保証する（述語・定数の実在照合）。
        let refract_src = include_str!("shaders/refract_common.wgsl");
        assert!(
            refract_src.contains("const IOR_EPSILON: f32 = 1.0e-4;"),
            "refract_common.wgsl の IOR_EPSILON 定義がミラー値と一致すること"
        );
        assert!(
            refract_src.contains("u_material.ior > 1.0 + IOR_EPSILON || tr > 0.0"),
            "refract_common.wgsl の grab-pass ゲート述語が想定どおりであること"
        );
    }

    /// 屈折オフ経路（!refract_on）の実効被覆が babf5f1 パリティ（a_eff = a）であることを固定する
    /// 回帰テスト。かつて a_eff = a*(1-transmission) で被覆を下げていたため、
    ///   ・距離ソート: 実効被覆が下がり「アルファを下げただけ」の見た目（症状2）
    ///   ・WBOIT: a_eff→0 で reveal がクリア値 1 のまま残り post_wboit_composite が discard →
    ///     ガラスが完全に消える（症状1）
    /// を招いていた。屈折オフでは transmission を被覆へ反映せず素のアルファ (c*a, a) を保つこと。
    ///
    /// WGSL は cargo から実行できないため、シェーダ本文へのソース照合で不変条件を担保する。
    #[test]
    fn refract_off_alpha_parity_with_babf5f1() {
        let refract_src = include_str!("shaders/refract_common.wgsl");
        // !refract_on ブロックが素のアルファ (premult=c*a, a_eff=a) を返すこと。
        assert!(
            refract_src.contains("o.premult = c * a;"),
            "屈折オフ経路の premult は c*a（straight over と等価）であること"
        );
        assert!(
            refract_src.contains("o.a_eff   = a;"),
            "屈折オフ経路の a_eff は a（babf5f1 パリティ）であること。a*(1-tr) への再退行を禁止する"
        );
        // 屈折オフ経路で transmission による被覆低下（a*(1-tr) 系）が復活していないこと。
        assert!(
            !refract_src.contains("a * (1.0 - tr)"),
            "屈折オフ経路で a_eff = a*(1-tr) は禁止（WBOIT discard／距離ソートのアルファ低下を招く）"
        );
    }
}
