// ============================================================
//  post_wboit_composite.wgsl — WBOIT 合成パス（色付き透過率 / per-channel transmittance）
//
//  shader_wboit.wgsl が蓄積した accum / reveal から順序独立の透明結果を復元し、
//  シーン HDR の上へ合成する。合成式は McGuire & Bavoil 2013 を per-channel 透過率へ拡張したもの:
//    avg      = accum.rgb / max(accum.a, eps)      // 自色の重み平均（背景を含まない）
//    T_rgb    = reveal.rgb                          // Π T_frag（per-channel の背景透過率）
//    coverage = 1 - mean(T_rgb)                     // 自色の被覆スカラー（後述の飽和量）
//    final    = scene_hdr * T_rgb  +  avg * coverage
//
//  【なぜ 2 パスか】
//    final = scene*T_rgb（per-channel の乗算）+ avg*coverage（加算の自色項）は、
//    「per-channel の乗算係数」と「加算項」の 2 つの独立した per-channel ベクトルを要する。
//    固定機能ブレンドは 1 パスにつき src 由来の per-channel ベクトルを 1 本しか使えず、
//    dual-source ブレンド（本エンジンは feature 未要求）も使えないため、2 パスに分ける:
//      パス1（背景濾過）: blend=(src=Zero, dst=Src) で final = scene * T_rgb（背景を色フィルタの積で減衰）。
//      パス2（自色加算）: blend=Additive（src=One, dst=One）で final += avg * coverage（自色を上乗せ）。
//    どちらも LoadOp::Load の同一 HDR へ順に描く（パス1→パス2 の順序が必須: 自色は T で減衰させない）。
//
//  【coverage に mean(T_rgb) を選ぶ理由（重なり N に対する飽和）】
//    coverage は「N 枚重ねても飽和する量」でなければ「1赤+1赤=1赤」が壊れる。
//    純赤フィルタ T=(1,0,0) は何枚重ねても Π T=(1,0,0) なので:
//      ・1 - mean(T) = 1 - (1+0+0)/3 = 0.667  … N に依存せず一定＝飽和（採用）。
//      ・1 - max(T)  = 1 - 1 = 0               … 自色が完全に消える（赤面の反射が見えない）ため不採用。
//    mean は「バランスの取れた被覆」を与え、不透明（T=0 全チャンネル）で coverage=1、
//    素の Blend（T=1-a のスカラー）で coverage=a となり従来アルファ over と一致する（パリティ）。
//
//  連結順（各 TOML）: fullscreen.wgsl（fs_vs / FsOut）→ 本ファイル。
//
//  group 0: accum テクスチャ + サンプラー
//  group 1: reveal テクスチャ + サンプラー
//  ※ 両エントリ（composite_bg_fs / composite_self_fs）は同一モジュールに同居し、
//    リフレクション（全 global を走査）で両者とも accum(group0)+reveal(group1) の
//    同一 BGL を得る。合成側は 1 組の BindGroup を両パイプラインで共有する。
// ============================================================

/// ゼロ割れ防止の微小値（accum.a が 0 のピクセル向け）と、
/// 完全透過（透明物なし）判定のしきい値に使う。
const WBOIT_EPSILON: f32 = 1.0e-5;

/// coverage 算出の per-channel 平均係数（1/3）。マジックナンバー回避（RGB 3 チャンネルの平均）。
const WBOIT_RGB_INV: f32 = 1.0 / 3.0;

@group(0) @binding(0) var t_accum:  texture_2d<f32>;
@group(0) @binding(1) var s_accum:  sampler;

@group(1) @binding(0) var t_reveal: texture_2d<f32>;
@group(1) @binding(1) var s_reveal: sampler;

/// パス1: 背景濾過。reveal.rgb = Π T_frag を出力し、blend=(src=Zero, dst=Src) で
/// シーン HDR（dst）へ乗算する（final = scene * T_rgb）。透明物の無いピクセルは
/// reveal がクリア値 1（全チャンネル）のままで、乗算しても scene 不変のため discard で省く。
@fragment
fn composite_bg_fs(in: FsOut) -> @location(0) vec4<f32> {
    let reveal = textureSample(t_reveal, s_reveal, in.uv);
    // rgb が全て ≈1 は「そのピクセルに透明フラグメントが無い」= 背景を濾過しない。
    // 乗算 scene*1 は恒等なので discard してシーン HDR（LoadOp::Load）をそのまま残す。
    if reveal.r > 1.0 - WBOIT_EPSILON
        && reveal.g > 1.0 - WBOIT_EPSILON
        && reveal.b > 1.0 - WBOIT_EPSILON {
        discard;
    }
    // src.rgb = T_rgb。blend=(Zero, Src) → dst_new = dst * src.rgb = scene * T_rgb。
    // alpha は blend=(Zero, One) 側で dst.a を保持するため src.a の値は影響しない（1 を置く）。
    return vec4<f32>(reveal.rgb, 1.0);
}

/// パス2: 自色加算。accum を被覆 coverage=1-mean(T_rgb) で重み付けした自色を出力し、
/// blend=Additive（src=One, dst=One）で背景濾過済み HDR へ上乗せする（final += avg * coverage）。
@fragment
fn composite_self_fs(in: FsOut) -> @location(0) vec4<f32> {
    let accum = textureSample(t_accum, s_accum, in.uv);
    // 自色の寄与が無い（重み 0）ピクセルは加算 0 のため discard で省く。
    if accum.a < WBOIT_EPSILON {
        discard;
    }
    let reveal = textureSample(t_reveal, s_reveal, in.uv);
    // 自色の重み平均（背景を含まない）。accum.rgb は self*a_eff*w の和、accum.a は a_eff*w の和。
    let avg = accum.rgb / max(accum.a, WBOIT_EPSILON);
    // 被覆 = 1 - mean(T_rgb)。純赤 T=(1,0,0) で N に依存せず一定（飽和）＝「1赤+1赤=1赤」。
    let mean_t   = (reveal.r + reveal.g + reveal.b) * WBOIT_RGB_INV;
    let coverage = clamp(1.0 - mean_t, 0.0, 1.0);
    // src.rgb = avg * coverage（加算項）。blend=Additive → dst_new = scene*T_rgb + avg*coverage。
    // alpha は src=One,dst=One だが src.a=0 のため dst.a は不変（背景濾過パスと同じく HDR.a を保つ）。
    return vec4<f32>(avg * coverage, 0.0);
}
