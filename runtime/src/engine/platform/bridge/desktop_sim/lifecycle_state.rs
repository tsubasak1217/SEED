// ============================================================
//  platform/bridge/desktop_sim/lifecycle_state.rs — 模擬の前面・背面の状態（2026-10-01。platform.resumed / paused の源）
//
//  Android の MainActivity の onResume / onPause の代わり。PC には「アプリが背面へ回る」が無いので、窓のフォーカスの出入り
//  （winit の WindowEvent::Focused。Play の間だけ）と、模擬だけの命令 app.sim_lifecycle（IPC の PLATFORM_SIM:lifecycle,…・
//  スクリプトの PlatformDiagnostics.SimulateLifecycle）で状態を動かす。
//  規則（Android の platform/app/AppLifecycle と同じ意味）:
//    - 始まりは前面（Play の開始・単体起動の直後は「動いている」）。起動の最初の前面は知らせない（Android もプロセスの最初の
//      onResume では出さない）ので、resumed は必ず paused の後に来る
//    - 同じ状態への移り（前面で前面へ・背面で背面へ）は知らせない（窓のフォーカスの重なった知らせを 1 つにする）
//    - 回数は前面・背面それぞれ 1 から数える。resumed は直前の paused からの時間（ミリ秒）を添える
//  エディタの Play の区切り（clear）で始まりの状態へ戻す。
// ============================================================

use std::sync::{Mutex, MutexGuard, PoisonError};

use crate::engine::platform::bridge::wire::app as app_names;

/// 前面・背面のどちらへ移るか（wire の phase の 2 つの値）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SimLifecyclePhase {
    /// 前面へ戻る（platform.resumed）。
    Resumed,
    /// 前面を離れる（platform.paused）。
    Paused,
}

impl SimLifecyclePhase {
    /// wire の語。
    pub fn wire(self) -> &'static str {
        match self {
            SimLifecyclePhase::Resumed => app_names::LIFECYCLE_RESUMED,
            SimLifecyclePhase::Paused => app_names::LIFECYCLE_PAUSED,
        }
    }

    /// wire の語を読む（知らない語は None）。
    pub fn parse(word: &str) -> Option<Self> {
        match word {
            w if w == app_names::LIFECYCLE_RESUMED => Some(SimLifecyclePhase::Resumed),
            w if w == app_names::LIFECYCLE_PAUSED => Some(SimLifecyclePhase::Paused),
            _ => None,
        }
    }

    /// 窓のフォーカスから（得た = 前面へ・失った = 前面を離れる）。
    pub fn from_focus(focused: bool) -> Self {
        if focused { SimLifecyclePhase::Resumed } else { SimLifecyclePhase::Paused }
    }

    /// 積むイベントの名前。
    pub fn event_name(self) -> &'static str {
        match self {
            SimLifecyclePhase::Resumed => app_names::EVENT_RESUMED,
            SimLifecyclePhase::Paused => app_names::EVENT_PAUSED,
        }
    }
}

/// 状態が移ったときの知らせの中身。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SimLifecycleTransition {
    /// 移った先。
    pub phase: SimLifecyclePhase,
    /// 何回目か（前面・背面それぞれ 1 から）。
    pub count: u64,
    /// 背面にいた時間（ミリ秒。resumed だけ。paused は 0）。
    pub background_ms: i64,
}

/// 前面・背面の状態。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct Inner {
    /// 前面にいるか。
    foreground: bool,
    /// 前面へ戻った回数。
    resume_count: u64,
    /// 前面を離れた回数。
    pause_count: u64,
    /// 最後に前面を離れた時刻（UTC の epoch ミリ秒。前面にいる間は None）。
    paused_at_utc_ms: Option<i64>,
}

impl Default for Inner {
    fn default() -> Self {
        // 始まりは前面（動いている。最初の前面は知らせない）
        Self { foreground: true, resume_count: 0, pause_count: 0, paused_at_utc_ms: None }
    }
}

/// 模擬の前面・背面の状態。
#[derive(Debug, Default)]
pub struct SimLifecycleState {
    /// 状態（Mutex 1 つで守る。窓のイベントも命令もエンジンのスレッドから来るが、PlatformBridge は Send + Sync の約束）。
    inner: Mutex<Inner>,
}

impl SimLifecycleState {
    /// 始まりの状態（前面）で作る。
    pub fn new() -> Self {
        Self::default()
    }

    /// ロックを取る（毒されていても中身は壊れない値だけなので使い続ける）。
    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// 前面にいるか（単体テストで状態を確かめる）。
    #[cfg(test)]
    pub fn is_foreground(&self) -> bool {
        self.lock().foreground
    }

    /// 移る。既にその状態なら None（知らせない）。
    ///
    /// # 引数
    /// * `phase`      - 移る先
    /// * `now_utc_ms` - 今の時刻（UTC の epoch ミリ秒。背面にいた時間を測る。模擬の壁時計）
    ///
    /// # 戻り値
    /// 移ったら Some(知らせの中身)
    pub fn transition(&self, phase: SimLifecyclePhase, now_utc_ms: i64) -> Option<SimLifecycleTransition> {
        let mut inner = self.lock();
        match phase {
            SimLifecyclePhase::Resumed if !inner.foreground => {
                inner.foreground = true;
                inner.resume_count += 1;
                // 時計が戻った（壁時計の調整）ときは 0 にそろえる
                let background_ms = inner.paused_at_utc_ms.take().map_or(0, |paused_at| now_utc_ms.saturating_sub(paused_at).max(0));
                Some(SimLifecycleTransition { phase, count: inner.resume_count, background_ms })
            }
            SimLifecyclePhase::Paused if inner.foreground => {
                inner.foreground = false;
                inner.pause_count += 1;
                inner.paused_at_utc_ms = Some(now_utc_ms);
                Some(SimLifecycleTransition { phase, count: inner.pause_count, background_ms: 0 })
            }
            _ => None,
        }
    }

    /// 始まりの状態（前面・回数 0）へ戻す（エディタの Play の区切り）。
    pub fn clear(&self) {
        *self.lock() = Inner::default();
    }
}

// ============================================================
//  ユニットテスト（移り・重なった知らせ・回数・背面の時間・Play の区切り）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 始まりは前面で、前面への移りは知らせない（起動の最初の前面は出さない）。
    #[test]
    fn starts_in_foreground_without_event() {
        let state = SimLifecycleState::new();
        assert!(state.is_foreground());
        assert_eq!(state.transition(SimLifecyclePhase::Resumed, 1_000), None);
    }

    /// paused → resumed の組で、回数はそれぞれ 1 から・背面の時間は paused からの差。重なった知らせは 1 つにする。
    #[test]
    fn pause_resume_pairs_count_and_measure_background() {
        let state = SimLifecycleState::new();
        let paused = state.transition(SimLifecyclePhase::Paused, 10_000).unwrap();
        assert_eq!(paused, SimLifecycleTransition { phase: SimLifecyclePhase::Paused, count: 1, background_ms: 0 });
        assert_eq!(state.transition(SimLifecyclePhase::Paused, 10_500), None, "背面で背面へは知らせない");
        let resumed = state.transition(SimLifecyclePhase::Resumed, 12_345).unwrap();
        assert_eq!(resumed, SimLifecycleTransition { phase: SimLifecyclePhase::Resumed, count: 1, background_ms: 2_345 });
        state.transition(SimLifecyclePhase::Paused, 20_000);
        let second = state.transition(SimLifecyclePhase::Resumed, 19_000).unwrap();
        assert_eq!((second.count, second.background_ms), (2, 0), "時計が戻ったら 0");
    }

    /// Play の区切り: 前面・回数 0 へ戻る。
    #[test]
    fn clear_restores_foreground() {
        let state = SimLifecycleState::new();
        state.transition(SimLifecyclePhase::Paused, 1);
        state.clear();
        assert!(state.is_foreground());
        let paused = state.transition(SimLifecyclePhase::Paused, 2).unwrap();
        assert_eq!(paused.count, 1);
    }

    /// wire の語・フォーカス・イベントの名前の対応。
    #[test]
    fn phase_vocabulary() {
        for phase in [SimLifecyclePhase::Resumed, SimLifecyclePhase::Paused] {
            assert_eq!(SimLifecyclePhase::parse(phase.wire()), Some(phase));
        }
        assert_eq!(SimLifecyclePhase::parse("stopped"), None);
        assert_eq!(SimLifecyclePhase::from_focus(true), SimLifecyclePhase::Resumed);
        assert_eq!(SimLifecyclePhase::from_focus(false), SimLifecyclePhase::Paused);
        assert_eq!(SimLifecyclePhase::Resumed.event_name(), app_names::EVENT_RESUMED);
        assert_eq!(SimLifecyclePhase::Paused.event_name(), app_names::EVENT_PAUSED);
    }
}
