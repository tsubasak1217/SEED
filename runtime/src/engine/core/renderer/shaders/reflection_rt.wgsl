// ============================================================
// reflection_rt.wgsl — レイトレ反射（fragment fs_rt, RAY_QUERY 必須）
//
// G-Buffer から反射ベクトル R を作り、TLAS へ closest-hit レイを 1 本飛ばす。
// ヒット点を近似シェーディング（albedo*(direct+bounce)/PI）し、フレネル・粗面フェードを
// 掛けて RT_REFLECTION へ出力する。ミス時は DDGI（無効なら環境光）へフォールバックする。
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
//   反射  : 強度上位 RT_REFLECTION_HIT_LIGHTS 灯だけを各1本のシャドウレイ付きで加算し、残りは捨てる。
//           鏡面反射はヒット点がそのまま見えるため、影なしのライトを足すと「反射像に影が出ない」症状に直結する
//           （実機確認済み）。上位以外を捨てて暗くする方向の誤差は鏡面反射では知覚されにくい。
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
// 撃つとコストが跳ねるため、実効寄与が大きい上位この本数だけを影付きで加算し、残りは捨てる。
// 1 灯だと従来と同じ「主要光しか影が出ない」制限に戻るため 2 以上を推奨（>=1 が下限）。
const RT_REFLECTION_HIT_LIGHTS: u32 = 2u;

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

// 反射ヒット点の直接光放射照度 E を返す。
//   選定方針: 全灯を走査して各ライトの【実効寄与】(放射輝度 × N・L) を求め、その輝度が上位
//   RT_REFLECTION_HIT_LIGHTS 灯だけを各1本のシャドウレイ付きで加算する。上位以外は捨てる。
//   純粋な light.intensity 比較ではなく実効寄与で選ぶ理由: 距離減衰・スポット角・法線向きを
//   反映しないと、遠方や裏向きの強ライトを誤って上位に選び、近接の弱ライトの影を落としてしまう。
//   捨てる（影なしで足さない）理由: 影なしで足すと「反射像に影が出ない」症状が残るため。
//   鏡面反射では暗くなる方向の誤差は知覚されにくいので、上位以外の寄与は無視して良い。
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

    // 選抜した上位 K 灯だけに各1本のシャドウレイを撃ち、遮蔽されていなければ加算する。
    var e = vec3<f32>(0.0, 0.0, 0.0);
    for (var s: u32 = 0u; s < RT_REFLECTION_HIT_LIGHTS; s = s + 1u) {
        if top_score[s] <= 0.0 { continue; }
        let shadow = rt_refl_shadow_ray(hit_pos + n * RT_REFL_RAY_TMIN, top_l[s], top_dist[s]);
        e = e + top_contrib[s] * shadow;
    }
    return e;
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
        // ヒット先のベースカラー アルベド。実体は連結される reflection_rt_hit_{on,off}.wgsl が供給する:
        //   on（バインドレス対応）: instance_custom_data → instance_table → UV 補間 → テクスチャサンプル
        //   off（従来）          : instance_custom_data で平均アルベド storage を引く（ベタ塗り）
        // ミップは 0 固定（レイ微分は将来課題）。フォールバック（flags 対象外・tex 0）は on 側で平均色へ。
        let albedo  = rt_hit_base_color(hit.instance_custom_data, hit.primitive_index, hit.barycentrics);
        let direct = rt_refl_direct_irradiance(hit_pos, n_hit);
        let bounce = ddgi_sample_irradiance(rt_gi, hit_pos, n_hit, t_gi_irr, t_gi_vis, s_gi)
                   * rt_gi.recursive_weight;
        reflected = albedo * (direct + bounce) / RT_REFL_PI;
    }

    let color = reflected * fresnel * smooth_w * u_reflection.intensity;
    return vec4<f32>(color, 1.0);
}
