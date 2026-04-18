// ============================================================
// shader_common.wgsl  —  PBR シェーダ共通定義
// ============================================================

// ─── Group 0: カメラ ──────────────────────────────────────────

struct CameraUniform {
    view_proj:  mat4x4<f32>,
    view:       mat4x4<f32>,
    position:   vec3<f32>,
    _pad:       f32,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// ─── Group 1: モデル変換（インスタンス配列）─────────────────
//
// storage buffer に全インスタンス分の行列を格納し、
// 頂点シェーダが @builtin(instance_index) でインデックスする。
// インスタンス数 1 の通常描画でも同じ構造を使う。

struct ModelUniform {
    model:         mat4x4<f32>,
    normal_matrix: mat4x4<f32>,
}
/// ノードごとのワールド行列配列（全インスタンス分）
@group(1) @binding(0) var<storage, read> u_instances:    array<ModelUniform>;
/// 可視インスタンスのインデックス列（視錐台カリング後の compact list）
/// u_instances[ u_visible_list[instance_index] ] が実際のインスタンスデータ
@group(1) @binding(1) var<storage, read> u_visible_list: array<u32>;

// ─── Group 2: マテリアル ──────────────────────────────────────

struct MaterialUniform {
    base_color_factor:  vec4<f32>,
    metallic_factor:    f32,
    roughness_factor:   f32,
    alpha_cutoff:       f32,
    has_base_color_tex: u32,
    emissive_factor:    vec3<f32>,
    has_normal_tex:     u32,
    has_mr_tex:         u32,
    has_occlusion_tex:  u32,
    has_emissive_tex:   u32,
    _pad:               u32,
}
@group(2) @binding(0)  var<uniform> u_material:          MaterialUniform;
@group(2) @binding(1)  var          t_base_color:         texture_2d<f32>;
@group(2) @binding(2)  var          s_base_color:         sampler;
@group(2) @binding(3)  var          t_normal:             texture_2d<f32>;
@group(2) @binding(4)  var          s_normal:             sampler;
@group(2) @binding(5)  var          t_metallic_roughness: texture_2d<f32>;
@group(2) @binding(6)  var          s_metallic_roughness: sampler;
@group(2) @binding(7)  var          t_occlusion:          texture_2d<f32>;
@group(2) @binding(8)  var          s_occlusion:          sampler;
@group(2) @binding(9)  var          t_emissive:           texture_2d<f32>;
@group(2) @binding(10) var          s_emissive:           sampler;

// ─── 頂点シェーダ出力 / フラグメントシェーダ入力 ─────────────

struct VertexOutput {
    @builtin(position) clip_pos:     vec4<f32>,
    @location(0)       world_pos:    vec3<f32>,
    @location(1)       world_normal: vec3<f32>,
    @location(2)       world_tan:    vec3<f32>,
    @location(3)       world_bitan:  vec3<f32>,
    @location(4)       uv0:          vec2<f32>,
    @location(5)       uv1:          vec2<f32>,
    @location(6)       color:        vec4<f32>,
}

// ─── PBR ヘルパー関数 ────────────────────────────────────────

const PI: f32 = 3.14159265359;

fn distribution_ggx(N: vec3<f32>, H: vec3<f32>, roughness: f32) -> f32 {
    let a  = roughness * roughness;
    let a2 = a * a;
    let ndh = max(dot(N, H), 0.0);
    let d   = ndh * ndh * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

fn geometry_schlick_ggx(ndv: f32, roughness: f32) -> f32 {
    let r = roughness + 1.0;
    let k = (r * r) / 8.0;
    return ndv / (ndv * (1.0 - k) + k);
}

fn geometry_smith(N: vec3<f32>, V: vec3<f32>, L: vec3<f32>, roughness: f32) -> f32 {
    let ndv = max(dot(N, V), 0.0);
    let ndl = max(dot(N, L), 0.0);
    return geometry_schlick_ggx(ndv, roughness) * geometry_schlick_ggx(ndl, roughness);
}

fn fresnel_schlick(cos_theta: f32, F0: vec3<f32>) -> vec3<f32> {
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cos_theta, 0.0, 1.0), 5.0);
}
