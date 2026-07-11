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
/// ノードごとのワールド行列配列（全インスタンス分）。
/// @builtin(instance_index) は DrawIndexedIndirect.first_instance を通じて
/// GPU カリングが書き込んだ実インスタンス番号を直接受け取る。
@group(1) @binding(0) var<storage, read> u_instances: array<ModelUniform>;

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

// ─── Group 4: ライト（binding 0/1）＋シャドウ（binding 2〜5）───
//
// storage buffer に全ライト（最大 MAX_LIGHTS）を格納し、
// uniform（u_light_meta.count）で有効ライト数を渡す。
// フラグメントシェーダのライトループがこの配列を count 件だけ走査する。
//
// binding 2〜5 は Phase R2 のシャドウ資源（shadow.wgsl で宣言）。
// デバイスの max_bind_groups=5（group 0〜4）環境があるため group 5 は新設せず、
// シャドウを本グループへ同居させている（Rust 側の複合 BindGroup は lighting.rs）。
//
// GpuLight のレイアウトは Rust 側 lighting.rs の repr(C) 構造体と
// 厳密に一致させること（vec3 は 16 バイト境界、合計 96 バイト）。

/// ライト種別コード（Rust: LIGHT_KIND_*、LightKind::to_code() と一致）。
const LIGHT_KIND_DIRECTIONAL: u32 = 0u;
const LIGHT_KIND_POINT:       u32 = 1u;
const LIGHT_KIND_SPOT:        u32 = 2u;
const LIGHT_KIND_RECT:        u32 = 3u;

struct GpuLight {
    color:            vec3<f32>,   // 0
    intensity:        f32,         // 12
    position:         vec3<f32>,   // 16
    range:            f32,         // 28
    direction:        vec3<f32>,   // 32  光が進む向き（L = -direction）
    kind:             u32,         // 44
    inner_cos:        f32,         // 48
    outer_cos:        f32,         // 52
    rect_half_width:  f32,         // 56
    rect_half_height: f32,         // 60
    rect_right:       vec3<f32>,   // 64
    shadow_index:     f32,         // 76  影スロット（-1=影なし / dir:0=CSM有効 / spot:0..3=レイヤ）
    rect_up:          vec3<f32>,   // 80
    soft_radius:      f32,         // 92  ソフト影の見込み半径（Phase R8 ソフトシャドウ）
                                   //     directional: tan(角径) の無次元スロープ（Rust 側で度→tan 変換済み）
                                   //     point/spot/rect: 光源のワールド半径（シェーダで radius/距離＝見込み角に換算）
                                   //     0 = ハードシャドウ（従来の遮蔽レイ 1 本）
}

// count = 有効ライト数、rt_shadows = インラインレイトレ影フラグ（Phase R8, 0/1）。
// ambient_* は制御可能な環境光（Phase R1.5）。先頭スカラー 4 つで 16 バイト、その後に
// vec3 の ambient_color（16 バイト境界）＋ ambient_intensity で計 32 バイト。
// Rust 側 lighting.rs の LightMeta（repr(C), 32 バイト）と厳密に一致させること。
struct LightMeta {
    count:             u32,
    rt_shadows:        u32,
    _pad1:             u32,
    _pad2:             u32,
    ambient_color:     vec3<f32>,   // 16  環境光の色（リニア RGB）
    ambient_intensity: f32,         // 28  環境光の強度（0 で完全な暗闇）
}

@group(4) @binding(0) var<storage, read> u_lights:     array<GpuLight>;
@group(4) @binding(1) var<uniform>       u_light_meta: LightMeta;

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
