// ============================================================
//  shader_wboit.wgsl — Weighted Blended OIT フラグメント（Phase R5）
//
//  順序独立透明描画（McGuire & Bavoil 2013）の書き込み側。
//  ライティングは shader_fragment.wgsl の shade_pbr() を共有し、その結果へ
//  深度依存の重み w を掛けて 2 枚の MRT へ蓄積する:
//    - @location(0) accum : Rgba16Float。sum(color.rgb * a * w, a * w)。加算合成。
//    - @location(1) reveal: R16Float。   prod(1 - a)。 blend=(Zero, OneMinusSrc)。
//  合成は post_wboit_composite.wgsl が accum/reveal から行う。
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
    /// 重み付き色蓄積（premultiplied color * weight, alpha * weight）。
    @location(0) accum:  vec4<f32>,
    /// 透過率の積（1 - a の積）。R チャンネルのみ使用。
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

    // ── ガラス合成（屈折・透過率・アルファ。refract_common.wgsl の共有関数）─────────────
    // premultiplied 色（被覆込み）と実効被覆 a_eff を作る。
    //   transmission=0 かつ 屈折オフ: 従来どおり（premult=c.rgb*a, a_eff=a）。
    //     → 合成 avg=accum.rgb/accum.a=c.rgb, final=avg*(1-reveal)=c.rgb*被覆＝従来と一致（パリティ）。
    //   transmission=0 かつ 屈折オン: 背景をガラス越しに歪めた透過成分を premult へ加え、
    //     a_eff=1 として背景をこのフラグメントで確定表示する（reveal→(1-1)=0 で dst 背景を隠す）。
    //   transmission>0: フレネルで反射／透過を配分（glass_composite 内）。
    let g = glass_composite(c.rgb, a, in, front_facing);

    var out: WboitOut;
    // premultiplied color に重みを掛けて蓄積（加算合成: One/One）。accum.a は合成の正規化に使う。
    out.accum  = vec4<f32>(g.premult, g.a_eff) * w;
    // reveal は (1 - a_eff) の積。blend=(Zero, OneMinusSrc) により dst *= (1 - a_eff) となる。
    out.reveal = vec4<f32>(g.a_eff, g.a_eff, g.a_eff, g.a_eff);
    return out;
}
