// ============================================================
// refract_common.wgsl — スクリーンスペース屈折＋ガラス透過の共有定義
//
// 半透明フォワードパス（距離ソート fs_transparent_sorted / WBOIT fs_wboit）だけに連結する。
// 「不透明シーン HDR のコピー（背景）」を group4 の追加バインディング（15/16）で受け取り、
// 半透明フラグメントの法線と屈折率（IOR）から背景を歪めてサンプルする。
//
// 【機能】
//   1. スクリーンスペース屈折（Phase RT-Translucency）: 法線のビュー空間傾きで背景 UV をずらす。
//   2. すりガラス（ガラス表現）: 背景 RT のミップチェーン（下位ミップほど強くぼかす）を
//      roughness からミップレベルを選んでサンプルする（textureSampleLevel）。
//   3. 透過率（transmission）合成: アルファ（被覆）と分離した「向こうがどれだけ透けるか」を
//      フレネルで反射／透過へ配分して合成する（glass_composite）。
//
// 【バインドグループ】group4（ライト binding0/1・シャドウ 2〜5・クラスタ 7〜9・DDGI 10〜13 と同居）
//   binding 15: t_refract_bg（不透明シーン HDR のコピー＝屈折の背景。ミップチェーン付き）
//   binding 16: s_refract_bg（線形・トライリニアサンプラー）
// 番号は不透明側 group4（0〜13）と RT 影の 6/14 を避けて 15/16 を使う。
// max_bind_groups=5（group0〜4）を厳守（新グループは増やさない）。
//
// u_camera（group0）/ u_material（group2）/ u_light_meta（group4）は連結順で先に宣言済み
// （shader_common.wgsl / light_common.wgsl）。TRANSLUCENCY_RT_REFRACTION も light_common.wgsl。
// ============================================================

/// group4 binding15: 不透明シーン HDR のコピー（屈折の背景テクスチャ。ミップチェーン付き）。
@group(4) @binding(15) var t_refract_bg: texture_2d<f32>;
/// group4 binding16: 背景サンプラー（線形・トライリニア・ClampToEdge）。
@group(4) @binding(16) var s_refract_bg: sampler;

/// 屈折による画面内 UV オフセットの上限（画面幅に対する比率）。
/// これ以上ずらすと背景の別物が透けて不自然になるため上限で頭を押さえる。
const REFRACT_MAX_OFFSET: f32 = 0.06;

/// すりガラス用ミップチェーンの最大ミップレベル（0 起点）。
/// Rust 側 transparency::REFRACT_MIP_COUNT（=5）と一致させること（= REFRACT_MIP_COUNT - 1）。
/// 背景 RT がこのミップ数を持ち、下位ミップほどいもす法ブラーで強くぼかしてある。
const REFRACT_MAX_MIP: f32 = 4.0;

/// roughness → ミップレベルのマッピング係数（線形）。
/// roughness 0.0 → ミップ 0（シャープな屈折）、roughness 1.0 → REFRACT_MAX_MIP（最大ぼかし）。
/// 線形（perceptual roughness をそのまま比例）にするのは、GGX の視覚的ぼけ量が
/// roughness に概ね比例して増えるため。深いミップほど広半径ブラー済みなので、
/// 係数は「roughness=1 でチェーン最深へ届く」= REFRACT_MAX_MIP をそのまま使う。
const REFRACT_ROUGHNESS_TO_MIP: f32 = REFRACT_MAX_MIP;

/// 屈折した背景色（リニア HDR RGB）を返す。roughness からミップレベルを選んで
/// すりガラス（ぼけた屈折）を表現する。
/// - `n_world`  : 表面のワールド法線（シェーディング法線）。ビュー空間 xy が「画面内の傾き」。
/// - `frag_xy`  : フラグメントのフレームバッファ座標（@builtin(position).xy＝ピクセル単位）。
/// - `ior`      : 屈折率（1.0 で歪みゼロ＝素の背景）。ガラス≈1.5、水≈1.33。
/// - `roughness`: 表面のラフネス（0=鏡面のシャープな屈折、高=すりガラス）。
///
/// 実装方針: 厳密な屈折レイではなく、法線のビュー空間傾きに比例した安価な画面歪み
/// （scattered thin-glass 近似）。ior=1 で offset=0（背景そのまま）。画面外へ出るサンプルは
/// 素の UV へフェードして端の引き伸ばしアーティファクトを避ける。
fn refract_background(n_world: vec3<f32>, frag_xy: vec2<f32>, ior: f32, roughness: f32) -> vec3<f32> {
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
    // roughness からミップレベルを選ぶ（すりガラス）。0=ミップ0（シャープ）、高=深いミップ（強ぼかし）。
    // サンプラーは mipmap_filter=Linear なのでミップ間はトライリニア補間される（滑らかな遷移）。
    let mip = clamp(roughness * REFRACT_ROUGHNESS_TO_MIP, 0.0, REFRACT_MAX_MIP);
    return textureSampleLevel(t_refract_bg, s_refract_bg, final_uv, mip).rgb;
}

// ── ガラス透過（transmission）合成 ────────────────────────────

/// IOR から垂直入射のフレネル反射率 F0（誘電体）。ior=1.5 → 0.04 付近。
fn glass_f0_from_ior(ior: f32) -> f32 {
    let r = (ior - 1.0) / (ior + 1.0);
    return r * r;
}

/// スカラーのフレネル・シュリック近似（視線角での反射率）。cos_theta=dot(N,V)。
fn glass_fresnel(cos_theta: f32, f0: f32) -> f32 {
    let m  = clamp(1.0 - cos_theta, 0.0, 1.0);
    let m2 = m * m;
    return f0 + (1.0 - f0) * (m2 * m2 * m); // (1-cos)^5
}

/// premultiplied 出力（色 × 被覆, 実効被覆）。距離ソート／WBOIT が共有する。
struct GlassOut {
    premult: vec3<f32>,
    a_eff:   f32,
}

/// ガラス合成: ライティング色 c・アルファ a・屈折背景・透過率を統合し premultiplied 出力を返す。
///
/// 後方互換（最重要）: transmission==0 のとき、
///   - 屈折オフ → (c*a, a)（従来の straight AlphaBlending と数学的に等価＝Raster パリティ）
///   - 屈折オン → (c*a + bg*tint*(1-a), 1)（従来の屈折合成そのまま）
/// を **ビット一致** で返す（下の early-return と mix(x,y,0)=x の恒等性で担保）。
///
/// transmission>0 のとき:
///   - 屈折オン: フレネル F で反射／透過を配分し、ハイライト c を被覆として残したまま
///     背景をガラス色で色付けして (1-F) 分だけ透過させる端点へ transmission で補間する。
///   - 屈折オフ（背景コピー無し）: 被覆を a*(1-transmission) へ下げて背後（フレームバッファ）を
///     より見せる。固定関数ブレンドでは dst を色付けできないため、色付き透過は屈折経路のみ。
fn glass_composite(c: vec3<f32>, a: f32, in: VertexOutput, front_facing: bool) -> GlassOut {
    var o: GlassOut;
    let tr = clamp(u_material.transmission, 0.0, 1.0);
    let refract_on = (u_light_meta.translucency_rt & TRANSLUCENCY_RT_REFRACTION) != 0u && u_material.ior > 1.0;

    if !refract_on {
        // 屈折背景が無い経路。透過率は「被覆を下げて背後をより見せる」で表現する。
        // tr=0 → a_eff=a・premult=c*a（従来とビット一致: a*(1-0)=a, c*a 不変）。
        o.premult = c * a;
        o.a_eff   = a * (1.0 - tr);
        return o;
    }

    // 屈折オン: 背景を roughness 連動でぼかしつつ歪めてサンプルし、自前合成する。
    let surf = gather_surface(in, front_facing);
    let bg   = refract_background(surf.normal, in.clip_pos.xy, u_material.ior, surf.roughness);
    let tint = u_material.base_color_factor.rgb;
    // 従来式（transmission=0 の端点＝屈折オンの既存挙動）。
    let rgb0 = c * a + bg * tint * (1.0 - a);
    if tr <= 0.0 {
        o.premult = rgb0;
        o.a_eff   = 1.0;
        return o;
    }
    // 透過率>0 の端点: フレネルで反射／透過を配分。ハイライト c は被覆として残し、
    // 背景はガラス色で色付けして (1-F) 分だけ透過させる（表面成分をアルファに依存させない）。
    let V   = normalize(u_camera.position - surf.world_pos);
    let ndv = max(dot(normalize(surf.normal), V), 0.0);
    let fr  = glass_fresnel(ndv, glass_f0_from_ior(u_material.ior));
    let rgb1 = c + bg * tint * (1.0 - fr);
    // transmission で 従来式 rgb0 → 透過端点 rgb1 へ補間。mix(x,y,0)=x で tr=0 の互換は上で分岐済み。
    o.premult = mix(rgb0, rgb1, tr);
    o.a_eff   = 1.0;
    return o;
}
