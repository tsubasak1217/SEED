// ============================================================
//  post_wboit_composite.wgsl — WBOIT 合成パス（色付き透過率＋屈折歪み / 凸結合ハイブリッド）
//
//  shader_wboit.wgsl が蓄積した accum / reveal から順序独立の透明結果を復元し、
//  シーン HDR の上へ**チャンネルごとの凸結合（lerp）**で合成する:
//    avg    = accum.rgb / max(accum.a, eps)   // 透明色の重み平均（曲がった背景を焼き込んだ premult の平均）
//    T_rgb  = reveal.rgb                       // Π T_frag（per-channel の背景透過率）
//    final_c = scene_hdr_c * T_rgb_c  +  avg_c * (1 - T_rgb_c)   （c = R,G,B 各チャンネル）
//
//  【なぜ凸結合（加算ではなく lerp）か】
//    premult には屈折の曲がった背景が焼き込まれている（ガラスの存在感を保つため）。単純な
//    加算（scene*T + avg*coverage）だと、重なった層ぶん背景が二重計上されて明るくなる。
//    凸結合 final = lerp(avg, scene, T) はエネルギーが min/max(scene,avg) の範囲に収まるため、
//    背景が焼き込まれていても足し算的に明るくならない。N 枚重なると T_c が下がって
//    avg（曲がった背景の重み平均＝カーテン色）へ漸近するだけ（純赤 T=(1,0,0) は N 非依存＝「1赤+1赤≈1赤」）。
//    ※ 素の Blend（T=1-a のスカラー）では final = avg*(1-(1-a)^N) + scene*(1-a)^N ＝従来スカラー WBOIT と一致（パリティ）。
//
//  【なぜ 2 パスか】
//    final_c = scene_c*T_c（dst の per-channel 乗算）+ avg_c*(1-T_c)（src の per-channel 加算項）は、
//    「dst に掛ける per-channel 係数 T」と「加算する per-channel ベクトル avg*(1-T)」の 2 つの
//    独立した per-channel 量を要する。合成先（シーン HDR）は書き込み対象の dst であり
//    テクスチャとしてサンプルできない（フィードバック不可）ため in-shader で 1 パス lerp は組めず、
//    dual-source ブレンド（本エンジンは feature 未要求）も使えない。よって 2 パスに分ける:
//      パス1（背景濾過）: blend=WboitBgMultiply（src=Zero, dst=Src）で dst = scene * T_rgb。
//      パス2（透明色混合）: blend=Additive（src=One, dst=One）で dst += avg * (1 - T_rgb)。
//    合計 = scene*T + avg*(1-T) ＝ チャンネルごとの凸結合。パス1→パス2 の順序が必須。
//    どちらも LoadOp::Load の同一 HDR へ順に描く。
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

@group(0) @binding(0) var t_accum:  texture_2d<f32>;
@group(0) @binding(1) var s_accum:  sampler;

@group(1) @binding(0) var t_reveal: texture_2d<f32>;
@group(1) @binding(1) var s_reveal: sampler;

/// パス1: 背景濾過。reveal.rgb = Π T_frag を出力し、blend=WboitBgMultiply（src=Zero, dst=Src）で
/// シーン HDR（dst）へ乗算する（dst = scene * T_rgb）。透明物の無いピクセルは
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

/// パス2: 透明色混合。凸結合の src 項 avg * (1 - T_rgb)（per-channel）を出力し、
/// blend=Additive（src=One, dst=One）で背景濾過済み HDR（= scene * T_rgb）へ加算する。
/// 合計 = scene*T_rgb + avg*(1-T_rgb) ＝ チャンネルごとの凸結合 lerp(avg, scene, T_rgb)。
@fragment
fn composite_self_fs(in: FsOut) -> @location(0) vec4<f32> {
    let accum = textureSample(t_accum, s_accum, in.uv);
    // 透明色の寄与が無い（重み 0）ピクセルは加算 0 のため discard で省く。
    if accum.a < WBOIT_EPSILON {
        discard;
    }
    let reveal = textureSample(t_reveal, s_reveal, in.uv);
    // 重み平均（曲がった背景を焼き込んだ premult の平均）。accum.rgb は premult*w の和、accum.a は a_eff*w の和。
    // 正規化（÷accum.a）なので、層が増えても総和のようには膨らまない（重なりの二重計上を防ぐ核心）。
    let avg = accum.rgb / max(accum.a, WBOIT_EPSILON);
    // 凸結合の混合係数 (1 - T_rgb)（per-channel）。T=1（素通り）で 0、T=0（不透過）で 1。
    // 純赤 T=(1,0,0) → (0,1,1): 赤は scene が素通り、緑青は avg（カーテン色）＝ N 非依存。
    let mix_self = clamp(vec3<f32>(1.0) - reveal.rgb, vec3<f32>(0.0), vec3<f32>(1.0));
    // src.rgb = avg * (1 - T_rgb)。blend=Additive → dst_new = scene*T_rgb + avg*(1-T_rgb)。
    // alpha は src=One,dst=One だが src.a=0 のため dst.a は不変（背景濾過パスと同じく HDR.a を保つ）。
    return vec4<f32>(avg * mix_self, 0.0);
}
