// ============================================================
//  touch/os_timing/diag.rs — 実機の確かめ用のログ `[SEED TOUCH TIME]`（指が触れた・離れたときだけ 1 行）
//
//  【出す条件】Input::process_touch が `platform::CURRENT.lifecycle_diag_log` が真（Android）のときだけ呼ぶ。PC では何もしない。
//  指ごとの数（履歴の標本の数・控えが一致した Moved の数）は、lifecycle_diag.rs と同じく診断専用の値を Input の状態へ
//  混ぜないため、このモジュールの静的な表で持つ。
//
//  【行の形】
//    [SEED TOUCH TIME] id=<指の ID> Started|Ended matched=yes|no ev_ns=<控えの ns|-> rec_ns=<記録の時刻の ns|->
//      lag_ms=<受け取った時刻 − 控えの時刻|-> hist=<その指の履歴の標本の数> matched_moves=<一致した Moved>/<Moved>
//  - ev_ns … Java が送った MotionEvent の時刻（CLOCK_MONOTONIC の ns。Java のログ `[SEED TOUCH TIME] java … ev_ns=` と同じ値）
//  - rec_ns … ジェスチャーの記録（pointer_log）に使った時刻を CLOCK_MONOTONIC の ns へ戻した値。控えが一致していれば ev_ns と
//    1 µs 以内で一致する（「記録の時刻が MotionEvent の時刻」の確かめ）。往復の誤差: native の換算の対応（clock.rs の capture。
//    2 回の読みの間の半分以下＝ふつう数十 ns）＋ ここで読み直す対応（同じ）＋ 秒の f64 の丸め（起点から 1 日で 0.02 ns 程度）＋
//    Duration の ns の切り捨て（1 ns 未満）。指ごとの逆行の切り上げ（gesture/time_floor.rs）が効いたときは rec_ns が大きくなる
//  - lag_ms … 受け取った時刻（winit の Touch がエンジンへ届いた時刻）− MotionEvent の時刻。従来はこの分だけ遅い時刻で記録していた
//  - hist・matched_moves … Started からその Ended までの数（Started の行は 0）
// ============================================================

use std::sync::Mutex;
use std::time::Instant;

use winit::event::TouchPhase;

use super::clock::instant_to_monotonic_ns;
use super::stamp::OsTouchStamp;
use crate::engine::core::input::gesture::pointer_log::pointer_clock_instant;

/// ログの行の頭（Java のログは `[SEED TOUCH TIME] java`）。
pub const TOUCH_TIME_LOG_TAG: &str = "[SEED TOUCH TIME]";

/// 決められない値の表記。
const NONE_MARK: &str = "-";

/// 1 ms の ns 数（lag_ms の換算）。
const NANOS_PER_MILLI: f64 = 1_000_000.0;

/// 表に覚える指の数の上限（超えたら表を空にして数え直す。離れが届かなかった指が溜まらないように。
/// Android の pointer id は 0〜31 なので、ふつうは超えない）。
const MAX_TRACKED_FINGERS: usize = 64;

/// 指ごとの数（Started から数える）。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct FingerTimingStats {
    /// 記録した履歴の標本の数。
    pub history_samples: usize,
    /// Moved の数。
    pub moves: usize,
    /// 控えが一致した Moved の数。
    pub matched_moves: usize,
}

/// 指ごとの数の表【純ロジック】。
#[derive(Debug, Default)]
pub struct FingerTimingTable {
    /// (指の ID, 数)。
    per_finger: Vec<(u64, FingerTimingStats)>,
}

impl FingerTimingTable {
    /// 空の表（静的な表のために const）。
    pub const fn new() -> Self {
        Self { per_finger: Vec::new() }
    }

    /// Touch 1 件を数える。行を出すべき段階（Started・Ended）なら、その時点の数を返す。
    ///
    /// # 引数
    /// * `id`              - 指の ID
    /// * `phase`           - 段階
    /// * `matched`         - 控えが一致したか
    /// * `history_samples` - 記録した履歴の標本の数
    pub fn observe(&mut self, id: u64, phase: TouchPhase, matched: bool, history_samples: usize) -> Option<FingerTimingStats> {
        let index = self.per_finger.iter().position(|(key, _)| *key == id);
        match phase {
            TouchPhase::Started => {
                if let Some(i) = index {
                    self.per_finger.swap_remove(i);
                }
                if self.per_finger.len() >= MAX_TRACKED_FINGERS {
                    self.per_finger.clear();
                }
                self.per_finger.push((id, FingerTimingStats::default()));
                Some(FingerTimingStats::default())
            }
            TouchPhase::Moved => {
                if let Some(i) = index {
                    let stats = &mut self.per_finger[i].1;
                    stats.moves += 1;
                    stats.matched_moves += usize::from(matched);
                    stats.history_samples += history_samples;
                }
                None
            }
            TouchPhase::Ended => Some(index.map(|i| self.per_finger.swap_remove(i).1).unwrap_or_default()),
            TouchPhase::Cancelled => {
                if let Some(i) = index {
                    self.per_finger.swap_remove(i);
                }
                None
            }
        }
    }
}

/// 1 行に使う値。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TouchTimeLine {
    /// 指の ID。
    pub id: u64,
    /// 段階（Started か Ended）。
    pub phase: TouchPhase,
    /// 控えの MotionEvent の時刻（ns。一致しなければ None）。
    pub event_ns: Option<i64>,
    /// 記録の時刻（ns。換算できなければ None）。
    pub recorded_ns: Option<i64>,
    /// 受け取った時刻 − 控えの時刻（ms。一致しなければ None）。
    pub lag_ms: Option<f64>,
    /// その指の数。
    pub stats: FingerTimingStats,
}

impl TouchTimeLine {
    /// 1 行にする【純関数】。
    pub fn format(&self) -> String {
        let phase = match self.phase {
            TouchPhase::Started => "Started",
            TouchPhase::Moved => "Moved",
            TouchPhase::Ended => "Ended",
            TouchPhase::Cancelled => "Cancelled",
        };
        let or_none = |v: Option<String>| v.unwrap_or_else(|| NONE_MARK.to_string());
        format!(
            "{TOUCH_TIME_LOG_TAG} id={} {phase} matched={} ev_ns={} rec_ns={} lag_ms={} hist={} matched_moves={}/{}",
            self.id,
            if self.event_ns.is_some() { "yes" } else { "no" },
            or_none(self.event_ns.map(|v| v.to_string())),
            or_none(self.recorded_ns.map(|v| v.to_string())),
            or_none(self.lag_ms.map(|v| format!("{v:.3}"))),
            self.stats.history_samples,
            self.stats.matched_moves,
            self.stats.moves,
        )
    }
}

/// 指ごとの数の表（プロセスで 1 つ。エンジンのスレッドだけが使う）。
static TABLE: Mutex<FingerTimingTable> = Mutex::new(FingerTimingTable::new());

/// Touch 1 件を数え、触れた・離れたときは `[SEED TOUCH TIME]` の行を標準エラー（logcat）へ出す。
/// Input::process_touch が `lifecycle_diag_log` のときだけ呼ぶ。
///
/// # 引数
/// * `id`, `phase`    - winit の Touch
/// * `stamp`          - 一致した控え（無ければ None）
/// * `recorded_secs`  - 記録に使った時刻（pointer_log の時計の秒。記録しなかったら None）
/// * `received`       - 受け取った時刻
pub fn observe(id: u64, phase: TouchPhase, stamp: Option<&OsTouchStamp>, recorded_secs: Option<f64>, received: Instant) {
    let history = stamp.map_or(0, |s| s.history.len());
    let stats = {
        let mut table = TABLE.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        table.observe(id, phase, stamp.is_some(), history)
    };
    let Some(stats) = stats else { return };
    let line = TouchTimeLine {
        id,
        phase,
        event_ns: stamp.map(|s| s.current.monotonic_ns),
        recorded_ns: recorded_secs.and_then(pointer_clock_instant).and_then(instant_to_monotonic_ns),
        lag_ms: stamp.map(|s| received.saturating_duration_since(s.current.time).as_nanos() as f64 / NANOS_PER_MILLI),
        stats,
    };
    eprintln!("{}", line.format());
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// Started で数え直し、Moved を数え、Ended でその数を返して忘れる。Cancelled は行を出さずに忘れる。
    #[test]
    fn counts_between_started_and_ended() {
        let mut t = FingerTimingTable::new();
        assert_eq!(t.observe(0, TouchPhase::Started, true, 0), Some(FingerTimingStats::default()));
        assert_eq!(t.observe(0, TouchPhase::Moved, true, 2), None);
        assert_eq!(t.observe(0, TouchPhase::Moved, false, 0), None);
        assert_eq!(t.observe(1, TouchPhase::Moved, true, 5), None, "触れていない指は数えない");
        assert_eq!(
            t.observe(0, TouchPhase::Ended, true, 0),
            Some(FingerTimingStats { history_samples: 2, moves: 2, matched_moves: 1 })
        );
        assert_eq!(t.observe(0, TouchPhase::Ended, true, 0), Some(FingerTimingStats::default()), "忘れた後は 0");
        t.observe(2, TouchPhase::Started, true, 0);
        assert_eq!(t.observe(2, TouchPhase::Cancelled, true, 0), None);
        assert!(t.per_finger.is_empty());
    }

    /// 行の形（一致した・しなかった）。
    #[test]
    fn line_format() {
        let stats = FingerTimingStats { history_samples: 12, moves: 20, matched_moves: 20 };
        let line = TouchTimeLine {
            id: 0,
            phase: TouchPhase::Ended,
            event_ns: Some(123_456_789),
            recorded_ns: Some(123_456_790),
            lag_ms: Some(4.5),
            stats,
        };
        assert_eq!(
            line.format(),
            "[SEED TOUCH TIME] id=0 Ended matched=yes ev_ns=123456789 rec_ns=123456790 lag_ms=4.500 hist=12 matched_moves=20/20"
        );
        let line = TouchTimeLine { phase: TouchPhase::Started, event_ns: None, recorded_ns: None, lag_ms: None, stats: FingerTimingStats::default(), ..line };
        assert_eq!(line.format(), "[SEED TOUCH TIME] id=0 Started matched=no ev_ns=- rec_ns=- lag_ms=- hist=0 matched_moves=0/0");
    }
}
