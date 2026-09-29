// ============================================================
//  gesture/velocity.rs — 指の速度の推定（直近の標本の最小二乗。W2-2。頑健化 R1〜R4 は 2026-09-29）
//
//  【やり方】（Flutter の VelocityTracker・Android の VelocityTracker の LSQ2 と同じ考え方）
//    1. 最新の標本からさかのぼり、時間の窓（horizon。既定 100ms）の内側で、しかも標本どうしの間隔が
//       「止まった」とみなす間隔（stop。既定 40ms）以下の標本だけを使う（最大 max_samples 個。既定 20）
//    2. 時刻（最新を 0 とするミリ秒。過去は負）に対する x・y を、次数 degree（既定 2）の多項式へ最小二乗で当てはめ、
//       1 次の係数（時刻 0 での傾き）を速度とする。標本が少なければ次数を下げる（標本数 − 1 まで。Android の LSQ と同じ）
//  時刻はイベントの時刻（pointer_log.rs。Android は MotionEvent の eventTime と履歴。docs/input_gestures.md §5）なので、
//  フレームの時刻や fps に依らない。
//
//  【頑健化の規則】（docs/input_gestures.md §5.1。閾値は thresholds.rs の名前付きの定数で、project_settings.json の
//  "gestures" で上書きでき、0 でその規則を切る）
//    R1 止まった（従来）: 問い合わせの時刻が最新の標本から stop（40 ms）より後なら 0（離す前に止まっていた指はフリックにしない）
//    R2 持ち上げの揺れ（離しだけ）: 離した時刻 T の少し前の区間 [T − stop, T − lift_off]（既定 [T − 40, T − 16.7] ms）の標本を
//       直線で当てはめた速度が、最小のフリックの速度より遅ければ離しの速度を 0 にする（＝離す前に指がほぼ止まっていて、
//       最後の 1 フレームの動きは指の腹の転がり）。ここは材料（`ReleaseEstimate::before_lift_off`）だけを作り、
//       判定は軸と最小の速度を知る recognizers/fling.rs が行う。T − lift_off の時点で最新の標本が stop より古ければ
//       その時点で止まっていた（R1 をその時点を「今」として当てる）。区間の標本が 2 つ未満なら T − lift_off までの新しい
//       2 標本で当てはめ、T − lift_off までの標本が 1 つ以下なら決められない（R2 は当てない。触れてすぐ払ったフリックを消さない）
//    R3 幅が短すぎる: 推定に使った標本の時間の幅（最新 − 最古）が min_span（1 フレーム）未満なら 0
//       （1 フレームより短い幅は受け取りのまとまりの中の標本だけから出た値で、物理の速度ではない）
//    R4 間隔の下限: 前の標本との間隔が min_sample_interval（1 ms）未満の標本は、前の標本と入れ替える（新しい方を残す）
//
//  【単位】位置は画素、時刻は秒、戻り値は画素/秒。
// ============================================================

use std::collections::VecDeque;

use super::thresholds::{VelocityParams, MILLIS_PER_SECOND};

/// 最小二乗の未知数の最大数（2 次 = 3 個）。
const MAX_COEFFICIENTS: usize = 3;

/// 連立方程式を解くときに 0 とみなすピボットの大きさ（これより小さいと解けないとして次数を下げる）。
const PIVOT_EPSILON: f64 = 1e-12;

/// R2 の見る区間（離す前の [T − stop, T − lift_off]）を当てはめる多項式の次数（直線）。
///
/// 区間は短い（既定 23 ms・120 Hz の標本で 2〜3 個）ので、2 次の端の傾きは止めた指の揺れ（1 dp 未満）を大きく拾う。
/// 直線は揺れを区間の長さで均す。窓 100 ms・2 次の推定そのものを使うと、止める前の払いが窓に入って 2 次の当てはめの
/// 端の傾きが逆向きに振れ、「止まっていた」を見落とす（試算: 払ってから 40〜120 ms 止めて離した 160 通りのうち、
/// 0 にできたのは 60 通りだけだった。docs/input_gestures.md §5.1）。
const LIFT_OFF_PROBE_DEGREE: usize = 1;

/// R2 の見る区間の当てはめに要る標本の数（直線なので 2）。区間の中に足りなければ、その前の標本で補う。
const MIN_PROBE_SAMPLES: usize = LIFT_OFF_PROBE_DEGREE + 1;

/// 標本 1 つ。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct VelocitySample {
    /// 時刻（秒）。
    pub time: f64,
    /// 位置（画素）。
    pub position: [f32; 2],
}

/// 離しの速度を 0 にした理由（診断ログ `[SEED GESTURE] release … rule=` の値）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ReleaseRule {
    /// 規則に当たらなかった（推定の値をそのまま使う）。
    Ok,
    /// R1: 離す前に止まっていた（最新の標本から stop より後に離した）。
    Stopped,
    /// R2: 持ち上げの揺れ（離す前の区間で指がほぼ止まっていた。判定は recognizers/fling.rs）。
    LiftOff,
    /// R3: 推定に使った標本の時間の幅が短すぎる（標本が 1 つ以下も含む）。
    ShortSpan,
}

impl ReleaseRule {
    /// 診断ログの値（ok / stopped / lift_off / short_span）。
    pub fn label(self) -> &'static str {
        match self {
            ReleaseRule::Ok => "ok",
            ReleaseRule::Stopped => "stopped",
            ReleaseRule::LiftOff => "lift_off",
            ReleaseRule::ShortSpan => "short_span",
        }
    }
}

/// 離した時点の速度の推定の材料（R1・R3 を当てた結果と、R2 の材料）。画素/秒。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ReleaseEstimate {
    /// R1・R3 を当てた後の推定（当たれば 0）。
    pub velocity: [f32; 2],
    /// 規則（R1〜R3）を当てない当てはめの値（最新の標本の時点の傾き。診断用）。
    pub raw: [f32; 2],
    /// R1・R3 のどれで 0 にしたか（当たらなければ Ok。R2 は fling.rs が決める）。
    pub rule: ReleaseRule,
    /// R2 の材料: 持ち上げの前の区間の速度（その時点で止まっていれば 0。決められなければ None）。
    pub before_lift_off: Option<[f32; 2]>,
}

/// 推定に使う標本の選び方の結果。
enum Window {
    /// R1: 問い合わせの時刻に対して最新の標本が古すぎる（指は止まっていた）。
    Stopped,
    /// 使う標本（新しい順）。
    Samples(Vec<VelocitySample>),
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
    ///
    /// R4: 前の標本との間隔が `min_sample_interval_secs` 未満なら、足さずに前の標本と入れ替える（新しい位置を残す。
    /// 時刻は前の標本より前へ戻さない）。受け取りのまとまりで µs 差に並んだ標本を 1 つにまとめる。
    pub fn add(&mut self, time: f64, position: [f32; 2], params: &VelocityParams) {
        if params.min_sample_interval_secs > 0.0 {
            if let Some(last) = self.samples.back_mut() {
                if time - last.time < params.min_sample_interval_secs {
                    *last = VelocitySample { time: time.max(last.time), position };
                    return;
                }
            }
        }
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

    /// 時刻 `now` の速度（画素/秒）を推定する【純関数】（R1・R3 に当たれば 0）。
    ///
    /// # 引数
    /// * `now`    - 問い合わせの時刻（秒。移動・離したイベントの時刻など）
    /// * `params` - 窓・標本の数・止まった間隔・次数・R3 の幅の下限
    pub fn estimate(&self, now: f64, params: &VelocityParams) -> [f32; 2] {
        self.estimate_with_rule(now, params).0
    }

    /// 離した時点の速度の推定の材料を作る【純関数】（R1・R3 を当てた推定・規則を当てない値・R2 の材料）。
    ///
    /// # 引数
    /// * `up_time` - 離したイベントの時刻（秒）
    /// * `params`  - 推定の設定（R2 の持ち上げの間・R3 の幅の下限を含む）
    pub fn release_estimate(&self, up_time: f64, params: &VelocityParams) -> ReleaseEstimate {
        let (velocity, rule) = self.estimate_with_rule(up_time, params);
        ReleaseEstimate {
            velocity,
            raw: self.raw_estimate(params),
            rule,
            before_lift_off: self.lift_off_probe(up_time, params),
        }
    }

    /// 時刻 `now` の速度と、0 にした理由（R1・R3。当たらなければ Ok）。
    fn estimate_with_rule(&self, now: f64, params: &VelocityParams) -> ([f32; 2], ReleaseRule) {
        match self.window(now, params) {
            Window::Stopped => ([0.0, 0.0], ReleaseRule::Stopped),
            Window::Samples(used) => {
                // R3: 幅が 1 フレームより短い推定は物理の速度ではない（標本が 1 つ以下なら幅は 0）
                if params.min_span_secs > 0.0 && span_secs(&used) < params.min_span_secs {
                    return ([0.0, 0.0], ReleaseRule::ShortSpan);
                }
                (fit_slope(&used, params.degree), ReleaseRule::Ok)
            }
        }
    }

    /// 規則（R1・R3）を当てない当てはめ（最新の標本の時刻を「今」とした窓の傾き。診断用）。
    fn raw_estimate(&self, params: &VelocityParams) -> [f32; 2] {
        let Some(newest) = self.samples.back() else { return [0.0, 0.0] };
        match self.window(newest.time, params) {
            Window::Samples(used) => fit_slope(&used, params.degree),
            Window::Stopped => [0.0, 0.0],
        }
    }

    /// R2 の材料: 離した時刻 `up_time` の前の区間 [up_time − stop, up_time − lift_off] の速度（直線の当てはめ）。
    ///
    /// - lift_off が 0 なら None（R2 を切る）
    /// - その時点（up_time − lift_off）までの標本が 1 つ以下なら None（触れてから持ち上げの間の中で払った＝決められない。
    ///   触れてすぐ払った短いフリックを消さない）
    /// - その時点で最新の標本が stop より古ければ 0（R1 をその時点を「今」として当てる＝止まっていた）
    /// - 区間の標本が 2 つ未満なら、その時点までの新しい 2 標本で当てはめる（区間の前に止まっていた指が区間の中で
    ///   1 回だけ少し動いた＝stop を超える間隔の後の小さな動き、を「止まっていた」と測れるように。実機の記録の 3 回目の離し）
    fn lift_off_probe(&self, up_time: f64, params: &VelocityParams) -> Option<[f32; 2]> {
        if params.lift_off_secs <= 0.0 {
            return None;
        }
        let probe_time = up_time - params.lift_off_secs;
        let from = up_time - params.stop_secs;
        // その時点までの標本（新しい順）
        let before: Vec<VelocitySample> = self.samples.iter().rev().filter(|s| s.time <= probe_time).copied().collect();
        let newest = before.first()?;
        if probe_time - newest.time > params.stop_secs {
            return Some([0.0, 0.0]);
        }
        let in_interval = before.iter().take_while(|s| s.time >= from).count();
        let used = &before[..in_interval.max(MIN_PROBE_SAMPLES).min(before.len())];
        if used.len() < MIN_PROBE_SAMPLES {
            return None;
        }
        Some(fit_slope(used, LIFT_OFF_PROBE_DEGREE))
    }

    /// 推定に使う標本（新しい順）を選ぶ（R1 の止まりの判定を含む）。
    fn window(&self, now: f64, params: &VelocityParams) -> Window {
        let Some(newest) = self.samples.back().copied() else { return Window::Samples(Vec::new()) };
        // 問い合わせの時刻に対して最新の標本が古すぎれば、指は止まっていた
        if now - newest.time > params.stop_secs {
            return Window::Stopped;
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
        Window::Samples(used)
    }
}

/// 標本（新しい順）の時間の幅（最新 − 最古。秒。1 つ以下なら 0）【純関数】。
fn span_secs(used: &[VelocitySample]) -> f64 {
    match (used.first(), used.last()) {
        (Some(newest), Some(oldest)) => newest.time - oldest.time,
        _ => 0.0,
    }
}

/// 標本（新しい順）を次数 `degree` で当てはめ、最新の標本の時点の傾き（画素/秒）を返す【純関数】。
///
/// 時刻は最新を 0 とするミリ秒（過去は負）にして桁をそろえ、正規方程式の条件を良くする（Flutter と同じ単位）。
/// 解けなければ次数を下げて解き直し、最後は 0（標本が 1 つ以下・すべての時刻が同じ）。
fn fit_slope(used: &[VelocitySample], degree: usize) -> [f32; 2] {
    if used.len() < 2 {
        return [0.0, 0.0];
    }
    let newest = used[0].time;
    let ages: Vec<f64> = used
        .iter()
        .map(|s| (s.time - newest) * f64::from(MILLIS_PER_SECOND))
        .collect();
    let max_degree = degree.min(used.len() - 1).min(MAX_COEFFICIENTS - 1);
    let mut out = [0.0f32; 2];
    for (axis, value) in out.iter_mut().enumerate() {
        let ys: Vec<f64> = used.iter().map(|s| f64::from(s.position[axis])).collect();
        let slope_per_ms = (1..=max_degree).rev().find_map(|d| polyfit_slope(&ages, &ys, d));
        *value = slope_per_ms.map_or(0.0, |s| (s * f64::from(MILLIS_PER_SECOND)) as f32);
    }
    out
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

    /// 既定の設定（100ms・20 個・40ms・2 次・R2 = R3 = 1 フレーム・R4 = 1 ms）。
    fn params() -> VelocityParams {
        GestureMetrics::default().velocity
    }

    /// R2〜R4 を切った設定（規則が無い場合の値を比べる）。
    fn params_without_robust_rules() -> VelocityParams {
        VelocityParams { lift_off_secs: 0.0, min_span_secs: 0.0, min_sample_interval_secs: 0.0, ..params() }
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
    /// （3 個・5 ms 間隔は幅 10 ms で R3〈1 フレーム〉に掛かるので、この試験は R3 を切って上限だけを確かめる）
    #[test]
    fn sample_count_is_capped() {
        let p = VelocityParams { max_samples: 3, min_span_secs: 0.0, ..params() };
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

    // ─── 頑健化（R1〜R4）──────────────────────────────────

    /// R4: 前の標本から 1 ms 未満の標本は入れ替わる（新しい位置を残す）。1 ms 以上なら足す。0 で切れば全部残す。
    #[test]
    fn r4_merges_samples_closer_than_min_interval() {
        let p = params();
        let mut t = VelocityTracker::new();
        t.add(0.0, [0.0, 0.0], &p);
        t.add(0.0003, [3.0, 0.0], &p);
        t.add(0.0006, [6.0, 0.0], &p);
        assert_eq!(t.len(), 1, "µs 差の 3 標本は 1 つにまとまる");
        assert_eq!(t.samples[0], VelocitySample { time: 0.0006, position: [6.0, 0.0] }, "新しい方を残す");
        t.add(0.0016, [7.0, 0.0], &p);
        assert_eq!(t.len(), 2, "1 ms 離れた標本は足す");
        // 時刻が逆行した標本は、前の標本の時刻のまま位置だけ入れ替える（並びを壊さない）
        t.add(0.001, [8.0, 0.0], &p);
        assert_eq!(t.samples[1], VelocitySample { time: 0.0016, position: [8.0, 0.0] });
        let off = params_without_robust_rules();
        let mut t = VelocityTracker::new();
        for i in 0..3 {
            t.add(0.0, [f32::from(i as u8), 0.0], &off);
        }
        assert_eq!(t.len(), 3, "R4 を切れば同じ時刻の標本も残る（従来どおり）");
    }

    /// R3: 幅が 1 フレーム（16.7 ms）未満の推定は 0。R3 を切れば従来どおりの値。
    #[test]
    fn r3_short_span_is_zero() {
        let p = params();
        let mut t = VelocityTracker::new();
        for i in 0..3 {
            let time = f64::from(i) * 0.005;
            t.add(time, [2000.0 * time as f32, 0.0], &p);
        }
        let est = t.release_estimate(0.01, &p);
        assert_eq!((est.velocity, est.rule), ([0.0, 0.0], ReleaseRule::ShortSpan), "幅 10 ms");
        assert!((est.raw[0] - 2000.0).abs() < 1.0, "規則を当てない値は残す: {:?}", est.raw);
        let off = params_without_robust_rules();
        assert!((t.estimate(0.01, &off)[0] - 2000.0).abs() < 1.0);
        // 幅が 1 フレームちょうど（120 Hz の 3 標本 = 2 × 8.33 ms）なら推定する
        let mut t = VelocityTracker::new();
        for i in 0..3 {
            let time = f64::from(i) / 120.0;
            t.add(time, [2000.0 * time as f32, 0.0], &p);
        }
        let est = t.release_estimate(2.0 / 120.0, &p);
        assert_eq!(est.rule, ReleaseRule::Ok);
        assert!((est.velocity[0] - 2000.0).abs() < 1.0, "{:?}", est.velocity);
    }

    /// R1 は離しの結果の理由にも出る（規則を当てない値は止まる前の傾き）。
    #[test]
    fn r1_is_reported_as_stopped() {
        let p = params();
        let mut t = VelocityTracker::new();
        for i in 0..5 {
            let time = f64::from(i) * 0.01;
            t.add(time, [1000.0 * time as f32, 0.0], &p);
        }
        let est = t.release_estimate(0.04 + 0.041, &p);
        assert_eq!((est.velocity, est.rule), ([0.0, 0.0], ReleaseRule::Stopped));
        assert!((est.raw[0] - 1000.0).abs() < 1.0, "{:?}", est.raw);
        let probe = est.before_lift_off.expect("区間 [41, 64.3] ms に標本が無いので、その時点までの新しい 2 標本（30・40 ms）で測る");
        assert!((probe[0] - 1000.0).abs() < 1.0, "{probe:?}");
        // さらに待って離す: 離す 16.7 ms 前の時点でも最新の標本から 40 ms を超えている → R2 の材料も 0（止まっていた）
        let est = t.release_estimate(0.04 + 0.04 + 0.0167 + 0.001, &p);
        assert_eq!(est.before_lift_off, Some([0.0, 0.0]));
    }

    /// R2 の材料: 動き続けた指は区間の速度、止めた指はほぼ 0、触れてすぐ払った指は決められない（None）、lift_off 0 で切れる。
    #[test]
    fn lift_off_probe_measures_the_interval_before_release() {
        let p = params();
        // 8 ms ごとに 1000 px/s で 80 ms 動かし、そのまま離す → 区間の直線の速度 ≈ 1000
        let mut moving = VelocityTracker::new();
        for i in 0..=10 {
            let time = f64::from(i) * 0.008;
            moving.add(time, [1000.0 * time as f32, 0.0], &p);
        }
        let probe = moving.release_estimate(0.0805, &p).before_lift_off.expect("区間に標本がある");
        assert!((probe[0] - 1000.0).abs() < 1.0, "{probe:?}");
        // 払ってから 48 ms 止めた（揺れ ±0.3 px が 8 ms ごと）→ 区間の速度はほぼ 0
        let mut held = VelocityTracker::new();
        for i in 0..=10 {
            let time = f64::from(i) * 0.008;
            held.add(time, [1000.0 * time as f32, 0.0], &p);
        }
        for (k, jitter) in [0.3f32, -0.2, 0.1, -0.3, 0.2, -0.1].iter().enumerate() {
            held.add(0.08 + 0.008 * (k as f64 + 1.0), [80.0 + jitter, 0.0], &p);
        }
        let probe = held.release_estimate(0.128 + 0.001, &p).before_lift_off.expect("区間に標本がある");
        assert!(probe[0].abs() < 50.0, "止めた指: {probe:?}");
        // 触れて 20 ms で離した速い払い → 区間（離す 16.7 ms 前まで）に標本が 1 つしか無い → 決められない
        let mut quick = VelocityTracker::new();
        for (time, x) in [(0.0, 0.0f32), (0.01, 20.0), (0.02, 40.0)] {
            quick.add(time, [x, 0.0], &p);
        }
        assert_eq!(quick.release_estimate(0.02, &p).before_lift_off, None);
        // 区間の標本が 1 つ（50 ms 止まった後の 0.5 px の小さな動き。実機の記録の 3 回目）→ その前の標本と 2 つで測る → ほぼ 0
        let mut paused = VelocityTracker::new();
        for (time, y) in [(0.0, 0.0f32), (0.008, 2.0), (0.058, 1.5)] {
            paused.add(time, [0.0, y], &p);
        }
        let probe = paused.release_estimate(0.081, &p).before_lift_off.expect("前の標本で補う");
        assert!((probe[1] + 10.0).abs() < 0.1, "−0.5 px / 50 ms = −10 px/s: {probe:?}");
        // lift_off を 0 にすると R2 の材料を作らない
        let off = VelocityParams { lift_off_secs: 0.0, ..p };
        assert_eq!(moving.release_estimate(0.0805, &off).before_lift_off, None);
    }
}
