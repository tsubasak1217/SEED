// ============================================================
//  post_bloom_prefilter.wgsl — ブルーム しきい値抽出（ソフトニー）
//
//  シーン HDR から高輝度成分だけを取り出してブルームチェーンの入力にする
//  （Phase R4）。しきい値付近を硬く切ると縁がちらつくため、ソフトニー
//  （knee 幅の二次カーブ）でなだらかに立ち上げる（Unity / CoD と同系）。
//
//  連結順（post_bloom_prefilter.toml の shader_sources）:
//    fullscreen.wgsl（頂点 fs_vs / FsOut）→ 本ファイル。
//
//  group 0: パラメータ UBO（しきい値・ニー幅）
//  group 1: 入力 HDR テクスチャ + サンプラー
//
//  出力は半解像度のブルーム mip0（HDR フォーマット）。
// ============================================================

/// プレフィルタパラメータ（CPU 側 BloomPrefilterParams と #[repr(C)] 一致）。
struct BloomPrefilterParams {
    /// 抽出しきい値（この輝度未満は 0 に落とす）。
    threshold: f32,
    /// ソフトニー幅の係数（0..1）。threshold*knee がなだらかな遷移幅になる。
    knee:      f32,
    _pad0:     f32,
    _pad1:     f32,
}
@group(0) @binding(0) var<uniform> u_pf: BloomPrefilterParams;

@group(1) @binding(0) var t_in: texture_2d<f32>;
@group(1) @binding(1) var s_in: sampler;

@fragment
fn bloom_prefilter_fs(in: FsOut) -> @location(0) vec4<f32> {
    let c = textureSample(t_in, s_in, in.uv).rgb;
    // 最大チャンネルを輝度指標に使う（色付き高輝度も拾う）。
    let br = max(c.r, max(c.g, c.b));

    // ── ソフトニーによるしきい値応答 ──────────────────────────
    //   knee 幅 = threshold * knee。threshold の下側 knee から上側 knee にかけて
    //   二次カーブで 0→(br-threshold) へ立ち上げ、ハードカットの縁ちらつきを抑える。
    let knee = u_pf.threshold * u_pf.knee;
    var soft = br - u_pf.threshold + knee;
    soft = clamp(soft, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 1e-4);
    // ハードしきい値応答（threshold 超過分）とソフト応答の大きい方を採用。
    let contrib = max(soft, br - u_pf.threshold) / max(br, 1e-4);

    return vec4<f32>(c * contrib, 1.0);
}
