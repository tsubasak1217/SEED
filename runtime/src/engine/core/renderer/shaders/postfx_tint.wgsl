// ============================================================
//  postfx_tint.wgsl — テクスチャ単位ポスト「tint（乗算色）」＋ ingest コピー
//
//  .postfx の tint エフェクト。入力テクスチャに定数色を乗算する。
//  color=[1,1,1,1]（白）にすれば恒等コピーになるため、チェーン先頭の
//  「sRGB ベーステクスチャ → リニア HDR 作業バッファ」取り込み（ingest）にも流用する。
//
//  連結順（postfx_tint.toml の shader_sources）:
//    fullscreen.wgsl（頂点 fs_vs / FsOut）→ 本ファイル。
//
//  group 0: パラメータ UBO（乗算色 RGBA）
//  group 1: 入力テクスチャ + サンプラー
//  group 2: マスクテクスチャ + サンプラー（未指定時は白 1x1 = 全面適用）
//           マスク値（R）で tint の効き具合を線形補間する（1=フル適用 / 0=無変化）。
// ============================================================

/// tint パラメータ（CPU 側 TintParams と #[repr(C)] 一致）。
struct TintParams {
    /// 乗算色（RGBA）。
    color: vec4<f32>,
};
@group(0) @binding(0) var<uniform> u_tint: TintParams;

@group(1) @binding(0) var t_in: texture_2d<f32>;
@group(1) @binding(1) var s_in: sampler;

@group(2) @binding(0) var t_mask: texture_2d<f32>;
@group(2) @binding(1) var s_mask: sampler;

@fragment
fn tint_fs(in: FsOut) -> @location(0) vec4<f32> {
    let col  = textureSample(t_in,   s_in,   in.uv);
    let mask = textureSample(t_mask, s_mask, in.uv).r;
    // tint 適用結果とマスクで線形補間（mask=0 なら元の色のまま）。
    let tinted = col * u_tint.color;
    return mix(col, tinted, mask);
}
