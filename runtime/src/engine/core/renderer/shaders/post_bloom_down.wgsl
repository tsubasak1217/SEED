// ============================================================
//  post_bloom_down.wgsl — ブルーム ダウンサンプル（13-tap）
//
//  ブルームチェーンを 1/2 ずつ縮小しながらぼかす（Phase R4）。CoD:AW の
//  Next Generation Post Processing（Siggraph 2014）の 13 タップフィルタを使う。
//  単純なバイリニア縮小より安定してエイリアス／ちらつきに強い。
//
//  連結順（post_bloom_down.toml の shader_sources）:
//    fullscreen.wgsl（頂点 fs_vs / FsOut）→ 本ファイル。
//
//  group 0: パラメータ UBO（入力テクスチャの 1 テクセルサイズ）
//  group 1: 入力テクスチャ（1 段上・2 倍解像度）+ サンプラー（リニア）
// ============================================================

/// ダウンサンプルパラメータ（CPU 側 BloomDownParams と #[repr(C)] 一致）。
struct BloomDownParams {
    /// 入力テクスチャの 1 テクセルサイズ（1/幅, 1/高さ）。
    texel: vec2<f32>,
    _pad0: f32,
    _pad1: f32,
}
@group(0) @binding(0) var<uniform> u_dn: BloomDownParams;

@group(1) @binding(0) var t_in: texture_2d<f32>;
@group(1) @binding(1) var s_in: sampler;

@fragment
fn bloom_down_fs(in: FsOut) -> @location(0) vec4<f32> {
    let uv = in.uv;
    let tx = u_dn.texel.x;
    let ty = u_dn.texel.y;

    // 13-tap: 中央 3x3（内側 4 点＝半テクセル）＋外周 3x3 の重み付き平均。
    // 内側 4 タップ（±0.5 テクセル）＝ぼけの主成分。
    let a = textureSample(t_in, s_in, uv + vec2<f32>(-tx, -ty)).rgb;
    let b = textureSample(t_in, s_in, uv + vec2<f32>( 0.0, -ty)).rgb;
    let c = textureSample(t_in, s_in, uv + vec2<f32>( tx, -ty)).rgb;
    let d = textureSample(t_in, s_in, uv + vec2<f32>(-tx,  0.0)).rgb;
    let e = textureSample(t_in, s_in, uv                       ).rgb;
    let f = textureSample(t_in, s_in, uv + vec2<f32>( tx,  0.0)).rgb;
    let g = textureSample(t_in, s_in, uv + vec2<f32>(-tx,  ty)).rgb;
    let h = textureSample(t_in, s_in, uv + vec2<f32>( 0.0,  ty)).rgb;
    let i = textureSample(t_in, s_in, uv + vec2<f32>( tx,  ty)).rgb;

    let j = textureSample(t_in, s_in, uv + vec2<f32>(-0.5 * tx, -0.5 * ty)).rgb;
    let k = textureSample(t_in, s_in, uv + vec2<f32>( 0.5 * tx, -0.5 * ty)).rgb;
    let l = textureSample(t_in, s_in, uv + vec2<f32>(-0.5 * tx,  0.5 * ty)).rgb;
    let m = textureSample(t_in, s_in, uv + vec2<f32>( 0.5 * tx,  0.5 * ty)).rgb;

    // 重み: 内側 4 点(j,k,l,m)=0.5、外周の 4 ブロックそれぞれ 0.125 を配分。
    var col = e * 0.125;
    col += (a + c + g + i) * 0.03125;
    col += (b + d + f + h) * 0.0625;
    col += (j + k + l + m) * 0.125;
    return vec4<f32>(col, 1.0);
}
