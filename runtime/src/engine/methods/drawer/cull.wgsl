// ============================================================
// cull.wgsl  —  GPU 視錐台カリング コンピュートシェーダ
//
// 各インスタンスのワールド空間 AABB を視錐台 6 平面でテストし、
// カリングされたインスタンスは index_count=0 のコマンドを書き込む。
// 可視インスタンスは実際の index_count を書き込む。
// atomic カウンタは使用しない（DX12 バリア問題を回避するため）。
//
// Group 0:
//   0 = cull_data        (per-instance AABB, storage read)
//   1 = u_frustum        (6 平面, uniform)
//   2 = draw_cmds        (DrawIndexedIndirect × N_prims × MAX_INST, storage rw)
//   3 = prim_index_counts(プリミティブごとのインデックス数, storage read)
// ============================================================

struct GpuCullData {
    aabb_min: vec3<f32>,
    _pad0:    f32,
    aabb_max: vec3<f32>,
    _pad1:    f32,
}

// draw_indexed_indirect の引数レイアウト（wgpu 仕様準拠）
struct DrawIndexedIndirect {
    index_count:    u32,
    instance_count: u32,  // 常に 1（1 コマンド = 1 インスタンス）
    first_index:    u32,  // 常に 0（プリミティブごとに専用バッファを持つため）
    base_vertex:    i32,  // 常に 0
    first_instance: u32,  // ← 実インスタンスインデックス（頂点シェーダが使用）
}

struct FrustumUniform {
    planes: array<vec4<f32>, 6>,
}

@group(0) @binding(0) var<storage, read>       cull_data:         array<GpuCullData>;
@group(0) @binding(1) var<uniform>             u_frustum:         FrustumUniform;
@group(0) @binding(2) var<storage, read_write> draw_cmds:         array<DrawIndexedIndirect>;
@group(0) @binding(3) var<storage, read>       prim_index_counts: array<u32>;

// ── メイン ────────────────────────────────────────────────────

@compute @workgroup_size(64)
fn cs_main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let inst_idx = gid.x;
    let n_inst   = arrayLength(&cull_data);
    let n_prims  = arrayLength(&prim_index_counts);

    if inst_idx >= n_inst { return; }

    let d       = cull_data[inst_idx];
    let visible = frustum_test(d.aabb_min, d.aabb_max);

    // 全プリミティブ分のコマンドを書き込む（インスタンス固有スロットに直接書く）
    // カリングされた場合は index_count=0 にして GPU が何も描かないようにする。
    // draw_cmds レイアウト: [prim0_inst0, prim0_inst1, ..., prim1_inst0, ...]
    // プリミティブ p、インスタンス inst_idx のスロット = p * n_inst + inst_idx
    for (var p = 0u; p < n_prims; p++) {
        let idx_count = select(0u, prim_index_counts[p], visible);
        draw_cmds[p * n_inst + inst_idx] = DrawIndexedIndirect(
            idx_count,
            1u,       // instance_count = 1
            0u,       // first_index = 0
            0i,       // base_vertex = 0
            inst_idx, // first_instance → @builtin(instance_index) として渡る
        );
    }
}

// ── 視錐台 AABB テスト ────────────────────────────────────────

/// AABB が視錐台と交差するか判定する。
/// 各平面に対して「正頂点」（平面法線に最も近い頂点）を使う保守的なテスト。
fn frustum_test(aabb_min: vec3<f32>, aabb_max: vec3<f32>) -> bool {
    for (var i = 0u; i < 6u; i++) {
        let p  = u_frustum.planes[i];
        // 正頂点: 各軸で平面法線と同符号の端点を選ぶ
        let px = select(aabb_min.x, aabb_max.x, p.x >= 0.0);
        let py = select(aabb_min.y, aabb_max.y, p.y >= 0.0);
        let pz = select(aabb_min.z, aabb_max.z, p.z >= 0.0);
        // 正頂点が平面の外側 → AABB 全体が外側
        if dot(p.xyz, vec3(px, py, pz)) + p.w < 0.0 { return false; }
    }
    return true;
}
