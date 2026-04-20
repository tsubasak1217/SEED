// ============================================================
// gizmo_line.wgsl — スクリーン空間太線ギズモ
//
// 各セグメントを 2 三角形（クワッド）に展開する。
// GizmoVertex は (pos_a, t, pos_b, side, color) を持ち、
// 頂点シェーダがスクリーン空間の垂直方向オフセットを計算する。
// ============================================================

struct CameraUniform {
    view_proj:  mat4x4<f32>,
    view:       mat4x4<f32>,
    position:   vec3<f32>,
    _pad:       f32,
    resolution: vec2<f32>,
    _pad2:      vec2<f32>,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

struct ModelUniform {
    model:         mat4x4<f32>,
    normal_matrix: mat4x4<f32>,
}
@group(1) @binding(0) var<uniform> u_model: ModelUniform;

struct VertexInput {
    @location(0) pos_a: vec3<f32>,
    @location(1) t:     f32,        // 0.0 = pos_a 側, 1.0 = pos_b 側
    @location(2) pos_b: vec3<f32>,
    @location(3) side:  f32,        // -1.0 または +1.0
    @location(4) color: vec4<f32>,
}

struct VertexOutput {
    @builtin(position) clip_pos: vec4<f32>,
    @location(0)       color:    vec4<f32>,
}

const THICKNESS_PX: f32 = 3.33;

@vertex
fn vs_main(v: VertexInput) -> VertexOutput {
    let m = u_model.model;
    let vp = u_camera.view_proj;

    let clip_a = vp * m * vec4<f32>(v.pos_a, 1.0);
    let clip_b = vp * m * vec4<f32>(v.pos_b, 1.0);

    // NDC → スクリーンピクセル座標
    let res     = u_camera.resolution;
    let screen_a = (clip_a.xy / clip_a.w) * res * 0.5;
    let screen_b = (clip_b.xy / clip_b.w) * res * 0.5;

    // スクリーン空間での線方向・垂直ベクトル
    let seg_dir = screen_b - screen_a;
    let seg_len = length(seg_dir);
    let dir     = select(vec2<f32>(1.0, 0.0), seg_dir / seg_len, seg_len > 0.001);
    let perp    = vec2<f32>(-dir.y, dir.x);

    // ピクセル単位のオフセット → NDC → クリップ空間
    let base_clip  = select(clip_a, clip_b, v.t > 0.5);
    let ndc_offset = perp / (res * 0.5) * v.side * THICKNESS_PX * 0.5;
    let clip_offset = ndc_offset * base_clip.w;

    var out: VertexOutput;
    out.clip_pos = base_clip + vec4<f32>(clip_offset, 0.0, 0.0);
    out.color    = v.color;
    return out;
}

@fragment
fn fs_main(in: VertexOutput) -> @location(0) vec4<f32> {
    return in.color;
}
