// ============================================================
//  sprite_shape.wgsl — スプライトの「形と塗り」（W2-4）
//
//  角丸の矩形（四隅ごと）・楕円・弧を SDF で描き、縁の線・グラデーション（線形・放射・2〜4 色）・
//  画像の 9 スライス（伸ばす／繰り返す）・ぼかしの影・角丸と楕円の切り抜き（いちばん内側の祖先 1 つ）を行う。
//  欄がすべて既定のスプライトはこのシェーダーを通らず、従来の sprite.wgsl で描く（batch2d.rs が振り分ける）。
//
//  Group 0: CameraUniform（sprite.wgsl と同じ）
//  Group 1: テクスチャ + サンプラー（sprite.wgsl と同じ。テクスチャの無いスプライトは白 1×1）
//  Group 2: 形のパラメータの表（ストレージバッファ。ui_shape/params.rs の ShapeParamsGpu と 1:1）
//
//  頂点: slot0 = ユニットクワッド [0,1]²（位置だけ）、slot1 = インスタンス（モデル行列 + パラメータの番号）。
//  「形の空間」= スプライトの矩形 [0, 幅]×[0, 高さ]（キャンバスの単位・Y 下向き）。描く四角形はアンチエイリアス・
//  影のために形の空間で広げてあり（params.quad）、頂点シェーダーがユニットクワッドをその四角形の形の空間の位置へ写す。
//
//  出力は乗算済みアルファ（パイプラインの合成は PremultipliedAlpha。sprite.wgsl の AlphaBlending と同じ結果になる）。
//  式は ui_shape/{sdf,fill,nine_slice,clip_sdf}.rs と同じ（単体テストはそちらで検算する）。
// ============================================================

// ─── バインドグループ ─────────────────────────────────────────

struct CameraUniform {
    view_proj: mat4x4<f32>,
    view:      mat4x4<f32>,
    position:  vec3<f32>,
    _pad:      f32,
}
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

@group(1) @binding(0) var t_sprite: texture_2d<f32>;
@group(1) @binding(1) var s_sprite: sampler;

/// 形と塗りのパラメータ（ui_shape/params.rs の ShapeParamsGpu と 1:1）。
struct ShapeParams {
    quad:         vec4<f32>,             // 描く四角形（形の空間。x, y, 幅, 高さ）
    size:         vec4<f32>,             // 形の空間の大きさ（幅, 高さ）, 影のσ, 縁の太さ
    modes:        vec4<u32>,             // 形の種類, 塗りの種類, 旗, 色の数
    radii:        vec4<f32>,             // 四隅の半径（左上, 右上, 右下, 左下）
    arc:          vec4<f32>,             // 弧（中心線の半径, 太さの半分, 開始角, 角度。ラジアン）
    border_color: vec4<f32>,             // 縁の色
    colors:       array<vec4<f32>, 4>,   // 塗りの色の表（color を掛け済み）
    stops:        vec4<f32>,             // 色の位置
    grad:         vec4<f32>,             // 線形: p0, p1 ／ 放射: 中心, 半径
    nine_x:       vec4<f32>,             // 9 スライスの横（a, b, ua, ub）
    nine_y:       vec4<f32>,             // 9 スライスの縦（a, b, va, vb）
    nine_tiles:   vec4<f32>,             // 繰り返しの回数（横, 縦）
    clip_row0:    vec4<f32>,             // 切り抜き: ワールド → 領域のローカルの x
    clip_row1:    vec4<f32>,             // 切り抜き: ワールド → 領域のローカルの y
    clip_size:    vec4<f32>,             // 切り抜きの領域の大きさ
    clip_radii:   vec4<f32>,             // 切り抜きの四隅の半径
}
@group(2) @binding(0) var<storage, read> u_shapes: array<ShapeParams>;

// ─── 定数（ui_shape/params.rs と一致必須。単体テストで照合する）──

const SHAPE_KIND_NONE: u32 = 0u;
const SHAPE_KIND_ROUNDED_RECT: u32 = 1u;
const SHAPE_KIND_ELLIPSE: u32 = 2u;
const SHAPE_KIND_ARC: u32 = 3u;

const FILL_SOLID: u32 = 0u;
const FILL_LINEAR: u32 = 1u;
const FILL_RADIAL: u32 = 2u;

const FLAG_NINE_SLICE: u32 = 1u;
const FLAG_EDGE_REPEAT: u32 = 2u;
const FLAG_CENTER_REPEAT: u32 = 4u;
const FLAG_HOLLOW_CENTER: u32 = 8u;
const FLAG_SHADOW: u32 = 16u;
const FLAG_ROUND_CAPS: u32 = 32u;
const FLAG_CLIP_ROUNDED: u32 = 64u;
const FLAG_CLIP_ELLIPSE: u32 = 128u;

/// 色の表の大きさ。
const MAX_COLORS: u32 = 4u;
/// 半分（中心・被覆率の境界）。
const HALF: f32 = 0.5;
/// 0 とみなす長さ・アルファ（割り算の保護）。
const EPSILON: f32 = 1e-6;
/// 2π。
const TAU: f32 = 6.283185307179586;
/// √2。
const SQRT_2: f32 = 1.4142135623730951;
/// アルファがこれ未満の画素は描かない（sprite.wgsl と同じハードカット）。
const ALPHA_CUTOFF: f32 = 0.004;

// erf の近似（Abramowitz & Stegun 7.1.26）の係数（ui_shape/sdf.rs と同じ）
const ERF_P: f32 = 0.3275911;
const ERF_A1: f32 = 0.254829592;
const ERF_A2: f32 = -0.284496736;
const ERF_A3: f32 = 1.421413741;
const ERF_A4: f32 = -1.453152027;
const ERF_A5: f32 = 1.061405429;

// ─── 頂点入出力 ───────────────────────────────────────────────

struct VertIn {
    // per-vertex（ユニットクワッドの位置）
    @location(0) position: vec2<f32>,
    // per-instance（モデル行列の 4 列 + パラメータの番号）
    @location(1) m0:     vec4<f32>,
    @location(2) m1:     vec4<f32>,
    @location(3) m2:     vec4<f32>,
    @location(4) m3:     vec4<f32>,
    @location(5) params: u32,
}

struct VertOut {
    @builtin(position) clip_pos: vec4<f32>,
    /// 形の空間の位置。
    @location(0) local: vec2<f32>,
    /// ワールドの位置（切り抜きの写像の入力）。
    @location(1) world: vec2<f32>,
    /// パラメータの番号。
    @location(2) @interpolate(flat) params: u32,
}

@vertex
fn vs_main(v: VertIn) -> VertOut {
    let model = mat4x4<f32>(v.m0, v.m1, v.m2, v.m3);
    let world = model * vec4<f32>(v.position, 0.0, 1.0);
    let quad = u_shapes[v.params].quad;
    var out: VertOut;
    out.clip_pos = u_camera.view_proj * world;
    out.local = quad.xy + v.position * quad.zw;
    out.world = world.xy;
    out.params = v.params;
    return out;
}

// ─── SDF（ui_shape/sdf.rs と同じ式）───────────────────────────

/// 角丸の矩形（中心が原点・Y 下向き。半径は左上, 右上, 右下, 左下）。
fn sd_rounded_rect(p: vec2<f32>, half_size: vec2<f32>, radii: vec4<f32>) -> f32 {
    let r_left = select(radii.w, radii.x, p.y < 0.0);
    let r_right = select(radii.z, radii.y, p.y < 0.0);
    let r = select(r_right, r_left, p.x < 0.0);
    let q = abs(p) - half_size + vec2<f32>(r);
    return min(max(q.x, q.y), 0.0) + length(max(q, vec2<f32>(0.0))) - r;
}

/// 楕円の近似（円なら厳密）。
fn sd_ellipse(p: vec2<f32>, r: vec2<f32>) -> f32 {
    let rr = max(r, vec2<f32>(EPSILON));
    let k0 = length(p / rr);
    let k1 = length(p / (rr * rr));
    if (k1 <= EPSILON) {
        return -min(rr.x, rr.y);
    }
    return k0 * (k0 - 1.0) / k1;
}

/// 点から線分 [a, b] までの距離。
fn segment_distance(p: vec2<f32>, a: vec2<f32>, b: vec2<f32>) -> f32 {
    let ab = b - a;
    let ap = p - a;
    let len2 = dot(ab, ab);
    let t = select(0.0, clamp(dot(ap, ab) / max(len2, EPSILON), 0.0, 1.0), len2 > EPSILON);
    return length(ap - ab * t);
}

/// 弧（リング）。arc = (中心線の半径, 太さの半分, 開始角, 角度)。
fn sd_arc(p: vec2<f32>, arc: vec4<f32>, round_caps: bool) -> f32 {
    let ra = arc.x;
    let rb = arc.y;
    let ring = abs(length(p) - ra) - rb;
    if (arc.w >= TAU) {
        return ring;
    }
    var theta = atan2(p.y, p.x) - arc.z;
    theta = theta - floor(theta / TAU) * TAU;
    let e0 = vec2<f32>(cos(arc.z), sin(arc.z));
    let e1 = vec2<f32>(cos(arc.z + arc.w), sin(arc.z + arc.w));
    if (round_caps) {
        if (theta <= arc.w) {
            return ring;
        }
        return min(length(p - e0 * ra), length(p - e1 * ra)) - rb;
    }
    let cap = min(
        segment_distance(p, e0 * (ra - rb), e0 * (ra + rb)),
        segment_distance(p, e1 * (ra - rb), e1 * (ra + rb)),
    );
    if (theta <= arc.w) {
        return max(ring, -cap);
    }
    return cap;
}

/// 画素の幅のアンチエイリアスの被覆率。
fn coverage(d: f32, aa: f32) -> f32 {
    if (aa <= 0.0) {
        return select(0.0, 1.0, d <= 0.0);
    }
    return clamp(HALF - d / aa, 0.0, 1.0);
}

/// erf の近似。
fn erf_approx(x: f32) -> f32 {
    let s = select(1.0, -1.0, x < 0.0);
    let ax = abs(x);
    let t = 1.0 / (1.0 + ERF_P * ax);
    let poly = ((((ERF_A5 * t + ERF_A4) * t + ERF_A3) * t + ERF_A2) * t + ERF_A1) * t;
    return s * (1.0 - poly * exp(-ax * ax));
}

/// ぼかしの影の被覆率（σ がアンチエイリアスの幅より小さければ通常の被覆率）。
fn shadow_coverage(d: f32, sigma: f32, aa: f32) -> f32 {
    if (sigma <= max(aa, EPSILON)) {
        return coverage(d, aa);
    }
    return HALF * (1.0 - erf_approx(d / (sigma * SQRT_2)));
}

/// 画面の 1 画素がこの座標で何単位か（x・y の微分の二乗平均。分岐の前に呼ぶこと）。
fn pixel_width(p: vec2<f32>) -> f32 {
    let dx = dpdx(p);
    let dy = dpdy(p);
    return sqrt((dot(dx, dx) + dot(dy, dy)) * HALF);
}

/// パラメータ i の形の距離（形の空間の点）。
fn shape_distance(i: u32, p: vec2<f32>) -> f32 {
    let half_size = u_shapes[i].size.xy * HALF;
    let c = p - half_size;
    let kind = u_shapes[i].modes.x;
    if (kind == SHAPE_KIND_ELLIPSE) {
        return sd_ellipse(c, half_size);
    }
    if (kind == SHAPE_KIND_ARC) {
        return sd_arc(c, u_shapes[i].arc, (u_shapes[i].modes.z & FLAG_ROUND_CAPS) != 0u);
    }
    // 形なし・角丸の矩形（形なしの半径は 0）
    return sd_rounded_rect(c, half_size, u_shapes[i].radii);
}

// ─── 塗り（ui_shape/fill.rs と同じ式）─────────────────────────

/// 乗算済みアルファにする。
fn premultiply(c: vec4<f32>) -> vec4<f32> {
    return vec4<f32>(c.rgb * c.a, c.a);
}

/// 乗算済みアルファから戻す（アルファ 0 は黒の透明）。
fn unpremultiply(c: vec4<f32>) -> vec4<f32> {
    if (c.a <= EPSILON) {
        return vec4<f32>(0.0);
    }
    return vec4<f32>(c.rgb / c.a, c.a);
}

/// パラメータ i の塗りの色（ストレートアルファ）。
fn fill_color(i: u32, p: vec2<f32>) -> vec4<f32> {
    let kind = u_shapes[i].modes.y;
    let count = min(u_shapes[i].modes.w, MAX_COLORS);
    if (kind == FILL_SOLID || count <= 1u) {
        return u_shapes[i].colors[0];
    }
    let g = u_shapes[i].grad;
    var t: f32;
    if (kind == FILL_LINEAR) {
        let ab = g.zw - g.xy;
        t = dot(p - g.xy, ab) / max(dot(ab, ab), EPSILON);
    } else {
        t = length((p - g.xy) / max(g.zw, vec2<f32>(EPSILON)));
    }
    t = clamp(t, 0.0, 1.0);
    if (t <= u_shapes[i].stops[0]) {
        return u_shapes[i].colors[0];
    }
    for (var k = 1u; k < count; k = k + 1u) {
        let s1 = u_shapes[i].stops[k];
        if (t <= s1) {
            let s0 = u_shapes[i].stops[k - 1u];
            let f = clamp((t - s0) / max(s1 - s0, EPSILON), 0.0, 1.0);
            let a = premultiply(u_shapes[i].colors[k - 1u]);
            let b = premultiply(u_shapes[i].colors[k]);
            return unpremultiply(mix(a, b, f));
        }
    }
    return u_shapes[i].colors[count - 1u];
}

// ─── 9 スライス（ui_shape/nine_slice.rs の map_axis と同じ式）──

/// 1 軸の UV。s = (a, b, ua, ub)。
fn nine_axis(x: f32, s: vec4<f32>, size: f32, tiles: f32, repeat_mid: bool) -> f32 {
    if (x < s.x) {
        return select(0.0, x / max(s.x, EPSILON) * s.z, s.x > EPSILON);
    }
    if (x > s.y) {
        let len = size - s.y;
        return select(1.0, 1.0 - (size - x) / max(len, EPSILON) * (1.0 - s.w), len > EPSILON);
    }
    let t = (x - s.x) / max(s.y - s.x, EPSILON);
    let f = select(t, fract(t * tiles), repeat_mid);
    return s.z + f * (s.w - s.z);
}

// ─── フラグメント ─────────────────────────────────────────────

@fragment
fn fs_main(in: VertOut) -> @location(0) vec4<f32> {
    let i = in.params;
    // 微分は分岐の前（一様な制御の流れ）で求める
    let aa = pixel_width(in.local);
    let clip_local = vec2<f32>(
        dot(u_shapes[i].clip_row0.xyz, vec3<f32>(in.world, 1.0)),
        dot(u_shapes[i].clip_row1.xyz, vec3<f32>(in.world, 1.0)),
    );
    let clip_aa = pixel_width(clip_local);

    let modes = u_shapes[i].modes;
    let flags = modes.z;
    let size = u_shapes[i].size.xy;

    // ── 形の被覆率 ──
    let d = shape_distance(i, in.local);
    var cover = 1.0;
    if ((flags & FLAG_SHADOW) != 0u) {
        cover = shadow_coverage(d, u_shapes[i].size.z, aa);
    } else if (modes.x != SHAPE_KIND_NONE) {
        cover = coverage(d, aa);
    }

    // ── 切り抜き（いちばん内側の角丸・楕円の祖先）──
    if ((flags & (FLAG_CLIP_ROUNDED | FLAG_CLIP_ELLIPSE)) != 0u) {
        let half_clip = u_shapes[i].clip_size.xy * HALF;
        let c = clip_local - half_clip;
        var cd: f32;
        if ((flags & FLAG_CLIP_ELLIPSE) != 0u) {
            cd = sd_ellipse(c, half_clip);
        } else {
            cd = sd_rounded_rect(c, half_clip, u_shapes[i].clip_radii);
        }
        cover = cover * coverage(cd, clip_aa);
    }
    if (cover <= 0.0) {
        discard;
    }

    // ── 影: テクスチャを見ず、影の色 × 被覆率 ──
    if ((flags & FLAG_SHADOW) != 0u) {
        let shadow = premultiply(u_shapes[i].colors[0]) * cover;
        if (shadow.a < ALPHA_CUTOFF) {
            discard;
        }
        return shadow;
    }

    // ── UV（9 スライスか、矩形いっぱい）──
    var uv = in.local / max(size, vec2<f32>(EPSILON));
    var hollow = false;
    if ((flags & FLAG_NINE_SLICE) != 0u) {
        let nx = u_shapes[i].nine_x;
        let ny = u_shapes[i].nine_y;
        let tiles = u_shapes[i].nine_tiles.xy;
        let p = clamp(in.local, vec2<f32>(0.0), size);
        let in_cx = p.x >= nx.x && p.x <= nx.y;
        let in_cy = p.y >= ny.x && p.y <= ny.y;
        let edge_repeat = (flags & FLAG_EDGE_REPEAT) != 0u;
        let center_repeat = (flags & FLAG_CENTER_REPEAT) != 0u;
        // 横の写像: 縦が中央の帯なら中央の片、そうでなければ上下の辺
        let repeat_x = select(edge_repeat, center_repeat, in_cy);
        let repeat_y = select(edge_repeat, center_repeat, in_cx);
        uv = vec2<f32>(
            nine_axis(p.x, nx, size.x, tiles.x, repeat_x),
            nine_axis(p.y, ny, size.y, tiles.y, repeat_y),
        );
        hollow = in_cx && in_cy && (flags & FLAG_HOLLOW_CENTER) != 0u;
    }
    let tex = textureSampleLevel(t_sprite, s_sprite, uv, 0.0);
    // 塗り（従来と同じ tex × 色。単色なら色 = スプライトの color）
    var body = premultiply(tex * fill_color(i, in.local));
    if (hollow) {
        body = vec4<f32>(0.0);
    }

    // ── 縁の線（形の内側。縁の内側の境界 d = −太さ で塗りへ切り替える）──
    let border_width = u_shapes[i].size.w;
    if (border_width > 0.0 && modes.x != SHAPE_KIND_NONE) {
        let inner = coverage(d + border_width, aa);
        body = mix(premultiply(u_shapes[i].border_color), body, inner);
    }

    let out = body * cover;
    if (out.a < ALPHA_CUTOFF) {
        discard;
    }
    return out;
}
