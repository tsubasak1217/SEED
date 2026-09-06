// ============================================================
//  font/atlas.rs — グリフアトラス（サイズ非依存 SDF・動的シェルフパッキング）
//
//  【役割】
//  初回描画時にグリフを `SDF_EM_PX` の固定サイズで距離場化して CPU バッファへ置き、
//  `upload_if_dirty()` で R8Unorm GPU テクスチャへ転送する。
//
//  【サイズ非依存】
//  キーにフォントサイズを持たない。1 グリフ = 1 エントリで、描画時に
//  em 単位のメトリクスへフォントサイズを掛けて任意サイズへ拡大縮小する。
//  （旧実装はサイズごとに焼き直していたため、同じ文字がサイズ数だけ場所を食っていた）
//
//  【容量の目安】
//  em 64 + 四方 8px パディングで 1 グリフ ≒ 80x80px。
//  4096 アトラスなら 51 列 × 51 行 ≒ 2500 グリフ入る（日本語 HUD には十分）。
//
//  【あふれた場合】
//  追い出し（eviction）は行わず、そのグリフを描画しない。
//  無言だと原因不明の「字が出ない」になるため、最初の 1 回だけ警告を出す。
// ============================================================

use std::collections::HashMap;

use super::rasterizer::GlyphSdf;
use super::sdf::SDF_SPREAD_EM;

/// グリフ間のパディング（ピクセル）。
/// 隣のグリフのにじみ（バイリニア補間）を拾わないための隙間。
const ATLAS_PADDING: u32 = 2;

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
/// `tight_*` はスプレッドのパディングを取り除いた「文字の実寸」で、
/// 外接矩形の実測（操作ガイドのプレートなど）に使う。
#[derive(Clone, Copy, Debug)]
pub struct GlyphInfo {
    /// アトラス UV の左上・右下 [0, 1]
    pub uv_min: [f32; 2],
    pub uv_max: [f32; 2],
    /// クアッドサイズ（em 単位、スプレッドのパディング込み）
    pub size_em: [f32; 2],
    /// ペン基点からクアッド左上へのオフセット（em 単位、Y 下向き）
    pub bearing_em: [f32; 2],
    /// 水平アドバンス幅（em 単位）
    pub advance_em: f32,
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

    /// スプレッドのパディングを除いた、文字そのものの左上オフセット（px）。
    #[inline]
    pub fn tight_bearing_px(&self, font_size: f32) -> [f32; 2] {
        let pad = SDF_SPREAD_EM * font_size;
        [
            self.bearing_em[0] * font_size + pad,
            self.bearing_em[1] * font_size + pad,
        ]
    }

    /// スプレッドのパディングを除いた、文字そのもののサイズ（px）。
    #[inline]
    pub fn tight_size_px(&self, font_size: f32) -> [f32; 2] {
        let pad2 = 2.0 * SDF_SPREAD_EM * font_size;
        [
            self.size_em[0] * font_size - pad2,
            self.size_em[1] * font_size - pad2,
        ]
    }
}

// ── Shelf ─────────────────────────────────────────────────────

/// シェルフパッキングの1段。左から順にグリフを詰め、高さが足りなくなったら新しい段を作る。
struct Shelf {
    y: u32,      // シェルフの Y 開始位置
    height: u32, // シェルフの高さ（最大グリフ高さ + パディング）
    cursor: u32, // 現在の X 書き込み位置
}

// ── GlyphAtlas ────────────────────────────────────────────────

/// 動的シェルフパッキングによるグリフアトラス。
///
/// グリフを CPU バッファへ距離場としてキャッシュし、`upload_if_dirty` で
/// R8Unorm の GPU テクスチャへ **更新のあった行だけ** 転送する。
pub struct GlyphAtlas {
    pub texture: wgpu::Texture,
    pub texture_view: wgpu::TextureView,
    pub atlas_size: u32,

    cpu_data: Vec<u8>,                    // CPU 側の R8 バッファ
    glyphs: HashMap<GlyphKey, GlyphInfo>, // キャッシュ
    shelves: Vec<Shelf>,

    /// 未アップロードの行範囲 [dirty_y_min, dirty_y_max)。空なら min >= max。
    dirty_y_min: u32,
    dirty_y_max: u32,
    /// あふれ警告を出したか（毎フレーム出さないための 1 回きりフラグ）。
    overflow_warned: bool,
}

impl GlyphAtlas {
    pub fn new(device: &wgpu::Device, atlas_size: u32) -> Self {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Glyph Atlas"),
            size: wgpu::Extent3d {
                width: atlas_size,
                height: atlas_size,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::R8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        let texture_view = texture.create_view(&wgpu::TextureViewDescriptor::default());

        Self {
            texture,
            texture_view,
            atlas_size,
            cpu_data: vec![0u8; (atlas_size * atlas_size) as usize],
            glyphs: HashMap::new(),
            shelves: Vec::new(),
            // 空のダーティ範囲（min >= max）で開始する。
            dirty_y_min: u32::MAX,
            dirty_y_max: 0,
            overflow_warned: false,
        }
    }

    /// キャッシュ済みグリフ情報を返す。
    #[inline]
    pub fn get(&self, key: &GlyphKey) -> Option<&GlyphInfo> {
        self.glyphs.get(key)
    }

    /// 焼き上がった SDF グリフをアトラスに追加する。
    ///
    /// 成功時は `GlyphInfo` を返す（既に存在する場合も返す）。
    /// アトラスが満杯の場合は `None`（そのグリフは描画されない）。
    pub fn insert(&mut self, key: GlyphKey, glyph: &GlyphSdf) -> Option<GlyphInfo> {
        if let Some(info) = self.glyphs.get(&key) {
            return Some(*info);
        }

        let (width, height) = (glyph.width, glyph.height);
        let Some((sx, sy)) = self.alloc_shelf(width + ATLAS_PADDING, height + ATLAS_PADDING) else {
            // あふれた: 追い出しはしない。原因追跡できるよう最初の 1 回だけ警告する。
            if !self.overflow_warned {
                self.overflow_warned = true;
                eprintln!(
                    "[SEED FONT] グリフアトラス({}x{}) が満杯です。以降の新規グリフは描画されません。",
                    self.atlas_size, self.atlas_size
                );
            }
            return None;
        };

        // CPU バッファへ距離場をコピー
        let atlas_w = self.atlas_size as usize;
        for row in 0..height as usize {
            let src = &glyph.data[row * width as usize..(row + 1) * width as usize];
            let dst_off = (sy as usize + row) * atlas_w + sx as usize;
            self.cpu_data[dst_off..dst_off + width as usize].copy_from_slice(src);
        }

        // 書き込んだ行範囲をダーティに積む（アップロードはこの範囲だけ）。
        self.dirty_y_min = self.dirty_y_min.min(sy);
        self.dirty_y_max = self.dirty_y_max.max(sy + height);

        let inv = 1.0 / self.atlas_size as f32;
        let info = GlyphInfo {
            uv_min: [sx as f32 * inv, sy as f32 * inv],
            uv_max: [(sx + width) as f32 * inv, (sy + height) as f32 * inv],
            size_em: glyph.size_em,
            bearing_em: glyph.bearing_em,
            advance_em: glyph.advance_em,
        };
        self.glyphs.insert(key, info);
        Some(info)
    }

    /// ダーティな行範囲だけを GPU テクスチャへアップロードする。
    ///
    /// 全域転送（4096x4096 = 16MB）を毎回やると新規グリフ 1 文字でフレームが落ちるため、
    /// 実際に書き込んだ行だけを送る。
    pub fn upload_if_dirty(&mut self, queue: &wgpu::Queue) {
        // 空のダーティ範囲なら何もしない。
        if self.dirty_y_min >= self.dirty_y_max {
            return;
        }
        let y0 = self.dirty_y_min;
        let rows = self.dirty_y_max - y0;
        let row_bytes = self.atlas_size as usize;
        let offset = y0 as usize * row_bytes;

        queue.write_texture(
            wgpu::ImageCopyTexture {
                texture: &self.texture,
                mip_level: 0,
                origin: wgpu::Origin3d { x: 0, y: y0, z: 0 },
                aspect: wgpu::TextureAspect::All,
            },
            &self.cpu_data[offset..offset + rows as usize * row_bytes],
            wgpu::ImageDataLayout {
                offset: 0,
                bytes_per_row: Some(self.atlas_size),
                rows_per_image: Some(rows),
            },
            wgpu::Extent3d {
                width: self.atlas_size,
                height: rows,
                depth_or_array_layers: 1,
            },
        );

        // 範囲を空へ戻す。
        self.dirty_y_min = u32::MAX;
        self.dirty_y_max = 0;
    }

    // ── シェルフパッキング ────────────────────────────────────

    /// 指定サイズの矩形を配置できるシェルフを探し、(x, y) を返す。
    fn alloc_shelf(&mut self, w: u32, h: u32) -> Option<(u32, u32)> {
        let atlas_w = self.atlas_size;
        let atlas_h = self.atlas_size;

        // 既存シェルフに収まるか確認
        for shelf in &mut self.shelves {
            if shelf.height >= h && shelf.cursor + w <= atlas_w {
                let x = shelf.cursor;
                shelf.cursor += w;
                return Some((x, shelf.y));
            }
        }

        // 新しいシェルフを作る
        let new_y = self.shelves.last().map(|s| s.y + s.height).unwrap_or(0);
        if new_y + h > atlas_h {
            return None;
        }

        self.shelves.push(Shelf {
            y: new_y,
            height: h,
            cursor: w,
        });
        Some((0, new_y))
    }
}
