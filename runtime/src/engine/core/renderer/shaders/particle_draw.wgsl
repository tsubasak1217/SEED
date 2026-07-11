// ============================================================
// particle_draw.wgsl  —  GPU パーティクル描画シェーダ（ビルボード頂点プリング）
//
// 頂点バッファは持たない。@builtin(vertex_index)（0..6）でクアッドの 6 頂点を
// 生成し、@builtin(instance_index) でストレージバッファから粒子を読む
// （vertex pulling）。粒子はカメラのビュー行列でビルボード化する。
//
// dead（age>=lifetime || lifetime<=0）はサイズ 0 に縮退させて不可視にする。
// サイズ・色は age/lifetime の補間。出力は HDR（トーンマップしない）で、
// フラグメントは premultiplied alpha を返す:
//   - Additive パイプライン: src=One, dst=One          → rgb*a を加算
//   - Alpha   パイプライン: src=One, dst=OneMinusSrcA  → premultiplied over
//
// Group 0: camera（既存 CameraUniform BGL を流用。頂点でのみ使用）
// Group 1: particles（storage read）+ params（uniform）
// Group 2: texture + sampler（未指定時は白 1x1＋プロシージャル円）
//
// ※ Particle / EmitterParams のレイアウトは particle_sim.wgsl・Rust 側
//    particle_system.rs の repr(C) と厳密一致させること。
// ============================================================

// ─── 定数 ─────────────────────────────────────────────────────
const PI: f32 = 3.14159265359;
// プロシージャル円のエッジのソフトネス幅（UV 距離の割合）。
const CIRCLE_EDGE: f32 = 0.08;
// サイズ乱数のソルト（particle_sim.wgsl の系列と独立。seed から再現する）。
const SALT_SIZE: u32 = 0x165667B1u;
// 混合ハッシュ乗数／XOR（particle_sim.wgsl と同一である必要はないが決定的であればよい）。
const HASH_MUL: u32 = 2654435769u;
const HASH_XOR: u32 = 2747636419u;

// ─── Group 0: カメラ（shader_common.wgsl の CameraUniform と同一レイアウト）──
struct CameraUniform {
    view_proj: mat4x4<f32>,
    view:      mat4x4<f32>,
    position:  vec3<f32>,
    _pad:      f32,
};
@group(0) @binding(0) var<uniform> u_camera: CameraUniform;

// ─── Group 1: 粒子ストレージ＋エミッタパラメータ ──────────────
struct Particle {
    pos:      vec3<f32>,
    age:      f32,
    vel:      vec3<f32>,
    lifetime: f32,
    seed:     u32,
    _pad0:    u32,
    _pad1:    u32,
    _pad2:    u32,
};

struct EmitterParams {
    world_mat:       mat4x4<f32>,
    dt:              f32,
    emit_count:      u32,
    ring_start:      u32,
    max_particles:   u32,
    frame_nonce:     u32,
    drag:            f32,
    spread_rad:      f32,
    end_size_scale:  f32,
    direction_local: vec3<f32>,
    speed_min:       f32,
    speed_max:       f32,
    lifetime_min:    f32,
    lifetime_max:    f32,
    size_min:        f32,
    gravity:         vec3<f32>,
    size_max:        f32,
    start_color:     vec4<f32>,
    end_color:       vec4<f32>,
    sim_space:       u32,
    use_texture:     u32,
    _pad0:           u32,
    _pad1:           u32,
};

@group(1) @binding(0) var<storage, read> particles: array<Particle>;
@group(1) @binding(1) var<uniform>       params:    EmitterParams;

// ─── Group 2: テクスチャ＋サンプラー ──────────────────────────
@group(2) @binding(0) var t_particle: texture_2d<f32>;
@group(2) @binding(1) var s_particle: sampler;

// ─── ハッシュ（サイズ乱数の再現用。seed から決定的にサイズを復元）──
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

// ─── 頂点出力 ─────────────────────────────────────────────────
struct VsOut {
    @builtin(position) clip:  vec4<f32>,
    @location(0)       uv:    vec2<f32>,
    @location(1)       color: vec4<f32>,
};

// ─── 頂点シェーダ（ビルボードクアッド）────────────────────────
@vertex
fn vs_main(
    @builtin(vertex_index)   vi: u32,
    @builtin(instance_index) ii: u32,
) -> VsOut {
    var out: VsOut;

    // クアッドの 6 頂点（2 三角形）をローカル [-1,1] コーナーで生成する。
    var corners = array<vec2<f32>, 6>(
        vec2<f32>(-1.0, -1.0),
        vec2<f32>( 1.0, -1.0),
        vec2<f32>( 1.0,  1.0),
        vec2<f32>(-1.0, -1.0),
        vec2<f32>( 1.0,  1.0),
        vec2<f32>(-1.0,  1.0),
    );
    let c  = corners[vi];
    out.uv = c * 0.5 + vec2<f32>(0.5, 0.5);

    let p = particles[ii];
    // dead 判定（未生成／寿命切れ）。サイズ 0 縮退で不可視にする。
    let dead = (p.lifetime <= 0.0) || (p.age >= p.lifetime);
    let life = max(p.lifetime, 1e-6);
    let t    = clamp(p.age / life, 0.0, 1.0);

    // サイズ: seed からサイズ乱数を再現し、開始→終了へ補間。
    let size_rand  = rand_f32(p.seed ^ SALT_SIZE);
    let start_size = mix(params.size_min, params.size_max, size_rand);
    let size       = mix(start_size, start_size * params.end_size_scale, t);

    // 粒子中心のワールド座標。Local シムは描画時に行列変換する。
    var world_pos = p.pos;
    if params.sim_space == 1u {
        world_pos = (params.world_mat * vec4<f32>(p.pos, 1.0)).xyz;
    }

    // ビルボード基底: ビュー行列の行 0/1（列優先なので [col][row]）＝カメラ右／上。
    let right = vec3<f32>(u_camera.view[0][0], u_camera.view[1][0], u_camera.view[2][0]);
    let up    = vec3<f32>(u_camera.view[0][1], u_camera.view[1][1], u_camera.view[2][1]);

    // dead はオフセット 0 でクアッドを 1 点に潰す（面積 0＝ラスタライズされない）。
    let half   = size * 0.5;
    let offset = select((right * c.x + up * c.y) * half, vec3<f32>(0.0), dead);
    let wp     = world_pos + offset;

    out.clip  = u_camera.view_proj * vec4<f32>(wp, 1.0);
    // 色: 開始→終了へ補間。dead はアルファ 0。
    let col   = mix(params.start_color, params.end_color, t);
    out.color = select(col, vec4<f32>(col.rgb, 0.0), dead);
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
        let d = distance(in.uv, vec2<f32>(0.5, 0.5)) * 2.0; // 0=中心, 1=外接円
        let a = 1.0 - smoothstep(1.0 - CIRCLE_EDGE, 1.0, d);
        col   = vec4<f32>(col.rgb, col.a * a);
    }

    // premultiplied alpha を出力（Additive=One/One, Alpha=One/OneMinusSrcA 両対応）。
    return vec4<f32>(col.rgb * col.a, col.a);
}
