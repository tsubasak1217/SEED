// ============================================================
//  water_shade_dispatch.wgsl — 水面シェーディングの既定ディスパッチ（Phase W8）
//
//  ## 役割（単一責任）
//  契約関数 `water_shade_entry` の実装を **1 本だけ** 供給する。本ファイルは
//  「その水域にシェーディングアセットが指定されていないとき」に連結される既定版で、
//  常にエンジン標準の `water_shade_default` へ直行する。
//  型・定数・標準実装は `water_shading_contract.wgsl` が定義する。
//
//  ## アセット指定時の差し替え
//  水域の `surface_shader` にアセットが指定されているとき、Rust 側
//  （`renderer/water/shading_asset.rs`）が **本ファイルの代わりに**
//  「アセット本体（`water_shade` の定義）＋ 生成した `water_shade_entry`」を連結する。
//  生成される `water_shade_entry` は
//      `return water_shade_nan_guard(water_shade(input));`
//  であり、ユーザーの返り値には必ず NaN/Inf/負値のガードが掛かる。
//  アセットが `water_shade` を定義していない場合は生成側も `water_shade_default` へ落ちる。
//
//  どちらの経路でも `water_shade_entry` の定義は連結全体でちょうど 1 本であり、
//  二重定義にはならない（本ファイルと生成版は排他）。
//
//  ## なぜ既定経路にガードを掛けないのか
//  `water_shade_nan_guard` は「**ユーザーが書いたアセットの返り値**をエンジンへ入れる前に
//  通す門」であって、エンジン自身の標準実装には不要である。標準実装の出力は理論上
//  非負・有限だが、ガードは `WATER_SHADE_RADIANCE_MAX` での上限クランプも行うため、
//  極端な HDR スパイクだけは値が変わり得る。
//  「アセットを指定していない水は W7 以前と 1 ビットも変わらない」という設計要件に
//  疑いを残さないため、既定経路には一切ガードを挟まない
//  （`shading_dispatch.wgsl` のモデル 0 と同じ流儀）。
//
//  ## 連結順序
//  `water_shading_contract.wgsl` の**直後**、`water_surface.wgsl` の**直前**。
//
//  ## 依存
//   - water_shading_contract.wgsl : WaterShadeInput / water_shade_default
// ============================================================

/// 水面 1 フラグメントの最終色（リニア HDR rgb ＋ アルファ）を返す契約関数。
/// 既定版はエンジン標準の水の見た目を素通しする。
fn water_shade_entry(input: WaterShadeInput) -> vec4<f32> {
    return water_shade_default(input);
}
