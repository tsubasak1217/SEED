// ============================================================
//  text.wgsl — スクリーン空間テキスト描画シェーダー
//
//  Group 0 : TextParams  (sdf_mode フラグ)
//  Group 1 : グリフアトラス テクスチャ + サンプラー
//
//  頂点座標は NDC (-1..1) で渡す（スクリーン空間）。
//  Bitmap モード: テクスチャの coverage 値をそのまま alpha に使用。
//  SDF モード   : fwidth を使ったアンチエイリアス付きスムースエッジ。
// ============================================================

struct TextParams {
    sdf_mode : u32,   // 0 = Bitmap, 1 = SDF
    _pad0    : u32,
    _pad1    : u32,
    _pad2    : u32,
}

@group(0) @binding(0) var<uniform> params : TextParams;
@group(1) @binding(0) var atlas      : texture_2d<f32>;
@group(1) @binding(1) var atlas_samp : sampler;

// ── 頂点入出力 ────────────────────────────────────────────────

struct VertIn {
    @location(0) position : vec3<f32>,
    @location(1) uv       : vec2<f32>,
    @location(2) color    : vec4<f32>,
}

struct VertOut {
    @builtin(position) clip_pos : vec4<f32>,
    @location(0)       uv       : vec2<f32>,
    @location(1)       color    : vec4<f32>,
}

// ── 頂点シェーダー ────────────────────────────────────────────

@vertex
fn vs_main(in: VertIn) -> VertOut {
    var out : VertOut;
    // 入力は NDC 座標（スクリーン空間）そのまま clip space へ
    out.clip_pos = vec4<f32>(in.position.xy, 0.0, 1.0);
    out.uv       = in.uv;
    out.color    = in.color;
    return out;
}

// ── フラグメントシェーダー ────────────────────────────────────

@fragment
fn fs_main(in: VertOut) -> @location(0) vec4<f32> {
    let sample = textureSample(atlas, atlas_samp, in.uv).r;

    var alpha : f32;

    if params.sdf_mode != 0u {
        // SDF: fwidth ベースのアンチエイリアス
        // atlas 値 0.5 = エッジ、>0.5 = 内側、<0.5 = 外側
        let fw    = fwidth(sample) * 0.7;
        let edge  = 0.5;
        alpha = clamp((sample - edge) / max(fw, 0.0001) + 0.5, 0.0, 1.0);
    } else {
        // Bitmap: coverage 値をそのまま alpha として使用
        alpha = sample;
    }

    if alpha < 0.01 { discard; }
    return vec4<f32>(in.color.rgb, in.color.a * alpha);
}
