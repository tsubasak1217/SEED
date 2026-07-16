// ============================================================
//  renderer/post — HDR ポストプロセス土台（Phase R3）
//
//  シーンを HDR オフスクリーン（Rgba16Float）へ描画し、フルスクリーンの
//  トーンマップパスでスワップチェーンへ出力する。各メッシュシェーダ内の
//  Reinhard を撤去してここへ一元化した。
//
//  構成:
//    - RtPool        : 名前付きレンダーターゲットの確保・再利用（rt_pool.rs）
//    - PostPipeline  : TOML+WGSL からのポストパスパイプライン（post_pass.rs）
//    - run_post_stage: フルスクリーン三角形での 1 パス実行（post_pass.rs）
//    - PostContext   : ポストの静的リソース一式（本ファイル）
//
//  ポストパスのチェーン実行（前段出力→次段入力）は `PostContext::run` が
//  「ビネット（任意, HDR 中間へ）→ トーンマップ（スワップチェーンへ）」として
//  最小実装する。R4 のブルーム/FXAA はこの上に stage を積む前提。
// ============================================================

mod rt_pool;
mod post_pass;
mod bloom;

pub use rt_pool::RtPool;
pub use post_pass::{PostPipeline, run_post_stage};
pub use bloom::{BloomPipelines, BloomParams};
// run_post_stage_load / MAX_BLOOM_MIPS は post 配下（bloom.rs）でのみ使うため再公開しない。
// PostFxSettings とそのデフォルト定数は本ファイル下部で定義・公開する。

// ─── RtPool のターゲット名（フレーム間で安定させる）──────────────
/// シーン描画先の HDR オフスクリーン（Rgba16Float）。
pub const RT_SCENE_HDR: &str = "scene_hdr";
/// ビネット等ポスト中間の HDR バッファ（トーンマップ前段の出力）。
pub const RT_POST_INTER: &str = "post_inter";
/// トーンマップ後の LDR 中間バッファ（Phase R4）。
///
/// トーンマップ済みの表示リニア色（[0,1] 近傍）を保持する Rgba16Float RT。
/// 2D UI オーバーレイをこの上へ「トーンマップを通さず」直描きし（R3 の暗化課題を解消）、
/// 最終段の FXAA／プレゼントコピーでスワップチェーンへ書き出す。物理フォーマットは
/// HDR と同じ Rgba16Float にすることで、オーバーレイ用パイプライン（HDR フォーマットで
/// 構築済み）を一切変更せずそのまま描ける。
pub const RT_LDR: &str = "post_ldr";

// ============================================================
//  GiSettings - DDGI（プローブ格子レイトレGI）のランタイム設定
// ============================================================

/// GI 強度の既定（アンビエント項に対する間接光の倍率）。
pub const DEFAULT_GI_INTENSITY: f32 = 1.0;
/// 1 フレームで更新するプローブ数の既定（ローテーション）。
pub const DEFAULT_GI_PROBES_PER_FRAME: u32 = 256;
/// 1 プローブあたりのレイ本数の既定。
pub const DEFAULT_GI_RAYS_PER_PROBE: u32 = 64;
/// ヒステリシス（時間的蓄積で前フレームを保持する割合）の既定。
pub const DEFAULT_GI_HYSTERESIS: f32 = 0.97;
/// 多重バウンス再帰項の重みの既定。
pub const DEFAULT_GI_RECURSIVE_WEIGHT: f32 = 0.5;

/// DDGI の**数値**設定（SET_POST_FX に相乗り。欠落キーは既定値）。
///
/// GI の有効/無効は `RenderFeatures.gi`（GiMode）へ移行済み。本構造体は強度・
/// プローブ数などの数値パラメータのみを保持する。
#[derive(Copy, Clone, Debug)]
pub struct GiSettings {
    /// 間接光の強度倍率。
    pub intensity: f32,
    /// 1 フレームで更新するプローブ数（ローテーション）。
    pub probes_per_frame: u32,
    /// 1 プローブあたりのレイ本数。
    pub rays_per_probe: u32,
    /// 時間的蓄積のヒステリシス（0..1、大きいほど滑らか・遅い）。
    pub hysteresis: f32,
    /// 多重バウンス再帰項の重み。
    pub recursive_weight: f32,
}

impl Default for GiSettings {
    fn default() -> Self {
        Self {
            intensity:        DEFAULT_GI_INTENSITY,
            probes_per_frame: DEFAULT_GI_PROBES_PER_FRAME,
            rays_per_probe:   DEFAULT_GI_RAYS_PER_PROBE,
            hysteresis:       DEFAULT_GI_HYSTERESIS,
            recursive_weight: DEFAULT_GI_RECURSIVE_WEIGHT,
        }
    }
}

// ============================================================
//  PostFxSettings — ブルーム／FXAA のランタイム設定（Phase R4）
// ============================================================

/// ポストエフェクト設定（renderer 側に集約）。
///
/// project_settings.json の起動時読み込み（読み側 unwrap_or でデフォルト）と、
/// IPC `SET_POST_FX:{json}` による実行時変更の両方から更新される。
/// デフォルトはブルーム／FXAA ともに OFF（見た目の後方互換を維持）。
#[derive(Copy, Clone, Debug)]
pub struct PostFxSettings {
    /// ブルーム有効フラグ。
    pub bloom_enabled:   bool,
    /// ブルーム抽出しきい値。
    pub bloom_threshold: f32,
    /// ブルームのソフトニー幅係数（0..1）。
    pub bloom_knee:      f32,
    /// ブルーム合成強度。
    pub bloom_intensity: f32,
    /// FXAA 有効フラグ（最終 LDR 段）。
    pub fxaa_enabled:    bool,
    /// 透明描画の方式（距離ソート / WBOIT）。既定は距離ソート（Phase R5）。
    pub transparency:    super::transparency::TransparencyMode,
    /// GPU メッシュレットカリング有効フラグ（第1弾）。既定 true。
    /// OFF で完全に従来経路（CPU カリング＋draw_indexed）＝A/B パリティ検証用。
    /// MULTI_DRAW_INDIRECT_COUNT 非対応 GPU では本フラグに関わらず従来経路。
    pub meshlet_cull:    bool,
    /// Deferred（G-Buffer）レンダリング有効フラグ（Phase D3 Deferred Phase B）。既定 true。
    /// OFF で完全に従来のフォワード経路（不透明を direct にシーン HDR へ描く）にフォールバックする
    /// （A/B パリティ検証用）。デファードはメインカメラの不透明・Lit のみが対象で、
    /// unlit／ワイヤーフレーム／2D シーンビュー等は本フラグに関わらず常にフォワードで描く
    /// （frame_renderer.rs の deferred_active 判定を参照）。
    pub deferred:        bool,
    /// DDGI（レイトレGI）設定（Phase RT-GI）。相乗りで SET_POST_FX から更新される。
    pub gi:              GiSettings,
}

// ─── デフォルト値（マジックナンバー回避）──────────────────────
/// ブルーム抽出しきい値の既定（この輝度以上をブルーム対象にする）。
pub const DEFAULT_BLOOM_THRESHOLD: f32 = 1.0;
/// ブルームのソフトニー幅係数の既定。
pub const DEFAULT_BLOOM_KNEE:      f32 = 0.5;
/// ブルーム合成強度の既定。
pub const DEFAULT_BLOOM_INTENSITY: f32 = 0.6;

impl Default for PostFxSettings {
    fn default() -> Self {
        Self {
            bloom_enabled:   false,
            bloom_threshold: DEFAULT_BLOOM_THRESHOLD,
            bloom_knee:      DEFAULT_BLOOM_KNEE,
            bloom_intensity: DEFAULT_BLOOM_INTENSITY,
            fxaa_enabled:    false,
            transparency:    super::transparency::TransparencyMode::DistanceSort,
            meshlet_cull:    true,
            deferred:        true,
            gi:              GiSettings::default(),
        }
    }
}

/// FXAA パラメータ UBO（post_fxaa.wgsl FxaaParams と #[repr(C)] 一致）。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
struct FxaaParams {
    /// 1 テクセルサイズ（1/幅, 1/高さ）。
    inv_res: [f32; 2],
    /// FXAA 有効フラグ（0=単純コピー, 1=FXAA）。
    enabled: u32,
    _pad:    u32,
}

// ============================================================
//  トーンマップ / ビネット パラメータ
// ============================================================

/// トーンマップ演算子（WGSL tonemap_ops.wgsl の TONEMAP_* と一致させること）。
#[repr(u32)]
#[derive(Clone, Copy, Debug)]
pub enum TonemapOperator {
    /// 輝度ベース Reinhard（現行の見た目を維持する既定）。
    ReinhardLuma = 0,
}

/// トーンマップパラメータ UBO（WGSL TonemapParams と #[repr(C)] 一致）。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct TonemapParams {
    /// 演算子 ID（TonemapOperator）
    pub operator: u32,
    /// 露出倍率（トーンマップ前に乗算。既定 1.0）
    pub exposure: f32,
    pub _pad0:    u32,
    pub _pad1:    u32,
}

impl Default for TonemapParams {
    fn default() -> Self {
        Self { operator: TonemapOperator::ReinhardLuma as u32, exposure: 1.0, _pad0: 0, _pad1: 0 }
    }
}

/// ビネットパラメータ UBO（WGSL VignetteParams と #[repr(C)] 一致）。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct VignetteParams {
    /// 効果の強さ（0=無効, 1=標準）
    pub intensity: f32,
    /// 効果の開始半径（正規化距離 0..1）
    pub radius:    f32,
    /// 減衰の柔らかさ
    pub softness:  f32,
    pub _pad:      f32,
}

impl Default for VignetteParams {
    fn default() -> Self {
        // 既定の見た目（強度は呼び出し側で決定。ここは形状パラメータの妥当な初期値）。
        Self { intensity: 0.0, radius: 0.5, softness: 0.5, _pad: 0.0 }
    }
}

// ─── ビネットステージ指定（チェーンにビネットを挿す場合の入力）────
/// ビネットをトーンマップ前段に挿すためのステージ指定。
pub struct VignetteStage<'a> {
    /// ビネット出力先の HDR 中間ターゲット（RtPool から確保）。トーンマップの入力になる。
    pub inter_view: &'a wgpu::TextureView,
    /// ビネットパラメータ。
    pub params: VignetteParams,
    /// 任意のマスク（None なら全面適用の白 1x1）。
    pub mask: Option<&'a wgpu::TextureView>,
}

// ============================================================
//  シェーダソースリゾルバ（ポスト専用・自己完結）
// ============================================================

/// ポストパスの WGSL ソースを解決する。post 配下で自己完結させる。
fn resolve_post_shader(name: &str) -> &'static str {
    match name {
        "fullscreen.wgsl"    => include_str!("../shaders/fullscreen.wgsl"),
        "tonemap_ops.wgsl"   => include_str!("../shaders/tonemap_ops.wgsl"),
        "post_tonemap.wgsl"  => include_str!("../shaders/post_tonemap.wgsl"),
        "post_vignette.wgsl" => include_str!("../shaders/post_vignette.wgsl"),
        "post_bloom_prefilter.wgsl" => include_str!("../shaders/post_bloom_prefilter.wgsl"),
        "post_bloom_down.wgsl"      => include_str!("../shaders/post_bloom_down.wgsl"),
        "post_bloom_up.wgsl"        => include_str!("../shaders/post_bloom_up.wgsl"),
        "post_fxaa.wgsl"            => include_str!("../shaders/post_fxaa.wgsl"),
        other => panic!("unknown post shader source: {other}"),
    }
}

// ============================================================
//  PostContext — ポストの静的リソース一式
// ============================================================

/// ポストプロセスの静的リソース（パイプライン・共有サンプラー・既定マスク）。
///
/// 動的なレンダーターゲット（サイズ追従が要る HDR バッファ群）は `RtPool` が持つ。
/// PostContext は起動時に 1 回生成し、毎フレーム `run` で使い回す。
pub struct PostContext {
    /// トーンマップパス（HDR → LDR 中間）。出力は LDR 中間 RT（Rgba16Float, Phase R4）。
    tonemap:  PostPipeline,
    /// ビネットパス（サンプル）。出力は HDR 中間（トーンマップ前段に挿す）。
    vignette: PostPipeline,
    /// ブルームパイプライン一式（プレフィルタ／ダウン／アップ, Phase R4）。出力は HDR。
    bloom:    BloomPipelines,
    /// FXAA／プレゼントコピー最終段（LDR 中間 → スワップチェーン, Phase R4）。
    fxaa:     PostPipeline,
    /// 入力・マスク共通のリニア clamp サンプラー。
    sampler:  wgpu::Sampler,
    /// マスク未指定時の既定（白 1x1、全面適用）。
    white_view: wgpu::TextureView,
    /// シーン HDR / ポスト中間のフォーマット（RtPool 確保時に使う）。
    pub hdr_format: wgpu::TextureFormat,
}

impl PostContext {
    /// ポストの静的リソースを生成する。
    ///
    /// - `hdr_format`     : シーン HDR / ポスト中間バッファのフォーマット（Rgba16Float 想定）
    /// - `surface_format` : スワップチェーンフォーマット（トーンマップの出力先）
    pub fn new(
        device:         &wgpu::Device,
        queue:          &wgpu::Queue,
        hdr_format:     wgpu::TextureFormat,
        surface_format: wgpu::TextureFormat,
        cache:          Option<&wgpu::PipelineCache>,
    ) -> Self {
        // トーンマップ: 出力は LDR 中間 RT（Rgba16Float = hdr_format, Phase R4）。
        //   R3 ではスワップチェーンへ直接出していたが、R4 で 2D オーバーレイをトーンマップ後の
        //   LDR へ描くため、いったん LDR 中間へ出す構成に変えた。
        let tonemap = PostPipeline::from_toml(
            device, include_str!("../pipelines/post_tonemap.toml"), hdr_format, cache, resolve_post_shader,
        );
        let vignette = PostPipeline::from_toml(
            device, include_str!("../pipelines/post_vignette.toml"), hdr_format, cache, resolve_post_shader,
        );
        // ブルームは全パス HDR 出力。FXAA／プレゼントはスワップチェーンへ出す最終段。
        let bloom = BloomPipelines::new(device, hdr_format, cache, resolve_post_shader);
        let fxaa  = PostPipeline::from_toml(
            device, include_str!("../pipelines/post_fxaa.toml"), surface_format, cache, resolve_post_shader,
        );

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Post Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Linear,
            min_filter:     wgpu::FilterMode::Linear,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        // 既定マスク（白 1x1, R=1 = 全面適用）。
        let white_tex = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Post White Mask"),
            size:            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          wgpu::TextureFormat::Rgba8Unorm,
            usage:           wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats:    &[],
        });
        queue.write_texture(
            white_tex.as_image_copy(),
            &[255u8, 255, 255, 255],
            wgpu::ImageDataLayout { offset: 0, bytes_per_row: Some(4), rows_per_image: Some(1) },
            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
        );
        let white_view = white_tex.create_view(&wgpu::TextureViewDescriptor::default());

        Self { tonemap, vignette, bloom, fxaa, sampler, white_view, hdr_format }
    }

    /// ブルーム一式を記録し、シーン HDR へ加算合成する（Phase R4）。
    ///
    /// `targets` は `BloomPipelines::ensure_targets` で確保済みの mip 名＋サイズ。
    /// `rt_pool` は `targets` と `scene_hdr` を含む確保済みプール。
    pub fn run_bloom(
        &self,
        device:    &wgpu::Device,
        encoder:   &mut wgpu::CommandEncoder,
        rt_pool:   &RtPool,
        targets:   &[(&'static str, u32, u32)],
        scene_hdr: &wgpu::TextureView,
        params:    BloomParams,
    ) {
        self.bloom.record(
            device, encoder, rt_pool, targets, scene_hdr,
            &self.sampler, &self.white_view, params,
        );
    }

    /// LDR 中間をスワップチェーンへ書き出す最終段（FXAA or 単純コピー, Phase R4）。
    ///
    /// `fxaa_enabled` が真なら FXAA、偽なら中央 1 タップのコピー。いずれもこの 1 パスが
    /// トーンマップ後 LDR → スワップチェーンの橋渡しを担う（常に実行）。
    #[allow(clippy::too_many_arguments)]
    pub fn present(
        &self,
        device:       &wgpu::Device,
        encoder:      &mut wgpu::CommandEncoder,
        ldr_view:     &wgpu::TextureView,
        swapchain:    &wgpu::TextureView,
        width:        u32,
        height:       u32,
        fxaa_enabled: bool,
    ) {
        let p = FxaaParams {
            inv_res: [1.0 / width.max(1) as f32, 1.0 / height.max(1) as f32],
            enabled: if fxaa_enabled { 1 } else { 0 },
            _pad:    0,
        };
        run_post_stage(
            device, encoder, &self.fxaa,
            ldr_view, None, &self.white_view, &self.sampler,
            bytemuck::bytes_of(&p), swapchain, "Post FXAA/Present",
        );
    }

    /// シーン HDR をトーンマップ（＋任意でビネット）して最終ターゲットへ出力する。
    ///
    /// チェーン:
    ///   - `vignette` = Some: hdr_view →(ビネット)→ inter_view(HDR) →(トーンマップ)→ final_view
    ///   - `vignette` = None : hdr_view →(トーンマップ)→ final_view
    ///
    /// `final_view` は通常スワップチェーンの sRGB ビュー（トーンマップ出力のリニア色を
    /// GPU が自動で sRGB エンコードする）。
    pub fn run(
        &self,
        device:     &wgpu::Device,
        encoder:    &mut wgpu::CommandEncoder,
        hdr_view:   &wgpu::TextureView,
        final_view: &wgpu::TextureView,
        vignette:   Option<VignetteStage<'_>>,
    ) {
        // ── 前段: ビネット（任意）。hdr → HDR 中間 ─────────────
        let tonemap_input: &wgpu::TextureView = if let Some(v) = &vignette {
            run_post_stage(
                device, encoder, &self.vignette,
                hdr_view, v.mask, &self.white_view, &self.sampler,
                bytemuck::bytes_of(&v.params), v.inter_view, "Post Vignette",
            );
            v.inter_view
        } else {
            hdr_view
        };

        // ── 最終段: トーンマップ。入力 → 最終ターゲット ─────────
        let tm = TonemapParams::default();
        run_post_stage(
            device, encoder, &self.tonemap,
            tonemap_input, None, &self.white_view, &self.sampler,
            bytemuck::bytes_of(&tm), final_view, "Post Tonemap",
        );
    }
}

// ============================================================
//  WGSL 静的検証（naga parse + validate）
// ============================================================
//
// 新規ポストシェーダ（トーンマップ・ビネット）を cargo test で parse+validate する。
// 連結順は pipelines/post_*.toml の shader_sources と一致させること。
#[cfg(test)]
mod tests {
    /// トーンマップ・ビネットの連結 WGSL を naga で parse + validate する。
    #[test]
    fn post_shaders_parse_and_validate() {
        let fullscreen = include_str!("../shaders/fullscreen.wgsl");
        let tm_ops     = include_str!("../shaders/tonemap_ops.wgsl");
        let tonemap    = include_str!("../shaders/post_tonemap.wgsl");
        let vignette   = include_str!("../shaders/post_vignette.wgsl");
        // Phase R4 追加分。
        let bloom_pf   = include_str!("../shaders/post_bloom_prefilter.wgsl");
        let bloom_dn   = include_str!("../shaders/post_bloom_down.wgsl");
        let bloom_up   = include_str!("../shaders/post_bloom_up.wgsl");
        let fxaa       = include_str!("../shaders/post_fxaa.wgsl");

        let variants: [(&str, Vec<&str>); 6] = [
            ("post_tonemap",          vec![fullscreen, tm_ops, tonemap]),
            ("post_vignette",         vec![fullscreen, vignette]),
            ("post_bloom_prefilter",  vec![fullscreen, bloom_pf]),
            ("post_bloom_down",       vec![fullscreen, bloom_dn]),
            ("post_bloom_up",         vec![fullscreen, bloom_up]),
            ("post_fxaa",             vec![fullscreen, fxaa]),
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
}
