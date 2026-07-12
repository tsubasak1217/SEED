// ============================================================
// particle_sim.wgsl  —  GPU パーティクル シミュレーション コンピュートシェーダ（拡張版）
//
// 1 エミッタあたり 1 回（プリウォーム時は複数回）ディスパッチする。スレッド
// （slot index i）はパーティクルプールの 1 スロットを担当し、以下のいずれかを行う:
//   - リングカーソル区間 [ring_start, ring_start+emit_count)（mod max）に入って
//     いれば「再スポーン」（過剰放出時は生存粒子を上書き＝標準的なリング挙動）。
//   - それ以外は通常更新（射出速度カーブ変調＋重力／空気抵抗の蓄積速度による積分・
//     回転角の加算・加齢）。dead 粒子は放置。
//
// atomic は一切使わない（CPU が spawn_cursor と emit_count を uniform で渡す）。
// dead 判定: age >= lifetime もしくは lifetime <= 0（zeroed バッファ＝全 dead）。
//
// 【速度モデル（設計）】
//   射出速度 = emit_dir * base_speed * speed_curve(t)   … 毎フレーム カーブで変調
//   蓄積速度 vel                                        … 重力／空気抵抗のみを積分
//   位置更新 pos += 射出成分の変位 + 蓄積速度の変位
//   → 射出成分はカーブでフレームごとに変わり、重力／抵抗は別の蓄積速度に持つ。
//
// 【フレームレート非依存（重要）】
//   時間発展する量（位置・速度・回転角・寿命）は「すべて dt を経由」し、かつ
//   dt の刻み方に依らず同じ軌道になる形で積分する:
//     - 重力＋空気抵抗: dv/dt = g - k*v の【閉形式（指数減衰）】で厳密に 1 ステップ進める。
//       旧実装の v *= (1 - k*dt) は 1 次近似で、終端速度が dt に依存し（＝FPS で速度が
//       変わる）、k*dt >= 1 では速度が 0 に潰れていた。
//     - カーブ変調される量（射出速度・回転速度）: 区間【中点】でカーブを評価する
//       （中点則＝2 次精度）。左端評価は 1 次精度で dt に比例した誤差が残る。
//     - 位置の重力寄与: 等加速度運動の厳密解 (v*dt + 0.5*g*dt^2) を使う。
//       半陰的オイラー（pos += v_new*dt）は g*T*dt/2 の dt 依存誤差を残す。
//
// Group 0:
//   0 = particles (array<Particle>, storage read_write)
//   1 = params    (EmitterParams,   uniform)
//   2 = lut       (array<vec4<f32>>, storage read)  … カーブ LUT（連結）
//
// ※ Particle / EmitterParams / LUT レイアウトは Rust 側 particle_system.rs の
//    repr(C) 構造体および particle_draw.wgsl と厳密に一致させること
//    （不一致は静かに描画バグを生む。Rust 側 layout_tests で固定）。
// ============================================================

// ─── 定数 ─────────────────────────────────────────────────────
const PI: f32 = 3.14159265359;

// シミュレーション空間コード（ParticleSimSpace::to_code と一致）。
const SIM_SPACE_WORLD: u32 = 0u;
const SIM_SPACE_LOCAL: u32 = 1u;

// スポーン体積コード（SpawnVolume::to_code と一致）。
const SPAWN_POINT:  u32 = 0u;
const SPAWN_BOX:    u32 = 1u;
const SPAWN_SPHERE: u32 = 2u;

// 乱数ソルト（属性ごとに決定的な別系列を得るための名前付き定数）。
const SALT_LIFETIME: u32 = 0x9E3779B9u; // 寿命
const SALT_SPEED:    u32 = 0x85EBCA6Bu; // 初速
const SALT_AZIMUTH:  u32 = 0xC2B2AE35u; // 円錐方位角
const SALT_CONE:     u32 = 0x27D4EB2Fu; // 円錐仰角（cosθ）
const SALT_ROT:      u32 = 0x94D049BBu; // 基準回転速度（rot_speed_range 内の乱数）
const SALT_BOXX:     u32 = 0xD1B54A32u; // スポーン Box X
const SALT_BOXY:     u32 = 0x9FB21C65u; // スポーン Box Y
const SALT_BOXZ:     u32 = 0xCA5A8267u; // スポーン Box Z
const SALT_SPH_R:    u32 = 0x6C8E9CF5u; // スポーン Sphere 半径係数
const SALT_INITROT:  u32 = 0x45D9F3B3u; // 初期回転角（initial_rotation_range 内の乱数）
// 混合ハッシュ用の奇数乗数（PCG/wanghash 系の定数）。
const HASH_MUL: u32 = 2654435769u;
const HASH_XOR: u32 = 2747636419u;

/// 空気抵抗係数 k がこの値以下なら「抵抗なし」とみなすしきい値。
/// 指数減衰の閉形式は v∞ = g/k を含むため、k→0 で 0 除算になる。
/// この境界より下では等加速度運動の厳密解（こちらも dt 非依存）へ分岐する。
const DRAG_EPSILON: f32 = 1e-4;

/// 区間中点の係数（中点則でカーブを評価するときの dt 係数）。
const MIDPOINT_RATIO: f32 = 0.5;

// ─── パーティクル 1 個（std430 storage, stride 64）─────────────
struct Particle {
    pos:        vec3<f32>, // 0   ワールド or ローカル位置（sim_space による）
    age:        f32,       // 12  経過秒
    vel:        vec3<f32>, // 16  蓄積速度（重力／抵抗のみ。スポーン時 0）
    lifetime:   f32,       // 28  寿命秒（<=0 は dead）
    emit_dir:   vec3<f32>, // 32  正規化射出方向（World or Local, sim_space による）
    base_speed: f32,       // 44  基準初速（速度カーブ変調前の速さ）
    seed:       u32,       // 48  スポーン時に確定した乱数シード（描画が再現に使う）
    rot_angle:  f32,       // 52  蓄積回転角（ラジアン）
    _pad0:      u32,       // 56
    _pad1:      u32,       // 60
};

// ─── エミッタパラメータ（uniform, 192 バイト）─────────────────
struct EmitterParams {
    world_mat:           mat4x4<f32>, // 0    エミッタのワールド行列（列優先）
    dt:                  f32,         // 64   このステップの経過秒
    emit_count:          u32,         // 68   今フレームの放出個数
    ring_start:          u32,         // 72   リングカーソル開始スロット
    max_particles:       u32,         // 76   プール容量
    frame_nonce:         u32,         // 80   フレーム固有ノンス（乱数系列の変化用）
    drag:                f32,         // 84   空気抵抗係数
    spread_rad:          f32,         // 88   放出円錐の半頂角（ラジアン）
    shape_mode:          u32,         // 92   0=billboard / 1=mesh（描画用）
    direction_local:     vec3<f32>,   // 96   ローカル放出方向
    speed_min:           f32,         // 108  初速 min
    speed_max:           f32,         // 112  初速 max
    lifetime_min:        f32,         // 116  寿命 min
    lifetime_max:        f32,         // 120  寿命 max
    rot_speed_min:       f32,         // 124  回転速度 min（rad/s）
    gravity:             vec3<f32>,   // 128  重力加速度
    rot_speed_max:       f32,         // 140  回転速度 max（rad/s）
    spawn_box:           vec3<f32>,   // 144  スポーン Box 半径（half extents）
    spawn_sphere_radius: f32,         // 156  スポーン Sphere 半径
    size_min:            f32,         // 160  全体サイズ倍率 min（描画用）
    size_max:            f32,         // 164  全体サイズ倍率 max（描画用）
    spawn_volume:        u32,         // 168  0=Point / 1=Box / 2=Sphere
    sim_space:           u32,         // 172  0=World / 1=Local
    use_texture:         u32,         // 176  1=テクスチャ / 0=プロシージャル円（描画用）
    lut_samples:         u32,         // 180  カーブ LUT のサンプル数（CURVE_LUT_SAMPLES）
    color_count:         u32,         // 184  色カーブ本数（最低 1。描画用）
    initial_rot_min:     f32,         // 188  初期回転角 min（ラジアン）
    initial_rot_max:     f32,         // 192  初期回転角 max（ラジアン）
    tex_layer_count:     u32,         // 196  テクスチャ配列レイヤ数（描画用）
    _pad0:               u32,         // 200
    _pad1:               u32,         // 204
};

@group(0) @binding(0) var<storage, read_write> particles: array<Particle>;
@group(0) @binding(1) var<uniform>             params:    EmitterParams;
@group(0) @binding(2) var<storage, read>       lut:       array<vec4<f32>>;

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

// ─── カーブ LUT サンプリング ──────────────────────────────────
//
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

// ─── 円錐方向サンプリング用の基底構築 ─────────────────────────

/// 単位ベクトル d を軸とする正規直交基底 (t, b, d) を返す。
fn make_basis(d: vec3<f32>, out_t: ptr<function, vec3<f32>>, out_b: ptr<function, vec3<f32>>) {
    // d が Y に近いと cross が退化するため参照軸を切り替える。
    let up_ref = select(vec3<f32>(0.0, 1.0, 0.0), vec3<f32>(1.0, 0.0, 0.0), abs(d.y) > 0.99);
    let t = normalize(cross(up_ref, d));
    *out_t = t;
    *out_b = cross(d, t);
}

// ─── スポーン位置サンプリング（ローカル空間・体積内一様）──────

/// spawn_volume に応じたローカル空間のスポーン位置を返す。
///   Point : 原点
///   Box   : 各軸 [-half, half] 一様
///   Sphere: 半径内一様（r = radius * u^(1/3)、方向は球面一様）
fn sample_spawn_pos(base: u32) -> vec3<f32> {
    if params.spawn_volume == SPAWN_BOX {
        let rx = rand_f32(base ^ SALT_BOXX) * 2.0 - 1.0;
        let ry = rand_f32(base ^ SALT_BOXY) * 2.0 - 1.0;
        let rz = rand_f32(base ^ SALT_BOXZ) * 2.0 - 1.0;
        return vec3<f32>(rx, ry, rz) * params.spawn_box;
    } else if params.spawn_volume == SPAWN_SPHERE {
        // 球面一様方向（cosθ 一様 + 方位角一様）。
        let cos_t = rand_f32(base ^ SALT_BOXX) * 2.0 - 1.0;
        let sin_t = sqrt(max(0.0, 1.0 - cos_t * cos_t));
        let phi   = rand_f32(base ^ SALT_BOXY) * 2.0 * PI;
        let dir   = vec3<f32>(sin_t * cos(phi), sin_t * sin(phi), cos_t);
        // 体積一様のため半径を u^(1/3) でスケール。
        let u = rand_f32(base ^ SALT_SPH_R);
        let r = params.spawn_sphere_radius * pow(u, 1.0 / 3.0);
        return dir * r;
    }
    // Point。
    return vec3<f32>(0.0);
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

        // ローカル空間の体積内スポーン位置。
        let local_pos = sample_spawn_pos(base);

        var p: Particle;
        p.seed       = base;
        p.age        = 0.0;
        p.lifetime   = lifetime;
        p.base_speed = speed;
        p.vel        = vec3<f32>(0.0);       // 蓄積速度は 0 から（重力／抵抗を積む）
        // 初期回転角: initial_rotation_range 内で乱数抽選（ラジアン。CPU で度→rad 済み）。
        p.rot_angle  = mix(params.initial_rot_min, params.initial_rot_max, rand_f32(base ^ SALT_INITROT));
        p._pad0      = 0u;
        p._pad1      = 0u;

        if params.sim_space == SIM_SPACE_WORLD {
            // ワールド空間シム: 位置・方向をエミッタ行列で変換して固定する。
            p.pos      = (params.world_mat * vec4<f32>(local_pos, 1.0)).xyz;
            let wdir   = (params.world_mat * vec4<f32>(cone_dir, 0.0)).xyz;
            p.emit_dir = normalize(wdir);
        } else {
            // ローカル空間シム: ローカルのまま保持し、描画時に行列変換する。
            p.pos      = local_pos;
            p.emit_dir = cone_dir;
        }

        particles[i] = p;
        return;
    }

    // ── 通常更新 ──────────────────────────────────────────────
    var p = particles[i];
    // dead（未生成 or 寿命切れ）は放置。age は既に lifetime 以上なので描画側で縮退する。
    if p.lifetime <= 0.0 || p.age >= p.lifetime { return; }

    // このステップの経過秒（全ての時間発展項はこの dt を経由する）。
    let dt = params.dt;

    // カーブ評価用の正規化寿命。区間 [age, age+dt] の【中点】で評価する（中点則）。
    // 左端（age）評価だと 1 ステップあたり O(dt) の誤差が出て、刻み幅（＝FPS）で
    // 軌道が変わってしまう。中点なら O(dt^2) となり実用上フレームレート非依存。
    let t_mid = clamp((p.age + dt * MIDPOINT_RATIO) / p.lifetime, 0.0, 1.0);

    // ── 射出成分の変位（カーブ変調された射出速度 × dt）──
    let speed_mult = sample_lut(0u, t_mid).x;
    let emit_disp  = p.emit_dir * (p.base_speed * speed_mult * dt);

    // ── 重力＋空気抵抗の解析積分（フレームレート非依存の閉形式）──
    //   支配方程式: dv/dt = g - k*v      （k = drag >= 0, g = gravity）
    //   閉形式    : v(t+dt) = v∞ + (v - v∞) * exp(-k*dt)         , v∞ = g / k
    //               Δx      = v∞*dt + (v - v∞) * (1 - exp(-k*dt)) / k
    //   これは任意の dt に対する厳密解なので、dt=1/60 を 2 回積んだ結果と
    //   dt=1/30 を 1 回積んだ結果が（丸め誤差を除き）完全に一致する。
    let k = max(params.drag, 0.0);
    var vel_disp: vec3<f32>;
    if k > DRAG_EPSILON {
        let decay = exp(-k * dt);            // 指数減衰係数
        let v_inf = params.gravity / k;      // 終端速度 v∞ = g/k
        let dv    = p.vel - v_inf;           // 終端速度からのずれ
        vel_disp  = v_inf * dt + dv * (1.0 - decay) / k;
        p.vel     = v_inf + dv * decay;
    } else {
        // 抵抗ほぼ 0: 上式は 0 除算になるため等加速度運動の厳密解へ分岐する。
        vel_disp = p.vel * dt + params.gravity * (0.5 * dt * dt);
        p.vel    = p.vel + params.gravity * dt;
    }

    // 位置更新: 射出成分の変位 ＋ 蓄積速度（重力／抵抗）の変位。
    p.pos = p.pos + emit_disp + vel_disp;

    // 回転角の更新: 基準回転速度（粒子ごと乱数）× 回転速度カーブ（中点評価）× dt。
    let base_rot = mix(params.rot_speed_min, params.rot_speed_max, rand_f32(p.seed ^ SALT_ROT));
    let rot_mult = sample_lut(params.lut_samples, t_mid).x; // rot_speed チャンネルは行 S..2S
    p.rot_angle = p.rot_angle + base_rot * rot_mult * dt;

    p.age = p.age + dt;
    particles[i] = p;
}
