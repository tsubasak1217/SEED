// ============================================================
// ssgi_common.wgsl — SSGI パス共通定義（group0/1/2 宣言＋純関数＋フルスクリーン頂点）
//
// SSGI 生成パス（ssgi_gen.wgsl）が使う共通定義。AO パス（ao_common.wgsl）の**カラー版**で、
// 構成はほぼ同一だが、遮蔽率（スカラー）ではなく間接放射輝度（カラー）を扱うため、
// 反射（reflection_common.wgsl）と同じく **scene_hdr（不透明ライティング済み）** を入力に持つ。
//
//   - group0（カメラ, deferred_lighting.wgsl / uniforms::CameraUniform と同一 224B）
//   - group1（G-Buffer 入力, deferred.rs の gbuffer_bgl と同一の 0..5 宣言。subset は合法）
//   - group2（SsgiParams: フラットアンビエント色 ＋ scene_hdr テクスチャ ＋ サンプラー）
//   - 半解像度フルスクリーン三角形頂点（UV を varying で渡す＝解像度非依存。ao_common と同一）
//   - 深度→ワールド復元 ssgi_world_pos（reflection_world_pos / ao_world_pos と同式）
//   - Interleaved Gradient Noise（rt_shadow_on.wgsl と同式）／直交基底 ssgi_perp
//
// 連結順  SSGI: [ssgi_common, ssgi_gen]
//
// 【半解像度と UV varying】ao_common.wgsl と同じ理由。生成パスは半解像度 ssgi_raw へ描くため
// @builtin(position) は半解像度画素座標になる。頂点で UV（0..1）を varying 出力し、フラグメントは
// この UV で **フル解像度**の G-Buffer / 深度 / scene_hdr をサンプルする（解像度非依存）。
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
// gbuffer_bgl は 10 binding（+6/7=AO, +8/9=SSGI）へ拡張済みだが、本生成シェーダは 0..5 のみ
// 宣言する（wgpu は「シェーダ binding ⊆ BGL binding」を許すため subset は合法）。
@group(1) @binding(0) var t_gbuffer0: texture_2d<f32>;   // albedo.rgb + occlusion.a
@group(1) @binding(1) var t_gbuffer1: texture_2d<f32>;   // world normal.xyz
@group(1) @binding(2) var t_gbuffer2: texture_2d<f32>;   // metallic.r + roughness.g
@group(1) @binding(3) var t_gbuffer3: texture_2d<f32>;   // emissive.rgb（未使用・レイアウト一致用）
@group(1) @binding(4) var t_depth:    texture_depth_2d;  // 深度（textureLoad 専用）
@group(1) @binding(5) var s_gbuffer:  sampler;           // 予約（未使用）

// ─── Group 2: SSGI パラメータ ＋ scene_hdr（Rust ssgi::SsgiParams 16B と同期）───
// ミス時（レイが画面外/背景へ抜けた）に埋めるフラットアンビエント色（＝ambient_color*ambient_intensity）。
// これにより SSGI は「画面外・遮蔽の情報欠落」を真っ黒でなくフラットアンビエントで埋める。
struct SsgiParams {
    /// フラットアンビエント放射照度（リニア RGB）。ミス埋め色。
    ambient: vec3<f32>,
    _pad0:   f32,
}
@group(2) @binding(0) var<uniform> u_ssgi:     SsgiParams;
@group(2) @binding(1) var          t_scene_hdr: texture_2d<f32>;  // 不透明ライティング済み HDR（入力）
@group(2) @binding(2) var          s_scene:     sampler;          // Filtering（linear）

// ─── フルスクリーン三角形（UV を varying で出力。ao_common.wgsl と同一）───
struct SsgiVsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0)       uv:  vec2<f32>,
}
const SSGI_FS_POS: array<vec2<f32>, 3> = array<vec2<f32>, 3>(
    vec2<f32>(-1.0, -1.0),
    vec2<f32>( 3.0, -1.0),
    vec2<f32>(-1.0,  3.0),
);
@vertex
fn vs_fullscreen(@builtin(vertex_index) vi: u32) -> SsgiVsOut {
    var out: SsgiVsOut;
    let p = SSGI_FS_POS[vi];
    out.pos = vec4<f32>(p, 0.0, 1.0);
    // クリップ座標 → UV（uv.x=0 左端 / uv.y=0 上端）。ssgi_world_pos の ndc.y = 1 - uv.y*2 と整合。
    out.uv = vec2<f32>(p.x * 0.5 + 0.5, (1.0 - p.y) * 0.5);
    return out;
}

// ─── 深度→ワールド復元（reflection_world_pos / ao_world_pos と同式）───
fn ssgi_world_pos(uv: vec2<f32>, depth: f32) -> vec3<f32> {
    let ndc  = vec3<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth);
    let clip = u_camera.inv_view_proj * vec4<f32>(ndc, 1.0);
    return clip.xyz / clip.w;
}

// ─── UV → フル解像度 G-Buffer 整数座標（textureDimensions ベース。半解像度非依存）───
fn ssgi_full_pix(uv: vec2<f32>) -> vec2<i32> {
    let dims = vec2<f32>(textureDimensions(t_gbuffer0));
    let p    = uv * dims;
    let mx   = dims - vec2<f32>(1.0, 1.0);
    return vec2<i32>(clamp(p, vec2<f32>(0.0, 0.0), mx));
}

// ─── Interleaved Gradient Noise（Jimenez, rt_shadow_on.wgsl / ao_common.wgsl と同式）───
fn ssgi_ign(p: vec2<f32>) -> f32 {
    return fract(52.9829189 * fract(dot(p, vec2<f32>(0.06711056, 0.00583715))));
}

// ─── 任意ベクトルに直交する単位ベクトル（rt_shadow_on.wgsl の perp と同式）───
fn ssgi_perp(v: vec3<f32>) -> vec3<f32> {
    let a = select(vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(1.0, 0.0, 0.0), abs(v.x) < 0.9);
    return normalize(cross(a, v));
}

// ─── 円周率・黄金角（本パスで自己完結）───
const SSGI_PI:           f32 = 3.14159265359;
const SSGI_GOLDEN_ANGLE: f32 = 2.39996323;

/// 背景深度（DepthStencil の Clear=1.0）。この値以上は「何も描かれていない背景」。
const SSGI_BACKGROUND_DEPTH: f32 = 1.0;
