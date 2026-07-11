// ============================================================
//  post_vignette.wgsl — ビネット ポストパス（サンプル実装）
//
//  ポストパス抽象（RTプール＋入力テクスチャ＋任意マスク＋パラメータ UBO）の
//  実証用サンプル（Phase R3）。入力テクスチャの周辺を暗くする。
//  トーンマップの前段に挿して「チェーン実行（前段出力→次段入力）」を実証する。
//
//  連結順（post_vignette.toml の shader_sources）:
//    fullscreen.wgsl（頂点 fs_vs / FsOut）→ 本ファイル。
//
//  group 0: パラメータ UBO（強度・半径・柔らかさ）
//  group 1: 入力テクスチャ + サンプラー
//  group 2: マスクテクスチャ + サンプラー
//           マスクは常にバインド可能（未指定時は白 1x1 = 全面適用）。
//           マスク値（R）が小さいほどビネットを弱める＝「マスクのかけやすさ」の担保。
// ============================================================

/// ビネットパラメータ（CPU 側 VignetteParams と #[repr(C)] 一致）。
struct VignetteParams {
    /// 効果の強さ（0=無効, 1=標準）
    intensity: f32,
    /// 効果の開始半径（画面中心からの正規化距離 0..1、既定 0.5）
    radius:    f32,
    /// 減衰の柔らかさ（既定 0.5）
    softness:  f32,
    _pad:      f32,
}
@group(0) @binding(0) var<uniform> u_vig: VignetteParams;

@group(1) @binding(0) var t_in: texture_2d<f32>;
@group(1) @binding(1) var s_in: sampler;

@group(2) @binding(0) var t_mask: texture_2d<f32>;
@group(2) @binding(1) var s_mask: sampler;

@fragment
fn vig_fs(in: FsOut) -> @location(0) vec4<f32> {
    let col  = textureSample(t_in,   s_in,   in.uv);
    let mask = textureSample(t_mask, s_mask, in.uv).r;
    // 画面中心からの距離（対角で 1.0 に正規化。アスペクト無視の簡易版・土台）。
    let d = distance(in.uv, vec2<f32>(0.5, 0.5)) * 1.41421356;
    // radius から radius+softness にかけて 0→1 で暗くする係数を上げる。
    let v      = smoothstep(u_vig.radius, u_vig.radius + max(u_vig.softness, 1e-3), d);
    let darken = 1.0 - u_vig.intensity * v * mask;
    return vec4<f32>(col.rgb * darken, col.a);
}
