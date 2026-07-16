// ============================================================
// reflection_common.wgsl — 反射パス共通定義（group0/1/2 宣言＋純関数）
//
// SSR（reflection_ssr.wgsl）と RT（reflection_rt.wgsl）が共有する
// フレネル／粗さフェード／画面端フェードの純関数、group0（カメラ）・
// group1（G-Buffer 入力）・group2（反射パラメータ＋シーン HDR）の宣言、
// フルスクリーン三角形頂点、深度→ワールド復元をまとめる。
//
// 連結順  SSR: [reflection_common, ddgi_common, reflection_ssr]
//         RT : [cluster_common, reflection_common, ddgi_common, reflection_rt]
// composite は本ファイルを連結しない（group0 の CameraUniform が composite の
// group0 反射テクスチャと衝突するため。composite は自前で完結）。
//
// HDR 読み書き分離: 反射は専用 RT_REFLECTION へ出力し、シーン HDR
// （t_scene_hdr, group2 binding1）はサンプル「入力」として読む＝読み書き競合なし。
// ============================================================

// 1 本レイの鏡面反射は粗面で嘘になるためフェードする（範囲の根拠つき）。
const REFLECTION_ROUGHNESS_FADE_START: f32 = 0.30; // ここまで全反射
const REFLECTION_ROUGHNESS_FADE_END:   f32 = 0.55; // ここで反射 0
const REFLECTION_EDGE_FADE:            f32 = 0.12; // 画面端フェード幅（UV 単位）

fn reflection_smoothness_weight(roughness: f32) -> f32 {
    return 1.0 - smoothstep(REFLECTION_ROUGHNESS_FADE_START, REFLECTION_ROUGHNESS_FADE_END, roughness);
}
fn reflection_f0(albedo: vec3<f32>, metallic: f32) -> vec3<f32> {
    return mix(vec3<f32>(0.04, 0.04, 0.04), albedo, metallic);
}
fn reflection_fresnel(f0: vec3<f32>, ndotv: f32) -> vec3<f32> {
    let m = clamp(1.0 - ndotv, 0.0, 1.0);
    return f0 + (vec3<f32>(1.0, 1.0, 1.0) - f0) * pow(m, 5.0);
}
fn reflection_screen_edge_fade(uv: vec2<f32>) -> f32 {
    let fx = min(uv.x, 1.0 - uv.x) / REFLECTION_EDGE_FADE;
    let fy = min(uv.y, 1.0 - uv.y) / REFLECTION_EDGE_FADE;
    return clamp(min(fx, fy), 0.0, 1.0);
}

// Group 0: カメラ（deferred_lighting.wgsl / uniforms::CameraUniform と同一 224B）。
struct CameraUniform {
    view_proj:      mat4x4<f32>,
    view:           mat4x4<f32>,
    position:       vec3<f32>,
    _pad:           f32,
    resolution:     vec2<f32>,
    _pad2:          vec2<f32>,
    inv_view_proj:  mat4x4<f32>,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// Group 1: G-Buffer 入力（deferred.rs の gbuffer_bgl と同一レイアウト）。
@group(1) @binding(0) var t_gbuffer0: texture_2d<f32>;   // albedo.rgb + occlusion.a
@group(1) @binding(1) var t_gbuffer1: texture_2d<f32>;   // world normal.xyz
@group(1) @binding(2) var t_gbuffer2: texture_2d<f32>;   // metallic.r + roughness.g
@group(1) @binding(3) var t_gbuffer3: texture_2d<f32>;   // emissive.rgb（未使用・レイアウト一致用）
@group(1) @binding(4) var t_depth:    texture_depth_2d;  // 深度（textureLoad 専用）
@group(1) @binding(5) var s_gbuffer:  sampler;           // 予約（未使用）

// Group 2: 反射パラメータ＋シーン HDR（Rust reflection::ReflectionParams 16B と同期）。
struct ReflectionParams {
    intensity: f32,
    _pad0:     f32,
    _pad1:     f32,
    _pad2:     f32,
}
@group(2) @binding(0) var<uniform> u_reflection: ReflectionParams;
@group(2) @binding(1) var t_scene_hdr: texture_2d<f32>;
@group(2) @binding(2) var s_scene:     sampler;

// フルスクリーン三角形（deferred_lighting.wgsl と同一手法）。
const REFLECTION_FS_POS: array<vec2<f32>, 3> = array<vec2<f32>, 3>(
    vec2<f32>(-1.0, -1.0),
    vec2<f32>( 3.0, -1.0),
    vec2<f32>(-1.0,  3.0),
);
@vertex
fn vs_fullscreen(@builtin(vertex_index) vi: u32) -> @builtin(position) vec4<f32> {
    return vec4<f32>(REFLECTION_FS_POS[vi], 0.0, 1.0);
}

// 深度（0..1 非線形）と UV からワールド座標を復元する（deferred_lighting.wgsl と同式）。
fn reflection_world_pos(uv: vec2<f32>, depth: f32) -> vec3<f32> {
    let ndc  = vec3<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth);
    let clip = u_camera.inv_view_proj * vec4<f32>(ndc, 1.0);
    return clip.xyz / clip.w;
}
