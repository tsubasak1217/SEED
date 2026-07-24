// ============================================================
//  sampler.rs — トラックの補間評価（汎用サンプラー）
//
//  【役割】
//  Track と時刻からキーフレームを補間して AnimValue を返す。
//  step / linear / bezier(エルミート) をサポートし、bezier のタンジェント
//  省略時は Catmull-Rom 風の自動タンジェントを用いる。
//
//  【設計方針】
//  補間はすべて f32 成分列（AnimValue::to_components）に対して成分ごとに行い、
//  最後に value_type で再構築する。これにより Float/Vec2/Vec3/Color を同一経路で扱える。
//  Bool は補間不可なので常に直前キーの値を保持する（clip.rs で interp=Step に矯正済み）。
//
//  【備考】
//  既存の glTF 側補間ヘルパ（core/renderer/animator.rs の sample_vec3 等）とは
//  用途が異なるため統合していない。将来的な共通化余地あり（TODO）。
// ============================================================

use super::clip::{AnimValue, Interp, Track};

/// トラックを指定時刻で評価して値を返す。キーが無ければ None。
///
/// - time がキー範囲の外側なら端のキー値でクランプ（ホールド）する。
/// - 区間の補間種別は「区間開始キー（k0）の interp」で決まる。
pub fn sample_track(track: &Track, time: f32) -> Option<AnimValue> {
    let keys = &track.keys;
    if keys.is_empty() {
        return None;
    }

    // 範囲外クランプ（先頭以前・末尾以降は端の値を保持）
    if time <= keys[0].time {
        return Some(keys[0].value);
    }
    let last = keys.len() - 1;
    if time >= keys[last].time {
        return Some(keys[last].value);
    }

    // time を含む区間 [k0, k1] を線形探索で特定する（キー数は通常少数）
    let mut i = 0;
    while i + 1 < keys.len() && keys[i + 1].time <= time {
        i += 1;
    }
    let k0 = &keys[i];
    let k1 = &keys[i + 1];

    let dt = k1.time - k0.time;
    // 同一時刻の重複キーはゼロ除算回避のため k0 を返す
    if dt <= f32::EPSILON {
        return Some(k0.value);
    }
    let t = ((time - k0.time) / dt).clamp(0.0, 1.0);

    let c0 = k0.value.to_components();
    let c1 = k1.value.to_components();
    let n = c0.len().min(c1.len());

    let out: Vec<f32> = match k0.interp {
        // ステップ: 区間内は開始キーの値を保持
        Interp::Step => c0[..n].to_vec(),

        // 線形補間
        Interp::Linear => (0..n).map(|j| lerp(c0[j], c1[j], t)).collect(),

        // ベジェ（エルミート）補間
        Interp::Bezier => {
            // 出タンジェント（k0）: 明示指定が無ければ Catmull-Rom 風の自動値
            let m0 = out_tangent(track, i, n);
            // 入りタンジェント（k1）: 同上
            let m1 = in_tangent(track, i + 1, n);
            (0..n)
                .map(|j| hermite(c0[j], m0[j], c1[j], m1[j], dt, t))
                .collect()
        }
    };

    Some(AnimValue::from_components(track.value_type, &out))
}

// ─── 補間プリミティブ ────────────────────────────────────────

/// 線形補間。
fn lerp(a: f32, b: f32, t: f32) -> f32 {
    a + (b - a) * t
}

/// 3 次エルミート補間。
///
/// p(t) = h00*p0 + h10*(dt*m0) + h01*p1 + h11*(dt*m1)
/// m0/m1 は「値単位 / 時間単位」のタンジェント（傾き）。dt は区間の秒数。
fn hermite(p0: f32, m0: f32, p1: f32, m1: f32, dt: f32, t: f32) -> f32 {
    let t2 = t * t;
    let t3 = t2 * t;
    let h00 = 2.0 * t3 - 3.0 * t2 + 1.0;
    let h10 = t3 - 2.0 * t2 + t;
    let h01 = -2.0 * t3 + 3.0 * t2;
    let h11 = t3 - t2;
    h00 * p0 + h10 * (dt * m0) + h01 * p1 + h11 * (dt * m1)
}

// ─── タンジェント計算 ────────────────────────────────────────

/// キー idx の出タンジェント（成分列）を返す。
/// 明示指定があればそれを、無ければ Catmull-Rom 風の自動タンジェントを使う。
fn out_tangent(track: &Track, idx: usize, n: usize) -> Vec<f32> {
    let keys = &track.keys;
    if let Some(explicit) = &keys[idx].out_tan {
        return pad(explicit, n);
    }
    auto_tangent(track, idx, n)
}

/// キー idx の入りタンジェント（成分列）を返す。
/// 明示指定があればそれを、無ければ Catmull-Rom 風の自動タンジェントを使う。
fn in_tangent(track: &Track, idx: usize, n: usize) -> Vec<f32> {
    let keys = &track.keys;
    if let Some(explicit) = &keys[idx].in_tan {
        return pad(explicit, n);
    }
    auto_tangent(track, idx, n)
}

/// Catmull-Rom 風の自動タンジェント（成分ごとの傾き）。
///
/// キー idx の傾きを前後キーの中央差分 (next - prev) / (t_next - t_prev) で求める。
/// 端点では隣接する 1 区間の傾きで代用する。
fn auto_tangent(track: &Track, idx: usize, n: usize) -> Vec<f32> {
    let keys = &track.keys;
    let cur = keys[idx].value.to_components();

    // 前後の参照キー（端では自身にフォールバック）
    let prev_i = if idx > 0 { idx - 1 } else { idx };
    let next_i = if idx + 1 < keys.len() { idx + 1 } else { idx };
    let prev = keys[prev_i].value.to_components();
    let next = keys[next_i].value.to_components();
    let dt = keys[next_i].time - keys[prev_i].time;

    (0..n)
        .map(|j| {
            if dt.abs() <= f32::EPSILON {
                0.0
            } else {
                let a = prev
                    .get(j)
                    .copied()
                    .unwrap_or_else(|| cur.get(j).copied().unwrap_or(0.0));
                let b = next
                    .get(j)
                    .copied()
                    .unwrap_or_else(|| cur.get(j).copied().unwrap_or(0.0));
                (b - a) / dt
            }
        })
        .collect()
}

/// 成分列を長さ n に整える（不足は 0.0 で補い、超過は切り詰める）。
fn pad(v: &[f32], n: usize) -> Vec<f32> {
    (0..n).map(|j| v.get(j).copied().unwrap_or(0.0)).collect()
}
