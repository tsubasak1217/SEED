// ============================================================
//  sprite.wgsl — ワールド空間スプライトシェーダー
//
//  Group 0: CameraUniform（mesh/unlit パイプラインと同一レイアウト）
//  Group 1: SpriteUniform（モデル行列 + カラー乗算）
//  Group 2: テクスチャ + サンプラー
//
//  頂点は正規化ローカル座標 [0,1]×[0,1] のユニットクワッド。
//  モデル行列側でワールド空間への変換（サイズ・回転・移動）を行う。
// ============================================================

// ─── バインドグループ ─────────────────────────────────────────

struct CameraUniform {
    view_proj: mat4x4<f32>,
    view:      mat4x4<f32>,
    position:  vec3<f32>,
    _pad:      f32,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

struct SpriteUniform {
    model: mat4x4<f32>,
    color: vec4<f32>,
}
@group(1) @binding(0) var<uniform> u_sprite: SpriteUniform;

@group(2) @binding(0) var t_sprite: texture_2d<f32>;
@group(2) @binding(1) var s_sprite: sampler;

// ─── 頂点入出力 ───────────────────────────────────────────────

struct VertIn {
    @location(0) position: vec2<f32>,
    @location(1) uv:       vec2<f32>,
}

struct VertOut {
    @builtin(position) clip_pos: vec4<f32>,
    @location(0)       uv:       vec2<f32>,
}

// ─── シェーダー ───────────────────────────────────────────────

@vertex
fn vs_main(v: VertIn) -> VertOut {
    var out: VertOut;
    out.clip_pos = u_camera.view_proj * u_sprite.model * vec4<f32>(v.position, 0.0, 1.0);
    out.uv       = v.uv;
    return out;
}

@fragment
fn fs_main(in: VertOut) -> @location(0) vec4<f32> {
    let col = textureSample(t_sprite, s_sprite, in.uv) * u_sprite.color;
    // アルファが極めて小さいピクセルは描画しない（ハードカット）
    if col.a < 0.004 { discard; }
    return col;
}
