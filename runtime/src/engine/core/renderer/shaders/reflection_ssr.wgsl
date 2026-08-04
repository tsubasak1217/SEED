// ============================================================
// reflection_ssr.wgsl — スクリーンスペース反射（fragment fs_ssr, RAY_QUERY 不要）
//
// G-Buffer から反射ベクトル R を作り、シーン HDR をビュー空間の線形マーチ＋
// バイナリリファインで辿ってヒット色を拾う。粗面フェード・フレネル・画面端フェードを
// 掛けて RT_REFLECTION へ出力する。
//
// 連結順: [reflection_common, ddgi_common, reflection_ssr]
//
// マーチ定数（根拠）
//   SSR_MAX_STEPS=64      : 最大サンプル数（コスト上限）
//   SSR_MAX_DISTANCE=30.0 : ワールド単位の最大到達距離
//   SSR_STEP              : 1 ステップ前進量（= MAX_DISTANCE / MAX_STEPS）
//   SSR_THICKNESS=0.5     : 交差判定のビュー空間 Z 厚み（擦り抜け防止と誤ヒット抑制の折衷）
//   SSR_BINARY_STEPS=5    : 交差区間の二分細分（2^5=32 分解能）
// ============================================================

const SSR_MAX_STEPS:    i32 = 64;
const SSR_MAX_DISTANCE: f32 = 30.0;
const SSR_STEP:         f32 = SSR_MAX_DISTANCE / 64.0;
const SSR_THICKNESS:    f32 = 0.5;
const SSR_BINARY_STEPS: i32 = 5;
const SSR_BG_DEPTH:     f32 = 1.0;

// Group 3: DDGI（ミス時フォールバックの間接光）。
@group(3) @binding(0) var<uniform> ssr_gi:      GiParams;
@group(3) @binding(1) var          ssr_gi_irr:  texture_2d<f32>;
@group(3) @binding(2) var          ssr_gi_vis:  texture_2d<f32>;
@group(3) @binding(3) var          ssr_gi_samp: sampler;

fn ssr_view_z(world_pos: vec3<f32>) -> f32 {
    return (u_camera.view * vec4<f32>(world_pos, 1.0)).z;
}

struct SsrProj { uv: vec2<f32>, valid: bool }
fn ssr_project(world_pos: vec3<f32>) -> SsrProj {
    var r: SsrProj;
    let clip = u_camera.view_proj * vec4<f32>(world_pos, 1.0);
    if clip.w <= 0.0 {
        r.uv = vec2<f32>(0.0, 0.0);
        r.valid = false;
        return r;
    }
    let ndc = clip.xyz / clip.w;
    // NDC → **ビューポート相対** UV。画面内判定はこちらで行う（黒帯の外は画面外）。
    let uv_vp = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    r.valid = all(uv_vp >= vec2<f32>(0.0, 0.0)) && all(uv_vp <= vec2<f32>(1.0, 1.0));
    // 返すのは **RT 全面基準** UV。深度・G-Buffer・scene_hdr はすべて RT 全面で
    // 生成されるため、以降のサンプリングはこの規約で統一する。
    r.uv = reflection_rt_uv(uv_vp);
    return r;
}

// ミス時（マーチが画面外へ抜けた／何にも当たらなかった）の色。優先順位には理由がある:
//   ① 天球テクスチャの直接サンプル … 反射方向の空が**画面外**でも本物の空の色が返る。
//      これが無いと、空を映すべき床・水たまり・磨かれた面が GI の平坦色（GI 無効なら黒）になり、
//      同じシーンの水面反射（W5.2 は同じ経路を持つ）と見た目が食い違う。
//      画面内に写っている空を拾う段は D6 には無い（reflection_common.wgsl の理由参照:
//      本パスの時点で scene_hdr にはまだ skybox が描かれていない）。
//   ② GI プローブ … スカイボックスが無いシーンのフォールバック。
//   ③ 0（黒）… GI も無効なら寄与なし（加算合成なので黒は無害）。
fn ssr_fallback(world_pos: vec3<f32>, r_dir: vec3<f32>) -> vec3<f32> {
    let sky = reflection_sky_miss(r_dir);
    if sky.valid {
        return sky.color;
    }
    if ssr_gi.enabled != 0u {
        return ddgi_sample_irradiance(ssr_gi, world_pos, r_dir, ssr_gi_irr, ssr_gi_vis, ssr_gi_samp);
    }
    return vec3<f32>(0.0, 0.0, 0.0);
}

@fragment
fn fs_ssr(@builtin(position) frag: vec4<f32>) -> @location(0) vec4<f32> {
    let pix = vec2<i32>(frag.xy);
    let depth = textureLoad(t_depth, pix, 0);
    if depth >= SSR_BG_DEPTH {
        return vec4<f32>(0.0, 0.0, 0.0, 0.0);
    }

    let uv        = frag.xy / vec2<f32>(textureDimensions(t_gbuffer0));
    let world_pos = reflection_world_pos(uv, depth);
    let N         = normalize(textureLoad(t_gbuffer1, pix, 0).xyz);
    let V         = normalize(u_camera.position - world_pos);
    let ndotv     = max(dot(N, V), 1e-4);
    let R         = normalize(reflect(-V, N));

    let g0        = textureLoad(t_gbuffer0, pix, 0);
    let g2        = textureLoad(t_gbuffer2, pix, 0);
    let albedo    = g0.rgb;
    let metallic  = g2.r;
    let roughness = g2.g;

    let smooth_w  = reflection_smoothness_weight(roughness);
    if smooth_w <= 0.0 {
        return vec4<f32>(0.0, 0.0, 0.0, 0.0);
    }

    let f0      = reflection_f0(albedo, metallic);
    let fresnel = reflection_fresnel(f0, ndotv);

    var hit    = false;
    var hit_uv = vec2<f32>(0.0, 0.0);
    var t      = SSR_STEP;
    var prev_t = 0.0;
    for (var i: i32 = 0; i < SSR_MAX_STEPS; i = i + 1) {
        let p    = world_pos + R * t;
        let proj = ssr_project(p);
        if !proj.valid {
            break;
        }
        let spix        = vec2<i32>(proj.uv * vec2<f32>(textureDimensions(t_gbuffer0)));
        let scene_depth = textureLoad(t_depth, spix, 0);
        if scene_depth >= SSR_BG_DEPTH {
            prev_t = t;
            t = t + SSR_STEP;
            continue;
        }
        let scene_world = reflection_world_pos(proj.uv, scene_depth);
        let scene_vz    = ssr_view_z(scene_world);
        let ray_vz      = ssr_view_z(p);
        let diff        = ray_vz - scene_vz;
        if diff > 0.0 && diff < SSR_THICKNESS {
            let n_hit = normalize(textureLoad(t_gbuffer1, spix, 0).xyz);
            if dot(n_hit, R) > 0.0 {
                prev_t = t;
                t = t + SSR_STEP;
                continue;
            }
            var lo = prev_t;
            var hi = t;
            for (var k: i32 = 0; k < SSR_BINARY_STEPS; k = k + 1) {
                let mid = (lo + hi) * 0.5;
                let pm  = world_pos + R * mid;
                let pjm = ssr_project(pm);
                if !pjm.valid {
                    hi = mid;
                    continue;
                }
                let sdm = textureLoad(t_depth, vec2<i32>(pjm.uv * vec2<f32>(textureDimensions(t_gbuffer0))), 0);
                let swm = reflection_world_pos(pjm.uv, sdm);
                let dm  = ssr_view_z(pm) - ssr_view_z(swm);
                if dm > 0.0 {
                    hi = mid;
                } else {
                    lo = mid;
                }
            }
            let pf  = world_pos + R * hi;
            let pjf = ssr_project(pf);
            hit_uv  = select(proj.uv, pjf.uv, pjf.valid);
            hit     = true;
            break;
        }
        prev_t = t;
        t = t + SSR_STEP;
    }

    var reflected: vec3<f32>;
    var edge = 1.0;
    if hit {
        reflected = textureSampleLevel(t_scene_hdr, s_scene, hit_uv, 0.0).rgb;
        edge      = reflection_screen_edge_fade(hit_uv);
    } else {
        reflected = ssr_fallback(world_pos, R);
    }

    let color = reflected * fresnel * smooth_w * edge * u_reflection.intensity;
    return vec4<f32>(color, 1.0);
}
