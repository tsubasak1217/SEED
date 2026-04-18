// ============================================================
// unlit.wgsl  —  アンライトシェーダ（デバッグ描画用）
//
// 頂点色をそのまま出力する。ライティング計算なし。
// ラインバッチ（AABB / スフィア / レイ等）のデバッグ描画に使用。
// ============================================================

// ─── バインドグループ ─────────────────────────────────────────

struct CameraUniform {
    view_proj:  mat4x4<f32>,
    view:       mat4x4<f32>,
    position:   vec3<f32>,
    _pad:       f32,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

struct ModelUniform {
    model:         mat4x4<f32>,
    normal_matrix: mat4x4<f32>,
}
@group(1) @binding(0) var<uniform> u_model: ModelUniform;

// ─── 頂点入出力 ───────────────────────────────────────────────

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) color:    vec4<f32>,
}

struct VertexOutput {
    @builtin(position) clip_pos: vec4<f32>,
    @location(0)       color:    vec4<f32>,
}

// ─── シェーダ ─────────────────────────────────────────────────

@vertex
fn vs_main(v: VertexInput) -> VertexOutput {
    var out: VertexOutput;
    out.clip_pos = u_camera.view_proj * u_model.model * vec4<f32>(v.position, 1.0);
    out.color    = v.color;
    return out;
}

@fragment
fn fs_main(in: VertexOutput) -> @location(0) vec4<f32> {
    return in.color;
}
