// sprite_drawer.rs — 2D スプライトテクスチャの GPU アップロード
//
// Phase R6（汎用バッチング）で描画経路はインスタンシング化され、
// 実際の描画は core::renderer::batch2d（SpriteBatcher / draw_sprite_batches）が担う。
// 本ファイルはテクスチャのロード／GPU 常駐（キャッシュ対象）のみを提供する。
//
//   - SpriteVertex: ユニットクワッドの頂点レイアウト定義（ドキュメント用途）
//   - GpuSpriteTexture: アップロード済みテクスチャ＋テクスチャ BindGroup（テクスチャ単位で永続キャッシュ）
//   - load_sprite_texture: ファイル→GpuSpriteTexture

use std::sync::Arc;

// ============================================================
//  頂点型
// ============================================================

/// スプライト頂点（位置 + UV）。
/// 正規化ローカル座標 [0,1]×[0,1] のユニットクワッドに使用する。
/// （実バッファは SpritePipeline.unit_quad_vbuf が raw f32 で保持する。
///   この型はレイアウト（16 bytes / location 0,1）のドキュメントを兼ねる。）
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct SpriteVertex {
    /// ローカル座標 (x, y)
    pub position: [f32; 2],
    /// UV 座標 (u, v)
    pub uv: [f32; 2],
}

// ============================================================
//  GpuSpriteTexture — アップロード済みテクスチャ
// ============================================================

/// GPU にアップロードされたスプライトテクスチャ。
/// テクスチャ本体・ビュー・BindGroup を一体で保持する。
///
/// `bind_group` は SpritePipeline.tex_bgl（group 1: テクスチャ＋サンプラー）で生成され、
/// テクスチャ単位で永続キャッシュされる（DrawContext.sprite_tex_cache）。
/// バッチ描画時はテクスチャ境界でこの BindGroup を 1 度だけバインドする。
pub struct GpuSpriteTexture {
    pub bind_group: wgpu::BindGroup,
    /// テクスチャビュー（キャンバス ID パス等の別パイプラインでアルファ参照に使用）
    pub view:     wgpu::TextureView,
    _texture:     wgpu::Texture,
}

/// テクスチャファイルを読み込んで GpuSpriteTexture を生成する。
///
/// 対応フォーマット: image クレートがサポートする形式（PNG, JPEG 等）。
/// asset_fs::read_image を使うことで PAK（パッケージモード）にも対応する。
/// 失敗時は 1×1 マゼンタ画像が返るため None にはならない
/// （デバッグ用の eprintln は asset_fs 側で出力される）。
pub fn load_sprite_texture(
    device:  &wgpu::Device,
    queue:   &wgpu::Queue,
    path:    &str,
    tex_bgl: &wgpu::BindGroupLayout,
    sampler: &wgpu::Sampler,
) -> Option<Arc<GpuSpriteTexture>> {
    let rgba = crate::engine::asset_fs::read_image(path);
    let (w, h) = rgba.dimensions();

    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label:           Some("SpriteTexture"),
        size:            wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
        mip_level_count: 1,
        sample_count:    1,
        dimension:       wgpu::TextureDimension::D2,
        format:          wgpu::TextureFormat::Rgba8UnormSrgb,
        usage:           wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
        view_formats:    &[],
    });
    queue.write_texture(
        texture.as_image_copy(),
        &rgba,
        wgpu::ImageDataLayout {
            offset:         0,
            bytes_per_row:  Some(4 * w),
            rows_per_image: Some(h),
        },
        wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
    );
    let view = texture.create_view(&Default::default());
    let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
        label:   Some("SpriteTexture BG"),
        layout:  tex_bgl,
        entries: &[
            wgpu::BindGroupEntry {
                binding:  0,
                resource: wgpu::BindingResource::TextureView(&view),
            },
            wgpu::BindGroupEntry {
                binding:  1,
                resource: wgpu::BindingResource::Sampler(sampler),
            },
        ],
    });
    Some(Arc::new(GpuSpriteTexture { bind_group, view, _texture: texture }))
}
