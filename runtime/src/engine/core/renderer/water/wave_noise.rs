// ============================================================
//  water/wave_noise.rs — 波形ランダマイズ（Phase W6.4）の CPU ミラー
//
//  ## このファイルは何か
//  `shaders/water_height_field.wgsl` の**ノイズ節と解析波の芯だけ**を、
//  1 行ずつ対応する形で Rust へ写したものである。実行時には一切使われない
//  （実行時の実装は WGSL 側にしかない）。
//
//  ## なぜ実行されないコードを置くのか
//  W6.4 で高さ場に入った「ドメインワープ」と「高さノイズ」は、**解析微分**で
//  勾配を出している。ここが 1 項でも間違うと
//    ・法線（勾配）と W5.1 の頂点変位（高さ）が食い違い、波の山で陰影が裏返る
//    ・コースティクス（W5.3）が実在しない集光を描く
//  という、見た目では「なんとなく変」としか分からない壊れ方をする。
//  GPU 上の式は単体テストできないので、**同一式を CPU へ写して
//  「数値微分 ≒ 解析勾配」をユニットテストで固定する**
//  （`tessellation.rs` の格子ミラーと同じ方針。`#[cfg(test)]` で隠さないのも同じ理由で、
//   「シェーダー式の正典がどこにあるか」をソースから見えるようにするため）。
//
//  ## WGSL との同期
//  下の `mirrors_wgsl_constants` テストが、ここに書いた定数リテラルが
//  WGSL 側に**同じ文字列で存在すること**を検査する。片方だけ値を変えると落ちる。
// ============================================================

// ─── WGSL と共有する基本定数 ─────────────────────────────────

/// 円周（2π）。**WGSL `WATER_TAU` と一致必須。**
const WATER_TAU: f32 = 6.28318530718;
/// ゼロ除算回避の下限値。**WGSL `WATER_EPSILON` と一致必須。**
const WATER_EPSILON: f32 = 1.0e-4;

/// 重ね合わせるサイン波の層数。**WGSL `WAVE_LAYER_COUNT` と一致必須。**
const WAVE_LAYER_COUNT: u32 = 6;

/// 層ごとの進行方向（XZ 単位ベクトル）。**WGSL `WAVE_DIR_0..5` と一致必須。**
const WAVE_DIR: [[f32; 2]; WAVE_LAYER_COUNT as usize] = [
    [0.00000, 1.00000],
    [0.65606, 0.75471],
    [0.99255, -0.12187],
    [0.37461, -0.92718],
    [-0.71934, -0.69466],
    [-0.94552, 0.32557],
];
/// 層ごとの周波数倍率。**WGSL `WAVE_FREQ_MUL_0..5` と一致必須。**
const WAVE_FREQ_MUL: [f32; WAVE_LAYER_COUNT as usize] =
    [1.0, 1.618034, 2.414214, 3.302776, 4.732051, 6.854102];
/// 層ごとの振幅倍率。**WGSL `WAVE_AMP_MUL_0..5` と一致必須。**
const WAVE_AMP_MUL: [f32; WAVE_LAYER_COUNT as usize] =
    [0.8670, 0.5008, 0.3167, 0.2182, 0.1462, 0.0958];
/// 層ごとのスクロール速度倍率。**WGSL `WAVE_SPEED_MUL_0..5` と一致必須。**
const WAVE_SPEED_MUL: [f32; WAVE_LAYER_COUNT as usize] =
    [1.0, 1.31, 0.79, 1.63, 0.58, 1.13];
/// 層ごとの初期位相（ラジアン）。**WGSL `WAVE_PHASE_0..5` と一致必須。**
const WAVE_PHASE: [f32; WAVE_LAYER_COUNT as usize] = [0.0, 1.7, 3.4, 5.1, 2.2, 4.9];

// ─── ノイズの定数（WGSL の同名定数と一致必須）──────────────

/// fBm のオクターブ数。
const WATER_NOISE_OCTAVES: u32 = 2;
/// オクターブごとの周波数倍率。
const WATER_NOISE_LACUNARITY: f32 = 1.937;
/// オクターブごとの振幅倍率。
const WATER_NOISE_GAIN: f32 = 0.5;
/// オクターブごとの回転（37°）。
const WATER_NOISE_ROT_COS: f32 = 0.79864;
const WATER_NOISE_ROT_SIN: f32 = 0.60182;

/// 格子座標を u32 へ畳む奇数大素数。
const WATER_NOISE_HASH_PRIME_X: u32 = 1597334677;
const WATER_NOISE_HASH_PRIME_Y: u32 = 3812015801;
/// murmur3 finalizer（fmix32）の乗数とシフト量。
const WATER_NOISE_HASH_MIX_A: u32 = 2246822519;
const WATER_NOISE_HASH_MIX_B: u32 = 3266489917;
const WATER_NOISE_HASH_SHIFT_A: u32 = 16;
const WATER_NOISE_HASH_SHIFT_B: u32 = 13;
const WATER_NOISE_HASH_SHIFT_C: u32 = 16;
/// 1 ハッシュから 2 チャンネル取り出すためのビット分割。
const WATER_NOISE_CHANNEL_SHIFT: u32 = 16;
const WATER_NOISE_CHANNEL_MASK: u32 = 65535;
const WATER_NOISE_CHANNEL_INV: f32 = 1.0 / 65535.0;

/// 5 次補間関数 `w(t) = 6t⁵ − 15t⁴ + 10t³` の係数と、その導関数の係数。
const WATER_NOISE_FADE_C5: f32 = 6.0;
const WATER_NOISE_FADE_C4: f32 = -15.0;
const WATER_NOISE_FADE_C3: f32 = 10.0;
const WATER_NOISE_FADE_DERIV_C: f32 = 30.0;

/// ドメインワープの周波数比・変位比・流れる速さと向き。
const WATER_NOISE_WARP_FREQ_RATIO: f32 = 0.35;
const WATER_NOISE_WARP_AMP_RATIO: f32 = 0.06;
const WATER_NOISE_WARP_DRIFT_RATIO: f32 = 0.05;
const WATER_NOISE_WARP_DRIFT_DIR: [f32; 2] = [0.86603, 0.50000];

/// 高さへ直接足すノイズの周波数比・振幅比・流れる速さと向き・原点ずらし。
const WATER_NOISE_DETAIL_FREQ_RATIO: f32 = 2.5;
const WATER_NOISE_DETAIL_AMP_RATIO: f32 = 0.4;
const WATER_NOISE_DETAIL_DRIFT_RATIO: f32 = 0.09;
const WATER_NOISE_DETAIL_DRIFT_DIR: [f32; 2] = [-0.42262, 0.90631];
const WATER_NOISE_DETAIL_OFFSET: [f32; 2] = [137.3, 61.7];

/// レイヤジッタの上限とハッシュ種。
const WAVE_JITTER_ANGLE_MAX_RAD: f32 = 0.34907;
const WAVE_JITTER_PHASE_MAX_RAD: f32 = 3.14159265;
const WAVE_JITTER_SEED_DIR: u32 = 2654435769;
const WAVE_JITTER_SEED_PHASE: u32 = 1013904223;

// ─── ノイズ本体（WGSL と同一式）──────────────────────────────

/// 32bit 整数ハッシュ（murmur3 finalizer）。**WGSL `water_noise_hash_u32` と同一式。**
#[inline]
fn hash_u32(x: u32) -> u32 {
    let mut h = x;
    h ^= h >> WATER_NOISE_HASH_SHIFT_A;
    h = h.wrapping_mul(WATER_NOISE_HASH_MIX_A);
    h ^= h >> WATER_NOISE_HASH_SHIFT_B;
    h = h.wrapping_mul(WATER_NOISE_HASH_MIX_B);
    h ^= h >> WATER_NOISE_HASH_SHIFT_C;
    h
}

/// 整数種から −1..1 の擬似乱数を 1 個作る。**WGSL `water_noise_hash_signed` と同一式。**
#[inline]
fn hash_signed(seed: u32) -> f32 {
    let h = hash_u32(seed);
    ((h >> WATER_NOISE_CHANNEL_SHIFT) as f32 * WATER_NOISE_CHANNEL_INV) * 2.0 - 1.0
}

/// 格子セルから 2 チャンネルの −1..1 擬似乱数。**WGSL `water_noise_hash2` と同一式。**
#[inline]
fn hash2(cx: i32, cy: i32) -> [f32; 2] {
    let h = hash_u32(
        (cx as u32).wrapping_mul(WATER_NOISE_HASH_PRIME_X)
            ^ (cy as u32).wrapping_mul(WATER_NOISE_HASH_PRIME_Y),
    );
    let a = (h >> WATER_NOISE_CHANNEL_SHIFT) as f32 * WATER_NOISE_CHANNEL_INV;
    let b = (h & WATER_NOISE_CHANNEL_MASK) as f32 * WATER_NOISE_CHANNEL_INV;
    [a * 2.0 - 1.0, b * 2.0 - 1.0]
}

/// 5 次補間関数 `w(t)`。
#[inline]
fn fade(t: f32) -> f32 {
    t * t * t * (t * (t * WATER_NOISE_FADE_C5 + WATER_NOISE_FADE_C4) + WATER_NOISE_FADE_C3)
}

/// 5 次補間関数の導関数 `w'(t) = 30 t²(t−1)²`。
#[inline]
fn fade_deriv(t: f32) -> f32 {
    WATER_NOISE_FADE_DERIV_C * t * t * (t - 1.0) * (t - 1.0)
}

/// 2 チャンネル value noise の値と解析勾配。**WGSL `WaterNoise2` と同一。**
#[derive(Clone, Copy, Debug, Default)]
pub struct Noise2 {
    /// チャンネル x, y のノイズ値
    pub value: [f32; 2],
    /// チャンネル x の勾配 (∂/∂p.x, ∂/∂p.y)
    pub grad_x: [f32; 2],
    /// チャンネル y の勾配 (∂/∂p.x, ∂/∂p.y)
    pub grad_y: [f32; 2],
}

/// 2 チャンネル value noise。**WGSL `water_value_noise2` と同一式。**
#[allow(dead_code)] // WGSL の CPU ミラー（テストが仕様を固定。冒頭コメント参照）
pub fn value_noise2(p: [f32; 2]) -> Noise2 {
    let bx = p[0].floor();
    let by = p[1].floor();
    let fx = p[0] - bx;
    let fy = p[1] - by;
    let (cx, cy) = (bx as i32, by as i32);

    let a = hash2(cx, cy);
    let b = hash2(cx + 1, cy);
    let c = hash2(cx, cy + 1);
    let d = hash2(cx + 1, cy + 1);

    let ux = fade(fx);
    let uy = fade(fy);
    let dux = fade_deriv(fx);
    let duy = fade_deriv(fy);

    let mut n = Noise2::default();
    for ch in 0..2 {
        // 双一次補間の係数（v = k0 + k1·u.x + k2·u.y + k3·u.x·u.y）。
        let k0 = a[ch];
        let k1 = b[ch] - a[ch];
        let k2 = c[ch] - a[ch];
        let k3 = a[ch] - b[ch] - c[ch] + d[ch];
        let v = k0 + k1 * ux + k2 * uy + k3 * (ux * uy);
        let gx = (k1 + k3 * uy) * dux;
        let gy = (k2 + k3 * ux) * duy;
        n.value[ch] = v;
        if ch == 0 {
            n.grad_x = [gx, gy];
        } else {
            n.grad_y = [gx, gy];
        }
    }
    n
}

/// ヤコビアン転置の適用（`Jᵀ = s·[[c, sn], [−sn, c]]`）。
#[inline]
fn jacobian_t(g: [f32; 2], s: f32, c: f32, sn: f32) -> [f32; 2] {
    [s * (c * g[0] + sn * g[1]), s * (-sn * g[0] + c * g[1])]
}

/// 2 チャンネル value noise の fBm。**WGSL `water_noise_fbm2` と同一式。**
#[allow(dead_code)] // WGSL の CPU ミラー（テストが仕様を固定。冒頭コメント参照）
pub fn noise_fbm2(p: [f32; 2]) -> Noise2 {
    let mut q = p;
    let mut amp = 1.0f32;
    let mut norm = 0.0f32;
    let mut value = [0.0f32; 2];
    let mut grad_x = [0.0f32; 2];
    let mut grad_y = [0.0f32; 2];
    // ヤコビアン `∂q/∂p = L^k R^k` を「スケール ＋ 回転」として持ち回る。
    let (mut js, mut jc, mut jsn) = (1.0f32, 1.0f32, 0.0f32);

    for _ in 0..WATER_NOISE_OCTAVES {
        let n = value_noise2(q);
        for ch in 0..2 {
            value[ch] += amp * n.value[ch];
        }
        let gx = jacobian_t(n.grad_x, js, jc, jsn);
        let gy = jacobian_t(n.grad_y, js, jc, jsn);
        for i in 0..2 {
            grad_x[i] += amp * gx[i];
            grad_y[i] += amp * gy[i];
        }
        norm += amp;
        amp *= WATER_NOISE_GAIN;
        q = [
            (q[0] * WATER_NOISE_ROT_COS - q[1] * WATER_NOISE_ROT_SIN) * WATER_NOISE_LACUNARITY,
            (q[0] * WATER_NOISE_ROT_SIN + q[1] * WATER_NOISE_ROT_COS) * WATER_NOISE_LACUNARITY,
        ];
        js *= WATER_NOISE_LACUNARITY;
        let nc = jc * WATER_NOISE_ROT_COS - jsn * WATER_NOISE_ROT_SIN;
        let ns = jc * WATER_NOISE_ROT_SIN + jsn * WATER_NOISE_ROT_COS;
        jc = nc;
        jsn = ns;
    }

    let inv = 1.0 / norm.max(WATER_EPSILON);
    Noise2 {
        value: [value[0] * inv, value[1] * inv],
        grad_x: [grad_x[0] * inv, grad_x[1] * inv],
        grad_y: [grad_y[0] * inv, grad_y[1] * inv],
    }
}

/// レイヤ i のジッタ（x = 方向の回転角 / y = 位相の追加）。
/// **WGSL `water_wave_layer_jitter` と同一式。**
#[inline]
fn layer_jitter(i: u32, strength: f32) -> [f32; 2] {
    let pair = i >> 1;
    let sgn = 1.0 - 2.0 * (i & 1) as f32;
    let ang = sgn
        * hash_signed(pair.wrapping_add(WAVE_JITTER_SEED_DIR))
        * WAVE_JITTER_ANGLE_MAX_RAD;
    let ph = hash_signed(i.wrapping_add(WAVE_JITTER_SEED_PHASE)) * WAVE_JITTER_PHASE_MAX_RAD;
    [ang * strength, ph * strength]
}

/// ノイズによるサンプル位置の歪みと高さ加算。**WGSL `WaveNoiseSample` と同一。**
#[derive(Clone, Copy, Debug)]
pub struct NoiseSample {
    /// 歪めたあとのサンプル位置
    pub pos: [f32; 2],
    /// ワープのヤコビアン `∂pos/∂q` の第 1 行・第 2 行
    pub jac_row0: [f32; 2],
    pub jac_row1: [f32; 2],
    /// 高さへ直接足すノイズ（m）とその勾配
    pub height: f32,
    pub height_grad: [f32; 2],
}

/// ノイズ項をまとめて求める。**WGSL `water_wave_noise_sample` と同一式。**
#[allow(dead_code)] // WGSL の CPU ミラー（テストが仕様を固定。冒頭コメント参照）
pub fn wave_noise_sample(
    q: [f32; 2],
    amplitude: f32,
    scale: f32,
    speed: f32,
    t: f32,
    strength: f32,
    noise_scale: f32,
) -> NoiseSample {
    let mut s = NoiseSample {
        pos: q,
        jac_row0: [1.0, 0.0],
        jac_row1: [0.0, 1.0],
        height: 0.0,
        height_grad: [0.0, 0.0],
    };
    if strength <= 0.0 {
        return s;
    }

    // 波の位相速度（m/s 相当）。
    let phase_speed = speed / scale.max(WATER_EPSILON);

    // ① ドメインワープ。
    let warp_freq =
        (scale * noise_scale * WATER_NOISE_WARP_FREQ_RATIO).max(WATER_EPSILON);
    let warp_amp = strength * WATER_NOISE_WARP_AMP_RATIO * (WATER_TAU / warp_freq);
    let drift = t * phase_speed * WATER_NOISE_WARP_DRIFT_RATIO;
    let wn = noise_fbm2([
        (q[0] + WATER_NOISE_WARP_DRIFT_DIR[0] * drift) * warp_freq,
        (q[1] + WATER_NOISE_WARP_DRIFT_DIR[1] * drift) * warp_freq,
    ]);
    s.pos = [q[0] + warp_amp * wn.value[0], q[1] + warp_amp * wn.value[1]];
    let jk = warp_amp * warp_freq;
    s.jac_row0 = [1.0 + jk * wn.grad_x[0], jk * wn.grad_x[1]];
    s.jac_row1 = [jk * wn.grad_y[0], 1.0 + jk * wn.grad_y[1]];

    // ② 高さへ直接足す細かなノイズ。
    let det_freq =
        (scale * noise_scale * WATER_NOISE_DETAIL_FREQ_RATIO).max(WATER_EPSILON);
    let det_amp = strength * amplitude * WATER_NOISE_DETAIL_AMP_RATIO;
    let ddrift = t * phase_speed * WATER_NOISE_DETAIL_DRIFT_RATIO;
    let dn = noise_fbm2([
        (q[0] + WATER_NOISE_DETAIL_DRIFT_DIR[0] * ddrift) * det_freq
            + WATER_NOISE_DETAIL_OFFSET[0],
        (q[1] + WATER_NOISE_DETAIL_DRIFT_DIR[1] * ddrift) * det_freq
            + WATER_NOISE_DETAIL_OFFSET[1],
    ]);
    s.height = det_amp * dn.value[0];
    s.height_grad = [
        det_amp * det_freq * dn.grad_x[0],
        det_amp * det_freq * dn.grad_x[1],
    ];
    s
}

// ─── 解析波の芯（包絡・回転を除いた部分）────────────────────
//
// **低周波の空間包絡（`water_wave_envelope`）と全体回転は意図的に含めない。**
//   ・包絡は W6.1 の時点から「局所定数とみなして微分を無視する」近似であり、
//     高さと勾配が厳密には一致しない（これは既知・意図された近似）。
//     ここへ混ぜると、W6.4 のノイズ項の誤りを包絡の近似誤差が覆い隠してしまう。
//   ・全体回転は直交変換なので、高さ／勾配の対応を壊しようがない
//     （壊れるとしたら回転の向きであり、それは別のテストの担当）。
// したがってこのミラーは「**厳密一致しなければならない部分**」だけを写している。

/// ジッタ＋ドメインワープ込みの解析波の高さ（包絡・回転を除く）。
/// **WGSL `water_wave_height` の `(h + n.height)` までと同一式。**
#[allow(dead_code)] // WGSL の CPU ミラー（テストが仕様を固定。冒頭コメント参照）
pub fn wave_core_height(
    q: [f32; 2],
    amplitude: f32,
    scale: f32,
    speed: f32,
    t: f32,
    strength: f32,
    noise_scale: f32,
) -> f32 {
    let n = wave_noise_sample(q, amplitude, scale, speed, t, strength, noise_scale);
    let mut h = 0.0f32;
    for i in 0..WAVE_LAYER_COUNT {
        let k = i as usize;
        let j = layer_jitter(i, strength);
        let dir = rotate_dir(WAVE_DIR[k], j[0]);
        let freq = scale * WAVE_FREQ_MUL[k];
        let amp = amplitude * WAVE_AMP_MUL[k];
        let phase = (dir[0] * n.pos[0] + dir[1] * n.pos[1]) * freq
            + t * speed * WAVE_SPEED_MUL[k]
            + WAVE_PHASE[k]
            + j[1];
        h += amp * phase.sin();
    }
    h + n.height
}

/// 上の高さの解析勾配（包絡・回転を除く）。
/// **WGSL `water_wave_gradient` の `grad` を作るところまでと同一式。**
#[allow(dead_code)] // WGSL の CPU ミラー（テストが仕様を固定。冒頭コメント参照）
pub fn wave_core_gradient(
    q: [f32; 2],
    amplitude: f32,
    scale: f32,
    speed: f32,
    t: f32,
    strength: f32,
    noise_scale: f32,
) -> [f32; 2] {
    let n = wave_noise_sample(q, amplitude, scale, speed, t, strength, noise_scale);
    let mut g = [0.0f32; 2];
    for i in 0..WAVE_LAYER_COUNT {
        let k = i as usize;
        let j = layer_jitter(i, strength);
        let dir = rotate_dir(WAVE_DIR[k], j[0]);
        let freq = scale * WAVE_FREQ_MUL[k];
        let amp = amplitude * WAVE_AMP_MUL[k];
        let phase = (dir[0] * n.pos[0] + dir[1] * n.pos[1]) * freq
            + t * speed * WAVE_SPEED_MUL[k]
            + WAVE_PHASE[k]
            + j[1];
        let c = amp * freq * phase.cos();
        g[0] += dir[0] * c;
        g[1] += dir[1] * c;
    }
    // ワープの連鎖律: ∇_q = Jᵀ ∇_pos。
    [
        g[0] * n.jac_row0[0] + g[1] * n.jac_row1[0] + n.height_grad[0],
        g[0] * n.jac_row0[1] + g[1] * n.jac_row1[1] + n.height_grad[1],
    ]
}

/// 進行方向ベクトルを角度 `a` だけ回す。**WGSL `water_wave_rotate_dir` と同一式。**
#[inline]
fn rotate_dir(d: [f32; 2], a: f32) -> [f32; 2] {
    let (s, c) = a.sin_cos();
    [d[0] * c - d[1] * s, d[0] * s + d[1] * c]
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の代表的な波パラメータ（既定値相当）。
    const T_AMPLITUDE: f32 = 0.06;
    const T_SCALE: f32 = 0.12;
    const T_SPEED: f32 = 0.6;

    /// 数値微分（中央差分）で高さ場の勾配を求める。
    fn numeric_gradient(
        q: [f32; 2], t: f32, strength: f32, noise_scale: f32, step: f32,
    ) -> [f32; 2] {
        let h = |x: f32, z: f32| {
            wave_core_height([x, z], T_AMPLITUDE, T_SCALE, T_SPEED, t, strength, noise_scale)
        };
        [
            (h(q[0] + step, q[1]) - h(q[0] - step, q[1])) / (2.0 * step),
            (h(q[0], q[1] + step) - h(q[0], q[1] - step)) / (2.0 * step),
        ]
    }

    /// **本フェーズの中核テスト**: ノイズ有効時、解析勾配が数値微分と一致すること。
    ///
    /// ここが割れると「頂点は上がっているのに法線は平ら（またはその逆）」になり、
    /// 波の山で陰影が裏返る・コースティクスが実在しない集光を描く、という壊れ方をする。
    /// 中央差分は 2 次精度なので、刻み 1mm なら相対誤差 1e-3 以内に収まる。
    #[test]
    fn analytic_gradient_matches_numeric_with_noise() {
        // 差分の刻み（m）。
        //
        // **小さくしすぎてはいけない。** 高さ場は f32 で、遠方（座標 100m 超）では
        // 位相 `dot(dir,pos)·freq` が数百ラジアンになり、その丸め誤差（相対 1e-7）が
        // 高さへ 1e-7 m 程度の雑音として乗る。刻み 1mm だと 2 点間の高さ差自体が
        // 数 µm しかないため、この雑音が差の 10% を占めて**数値微分の側が壊れる**
        // （実測: 刻み 1mm・座標 [123,−456] で 13% の食い違い。解析側は正しい）。
        // 打ち切り誤差（∝ step²·|h'''|）は 2cm でも 1e-6 未満なので、
        // 桁落ちが消えるところまで刻みを大きく取るのが正しい。
        const STEP_M: f32 = 2.0e-2;
        // 許容する相対誤差（勾配の大きさに対する比）。中央差分の打ち切り誤差ぶん。
        const REL_TOL: f32 = 2.0e-2;
        // 勾配がほぼ 0 の点で相対比較すると発散するため、絶対誤差でも逃がす。
        const ABS_TOL: f32 = 1.0e-5;

        // 強さ・ノイズ細かさ・時刻・位置を広く振る（格子境界・負座標も含める）。
        for &strength in &[0.35f32, 1.0, 2.5] {
            for &noise_scale in &[0.5f32, 1.0, 4.0] {
                for &t in &[0.0f32, 3.7, 41.25] {
                    for &q in &[
                        [0.0f32, 0.0],
                        [1.0, -1.0],
                        [12.5, 7.25],
                        [-33.75, 88.5],
                        [123.0, -456.0],
                    ] {
                        let a = wave_core_gradient(
                            q, T_AMPLITUDE, T_SCALE, T_SPEED, t, strength, noise_scale);
                        let n = numeric_gradient(q, t, strength, noise_scale, STEP_M);
                        let mag = (a[0] * a[0] + a[1] * a[1]).sqrt().max(
                            (n[0] * n[0] + n[1] * n[1]).sqrt());
                        for axis in 0..2 {
                            let err = (a[axis] - n[axis]).abs();
                            assert!(
                                err <= ABS_TOL + REL_TOL * mag,
                                "解析勾配と数値微分が食い違う: 軸{axis} \
                                 解析={:e} 数値={:e} 誤差={:e} \
                                 (strength={strength} noise_scale={noise_scale} t={t} q={q:?})",
                                a[axis], n[axis], err,
                            );
                        }
                    }
                }
            }
        }
    }

    /// value noise 単体でも解析勾配が数値微分と一致すること。
    ///
    /// 上の統合テストが落ちたときに「ノイズ本体が悪いのか、ワープの連鎖律が悪いのか」を
    /// 切り分けるための、独立して成立する検査（Section 2 の「単体で検査できる部品」）。
    #[test]
    fn value_noise_gradient_matches_numeric() {
        const STEP: f32 = 1.0e-3;
        const TOL: f32 = 5.0e-3;
        for &p in &[
            [0.25f32, 0.5],
            [0.999, 0.001],   // 格子境界の直近
            [-1.75, 2.5],     // 負の座標（floor の符号）
            [10.3, -20.7],
        ] {
            let n = noise_fbm2(p);
            for ch in 0..2 {
                for axis in 0..2 {
                    let mut a = p;
                    let mut b = p;
                    a[axis] += STEP;
                    b[axis] -= STEP;
                    let num = (noise_fbm2(a).value[ch] - noise_fbm2(b).value[ch]) / (2.0 * STEP);
                    let ana = if ch == 0 { n.grad_x[axis] } else { n.grad_y[axis] };
                    assert!((ana - num).abs() <= TOL,
                        "fBm の勾配が数値微分と食い違う: ch{ch} 軸{axis} 解析={ana:e} 数値={num:e} p={p:?}");
                }
            }
        }
    }

    /// 強さ 0 なら**恒等**（＝W6.3 以前と完全に同じ高さ場）であること。
    #[test]
    fn zero_strength_is_identity() {
        let s = wave_noise_sample([12.0, -3.0], T_AMPLITUDE, T_SCALE, T_SPEED, 5.0, 0.0, 1.0);
        assert_eq!(s.pos, [12.0, -3.0], "ワープが掛かっている");
        assert_eq!(s.jac_row0, [1.0, 0.0], "ヤコビアンが単位行列でない");
        assert_eq!(s.jac_row1, [0.0, 1.0], "ヤコビアンが単位行列でない");
        assert_eq!(s.height, 0.0, "高さノイズが乗っている");
        assert_eq!(s.height_grad, [0.0, 0.0], "高さノイズの勾配が乗っている");
        for i in 0..WAVE_LAYER_COUNT {
            assert_eq!(layer_jitter(i, 0.0), [0.0, 0.0], "レイヤ {i} にジッタが乗っている");
        }
    }

    /// レイヤ方向ジッタの**総和が厳密に 0** であること（Phase W6.4）。
    ///
    /// これが崩れると「全層がまとめて少し回った」＝`wave_direction_deg` で指定した
    /// 進行方向と実際の波の向きが食い違う、という気づきにくい不具合になる。
    #[test]
    fn direction_jitter_sums_to_zero() {
        for &strength in &[0.35f32, 1.0, 3.0] {
            let sum: f32 = (0..WAVE_LAYER_COUNT).map(|i| layer_jitter(i, strength)[0]).sum();
            assert!(sum.abs() < 1.0e-6,
                "方向ジッタの総和が 0 でない（{sum}）＝波の平均進行方向がずれている");
            // ばらけてはいること（全部 0 なら「総和 0」は自明に通ってしまう）。
            let spread: f32 = (0..WAVE_LAYER_COUNT)
                .map(|i| layer_jitter(i, strength)[0].abs()).sum();
            assert!(spread > 0.05 * strength, "方向ジッタが実質効いていない（{spread}）");
        }
        assert_eq!(WAVE_LAYER_COUNT % 2, 0,
            "ペア符号反転で総和 0 を作っているので層数は偶数であること");
    }

    /// ドメインワープが**折り返さない**（ヤコビアンの行列式が正）こと。
    ///
    /// 行列式が 0 を跨ぐと、その線上で水面の模様が鏡像反転して皺のような筋になる。
    /// 高さ・勾配の整合は保たれる（微分としては正しい）ので、テストで見るしかない。
    /// 既定強さでは十分な余裕があることを固定しておく。
    #[test]
    fn warp_does_not_fold_at_default_strength() {
        const DEFAULT_STRENGTH: f32 = 0.35;
        let mut min_det = f32::INFINITY;
        // 広い範囲を粗く走査する（格子セルを何十個も跨ぐ間隔）。
        for ix in -40..40 {
            for iz in -40..40 {
                let q = [ix as f32 * 3.7, iz as f32 * 4.3];
                let s = wave_noise_sample(
                    q, T_AMPLITUDE, T_SCALE, T_SPEED, 9.0, DEFAULT_STRENGTH, 1.0);
                let det = s.jac_row0[0] * s.jac_row1[1] - s.jac_row0[1] * s.jac_row1[0];
                min_det = min_det.min(det);
            }
        }
        assert!(min_det > 0.5,
            "既定強さでワープのヤコビアン行列式が小さすぎる（最小 {min_det}）＝折り返しが近い");
    }

    /// ここに写した定数が WGSL 側と**同じリテラル**で存在すること。
    ///
    /// CPU ミラーの意味は「GPU で走る式と同一であること」に尽きるので、
    /// 片方だけ値を変えた瞬間に落ちるようにしておく。
    #[test]
    fn mirrors_wgsl_constants() {
        let src: String = include_str!("../shaders/water_height_field.wgsl")
            .chars().filter(|c| !c.is_whitespace()).collect();
        let mut need = |decl: &str| {
            let n: String = decl.chars().filter(|c| !c.is_whitespace()).collect();
            assert!(src.contains(&n), "WGSL に定数宣言が無い／値が違う: {decl}");
        };
        // ノイズ本体
        need("const WATER_NOISE_OCTAVES: u32 = 2u;");
        need("const WATER_NOISE_LACUNARITY: f32 = 1.937;");
        need("const WATER_NOISE_GAIN: f32 = 0.5;");
        need("const WATER_NOISE_ROT_COS: f32 = 0.79864;");
        need("const WATER_NOISE_ROT_SIN: f32 = 0.60182;");
        need("const WATER_NOISE_HASH_PRIME_X: u32 = 1597334677u;");
        need("const WATER_NOISE_HASH_PRIME_Y: u32 = 3812015801u;");
        need("const WATER_NOISE_HASH_MIX_A: u32 = 2246822519u;");
        need("const WATER_NOISE_HASH_MIX_B: u32 = 3266489917u;");
        need("const WATER_NOISE_HASH_SHIFT_A: u32 = 16u;");
        need("const WATER_NOISE_HASH_SHIFT_B: u32 = 13u;");
        need("const WATER_NOISE_HASH_SHIFT_C: u32 = 16u;");
        need("const WATER_NOISE_CHANNEL_SHIFT: u32 = 16u;");
        need("const WATER_NOISE_CHANNEL_MASK:  u32 = 65535u;");
        need("const WATER_NOISE_CHANNEL_INV:   f32 = 1.0 / 65535.0;");
        need("const WATER_NOISE_FADE_C5: f32 =   6.0;");
        need("const WATER_NOISE_FADE_C4: f32 = -15.0;");
        need("const WATER_NOISE_FADE_C3: f32 =  10.0;");
        need("const WATER_NOISE_FADE_DERIV_C: f32 = 30.0;");
        // ワープ・高さノイズ・ジッタ
        need("const WATER_NOISE_WARP_FREQ_RATIO: f32 = 0.35;");
        need("const WATER_NOISE_WARP_AMP_RATIO: f32 = 0.06;");
        need("const WATER_NOISE_WARP_DRIFT_RATIO: f32 = 0.05;");
        need("const WATER_NOISE_WARP_DRIFT_DIR: vec2<f32> = vec2<f32>(0.86603, 0.50000);");
        need("const WATER_NOISE_DETAIL_FREQ_RATIO: f32 = 2.5;");
        need("const WATER_NOISE_DETAIL_AMP_RATIO: f32 = 0.4;");
        need("const WATER_NOISE_DETAIL_DRIFT_RATIO: f32 = 0.09;");
        need("const WATER_NOISE_DETAIL_DRIFT_DIR: vec2<f32> = vec2<f32>(-0.42262, 0.90631);");
        need("const WATER_NOISE_DETAIL_OFFSET: vec2<f32> = vec2<f32>(137.3, 61.7);");
        need("const WAVE_JITTER_ANGLE_MAX_RAD: f32 = 0.34907;");
        need("const WAVE_JITTER_PHASE_MAX_RAD: f32 = 3.14159265;");
        need("const WAVE_JITTER_SEED_DIR:   u32 = 2654435769u;");
        need("const WAVE_JITTER_SEED_PHASE: u32 = 1013904223u;");
        // 解析波の層テーブル（ミラーの前提そのもの）
        need("const WAVE_LAYER_COUNT: u32 = 6u;");
        need("const WATER_TAU: f32 = 6.28318530718;");
        need("const WATER_EPSILON: f32 = 1.0e-4;");
        for i in 0..WAVE_LAYER_COUNT as usize {
            need(&format!("const WAVE_DIR_{i}: vec2<f32> = vec2<f32>({:.5}, {:.5});",
                WAVE_DIR[i][0], WAVE_DIR[i][1]));
            need(&format!("const WAVE_FREQ_MUL_{i}: f32 = {};", fmt(WAVE_FREQ_MUL[i])));
            need(&format!("const WAVE_AMP_MUL_{i}: f32 = {:.4};", WAVE_AMP_MUL[i]));
            need(&format!("const WAVE_SPEED_MUL_{i}: f32 = {};", fmt(WAVE_SPEED_MUL[i])));
            need(&format!("const WAVE_PHASE_{i}: f32 = {};", fmt(WAVE_PHASE[i])));
        }
    }

    /// WGSL のリテラル表記（整数値でも `1.0`、それ以外は最短表記）に合わせる。
    fn fmt(v: f32) -> String {
        if v.fract() == 0.0 { format!("{v:.1}") } else { format!("{v}") }
    }
}
