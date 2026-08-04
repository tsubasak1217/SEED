// ============================================================
//  サブモジュール（GPU リソース・パイプライン管理）
// ============================================================

pub(crate) mod uniforms;
pub(crate) mod gpu_resources;
pub(crate) mod pipeline_config;
pub(crate) mod pipeline;
pub(crate) mod hiz;
pub(crate) mod skin_system;
pub(crate) mod animator;
pub(crate) mod lighting;
/// Clustered Lighting（3D フロクセル単位のライトカリング, Phase C1）
pub(crate) mod clustered;
pub(crate) mod ddgi;
pub(crate) mod shadow;
pub(crate) mod rt_shadow;
/// スキンメッシュの RT 加速構造（Phase RT-Skin）。変形後頂点の書き出し＋専用 BLAS 管理。
pub(crate) mod rt_skin_blas;
pub(crate) mod post;
pub(crate) mod transparency;
pub(crate) mod batch2d;
/// GPU パーティクル シミュレーション＋描画（Phase RP）
pub(crate) mod particle_system;
/// GPU パーティクルの組込み形状メッシュ（Point/Sphere/Box/Plane/Model）
pub(crate) mod particle_shapes;
/// スカイボックス（天球：equirectangular 背景, Phase R9）
pub(crate) mod skybox;
/// .mat マテリアルアセット（Phase R7: マルチマテリアル編集）
pub mod material_asset;
/// .postfx ポストエフェクトアセット＋テクスチャ単位ポストプロセス（Phase R3 応用）
pub mod postfx;
/// エディタのシーンビュー表示モード（Lit / Unlit / Wireframe）
pub(crate) mod view_mode;
/// G-Buffer リソース＋MRT ジオメトリパイプライン（Phase D3 Deferred Phase A）
pub(crate) mod gbuffer;
/// G-Buffer の空きチャンネルへ詰める「サーフェス識別情報」のビット規約
/// （セマンティックタグ／シェーディングモデル ID／ユーザーデータ）。
pub mod surface_id;
/// シェーディングアセット（1 本のユーザー WGSL）のロード・検証・パイプライン生成（L3-a 第 2 段）。
/// 未指定時は一切通らない（従来の連結・従来のパイプラインがそのまま使われる）。
pub mod shading_asset;
/// 地形レイヤブレンド用 G-Buffer パイプライン（Terrain T2）。
pub(crate) mod terrain_gbuffer;

/// プロシージャル草の GPU インスタンシング パイプライン（G-Buffer 書き込み）。
pub(crate) mod grass_gbuffer;

/// 水面描画パス（Phase W1）。`engine::water` が解決した水ボリュームを 1 ドローで描く。
pub mod water;

/// 水中コースティクス生成パス（Phase W5.3）。深度から復元した水中ピクセルの集光係数を焼く。
/// 生成物は deferred ライティングが `Surface.caustics` として読む（平行光の直達を増幅）。
pub mod caustics;

/// 瞬発インタラクションフィールド（Phase I1）。動く物の速度をワールド俯瞰テクスチャへ焼く。
pub mod interaction;
/// 提示フレームの PNG 書き出し（環境変数ゲートの常設デバッグフック）。
pub(crate) mod screenshot;
/// 地形レイヤテクスチャ配列（texture_2d_array）の構築（Terrain T2b）。
pub(crate) mod terrain_layer_textures;
/// フルスクリーン・ライティングパイプライン（G-Buffer 復元, Phase D3 Deferred Phase A）
pub(crate) mod deferred;
// 速度バッファ（モーションベクタ）のデバッグ可視化（SEED_DEBUG_VELOCITY=1 のときだけ構築）。
pub(crate) mod velocity_debug;
/// G-Buffer 各チャンネルのデバッグ可視化（シーンビュー表示モード「G-Buffer: 〜」）。
pub(crate) mod gbuffer_debug;
/// 反射（SSR / RT）フルスクリーンパス＋合成（Phase D6）
pub(crate) mod reflection;
/// 反射のミス経路（レイが空へ抜けたとき）に映すスカイボックスの入力データ。
/// D6 不透明反射（`reflection`）と W5.2 水面反射（`water_reflection`）が共用する。
pub(crate) mod reflection_sky;
/// 水面反射パス（Phase W5.2）。D6 のハイブリッド RT 反射を水面の格子へ流用し、
/// 反射色を専用 RT へ焼く（水面パスはそれを 1 枚読むだけ）。
pub(crate) mod water_reflection;
/// いもす法（累積和）単一チャンネル分離ボックスブラー基盤（Phase D4。AO で初適用・SSGI へ転用可）
pub(crate) mod imos_blur;
/// AO（SSAO / RT-AO）フルスクリーンパス＋いもす法ブラー＋半解像度リソース（Phase D4）
pub(crate) mod ao;
/// SSGI（スクリーンスペース GI）フルスクリーンパス＋いもす法カラーブラー＋半解像度リソース（Phase SSGI）
pub(crate) mod ssgi;
/// RT ソフト影マスク生成＋バイラテラルデノイズ＋半解像度リソース（Phase RT-Shadow-Denoise）
pub(crate) mod shadow_mask;
/// 影マスク専用 separable バイラテラルブラー基盤（深度エッジ保持。imos とは別物）
pub(crate) mod shadow_mask_bilateral;
/// レンダリング機能マトリクス（RT/代替のモード管理）
pub(crate) mod render_features;
/// すりガラス用の屈折背景ミップチェーン（ダウンサンプル→いもす法ブラー。ガラス表現）
pub(crate) mod refract_pyramid;
/// バインドレス基盤（フェーズ B1）: テクスチャ配列レジストリ・UV/index メガバッファ・
/// インスタンステーブル。RT ヒットシェーディングの土台（消費は B2/B3）。
pub(crate) mod bindless;

pub use uniforms::{CameraUniform, ModelUniform, MaterialUniform, JointUniform, ColorVertex,
                   GpuCullData, GizmoVertex};
pub use gpu_resources::{GpuTexture, GpuMaterial, GpuPrimitive, GpuMesh, GpuModel, InlineUpdateResult,
                        InstancedModelBatch, NodePrimDraw, GpuLineBatch, GpuGizmoBatch,
                        DefaultTextures, CameraBuffer,
                        extract_frustum_planes, NUM_LODS};
pub use pipeline::{MeshPipeline, SkinnedMeshPipeline, UnlitPipeline, DrawPipelines,
                   SkinComputePipeline, IdPassPipeline, OutlinePipeline, DepthPrepassPipelines,
                   SpritePipeline, SpriteOutlinePipeline, CanvasIdPipeline, CanvasIdUniform,
                   CameraPreviewBlitPipeline, ShadowDepthPipelines,
                   BarFillPipeline, BarFillUniform};
pub use particle_system::ParticleSystem;
pub use skybox::{SkyboxSystem, SkyboxPipelines};
pub use lighting::{GpuLight, LightBuffer, LightMeta, LightingPass, MAX_LIGHTS,
                   DEFAULT_AMBIENT_COLOR, DEFAULT_AMBIENT_INTENSITY};
pub use clustered::{ClusterResources, partition_directional_first,
                    CLUSTER_TILES_X, CLUSTER_TILES_Y, CLUSTER_SLICES_Z, CLUSTER_COUNT,
                    MAX_LIGHTS_PER_CLUSTER};
pub use shadow::{ShadowResources, ShadowPlan, ShadowMatricesUbo,
                 CSM_CASCADE_COUNT, MAX_SHADOW_SPOTS, SHADOW_DEPTH_FORMAT};
pub use rt_shadow::RtShadowResources;
// B2/B3 が消費する API 群。B1 時点ではエンジン内から一部しか参照しないため未使用警告が出るが、
// 公開 API 面の一覧として明示的に re-export しておく（消費側 B2 が pub パスで使う）。
#[allow(unused_imports)]
pub use bindless::{BindlessResources, BindlessInstanceRecord, BindlessModelAlloc,
                   BINDLESS_MAX_TEXTURES, BINDLESS_DUMMY_TEX_INDEX, BINDLESS_FLAG_ELIGIBLE,
                   set_bindless_supported, bindless_supported, bindless_capacity};
pub use refract_pyramid::{RefractPyramid, REFRACT_MIP_COUNT};
pub use water::{WaterRenderer, WaterParams, WATER_MAX_VOLUMES};
pub use caustics::CausticsTargets;
pub use interaction::{InteractionFieldRenderer, InteractionFieldUniformGpu, InteractionSourceGpu,
                      INTERACTION_FIELD_EXTENT_M, INTERACTION_FIELD_RESOLUTION,
                      INTERACTION_FIELD_DECAY_TAU_SECS, INTERACTION_MAX_SOURCES};
pub use reflection::{ReflectionPipelines, ReflectionParams,
                     RT_REFLECTION_NAME, REFLECTION_FORMAT, DEFAULT_REFLECTION_INTENSITY};
// 反射のミス経路が映すスカイボックス（D6 不透明反射と W5.2 水面反射で共用する入力データ）。
pub use reflection_sky::{ReflectionSkySource, ReflectionSkyUniform};
// パイプライン本体（`WaterReflectionPipelines`）は `DrawPipelines::water_reflection` 経由でしか
// 触らないので再エクスポートしない（型名を直接書く箇所が無い）。RT 名とフォーマットだけ公開する。
pub use water_reflection::{
    RT_WATER_REFLECTION_NAME, WATER_REFLECTION_FORMAT,
    WaterReflectionBlurTargets,
    water_reflection_blur_radius_px, WATER_REFL_BLUR_ITERATIONS,
};
pub use imos_blur::{ImosBlur, ImosBlurParams, IMOS_BLUR_FORMAT};
pub use ao::{AoPipelines, AoTargets, AoParams, AO_FORMAT, AO_RESOLUTION_DIVISOR,
             AO_SSAO_WORLD_RADIUS, AO_RTAO_WORLD_RADIUS, DEFAULT_AO_INTENSITY};
pub use ssgi::{SsgiPipelines, SsgiTargets, SsgiParams, SSGI_FORMAT, SSGI_RESOLUTION_DIVISOR};
pub use shadow_mask::{ShadowMaskPipelines, ShadowMaskTargets, ShadowMaskParams,
                      RT_SHADOW_MASK_LIGHTS, SHADOW_MASK_FORMAT, SHADOW_MASK_RESOLUTION_DIVISOR,
                      select_shadow_mask_lights, assign_shadow_mask_slots};
pub use shadow_mask_bilateral::{ShadowMaskBilateral, ShadowMaskBilateralParams};

/// **実 GPU テスト（`--ignored`）を直列化するためのロック**。
///
/// RT 対応・バインドレス対応は `rt_shadow` / `bindless` の**プロセス全体で 1 つのグローバル**で
/// 表現されており、GPU テストはそれを一時的に書き換えてから元へ戻す。`cargo test` は既定で
/// テストを並列に走らせるため、複数の GPU テストが同時に走ると
/// **片方の「元へ戻す」がもう片方の設定を潰し**、「RT 対応アダプタなのに RT 変種が構築されない」
/// といった偽陽性の失敗になる（実際に反射と水面反射の GPU テストで発生した）。
/// グローバルを書き換える GPU テストは必ず本ロックを取ること。
///
/// 毒されたロック（別テストが panic した後）でも検証は続行したいので、`unwrap_or_else` で
/// 中身を取り出して使う（`PoisonError::into_inner`）。
#[cfg(test)]
pub(crate) static GPU_TEST_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
pub use post::{RtPool, PostContext, VignetteParams, VignetteStage,
               PostFxSettings, BloomParams, BloomPipelines,
               DEFAULT_BLOOM_THRESHOLD, DEFAULT_BLOOM_KNEE, DEFAULT_BLOOM_INTENSITY,
               RT_SCENE_HDR, RT_POST_INTER, RT_LDR, GiSettings, TonemapOperator};
pub use transparency::{TransparencyMode, TransparentPipelines,
                       RT_WBOIT_ACCUM, RT_WBOIT_REVEAL,
                       WBOIT_ACCUM_FORMAT, WBOIT_REVEAL_FORMAT};
pub use batch2d::{SpriteBatcher, SpriteInstance, SpriteBatch, SpriteBatchList,
                  SPRITE_INSTANCE_SIZE, draw_sprite_batches, draw_sprite_outline_batches};
pub use postfx::{PostfxContext, SpritePostfxCache};
pub use view_mode::{SceneViewMode, GBufferDebugChannel, set_wireframe_supported, wireframe_supported};
pub use velocity_debug::{VelocityDebugPipeline, VELOCITY_DEBUG_ENABLED};
pub use gbuffer_debug::GBufferDebugPipeline;
pub use render_features::{RenderFeatures, ResolvedFeatures, ShadowMode, GiMode,
                          ReflectionMode, AoMode, TranslucencyMode};

// ============================================================
//  Renderer 本体
// ============================================================

use std::sync::Arc;
use winit::dpi::PhysicalSize;
use winit::window::Window;

// ============================================================
//  深度テクスチャ
// ============================================================

// Depth24PlusStencil8: ステンシルバッファを使用するため結合フォーマットを選択。
// Hi-Z 深度サンプリング用には DepthOnly ビューを別途作成する。
pub const DEPTH_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Depth24PlusStencil8;

/// シーン描画先の HDR オフスクリーンフォーマット（Phase R3）。
///
/// 3D メッシュ・スプライト・ギズモ等のメインパス＋キャンバスオーバーレイを
/// この Rgba16Float オフスクリーンへ描画し、フルスクリーンのトーンマップパスで
/// スワップチェーンへ出力する。全「シーン描画」パイプラインのカラーターゲット
/// フォーマットはこの値に合わせる（スワップチェーンフォーマットではない）。
pub const HDR_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;

/// wgpu の COPY_BYTES_PER_ROW_ALIGNMENT 要件 (256 バイト境界)。
const COPY_ROW_ALIGNMENT: u32 = 256;

/// メインパスの深度バッファ（Depth24PlusStencil8）。
///
/// レンダーアタッチメント用の All aspect ビューと、Hi-Z 等のテクスチャサンプリング用の
/// DepthOnly aspect ビューの両方を保持する。`Renderer::resize` / `begin_frame` の
/// サーフェスサイズ変化時に作り直される。
struct DepthTexture {
    #[allow(dead_code)]
    texture:         wgpu::Texture,
    /// レンダーアタッチメント用（All aspect: depth+stencil 両方）
    view:            wgpu::TextureView,
    /// Hi-Z テクスチャサンプリング用（DepthOnly aspect）
    depth_only_view: wgpu::TextureView,
    /// 作成時のテクスチャ幅（サーフェスサイズとの不一致検出に使用）
    width:           u32,
    /// 作成時のテクスチャ高さ（サーフェスサイズとの不一致検出に使用）
    height:          u32,
}

impl DepthTexture {
    fn new(device: &wgpu::Device, width: u32, height: u32) -> Self {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Depth Texture"),
            size: wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          DEPTH_FORMAT,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                 | wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        // All aspect: レンダーパスで depth+stencil を同時に操作するために使用
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        // DepthOnly aspect: Hi-Z コンピュートシェーダでの texture_depth_2d サンプリング用
        let depth_only_view = texture.create_view(&wgpu::TextureViewDescriptor {
            aspect: wgpu::TextureAspect::DepthOnly,
            ..Default::default()
        });
        Self { texture, view, depth_only_view, width, height }
    }
}

// ============================================================
//  Renderer
// ============================================================

/// wgpu 描画コンテキスト一式。
///
/// `device` / `queue` は `Arc` で共有し、`DrawContext` と所有権なしに共用できる。
pub struct Renderer {
    surface:        wgpu::Surface<'static>,
    device:         Arc<wgpu::Device>,
    queue:          Arc<wgpu::Queue>,
    config:         wgpu::SurfaceConfiguration,
    size:           PhysicalSize<u32>,
    depth_texture:  DepthTexture,
    /// コンパイル済みパイプライン状態のキャッシュ。
    /// GPU が PIPELINE_CACHE フィーチャーをサポートする場合のみ Some になる。
    pipeline_cache: Option<wgpu::PipelineCache>,
}

impl Renderer {
    pub fn new(window: Arc<Window>) -> Self {
        let size = window.inner_size();

        // Windows は Vulkan を優先使用する。
        // DX12 は PIPELINE_CACHE フィーチャー非対応の GPU が多く、起動時のシェーダー
        // コンパイル（~15秒）をキャッシュできない。Vulkan は PIPELINE_CACHE を
        // 普遍的にサポートするため、2回目以降の起動が大幅に高速化される。
        // NvOptimusEnablement シンボルエクスポートにより Optimus 環境でも
        // dGPU が列挙される。
        let backends = if cfg!(target_os = "windows") {
            wgpu::Backends::VULKAN
        } else {
            wgpu::Backends::PRIMARY
        };
        // Vulkan バリデーションレイヤーはデバッグビルドでも明示的に無効化する。
        // wgpu のデフォルト (InstanceFlags::from_build_config) は debug_assertions 時に
        // VALIDATION | DEBUG を有効化するが、Vulkan では全 API 呼び出しを検査するため
        // CPU オーバーヘッドが 10〜50 倍になりフレームレートが著しく低下する。
        // DX12 は同等の検査が外部レイヤーで行われるため影響が小さかった。
        // GPU レベルのデバッグが必要な場合は環境変数 WGPU_VALIDATION=1 で有効化する。
        let instance_flags = if std::env::var("WGPU_VALIDATION").is_ok() {
            wgpu::InstanceFlags::DEBUG | wgpu::InstanceFlags::VALIDATION
        } else {
            wgpu::InstanceFlags::empty()
        };

        let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor {
            backends,
            flags: instance_flags,
            ..Default::default()
        });

        let surface = instance
            .create_surface(window.clone())
            .expect("Failed to create surface");

        let adapter = Self::select_adapter(&instance, backends, &surface);

        // PIPELINE_CACHE はオプション機能（DX12 の一部 GPU では非対応）。
        // アダプターが対応している場合のみ要求する。
        let supports_pipeline_cache = adapter.features()
            .contains(wgpu::Features::PIPELINE_CACHE);

        // TEXTURE_COMPRESSION_BC はデスクトップ GPU ではほぼ全対応だが念のため確認する。
        // 対応時は派生キャッシュのテクスチャを BC 圧縮形式で保持し、
        // 非対応時は非圧縮 RGBA ミップでキャッシュする（デコードスキップだけでも高速）。
        let supports_bc = adapter.features()
            .contains(wgpu::Features::TEXTURE_COMPRESSION_BC);
        crate::engine::core::loader::asset_cache::set_bc_supported(supports_bc);

        // インラインレイトレ影（Phase R8）のための実験的 RT フィーチャー対応判定。
        // wgpu 25 は DX12/Vulkan で EXPERIMENTAL_RAY_QUERY（シェーダの rayQuery 構文）と
        // EXPERIMENTAL_RAY_TRACING_ACCELERATION_STRUCTURE（BLAS/TLAS 構築）を公開する。
        // 影のレイクエリには両方が必須のため、両対応の場合のみ RT 対応とみなす
        // （ドライバによっては片方のみ対応もあり得るため個別に確認する）。
        let af = adapter.features();
        let supports_rt = af.contains(wgpu::Features::EXPERIMENTAL_RAY_QUERY)
            && af.contains(wgpu::Features::EXPERIMENTAL_RAY_TRACING_ACCELERATION_STRUCTURE);
        // 対応フラグをグローバルへ設定する。頂点/インデックスバッファ生成時に
        // BLAS_INPUT 用途を付与するか否か（gpu_resources）を本フラグで判断する。
        // request_device より前に設定する必要はないが、以降のリソース生成に効くよう早めに設定。
        rt_shadow::set_rt_shadows_supported(supports_rt);
        if supports_rt {
            eprintln!("[SEED RT] インラインレイトレ: 対応（EXPERIMENTAL_RAY_QUERY + ACCELERATION_STRUCTURE を要求）");
        } else {
            // 非対応理由をできるだけ具体的に出す（両フィーチャーのどちらが欠けているか）。
            let has_q = af.contains(wgpu::Features::EXPERIMENTAL_RAY_QUERY);
            let has_a = af.contains(wgpu::Features::EXPERIMENTAL_RAY_TRACING_ACCELERATION_STRUCTURE);
            let reason = match (has_q, has_a) {
                (false, false) => "RAY_QUERY と ACCELERATION_STRUCTURE の両方が非対応",
                (true,  false) => "ACCELERATION_STRUCTURE が非対応",
                (false, true ) => "RAY_QUERY が非対応",
                (true,  true ) => "不明",
            };
            eprintln!("[SEED RT] インラインレイトレ: 非対応（{reason}）→ シャドウマップ経路を使用");
        }

        // DDGI（Phase RT-GI）は EXPERIMENTAL_RAY_QUERY（プローブ更新 compute の rayQuery）に依存する
        // ため、RT 対応と同一条件で有効になる。非対応 GPU では GI 無効＝従来のフラットアンビエント。
        {
            let dims = ddgi::GI_DEFAULT_DIMS;
            let probes = dims[0] * dims[1] * dims[2];
            let ppf = crate::engine::core::renderer::post::DEFAULT_GI_PROBES_PER_FRAME;
            let rpp = crate::engine::core::renderer::post::DEFAULT_GI_RAYS_PER_PROBE;
            if supports_rt {
                eprintln!(
                    "[SEED GI] DDGI: 有効（プローブ {probes} 個 / 更新 {ppf}個×{rpp}レイ/フレーム）"
                );
            } else {
                eprintln!("[SEED GI] DDGI: 非対応（EXPERIMENTAL_RAY_QUERY 非対応）→ フラットアンビエントを使用");
            }
        }

        // バインドレス基盤（フェーズ B1）の対応判定。
        // inline RT のヒットシェーディングでヒット先の albedo テクスチャ・UV を引くための土台。
        // 必要フィーチャー:
        //   - TEXTURE_BINDING_ARRAY: `binding_array<texture_2d>` 宣言そのもの。
        //   - SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING:
        //     ヒットごとに異なる（＝非一様な）インデックスで配列を引くために必須。
        //   - PARTIALLY_BOUND_BINDING_ARRAY は「望ましい」だけで必須ではない（B1 は全スロットを
        //     ダミーで埋めて構築するため非対応でも動く）。対応していれば併せて要求する。
        // さらに wgpu 25 は binding_array のサイズを `max_binding_array_elements_per_shader_stage`
        // （既定 0＝配列不可）で制限するため、アダプタがこれを 1 以上公開していることも条件にする。
        let adapter_limits = adapter.limits();
        let bindless_feat = af.contains(wgpu::Features::TEXTURE_BINDING_ARRAY)
            && af.contains(wgpu::Features::SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING);
        let bindless_array_cap = adapter_limits.max_binding_array_elements_per_shader_stage;
        let supports_bindless = bindless_feat && bindless_array_cap > 0;
        // テクスチャ配列容量 = 目安上限をアダプタ上限（配列要素数・sampled テクスチャ数）でクランプ。
        let bindless_capacity = BINDLESS_MAX_TEXTURES
            .min(bindless_array_cap)
            .min(adapter_limits.max_sampled_textures_per_shader_stage)
            .max(1);
        let supports_partially_bound = af.contains(wgpu::Features::PARTIALLY_BOUND_BINDING_ARRAY);
        bindless::set_bindless_supported(supports_bindless);
        bindless::set_bindless_capacity(if supports_bindless { bindless_capacity } else { 0 });
        if supports_bindless {
            eprintln!(
                "[SEED BINDLESS] 対応（TEXTURE_BINDING_ARRAY + 非一様インデックス）。\
                 テクスチャ配列容量={bindless_capacity}（要求目安 {BINDLESS_MAX_TEXTURES} / \
                 アダプタ上限: 配列要素 {bindless_array_cap} / sampled {}）, 部分バインド={}",
                adapter_limits.max_sampled_textures_per_shader_stage,
                if supports_partially_bound { "対応" } else { "非対応（全スロットをダミーで充填）" }
            );
        } else {
            let reason = if !bindless_feat {
                "TEXTURE_BINDING_ARRAY / 非一様インデックスが非対応"
            } else {
                "binding_array 要素数上限が 0"
            };
            eprintln!("[SEED BINDLESS] 非対応（{reason}）→ 従来の平均色経路を使用");
        }

        // GPU メッシュレットカリング（第1弾）は MULTI_DRAW_INDIRECT_COUNT に依存する。
        // 非対応 GPU では従来の CPU カリング＋draw_indexed 経路へ完全フォールバックする。
        let supports_mdi_count = af.contains(wgpu::Features::MULTI_DRAW_INDIRECT_COUNT);
        gpu_resources::set_meshlet_cull_supported(supports_mdi_count);
        if supports_mdi_count {
            eprintln!("[SEED MESHLET] メッシュレットカリング: 対応（MULTI_DRAW_INDIRECT_COUNT を要求）");
        } else {
            eprintln!("[SEED MESHLET] メッシュレットカリング: 非対応 → 従来 CPU カリング経路を使用");
        }

        // エディタのシーンビュー「ワイヤーフレーム」モードは POLYGON_MODE_LINE（ネイティブ限定）
        // に依存する。非対応 GPU ではワイヤ用パイプラインを生成せず、ワイヤ選択時も Unlit 表示へ
        // フォールバックする（クラッシュさせない）。対応可否は起動時に一度だけログする。
        let supports_wireframe = af.contains(wgpu::Features::POLYGON_MODE_LINE);
        view_mode::set_wireframe_supported(supports_wireframe);
        if supports_wireframe {
            eprintln!("[SEED VIEWMODE] ワイヤーフレーム: 対応（POLYGON_MODE_LINE を要求）");
        } else {
            eprintln!("[SEED VIEWMODE] ワイヤーフレーム: 非対応 → ワイヤ選択時は Unlit 表示にフォールバック");
        }

        // G-Buffer の MRT 帯域（速度バッファ追加で 5 枚 = 36 byte/sample）。
        // アダプタが申告した上限をそのまま要求する（申告値は定義上必ず通る）。
        // 万一 G-Buffer が要求する量に届かないアダプタなら、パイプライン生成時に
        // 検証エラーで落ちるより先にここで原因を明示しておく。
        let color_attachment_bytes_limit = adapter_limits.max_color_attachment_bytes_per_sample;
        if color_attachment_bytes_limit < gbuffer::GBUFFER_BYTES_PER_SAMPLE {
            eprintln!(
                "[SEED renderer] 警告: このアダプタの max_color_attachment_bytes_per_sample = {} は \
                 G-Buffer（MRT {} 枚 = {} byte/sample）に不足しています。\
                 G-Buffer パイプラインの生成に失敗する可能性があります。",
                color_attachment_bytes_limit,
                gbuffer::GBUFFER_ATTACHMENT_COUNT,
                gbuffer::GBUFFER_BYTES_PER_SAMPLE,
            );
        }

        let (device, queue) = pollster::block_on(adapter.request_device(
            &wgpu::DeviceDescriptor {
                label:             None,
                required_features: wgpu::Features::MULTI_DRAW_INDIRECT
                                 | wgpu::Features::INDIRECT_FIRST_INSTANCE
                                 | if supports_mdi_count { wgpu::Features::MULTI_DRAW_INDIRECT_COUNT } else { wgpu::Features::empty() }
                                 // ワイヤーフレーム描画（PolygonMode::Line）。対応時のみ要求する。
                                 | if supports_wireframe { wgpu::Features::POLYGON_MODE_LINE } else { wgpu::Features::empty() }
                                 | if supports_bc { wgpu::Features::TEXTURE_COMPRESSION_BC } else { wgpu::Features::empty() }
                                 | if supports_pipeline_cache { wgpu::Features::PIPELINE_CACHE } else { wgpu::Features::empty() }
                                 // RT 影は「対応していれば常に要求」する。設定によるオン/オフは
                                 // シェーダバリアント（RT対応時は常に RT パイプライン）と実行時フラグ
                                 // （LightMeta.rt_shadows）で切り替えるため、features は起動時固定でよい。
                                 | if supports_rt { wgpu::Features::EXPERIMENTAL_RAY_QUERY | wgpu::Features::EXPERIMENTAL_RAY_TRACING_ACCELERATION_STRUCTURE } else { wgpu::Features::empty() }
                                 // バインドレス基盤（フェーズ B1）。対応時のみ要求する。
                                 // 部分バインドは任意（対応していれば要求＝将来のスパースな配列に備える）。
                                 | if supports_bindless {
                                       wgpu::Features::TEXTURE_BINDING_ARRAY
                                     | wgpu::Features::SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING
                                     | if supports_partially_bound { wgpu::Features::PARTIALLY_BOUND_BINDING_ARRAY } else { wgpu::Features::empty() }
                                   } else { wgpu::Features::empty() },
                required_limits:   wgpu::Limits {
                    max_storage_buffers_per_shader_stage: 12,
                    max_bind_groups: 5,
                    // ── G-Buffer MRT 5 枚（速度バッファ追加）に必要なカラーアタッチメント帯域 ──
                    //
                    // 【なぜ既定値のままでは足りないのか】
                    // WebGPU 仕様の「1 サンプルあたりのカラーアタッチメント byte cost」は
                    // チャンネル実サイズではなく **フォーマットごとの固定表** で決まり、
                    // 4 チャンネル形式は 8bit でも 16bit でも一律 8 byte として数える
                    // （Rgba8Unorm=8 / Rgba16Float=8 / Rg16Float=4）。
                    // 速度 RT を足す前の G-Buffer 4 枚だけで既に 8+8+8+8 = **32 byte**、
                    // つまり既定リミット `max_color_attachment_bytes_per_sample = 32` に
                    // **ちょうど張り付いていた**。速度（Rg16Float=4）を足すと 36 になり、
                    // 既定のままでは `create_render_pipeline` が検証エラーで落ちる。
                    //
                    // 【引き上げが安全な理由】この上限はアダプタ実測ではなく
                    // 「max_color_attachments × 16」で導出される（wgpu-hal の DX12 / Vulkan は
                    // どちらもこの式）。既に `Limits::default()` で max_color_attachments = 8 を
                    // 要求済みなので、実効上限は 8×16 = 128 になり 36 は余裕で内側に入る。
                    // それでも念のためアダプタ実値でクランプし、要求値がアダプタ上限を
                    // 超えてデバイス生成が失敗することを構造的に避ける
                    // （アダプタが申告した値は定義上必ず要求できる）。
                    max_color_attachment_bytes_per_sample: color_attachment_bytes_limit,
                    // バインドレス対応時のみ、テクスチャ配列に必要なリミットをアダプタ上限内で引き上げる。
                    // 既定（sampled=16, binding_array 要素=0）のままでは容量分の配列を宣言できない。
                    // 非対応時は既定値のまま（＝従来と完全に同一）。
                    max_sampled_textures_per_shader_stage: if supports_bindless {
                        bindless_capacity.max(wgpu::Limits::default().max_sampled_textures_per_shader_stage)
                    } else {
                        wgpu::Limits::default().max_sampled_textures_per_shader_stage
                    },
                    max_binding_array_elements_per_shader_stage: if supports_bindless {
                        bindless_capacity
                    } else {
                        wgpu::Limits::default().max_binding_array_elements_per_shader_stage
                    },
                    ..wgpu::Limits::default()
                },
                memory_hints:      wgpu::MemoryHints::default(),
                ..Default::default()
            },
        ))
        .expect("Failed to create device");

        let device = Arc::new(device);
        let queue  = Arc::new(queue);

        // GPU が PIPELINE_CACHE をサポートする場合のみキャッシュを生成する。
        // exe 隣の pipeline_cache.bin からデータを読み込み、
        // ファイルが存在しない場合・不正データの場合は fallback=true により
        // 通常コンパイルにフォールバックする。
        let pipeline_cache = if supports_pipeline_cache {
            let cache_path = std::env::current_exe()
                .ok()
                .and_then(|p| p.parent().map(|d| d.join("pipeline_cache.bin")));
            let cache_data = cache_path.as_ref().and_then(|p| std::fs::read(p).ok());

            // Safety: cache_data は自分のプロセスが書き出したものを読み込む。
            // fallback=true なので不正データでもパニックせず再コンパイルに移行する。
            let cache = unsafe {
                device.create_pipeline_cache(&wgpu::PipelineCacheDescriptor {
                    label:    Some("SEED Pipeline Cache"),
                    data:     cache_data.as_deref(),
                    fallback: true,
                })
            };
            Some(cache)
        } else {
            None
        };

        let surface_caps   = surface.get_capabilities(&adapter);
        // sRGB フォーマットを優先して選択する。
        // Bgra8UnormSrgb 等の sRGB サーフェスは GPU がレンダーターゲット書き込み時に
        // linear → sRGB エンコードを自動適用するため、全シェーダーで統一的にガンマ補正される。
        let surface_format = surface_caps
            .formats
            .iter()
            .copied()
            .find(|f| f.is_srgb())
            .unwrap_or(surface_caps.formats[0]);

        // Mailbox → Immediate → Fifo の優先順で選択する。
        //
        // このランタイムは WPF の子ウィンドウとして DWM に埋め込まれるケースが多い。
        // DWM 自体が OS レベルの VSync を担当するため、Vulkan 側に独自の VSync（Fifo）を
        // 加えると「二重 VSync 待ち」になり、フレームが DWM タイミングとズレるたびに
        // 1 サイクル余計に待機してカクつきが発生する。
        //
        // Mailbox: GPU が終わり次第フレームを上書き → DWM の次コンポジットで最新フレームが映る
        // Immediate: 即時表示（DWM が VSync を管理するので実用上問題なし）
        // Fifo: 埋め込みモードでは二重 VSync でカクつくため最後の手段とする
        let present_mode = if surface_caps.present_modes.contains(&wgpu::PresentMode::Mailbox) {
            wgpu::PresentMode::Mailbox
        } else if surface_caps.present_modes.contains(&wgpu::PresentMode::Immediate) {
            wgpu::PresentMode::Immediate
        } else {
            wgpu::PresentMode::Fifo
        };

        // スクリーンショット機能が有効なときだけ COPY_SRC を足す。
        // サーフェスからの読み戻し（copy_texture_to_buffer）に必須だが、
        // 常時付けるとドライバによっては最適な提示パスを外れる可能性があるため、
        // 通常起動では従来どおり RENDER_ATTACHMENT のみにする。
        let surface_usage = if screenshot::is_enabled() {
            wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC
        } else {
            wgpu::TextureUsages::RENDER_ATTACHMENT
        };
        let config = wgpu::SurfaceConfiguration {
            usage:                        surface_usage,
            format:                       surface_format,
            width:                        size.width,
            height:                       size.height,
            present_mode,
            alpha_mode:                   surface_caps.alpha_modes[0],
            view_formats:                 vec![],
            desired_maximum_frame_latency: 2,
        };
        surface.configure(&device, &config);

        let depth_texture = DepthTexture::new(&device, size.width, size.height);

        Self { surface, device, queue, config, size, depth_texture, pipeline_cache }
    }

    // ── アダプター選択 ──────────────────────────────────────────

    /// 利用可能な GPU アダプターを列挙し、最適なものを選ぶ。
    ///
    /// 優先順位: DiscreteGpu > IntegratedGpu > その他
    /// サーフェスと互換性のないアダプターは除外する。
    fn select_adapter(
        instance: &wgpu::Instance,
        backends: wgpu::Backends,
        surface:  &wgpu::Surface<'_>,
    ) -> wgpu::Adapter {
        use wgpu::DeviceType;

        // DiscreteGpu を最優先にスコアリングする。
        fn adapter_score(info: &wgpu::AdapterInfo) -> u8 {
            match info.device_type {
                DeviceType::DiscreteGpu   => 3,
                DeviceType::IntegratedGpu => 2,
                DeviceType::VirtualGpu    => 1,
                _                         => 0,
            }
        }

        let mut adapters: Vec<wgpu::Adapter> = instance.enumerate_adapters(backends);

        // サーフェスと互換性のないアダプターを除外してからスコア順に並べる。
        // 除外しないとマルチ GPU 環境で対応外 GPU が選ばれ初期化エラーになる場合がある。
        adapters.retain(|a| a.is_surface_supported(surface));
        adapters.sort_by_key(|a| core::cmp::Reverse(adapter_score(&a.get_info())));

        let adapter = adapters.into_iter().next().unwrap_or_else(|| {
            // enumerate_adapters が空（環境依存でまれに発生）の場合のフォールバック。
            // HighPerformance を明示して dGPU を優先させる（iGPU への揺れを防ぐ）。
            pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions {
                power_preference:       wgpu::PowerPreference::HighPerformance,
                compatible_surface:     Some(surface),
                force_fallback_adapter: false,
            }))
            .expect("Failed to find a suitable GPU adapter")
        });

        // ── 選択アダプターを起動ログに必ず出力する ─────────────────────────
        // 「起動ごとに FPS が数倍変わる」不具合の第一容疑は、iGPU/dGPU の選択揺れ。
        // 20fps を引いたときにどの GPU・バックエンド・ドライバが選ばれたか一目で
        // 分かるよう、名前・種別・バックエンド・ドライバ情報を必ず出す。
        let info = adapter.get_info();
        eprintln!(
            "[SEED INIT] adapter={} type={:?} backend={:?} driver={} driver_info={} vendor=0x{:04x} device=0x{:04x}",
            info.name, info.device_type, info.backend,
            info.driver, info.driver_info, info.vendor, info.device,
        );

        adapter
    }

    // ── アクセサ ────────────────────────────────────────────────

    /// `wgpu::Device` の Arc クローンを返す。
    pub fn device(&self) -> Arc<wgpu::Device> { Arc::clone(&self.device) }

    /// `wgpu::Queue` の Arc クローンを返す。
    pub fn queue(&self) -> Arc<wgpu::Queue> { Arc::clone(&self.queue) }

    /// スワップチェーンのピクセルフォーマットを返す。
    pub fn surface_format(&self) -> wgpu::TextureFormat { self.config.format }

    /// 深度バッファのフォーマットを返す。
    pub fn depth_format(&self) -> wgpu::TextureFormat { DEPTH_FORMAT }

    /// コンパイル済みパイプラインキャッシュへの参照を返す。
    /// GPU が非対応の場合は None を返す。
    pub fn pipeline_cache(&self) -> Option<&wgpu::PipelineCache> { self.pipeline_cache.as_ref() }

    // ── パイプラインキャッシュ保存 ──────────────────────────────

    /// パイプラインキャッシュをディスクへ書き出す。
    ///
    /// exe 隣の `pipeline_cache.bin` に保存する。
    /// キャッシュが None（GPU 非対応）または `get_data()` が None の場合は何もしない。
    pub fn save_pipeline_cache(&self) {
        let Some(cache) = &self.pipeline_cache else { return; };
        let Some(data)  = cache.get_data() else { return; };
        let Some(path)  = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|d| d.join("pipeline_cache.bin")))
        else { return; };
        if let Err(e) = std::fs::write(&path, &data) {
            eprintln!("[SEED] pipeline cache save failed: {e}");
        }
    }

    // ── リサイズ ────────────────────────────────────────────────

    pub fn resize(&mut self, new_size: PhysicalSize<u32>) {
        if new_size.width > 0 && new_size.height > 0 {
            self.size = new_size;
            self.config.width  = new_size.width;
            self.config.height = new_size.height;
            self.surface.configure(&self.device, &self.config);
            self.depth_texture = DepthTexture::new(&self.device, new_size.width, new_size.height);
        }
    }

    // ── 描画 ────────────────────────────────────────────────────

    /// フレームの描画を開始し、`RenderFrame` を返す。
    ///
    /// 呼び出し元は `RenderFrame::begin_render_pass()` でレンダーパスを取得し、
    /// 描画コマンドを記録したあと `RenderFrame::finish()` で GPU にサブミットする。
    ///
    /// # 戻り値
    /// - `Ok(RenderFrame)`: 成功
    /// - `Err(SurfaceError)`: スワップチェーン取得失敗
    ///
    /// # 使用例
    /// ```rust
    /// let mut frame = renderer.begin_frame()?;
    /// {
    ///     let mut pass = frame.begin_render_pass();
    ///     draw_model(&mut pass, &gpu_model, &model, ...);
    /// }
    /// frame.finish();
    /// ```
    /// 深度テクスチャビューへの参照を返す（Hi-Z ピラミッド生成用、DepthOnly aspect）。
    pub fn depth_view(&self) -> &wgpu::TextureView { &self.depth_texture.depth_only_view }

    pub fn begin_frame(&mut self) -> Result<RenderFrame<'_>, wgpu::SurfaceError> {
        let output = self.surface.get_current_texture()?;

        // Vulkan の swapchain は surface.configure() の要求サイズを
        // current_extent（実際のウィンドウサイズ）にクランプする場合がある。
        // その際、depth_texture は要求サイズで作成済みのためサイズ不一致が発生し
        // レンダーパス検証でパニックする。フレーム開始時に実際のサーフェステクスチャ
        // サイズと depth_texture を同期させることで問題を防ぐ。
        let surf_extent = output.texture.size();
        if surf_extent.width  != self.depth_texture.width
        || surf_extent.height != self.depth_texture.height
        {
            self.config.width  = surf_extent.width;
            self.config.height = surf_extent.height;
            self.size = winit::dpi::PhysicalSize {
                width:  surf_extent.width,
                height: surf_extent.height,
            };
            self.depth_texture = DepthTexture::new(
                &self.device, surf_extent.width, surf_extent.height,
            );
        }

        let color_view = output.texture.create_view(&wgpu::TextureViewDescriptor::default());
        let encoder    = self.device.create_command_encoder(
            &wgpu::CommandEncoderDescriptor { label: Some("Render Encoder") },
        );
        Ok(RenderFrame {
            output,
            encoder,
            color_view,
            depth_view:      &self.depth_texture.view,
            depth_only_view: &self.depth_texture.depth_only_view,
            queue:           &self.queue,
            device:          &self.device,
        })
    }
}

// ============================================================
//  Drop — パイプラインキャッシュの自動保存
// ============================================================

/// `Renderer` がドロップされる際にパイプラインキャッシュを自動保存する。
impl Drop for Renderer {
    fn drop(&mut self) {
        self.save_pipeline_cache();
    }
}

// ============================================================
//  RenderFrame — 1 フレーム分の描画リソース
// ============================================================

/// `Renderer::begin_frame()` が返すフレームハンドル。
///
/// `begin_render_pass()` でレンダーパスを開き、描画コマンドを積んだあと、
/// スコープを外れてレンダーパスを閉じてから `finish()` でサブミットする。
pub struct RenderFrame<'r> {
    output:          wgpu::SurfaceTexture,
    encoder:         wgpu::CommandEncoder,
    color_view:      wgpu::TextureView,
    /// All aspect: レンダーアタッチメント（depth+stencil 操作）用
    depth_view:      &'r wgpu::TextureView,
    /// DepthOnly aspect: Hi-Z テクスチャサンプリング用
    depth_only_view: &'r wgpu::TextureView,
    queue:           &'r wgpu::Queue,
    /// スクリーンショットの読み戻し（バッファ生成 + マップ完了待ち）に使う。
    device:          &'r wgpu::Device,
}

impl<'r> RenderFrame<'r> {
    /// コマンドエンコーダへの可変参照を返す（Hi-Z compute pass 等に使用）。
    pub fn encoder_mut(&mut self) -> &mut wgpu::CommandEncoder { &mut self.encoder }

    /// 深度バッファビューへの参照を返す（Hi-Z ピラミッド生成用、DepthOnly aspect）。
    pub fn depth_view(&self) -> &wgpu::TextureView { self.depth_only_view }

    /// 深度テクスチャの DepthOnly aspect ビューを返す（G-Buffer 深度サンプリング用）。
    ///
    /// `depth_view()` と全く同じビューを指すが、デファードのライティングパスが
    /// 「深度をテクスチャとしてサンプルする」用途であることを呼び出し側で明示するための別名。
    /// アタッチメント用途（レンダーパスの depth_stencil_attachment）には引き続き
    /// `depth_view` フィールド（All aspect）側を使う（begin_gbuffer_pass_to 等を参照）。
    pub fn depth_only_view(&self) -> &wgpu::TextureView { self.depth_only_view }

    /// DepthOnly ビューを **フレーム('r) 寿命** で返す（Hi-Z ピラミッド生成用）。
    ///
    /// `depth_only_view()` は戻り値が `&self` 借用に縛られ、同時に `encoder_mut(&mut self)`
    /// を呼べない。Hi-Z は「深度ビュー＋エンコーダ」を同時に使うため、深度ビューを
    /// フレーム全体の借用 'r として取り出せるこのメソッドを使う（深度テクスチャは
    /// Renderer が所有し 'r の間は不変なので安全）。
    pub fn depth_only_view_r(&self) -> &'r wgpu::TextureView { self.depth_only_view }

    // ── 深度プリパス（カラー出力なし、深度クリアあり）────────────

    /// 深度のみ書き込むプリパスを開始する。
    ///
    /// Hi-Z ピラミッド生成の前に呼び出し、終了後に `encoder_mut()` 経由で
    /// `build_pyramid` を呼ぶこと。
    pub fn begin_depth_prepass<'f>(&'f mut self) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label:             Some("Depth Prepass"),
            color_attachments: &[],  // カラー出力なし
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(1.0),
                    store: wgpu::StoreOp::Store,
                }),
                // 深度プリパスではステンシルを使わない
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(0),
                    store: wgpu::StoreOp::Discard,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    // ── メインレンダーパス（深度プリパスの結果を保持）────────────

    /// 深度プリパスの結果を保持したままメインレンダーパスを開始する。
    ///
    /// 深度バッファは `LoadOp::Load`（プリパスの深度を引き継ぎ）。
    /// Early-Z により、深度プリパスで隠れたフラグメントはシェーダー実行をスキップする。
    pub fn begin_render_pass_with_prepass<'f>(&'f mut self) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Main Render Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           &self.color_view,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(wgpu::Color { r: 0.1, g: 0.1, b: 0.1, a: 1.0 }),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Load,   // プリパスの深度を引き継ぐ
                    store: wgpu::StoreOp::Store,
                }),
                // ステンシルをクリアして、このパスで描画された全ピクセルに 1 を書き込む
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(0),
                    store: wgpu::StoreOp::Store,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// クリア付きのメインレンダーパスを開始する。
    ///
    /// 返値のレンダーパスをドロップしてから `finish()` を呼ぶこと。
    pub fn begin_render_pass<'f>(&'f mut self, clear_color: wgpu::Color) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Main Render Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           &self.color_view,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(clear_color),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(1.0),
                    store: wgpu::StoreOp::Store,
                }),
                // ステンシルを 0 にクリア。draw_model_indirect が 1 を書き込み、
                // draw_outline が 0 の箇所（シルエット外側）のみ描画する。
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(0),
                    store: wgpu::StoreOp::Store,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// スワップチェーンの実サーフェスサイズ（ピクセル）を返す。
    ///
    /// HDR オフスクリーンをスワップチェーンと 1:1 で確保するために使う。
    pub fn surface_size(&self) -> (u32, u32) {
        let s = self.output.texture.size();
        (s.width, s.height)
    }

    /// スワップチェーンのカラービューへの参照を返す（トーンマップ出力先）。
    pub fn swapchain_view(&self) -> &wgpu::TextureView { &self.color_view }

    /// 外部カラービュー（HDR オフスクリーン等）へメインレンダーパスを開始する（Phase R3）。
    ///
    /// `begin_render_pass` と同じクリア/深度/ステンシル構成だが、カラーターゲットのみ
    /// 呼び出し側指定のビュー（Rgba16Float の HDR オフスクリーン）へ差し替える。
    /// 深度・ステンシルは従来どおり共有深度テクスチャを使う（ID パス等がこの深度を参照する）。
    pub fn begin_scene_pass_to<'f>(
        &'f mut self,
        color_view:  &'f wgpu::TextureView,
        clear_color: wgpu::Color,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Main Render Pass (HDR)"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           color_view,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(clear_color),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(1.0),
                    store: wgpu::StoreOp::Store,
                }),
                // begin_render_pass と同じくステンシルを 0 にクリア（アウトライン用）。
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(0),
                    store: wgpu::StoreOp::Store,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// 外部カラービュー（HDR オフスクリーン）へキャンバスオーバーレイパスを開始する（Phase R3）。
    ///
    /// `begin_canvas_overlay_pass` と同じ（カラー Load・深度 Clear）だが、カラーターゲットを
    /// 呼び出し側指定の HDR ビューへ差し替える（メインパスと同じ HDR へ合成する）。
    pub fn begin_canvas_overlay_pass_to<'f>(
        &'f mut self,
        color_view: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Canvas Overlay Pass (HDR)"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           color_view,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(1.0),
                    store: wgpu::StoreOp::Store,
                }),
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(0),
                    store: wgpu::StoreOp::Store,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// エディタオーバーレイ（ギズモ・軸・各種ライン／ワイヤー・アイコン・選択アウトライン等）を
    /// WBOIT 合成後の HDR へ重ねるためのパスを開始する。
    ///
    /// 背景: WBOIT 合成はフルスクリーンの `no_depth` クアッドでシーン HDR を上書き／減衰するため、
    /// オーバーレイをメインパス内（＝合成より前）に描くと、半透明被覆のある画面座標で 3D 前後関係を
    /// 無視して消されてしまう。これを避けるため、オーバーレイは合成の「後」に本パスで描く。
    ///
    /// - color  = `color_view`（シーン HDR）を `LoadOp::Load`（合成済み HDR を保持して上に重ねる）。
    /// - depth  = メインパス共有の `depth_view` を `LoadOp::Load`（不透明深度を保持しテストのみ。
    ///   パイプライン側 `depth_write=false` のため書き込みは行わない → `LessEqual` のワイヤは
    ///   不透明に隠れ、`Always` のギズモ／軸は常に最前面に出る）。
    /// - stencil = `LoadOp::Clear(0)`（選択アウトラインのステンシルマスク用に 0 初期化。
    ///   深度とは独立に aspect ごとの ops を指定できるため、深度 Load ＋ ステンシル Clear が成立する）。
    ///
    /// WBOIT 合成後・GPU パーティクル前に呼ぶこと（HDR・トーンマップ前）。
    pub fn begin_overlay_pass_to<'f>(
        &'f mut self,
        color_view: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Editor Overlay Pass (HDR)"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           color_view,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Load, // 不透明の深度を保持（テストのみ・書込なし）。
                    store: wgpu::StoreOp::Store,
                }),
                // 選択アウトライン用にステンシルは 0 クリアする（深度とは独立）。
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(0),
                    store: wgpu::StoreOp::Store,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// 水面描画パスを開始する（Phase W1。WBOIT 合成後・エディタオーバーレイ前）。
    ///
    /// - color = `hdr`（シーン HDR）を `LoadOp::Load`（既存の描画結果を保持し、水面を上に描く）。
    /// - depth = **アタッチメントを持たない**（`depth_stencil_attachment: None`）。
    ///
    /// 【なぜ深度アタッチメントを持たないか】
    /// 共有深度テクスチャは他パスで書き込み可能としてアタッチされており、同一パス内で
    /// 「アタッチメント」と「サンプルテクスチャ」に同時バインドするとエイリアシングになる。
    /// 水面シェーダは深度を **サンプル** して手動深度テスト（遮蔽 → discard）と
    /// 水の厚み復元を行うため、アタッチメントを外してサンプル専用にする。
    /// 水面は元々深度を書かないので、通常の Z テストと挙動は等価。
    ///
    /// 【なぜこの位置か】WBOIT 合成の後に置くことで、距離ソート／WBOIT どちらのモードでも
    /// 同じ経路になり、かつ屈折の背景グラブに既存半透明・スカイボックスが含まれる。
    /// エディタオーバーレイ（ギズモ等）より前なので、ギズモは水面の上に出る。
    pub fn begin_water_pass_to<'f>(
        &'f mut self,
        hdr: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Water Surface Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           hdr,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                },
            })],
            // 深度は group1 のサンプルテクスチャとして読む（上記コメント参照）。
            depth_stencil_attachment: None,
            occlusion_query_set:      None,
            timestamp_writes:         None,
        })
    }

    /// WBOIT の accum/reveal ターゲットへ透明描画パスを開始する（Phase R5）。
    ///
    /// - color0 = accum : LoadOp::Clear(0,0,0,0)（加算蓄積の初期値）。
    /// - color1 = reveal: LoadOp::Clear(1,1,1,1)（透過率 1＝未遮蔽の初期値）。
    /// - depth = メインパス共有の depth_view を LoadOp::Load（不透明深度でテスト、
    ///   書き込みはパイプライン側 depth_write=false のため行わない）。
    /// メインパス drop 後・ブルーム前に呼ぶこと。
    pub fn begin_wboit_pass_to<'f>(
        &'f mut self,
        accum_view:  &'f wgpu::TextureView,
        reveal_view: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("WBOIT Accum/Reveal Pass"),
            color_attachments: &[
                Some(wgpu::RenderPassColorAttachment {
                    view:           accum_view,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load:  wgpu::LoadOp::Clear(wgpu::Color { r: 0.0, g: 0.0, b: 0.0, a: 0.0 }),
                        store: wgpu::StoreOp::Store,
                    },
                }),
                Some(wgpu::RenderPassColorAttachment {
                    view:           reveal_view,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load:  wgpu::LoadOp::Clear(wgpu::Color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 }),
                        store: wgpu::StoreOp::Store,
                    },
                }),
            ],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Load, // 不透明の深度を保持（テストのみ）。
                    store: wgpu::StoreOp::Store,
                }),
                // Depth24PlusStencil8 は stencil 面を持つため ops を明示（Load/Store）。
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// GPU パーティクル描画パスを開始する（Phase RP）。
    ///
    /// - color = `hdr_view`（シーン HDR）を LoadOp::Load（既存の描画へ加算／合成で重ねる）。
    /// - depth = メインパス共有の depth_view を LoadOp::Load（不透明深度で遮蔽テストのみ。
    ///   書き込みはパイプライン側 depth_write=false のため行わない）。
    /// メインパス drop 後・WBOIT 合成後・ブルーム前に呼ぶこと（HDR・トーンマップ前）。
    /// エミッタ 0 個ならそもそもこのパスを開かないこと（呼び出し側が has_emitters で判定）。
    pub fn begin_particle_pass_to<'f>(
        &'f mut self,
        hdr_view: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Particle Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           hdr_view,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Load, // 既存シーン HDR を保持して重ねる。
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Load, // 不透明の深度を保持（テストのみ・書込なし）。
                    store: wgpu::StoreOp::Store,
                }),
                // Depth24PlusStencil8 は stencil 面を持つため ops を明示（Load/Store）。
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    // ── デファード（G-Buffer + フルスクリーン・ライティング）パス（Phase D3 Deferred Phase B）───

    /// G-Buffer 書き込みパスを開始する（5 枚の MRT + 深度）。
    ///
    /// 各 RT は LoadOp::Clear(0,0,0,0)（不透明ジオメトリで上書きするため加算合成は行わない。
    /// クリア値 0 は「ジオメトリが存在しない画素＝背景」を意味し、ライティングパス側は
    /// 深度 >= 1.0（背景）を discard することで区別する。gbuffer_write.wgsl 側のクリア規約と一致）。
    /// 深度は `begin_scene_pass_to` と同じ規約（Clear(1.0) / ステンシル Clear(0)）で新規に確保し直す
    /// （デファードのフレームでは本パスが「深度を最初に書く」パスになるため）。
    ///
    /// `velocity`（RT4・速度＝モーションベクタ）のクリア値 0 は「移動量ゼロ」を意味し、
    /// ジオメトリが無い背景画素の値としてそのまま正しい（背景は動かない）。
    /// レターボックス帯のように viewport 外で描画されない画素も 0 のまま残る。
    pub fn begin_gbuffer_pass_to<'f>(
        &'f mut self,
        g0: &'f wgpu::TextureView,
        g1: &'f wgpu::TextureView,
        g2: &'f wgpu::TextureView,
        g3: &'f wgpu::TextureView,
        velocity: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        let mrt_clear = wgpu::Operations {
            load:  wgpu::LoadOp::Clear(wgpu::Color { r: 0.0, g: 0.0, b: 0.0, a: 0.0 }),
            store: wgpu::StoreOp::Store,
        };
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("GBuffer Pass"),
            color_attachments: &[
                Some(wgpu::RenderPassColorAttachment { view: g0, resolve_target: None, ops: mrt_clear }),
                Some(wgpu::RenderPassColorAttachment { view: g1, resolve_target: None, ops: mrt_clear }),
                Some(wgpu::RenderPassColorAttachment { view: g2, resolve_target: None, ops: mrt_clear }),
                Some(wgpu::RenderPassColorAttachment { view: g3, resolve_target: None, ops: mrt_clear }),
                Some(wgpu::RenderPassColorAttachment { view: velocity, resolve_target: None, ops: mrt_clear }),
            ],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(1.0),
                    store: wgpu::StoreOp::Store,
                }),
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(0),
                    store: wgpu::StoreOp::Store,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// G-Buffer 書き込みパスを **外部深度ビュー** へ向けて開始する（カメラプレビュー用・案A）。
    ///
    /// `begin_gbuffer_pass_to` と挙動は同一（4 枚 MRT を Clear(0)・深度を Clear(1.0)/
    /// ステンシル Clear(0)）だが、深度アタッチメントを共有サーフェス深度ではなく呼び出し側
    /// 指定のビュー（プレビュー専用の小さな深度テクスチャ）へ差し替える。
    /// プレビューはメインパスとは別解像度・別深度のミニ G-Buffer へ焼くため、共有深度を使う
    /// `begin_gbuffer_pass_to` は流用できない（サイズ不一致・メインパスとの取り合いが起きる）。
    ///
    /// ## 速度 RT（RT4）はプレビューでは「捨てる先」である
    /// カメラプレビューは TAA もモーションブラーも掛けない小窓であり、速度を必要としない。
    /// しかし G-Buffer パイプラインは本番と**同一のもの**（＝カラーターゲット 5 枚）を
    /// 共有しているため、アタッチメント数を減らすことはできない
    /// （減らすには 4 ターゲット版のパイプラインを全バリアント複製する必要があり、
    ///   パイプライン数が倍になる。プレビューは小解像度なので捨て RT の方が圧倒的に安い）。
    /// そこでプレビュー専用の小さな速度テクスチャへ書き、**誰も読まない**。
    /// さらにプレビューカメラの `CameraUniform` は `prev_view_proj = view_proj`
    /// （＝カメラ由来の速度が常に 0）で更新するため、実質的に無効化されている。
    pub fn begin_gbuffer_pass_to_depth<'f>(
        &'f mut self,
        g0: &'f wgpu::TextureView,
        g1: &'f wgpu::TextureView,
        g2: &'f wgpu::TextureView,
        g3: &'f wgpu::TextureView,
        velocity: &'f wgpu::TextureView,
        depth_view: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        let mrt_clear = wgpu::Operations {
            load:  wgpu::LoadOp::Clear(wgpu::Color { r: 0.0, g: 0.0, b: 0.0, a: 0.0 }),
            store: wgpu::StoreOp::Store,
        };
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("GBuffer Pass (Camera Preview)"),
            color_attachments: &[
                Some(wgpu::RenderPassColorAttachment { view: g0, resolve_target: None, ops: mrt_clear }),
                Some(wgpu::RenderPassColorAttachment { view: g1, resolve_target: None, ops: mrt_clear }),
                Some(wgpu::RenderPassColorAttachment { view: g2, resolve_target: None, ops: mrt_clear }),
                Some(wgpu::RenderPassColorAttachment { view: g3, resolve_target: None, ops: mrt_clear }),
                Some(wgpu::RenderPassColorAttachment { view: velocity, resolve_target: None, ops: mrt_clear }),
            ],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(1.0),
                    store: wgpu::StoreOp::Store,
                }),
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(0),
                    store: wgpu::StoreOp::Store,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// G-Buffer からライティングを復元するフルスクリーンパスを開始する（HDR シーンへ出力）。
    ///
    /// - color = `hdr` を LoadOp::Clear(clear)（背景色でクリアしてからフルスクリーン三角形で
    ///   ライティング結果を上書きする。深度 >= 1.0＝背景の画素はシェーダ側で discard するため
    ///   クリア色がそのまま残る＝メインパス clear と同じ見た目になる）。
    /// - depth_stencil_attachment = None（本パスは深度を「アタッチメント」としては持たない。
    ///   G-Buffer 深度は `depth_only_view` からテクスチャとしてサンプルして使うため）。
    pub fn begin_deferred_lighting_pass_to<'f>(
        &'f mut self,
        hdr:   &'f wgpu::TextureView,
        clear: wgpu::Color,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Deferred Lighting Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           hdr,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(clear),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: None,
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// デファードのメインシーンパスを「Load」で再開する（G-Buffer パス＋ライティングパスが
    /// 書いた HDR／深度／ステンシルを一切クリアせず保持する）。
    ///
    /// `begin_scene_pass_to` の Load 版：半透明・スカイボックス・ギズモ等、フォワードで
    /// 描き続ける要素をデファードのライティング結果の上に重ねるためのパス。
    /// Depth24PlusStencil8 はステンシル面を持つため ops を明示（Load/Store）する
    /// （`begin_wboit_pass_to` 等の既存 Load パスと同じ規約）。
    pub fn begin_scene_pass_load_to<'f>(
        &'f mut self,
        hdr: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Main Render Pass (HDR, Deferred Load)"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           hdr,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                }),
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    // ── AO（SSAO / RT-AO）生成パス（Phase D4）─────────────────

    /// 半解像度 AO ターゲット（ao_raw）へ AO 生成パスを開始する（G-Buffer＋深度 → AO=1..0）。
    ///
    /// - color = `ao` を LoadOp::Clear(1,1,1,1)（AO=1＝遮蔽なし。フルスクリーン三角形が全画素を
    ///   上書きするためクリア値は保険。背景画素はシェーダが AO=1 を返す）。
    /// - depth = None（フルスクリーン三角形。G-Buffer 深度はテクスチャとして読む）。
    /// この後にいもす法ブラー（compute）を同一エンコーダで走らせ、結果 ao_b を deferred
    /// ライティングの group1 binding6（t_ao）へ供給する。
    pub fn begin_ao_pass_to<'f>(
        &'f mut self,
        ao: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("AO Generation Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           ao,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(wgpu::Color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 }),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: None,
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    // ── 水中コースティクス生成パス（Phase W5.3）────────────────────

    /// フル解像度のコースティクスターゲットへ生成パスを開始する。
    ///
    /// - color = `caustics` を **LoadOp::Clear(0.0)**。0 ＝「集光の寄与なし」であり、
    ///   水中でないピクセル（フラグメントが `discard` する所）はこの値のまま残る。
    ///   したがってクリア値は保険ではなく **意味のある既定値**である。
    /// - depth = None（フルスクリーン三角形。共有深度は group3 のサンプルテクスチャとして読む。
    ///   同一パスでアタッチメント兼サンプルにすると read/write 競合になるため）。
    pub fn begin_caustics_pass_to<'f>(
        &'f mut self,
        caustics: &'f wgpu::TextureView,
        caustics_offset: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        // 2 枚とも Clear(0)。**0 が両方の中立値**であることがこのパスの安全弁である:
        //   location0（透過率 rgb / 集光 a）: 全成分 0 は「水中ではない」の印で、
        //     消費側（deferred）が中立値 (1,1,1,0) へ写し直す（乗算項のため）。
        //   location1（影の屈折オフセット XZ）: 0 がそのまま「ずらさない」＝加算の中立値。
        // 非水中ピクセルは fs_caustics が discard するため、どちらもクリア値のまま残る。
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Water Caustics Generation Pass"),
            color_attachments: &[
                Some(wgpu::RenderPassColorAttachment {
                    view:           caustics,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load:  wgpu::LoadOp::Clear(wgpu::Color { r: 0.0, g: 0.0, b: 0.0, a: 0.0 }),
                        store: wgpu::StoreOp::Store,
                    },
                }),
                Some(wgpu::RenderPassColorAttachment {
                    view:           caustics_offset,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load:  wgpu::LoadOp::Clear(wgpu::Color { r: 0.0, g: 0.0, b: 0.0, a: 0.0 }),
                        store: wgpu::StoreOp::Store,
                    },
                }),
            ],
            depth_stencil_attachment: None,
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    // ── 水面反射パス（Phase W5.2）──────────────────────────────

    /// 水面反射 RT へ生成パスを開始する（**屈折背景グラブの後・水面パスの前**に呼ぶ）。
    ///
    /// - color = `water_reflection` を **LoadOp::Clear(0,0,0,0)**。
    ///   A（＝反射強度）0 が「反射寄与なし」の中立値であり、水面が無いピクセル・
    ///   不透明物に遮蔽されて `discard` したピクセルはこの値のまま残る。
    ///   したがってクリア値は保険ではなく **意味のある既定値**である。
    /// - depth = None（共有深度は group3 のサンプルテクスチャとして読む。同一パスで
    ///   アタッチメント兼サンプルにすると read/write 競合になるため。水面パスと同じ理由）。
    ///
    /// 【なぜこの位置か】反射には「不透明＋スカイボックスが描き終わったシーンカラー」が要る。
    /// グラブ（`WaterRenderer::record_grab`）はまさにそれのコピーなので、その直後に置けば
    /// 空が映る反射になり、かつ scene_hdr との読み書き競合も起きない。
    /// 水面自身はまだ描かれていないので、反射が水面を映す自己参照も構造的に起きない。
    pub fn begin_water_reflection_pass_to<'f>(
        &'f mut self,
        water_reflection: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Water Reflection Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           water_reflection,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(wgpu::Color { r: 0.0, g: 0.0, b: 0.0, a: 0.0 }),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: None,
            occlusion_query_set:      None,
            timestamp_writes:         None,
        })
    }

    // ── SSGI（スクリーンスペース GI）生成パス（Phase SSGI）─────────

    /// 半解像度 SSGI ターゲット（ssgi_raw）へ生成パスを開始する（G-Buffer＋scene_hdr → 間接光 .rgb）。
    ///
    /// - color = `ssgi` を LoadOp::Clear(0,0,0,0)（フルスクリーン三角形が全画素を上書きするため
    ///   クリア値は保険）。 - depth = None（フルスクリーン三角形。G-Buffer 深度はテクスチャとして読む）。
    /// scene_hdr（不透明ライティング済み）はサンプル入力として読む＝描画先が別テクスチャ（ssgi_raw）の
    /// ため読み書き競合しない。この後にいもす法カラーブラー（compute）を同一エンコーダで走らせ、
    /// 結果 ssgi_b を **次フレーム** の deferred ライティングの group1 binding8（t_ssgi）へ供給する。
    pub fn begin_ssgi_pass_to<'f>(
        &'f mut self,
        ssgi: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("SSGI Generation Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           ssgi,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(wgpu::Color { r: 0.0, g: 0.0, b: 0.0, a: 0.0 }),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: None,
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    // ── RT ソフト影マスク生成パス（Phase RT-Shadow-Denoise）─────────

    /// 半解像度シャドウマスク（texture_2d_array の 4 レイヤ）へマスク生成パスを開始する。
    ///
    /// - color location 0..3 = 4 レイヤぶんの単層 2D ビューを MRT として LoadOp::Clear(1,1,1,1)
    ///   （.rgb=透過率 1＝遮蔽なし。count 未満のスロット・背景はシェーダが .rgb=既定値を返す）。
    ///   各レイヤの .a には half-res ビュー空間深度が同梱される（バイラテラルブラーの深度ガイド。
    ///   全テクセルがシェーダで上書きされるため .a のクリア値は結果に影響しない）。
    /// - depth = None（フルスクリーン三角形。G-Buffer 深度はテクスチャとして読む）。
    /// この後にバイラテラルブラー（各レイヤ・compute）を同一エンコーダで走らせ、結果 mask_b を
    /// deferred ライティングの group1 binding10（t_shadow_mask）へ供給する。
    pub fn begin_shadow_mask_pass_to<'f>(
        &'f mut self,
        l0: &'f wgpu::TextureView,
        l1: &'f wgpu::TextureView,
        l2: &'f wgpu::TextureView,
        l3: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        let clear = wgpu::Operations {
            load:  wgpu::LoadOp::Clear(wgpu::Color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 }),
            store: wgpu::StoreOp::Store,
        };
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Shadow Mask Generation Pass"),
            color_attachments: &[
                Some(wgpu::RenderPassColorAttachment { view: l0, resolve_target: None, ops: clear }),
                Some(wgpu::RenderPassColorAttachment { view: l1, resolve_target: None, ops: clear }),
                Some(wgpu::RenderPassColorAttachment { view: l2, resolve_target: None, ops: clear }),
                Some(wgpu::RenderPassColorAttachment { view: l3, resolve_target: None, ops: clear }),
            ],
            depth_stencil_attachment: None,
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    // ── 反射（SSR / RT）パス（Phase D6）─────────────────────────

    /// 反射専用 RT（RT_REFLECTION）へ反射パスを開始する（G-Buffer＋scene_hdr 入力→反射色）。
    ///
    /// - color = `reflection` を LoadOp::Clear(0,0,0,0)（反射の無い画素は 0＝合成加算で無害）。
    /// - depth = None（フルスクリーン三角形。深度は G-Buffer 深度をテクスチャとして読む）。
    /// scene_hdr（不透明ライティング完成済み）はサンプル入力として読むため、描画先が別テクスチャ
    /// （RT_REFLECTION）である本パスでは読み書き競合が起きない（SSR 自己参照制約の回避）。
    pub fn begin_reflection_pass_to<'f>(
        &'f mut self,
        reflection: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Reflection Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           reflection,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(wgpu::Color { r: 0.0, g: 0.0, b: 0.0, a: 0.0 }),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: None,
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// 反射合成パスを開始する（RT_REFLECTION を scene_hdr へ Additive 加算）。
    ///
    /// - color = `hdr`（scene_hdr）を LoadOp::Load（不透明ライティング結果を保持）。
    ///   合成パイプライン側が One/One の Additive ブレンドで反射色を足し込む。
    /// - depth = None（フルスクリーン三角形・深度不要。合成パイプラインも depth_stencil なし）。
    pub fn begin_reflection_composite_pass_to<'f>(
        &'f mut self,
        hdr: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Reflection Composite Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           hdr,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: None,
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// HDR オフスクリーンをトーンマップ（＋任意ビネット）して LDR 中間 RT へ出力する（Phase R4）。
    ///
    /// R3 では直接スワップチェーンへ出していたが、R4 で 2D オーバーレイをトーンマップ後の
    /// LDR へ描くため、いったん `ldr_view`（RtPool の RT_LDR, Rgba16Float）へ出す。
    /// この後にオーバーレイを `ldr_view` へ描き、最終段 `present_to_swapchain` で書き出す。
    pub fn tonemap_to_ldr(
        &mut self,
        post:     &PostContext,
        device:   &wgpu::Device,
        hdr_view: &wgpu::TextureView,
        ldr_view: &wgpu::TextureView,
        vignette: Option<VignetteStage<'_>>,
        operator: post::TonemapOperator,
    ) {
        post.run(device, &mut self.encoder, hdr_view, ldr_view, vignette, operator);
    }

    /// LDR 中間（＋オーバーレイ）をスワップチェーンへ書き出す最終段（FXAA or コピー, Phase R4）。
    pub fn present_to_swapchain(
        &mut self,
        post:         &PostContext,
        device:       &wgpu::Device,
        ldr_view:     &wgpu::TextureView,
        fxaa_enabled: bool,
    ) {
        let (w, h) = self.surface_size();
        // self.encoder（可変）と self.color_view（不変）は別フィールドのため同時借用可。
        post.present(device, &mut self.encoder, ldr_view, &self.color_view, w, h, fxaa_enabled);
    }

    /// キャンバスオーバーレイパスを開始する。
    ///
    /// カラーバッファを保持（3D シーンを維持）しつつ深度バッファのみクリアして、
    /// 2D キャンバス要素（スプライト・ギズモ等）を常に前面へ描画する。
    /// begin_render_pass より後に呼ぶこと。
    pub fn begin_canvas_overlay_pass<'f>(&'f mut self) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Canvas Overlay Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           &self.color_view,
                resolve_target: None,
                ops: wgpu::Operations {
                    // 3D シーンのカラーを保持する（クリアしない）
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    // 深度をクリアして 2D 要素を必ず前面描画
                    load:  wgpu::LoadOp::Clear(1.0),
                    store: wgpu::StoreOp::Store,
                }),
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(0),
                    store: wgpu::StoreOp::Store,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// オフスクリーンテクスチャへの描画パスを開始する（カメラプレビュー等に使用）。
    ///
    /// カラーバッファは `color_view` にクリアして描画する。
    /// 深度バッファは `depth_view` にクリア（1.0）して描画する。
    pub fn begin_offscreen_pass<'f>(
        &'f mut self,
        color_view:  &'f wgpu::TextureView,
        depth_view:  &'f wgpu::TextureView,
        clear_color: wgpu::Color,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Offscreen Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           color_view,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(clear_color),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Clear(1.0),
                    store: wgpu::StoreOp::Store,
                }),
                stencil_ops: None,
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// オフスクリーンテクスチャへ **Load**（クリアせず保持）で描画パスを開始する。
    ///
    /// カメラプレビューのミニデファード（案A）で、フルスクリーン・ライティングが焼いた
    /// カラー（`color_view`）と、G-Buffer パスが書いた深度（`depth_view`）を保持したまま、
    /// フォワード要素（スカイボックス・半透明・3D スプライト）を上に重ねるためのパス。
    /// 深度は Load（G-Buffer 深度で遮蔽テスト＋スカイボックス等は書き込みあり）。
    /// ステンシルは使わない（プレビューはアウトライン非対象）ため ops を持たせない。
    pub fn begin_offscreen_load_pass<'f>(
        &'f mut self,
        color_view: &'f wgpu::TextureView,
        depth_view: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Offscreen Pass (Camera Preview, Load)"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           color_view,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                }),
                stencil_ops: None,
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// メインカラーバッファへのブリットパスを開始する（UI オーバーレイ等に使用）。
    ///
    /// 深度なし・カラー Load（既存の描画を保持）で、最前面に描画する。
    /// `depth_compare = "Always"` のパイプラインと組み合わせること。
    pub fn begin_blit_pass<'f>(&'f mut self) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("Blit Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           &self.color_view,
                resolve_target: None,
                ops: wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Store,
                },
            })],
            // 深度アタッチメントなし（常に最前面）
            depth_stencil_attachment: None,
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// フレーム先頭でバッファ全体を 0 クリアする（draw_count リセット用）。
    pub fn clear_buffer(&mut self, buf: &wgpu::Buffer) {
        self.encoder.clear_buffer(buf, 0, None);
    }

    /// コンピュートパスを開始する。
    ///
    /// 返値のパスをドロップしてから次の操作（レンダーパス等）を行うこと。
    pub fn begin_compute_pass<'f>(&'f mut self) -> wgpu::ComputePass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label:            Some("Cull Compute Pass"),
            timestamp_writes: None,
        })
    }

    /// ID バッファパスを開始する（メインレンダーパスの後に呼ぶ）。
    ///
    /// - `id_view`  : R32Uint テクスチャビュー（クリアして書き込む）
    /// - 深度は Load して参照するのみ（write なし）
    pub fn begin_id_pass<'f>(
        &'f mut self,
        id_view: &'f wgpu::TextureView,
    ) -> wgpu::RenderPass<'f>
    where
        'r: 'f,
    {
        self.encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("World Pos ID Pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view:           id_view,
                resolve_target: None,
                ops: wgpu::Operations {
                    // 全チャンネル 0.0 でクリア。
                    // A = bitcast<f32>(0u) = 0.0 が「背景」を意味する。
                    load:  wgpu::LoadOp::Clear(wgpu::Color { r: 0.0, g: 0.0, b: 0.0, a: 0.0 }),
                    store: wgpu::StoreOp::Store,
                },
            })],
            depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                view: self.depth_view,
                depth_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Discard,
                }),
                stencil_ops: Some(wgpu::Operations {
                    load:  wgpu::LoadOp::Load,
                    store: wgpu::StoreOp::Discard,
                }),
            }),
            occlusion_query_set: None,
            timestamp_writes:    None,
        })
    }

    /// 1 ピクセル分を `readback_buf` へコピーするコマンドを積む。
    ///
    /// `frame.finish()` → GPU サブミット後に `map_async` + `poll(Wait)` で読み出す。
    pub fn schedule_id_copy(
        &mut self,
        src_texture: &wgpu::Texture,
        x:           u32,
        y:           u32,
        readback_buf: &wgpu::Buffer,
    ) {
        self.encoder.copy_texture_to_buffer(
            wgpu::ImageCopyTexture {
                texture:   src_texture,
                mip_level: 0,
                origin:    wgpu::Origin3d { x, y, z: 0 },
                aspect:    wgpu::TextureAspect::All,
            },
            wgpu::ImageCopyBuffer {
                buffer: readback_buf,
                layout: wgpu::ImageDataLayout {
                    offset:         0,
                    bytes_per_row:  Some(COPY_ROW_ALIGNMENT),
                    rows_per_image: None,
                },
            },
            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
        );
    }

    /// コマンドを GPU にサブミットしてフレームを表示する。
    ///
    /// スクリーンショット機能が有効かつ現フレームが撮影対象なら、
    /// サブミット前にカラーターゲット → 読み戻しバッファのコピーを積み、
    /// present 後に PNG を書き出す（`screenshot` モジュール参照）。
    pub fn finish(mut self) {
        // 提示フレームの通し番号を払い出す（機能無効時は撮影判定で即 None になる）。
        let frame_index = screenshot::next_frame_index();
        let pending = screenshot::schedule(
            self.device,
            &mut self.encoder,
            &self.output.texture,
            frame_index,
        );

        self.queue.submit(std::iter::once(self.encoder.finish()));
        self.output.present();

        // present 後に読み出す。マップ完了待ちで同期するため、撮影フレームだけ重くなる。
        if let Some(pending) = pending {
            screenshot::resolve(self.device, pending);
        }
    }
}
