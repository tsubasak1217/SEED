// ============================================================
// shadow.wgsl  —  シャドウマップ サンプリング（group 5）
//
// Phase R2: 方向光 CSM（3 カスケード）＋ スポット（最大 4）の
// 深度シャドウマップを PCF 3x3 でサンプルし、可視率 [0,1] を返す。
//
// このファイルは mesh.toml / skinned_mesh.toml の shader_sources に
// shader_common.wgsl と shader_fragment.wgsl の間に連結され、
// PBR フラグメントシェーダのライトループから呼ばれる。
//
// 【バインドグループ】group 5（mesh / skinned_mesh 共通）
//   binding 0: texture_depth_2d_array  方向光 CSM（CSM_CASCADE_COUNT レイヤ）
//   binding 1: texture_depth_2d_array  スポット（MAX_SHADOW_SPOTS レイヤ）
//   binding 2: sampler_comparison      比較サンプラー（LessEqual）
//   binding 3: uniform ShadowMatrices  カスケード/スポットの view-proj と分割距離
// Rust 側 renderer/shadow.rs の定数・UBO レイアウトと厳密に一致させること。
// ============================================================

// ─── 定数（Rust: renderer/shadow.rs と一致）─────────────────
/// 方向光カスケード数。
const CSM_CASCADE_COUNT: u32 = 3u;
/// 影付きスポットの最大数（配列レイヤ数）。
const MAX_SHADOW_SPOTS: u32 = 4u;

/// CSM 1 テクセルの UV サイズ（1 / SHADOW_MAP_SIZE=2048）。PCF オフセット用。
const DIR_SHADOW_TEXEL:  f32 = 0.00048828125;   // 1/2048
/// スポット 1 テクセルの UV サイズ（1 / SPOT_SHADOW_SIZE=1024）。
const SPOT_SHADOW_TEXEL: f32 = 0.0009765625;    // 1/1024

/// シェーダ側の定数深度バイアス（NDC 深度 [0,1] 単位）。
/// パイプラインの slope-scaled バイアス（DepthBiasState）と併用してアクネを防ぐ。
/// 大きすぎるとピーターパン（影の浮き）、小さすぎるとシャドウアクネ（縞）。
/// 調整はこの値と shadow.rs の SHADOW_DEPTH_BIAS_* を合わせて行う。
const SHADOW_CONST_BIAS: f32 = 0.0012;

// ─── ShadowMatrices（uniform, std140）───────────────────────
// Rust: renderer/shadow.rs ShadowMatricesUbo と 1 バイト単位で一致。
//   cascade_vp:     mat4x4 × 3   (192 bytes)  カスケードごとの light view-proj
//   spot_vp:        mat4x4 × 4   (256 bytes)  スポットごとの light view-proj
//   cascade_splits: vec4         (16 bytes)   カスケード遠端のビュー空間距離 (x,y,z)
//   params:         vec4<u32>    (16 bytes)   x=方向光カスケード数(0=影なし), y=スポット影数
struct ShadowMatrices {
    cascade_vp:     array<mat4x4<f32>, 3>,
    spot_vp:        array<mat4x4<f32>, 4>,
    cascade_splits: vec4<f32>,
    params:         vec4<u32>,
}

@group(5) @binding(0) var t_shadow_dir:  texture_depth_2d_array;
@group(5) @binding(1) var t_shadow_spot: texture_depth_2d_array;
@group(5) @binding(2) var s_shadow_cmp:  sampler_comparison;
@group(5) @binding(3) var<uniform> u_shadow: ShadowMatrices;

// ─── PCF 3x3 ────────────────────────────────────────────────

/// 方向光 CSM を PCF 3x3 でサンプルし可視率 [0,1] を返す（1=全照射, 0=全遮蔽）。
fn pcf_dir(uv: vec2<f32>, layer: i32, ref_depth: f32) -> f32 {
    var sum = 0.0;
    for (var y: i32 = -1; y <= 1; y = y + 1) {
        for (var x: i32 = -1; x <= 1; x = x + 1) {
            let off = vec2<f32>(f32(x), f32(y)) * DIR_SHADOW_TEXEL;
            // 比較サンプラー(LessEqual): ref_depth <= 格納深度 → 1.0（手前=照射）
            sum = sum + textureSampleCompare(t_shadow_dir, s_shadow_cmp, uv + off, layer, ref_depth);
        }
    }
    return sum / 9.0;
}

/// スポットマップを PCF 3x3 でサンプルし可視率 [0,1] を返す。
fn pcf_spot(uv: vec2<f32>, layer: i32, ref_depth: f32) -> f32 {
    var sum = 0.0;
    for (var y: i32 = -1; y <= 1; y = y + 1) {
        for (var x: i32 = -1; x <= 1; x = x + 1) {
            let off = vec2<f32>(f32(x), f32(y)) * SPOT_SHADOW_TEXEL;
            sum = sum + textureSampleCompare(t_shadow_spot, s_shadow_cmp, uv + off, layer, ref_depth);
        }
    }
    return sum / 9.0;
}

// ─── 方向光 CSM ─────────────────────────────────────────────

/// フラグメントのビュー空間深度からカスケードを選び、方向光の可視率を返す。
/// - `world_pos`: フラグメントのワールド座標
/// - `view_z`:    ビュー空間深度（正、= (u_camera.view * world).z）
fn sample_shadow_dir(world_pos: vec3<f32>, view_z: f32) -> f32 {
    // 方向光影が無効（params.x == 0）なら常に照射。
    if u_shadow.params.x == 0u { return 1.0; }

    // カスケード選択（ビュー空間距離の分割境界で判定）。
    // vec4 は動的添字不可のため成分比較で分岐する。
    var cascade: i32 = 2;
    if view_z < u_shadow.cascade_splits.x {
        cascade = 0;
    } else if view_z < u_shadow.cascade_splits.y {
        cascade = 1;
    } else {
        cascade = 2;
    }

    // ライト空間クリップ座標 → NDC。
    let clip = u_shadow.cascade_vp[cascade] * vec4<f32>(world_pos, 1.0);
    if clip.w <= 0.0 { return 1.0; }
    let ndc = clip.xyz / clip.w;

    // NDC(x,y) [-1,1] → UV [0,1]（Y は反転）。
    let uv = vec2<f32>(ndc.x * 0.5 + 0.5, ndc.y * -0.5 + 0.5);
    // マップ外・深度 [0,1] 外は影なし扱い（境界破綻を防ぐ）。
    if uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || ndc.z > 1.0 || ndc.z < 0.0 {
        return 1.0;
    }

    let ref_depth = ndc.z - SHADOW_CONST_BIAS;
    return pcf_dir(uv, cascade, ref_depth);
}

// ─── スポット ───────────────────────────────────────────────

/// スポット光の可視率を返す。`slot` は 0..MAX_SHADOW_SPOTS-1 の配列レイヤ。
fn sample_shadow_spot(world_pos: vec3<f32>, slot: i32) -> f32 {
    let clip = u_shadow.spot_vp[slot] * vec4<f32>(world_pos, 1.0);
    if clip.w <= 0.0 { return 1.0; }
    let ndc = clip.xyz / clip.w;

    let uv = vec2<f32>(ndc.x * 0.5 + 0.5, ndc.y * -0.5 + 0.5);
    if uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || ndc.z > 1.0 || ndc.z < 0.0 {
        return 1.0;
    }

    let ref_depth = ndc.z - SHADOW_CONST_BIAS;
    return pcf_spot(uv, slot, ref_depth);
}
