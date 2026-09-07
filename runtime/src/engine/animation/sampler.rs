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

/// クランプ付き自動タンジェント（成分ごとの傾き）。
///
/// 基本は前後キーの中央差分（Catmull-Rom 風）だが、そのままでは
/// 0 → 1 → 1 → 0 のような「極値で止まる」キー列で 1 を通り越す
/// （オーバーシュート）ため、Fritsch–Carlson の単調性条件で傾きを抑える:
///
/// - キーが**局所的な極値**（前後どちらの割線とも向きが揃わない・片方が 0）なら傾き 0
///   （Unity の "Clamped Auto"・Blender の "Auto Clamped" と同じ振る舞い）。
/// - それ以外は中央差分を、隣接する両割線の 3 倍以内に抑える（曲線が区間の値域を出ない）。
///
/// 端点では隣接する 1 区間の割線をそのまま使う（1 区間しか無ければ端で減速しない
/// 直線的な出入りになる。これは従来どおり）。
fn auto_tangent(track: &Track, idx: usize, n: usize) -> Vec<f32> {
    let keys = &track.keys;
    let cur = keys[idx].value.to_components();
    let comp = |v: &[f32], j: usize| -> f32 {
        v.get(j)
            .copied()
            .unwrap_or_else(|| cur.get(j).copied().unwrap_or(0.0))
    };

    let has_prev = idx > 0;
    let has_next = idx + 1 < keys.len();

    // 前区間・後区間の割線（値/秒）。存在しない側は None。
    let prev_secant = |j: usize| -> Option<f32> {
        if !has_prev {
            return None;
        }
        let dt = keys[idx].time - keys[idx - 1].time;
        if dt.abs() <= f32::EPSILON {
            return None;
        }
        Some((comp(&cur, j) - comp(&keys[idx - 1].value.to_components(), j)) / dt)
    };
    let next_secant = |j: usize| -> Option<f32> {
        if !has_next {
            return None;
        }
        let dt = keys[idx + 1].time - keys[idx].time;
        if dt.abs() <= f32::EPSILON {
            return None;
        }
        Some((comp(&keys[idx + 1].value.to_components(), j) - comp(&cur, j)) / dt)
    };

    (0..n)
        .map(|j| clamped_auto_slope(prev_secant(j), next_secant(j)))
        .collect()
}

/// 前後の割線から、オーバーシュートしない傾きを決める（単一成分）。
///
/// - 両側あり: 向きが異なる／どちらかが 0 なら 0（極値）。同じ向きなら中央差分を
///   `AUTO_TANGENT_SECANT_LIMIT` × min(|割線|) で頭打ちにする。
/// - 片側のみ（端点）: その割線をそのまま使う。
/// - 両側なし（キー 1 つ）: 0。
fn clamped_auto_slope(prev: Option<f32>, next: Option<f32>) -> f32 {
    match (prev, next) {
        (Some(a), Some(b)) => {
            if a == 0.0 || b == 0.0 || (a > 0.0) != (b > 0.0) {
                return 0.0;
            }
            let central = (a + b) * 0.5;
            let limit = AUTO_TANGENT_SECANT_LIMIT * a.abs().min(b.abs());
            central.clamp(-limit, limit)
        }
        (Some(a), None) => a,
        (None, Some(b)) => b,
        (None, None) => 0.0,
    }
}

/// 自動タンジェントの上限（隣接割線の何倍まで許すか）。
/// 3 は 3 次エルミートが区間内で単調になる Fritsch–Carlson の十分条件。
const AUTO_TANGENT_SECANT_LIMIT: f32 = 3.0;

/// 成分列を長さ n に整える（不足は 0.0 で補い、超過は切り詰める）。
fn pad(v: &[f32], n: usize) -> Vec<f32> {
    (0..n).map(|j| v.get(j).copied().unwrap_or(0.0)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::animation::clip::{Keyframe, TrackTarget, ValueType};

    /// 自動タンジェント（in/out 省略）の Bezier キー列から Float トラックを組む。
    fn bezier_float_track(points: &[(f32, f32)]) -> Track {
        Track {
            target: TrackTarget {
                actor_path: String::new(),
                component: "canvas_transform".into(),
                property: "rotation".into(),
            },
            value_type: ValueType::Float,
            keys: points
                .iter()
                .map(|&(time, v)| Keyframe {
                    time,
                    value: AnimValue::Float(v),
                    interp: Interp::Bezier,
                    in_tan: None,
                    out_tan: None,
                })
                .collect(),
        }
    }

    fn float_of(v: AnimValue) -> f32 {
        match v {
            AnimValue::Float(f) => f,
            other => panic!("Float を期待したが {other:?}"),
        }
    }

    /// 0 → 1 → 1 → 0 の自動タンジェント Bezier は、どの時刻でも [0, 1] の範囲を出ない
    /// （極値キーで傾き 0 になり、1 を通り越さない）。
    #[test]
    fn auto_tangent_does_not_overshoot_on_plateau() {
        let track = bezier_float_track(&[(0.0, 0.0), (1.0, 1.0), (2.0, 1.0), (3.0, 0.0)]);
        const SAMPLES: u32 = 300;
        for i in 0..=SAMPLES {
            let t = 3.0 * i as f32 / SAMPLES as f32;
            let v = float_of(sample_track(&track, t).unwrap());
            assert!(
                (-1e-4..=1.0 + 1e-4).contains(&v),
                "t={t} で値 {v} が [0,1] を外れた（オーバーシュート）"
            );
        }
        // 平坦区間（1 → 1）は厳密に 1 のまま
        let mid = float_of(sample_track(&track, 1.5).unwrap());
        assert!((mid - 1.0).abs() < 1e-5, "平坦区間の値 {mid}");
    }

    /// 単調増加のキー列（0 → 1 → 3 → 4）は単調のまま補間される（勾配の頭打ちが効く）。
    #[test]
    fn auto_tangent_keeps_monotonic_sequence_monotonic() {
        let track = bezier_float_track(&[(0.0, 0.0), (1.0, 1.0), (1.2, 3.0), (2.2, 4.0)]);
        let mut last = f32::NEG_INFINITY;
        const SAMPLES: u32 = 400;
        for i in 0..=SAMPLES {
            let t = 2.2 * i as f32 / SAMPLES as f32;
            let v = float_of(sample_track(&track, t).unwrap());
            assert!(v >= last - 1e-4, "t={t} で値が減少した: {last} → {v}");
            last = v;
        }
    }

    /// 傾きの決定規則（単一成分）。
    #[test]
    fn clamped_auto_slope_rules() {
        // 極値（向きが逆）は 0
        assert_eq!(clamped_auto_slope(Some(1.0), Some(-1.0)), 0.0);
        // 片側が平坦なら 0
        assert_eq!(clamped_auto_slope(Some(1.0), Some(0.0)), 0.0);
        // 同じ向きは中央差分（上限内）
        assert!((clamped_auto_slope(Some(1.0), Some(2.0)) - 1.5).abs() < 1e-6);
        // 片側が極端に急でも、緩い側の 3 倍で頭打ち
        assert!((clamped_auto_slope(Some(1.0), Some(100.0)) - 3.0).abs() < 1e-6);
        // 端点は隣接割線そのまま
        assert_eq!(clamped_auto_slope(None, Some(2.0)), 2.0);
        assert_eq!(clamped_auto_slope(Some(-2.0), None), -2.0);
        assert_eq!(clamped_auto_slope(None, None), 0.0);
    }
}
