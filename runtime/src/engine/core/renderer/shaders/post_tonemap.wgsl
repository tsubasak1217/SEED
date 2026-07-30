// ============================================================
//  post_tonemap.wgsl — トーンマップ ポストパス（HDR → 出力）
//
//  シーンの HDR オフスクリーン（Rgba16Float）をサンプルし、トーンマップ演算子
//  （既定: 輝度ベース Reinhard）で圧縮してスワップチェーンへ書き出す。
//  各メッシュシェーダ内で個別に行っていた Reinhard をここへ一元化した（Phase R3）。
//
//  連結順（post_tonemap.toml の shader_sources）:
//    fullscreen.wgsl（頂点 fs_vs / FsOut）→ postfx_common.wgsl（健全化）
//    → tonemap_ops.wgsl（演算子）→ 本ファイル。
//
//  group 0: パラメータ UBO（演算子・露出）
//  group 1: 入力 HDR テクスチャ + サンプラー
//
//  出力はリニア色。スワップチェーンが sRGB フォーマットの場合は GPU が
//  レンダーターゲット書き込み時に linear → sRGB エンコードを自動適用する。
// ============================================================

/// トーンマップパラメータ（CPU 側 TonemapParams と #[repr(C)] 一致）。
struct TonemapParams {
    /// 演算子 ID（tonemap_ops.wgsl の TONEMAP_* 定数）。
    /// ※ WGSL では `operator` が予約語のため `op` とする（CPU 側 TonemapParams.operator と
    ///   バイトレイアウトは一致。フィールド名は言語間で独立）。
    op:       u32,
    /// 露出倍率（トーンマップ前に乗算。既定 1.0。将来の露出制御用）
    exposure: f32,
    _pad0:    u32,
    _pad1:    u32,
}
@group(0) @binding(0) var<uniform> u_tm: TonemapParams;

@group(1) @binding(0) var t_hdr: texture_2d<f32>;
@group(1) @binding(1) var s_hdr: sampler;

@fragment
fn tm_fs(in: FsOut) -> @location(0) vec4<f32> {
    // 入力を健全化してから露出を掛ける（postfx_common.wgsl）。
    // ここが LDR 出力への最後の関門であり、上流のどこかで生まれた ±Inf / NaN を
    // そのままトーンマップ演算子へ入れると（Inf/Inf 等で）NaN になり、出力が
    // 「黒い塗り潰し」になる。有限域に落としておけば必ず妥当な明色に飽和する。
    let hdr    = postfx_sanitize_hdr(textureSample(t_hdr, s_hdr, in.uv).rgb) * u_tm.exposure;
    let mapped = tonemap_apply(hdr, u_tm.op);
    return vec4<f32>(mapped, 1.0);
}
