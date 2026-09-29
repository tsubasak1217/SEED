// ============================================================
//  touch/os_timing/timeline.rs — OS のタッチの時刻の控えの箱と、winit の Touch との突き合わせ【純ロジック】
//
//  【流れ】
//  Java の UI スレッドが MotionEvent を GameActivity の glue へ渡す**前に**控えを積む（push）。glue がネイティブのスレッドの
//  winit へ入力を渡し、winit が WindowEvent::Touch にする。エンジン（Input::process_touch）は Touch が来るたびに、
//  箱の先頭から「ID・段階・位置の f32 のビット」が一致する最初の控えを探して取り出す（take_matching）。
//  控えは Touch より必ず先に積まれ、積む順と winit の Touch の順は同じ（MotionEvent の順・指の順）。
//
//  【ずれへの備え】
//  - 一致する控えより前の控えは捨てる（winit へ届かなかった分: glue が捨てた・ネイティブが止まっていた・取りこぼし）
//  - 一致する控えが無ければ箱はそのままで None（そのイベントは受け取った時刻に戻る。後の Touch が箱の中の控えに一致すれば
//    そこで前の分がまとめて捨てられる）
//  - 上限（MAX_PENDING_STAMPS）を超えたら古い控えから捨てる（エンジンのスレッドが長く止まったとき）
// ============================================================

use std::collections::VecDeque;

use winit::event::TouchPhase;

use super::stamp::OsTouchStamp;

/// 箱に溜める控えの上限（あふれたら古いものから捨てる）。
/// エンジンのスレッドが止まっている間（シーンの読み込みなど）に UI スレッドが積む分の上限。120 Hz の MotionEvent × 指 2 本 =
/// 毎秒 240 件なので約 4 秒分。あふれた控えのイベントは受け取った時刻に戻るだけで、入力そのものは失わない。
pub const MAX_PENDING_STAMPS: usize = 1024;

/// 控えの箱。
#[derive(Debug, Default)]
pub struct OsTouchTimeline {
    /// 控え（積んだ順＝winit の Touch の順）。
    pending: VecDeque<OsTouchStamp>,
    /// 突き合わせで一致より前にあったので捨てた控えの数（累計。診断用）。
    skipped: u64,
    /// 上限を超えて捨てた控えの数（累計。診断用）。
    overflowed: u64,
}

impl OsTouchTimeline {
    /// 空の箱を作る（静的な箱のために const）。
    pub const fn new() -> Self {
        Self { pending: VecDeque::new(), skipped: 0, overflowed: 0 }
    }

    /// 控えを 1 件積む（上限を超えたら古いものから捨てる）。
    pub fn push(&mut self, stamp: OsTouchStamp) {
        self.pending.push_back(stamp);
        while self.pending.len() > MAX_PENDING_STAMPS {
            self.pending.pop_front();
            self.overflowed += 1;
        }
    }

    /// winit の Touch に一致する最初の控えを取り出す（それより前の控えは捨てる。無ければ箱はそのままで None）。
    ///
    /// # 引数
    /// * `pointer_id` - winit の Touch::id
    /// * `phase`      - winit の Touch::phase
    /// * `x`, `y`     - winit の Touch::location を f32 へ戻した値
    pub fn take_matching(&mut self, pointer_id: u64, phase: TouchPhase, x: f32, y: f32) -> Option<OsTouchStamp> {
        let index = self.pending.iter().position(|s| s.matches(pointer_id, phase, x, y))?;
        self.skipped += index as u64;
        self.pending.drain(..index);
        self.pending.pop_front()
    }

    /// 積んである控えの数。
    pub fn len(&self) -> usize {
        self.pending.len()
    }

    /// 控えが無いか。
    pub fn is_empty(&self) -> bool {
        self.pending.is_empty()
    }

    /// 突き合わせで捨てた控えの数（累計）。
    pub fn skipped(&self) -> u64 {
        self.skipped
    }

    /// 上限を超えて捨てた控えの数（累計）。
    pub fn overflowed(&self) -> u64 {
        self.overflowed
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::input::touch::os_timing::stamp::OsTouchSample;
    use std::time::Instant;

    /// 控え 1 件（時刻の ns は見分けの番号に使う）。
    fn stamp(id: u64, phase: TouchPhase, x: f32, y: f32, ns: i64) -> OsTouchStamp {
        OsTouchStamp {
            pointer_id: id,
            phase,
            current: OsTouchSample { position: [x, y], time: Instant::now(), monotonic_ns: ns },
            history: Vec::new(),
        }
    }

    /// 順どおり: 積んだ順に一致して取り出せる（Started / Moved / Ended / Cancelled の対応）。
    #[test]
    fn matches_in_order() {
        let mut t = OsTouchTimeline::new();
        t.push(stamp(0, TouchPhase::Started, 1.0, 1.0, 1));
        t.push(stamp(0, TouchPhase::Moved, 2.0, 1.0, 2));
        t.push(stamp(0, TouchPhase::Ended, 2.0, 1.0, 3));
        t.push(stamp(1, TouchPhase::Cancelled, 5.0, 5.0, 4));
        assert_eq!(t.take_matching(0, TouchPhase::Started, 1.0, 1.0).map(|s| s.current.monotonic_ns), Some(1));
        assert_eq!(t.take_matching(0, TouchPhase::Moved, 2.0, 1.0).map(|s| s.current.monotonic_ns), Some(2));
        assert_eq!(t.take_matching(0, TouchPhase::Ended, 2.0, 1.0).map(|s| s.current.monotonic_ns), Some(3));
        assert_eq!(t.take_matching(1, TouchPhase::Cancelled, 5.0, 5.0).map(|s| s.current.monotonic_ns), Some(4));
        assert!(t.is_empty());
        assert_eq!(t.skipped(), 0);
    }

    /// 取りこぼし: 一致した控えより前の控えは捨てる。
    #[test]
    fn skips_stamps_before_the_match() {
        let mut t = OsTouchTimeline::new();
        t.push(stamp(0, TouchPhase::Moved, 2.0, 1.0, 1));
        t.push(stamp(0, TouchPhase::Moved, 3.0, 1.0, 2));
        t.push(stamp(0, TouchPhase::Moved, 4.0, 1.0, 3));
        assert_eq!(t.take_matching(0, TouchPhase::Moved, 4.0, 1.0).map(|s| s.current.monotonic_ns), Some(3));
        assert_eq!((t.len(), t.skipped()), (0, 2));
    }

    /// 余った控え: 一致の後ろの控えは残る。見つからなければ None で箱はそのまま。
    #[test]
    fn leftover_and_missing() {
        let mut t = OsTouchTimeline::new();
        t.push(stamp(0, TouchPhase::Started, 1.0, 1.0, 1));
        t.push(stamp(0, TouchPhase::Moved, 2.0, 1.0, 2));
        assert!(t.take_matching(0, TouchPhase::Started, 1.0, 1.0).is_some());
        assert_eq!(t.len(), 1, "後ろの控えは残る");
        assert_eq!(t.take_matching(0, TouchPhase::Moved, 9.0, 9.0), None, "位置が違う");
        assert_eq!(t.take_matching(1, TouchPhase::Moved, 2.0, 1.0), None, "指が違う");
        assert_eq!(t.take_matching(0, TouchPhase::Ended, 2.0, 1.0), None, "段階が違う");
        assert_eq!((t.len(), t.skipped()), (1, 0), "見つからなければ箱はそのまま");
    }

    /// 同じ位置の控えが続いたら、先頭の（古い）ものから順に一致する（2 本の指の片方が止まっている Moved）。
    #[test]
    fn identical_stamps_match_oldest_first() {
        let mut t = OsTouchTimeline::new();
        t.push(stamp(0, TouchPhase::Moved, 2.0, 1.0, 1));
        t.push(stamp(1, TouchPhase::Moved, 7.0, 7.0, 2));
        t.push(stamp(0, TouchPhase::Moved, 2.0, 1.0, 3));
        assert_eq!(t.take_matching(0, TouchPhase::Moved, 2.0, 1.0).map(|s| s.current.monotonic_ns), Some(1));
        assert_eq!(t.take_matching(1, TouchPhase::Moved, 7.0, 7.0).map(|s| s.current.monotonic_ns), Some(2));
        assert_eq!(t.take_matching(0, TouchPhase::Moved, 2.0, 1.0).map(|s| s.current.monotonic_ns), Some(3));
    }

    /// 上限: あふれたら古いものから捨てる。
    #[test]
    fn overflow_drops_the_oldest() {
        let mut t = OsTouchTimeline::new();
        for i in 0..(MAX_PENDING_STAMPS + 2) {
            t.push(stamp(0, TouchPhase::Moved, i as f32, 0.0, i as i64));
        }
        assert_eq!((t.len(), t.overflowed()), (MAX_PENDING_STAMPS, 2));
        assert_eq!(t.take_matching(0, TouchPhase::Moved, 0.0, 0.0), None, "最も古い 2 件は捨てた");
        assert_eq!(t.take_matching(0, TouchPhase::Moved, 2.0, 0.0).map(|s| s.current.monotonic_ns), Some(2));
    }
}
