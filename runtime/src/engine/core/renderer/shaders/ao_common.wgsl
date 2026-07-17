// ============================================================
// ao_common.wgsl — AO パス共通定義（group0/1/2 宣言＋純関数＋フルスクリーン頂点）
//
// SSAO（ao_ssao.wgsl）と RT-AO（ao_rt.wgsl）が共有する:
//   - group0（カメラ, reflection_common.wgsl / deferred_lighting.wgsl と同一 CameraUniform 224B）
//   - group1（G-Buffer 入力, deferred.rs の gbuffer_bgl と同一の 0..5 宣言）
//   - group2（AoParams: intensity / radius）
//   - 半解像度フルスクリーン三角形頂点（UV を varying で渡す＝解像度非依存）
//   - 深度→ワールド復元 ao_world_pos（reflection_world_pos と同式）
//   - Interleaved Gradient Noise（rt_shadow_on.wgsl と同式）
//   - 任意ベクトルの直交基底 ao_perp（rt_shadow_on.wgsl の perp と同式）
//
// 連結順  SSAO: [ao_common, ao_ssao]
//         RT  : [ao_common, ao_rt]
//
// 【半解像度と UV varying】
// AO 生成パスは半解像度ターゲット（ao_raw）へ描く。フラグメントの @builtin(position) は
// 半解像度画素座標なので、u_camera.resolution（フル解像度）では UV を復元できない。
// そこで頂点シェーダで UV（0..1）を varying として出力し、フラグメントはこの UV を使って
// フル解像度の G-Buffer / 深度を textureDimensions ベースでサンプルする（解像度非依存）。
// ============================================================

// ─── Group 0: カメラ（deferred_lighting.wgsl / uniforms::CameraUniform と同一 224B）───
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

// ─── Group 1: G-Buffer 入力（deferred.rs の gbuffer_bgl と同一レイアウトの 0..5）───
// gbuffer_bgl は 8 binding（+6=t_ao / +7=s_ao）へ拡張済みだが、AO 生成シェーダは
// 0..5 のみ宣言する（wgpu は「シェーダ binding ⊆ BGL binding」を許すため subset は合法）。
@group(1) @binding(0) var t_gbuffer0: texture_2d<f32>;   // albedo.rgb + occlusion.a
@group(1) @binding(1) var t_gbuffer1: texture_2d<f32>;   // world normal.xyz
@group(1) @binding(2) var t_gbuffer2: texture_2d<f32>;   // metallic.r + roughness.g
@group(1) @binding(3) var t_gbuffer3: texture_2d<f32>;   // emissive.rgb（未使用・レイアウト一致用）
@group(1) @binding(4) var t_depth:    texture_depth_2d;  // 深度（textureLoad 専用）
@group(1) @binding(5) var s_gbuffer:  sampler;           // 予約（未使用）

// ─── Group 2: AO パラメータ（Rust ao::AoParams 16B と同期）───
struct AoParams {
    /// AO 寄与の全体倍率（エディタの「AO 強度」スライダー由来）。
    intensity: f32,
    /// AO の世界単位半径（SSAO=サンプル球半径 / RT=レイ tmax。Rust 側で方式ごとの定数を書く）。
    radius:    f32,
    _pad0:     f32,
    _pad1:     f32,
}
@group(2) @binding(0) var<uniform> u_ao: AoParams;

// ─── フルスクリーン三角形（UV を varying で出力）───
struct AoVsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0)       uv:  vec2<f32>,
}
const AO_FS_POS: array<vec2<f32>, 3> = array<vec2<f32>, 3>(
    vec2<f32>(-1.0, -1.0),
    vec2<f32>( 3.0, -1.0),
    vec2<f32>(-1.0,  3.0),
);
@vertex
fn vs_fullscreen(@builtin(vertex_index) vi: u32) -> AoVsOut {
    var out: AoVsOut;
    let p = AO_FS_POS[vi];
    out.pos = vec4<f32>(p, 0.0, 1.0);
    // クリップ座標 → UV（uv.x=0 左端 / uv.y=0 上端）。
    // reflection_world_pos の ndc.y = 1 - uv.y*2 と整合する向き（uv.y=0 で ndc.y=+1＝上端）。
    out.uv = vec2<f32>(p.x * 0.5 + 0.5, (1.0 - p.y) * 0.5);
    return out;
}

// ─── 深度→ワールド復元（reflection_world_pos / deferred_lighting.wgsl と同式）───
fn ao_world_pos(uv: vec2<f32>, depth: f32) -> vec3<f32> {
    let ndc  = vec3<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth);
    let clip = u_camera.inv_view_proj * vec4<f32>(ndc, 1.0);
    return clip.xyz / clip.w;
}

// ─── UV → フル解像度 G-Buffer 整数座標（textureDimensions ベース。半解像度非依存）───
fn ao_full_pix(uv: vec2<f32>) -> vec2<i32> {
    let dims = vec2<f32>(textureDimensions(t_gbuffer0));
    let p    = uv * dims;
    let mx   = dims - vec2<f32>(1.0, 1.0);
    return vec2<i32>(clamp(p, vec2<f32>(0.0, 0.0), mx));
}

// ─── Interleaved Gradient Noise（Jimenez, rt_shadow_on.wgsl と同式）───
fn ao_ign(p: vec2<f32>) -> f32 {
    return fract(52.9829189 * fract(dot(p, vec2<f32>(0.06711056, 0.00583715))));
}

// ─── 任意ベクトルに直交する単位ベクトル（rt_shadow_on.wgsl の perp と同式）───
fn ao_perp(v: vec3<f32>) -> vec3<f32> {
    let a = select(vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(1.0, 0.0, 0.0), abs(v.x) < 0.9);
    return normalize(cross(a, v));
}

// ─── 円周率・黄金角（本パスで自己完結）───
const AO_PI:           f32 = 3.14159265359;
const AO_GOLDEN_ANGLE: f32 = 2.39996323;

/// 背景深度（DepthStencil の Clear=1.0）。この値以上は「何も描かれていない背景」＝AO なし。
const AO_BACKGROUND_DEPTH: f32 = 1.0;
