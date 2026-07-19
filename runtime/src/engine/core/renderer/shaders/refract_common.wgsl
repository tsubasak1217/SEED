// ============================================================
// refract_common.wgsl — ガラス透過（屈折）合成の共有定義（背景取得の方式非依存部）
//
// 半透明フォワードパス（距離ソート fs_transparent_sorted / WBOIT fs_wboit）だけに連結する。
// 「不透明シーン HDR のコピー（背景）」を group4 の追加バインディング（15/16）で受け取り、
// 半透明フラグメントから見た背景色を求めて透過成分として合成する（glass_composite）。
//
// 【背景取得の方式は 2 種を差し替え可能（tint_avg / tint_bindless と同じ分離方式）】
//   本ファイルは方式非依存の共有部（背景バインディング 15/16・すりガラスのミップ選択・
//   フレネル配分・glass_composite）だけを持ち、実際の「背景色 bg をどう得るか」＝
//   `refract_sample_bg(surf, frag_xy, ior)` は、連結される **どちらか 1 本** が供給する:
//     - refract_ss.wgsl : スクリーンスペース屈折（法線のビュー空間傾きで背景 UV をずらす安価な近似）。
//                         非 RT 対応 GPU・および translucency≠Rt のフォールバック経路。
//     - refract_rt.wgsl : TLAS を使う本物の屈折レイ（入射屈折→界面トレース→不透明トレース→
//                         ヒット点を画面へ射影して背景サンプル or DDGI フォールバック）。RT 対応 GPU の
//                         translucency=Rt 経路のみ。group4 に TLAS(6)＋平均アルベド(14) を追加宣言する。
//   glass_composite は `refract_sample_bg` を呼ぶだけで、合成式（premult / a_eff / フレネル配分）は
//   両方式で完全共有される（差分は背景取得のみ）。距離ソート／WBOIT も glass_composite 共有で両対応。
//
// 【機能】
//   1. すりガラス（ガラス表現）: 背景 RT のミップチェーン（下位ミップほど強くぼかす）を
//      roughness からミップレベルを選んでサンプルする（refract_bg_sample）。両方式が共有する。
//   2. 透過率（transmission）合成: アルファ（被覆）と分離した「向こうがどれだけ透けるか」を
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
// Surface（surf 引数の型）は surface.wgsl で先に宣言済み（連結順で本ファイルより前）。
// ============================================================

/// group4 binding15: 不透明シーン HDR のコピー（屈折の背景テクスチャ。ミップチェーン付き）。
@group(4) @binding(15) var t_refract_bg: texture_2d<f32>;
/// group4 binding16: 背景サンプラー（線形・トライリニア・ClampToEdge）。
@group(4) @binding(16) var s_refract_bg: sampler;

// REFRACT_MAX_OFFSET（SS 屈折の画面内 UV オフセット上限）は SS 専用のため refract_ss.wgsl へ移動した。

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

/// roughness → 背景ミップレベル（すりガラス）。0=ミップ0（シャープ）、高=深いミップ（強ぼかし）。
/// SS / RT どちらの背景取得も、最終的にこの規則で背景ピラミッドをサンプルする（すりガラス共有）。
fn refract_bg_mip(roughness: f32) -> f32 {
    return clamp(roughness * REFRACT_ROUGHNESS_TO_MIP, 0.0, REFRACT_MAX_MIP);
}

/// 屈折背景ピラミッド（binding15）を UV＋roughness で 1 回サンプルする共有ヘルパ。
/// サンプラーは mipmap_filter=Linear なのでミップ間はトライリニア補間される（滑らかな遷移）。
/// SS 版（歪めた UV）も RT 版（ヒット点を画面へ射影した UV）もこれを最終段で使う。
fn refract_bg_sample(uv: vec2<f32>, roughness: f32) -> vec3<f32> {
    return textureSampleLevel(t_refract_bg, s_refract_bg, uv, refract_bg_mip(roughness)).rgb;
}

// ── 背景取得 refract_sample_bg(surf, frag_xy, ior) は【別ファイル】が供給する ──
//   refract_ss.wgsl : スクリーンスペース屈折（フォールバック）。
//   refract_rt.wgsl : TLAS 本物屈折レイ（RT 対応 GPU・translucency=Rt）。
// glass_composite（下記）から前方参照で呼ばれる（WGSL は宣言順に依存しない）。
// 連結時は本ファイルの後ろにいずれか 1 本だけを必ず並べること（両方＝重複定義／どちらも無し＝未定義）。

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
    let refract_on = (u_light_meta.translucency_rt & TRANSLUCENCY_RT_REFRACTION) != 0u && material_refracts;

    if !refract_on {
        // 屈折背景が無い経路（grab-pass 非実行）。babf5f1 と**ビット一致**の素の
        // premultiplied over（premult=c*a, a_eff=a）に落とす。透過率（transmission）の
        // 本来の効果は「背景をガラス色で色付けして透過させる」ことで、これは背景コピー
        // （grab-pass）を持つ屈折オン経路でのみ意味を持つ。
        //
        // 【重要 / 回帰修正】以前ここは a_eff = a*(1-tr) で被覆を下げていたが、これが:
        //   ・距離ソート: premult over の実効被覆を下げ「アルファを下げただけ」の見た目に。
        //   ・WBOIT: a_eff→0 で reveal がクリア値 1 のまま残り、post_wboit_composite が
        //     `reveal > 1-eps` で discard（かつ coverage=1-reveal≈0）→ ガラスが完全に消える。
        // という 2 症状を招いていた。屈折オフでは transmission を被覆に反映せず、babf5f1
        // 同等の可視性（素のアルファ）を保証する（色付き透過は屈折オン経路が担う）。
        o.premult = c * a;
        o.a_eff   = a;
        return o;
    }

    // 屈折オン: 背景を取得（方式は連結された refract_ss / refract_rt が供給する refract_sample_bg）。
    // SS 版は法線傾きで背景 UV を歪め、RT 版は TLAS へ屈折レイを撃ってヒット点を画面へ射影する。
    // どちらも最終段は roughness 連動のミップ（refract_bg_sample）で背景ピラミッドを引く（すりガラス共有）。
    let surf = gather_surface(in, front_facing);
    let bg   = refract_sample_bg(surf, in.clip_pos.xy, u_material.ior);
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

// ── 色付き透過率 WBOIT（per-channel transmittance）＋屈折歪みの合成 ────────────────
//
// 【方針（凸結合ハイブリッド）】
//   実機確認の結果、屈折の曲がった背景を完全に落とすと WBOIT ガラスの存在感が消え、
//   セロファンを貼ったような平坦な見た目になった。そこで:
//     (1) フラグメントは距離ソートと同じ glass_composite で「曲がった背景×ガラス色 + 自色
//         ライティング」を焼き込んだ premult を出す（屈折歪みを復活）。
//     (2) 背景の減衰はチャンネルごとの透過率の積 Π T_frag として reveal に別途貯める（維持）。
//     (3) 合成は **加算ではなくチャンネルごとの凸結合（lerp）**:
//           final_c = scene_c · ΠT_c + avg_c · (1 − ΠT_c)
//         にする。凸結合はエネルギーが max(scene, avg) を超えないため、premult に背景が
//         焼き込まれていても「重なった層ぶん足し算されて明るくなる」二重計上が起きない。
//   N 枚重なると ΠT_c が下がり avg（曲がった背景の重み平均＝カーテンの見た目）へ漸近する
//   だけで、加算的増強にはならない（純赤 T=(1,0,0) は ΠT が飽和し N 非依存＝「1赤+1赤≈1赤」）。
//   ※ avg = accum.rgb/accum.a は重み平均（正規化）なので、層数が増えても総和のようには膨らまない。
//
// 【出力の意味】
//   premult  : 曲がった背景×ガラス色 + 自色ライティング（distance-sort と同じ glass_composite の premult）。
//              accum に深度重み w を掛けて積む。合成で avg=accum.rgb/accum.a に戻す。
//   a_eff    : accum.a の重み（glass は 1.0・素の Blend は a）。glass_composite と同一。
//   transmit : このフラグメントを通り抜ける背景の per-channel 透過率 T_frag。
//              合成パスで Π T_frag（reveal.rgb）を作り、凸結合の混合係数に使う。
//
// 【T_frag の式（色付き影・refract_layer_tint と同じセマンティクス）】
//   T_frag = (1 - a) + a * tr * albedo
//     ・(1 - a)        : 被覆されない部分は素通り（無着色）。
//     ・a * tr * albedo : 被覆部のうち transmission=tr の割合が、ガラス色 albedo で色付けされて透ける。
//   例) 純赤ガラス（a=1, tr=1, albedo=(1,0,0)）→ T=(1,0,0)。Π で N 枚重ねても (1,0,0) のまま＝飽和。
//       半透明赤（a=0.5, tr=1, albedo=(1,0,0)）→ T=(1,0.5,0.5)。N 枚で (1,0.5^N,0.5^N)→(1,0,0) へ収束。
//       素の Blend（tr=0）→ T=(1-a) のスカラー（全チャンネル等しい）＝従来 WBOIT の revealage と一致（パリティ）。
//   transmittance は物理的に [0,1] なので、albedo が HDR（>1）でも clamp して増幅を防ぐ。
struct WboitGlass {
    /// 曲がった背景×ガラス色 + 自色（glass_composite の premult）。accum に w を掛けて積む。
    premult:  vec3<f32>,
    /// accum.a の重み（glass=1.0・素の Blend=a）。glass_composite と同一。
    a_eff:    f32,
    /// per-channel 透過率 T_frag（合成パスで Π を作り凸結合の混合係数にする）。
    transmit: vec3<f32>,
}

/// 色付き透過率 WBOIT のフラグメント合成。屈折歪みを保つため距離ソートと同じ glass_composite で
/// 「曲がった背景×ガラス色 + 自色」を焼き込んだ premult / a_eff を得つつ、背景減衰用の
/// per-channel 透過率 T_frag を別途返す（合成パスの凸結合で二重計上を回避する）。
fn glass_composite_wboit(c: vec3<f32>, a: f32, in: VertexOutput, front_facing: bool) -> WboitGlass {
    var o: WboitGlass;
    // 距離ソートと同一の合成（曲がった背景を焼き込む）。RT 版は TLAS 屈折レイ、SS 版は grab-pass。
    let base   = glass_composite(c, a, in, front_facing);
    o.premult  = base.premult;
    o.a_eff    = base.a_eff;
    let tr     = clamp(u_material.transmission, 0.0, 1.0);
    let albedo = u_material.base_color_factor.rgb;
    // T_frag = (1-a) + a*tr*albedo。物理透過率として [0,1] にクランプ（HDR albedo の増幅防止）。
    o.transmit = clamp(vec3<f32>(1.0 - a) + a * tr * albedo, vec3<f32>(0.0), vec3<f32>(1.0));
    return o;
}
