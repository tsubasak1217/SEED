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

/// 屈折（grab-pass 置き換え合成）を有効化する ior のしきい値マージン。
/// ior==1.0（屈折なし＝素の Blend）を確実に非屈折側へ落とすため、しきい値を
/// 1.0 + IOR_EPSILON にして浮動小数の丸め（例: エディタ入力の 1.0000001）を吸収する。
/// この値未満の ior は「屈折しないマテリアル」として通常アルファブレンド経路へ流す。
const IOR_EPSILON: f32 = 1.0e-4;

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

// ── ガラス表面の反射（ミニ SSR ＋ プローブ／アンビエント フォールバック, Phase RT-Reflection）──
//
// 不透明用の反射（SSR/RT, RT_REFLECTION）はガラス面には効かない（G-Buffer は不透明のみ）。
// そこで半透明フォワードのフラグメントで、既にバインド済みの資源だけを使って簡易反射を足す:
//   - 屈折背景ピラミッド（binding15/16, 不透明シーン HDR のミップ付きコピー）＝ミニ SSR のヒット色
//   - DDGI プローブ照度（binding10-13）＝ミス／画面外のフォールバック
//   - 環境光（u_light_meta.ambient_*）＝GI 無効時のフォールバック（真っ黒にしない）
// 深度は透明パスにバインドされていない（メイン深度は同パスのアタッチメントで読めない）。
// よって深度テスト付きの厳密な SSR は行わず、反射レイを画面へ射影して「画面内に留まる最遠点」を
// ヒットとみなす**深度レス近似**にする（限界: 遮蔽テストが無いためゴースト反射が出る＝ロードマップに明記）。

/// ミニ SSR のマーチステップ数（コスト上限。8〜12 の中庸）。深度レスのため多くしても情報は増えない。
const MINI_SSR_STEPS:    i32 = 10;
/// ミニ SSR のワールド単位到達距離（反射レイをこの距離まで画面へ射影して辿る）。
const MINI_SSR_MAX_DIST: f32 = 20.0;
/// 1 ステップ前進量（= MAX_DIST / STEPS）。
const MINI_SSR_STEP:     f32 = MINI_SSR_MAX_DIST / 10.0;
/// 反射の粗さフェード（reflection_common.wgsl と同値の意図＝1 本レイ反射は粗面で嘘になる）。
/// ここまで全反射 → ここで反射 0。透明パスは reflection_common を連結しないため定数を再掲する
/// （Rust reflection.rs 側の値と乖離しないこと。ガラスは概ね低粗さ＝この範囲で十分）。
const REFL_ROUGHNESS_FADE_START: f32 = 0.30;
const REFL_ROUGHNESS_FADE_END:   f32 = 0.55;

/// ガラス面の反射色（リニア HDR RGB）を返す。呼び出し側で material_refracts かつ反射ビットが
/// 立っていることを確認済みであること。weight（フレネル×粗さフェード×強度）まで畳み込んで返す。
/// - `surf`      : 面情報（world_pos / normal / roughness）。
/// - `V`         : 視線ベクトル（面→カメラ, 正規化済み）。
/// - `ndv`       : max(dot(N,V), eps)。
/// - `ssr_enabled`: 屈折背景ピラミッドがこのフレームに存在するか（bit1）。true のときのみミニ SSR。
fn glass_reflection(surf: Surface, V: vec3<f32>, ndv: f32, ssr_enabled: bool) -> vec3<f32> {
    // 粗さフェード: 粗面では 1 本レイ反射が破綻するので落とす（0 で反射なし）。
    let smooth_w = 1.0 - smoothstep(REFL_ROUGHNESS_FADE_START, REFL_ROUGHNESS_FADE_END, surf.roughness);
    if smooth_w <= 0.0 {
        return vec3<f32>(0.0, 0.0, 0.0);
    }
    let N = normalize(surf.normal);
    let R = normalize(reflect(-V, N));
    // フレネル（誘電体 F0）。視線角が浅いほど反射が強い（ガラスのエッジ光り）。
    let fr = glass_fresnel(ndv, glass_f0_from_ior(u_material.ior));

    // ── ミニ SSR（深度レス近似）: 反射レイを画面へ射影し、画面内に留まる最遠点をヒットとする ──
    var hit    = false;
    var hit_uv = vec2<f32>(0.0, 0.0);
    if ssr_enabled {
        var t = MINI_SSR_STEP;
        for (var i: i32 = 0; i < MINI_SSR_STEPS; i = i + 1) {
            let p    = surf.world_pos + R * t;
            let clip = u_camera.view_proj * vec4<f32>(p, 1.0);
            if clip.w <= 0.0 { break; }            // カメラ後方へ抜けた
            let ndc = clip.xyz / clip.w;
            let uv  = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
            if all(uv >= vec2<f32>(0.0)) && all(uv <= vec2<f32>(1.0)) {
                hit_uv = uv;   // 画面内: 反射先候補を更新（より遠い＝より反射像らしい点を残す）
                hit    = true;
            } else {
                break;         // 画面外へ抜けた: それ以上辿らない
            }
            t = t + MINI_SSR_STEP;
        }
    }

    var refl: vec3<f32>;
    if hit {
        // ヒット: 屈折背景ピラミッドをサンプル（roughness でミップ選択＝ぼけた反射）。
        let mip = clamp(surf.roughness * REFRACT_ROUGHNESS_TO_MIP, 0.0, REFRACT_MAX_MIP);
        refl = textureSampleLevel(t_refract_bg, s_refract_bg, hit_uv, mip).rgb;
    } else {
        // ミス／画面外／SSR 無効: DDGI プローブ照度（反射方向）→ 無ければアンビエント。
        if u_gi_params.enabled != 0u {
            refl = ddgi_sample_irradiance(u_gi_params, surf.world_pos, R, t_gi_irradiance, t_gi_visibility, s_gi);
        } else {
            refl = u_light_meta.ambient_color * u_light_meta.ambient_intensity;
        }
    }
    // weight: フレネル × 粗さフェード × 反射強度ノブ（LightMeta.reflection_intensity）。
    return refl * fr * smooth_w * u_light_meta.reflection_intensity;
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
/// grab-pass（背景を自前合成して a_eff=1 で確定表示する屈折経路）は material_refracts
/// = (ior > 1.0 + IOR_EPSILON) || (transmission > 0) のマテリアルだけに入る。素の Blend
/// （ior==1・transmission==0）は屈折ビットの有無に関わらず必ず !refract_on 経路に落ちる。
///
/// 後方互換（最重要）: transmission==0 かつ ior==1（＝素の Blend）のとき、
///   - 屈折オン/オフどちらでも → (c*a, a)（従来の straight AlphaBlending と数学的に等価＝Raster パリティ）
/// を **ビット一致** で返す（material_refracts=false → !refract_on → premult=c*a, a_eff=a）。
/// transmission==0 かつ ior>1（＝ガラス）で屈折オンのとき:
///   - (c*a + bg*tint*(1-a), 1)（従来の屈折合成そのまま。下の tr<=0 early-return）。
///
/// transmission>0 のとき（material_refracts=true）:
///   - 屈折オン: フレネル F で反射／透過を配分し、ハイライト c を被覆として残したまま
///     背景をガラス色で色付けして (1-F) 分だけ透過させる端点へ transmission で補間する。
///     ior==1（歪みなし）のガラスでも、背景を素のまま色付き透過できる。
///   - 屈折オフ（translucency_rt の屈折ビットが立っていない＝背景コピー無し）: babf5f1 と
///     ビット一致の素のアルファブレンド (c*a, a) に落とす（transmission は被覆に反映しない）。
///     固定関数ブレンドでは dst を色付けできず、a_eff を下げると距離ソートは「アルファを下げた
///     だけ」の見た目に、WBOIT は a_eff→0 で discard され不可視になるため。色付き透過は屈折オン経路のみ。
fn glass_composite(c: vec3<f32>, a: f32, in: VertexOutput, front_facing: bool) -> GlassOut {
    var o: GlassOut;
    let tr = clamp(u_material.transmission, 0.0, 1.0);

    // ── 透過 0 の動的裏面カリング（Phase RT-Translucency, シェーダ discard）─────────────────
    // 透過率が 0（＝実質不透明な Blend＝光を透過しないガラス）のとき、自分の裏面（front_facing==false）は
    // 原理的に前面に隠れて見えないはずなので破棄する。これで「透過しないガラスの裏面が自分越しに
    // 透けて見える」不具合が消える。透過率>0 のガラスは両面を残す（厚み感・奥行きの見えを維持）。
    // マテリアルの cull_face=None（両面）設定より優先されるのは**意図的**（透過 0 なら裏面は
    // 見えないという物理的根拠）。既知の限界: 単層両面 Blend（透過 0 のカーテン等）は裏面が
    // 消えて片面化する＝ロードマップに明記。透過するファブリック（transmission>0）は対象外で安全。
    if !front_facing && u_material.transmission <= 0.0 {
        discard;
    }
    // grab-pass 置き換え合成（背景を自前で歪めて合成し a_eff=1 で確定表示する）は、
    // 「実際に屈折/透過するマテリアル」だけに限定する。すなわち:
    //   ・ior > 1.0 + IOR_EPSILON  … スクリーンスペース屈折を持つ（ガラス・水など）
    //   ・transmission > 0.0        … アルファと分離した透過（色付きガラス越し）を持つ
    // このいずれでもない素の Blend（ior==1.0 かつ transmission==0）は grab-pass に入れず、
    // 下の !refract_on 経路で通常のプリマルチプライド・アルファブレンド (c*a, a) に落とす。
    // これにより「不透明シーンのコピー(bg)で dst を上書きして a_eff=1 にする」挙動が
    // 屈折/透過を意図しない Blend へ波及して描画順（先に描いた半透明・スカイボックス）を
    // 壊すことを防ぐ。translucency_rt の屈折ビットが立っていない場合も従来どおり非屈折。
    let material_refracts = u_material.ior > 1.0 + IOR_EPSILON || tr > 0.0;
    // 屈折背景ピラミッドがこのフレームに存在するか（bit1）。ミニ SSR のヒット色に使える。
    let refract_bit = (u_light_meta.translucency_rt & TRANSLUCENCY_RT_REFRACTION) != 0u;
    let refract_on  = refract_bit && material_refracts;
    // 半透明表面反射（bit2）が有効か。ガラス（material_refracts）にのみ乗せる。
    let reflect_on  = (u_light_meta.translucency_rt & TRANSLUCENCY_RT_REFLECTION) != 0u && material_refracts;

    if !material_refracts {
        // 素の Blend（ガラスでない）: babf5f1 と**ビット一致**の premultiplied over
        // （premult=c*a, a_eff=a）。反射・屈折・透過はいずれも波及させない（Raster パリティ保証）。
        //
        // 【重要 / 回帰修正】以前ここは a_eff = a*(1-tr) で被覆を下げていたが、これが距離ソートの
        // アルファ低下・WBOIT の discard（reveal=1 残り）を招いた。透過 0 の Blend は素のアルファを保つ。
        o.premult = c * a;
        o.a_eff   = a;
        return o;
    }

    // ── ガラス（material_refracts=true）: 面情報を 1 回だけ採取し反射／屈折を合成する ──
    let surf = gather_surface(in, front_facing);
    let V    = normalize(u_camera.position - surf.world_pos);
    let ndv  = max(dot(normalize(surf.normal), V), 1e-4);

    // 表面反射（ミニ SSR ＋ プローブ／アンビエント フォールバック）。反射ビット時のみ、
    // ミニ SSR は屈折ピラミッドがあるフレーム（refract_bit）だけ。無いフレームはフォールバック項のみ。
    var reflection_col = vec3<f32>(0.0, 0.0, 0.0);
    if reflect_on {
        reflection_col = glass_reflection(surf, V, ndv, refract_bit);
    }

    if !refract_on {
        // ガラスだが屈折背景がこのフレームに無い（forward／非 deferred／背景未コピー）。
        // 素のアルファブレンドに、フォールバック反射（真っ黒にしない）を加算する。
        o.premult = c * a + reflection_col;
        o.a_eff   = a;
        return o;
    }

    // 屈折オン: 背景を roughness 連動でぼかしつつ歪めてサンプルし、自前合成する。
    let bg   = refract_background(surf.normal, in.clip_pos.xy, u_material.ior, surf.roughness);
    let tint = u_material.base_color_factor.rgb;
    // 従来式（transmission=0 の端点＝屈折オンの既存挙動）＋ 表面反射。
    let rgb0 = c * a + bg * tint * (1.0 - a);
    if tr <= 0.0 {
        o.premult = rgb0 + reflection_col;
        o.a_eff   = 1.0;
        return o;
    }
    // 透過率>0 の端点: フレネルで反射／透過を配分。ハイライト c は被覆として残し、
    // 背景はガラス色で色付けして (1-F) 分だけ透過させる（表面成分をアルファに依存させない）。
    let fr  = glass_fresnel(ndv, glass_f0_from_ior(u_material.ior));
    let rgb1 = c + bg * tint * (1.0 - fr);
    // transmission で 従来式 rgb0 → 透過端点 rgb1 へ補間。mix(x,y,0)=x で tr=0 の互換は上で分岐済み。
    o.premult = mix(rgb0, rgb1, tr) + reflection_col;
    o.a_eff   = 1.0;
    return o;
}
