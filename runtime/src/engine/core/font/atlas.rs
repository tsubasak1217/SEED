// ============================================================
//  font/atlas.rs — グリフアトラス（動的シェルフパッキング）
//
//  初回描画時にグリフを CPU バッファへラスタライズし、
//  upload_if_dirty() で R8Unorm GPU テクスチャへ転送する。
//  シェルフパッキングにより断片化を抑えながら高さの異なる
//  グリフを効率よく配置する。
// ============================================================

use std::collections::HashMap;

/// グリフ間のパディング（ピクセル）
const ATLAS_PADDING: u32 = 2;

// ── GlyphKey ──────────────────────────────────────────────────

/// グリフキャッシュのキー。
#[derive(Hash, PartialEq, Eq, Clone, Debug)]
pub struct GlyphKey {
    pub codepoint: char,
    pub font_size_px: u32, // f32 の切り捨て値
}

// ── GlyphInfo ─────────────────────────────────────────────────

/// アトラス内のグリフ情報。
#[derive(Clone, Copy, Debug)]
pub struct GlyphInfo {
    /// アトラス UV の左上・右下 [0, 1]
    pub uv_min: [f32; 2],
    pub uv_max: [f32; 2],
    /// グリフビットマップのピクセルサイズ
    pub size: [f32; 2],
    /// ペン基点からグリフビットマップ左上へのオフセット（スクリーン座標系、Y 下向き）
    pub bearing: [f32; 2],
    /// 水平アドバンス幅（ピクセル）
    pub advance: f32,
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
/// グリフを CPU バッファへラスタライズしてキャッシュし、`upload_if_dirty` で
/// R8Unorm の GPU テクスチャへまとめて転送する。
pub struct GlyphAtlas {
    pub texture: wgpu::Texture,
    pub texture_view: wgpu::TextureView,
    pub atlas_size: u32,

    cpu_data: Vec<u8>,                    // CPU 側の R8 バッファ
    glyphs: HashMap<GlyphKey, GlyphInfo>, // キャッシュ
    shelves: Vec<Shelf>,
    dirty: bool,
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
            dirty: false,
        }
    }

    /// キャッシュ済みグリフ情報を返す。
    #[inline]
    pub fn get(&self, key: &GlyphKey) -> Option<&GlyphInfo> {
        self.glyphs.get(key)
    }

    /// グリフビットマップをアトラスに追加する。
    ///
    /// 成功時は `GlyphInfo` を返す（既に存在する場合も返す）。
    /// アトラスが満杯の場合は `None`。
    pub fn insert(
        &mut self,
        key: GlyphKey,
        bitmap: &[u8],
        width: u32,
        height: u32,
        bearing: [f32; 2],
        advance: f32,
    ) -> Option<GlyphInfo> {
        if let Some(info) = self.glyphs.get(&key) {
            return Some(*info);
        }

        let (sx, sy) = self.alloc_shelf(width + ATLAS_PADDING, height + ATLAS_PADDING)?;

        // CPU バッファへビットマップをコピー
        let atlas_w = self.atlas_size as usize;
        for row in 0..height as usize {
            let src = &bitmap[row * width as usize..(row + 1) * width as usize];
            let dst_off = (sy as usize + row) * atlas_w + sx as usize;
            self.cpu_data[dst_off..dst_off + width as usize].copy_from_slice(src);
        }

        let inv = 1.0 / self.atlas_size as f32;
        let info = GlyphInfo {
            uv_min: [sx as f32 * inv, sy as f32 * inv],
            uv_max: [(sx + width) as f32 * inv, (sy + height) as f32 * inv],
            size: [width as f32, height as f32],
            bearing,
            advance,
        };
        self.glyphs.insert(key, info);
        self.dirty = true;
        Some(info)
    }

    /// ダーティなら GPU テクスチャへ全域アップロードする。
    pub fn upload_if_dirty(&mut self, queue: &wgpu::Queue) {
        if !self.dirty {
            return;
        }
        queue.write_texture(
            wgpu::ImageCopyTexture {
                texture: &self.texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            &self.cpu_data,
            wgpu::ImageDataLayout {
                offset: 0,
                bytes_per_row: Some(self.atlas_size),
                rows_per_image: Some(self.atlas_size),
            },
            wgpu::Extent3d {
                width: self.atlas_size,
                height: self.atlas_size,
                depth_or_array_layers: 1,
            },
        );
        self.dirty = false;
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
