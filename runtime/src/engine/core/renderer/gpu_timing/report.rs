// ============================================================
//  gpu_timing/report.rs — GPU タイムスタンプの区間化と集計（純粋な処理）
//
//  【区間の決め方】
//  1 フレームの中で、描画の節目ごとに「区間名」を付けたタイムスタンプを書く（timer.rs）。
//  区間の長さ ＝ そのタイムスタンプ − 1 つ前のタイムスタンプ。最初のタイムスタンプ（BEGIN）は基準で区間を持たない。
//  同じ区間名が 1 フレームに 2 回出たら（前方描画が水面で 2 つに割れる等）足し合わせる。
//  そのフレームで走らなかったパスの区間名は書かれないので 0 として扱う。
//
//  【読めないフレーム】
//  タイムスタンプが逆戻りした・1 フレームが上限（MAX_PLAUSIBLE_FRAME_MS）を超えた、はドライバの
//  報告が信用できないので、そのフレームを丸ごと捨てて数だけ数える（集計を汚さない）。
//
//  集計は一定時間（REPORT_INTERVAL_SECS）ごとに 1 行にして出し、次の区間へ持ち越さない。
// ============================================================

/// 基準（フレームの最初）のタイムスタンプの名前。区間は持たない。
pub const BEGIN: &str = "begin";

/// 1 フレームの GPU 時間として信用する上限 [ms]（これを超えるのはタイムスタンプの報告の誤りとみなす）。
pub const MAX_PLAUSIBLE_FRAME_MS: f64 = 1000.0;

/// ナノ秒 → ミリ秒。
const NS_PER_MS: f64 = 1_000_000.0;

/// 1 フレームぶんの区間の長さを求める【純関数】。
///
/// # 引数
/// * `labels`    - タイムスタンプを書いた順の区間名（先頭は BEGIN）
/// * `ticks`     - 同じ順のタイムスタンプ（GPU のカウンタ値）
/// * `period_ns` - カウンタ 1 つあたりのナノ秒（`Queue::get_timestamp_period`）
///
/// # 戻り値
/// (区間名, ミリ秒) の並び（同名は足し合わせ、初出の順）と合計 [ms]。読めないフレームは None。
pub fn segment_durations(
    labels: &[&'static str],
    ticks: &[u64],
    period_ns: f32,
) -> Option<(Vec<(&'static str, f64)>, f64)> {
    if labels.len() != ticks.len() || ticks.len() < 2 || !(period_ns.is_finite() && period_ns > 0.0) {
        return None;
    }
    let to_ms = |delta: u64| delta as f64 * f64::from(period_ns) / NS_PER_MS;
    let mut segments: Vec<(&'static str, f64)> = Vec::new();
    for index in 1..ticks.len() {
        // 逆戻り（別のキューの時計・カウンタの巻き戻り）は信用できないのでフレームごと捨てる。
        let delta = ticks[index].checked_sub(ticks[index - 1])?;
        let ms = to_ms(delta);
        match segments.iter_mut().find(|(name, _)| *name == labels[index]) {
            Some((_, sum)) => *sum += ms,
            None => segments.push((labels[index], ms)),
        }
    }
    let total = to_ms(ticks[ticks.len() - 1] - ticks[0]);
    if total > MAX_PLAUSIBLE_FRAME_MS {
        return None;
    }
    Some((segments, total))
}

/// CPU 側で測った 1 フレームの値（フレームループの既存の計測値をそのまま渡す）。
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct CpuFrameSample {
    /// フレーム全体（フレームの頭から提示の直後まで）[ms]。
    pub frame_ms: f64,
    /// 描画先の取得待ち（get_current_texture。[PERF] の bf）[ms]。
    pub acquire_ms: f64,
    /// 提出と提示（submit + present。[PERF] の finish）[ms]。
    pub present_ms: f64,
}

/// 一定時間ぶんの集計。
#[derive(Debug, Clone, Default, PartialEq)]
pub struct TimingAccumulator {
    /// CPU の計測を受け取ったフレーム数。
    cpu_frames: u32,
    /// CPU の値の合計。
    cpu_sum: CpuFrameSample,
    /// GPU の計測を集計できたフレーム数。
    gpu_frames: u32,
    /// GPU の計測を捨てたフレーム数（読めないタイムスタンプ）。
    gpu_rejected: u32,
    /// GPU の合計時間の合計 [ms]。
    gpu_total_sum: f64,
    /// 区間ごとの合計 [ms]（初出の順）。
    segments: Vec<(&'static str, f64)>,
}

impl TimingAccumulator {
    /// CPU の 1 フレームを足す。
    pub fn add_cpu_frame(&mut self, sample: CpuFrameSample) {
        self.cpu_frames += 1;
        self.cpu_sum.frame_ms += sample.frame_ms;
        self.cpu_sum.acquire_ms += sample.acquire_ms;
        self.cpu_sum.present_ms += sample.present_ms;
    }

    /// GPU の 1 フレーム（タイムスタンプの並び）を足す。読めないフレームは数だけ数えて捨てる。
    pub fn add_gpu_frame(&mut self, labels: &[&'static str], ticks: &[u64], period_ns: f32) {
        let Some((segments, total)) = segment_durations(labels, ticks, period_ns) else {
            self.gpu_rejected += 1;
            return;
        };
        self.gpu_frames += 1;
        self.gpu_total_sum += total;
        for (name, ms) in segments {
            match self.segments.iter_mut().find(|(n, _)| *n == name) {
                Some((_, sum)) => *sum += ms,
                None => self.segments.push((name, ms)),
            }
        }
    }

    /// 何も集計していないか。
    pub fn is_empty(&self) -> bool {
        self.cpu_frames == 0 && self.gpu_frames == 0 && self.gpu_rejected == 0
    }

    /// 平均を 1 行にする（`[SEED GPU]` の後ろ）。区間は `order` の順に並べ、`order` に無い区間は後ろへ足す。
    ///
    /// 例: `3.0s cpu_frames=57 gpu_frames=55 | cpu frame=52.63 acquire=0.41 present=36.10 |
    ///      gpu total=49.80 shadow=2.10 gbuffer=15.00 …`（単位はすべて ms）
    pub fn format(&self, elapsed_secs: f64, order: &[&'static str]) -> String {
        let avg = |sum: f64, count: u32| if count > 0 { sum / f64::from(count) } else { 0.0 };
        let mut line = format!(
            "{elapsed_secs:.1}s cpu_frames={} gpu_frames={} rejected={} | cpu frame={:.2} acquire={:.2} present={:.2} | gpu total={:.2}",
            self.cpu_frames,
            self.gpu_frames,
            self.gpu_rejected,
            avg(self.cpu_sum.frame_ms, self.cpu_frames),
            avg(self.cpu_sum.acquire_ms, self.cpu_frames),
            avg(self.cpu_sum.present_ms, self.cpu_frames),
            avg(self.gpu_total_sum, self.gpu_frames),
        );
        let mut push = |name: &str, sum: f64| {
            line.push_str(&format!(" {name}={:.2}", avg(sum, self.gpu_frames)));
        };
        for name in order {
            let sum = self.segments.iter().find(|(n, _)| n == name).map_or(0.0, |(_, s)| *s);
            push(name, sum);
        }
        for (name, sum) in &self.segments {
            if !order.contains(name) {
                push(name, *sum);
            }
        }
        line
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 区間は前のタイムスタンプとの差。同名は足し合わせ、周期（ns/カウンタ）を掛ける。
    #[test]
    fn segments_are_differences_and_same_names_add_up() {
        let labels = [BEGIN, "shadow", "forward", "water", "forward", "present"];
        // 周期 1 ns・1 ms = 1_000_000 カウンタ。
        let ticks = [0, 2_000_000, 5_000_000, 6_000_000, 8_000_000, 9_000_000];
        let (segments, total) = segment_durations(&labels, &ticks, 1.0).unwrap();
        assert_eq!(total, 9.0);
        assert_eq!(
            segments,
            vec![("shadow", 2.0), ("forward", 5.0), ("water", 1.0), ("present", 1.0)]
        );
        // 周期 2 ns なら 2 倍。
        let (_, total) = segment_durations(&labels, &ticks, 2.0).unwrap();
        assert_eq!(total, 18.0);
    }

    /// 読めないフレーム（逆戻り・長さ不一致・周期が 0・長すぎる）は捨てる。
    #[test]
    fn implausible_frames_are_rejected() {
        assert!(segment_durations(&[BEGIN, "a"], &[10, 5], 1.0).is_none(), "逆戻り");
        assert!(segment_durations(&[BEGIN, "a"], &[0], 1.0).is_none(), "長さ不一致");
        assert!(segment_durations(&[BEGIN], &[0], 1.0).is_none(), "区間が無い");
        assert!(segment_durations(&[BEGIN, "a"], &[0, 1], 0.0).is_none(), "周期 0");
        assert!(segment_durations(&[BEGIN, "a"], &[0, 2_000_000_000], 1.0).is_none(), "2 秒は長すぎる");
    }

    /// 集計: 平均を出し、order の順に並べ、order に無い区間は後ろへ。捨てたフレームは数える。
    #[test]
    fn accumulator_formats_averages_in_order() {
        let mut acc = TimingAccumulator::default();
        assert!(acc.is_empty());
        acc.add_cpu_frame(CpuFrameSample { frame_ms: 50.0, acquire_ms: 1.0, present_ms: 30.0 });
        acc.add_cpu_frame(CpuFrameSample { frame_ms: 30.0, acquire_ms: 3.0, present_ms: 10.0 });
        acc.add_gpu_frame(&[BEGIN, "gbuffer", "extra"], &[0, 10_000_000, 12_000_000], 1.0);
        acc.add_gpu_frame(&[BEGIN, "gbuffer", "extra"], &[0, 20_000_000, 22_000_000], 1.0);
        acc.add_gpu_frame(&[BEGIN, "gbuffer"], &[5, 1], 1.0);
        let line = acc.format(2.0, &["shadow", "gbuffer"]);
        assert!(line.starts_with("2.0s cpu_frames=2 gpu_frames=2 rejected=1 |"), "{line}");
        assert!(line.contains("cpu frame=40.00 acquire=2.00 present=20.00"), "{line}");
        assert!(line.contains("gpu total=17.00 shadow=0.00 gbuffer=15.00 extra=2.00"), "{line}");
    }
}
