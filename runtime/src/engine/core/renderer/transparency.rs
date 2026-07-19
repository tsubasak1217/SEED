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
/// reveal ターゲットのフォーマット（色付き透過率の積）。
/// 色付き透過率（per-channel transmittance）方式のため RGBA へ拡張:
///   rgb = Π T_frag（per-channel の背景透過率）、a = Π(1 - a)（スカラー予備）。
/// 旧スカラー方式（R16Float）比でピクセルあたり +6 バイト（2→8 バイト）。バンディング回避で Float を維持。
pub const WBOIT_REVEAL_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;

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
    /// WBOIT 合成 パス1: 背景濾過（scene *= Π T_frag。blend=WboitBgMultiply）。
    wboit_composite_bg: PostPipeline,
    /// WBOIT 合成 パス2: 自色加算（final += avg * coverage。blend=Additive）。
    /// パス1 と同一シェーダ・同一 BGL（accum group0 + reveal group1）を共有する。
    wboit_composite_self: PostPipeline,
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
    /// 本物の RT 屈折バリアント一式（EXPERIMENTAL_RAY_QUERY 対応 GPU のみ Some）。
    /// 非対応時は None で、上の SS 版（sorted_mesh 等）だけを使う（完全フォールバック）。
    /// frame_renderer は translucency=Rt かつ Some のときだけ RT 版を描く。
    pub rt: Option<TransparentRtPipelines>,
}

/// 本物の RT 屈折の透明パイプライン一式（RT 対応 GPU のみ生成）。
///
/// SS 版（`TransparentPipelines` の sorted_mesh 等）と同一のブレンド／深度／フラグメントだが、
/// 背景取得が refract_rt.wgsl（TLAS 屈折レイ）。refract_rt.wgsl が group4 に TLAS(binding6)＋
/// 平均アルベド(binding14) を追加宣言するため、group4 レイアウト（`lights_bgl`）は
/// 「ライト＋シャドウ＋クラスタ＋DDGI＋TLAS＋アルベド＋屈折背景(15/16)」の superset になる。
/// この superset に一致する BindGroup は `LightBuffer::create_transparent_rt_bind_group` が供給する。
pub struct TransparentRtPipelines {
    /// 距離ソート用 RT（カリング面 3 バリアント）。
    sorted_mesh:    CullPipelineSet,
    sorted_skinned: CullPipelineSet,
    /// WBOIT 用 RT（デュアル MRT、fs_wboit）。
    wboit_mesh:     CullPipelineSet,
    wboit_skinned:  CullPipelineSet,
    /// group3 の空 gap BindGroup（非スキン描画で必須セット。RT 版レイアウト由来）。
    mesh_empty_bg3: wgpu::BindGroup,
    /// RT 透明の group4 レイアウト（TLAS＋アルベド込みの superset）。RT 用複合 BG の生成に使う。
    pub lights_bgl: wgpu::BindGroupLayout,
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
        // RT-Translucency（Phase RT-Translucency）: 屈折の共有ヘルパ（group4 binding15/16・
        // glass_composite）と背景取得 2 方式（SS フォールバック / RT 本物）、距離ソート専用フラグメント。
        // refract_ss / refract_rt は排他（refract_sample_bg を 1 本だけ供給する）。半透明パスにのみ連結する。
        // バインドレス基盤ミラー（BindlessInstanceRecord 定義＋八面体法線デコード＋GEOM フラグ）。
        // RT 屈折（refract_rt.wgsl）が界面法線復元に参照するため、その前に連結する。
        "bindless_common.wgsl"       => include_str!("shaders/bindless_common.wgsl"),
        "refract_common.wgsl"        => include_str!("shaders/refract_common.wgsl"),
        "refract_ss.wgsl"            => include_str!("shaders/refract_ss.wgsl"),
        "refract_rt.wgsl"            => include_str!("shaders/refract_rt.wgsl"),
        "shader_transparent.wgsl"    => include_str!("shaders/shader_transparent.wgsl"),
        "fullscreen.wgsl"            => include_str!("shaders/fullscreen.wgsl"),
        "post_wboit_composite.wgsl"  => include_str!("shaders/post_wboit_composite.wgsl"),
        other => panic!("unknown transparency shader source: {other}"),
    }
}

/// WBOIT MRT のブレンド定義を組み立てる（色付き透過率 / per-channel transmittance 方式）。
///   accum : One/One 加算（self 色 × a_eff × weight を積む）。
///   reveal: (Dst, Zero) — dst *= src（チャンネルごとの乗算累積）。
///           フラグメントが per-channel 透過率 T_frag（rgb）とスカラー (1-a)（a）を出し、
///           dst_new = src * dst で Π T_frag（rgb）と Π(1-a)（a）を作る（クリア値 1）。
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
    // reveal: dst *= src（乗算累積）。src 係数 Dst、dst 係数 Zero → result = src*dst + dst*0 = src*dst。
    // これで per-channel の透過率の積 Π T_frag（rgb）とスカラー Π(1-a)（a）が同時に貯まる。
    // 乗算は可換なので順序独立（WBOIT の要件）を満たす。
    let reveal = wgpu::ColorTargetState {
        format: WBOIT_REVEAL_FORMAT,
        blend: Some(wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::Dst,
                dst_factor: wgpu::BlendFactor::Zero,
                operation:  wgpu::BlendOperation::Add,
            },
            alpha: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::Dst,
                dst_factor: wgpu::BlendFactor::Zero,
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
        // WBOIT SS 版: 背景取得は refract_ss.wgsl（refract_common の後ろに連結）。
        let wboit_mesh: CullPipelineSet = std::array::from_fn(|i| build_wboit_pipeline(
            device, df, cache, &mesh_bgls,
            &["cluster_common.wgsl", "pbr_common.wgsl", "shader_common.wgsl", "ddgi_common.wgsl", "light_common.wgsl", "shadow.wgsl", "rt_shadow_off.wgsl",
              "shader_static_vertex.wgsl", "surface.wgsl", "surface_gather.wgsl",
              "lighting_eval.wgsl", "shader_fragment.wgsl", "refract_common.wgsl", "refract_ss.wgsl", "shader_wboit.wgsl"],
            &["mesh_vertex"],
            "wboit_mesh",
            CULL_FACE_VARIANTS[i],
        ));
        let wboit_skinned: CullPipelineSet = std::array::from_fn(|i| build_wboit_pipeline(
            device, df, cache, &skin_bgls,
            &["cluster_common.wgsl", "pbr_common.wgsl", "shader_common.wgsl", "ddgi_common.wgsl", "light_common.wgsl", "shadow.wgsl", "rt_shadow_off.wgsl",
              "shader_skinned_vertex.wgsl", "surface.wgsl", "surface_gather.wgsl",
              "lighting_eval.wgsl", "shader_fragment.wgsl", "refract_common.wgsl", "refract_ss.wgsl", "shader_wboit.wgsl"],
            &["mesh_vertex", "skin_vertex"],
            "wboit_skinned",
            CULL_FACE_VARIANTS[i],
        ));

        // ── WBOIT 合成パイプライン（色付き透過率＝2 パス）──
        // パス1: 背景濾過（scene *= Π T_frag）。パス2: 自色加算（final += avg*coverage）。
        // 両 TOML は同一シェーダ（post_wboit_composite.wgsl）を指し、リフレクションが
        // 両エントリの global（accum group0 + reveal group1）から同一 BGL を得る（BindGroup 共有）。
        let wboit_composite_bg = PostPipeline::from_toml(
            device, include_str!("pipelines/post_wboit_composite_bg.toml"), sf, cache, resolve_shader,
        );
        let wboit_composite_self = PostPipeline::from_toml(
            device, include_str!("pipelines/post_wboit_composite_self.toml"), sf, cache, resolve_shader,
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

        // ── 本物の RT 屈折バリアント（RAY_QUERY ＋ バインドレス対応 GPU のみ）─────────────
        // acceleration_structure を宣言する RT 連結は非対応デバイスでモジュール作成に失敗し得るため、
        // RT 影と同じ対応判定（rt_shadows_supported）を通ったときだけビルドする。
        // さらに、界面ごとの本物の再屈折は group4 の追加バインディング（instance_table 17／index 18／
        // 法線 19＝すべて bindless.rs 所有の storage）を要するため、**バインドレス対応も必須**にする。
        // これらは storage バッファ（binding_array ではない）なので group4 の uniform と同居できるが、
        // 実体は BindlessResources が確保する＝バインドレス非対応 GPU では存在しないため、その場合は
        // RT 屈折パイプラインを作らず SS 屈折（refract_ss）へフォールバックする（実 GPU では RT 対応＝
        // ほぼ常にバインドレスも対応のため、実害は理論上のみ）。
        let rt = if super::rt_shadow::rt_shadows_supported() && super::bindless::bindless_supported() {
            Some(TransparentRtPipelines::new(device, sf, df, cache))
        } else {
            None
        };

        Self {
            sorted_mesh, sorted_skinned,
            wboit_mesh, wboit_skinned,
            mesh_empty_bg3,
            wboit_composite_bg,
            wboit_composite_self,
            composite_sampler,
            lights_bgl,
            refract_sampler,
            dummy_refract_view,
            rt,
        }
    }

    /// 屈折オフ時のダミー背景ビュー（1x1）への参照。屈折用 RT が未確保のフレームで使う。
    pub fn dummy_refract_view(&self) -> &wgpu::TextureView { &self.dummy_refract_view }

    /// WBOIT 合成（色付き透過率＝2 パス）: accum/reveal を読み、シーン HDR（`hdr_view`）へ
    ///   final = scene * Π T_frag + avg * coverage
    /// を合成する。出力は LoadOp::Load（既存 HDR を保持）。ブルーム／トーンマップより前に呼ぶこと。
    ///
    /// 2 パスは同一の色アタッチメント（同一レンダーパス内）で順に描く:
    ///   パス1（背景濾過）: blend=WboitBgMultiply で scene *= Π T_frag。
    ///   パス2（自色加算）: blend=Additive        で final += avg * coverage。
    /// パス1→パス2 の順序が必須（自色は背景透過率で減衰させない）。BindGroup は両パスで共有する
    /// （両パイプラインが同一シェーダ由来で同一 BGL のため）。
    pub fn composite_wboit(
        &self,
        device:      &wgpu::Device,
        encoder:     &mut wgpu::CommandEncoder,
        hdr_view:    &wgpu::TextureView,
        accum_view:  &wgpu::TextureView,
        reveal_view: &wgpu::TextureView,
    ) {
        // group 0: accum + サンプラー、group 1: reveal + サンプラー。両パス共通。
        // BGL は bg/self どちらのパイプラインでも同一（同一シェーダのリフレクション結果）なので
        // 片方（背景濾過パス）の bgls から作り、両パイプラインで使い回す。
        let bg0 = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("WBOIT Composite BG0 accum"),
            layout:  &self.wboit_composite_bg.bgls[0],
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(accum_view) },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(&self.composite_sampler) },
            ],
        });
        let bg1 = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("WBOIT Composite BG1 reveal"),
            layout:  &self.wboit_composite_bg.bgls[1],
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
        pass.set_bind_group(0, &bg0, &[]);
        pass.set_bind_group(1, &bg1, &[]);
        // パス1: 背景濾過（scene *= Π T_frag）。
        pass.set_pipeline(&self.wboit_composite_bg.pipeline);
        pass.draw(0..3, 0..1);
        // パス2: 自色加算（final += avg * coverage）。必ずパス1 の後。
        pass.set_pipeline(&self.wboit_composite_self.pipeline);
        pass.draw(0..3, 0..1);
    }
}

impl TransparentRtPipelines {
    /// 本物の RT 屈折の透明パイプライン一式を生成する（RT 対応時のみ呼ぶこと）。
    ///
    /// SS 版と同じ手順（TOML＋リフレクションで距離ソート、WBOIT は同一 BGL を再利用して手動構築）
    /// だが、連結末尾の背景取得を refract_rt.wgsl（TLAS 屈折レイ）にする。距離ソート RT のビルドで
    /// 得た group4 BGL（TLAS binding6＋アルベド binding14 を含む superset）を WBOIT RT でも再利用する。
    pub fn new(
        device: &wgpu::Device,
        sf:     wgpu::TextureFormat,
        df:     wgpu::TextureFormat,
        cache:  Option<&wgpu::PipelineCache>,
    ) -> Self {
        use super::pipeline_config::RenderPipelineBuilder;

        // 距離ソート RT（TOML＋リフレクション）。カリング面 3 種を 1 ファイルから生成する。
        let build_sorted_rt = |toml_src: &str, label_base: &str| -> (CullPipelineSet, Vec<wgpu::BindGroupLayout>) {
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
            (pipes, bgls.expect("transparent RT: BGL が生成されていない"))
        };
        let (sorted_mesh, mesh_bgls) =
            build_sorted_rt(include_str!("pipelines/transparent_mesh_rt.toml"), "transparent_mesh_rt");
        let (sorted_skinned, skin_bgls) =
            build_sorted_rt(include_str!("pipelines/transparent_skinned_rt.toml"), "transparent_skinned_rt");

        // group3 空 gap BindGroup（mesh 用）。
        let mesh_empty_bg3 = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Transparent RT Empty BG (group 3)"),
            layout:  &mesh_bgls[3],
            entries: &[],
        });

        // WBOIT RT（デュアル MRT・手動構築）。連結末尾に refract_rt.wgsl＋shader_wboit.wgsl を置く。
        // BGL は距離ソート RT のビルド結果（TLAS＋アルベド込み group4）を再利用する。
        let wboit_mesh: CullPipelineSet = std::array::from_fn(|i| build_wboit_pipeline(
            device, df, cache, &mesh_bgls,
            &["cluster_common.wgsl", "pbr_common.wgsl", "shader_common.wgsl", "ddgi_common.wgsl", "light_common.wgsl", "shadow.wgsl", "rt_shadow_off.wgsl",
              "shader_static_vertex.wgsl", "surface.wgsl", "surface_gather.wgsl",
              "lighting_eval.wgsl", "shader_fragment.wgsl", "bindless_common.wgsl", "refract_common.wgsl", "refract_rt.wgsl", "shader_wboit.wgsl"],
            &["mesh_vertex"],
            "wboit_mesh_rt",
            CULL_FACE_VARIANTS[i],
        ));
        let wboit_skinned: CullPipelineSet = std::array::from_fn(|i| build_wboit_pipeline(
            device, df, cache, &skin_bgls,
            &["cluster_common.wgsl", "pbr_common.wgsl", "shader_common.wgsl", "ddgi_common.wgsl", "light_common.wgsl", "shadow.wgsl", "rt_shadow_off.wgsl",
              "shader_skinned_vertex.wgsl", "surface.wgsl", "surface_gather.wgsl",
              "lighting_eval.wgsl", "shader_fragment.wgsl", "bindless_common.wgsl", "refract_common.wgsl", "refract_rt.wgsl", "shader_wboit.wgsl"],
            &["mesh_vertex", "skin_vertex"],
            "wboit_skinned_rt",
            CULL_FACE_VARIANTS[i],
        ));

        // group4 レイアウト（TLAS＋アルベド込み superset）は距離ソート RT のビルド結果を使う。
        let lights_bgl = mesh_bgls[4].clone();

        Self {
            sorted_mesh, sorted_skinned,
            wboit_mesh, wboit_skinned,
            mesh_empty_bg3,
            lights_bgl,
        }
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

/// 距離ソート方式で半透明を描画する（本物の RT 屈折バリアント）。
/// SS 版 `draw_sorted` と同一だが、RT 用パイプライン集合（TLAS＋アルベド込み group4）を使う。
/// `lights_bg` は `create_transparent_rt_bind_group` が作った RT superset BindGroup であること。
/// 呼び出し側は translucency=Rt かつ `tp.rt` が Some のときだけ本関数を使う（gating は frame_renderer）。
pub fn draw_sorted_rt<'p>(
    pass:       &mut wgpu::RenderPass<'p>,
    models:     &'p TransparentModels<'p>,
    camera_bg:  &'p wgpu::BindGroup,
    lights_bg:  &'p wgpu::BindGroup,
    rt:         &'p TransparentRtPipelines,
    camera_pos: [f32; 3],
) {
    let mut items = gather_items(models, camera_pos);
    if items.is_empty() { return; }
    items.sort_by(|a, b| b.dist_sq.partial_cmp(&a.dist_sq).unwrap_or(std::cmp::Ordering::Equal));
    for item in &items {
        draw_one(
            pass, item, models, camera_bg, lights_bg,
            &rt.sorted_mesh, &rt.sorted_skinned, &rt.mesh_empty_bg3,
        );
    }
}

/// WBOIT 方式で半透明を描画する（本物の RT 屈折バリアント）。
/// SS 版 `draw_wboit` と同一だが、RT 用パイプライン集合を使う。gating は frame_renderer。
pub fn draw_wboit_rt<'p>(
    pass:       &mut wgpu::RenderPass<'p>,
    models:     &'p TransparentModels<'p>,
    camera_bg:  &'p wgpu::BindGroup,
    lights_bg:  &'p wgpu::BindGroup,
    rt:         &'p TransparentRtPipelines,
    camera_pos: [f32; 3],
) {
    let items = gather_items(models, camera_pos);
    for item in &items {
        draw_one(
            pass, item, models, camera_bg, lights_bg,
            &rt.wboit_mesh, &rt.wboit_skinned, &rt.mesh_empty_bg3,
        );
    }
}

// ============================================================
//  sequential grab（屈折の逐次グラブ）— 距離ソート専用
// ============================================================

/// 屈折関与判定の IOR 閾値。refract_common.wgsl の `IOR_EPSILON`（= 1.0e-4）と一致させること。
/// ズレると「屈折するガラス」の判定がシェーダの grab-pass ゲート（material_refracts）と食い違い、
/// 逐次グラブの再グラブ地点がシェーダの実際のサンプル要否とずれる（下のソース照合テストで担保）。
const REFRACT_IOR_EPSILON: f32 = 1.0e-4;

/// sequential grab の 1 描画単位。距離ソート済みアイテムに「屈折に関与するか」を付帯させる。
///
/// `refracts` は refract_common.wgsl の `material_refracts`（ior>1+eps || transmission>0）と同一述語。
/// true のアイテムは屈折背景をサンプルするため、描画の手前で最新の背景（＝先に描いた半透明を含む
/// scene_hdr のコピー＋ブラーピラミッド）が要る。false（素の Blend＝アルファ板等）は背景を読まないので
/// 手前での再グラブは不要（＝連続する非屈折半透明は 1 パスにまとめられる）。
pub struct SequentialItem {
    /// 描画対象（`draw_one` に渡す内部アイテム）。
    item:         TransparentItem,
    /// 屈折背景をサンプルするマテリアルか（描画手前で最新背景が要るか）。
    pub refracts: bool,
}

/// sequential grab 用: 半透明を背面→前面（dist_sq 降順）にソートし、各アイテムの屈折関与フラグを付ける。
///
/// ソート規約は `draw_sorted` と同一（アルファブレンドの描画順依存を満たす）。屈折関与は GpuModel の
/// `primitive_ior` / `primitive_transmission`（materials と同期した CPU ミラー）から純粋計算で決まる。
pub fn plan_sorted_sequential(
    models:     &TransparentModels,
    camera_pos: [f32; 3],
) -> Vec<SequentialItem> {
    let mut items = gather_items(models, camera_pos);
    // 背面→前面（遠い順）。draw_sorted と同一のソートキー。
    items.sort_by(|a, b| b.dist_sq.partial_cmp(&a.dist_sq).unwrap_or(std::cmp::Ordering::Equal));
    items.into_iter().map(|item| {
        let (gpu, _) = &models[item.model_idx];
        let prim = &gpu.meshes[item.mesh_idx].primitives[item.prim_idx];
        let mi = prim.material_index;
        // refract_common.wgsl の material_refracts と同一述語（屈折/透過するマテリアルか）。
        let refracts = gpu.primitive_ior(mi) > 1.0 + REFRACT_IOR_EPSILON
            || gpu.primitive_transmission(mi) > 0.0;
        SequentialItem { item, refracts }
    }).collect()
}

/// sequential grab 用: 1 アイテムを描画する（`draw_sorted` の 1 反復ぶんを外から駆動できるように分離）。
///
/// `rt` が Some なら本物の RT 屈折パイプライン集合を、None なら SS 版を使う（`draw_sorted` /
/// `draw_sorted_rt` と同じパイプライン選択）。frame_renderer が屈折アイテムの手前で背景を再グラブしつつ
/// このメソッドをアイテム単位に呼ぶことで逐次グラブを実現する。
pub fn draw_sequential_one<'p>(
    pass:      &mut wgpu::RenderPass<'p>,
    sp:        &'p SequentialItem,
    models:    &'p TransparentModels<'p>,
    camera_bg: &'p wgpu::BindGroup,
    lights_bg: &'p wgpu::BindGroup,
    tp:        &'p TransparentPipelines,
    rt:        Option<&'p TransparentRtPipelines>,
) {
    if let Some(rt) = rt {
        draw_one(
            pass, &sp.item, models, camera_bg, lights_bg,
            &rt.sorted_mesh, &rt.sorted_skinned, &rt.mesh_empty_bg3,
        );
    } else {
        draw_one(
            pass, &sp.item, models, camera_bg, lights_bg,
            &tp.sorted_mesh, &tp.sorted_skinned, &tp.mesh_empty_bg3,
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
        // RT-Translucency: 屈折ヘルパ（group4 binding15/16・glass_composite）＋背景取得 2 方式
        // （SS フォールバック / RT 本物）＋距離ソート専用フラグメント。
        let refract    = include_str!("shaders/refract_common.wgsl");
        let refract_ss = include_str!("shaders/refract_ss.wgsl");
        let refract_rt = include_str!("shaders/refract_rt.wgsl");
        // RT 屈折の界面法線復元が参照するバインドレス基盤ミラー（BindlessInstanceRecord＋八面体デコード）。
        let bindless_c = include_str!("shaders/bindless_common.wgsl");
        let transp     = include_str!("shaders/shader_transparent.wgsl");
        let fullscreen = include_str!("shaders/fullscreen.wgsl");
        let composite  = include_str!("shaders/post_wboit_composite.wgsl");

        // ── SS 版（非 RT・Capabilities::empty で validate）─────────────────────────
        // 連結順は transparency.rs のリゾルバ・TOML と一致させること。背景取得は refract_ss。
        // 距離ソート（fs_transparent_sorted）と WBOIT（fs_wboit）はいずれも refract_common＋refract_ss を
        // 含み、group4 に屈折背景（binding15/16）を宣言する superset レイアウトになる。
        let ss_variants: [(&str, Vec<&str>); 4] = [
            ("sorted_mesh",           vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_off, static_v, surf, gather, light_eval, frag, refract, refract_ss, transp]),
            ("wboit_mesh",            vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_off, static_v, surf, gather, light_eval, frag, refract, refract_ss, wboit]),
            ("wboit_skinned",         vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_off, skin_v,   surf, gather, light_eval, frag, refract, refract_ss, wboit]),
            ("post_wboit_composite",  vec![fullscreen, composite]),
        ];
        for (name, parts) in ss_variants {
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

        // ── RT 版（本物の屈折レイ・RAY_QUERY capability で validate）───────────────
        // 背景取得は refract_rt.wgsl（group4 に TLAS binding6＋アルベド binding14 を追加宣言）。
        // 影経路は rt_off（シャドウマップのみ・TLAS 非宣言）＝TLAS/アルベドは refract_rt が単独で宣言する。
        // acceleration_structure / rayQuery* を含むため RAY_QUERY capability が要る（empty では validate 失敗）。
        let rt_variants: [(&str, Vec<&str>); 3] = [
            ("sorted_mesh_rt",  vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_off, static_v, surf, gather, light_eval, frag, bindless_c, refract, refract_rt, transp]),
            ("wboit_mesh_rt",   vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_off, static_v, surf, gather, light_eval, frag, bindless_c, refract, refract_rt, wboit]),
            ("wboit_skinned_rt",vec![cluster, pbr_c, common, ddgi_c, light_c, shadow, rt_off, skin_v,   surf, gather, light_eval, frag, bindless_c, refract, refract_rt, wboit]),
        ];
        for (name, parts) in rt_variants {
            let src = parts.join("\n");
            let module = naga::front::wgsl::parse_str(&src)
                .unwrap_or_else(|e| panic!("[{name}] WGSL parse 失敗: {e:?}"));
            let mut validator = naga::valid::Validator::new(
                naga::valid::ValidationFlags::all(),
                naga::valid::Capabilities::RAY_QUERY,
            );
            validator
                .validate(&module)
                .unwrap_or_else(|e| panic!("[{name}] WGSL validate 失敗: {e:?}"));
        }
    }

    /// refract_rt.wgsl の界面パック定数・マスク定数が Rust 側（rt_shadow.rs）と一致することの
    /// ソース照合（WGSL は cargo から実行できないため、定数の実在＋値をソースで固定する）。
    #[test]
    fn refract_rt_constants_match_rust() {
        let rt_src = include_str!("shaders/refract_rt.wgsl");
        // 色付き影と同一のパック定数（rt_shadow.rs::SHADOW_PACK_QUANT=255 / SHADOW_PACK_RADIX=256）。
        assert!(rt_src.contains("const REFRACT_PACK_QUANT: f32 = 255.0;"),
            "refract_rt の量子化段数は 255（rt_shadow.rs::SHADOW_PACK_QUANT と一致）");
        assert!(rt_src.contains("const REFRACT_PACK_RADIX: f32 = 256.0;"),
            "refract_rt の基数は 256（rt_shadow.rs::SHADOW_PACK_RADIX と一致）");
        // インスタンスカリングマスク（rt_shadow.rs::RT_MASK_OPAQUE=0x01 / RT_MASK_NON_OPAQUE=0x02）。
        assert!(rt_src.contains("const REFRACT_RT_MASK_OPAQUE: u32 = 0x01u;"),
            "不透明マスクは 0x01（rt_shadow.rs::RT_MASK_OPAQUE と一致）");
        assert!(rt_src.contains("const REFRACT_RT_MASK_TRANSLUCENT: u32 = 0x02u;"),
            "半透明マスクは 0x02（rt_shadow.rs::RT_MASK_NON_OPAQUE と一致）");
        // 界面トレースの上限は 4（コスト有界＝最大 4 界面 + 1 不透明 = 5 レイ/px）。
        assert!(rt_src.contains("const REFRACT_MAX_INTERFACES: u32 = 4u;"),
            "界面トレース上限は 4（5 レイ/px の有界コスト）");
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

    /// sequential grab（屈折の逐次グラブ）の屈折関与述語が、シェーダの grab-pass ゲートと
    /// 同一の閾値・同一の述語であることを固定する回帰テスト。
    ///
    /// `plan_sorted_sequential` は「再グラブが要るアイテムか」を
    ///   refracts = ior > 1.0 + REFRACT_IOR_EPSILON || transmission > 0.0
    /// で決める。これは refract_common.wgsl の material_refracts
    ///   (u_material.ior > 1.0 + IOR_EPSILON || tr > 0.0)
    /// と同一でなければならない（ズレると、屈折するガラスの手前で背景が再グラブされず
    /// 古い背景を拾う／逆に非屈折半透明で無駄に再グラブする）。定数値とシェーダ実体を照合する。
    #[test]
    fn sequential_refract_predicate_matches_shader_gate() {
        // Rust ミラー定数が 1.0e-4 であること（refract_common.wgsl の IOR_EPSILON と一致）。
        assert_eq!(
            super::REFRACT_IOR_EPSILON, 1.0e-4_f32,
            "REFRACT_IOR_EPSILON は refract_common.wgsl の IOR_EPSILON（1.0e-4）と一致すること",
        );
        let refract_src = include_str!("shaders/refract_common.wgsl");
        assert!(
            refract_src.contains("const IOR_EPSILON: f32 = 1.0e-4;"),
            "refract_common.wgsl の IOR_EPSILON 定義がミラー値と一致すること",
        );
        assert!(
            refract_src.contains("u_material.ior > 1.0 + IOR_EPSILON || tr > 0.0"),
            "refract_common.wgsl の grab-pass ゲート述語が sequential 側ミラーと同一であること",
        );
    }

    // ============================================================
    //  色付き透過率 WBOIT（per-channel transmittance）の合成数式の CPU 参照テスト
    // ============================================================

    /// per-channel 透過率 T_frag = clamp((1-a) + a*tr*albedo, 0, 1)。
    /// refract_common.wgsl の glass_composite_wboit と同一式（下のソース照合で乖離を防ぐ）。
    fn t_frag(a: f32, tr: f32, albedo: [f32; 3]) -> [f32; 3] {
        let mut o = [0.0f32; 3];
        for i in 0..3 {
            o[i] = ((1.0 - a) + a * tr * albedo[i]).clamp(0.0, 1.0);
        }
        o
    }

    /// N 枚の同一フラグメントを合成した最終色を CPU で参照計算する。
    /// post_wboit_composite.wgsl の 2 パス合成（final = scene*ΠT + avg*coverage）と同一式。
    /// 同一層 N 枚なので avg（accum の重み平均）= self（重みに依らず一定）。
    fn composite_n(n: u32, scene: [f32; 3], self_lit: [f32; 3], a: f32, tr: f32, albedo: [f32; 3]) -> [f32; 3] {
        let t = t_frag(a, tr, albedo);
        // reveal.rgb = Π T_frag（乗算累積）。クリア値 1 から N 回掛ける。
        let mut reveal = [1.0f32; 3];
        for _ in 0..n {
            for i in 0..3 { reveal[i] *= t[i]; }
        }
        // coverage = 1 - mean(reveal.rgb)。
        let mean_t = (reveal[0] + reveal[1] + reveal[2]) / 3.0;
        let coverage = (1.0 - mean_t).clamp(0.0, 1.0);
        // avg = self（同一層のため重み平均は self そのもの）。final = scene*reveal + avg*coverage。
        let mut out = [0.0f32; 3];
        for i in 0..3 {
            out[i] = scene[i] * reveal[i] + self_lit[i] * coverage;
        }
        out
    }

    /// 【核心】純赤フィルタ（a=1, tr=1, albedo=(1,0,0)）は重ね数 N に依存しない＝「1赤+1赤=1赤」。
    /// Π T=(1,0,0) が飽和し、coverage=1-1/3 も一定になるため、N=1/2/8/64 で完全一致すること。
    #[test]
    fn wboit_colored_pure_red_is_n_independent() {
        let scene = [0.3, 0.7, 0.2];      // 任意の背景。
        let selfc = [0.8, 0.1, 0.1];      // 赤い面の自色。
        let albedo = [1.0, 0.0, 0.0];     // 純赤フィルタ。
        let base = composite_n(1, scene, selfc, 1.0, 1.0, albedo);
        for &n in &[2u32, 8, 64] {
            let got = composite_n(n, scene, selfc, 1.0, 1.0, albedo);
            for i in 0..3 {
                assert!((got[i] - base[i]).abs() < 1.0e-6,
                    "純赤フィルタは N={n} でも N=1 と一致すること (ch{i}: {} vs {})", got[i], base[i]);
            }
        }
        // 純赤なので緑・青チャンネルの背景は完全に遮断される（reveal.g=reveal.b=0）。
        assert!((base[1] - selfc[1] * (2.0 / 3.0)).abs() < 1.0e-6, "緑は背景遮断＝自色×coverage のみ");
        assert!((base[2] - selfc[2] * (2.0 / 3.0)).abs() < 1.0e-6, "青は背景遮断＝自色×coverage のみ");
    }

    /// 半透明の色付きガラス（a=0.5, tr=1, 赤）は N で単調に飽和し、発散しない（足し算的増加の禁止）。
    /// 赤チャンネルの透過率 T.r=1 は N によらず背景の赤を素通しし、緑青は Π で 0 へ収束する。
    #[test]
    fn wboit_colored_semi_red_saturates_not_adds() {
        let scene = [1.0, 1.0, 1.0];
        let selfc = [0.5, 0.0, 0.0];
        let albedo = [1.0, 0.0, 0.0];
        let mut prev_g = f32::INFINITY;
        for &n in &[1u32, 2, 4, 16] {
            let got = composite_n(n, scene, selfc, 0.5, 1.0, albedo);
            // 全チャンネル [0, scene+self] に有界（加算的発散が無い）。
            for i in 0..3 {
                assert!(got[i] >= -1.0e-6 && got[i] <= scene[i] + selfc[i] + 1.0e-6,
                    "N={n} ch{i} が有界であること: {}", got[i]);
            }
            // 緑チャンネルは N 増加で単調減少（背景の緑が層ごとに濾過されて暗くなる＝飽和方向）。
            assert!(got[1] <= prev_g + 1.0e-6, "緑は N 増加で単調減少（飽和）: N={n} {}", got[1]);
            prev_g = got[1];
        }
    }

    /// 素の Blend（tr=0）は per-channel 透過率が (1-a) のスカラー（全チャンネル等しい）になり、
    /// 従来スカラー WBOIT の revealage=Π(1-a)・coverage=1-Π(1-a) と一致する（パリティ）。
    #[test]
    fn wboit_plain_blend_matches_scalar_parity() {
        let a = 0.4f32;
        let t = t_frag(a, 0.0, [1.0, 0.0, 0.0]); // tr=0 なら albedo は無関係。
        for i in 0..3 {
            assert!((t[i] - (1.0 - a)).abs() < 1.0e-6, "tr=0 は T=(1-a) のスカラー（ch{i}）");
        }
        // 2 枚重ね: reveal=(1-a)^2、coverage=1-(1-a)^2。標準 WBOIT と一致。
        let scene = [0.5, 0.5, 0.5];
        let selfc = [0.2, 0.6, 0.9];
        let got = composite_n(2, scene, selfc, a, 0.0, [0.0, 0.0, 0.0]);
        let reveal = (1.0 - a) * (1.0 - a);
        let coverage = 1.0 - reveal;
        for i in 0..3 {
            let expect = scene[i] * reveal + selfc[i] * coverage;
            assert!((got[i] - expect).abs() < 1.0e-6, "素の Blend は標準 WBOIT と一致（ch{i}）");
        }
    }

    /// CPU 参照ミラーがシェーダ実体から静かに乖離しないよう、式・ブレンド・フォーマットの
    /// キー文字列をソース照合で固定する（既存テスト群と同じ二段構え）。
    #[test]
    fn wboit_colored_shader_sources_match_mirror() {
        // フラグメント: T_frag = clamp((1-a) + a*tr*albedo, [0,1])。
        let refract_src = include_str!("shaders/refract_common.wgsl");
        assert!(
            refract_src.contains("clamp(vec3<f32>(1.0 - a) + a * tr * albedo, vec3<f32>(0.0), vec3<f32>(1.0))"),
            "glass_composite_wboit の T_frag 式が CPU ミラーと一致すること",
        );
        // reveal 出力: rgb=T_frag, a=1-a_eff。
        let wboit_src = include_str!("shaders/shader_wboit.wgsl");
        assert!(
            wboit_src.contains("out.reveal = vec4<f32>(g.transmit, 1.0 - g.a_eff);"),
            "fs_wboit の reveal 出力が per-channel T_frag + スカラー(1-a) であること",
        );
        assert!(
            wboit_src.contains("out.accum  = vec4<f32>(g.self_lit * g.a_eff, g.a_eff) * w;"),
            "fs_wboit の accum 出力が self*a_eff*w であること",
        );
        // 合成: coverage = 1 - mean(reveal.rgb)。
        let comp_src = include_str!("shaders/post_wboit_composite.wgsl");
        assert!(
            comp_src.contains("let coverage = clamp(1.0 - mean_t, 0.0, 1.0);"),
            "合成の coverage が 1 - mean(T_rgb) であること（N 飽和量）",
        );
        assert!(
            comp_src.contains("return vec4<f32>(reveal.rgb, 1.0);"),
            "背景濾過パスが reveal.rgb（Π T_frag）を出力すること",
        );
        // reveal ブレンドが乗算累積（Dst, Zero）であること。
        let transp_src = include_str!("transparency.rs");
        assert!(
            transp_src.contains("src_factor: wgpu::BlendFactor::Dst,"),
            "reveal ブレンドが (Dst, Zero) の乗算累積であること",
        );
        // reveal フォーマットが RGBA（色付き透過率）へ拡張されていること。
        assert!(
            transp_src.contains("pub const WBOIT_REVEAL_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;"),
            "WBOIT_REVEAL_FORMAT が Rgba16Float（色付き透過率）であること",
        );
    }
}
