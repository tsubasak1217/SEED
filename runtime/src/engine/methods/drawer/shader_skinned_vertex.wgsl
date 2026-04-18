// ============================================================
// shader_skinned_vertex.wgsl  —  スキンメッシュ頂点シェーダ
//
// Group 3 にジョイント行列（最大 128 本）を追加。
// Vertex 属性 location 0-5 は static と同じ、6-7 がスキニング用。
// ============================================================

struct JointUniform {
    matrices: array<mat4x4<f32>, 128>,
}
@group(3) @binding(0) var<uniform> u_joints: JointUniform;

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal:   vec3<f32>,
    @location(2) tangent:  vec4<f32>,
    @location(3) uv0:      vec2<f32>,
    @location(4) uv1:      vec2<f32>,
    @location(5) color:    vec4<f32>,
    // スキニング
    @location(6) joints:  vec4<u32>,   // Uint16x4 → vec4<u32>
    @location(7) weights: vec4<f32>,
}

@vertex
fn vs_main(v: VertexInput, @builtin(instance_index) inst_idx: u32) -> VertexOutput {
    // inst_idx は DrawIndexedIndirect.first_instance 経由で実インスタンス番号を直接受け取る。
    let u_model = u_instances[inst_idx];
    // インデックスを 127 以下にクランプして境界外アクセスを防ぐ
    let j = vec4<u32>(
        min(v.joints.x, 127u),
        min(v.joints.y, 127u),
        min(v.joints.z, 127u),
        min(v.joints.w, 127u),
    );

    // ブレンドスキニング行列
    let skin =
        v.weights.x * u_joints.matrices[j.x] +
        v.weights.y * u_joints.matrices[j.y] +
        v.weights.z * u_joints.matrices[j.z] +
        v.weights.w * u_joints.matrices[j.w];

    let world_pos4 = u_model.model * (skin * vec4<f32>(v.position,   1.0));
    let nm         = u_model.normal_matrix;
    let wn         = normalize((nm * (skin * vec4<f32>(v.normal,      0.0))).xyz);
    let wt         = normalize((nm * (skin * vec4<f32>(v.tangent.xyz, 0.0))).xyz);
    let wbt        = normalize(cross(wn, wt) * v.tangent.w);

    var out: VertexOutput;
    out.clip_pos     = u_camera.view_proj * world_pos4;
    out.world_pos    = world_pos4.xyz;
    out.world_normal = wn;
    out.world_tan    = wt;
    out.world_bitan  = wbt;
    out.uv0          = v.uv0;
    out.uv1          = v.uv1;
    out.color        = v.color;
    return out;
}
