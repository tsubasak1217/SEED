// ============================================================
//  outline.rs — 選択インスタンスのオレンジアウトライン描画
// ============================================================

use super::{
    gpu_resources::{GpuModel, InstancedModelBatch},
    pipeline::DrawPipelines,
};

// ============================================================
//  内部ヘルパー
// ============================================================

/// ソート済みコンパクトインデックス列を連続範囲 (first, last_inclusive) にまとめる。
fn to_ranges(sorted: &[u32]) -> Vec<(u32, u32)> {
    let mut out = Vec::new();
    if sorted.is_empty() {
        return out;
    }
    let (mut s, mut e) = (sorted[0], sorted[0]);
    for &v in &sorted[1..] {
        if v == e + 1 {
            e = v;
        } else {
            out.push((s, e));
            s = v;
            e = v;
        }
    }
    out.push((s, e));
    out
}

/// LOD ごとの選択コンパクトインデックスを収集してソートする。
/// 戻り値: lod → sorted compact indices
fn collect_lod_compact(batch: &InstancedModelBatch, selected: &[u32]) -> Vec<Vec<u32>> {
    let n = batch.lod_compact_insts.len();
    let mut per_lod: Vec<Vec<u32>> = vec![Vec::new(); n];
    for &inst in selected {
        if let Some((lod, ci)) = batch.find_compact_index(inst) {
            per_lod[lod].push(ci);
        }
    }
    for v in &mut per_lod {
        v.sort_unstable();
    }
    per_lod
}

// ============================================================
//  複数選択対応の一括描画（メイン API）
// ============================================================

/// 選択インスタンス群の前面をまとめてステンシル=1 に書き込む。
///
/// LOD ごとにコンパクトインデックスを収集・ソートし、連続範囲を 1 draw call に束ねる。
/// draw call 数は O(LOD × node_prims × 連続範囲数) — N 個選択しても
/// 連続範囲が少ない場合は N に依存しない。
pub fn draw_stencil_mask_multi<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    gpu_model: &'pass GpuModel,
    batch: &'pass InstancedModelBatch,
    camera_bg: &'pass wgpu::BindGroup,
    pipelines: &'pass DrawPipelines,
    selected: &[u32],
) {
    let per_lod = collect_lod_compact(batch, selected);
    render_pass.set_stencil_reference(1);

    for (lod, indices) in per_lod.iter().enumerate() {
        if indices.is_empty() {
            continue;
        }
        let ranges = to_ranges(indices);
        let joint_bg = batch.joint_vs_bg(lod);
        let mut cur_skinned: Option<bool> = None;

        for draw in &batch.node_prim_list {
            let Some((_, model_bg)) = batch.lod_node_data[lod][draw.node_idx].as_ref() else {
                continue;
            };

            let prim = &gpu_model.meshes[draw.mesh_idx].primitives[draw.prim_idx];

            if cur_skinned != Some(draw.is_skinned) {
                if draw.is_skinned {
                    render_pass.set_pipeline(&pipelines.outline.skinned_stencil_pipeline);
                    render_pass.set_bind_group(0, camera_bg, &[]);
                    render_pass.set_bind_group(1, model_bg, &[]);
                    render_pass.set_bind_group(
                        2,
                        joint_bg.unwrap_or(&gpu_model.identity_joints_bg),
                        &[],
                    );
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
                render_pass
                    .set_vertex_buffer(1, prim.skin_vertex_buffer.as_ref().unwrap().slice(..));
            }

            let (idx_buf, idx_count) = prim.get_lod_index_buffer(lod);
            render_pass.set_index_buffer(idx_buf.slice(..), wgpu::IndexFormat::Uint32);

            for &(first, last) in &ranges {
                render_pass.draw_indexed(0..idx_count, 0, first..last + 1);
            }
        }
    }
}

/// 選択インスタンス群のアウトラインをまとめて描画する。
///
/// `draw_stencil_mask_multi` の後に呼び出す。ステンシル Equal(0) により
/// 内側を除いた合成シルエットのアウトラインのみ描画する。
pub fn draw_outline_multi<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    gpu_model: &'pass GpuModel,
    batch: &'pass InstancedModelBatch,
    camera_bg: &'pass wgpu::BindGroup,
    pipelines: &'pass DrawPipelines,
    selected: &[u32],
) {
    let per_lod = collect_lod_compact(batch, selected);
    render_pass.set_stencil_reference(0);

    for (lod, indices) in per_lod.iter().enumerate() {
        if indices.is_empty() {
            continue;
        }
        let ranges = to_ranges(indices);
        let joint_bg = batch.joint_vs_bg(lod);
        let mut cur_skinned: Option<bool> = None;

        for draw in &batch.node_prim_list {
            let Some((_, model_bg)) = batch.lod_node_data[lod][draw.node_idx].as_ref() else {
                continue;
            };

            let prim = &gpu_model.meshes[draw.mesh_idx].primitives[draw.prim_idx];

            if cur_skinned != Some(draw.is_skinned) {
                if draw.is_skinned {
                    render_pass.set_pipeline(&pipelines.outline.skinned_pipeline);
                    render_pass.set_bind_group(0, camera_bg, &[]);
                    render_pass.set_bind_group(1, model_bg, &[]);
                    render_pass.set_bind_group(
                        2,
                        joint_bg.unwrap_or(&gpu_model.identity_joints_bg),
                        &[],
                    );
                } else {
                    render_pass.set_pipeline(&pipelines.outline.mesh_pipeline);
                    render_pass.set_bind_group(0, camera_bg, &[]);
                    render_pass.set_bind_group(1, model_bg, &[]);
                }
                cur_skinned = Some(draw.is_skinned);
            } else {
                render_pass.set_bind_group(1, model_bg, &[]);
            }

            render_pass.set_vertex_buffer(0, prim.vertex_buffer.slice(..));
            if draw.is_skinned {
                render_pass
                    .set_vertex_buffer(1, prim.skin_vertex_buffer.as_ref().unwrap().slice(..));
                render_pass.set_vertex_buffer(2, prim.smooth_normal_buffer.slice(..));
            } else {
                render_pass.set_vertex_buffer(1, prim.smooth_normal_buffer.slice(..));
            }

            let (idx_buf, idx_count) = prim.get_lod_index_buffer(lod);
            render_pass.set_index_buffer(idx_buf.slice(..), wgpu::IndexFormat::Uint32);

            for &(first, last) in &ranges {
                render_pass.draw_indexed(0..idx_count, 0, first..last + 1);
            }
        }
    }
}

// ============================================================
//  単一選択版（後方互換のため残す）
// ============================================================

pub fn draw_stencil_mask<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    gpu_model: &'pass GpuModel,
    batch: &'pass InstancedModelBatch,
    camera_bg: &'pass wgpu::BindGroup,
    pipelines: &'pass DrawPipelines,
    selected_inst: u32,
) {
    draw_stencil_mask_multi(
        render_pass,
        gpu_model,
        batch,
        camera_bg,
        pipelines,
        &[selected_inst],
    );
}

pub fn draw_outline<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    gpu_model: &'pass GpuModel,
    batch: &'pass InstancedModelBatch,
    camera_bg: &'pass wgpu::BindGroup,
    pipelines: &'pass DrawPipelines,
    selected_inst: u32,
) {
    draw_outline_multi(
        render_pass,
        gpu_model,
        batch,
        camera_bg,
        pipelines,
        &[selected_inst],
    );
}
