// ============================================================
//  platform/bridge/desktop_sim/wall_clock.rs — 模擬の壁時計（W1-3）
//
//  デスクトップの模擬の目覚ましは、予定時刻（UTC の epoch ミリ秒）を壁時計と比べて鳴らす（Android の AlarmManager の
//  RTC_WAKEUP と同じく壁時計の時刻で判定する）。単体テストで時刻を進められるよう、時計を trait にして差し替えられるようにする。
// ============================================================

use std::fmt::Debug;
use std::time::{SystemTime, UNIX_EPOCH};

/// 壁時計（UTC の epoch ミリ秒）。
pub trait WallClock: Send + Sync + Debug {
    /// 今の時刻（UTC の epoch ミリ秒）。
    fn now_utc_ms(&self) -> i64;
}

/// 本物の壁時計（SystemTime）。
#[derive(Debug, Default, Clone, Copy)]
pub struct SystemWallClock;

impl WallClock for SystemWallClock {
    fn now_utc_ms(&self) -> i64 {
        // 1970 年より前（時計の狂い）は 0。i64 に収まらない未来は上限で止める
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|elapsed| i64::try_from(elapsed.as_millis()).unwrap_or(i64::MAX))
            .unwrap_or(0)
    }
}

/// 手で進める時計（単体テスト用）。
#[cfg(test)]
#[derive(Debug)]
pub struct ManualClock {
    /// 今の時刻（UTC の epoch ミリ秒）。
    now: std::sync::atomic::AtomicI64,
}

#[cfg(test)]
impl ManualClock {
    /// 指定の時刻から始める。
    pub fn starting_at(now_utc_ms: i64) -> Self {
        Self { now: std::sync::atomic::AtomicI64::new(now_utc_ms) }
    }

    /// 時刻を進める（ミリ秒）。
    pub fn advance(&self, millis: i64) {
        self.now.fetch_add(millis, std::sync::atomic::Ordering::SeqCst);
    }
}

#[cfg(test)]
impl WallClock for ManualClock {
    fn now_utc_ms(&self) -> i64 {
        self.now.load(std::sync::atomic::Ordering::SeqCst)
    }
}
