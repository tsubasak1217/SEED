// ============================================================
// shader_transparent.wgsl — 距離ソート半透明のフラグメント（Phase RT-Translucency）
//
// 距離ソート（背面→前面）パスの専用フラグメント。連結順の後方（shader_fragment.wgsl で
// shade_pbr/gather_surface、refract_common.wgsl で屈折ヘルパが宣言済み）に置く。
//
// 【ブレンド】premultiplied over（One, OneMinusSrcAlpha）を前提に premultiplied 色を出力する。
//   - 屈折オフ（translucency_rt==0 or ior<=1）: out = (lit*a, a)。
//     premultiplied over は straight over と数学的に等価なので、従来の AlphaBlending（fs_main）
//     と見た目が一致する（Raster モードのパリティ保証）。
//   - 屈折オン: 背景（不透明シーンのコピー）を法線方向へ歪めてサンプルし、ガラス色×(1-a) を
//     透過成分として自前合成する。alpha=1 を出力して背景をフラグメント側で確定させる
//     （固定ブレンドでは dst を屈折後の背景に差し替えられないため）。
// ============================================================

/// 距離ソート半透明フラグメント。premultiplied 色を返す（ブレンドは One/OneMinusSrcAlpha）。
@fragment
fn fs_transparent_sorted(in: VertexOutput, @builtin(front_facing) front_facing: bool) -> @location(0) vec4<f32> {
    // ライティング済みの straight 色＋アルファ（不透明パスと同一の shade_pbr を共有）。
    let c = shade_pbr(in, front_facing);
    let a = clamp(c.a, 0.0, 1.0);

    // ガラス合成（屈折・透過率・アルファを統合。refract_common.wgsl の共有関数）。
    // transmission=0 かつ 屈折オフ → (c*a, a)（従来とビット一致）。
    // transmission=0 かつ 屈折オン → (c*a + bg*tint*(1-a), 1)（従来の屈折合成とビット一致）。
    let g = glass_composite(c.rgb, a, in, front_facing);
    return vec4<f32>(g.premult, g.a_eff);
}
