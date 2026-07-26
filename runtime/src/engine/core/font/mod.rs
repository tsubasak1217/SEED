// ============================================================
//  font/mod.rs — フォントシステム統合
//
//  使い方:
//    let font_sys = FontSystem::new(&device, surface_format, FontConfig::default());
//    // 毎フレーム:
//    let mut batch = TextBatch::new();
//    batch.add_text_screen("Hello", 0.0, 0.0, 24.0, [1.0,1.0,1.0,1.0]);
//    let gpu = font_sys.build_batch(&mut batch, &device, &queue, screen_w, screen_h);
//    font_sys.draw_text_batch(&gpu, &mut render_pass);
// ============================================================

pub mod atlas;
pub mod axis_gizmo;
pub mod icon_overlay;
pub mod pipeline;
pub mod rasterizer;

use ab_glyph::{FontArc, InvalidFont};
use wgpu::util::DeviceExt;

use atlas::{GlyphAtlas, GlyphInfo, GlyphKey};
use pipeline::{TextPipeline, TextVertex};
use rasterizer::{downsample, generate_sdf, rasterize_glyph_bitmap};

// デフォルトフォント（バイナリ埋め込み）
static DEFAULT_FONT_BYTES: &[u8] = include_bytes!(
    "../../engine_resources/fonts/M_PLUS_Rounded_1c/MPLUSRounded1c-Regular.ttf"
);

// ── FontMode ──────────────────────────────────────────────────

/// グリフのラスタライズ方式。Bitmap は通常の輝度ビットマップ、Sdf は距離場（拡大縮小に強い）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FontMode {
    Bitmap,
    Sdf,
}

impl Default for FontMode {
    fn default() -> Self { FontMode::Bitmap }
}

// ── FontConfig ────────────────────────────────────────────────

/// `FontSystem` の初期化パラメータ（描画モード・SDF設定・アトラスサイズ）。
#[derive(Clone, Debug)]
pub struct FontConfig {
    pub mode:           FontMode,
    /// SDF 生成時のオーバーサンプル倍率（デフォルト 2）
    pub sdf_oversample: u32,
    /// グリフアトラスの一辺ピクセル数（デフォルト 2048）
    pub atlas_size:     u32,
    /// SDF のスプレッド半径（高解像度空間でのピクセル数）
    pub sdf_spread:     u32,
}

impl Default for FontConfig {
    fn default() -> Self {
        Self {
            mode:           FontMode::Bitmap,
            sdf_oversample: 2,
            atlas_size:     2048,
            sdf_spread:     8,
        }
    }
}

// ── TextBatch ─────────────────────────────────────────────────

/// CPU 側のテキスト描画バッチ。
pub struct TextBatch {
    vertices: Vec<TextVertex>,
    indices:  Vec<u32>,
}

impl TextBatch {
    pub fn new() -> Self {
        Self { vertices: Vec::new(), indices: Vec::new() }
    }

    pub fn is_empty(&self) -> bool { self.vertices.is_empty() }

    /// スクリーン座標（ピクセル）でテキストを追加する。
    ///
    /// `x`, `y` はペン基点（スクリーン左上原点、Y 下向き）。
    pub fn add_text_screen(
        &mut self,
        text:      &str,
        mut pen_x: f32,
        pen_y:     f32,
        font_size: f32,
        color:     [f32; 4],
        glyphs:    &[(char, GlyphInfo)],
        sw:        f32,
        sh:        f32,
    ) {
        // スクリーン座標 → NDC 変換ヘルパー
        let to_ndc_x = |px: f32| px / sw * 2.0 - 1.0;
        let to_ndc_y = |py: f32| 1.0 - py / sh * 2.0;  // Y 反転

        for (ch, info) in glyphs {
            // bearing[0] = left, bearing[1] = top (スクリーン座標系 bounds.min)
            let x0 = pen_x + info.bearing[0];
            let y0 = pen_y + info.bearing[1];
            let x1 = x0 + info.size[0];
            let y1 = y0 + info.size[1];

            let nx0 = to_ndc_x(x0);
            let nx1 = to_ndc_x(x1);
            let ny0 = to_ndc_y(y0);
            let ny1 = to_ndc_y(y1);

            let base = self.vertices.len() as u32;
            self.vertices.extend_from_slice(&[
                TextVertex { position: [nx0, ny0, 0.0], uv: [info.uv_min[0], info.uv_min[1]], color },
                TextVertex { position: [nx1, ny0, 0.0], uv: [info.uv_max[0], info.uv_min[1]], color },
                TextVertex { position: [nx1, ny1, 0.0], uv: [info.uv_max[0], info.uv_max[1]], color },
                TextVertex { position: [nx0, ny1, 0.0], uv: [info.uv_min[0], info.uv_max[1]], color },
            ]);
            self.indices.extend_from_slice(&[
                base, base+1, base+2,
                base, base+2, base+3,
            ]);

            pen_x += info.advance;
        }
    }
}

// ── GpuTextBatch ─────────────────────────────────────────────

/// GPU にアップロード済みのテキストバッチ。
pub struct GpuTextBatch {
    pub vertex_buf:  wgpu::Buffer,
    pub index_buf:   wgpu::Buffer,
    pub index_count: u32,
}

// ── FontSystem ────────────────────────────────────────────────

/// フォント描画システムの本体。フォントデータ・グリフアトラス・描画パイプラインを保持し、
/// `prepare_glyphs` でグリフを準備、`build_gpu_batch`/`draw_text_batch` でテキストを描画する。
pub struct FontSystem {
    pub font:    FontArc,
    pub config:  FontConfig,
    pub atlas:   GlyphAtlas,
    pipeline:    TextPipeline,
    params_buf:  wgpu::Buffer,
    params_bg:   wgpu::BindGroup,
    atlas_bg:    wgpu::BindGroup,
}

impl FontSystem {
    /// デフォルトフォント（M PLUS Rounded 1c Regular）で初期化する。
    pub fn new(
        device:         &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
        config:         FontConfig,
    ) -> Result<Self, InvalidFont> {
        Self::new_with_bytes(device, surface_format, depth_format, config, DEFAULT_FONT_BYTES)
    }

    /// 任意のフォントバイト列で初期化する。
    pub fn new_with_bytes(
        device:         &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format:   wgpu::TextureFormat,
        config:         FontConfig,
        font_bytes:     &'static [u8],
    ) -> Result<Self, InvalidFont> {
        let font     = FontArc::try_from_slice(font_bytes)?;
        let atlas    = GlyphAtlas::new(device, config.atlas_size);
        let pipeline = TextPipeline::new(device, surface_format, depth_format);

        let sdf_mode: u32 = if config.mode == FontMode::Sdf { 1 } else { 0 };
        let params_data: [u32; 4] = [sdf_mode, 0, 0, 0];
        let params_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Text Params Buffer"),
            contents: bytemuck::cast_slice(&params_data),
            usage:    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });

        let params_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Text Params BG"),
            layout:  &pipeline.params_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding:  0,
                resource: params_buf.as_entire_binding(),
            }],
        });

        let atlas_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Text Atlas BG"),
            layout:  &pipeline.atlas_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding:  0,
                    resource: wgpu::BindingResource::TextureView(&atlas.texture_view),
                },
                wgpu::BindGroupEntry {
                    binding:  1,
                    resource: wgpu::BindingResource::Sampler(&pipeline.sampler),
                },
            ],
        });

        Ok(Self { font, config, atlas, pipeline, params_buf, params_bg, atlas_bg })
    }

    /// グリフを取得またはラスタライズしてアトラスに追加する。
    ///
    /// 返り値: (char, GlyphInfo) ペアのリスト（スペース等アウトラインなしは除外）。
    pub fn prepare_glyphs(
        &mut self,
        text:      &str,
        font_size: f32,
    ) -> Vec<(char, GlyphInfo)> {
        let key_size = font_size as u32;
        let mut result = Vec::new();

        for ch in text.chars() {
            let key = GlyphKey { codepoint: ch, font_size_px: key_size };

            if let Some(info) = self.atlas.get(&key) {
                result.push((ch, *info));
                continue;
            }

            // ラスタライズ
            let raster_size = if self.config.mode == FontMode::Sdf {
                font_size * self.config.sdf_oversample as f32
            } else {
                font_size
            };

            let Some((bitmap, bw, bh, bearing, advance)) =
                rasterize_glyph_bitmap(&self.font, ch, raster_size)
            else {
                // アウトラインなし（スペース等）→ advance だけ使いたいが GlyphInfo は作れない
                continue;
            };

            let (final_bitmap, final_w, final_h, final_bearing, final_advance) =
                if self.config.mode == FontMode::Sdf {
                    let sdf = generate_sdf(&bitmap, bw, bh, self.config.sdf_spread);
                    let factor = self.config.sdf_oversample;
                    let (ds, dw, dh) = downsample(&sdf, bw, bh, factor);
                    let inv = factor as f32;
                    let scaled_bearing = [bearing[0] / inv, bearing[1] / inv];
                    let scaled_advance = advance / inv;
                    (ds, dw, dh, scaled_bearing, scaled_advance)
                } else {
                    (bitmap, bw, bh, bearing, advance)
                };

            if let Some(info) = self.atlas.insert(
                key.clone(),
                &final_bitmap,
                final_w,
                final_h,
                final_bearing,
                final_advance,
            ) {
                result.push((ch, info));
            }
        }

        result
    }

    /// アトラスを GPU にアップロードする（毎フレーム呼ぶ）。
    pub fn flush(&mut self, queue: &wgpu::Queue) {
        self.atlas.upload_if_dirty(queue);
    }

    /// atlas_bg を再構築する（アトラステクスチャ変更後に呼ぶ必要がある場合）。
    ///
    /// 現在の実装ではアトラスはリサイズしないため通常不要。
    pub fn rebuild_atlas_bg(&mut self, device: &wgpu::Device) {
        self.atlas_bg = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Text Atlas BG"),
            layout:  &self.pipeline.atlas_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding:  0,
                    resource: wgpu::BindingResource::TextureView(&self.atlas.texture_view),
                },
                wgpu::BindGroupEntry {
                    binding:  1,
                    resource: wgpu::BindingResource::Sampler(&self.pipeline.sampler),
                },
            ],
        });
    }

    /// TextBatch を GPU バッファへアップロードする。
    pub fn build_gpu_batch(
        &self,
        batch:  &TextBatch,
        device: &wgpu::Device,
    ) -> Option<GpuTextBatch> {
        if batch.is_empty() { return None; }

        let vertex_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Text Vertex Buffer"),
            contents: bytemuck::cast_slice(&batch.vertices),
            usage:    wgpu::BufferUsages::VERTEX,
        });
        let index_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Text Index Buffer"),
            contents: bytemuck::cast_slice(&batch.indices),
            usage:    wgpu::BufferUsages::INDEX,
        });

        Some(GpuTextBatch {
            vertex_buf,
            index_buf,
            index_count: batch.indices.len() as u32,
        })
    }

    /// レンダーパスにテキストバッチを描画する。
    pub fn draw_text_batch<'pass>(
        &'pass self,
        gpu:  &'pass GpuTextBatch,
        pass: &mut wgpu::RenderPass<'pass>,
    ) {
        pass.set_pipeline(&self.pipeline.pipeline);
        pass.set_bind_group(0, &self.params_bg, &[]);
        pass.set_bind_group(1, &self.atlas_bg,  &[]);
        pass.set_vertex_buffer(0, gpu.vertex_buf.slice(..));
        pass.set_index_buffer(gpu.index_buf.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(0..gpu.index_count, 0, 0..1);
    }

    /// ワンショットヘルパー: テキストを準備してバッチに追加する。
    ///
    /// `pen_x`, `pen_y` はスクリーン座標（ピクセル、左上原点、Y 下向き）。
    pub fn queue_text(
        &mut self,
        batch:     &mut TextBatch,
        text:      &str,
        pen_x:     f32,
        pen_y:     f32,
        font_size: f32,
        color:     [f32; 4],
        sw:        f32,
        sh:        f32,
    ) {
        let glyphs = self.prepare_glyphs(text, font_size);
        batch.add_text_screen(text, pen_x, pen_y, font_size, color, &glyphs, sw, sh);
    }
}
