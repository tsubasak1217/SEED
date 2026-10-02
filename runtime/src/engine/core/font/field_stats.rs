// ============================================================
//  font/field_stats.rs — MTSDF を焼いた数・時間・検査（安全弁）に落ちた字の記録・アトラスのページ数と使用率のログ
//
//  【ログ】
//  - 検査に落ちて真の SDF で描く字は、焼いたときに必ず 1 行出す（字・食い違いの画素・これまでの数と例）。
//  - 環境変数 SEED_FONT_FIELD_LOG=1 なら、焼いた字ごとに大きさ・辺の数・各段の時間・検査の結果を 1 行ずつ出す（計測用）。
//    まとめて焼いた回ごとに、アトラスのページ数・使用率の 1 行も出す（mod.rs の bake_and_insert・atlas.rs の usage_line）。
//  - アトラスにページを足したとき・上限まで満杯になったときは atlas.rs が必ず 1 行出す（2026-10-03）。
//  集計の 1 行（`summary_line`）にはアトラスのページ数・字数・上限に対する使用率も入る。
// ============================================================

use std::time::Duration;

use super::field_settings::LOG_TAG;
use super::msdf::BakeStats;

/// 例として覚える落ちた字の数（ログの長さを抑える）。
const MAX_FALLBACK_EXAMPLES: usize = 24;
/// 細い線で em を上げた字を 1 字ずつログに出す数（これを超えたら `THIN_RAISE_LOG_EVERY` 字ごとの集計の 1 行だけ。細字の書体で行が溢れないように）。
const MAX_THIN_RAISE_LOG_LINES: usize = 24;
/// 細い線で em を上げた字の集計の行を出す間隔（字数）。
const THIN_RAISE_LOG_EVERY: usize = 100;
/// ミリ秒への換算。
const MS_PER_SEC: f64 = 1000.0;

/// アトラスの状態の写し（焼いて入れた後に FontSystem が写す。集計の 1 行に出す）。
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct AtlasStatsSnapshot {
    /// 今あるページの数。
    pub pages: u32,
    /// ページの上限。
    pub max_pages: u32,
    /// 入っている字の数。
    pub glyphs: usize,
    /// 上限のページ数に対する使った高さの割合（0..1）。
    pub used_fraction_of_limit: f32,
}

/// 百分率への換算。
const PERCENT: f32 = 100.0;

/// MTSDF を焼いた記録（FontSystem ごと）。
#[derive(Debug, Default)]
pub struct FieldBakeStats {
    /// 焼いた字の数。
    pub baked: usize,
    /// 検査に落ちて真の SDF で描く字の数。
    pub fallback: usize,
    /// そのうち、アルファも落ちてラスタからの真の SDF で作り直した字の数（2026-10-03）。
    pub raster_fallback: usize,
    /// 細い線が消えるので em を上げて焼き直した字の数（2026-10-03。レビュー #5）。
    pub thin_raised: usize,
    /// 焼いた距離場の面積の合計（テクセル²。アトラスの使い方の目安）と、そのうち em を上げて増えた分。
    pub field_texels: usize,
    pub thin_raised_extra_texels: usize,
    /// 焼いた em の合計（平均の em の計算用）。
    pub em_sum: f64,
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
    /// アトラスの最後の状態（ページ数・字数・使用率）。
    pub atlas: AtlasStatsSnapshot,
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
        self.field_texels += s.width * s.height;
        self.em_sum += f64::from(s.em_px);
        if let Some(from) = s.thin_raised_from_em {
            self.thin_raised += 1;
            self.thin_raised_extra_texels += s.thin_raised_extra_texels;
            // em を上げた字はログに出す（アトラスの容量への影響が分かるように。最初の数字は 1 字ずつ、その後は一定の字数ごとの集計）
            if self.thin_raised <= MAX_THIN_RAISE_LOG_LINES {
                eprintln!(
                    "{LOG_TAG} 細い線が消えるので em を上げて焼き直しました: '{ch}' U+{:04X}（em {from} → {}・消えた画素 {}・距離場 {}x{}）。これまでに {} 字（面積 +{} テクセル²）",
                    ch as u32,
                    s.em_px,
                    s.verify.vanished_px,
                    s.width,
                    s.height,
                    self.thin_raised,
                    self.thin_raised_extra_texels
                );
            } else if self.thin_raised % THIN_RAISE_LOG_EVERY == 0 {
                eprintln!(
                    "{LOG_TAG} 細い線で em を上げた字がこれまでに {} 字（距離場の面積 +{} テクセル²。焼いた全体の {:.1}%）",
                    self.thin_raised,
                    self.thin_raised_extra_texels,
                    self.thin_raised_extra_texels as f32 / self.field_texels.max(1) as f32 * PERCENT
                );
            }
        }
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
            // アルファも落ちてラスタからの真の SDF で作り直した字（最後の落ち先。bake.rs の apply_safety_valve）
            let raster = match (s.raster_fallback, s.raster_verify) {
                (true, Some(r)) => format!("。アルファも落ちたのでラスタからの真の SDF で作り直しました（{} 画素）", r.artifact_px),
                _ => String::new(),
            };
            if s.raster_fallback {
                self.raster_fallback += 1;
            }
            eprintln!(
                "{LOG_TAG} MTSDF の検査に落ちた字を真の SDF で描きます: '{ch}' U+{:04X}（食い違い {} 画素 > {}。真の SDF では {alpha}{raster}）。これまでに {} 字（例: {}）",
                ch as u32,
                s.verify.artifact_px,
                s.verify.threshold,
                self.fallback,
                self.examples_text()
            );
        }
        if self.log_each {
            eprintln!(
                "{LOG_TAG} mtsdf '{ch}' U+{:04X} em {} {}x{} 輪郭 {} 辺 {} 時間 {:.2} ms（輪郭 {:.2} / 距離 {:.2} / 補正 {:.2} / 検査 {:.2}） 補正 {} テクセル 検査 {}/{} 消えた画素 {}{}",
                ch as u32,
                s.em_px,
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
                s.verify.vanished_px,
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

    /// アトラスの状態を写す（焼いて入れた後に毎回）。
    pub fn record_atlas(&mut self, atlas: AtlasStatsSnapshot) {
        self.atlas = atlas;
    }

    /// 字ごとのログを出すか（SEED_FONT_FIELD_LOG=1）。
    pub fn log_each(&self) -> bool {
        self.log_each
    }

    /// 落ちた字の例（空白で区切る）。
    pub fn examples_text(&self) -> String {
        self.fallback_examples.iter().map(|c| c.to_string()).collect::<Vec<_>>().join(" ")
    }

    /// 集計の 1 行（ログ・報告用）。
    pub fn summary_line(&self) -> String {
        let avg = if self.baked > 0 { ms(self.total_time) / self.baked as f64 } else { 0.0 };
        format!(
            "MTSDF 焼いた字 {} 平均 {:.2} ms 最大 {:.2} ms（{}） まとめて焼いた最長 {:.2} ms（{} 字） 検査に落ちた字 {}（うちラスタから作り直し {}。例: {}） 細い線で em を上げた字 {}（距離場の面積 +{:.1}%）平均の em {:.1} アトラス {}/{} ページ・{} 字・上限に対する使用率 {:.1}%",
            self.baked,
            avg,
            ms(self.max_time),
            self.max_char.map(|c| c.to_string()).unwrap_or_default(),
            ms(self.max_batch_time),
            self.max_batch_count,
            self.fallback,
            self.raster_fallback,
            self.examples_text(),
            self.thin_raised,
            if self.field_texels > 0 { self.thin_raised_extra_texels as f32 / self.field_texels as f32 * PERCENT } else { 0.0 },
            if self.baked > 0 { self.em_sum / self.baked as f64 } else { 0.0 },
            self.atlas.pages,
            self.atlas.max_pages,
            self.atlas.glyphs,
            self.atlas.used_fraction_of_limit * PERCENT
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
        let ok = BakeStats { time_distance: Duration::from_millis(2), verify: VerifyReport { artifact_px: 0, threshold: 8, sampled_px: 100, detail_px: 0, vanished_px: 0 }, ..Default::default() };
        let bad = BakeStats { time_distance: Duration::from_millis(5), fallback: true, verify: VerifyReport { artifact_px: 30, threshold: 8, sampled_px: 100, detail_px: 0, vanished_px: 0 }, ..Default::default() };
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
        // 細い線で em を上げた字は数と面積の増分に残る
        let raised = BakeStats { em_px: 64.0, width: 10, height: 10, thin_raised_from_em: Some(40.0), thin_raised_extra_texels: 60, ..Default::default() };
        st.record('一', &raised);
        assert_eq!((st.thin_raised, st.thin_raised_extra_texels, st.field_texels), (1, 60, 100));
        assert!(st.summary_line().contains("細い線で em を上げた字 1（距離場の面積 +60.0%）"), "{}", st.summary_line());
        // アトラスの状態は最後に写したものが集計に出る
        st.record_atlas(AtlasStatsSnapshot { pages: 2, max_pages: 4, glyphs: 3100, used_fraction_of_limit: 0.375 });
        let line = st.summary_line();
        assert!(line.contains("アトラス 2/4 ページ・3100 字・上限に対する使用率 37.5%"), "{line}");
    }
}
