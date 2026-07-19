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
//   (a) ガラス越しのガラスが映る（界面トレースで後続の半透明レイヤーで**実際に再屈折**する）。
//   (b) 自己屈折（厚み）: 入射面→出射面を貫くため、厚みぶんの色付き吸収が乗る。
//   (c) 視差が正確: 背景はレイのヒット点を実際に画面へ射影して得る（画面歪み近似ではない）。
//
// 【界面ごとの本物の再屈折（バインドレス法線メガバッファ）】
//   inline ray query の committed intersection は**ヒット三角形の頂点法線を直接は返さない**が、
//   primitive_index＋barycentrics＋instance_custom_data から、group4 の追加バインディング
//   （instance_table 17／index メガバッファ 18／法線メガバッファ 19。bindless.rs 所有）を引いて
//   **ヒット三角形の補間法線をオブジェクト空間で復元**し、hit.world_to_object の転置でワールド法線化する。
//   これを使い、front_face（入射＝空気→ガラス, eta=1/ior）／back_face（出射＝ガラス→空気, eta=ior）に
//   応じて **refract() で界面ごとに実屈折**する（多重ガラスが正しく屈折して重なる）。
//   ・法線が引けないインスタンス（BINDLESS_FLAG_GEOM 非立＝ジオメトリ未登録／範囲外 custom_data）は、
//     方向を更新せず直進して界面色（tint）だけ累積する従来近似へ**その界面だけ縮退**する（安全）。
//   ・ior は界面インスタンス個別値がテーブルに無いため、シェーディング面の material.ior を全界面に流用する
//     （限界。正確化するにはレコードに ior を積む必要がある）。
//   ・全反射（TIR）: refract が 0 ベクトルを返す界面では**方向を更新せず直進**する（レイ反転や
//     同一面ループを避けるため。入射面の一次屈折は shading 面 N で別途処理済み）。
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

/// group4 binding17: バインドレス インスタンステーブル（BindlessInstanceRecord 配列）。
/// TLAS custom_data で引き、界面ヒット先の index_offset / normal_offset / flags を得る。
/// bindless.rs 所有（色付き影の group3 binding0 と同一バッファ）。storage なので uniform と同居可。
@group(4) @binding(17) var<storage, read> refr_records: array<BindlessInstanceRecord>;
/// group4 binding18: インデックス メガバッファ（u32・三角形の頂点番号）。bindless.rs 所有。
@group(4) @binding(18) var<storage, read> refr_index:   array<u32>;
/// group4 binding19: 法線メガバッファ（八面体エンコード u32・頂点順）。bindless.rs 所有。
/// index メガバッファ経由で三角形の 3 頂点法線を引き、barycentrics で補間する。
@group(4) @binding(19) var<storage, read> refr_normal:  array<u32>;

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

/// ヒット三角形の頂点法線を八面体デコード＋重心補間し、ワールド空間の単位法線を返す。
/// - `rec`        : ヒット先インスタンスのレコード（index_offset / normal_offset を持つ）。
/// - `prim_index` : hit.primitive_index（三角形番号）。
/// - `bary`       : hit.barycentrics（頂点 1,2 の重み。頂点 0 の重み = 1-bary.x-bary.y）。
/// - `w2o`        : hit.world_to_object（mat4x3。ワールド→オブジェクトのアフィン）。
///
/// オブジェクト空間の補間法線 n_obj を、法線変換 = 逆転置(object_to_world) = 転置(world_to_object の線形部)
/// でワールドへ移す（非一様スケールでも正しい向きになる）。BLAS はオブジェクト空間の頂点位置で
/// 構築され、法線メガバッファも同じオブジェクト空間の頂点法線を保持するため整合する。
fn refr_hit_world_normal(rec: BindlessInstanceRecord, prim_index: u32, bary: vec2<f32>,
                         w2o: mat4x3<f32>) -> vec3<f32> {
    let tri = rec.index_offset + prim_index * 3u;
    let i0  = refr_index[tri + 0u];
    let i1  = refr_index[tri + 1u];
    let i2  = refr_index[tri + 2u];
    let n0  = oct_decode_normal(refr_normal[rec.normal_offset + i0]);
    let n1  = oct_decode_normal(refr_normal[rec.normal_offset + i1]);
    let n2  = oct_decode_normal(refr_normal[rec.normal_offset + i2]);
    let w   = vec3<f32>(1.0 - bary.x - bary.y, bary.x, bary.y);
    let n_obj = n0 * w.x + n1 * w.y + n2 * w.z;
    // world_to_object の線形部（列 0..2）の 3x3。法線ワールド化 = 転置(線形部) * n_obj。
    let m = mat3x3<f32>(w2o[0], w2o[1], w2o[2]);
    return normalize(transpose(m) * n_obj);
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
        let ai      = hit.instance_custom_data;

        // 入射面（front_face）でだけ界面色を乗せる（色付き影と同一規約＝二重計上防止）。
        // 裏面（出射面）は tint を掛けない（媒質 1 個につき透過色は入射面で 1 回だけ）。
        //
        // 【i==0 の自己入射面は必ずスキップ（二重計上防止・本修正の主眼）】
        // 原点を法線側へ押し出しているため i==0 のヒットは「シェーディング面自身の入射面」
        // （＝自分のガラスへ入る面）である。この面の透過色（自分のガラス自身の base_color）は
        // glass_composite 側で `bg * base_color_factor` として別途 1 回乗る（refract_common.wgsl
        // line 161/172）。ここでも乗せると自分のガラス色が **二乗** され、色ガラス（例: シアン
        // base_color=[0,1,0.9773], transmission=1）で背景がベタッと濃い色ベールになる（症状2）。
        // よって i==0 の自己入射面は tint を掛けず、i>=1 の後続界面（自分の出射面＝back_face は
        // 元々スキップ／2 枚目以降のガラスの入射面）だけ tint を累積する。
        //
        // 【WBOIT では界面 tint を一切累積しない（半透明相互の遮蔽が無いことによる二重計上の回避）】
        // 距離ソートでは手前ガラスが奥の半透明ピクセルを a_eff=1 で上書きするため、奥レイヤー
        // （例: 奥ガラスのカーテン）の色は「手前ガラスの背景 tint」として 1 回だけ現れる＝正しい。
        // ところが WBOIT は半透明同士を遮蔽できず全フラグメントが平均へ加算されるため、奥レイヤーは
        // (a) 手前ガラスの refract_sample_bg が乗せる界面 tint と、(b) 奥レイヤー自身の WBOIT 寄与、の
        // **2 回**計上され、色が過剰に濃く彩度過多になる（実機症状: 緑ガラス越しの赤カーテンがベッタリ）。
        // そこで WBOIT のときは界面 tint 累積を落とし（レイの屈折・最終不透明背景サンプルは維持）、
        // 奥レイヤーの色は WBOIT 平均で本人（b）が 1 回だけ寄与する形へ一本化する。
        let wboit_mode = (u_light_meta.translucency_rt & TRANSLUCENCY_RT_WBOIT) != 0u;
        if hit.front_face && i > 0u && !wboit_mode {
            tint = tint * refract_layer_tint(ai);
        }

        // ── 界面ごとの本物の再屈折 ──────────────────────────────
        // i==0 かつ front_face のヒットは「シェーディング面自身の入射面」＝一次屈折（上で
        // shading 面 N により処理済み）なので**再屈折しない**（原点を法線側へ押し出しているため
        // 最初に自分の表面を踏む。ここで再度 refract すると入射屈折が二重に掛かる）。それ以外の
        // 界面（自分の出射面・後続ガラスの入射/出射面）は補間法線で実屈折する。
        let do_refract = (i > 0u) || (!hit.front_face);
        if do_refract && ai < arrayLength(&refr_records) {
            let rec = refr_records[ai];
            // ジオメトリ（法線）登録済みインスタンスのみ再屈折。未登録は直進近似へ縮退（安全）。
            if (rec.flags & BINDLESS_FLAG_GEOM) != 0u {
                var n = refr_hit_world_normal(rec, hit.primitive_index, hit.barycentrics,
                                              hit.world_to_object);
                // refract() は N が入射側（-dir 側）を向くことを要求する。レイと同じ側なら反転。
                if dot(dir, n) > 0.0 { n = -n; }
                // front_face=入射（空気→ガラス, eta=1/ior）／back_face=出射（ガラス→空気, eta=ior）。
                let eta = select(ior, 1.0 / max(ior, 1.0), hit.front_face);
                let refr = refract(dir, n, eta);
                // 全反射（0 ベクトル）でなければ方向を更新。TIR は直進のまま（限界はファイル冒頭参照）。
                if dot(refr, refr) >= REFRACT_RT_TIR_EPS {
                    dir = normalize(refr);
                }
            }
        }

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
