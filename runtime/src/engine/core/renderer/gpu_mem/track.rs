// ============================================================
//  gpu_mem/track.rs — `wgpu::Device` の拡張: 作った資源を記録する create_*_tracked
//
//  【使い方】
//  エンジンの GPU 資源の生成は、wgpu の `create_texture` などの代わりに次の名前で呼ぶ
//  （`use crate::engine::core::renderer::gpu_mem::GpuMemDeviceExt;` を書く）。
//      device.create_texture(&desc)                → device.create_texture_tracked(&desc)
//      device.create_buffer(&desc)                 → device.create_buffer_tracked(&desc)
//      device.create_buffer_init(&desc)            → device.create_buffer_init_tracked(&desc)
//      device.create_texture_with_data(q, &d, o, x) → device.create_texture_with_data_tracked(q, &d, o, x)
//  中身は wgpu をそのまま呼ぶだけで、計測が有効なとき（gpu_mem::enabled）だけ記録を足す
//  （無効なら旗を 1 つ読む分しか変わらない）。分類は呼び出し元のソースの場所（#[track_caller]）と
//  ラベルから決まるので、呼ぶ側は何も渡さなくてよい。
// ============================================================

use std::panic::Location;

use wgpu::util::DeviceExt;

use super::registry::{record_global, ResourceKind};
use super::size;

/// GPU 資源を作り、計測が有効なら記録する `wgpu::Device` の拡張。
pub trait GpuMemDeviceExt {
    /// `create_texture` ＋記録。
    #[track_caller]
    fn create_texture_tracked(&self, desc: &wgpu::TextureDescriptor<'_>) -> wgpu::Texture;

    /// `create_buffer` ＋記録。
    #[track_caller]
    fn create_buffer_tracked(&self, desc: &wgpu::BufferDescriptor<'_>) -> wgpu::Buffer;

    /// `create_buffer_init`（wgpu::util::DeviceExt）＋記録。
    #[track_caller]
    fn create_buffer_init_tracked(&self, desc: &wgpu::util::BufferInitDescriptor<'_>) -> wgpu::Buffer;

    /// `create_texture_with_data`（wgpu::util::DeviceExt）＋記録。
    #[track_caller]
    fn create_texture_with_data_tracked(
        &self,
        queue: &wgpu::Queue,
        desc: &wgpu::TextureDescriptor<'_>,
        order: wgpu::util::TextureDataOrder,
        data: &[u8],
    ) -> wgpu::Texture;
}

/// テクスチャを記録する（計測が有効なときだけ呼ぶ）。
fn record_texture(desc: &wgpu::TextureDescriptor<'_>, site: &'static Location<'static>) {
    record_global(
        ResourceKind::Texture,
        desc.label,
        size::describe_texture(desc),
        size::texture_bytes(desc),
        site.file(),
        site.line(),
    );
}

/// バッファを記録する（計測が有効なときだけ呼ぶ）。
fn record_buffer(
    label: Option<&str>,
    bytes: u64,
    usage: wgpu::BufferUsages,
    site: &'static Location<'static>,
) {
    record_global(
        ResourceKind::Buffer,
        label,
        size::describe_buffer(usage),
        bytes,
        site.file(),
        site.line(),
    );
}

impl GpuMemDeviceExt for wgpu::Device {
    #[track_caller]
    fn create_texture_tracked(&self, desc: &wgpu::TextureDescriptor<'_>) -> wgpu::Texture {
        let texture = self.create_texture(desc);
        if super::enabled() {
            record_texture(desc, Location::caller());
        }
        texture
    }

    #[track_caller]
    fn create_buffer_tracked(&self, desc: &wgpu::BufferDescriptor<'_>) -> wgpu::Buffer {
        let buffer = self.create_buffer(desc);
        if super::enabled() {
            record_buffer(desc.label, desc.size, desc.usage, Location::caller());
        }
        buffer
    }

    #[track_caller]
    fn create_buffer_init_tracked(&self, desc: &wgpu::util::BufferInitDescriptor<'_>) -> wgpu::Buffer {
        let buffer = self.create_buffer_init(desc);
        if super::enabled() {
            record_buffer(
                desc.label,
                size::buffer_init_bytes(desc.contents.len()),
                desc.usage,
                Location::caller(),
            );
        }
        buffer
    }

    #[track_caller]
    fn create_texture_with_data_tracked(
        &self,
        queue: &wgpu::Queue,
        desc: &wgpu::TextureDescriptor<'_>,
        order: wgpu::util::TextureDataOrder,
        data: &[u8],
    ) -> wgpu::Texture {
        let texture = self.create_texture_with_data(queue, desc, order, data);
        if super::enabled() {
            record_texture(desc, Location::caller());
        }
        texture
    }
}
