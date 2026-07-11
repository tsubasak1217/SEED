// ============================================================
// particle_sim.wgsl  —  GPU パーティクル シミュレーション コンピュートシェーダ
//
// 1 エミッタあたり 1 回ディスパッチする。スレッド（slot index i）は
// パーティクルプールの 1 スロットを担当し、以下のいずれかを行う:
//   - リングカーソル区間 [ring_start, ring_start+emit_count)（mod max）に
//     入っていれば「再スポーン」（過剰放出時は生存粒子を上書き＝標準的な
//     リングバッファ挙動。CPU が emit_count を制御する）。
//   - それ以外は通常更新（重力・空気抵抗・積分・加齢）。dead 粒子は放置。
//
// atomic は一切使わない（CPU が spawn_cursor と emit_count を uniform で渡す）。
// dead 判定: age >= lifetime もしくは lifetime <= 0（zeroed バッファ＝全 dead）。
//
// Group 0:
//   0 = particles (array<Particle>, storage read_write)
//   1 = params    (EmitterParams,   uniform)
//
// ※ Particle / EmitterParams のレイアウトは Rust 側 particle_system.rs の
//    repr(C) 構造体および particle_draw.wgsl と厳密に一致させること
//    （不一致は静かに描画バグを生む。Rust 側 layout_tests で固定）。
// ============================================================

// ─── 定数 ─────────────────────────────────────────────────────
const PI: f32 = 3.14159265359;

// シミュレーション空間コード（ParticleSimSpace::to_code と一致）。
const SIM_SPACE_WORLD: u32 = 0u;
const SIM_SPACE_LOCAL: u32 = 1u;

// 乱数ソルト（属性ごとに決定的な別系列を得るための名前付き定数）。
// マジックナンバー禁止のため意味のある名前を与える。
const SALT_LIFETIME: u32 = 0x9E3779B9u; // 寿命
const SALT_SPEED:    u32 = 0x85EBCA6Bu; // 初速
const SALT_AZIMUTH:  u32 = 0xC2B2AE35u; // 円錐方位角
const SALT_CONE:     u32 = 0x27D4EB2Fu; // 円錐仰角（cosθ）
// 混合ハッシュ用の奇数乗数（PCG/wanghash 系の定数）。
const HASH_MUL: u32 = 2654435769u;
const HASH_XOR: u32 = 2747636419u;

// ─── パーティクル 1 個（std430 storage, stride 48）─────────────
struct Particle {
    pos:      vec3<f32>, // 0   ワールド or ローカル位置（sim_space による）
    age:      f32,       // 12  経過秒
    vel:      vec3<f32>, // 16  速度
    lifetime: f32,       // 28  寿命秒（<=0 は dead）
    seed:     u32,       // 32  スポーン時に確定した乱数シード（描画がサイズ再現に使う）
    _pad0:    u32,       // 36  16 バイト境界パディング
    _pad1:    u32,       // 40
    _pad2:    u32,       // 44
};

// ─── エミッタパラメータ（uniform, 192 バイト）─────────────────
struct EmitterParams {
    world_mat:       mat4x4<f32>, // 0    エミッタのワールド行列（列優先）
    dt:              f32,         // 64   このステップの経過秒
    emit_count:      u32,         // 68   今フレームの放出個数
    ring_start:      u32,         // 72   リングカーソル開始スロット
    max_particles:   u32,         // 76   プール容量
    frame_nonce:     u32,         // 80   フレーム固有ノンス（乱数系列の変化用）
    drag:            f32,         // 84   空気抵抗係数
    spread_rad:      f32,         // 88   放出円錐の半頂角（ラジアン）
    end_size_scale:  f32,         // 92   寿命末のサイズ倍率（描画用・sim では未使用）
    direction_local: vec3<f32>,   // 96   ローカル放出方向
    speed_min:       f32,         // 108  初速 min
    speed_max:       f32,         // 112  初速 max
    lifetime_min:    f32,         // 116  寿命 min
    lifetime_max:    f32,         // 120  寿命 max
    size_min:        f32,         // 124  開始サイズ min（描画用）
    gravity:         vec3<f32>,   // 128  重力加速度
    size_max:        f32,         // 140  開始サイズ max（描画用）
    start_color:     vec4<f32>,   // 144  開始色（描画用）
    end_color:       vec4<f32>,   // 160  終了色（描画用）
    sim_space:       u32,         // 176  0=World / 1=Local
    use_texture:     u32,         // 180  1=テクスチャ / 0=プロシージャル円（描画用）
    _pad0:           u32,         // 184
    _pad1:           u32,         // 188
};

@group(0) @binding(0) var<storage, read_write> particles: array<Particle>;
@group(0) @binding(1) var<uniform>             params:    EmitterParams;

// ─── ハッシュ乱数（決定的・PCG 風）─────────────────────────────

/// 32bit → 32bit 混合ハッシュ。
fn hash_u32(x: u32) -> u32 {
    var s = x ^ HASH_XOR;
    s = s * HASH_MUL;
    s = s ^ (s >> 16u);
    s = s * HASH_MUL;
    s = s ^ (s >> 16u);
    s = s * HASH_MUL;
    return s;
}

/// ハッシュ値から [0,1) の一様乱数を得る（上位ビットを避け 24bit 精度）。
fn rand_f32(x: u32) -> f32 {
    return f32(hash_u32(x) & 0x00FFFFFFu) / f32(0x01000000u);
}

// ─── 円錐方向サンプリング用の基底構築 ─────────────────────────

/// 単位ベクトル d を軸とする正規直交基底 (t, b, d) を返す。
fn make_basis(d: vec3<f32>, out_t: ptr<function, vec3<f32>>, out_b: ptr<function, vec3<f32>>) {
    // d が Y に近いと cross が退化するため参照軸を切り替える。
    let up_ref = select(vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(1.0, 0.0, 0.0), abs(d.y) > 0.99);
    let t = normalize(cross(up_ref, d));
    *out_t = t;
    *out_b = cross(d, t);
}

// ─── メイン ────────────────────────────────────────────────────

@compute @workgroup_size(64)
fn cs_main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let i = gid.x;
    // 境界ガード（プール容量を超えるスレッドは何もしない）。
    if i >= params.max_particles { return; }

    // ── リング区間判定（このスロットが今フレーム再スポーン対象か）──
    // rel = (i - ring_start) の mod max。rel < emit_count なら区間内。
    let rel = (i + params.max_particles - params.ring_start) % params.max_particles;
    let spawn = rel < params.emit_count;

    if spawn {
        // ── 再スポーン（過剰放出時は生存粒子を上書き＝標準リング挙動）──
        // スロットとフレームノンスから決定的シードを作る。
        let base = hash_u32(i ^ hash_u32(params.frame_nonce));

        let r_life = rand_f32(base ^ SALT_LIFETIME);
        let lifetime = mix(params.lifetime_min, params.lifetime_max, r_life);
        let r_speed = rand_f32(base ^ SALT_SPEED);
        let speed = mix(params.speed_min, params.speed_max, r_speed);

        // 円錐内一様サンプリング: cosθ を [cos(spread), 1] で一様に取る。
        let r_cone = rand_f32(base ^ SALT_CONE);
        let cos_theta = mix(cos(params.spread_rad), 1.0, r_cone);
        let sin_theta = sqrt(max(0.0, 1.0 - cos_theta * cos_theta));
        let phi = rand_f32(base ^ SALT_AZIMUTH) * 2.0 * PI;

        // ローカル放出方向 d を軸に円錐ベクトルを構築。
        let d = normalize(params.direction_local);
        var t: vec3<f32>;
        var b: vec3<f32>;
        make_basis(d, &t, &b);
        let cone_dir = normalize(
            d * cos_theta + (t * cos(phi) + b * sin(phi)) * sin_theta
        );

        var p: Particle;
        p.seed     = base;
        p.age      = 0.0;
        p.lifetime = lifetime;
        p._pad0    = 0u;
        p._pad1    = 0u;
        p._pad2    = 0u;

        if params.sim_space == SIM_SPACE_WORLD {
            // ワールド空間シム: 位置＝行列の平行移動、方向＝行列で回した円錐。
            p.pos = (params.world_mat * vec4<f32>(0.0, 0.0, 0.0, 1.0)).xyz;
            let world_dir = (params.world_mat * vec4<f32>(cone_dir, 0.0)).xyz;
            p.vel = normalize(world_dir) * speed;
        } else {
            // ローカル空間シム: 原点発生・ローカル方向。描画時に行列で変換する。
            p.pos = vec3<f32>(0.0);
            p.vel = cone_dir * speed;
        }

        particles[i] = p;
        return;
    }

    // ── 通常更新 ──────────────────────────────────────────────
    var p = particles[i];
    // dead（未生成 or 寿命切れ）は放置。age は既に lifetime 以上なので描画側で縮退する。
    if p.lifetime <= 0.0 || p.age >= p.lifetime { return; }

    // 速度積分（重力 → 空気抵抗 → 位置 → 加齢）。
    p.vel = p.vel + params.gravity * params.dt;
    p.vel = p.vel * max(0.0, 1.0 - params.drag * params.dt);
    p.pos = p.pos + p.vel * params.dt;
    p.age = p.age + params.dt;
    particles[i] = p;
}
