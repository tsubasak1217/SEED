// ============================================================
//  shader_wboit.wgsl — Weighted Blended OIT フラグメント（Phase R5）
//
//  順序独立透明描画（McGuire & Bavoil 2013）の書き込み側。
//  ライティングは shader_fragment.wgsl の shade_pbr() を共有し、その結果へ
//  深度依存の重み w を掛けて 2 枚の MRT へ蓄積する:
//    - @location(0) accum : Rgba16Float。sum(self.rgb * a * w, a * w)。加算合成（One/One）。
//    - @location(1) reveal: Rgba16Float。prod(T_frag)（per-channel 色付き透過率）を rgb に、
//      スカラーの prod(1 - a) を a に積む。 blend=(Dst, Zero)＝dst *= src（乗算累積）。
//  合成は post_wboit_composite.wgsl が accum/reveal から行う。
//
//  【色付き透過率（per-channel transmittance）方式】
//    従来は reveal をスカラー Π(1-a) にし、背景を各屈折フラグメントで焼き込んでいたため、
//    同色の半透明が重なると足し算的に明るくなった（赤カーテンのシワ）。本方式では
//    背景を焼き込まず、各フラグメントが per-channel 透過率 T_frag を出し、reveal に
//    Π T_frag（乗算累積）を積む。合成パスが背景 HDR に Π T_frag を乗算するため、
//    純赤フィルタ T=(1,0,0) は Π で飽和し、何枚重ねても「1赤+1赤=1赤」になる（乗算は可換＝順序独立）。
//    詳細は refract_common.wgsl の glass_composite_wboit を参照。
//
//  連結順（transparency.rs のリゾルバ）:
//    shader_common → shadow → rt_shadow_off → (static|skinned)_vertex →
//    shader_fragment（shade_pbr 提供）→ 本ファイル。
//  ※ shader_fragment.wgsl の fs_main も同一モジュールに含まれるが、
//    パイプラインは fragment_entry = "fs_wboit" を指定するため未使用。
// ============================================================

// ── McGuire 重み関数の定数（マジックナンバー回避）──────────────
/// アルファに掛ける前段スケール（alpha を強調して近距離寄与を確保）。
const WBOIT_ALPHA_SCALE:  f32 = 10.0;
/// アルファ前段バイアス（極小アルファでも最小重みを持たせる）。
const WBOIT_ALPHA_BIAS:   f32 = 0.01;
/// 深度重みの全体スケール（近距離を強く優先させる係数）。
const WBOIT_DEPTH_SCALE:  f32 = 1.0e8;
/// 深度 z（0..1）に掛ける係数。1 に近い遠方ほど重みを急減させる。
const WBOIT_DEPTH_Z_BIAS: f32 = 0.9;
/// 重みの下限（数値的ゼロ割れ防止）。
const WBOIT_W_MIN:        f32 = 1.0e-2;
/// 重みの上限（近距離・高アルファでの発散防止）。
const WBOIT_W_MAX:        f32 = 3.0e3;

/// WBOIT の 2 ターゲット出力（accum / reveal）。
struct WboitOut {
    /// 重み付き自色蓄積（self.rgb * a * weight, a * weight）。加算合成（One/One）。
    @location(0) accum:  vec4<f32>,
    /// 色付き透過率の積: rgb = Π T_frag（per-channel）、a = Π(1 - a)（スカラー）。
    /// blend=(Dst, Zero) により dst *= src（チャンネルごとの乗算累積）。
    @location(1) reveal: vec4<f32>,
}

// ── WBOIT フラグメントエントリ ────────────────────────────────
// 深度は VertexOutput.clip_pos（@builtin(position)）を利用する。
// フラグメントステージでは clip_pos はフレームバッファ座標になり、
// .z がその断片の深度（0..1）である。専用の @builtin(position) 引数を
// 追加すると builtin が重複して検証エラーになるため VertexOutput から読む。
// front_facing は両面描画（cull_mode = None）マテリアルの裏面で false になり、
// shade_pbr が法線を反転する（不透明パスの fs_main と同じ扱い）。
@fragment
fn fs_wboit(in: VertexOutput, @builtin(front_facing) front_facing: bool) -> WboitOut {
    // shade_pbr は shader_fragment.wgsl 由来。ライティング結果 rgb と
    // マテリアルアルファ a を返す（Mask discard も内部で処理される）。
    let c = shade_pbr(in, front_facing);
    let a = clamp(c.a, 0.0, 1.0);

    // 断片深度（0..1）。1 に近いほど遠方。
    let z = in.clip_pos.z;

    // McGuire/Bavoil の深度依存重み。近距離・高アルファほど大きな重みを持つ。
    let depth_term = pow(1.0 - z * WBOIT_DEPTH_Z_BIAS, 3.0);
    let alpha_term = pow(min(1.0, a * WBOIT_ALPHA_SCALE) + WBOIT_ALPHA_BIAS, 3.0);
    let w = clamp(alpha_term * WBOIT_DEPTH_SCALE * depth_term, WBOIT_W_MIN, WBOIT_W_MAX);

    // ── 色付き透過率 WBOIT 合成（refract_common.wgsl の共有関数）─────────────
    // 自色 self_lit（背景を含まない）・被覆 a_eff・per-channel 透過率 transmit を得る。
    //   transmission=0（素の Blend）: transmit=(1-a) のスカラー＝従来 WBOIT の revealage と一致（パリティ）。
    //   transmission>0（色付きガラス）: transmit=(1-a)+a*tr*albedo で背景を色フィルタとして減衰させる。
    //   ※ WBOIT は順序独立のため屈折の曲がった背景は焼き込まない（合成パスで Π transmit を背景へ乗算）。
    let g = glass_composite_wboit(c.rgb, a);

    var out: WboitOut;
    // 自色に被覆 a_eff と深度重み w を掛けて蓄積（加算合成: One/One）。
    // 合成パスで avg = accum.rgb/accum.a = a_eff・w 重み付き平均色に戻す（正規化＝足し算にならない）。
    out.accum  = vec4<f32>(g.self_lit * g.a_eff, g.a_eff) * w;
    // reveal: rgb に per-channel 透過率 T_frag、a にスカラー (1 - a_eff)。
    // blend=(Dst, Zero) により dst *= src ＝ rgb は Π T_frag、a は Π(1 - a_eff)（乗算累積）。
    out.reveal = vec4<f32>(g.transmit, 1.0 - g.a_eff);
    return out;
}
