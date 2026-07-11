// ============================================================
//  post_bloom_up.wgsl — ブルーム アップサンプル＋加算合成（3x3 テント）
//
//  ダウンサンプルで作った各 mip を、1 段大きい mip へ 3x3 テントフィルタで
//  拡大しながら加算合成していく（Phase R4, デュアルフィルタのアップ側）。
//  最終段（mip0 → シーン HDR）は同じシェーダを scale=intensity で使い、
//  ブルームをシーンへ足し込む（合成式: scene += tent(bloom_mip0) * intensity）。
//
//  加算合成はパイプライン側 blend="Additive" ＋ 描画時 LoadOp::Load で行うため、
//  本シェーダは「拡大サンプル値 * scale」を出力するだけでよい。
//
//  連結順（post_bloom_up.toml の shader_sources）:
//    fullscreen.wgsl（頂点 fs_vs / FsOut）→ 本ファイル。
//
//  group 0: パラメータ UBO（入力テクセルサイズ・合成スケール）
//  group 1: 入力テクスチャ（1 段小さい mip）+ サンプラー（リニア）
// ============================================================

/// アップサンプルパラメータ（CPU 側 BloomUpParams と #[repr(C)] 一致）。
struct BloomUpParams {
    /// 入力テクスチャの 1 テクセルサイズ（1/幅, 1/高さ）。テント半径に使う。
    texel: vec2<f32>,
    /// 加算合成スケール。中間アップは 1.0、最終合成は intensity。
    scale: f32,
    _pad:  f32,
}
@group(0) @binding(0) var<uniform> u_up: BloomUpParams;

@group(1) @binding(0) var t_in: texture_2d<f32>;
@group(1) @binding(1) var s_in: sampler;

@fragment
fn bloom_up_fs(in: FsOut) -> @location(0) vec4<f32> {
    let uv = in.uv;
    let tx = u_up.texel.x;
    let ty = u_up.texel.y;

    // 3x3 テント（重み 1-2-1 / 1-2-1 の外積 = 合計 16）。
    var col  = textureSample(t_in, s_in, uv + vec2<f32>(-tx, -ty)).rgb * 1.0;
    col += textureSample(t_in, s_in, uv + vec2<f32>( 0.0, -ty)).rgb * 2.0;
    col += textureSample(t_in, s_in, uv + vec2<f32>( tx, -ty)).rgb * 1.0;
    col += textureSample(t_in, s_in, uv + vec2<f32>(-tx,  0.0)).rgb * 2.0;
    col += textureSample(t_in, s_in, uv                       ).rgb * 4.0;
    col += textureSample(t_in, s_in, uv + vec2<f32>( tx,  0.0)).rgb * 2.0;
    col += textureSample(t_in, s_in, uv + vec2<f32>(-tx,  ty)).rgb * 1.0;
    col += textureSample(t_in, s_in, uv + vec2<f32>( 0.0,  ty)).rgb * 2.0;
    col += textureSample(t_in, s_in, uv + vec2<f32>( tx,  ty)).rgb * 1.0;
    col = col * (1.0 / 16.0);

    return vec4<f32>(col * u_up.scale, 1.0);
}
