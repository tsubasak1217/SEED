// ============================================================
//  id_pass.rs — ワールド座標 + Actor ID 統合バッファ
//
//  テクスチャフォーマット: Rgba32Float
//    R: ワールド座標 X
//    G: ワールド座標 Y
//    B: ワールド座標 Z
//    A: bitcast<f32>(instance_id)  ※ 0 = 背景
//
//  ピッキング (A チャンネル) とD&Dワールド座標 (RGB) を1枚で共有する。
// ============================================================

use super::{
    gpu_resources::{GpuModel, InstancedModelBatch, NUM_LODS},
    pipeline::DrawPipelines,
};

// ピクセルあたりのバイト数 (Rgba32Float = 4 × 4bytes)
const BYTES_PER_PIXEL: usize = 16;
// wgpu の CopyTextureToBuffer で要求される bytes_per_row の最小アライメント
const ROW_ALIGNMENT: u64 = 256;

// ============================================================
//  WorldPosIdBuffer — Rgba32Float テクスチャ + CPU リードバックバッファ
// ============================================================

/// ワールド座標 (RGB) と インスタンスID (A) を格納する統合バッファ。
///
/// ID パスのピッキングと、ドラッグ&ドロップ時のワールド座標取得を
/// 1 テクスチャで兼ねる。
pub struct IdBuffer {
    pub texture:      wgpu::Texture,
    pub view:         wgpu::TextureView,
    pub readback_buf: wgpu::Buffer,
    pub width:        u32,
    pub height:       u32,
}

impl IdBuffer {
    pub fn new(device: &wgpu::Device, width: u32, height: u32) -> Self {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("World Pos ID Buffer Texture"),
            size: wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          wgpu::TextureFormat::Rgba32Float,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());

        // 1ピクセル = 16 bytes。bytes_per_row は 256 の倍数が必要なので 256 で確保。
        let readback_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label:              Some("World Pos ID Readback Buffer"),
            size:               ROW_ALIGNMENT,
            usage:              wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
            mapped_at_creation: false,
        });

        Self { texture, view, readback_buf, width, height }
    }

    /// GPU サブミット済みのコピーコマンド実行後に呼び出す。
    ///
    /// 戻り値は `(world_pos: Option<[f32; 3]>, instance_id: u32)`:
    ///   - `world_pos`: 背景ピクセル (id == 0) の場合は None
    ///   - `instance_id`: 0 = 背景、N+1 = インスタンス N
    pub fn read_pixel(&self, device: &wgpu::Device) -> (Option<[f32; 3]>, u32) {
        let buf_slice = self.readback_buf.slice(..);
        buf_slice.map_async(wgpu::MapMode::Read, |_| {});
        let _ = device.poll(wgpu::PollType::Wait);
        let data = buf_slice.get_mapped_range();

        // RGBA 各 4 bytes = 合計 16 bytes
        let x  = f32::from_ne_bytes(data[0..4].try_into().unwrap());
        let y  = f32::from_ne_bytes(data[4..8].try_into().unwrap());
        let z  = f32::from_ne_bytes(data[8..12].try_into().unwrap());
        // A チャンネルは bitcast<f32>(u32) で書き込まれているためビット変換で復元
        let id = u32::from_ne_bytes(data[12..16].try_into().unwrap());

        drop(data);
        self.readback_buf.unmap();

        let world_pos = if id != 0 { Some([x, y, z]) } else { None };
        (world_pos, id)
    }
}

// ============================================================
//  draw_id_pass — ID パスでモデルを描画
// ============================================================

/// ワールド座標 + ID バッファパスでメッシュを描画する。
///
/// メインレンダーパスの後に呼び出す（深度テストで同等の可視性を再現する）。
pub fn draw_id_pass<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    gpu_model:   &'pass GpuModel,
    batch:       &'pass InstancedModelBatch,
    camera_bg:   &'pass wgpu::BindGroup,
    pipelines:   &'pass DrawPipelines,
    id_base_bg:  &'pass wgpu::BindGroup,
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
                    render_pass.set_bind_group(3, &gpu_model.identity_joints_bg, &[]);
                }
                render_pass.set_bind_group(0, camera_bg, &[]);
                render_pass.set_bind_group(2, &batch.lod_id_bgs[lod], &[]);
                render_pass.set_bind_group(4, id_base_bg, &[]);
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
