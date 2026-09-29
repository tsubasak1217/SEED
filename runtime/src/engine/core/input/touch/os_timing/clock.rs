// ============================================================
//  touch/os_timing/clock.rs — CLOCK_MONOTONIC の ns と Instant の換算
//
//  【なぜ換算できるか】
//  Android の MotionEvent の時刻（getEventTime の ms・API 34 以上は getEventTimeNanos の ns）は CLOCK_MONOTONIC
//  （SystemClock.uptimeMillis の基準。NDK の input.h の AMotionEvent_getEventTime は「System.nanoTime の基準」）。
//  Rust の Instant も Android（Apple 以外の Unix）では CLOCK_MONOTONIC（std の sys/time/unix.rs の CLOCK_ID）。
//  同じ時計なので、ほぼ同時に読んだ 1 組（`ClockAnchor`）があれば、差を足し引きするだけで行き来できる。
//
//  【ずれ】`ClockAnchor::capture` は ns → Instant → ns の順に読み、2 回の ns の中点を Instant の読みに対応させる。
//  ずれは 2 回の読みの間の半分以下（vDSO の clock_gettime は数十 ns。読みの間にスレッドが止められると大きくなりうる）。
//
//  【時計の読み口】エンジン（このクレート）は libc に依存しないので、CLOCK_MONOTONIC の読み口は native
//  （runtime/android/native の touch_timeline.rs）が起動時に `install_monotonic_clock` で入れる。PC では誰も入れないので、
//  診断ログの ns への換算（`instant_to_monotonic_ns`）は None になる（PC では出さないログなので困らない）。
// ============================================================

use std::sync::OnceLock;
use std::time::{Duration, Instant};

/// CLOCK_MONOTONIC を読む関数（ns）。native が入れる。
pub type MonotonicClock = fn() -> i64;

/// 入れられた CLOCK_MONOTONIC の読み口（Android だけ。最初の 1 回だけ有効）。
static MONOTONIC_CLOCK: OnceLock<MonotonicClock> = OnceLock::new();

/// Instant と CLOCK_MONOTONIC の ns の対応（ほぼ同時に読んだ 1 組）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ClockAnchor {
    /// その瞬間の Instant。
    pub instant: Instant,
    /// 同じ瞬間の CLOCK_MONOTONIC（ns）。
    pub monotonic_ns: i64,
}

impl ClockAnchor {
    /// 今の対応を読む（ns → Instant → ns の順に読み、ns の中点を Instant に対応させる）。
    ///
    /// # 引数
    /// * `read_monotonic_ns` - CLOCK_MONOTONIC を ns で読む関数
    pub fn capture(read_monotonic_ns: MonotonicClock) -> Self {
        let before = read_monotonic_ns();
        let instant = Instant::now();
        let after = read_monotonic_ns();
        // 中点（オーバーフローしない形。before ≤ after のはずだが、逆でも片方へ寄るだけ）
        let monotonic_ns = before.saturating_add(after.saturating_sub(before) / 2);
        Self { instant, monotonic_ns }
    }

    /// CLOCK_MONOTONIC の ns を Instant へ直す【純関数】。
    ///
    /// 対応の時点より未来の値（時計の読みのずれ・壊れた値）は対応の時点（＝受け取った今）へ丸める。
    /// Instant で表せないほど過去の値（起動より前など。Instant::checked_sub が失敗）も対応の時点に丸める（失敗させない）。
    pub fn instant_of(&self, monotonic_ns: i64) -> Instant {
        let behind_ns = self.monotonic_ns.saturating_sub(monotonic_ns);
        if behind_ns <= 0 {
            return self.instant;
        }
        let behind = Duration::from_nanos(behind_ns.unsigned_abs());
        self.instant.checked_sub(behind).unwrap_or(self.instant)
    }

    /// Instant を CLOCK_MONOTONIC の ns へ直す【純関数】（診断ログ用。i64 に収まらなければ飽和させる）。
    pub fn monotonic_ns_of(&self, at: Instant) -> i64 {
        if at >= self.instant {
            let ahead = i64::try_from(at.duration_since(self.instant).as_nanos()).unwrap_or(i64::MAX);
            self.monotonic_ns.saturating_add(ahead)
        } else {
            let behind = i64::try_from(self.instant.duration_since(at).as_nanos()).unwrap_or(i64::MAX);
            self.monotonic_ns.saturating_sub(behind)
        }
    }
}

/// CLOCK_MONOTONIC の読み口を入れる（native が起動時に 1 回。2 回目以降は何もしない）。
pub fn install_monotonic_clock(read: MonotonicClock) {
    let _ = MONOTONIC_CLOCK.set(read);
}

/// 入れられた CLOCK_MONOTONIC の読み口（PC では None）。
pub fn monotonic_clock() -> Option<MonotonicClock> {
    MONOTONIC_CLOCK.get().copied()
}

/// Instant を CLOCK_MONOTONIC の ns へ直す（診断ログ用。読み口が無ければ None）。
///
/// その場で対応を読み直す（`ClockAnchor::capture`）ので、ずれは読みの間の半分以下。
pub fn instant_to_monotonic_ns(at: Instant) -> Option<i64> {
    let read = monotonic_clock()?;
    Some(ClockAnchor::capture(read).monotonic_ns_of(at))
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 1 秒 = 1,000,000,000 ns（試験の数値の読みやすさのため）。
    const NS_PER_SEC: i64 = 1_000_000_000;

    /// 対応の時点（ns = 100 秒）。
    fn anchor() -> ClockAnchor {
        ClockAnchor { instant: Instant::now(), monotonic_ns: 100 * NS_PER_SEC }
    }

    /// 過去の ns は差だけ前の Instant。往復で元の ns に戻る。
    #[test]
    fn past_ns_round_trip() {
        let a = anchor();
        let event_ns = a.monotonic_ns - 12_345_678;
        let at = a.instant_of(event_ns);
        assert_eq!(a.instant.duration_since(at), Duration::from_nanos(12_345_678));
        assert_eq!(a.monotonic_ns_of(at), event_ns);
    }

    /// 未来の ns は対応の時点へ丸める。Instant で表せない過去も失敗させずに対応の時点。
    #[test]
    fn future_and_unrepresentable_are_clamped() {
        let a = anchor();
        assert_eq!(a.instant_of(a.monotonic_ns + 5_000_000), a.instant, "未来");
        // 表せないほどの過去: checked_sub が失敗する環境では対応の時点、表せる環境では過去の Instant（どちらも panic しない）
        assert!(a.instant_of(i64::MIN) <= a.instant);
        let later = a.instant + Duration::from_millis(3);
        assert_eq!(a.monotonic_ns_of(later), a.monotonic_ns + 3_000_000);
    }

    /// 読み口の中点: 2 回の読みが 10 と 30 なら 20。
    #[test]
    fn capture_uses_the_midpoint() {
        use std::sync::atomic::{AtomicI64, Ordering};
        static CALLS: AtomicI64 = AtomicI64::new(0);
        /// 呼ぶたびに 10・30・50… を返す読み口。
        fn fake() -> i64 {
            10 + 20 * CALLS.fetch_add(1, Ordering::Relaxed)
        }
        CALLS.store(0, Ordering::Relaxed);
        assert_eq!(ClockAnchor::capture(fake).monotonic_ns, 20);
    }
}
