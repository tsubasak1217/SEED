// ============================================================
// shader_skinned_vertex.wgsl  —  スキンメッシュ頂点シェーダ
//
// Group 3: ジョイント行列ストレージバッファ（全インスタンス分）
//   joint_matrices[inst_idx * MAX_JOINTS + joint_idx] で参照。
//   コンピュートシェーダが毎フレーム書き込む。
// ============================================================

const MAX_JOINTS: u32 = 128u;

/// ジョイント行列（列優先、コンパクト順）
@group(3) @binding(0) var<storage, read> joint_matrices: array<mat4x4<f32>>;

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal:   vec3<f32>,
    @location(2) tangent:  vec4<f32>,
    @location(3) uv0:      vec2<f32>,
    @location(4) uv1:      vec2<f32>,
    @location(5) color:    vec4<f32>,
    // スキニング
    @location(6) joints:  vec4<u32>,
    @location(7) weights: vec4<f32>,
}

@vertex
fn vs_main(v: VertexInput, @builtin(instance_index) inst_idx: u32) -> VertexOutput {
    let u_model = u_instances[inst_idx];

    // このインスタンスのジョイント行列基点
    let base = inst_idx * MAX_JOINTS;
    let j = vec4<u32>(
        min(v.joints.x, MAX_JOINTS - 1u),
        min(v.joints.y, MAX_JOINTS - 1u),
        min(v.joints.z, MAX_JOINTS - 1u),
        min(v.joints.w, MAX_JOINTS - 1u),
    );

    // ブレンドスキニング行列（列優先）
    let skin =
        v.weights.x * joint_matrices[base + j.x] +
        v.weights.y * joint_matrices[base + j.y] +
        v.weights.z * joint_matrices[base + j.z] +
        v.weights.w * joint_matrices[base + j.w];

    let world_pos4 = u_model.model * (skin * vec4<f32>(v.position,   1.0));
    let nm         = u_model.normal_matrix;
    let wn         = normalize((nm * (skin * vec4<f32>(v.normal,      0.0))).xyz);
    let wt         = normalize((nm * (skin * vec4<f32>(v.tangent.xyz, 0.0))).xyz);
    let wbt        = normalize(cross(wn, wt) * v.tangent.w);

    var out: VertexOutput;
    let clip         = u_camera.view_proj * world_pos4;
    out.clip_pos     = clip;
    // 速度用クリップ座標: フォワード経路は速度 MRT を持たないため prev = curr（速度 0）を入れる。
    // 実際の前フレーム再投影は G-Buffer 専用の gbuffer_skinned_vertex.wgsl が行う。
    out.curr_clip    = clip;
    out.prev_clip    = clip;
    out.world_pos    = world_pos4.xyz;
    out.world_normal = wn;
    out.world_tan    = wt;
    out.world_bitan  = wbt;
    out.uv0          = v.uv0;
    out.uv1          = v.uv1;
    out.color        = v.color;
    // アクタ単位のセマンティックタグ（インスタンス拡張スロット＝normal_matrix の 4 列目）。
    // スキンの場合も法線変換は `normal_matrix * (skin * vec4(n, 0.0))` の形であり、
    // `skin` はアフィン行列（アップロード規約上 col_i.w = 0 for i<3）なので内側の結果も
    // 必ず w=0 になる。よって normal_matrix の 4 列目（拡張スロット）は結果に寄与せず、
    // タグを載せてもスキン法線は 1 ビットも変わらない。
    out.render_tag   = instance_render_tag(u_model);
    return out;
}
