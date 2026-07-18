// ============================================================
// refract_ss.wgsl — スクリーンスペース屈折（背景取得のフォールバック実装）
//
// refract_common.wgsl の後ろに連結し、glass_composite から呼ばれる
// `refract_sample_bg(surf, frag_xy, ior)` を **スクリーンスペース近似** で供給する。
// 非 RT 対応 GPU、および translucency≠Rt の経路がこのファイルを連結する
// （RT 対応かつ translucency=Rt では代わりに refract_rt.wgsl を連結する。両者は排他）。
//
// 連結順（距離ソート例）:
//   [... surface, surface_gather, lighting_eval, shader_fragment,
//    refract_common, refract_ss, shader_transparent]
//
// 実装方針: 厳密な屈折レイではなく、法線のビュー空間傾きに比例した安価な画面歪み
// （scattered thin-glass 近似）。原理的な限界（ガラス越しのガラスが映らない・自己屈折なし・
// 視差が不正確）があるため、RT 対応 GPU では refract_rt.wgsl が本物の屈折レイで置き換える。
// ============================================================

/// 屈折による画面内 UV オフセットの上限（画面幅に対する比率）。
/// これ以上ずらすと背景の別物が透けて不自然になるため上限で頭を押さえる。
/// （SS 専用。RT 版は画面歪みではなくレイのヒット点射影で背景 UV を得るため使わない。）
const REFRACT_MAX_OFFSET: f32 = 0.06;

/// スクリーンスペース屈折の背景色（リニア HDR RGB）を返す。
/// - `surf`   : シェーディング面（normal＝画面内傾き, roughness＝すりガラス量に使う）。
/// - `frag_xy`: フラグメントのフレームバッファ座標（@builtin(position).xy＝ピクセル単位）。
/// - `ior`    : 屈折率（1.0 で歪みゼロ＝素の背景）。ガラス≈1.5、水≈1.33。
///
/// 法線のビュー空間 xy 傾きに比例して背景 UV をずらし、roughness からミップを選んで
/// すりガラス（ぼけた屈折）を表現する。ior=1 で offset=0（背景そのまま）。画面外へ出る
/// サンプルは素の UV へフェードして端の引き伸ばしアーティファクトを避ける。
fn refract_sample_bg(surf: Surface, frag_xy: vec2<f32>, ior: f32) -> vec3<f32> {
    // 画面 UV（[0,1]）。resolution は CameraUniform（group0, shader_common.wgsl）。
    let res = max(u_camera.resolution, vec2<f32>(1.0, 1.0));
    let uv  = frag_xy / res;
    // 法線をビュー空間へ回転（view の 3x3 部分）。ビュー空間 xy が画面内の傾き方向。
    let n_view = normalize((u_camera.view * vec4<f32>(surf.normal, 0.0)).xyz);
    // 屈折の強さ = 1 - 1/ior。ior=1 で 0（歪みなし）。ガラス 1.5 → 0.333。
    let strength = clamp(1.0 - 1.0 / max(ior, 1.0), 0.0, 1.0);
    // 画面内オフセット（y は UV 座標系に合わせて反転）。
    let offset = vec2<f32>(n_view.x, -n_view.y) * strength * REFRACT_MAX_OFFSET;
    let suv = uv + offset;
    // 画面外（0..1 を外れる）はオフセットを打ち消し素の背景へ（端アーティファクト回避）。
    let inside = all(suv >= vec2<f32>(0.0, 0.0)) && all(suv <= vec2<f32>(1.0, 1.0));
    let final_uv = select(uv, suv, inside);
    // roughness からミップを選んで背景ピラミッドをサンプル（すりガラス。共有ヘルパ）。
    return refract_bg_sample(final_uv, surf.roughness);
}
