// ============================================================
//  outline.rs — 選択インスタンスのオレンジアウトライン描画
// ============================================================

use super::{
    gpu_resources::{GpuModel, InstancedModelBatch},
    pipeline::DrawPipelines,
};

/// 選択インスタンスの前面を描画してステンシル=1 を書き込む（カラー出力なし）。
///
/// `draw_model_indirect` → `draw_stencil_mask` → `draw_outline` の順で呼び出す。
/// 選択インスタンスのみがステンシルを書くため、他のオブジェクトの背後でも
/// アウトラインが正しく表示される（他オブジェクトによる遮蔽は深度テストが担う）。
pub fn draw_stencil_mask<'pass>(
    render_pass:   &mut wgpu::RenderPass<'pass>,
    gpu_model:     &'pass GpuModel,
    batch:         &'pass InstancedModelBatch,
    camera_bg:     &'pass wgpu::BindGroup,
    pipelines:     &'pass DrawPipelines,
    selected_inst: u32,
) {
    let Some((lod, compact_idx)) = batch.find_compact_index(selected_inst) else { return };

    render_pass.set_stencil_reference(1);

    let joint_bg = batch.joint_vs_bg(lod);
    let mut cur_skinned: Option<bool> = None;

    for draw in &batch.node_prim_list {
        let Some((_, model_bg)) = batch.lod_node_data[lod][draw.node_idx].as_ref()
            else { continue };

        let gpu_mesh = &gpu_model.meshes[draw.mesh_idx];
        let prim     = &gpu_mesh.primitives[draw.prim_idx];

        if cur_skinned != Some(draw.is_skinned) {
            if draw.is_skinned {
                render_pass.set_pipeline(&pipelines.outline.skinned_stencil_pipeline);
                render_pass.set_bind_group(0, camera_bg, &[]);
                render_pass.set_bind_group(1, model_bg, &[]);
                let jbg = joint_bg.unwrap_or(&gpu_model.identity_joints_bg);
                render_pass.set_bind_group(2, jbg, &[]);
            } else {
                render_pass.set_pipeline(&pipelines.outline.mesh_stencil_pipeline);
                render_pass.set_bind_group(0, camera_bg, &[]);
                render_pass.set_bind_group(1, model_bg, &[]);
            }
            cur_skinned = Some(draw.is_skinned);
        } else {
            render_pass.set_bind_group(1, model_bg, &[]);
        }

        render_pass.set_vertex_buffer(0, prim.vertex_buffer.slice(..));
        if draw.is_skinned {
            render_pass.set_vertex_buffer(1, prim.skin_vertex_buffer.as_ref().unwrap().slice(..));
        }

        let (idx_buf, idx_count) = prim.get_lod_index_buffer(lod);
        render_pass.set_index_buffer(idx_buf.slice(..), wgpu::IndexFormat::Uint32);
        render_pass.draw_indexed(0..idx_count, 0, compact_idx..compact_idx + 1);
    }
}

/// 選択インスタンスをバックフェース膨張法でオレンジアウトライン描画する。
///
/// `draw_stencil_mask` の後に呼び出す。ステンシル Equal(0) により
/// 選択インスタンスの内側には描画しない。
pub fn draw_outline<'pass>(
    render_pass:   &mut wgpu::RenderPass<'pass>,
    gpu_model:     &'pass GpuModel,
    batch:         &'pass InstancedModelBatch,
    camera_bg:     &'pass wgpu::BindGroup,
    pipelines:     &'pass DrawPipelines,
    selected_inst: u32,
) {
    let Some((lod, compact_idx)) = batch.find_compact_index(selected_inst) else { return };

    // ② ステンシル参照値 0: draw_model_indirect が 1 を書いた箇所はスキップ
    render_pass.set_stencil_reference(0);

    let joint_bg = batch.joint_vs_bg(lod);
    let mut cur_skinned: Option<bool> = None;

    for draw in &batch.node_prim_list {
        let Some((_, model_bg)) = batch.lod_node_data[lod][draw.node_idx].as_ref()
            else { continue };

        let gpu_mesh = &gpu_model.meshes[draw.mesh_idx];
        let prim     = &gpu_mesh.primitives[draw.prim_idx];

        if cur_skinned != Some(draw.is_skinned) {
            if draw.is_skinned {
                render_pass.set_pipeline(&pipelines.outline.skinned_pipeline);
                render_pass.set_bind_group(0, camera_bg, &[]);
                render_pass.set_bind_group(1, model_bg, &[]);
                let jbg = joint_bg.unwrap_or(&gpu_model.identity_joints_bg);
                render_pass.set_bind_group(2, jbg, &[]);
            } else {
                render_pass.set_pipeline(&pipelines.outline.mesh_pipeline);
                render_pass.set_bind_group(0, camera_bg, &[]);
                render_pass.set_bind_group(1, model_bg, &[]);
            }
            cur_skinned = Some(draw.is_skinned);
        } else {
            render_pass.set_bind_group(1, model_bg, &[]);
        }

        // slot 0: 通常頂点バッファ (position 等)
        render_pass.set_vertex_buffer(0, prim.vertex_buffer.slice(..));
        if draw.is_skinned {
            // slot 1: スキン頂点、slot 2: ① スムーズ法線
            render_pass.set_vertex_buffer(1, prim.skin_vertex_buffer.as_ref().unwrap().slice(..));
            render_pass.set_vertex_buffer(2, prim.smooth_normal_buffer.slice(..));
        } else {
            // slot 1: ① スムーズ法線
            render_pass.set_vertex_buffer(1, prim.smooth_normal_buffer.slice(..));
        }

        let (idx_buf, idx_count) = prim.get_lod_index_buffer(lod);
        render_pass.set_index_buffer(idx_buf.slice(..), wgpu::IndexFormat::Uint32);
        render_pass.draw_indexed(0..idx_count, 0, compact_idx..compact_idx + 1);
    }
}
