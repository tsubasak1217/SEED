// ============================================================
//  model_drawer.rs — インスタンシング描画 (CPU LOD + 視錐台カリング)
//
//  lod_node_data[lod][node_idx] は各 LOD の可視インスタンス分の
//  ModelUniform 配列。lod_visible_counts[lod] がそのインスタンス数。
//  draw_indexed(0..idx_count, 0, 0..visible) で一括描画する。
// ============================================================

use super::{
    gpu_resources::{GpuModel, InstancedModelBatch, NUM_LODS},
    pipeline::{DrawPipelines, RtMeshPipelines},
};
use crate::engine::core::loader::model::CullFace;

// ============================================================
//  draw_model_indirect — LOD + 視錐台カリング描画
// ============================================================

/// 全 LOD を順に描画する。各 LOD の可視インスタンス数は `batch.lod_visible_counts` が保持。
///
/// `batch.node_prim_list` は `(is_skinned, material_idx)` でソート済みのため、
/// パイプライン・マテリアル切り替え回数を最小に抑える。
/// `rt_pipes` が Some のとき（RT 影オン）は mesh/skinned の RT バリアントパイプラインと
/// その group 3 空 BG を使う。この場合 `lights_bg` には RT 用複合 BindGroup（TLAS 含む）を
/// 渡すこと。None のときは従来パイプライン＋従来 group 4 BG を使う。
pub fn draw_model_indirect<'pass>(
    render_pass: &mut wgpu::RenderPass<'pass>,
    gpu_model:   &'pass GpuModel,
    batch:       &'pass InstancedModelBatch,
    camera_bg:   &'pass wgpu::BindGroup,
    lights_bg:   &'pass wgpu::BindGroup,
    pipelines:   &'pass DrawPipelines,
    rt_pipes:    Option<&'pass RtMeshPipelines>,
    // GPU メッシュレットカリング（第1弾）を LOD0 で使うか。true でも対象外プリミティブ
    // （スキン/メッシュレット無し/Blend/非アクティブスロット）は従来 draw_indexed へ自動フォールバック。
    // preview/gizmo 等の呼び出しは false を渡し、従来経路と完全一致（パリティ担保）。
    meshlet_cull: bool,
) {
    if batch.n_prims == 0 { return; }

    // RT 影オン時は RT バリアント、オフ/非対応時は従来パイプラインを選ぶ。
    // フラグメントは LightMeta.rt_shadows で RT/シャドウマップを分岐するため、
    // ここでの選択は「TLAS バインディングを持つレイアウトか否か」の違いに対応する。
    // 各々はさらにカリング面（Back/Front/None）の 3 バリアント配列で、
    // プリミティブのマテリアルの CullFace で添字を引く。
    let mesh_pipelines    = rt_pipes.map_or(&pipelines.mesh.pipelines,         |r| &r.mesh);
    let skinned_pipelines = rt_pipes.map_or(&pipelines.skinned_mesh.pipelines, |r| &r.skinned);
    let empty_bg3         = rt_pipes.map_or(&pipelines.mesh.empty_bg3,         |r| &r.empty_bg3);

    for lod in 0..NUM_LODS {
        let visible = batch.lod_visible_counts[lod];
        if visible == 0 { continue; }

        let mut cur_skinned: Option<bool>                   = None;
        // 現在バインド中のカリング面。node_prim_list は (is_skinned, cull_face, material_idx) 順に
        // ソート済みのため、同じカリング面が連続する区間では set_pipeline を省略できる。
        let mut cur_cull:    Option<CullFace>               = None;
        let mut cur_mat_ptr: Option<*const wgpu::BindGroup> = None;

        // LOD のジョイント BG（スキンなしの場合は None）
        let joint_bg = batch.joint_vs_bg(lod);

        for (draw_idx, draw) in batch.node_prim_list.iter().enumerate() {
            let Some((_, model_bg)) = batch.lod_node_data[lod][draw.node_idx].as_ref()
                else { continue };

            // 半透明（Blend）プリミティブは不透明パスでは描かず、透明パス
            // （transparency.rs）へ委ねる（Phase R5）。Opaque/Mask のみここで描画する。
            // 全 Opaque シーンでは Blend が存在しないため挙動は従来と完全一致。
            if gpu_model.primitive_alpha_mode(draw.material_idx)
                == crate::engine::core::loader::model::AlphaMode::Blend
            {
                continue;
            }

            let gpu_mesh = &gpu_model.meshes[draw.mesh_idx];
            let prim     = &gpu_mesh.primitives[draw.prim_idx];

            let mat_bg: &wgpu::BindGroup = draw.material_idx
                .and_then(|mi| gpu_model.materials.get(mi))
                .map(|m| &m.bind_group)
                .unwrap_or(&gpu_model.default_material.bind_group);

            // このプリミティブのマテリアルのカリング面（オーバーライド適用後の実効値）。
            let cull = gpu_model.primitive_cull_face(draw.material_idx);

            // ── パイプライン切り替え ──────────────────────────────
            // スキン有無・カリング面のどちらかが変わったときだけ set_pipeline する。
            // カリング面だけが変わる場合でもレイアウトは同一だが、group 0/3/4 の再設定は
            // 従来どおりまとめて行う（数回/フレームのコストであり、レイアウト無効化の
            // ルールを場合分けするより安全側に倒す）。
            if cur_skinned != Some(draw.is_skinned) || cur_cull != Some(cull) {
                if draw.is_skinned {
                    render_pass.set_pipeline(&skinned_pipelines[cull.index()]);
                    // GPU スキニング: コンピュートシェーダが書き込んだ joint BG を設定
                    if let Some(jbg) = joint_bg {
                        render_pass.set_bind_group(3, jbg, &[]);
                    } else {
                        render_pass.set_bind_group(3, &gpu_model.identity_joints_bg, &[]);
                    }
                } else {
                    render_pass.set_pipeline(&mesh_pipelines[cull.index()]);
                    // group 3: mesh パイプラインではライト（group 4）参照の都合で
                    // レイアウト上「空の gap グループ」になる。wgpu はレイアウトに
                    // 存在する全 group への BindGroup 設定を要求する（未設定のまま
                    // draw すると "expects a BindGroup to be set at index 3" の
                    // 検証エラー＝Sponza 等の非スキン glTF がクラッシュ）ため、
                    // 使い回しの空 BG を必ずセットする。
                    render_pass.set_bind_group(3, empty_bg3, &[]);
                }
                render_pass.set_bind_group(0, camera_bg, &[]);
                // group 4: ライト＋シャドウ複合（lights storage + メタ + CSM/スポット
                // 深度配列 + 比較サンプラー + シャドウ行列 UBO）。max_bind_groups=5 の
                // デバイス対応のため group 5 は新設せず group 4 へ同居させている。
                // パイプライン切り替えで group 3 のレイアウトが変わると group 4 も
                // 無効化されるため、camera と同様に切り替えのたびに再設定する。
                render_pass.set_bind_group(4, lights_bg, &[]);
                cur_skinned = Some(draw.is_skinned);
                cur_cull    = Some(cull);
                cur_mat_ptr = None;
            }

            // ── マテリアル bind group（group 2）────────────────────
            let mat_ptr = mat_bg as *const _;
            if cur_mat_ptr != Some(mat_ptr) {
                render_pass.set_bind_group(2, mat_bg, &[]);
                cur_mat_ptr = Some(mat_ptr);
            }

            // ── モデル行列 bind group（group 1）───────────────────
            render_pass.set_bind_group(1, model_bg, &[]);

            // ── 頂点バッファ ───────────────────────────────────────
            render_pass.set_vertex_buffer(0, prim.vertex_buffer.slice(..));
            if draw.is_skinned {
                render_pass.set_vertex_buffer(1, prim.skin_vertex_buffer.as_ref().unwrap().slice(..));
            }

            // ── メッシュレット間接描画（LOD0 のみ、対象プリミティブのみ）──────
            // 対象条件を満たすとき: 展開済みメッシュレットインデックスを張り、
            // カリング compute が詰めた DrawIndexedIndirect を multi_draw_indexed_indirect_count で描画。
            // instance_index は各コマンドの first_instance（= 可視インスタンス番号）から供給される。
            if meshlet_cull && lod == 0 && !draw.is_skinned {
                if let (Some(mi_buf), Some((cmd_buf, count_buf, capacity))) =
                    (prim.meshlet_index_buffer.as_ref(), batch.meshlet_draw(draw_idx))
                {
                    render_pass.set_index_buffer(mi_buf.slice(..), wgpu::IndexFormat::Uint32);
                    render_pass.multi_draw_indexed_indirect_count(
                        cmd_buf, 0, count_buf, 0, capacity,
                    );
                    continue;
                }
            }

            // ── 従来経路（CPU カリング済み draw_indexed）──────────────
            let (idx_buf, idx_count) = prim.get_lod_index_buffer(lod);
            render_pass.set_index_buffer(idx_buf.slice(..), wgpu::IndexFormat::Uint32);

            // lod_node_data[lod][node_idx] は visible 件分の行列
            // instance_index = 0..visible-1 → u_instances[instance_index]
            render_pass.draw_indexed(0..idx_count, 0, 0..visible);
        }
    }
}
