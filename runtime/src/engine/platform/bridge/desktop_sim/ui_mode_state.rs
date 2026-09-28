// ============================================================
//  platform/bridge/desktop_sim/ui_mode_state.rs — 模擬の端末の明暗（W2-9。app.ui_mode の源）
//
//  Android の Configuration.uiMode の夜の bit の代わり。値の出どころは 2 つ:
//    - OS の設定（os_ui_mode.rs。Windows の「既定のアプリ モード」。読む係は差し替えられる＝単体テスト）
//    - 模擬の差し替え（app.sim_set_ui_mode。OS の設定を変えずに明暗の切り替えを PC で試すため。"system" でやめる）
//  変化のイベント（platform.ui_mode_changed）は、最後に知らせた値（問い合わせ・イベントのどちらか）と違うときだけ出す。
//  エディタの Play の区切りで差し替えと最後に知らせた値を捨てる。
// ============================================================

use std::sync::{Mutex, MutexGuard, PoisonError};

use crate::engine::platform::bridge::wire::app as app_names;

/// 夜の表示か（wire の night の 3 つの値）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SimNight {
    /// 夜の表示（暗い）。
    Yes,
    /// 夜の表示でない（明るい）。
    No,
    /// 取れない。
    Unknown,
}

impl SimNight {
    /// wire の語。
    pub fn wire(self) -> &'static str {
        match self {
            SimNight::Yes => app_names::NIGHT_YES,
            SimNight::No => app_names::NIGHT_NO,
            SimNight::Unknown => app_names::NIGHT_UNKNOWN,
        }
    }

    /// wire の語を読む（知らない語は None。system は差し替えの取り消しなのでここでは読まない）。
    pub fn parse(word: &str) -> Option<Self> {
        match word {
            w if w == app_names::NIGHT_YES => Some(SimNight::Yes),
            w if w == app_names::NIGHT_NO => Some(SimNight::No),
            w if w == app_names::NIGHT_UNKNOWN => Some(SimNight::Unknown),
            _ => None,
        }
    }
}

/// OS の設定を読む係。
pub type OsNightReader = fn() -> SimNight;

/// 差し替えと最後に知らせた値。
#[derive(Debug, Default)]
struct Inner {
    /// 模擬の差し替え（None = OS の設定）。
    overridden: Option<SimNight>,
    /// 最後にスクリプトへ知らせた値（問い合わせの返答・イベント。まだなら None）。
    last_reported: Option<SimNight>,
}

/// 模擬の端末の明暗。
#[derive(Debug)]
pub struct SimUiModeState {
    /// 差し替えと最後に知らせた値（Mutex 1 つで守る）。
    inner: Mutex<Inner>,
    /// OS の設定を読む係。
    reader: Mutex<OsNightReader>,
}

impl SimUiModeState {
    /// OS の設定を読む係を指定して作る。
    pub fn new(reader: OsNightReader) -> Self {
        Self { inner: Mutex::new(Inner::default()), reader: Mutex::new(reader) }
    }

    /// ロックを取る（毒されていても中身は壊れない値だけなので使い続ける）。
    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// OS の設定を読む係を差し替える（単体テスト）。
    #[cfg(test)]
    pub fn set_reader(&self, reader: OsNightReader) {
        *self.reader.lock().unwrap_or_else(PoisonError::into_inner) = reader;
    }

    /// 今の値（差し替えがあればそれ・無ければ OS の設定）。
    pub fn current(&self) -> SimNight {
        let overridden = self.lock().overridden;
        overridden.unwrap_or_else(|| (*self.reader.lock().unwrap_or_else(PoisonError::into_inner))())
    }

    /// 差し替えているか。
    pub fn is_overridden(&self) -> bool {
        self.lock().overridden.is_some()
    }

    /// 問い合わせに答える値（今の値を「知らせた値」として覚える）。
    pub fn report(&self) -> SimNight {
        let now = self.current();
        self.lock().last_reported = Some(now);
        now
    }

    /// 差し替えを置く（None で OS の設定へ戻す）。
    pub fn set_override(&self, night: Option<SimNight>) {
        self.lock().overridden = night;
    }

    /// 今の値が最後に知らせた値と違えば、知らせた値を今の値にして返す（イベントを出す値）。同じなら None。
    pub fn take_change(&self) -> Option<SimNight> {
        let now = self.current();
        let mut inner = self.lock();
        if inner.last_reported == Some(now) {
            return None;
        }
        inner.last_reported = Some(now);
        Some(now)
    }

    /// 差し替えと最後に知らせた値を捨てる（エディタの Play の区切り）。
    pub fn clear(&self) {
        *self.lock() = Inner::default();
    }
}
