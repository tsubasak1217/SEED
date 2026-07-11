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
pub(crate) mod shadow;
pub(crate) mod rt_shadow;
pub(crate) mod post;
pub(crate) mod transparency;
pub(crate) mod batch2d;
/// GPU パーティクル シミュレーション＋描画（Phase RP）
pub(crate) mod particle_system;
/// .mat マテリアルアセット（Phase R7: マルチマテリアル編集）
pub mod material_asset;

pub use uniforms::{CameraUniform, ModelUniform, MaterialUniform, JointUniform, ColorVertex,
                   GpuCullData, FrustumUniform, GizmoVertex};
pub use gpu_resources::{GpuTexture, GpuMaterial, GpuPrimitive, GpuMesh, GpuModel,
                        InstancedModelBatch, NodePrimDraw, GpuLineBatch, GpuGizmoBatch,
                        DefaultTextures, CameraBuffer,
                        extract_frustum_planes, test_aabb_frustum, NUM_LODS};
pub use pipeline::{MeshPipeline, SkinnedMeshPipeline, UnlitPipeline, CullPipeline, DrawPipelines,
                   SkinComputePipeline, IdPassPipeline, OutlinePipeline, DepthPrepassPipelines,
                   SpritePipeline, SpriteOutlinePipeline, CanvasIdPipeline, CanvasIdUniform,
                   CameraPreviewBlitPipeline, ShadowDepthPipelines,
                   BarFillPipeline, BarFillUniform};
pub use particle_system::ParticleSystem;
pub use lighting::{GpuLight, LightBuffer, LightMeta, MAX_LIGHTS,
                   DEFAULT_AMBIENT_COLOR, DEFAULT_AMBIENT_INTENSITY};
pub use shadow::{ShadowResources, ShadowPlan, ShadowMatricesUbo,
                 CSM_CASCADE_COUNT, MAX_SHADOW_SPOTS, SHADOW_DEPTH_FORMAT};
pub use rt_shadow::RtShadowResources;
pub use post::{RtPool, PostContext, VignetteParams, VignetteStage,
               PostFxSettings, BloomParams, BloomPipelines,
               DEFAULT_BLOOM_THRESHOLD, DEFAULT_BLOOM_KNEE, DEFAULT_BLOOM_INTENSITY,
               RT_SCENE_HDR, RT_POST_INTER, RT_LDR};
pub use transparency::{TransparencyMode, TransparentPipelines,
                       RT_WBOIT_ACCUM, RT_WBOIT_REVEAL,
                       WBOIT_ACCUM_FORMAT, WBOIT_REVEAL_FORMAT};
pub use batch2d::{SpriteBatcher, SpriteInstance, SpriteBatch, SpriteBatchList,
                  SPRITE_INSTANCE_SIZE, draw_sprite_batches, draw_sprite_outline_batches};

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

        let (device, queue) = pollster::block_on(adapter.request_device(
            &wgpu::DeviceDescriptor {
                label:             None,
                required_features: wgpu::Features::MULTI_DRAW_INDIRECT
                                 | wgpu::Features::INDIRECT_FIRST_INSTANCE
                                 | if supports_bc { wgpu::Features::TEXTURE_COMPRESSION_BC } else { wgpu::Features::empty() }
                                 | if supports_pipeline_cache { wgpu::Features::PIPELINE_CACHE } else { wgpu::Features::empty() }
                                 // RT 影は「対応していれば常に要求」する。設定によるオン/オフは
                                 // シェーダバリアント（RT対応時は常に RT パイプライン）と実行時フラグ
                                 // （LightMeta.rt_shadows）で切り替えるため、features は起動時固定でよい。
                                 | if supports_rt { wgpu::Features::EXPERIMENTAL_RAY_QUERY | wgpu::Features::EXPERIMENTAL_RAY_TRACING_ACCELERATION_STRUCTURE } else { wgpu::Features::empty() },
                required_limits:   wgpu::Limits {
                    max_storage_buffers_per_shader_stage: 12,
                    max_bind_groups: 5,
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

        let config = wgpu::SurfaceConfiguration {
            usage:                        wgpu::TextureUsages::RENDER_ATTACHMENT,
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
}

impl<'r> RenderFrame<'r> {
    /// コマンドエンコーダへの可変参照を返す（Hi-Z compute pass 等に使用）。
    pub fn encoder_mut(&mut self) -> &mut wgpu::CommandEncoder { &mut self.encoder }

    /// 深度バッファビューへの参照を返す（Hi-Z ピラミッド生成用、DepthOnly aspect）。
    pub fn depth_view(&self) -> &wgpu::TextureView { self.depth_only_view }

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
    ) {
        post.run(device, &mut self.encoder, hdr_view, ldr_view, vignette);
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
    pub fn finish(self) {
        self.queue.submit(std::iter::once(self.encoder.finish()));
        self.output.present();
    }
}
