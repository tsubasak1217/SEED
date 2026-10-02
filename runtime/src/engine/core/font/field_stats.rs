// ============================================================
//  font/field_stats.rs — MTSDF を焼いた数・時間・検査（安全弁）に落ちた字の記録とログ
//
//  【ログ】
//  - 検査に落ちて真の SDF で描く字は、焼いたときに必ず 1 行出す（字・食い違いの画素・これまでの数と例）。
//  - 環境変数 SEED_FONT_FIELD_LOG=1 なら、焼いた字ごとに大きさ・辺の数・各段の時間・検査の結果を 1 行ずつ出す（計測用）。
// ============================================================

use std::time::Duration;

use super::field_settings::LOG_TAG;
use super::msdf::BakeStats;

/// 例として覚える落ちた字の数（ログの長さを抑える）。
const MAX_FALLBACK_EXAMPLES: usize = 24;
/// ミリ秒への換算。
const MS_PER_SEC: f64 = 1000.0;

/// MTSDF を焼いた記録（FontSystem ごと）。
#[derive(Debug, Default)]
pub struct FieldBakeStats {
    /// 焼いた字の数。
    pub baked: usize,
    /// 検査に落ちて真の SDF で描く字の数。
    pub fallback: usize,
    /// 落ちた字の例（最初の MAX_FALLBACK_EXAMPLES 字）。
    pub fallback_examples: Vec<char>,
    /// 焼いた時間の合計・最大（とその字）。
    pub total_time: Duration,
    pub max_time: Duration,
    pub max_char: Option<char>,
    /// まとめて焼いた回数と、いちばん長かった 1 回の時間・字数。
    pub batches: usize,
    pub max_batch_time: Duration,
    pub max_batch_count: usize,
    /// 字ごとにログを出すか。
    log_each: bool,
}

impl FieldBakeStats {
    /// 記録を作る（`log_each` は SEED_FONT_FIELD_LOG=1 のとき真）。
    pub fn new(log_each: bool) -> Self {
        Self { log_each, ..Default::default() }
    }

    /// 1 字ぶんを記録し、必要ならログを出す。
    pub fn record(&mut self, ch: char, s: &BakeStats) {
        self.baked += 1;
        let total = s.total();
        self.total_time += total;
        if total > self.max_time {
            self.max_time = total;
            self.max_char = Some(ch);
        }
        if s.fallback {
            self.fallback += 1;
            if self.fallback_examples.len() < MAX_FALLBACK_EXAMPLES {
                self.fallback_examples.push(ch);
            }
            let alpha = s.alpha_verify.map(|a| format!("{} 画素", a.artifact_px)).unwrap_or_else(|| "-".to_string());
            eprintln!(
                "{LOG_TAG} MTSDF の検査に落ちた字を真の SDF で描きます: '{ch}' U+{:04X}（食い違い {} 画素 > {}。真の SDF では {alpha}）。これまでに {} 字（例: {}）",
                ch as u32,
                s.verify.artifact_px,
                s.verify.threshold,
                self.fallback,
                self.examples_text()
            );
        }
        if self.log_each {
            eprintln!(
                "{LOG_TAG} mtsdf '{ch}' U+{:04X} {}x{} 輪郭 {} 辺 {} 時間 {:.2} ms（輪郭 {:.2} / 距離 {:.2} / 補正 {:.2} / 検査 {:.2}） 補正 {} テクセル 検査 {}/{}{}",
                ch as u32,
                s.width,
                s.height,
                s.contours,
                s.edges,
                ms(total),
                ms(s.time_outline),
                ms(s.time_distance),
                ms(s.time_correct),
                ms(s.time_verify),
                s.correction.corrected,
                s.verify.artifact_px,
                s.verify.threshold,
                if s.fallback { " → 真の SDF" } else { "" }
            );
        }
    }

    /// まとめて焼いた 1 回を記録する（字ごとのログが有効なときだけ 1 行出す）。
    ///
    /// `count` は焼いた字数（輪郭の無い字も含む）、`elapsed` は壁時計の時間（並列に焼いたときは全体の時間）。
    pub fn record_batch(&mut self, count: usize, elapsed: Duration, kind: &str) {
        self.batches += 1;
        if elapsed > self.max_batch_time {
            self.max_batch_time = elapsed;
            self.max_batch_count = count;
        }
        if self.log_each {
            eprintln!("{LOG_TAG} {kind} まとめて焼いた {count} 字 {:.2} ms（{} 回目）", ms(elapsed), self.batches);
        }
    }

    /// 落ちた字の例（空白で区切る）。
    pub fn examples_text(&self) -> String {
        self.fallback_examples.iter().map(|c| c.to_string()).collect::<Vec<_>>().join(" ")
    }

    /// 集計の 1 行（ログ・報告用）。
    pub fn summary_line(&self) -> String {
        let avg = if self.baked > 0 { ms(self.total_time) / self.baked as f64 } else { 0.0 };
        format!(
            "MTSDF 焼いた字 {} 平均 {:.2} ms 最大 {:.2} ms（{}） まとめて焼いた最長 {:.2} ms（{} 字） 検査に落ちた字 {}（例: {}）",
            self.baked,
            avg,
            ms(self.max_time),
            self.max_char.map(|c| c.to_string()).unwrap_or_default(),
            ms(self.max_batch_time),
            self.max_batch_count,
            self.fallback,
            self.examples_text()
        )
    }
}

/// 時間 → ミリ秒。
fn ms(d: Duration) -> f64 {
    d.as_secs_f64() * MS_PER_SEC
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::font::msdf::verify::VerifyReport;

    /// 落ちた字は数と例に残り、合計・最大の時間が積み上がる。
    #[test]
    fn records_fallbacks_and_times() {
        let mut st = FieldBakeStats::new(false);
        let ok = BakeStats { time_distance: Duration::from_millis(2), verify: VerifyReport { artifact_px: 0, threshold: 8, sampled_px: 100, detail_px: 0 }, ..Default::default() };
        let bad = BakeStats { time_distance: Duration::from_millis(5), fallback: true, verify: VerifyReport { artifact_px: 30, threshold: 8, sampled_px: 100, detail_px: 0 }, ..Default::default() };
        st.record('あ', &ok);
        st.record('鬱', &bad);
        st.record_batch(2, Duration::from_millis(6), "mtsdf");
        assert_eq!((st.batches, st.max_batch_count), (1, 2));
        assert_eq!(st.baked, 2);
        assert_eq!(st.fallback, 1);
        assert_eq!(st.fallback_examples, vec!['鬱']);
        assert_eq!(st.max_char, Some('鬱'));
        assert_eq!(st.total_time, Duration::from_millis(7));
        assert!(st.summary_line().contains("検査に落ちた字 1"));
    }
}
