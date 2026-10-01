// ============================================================
//  gpu_mem/size.rs — GPU 資源の大きさ（バイト）の計算（純関数）
//
//  【何を数えるか】
//  テクスチャ … 各ミップの「ブロック数（圧縮形式は 4x4 などのブロック単位）× ブロック 1 つのバイト数
//               × 層（2D 配列の層・3D の奥行き。3D はミップごとに半分）」の和 × サンプル数。
//               深度・ステンシルの合成形式は wgpu がコピーの大きさを返さないので、下の表で補う。
//  バッファ   … 作った大きさそのもの（create_buffer_init は wgpu と同じく 4 バイト境界へ切り上げた中身の長さ）。
//  いずれも論理値で、ドライバの整列・タイルの余白・圧縮・アロケータの切り上げは含まない
//  （それらを含む実際の確保は heap_budget.rs の VK_EXT_memory_budget で見る）。
// ============================================================

/// create_buffer_init が中身の長さを切り上げる境界（wgpu の COPY_BUFFER_ALIGNMENT と同じ値）。
pub const BUFFER_INIT_ALIGNMENT: u64 = wgpu::COPY_BUFFER_ALIGNMENT;

/// 深度・ステンシルの形式の 1 画素のバイト数（wgpu の `block_copy_size(None)` が None を返す形式の補い）。
///
/// 実機の内部表現に合わせた見積り: Depth24Plus / Depth24PlusStencil8 は多くの GPU で 24+8 bit の 4 バイト
/// （D24 の無い GPU では 32bit の深度＋ステンシルになり 8 バイト近くになる）。Depth32FloatStencil8 は 4+1 を 8 に揃えた値。
pub fn depth_stencil_texel_bytes(format: wgpu::TextureFormat) -> Option<u32> {
    use wgpu::TextureFormat as F;
    match format {
        F::Stencil8 => Some(1),
        F::Depth16Unorm => Some(2),
        F::Depth24Plus | F::Depth24PlusStencil8 | F::Depth32Float => Some(4),
        F::Depth32FloatStencil8 => Some(8),
        _ => None,
    }
}

/// 形式のブロック 1 つのバイト数（圧縮形式はブロック、非圧縮は 1 画素）。分からない形式（多平面など）は None。
pub fn block_bytes(format: wgpu::TextureFormat) -> Option<u32> {
    format
        .block_copy_size(None)
        .or_else(|| depth_stencil_texel_bytes(format))
}

/// テクスチャの記述からバイト数を計算する（全ミップ・全層・全サンプル）。形式が分からなければ 0。
pub fn texture_bytes(desc: &wgpu::TextureDescriptor<'_>) -> u64 {
    let Some(bytes_per_block) = block_bytes(desc.format) else {
        return 0;
    };
    let (block_w, block_h) = desc.format.block_dimensions();
    let mut total: u64 = 0;
    for level in 0..desc.mip_level_count {
        let Some(extent) = desc.mip_level_size(level) else {
            break;
        };
        // ブロックの数（端の半端なブロックも 1 つと数える）。
        let blocks_x = u64::from(extent.width.div_ceil(block_w));
        let blocks_y = u64::from(extent.height.div_ceil(block_h));
        let layers = u64::from(extent.depth_or_array_layers);
        total += blocks_x * blocks_y * layers * u64::from(bytes_per_block);
    }
    total * u64::from(desc.sample_count.max(1))
}

/// create_buffer_init の中身の長さから、実際に作られるバッファの大きさを計算する（wgpu と同じ切り上げ）。
pub fn buffer_init_bytes(contents_len: usize) -> u64 {
    let len = contents_len as u64;
    len.div_ceil(BUFFER_INIT_ALIGNMENT) * BUFFER_INIT_ALIGNMENT
}

/// テクスチャの短い説明（"1080x2400 Rgba16Float"・層・ミップ・サンプルが 1 でなければ付ける）。
pub fn describe_texture(desc: &wgpu::TextureDescriptor<'_>) -> String {
    let size = desc.size;
    let mut text = format!("{}x{} {:?}", size.width, size.height, desc.format);
    if size.depth_or_array_layers > 1 {
        let what = if desc.dimension == wgpu::TextureDimension::D3 { "depth" } else { "layers" };
        text.push_str(&format!(" {what}={}", size.depth_or_array_layers));
    }
    if desc.mip_level_count > 1 {
        text.push_str(&format!(" mips={}", desc.mip_level_count));
    }
    if desc.sample_count > 1 {
        text.push_str(&format!(" msaa={}", desc.sample_count));
    }
    text
}

/// バッファの短い説明（用途の旗）。
pub fn describe_buffer(usage: wgpu::BufferUsages) -> String {
    format!("{usage:?}")
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 記述を短く作る補助。
    fn desc(
        w: u32,
        h: u32,
        layers: u32,
        mips: u32,
        dim: wgpu::TextureDimension,
        format: wgpu::TextureFormat,
    ) -> wgpu::TextureDescriptor<'static> {
        wgpu::TextureDescriptor {
            label: None,
            size: wgpu::Extent3d { width: w, height: h, depth_or_array_layers: layers },
            mip_level_count: mips,
            sample_count: 1,
            dimension: dim,
            format,
            usage: wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        }
    }

    /// 1080x2400 の Rgba16Float は 1 画素 8 バイト。
    #[test]
    fn plain_render_target() {
        let d = desc(1080, 2400, 1, 1, wgpu::TextureDimension::D2, wgpu::TextureFormat::Rgba16Float);
        assert_eq!(texture_bytes(&d), 1080 * 2400 * 8);
    }

    /// 深度・ステンシルの合成形式も数える（wgpu は None を返す）。
    #[test]
    fn depth_stencil_is_counted() {
        let d = desc(1080, 2400, 1, 1, wgpu::TextureDimension::D2, wgpu::TextureFormat::Depth24PlusStencil8);
        assert_eq!(texture_bytes(&d), 1080 * 2400 * 4);
    }

    /// 2D 配列は層の数を掛け、ミップは 1/4 ずつ足す（層はミップで減らない）。
    #[test]
    fn array_with_mips() {
        let d = desc(256, 256, 4, 3, wgpu::TextureDimension::D2, wgpu::TextureFormat::R8Unorm);
        let expected = (256 * 256 + 128 * 128 + 64 * 64) * 4;
        assert_eq!(texture_bytes(&d), expected);
    }

    /// 3D はミップごとに奥行きも半分になる。
    #[test]
    fn volume_mips_halve_depth() {
        let d = desc(8, 8, 8, 2, wgpu::TextureDimension::D3, wgpu::TextureFormat::Rgba8Unorm);
        assert_eq!(texture_bytes(&d), (8 * 8 * 8 + 4 * 4 * 4) * 4);
    }

    /// 圧縮形式はブロック単位（BC1 は 4x4 で 8 バイト。端の半端も 1 ブロック）。
    #[test]
    fn compressed_counts_blocks() {
        let d = desc(10, 10, 1, 1, wgpu::TextureDimension::D2, wgpu::TextureFormat::Bc1RgbaUnorm);
        assert_eq!(texture_bytes(&d), 3 * 3 * 8);
    }

    /// create_buffer_init は 4 バイト境界へ切り上げる（空は 0）。
    #[test]
    fn buffer_init_rounds_up() {
        assert_eq!(buffer_init_bytes(0), 0);
        assert_eq!(buffer_init_bytes(1), 4);
        assert_eq!(buffer_init_bytes(4), 4);
        assert_eq!(buffer_init_bytes(13), 16);
    }

    /// 説明は寸法と形式、1 でない層・ミップだけを書く。
    #[test]
    fn description_is_short() {
        let d = desc(64, 32, 1, 1, wgpu::TextureDimension::D2, wgpu::TextureFormat::Rgba8Unorm);
        assert_eq!(describe_texture(&d), "64x32 Rgba8Unorm");
        let d = desc(64, 32, 6, 2, wgpu::TextureDimension::D2, wgpu::TextureFormat::Rgba8Unorm);
        assert_eq!(describe_texture(&d), "64x32 Rgba8Unorm layers=6 mips=2");
    }
}
