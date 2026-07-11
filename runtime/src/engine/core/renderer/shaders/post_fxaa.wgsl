// ============================================================
//  post_fxaa.wgsl — FXAA（最終 LDR パス）／プレゼントコピー
//
//  トーンマップ済み LDR 中間バッファ（＋その上に合成した 2D UI オーバーレイ）を
//  スワップチェーンへ書き出す最終段（Phase R4）。`enabled` が真なら FXAA で
//  エッジをなめらかにし、偽なら単純コピー（プレゼント）する。
//
//  この最終段は常に 1 パス走る（2D オーバーレイをトーンマップ後の LDR へ描く
//  ための橋渡しを兼ねるため）。FXAA 無効時は中央 1 タップのコピーのみで安価。
//
//  FXAA は Timothy Lottes 版の標準的な簡易実装（FXAA 3.x 相当の品質）。
//  入力はトーンマップ済みリニア LDR（[0,1] 近傍）。輝度はリニア RGB から算出する
//  （厳密には sRGB 空間で行うのが理想だが、標準実装として許容範囲）。出力先が
//  sRGB スワップチェーンの場合、GPU が書き込み時に linear→sRGB エンコードする。
//
//  連結順（post_fxaa.toml の shader_sources）:
//    fullscreen.wgsl（頂点 fs_vs / FsOut）→ 本ファイル。
//
//  group 0: パラメータ UBO（逆解像度・有効フラグ）
//  group 1: 入力 LDR テクスチャ + サンプラー（リニアフィルタ）
// ============================================================

/// FXAA パラメータ（CPU 側 FxaaParams と #[repr(C)] 一致）。
struct FxaaParams {
    /// 1 テクセルサイズ（1/幅, 1/高さ）。近傍サンプルのオフセットに使う。
    inv_res: vec2<f32>,
    /// FXAA 有効フラグ（0=単純コピー, 1=FXAA 適用）。
    enabled: u32,
    _pad:    u32,
}
@group(0) @binding(0) var<uniform> u_fx: FxaaParams;

@group(1) @binding(0) var t_in: texture_2d<f32>;
@group(1) @binding(1) var s_in: sampler;

// ─── FXAA チューニング定数 ─────────────────────────────────────
/// エッジ探索のサブピクセル移動量の上限（テクセル単位）。
const FXAA_SPAN_MAX:    f32 = 8.0;
/// 方向ベクトル低減の乗算係数（局所コントラストに応じた減衰）。
const FXAA_REDUCE_MUL:  f32 = 1.0 / 8.0;
/// 方向ベクトル低減の下限（0 割り回避）。
const FXAA_REDUCE_MIN:  f32 = 1.0 / 128.0;

/// リニア RGB からの知覚輝度（FXAA の標準係数）。
fn fxaa_luma(c: vec3<f32>) -> f32 {
    return dot(c, vec3<f32>(0.299, 0.587, 0.114));
}

@fragment
fn fxaa_fs(in: FsOut) -> @location(0) vec4<f32> {
    let uv  = in.uv;
    let inv = u_fx.inv_res;

    // FXAA 無効時は中央 1 タップのコピー（プレゼント）。
    if (u_fx.enabled == 0u) {
        return textureSample(t_in, s_in, uv);
    }

    // ── 近傍 4 隅＋中央の輝度を求める ──────────────────────────
    let rgb_nw = textureSample(t_in, s_in, uv + vec2<f32>(-inv.x, -inv.y)).rgb;
    let rgb_ne = textureSample(t_in, s_in, uv + vec2<f32>( inv.x, -inv.y)).rgb;
    let rgb_sw = textureSample(t_in, s_in, uv + vec2<f32>(-inv.x,  inv.y)).rgb;
    let rgb_se = textureSample(t_in, s_in, uv + vec2<f32>( inv.x,  inv.y)).rgb;
    let rgb_m  = textureSample(t_in, s_in, uv).rgb;

    let luma_nw = fxaa_luma(rgb_nw);
    let luma_ne = fxaa_luma(rgb_ne);
    let luma_sw = fxaa_luma(rgb_sw);
    let luma_se = fxaa_luma(rgb_se);
    let luma_m  = fxaa_luma(rgb_m);

    let luma_min = min(luma_m, min(min(luma_nw, luma_ne), min(luma_sw, luma_se)));
    let luma_max = max(luma_m, max(max(luma_nw, luma_ne), max(luma_sw, luma_se)));

    // ── エッジ方向（輝度勾配に直交する走査方向）を求める ──────
    var dir: vec2<f32>;
    dir.x = -((luma_nw + luma_ne) - (luma_sw + luma_se));
    dir.y =  ((luma_nw + luma_sw) - (luma_ne + luma_se));

    // 局所コントラストが低いほど走査量を抑える（過剰ぼけ防止）。
    let dir_reduce = max(
        (luma_nw + luma_ne + luma_sw + luma_se) * 0.25 * FXAA_REDUCE_MUL,
        FXAA_REDUCE_MIN,
    );
    let rcp_dir_min = 1.0 / (min(abs(dir.x), abs(dir.y)) + dir_reduce);
    dir = clamp(dir * rcp_dir_min, vec2<f32>(-FXAA_SPAN_MAX), vec2<f32>(FXAA_SPAN_MAX)) * inv;

    // ── 走査方向に沿ってサンプルして混合 ──────────────────────
    let rgb_a = 0.5 * (
        textureSample(t_in, s_in, uv + dir * (1.0 / 3.0 - 0.5)).rgb +
        textureSample(t_in, s_in, uv + dir * (2.0 / 3.0 - 0.5)).rgb
    );
    let rgb_b = rgb_a * 0.5 + 0.25 * (
        textureSample(t_in, s_in, uv + dir * -0.5).rgb +
        textureSample(t_in, s_in, uv + dir *  0.5).rgb
    );

    // 端の 2 タップ混合(rgb_b)が近傍輝度範囲を外れたら中央寄り(rgb_a)へフォールバック。
    let luma_b = fxaa_luma(rgb_b);
    if (luma_b < luma_min || luma_b > luma_max) {
        return vec4<f32>(rgb_a, 1.0);
    }
    return vec4<f32>(rgb_b, 1.0);
}
