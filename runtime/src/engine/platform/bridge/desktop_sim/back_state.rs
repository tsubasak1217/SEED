// ============================================================
//  platform/bridge/desktop_sim/back_state.rs — 模擬の予測型の戻るの状態（W2 の手直し P1-3）
//
//  Android の Java（back/BackCallbackController・BackGestureReporter）の代わりに持つもの:
//    - アプリが戻るを受けるか（スクリプトが app.set_back_callback で最後に知らせた値。まだなら None）
//    - 戻るの手ぶりの通し番号と「手ぶりの途中か」（app.sim_back_gesture が積むイベントの gesture）
//  番号の決まりは Java の BackGestureReporter と同じ（SimBackState::advance。命令は back_commands.rs）:
//    started   … 番号を 1 増やし、手ぶりの途中にする
//    progressed … 今の番号のまま（途中かは変えない）
//    cancelled … 今の番号のまま、途中を終える
//    invoked   … 途中でなければ番号を 1 増やし（Android 13 は started が無い）、途中を終える
//  エディタの Play の区切り（reset_session）で両方を捨てる（番号も 0 に戻す）。
// ============================================================

use std::sync::{Mutex, MutexGuard, PoisonError};

use crate::engine::platform::bridge::wire::app as app_names;

/// 戻るの手ぶりの段階（sim_back_gesture の phase）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SimBackPhase {
    /// 始まった（platform.back_started）。
    Started,
    /// 進んだ（platform.back_progressed）。
    Progressed,
    /// 取り消された（platform.back_cancelled）。
    Cancelled,
    /// 確定した（platform.back_invoked）。
    Invoked,
}

impl SimBackPhase {
    /// wire の語（phase）を読む（知らない語は None）。
    pub fn parse(word: &str) -> Option<Self> {
        match word {
            w if w == app_names::PHASE_STARTED => Some(SimBackPhase::Started),
            w if w == app_names::PHASE_PROGRESSED => Some(SimBackPhase::Progressed),
            w if w == app_names::PHASE_CANCELLED => Some(SimBackPhase::Cancelled),
            w if w == app_names::PHASE_INVOKED => Some(SimBackPhase::Invoked),
            _ => None,
        }
    }

    /// この段階で積むイベントの名前。
    pub fn event_name(self) -> &'static str {
        match self {
            SimBackPhase::Started => app_names::EVENT_BACK_STARTED,
            SimBackPhase::Progressed => app_names::EVENT_BACK_PROGRESSED,
            SimBackPhase::Cancelled => app_names::EVENT_BACK_CANCELLED,
            SimBackPhase::Invoked => app_names::EVENT_BACK_INVOKED,
        }
    }

    /// イベントの data に進み具合・端・指の位置を入れる段階か（started・progressed。Android の BackEvent がある段階）。
    pub fn carries_back_event(self) -> bool {
        matches!(self, SimBackPhase::Started | SimBackPhase::Progressed)
    }
}

/// 模擬の予測型の戻るの状態の写し（単体テスト・診断用）。
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct SimBackSnapshot {
    /// アプリが戻るを受けるか（None は「スクリプトがまだ知らせていない」）。
    pub app_handles_back: Option<bool>,
    /// 最後の手ぶりの通し番号（まだ無ければ既定値の 0。最初の手ぶりが 1）。
    pub gesture: u64,
    /// 手ぶりの途中か（started の後、cancelled・invoked の前）。
    pub in_progress: bool,
}

/// 模擬の予測型の戻るの状態（Mutex 1 つで守る）。
#[derive(Debug, Default)]
pub struct SimBackState {
    /// 今の状態。
    inner: Mutex<SimBackSnapshot>,
}

impl SimBackState {
    /// 既定の状態（知らせなし・番号 0）。
    pub fn new() -> Self {
        Self::default()
    }

    /// ロックを取る（毒されていても中身は壊れない値だけなので使い続ける）。
    fn lock(&self) -> MutexGuard<'_, SimBackSnapshot> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// 今の状態の写し。
    pub fn snapshot(&self) -> SimBackSnapshot {
        *self.lock()
    }

    /// アプリが戻るを受けるかを記録する。
    ///
    /// # 戻り値
    /// 前と違う値になったら true（ログを出すかの判断。Java の BackCallbackController も変わったときだけログを出す）
    pub fn set_app_handles_back(&self, on: bool) -> bool {
        let mut inner = self.lock();
        let changed = inner.app_handles_back != Some(on);
        inner.app_handles_back = Some(on);
        changed
    }

    /// 手ぶりの段階を 1 つ進め、そのイベントに入れる通し番号を返す（決まりはファイル冒頭）。
    pub fn advance(&self, phase: SimBackPhase) -> u64 {
        let mut inner = self.lock();
        match phase {
            SimBackPhase::Started => {
                inner.gesture += 1;
                inner.in_progress = true;
            }
            SimBackPhase::Progressed => {}
            SimBackPhase::Cancelled => inner.in_progress = false,
            SimBackPhase::Invoked => {
                if !inner.in_progress {
                    inner.gesture += 1;
                }
                inner.in_progress = false;
            }
        }
        inner.gesture
    }

    /// 既定の状態へ戻す（エディタの Play の区切り。知らせなし・番号 0・途中でない）。
    pub fn clear(&self) {
        *self.lock() = SimBackSnapshot::default();
    }
}
