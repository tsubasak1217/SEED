// ============================================================
// ddgi_common.wgsl - DDGI shared definitions (no bindings)
//
// GiParams struct + octahedral map + probe grid/atlas indexing + probe sampling
// (trilinear + Chebyshev visibility). Pure functions only, no bindings declared
// (same idea as cluster_common.wgsl; concatenated into both compute and fragment
// so reflection does not mix groups).
//
// Concat order:
//   fragment: cluster_common -> ddgi_common -> light_common -> ...
//   compute : cluster_common -> ddgi_common -> ddgi_probe_update
// light_common.wgsl references GiParams, so ddgi_common must precede it.
//
// Rust mirrors:
//   GiParams        -> ddgi/params.rs (naga size cross-check)
//   oct_encode/dec  -> ddgi/octahedral.rs (roundtrip test)
//   grid/atlas index-> ddgi/grid.rs (same convention)
// No vec3 padding (align16 trap): origin/spacing packed as 3 scalars.
// ============================================================

const GI_IRR_RES:   u32 = 8u;
const GI_VIS_RES:   u32 = 16u;
const GI_BORDER:    u32 = 1u;
const GI_IRR_TILE:  u32 = 10u;
const GI_VIS_TILE:  u32 = 18u;

struct GiParams {
    origin_x: f32,
    origin_y: f32,
    origin_z: f32,
    spacing_x: f32,
    spacing_y: f32,
    spacing_z: f32,
    dim_x: u32,
    dim_y: u32,
    dim_z: u32,
    probe_count: u32,
    atlas_cols: u32,
    atlas_rows: u32,
    enabled: u32,
    rays_per_probe: u32,
    probe_update_base: u32,
    frame_index: u32,
    intensity: f32,
    hysteresis: f32,
    recursive_weight: f32,
    // GI 方式コード（旧 _pad0 を転用・サイズ 80B 不変）。GI_MODE_* と一致。
    gi_mode: u32,
}

// GI 方式コード（Rust ddgi/params.rs の GI_MODE_* と一致させること）。
const GI_MODE_FLAT: u32 = 0u; // フラットアンビエント（間接光なし）
const GI_MODE_DDGI: u32 = 1u; // DDGI（プローブ格子）
const GI_MODE_SSGI: u32 = 2u; // SSGI（スクリーンスペース GI）

fn oct_encode(dir: vec3<f32>) -> vec2<f32> {
    let l1 = abs(dir.x) + abs(dir.y) + abs(dir.z);
    let inv = select(0.0, 1.0 / l1, l1 > 1e-8);
    var p = dir.xy * inv;
    if dir.z < 0.0 {
        let sx = select(-1.0, 1.0, p.x >= 0.0);
        let sy = select(-1.0, 1.0, p.y >= 0.0);
        p = vec2<f32>((1.0 - abs(p.y)) * sx, (1.0 - abs(p.x)) * sy);
    }
    return p * 0.5 + vec2<f32>(0.5);
}

fn oct_decode(uv: vec2<f32>) -> vec3<f32> {
    var p = uv * 2.0 - vec2<f32>(1.0);
    let z = 1.0 - abs(p.x) - abs(p.y);
    if z < 0.0 {
        let sx = select(-1.0, 1.0, p.x >= 0.0);
        let sy = select(-1.0, 1.0, p.y >= 0.0);
        p = vec2<f32>((1.0 - abs(p.y)) * sx, (1.0 - abs(p.x)) * sy);
    }
    return normalize(vec3<f32>(p.x, p.y, z));
}

// --- Probe grid indexing (mirror of ddgi/grid.rs) ---

// Probe index -> grid coord (px,py,pz). x is innermost.
fn gi_coord_of_index(gp: GiParams, index: u32) -> vec3<u32> {
    let px = index % gp.dim_x;
    let py = (index / gp.dim_x) % gp.dim_y;
    let pz = index / (gp.dim_x * gp.dim_y);
    return vec3<u32>(px, py, pz);
}

// Grid coord -> probe index.
fn gi_index_of_coord(gp: GiParams, c: vec3<u32>) -> u32 {
    return c.x + c.y * gp.dim_x + c.z * gp.dim_x * gp.dim_y;
}

// World position of the probe at grid coord.
fn gi_probe_world(gp: GiParams, c: vec3<u32>) -> vec3<f32> {
    return vec3<f32>(gp.origin_x, gp.origin_y, gp.origin_z)
         + vec3<f32>(f32(c.x) * gp.spacing_x, f32(c.y) * gp.spacing_y, f32(c.z) * gp.spacing_z);
}

// Atlas tile coord (tile units): tile_x = px + pz*dim_x, tile_y = py.
fn gi_probe_tile(gp: GiParams, c: vec3<u32>) -> vec2<u32> {
    return vec2<u32>(c.x + c.z * gp.dim_x, c.y);
}

// --- Atlas sampling (bilinear via explicit-LOD sample; safe in compute) ---
// Texel-center convention: interior texel i has oct UV (i+0.5)/res.
// Sampling maps oct uv in [0,1] to pixel-center coord base+border + uv*res.
fn gi_sample_tile(
    tex: texture_2d<f32>, samp: sampler,
    tile: vec2<u32>, dir: vec3<f32>,
    res: u32, tile_px: u32, atlas_cols: u32, atlas_rows: u32,
) -> vec4<f32> {
    let uv = oct_encode(dir);
    let base_x = f32(tile.x * tile_px + GI_BORDER);
    let base_y = f32(tile.y * tile_px + GI_BORDER);
    let px = base_x + uv.x * f32(res);
    let py = base_y + uv.y * f32(res);
    let w = f32(atlas_cols * tile_px);
    let h = f32(atlas_rows * tile_px);
    return textureSampleLevel(tex, samp, vec2<f32>(px / w, py / h), 0.0);
}

struct GiTrilinear {
    base: vec3<u32>,
    frac: vec3<f32>,
}

// Continuous grid coord of P; base is the low corner cell, frac in 0..1.
fn gi_trilinear_setup(gp: GiParams, p: vec3<f32>) -> GiTrilinear {
    let g = vec3<f32>(
        (p.x - gp.origin_x) / max(gp.spacing_x, 1e-6),
        (p.y - gp.origin_y) / max(gp.spacing_y, 1e-6),
        (p.z - gp.origin_z) / max(gp.spacing_z, 1e-6),
    );
    let maxc = vec3<f32>(f32(gp.dim_x - 1u), f32(gp.dim_y - 1u), f32(gp.dim_z - 1u));
    let gc = clamp(g, vec3<f32>(0.0), maxc);
    let base_max = max(maxc - vec3<f32>(1.0), vec3<f32>(0.0));
    let base_f = clamp(floor(gc), vec3<f32>(0.0), base_max);
    var r: GiTrilinear;
    r.base = vec3<u32>(u32(base_f.x), u32(base_f.y), u32(base_f.z));
    r.frac = clamp(gc - base_f, vec3<f32>(0.0), vec3<f32>(1.0));
    return r;
}

// Interpolate indirect irradiance at P with normal N from the 8 surrounding probes.
// Weight = trilinear * cosine(wrap) * Chebyshev-visibility.
// irr_tex: radiance (interior 8x8). vis_tex: visibility (.r=mean depth, .g=mean depth^2, interior 16x16).
// Uses textureSampleLevel (explicit LOD) so it is callable from compute too.
fn ddgi_sample_irradiance(
    gp: GiParams, p: vec3<f32>, n: vec3<f32>,
    irr_tex: texture_2d<f32>, vis_tex: texture_2d<f32>, samp: sampler,
) -> vec3<f32> {
    let tri = gi_trilinear_setup(gp, p);
    var sum = vec3<f32>(0.0);
    var wsum = 0.0;
    for (var i: u32 = 0u; i < 8u; i = i + 1u) {
        let ox = i & 1u;
        let oy = (i >> 1u) & 1u;
        let oz = (i >> 2u) & 1u;
        let c = vec3<u32>(
            min(tri.base.x + ox, gp.dim_x - 1u),
            min(tri.base.y + oy, gp.dim_y - 1u),
            min(tri.base.z + oz, gp.dim_z - 1u),
        );
        let wx = mix(1.0 - tri.frac.x, tri.frac.x, f32(ox));
        let wy = mix(1.0 - tri.frac.y, tri.frac.y, f32(oy));
        let wz = mix(1.0 - tri.frac.z, tri.frac.z, f32(oz));
        var w = max(wx * wy * wz, 1e-6);

        let probe_pos = gi_probe_world(gp, c);
        let tile = gi_probe_tile(gp, c);

        // Cosine (wrap) weight: stronger when the surface faces the probe.
        let dir_to_probe = normalize(probe_pos - p);
        let cosw = max(dot(dir_to_probe, n) * 0.5 + 0.5, 0.0);
        w = w * (cosw * cosw + 0.2);

        // Chebyshev visibility: attenuate probes occluded from P.
        let to_probe = probe_pos - p;
        let dist = length(to_probe);
        let vdir = normalize(-to_probe);
        let moments = gi_sample_tile(vis_tex, samp, tile, vdir, GI_VIS_RES, GI_VIS_TILE, gp.atlas_cols, gp.atlas_rows).rg;
        let mean = moments.x;
        let mean2 = moments.y;
        if dist > mean {
            let variance = max(mean2 - mean * mean, 0.0);
            let d = dist - mean;
            let cheb = variance / (variance + d * d);
            w = w * max(cheb * cheb * cheb, 0.0);
        }

        let irr = gi_sample_tile(irr_tex, samp, tile, n, GI_IRR_RES, GI_IRR_TILE, gp.atlas_cols, gp.atlas_rows).rgb;
        sum = sum + w * irr;
        wsum = wsum + w;
    }
    if wsum <= 1e-6 {
        return vec3<f32>(0.0);
    }
    return sum / wsum;
}

// --- Update-compute helpers: interior texel <-> direction, gutter source texel ---

// Octahedral direction of interior texel (i,j), i,j in [0,res-1]. UV = ((i+0.5)/res,(j+0.5)/res).
fn gi_texel_dir(ij: vec2<u32>, res: u32) -> vec3<f32> {
    let uv = (vec2<f32>(f32(ij.x), f32(ij.y)) + vec2<f32>(0.5)) / f32(res);
    return oct_decode(uv);
}

// For a gutter (border) texel, return the interior texel to replicate (octahedral continuity).
// l is full-tile local coord [0, res+1] (interior is [1, res]); result is in the same coord space.
// Edges: mirror the tangential coord. Corners: copy the diagonal interior corner.
fn gi_border_source(l: vec2<i32>, res: i32) -> vec2<i32> {
    let hi = res + 1;
    let on_left = l.x == 0;
    let on_right = l.x == hi;
    let on_top = l.y == 0;
    let on_bottom = l.y == hi;
    let corner = (on_left || on_right) && (on_top || on_bottom);
    var s = l;
    if corner {
        s.x = select(res, 1, on_left);
        s.y = select(res, 1, on_top);
        return s;
    }
    if on_left  { s = vec2<i32>(1,   res + 1 - l.y); }
    if on_right { s = vec2<i32>(res, res + 1 - l.y); }
    if on_top   { s = vec2<i32>(res + 1 - l.x, 1);   }
    if on_bottom{ s = vec2<i32>(res + 1 - l.x, res); }
    return s;
}
