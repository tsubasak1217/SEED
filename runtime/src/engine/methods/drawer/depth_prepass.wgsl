// ============================================================
//  depth_prepass.wgsl — 深度プリパス専用頂点シェーダー
//  フラグメントシェーダーなし。深度バッファのみ書き込む。
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
const MAX_JOINTS: u32 = 128u;

@group(0) @binding(0) var<uniform>       u_camera:       CameraUniform;
@group(1) @binding(0) var<storage, read> u_instances:    array<ModelUniform>;
@group(3) @binding(0) var<storage, read> joint_matrices: array<mat4x4<f32>>;

// ── 非スキンメッシュ ─────────────────────────────────────────

struct MeshIn {
    @location(0) position:  vec3<f32>,
    @builtin(instance_index) inst_idx: u32,
}

@vertex
fn vs_mesh(in: MeshIn) -> @builtin(position) vec4<f32> {
    let world = u_instances[in.inst_idx].model * vec4<f32>(in.position, 1.0);
    return u_camera.view_proj * world;
}

// ── スキンメッシュ ───────────────────────────────────────────

struct SkinnedIn {
    @location(0) position:  vec3<f32>,
    @location(6) joints:    vec4<u32>,
    @location(7) weights:   vec4<f32>,
    @builtin(instance_index) inst_idx: u32,
}

@vertex
fn vs_skinned(in: SkinnedIn) -> @builtin(position) vec4<f32> {
    let base = in.inst_idx * MAX_JOINTS;
    let j    = in.joints;
    let w    = in.weights;
    let skin_mat =
        w.x * joint_matrices[base + min(j.x, MAX_JOINTS - 1u)] +
        w.y * joint_matrices[base + min(j.y, MAX_JOINTS - 1u)] +
        w.z * joint_matrices[base + min(j.z, MAX_JOINTS - 1u)] +
        w.w * joint_matrices[base + min(j.w, MAX_JOINTS - 1u)];
    let local = skin_mat * vec4<f32>(in.position, 1.0);
    let world = u_instances[in.inst_idx].model * local;
    return u_camera.view_proj * world;
}
