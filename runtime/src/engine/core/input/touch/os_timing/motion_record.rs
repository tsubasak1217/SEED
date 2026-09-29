// ============================================================
//  touch/os_timing/motion_record.rs — Java から届いた MotionEvent 1 つ分の並び（配列）を、指ごとの控えへほどく
//
//  【並びの形】（Java の `com.seedengine.runtime.input.TouchTimeline` が作り、native の JNI が配列を読んで渡す）
//    phase_code    … 段階の番号（stamp.rs の OS_PHASE_*）
//    pointer_count … 指の数 n（Started / Ended は action の指 1 本、Moved / Cancelled は添字の順に全部の指。GameActivity の glue の上限 8 まで）
//    history_size  … 履歴の標本の数 H（Moved だけ。他は 0）
//    ids           … 指の ID（n 個。MotionEvent の getPointerId）
//    coords        … 位置（(H + 1) × n × 2 個。標本の順〈履歴の古い順 → 最後が今〉に、指の順に x, y）
//    times_ns      … 時刻（H + 1 個。CLOCK_MONOTONIC の ns。履歴の古い順 → 最後が今の eventTime）
//  winit と同じ並び（指の順）の控えを返すので、箱へ積んだ順と winit の Touch の順がそろう。
//  ここは配列を読むだけの純関数（時刻の換算は clock.rs の ClockAnchor）。長さが合わない並びは積まない（Err）。
// ============================================================

use super::clock::ClockAnchor;
use super::stamp::{phase_from_code, OsTouchSample, OsTouchStamp};

use winit::event::TouchPhase;

/// 1 つの MotionEvent で控える指の数の上限。
/// 出典: GameActivity の glue（android-games-sdk の GameActivityEvents.h の GAMEACTIVITY_MAX_NUM_POINTERS_IN_MOTION_EVENT = 8）。
/// glue はこれより多い指を捨てるので winit の Touch にもならない（Java 側も同じ上限で控える）。
pub const MAX_POINTERS_PER_EVENT: usize = 8;

/// 1 件の控えに残す履歴の標本の数の上限（新しい方を残す）。
/// 240 Hz の走査でも 64 標本 ≈ 267 ms で、速度の推定の窓（100 ms）と標本の数（20）を十分に覆う。
/// UI スレッドが長く止まって履歴が溜まったときに、1 回の処理を重くしないための上限。
pub const MAX_HISTORY_SAMPLES: usize = 64;

/// x, y の 2 つ。
const COORDS_PER_POINT: usize = 2;

/// 並びが読めなかった理由。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MotionRecordError {
    /// 知らない段階の番号（Java と native の版の食い違い）。
    UnknownPhase(i32),
    /// 指の数が 1〜上限の外。
    BadPointerCount(i32),
    /// 履歴の数が負。
    BadHistorySize(i32),
    /// 配列の長さが足りない。
    ShortArray,
}

/// Java から届いた MotionEvent 1 つ分の並び（配列を借りる）。
#[derive(Clone, Copy, Debug)]
pub struct OsMotionRecord<'a> {
    /// 段階の番号（stamp.rs の OS_PHASE_*）。
    pub phase_code: i32,
    /// 指の数。
    pub pointer_count: i32,
    /// 履歴の標本の数（Moved だけ）。
    pub history_size: i32,
    /// 指の ID（pointer_count 個以上）。
    pub ids: &'a [i32],
    /// 位置（(history_size + 1) × pointer_count × 2 個以上）。
    pub coords: &'a [f32],
    /// 時刻（CLOCK_MONOTONIC の ns。history_size + 1 個以上）。
    pub times_ns: &'a [i64],
}

impl OsMotionRecord<'_> {
    /// 指ごとの控えへほどく【純関数】（指の順＝winit の Touch の順）。
    ///
    /// # 引数
    /// * `anchor` - 時刻の換算の対応（native が受け取った瞬間に読んだもの）
    pub fn to_stamps(&self, anchor: &ClockAnchor) -> Result<Vec<OsTouchStamp>, MotionRecordError> {
        let phase = phase_from_code(self.phase_code).ok_or(MotionRecordError::UnknownPhase(self.phase_code))?;
        let count = usize::try_from(self.pointer_count)
            .ok()
            .filter(|n| (1..=MAX_POINTERS_PER_EVENT).contains(n))
            .ok_or(MotionRecordError::BadPointerCount(self.pointer_count))?;
        let history = usize::try_from(self.history_size).map_err(|_| MotionRecordError::BadHistorySize(self.history_size))?;
        let samples = history + 1;
        // 必要な位置の数（大きすぎる履歴の数で桁あふれしない形）
        let coords_needed = samples.checked_mul(count).and_then(|v| v.checked_mul(COORDS_PER_POINT));
        if self.ids.len() < count || self.times_ns.len() < samples || coords_needed.is_none_or(|need| self.coords.len() < need) {
            return Err(MotionRecordError::ShortArray);
        }
        // 履歴は Moved だけ（他の段階に履歴が来ても今の標本だけを使う）。多すぎれば新しい方を残す
        let kept_from = if phase == TouchPhase::Moved { history.saturating_sub(MAX_HISTORY_SAMPLES) } else { history };
        let sample_at = |s: usize, k: usize| {
            let base = (s * count + k) * COORDS_PER_POINT;
            OsTouchSample {
                position: [self.coords[base], self.coords[base + 1]],
                time: anchor.instant_of(self.times_ns[s]),
                monotonic_ns: self.times_ns[s],
            }
        };
        Ok((0..count)
            .map(|k| OsTouchStamp {
                // winit と同じ変換（pointer_id() as u64。負の ID は符号拡張になる）
                pointer_id: self.ids[k] as u64,
                phase,
                current: sample_at(history, k),
                history: (kept_from..history).map(|s| sample_at(s, k)).collect(),
            })
            .collect())
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use super::super::stamp::{OS_PHASE_CANCELLED, OS_PHASE_MOVED, OS_PHASE_STARTED};
    use std::time::{Duration, Instant};

    /// 対応の時点（ns = 1,000,000,000）。
    fn anchor() -> ClockAnchor {
        ClockAnchor { instant: Instant::now(), monotonic_ns: 1_000_000_000 }
    }

    /// Moved・2 本の指・履歴 2: 指の順に控えができ、履歴は古い順、時刻は ns の差だけ前。
    #[test]
    fn moved_with_history_for_two_pointers() {
        let a = anchor();
        // 標本 0（履歴）・1（履歴）・2（今）× 指 2 本 × (x, y)
        let coords = [1.0, 2.0, 11.0, 12.0, 3.0, 4.0, 13.0, 14.0, 5.0, 6.0, 15.0, 16.0];
        let times = [a.monotonic_ns - 16_000_000, a.monotonic_ns - 8_000_000, a.monotonic_ns - 1_000_000];
        let r = OsMotionRecord { phase_code: OS_PHASE_MOVED, pointer_count: 2, history_size: 2, ids: &[0, 3], coords: &coords, times_ns: &times };
        let s = r.to_stamps(&a).unwrap();
        assert_eq!(s.len(), 2);
        assert_eq!((s[0].pointer_id, s[1].pointer_id), (0, 3));
        assert_eq!(s[0].current.position, [5.0, 6.0]);
        assert_eq!(s[1].current.position, [15.0, 16.0]);
        assert_eq!(s[1].history.iter().map(|h| h.position).collect::<Vec<_>>(), vec![[11.0, 12.0], [13.0, 14.0]]);
        assert_eq!(a.instant.duration_since(s[0].current.time), Duration::from_millis(1));
        assert_eq!(a.instant.duration_since(s[0].history[0].time), Duration::from_millis(16));
        assert_eq!(s[0].current.monotonic_ns, times[2]);
    }

    /// Started は 1 本・履歴なし。Cancelled に履歴が来ても今の標本だけ。
    #[test]
    fn started_and_cancelled_have_no_history() {
        let a = anchor();
        let r = OsMotionRecord { phase_code: OS_PHASE_STARTED, pointer_count: 1, history_size: 0, ids: &[7], coords: &[9.0, 8.0], times_ns: &[a.monotonic_ns] };
        let s = r.to_stamps(&a).unwrap();
        assert_eq!((s[0].pointer_id, s[0].phase, s[0].history.len()), (7, TouchPhase::Started, 0));
        let r = OsMotionRecord { phase_code: OS_PHASE_CANCELLED, pointer_count: 1, history_size: 1, ids: &[7], coords: &[1.0, 1.0, 2.0, 2.0], times_ns: &[1, 2] };
        let s = r.to_stamps(&a).unwrap();
        assert_eq!((s[0].current.position, s[0].history.len()), ([2.0, 2.0], 0));
    }

    /// 壊れた並びは積まない（知らない段階・指の数・履歴の数・短い配列）。
    #[test]
    fn rejects_malformed_records() {
        let a = anchor();
        let ok = OsMotionRecord { phase_code: OS_PHASE_MOVED, pointer_count: 1, history_size: 0, ids: &[0], coords: &[0.0, 0.0], times_ns: &[0] };
        assert!(ok.to_stamps(&a).is_ok());
        assert_eq!(OsMotionRecord { phase_code: 9, ..ok }.to_stamps(&a), Err(MotionRecordError::UnknownPhase(9)));
        assert_eq!(OsMotionRecord { pointer_count: 0, ..ok }.to_stamps(&a), Err(MotionRecordError::BadPointerCount(0)));
        assert_eq!(OsMotionRecord { pointer_count: 9, ..ok }.to_stamps(&a), Err(MotionRecordError::BadPointerCount(9)));
        assert_eq!(OsMotionRecord { history_size: -1, ..ok }.to_stamps(&a), Err(MotionRecordError::BadHistorySize(-1)));
        assert_eq!(OsMotionRecord { history_size: 1, ..ok }.to_stamps(&a), Err(MotionRecordError::ShortArray));
        assert_eq!(OsMotionRecord { pointer_count: 2, ..ok }.to_stamps(&a), Err(MotionRecordError::ShortArray));
    }

    /// 履歴が上限を超えたら新しい方を残す。
    #[test]
    fn history_is_capped_to_the_newest() {
        let a = anchor();
        let history = MAX_HISTORY_SAMPLES + 3;
        let coords: Vec<f32> = (0..=history).flat_map(|s| [s as f32, 0.0]).collect();
        let times: Vec<i64> = (0..=history).map(|s| s as i64).collect();
        let r = OsMotionRecord {
            phase_code: OS_PHASE_MOVED,
            pointer_count: 1,
            history_size: history as i32,
            ids: &[0],
            coords: &coords,
            times_ns: &times,
        };
        let s = r.to_stamps(&a).unwrap();
        assert_eq!(s[0].history.len(), MAX_HISTORY_SAMPLES);
        assert_eq!(s[0].history[0].position[0], 3.0, "古い 3 つを捨てた");
        assert_eq!(s[0].current.position[0], history as f32);
    }
}
