// ============================================================
//  platform/bridge/desktop_sim/haptics_state.rs — 模擬の触感の記録（W1-6）
//
//  Android の振動子の代わりに、鳴らした回数と最後の振動の長さを持つだけ（PC は振動しない）。haptics_commands.rs が書き、
//  ログと単体テストが読む。エディタの Play の区切りで空にする。
// ============================================================

use std::sync::{Mutex, MutexGuard, PoisonError};

/// 触感の記録の写し。
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct SimHapticsSnapshot {
    /// haptics.tap を受け付けた回数。
    pub taps: u64,
    /// haptics.vibrate を受け付けた回数。
    pub vibrations: u64,
    /// 最後に受け付けた振動の長さ（ミリ秒。上限にそろえた後。まだ無ければ None）。
    pub last_vibrate_ms: Option<i64>,
}

/// 模擬の触感の記録（Mutex 1 つで守る）。
#[derive(Debug, Default)]
pub struct SimHapticsLog {
    /// 今の記録。
    inner: Mutex<SimHapticsSnapshot>,
}

impl SimHapticsLog {
    /// 空の記録。
    pub fn new() -> Self {
        Self::default()
    }

    /// ロックを取る（毒されていても中身は壊れない値だけなので使い続ける）。
    fn lock(&self) -> MutexGuard<'_, SimHapticsSnapshot> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// 今の記録の写し。
    pub fn snapshot(&self) -> SimHapticsSnapshot {
        *self.lock()
    }

    /// tap を 1 回記録する。
    ///
    /// # 戻り値
    /// 記録した後の回数
    pub fn record_tap(&self) -> u64 {
        let mut inner = self.lock();
        inner.taps += 1;
        inner.taps
    }

    /// vibrate を 1 回記録する。
    ///
    /// # 戻り値
    /// 記録した後の回数
    pub fn record_vibrate(&self, milliseconds: i64) -> u64 {
        let mut inner = self.lock();
        inner.vibrations += 1;
        inner.last_vibrate_ms = Some(milliseconds);
        inner.vibrations
    }

    /// 空にする（エディタの Play の区切り）。
    pub fn clear(&self) {
        *self.lock() = SimHapticsSnapshot::default();
    }
}
