// ============================================================
//  post_wboit_composite.wgsl — WBOIT 合成パス（Phase R5）
//
//  shader_wboit.wgsl が蓄積した accum / reveal から順序独立の透明結果を
//  復元し、シーン HDR の上へアルファブレンドで合成する。
//  合成式（McGuire & Bavoil 2013）:
//    avg      = accum.rgb / max(accum.a, eps)   // 重み平均色
//    coverage = 1 - reveal                       // 透明物の被覆率
//    out      = vec4(avg, coverage)
//  パイプライン側 blend = AlphaBlending（src_alpha / 1-src_alpha）と
//  LoadOp::Load により out.rgb*coverage + scene_hdr*(1-coverage) となる。
//
//  連結順（post_wboit_composite.toml）: fullscreen.wgsl（fs_vs / FsOut）→ 本ファイル。
//
//  group 0: accum テクスチャ + サンプラー
//  group 1: reveal テクスチャ + サンプラー
// ============================================================

/// ゼロ割れ防止の微小値（accum.a が 0 のピクセル向け）と、
/// 完全透過（透明物なし）判定のしきい値に使う。
const WBOIT_EPSILON: f32 = 1.0e-5;

@group(0) @binding(0) var t_accum:  texture_2d<f32>;
@group(0) @binding(1) var s_accum:  sampler;

@group(1) @binding(0) var t_reveal: texture_2d<f32>;
@group(1) @binding(1) var s_reveal: sampler;

@fragment
fn composite_fs(in: FsOut) -> @location(0) vec4<f32> {
    let reveal = textureSample(t_reveal, s_reveal, in.uv).r;
    // reveal ≈ 1 は「そのピクセルに透明フラグメントが無い」= 全透過。
    // discard してシーン HDR（LoadOp::Load）をそのまま残す。
    if reveal > 1.0 - WBOIT_EPSILON {
        discard;
    }
    let accum = textureSample(t_accum, s_accum, in.uv);
    // 重み付き premultiplied 色を重み（accum.a）で割って平均色に戻す。
    let avg = accum.rgb / max(accum.a, WBOIT_EPSILON);
    // A に被覆率 (1 - reveal) を入れ、AlphaBlending でシーンへ重ねる。
    return vec4<f32>(avg, 1.0 - reveal);
}
