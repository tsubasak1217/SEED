// ============================================================
//  model_drawer.rs — インスタンシングモデル描画関数
// ============================================================

use crate::engine::core::loader::model::Model;
use super::{
    gpu_resources::{GpuModel, GpuMesh, GpuMaterial, InstancedModelBatch},
    pipeline::{DrawPipelines, MeshPipeline, SkinnedMeshPipeline},
};

// ============================================================
//  draw_model_instanced — シーングラフ全体をインスタンシング描画
// ============================================================

/// モデルのシーングラフをルートから再帰的にトラバースして
/// インスタンシング描画する。
///
/// # 引数
/// - `render_pass`  : アクティブなレンダーパス
/// - `gpu_model`    : GPU にアップロード済みのモデルリソース
/// - `model`        : CPU 側のモデルデータ（シーングラフ情報に使用）
/// - `batch`        : インスタンスバッファ（毎フレーム `update()` 済みのもの）
/// - `camera_bg`    : カメラユニフォームの bind group（group 0）
/// - `pipelines`    : 描画パイプライン一式
pub fn draw_model_instanced<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    gpu_model:   &'pass GpuModel,
    model:       &Model,
    batch:       &'pass InstancedModelBatch,
    camera_bg:   &'pass wgpu::BindGroup,
    pipelines:   &'pass DrawPipelines,
) {
    // 可視インスタンスが 0 のときは描画不要
    if batch.visible_count == 0 { return; }

    for &root in &model.root_nodes {
        draw_node(render_pass, gpu_model, model, root, batch, camera_bg, pipelines);
    }
}

fn draw_node<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    gpu_model:   &'pass GpuModel,
    model:       &Model,
    node_idx:    usize,
    batch:       &'pass InstancedModelBatch,
    camera_bg:   &'pass wgpu::BindGroup,
    pipelines:   &'pass DrawPipelines,
) {
    let node = &model.nodes[node_idx];

    if let Some(mesh_idx) = node.mesh_index {
        if let Some((_, model_bg)) = batch.node_data.get(node_idx).and_then(|x| x.as_ref()) {
            let gpu_mesh = &gpu_model.meshes[mesh_idx];
            draw_mesh_instanced(
                render_pass, gpu_mesh, &gpu_model.materials,
                &gpu_model.default_material, &gpu_model.identity_joints_bg,
                camera_bg, model_bg, pipelines, batch.visible_count,
            );
        }
    }

    for &child in &node.children {
        draw_node(render_pass, gpu_model, model, child, batch, camera_bg, pipelines);
    }
}

// ============================================================
//  draw_mesh_instanced — 単一メッシュをインスタンシング描画
// ============================================================

/// 単一メッシュ（全プリミティブ）を `num_instances` 本まとめて描画する。
///
/// ストレージバッファ（group 1）に全インスタンスのモデル行列が格納されており、
/// 頂点シェーダが `@builtin(instance_index)` でインデックスする。
pub fn draw_mesh_instanced<'pass>(
    render_pass:       &mut wgpu::RenderPass<'pass>,
    gpu_mesh:          &'pass GpuMesh,
    materials:         &'pass [GpuMaterial],
    default_mat:       &'pass GpuMaterial,
    identity_joints_bg: &'pass wgpu::BindGroup,
    camera_bg:         &'pass wgpu::BindGroup,
    model_bg:          &'pass wgpu::BindGroup,
    pipelines:         &'pass DrawPipelines,
    num_instances:     u32,
) {
    for prim in &gpu_mesh.primitives {
        let mat_bg = prim.material_index
            .and_then(|mi| materials.get(mi))
            .map(|m| &m.bind_group)
            .unwrap_or(&default_mat.bind_group);

        if prim.skin_vertex_buffer.is_some() {
            // ── スキンメッシュ ────────────────────────────────
            render_pass.set_pipeline(&pipelines.skinned_mesh.pipeline);
            render_pass.set_bind_group(0, camera_bg, &[]);
            render_pass.set_bind_group(1, model_bg,  &[]);
            render_pass.set_bind_group(2, mat_bg,    &[]);
            render_pass.set_bind_group(3, identity_joints_bg, &[]);
            render_pass.set_vertex_buffer(0, prim.vertex_buffer.slice(..));
            render_pass.set_vertex_buffer(
                1, prim.skin_vertex_buffer.as_ref().unwrap().slice(..));
        } else {
            // ── スタティックメッシュ ──────────────────────────
            render_pass.set_pipeline(&pipelines.mesh.pipeline);
            render_pass.set_bind_group(0, camera_bg, &[]);
            render_pass.set_bind_group(1, model_bg,  &[]);
            render_pass.set_bind_group(2, mat_bg,    &[]);
            render_pass.set_vertex_buffer(0, prim.vertex_buffer.slice(..));
        }

        render_pass.set_index_buffer(prim.index_buffer.slice(..), wgpu::IndexFormat::Uint32);
        // instance range = 0..visible_count（カリング後の可視インスタンス数）
        // 頂点シェーダ内で u_visible_list[instance_index] → 実インスタンス番号に変換する
        render_pass.draw_indexed(0..prim.index_count, 0, 0..num_instances);
    }
}
