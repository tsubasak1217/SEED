// ============================================================
//  id_pass.rs — Actor 選択用 ID バッファ
// ============================================================

use super::{
    gpu_resources::{GpuModel, InstancedModelBatch, NUM_LODS},
    pipeline::DrawPipelines,
};

// ============================================================
//  IdBuffer — R32Uint テクスチャ + CPU リードバックバッファ
// ============================================================

pub struct IdBuffer {
    pub texture:     wgpu::Texture,
    pub view:        wgpu::TextureView,
    pub readback_buf: wgpu::Buffer,
    pub width:       u32,
    pub height:      u32,
}

impl IdBuffer {
    pub fn new(device: &wgpu::Device, width: u32, height: u32) -> Self {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ID Buffer Texture"),
            size: wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          wgpu::TextureFormat::R32Uint,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());

        // wgpu の CopyTextureToBuffer では bytes_per_row が 256 の倍数である必要がある。
        // R32Uint は 4 bytes/pixel なので 1 ピクセル分でも 256 bytes 確保する。
        let readback_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label:              Some("ID Readback Buffer"),
            size:               256,
            usage:              wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });

        Self { texture, view, readback_buf, width, height }
    }

    /// GPU にサブミット済みのコピーコマンド実行後に呼び出す。
    /// map_async + poll(Wait) でピクセル値を CPU に読み出す。
    /// 戻り値は u32 の生 ID（0 = 背景、N+1 = インスタンス N）。
    pub fn read_pixel(&self, device: &wgpu::Device) -> u32 {
        let buf_slice = self.readback_buf.slice(..);
        buf_slice.map_async(wgpu::MapMode::Read, |_| {});
        let _ = device.poll(wgpu::PollType::Wait);
        let data = buf_slice.get_mapped_range();
        let id = u32::from_ne_bytes(data[0..4].try_into().unwrap());
        drop(data);
        self.readback_buf.unmap();
        id
    }
}

// ============================================================
//  draw_id_pass — ID パスでモデルを描画
// ============================================================

/// ID バッファパスでメッシュを描画する。
///
/// メインレンダーパスの後に呼び出す（深度テストで同等の可視性を再現する）。
pub fn draw_id_pass<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    gpu_model:   &'pass GpuModel,
    batch:       &'pass InstancedModelBatch,
    camera_bg:   &'pass wgpu::BindGroup,
    pipelines:   &'pass DrawPipelines,
) {
    if batch.n_prims == 0 { return; }

    for lod in 0..NUM_LODS {
        let visible = batch.lod_visible_counts[lod];
        if visible == 0 { continue; }

        let joint_bg = batch.joint_vs_bg(lod);
        let mut cur_skinned: Option<bool> = None;

        for draw in &batch.node_prim_list {
            let Some((_, model_bg)) = batch.lod_node_data[lod][draw.node_idx].as_ref()
                else { continue };

            let gpu_mesh = &gpu_model.meshes[draw.mesh_idx];
            let prim     = &gpu_mesh.primitives[draw.prim_idx];

            if cur_skinned != Some(draw.is_skinned) {
                if draw.is_skinned {
                    render_pass.set_pipeline(&pipelines.id_pass.skinned_pipeline);
                    if let Some(jbg) = joint_bg {
                        render_pass.set_bind_group(3, jbg, &[]);
                    } else {
                        render_pass.set_bind_group(3, &gpu_model.identity_joints_bg, &[]);
                    }
                } else {
                    render_pass.set_pipeline(&pipelines.id_pass.mesh_pipeline);
                }
                render_pass.set_bind_group(0, camera_bg, &[]);
                render_pass.set_bind_group(2, &batch.lod_id_bgs[lod], &[]);
                cur_skinned = Some(draw.is_skinned);
            }

            render_pass.set_bind_group(1, model_bg, &[]);

            render_pass.set_vertex_buffer(0, prim.vertex_buffer.slice(..));
            if draw.is_skinned {
                render_pass.set_vertex_buffer(1, prim.skin_vertex_buffer.as_ref().unwrap().slice(..));
            }

            let (idx_buf, idx_count) = prim.get_lod_index_buffer(lod);
            render_pass.set_index_buffer(idx_buf.slice(..), wgpu::IndexFormat::Uint32);

            render_pass.draw_indexed(0..idx_count, 0, 0..visible);
        }
    }
}
