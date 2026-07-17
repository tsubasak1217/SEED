// ============================================================
// refract_common.wgsl — スクリーンスペース屈折の共有定義（Phase RT-Translucency）
//
// 半透明フォワードパス（距離ソート fs_transparent_sorted / WBOIT fs_wboit）だけに連結する。
// 「不透明シーン HDR のコピー（背景）」を group4 の追加バインディング（15/16）で受け取り、
// 半透明フラグメントの法線と屈折率（IOR）から背景を歪めてサンプルする。
//
// 【バインドグループ】group4（ライト binding0/1・シャドウ 2〜5・クラスタ 7〜9・DDGI 10〜13 と同居）
//   binding 15: t_refract_bg（不透明シーン HDR のコピー。屈折の背景）
//   binding 16: s_refract_bg（線形クランプサンプラー）
// 番号は不透明側 group4（0〜13）と RT 影の 6/14 を避けて 15/16 を使う。
// max_bind_groups=5（group0〜4）を厳守（新グループは増やさない）。
//
// Rust 側は lighting.rs::create_transparent_bind_group が binding15/16 を供給し、
// frame_renderer.rs が不透明ライティング完成後の scene_hdr をコピーした RT を差す
// （屈折オフのフレームはダミー 1x1 を差すため、常にバインド可能でパイプラインが壊れない）。
//
// u_camera（group0）/ u_material（group2）/ u_light_meta（group4）は連結順で先に宣言済み
// （shader_common.wgsl / light_common.wgsl）。
// ============================================================

/// group4 binding15: 不透明シーン HDR のコピー（屈折の背景テクスチャ）。
@group(4) @binding(15) var t_refract_bg: texture_2d<f32>;
/// group4 binding16: 背景サンプラー（線形・ClampToEdge）。
@group(4) @binding(16) var s_refract_bg: sampler;

/// 屈折による画面内 UV オフセットの上限（画面幅に対する比率）。
/// これ以上ずらすと背景の別物が透けて不自然になるため上限で頭を押さえる。
const REFRACT_MAX_OFFSET: f32 = 0.06;

/// 屈折した背景色（リニア HDR RGB）を返す。
/// - `n_world`: 表面のワールド法線（シェーディング法線）。ビュー空間 xy が「画面内の傾き」。
/// - `frag_xy`: フラグメントのフレームバッファ座標（@builtin(position).xy＝ピクセル単位）。
/// - `ior`    : 屈折率（1.0 で歪みゼロ＝素の背景）。ガラス≈1.5、水≈1.33。
///
/// 実装方針: 厳密な屈折レイではなく、法線のビュー空間傾きに比例した安価な画面歪み
/// （scattered thin-glass 近似）。ior=1 で offset=0（背景そのまま）。画面外へ出るサンプルは
/// 素の UV へフェードして端の引き伸ばしアーティファクトを避ける。
fn refract_background(n_world: vec3<f32>, frag_xy: vec2<f32>, ior: f32) -> vec3<f32> {
    // 画面 UV（[0,1]）。resolution は CameraUniform（group0, shader_common.wgsl）。
    let res = max(u_camera.resolution, vec2<f32>(1.0, 1.0));
    let uv  = frag_xy / res;
    // 法線をビュー空間へ回転（view の 3x3 部分）。ビュー空間 xy が画面内の傾き方向。
    let n_view = normalize((u_camera.view * vec4<f32>(n_world, 0.0)).xyz);
    // 屈折の強さ = 1 - 1/ior。ior=1 で 0（歪みなし）。ガラス 1.5 → 0.333。
    let strength = clamp(1.0 - 1.0 / max(ior, 1.0), 0.0, 1.0);
    // 画面内オフセット（y は UV 座標系に合わせて反転）。
    let offset = vec2<f32>(n_view.x, -n_view.y) * strength * REFRACT_MAX_OFFSET;
    let suv = uv + offset;
    // 画面外（0..1 を外れる）はオフセットを打ち消し素の背景へ（端アーティファクト回避）。
    let inside = all(suv >= vec2<f32>(0.0, 0.0)) && all(suv <= vec2<f32>(1.0, 1.0));
    let final_uv = select(uv, suv, inside);
    return textureSampleLevel(t_refract_bg, s_refract_bg, final_uv, 0.0).rgb;
}
