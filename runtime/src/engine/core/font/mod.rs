// ============================================================
//  font/mod.rs — フォントシステム統合
//
//  【方式】
//  グリフは常に **サイズ非依存 SDF**（固定 em 64px で焼いた距離場）としてアトラスへ入る。
//  描画時にフォントサイズを掛けて拡大縮小するので、
//    ・同じ文字はサイズが違っても 1 エントリで済む（アトラス消費が激減）
//    ・拡大してもエッジが階段状にならない
//    ・距離場を使って縁取り（アウトライン）を 1 パスで描ける
//
//  【フォント選択】
//  `FontRegistry` がアセットパス → フォント ID を管理し、アトラスのキーに ID を含める。
//  テキストごとに違うフォントを指定しても 1 枚のアトラス・1 本のバッチで描ける。
//
//  使い方:
//    let font_sys = FontSystem::new(&device, surface_format, depth_format, FontConfig::default());
//    // 毎フレーム:
//    let mut batch = TextBatch::new();
//    let glyphs = font_sys.prepare_glyphs("Hello", "");
//    batch.add_text_screen("Hello", 0.0, 0.0, 24.0, [1.0,1.0,1.0,1.0], &glyphs, sw, sh);
//    font_sys.flush(&queue);
//    let gpu = font_sys.build_gpu_batch(&batch, &device);
//    font_sys.draw_text_batch(&gpu, &mut render_pass);
// ============================================================

pub mod atlas;
pub mod axis_gizmo;
/// キャンバス上の TextComponent 描画（CPU で NDC まで変換して既存パイプラインへ流す）
pub mod canvas_text;
/// 操作ガイドの背景プレート（角丸クアッド。screen_hint 専用の極小パイプライン）
pub mod hint_plate;
pub mod icon_overlay;
pub mod pipeline;
/// フォント実体のレジストリ（アセットパス → フォント ID）
pub mod registry;
/// カーソル脇に出すスクリーンスペース操作ガイド（配置モード等の「いま何ができるか」）
pub mod screen_hint;
pub mod rasterizer;
/// SDF アトラスの共通定数とアウトライン太さ変換
pub mod sdf;

use ab_glyph::{Font, InvalidFont, PxScale, ScaleFont};
use wgpu::util::DeviceExt;

use atlas::{GlyphAtlas, GlyphInfo, GlyphKey};
use pipeline::{TextPipeline, TextVertex};
use rasterizer::rasterize_glyph_sdf;
use registry::FontRegistry;
use sdf::SDF_EM_PX;

/// デフォルトフォント（バイナリ埋め込み）。
/// `FontRegistry` の ID 0（組み込みフォント）になる。
pub static DEFAULT_FONT_BYTES: &[u8] =
    include_bytes!("../../engine_resources/fonts/M_PLUS_Rounded_1c/MPLUSRounded1c-Regular.ttf");

// ── FontConfig ────────────────────────────────────────────────

/// グリフアトラスの既定サイズ（一辺のピクセル数）。
/// ギズモ・操作ガイドのようにラテン数十文字しか使わない用途向け。
pub const DEFAULT_ATLAS_SIZE: u32 = 2048;

/// キャンバステキスト用アトラスサイズ（一辺のピクセル数）。
///
/// 日本語は使用字種が多い（HUD だけでも数百字）。em 64 + パディングで
/// 1 グリフ ≒ 80x80px なので、4096 なら約 2500 字を保持できる。
pub const CANVAS_ATLAS_SIZE: u32 = 4096;

/// `FontSystem` の初期化パラメータ。
///
/// 描画方式は常に SDF なのでモード指定は持たない（旧 `FontMode` は廃止）。
#[derive(Clone, Debug)]
pub struct FontConfig {
    /// グリフアトラスの一辺ピクセル数。
    pub atlas_size: u32,
}

impl Default for FontConfig {
    fn default() -> Self {
        Self {
            atlas_size: DEFAULT_ATLAS_SIZE,
        }
    }
}

impl FontConfig {
    /// キャンバステキスト用の設定（大きめのアトラス）。
    pub fn canvas() -> Self {
        Self {
            atlas_size: CANVAS_ATLAS_SIZE,
        }
    }
}

// ── TextBatch ─────────────────────────────────────────────────

/// 縁取り無しを表す縁取り色（完全透明）。
const NO_OUTLINE_COLOR: [f32; 4] = [0.0, 0.0, 0.0, 0.0];
/// 縁取り無しを表す SDF 距離（0 = シェーダー側で縁取りを無効化する）。
const NO_OUTLINE_DIST: f32 = 0.0;

/// CPU 側のテキスト描画バッチ。
pub struct TextBatch {
    vertices: Vec<TextVertex>,
    indices: Vec<u32>,
}

impl TextBatch {
    pub fn new() -> Self {
        Self {
            vertices: Vec::new(),
            indices: Vec::new(),
        }
    }

    pub fn is_empty(&self) -> bool {
        self.vertices.is_empty()
    }

    /// スクリーン座標（ピクセル）でテキストを追加する。
    ///
    /// `x`, `y` はペン基点（スクリーン左上原点、Y 下向き）。
    /// `glyphs` のメトリクスは em 単位なので、ここで `font_size` を掛けて px にする。
    /// この経路では縁取りを使わない（ギズモ・操作ガイド用）。
    #[allow(clippy::too_many_arguments)]
    pub fn add_text_screen(
        &mut self,
        _text: &str,
        mut pen_x: f32,
        pen_y: f32,
        font_size: f32,
        color: [f32; 4],
        glyphs: &[(char, GlyphInfo)],
        sw: f32,
        sh: f32,
    ) {
        // スクリーン座標 → NDC 変換ヘルパー
        let to_ndc_x = |px: f32| px / sw * 2.0 - 1.0;
        let to_ndc_y = |py: f32| 1.0 - py / sh * 2.0; // Y 反転

        for (_ch, info) in glyphs {
            // bearing[0] = left, bearing[1] = top（スクリーン座標系。em → px 換算）
            let bearing = info.bearing_px(font_size);
            let size = info.size_px(font_size);
            let x0 = pen_x + bearing[0];
            let y0 = pen_y + bearing[1];
            let x1 = x0 + size[0];
            let y1 = y0 + size[1];

            let nx0 = to_ndc_x(x0);
            let nx1 = to_ndc_x(x1);
            let ny0 = to_ndc_y(y0);
            let ny1 = to_ndc_y(y1);

            self.push_quad(
                [
                    [nx0, ny0, 0.0],
                    [nx1, ny0, 0.0],
                    [nx1, ny1, 0.0],
                    [nx0, ny1, 0.0],
                ],
                info.uv_min,
                info.uv_max,
                color,
                NO_OUTLINE_COLOR,
                NO_OUTLINE_DIST,
            );

            pen_x += info.advance_px(font_size);
        }
    }

    /// NDC 座標を直接指定してクアッド 1 枚（2 三角形）を追加する。
    ///
    /// `corners` は左上→右上→右下→左下 の順（時計回り）。
    /// `add_text_screen` が内部でやっている「スクリーン → NDC」変換を
    /// 呼び出し側が済ませている場合に使う（キャンバステキストが CPU で
    /// カメラ VP まで通した結果を積むための入口）。
    ///
    /// - `outline_color`: 縁取りの色（RGBA）
    /// - `outline_dist` : 縁取りの太さ（SDF テクスチャ単位。0 = 縁取りなし。
    ///   `sdf::outline_px_to_sdf` で px から変換する）
    pub fn add_quad_ndc(
        &mut self,
        corners: [[f32; 3]; 4],
        uv_min: [f32; 2],
        uv_max: [f32; 2],
        color: [f32; 4],
        outline_color: [f32; 4],
        outline_dist: f32,
    ) {
        self.push_quad(corners, uv_min, uv_max, color, outline_color, outline_dist);
    }

    /// 4 隅・UV・色からクアッドを積む共通処理（頂点順と索引の唯一の定義）。
    fn push_quad(
        &mut self,
        corners: [[f32; 3]; 4],
        uv_min: [f32; 2],
        uv_max: [f32; 2],
        color: [f32; 4],
        outline_color: [f32; 4],
        outline_dist: f32,
    ) {
        let base = self.vertices.len() as u32;
        let uvs = [
            [uv_min[0], uv_min[1]],
            [uv_max[0], uv_min[1]],
            [uv_max[0], uv_max[1]],
            [uv_min[0], uv_max[1]],
        ];
        for (position, uv) in corners.into_iter().zip(uvs) {
            self.vertices.push(TextVertex {
                position,
                uv,
                color,
                outline_color,
                outline_dist,
            });
        }
        self.indices
            .extend_from_slice(&[base, base + 1, base + 2, base, base + 2, base + 3]);
    }
}

impl Default for TextBatch {
    fn default() -> Self {
        Self::new()
    }
}

// ── GpuTextBatch ─────────────────────────────────────────────

/// GPU にアップロード済みのテキストバッチ。
pub struct GpuTextBatch {
    pub vertex_buf: wgpu::Buffer,
    pub index_buf: wgpu::Buffer,
    pub index_count: u32,
}

// ── FontSystem ────────────────────────────────────────────────

/// フォント描画システムの本体。
///
/// フォントレジストリ（複数フォント）・グリフアトラス（サイズ非依存 SDF）・
/// 描画パイプラインを保持し、`prepare_glyphs` でグリフを準備、
/// `build_gpu_batch` / `draw_text_batch` でテキストを描画する。
pub struct FontSystem {
    /// アセットパス → フォント実体。
    pub registry: FontRegistry,
    pub config: FontConfig,
    pub atlas: GlyphAtlas,
    pipeline: TextPipeline,
    atlas_bg: wgpu::BindGroup,
}

impl FontSystem {
    /// デフォルトフォント（M PLUS Rounded 1c Regular）で初期化する。
    pub fn new(
        device: &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format: wgpu::TextureFormat,
        config: FontConfig,
    ) -> Result<Self, InvalidFont> {
        Self::new_with_bytes(
            device,
            surface_format,
            depth_format,
            config,
            DEFAULT_FONT_BYTES,
        )
    }

    /// 任意のフォントバイト列を「組み込みフォント」として初期化する。
    pub fn new_with_bytes(
        device: &wgpu::Device,
        surface_format: wgpu::TextureFormat,
        depth_format: wgpu::TextureFormat,
        config: FontConfig,
        font_bytes: &'static [u8],
    ) -> Result<Self, InvalidFont> {
        let registry = FontRegistry::new(font_bytes)?;
        let atlas = GlyphAtlas::new(device, config.atlas_size);
        let pipeline = TextPipeline::new(device, surface_format, depth_format);

        let atlas_bg = Self::create_atlas_bg(device, &pipeline, &atlas);

        Ok(Self {
            registry,
            config,
            atlas,
            pipeline,
            atlas_bg,
        })
    }

    /// アトラス用バインドグループ（Group 0）を作る。
    fn create_atlas_bg(
        device: &wgpu::Device,
        pipeline: &TextPipeline,
        atlas: &GlyphAtlas,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Text Atlas BG"),
            layout: &pipeline.atlas_bgl,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::TextureView(&atlas.texture_view),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::Sampler(&pipeline.sampler),
                },
            ],
        })
    }

    /// 指定フォントのグリフを取得またはラスタライズしてアトラスに追加する。
    ///
    /// - `font_path`: フォントのアセットパス。空文字 = 組み込みフォント。
    ///   未ロードならここで読み込まれる（失敗しても組み込みで描画は継続する）。
    ///
    /// 返り値: (char, GlyphInfo) ペアのリスト（スペース等アウトラインなしは除外）。
    /// メトリクスは **em 単位** なので、描画側でフォントサイズを掛けること。
    pub fn prepare_glyphs(&mut self, text: &str, font_path: &str) -> Vec<(char, GlyphInfo)> {
        let font_id = self.registry.font_id(font_path);
        let mut result = Vec::new();

        for ch in text.chars() {
            let key = GlyphKey {
                font_id,
                codepoint: ch,
            };

            // キャッシュ済みならそのまま使う。
            if let Some(info) = self.atlas.get(&key) {
                result.push((ch, *info));
                continue;
            }

            // 未キャッシュ: 固定 em サイズで距離場を焼いてアトラスへ入れる。
            let font = self.registry.font(font_id);
            let Some(glyph) = rasterize_glyph_sdf(font, ch) else {
                // アウトラインなし（スペース等）→ 描くものが無いので飛ばす。
                // 送り幅が要る場合は `advance_em` を使うこと。
                continue;
            };
            if let Some(info) = self.atlas.insert(key, &glyph) {
                result.push((ch, info));
            }
        }

        result
    }

    /// アウトラインを持たない文字（スペース等）の送り幅を em 単位で返す。
    ///
    /// `prepare_glyphs` が返さない文字の字送りを埋めるために使う。
    /// 落とすと空白が詰まって字面が崩れる。
    pub fn advance_em(&mut self, font_path: &str, ch: char) -> f32 {
        let font_id = self.registry.font_id(font_path);
        let font = self.registry.font(font_id);
        // 基準 em サイズで引いて em 単位へ正規化する（メトリクスはスケールに線形）。
        let scaled = font.as_scaled(PxScale::from(SDF_EM_PX));
        scaled.h_advance(font.glyph_id(ch)) / SDF_EM_PX
    }

    /// アトラスを GPU にアップロードする（毎フレーム呼ぶ）。
    pub fn flush(&mut self, queue: &wgpu::Queue) {
        self.atlas.upload_if_dirty(queue);
    }

    /// atlas_bg を再構築する（アトラステクスチャ変更後に呼ぶ必要がある場合）。
    ///
    /// 現在の実装ではアトラスはリサイズしないため通常不要。
    pub fn rebuild_atlas_bg(&mut self, device: &wgpu::Device) {
        self.atlas_bg = Self::create_atlas_bg(device, &self.pipeline, &self.atlas);
    }

    /// TextBatch を GPU バッファへアップロードする。
    pub fn build_gpu_batch(&self, batch: &TextBatch, device: &wgpu::Device) -> Option<GpuTextBatch> {
        if batch.is_empty() {
            return None;
        }

        let vertex_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("Text Vertex Buffer"),
            contents: bytemuck::cast_slice(&batch.vertices),
            usage: wgpu::BufferUsages::VERTEX,
        });
        let index_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("Text Index Buffer"),
            contents: bytemuck::cast_slice(&batch.indices),
            usage: wgpu::BufferUsages::INDEX,
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
        gpu: &'pass GpuTextBatch,
        pass: &mut wgpu::RenderPass<'pass>,
    ) {
        pass.set_pipeline(&self.pipeline.pipeline);
        pass.set_bind_group(0, &self.atlas_bg, &[]);
        pass.set_vertex_buffer(0, gpu.vertex_buf.slice(..));
        pass.set_index_buffer(gpu.index_buf.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(0..gpu.index_count, 0, 0..1);
    }

    /// ワンショットヘルパー: 組み込みフォントでテキストを準備してバッチに追加する。
    ///
    /// `pen_x`, `pen_y` はスクリーン座標（ピクセル、左上原点、Y 下向き）。
    #[allow(clippy::too_many_arguments)]
    pub fn queue_text(
        &mut self,
        batch: &mut TextBatch,
        text: &str,
        pen_x: f32,
        pen_y: f32,
        font_size: f32,
        color: [f32; 4],
        sw: f32,
        sh: f32,
    ) {
        // 操作ガイド／ギズモは組み込みフォント固定（空文字 = 組み込み）。
        let glyphs = self.prepare_glyphs(text, "");
        batch.add_text_screen(text, pen_x, pen_y, font_size, color, &glyphs, sw, sh);
    }
}
