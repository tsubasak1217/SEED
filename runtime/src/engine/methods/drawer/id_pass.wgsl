// ============================================================
//  id_pass.wgsl — Actor 選択用 ID バッファパス
//
//  各フラグメントに元のインスタンスインデックス + 1 を書き込む。
//  0 は背景（何も描かれていないピクセル）を意味する。
// ============================================================

struct CameraUniform {
    view_proj: mat4x4<f32>,
    view:      mat4x4<f32>,
    position:  vec3<f32>,
    _pad:      f32,
}
struct ModelUniform {
    model:         mat4x4<f32>,
    normal_matrix: mat4x4<f32>,
}

@group(0) @binding(0) var<uniform>       u_camera:    CameraUniform;
@group(1) @binding(0) var<storage, read> u_instances: array<ModelUniform>;
/// u_ids[compact_index] = 元のインスタンスインデックス（0-based）
@group(2) @binding(0) var<storage, read> u_ids:       array<u32>;

const MAX_JOINTS: u32 = 128u;
@group(3) @binding(0) var<storage, read> joint_matrices: array<mat4x4<f32>>;

struct VsOut {
    @builtin(position)              clip_pos: vec4<f32>,
    @location(0) @interpolate(flat) inst_id:  u32,
}

// ── 非スキンメッシュ ─────────────────────────────────────────

@vertex
fn vs_mesh(
    @location(0)             position: vec3<f32>,
    @builtin(instance_index) inst_idx: u32,
) -> VsOut {
    let world = u_instances[inst_idx].model * vec4<f32>(position, 1.0);
    let clip  = u_camera.view_proj * world;
    return VsOut(clip, u_ids[inst_idx] + 1u);
}

// ── スキンメッシュ ───────────────────────────────────────────

@vertex
fn vs_skinned(
    @location(0)             position: vec3<f32>,
    @location(6)             joints:   vec4<u32>,
    @location(7)             weights:  vec4<f32>,
    @builtin(instance_index) inst_idx: u32,
) -> VsOut {
    let base = inst_idx * MAX_JOINTS;
    let j = vec4<u32>(
        min(joints.x, MAX_JOINTS - 1u),
        min(joints.y, MAX_JOINTS - 1u),
        min(joints.z, MAX_JOINTS - 1u),
        min(joints.w, MAX_JOINTS - 1u),
    );
    let skin =
        weights.x * joint_matrices[base + j.x] +
        weights.y * joint_matrices[base + j.y] +
        weights.z * joint_matrices[base + j.z] +
        weights.w * joint_matrices[base + j.w];
    let local = skin * vec4<f32>(position, 1.0);
    let world = u_instances[inst_idx].model * local;
    let clip  = u_camera.view_proj * world;
    return VsOut(clip, u_ids[inst_idx] + 1u);
}

// ── フラグメントシェーダー ────────────────────────────────────

@fragment
fn fs_main(in: VsOut) -> @location(0) u32 {
    return in.inst_id;
}
