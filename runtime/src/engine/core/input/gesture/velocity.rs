// ============================================================
//  gesture/velocity.rs — 指の速度の推定（直近の標本の最小二乗。W2-2）
//
//  【やり方】（Flutter の VelocityTracker・Android の VelocityTracker の LSQ2 と同じ考え方）
//    1. 最新の標本からさかのぼり、時間の窓（horizon。既定 100ms）の内側で、しかも標本どうしの間隔が
//       「止まった」とみなす間隔（stop。既定 40ms）以下の標本だけを使う（最大 max_samples 個。既定 20）
//    2. 時刻（最新を 0 とするミリ秒。過去は負）に対する x・y を、次数 degree（既定 2）の多項式へ最小二乗で当てはめ、
//       1 次の係数（時刻 0 での傾き）を速度とする。標本が少なければ次数を下げる（標本数 − 1 まで。Android の LSQ と同じ）
//    3. 問い合わせの時刻が最新の標本から stop 以上あいていたら 0（離す前に止まっていた指はフリックにしない）
//  時刻はイベントの時刻（pointer_log.rs）なので、フレームの時刻や fps に依らない。
//
//  【単位】位置は画素、時刻は秒、戻り値は画素/秒。
// ============================================================

use std::collections::VecDeque;

use super::thresholds::{VelocityParams, MILLIS_PER_SECOND};

/// 最小二乗の未知数の最大数（2 次 = 3 個）。
const MAX_COEFFICIENTS: usize = 3;

/// 連立方程式を解くときに 0 とみなすピボットの大きさ（これより小さいと解けないとして次数を下げる）。
const PIVOT_EPSILON: f64 = 1e-12;

/// 標本 1 つ。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct VelocitySample {
    /// 時刻（秒）。
    pub time: f64,
    /// 位置（画素）。
    pub position: [f32; 2],
}

/// 指 1 本の速度の推定（直近の標本を持つ）。
#[derive(Clone, Debug, Default)]
pub struct VelocityTracker {
    /// 標本（古い順）。上限は追加のときの max_samples。
    samples: VecDeque<VelocitySample>,
}

impl VelocityTracker {
    /// 空の推定を作る。
    pub fn new() -> Self {
        Self::default()
    }

    /// 標本を 1 つ足す（古いものから捨てて max_samples 個までにする）。
    pub fn add(&mut self, time: f64, position: [f32; 2], params: &VelocityParams) {
        self.samples.push_back(VelocitySample { time, position });
        while self.samples.len() > params.max_samples {
            self.samples.pop_front();
        }
    }

    /// 持っている標本の数。
    pub fn len(&self) -> usize {
        self.samples.len()
    }

    /// 標本が無いか。
    pub fn is_empty(&self) -> bool {
        self.samples.is_empty()
    }

    /// 時刻 `now` の速度（画素/秒）を推定する【純関数】。
    ///
    /// # 引数
    /// * `now`    - 問い合わせの時刻（秒。離したイベントの時刻など）
    /// * `params` - 窓・標本の数・止まった間隔・次数
    pub fn estimate(&self, now: f64, params: &VelocityParams) -> [f32; 2] {
        let used = self.window(now, params);
        if used.len() < 2 {
            return [0.0, 0.0];
        }
        let newest = used[0].time;
        // 時刻は最新を 0 とするミリ秒（過去は負）。桁をそろえて正規方程式の条件を良くする（Flutter と同じ単位）
        let ages: Vec<f64> = used
            .iter()
            .map(|s| (s.time - newest) * f64::from(MILLIS_PER_SECOND))
            .collect();
        let max_degree = params.degree.min(used.len() - 1).min(MAX_COEFFICIENTS - 1);
        let mut out = [0.0f32; 2];
        for (axis, value) in out.iter_mut().enumerate() {
            let ys: Vec<f64> = used.iter().map(|s| f64::from(s.position[axis])).collect();
            // 解けなければ次数を下げて解き直す（すべての時刻が同じなら 0）
            let slope_per_ms = (1..=max_degree).rev().find_map(|degree| polyfit_slope(&ages, &ys, degree));
            *value = slope_per_ms.map_or(0.0, |s| (s * f64::from(MILLIS_PER_SECOND)) as f32);
        }
        out
    }

    /// 推定に使う標本（新しい順）を選ぶ。
    fn window(&self, now: f64, params: &VelocityParams) -> Vec<VelocitySample> {
        let Some(newest) = self.samples.back().copied() else { return Vec::new() };
        // 問い合わせの時刻に対して最新の標本が古すぎれば、指は止まっていた
        if now - newest.time > params.stop_secs {
            return Vec::new();
        }
        let mut used = vec![newest];
        let mut previous = newest;
        for sample in self.samples.iter().rev().skip(1) {
            let age = newest.time - sample.time;
            let gap = previous.time - sample.time;
            if age > params.horizon_secs || gap > params.stop_secs || used.len() >= params.max_samples {
                break;
            }
            used.push(*sample);
            previous = *sample;
        }
        used
    }
}

/// 点列 (x, y) を次数 `degree` の多項式へ最小二乗で当てはめ、x = 0 での傾き（1 次の係数）を返す【純関数】。
///
/// 正規方程式 (AᵀA) c = Aᵀy を部分ピボットのガウスの消去法で解く（未知数は高々 3 個）。
/// 解けない（ピボットが 0。x の種類が次数 + 1 より少ない等）ときは None。
fn polyfit_slope(xs: &[f64], ys: &[f64], degree: usize) -> Option<f64> {
    let n = degree + 1;
    if n > MAX_COEFFICIENTS || xs.len() < n || degree == 0 {
        return None;
    }
    // 正規方程式の拡大係数行列 [AᵀA | Aᵀy]
    let mut m = [[0.0f64; MAX_COEFFICIENTS + 1]; MAX_COEFFICIENTS];
    for (&x, &y) in xs.iter().zip(ys) {
        let mut powers = [1.0f64; 2 * MAX_COEFFICIENTS - 1];
        for k in 1..powers.len() {
            powers[k] = powers[k - 1] * x;
        }
        for (r, row) in m.iter_mut().enumerate().take(n) {
            for (c, cell) in row.iter_mut().enumerate().take(n) {
                *cell += powers[r + c];
            }
            row[n] += powers[r] * y;
        }
    }
    // 前進消去（部分ピボット）
    for col in 0..n {
        let pivot_row = (col..n).max_by(|&a, &b| m[a][col].abs().total_cmp(&m[b][col].abs()))?;
        if m[pivot_row][col].abs() < PIVOT_EPSILON {
            return None;
        }
        m.swap(col, pivot_row);
        for row in (col + 1)..n {
            let factor = m[row][col] / m[col][col];
            for k in col..=n {
                m[row][k] -= factor * m[col][k];
            }
        }
    }
    // 後退代入
    let mut coef = [0.0f64; MAX_COEFFICIENTS];
    for row in (0..n).rev() {
        let mut sum = m[row][n];
        for k in (row + 1)..n {
            sum -= m[row][k] * coef[k];
        }
        coef[row] = sum / m[row][row];
    }
    coef[1].is_finite().then_some(coef[1])
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::input::gesture::thresholds::GestureMetrics;

    /// 既定の設定（100ms・20 個・40ms・2 次）。
    fn params() -> VelocityParams {
        GestureMetrics::default().velocity
    }

    /// 等速の直線運動（1000 px/s）なら、次数 1・2 とも 1000 px/s。
    #[test]
    fn constant_velocity_is_recovered() {
        let p = params();
        let mut t = VelocityTracker::new();
        for i in 0..10 {
            let time = f64::from(i) * 0.008;
            t.add(time, [1000.0 * time as f32, -500.0 * time as f32], &p);
        }
        let v = t.estimate(0.072, &p);
        assert!((v[0] - 1000.0).abs() < 1.0 && (v[1] + 500.0).abs() < 1.0, "{v:?}");
        let linear = VelocityParams { degree: 1, ..p };
        let v1 = t.estimate(0.072, &linear);
        assert!((v1[0] - 1000.0).abs() < 1.0, "{v1:?}");
    }

    /// 加速度がある運動（x = 5000 t²）の時刻 0.1 の傾き（1000 px/s）を 2 次なら正しく拾う。
    #[test]
    fn quadratic_fit_reports_instant_slope() {
        let p = params();
        let mut t = VelocityTracker::new();
        for i in 0..=10 {
            let time = 0.05 + f64::from(i) * 0.005;
            t.add(time, [(5000.0 * time * time) as f32, 0.0], &p);
        }
        let v = t.estimate(0.1, &p);
        assert!((v[0] - 1000.0).abs() < 1.0, "{v:?}");
    }

    /// 時間の窓: 100ms より古い標本は使わない（窓の外の速い動きが混ざらない）。
    #[test]
    fn horizon_excludes_old_samples() {
        let p = params();
        let mut t = VelocityTracker::new();
        // 古い区間（0〜0.08 秒）は 5000 px/s、その後 0.02 秒ごとに 0.3 秒まで止まらず 100 px/s
        for i in 0..5 {
            let time = f64::from(i) * 0.02;
            t.add(time, [5000.0 * time as f32, 0.0], &p);
        }
        let base = 5000.0 * 0.08;
        for i in 1..=11 {
            let time = 0.08 + f64::from(i) * 0.02;
            t.add(time, [base + 100.0 * (time - 0.08) as f32, 0.0], &p);
        }
        let v = t.estimate(0.3, &p);
        assert!((v[0] - 100.0).abs() < 1.0, "窓の内側だけ: {v:?}");
    }

    /// 標本の数の上限: 直近 max_samples 個まで。
    #[test]
    fn sample_count_is_capped() {
        let p = VelocityParams { max_samples: 3, ..params() };
        let mut t = VelocityTracker::new();
        for i in 0..10 {
            t.add(f64::from(i) * 0.005, [f32::from(i as u8) * 10.0, 0.0], &p);
        }
        assert_eq!(t.len(), 3);
        // 最後の 3 個は 5ms ごとに 10px = 2000 px/s
        let v = t.estimate(0.045, &p);
        assert!((v[0] - 2000.0).abs() < 1.0, "{v:?}");
    }

    /// 止まってから離した: 最新の標本から 40ms 以上あいたら 0。標本の間が 40ms 以上あいたら、それより前は使わない。
    #[test]
    fn stopped_pointer_reports_zero() {
        let p = params();
        let mut t = VelocityTracker::new();
        for i in 0..5 {
            let time = f64::from(i) * 0.01;
            t.add(time, [1000.0 * time as f32, 0.0], &p);
        }
        assert_eq!(t.estimate(0.04 + 0.041, &p), [0.0, 0.0], "40ms を超えて止まっていた");
        assert!(t.estimate(0.04 + 0.039, &p)[0] > 900.0, "40ms 以内なら速度がある");
        // 間が 50ms あいた後の 2 標本だけで推定する（1 標本なら 0）
        t.add(0.09, [40.0, 0.0], &p);
        assert_eq!(t.estimate(0.09, &p), [0.0, 0.0], "直前の標本と 50ms あいているので標本は 1 つ");
    }

    /// 標本が 1 つ・同じ時刻ばかりのときは 0（解けないものは次数を下げ、最後は 0）。
    #[test]
    fn degenerate_inputs_are_zero() {
        let p = params();
        let mut t = VelocityTracker::new();
        assert_eq!(t.estimate(0.0, &p), [0.0, 0.0]);
        t.add(0.0, [0.0, 0.0], &p);
        assert_eq!(t.estimate(0.0, &p), [0.0, 0.0]);
        t.add(0.0, [10.0, 0.0], &p);
        t.add(0.0, [20.0, 0.0], &p);
        assert_eq!(t.estimate(0.0, &p), [0.0, 0.0], "時刻が同じ標本だけでは傾きは決まらない");
    }
}
