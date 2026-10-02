// ============================================================
//  font/mod.rs — フォントシステム統合
//
//  【方式】
//  グリフは常に **サイズ非依存の距離場**（固定の大きさで焼いた距離場）としてアトラスへ入る。
//  描画時にフォントサイズを掛けて拡大縮小するので、
//    ・同じ文字はサイズが違っても 1 エントリで済む（アトラス消費が激減）
//    ・拡大してもエッジが階段状にならない
//    ・距離場を使って縁取り（アウトライン）を 1 パスで描ける
//  距離場の種類（glyph_field.rs の DistanceFieldKind。設定は field_settings.rs）:
//    ・MTSDF（既定。2026-10-02）… 輪郭から作る 3 チャネルの MSDF + 真の SDF（msdf/。RGBA8・em 40）。大きな文字でも角が立ち曲線が滑らか
//    ・SDF（A/B と退避）       … em 64 で 2 値にしたビットマップから距離変換（rasterizer.rs。R8）
//  キャンバスの文字は設定の種類、ギズモ・操作ガイドの文字は SDF（FontConfig::default）。
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
/// 文字の距離場の設定（sdf / mtsdf・辺の色分け。project_settings の font 節・起動オプション）
pub mod field_settings;
/// MTSDF を焼いた数・時間・検査に落ちた字の記録とログ
pub mod field_stats;
/// 焼き上がった 1 グリフの距離場（SDF / MTSDF）の形と距離場の種類
pub mod glyph_field;
/// キャンバス上の TextComponent 描画（CPU で NDC まで変換して既存パイプラインへ流す）
pub mod canvas_text;
/// 操作ガイドの背景プレート（角丸クアッド。screen_hint 専用の極小パイプライン）
pub mod hint_plate;
pub mod icon_overlay;
/// 本文中のインライン画像（記法・アイコンセット・配置）
pub mod inline;
/// 描画器を持たない層のための GPU 非依存フォントキャッシュ
pub mod layout_fonts;
/// 輪郭から作る MTSDF（3 チャネルの MSDF + 真の SDF。2026-10-02）
pub mod msdf;
pub mod pipeline;
/// フォント実体のレジストリ（アセットパス → フォント ID）
pub mod registry;
/// カーソル脇に出すスクリーンスペース操作ガイド（配置モード等の「いま何ができるか」）
pub mod screen_hint;
pub mod rasterizer;
/// SDF アトラスの共通定数とアウトライン太さ変換
pub mod sdf;
/// 2 値画像の厳密な二乗距離変換（グリフの SDF を画素数に比例の時間で焼く。2026-09-28）
pub mod sdf_edt;
/// テキスト寸法計算（GPU 非依存の純関数。描画とピックで共有する）
pub mod text_layout;
/// キャンバスのテキストのレイアウトの使い回し（文字列・書体・大きさ・枠が同じなら前のフレームの配置を使う。2026-09-28）
pub mod text_layout_cache;
/// キャンバスのテキストの頂点・添字を使い回しの GPU バッファへ送る（毎フレーム作って捨てない。2026-09-28）
pub mod text_gpu_stream;
/// テキストの自動折り返し（枠幅に収める行分割・簡易禁則。GPU 非依存の純関数）
pub mod text_wrap;
/// 文字の塗り方（text.wgsl）の CPU の写しと試験（細い横画が副画素の位置で消えないこと。2026-10-01）
#[cfg(test)]
mod text_aa_tests;

use crate::engine::core::renderer::gpu_mem::GpuMemDeviceExt;
use ab_glyph::{Font, InvalidFont, PxScale, ScaleFont};
use rayon::prelude::*;
use std::collections::HashSet;

use atlas::{GlyphAtlas, GlyphInfo, GlyphKey};
use field_stats::FieldBakeStats;
use glyph_field::{DistanceFieldKind, GlyphField};
use msdf::{bake_glyph_mtsdf, BakeStats, ColoringStrategy};
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

/// キャンバステキスト用アトラスサイズ（一辺のピクセル数。1 チャネルの SDF のとき）。
///
/// 日本語は使用字種が多い（HUD だけでも数百字）。em 64 + パディングで
/// 全角 1 字 ≒ 60x60px なので、4096（R8 = 16 MiB）なら約 4,500 字を保持できる。
/// MTSDF のときは `msdf::params::MTSDF_CANVAS_ATLAS_SIZE`（2048。RGBA8 = 16 MiB・約 2,500 字）。
pub const CANVAS_ATLAS_SIZE: u32 = 4096;

/// まとめて焼く字がこの数以上なら並列に焼く（MTSDF。1 字だけなら行の並列だけで足りる）。
const PARALLEL_BAKE_MIN_GLYPHS: usize = 2;

/// `FontSystem` の初期化パラメータ。
#[derive(Clone, Debug)]
pub struct FontConfig {
    /// グリフアトラスの一辺ピクセル数。
    pub atlas_size: u32,
    /// 距離場の種類（アトラスの形式とシェーダーの入口が決まる）。
    pub field: DistanceFieldKind,
    /// MSDF の辺の色分け（MTSDF のときだけ使う）。
    pub coloring: ColoringStrategy,
}

impl Default for FontConfig {
    /// ギズモ・操作ガイド用（ラテン数十字・小さい字なので 1 チャネルの SDF・2048² の R8 = 4 MiB。2026-10-01 までと同じ）。
    fn default() -> Self {
        Self {
            atlas_size: DEFAULT_ATLAS_SIZE,
            field: DistanceFieldKind::Sdf,
            coloring: ColoringStrategy::default(),
        }
    }
}

impl FontConfig {
    /// キャンバステキスト用の設定（設定の距離場の種類・大きめのアトラス。どちらも 16 MiB）。
    pub fn canvas() -> Self {
        let settings = field_settings::active();
        let atlas_size = match settings.kind {
            DistanceFieldKind::Sdf => CANVAS_ATLAS_SIZE,
            DistanceFieldKind::Mtsdf => msdf::params::MTSDF_CANVAS_ATLAS_SIZE,
        };
        Self { atlas_size, field: settings.kind, coloring: settings.coloring }
    }
}

// ── TextBatch ─────────────────────────────────────────────────

/// 縁取り無しを表す縁取り色（完全透明）。
const NO_OUTLINE_COLOR: [f32; 4] = [0.0, 0.0, 0.0, 0.0];
/// 縁取り無しを表す SDF 距離（0 = シェーダー側で縁取りを無効化する）。
const NO_OUTLINE_DIST: f32 = 0.0;

/// グリフ 1 枚の陰影パラメータ（頂点属性として運ぶ値の束）。
///
/// 引数を並べると 8 個を超えて取り違えが起きるため、意味のある単位で束ねる。
/// すべて「クアッド内で定数」なので 4 頂点へ同じ値を積む。
#[derive(Clone, Copy, Debug)]
pub struct GlyphShading {
    /// 文字本体の色（RGBA 0..1）。
    pub color: [f32; 4],
    /// 縁取りの色（RGBA 0..1）。
    pub outline_color: [f32; 4],
    /// 縁取りの太さ（SDF テクスチャ単位。0 = 縁取りなし）。
    pub outline_dist: f32,
    /// 太さ調整（SDF テクスチャ単位。正で太く・負で細く。0 = フォント本来）。
    pub weight_dist: f32,
    /// 追加のスムース幅（SDF テクスチャ単位。影のぼかし。0 = シャープ）。
    pub softness: f32,
}

impl GlyphShading {
    /// 縁取り・太さ調整・ぼかしのいずれも無い、色だけの陰影を作る。
    ///
    /// ギズモ・操作ガイドなど「ただ文字を出す」経路が使う（従来挙動と同一）。
    pub fn plain(color: [f32; 4]) -> Self {
        Self {
            color,
            outline_color: NO_OUTLINE_COLOR,
            outline_dist: NO_OUTLINE_DIST,
            weight_dist: 0.0,
            softness: 0.0,
        }
    }
}

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

    /// 中身を空にする（確保した領域は残す。フレームごとに同じバッチへ積み直すため）。
    pub fn clear(&mut self) {
        self.vertices.clear();
        self.indices.clear();
    }

    /// 現在までに積んだインデックス数。
    ///
    /// 「1 本のバッチの中の部分区間だけを描く」（UI 描画順の統合＝ラン単位描画）ために、
    /// アイテムを積む合間で区間の境目を記録する用途に使う。
    pub fn index_len(&self) -> u32 {
        self.indices.len() as u32
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
                info.field_em_px,
                &GlyphShading::plain(color),
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
    /// - `field_em`: このグリフの距離場の解像度（`GlyphInfo::field_em_px`）
    /// - `shading`: 色・縁取り・太さ・ぼかし（`GlyphShading`。px からの変換は
    ///   `FontSystem::value_spec` を使う）
    pub fn add_quad_ndc(
        &mut self,
        corners: [[f32; 3]; 4],
        uv_min: [f32; 2],
        uv_max: [f32; 2],
        field_em: f32,
        shading: &GlyphShading,
    ) {
        self.push_quad(corners, uv_min, uv_max, field_em, shading);
    }

    /// 4 隅・UV・陰影からクアッドを積む共通処理（頂点順と索引の唯一の定義）。
    fn push_quad(
        &mut self,
        corners: [[f32; 3]; 4],
        uv_min: [f32; 2],
        uv_max: [f32; 2],
        field_em: f32,
        shading: &GlyphShading,
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
                color: shading.color,
                outline_color: shading.outline_color,
                outline_dist: shading.outline_dist,
                weight_dist: shading.weight_dist,
                softness: shading.softness,
                field_em,
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
    /// アトラスに置けない字（アウトラインが無い・アトラスが満杯で入らなかった）の表（2026-09-28）。
    ///
    /// どちらも結果が変わらない（フォント ID の実体は固定・アトラスは追い出さない）ので、焼き直さずに飛ばす。
    /// `prepare_glyphs` の返り値は従来と同じ（どちらの字も返さない）。
    unplaceable: HashSet<GlyphKey>,
    /// MTSDF を焼いた記録（数・時間・検査に落ちた字。ログは field_stats.rs）。
    pub field_stats: FieldBakeStats,
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
        let atlas = GlyphAtlas::new(device, config.atlas_size, config.field);
        let pipeline = TextPipeline::new(device, surface_format, depth_format, config.field);

        let atlas_bg = Self::create_atlas_bg(device, &pipeline, &atlas);

        Ok(Self {
            registry,
            config,
            atlas,
            pipeline,
            atlas_bg,
            unplaceable: HashSet::new(),
            field_stats: FieldBakeStats::new(field_settings::per_glyph_log_enabled()),
        })
    }

    /// 距離場の種類。
    pub fn field_kind(&self) -> DistanceFieldKind {
        self.config.field
    }

    /// px → 値の変換の決まり（縁取り・太さ・影のぼかし。距離場の種類ごと）。
    pub fn value_spec(&self) -> sdf::FieldValueSpec {
        self.config.field.value_spec()
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

    /// 指定フォントのグリフを取得または焼いてアトラスに追加する。
    ///
    /// - `font_path`: フォントのアセットパス。空文字 = 組み込みフォント。
    ///   未ロードならここで読み込まれる（失敗しても組み込みで描画は継続する）。
    ///
    /// 返り値: (char, GlyphInfo) ペアのリスト（スペース等アウトラインなしは除外）。
    /// メトリクスは **em 単位** なので、描画側でフォントサイズを掛けること。
    ///
    /// 【まとめて焼く（2026-10-02）】まだアトラスに無い字を先に集めて焼く。MTSDF は 1 字が SDF より重いので、
    /// 2 字以上なら字ごとに並列で焼く（rayon。1 字の中も行ごとに並列）。アトラスへの登録は順に行う。
    pub fn prepare_glyphs(&mut self, text: &str, font_path: &str) -> Vec<(char, GlyphInfo)> {
        let font_id = self.registry.font_id(font_path);

        // ── 1. まだアトラスに無い字を集める（重複なし・出た順）──
        // 焼いても描くものが無い・アトラスに入らないと分かっている字は焼き直さない（2026-09-28）。
        let mut missing: Vec<char> = Vec::new();
        let mut seen: HashSet<char> = HashSet::new();
        for ch in text.chars() {
            let key = GlyphKey { font_id, codepoint: ch };
            if self.atlas.get(&key).is_none() && !self.unplaceable.contains(&key) && seen.insert(ch) {
                missing.push(ch);
            }
        }

        // ── 2. 焼いてアトラスへ入れる ──
        if !missing.is_empty() {
            self.bake_and_insert(font_id, &missing);
        }

        // ── 3. 入力の順にグリフ情報を返す（アトラスに無い字は返さない）──
        text.chars()
            .filter_map(|ch| self.atlas.get(&GlyphKey { font_id, codepoint: ch }).map(|info| (ch, *info)))
            .collect()
    }

    /// 字の列を焼いてアトラスへ入れる（焼けない字・入らない字は `unplaceable` へ覚える）。
    fn bake_and_insert(&mut self, font_id: u16, chars: &[char]) {
        // フォント実体は clone（Arc）して借用を分ける（このあとアトラスと記録を可変で使う）。
        let font = self.registry.font(font_id).clone();
        let kind = self.config.field;
        let coloring = self.config.coloring;
        let bake_one = |ch: char| -> Option<(GlyphField, Option<BakeStats>)> {
            match kind {
                DistanceFieldKind::Sdf => rasterize_glyph_sdf(&font, ch).map(|g| (g, None)),
                DistanceFieldKind::Mtsdf => bake_glyph_mtsdf(&font, ch, coloring).map(|(g, st)| (g, Some(st))),
            }
        };
        let batch_start = std::time::Instant::now();
        let baked: Vec<(char, Option<(GlyphField, Option<BakeStats>)>)> =
            if kind == DistanceFieldKind::Mtsdf && chars.len() >= PARALLEL_BAKE_MIN_GLYPHS {
                // まとめて並列に焼く（この区間の時間 = 並列に焼いた壁時計の時間）
                crate::profile_scope!("描画/UI/テキスト/グリフ焼き（並列）");
                chars.par_iter().map(|&ch| (ch, bake_one(ch))).collect()
            } else {
                chars
                    .iter()
                    .map(|&ch| {
                        // 初めて出る文字だけがここへ来る（呼び出し回数＝そのフレームに新しく焼いた字数。スクロール開始の山の切り分け用）
                        crate::profile_scope!("描画/UI/テキスト/グリフ焼き");
                        (ch, bake_one(ch))
                    })
                    .collect()
            };
        // 計測用（SEED_FONT_FIELD_LOG=1）: まとめて焼いた字数と壁時計の時間（初めて出る画面の詰まりの大きさの目安）
        self.field_stats.record_batch(chars.len(), batch_start.elapsed(), kind.as_str());
        for (ch, result) in baked {
            let key = GlyphKey { font_id, codepoint: ch };
            let Some((glyph, stats)) = result else {
                // アウトラインなし（スペース等）→ 描くものが無いので飛ばす。送り幅が要る場合は `advance_em` を使うこと。
                // フォント ID ごとの実体は変わらない（registry は読み直さない）ので、結果は何度焼いても同じ＝覚えてよい。
                self.unplaceable.insert(key);
                continue;
            };
            if let Some(stats) = stats {
                self.field_stats.record(ch, &stats);
            }
            // アトラスが満杯（追い出しはしない＝空きは増えない）: 同じ大きさの字は二度と入らないので覚える
            if self.atlas.insert(key.clone(), &glyph).is_none() {
                self.unplaceable.insert(key);
            }
        }
    }

    /// アウトラインを持たない文字（スペース等）の送り幅を em 単位で返す。
    ///
    /// `prepare_glyphs` が返さない文字の字送りを埋めるために使う。
    /// 落とすと空白が詰まって字面が崩れる。
    pub fn advance_em(&mut self, font_path: &str, ch: char) -> f32 {
        let font_id = self.registry.font_id(font_path);
        let font = self.registry.font(font_id);
        // 基準 em サイズで引いて em 単位へ正規化する（メトリクスはスケールに線形）。
        // 送り幅の定義は text_layout に一本化する（描画とピックで必ず同値にするため）
        text_layout::advance_em(font, ch)
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

        let vertex_buf = device.create_buffer_init_tracked(&wgpu::util::BufferInitDescriptor {
            label: Some("Text Vertex Buffer"),
            contents: bytemuck::cast_slice(&batch.vertices),
            usage: wgpu::BufferUsages::VERTEX,
        });
        let index_buf = device.create_buffer_init_tracked(&wgpu::util::BufferInitDescriptor {
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

    /// テキストバッチの **部分区間だけ**をレンダーパスへ描画する。
    ///
    /// UI 描画順の統合（スプライト／プリミティブ／テキストをレイヤー順に 1 列へ並べる）で、
    /// 1 本の頂点バッファを保ったままラン単位に分割描画するために使う。
    ///
    /// - `first_index`: バッチ先頭からのインデックス番号
    /// - `index_count`: 描くインデックス数（0 なら何もしない）
    pub fn draw_text_batch_range<'pass>(
        &'pass self,
        gpu: &'pass GpuTextBatch,
        first_index: u32,
        index_count: u32,
        pass: &mut wgpu::RenderPass<'pass>,
    ) {
        // 空区間、またはバッチ範囲外の指定は描かない（安全側に倒す）。
        if index_count == 0 || first_index + index_count > gpu.index_count {
            return;
        }
        pass.set_pipeline(&self.pipeline.pipeline);
        pass.set_bind_group(0, &self.atlas_bg, &[]);
        pass.set_vertex_buffer(0, gpu.vertex_buf.slice(..));
        pass.set_index_buffer(gpu.index_buf.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(first_index..(first_index + index_count), 0, 0..1);
    }

    /// 使い回しのバッファ（`text_gpu_stream::TextGpuStream`）の **部分区間だけ**をレンダーパスへ描画する。
    ///
    /// - `uploaded_indices`: そのバッファへ今のフレームで送った添字の数（区間がこれを超えたら描かない）
    /// - `first_index` / `index_count`: 区間（`index_count` が 0 なら何もしない）
    #[allow(clippy::too_many_arguments)]
    pub fn draw_buffers_range<'pass>(
        &'pass self,
        vertex_buf: &'pass wgpu::Buffer,
        index_buf: &'pass wgpu::Buffer,
        uploaded_indices: u32,
        first_index: u32,
        index_count: u32,
        pass: &mut wgpu::RenderPass<'pass>,
    ) {
        // 空区間、または送った範囲の外の指定は描かない（安全側に倒す。draw_text_batch_range と同じ規則）。
        if index_count == 0 || first_index + index_count > uploaded_indices {
            return;
        }
        pass.set_pipeline(&self.pipeline.pipeline);
        pass.set_bind_group(0, &self.atlas_bg, &[]);
        pass.set_vertex_buffer(0, vertex_buf.slice(..));
        pass.set_index_buffer(index_buf.slice(..), wgpu::IndexFormat::Uint32);
        pass.draw_indexed(first_index..(first_index + index_count), 0, 0..1);
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
