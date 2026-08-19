// ============================================================
// ddgi_probe_update.wgsl - DDGI probe update compute (inline ray query)
//
// One workgroup = one probe. Rotates through probes_this_frame probes/frame.
// Phase 1: cast rays_per_probe rays from the probe, shade each hit, store in
//          workgroup shared memory (radiance + distance).
// Phase 2: integrate the ray results into the radiance octahedral tile (8x8) and
//          the visibility tile (16x16), temporally blend with the history atlas,
//          write the current atlas. Then fill the 1px gutter (border replication).
//
// Concat order: cluster_common.wgsl -> ddgi_common.wgsl -> this file.
//   cluster_common supplies LIGHT_KIND_* consts. GpuLight/LightMeta are mirrored
//   here (compute cannot include light_common.wgsl: its group-4 bindings would leak
//   into reflection). ddgi_common supplies GiParams + octahedral + grid + sampling.
//
// Approximations (bindless avoidance):
//   - hit material: primitive average albedo indexed by TLAS custom_index.
//   - hit normal:   -ray_dir (no vertex fetch). Surfaces roughly face the ray origin.
//   - direct light: distance attenuation + ndl(approx normal) + ONE shadow ray for
//                   the primary light only.
//   - recursive:    sample previous-frame probe irradiance at the hit point
//                   (multi-bounce), scaled by recursive_weight.
// See docs/rendering_roadmap.md for limits.
// ============================================================

// GpuLight mirror (112 bytes; must match lighting.rs / light_common.wgsl).
// vec3 padding is forbidden (align16 stride trap); tail padded with 3 scalars.
struct GiLight {
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

// LightMeta mirror (32 bytes; must match lighting.rs).
struct GiLightMeta {
    count:             u32,
    rt_shadows:        u32,
    view_mode:         u32,
    _pad2:             u32,
    ambient_color:     vec3<f32>,
    ambient_intensity: f32,
}

// --- group 0 bindings (compute-only manual layout; mirror in pipeline.rs) ---
@group(0) @binding(0) var<uniform>       u_gi:       GiParams;
@group(0) @binding(1) var<storage, read> gi_lights:  array<GiLight>;
@group(0) @binding(2) var<uniform>       gi_meta:    GiLightMeta;
@group(0) @binding(3) var                gi_tlas:    acceleration_structure;
@group(0) @binding(4) var<storage, read> gi_albedo:  array<vec4<f32>>; // per TLAS instance
@group(0) @binding(5) var                hist_irr:   texture_2d<f32>;
@group(0) @binding(6) var                hist_vis:   texture_2d<f32>;
@group(0) @binding(7) var                gi_samp:    sampler;
@group(0) @binding(8) var                out_irr:    texture_storage_2d<rgba16float, write>;
@group(0) @binding(9) var                out_vis:    texture_storage_2d<rgba16float, write>;

// --- constants ---
const GI_PI: f32 = 3.14159265359;
const GI_RAY_TMIN: f32 = 0.02;   // origin offset to avoid self-hit at the probe
const GI_RAY_TMAX: f32 = 1.0e4;  // radiance can come from any distance
const GI_VIS_SHARPNESS: f32 = 50.0;
const GI_SHADOW_CULL_MASK: u32 = 0x01u; // opaque occluders only (matches rt_shadow)
const GI_WG_THREADS: u32 = 64u;
const GI_MAX_RAYS: u32 = 64u;

// Workgroup-shared ray results and interior tile caches.
var<workgroup> s_dir:  array<vec3<f32>, GI_MAX_RAYS>;
var<workgroup> s_rad:  array<vec3<f32>, GI_MAX_RAYS>;
var<workgroup> s_dist: array<f32, GI_MAX_RAYS>;
var<workgroup> s_irr:  array<vec3<f32>, 64>;   // 8x8 interior radiance
var<workgroup> s_vis:  array<vec2<f32>, 256>;  // 16x16 interior visibility

// --- small hash / rng ---
fn gi_hash_u32(x0: u32) -> u32 {
    var x = x0;
    x = x ^ (x >> 16u);
    x = x * 0x7feb352du;
    x = x ^ (x >> 15u);
    x = x * 0x846ca68bu;
    x = x ^ (x >> 16u);
    return x;
}
fn gi_rand01(seed: u32) -> f32 {
    return f32(gi_hash_u32(seed) & 0x00ffffffu) / f32(0x01000000u);
}

// Spherical Fibonacci direction i of n (uniform on the sphere).
fn gi_sphere_fib(i: u32, n: u32) -> vec3<f32> {
    let gr = 0.6180339887498949; // (sqrt(5)-1)/2
    let phi = 2.0 * GI_PI * fract(f32(i) * gr);
    let cos_t = 1.0 - (2.0 * f32(i) + 1.0) / f32(n);
    let sin_t = sqrt(max(1.0 - cos_t * cos_t, 0.0));
    return vec3<f32>(cos(phi) * sin_t, sin(phi) * sin_t, cos_t);
}

// Per-frame random orthonormal basis (decorrelates the ray set each frame).
fn gi_frame_basis(frame: u32) -> mat3x3<f32> {
    // random unit vector from the frame index
    let u = gi_rand01(frame * 3u + 0u);
    let v = gi_rand01(frame * 3u + 1u);
    let z = u * 2.0 - 1.0;
    let a = 2.0 * GI_PI * v;
    let r = sqrt(max(1.0 - z * z, 0.0));
    let f = vec3<f32>(r * cos(a), r * sin(a), z);
    // build a basis around f
    let up = select(vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(1.0, 0.0, 0.0), abs(f.y) > 0.9);
    let t = normalize(cross(up, f));
    let b = cross(f, t);
    return mat3x3<f32>(t, b, f);
}

// Trace an occlusion (shadow) ray; returns 1.0 if unobstructed, 0.0 if hit.
fn gi_shadow_ray(o: vec3<f32>, dir: vec3<f32>, tmax: f32) -> f32 {
    var desc: RayDesc;
    desc.flags     = RAY_FLAG_TERMINATE_ON_FIRST_HIT;
    desc.cull_mask = GI_SHADOW_CULL_MASK;
    desc.tmin      = GI_RAY_TMIN;
    desc.tmax      = max(tmax, GI_RAY_TMIN);
    desc.origin    = o;
    desc.dir       = dir;
    var rq: ray_query;
    rayQueryInitialize(&rq, gi_tlas, desc);
    rayQueryProceed(&rq);
    let hit = rayQueryGetCommittedIntersection(&rq);
    if hit.kind != RAY_QUERY_INTERSECTION_NONE {
        return 0.0;
    }
    return 1.0;
}

// UE4-style distance attenuation (inverse square + range window). Mirrors lighting_eval.
fn gi_distance_atten(dist: f32, range: f32) -> f32 {
    let d2 = dist * dist;
    let inv_sqr = 1.0 / max(d2, 1e-4);
    let factor = d2 / max(range * range, 1e-4);
    let window = clamp(1.0 - factor * factor, 0.0, 1.0);
    return inv_sqr * window * window;
}

// Direct irradiance E at a hit point with approximate normal n.
// Shadow ray only for the primary light (index 0), per design.
fn gi_direct_irradiance(hit_pos: vec3<f32>, n: vec3<f32>) -> vec3<f32> {
    var e = vec3<f32>(0.0);
    let count = min(gi_meta.count, arrayLength(&gi_lights));
    for (var i: u32 = 0u; i < count; i = i + 1u) {
        let light = gi_lights[i];
        var l = vec3<f32>(0.0, 0.0, 1.0);
        var radiance = light.color * light.intensity;
        var dist = GI_RAY_TMAX;
        if light.kind == LIGHT_KIND_DIRECTIONAL {
            l = normalize(-light.direction);
        } else {
            let to_light = light.position - hit_pos;
            dist = length(to_light);
            l = to_light / max(dist, 1e-4);
            radiance = radiance * gi_distance_atten(dist, light.range);
            if light.kind == LIGHT_KIND_SPOT {
                let cos_ang = dot(light.direction, -l);
                radiance = radiance * smoothstep(light.outer_cos, light.inner_cos, cos_ang);
            }
        }
        let ndl = max(dot(n, l), 0.0);
        if ndl <= 0.0 { continue; }
        var shadow = 1.0;
        if i == 0u {
            shadow = gi_shadow_ray(hit_pos + n * GI_RAY_TMIN, l, dist);
        }
        e = e + radiance * ndl * shadow;
    }
    return e;
}

// Cast one GI ray and return its outgoing radiance + hit distance (capped).
struct GiRayResult { radiance: vec3<f32>, dist: f32 }
fn gi_cast_ray(o: vec3<f32>, dir: vec3<f32>, dist_cap: f32) -> GiRayResult {
    var desc: RayDesc;
    desc.flags     = 0u; // closest hit (all BLAS are OPAQUE -> committed = nearest)
    desc.cull_mask = GI_SHADOW_CULL_MASK;
    desc.tmin      = GI_RAY_TMIN;
    desc.tmax      = GI_RAY_TMAX;
    desc.origin    = o;
    desc.dir       = dir;
    var rq: ray_query;
    rayQueryInitialize(&rq, gi_tlas, desc);
    loop {
        if !rayQueryProceed(&rq) { break; }
    }
    let hit = rayQueryGetCommittedIntersection(&rq);

    var res: GiRayResult;
    if hit.kind == RAY_QUERY_INTERSECTION_NONE {
        // Miss -> sky/ambient radiance, far distance.
        res.radiance = gi_meta.ambient_color * gi_meta.ambient_intensity;
        res.dist = dist_cap;
        return res;
    }
    let hit_pos = o + dir * hit.t;
    let n = -dir; // approximate hit normal
    // Average albedo indexed by material record index.
    // 【レコード索引規約】TLAS の custom_data は「そのインスタンスの先頭レコード番号」で、
    // 実レコードは `custom_data + geometry_index` で引く。非スキンは 1 インスタンス =
    // 1 ジオメトリなので geometry_index=0 で従来と同一。スキンは 1 体の全プリミティブが
    // 1 BLAS の複数ジオメトリへ統合されているため、ここでプリミティブ別のマテリアルに解決される。
    // Rust 側 rt_shadow.rs の MAX_RT_RECORDS / record_count と対。
    var albedo = vec3<f32>(0.5);
    let ai = hit.instance_custom_data + hit.geometry_index;
    if ai < arrayLength(&gi_albedo) {
        albedo = gi_albedo[ai].rgb;
    }
    let direct = gi_direct_irradiance(hit_pos, n);
    let bounce = ddgi_sample_irradiance(u_gi, hit_pos, n, hist_irr, hist_vis, gi_samp)
               * u_gi.recursive_weight;
    // Lambert outgoing radiance L_o = albedo/pi * E (E = irradiance).
    res.radiance = albedo * (direct + bounce) / GI_PI;
    res.dist = min(hit.t, dist_cap);
    return res;
}

@compute @workgroup_size(64)
fn cs_main(
    @builtin(workgroup_id) wid: vec3<u32>,
    @builtin(local_invocation_id) lid: vec3<u32>,
) {
    let t = lid.x;
    let probe_id = (u_gi.probe_update_base + wid.x) % max(u_gi.probe_count, 1u);
    let coord = gi_coord_of_index(u_gi, probe_id);
    let probe_pos = gi_probe_world(u_gi, coord);
    let tile = gi_probe_tile(u_gi, coord);

    let rays = min(u_gi.rays_per_probe, GI_MAX_RAYS);
    let maxsp = max(max(u_gi.spacing_x, u_gi.spacing_y), u_gi.spacing_z);
    let dist_cap = maxsp * 4.0;
    let basis = gi_frame_basis(u_gi.frame_index);

    // --- Phase 1: cast rays (thread i handles ray i) ---
    if t < rays {
        let dir = basis * gi_sphere_fib(t, rays);
        let r = gi_cast_ray(probe_pos, dir, dist_cap);
        s_dir[t] = dir;
        s_rad[t] = r.radiance;
        s_dist[t] = r.dist;
    }
    workgroupBarrier();

    // --- Phase 2 interior: radiance (8x8) + visibility (16x16) ---
    // Radiance: 64 interior texels == 64 threads (one each).
    {
        let ij = vec2<u32>(t % GI_IRR_RES, t / GI_IRR_RES);
        let d = gi_texel_dir(ij, GI_IRR_RES);
        var num = vec3<f32>(0.0);
        var den = 0.0;
        for (var i: u32 = 0u; i < rays; i = i + 1u) {
            let wdot = max(dot(d, s_dir[i]), 0.0);
            num = num + wdot * s_rad[i];
            den = den + wdot;
        }
        let irr = num / max(den, 1e-4);
        let px = vec2<i32>(
            i32(tile.x * GI_IRR_TILE + GI_BORDER + ij.x),
            i32(tile.y * GI_IRR_TILE + GI_BORDER + ij.y),
        );
        let hist = textureLoad(hist_irr, px, 0).rgb;
        let blended = mix(irr, hist, u_gi.hysteresis);
        s_irr[t] = blended;
        textureStore(out_irr, px, vec4<f32>(blended, 1.0));
    }
    // Visibility: 256 interior texels; 64 threads stride by 64.
    for (var k: u32 = t; k < GI_VIS_RES * GI_VIS_RES; k = k + GI_WG_THREADS) {
        let ij = vec2<u32>(k % GI_VIS_RES, k / GI_VIS_RES);
        let d = gi_texel_dir(ij, GI_VIS_RES);
        var wsum = 0.0;
        var msum = 0.0;
        var m2sum = 0.0;
        for (var i: u32 = 0u; i < rays; i = i + 1u) {
            let w = pow(max(dot(d, s_dir[i]), 0.0), GI_VIS_SHARPNESS);
            wsum = wsum + w;
            msum = msum + w * s_dist[i];
            m2sum = m2sum + w * s_dist[i] * s_dist[i];
        }
        let mean = msum / max(wsum, 1e-4);
        let mean2 = m2sum / max(wsum, 1e-4);
        let px = vec2<i32>(
            i32(tile.x * GI_VIS_TILE + GI_BORDER + ij.x),
            i32(tile.y * GI_VIS_TILE + GI_BORDER + ij.y),
        );
        let hist = textureLoad(hist_vis, px, 0).rg;
        let blended = mix(vec2<f32>(mean, mean2), hist, u_gi.hysteresis);
        s_vis[k] = blended;
        textureStore(out_vis, px, vec4<f32>(blended.x, blended.y, 0.0, 0.0));
    }
    workgroupBarrier();

    // --- Phase 2 border (gutter replication) ---
    // Radiance tile 10x10: fill the 36 border texels from s_irr.
    for (var k: u32 = t; k < GI_IRR_TILE * GI_IRR_TILE; k = k + GI_WG_THREADS) {
        let l = vec2<i32>(i32(k % GI_IRR_TILE), i32(k / GI_IRR_TILE));
        let is_border = l.x == 0 || l.y == 0 || l.x == i32(GI_IRR_TILE) - 1 || l.y == i32(GI_IRR_TILE) - 1;
        if !is_border { continue; }
        let src = gi_border_source(l, i32(GI_IRR_RES));
        let interior = s_irr[u32((src.y - 1) * i32(GI_IRR_RES) + (src.x - 1))];
        let px = vec2<i32>(i32(tile.x * GI_IRR_TILE) + l.x, i32(tile.y * GI_IRR_TILE) + l.y);
        textureStore(out_irr, px, vec4<f32>(interior, 1.0));
    }
    // Visibility tile 18x18: fill the 68 border texels from s_vis.
    for (var k: u32 = t; k < GI_VIS_TILE * GI_VIS_TILE; k = k + GI_WG_THREADS) {
        let l = vec2<i32>(i32(k % GI_VIS_TILE), i32(k / GI_VIS_TILE));
        let is_border = l.x == 0 || l.y == 0 || l.x == i32(GI_VIS_TILE) - 1 || l.y == i32(GI_VIS_TILE) - 1;
        if !is_border { continue; }
        let src = gi_border_source(l, i32(GI_VIS_RES));
        let interior = s_vis[u32((src.y - 1) * i32(GI_VIS_RES) + (src.x - 1))];
        let px = vec2<i32>(i32(tile.x * GI_VIS_TILE) + l.x, i32(tile.y * GI_VIS_TILE) + l.y);
        textureStore(out_vis, px, vec4<f32>(interior.x, interior.y, 0.0, 0.0));
    }
}
