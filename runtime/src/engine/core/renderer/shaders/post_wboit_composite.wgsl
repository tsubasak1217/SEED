// ============================================================
//  post_wboit_composite.wgsl — WBOIT 合成パス（色付き透過率＋屈折歪み / 曲げと色フィルタの分離）
//
//  shader_wboit.wgsl が蓄積した accum / reveal から順序独立の透明結果を復元し、
//  「曲げ（幾何）」と「色フィルタ（分光透過）」を分離してシーン HDR の上へ合成する:
//    avg   = accum.rgb / max(accum.a, eps)   // 透明色の重み平均（生の曲がった背景 bent_bg + 自色。tint 非乗算）
//    T_rgb = reveal.rgb                       // Π T_frag（per-channel の色フィルタ）
//    C     = 1 - reveal.a                     // スカラーのカバレッジ（reveal.a = Π(1-a)）。曲げの度合い
//    bent  = lerp(scene, avg, C)              // 背景を直進 scene から曲がった avg へ C 分だけ置き換える
//    final = bent * T_rgb                      // 置き換え後の背景を per-channel 色フィルタで着色
//
//  【なぜこの分離か（前案の欠陥修正）】
//    前案は final_c = scene_c*ΠT_c + avg_c*(1-ΠT_c) のチャンネルごとの凸結合だったが、
//    透過率の高いチャンネル（シアンガラスの G/B 等）は scene（直進背景）がほぼ全量を占め、
//    avg（曲がった背景）の重み (1-ΠT_c)≈0 となって歪みが消えた（＝よく通す色ほど歪まない矛盾）。
//    本方式は曲げをスカラー C（色に依らない幾何）で行い、色フィルタ ΠT を bent 全体へ掛けるため、
//    よく通す色チャンネルでも曲がった背景が見える。N 枚重ねても ΠT は飽和（純赤 T=(1,0,0) は N 非依存）、
//    avg は重み平均で膨らまないため足し算的増強も起きない。
//
//  【自色（スペキュラ等）は avg に束ねてあり、まとめて ×ΠT が掛かる】
//    自色専用の 3 枚目 MRT を避けるため自色を avg に含める。合成で ×ΠT されるため自色がガラス色で
//    わずかに色付くが、スペキュラ寄与は微小で許容。詳細は refract_common.wgsl の glass_composite_wboit。
//
//  【半透明 a<1 のフェード端の軽微な過減衰（許容）】
//    素の Blend では C と ΠT の双方に (1-a) が効き、自色/背景が二重に減衰する（scene は (1-a)²）。
//    有界かつ単調で、屈折歪みを正しく出すためのトレードオフとして許容する。
//
//  【なぜ 2 パスか】
//    final = scene*(1-C)*ΠT + avg*C*ΠT は「dst に掛ける per-channel 係数 (1-C)*ΠT」と
//    「加算する per-channel ベクトル avg*C*ΠT」の 2 つを要する。合成先シーン HDR は書き込み対象の
//    dst でありサンプル不可（フィードバック不可）、dual-source ブレンドも feature 未要求のため 2 パス:
//      パス1（背景の曲げ＋濾過）: blend=WboitBgMultiply（src=Zero, dst=Src）で dst = scene * (1-C)*ΠT。
//                                 (1-C) = reveal.a なので src.rgb = reveal.a * reveal.rgb。
//      パス2（曲がった透明色の加算）: blend=Additive（src=One, dst=One）で dst += avg * C*ΠT。
//    合計 = scene*(1-C)*ΠT + avg*C*ΠT = [scene*(1-C)+avg*C]*ΠT = lerp(scene,avg,C)*ΠT。順序必須。
//    どちらも LoadOp::Load の同一 HDR へ順に描く（**ブルーム前＝トーンマップ前**）。
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

/// 透明物「自身の見え」（自色反射＋曲がった背景）の色フィルタ下限（最低可視度）。
///
/// 【なぜ必要か（黒転バグの根治）】
///   合成は透明物の見え avg（＝自色反射＋曲がった背景）に色フィルタ ΠT を掛ける。ところが
///   補色の透明物が重なると（例: シアンガラス T≈(0,.8,.9) × 赤/不透明ガラス T≈(1,0,0)）
///   ΠT が**全チャンネルで≈0**になり、背景だけでなく**ガラス表面の反射光まで 0 に潰れて
///   ピュアブラック**になる（実機で確認）。物理的には、背景の透過光は補色フィルタで確かに
///   遮断されるが、ガラス表面の反射光（フレネル反射・スペキュラ）は透過光ではないのでガラスの
///   透過率で減衰しない＝完全な黒にはならない。自色反射を別ターゲットに分離するのが厳密だが
///   （MRT 追加コスト）、ここでは「全チャンネルが下限を下回るときだけ灰色リフトで最低可視度を
///   与える」近似で真っ黒を回避する。
///
/// 【重要: 通常の色ガラスは無影響（減彩しない）】
///   リフトは max(全チャンネル) が下限未満のときだけ効く。単色ガラス（例: 純赤 ΠT=(1,0,0)、
///   シアン ΠT=(0,.8,.9)）は明るいチャンネルがあるので lift=0 ＝色そのまま。補色重なり／不透明色
///   （全チャンネル≈0）だけがリフトされ、灰色の最低可視度で見える。
const WBOIT_SELF_MIN_FILTER: f32 = 0.2;

@group(0) @binding(0) var t_accum:  texture_2d<f32>;
@group(0) @binding(1) var s_accum:  sampler;

@group(1) @binding(0) var t_reveal: texture_2d<f32>;
@group(1) @binding(1) var s_reveal: sampler;

/// パス1: 背景の曲げ＋濾過。dst = scene * (1-C)*ΠT を実現する。
/// (1-C) = reveal.a（= Π(1-a)）、ΠT = reveal.rgb なので src.rgb = reveal.a * reveal.rgb。
/// blend=WboitBgMultiply（src=Zero, dst=Src）で dst_new = dst * src.rgb = scene * (1-C)*ΠT。
/// 透明物の無いピクセルは reveal=(1,1,1,1) で (1-C)*ΠT=1 のため乗算恒等。reveal.a≈1 で discard。
/// ※ 背景（透過光）は補色フィルタで正しく遮断されるべきなので下限は掛けない（パス2 の自色のみ下限）。
@fragment
fn composite_bg_fs(in: FsOut) -> @location(0) vec4<f32> {
    let reveal = textureSample(t_reveal, s_reveal, in.uv);
    // reveal.a ≈ 1（Π(1-a)≈1 ＝ 被覆ゼロ）は透明フラグメントが無い。乗算 scene*1 は恒等なので discard。
    if reveal.a > 1.0 - WBOIT_EPSILON {
        discard;
    }
    // src.rgb = (1-C)*ΠT = reveal.a * reveal.rgb。blend=(Zero, Src) → dst *= src.rgb。
    // alpha は blend=(Zero, One) 側で dst.a を保持するため src.a は影響しない（1 を置く）。
    return vec4<f32>(reveal.a * reveal.rgb, 1.0);
}

/// パス2: 透明物「自身の見え」の加算。dst += avg * C * max(ΠT, F0) を実現する。
/// C = 1 - reveal.a、ΠT = reveal.rgb、avg = accum.rgb/max(accum.a,ε)。
/// 色フィルタに下限 WBOIT_SELF_MIN_FILTER（誘電体反射 F0）を設け、補色重なりで ΠT→0 でも
/// ガラスの見えが真っ黒に潰れないようにする（表面反射は透過率で消えないことの近似）。
/// blend=Additive（src=One, dst=One）で dst_new = scene*(1-C)*ΠT + avg*C*max(ΠT,F0)。
@fragment
fn composite_self_fs(in: FsOut) -> @location(0) vec4<f32> {
    let accum = textureSample(t_accum, s_accum, in.uv);
    // 透明色の寄与が無い（重み 0）ピクセルは加算 0 のため discard で省く。
    if accum.a < WBOIT_EPSILON {
        discard;
    }
    let reveal = textureSample(t_reveal, s_reveal, in.uv);
    // 重み平均（生の曲がった背景 bent_bg + 自色反射）。accum.rgb は premult*w の和、accum.a は a_eff*w の和。
    let avg = accum.rgb / max(accum.a, WBOIT_EPSILON);
    // カバレッジ C = 1 - Π(1-a)（reveal.a に Π(1-a) を保存済み）。ガラスがあれば 1 に近づく。
    let coverage = clamp(1.0 - reveal.a, 0.0, 1.0);
    // 全チャンネルが暗い（補色重なり／不透明色ガラス＝黒転条件）ときだけ灰色リフトで最低可視度を与える。
    // 明るいチャンネルを持つ通常の色ガラスは lift=0 ＝色そのまま（減彩なし）。
    let max_ch = max(reveal.r, max(reveal.g, reveal.b));
    let lift   = max(WBOIT_SELF_MIN_FILTER - max_ch, 0.0);
    let filt   = reveal.rgb + vec3<f32>(lift);
    // src.rgb = avg * C * filt。alpha は src.a=0 で dst.a 不変（背景の曲げ濾過パスと同じく HDR.a を保つ）。
    return vec4<f32>(avg * coverage * filt, 0.0);
}
