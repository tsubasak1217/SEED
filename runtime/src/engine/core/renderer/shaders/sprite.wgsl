// ============================================================
//  sprite.wgsl — ワールド空間スプライトシェーダー（Phase R6 インスタンシング）
//
//  Group 0: CameraUniform（mesh/unlit パイプラインと同一レイアウト）
//  Group 1: テクスチャ + サンプラー
//
//  頂点は正規化ローカル座標 [0,1]×[0,1] のユニットクワッド（6 頂点, step_mode=Vertex）。
//  モデル行列・カラーは per-instance の頂点属性（step_mode=Instance）で供給する。
//  → 同一テクスチャの連続スプライトを 1 ドローコールで一括描画できる（旧: 1 枚 1 ドロー
//    ＋毎フレーム uniform buffer/BindGroup 生成を撤廃）。
// ============================================================

// ─── バインドグループ ─────────────────────────────────────────

struct CameraUniform {
    view_proj: mat4x4<f32>,
    view:      mat4x4<f32>,
    position:  vec3<f32>,
    _pad:      f32,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

@group(1) @binding(0) var t_sprite: texture_2d<f32>;
@group(1) @binding(1) var s_sprite: sampler;

// ─── 頂点入出力 ───────────────────────────────────────────────

struct VertIn {
    // per-vertex（ユニットクワッド）
    @location(0) position: vec2<f32>,
    @location(1) uv:       vec2<f32>,
    // per-instance（model 行列の 4 列 + カラー）。列優先で格納されているため
    // mat4x4<f32>(col0, col1, col2, col3) でそのまま復元できる。
    @location(2) m0:    vec4<f32>,
    @location(3) m1:    vec4<f32>,
    @location(4) m2:    vec4<f32>,
    @location(5) m3:    vec4<f32>,
    @location(6) color: vec4<f32>,
}

struct VertOut {
    @builtin(position) clip_pos: vec4<f32>,
    @location(0)       uv:       vec2<f32>,
    @location(1)       color:    vec4<f32>,
}

// ─── シェーダー ───────────────────────────────────────────────

@vertex
fn vs_main(v: VertIn) -> VertOut {
    // per-instance の 4 列から model 行列を復元する（列優先）
    let model = mat4x4<f32>(v.m0, v.m1, v.m2, v.m3);
    var out: VertOut;
    out.clip_pos = u_camera.view_proj * model * vec4<f32>(v.position, 0.0, 1.0);
    out.uv       = v.uv;
    out.color    = v.color;
    return out;
}

@fragment
fn fs_main(in: VertOut) -> @location(0) vec4<f32> {
    let col = textureSample(t_sprite, s_sprite, in.uv) * in.color;
    // アルファが極めて小さいピクセルは描画しない（ハードカット）
    if col.a < 0.004 { discard; }
    return col;
}
