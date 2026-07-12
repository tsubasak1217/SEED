// ============================================================
// particle_draw.wgsl  —  GPU パーティクル描画シェーダ（形状メッシュ×インスタンス）
//
// 頂点バッファ（@location(0) 位置 vec3、particle_shapes.rs の単位メッシュ）を
// バインドし、@builtin(instance_index) でストレージバッファから粒子を読む。
// 形状モードで分岐する:
//   - shape_mode=0（billboard/Point）: 頂点 xy をカメラ右／上ベクトルで展開し、
//     rot_angle の面内回転を適用したビルボード。UV は回転前のコーナーから得る
//     （テクスチャはクアッドと一緒に回転して見える）。
//   - shape_mode=1（mesh: Sphere/Box/Plane/Model）: 粒子ごとのランダム軸
//     （seed から決定的に生成）まわりに rot_angle の軸角回転（Rodrigues）を適用し、
//     scale_curve(t)×サイズ倍率でスケールして粒子位置へ平行移動する。
//     UV は中心固定 vec2(0.5)（v1 制限: メッシュ形状はテクスチャ中心色×粒子色）。
//
// 色は LUT の HSVA カーブを t=age/lifetime で線形サンプルし、シェーダ内で
// hsv→rgb 変換する（HSVA のまま補間することで色相が正しく回る）。
// random_color_count>0 のときは seed ハッシュでランダム色カーブを 1 本選ぶ。
//
// dead（age>=lifetime || lifetime<=0）はサイズ 0 に縮退させて不可視にする。
// 出力は HDR（トーンマップしない）で、フラグメントは premultiplied alpha を返す:
//   - Additive パイプライン: src=One, dst=One          → rgb*a を加算
//   - Alpha   パイプライン: src=One, dst=OneMinusSrcA  → premultiplied over
//
// Group 0: camera（既存 CameraUniform BGL を流用。頂点でのみ使用）
// Group 1: particles（storage read）+ params（uniform）+ lut（storage read）
// Group 2: texture + sampler（未指定時は白 1x1＋プロシージャル円）
//
// ※ Particle / EmitterParams / LUT のレイアウトは particle_sim.wgsl・Rust 側
//    particle_system.rs の repr(C) と厳密一致させること。
// ============================================================

// ─── 定数 ─────────────────────────────────────────────────────
const PI: f32 = 3.14159265359;
// 乱数ソルト（particle_sim.wgsl の系列と独立。seed から決定的に再現する）。
const SALT_SIZE:      u32 = 0x165667B1u; // 全体サイズ倍率
const SALT_COLORPICK: u32 = 0xB5297A4Du; // 色カーブ選択（リストからランダム 1 本）
const SALT_AXIS_COS:  u32 = 0x68E31DA4u; // 回転軸の cosθ
const SALT_AXIS_PHI:  u32 = 0x1B56C4E9u; // 回転軸の方位角
const SALT_TEXLAYER:  u32 = 0x2545F491u; // テクスチャ配列レイヤ選択
// 混合ハッシュ乗数／XOR（sim と同一の系。決定的であればよい）。
const HASH_MUL: u32 = 2654435769u;
const HASH_XOR: u32 = 2747636419u;

// LUT の固定カーブ本数（speed / rot_speed / scale）。色カーブはこの後ろに続く。
const LUT_FIXED_CURVES: u32 = 3u;

// ─── Group 0: カメラ（shader_common.wgsl の CameraUniform と同一レイアウト）──
struct CameraUniform {
    view_proj: mat4x4<f32>,
    view:      mat4x4<f32>,
    position:  vec3<f32>,
    _pad:      f32,
};
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// ─── Group 1: 粒子ストレージ＋エミッタパラメータ＋カーブ LUT ──
// （particle_sim.wgsl と同一レイアウト。stride 64 / uniform 192）
struct Particle {
    pos:        vec3<f32>, // 0   位置（World or Local）
    age:        f32,       // 12  経過秒
    vel:        vec3<f32>, // 16  蓄積速度（重力／抵抗）
    lifetime:   f32,       // 28  寿命秒（<=0 は dead）
    emit_dir:   vec3<f32>, // 32  正規化射出方向
    base_speed: f32,       // 44  基準初速
    seed:       u32,       // 48  乱数シード
    rot_angle:  f32,       // 52  蓄積回転角（ラジアン）
    _pad0:      u32,       // 56
    _pad1:      u32,       // 60
};

struct EmitterParams {
    world_mat:           mat4x4<f32>, // 0
    dt:                  f32,         // 64
    emit_count:          u32,         // 68
    ring_start:          u32,         // 72
    max_particles:       u32,         // 76
    frame_nonce:         u32,         // 80
    drag:                f32,         // 84
    spread_rad:          f32,         // 88
    shape_mode:          u32,         // 92   0=billboard / 1=mesh
    direction_local:     vec3<f32>,   // 96
    speed_min:           f32,         // 108
    speed_max:           f32,         // 112
    lifetime_min:        f32,         // 116
    lifetime_max:        f32,         // 120
    rot_speed_min:       f32,         // 124
    gravity:             vec3<f32>,   // 128
    rot_speed_max:       f32,         // 140
    spawn_box:           vec3<f32>,   // 144
    spawn_sphere_radius: f32,         // 156
    size_min:            f32,         // 160  全体サイズ倍率 min
    size_max:            f32,         // 164  全体サイズ倍率 max
    spawn_volume:        u32,         // 168
    sim_space:           u32,         // 172  0=World / 1=Local
    use_texture:         u32,         // 176
    lut_samples:         u32,         // 180  カーブ LUT のサンプル数 S
    color_count:         u32,         // 184  色カーブ本数（最低 1）
    initial_rot_min:     f32,         // 188  初期回転角 min（ラジアン。描画では未使用）
    initial_rot_max:     f32,         // 192  初期回転角 max（ラジアン。描画では未使用）
    tex_layer_count:     u32,         // 196  テクスチャ配列レイヤ数
    _pad0:               u32,         // 200
    _pad1:               u32,         // 204
};

@group(1) @binding(0) var<storage, read> particles: array<Particle>;
@group(1) @binding(1) var<uniform>       params:    EmitterParams;
@group(1) @binding(2) var<storage, read> lut:       array<vec4<f32>>;

// ─── Group 2: テクスチャ配列＋サンプラー ──────────────────────
// texture_2d_array（最大 MAX_PARTICLE_TEXTURES レイヤ）。粒子ごとに seed で 1 枚選ぶ。
@group(2) @binding(0) var t_particle: texture_2d_array<f32>;
@group(2) @binding(1) var s_particle: sampler;

// ─── ハッシュ（seed から決定的に属性を再現する）───────────────
fn hash_u32(x: u32) -> u32 {
    var s = x ^ HASH_XOR;
    s = s * HASH_MUL;
    s = s ^ (s >> 16u);
    s = s * HASH_MUL;
    s = s ^ (s >> 16u);
    s = s * HASH_MUL;
    return s;
}
fn rand_f32(x: u32) -> f32 {
    return f32(hash_u32(x) & 0x00FFFFFFu) / f32(0x01000000u);
}

// ─── カーブ LUT サンプリング（particle_sim.wgsl と同一）───────
// LUT は [speed | rot_speed | color(HSVA) | scale(xyz) | random_color_0.. ] を
// 各 S=lut_samples 行で連結した vec4 配列。base はチャンネル先頭行のオフセット。
fn sample_lut(base: u32, t: f32) -> vec4<f32> {
    let s = params.lut_samples;
    let fidx = clamp(t, 0.0, 1.0) * f32(s - 1u);
    let i0 = u32(floor(fidx));
    let i1 = min(i0 + 1u, s - 1u);
    let f = fract(fidx);
    return mix(lut[base + i0], lut[base + i1], f);
}

// ─── HSV → RGB 変換（H/S/V とも 0..1 正規化）─────────────────
// 色相の正しい補間のため LUT には HSVA のまま格納し、ここで RGB 化する。
fn hsv2rgb(hsv: vec3<f32>) -> vec3<f32> {
    // H を [0,1) に折り返す（カーブ編集で 1 超の値もありうるため fract）。
    let h6 = fract(hsv.x) * 6.0;
    let c  = hsv.z * hsv.y;            // chroma
    let x  = c * (1.0 - abs(h6 % 2.0 - 1.0));
    let m  = hsv.z - c;
    var rgb: vec3<f32>;
    if h6 < 1.0 {
        rgb = vec3<f32>(c, x, 0.0);
    } else if h6 < 2.0 {
        rgb = vec3<f32>(x, c, 0.0);
    } else if h6 < 3.0 {
        rgb = vec3<f32>(0.0, c, x);
    } else if h6 < 4.0 {
        rgb = vec3<f32>(0.0, x, c);
    } else if h6 < 5.0 {
        rgb = vec3<f32>(x, 0.0, c);
    } else {
        rgb = vec3<f32>(c, 0.0, x);
    }
    return rgb + vec3<f32>(m);
}

// ─── 軸角回転（Rodrigues の回転公式）──────────────────────────
fn rotate_axis_angle(v: vec3<f32>, axis: vec3<f32>, angle: f32) -> vec3<f32> {
    let c = cos(angle);
    let s = sin(angle);
    return v * c + cross(axis, v) * s + axis * dot(axis, v) * (1.0 - c);
}

// ─── 頂点出力 ─────────────────────────────────────────────────
struct VsOut {
    @builtin(position)              clip:  vec4<f32>,
    @location(0)                    uv:    vec2<f32>,
    @location(1)                    color: vec4<f32>,
    // テクスチャ配列レイヤ（粒子ごとに一定＝flat 補間）。
    @location(2) @interpolate(flat) layer: u32,
};

// ─── 粒子の共通属性（色・スケール・中心・dead・レイヤ）──────────
struct ParticleAttr {
    dead:   bool,
    center: vec3<f32>,
    scale3: vec3<f32>,
    color:  vec4<f32>, // RGBA（HSV→RGB 済み）
    layer:  u32,       // テクスチャ配列レイヤ
};

/// 粒子 1 個の描画用属性を seed から決定的に計算する（vs_main / vs_pixel 共通）。
fn compute_attr(p: Particle) -> ParticleAttr {
    var a: ParticleAttr;
    a.dead = (p.lifetime <= 0.0) || (p.age >= p.lifetime);
    let life = max(p.lifetime, 1e-6);
    let t    = clamp(p.age / life, 0.0, 1.0);

    // 全体サイズ倍率 × scale カーブ（行 2S..3S、xyz）。
    let size_mult = mix(params.size_min, params.size_max, rand_f32(p.seed ^ SALT_SIZE));
    a.scale3 = sample_lut(params.lut_samples * 2u, t).xyz * size_mult;

    // 粒子中心のワールド座標（Local シムは行列変換）。
    var center = p.pos;
    if params.sim_space == 1u {
        center = (params.world_mat * vec4<f32>(p.pos, 1.0)).xyz;
    }
    a.center = center;

    // 色: 色カーブリスト（最低 1 本）から seed で 1 本選び、HSVA を RGB 化する。
    // 色カーブは LUT の行 (LUT_FIXED_CURVES + j)S..（固定 3 本の後ろに並ぶ）。
    let cc = max(params.color_count, 1u);
    let j  = hash_u32(p.seed ^ SALT_COLORPICK) % cc;
    let hsva = sample_lut(params.lut_samples * (LUT_FIXED_CURVES + j), t);
    a.color = vec4<f32>(hsv2rgb(hsva.xyz), hsva.w);

    // テクスチャ配列レイヤ選択（seed で 1 枚。tex_layer_count は最低 1 で割る）。
    let lc = max(params.tex_layer_count, 1u);
    a.layer = hash_u32(p.seed ^ SALT_TEXLAYER) % lc;
    return a;
}

// ─── メッシュ形状の頂点シェーダ（Sphere/Box/Plane/Model）──────
// 粒子ごとのランダム軸まわりに rot_angle の軸角回転（Rodrigues）を適用し、
// scale_curve×サイズ倍率でスケールして粒子中心へ平行移動する。
@vertex
fn vs_main(
    @location(0)             vpos: vec3<f32>, // 単位メッシュのローカル頂点位置
    @builtin(instance_index) ii:   u32,
) -> VsOut {
    var out: VsOut;
    let p = particles[ii];
    let a = compute_attr(p);

    // 粒子ごとのランダム回転軸（seed から球面一様に決定的生成）。
    let cos_t = rand_f32(p.seed ^ SALT_AXIS_COS) * 2.0 - 1.0;
    let sin_t = sqrt(max(0.0, 1.0 - cos_t * cos_t));
    let phi   = rand_f32(p.seed ^ SALT_AXIS_PHI) * 2.0 * PI;
    let axis  = vec3<f32>(sin_t * cos(phi), sin_t * sin(phi), cos_t);
    // スケール → 軸角回転 → 平行移動。
    let v = rotate_axis_angle(vpos * a.scale3, axis, p.rot_angle);

    var wp: vec3<f32>;
    if params.sim_space == 1u {
        // Local シム: 頂点オフセットもローカル空間で足してから行列変換する。
        wp = (params.world_mat * vec4<f32>(p.pos + v, 1.0)).xyz;
    } else {
        wp = a.center + v;
    }
    // dead はオフセットを潰して中心へ縮退させる（面積 0＝ラスタライズされない）。
    wp = select(wp, a.center, a.dead);

    out.clip  = u_camera.view_proj * vec4<f32>(wp, 1.0);
    // UV: ローカル xy を 0..1 へ（Plane は正確・他形状は近似。v1 制限）。
    out.uv    = clamp(vpos.xy + vec2<f32>(0.5), vec2<f32>(0.0), vec2<f32>(1.0));
    out.color = select(a.color, vec4<f32>(a.color.rgb, 0.0), a.dead);
    out.layer = a.layer;
    return out;
}

// ─── Pixel（PointList）頂点シェーダ ───────────────────────────
// ポリゴンを生成せず、粒子中心 1 点を 1 ピクセルとしてラスタライズする最軽量描画。
// 頂点バッファは不要（instance_index のみ）。サイズ・回転は持たない（1px 固定）。
@vertex
fn vs_pixel(@builtin(instance_index) ii: u32) -> VsOut {
    var out: VsOut;
    let p = particles[ii];
    let a = compute_attr(p);
    // dead は NDC 範囲外へ飛ばして描画対象から外す（点は縮退で消せないため）。
    var clip = u_camera.view_proj * vec4<f32>(a.center, 1.0);
    if a.dead {
        clip = vec4<f32>(2.0, 2.0, 2.0, 1.0);
    }
    out.clip  = clip;
    out.uv    = vec2<f32>(0.5, 0.5);
    out.color = a.color;
    out.layer = a.layer;
    return out;
}

// ─── フラグメントシェーダ（メッシュ・Pixel 共通）──────────────
@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    var col = in.color;
    if params.use_texture == 1u {
        // テクスチャ配列から粒子ごとのレイヤをサンプル（mip なし＝level 0 固定。
        // 点プリミティブでも微分に依存しないよう SampleLevel を使う）。
        col = col * textureSampleLevel(t_particle, s_particle, in.uv, i32(in.layer), 0.0);
    }
    // premultiplied alpha を出力（各ブレンドステートは pipeline.rs で選択）。
    return vec4<f32>(col.rgb * col.a, col.a);
}
