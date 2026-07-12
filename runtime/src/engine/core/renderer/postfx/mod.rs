// ============================================================
//  renderer/postfx — .postfx アセット＋テクスチャ単位ポストプロセス
//
//  Phase R3 のポスト土台（RtPool / post_pass 抽象）を応用し、個々の
//  テクスチャ（スプライト）に対してエフェクトチェーン（.postfx）を焼き込む。
//
//  構成:
//    - asset.rs : .postfx（JSON）のスキーマ・ロード・キャッシュ（material_asset 流儀）
//    - bake.rs  : ベーステクスチャ → エフェクトチェーン → 出力テクスチャの焼き込みと
//                 (texture_path, postfx_path, mtime) キャッシュ
//    - mod.rs   : PostfxContext（作業フォーマットのパイプライン一式）＋パラメータ UBO
//
//  【作業フォーマット】
//  チェーンは全てリニア HDR（Rgba16Float）で処理する。ベースの sRGB スプライト
//  テクスチャを最初に tint(白=恒等) で取り込む（sRGB→リニアへ自動デコード）と、
//  以降のエフェクト（ブラー=compute、vignette/tint=fragment）を全てリニア空間で
//  一貫して適用でき、blur の storage テクスチャ（rgba16float）とも整合する。
//  焼き上げた Rgba16Float テクスチャは元 sRGB テクスチャをサンプルした場合と
//  同じリニア値を返すため、スプライト描画は元テクスチャと同一挙動になる。
// ============================================================

mod asset;
mod bake;

pub use bake::{resolve_baked, SpritePostfxCache};
// asset モジュールの型・関数は bake から `super::asset::...` で参照する。
// default_postfx_json は雛形生成の正典として公開する（現状は C# 側が雛形を書き出すため
// crate 内未使用だが、schema の単一情報源として残す）。
#[allow(unused_imports)]
pub use asset::default_postfx_json;

use super::post::PostPipeline;

/// ポストエフェクトの作業／出力フォーマット（リニア HDR）。
/// スプライト用 tex_bgl（texture_2d<f32>）でサンプル可能で、blur の storage
/// テクスチャ（rgba16float）とも一致する。
pub const WORK_FORMAT: wgpu::TextureFormat = super::HDR_FORMAT;

// ============================================================
//  パラメータ UBO
// ============================================================

/// tint（乗算色）パラメータ（WGSL postfx_tint.wgsl TintParams と #[repr(C)] 一致）。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct TintParams {
    /// 乗算色 RGBA。
    pub color: [f32; 4],
}

/// blur（いもす法ボックス）パラメータ（WGSL postfx_blur.wgsl BlurParams と #[repr(C)] 一致）。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct BlurParams {
    /// ボックス半径（画素）。
    pub radius:     i32,
    /// 走査方向（1=水平 / 0=垂直）。
    pub horizontal: u32,
    /// 作業テクスチャ幅。
    pub width:      i32,
    /// 作業テクスチャ高さ。
    pub height:     i32,
}

// ============================================================
//  ブラー用コンピュートパイプライン
// ============================================================

/// いもす法ボックスブラーのコンピュートパイプライン一式。
pub struct BlurPipeline {
    pub pipeline: wgpu::ComputePipeline,
    /// group 0 レイアウト（binding 0=params UBO / 1=入力 texture / 2=出力 storage）。
    pub bgl:      wgpu::BindGroupLayout,
}

impl BlurPipeline {
    fn new(device: &wgpu::Device, cache: Option<&wgpu::PipelineCache>) -> Self {
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("Postfx Blur Shader"),
            source: wgpu::ShaderSource::Wgsl(include_str!("../shaders/postfx_blur.wgsl").into()),
        });
        let bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label:   Some("Postfx Blur BGL"),
            entries: &[
                // binding 0: params UBO
                wgpu::BindGroupLayoutEntry {
                    binding:    0,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Buffer {
                        ty:                 wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size:   None,
                    },
                    count: None,
                },
                // binding 1: 入力テクスチャ（textureLoad、サンプラー不要）
                wgpu::BindGroupLayoutEntry {
                    binding:    1,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::Texture {
                        sample_type:    wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled:   false,
                    },
                    count: None,
                },
                // binding 2: 出力 storage テクスチャ（write, rgba16float）
                wgpu::BindGroupLayoutEntry {
                    binding:    2,
                    visibility: wgpu::ShaderStages::COMPUTE,
                    ty: wgpu::BindingType::StorageTexture {
                        access:         wgpu::StorageTextureAccess::WriteOnly,
                        format:         WORK_FORMAT,
                        view_dimension: wgpu::TextureViewDimension::D2,
                    },
                    count: None,
                },
            ],
        });
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label:                Some("Postfx Blur Layout"),
            bind_group_layouts:   &[&bgl],
            push_constant_ranges: &[],
        });
        let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
            label:               Some("Postfx Blur Pipeline"),
            layout:              Some(&layout),
            module:              &shader,
            entry_point:         Some("blur_cs"),
            compilation_options: Default::default(),
            cache,
        });
        Self { pipeline, bgl }
    }
}

// ============================================================
//  シェーダソースリゾルバ（postfx 専用）
// ============================================================

/// postfx フラグメントパスの WGSL ソースを解決する。
fn resolve_postfx_shader(name: &str) -> &'static str {
    match name {
        "fullscreen.wgsl"    => include_str!("../shaders/fullscreen.wgsl"),
        "postfx_tint.wgsl"   => include_str!("../shaders/postfx_tint.wgsl"),
        "post_vignette.wgsl" => include_str!("../shaders/post_vignette.wgsl"),
        other => panic!("unknown postfx shader source: {other}"),
    }
}

// ============================================================
//  PostfxContext — 焼き込み用の静的リソース一式
// ============================================================

/// テクスチャ単位ポストプロセスの静的リソース（パイプライン・サンプラー・既定マスク）。
///
/// 作業フォーマットは常に `WORK_FORMAT`（Rgba16Float）。DrawContext が 1 個保持し、
/// スプライトの .postfx 焼き込み時に使い回す。
pub struct PostfxContext {
    /// tint パス（乗算色。白で恒等＝チェーン先頭の ingest 取り込みにも使う）。
    tint:     PostPipeline,
    /// vignette パス（周辺減光。post_vignette.wgsl を作業フォーマットで再構築）。
    vignette: PostPipeline,
    /// blur コンピュートパイプライン（いもす法ボックス 3 回）。
    blur:     BlurPipeline,
    /// 入力・マスク共通のリニア clamp サンプラー。
    sampler:  wgpu::Sampler,
    /// マスク未指定時の既定（白 1x1、全面適用）。
    white_view: wgpu::TextureView,
}

impl PostfxContext {
    /// postfx の静的リソースを生成する（DrawContext::new から 1 回）。
    pub fn new(
        device: &wgpu::Device,
        queue:  &wgpu::Queue,
        cache:  Option<&wgpu::PipelineCache>,
    ) -> Self {
        // tint / vignette は作業フォーマット（Rgba16Float）出力のフラグメントパス。
        let tint = PostPipeline::from_toml(
            device, include_str!("../pipelines/postfx_tint.toml"),
            WORK_FORMAT, cache, resolve_postfx_shader,
        );
        let vignette = PostPipeline::from_toml(
            device, include_str!("../pipelines/post_vignette.toml"),
            WORK_FORMAT, cache, resolve_postfx_shader,
        );
        let blur = BlurPipeline::new(device, cache);

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Postfx Sampler"),
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
            label:           Some("Postfx White Mask"),
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

        Self { tint, vignette, blur, sampler, white_view }
    }
}

// ============================================================
//  WGSL 静的検証（naga parse + validate）
// ============================================================
#[cfg(test)]
mod tests {
    /// postfx の全 WGSL（tint / vignette / blur）を naga で parse + validate する。
    #[test]
    fn postfx_shaders_parse_and_validate() {
        let fullscreen = include_str!("../shaders/fullscreen.wgsl");
        let tint       = include_str!("../shaders/postfx_tint.wgsl");
        let vignette   = include_str!("../shaders/post_vignette.wgsl");
        let blur       = include_str!("../shaders/postfx_blur.wgsl");

        // フラグメント（fullscreen 連結）と compute（単体）を検証する。
        let variants: [(&str, String); 3] = [
            ("postfx_tint",     [fullscreen, tint].join("\n")),
            ("postfx_vignette", [fullscreen, vignette].join("\n")),
            ("postfx_blur",     blur.to_string()),
        ];
        for (name, src) in variants {
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
