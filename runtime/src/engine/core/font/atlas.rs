// ============================================================
//  font/atlas.rs — グリフアトラス（サイズ非依存の距離場・複数ページの棚詰め）
//
//  【役割】
//  初回描画時にグリフを固定サイズで距離場化して CPU バッファへ置き、`upload_if_dirty()` で GPU テクスチャへ転送する。
//  距離場の種類（glyph_field.rs の DistanceFieldKind）でテクスチャの形式が決まる:
//    - 1 チャネルの SDF … R8Unorm（em 64。キャンバスは 1 ページ 4096²＝16 MiB）
//    - MTSDF          … Rgba8Unorm（em 40〜64。キャンバスは 1 ページ 2048²＝16 MiB。2026-10-02 からの既定）
//
//  【複数ページ（2026-10-03。docs/ui_components.md §12.15）】
//  GPU のテクスチャは `texture_2d_array`（1 層 = 1 ページ・どのページも同じ大きさ）。字は `GlyphInfo::page` の層にあり、
//  頂点でページ番号を運んでシェーダーが `textureSample(atlas, samp, uv, page)` で読む（ページごとにバッチを分けない）。
//  ページが満杯になったら上限（`FontConfig::max_atlas_pages`。既定 4・render.profile の memory_hint が memory_usage なら 2・
//  設定 `font.atlas_pages` で上書き）まで次のページを足す。配置の規則は atlas_pages.rs（wgpu を使わない純粋な計算）。
//  テクスチャの層の数は作った後に変えられないので、ページを足したフレームの `upload_if_dirty` で層を 1 つ増やした
//  テクスチャを作り直し、CPU の写しから全ページを送り直す（呼び出し側は bind group を作り直す。返り値が true）。
//  最初は 1 ページ（従来と同じ 16 MiB）で、字が増えたときだけ GPU・CPU のメモリが 1 ページずつ増える。
//
//  【サイズ非依存】
//  キーにフォントサイズを持たない。1 グリフ = 1 エントリで、描画時に
//  em 単位のメトリクスへフォントサイズを掛けて任意サイズへ拡大縮小する。
//  （旧実装はサイズごとに焼き直していたため、同じ文字がサイズ数だけ場所を食っていた）
//
//  【容量の目安】
//  SDF: em 64 + 四方 8px パディングで全角 1 字 ≒ 60x60px → 4096 の 1 ページに約 4,500 字。
//  MTSDF: em 40 + 四方 5px で全角 1 字 ≒ 40x40px → 2048 の 1 ページに約 2,500 字（画数の多い字は em 64 まで大きい）。
//
//  【上限まで満杯になった場合（追い出し。2026-10-03）】
//  ページごとに「最後に使われたフレーム」を持ち（字を置いた・描いたときに `touch_page`）、上限まで満杯で字が入らないとき、
//  呼び出し側（mod.rs の FontSystem）が `evict_idle_page` で **一定のフレーム数以上使われていないページのうち最も古いもの** を
//  丸ごと空にして使い直す（そのページの字はキャッシュから消え、次に使われたときに焼き直す。グリフ単位の穴の再利用はしない）。
//  空にしたページは CPU の写しを 0 で埋め直して全部を送り直す（字の間の隙間に古い字が残ると、クアッドの端のバイリニアで
//  にじむため）。どのページも最近使われていれば追い出さず、その字を描画しない（入れ替えの往復を作らない）。
//  無言だと原因不明の「字が出ない」になるため、最初の 1 回だけ警告を出す。
// ============================================================

use crate::engine::core::renderer::gpu_mem::GpuMemDeviceExt;
use std::collections::HashMap;

use super::atlas_pages::{AtlasPageAllocator, PageUsage};
use super::field_settings::LOG_TAG;
use super::glyph_field::{DistanceFieldKind, GlyphField};

/// グリフ間のパディング（ピクセル）。
/// 隣のグリフのにじみ（バイリニア補間）を拾わないための隙間。
const ATLAS_PADDING: u32 = 2;

/// MiB への換算（ログ用）。
const BYTES_PER_MIB: f64 = 1024.0 * 1024.0;

/// 百分率への換算（ログ用）。
const PERCENT: f32 = 100.0;

// ── GlyphKey ──────────────────────────────────────────────────

/// グリフキャッシュのキー。
///
/// サイズ非依存アトラスなのでフォントサイズは含まない。
/// フォント ID を含むので、同じ文字でも別フォントなら別エントリになる。
#[derive(Hash, PartialEq, Eq, Clone, Debug)]
pub struct GlyphKey {
    /// `FontRegistry` が割り当てたフォント ID。
    pub font_id: u16,
    /// Unicode コードポイント。
    pub codepoint: char,
}

// ── GlyphInfo ─────────────────────────────────────────────────

/// アトラス内のグリフ情報（メトリクスはすべて em 単位）。
///
/// px へ直すには `*_px(font_size)` を使う。
/// `tight_*` は距離場の余白（`pad_em`）を取り除いた「文字の実寸」で、
/// 外接矩形の実測（操作ガイドのプレートなど）に使う。
#[derive(Clone, Copy, Debug)]
pub struct GlyphInfo {
    /// アトラス UV の左上・右下 [0, 1]（ページの中の位置。どのページも同じ大きさ）
    pub uv_min: [f32; 2],
    pub uv_max: [f32; 2],
    /// この字が置かれたアトラスのページ（テクスチャ配列の層の番号。頂点で運んでシェーダーが層を選ぶ）
    pub page: u32,
    /// クアッドサイズ（em 単位、スプレッドのパディング込み）
    pub size_em: [f32; 2],
    /// ペン基点からクアッド左上へのオフセット（em 単位、Y 下向き）
    pub bearing_em: [f32; 2],
    /// 水平アドバンス幅（em 単位）
    pub advance_em: f32,
    /// 四方の余白（em 単位。SDF は spread 8/64・MTSDF は 5/40 = どちらも 0.125）
    pub pad_em: f32,
    /// この字の距離場の解像度（em あたりのテクセル数。頂点で運ぶ。glyph_field.rs の em_px）
    pub field_em_px: f32,
    /// MTSDF の検査（安全弁）に落ちて真の SDF で描く字か（ログ・試験用。描き方はアトラスの中身で決まる）
    pub msdf_fallback: bool,
}

impl GlyphInfo {
    /// クアッドサイズ（px）。
    #[inline]
    pub fn size_px(&self, font_size: f32) -> [f32; 2] {
        [self.size_em[0] * font_size, self.size_em[1] * font_size]
    }

    /// ペン基点からクアッド左上へのオフセット（px）。
    #[inline]
    pub fn bearing_px(&self, font_size: f32) -> [f32; 2] {
        [self.bearing_em[0] * font_size, self.bearing_em[1] * font_size]
    }

    /// 水平アドバンス幅（px）。
    #[inline]
    pub fn advance_px(&self, font_size: f32) -> f32 {
        self.advance_em * font_size
    }

    /// 距離場の余白を除いた、文字そのものの左上オフセット（px）。
    #[inline]
    pub fn tight_bearing_px(&self, font_size: f32) -> [f32; 2] {
        let pad = self.pad_em * font_size;
        [
            self.bearing_em[0] * font_size + pad,
            self.bearing_em[1] * font_size + pad,
        ]
    }

    /// 距離場の余白を除いた、文字そのもののサイズ（px）。
    #[inline]
    pub fn tight_size_px(&self, font_size: f32) -> [f32; 2] {
        let pad2 = 2.0 * self.pad_em * font_size;
        [
            self.size_em[0] * font_size - pad2,
            self.size_em[1] * font_size - pad2,
        ]
    }
}

// ── DirtyRows ─────────────────────────────────────────────────

/// 1 ページの未アップロードの行範囲 [min, max)。空なら min >= max。
#[derive(Clone, Copy, Debug)]
struct DirtyRows {
    min: u32,
    max: u32,
}

impl DirtyRows {
    /// 空の範囲。
    const EMPTY: DirtyRows = DirtyRows { min: u32::MAX, max: 0 };

    /// 行範囲 [y0, y1) を積む。
    fn add(&mut self, y0: u32, y1: u32) {
        self.min = self.min.min(y0);
        self.max = self.max.max(y1);
    }

    /// 空か。
    fn is_empty(&self) -> bool {
        self.min >= self.max
    }
}

// ── GlyphAtlas ────────────────────────────────────────────────

/// 複数ページの棚詰めによるグリフアトラス。
///
/// グリフを CPU バッファ（ページごと）へ距離場としてキャッシュし、`upload_if_dirty` で
/// GPU テクスチャ配列（R8Unorm / Rgba8Unorm。1 層 = 1 ページ）へ **更新のあった行だけ** 転送する。
pub struct GlyphAtlas {
    /// GPU のテクスチャ配列（層の数 = `gpu_layers`）。
    texture: wgpu::Texture,
    /// 配列として読むビュー（`D2Array`。bind group に入れる）。
    texture_view: wgpu::TextureView,
    /// 1 ページの一辺（テクセル）。
    pub atlas_size: u32,
    /// 距離場の種類（テクスチャの形式と 1 テクセルのバイト数）。
    pub kind: DistanceFieldKind,
    /// テクスチャのラベル（GPU メモリの計測の枠を持ち主ごとに分ける。キャンバス・ギズモ・操作ガイド）。
    label: &'static str,

    /// ページへの配置（棚詰め・ページの追加・上限。atlas_pages.rs）。
    pages: AtlasPageAllocator,
    /// CPU 側のバッファ（ページごと。1 テクセル = kind.bytes_per_texel() バイト）。
    cpu_pages: Vec<Vec<u8>>,
    /// ページごとの未アップロードの行範囲。
    dirty: Vec<DirtyRows>,
    /// 今の GPU のテクスチャの層の数（ページが増えたら `upload_if_dirty` で作り直す）。
    gpu_layers: u32,
    /// キャッシュ。
    glyphs: HashMap<GlyphKey, GlyphInfo>,
    /// あふれ警告を出したか（毎フレーム出さないための 1 回きりフラグ）。
    overflow_warned: bool,
    /// ページを空にした（追い出した）回数。字の場所が変わりうる印（使い回しのレイアウトを捨てる判断に使う）。
    evictions: u64,
}

/// 追い出したページの記録（ログ・呼び出し側の後始末用）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct EvictedPage {
    /// 空にしたページ。
    pub page: u32,
    /// そのページにあった字の数（キャッシュから消した数）。
    pub glyphs: usize,
    /// 最後に使われてから何フレーム経っていたか。
    pub idle_frames: u64,
}

impl GlyphAtlas {
    /// 一辺 `atlas_size` のページを上限 `max_pages` 枚まで持てるアトラスを作る（最初は 1 ページ）。
    ///
    /// `label` は GPU のテクスチャのラベル（計測の内訳に出る。持ち主ごとに変える）。
    pub fn new(device: &wgpu::Device, atlas_size: u32, kind: DistanceFieldKind, max_pages: u32, label: &'static str) -> Self {
        let pages = AtlasPageAllocator::new(atlas_size, max_pages);
        let layers = pages.page_count();
        let (texture, texture_view) = create_atlas_texture(device, label, atlas_size, kind, layers);
        Self {
            texture,
            texture_view,
            atlas_size,
            kind,
            label,
            cpu_pages: (0..layers).map(|_| blank_page(atlas_size, kind)).collect(),
            dirty: vec![DirtyRows::EMPTY; layers as usize],
            gpu_layers: layers,
            pages,
            glyphs: HashMap::new(),
            overflow_warned: false,
            evictions: 0,
        }
    }

    /// ページ `page` をフレーム `frame` に使った（描いた）印を付ける（追い出しの順。古いフレームでは戻さない）。
    #[inline]
    pub fn touch_page(&mut self, page: u32, frame: u64) {
        self.pages.touch(page, frame);
    }

    /// ページの組（ビット `1 << page` の和）をフレーム `frame` に使った印を付ける（使い回しのレイアウトの字のページ）。
    pub fn touch_pages(&mut self, page_mask: u64, frame: u64) {
        let mut mask = page_mask;
        while mask != 0 {
            let page = mask.trailing_zeros();
            self.pages.touch(page, frame);
            mask &= mask - 1;
        }
    }

    /// 上限まで満杯で字が入らなかった（追い出せるページも無かった）ことを、最初の 1 回だけ警告する（追い出した後はまた 1 回）。
    ///
    /// 無言だと原因不明の「字が出ない」になるため。
    pub fn warn_overflow_once(&mut self) {
        if self.overflow_warned {
            return;
        }
        self.overflow_warned = true;
        eprintln!(
            "{LOG_TAG} グリフアトラスが上限まで満杯です（{}）。しばらく使われていないページが無い間、新規グリフは描画されません。\
             字数の多いプロジェクトは project_settings.json の font.atlas_pages でページの上限を上げてください（docs/ui_components.md §12.15）",
            self.usage_line()
        );
    }

    /// ページを空にした（追い出した）回数（増えたら、覚えておいた字の場所が古くなっているかもしれない）。
    pub fn evictions(&self) -> u64 {
        self.evictions
    }

    /// 上限まで満杯のとき、`min_idle_frames` 以上使われていないページのうち最も古いものを丸ごと空にする（追い出し）。
    ///
    /// そのページの字をキャッシュから消し、CPU の写しを 0 で埋め直し、ページ全体を次の `upload_if_dirty` で送り直す。
    /// どのページも最近使われていれば何もしない（None）。
    pub fn evict_idle_page(&mut self, frame: u64, min_idle_frames: u64) -> Option<EvictedPage> {
        let page = self.pages.least_recently_used_idle_page(frame, min_idle_frames)?;
        let idle_frames = frame.saturating_sub(self.pages.last_used_frame(page).unwrap_or(0));
        // ── そのページの字をキャッシュから消す ──
        let before = self.glyphs.len();
        self.glyphs.retain(|_, info| info.page != page);
        let removed = before - self.glyphs.len();
        // ── 配置を空にし、CPU の写しを 0 に戻して全体を送り直す印を付ける ──
        self.pages.clear_page(page, frame);
        self.cpu_pages[page as usize].fill(0);
        self.dirty[page as usize].add(0, self.atlas_size);
        self.evictions += 1;
        // 空きができたので、次に満杯になったらまた警告する
        self.overflow_warned = false;
        let evicted = EvictedPage { page, glyphs: removed, idle_frames };
        eprintln!(
            "{LOG_TAG} グリフアトラスのページ {} を空にして使い直します（{} フレーム使われていない・字 {}・追い出し {} 回目。{}）",
            page,
            idle_frames,
            removed,
            self.evictions,
            self.usage_line()
        );
        Some(evicted)
    }

    /// bind group に入れるビュー（テクスチャ配列。`upload_if_dirty` が true を返したら作り直すこと）。
    pub fn texture_view(&self) -> &wgpu::TextureView {
        &self.texture_view
    }

    /// キャッシュ済みグリフ情報を返す。
    #[inline]
    pub fn get(&self, key: &GlyphKey) -> Option<&GlyphInfo> {
        self.glyphs.get(key)
    }

    /// 入っているグリフの数（容量の計測・ログ用）。
    pub fn glyph_count(&self) -> usize {
        self.glyphs.len()
    }

    /// 今あるページの数。
    pub fn page_count(&self) -> u32 {
        self.pages.page_count()
    }

    /// ページの上限。
    pub fn max_pages(&self) -> u32 {
        self.pages.max_pages()
    }

    /// ページごとの使用状況（ログ・計測用）。
    pub fn page_usage(&self) -> Vec<PageUsage> {
        self.pages.usage()
    }

    /// 上限のページ数に対する使った高さの割合（0..1。ログ・計測用）。
    pub fn used_fraction_of_limit(&self) -> f32 {
        self.pages.used_fraction_of_limit()
    }

    /// 1 ページの GPU のバイト数（一辺² × 1 テクセルのバイト数）。
    pub fn page_bytes(&self) -> u64 {
        u64::from(self.atlas_size) * u64::from(self.atlas_size) * self.kind.bytes_per_texel() as u64
    }

    /// ページ数と使用率の 1 行（`[SEED FONT]` のログ・field_stats の集計に使う）。
    pub fn usage_line(&self) -> String {
        let per_page: Vec<String> = self
            .page_usage()
            .iter()
            .map(|u| format!("{}字 高さ{:.0}% 面積{:.0}%", u.glyphs, u.height_fraction * PERCENT, u.area_fraction * PERCENT))
            .collect();
        format!(
            "アトラス {} {}x{} ページ {}/{}（{:.0} MiB）・字 {}・上限に対する使用率 {:.1}%［{}］",
            self.kind.as_str(),
            self.atlas_size,
            self.atlas_size,
            self.page_count(),
            self.max_pages(),
            (self.page_bytes() * u64::from(self.page_count())) as f64 / BYTES_PER_MIB,
            self.glyph_count(),
            self.used_fraction_of_limit() * PERCENT,
            per_page.join(" / ")
        )
    }

    /// 焼き上がったグリフの距離場をアトラスに追加する（距離場の種類はアトラスと同じであること）。
    ///
    /// 成功時は `GlyphInfo` を返す（既に存在する場合も返す）。今のページが満杯なら上限まで次のページを足して置く。
    /// 上限まで満杯の場合は `None`（呼び出し側が `evict_idle_page` を試し、だめなら `warn_overflow_once`）。
    pub fn insert(&mut self, key: GlyphKey, glyph: &GlyphField) -> Option<GlyphInfo> {
        self.insert_with(key, glyph, false)
    }

    /// 最後の手段: 古いページの棚の隙間へ入れる（追い出せるページが無いとき。atlas_pages.rs の `alloc_in_gaps`）。入らなければ None。
    pub fn insert_in_gaps(&mut self, key: GlyphKey, glyph: &GlyphField) -> Option<GlyphInfo> {
        self.insert_with(key, glyph, true)
    }

    /// `insert` / `insert_in_gaps` の本体（`in_gaps` が真なら古いページの隙間へ、偽ならいちばん新しいページへ）。
    fn insert_with(&mut self, key: GlyphKey, glyph: &GlyphField, in_gaps: bool) -> Option<GlyphInfo> {
        if let Some(info) = self.glyphs.get(&key) {
            return Some(*info);
        }

        let (width, height) = (glyph.width, glyph.height);
        let pages_before = self.pages.page_count();
        let (w, h) = (width + ATLAS_PADDING, height + ATLAS_PADDING);
        let allocated = if in_gaps { self.pages.alloc_in_gaps(w, h) } else { self.pages.alloc(w, h) };
        let Some(slot) = allocated else {
            // 上限まで満杯: 呼び出し側が追い出し・隙間を試す（だめなら `warn_overflow_once` で警告する）。
            return None;
        };
        // ページを足した: CPU の写しを用意する（GPU の層は次の upload_if_dirty で増やす）。
        if self.pages.page_count() > pages_before {
            while self.cpu_pages.len() < self.pages.page_count() as usize {
                self.cpu_pages.push(blank_page(self.atlas_size, self.kind));
                self.dirty.push(DirtyRows::EMPTY);
            }
            eprintln!(
                "{LOG_TAG} グリフアトラスにページを足しました: {} → {} ページ（{}）",
                pages_before,
                self.pages.page_count(),
                self.usage_line()
            );
        }

        // CPU バッファへ距離場をコピー（1 テクセルのバイト数ぶんの幅で行ごとに）
        debug_assert_eq!(glyph.kind, self.kind, "アトラスと違う種類の距離場");
        let bpt = self.kind.bytes_per_texel();
        let atlas_row_bytes = self.atlas_size as usize * bpt;
        let glyph_row_bytes = width as usize * bpt;
        let page_data = &mut self.cpu_pages[slot.page as usize];
        for row in 0..height as usize {
            let src = &glyph.data[row * glyph_row_bytes..(row + 1) * glyph_row_bytes];
            let dst_off = (slot.y as usize + row) * atlas_row_bytes + slot.x as usize * bpt;
            page_data[dst_off..dst_off + glyph_row_bytes].copy_from_slice(src);
        }

        // 書き込んだ行範囲をダーティに積む（アップロードはこの範囲だけ）。
        self.dirty[slot.page as usize].add(slot.y, slot.y + height);

        let inv = 1.0 / self.atlas_size as f32;
        let info = GlyphInfo {
            uv_min: [slot.x as f32 * inv, slot.y as f32 * inv],
            uv_max: [(slot.x + width) as f32 * inv, (slot.y + height) as f32 * inv],
            page: slot.page,
            size_em: glyph.size_em,
            bearing_em: glyph.bearing_em,
            advance_em: glyph.advance_em,
            pad_em: glyph.pad_em,
            field_em_px: glyph.em_px,
            msdf_fallback: glyph.msdf_fallback,
        };
        self.glyphs.insert(key, info);
        Some(info)
    }

    /// ダーティな行範囲だけを GPU テクスチャへアップロードする。
    ///
    /// 全域転送（1 ページ 16MB）を毎回やると新規グリフ 1 文字でフレームが落ちるため、
    /// 実際に書き込んだ行だけを送る。ページが増えていたら層を増やしたテクスチャを作り直して全ページを送り、
    /// **true を返す**（ビューが変わったので、呼び出し側は bind group を作り直すこと）。
    pub fn upload_if_dirty(&mut self, device: &wgpu::Device, queue: &wgpu::Queue) -> bool {
        // ── ページが増えた: 層を増やしたテクスチャを作り直し、CPU の写しから全ページを送る ──
        let layers = self.pages.page_count();
        if layers > self.gpu_layers {
            let (texture, view) = create_atlas_texture(device, self.label, self.atlas_size, self.kind, layers);
            self.texture = texture;
            self.texture_view = view;
            self.gpu_layers = layers;
            for page in 0..layers {
                self.write_rows(queue, page, 0, self.atlas_size);
                self.dirty[page as usize] = DirtyRows::EMPTY;
            }
            return true;
        }

        // ── ふだん: ページごとにダーティな行だけ ──
        for page in 0..layers {
            let rows = self.dirty[page as usize];
            if rows.is_empty() {
                continue;
            }
            self.write_rows(queue, page, rows.min, rows.max);
            // 範囲を空へ戻す。
            self.dirty[page as usize] = DirtyRows::EMPTY;
        }
        false
    }

    /// ページ `page` の行範囲 [y0, y1) を CPU の写しから GPU の層へ送る。
    fn write_rows(&self, queue: &wgpu::Queue, page: u32, y0: u32, y1: u32) {
        let rows = y1 - y0;
        let row_bytes = self.atlas_size as usize * self.kind.bytes_per_texel();
        let offset = y0 as usize * row_bytes;
        queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &self.texture,
                mip_level: 0,
                // z = 配列の層（ページ）
                origin: wgpu::Origin3d { x: 0, y: y0, z: page },
                aspect: wgpu::TextureAspect::All,
            },
            &self.cpu_pages[page as usize][offset..offset + rows as usize * row_bytes],
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(row_bytes as u32),
                rows_per_image: Some(rows),
            },
            wgpu::Extent3d {
                width: self.atlas_size,
                height: rows,
                depth_or_array_layers: 1,
            },
        );
    }
}

/// 空のページの CPU の写し（0 = 距離場のいちばん外側＝何も描かない）。
fn blank_page(atlas_size: u32, kind: DistanceFieldKind) -> Vec<u8> {
    vec![0u8; (atlas_size * atlas_size) as usize * kind.bytes_per_texel()]
}

/// 層が `layers` 枚のアトラスのテクスチャ配列と、配列として読むビューを作る。
///
/// 層が 1 枚でもビューは `D2Array`（シェーダーは常に `texture_2d_array`）。
fn create_atlas_texture(
    device: &wgpu::Device,
    label: &'static str,
    atlas_size: u32,
    kind: DistanceFieldKind,
    layers: u32,
) -> (wgpu::Texture, wgpu::TextureView) {
    let texture = device.create_texture_tracked(&wgpu::TextureDescriptor {
        label: Some(label),
        size: wgpu::Extent3d {
            width: atlas_size,
            height: atlas_size,
            depth_or_array_layers: layers,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: kind.texture_format(),
        usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
        view_formats: &[],
    });
    let view = texture.create_view(&wgpu::TextureViewDescriptor {
        label: Some(label),
        dimension: Some(wgpu::TextureViewDimension::D2Array),
        array_layer_count: Some(layers),
        ..Default::default()
    });
    (texture, view)
}
