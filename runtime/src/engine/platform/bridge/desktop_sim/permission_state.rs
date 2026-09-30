// ============================================================
//  platform/bridge/desktop_sim/permission_state.rs — 模擬の権限の状態と答え（2026-10-01。permission.* の源）
//
//  Android の PermissionStatusProbe（端末の本当の状態）と、確認の画面・設定の画面で利用者が選ぶ答えの代わり。
//    状態 … 種類ごとに 1 つ。既定は v1 の種類が granted、v2 の予約の種類は常に not_applicable（Android と同じく変えられない）。
//           起動時の設定（permission_config.rs の環境変数）で上書きし、実行中は permission.sim_set（IPC・スクリプト）で変える。
//    答え … permission.request のときに当てる状態（既定は granted＝従来の「求めれば許可」）か「答えない」。種類ごとに持つ。
//  エディタの Play の区切り（clear）で起動時の設定へ戻す（Play を止めれば実行中の変更は消える。ほかの模擬の状態と同じ）。
//  求めたときの規則（どの状態なら答えを当てるか）は decide_request（純粋な関数。単体テストで表を確かめる）。
// ============================================================

use std::sync::{Mutex, MutexGuard, PoisonError};

use super::permission_config::{SimAnswer, SimAnswerTarget, SimPermissionConfig};
use crate::engine::platform::bridge::permission::{PermissionKind, PermissionStatus};

/// 種類の数（種類ごとの表の大きさ）。
const KIND_COUNT: usize = PermissionKind::ALL.len();

/// 種類ごとの状態と答え。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct Tables {
    /// 今の状態（PermissionKind::index で引く）。
    statuses: [PermissionStatus; KIND_COUNT],
    /// 求めたときの答え（PermissionKind::index で引く）。
    answers: [SimAnswer; KIND_COUNT],
}

impl Tables {
    /// 既定（v1 の種類は granted・v2 の予約は not_applicable、答えはすべて既定＝granted）に設定を重ねた表。
    fn from_config(config: &SimPermissionConfig) -> Self {
        let mut tables = Self {
            statuses: PermissionKind::ALL.map(default_status),
            answers: [SimAnswer::DEFAULT; KIND_COUNT],
        };
        for (kind, status) in &config.statuses {
            tables.set_status(*kind, *status);
        }
        for (target, answer) in &config.answers {
            tables.set_answer(*target, *answer);
        }
        tables
    }

    /// 状態を置く（v2 の予約の種類は変えない）。変わったら true。
    fn set_status(&mut self, kind: PermissionKind, status: PermissionStatus) -> bool {
        if !kind.is_implemented() {
            return false;
        }
        let slot = &mut self.statuses[kind.index()];
        let changed = *slot != status;
        *slot = status;
        changed
    }

    /// 答えを置く（all ならすべての種類）。
    fn set_answer(&mut self, target: SimAnswerTarget, answer: SimAnswer) {
        match target {
            SimAnswerTarget::All => self.answers = [answer; KIND_COUNT],
            SimAnswerTarget::Kind(kind) => self.answers[kind.index()] = answer,
        }
    }
}

/// 模擬の権限（状態と答え）。
#[derive(Debug)]
pub struct SimPermissionBoard {
    /// 起動時の設定（Play の区切りでここへ戻す。単体テストが差し替えるので Mutex）。
    initial: Mutex<SimPermissionConfig>,
    /// 今の状態と答え（Mutex 1 つで守る。命令はエンジンのスレッドから来るが、PlatformBridge は Send + Sync の約束）。
    tables: Mutex<Tables>,
}

impl SimPermissionBoard {
    /// 起動時の設定から作る。
    pub fn new(initial: SimPermissionConfig) -> Self {
        let tables = Mutex::new(Tables::from_config(&initial));
        Self { initial: Mutex::new(initial), tables }
    }

    /// ロックを取る（毒されていても中身は壊れない値だけなので使い続ける）。
    fn lock(&self) -> MutexGuard<'_, Tables> {
        self.tables.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// 起動時の設定を差し替え、今の状態と答えもそれにする（単体テストで環境変数を使わずに設定を与える）。
    #[cfg(test)]
    pub fn reconfigure(&self, config: SimPermissionConfig) {
        *self.lock() = Tables::from_config(&config);
        *self.initial.lock().unwrap_or_else(PoisonError::into_inner) = config;
    }

    /// 今の状態。
    pub fn status(&self, kind: PermissionKind) -> PermissionStatus {
        self.lock().statuses[kind.index()]
    }

    /// 状態を変える（v2 の予約の種類は変えない）。
    ///
    /// # 戻り値
    /// 変わったら true（呼び出し側が platform.permission_changed を積む）
    pub fn set_status(&self, kind: PermissionKind, status: PermissionStatus) -> bool {
        self.lock().set_status(kind, status)
    }

    /// 求めたときの答え。
    pub fn answer(&self, kind: PermissionKind) -> SimAnswer {
        self.lock().answers[kind.index()]
    }

    /// 答えを決める（all ならすべての種類）。
    pub fn set_answer(&self, target: SimAnswerTarget, answer: SimAnswer) {
        self.lock().set_answer(target, answer);
    }

    /// 求めた: 今の状態と答えから新しい状態を決めて置く。
    ///
    /// # 戻り値
    /// (新しい状態, 変わったか)
    pub fn apply_request(&self, kind: PermissionKind) -> (PermissionStatus, bool) {
        let mut tables = self.lock();
        let index = kind.index();
        let next = decide_request(tables.statuses[index], tables.answers[index]);
        let changed = tables.set_status(kind, next);
        (next, changed)
    }

    /// 起動時の設定へ戻す（エディタの Play の区切り）。
    pub fn clear(&self) {
        let tables = Tables::from_config(&self.initial.lock().unwrap_or_else(PoisonError::into_inner));
        *self.lock() = tables;
    }
}

/// 種類の既定の状態（v1 は許可、v2 の予約は not_applicable。デスクトップの模擬の従来の値）。
fn default_status(kind: PermissionKind) -> PermissionStatus {
    if kind.is_implemented() { PermissionStatus::Granted } else { PermissionStatus::NotApplicable }
}

/// 求めたときの新しい状態（Android の Permissions.Request の振る舞いに合わせた規則）。
///
/// - granted・not_applicable … 画面を出さずに今の状態（答えは当てない）
/// - denied_permanently       … 確認の画面が出ないので今の状態（Android 13+ の通知と同じ。設定の画面へは OpenSettings）
/// - denied・needs_settings   … 確認の画面・設定の画面で利用者が答える: 答えの状態（答えないなら今の状態）
pub fn decide_request(current: PermissionStatus, answer: SimAnswer) -> PermissionStatus {
    match current {
        PermissionStatus::Granted | PermissionStatus::NotApplicable | PermissionStatus::DeniedPermanently => current,
        PermissionStatus::Denied | PermissionStatus::NeedsSettings => match answer {
            SimAnswer::Status(status) => status,
            SimAnswer::NoAnswer => current,
        },
    }
}

// ============================================================
//  ユニットテスト（既定・設定の重ね方・求めたときの規則・Play の区切り）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 既定: v1 は granted、v2 は not_applicable、答えは granted。
    #[test]
    fn defaults_match_previous_simulator() {
        let board = SimPermissionBoard::new(SimPermissionConfig::default());
        for kind in PermissionKind::ALL {
            let expected = if kind.is_implemented() { PermissionStatus::Granted } else { PermissionStatus::NotApplicable };
            assert_eq!(board.status(kind), expected, "{}", kind.wire_name());
            assert_eq!(board.answer(kind), SimAnswer::DEFAULT);
        }
    }

    /// 設定の重ね方: 状態は種類ごと、答えは並びどおり（all の後の種類の指定が勝つ）。v2 の予約は変わらない。
    #[test]
    fn config_overrides_defaults() {
        let config = SimPermissionConfig::from_texts(
            Some("post_notifications=denied;exact_alarm=needs_settings"),
            Some("none;exact_alarm=granted"),
        );
        let board = SimPermissionBoard::new(config);
        assert_eq!(board.status(PermissionKind::PostNotifications), PermissionStatus::Denied);
        assert_eq!(board.status(PermissionKind::ExactAlarm), PermissionStatus::NeedsSettings);
        assert_eq!(board.status(PermissionKind::FullScreenIntent), PermissionStatus::Granted);
        assert_eq!(board.answer(PermissionKind::PostNotifications), SimAnswer::NoAnswer);
        assert_eq!(board.answer(PermissionKind::ExactAlarm), SimAnswer::Status(PermissionStatus::Granted));
        assert!(!board.set_status(PermissionKind::SendSms, PermissionStatus::Granted), "v2 の予約は変えない");
        assert_eq!(board.status(PermissionKind::SendSms), PermissionStatus::NotApplicable);
    }

    /// 求めたときの規則の表（今の状態 × 答え → 新しい状態）。
    #[test]
    fn request_rules_table() {
        use PermissionStatus::*;
        let granted = SimAnswer::Status(Granted);
        let cases = [
            (Granted, SimAnswer::Status(Denied), Granted),
            (NotApplicable, granted, NotApplicable),
            (DeniedPermanently, granted, DeniedPermanently),
            (Denied, granted, Granted),
            (Denied, SimAnswer::Status(DeniedPermanently), DeniedPermanently),
            (Denied, SimAnswer::NoAnswer, Denied),
            (NeedsSettings, granted, Granted),
            (NeedsSettings, SimAnswer::NoAnswer, NeedsSettings),
        ];
        for (current, answer, expected) in cases {
            assert_eq!(decide_request(current, answer), expected, "{current:?} × {answer:?}");
        }
    }

    /// apply_request: 答えを当てて置き、変わったかを返す。set_status は同じ値なら false。
    #[test]
    fn apply_request_updates_status() {
        let board = SimPermissionBoard::new(SimPermissionConfig::from_texts(Some("exact_alarm=needs_settings"), None));
        assert_eq!(board.apply_request(PermissionKind::ExactAlarm), (PermissionStatus::Granted, true));
        assert_eq!(board.apply_request(PermissionKind::ExactAlarm), (PermissionStatus::Granted, false));
        assert!(!board.set_status(PermissionKind::ExactAlarm, PermissionStatus::Granted));
        assert!(board.set_status(PermissionKind::ExactAlarm, PermissionStatus::Denied));
        board.set_answer(SimAnswerTarget::All, SimAnswer::NoAnswer);
        assert_eq!(board.apply_request(PermissionKind::ExactAlarm), (PermissionStatus::Denied, false));
    }

    /// Play の区切り: 実行中の変更を捨てて起動時の設定へ戻る。
    #[test]
    fn clear_restores_initial_config() {
        let board = SimPermissionBoard::new(SimPermissionConfig::from_texts(Some("post_notifications=denied"), Some("denied")));
        board.set_status(PermissionKind::PostNotifications, PermissionStatus::Granted);
        board.set_status(PermissionKind::ExactAlarm, PermissionStatus::NeedsSettings);
        board.set_answer(SimAnswerTarget::All, SimAnswer::NoAnswer);
        board.clear();
        assert_eq!(board.status(PermissionKind::PostNotifications), PermissionStatus::Denied);
        assert_eq!(board.status(PermissionKind::ExactAlarm), PermissionStatus::Granted);
        assert_eq!(board.answer(PermissionKind::FullScreenIntent), SimAnswer::Status(PermissionStatus::Denied));
    }
}
