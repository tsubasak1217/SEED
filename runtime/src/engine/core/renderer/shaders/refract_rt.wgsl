// ============================================================
// refract_rt.wgsl — 本物の RT 屈折（背景取得の TLAS レイトレ実装）
//
// refract_common.wgsl の後ろに連結し、glass_composite から呼ばれる
// `refract_sample_bg(surf, frag_xy, ior)` を **TLAS への屈折レイ** で供給する。
// RT 対応 GPU（EXPERIMENTAL_RAY_QUERY）かつ translucency=Rt の透明フォワード RT バリアント
// パイプラインだけがこのファイルを連結する（非対応 GPU・translucency≠Rt は refract_ss.wgsl を
// 連結する。両者は排他＝同名 refract_sample_bg を 1 本だけ供給する）。
//
// 連結順（距離ソート RT 例）:
//   [cluster_common, pbr_common, shader_common, ddgi_common, light_common, shadow,
//    rt_shadow_off, static_vertex, surface, surface_gather, lighting_eval, shader_fragment,
//    refract_common, refract_rt, shader_transparent]
//   ※影経路は rt_shadow_off（シャドウマップのみ・TLAS 非宣言）。TLAS は本ファイルが binding6 に宣言する。
//     今回のスコープは「屈折のみ」＝透明パスの受光影は変更しない（従来どおりシャドウマップ）。
//
// 【group4 追加バインディング】本ファイルが自前で宣言する（rt_shadow_off は宣言しないため衝突なし）:
//   binding 6 : rt_accel（TLAS。屈折レイのトレース対象）
//   binding 14: rt_shadow_albedo（TLAS インスタンス順の平均アルベド .rgb ＋パック α/tr .a）
//               色付き影（rt_shadow_tint_avg.wgsl）が読むのと同一バッファ。界面の透過色付けに使う。
// DDGI（10-13）・屈折背景（15/16）は light_common.wgsl / refract_common.wgsl が既に宣言済み。
//
// 【SS 版に対する優位（本物の屈折レイで解決すること）】
//   (a) ガラス越しのガラスが映る（界面トレースで後続の半透明レイヤーの色が乗る）。
//   (b) 自己屈折（厚み）: 入射面→出射面を貫くため、厚みぶんの色付き吸収が乗る。
//   (c) 視差が正確: 背景はレイのヒット点を実際に画面へ射影して得る（画面歪み近似ではない）。
//
// 【既知の限界（正直に記す）】
//   ・後続界面ではレイを再屈折しない（＝一次屈折の方向のまま直進して界面色だけ累積する）:
//     inline ray query の committed intersection は**ヒット三角形の法線を返さない**
//     （バインドレス頂点フェッチは今回スコープ外）。法線が無いと界面での屈折方向を正しく
//     求められない（レイ正対で近似すると dot(N,I)=±1 の縮退で refract がレイを反転させるなど
//     破綻する）。そこで後続界面では**方向を更新せず**、界面色（tint）だけを累積して直進させる。
//     これでも **一次屈折（シェーディング表面 N は本物）** により視差・ガラス越し・厚み色は
//     得られる（多重界面の二次的な再屈折だけが省略）。正確な多重再屈折にはヒット点の実法線が要り、
//     将来 BindlessInstanceRecord に頂点/法線参照を足せば界面ごとに正確化できる拡張余地がある。
//     後続界面の ior も同様にインスタンス個別値がテーブルに無いため（rt_shadow_albedo には ior が
//     入っていない）、正確な再屈折を行うなら ior も同レコードへ足す必要がある。
//   ・背景 refract_bg は不透明のみのコピーなので、レイが最終的に当たる不透明面の色は
//     「画面内に見えていれば」正しく引ける。画面外／不透明ミスは DDGI（無効ならアンビエント）へ。
//     深度一致チェックは不要（refract_bg は不透明のみのコピーで遮蔽関係が単純＝手前の半透明が
//     混ざらない。反射 RT のような手前遮蔽の誤検出が起きないため素の画面内判定で足りる）。
//
// 【コスト】屈折レイは material_refracts の半透明ピクセルのみ。最大 REFRACT_MAX_INTERFACES(4)
//   界面 + 1 不透明 = 5 レイ/px。ゲート（translucency=Rt）オフや SS フォールバック時は増分ゼロ。
// ============================================================

/// group4 binding6: 屈折レイの TLAS。rt_shadow_off は TLAS を宣言しないため本ファイルが宣言する。
@group(4) @binding(6) var rt_accel: acceleration_structure;

/// group4 binding14: TLAS インスタンス順の平均アルベド（.rgb）＋パック α/transmission（.a）。
/// rt_shadow.rs 所有・色付き影 / GI / 反射と同一バッファ。界面の透過色（tint）付けに custom_data で引く。
/// - `.rgb`: 生の平均アルベド（GI/反射が読む共有値。意味を変えない）。
/// - `.a`  : α（base_color_factor.a）と transmission を各 8bit 固定小数でパック
///           （rt_shadow.rs::pack_shadow_alpha_transmission と対）。
@group(4) @binding(14) var<storage, read> rt_shadow_albedo: array<vec4<f32>>;

// ─── 定数 ────────────────────────────────────────────────────

/// 屈折・不透明レイの最小距離（自己交差回避の下限。原点バイアスと併用）。
const REFRACT_RT_TMIN: f32 = 0.001;
/// 界面ヒット後、次の界面レイの tmin（同一面の再ヒット回避の微小前進）。
const REFRACT_RT_T_STEP: f32 = 0.002;
/// レイ最大距離（十分遠方まで。directional の空まで届く大定数）。
const REFRACT_RT_TMAX: f32 = 1.0e4;
/// レイ原点をシェーディング面の法線側へ押し出す微小バイアス（入射面の自己交差回避）。
const REFRACT_RT_ORIGIN_BIAS: f32 = 0.002;
/// 全反射（TIR）判定: refract は全反射で 0 ベクトルを返す。長さ二乗がこの値未満なら TIR。
const REFRACT_RT_TIR_EPS: f32 = 1.0e-8;

/// 界面トレースの最大反復数（半透明レイヤーを貫く上限）。名前付き定数（マジックナンバー禁止）。
/// 最大 4 界面 + 最終 1 不透明 = 5 レイ/px（コスト有界）。
const REFRACT_MAX_INTERFACES: u32 = 4u;

/// インスタンスカリングマスク: 不透明（最終背景トレース対象）。rt_shadow.rs::RT_MASK_OPAQUE と一致。
const REFRACT_RT_MASK_OPAQUE: u32 = 0x01u;
/// インスタンスカリングマスク: 半透明レイヤー（界面トレース対象）。rt_shadow.rs::RT_MASK_NON_OPAQUE と一致。
const REFRACT_RT_MASK_TRANSLUCENT: u32 = 0x02u;

/// 色付き影と同一のパック定数（rt_shadow.rs::SHADOW_PACK_QUANT / SHADOW_PACK_RADIX と一致させること）。
/// rt_shadow_albedo[.a] から α・transmission をデコードするのに使う。
const REFRACT_PACK_QUANT: f32 = 255.0;
const REFRACT_PACK_RADIX: f32 = 256.0;

// ─── ヘルパ ──────────────────────────────────────────────────

/// 1 枚の半透明界面を通る光の RGB 透過率 T を平均アルベド storage から求める（色付き影と同一モデル）。
///   T = (1-α) + α·transmission·albedo.rgb
///     ・α=1, tr=1 → T = albedo （色ガラスは baseColor で透過光を濾過）
///     ・α=1, tr=0 → T = 0      （透過しない被覆面）
///     ・α=0        → T = 1      （素通り）
/// 範囲外インデックスは vec3(1)（色を付けない）。
fn refract_layer_tint(custom_data: u32) -> vec3<f32> {
    if custom_data >= arrayLength(&rt_shadow_albedo) {
        return vec3<f32>(1.0, 1.0, 1.0);
    }
    let entry  = rt_shadow_albedo[custom_data];
    let packed = entry.a;
    let a_q    = floor(packed / REFRACT_PACK_RADIX);
    let t_q    = packed - a_q * REFRACT_PACK_RADIX;
    let alpha  = clamp(a_q / REFRACT_PACK_QUANT, 0.0, 1.0);
    let tr     = clamp(t_q / REFRACT_PACK_QUANT, 0.0, 1.0);
    return vec3<f32>(1.0 - alpha) + alpha * tr * entry.rgb;
}

/// ワールド座標 → screen UV 射影の結果（UV と画面内フラグ）。
struct RefractRtProj { uv: vec2<f32>, valid: bool }

/// ワールド座標を view_proj で screen UV へ射影する（逆行列不要・reflection_rt.rt_refl_project と同式）。
/// clip.w<=0（カメラ背後）や UV が [0,1] の外なら valid=false。
fn refract_rt_project(world_pos: vec3<f32>) -> RefractRtProj {
    var r: RefractRtProj;
    let clip = u_camera.view_proj * vec4<f32>(world_pos, 1.0);
    if clip.w <= 0.0 {
        r.uv    = vec2<f32>(0.0, 0.0);
        r.valid = false;
        return r;
    }
    let ndc = clip.xyz / clip.w;
    r.uv    = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    r.valid = all(r.uv >= vec2<f32>(0.0, 0.0)) && all(r.uv <= vec2<f32>(1.0, 1.0));
    return r;
}

/// 背景の最終フォールバック（不透明ミス／画面外ヒット）。
/// GI 有効（DDGI）ならプローブ照度を、無効ならフラットアンビエントを返す
/// （本画面 evaluate_gi_ambient・反射 RT の rt_refl_fallback と同じ分岐方針）。
fn refract_rt_fallback(pos: vec3<f32>, dir: vec3<f32>) -> vec3<f32> {
    if u_gi_params.enabled != 0u {
        return ddgi_sample_irradiance(u_gi_params, pos, dir, t_gi_irradiance, t_gi_visibility, s_gi);
    }
    return u_light_meta.ambient_color * u_light_meta.ambient_intensity;
}

// ─── 背景取得本体（refract_common.wgsl の refract_sample_bg を RT で供給）─────

/// 本物の RT 屈折で背景色（リニア HDR RGB）を返す。glass_composite から呼ばれる。
/// - `surf`   : シェーディング面（world_pos/normal/roughness を使う。normal は front 反転済み）。
/// - `frag_xy`: フラグメント座標（未使用だが SS 版とシグネチャを一致させる）。
/// - `ior`    : 屈折率（>1）。material_refracts のときのみ呼ばれる（ior<=1 は glass_composite 手前で除外）。
fn refract_sample_bg(surf: Surface, frag_xy: vec2<f32>, ior: f32) -> vec3<f32> {
    // 視線ベクトル V（面→カメラ）。入射レイ方向は -V（カメラ→面）。
    let v = normalize(u_camera.position - surf.world_pos);
    let n = surf.normal; // gather_surface で front_facing 反転済み（常にカメラ側を向く）。
    let eta_in = 1.0 / max(ior, 1.0); // 空気→媒質（入射）。

    // 1. 入射屈折: 表面で refract(-V, N, 1/ior)。全反射（0 ベクトル）なら鏡面反射方向へ。
    var dir = refract(-v, n, eta_in);
    if dot(dir, dir) < REFRACT_RT_TIR_EPS {
        // 全反射（TIR）: 屈折レイが消えるので、背景レイには鏡面反射方向を使う（コメント）。
        dir = reflect(-v, n);
    }
    dir = normalize(dir);

    // レイ原点はシェーディング面から法線側へ微小に押し出す（入射面自身の三角形の再ヒット回避）。
    var origin = surf.world_pos + n * REFRACT_RT_ORIGIN_BIAS;

    // 界面を貫くごとに累積する透過色（色付き影と同一モデル）。背景の光がこの色でフィルタされる。
    var tint = vec3<f32>(1.0, 1.0, 1.0);

    // 2. 界面トレースループ（半透明マスク 0x02 のみ・最近ヒット・最大 REFRACT_MAX_INTERFACES）。
    for (var i: u32 = 0u; i < REFRACT_MAX_INTERFACES; i = i + 1u) {
        var desc: RayDesc;
        desc.flags     = RAY_FLAG_NONE;            // 最近ヒットを取り、次反復で先へ進める（TERMINATE なし）。
        desc.cull_mask = REFRACT_RT_MASK_TRANSLUCENT;
        desc.tmin      = select(REFRACT_RT_T_STEP, REFRACT_RT_TMIN, i == 0u); // 初回のみ TMIN、以降は微小前進。
        desc.tmax      = REFRACT_RT_TMAX;
        desc.origin    = origin;
        desc.dir       = dir;

        var rq: ray_query;
        rayQueryInitialize(&rq, rt_accel, desc);
        rayQueryProceed(&rq);
        let hit = rayQueryGetCommittedIntersection(&rq);
        if hit.kind == RAY_QUERY_INTERSECTION_NONE {
            break; // これ以上の半透明界面は無い。
        }

        let hit_pos = origin + dir * hit.t;

        // 入射面（front_face）でだけ界面色を乗せる（色付き影と同一規約＝二重計上防止）。
        // 裏面（出射面）は tint を掛けない（媒質 1 個につき透過色は入射面で 1 回だけ）。
        if hit.front_face {
            tint = tint * refract_layer_tint(hit.instance_custom_data);
        }

        // 後続界面では方向を更新しない（一次屈折の方向のまま直進して界面色だけ累積する）。
        // inline RQ はヒット法線を返さず、法線無しの再屈折は破綻するため（限界はファイル冒頭参照）。
        // 次のレイへ: 原点をヒット点へ進める（tmin は上の select で微小前進に切替）。
        origin = hit_pos;
    }

    // 3. 最終背景: 現在の方向で不透明（0x01）を最近トレースする。
    var odesc: RayDesc;
    odesc.flags     = RAY_FLAG_NONE;
    odesc.cull_mask = REFRACT_RT_MASK_OPAQUE;
    odesc.tmin      = REFRACT_RT_TMIN;
    odesc.tmax      = REFRACT_RT_TMAX;
    odesc.origin    = origin;
    odesc.dir       = dir;

    var orq: ray_query;
    rayQueryInitialize(&orq, rt_accel, odesc);
    rayQueryProceed(&orq);
    let ohit = rayQueryGetCommittedIntersection(&orq);

    var bg: vec3<f32>;
    if ohit.kind != RAY_QUERY_INTERSECTION_NONE {
        // 不透明にヒット: ヒット点を画面へ射影し、画面内なら refract_bg（不透明のみのコピー）を
        // その UV で roughness 連動サンプル（すりガラス維持。reflection_rt のハイブリッドと同じ考え方）。
        let opos = origin + dir * ohit.t;
        let proj = refract_rt_project(opos);
        if proj.valid {
            bg = refract_bg_sample(proj.uv, surf.roughness);
        } else {
            // 画面外ヒット（本画面に写っていない不透明面）→ DDGI プローブ照度 or アンビエント。
            bg = refract_rt_fallback(opos, dir);
        }
    } else {
        // 不透明ミス（空・開けた方向）→ DDGI プローブ照度 or アンビエント。
        bg = refract_rt_fallback(origin, dir);
    }

    // 背景の光は貫いた界面群の透過色でフィルタされる（ガラス越しのガラス・厚み色）。
    // シェーディング面自身の色は glass_composite 側で base_color_factor により別途乗る（二重計上しない）。
    return bg * tint;
}
