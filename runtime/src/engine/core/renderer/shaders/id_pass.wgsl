// ============================================================
//  id_pass.wgsl — ワールド座標 + Actor ID 統合バッファパス
//
//  各フラグメントに以下を書き込む:
//    RGB: ワールド座標 (X, Y, Z)
//    A:   bitcast<f32>(inst_id + u_id_base + 1u)  ※ 0 = 背景
//
//  ドラッグ&ドロップ時のワールド座標取得と、
//  ピッキング用インスタンスIDを1パスで取得する。
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
/// 複数モデル描画時の ID ベースオフセット（単一モデル時は 0）
@group(4) @binding(0) var<uniform>       u_id_base:   u32;

const MAX_JOINTS: u32 = 128u;
@group(3) @binding(0) var<storage, read> joint_matrices: array<mat4x4<f32>>;

struct VsOut {
    @builtin(position)              clip_pos:  vec4<f32>,
    @location(0)                    world_pos: vec3<f32>,
    @location(1) @interpolate(flat) inst_id:   u32,
}

// ── 非スキンメッシュ ─────────────────────────────────────────

@vertex
fn vs_mesh(
    @location(0)             position: vec3<f32>,
    @builtin(instance_index) inst_idx: u32,
) -> VsOut {
    let world    = u_instances[inst_idx].model * vec4<f32>(position, 1.0);
    let clip     = u_camera.view_proj * world;
    let raw_id   = u_ids[inst_idx] + u_id_base + 1u;
    return VsOut(clip, world.xyz, raw_id);
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
    let local  = skin * vec4<f32>(position, 1.0);
    let world  = u_instances[inst_idx].model * local;
    let clip   = u_camera.view_proj * world;
    let raw_id = u_ids[inst_idx] + u_id_base + 1u;
    return VsOut(clip, world.xyz, raw_id);
}

// ── フラグメントシェーダー ────────────────────────────────────

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    // A チャンネルに ID のビットパターンを float として書き込む
    return vec4<f32>(in.world_pos, bitcast<f32>(in.inst_id));
}
