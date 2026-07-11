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

pub use rt_pool::RtPool;
pub use post_pass::{PostPipeline, run_post_stage};

// ─── RtPool のターゲット名（フレーム間で安定させる）──────────────
/// シーン描画先の HDR オフスクリーン（Rgba16Float）。
pub const RT_SCENE_HDR: &str = "scene_hdr";
/// ビネット等ポスト中間の HDR バッファ（トーンマップ前段の出力）。
pub const RT_POST_INTER: &str = "post_inter";

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
    /// トーンマップパス（HDR → スワップチェーン）。出力はスワップチェーンフォーマット。
    tonemap:  PostPipeline,
    /// ビネットパス（サンプル）。出力は HDR 中間（トーンマップ前段に挿す）。
    vignette: PostPipeline,
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
        // トーンマップ: 出力はスワップチェーン。ビネット: 出力は HDR 中間。
        let tonemap = PostPipeline::from_toml(
            device, include_str!("../pipelines/post_tonemap.toml"), surface_format, cache, resolve_post_shader,
        );
        let vignette = PostPipeline::from_toml(
            device, include_str!("../pipelines/post_vignette.toml"), hdr_format, cache, resolve_post_shader,
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

        Self { tonemap, vignette, sampler, white_view, hdr_format }
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

        let variants: [(&str, Vec<&str>); 2] = [
            ("post_tonemap",  vec![fullscreen, tm_ops, tonemap]),
            ("post_vignette", vec![fullscreen, vignette]),
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
