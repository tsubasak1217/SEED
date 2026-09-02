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
    /// 速度バッファ用の前フレーム ViewProjection（本パスでは未使用。オフセット合わせ）。
    prev_view_proj: mat4x4<f32>,
    /// フレームバッファ基準のビューポート矩形 (x, y, w, h)。
    /// **NDC はこの矩形へ写像される**ため、深度→ワールド復元はこれで正規化する
    /// （Play のレターボックス時に RT 全面で正規化すると座標が横滑りする）。
    viewport:       vec4<f32>,
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

// ─── RT 全面基準 UV → ビューポート相対 UV ─────────────────────────────
//
// フルスクリーン三角形が出す UV はレンダーターゲット全面（0..1）を張るが、
// NDC `[-1,1]` が写像されるのは **ビューポート矩形だけ**（Play のレターボックス／
// ピラーボックスで set_viewport する矩形）。深度→ワールド復元はこの矩形で正規化する。
// Edit モード・黒帯なしでは viewport = (0, 0, RT幅, RT高さ) となり従来式と完全に同値。
// テクスチャのアドレッシング（G-Buffer / 深度の textureLoad）は RT 全面基準のままでよい。
fn ao_vp_uv(uv_rt: vec2<f32>) -> vec2<f32> {
    // RT の実寸は textureDimensions から取る（uniform の resolution はウィンドウ側の
    // 要求サイズで、リサイズ中の 1 フレームだけスワップチェーン実寸と食い違いうるため。
    // 既存の ao_full_pix と同じ流儀）。
    let pix = uv_rt * vec2<f32>(textureDimensions(t_gbuffer0));
    return (pix - u_camera.viewport.xy) / max(u_camera.viewport.zw, vec2<f32>(1.0, 1.0));
}

// ─── 深度→ワールド復元（reflection_world_pos / deferred_lighting.wgsl と同式）───
fn ao_world_pos(uv: vec2<f32>, depth: f32) -> vec3<f32> {
    let uv_vp = ao_vp_uv(uv);
    let ndc  = vec3<f32>(uv_vp.x * 2.0 - 1.0, 1.0 - uv_vp.y * 2.0, depth);
    let clip = u_camera.inv_view_proj * vec4<f32>(ndc, 1.0);
    return clip.xyz / clip.w;
}

// ─── 深度→**カメラ相対**座標復元（幾何法線の画面微分専用）───────────────────
//
/// `ao_world_pos` と同じ点を、カメラを原点とした相対座標で返す
/// （`ao_world_pos(uv,d) == ao_cam_rel_pos(uv,d) + u_camera.position`）。
///
/// ## なぜ絶対ワールド座標で微分してはいけないか（f32 の桁落ち）
/// 幾何法線を `cross(dpdx(p), dpdy(p))` で作るとき `p` に絶対ワールド座標を使うと、
/// 原点から離れたシーン（例: 45m）では f32 の刻み幅 ulp(45)≒3.8e-6 m が**画素ごとに
/// ランダムな**丸め誤差として乗る。1 画素ぶんの世界差分（近距離・狭 FOV で 1e-3 m 程度）
/// に対して無視できない比率になり、外積で角度誤差へ増幅される（数値実験で最大 4.5°）。
/// 復元**後**に camera_position を引いても無意味（既に丸められている）。大きな値を
/// f32 の最終結果として作らないよう、**行列の側で平行移動を落としてから**復元する。
/// 行列の減算誤差は全画素共通の定数なので、隣接画素の差である微分ではほぼ相殺される。
///
/// 列優先 mat4x4 における `T(-cam) * ivp` は、各列 j について
/// `xyz -= cam * w`（w 行は不変）で得られる。deferred_lighting.wgsl の
/// `deferred_camera_relative_ivp` と同一の式（両パスで Ng を一致させるため）。
fn ao_cam_rel_pos(uv: vec2<f32>, depth: f32) -> vec3<f32> {
    let uv_vp = ao_vp_uv(uv);
    let ndc4  = vec4<f32>(uv_vp.x * 2.0 - 1.0, 1.0 - uv_vp.y * 2.0, depth, 1.0);
    let ivp   = u_camera.inv_view_proj;
    let cam   = u_camera.position;
    let m = mat4x4<f32>(
        vec4<f32>(ivp[0].xyz - cam * ivp[0].w, ivp[0].w),
        vec4<f32>(ivp[1].xyz - cam * ivp[1].w, ivp[1].w),
        vec4<f32>(ivp[2].xyz - cam * ivp[2].w, ivp[2].w),
        vec4<f32>(ivp[3].xyz - cam * ivp[3].w, ivp[3].w),
    );
    let rel = m * ndc4;
    return rel.xyz / rel.w;
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
