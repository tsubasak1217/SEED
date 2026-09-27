// ============================================================
//  platform/bridge/desktop_sim/window_state.rs — 模擬の画面の状態（W1-6）
//
//  Android の MainActivity の窓（ロック画面の上に出す・画面を点けたまま・システムバー）の代わりに、スクリプトが最後に
//  切り替えた値を持つだけ（デスクトップのウィンドウには何もしない）。window_commands.rs が書き、ログと単体テストが読む。
//  システムバーは、スクリプトが切り替えるまでは「プロジェクト設定の既定のまま」（None）。エディタの Play の区切りで既定へ戻す。
// ============================================================

use std::sync::{Mutex, MutexGuard, PoisonError};

/// 画面の状態の写し。
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct SimWindowSnapshot {
    /// ロック画面の上に出す＋画面を点ける（Android の既定は false）。
    pub show_when_locked: bool,
    /// 画面を点けたままにする（Android の既定は false）。
    pub keep_screen_on: bool,
    /// システムバーを出すか（None は「スクリプトが切り替えていない＝プロジェクト設定 android.system_bars の既定のまま」）。
    pub system_bars_visible: Option<bool>,
}

/// 模擬の画面の状態（Mutex 1 つで守る）。
#[derive(Debug, Default)]
pub struct SimWindowState {
    /// 今の状態。
    inner: Mutex<SimWindowSnapshot>,
}

impl SimWindowState {
    /// 既定の状態。
    pub fn new() -> Self {
        Self::default()
    }

    /// ロックを取る（毒されていても中身は壊れない値だけなので使い続ける）。
    fn lock(&self) -> MutexGuard<'_, SimWindowSnapshot> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// 今の状態の写し。
    pub fn snapshot(&self) -> SimWindowSnapshot {
        *self.lock()
    }

    /// ロック画面の上に出す＋画面を点ける を記録する。
    pub fn set_show_when_locked(&self, on: bool) {
        self.lock().show_when_locked = on;
    }

    /// 画面を点けたままにする を記録する。
    pub fn set_keep_screen_on(&self, on: bool) {
        self.lock().keep_screen_on = on;
    }

    /// システムバーを出すかを記録する。
    pub fn set_system_bars_visible(&self, on: bool) {
        self.lock().system_bars_visible = Some(on);
    }

    /// 既定の状態へ戻す（エディタの Play の区切り）。
    pub fn clear(&self) {
        *self.lock() = SimWindowSnapshot::default();
    }
}
