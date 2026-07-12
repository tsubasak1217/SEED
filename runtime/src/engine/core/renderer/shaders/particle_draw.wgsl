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
// プロシージャル円のエッジのソフトネス幅（UV 距離の割合）。
const CIRCLE_EDGE: f32 = 0.08;
// 乱数ソルト（particle_sim.wgsl の系列と独立。seed から決定的に再現する）。
const SALT_SIZE:      u32 = 0x165667B1u; // 全体サイズ倍率
const SALT_COLORPICK: u32 = 0xB5297A4Du; // ランダム色カーブ選択
const SALT_AXIS_COS:  u32 = 0x68E31DA4u; // 回転軸の cosθ
const SALT_AXIS_PHI:  u32 = 0x1B56C4E9u; // 回転軸の方位角
// 混合ハッシュ乗数／XOR（sim と同一の系。決定的であればよい）。
const HASH_MUL: u32 = 2654435769u;
const HASH_XOR: u32 = 2747636419u;

// 形状モード（Rust 側 shape_mode と一致）。
const SHAPE_BILLBOARD: u32 = 0u;

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
    random_color_count:  u32,         // 184  ランダム色カーブ本数
    _pad0:               u32,         // 188
};

@group(1) @binding(0) var<storage, read> particles: array<Particle>;
@group(1) @binding(1) var<uniform>       params:    EmitterParams;
@group(1) @binding(2) var<storage, read> lut:       array<vec4<f32>>;

// ─── Group 2: テクスチャ＋サンプラー ──────────────────────────
@group(2) @binding(0) var t_particle: texture_2d<f32>;
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
    @builtin(position) clip:  vec4<f32>,
    @location(0)       uv:    vec2<f32>,
    @location(1)       color: vec4<f32>,
};

// ─── 頂点シェーダ ─────────────────────────────────────────────
@vertex
fn vs_main(
    @location(0)             vpos: vec3<f32>, // 単位メッシュのローカル頂点位置
    @builtin(instance_index) ii:   u32,
) -> VsOut {
    var out: VsOut;

    let p = particles[ii];
    // dead 判定（未生成／寿命切れ）。サイズ 0 縮退で不可視にする。
    let dead = (p.lifetime <= 0.0) || (p.age >= p.lifetime);
    let life = max(p.lifetime, 1e-6);
    let t    = clamp(p.age / life, 0.0, 1.0);

    // 全体サイズ倍率: seed から size_range 乱数を再現し、scale_curve(t) を掛ける。
    // scale チャンネルは LUT の行 3S..4S（xyz）。
    let size_mult = mix(params.size_min, params.size_max, rand_f32(p.seed ^ SALT_SIZE));
    let scale3    = sample_lut(params.lut_samples * 3u, t).xyz * size_mult;

    // 粒子中心のワールド座標。Local シムは描画時に行列変換する。
    var center = p.pos;
    if params.sim_space == 1u {
        center = (params.world_mat * vec4<f32>(p.pos, 1.0)).xyz;
    }

    var wp: vec3<f32>;
    if params.shape_mode == SHAPE_BILLBOARD {
        // ── ビルボード（Point）──
        // ビュー行列の行 0/1（列優先なので [col][row]）＝カメラ右／上。
        let right = vec3<f32>(u_camera.view[0][0], u_camera.view[1][0], u_camera.view[2][0]);
        let up    = vec3<f32>(u_camera.view[0][1], u_camera.view[1][1], u_camera.view[2][1]);
        // 面内回転（rot_angle）をローカル xy に適用してからカメラ基底で展開する。
        // UV は回転前のコーナーから得る＝テクスチャはクアッドと一緒に回転して見える。
        let ca = cos(p.rot_angle);
        let sa = sin(p.rot_angle);
        let rx = vpos.x * ca - vpos.y * sa;
        let ry = vpos.x * sa + vpos.y * ca;
        // ビルボードのサイズは scale の x 成分を全体サイズとして使う
        // （Point 形状は等方サイズ。y/z チャンネルは未使用）。
        wp = center + (right * rx + up * ry) * scale3.x;
    } else {
        // ── メッシュ形状（Sphere/Box/Plane/Model）──
        // 粒子ごとのランダム回転軸（seed から球面一様に決定的生成）。
        let cos_t = rand_f32(p.seed ^ SALT_AXIS_COS) * 2.0 - 1.0;
        let sin_t = sqrt(max(0.0, 1.0 - cos_t * cos_t));
        let phi   = rand_f32(p.seed ^ SALT_AXIS_PHI) * 2.0 * PI;
        let axis  = vec3<f32>(sin_t * cos(phi), sin_t * sin(phi), cos_t);
        // スケール → 軸角回転 → 平行移動。
        let v = rotate_axis_angle(vpos * scale3, axis, p.rot_angle);
        if params.sim_space == 1u {
            // Local シム: 頂点オフセットもローカル空間で足してから行列変換する。
            wp = (params.world_mat * vec4<f32>(p.pos + v, 1.0)).xyz;
        } else {
            wp = center + v;
        }
    }

    // dead はオフセットを潰して 1 点に縮退させる（面積 0＝ラスタライズされない）。
    wp = select(wp, center, dead);

    // UV: ビルボードは回転前コーナー（±0.5 → 0..1）、メッシュは中心固定（v1 制限）。
    if params.shape_mode == SHAPE_BILLBOARD {
        out.uv = vpos.xy + vec2<f32>(0.5, 0.5);
    } else {
        out.uv = vec2<f32>(0.5, 0.5);
    }

    out.clip = u_camera.view_proj * vec4<f32>(wp, 1.0);

    // ── 色: HSVA カーブを t でサンプルし RGB 化する ──
    // random_color_count>0 なら seed ハッシュでランダム色カーブ（行 (4+j)S..）を選ぶ。
    var color_base = params.lut_samples * 2u; // 既定は color チャンネル（行 2S..3S）
    if params.random_color_count > 0u {
        let j = hash_u32(p.seed ^ SALT_COLORPICK) % params.random_color_count;
        color_base = params.lut_samples * (4u + j);
    }
    let hsva = sample_lut(color_base, t);
    let rgb  = hsv2rgb(hsva.xyz);
    // dead はアルファ 0（縮退と併用の保険）。
    out.color = select(vec4<f32>(rgb, hsva.w), vec4<f32>(rgb, 0.0), dead);
    return out;
}

// ─── フラグメントシェーダ ─────────────────────────────────────
@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    var col = in.color;

    if params.use_texture == 1u {
        // テクスチャ乗算（HDR・トーンマップなし）。
        let tex = textureSample(t_particle, s_particle, in.uv);
        col = col * tex;
    } else {
        // プロシージャル円: UV 中心からの距離場でソフトエッジのアルファを作る。
        // メッシュ形状は UV 中心固定（d=0）のため常にフルアルファになる。
        let d = distance(in.uv, vec2<f32>(0.5, 0.5)) * 2.0; // 0=中心, 1=外接円
        let a = 1.0 - smoothstep(1.0 - CIRCLE_EDGE, 1.0, d);
        col   = vec4<f32>(col.rgb, col.a * a);
    }

    // premultiplied alpha を出力（Additive=One/One, Alpha=One/OneMinusSrcA 両対応）。
    return vec4<f32>(col.rgb * col.a, col.a);
}
