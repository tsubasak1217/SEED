// sprite_drawer.rs — 2D スプライトテクスチャ描画ユーティリティ
//
// スプライト 1 枚あたり:
//   - SpriteUniform バッファ（モデル行列 + カラー）
//   - SpriteUniform バインドグループ（group 1）
//   - テクスチャバインドグループ（group 2、キャッシュ or フォールバック）
// を準備し、ユニットクワッドで描画する。

use std::sync::Arc;
use crate::engine::components::CanvasTransform;
use crate::engine::core::renderer::pipeline::SpritePipeline;

// ============================================================
//  頂点・ユニフォーム型
// ============================================================

/// スプライト頂点（位置 + UV）。
/// 正規化ローカル座標 [0,1]×[0,1] のユニットクワッドに使用する。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct SpriteVertex {
    /// ローカル座標 (x, y)
    pub position: [f32; 2],
    /// UV 座標 (u, v)
    pub uv: [f32; 2],
}

/// GPU に送る SpriteUniform（80 bytes）。
///
/// WGSL mat4x4<f32> は列優先（column-major）でメモリに並ぶ。
/// model フィールドは [[col0], [col1], [col2], [col3]] として格納する。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct SpriteUniform {
    /// モデル行列（列優先 4×4 = 64 bytes）
    pub model: [[f32; 4]; 4],
    /// RGBA カラー乗算（16 bytes）
    pub color: [f32; 4],
}

// ============================================================
//  GpuSpriteTexture — アップロード済みテクスチャ
// ============================================================

/// GPU にアップロードされたスプライトテクスチャ。
/// テクスチャ本体・ビュー・バインドグループを一体で保持する。
pub struct GpuSpriteTexture {
    pub bind_group: wgpu::BindGroup,
    /// テクスチャビュー（キャンバス ID パス等の別パイプラインでアルファ参照に使用）
    pub view:     wgpu::TextureView,
    _texture:     wgpu::Texture,
}

/// テクスチャファイルを読み込んで GpuSpriteTexture を生成する。
///
/// 対応フォーマット: image クレートがサポートする形式（PNG, JPEG 等）。
/// ファイルが存在しない / 読み込み失敗の場合は None を返す。
/// 呼び出し元はフォールバックとして SpritePipeline::white_fallback_bg を使う。
pub fn load_sprite_texture(
    device:  &wgpu::Device,
    queue:   &wgpu::Queue,
    path:    &str,
    tex_bgl: &wgpu::BindGroupLayout,
    sampler: &wgpu::Sampler,
) -> Option<Arc<GpuSpriteTexture>> {
    // image::open() は拡張子でフォーマットを判定するが、
    // with_guessed_format() はファイル先頭のマジックバイトで判定するため
    // 拡張子と実際のフォーマットが一致しないファイルも正しく読める。
    let img = match image::io::Reader::open(path) {
        Ok(reader) => match reader.with_guessed_format() {
            Ok(guessed) => match guessed.decode() {
                Ok(img) => img,
                Err(e) => {
                    eprintln!("[SEED sprite] decode failed: path={:?} err={}", path, e);
                    return None;
                }
            },
            Err(e) => {
                eprintln!("[SEED sprite] format guess failed: path={:?} err={}", path, e);
                return None;
            }
        },
        Err(e) => {
            eprintln!("[SEED sprite] open failed: path={:?} err={}", path, e);
            return None;
        }
    };
    let rgba = img.to_rgba8();
    eprintln!("[SEED sprite] load ok: path={:?} size={}x{}", path, rgba.width(), rgba.height());
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

// ============================================================
//  GPU 行列計算
// ============================================================

/// CanvasTransform と幅・高さから GPU 用モデル行列を計算する。
///
/// `to_mat4_sized(w, h)` は行優先（row-major）で返す。
/// WGSL mat4x4 は列優先のため転置した形で構築する。
/// ユニットクワッド [0,1]×[0,1] を (w×h) のワールド矩形にマッピングする。
pub fn sprite_model_matrix(ct: &CanvasTransform, w: f32, h: f32) -> [[f32; 4]; 4] {
    let m = ct.to_mat4_sized(w, h);
    // WGSL mat4x4 メモリレイアウト: [[col0], [col1], [col2], [col3]]
    // col_i[j] = m[j][i]  （転置）
    // ユニットクワッドの局所座標 [0,1] に w/h スケールを吸収させる
    [
        [m[0][0] * w, m[1][0] * w, 0.0, 0.0],  // col 0 (x basis × w)
        [m[0][1] * h, m[1][1] * h, 0.0, 0.0],  // col 1 (y basis × h)
        [0.0,         0.0,         1.0, 0.0],  // col 2 (z)
        [m[0][3],     m[1][3],     0.0, 1.0],  // col 3 (translation)
    ]
}

// ============================================================
//  スプライト描画リソース（render pass 前に準備）
// ============================================================

/// スプライト 1 枚分の描画に必要な GPU リソース。
/// render pass の外で作成し、pass 中はこれを参照して draw する。
pub struct SpritePrepared {
    pub uniform_buf: wgpu::Buffer,
    pub uniform_bg:  wgpu::BindGroup,
    pub tex:         Option<Arc<GpuSpriteTexture>>,
}

/// スプライトの GPU リソースをまとめて準備する。
///
/// # 引数
/// - `items`: (CanvasTransform, 幅, 高さ, RGBA カラー, テクスチャ Arc or None) のリスト
/// - 戻り値は items と同じ順序の SpritePrepared の Vec
pub fn prepare_sprites(
    device:   &wgpu::Device,
    pipeline: &SpritePipeline,
    items:    &[(CanvasTransform, f32, f32, [f32; 4], Option<Arc<GpuSpriteTexture>>)],
) -> Vec<SpritePrepared> {
    use wgpu::util::DeviceExt;

    items.iter().map(|(ct, w, h, color, tex)| {
        let uniform = SpriteUniform {
            model: sprite_model_matrix(ct, *w, *h),
            color: *color,
        };
        let uniform_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("SpriteUniform Buf"),
            contents: bytemuck::bytes_of(&uniform),
            usage:    wgpu::BufferUsages::UNIFORM,
        });
        let uniform_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("SpriteUniform BG"),
            layout:  &pipeline.sprite_uniform_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding:  0,
                resource: uniform_buf.as_entire_binding(),
            }],
        });
        SpritePrepared { uniform_buf, uniform_bg, tex: tex.clone() }
    }).collect()
}

/// 計算済みモデル行列（GPU 列優先）からスプライト描画リソースを準備する。
///
/// `collect_sprite_items` でペアレント行列を合成済みの GPU 列優先行列を直接受け取る。
/// `prepare_sprites` がサイズ + CanvasTransform を受け取るのに対し、
/// こちらは合成済み行列をそのまま使うためペアレント伝播に対応している。
///
/// # 引数
/// - `items`: (GPU 列優先モデル行列, RGBA カラー, テクスチャ Arc or None) のリスト
pub fn prepare_sprites_from_mats(
    device:   &wgpu::Device,
    pipeline: &SpritePipeline,
    items:    &[([[f32; 4]; 4], [f32; 4], Option<Arc<GpuSpriteTexture>>)],
) -> Vec<SpritePrepared> {
    use wgpu::util::DeviceExt;

    items.iter().map(|(model_mat, color, tex)| {
        let uniform = SpriteUniform { model: *model_mat, color: *color };
        let uniform_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("SpriteUniform Buf"),
            contents: bytemuck::bytes_of(&uniform),
            usage:    wgpu::BufferUsages::UNIFORM,
        });
        let uniform_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("SpriteUniform BG"),
            layout:  &pipeline.sprite_uniform_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding:  0,
                resource: uniform_buf.as_entire_binding(),
            }],
        });
        SpritePrepared { uniform_buf, uniform_bg, tex: tex.clone() }
    }).collect()
}

/// 準備済みスプライトを render pass に描画する。
///
/// - camera_bg: group 0（mesh pipeline の camera_buf.bind_group と同レイアウト）
/// - prepared:  `prepare_sprites` の戻り値
pub fn draw_sprites<'rp>(
    pass:      &mut wgpu::RenderPass<'rp>,
    pipeline:  &'rp SpritePipeline,
    camera_bg: &'rp wgpu::BindGroup,
    prepared:  &'rp [SpritePrepared],
) {
    if prepared.is_empty() { return; }

    pass.set_pipeline(&pipeline.pipeline);
    pass.set_vertex_buffer(0, pipeline.unit_quad_vbuf.slice(..));
    pass.set_bind_group(0, camera_bg, &[]);

    for item in prepared {
        // group 1: SpriteUniform（モデル行列 + カラー）
        pass.set_bind_group(1, &item.uniform_bg, &[]);
        // group 2: テクスチャ（設定済みなら実テクスチャ、未設定なら白フォールバック）
        let tex_bg = item.tex.as_ref()
            .map(|t| &t.bind_group)
            .unwrap_or(&pipeline.white_fallback_bg);
        pass.set_bind_group(2, tex_bg, &[]);
        // ユニットクワッド 6 頂点（2 三角形）を描画
        pass.draw(0..6, 0..1);
    }
}
