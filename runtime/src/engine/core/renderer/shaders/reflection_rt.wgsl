// ============================================================
// reflection_rt.wgsl — レイトレ反射（fragment fs_rt, RAY_QUERY 必須）
//
// G-Buffer から反射ベクトル R を作り、TLAS へ closest-hit レイを 1 本飛ばす。
// ヒット点は【ハイブリッド】でシェーディングする:
//   画面内かつ深度一致（そのヒット面が本画面で実際に見えている）→ scene_hdr（本画面の
//     不透明ライティング済みコピー）を射影 UV でサンプル。本画面と同一のソフト影・GI・AO・
//     バウンスが反射に乗る＝「実際の影の濃さで反射」。
//   画面外／深度不一致（本画面で遮蔽され見えていない面）→ 従来の解析近似
//     （albedo*(direct/π + indirect)。間接光は本画面アンビエント規約に合わせ /π なし）。
// いずれもフレネル・粗面フェードを掛けて RT_REFLECTION へ出力する。
// レイのミス時は DDGI（無効なら環境光）へフォールバックする。
//
// 連結順: [cluster_common, reflection_common, ddgi_common, reflection_rt]
//   cluster_common   : LIGHT_KIND_* 定数
//   reflection_common: group0/1/2・純関数・reflection_world_pos
//   ddgi_common      : GiParams・ddgi_sample_irradiance
//
// ★同期必須★ 下記 rt_refl_distance_atten / rt_refl_shadow_ray は shaders/ddgi_probe_update.wgsl の
// gi_distance_atten / gi_shadow_ray の移植である。これらを変えたら両ファイルを合わせること。
// ただし rt_refl_direct_irradiance は DDGI の gi_direct_irradiance から【意図的に分岐】している:
//   DDGI  : 全灯を加算し、影は主要光(index 0)1灯のみ。プローブは面積積分なので多少の影漏れは平滑化される。
//   反射  : 強度上位 RT_REFLECTION_HIT_LIGHTS 灯だけを各1本のシャドウレイ付きで影評価する。
//           残りのライトは「捨てず」に、上位灯のシャドウレイ平均可視率で近似減衰して加算する（局所遮蔽の近似）。
//           影なし加算（反射に影が出ない）と全捨て（反射が黒く沈む）の中間を狙うバランス設計。
//           加えてヒット点へアンビエント/DDGI の床（rt_refl_hit_indirect）を足し、影の中でも黒つぶれさせない。
// この分岐は反射固有の品質要件によるもので、DDGI 側は従来のまま（触らない）。
// 別ファイルにしている理由: ddgi_probe_update.wgsl は group0 に compute 専用バインディングを
// 宣言しており、反射（fragment, group3/4）へ連結するとグループが混入して破綻するため。
// ============================================================

const RT_REFL_PI:        f32 = 3.14159265359;
const RT_REFL_RAY_TMIN:  f32 = 0.001;
const RT_REFL_ORIGIN_N:  f32 = 0.02;
const RT_REFL_RAY_TMAX:  f32 = 1.0e4;
const RT_REFL_CULL_MASK: u32 = 0x01u;

// 反射ヒット点で直接光を影付き評価する最大ライト数。反射レイごとに全灯へシャドウレイを
// 撃つとコストが跳ねるため、実効寄与が大きい上位この本数だけを各1本のシャドウレイで影評価する。
// 上位以外は捨てず、上位灯の平均可視率で近似減衰して加算する（rt_refl_direct_irradiance 参照）。
// 1 灯だと従来と同じ「主要光しか影が出ない」制限に戻るため 2 以上を推奨（>=1 が下限）。
const RT_REFLECTION_HIT_LIGHTS: u32 = 2u;

// 上位灯以外（＝シャドウレイを撃たない残りのライト）に掛ける近似可視率の下限。
// 残りライトの合計寄与に「上位灯のシャドウレイ平均可視率」を掛けて局所遮蔽を近似するが、
// その可視率をこの値でクランプして下限を持たせる（調整用ノブ）。
// 0.0 = 近似可視率を素通し（上位灯が完全遮蔽なら残りも 0 まで落ちる）。
// 上げると残りライトが影の中でも底上げされ、反射像のコントラストが緩む。
const REST_LIGHT_MIN_VISIBILITY: f32 = 0.0;

// 画面内ヒット採用（ハイブリッド）の深度一致許容（相対）。
// 反射レイのヒット点を screen へ射影し、その UV の G-Buffer 深度から復元したビュー深度と
// ヒット点のビュー深度の【相対差】がこの割合以内なら「そのヒット面は本画面で実際に見えている」
// と判定し、解析近似ではなく scene_hdr（本画面の不透明ライティング済みコピー）をサンプルして
// 反射色に採用する。本画面と同一のソフト影・GI・AO・バウンスが反射に乗る＝影の濃さが一致する。
// 相対 5% は shadow_mask / joint bilateral の深度一致判定と同じ流儀に揃えたもの。
const HIT_DEPTH_TOLERANCE: f32 = 0.05;

struct GpuLightR {
    color:            vec3<f32>,
    intensity:        f32,
    position:         vec3<f32>,
    range:            f32,
    direction:        vec3<f32>,
    kind:             u32,
    inner_cos:        f32,
    outer_cos:        f32,
    rect_half_width:  f32,
    rect_half_height: f32,
    rect_right:       vec3<f32>,
    shadow_index:     f32,
    rect_up:          vec3<f32>,
    soft_radius:      f32,
    bounce_intensity: f32,
    _pad0:            f32,
    _pad1:            f32,
    _pad2:            f32,
}

struct LightMetaR {
    count:             u32,
    rt_shadows:        u32,
    view_mode:         u32,
    _pad2:             u32,
    ambient_color:     vec3<f32>,
    ambient_intensity: f32,
}

@group(3) @binding(0) var<storage, read> rt_lights: array<GpuLightR>;
// meta は storage で読む（バインドレス B2: group3 は binding_array を含むため uniform 不可。
// レイアウトは 32B で uniform と一致。reflection.rs 側 BGL も storage_ro(1) に合わせる）。
@group(3) @binding(1) var<storage, read> rt_meta:   LightMetaR;
@group(3) @binding(2) var                rt_tlas:   acceleration_structure;
@group(3) @binding(3) var<storage, read> rt_albedo: array<vec4<f32>>;

@group(4) @binding(0) var<uniform> rt_gi:    GiParams;
@group(4) @binding(1) var          t_gi_irr: texture_2d<f32>;
@group(4) @binding(2) var          t_gi_vis: texture_2d<f32>;
@group(4) @binding(3) var          s_gi:     sampler;

fn rt_refl_distance_atten(dist: f32, range: f32) -> f32 {
    let d2 = dist * dist;
    let inv_sqr = 1.0 / max(d2, 1e-4);
    let factor = d2 / max(range * range, 1e-4);
    let window = clamp(1.0 - factor * factor, 0.0, 1.0);
    return inv_sqr * window * window;
}

fn rt_refl_shadow_ray(o: vec3<f32>, dir: vec3<f32>, tmax: f32) -> f32 {
    var desc: RayDesc;
    desc.flags     = RAY_FLAG_TERMINATE_ON_FIRST_HIT;
    desc.cull_mask = RT_REFL_CULL_MASK;
    desc.tmin      = RT_REFL_RAY_TMIN;
    desc.tmax      = max(tmax, RT_REFL_RAY_TMIN);
    desc.origin    = o;
    desc.dir       = dir;
    var rq: ray_query;
    rayQueryInitialize(&rq, rt_tlas, desc);
    rayQueryProceed(&rq);
    let hit = rayQueryGetCommittedIntersection(&rq);
    if hit.kind != RAY_QUERY_INTERSECTION_NONE {
        return 0.0;
    }
    return 1.0;
}

// ワールド座標のビュー空間 Z（カメラ前方が負）。画面内ヒットの深度一致判定に使う。
// u_camera.view は列優先アップロード済みのため view*world で正しくビュー座標になる
// （lighting_eval.wgsl / reflection_ssr.wgsl の ssr_view_z と同一）。
fn rt_refl_view_z(world_pos: vec3<f32>) -> f32 {
    return (u_camera.view * vec4<f32>(world_pos, 1.0)).z;
}

// ワールド座標 → screen UV 射影の結果（UV と画面内フラグ）。
struct RtReflProj { uv: vec2<f32>, valid: bool }

// ワールド座標を view_proj で screen UV へ射影する（逆行列不要）。
// clip.w<=0（カメラ背後）や UV が [0,1] の外なら valid=false。
// reflection_ssr.wgsl の ssr_project と同一手法（RT でも共有したいが SSR ファイルは RT へ
// 連結されないため、ここに同式を置く）。
fn rt_refl_project(world_pos: vec3<f32>) -> RtReflProj {
    var r: RtReflProj;
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

// 反射ヒット点の直接光放射照度 E を返す。
//   上位選定: 全灯を走査して各ライトの【実効寄与】(放射輝度 × N・L) を求め、その輝度が上位
//   RT_REFLECTION_HIT_LIGHTS 灯だけを各1本のシャドウレイ付きで影評価する。
//   残りライト: 捨てず、上位灯のシャドウレイ平均可視率（テスト灯が無ければ 1）で近似減衰して加算する。
//   REST_LIGHT_MIN_VISIBILITY で可視率の下限を持たせる。影ゼロ（反射に影なし）と全捨て（反射が黒沈み）の中間。
//   純粋な light.intensity 比較ではなく実効寄与で選ぶ理由: 距離減衰・スポット角・法線向きを
//   反映しないと、遠方や裏向きの強ライトを誤って上位に選び、近接の弱ライトの影を落としてしまう。
//   影の 0/1 ハード影（1灯1本レイ）は当面許容する。反射内のソフト影（1灯複数レイ）はコスト大のため将来課題。
fn rt_refl_direct_irradiance(hit_pos: vec3<f32>, n: vec3<f32>) -> vec3<f32> {
    // 上位 K 灯のスロット。昇順（index 0 が最弱）で維持し、最弱を追い出しながら K 灯を選抜する。
    // 各スロットは加算に必要な情報一式を持つ: 実効寄与ベクトル・ライト方向・シャドウレイ tmax・選定スコア。
    var top_contrib: array<vec3<f32>, RT_REFLECTION_HIT_LIGHTS>;
    var top_l:       array<vec3<f32>, RT_REFLECTION_HIT_LIGHTS>;
    var top_dist:    array<f32, RT_REFLECTION_HIT_LIGHTS>;
    var top_score:   array<f32, RT_REFLECTION_HIT_LIGHTS>;
    for (var s: u32 = 0u; s < RT_REFLECTION_HIT_LIGHTS; s = s + 1u) {
        top_contrib[s] = vec3<f32>(0.0, 0.0, 0.0);
        top_l[s]       = vec3<f32>(0.0, 0.0, 1.0);
        top_dist[s]    = RT_REFL_RAY_TMAX;
        top_score[s]   = 0.0;
    }

    // 全ライトの実効寄与の総和。上位灯ぶんを差し引いて「残りライトの合計寄与」を得るのに使う。
    var all_contrib_sum = vec3<f32>(0.0, 0.0, 0.0);

    let count = min(rt_meta.count, arrayLength(&rt_lights));
    for (var i: u32 = 0u; i < count; i = i + 1u) {
        let light = rt_lights[i];
        var l = vec3<f32>(0.0, 0.0, 1.0);
        var radiance = light.color * light.intensity;
        var dist = RT_REFL_RAY_TMAX;
        if light.kind == LIGHT_KIND_DIRECTIONAL {
            l = normalize(-light.direction);
        } else {
            let to_light = light.position - hit_pos;
            dist = length(to_light);
            l = to_light / max(dist, 1e-4);
            radiance = radiance * rt_refl_distance_atten(dist, light.range);
            if light.kind == LIGHT_KIND_SPOT {
                let cos_ang = dot(light.direction, -l);
                radiance = radiance * smoothstep(light.outer_cos, light.inner_cos, cos_ang);
            }
        }
        let ndl = max(dot(n, l), 0.0);
        if ndl <= 0.0 { continue; }
        let contrib = radiance * ndl;
        // 選定スコアは実効寄与の輝度（Rec.709）。0 以下は候補にならない。
        let score = dot(contrib, vec3<f32>(0.2126, 0.7152, 0.0722));
        if score <= 0.0 { continue; }
        // このライトの寄与を全体総和へ加える（後で上位灯ぶんを引いて「残り」を得る）。
        all_contrib_sum = all_contrib_sum + contrib;
        // 最弱スロット(index 0)より強ければ差し替え、隣接スワップで昇順を復元する（挿入ソート）。
        if score > top_score[0] {
            top_contrib[0] = contrib;
            top_l[0]       = l;
            top_dist[0]    = dist;
            top_score[0]   = score;
            for (var s: u32 = 0u; s + 1u < RT_REFLECTION_HIT_LIGHTS; s = s + 1u) {
                if top_score[s] > top_score[s + 1u] {
                    let tc = top_contrib[s]; top_contrib[s] = top_contrib[s + 1u]; top_contrib[s + 1u] = tc;
                    let tl = top_l[s];       top_l[s]       = top_l[s + 1u];       top_l[s + 1u]       = tl;
                    let td = top_dist[s];    top_dist[s]    = top_dist[s + 1u];    top_dist[s + 1u]    = td;
                    let ts = top_score[s];   top_score[s]   = top_score[s + 1u];   top_score[s + 1u]   = ts;
                }
            }
        }
    }

    // 選抜した上位 K 灯だけに各1本のシャドウレイを撃ち、遮蔽率（0/1）で減衰して加算する。
    // 同時に、上位灯ぶんの寄与総和・可視率の平均を集計する（残りライトの近似減衰に使う）。
    var e            = vec3<f32>(0.0, 0.0, 0.0);
    var top_contrib_sum = vec3<f32>(0.0, 0.0, 0.0);
    var vis_sum      = 0.0;
    var vis_count    = 0u;
    for (var s: u32 = 0u; s < RT_REFLECTION_HIT_LIGHTS; s = s + 1u) {
        if top_score[s] <= 0.0 { continue; }
        let shadow = rt_refl_shadow_ray(hit_pos + n * RT_REFL_RAY_TMIN, top_l[s], top_dist[s]);
        e               = e + top_contrib[s] * shadow;
        top_contrib_sum = top_contrib_sum + top_contrib[s];
        vis_sum         = vis_sum + shadow;
        vis_count       = vis_count + 1u;
    }

    // 残りライト（上位 K 灯以外）の合計寄与を、上位灯のシャドウレイ平均可視率で近似減衰して加算する。
    // 全灯へシャドウレイを撃つコストを避けつつ、局所的な遮蔽度合いをもっともらしく反映する近似。
    // テスト灯が無い（K=0 相当・上位が全て score<=0）ときは可視率 1（減衰なし）。
    // REST_LIGHT_MIN_VISIBILITY で可視率に下限を持たせる（0.0 なら素通し）。
    let rest_contrib = max(all_contrib_sum - top_contrib_sum, vec3<f32>(0.0, 0.0, 0.0));
    let avg_vis      = select(1.0, vis_sum / f32(max(vis_count, 1u)), vis_count > 0u);
    let rest_vis     = clamp(avg_vis, REST_LIGHT_MIN_VISIBILITY, 1.0);
    e = e + rest_contrib * rest_vis;
    return e;
}

// 反射ヒット点の間接光（アンビエントの床）放射照度を返す。本画面の evaluate_gi_ambient と同じ分岐:
//   GI 有効（rt_gi.enabled != 0）: DDGI プローブ照度を補間し、recursive_weight で反射内の再帰爆発を抑える。
//   GI 無効（0：RT 非対応・GI オフ・プレビューパス）: フラットアンビエント ambient_color × ambient_intensity。
// これがヒット照度の下限（床）になり、直接光が影で 0 になっても反射像が黒くつぶれない。
// ミス時のフォールバック rt_refl_fallback と同じ分岐方針（GI 有効ならプローブ、無効ならフラット）。
fn rt_refl_hit_indirect(hit_pos: vec3<f32>, n: vec3<f32>) -> vec3<f32> {
    if rt_gi.enabled != 0u {
        return ddgi_sample_irradiance(rt_gi, hit_pos, n, t_gi_irr, t_gi_vis, s_gi) * rt_gi.recursive_weight;
    }
    return rt_meta.ambient_color * rt_meta.ambient_intensity;
}

fn rt_refl_fallback(world_pos: vec3<f32>, r_dir: vec3<f32>) -> vec3<f32> {
    if rt_gi.enabled != 0u {
        return ddgi_sample_irradiance(rt_gi, world_pos, r_dir, t_gi_irr, t_gi_vis, s_gi);
    }
    return rt_meta.ambient_color * rt_meta.ambient_intensity;
}

@fragment
fn fs_rt(@builtin(position) frag: vec4<f32>) -> @location(0) vec4<f32> {
    let pix = vec2<i32>(frag.xy);
    let depth = textureLoad(t_depth, pix, 0);
    if depth >= 1.0 {
        return vec4<f32>(0.0, 0.0, 0.0, 0.0);
    }

    let uv        = frag.xy / u_camera.resolution;
    let world_pos = reflection_world_pos(uv, depth);
    let N         = normalize(textureLoad(t_gbuffer1, pix, 0).xyz);
    let V         = normalize(u_camera.position - world_pos);
    let ndotv     = max(dot(N, V), 1e-4);
    let R         = normalize(reflect(-V, N));

    let g0        = textureLoad(t_gbuffer0, pix, 0);
    let g2        = textureLoad(t_gbuffer2, pix, 0);
    let albedo0   = g0.rgb;
    let metallic  = g2.r;
    let roughness = g2.g;

    let smooth_w  = reflection_smoothness_weight(roughness);
    if smooth_w <= 0.0 {
        return vec4<f32>(0.0, 0.0, 0.0, 0.0);
    }

    let f0      = reflection_f0(albedo0, metallic);
    let fresnel = reflection_fresnel(f0, ndotv);

    var desc: RayDesc;
    desc.flags     = 0u;
    desc.cull_mask = RT_REFL_CULL_MASK;
    desc.tmin      = RT_REFL_RAY_TMIN;
    desc.tmax      = RT_REFL_RAY_TMAX;
    desc.origin    = world_pos + N * RT_REFL_ORIGIN_N;
    desc.dir       = R;
    var rq: ray_query;
    rayQueryInitialize(&rq, rt_tlas, desc);
    loop {
        if !rayQueryProceed(&rq) { break; }
    }
    let hit = rayQueryGetCommittedIntersection(&rq);

    var reflected: vec3<f32>;
    if hit.kind == RAY_QUERY_INTERSECTION_NONE {
        reflected = rt_refl_fallback(world_pos, R);
    } else {
        let hit_pos = desc.origin + R * hit.t;
        let n_hit   = -R;

        // ── ハイブリッド：画面内に見えているヒットは本画面のライティング結果を採用 ──
        // 反射レイのヒット点を screen へ射影し、その UV の G-Buffer 深度から復元したビュー深度と
        // ヒット点のビュー深度が相対一致（= その面が本画面で実際に見えており、手前で遮蔽されて
        // いない）なら、scene_hdr（本フレームの不透明ライティング済みコピー。SSR と同じ入力を
        // group2 で共有）をその UV でサンプルして反射色に採用する。これで本画面と同一のソフト影・
        // SSGI/DDGI・AO・疑似バウンスがそのまま反射に乗る＝ユーザー要望「実際の影の濃さで反射」。
        var used_screen      = false;
        var reflected_screen = vec3<f32>(0.0, 0.0, 0.0);
        let proj = rt_refl_project(hit_pos);
        if proj.valid {
            let spix   = vec2<i32>(proj.uv * u_camera.resolution);
            let sdepth = textureLoad(t_depth, spix, 0);
            // 背景（深度 1.0）は面が無い＝一致対象外。手前に別の面があるケースも下の相対差で弾く。
            if sdepth < 1.0 {
                let scene_world = reflection_world_pos(proj.uv, sdepth);
                let scene_vz    = rt_refl_view_z(scene_world);
                let hit_vz      = rt_refl_view_z(hit_pos);
                // 相対深度一致（HIT_DEPTH_TOLERANCE, 相対 5%）。一致＝その UV の可視面がヒット面本人。
                if abs(scene_vz - hit_vz) <= HIT_DEPTH_TOLERANCE * max(abs(hit_vz), 1e-4) {
                    reflected_screen = textureSampleLevel(t_scene_hdr, s_scene, proj.uv, 0.0).rgb;
                    used_screen      = true;
                }
            }
        }

        if used_screen {
            // 画面内ヒット：本画面の最終ライティング色をそのまま反射色に。影の濃さが本画面と一致する。
            reflected = reflected_screen;
        } else {
            // ── 画面外／深度不一致（本画面で遮蔽され見えていない面）は従来の解析近似へ ──
            // ヒット先のベースカラー アルベド。実体は連結される reflection_rt_hit_{on,off}.wgsl が供給する:
            //   on（バインドレス対応）: instance_custom_data → instance_table → UV 補間 → テクスチャサンプル
            //   off（従来）          : instance_custom_data で平均アルベド storage を引く（ベタ塗り）
            // ミップは 0 固定（レイ微分は将来課題）。フォールバック（flags 対象外・tex 0）は on 側で平均色へ。
            let albedo  = rt_hit_base_color(hit.instance_custom_data, hit.primitive_index, hit.barycentrics);
            let direct  = rt_refl_direct_irradiance(hit_pos, n_hit);
            // 間接光の床（GI 有効なら DDGI プローブ、無効ならフラットアンビエント）。
            // 従来は enabled に関わらず DDGI を無条件サンプルしていたが、GI 無効時にフラットアンビエントへ
            // フォールバックする分岐へ変更（本画面 evaluate_gi_ambient・ミス時 rt_refl_fallback と整合）。
            let indirect = rt_refl_hit_indirect(hit_pos, n_hit);
            // ── 明るさ規約を本画面 evaluate_lighting へ揃える（画面内⇔画面外の境界の飛びを抑える）──
            //   直接光: 拡散 Lambert = albedo/π × E（shade_light の kD*albedo/PI 相当）。BRDF どおり /π を残す。
            //   間接光: albedo × E（/π なし）。本画面アンビエント（ambient_color*intensity*albedo・
            //           evaluate_gi_ambient）は /π を掛けないため、従来の /π は本画面より約 1/π 暗かった。
            //           ここで間接光の /π を外して本画面と同一規約に補正する。
            // 【限界（完全一致は不可能）】画面内 scene_hdr サンプルとの境界には明るさ差が残る:
            //   - specular の視点依存（scene_hdr は本画面視点、反射は反射視点）
            //   - AO/occlusion の有無（解析側はヒット点の occlusion を持たない）
            //   - 影の質（解析側は上位 RT_REFLECTION_HIT_LIGHTS 灯のハード影、本画面はソフト影/デノイズ）
            //   境界のシームを完全には消せないが、/π 補正で段差を最小化する。
            reflected = albedo * (direct / RT_REFL_PI + indirect);
        }
    }

    let color = reflected * fresnel * smooth_w * u_reflection.intensity;
    return vec4<f32>(color, 1.0);
}
