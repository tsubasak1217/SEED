// ============================================================
//  platform/bridge/desktop_sim/permission_config.rs — 模擬の権限の起動時の設定（環境変数の読み取り。2026-10-01）
//
//  【役割】
//  PC の Play（エディタ・単体起動）で、権限の状態と「求めたときの模擬の利用者の答え」を起動の前に与える。
//  自動の確かめ（オンボーディングの「拒否 → 設定の画面 → 許可」の流れなど）を、端末なしで決まった状態から始めるため。
//    SEED_PLATFORM_SIM_PERMISSIONS        … 起動時の状態。  例 "post_notifications=denied;exact_alarm=needs_settings"
//    SEED_PLATFORM_SIM_PERMISSION_ANSWER  … 求めたときの答え。例 "granted" / "none;exact_alarm=granted"
//  書き方: 項目を ; で区切る。項目は「種類=値」（種類は wire の名前。答えは all=値 か、種類を書かない「値」で全部の種類）。
//  値は状態の wire の名前（granted / denied / denied_permanently / needs_settings / not_applicable）、答えは加えて none（答えない）。
//  v2 の予約の種類（record_audio・send_sms）は Android と同じく常に not_applicable なので変えられない（読み取りで誤りにする）。
//  読めない項目はログに出して飛ばし、読めた項目だけを使う（1 つの書き損じで全部を失わない）。
//  実行中の変更は IPC（PLATFORM_SIM:permission,… / permission_answer,…）かスクリプト（PlatformDiagnostics）で行う
//  （中身は permission_state.rs・permission_commands.rs）。
// ============================================================

use crate::engine::platform::bridge::permission::{PermissionKind, PermissionStatus};
use crate::engine::platform::bridge::wire::permission as names;
use crate::engine::platform::bridge::LOG_PREFIX;

/// 起動時の状態を与える環境変数。
pub const PERMISSIONS_ENV: &str = "SEED_PLATFORM_SIM_PERMISSIONS";

/// 求めたときの模擬の利用者の答えを与える環境変数。
pub const PERMISSION_ANSWER_ENV: &str = "SEED_PLATFORM_SIM_PERMISSION_ANSWER";

/// 項目の区切り。
const ENTRY_SEPARATOR: char = ';';

/// 種類と値の区切り。
const KEY_VALUE_SEPARATOR: char = '=';

/// 模擬の利用者の答え（求めたときに当てる状態、または答えない）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SimAnswer {
    /// その状態にする（確認の画面・設定の画面で利用者がそうした）。
    Status(PermissionStatus),
    /// 答えない（確認の画面の外を押して閉じた・設定の画面で何も変えずに戻った。状態は変わらない）。
    NoAnswer,
}

impl SimAnswer {
    /// 既定の答え（許可する。デスクトップの模擬の従来の振る舞い＝求めれば許可と同じ）。
    pub const DEFAULT: SimAnswer = SimAnswer::Status(PermissionStatus::Granted);

    /// wire の語（状態の名前か none）。
    pub fn wire_name(self) -> &'static str {
        match self {
            SimAnswer::Status(status) => status.wire_name(),
            SimAnswer::NoAnswer => names::ANSWER_NONE,
        }
    }

    /// wire の語を読む（状態の名前か none。知らない語は Err〈説明〉）。
    pub fn parse(word: &str) -> Result<Self, String> {
        if word == names::ANSWER_NONE {
            return Ok(SimAnswer::NoAnswer);
        }
        PermissionStatus::from_wire(word).map(SimAnswer::Status).ok_or_else(|| {
            format!("{}（または {}）", PermissionStatus::describe_unknown(names::KEY_ANSWER, word), names::ANSWER_NONE)
        })
    }
}

/// 答えを当てる相手（1 つの種類か、すべて）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SimAnswerTarget {
    /// すべての種類。
    All,
    /// 1 つの種類。
    Kind(PermissionKind),
}

impl SimAnswerTarget {
    /// wire の語（種類の名前か all）を読む。
    pub fn parse(word: &str) -> Result<Self, String> {
        if word == names::KIND_ALL {
            return Ok(SimAnswerTarget::All);
        }
        read_settable_kind(word).map(SimAnswerTarget::Kind)
    }

    /// wire の語。
    pub fn wire_name(self) -> &'static str {
        match self {
            SimAnswerTarget::All => names::KIND_ALL,
            SimAnswerTarget::Kind(kind) => kind.wire_name(),
        }
    }
}

/// 起動時の設定（環境変数から読んだ、状態と答えの上書き）。Play の区切りでここへ戻す。
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SimPermissionConfig {
    /// 起動時の状態の上書き（種類ごと。書いていない種類は既定＝v1 は granted）。
    pub statuses: Vec<(PermissionKind, PermissionStatus)>,
    /// 答えの上書き（書いた順に当てる。all の後の種類の指定はその種類だけを上書きする）。
    pub answers: Vec<(SimAnswerTarget, SimAnswer)>,
}

impl SimPermissionConfig {
    /// 環境変数から読む（無ければ空。読めない項目はログに出して飛ばす）。
    pub fn from_env() -> Self {
        Self::from_texts(std::env::var(PERMISSIONS_ENV).ok().as_deref(), std::env::var(PERMISSION_ANSWER_ENV).ok().as_deref())
    }

    /// 2 つの文字列から読む（単体テストでは環境変数を使わずにここを呼ぶ）。
    ///
    /// # 引数
    /// * `statuses` - 起動時の状態の書き方（PERMISSIONS_ENV の値。None・空なら上書きなし）
    /// * `answers`  - 答えの書き方（PERMISSION_ANSWER_ENV の値。None・空なら既定の答え）
    pub fn from_texts(statuses: Option<&str>, answers: Option<&str>) -> Self {
        let (statuses, status_errors) = parse_statuses(statuses.unwrap_or_default());
        let (answers, answer_errors) = parse_answers(answers.unwrap_or_default());
        for error in &status_errors {
            eprintln!("{LOG_PREFIX} 模擬: {PERMISSIONS_ENV} の項目を飛ばしました: {error}");
        }
        for error in &answer_errors {
            eprintln!("{LOG_PREFIX} 模擬: {PERMISSION_ANSWER_ENV} の項目を飛ばしました: {error}");
        }
        let config = Self { statuses, answers };
        if !config.is_empty() {
            eprintln!("{LOG_PREFIX} 模擬: 権限の起動時の設定 {}", config.describe());
        }
        config
    }

    /// 上書きが何も無いか。
    pub fn is_empty(&self) -> bool {
        self.statuses.is_empty() && self.answers.is_empty()
    }

    /// ログ向けの 1 行（例 "状態 [post_notifications=denied]・答え [all=none]"）。
    pub fn describe(&self) -> String {
        let statuses: Vec<String> =
            self.statuses.iter().map(|(kind, status)| format!("{}={}", kind.wire_name(), status.wire_name())).collect();
        let answers: Vec<String> =
            self.answers.iter().map(|(target, answer)| format!("{}={}", target.wire_name(), answer.wire_name())).collect();
        format!("状態 [{}]・答え [{}]", statuses.join(" "), answers.join(" "))
    }
}

/// 変えてよい種類の名前を読む（v1 の種類だけ。v2 の予約の種類は Android と同じく常に not_applicable なので断る）。
///
/// # 戻り値
/// 読めたら Ok(種類)。知らない名前・v2 の予約の種類なら Err(説明)
pub fn read_settable_kind(word: &str) -> Result<PermissionKind, String> {
    let settable: Vec<&str> =
        PermissionKind::ALL.iter().filter(|kind| kind.is_implemented()).map(|kind| kind.wire_name()).collect();
    match PermissionKind::from_wire(word) {
        Some(kind) if kind.is_implemented() => Ok(kind),
        Some(_) => Err(format!("{word} は v2 の予約の種類で、状態は常に {} です（変えられるのは {}）",
            names::STATUS_NOT_APPLICABLE, settable.join(" / "))),
        None => Err(format!("{} は {} のどれかにしてください（{word}）", names::KEY_KIND, settable.join(" / "))),
    }
}

/// 起動時の状態の書き方を読む（項目は「種類=状態」だけ）。
///
/// # 戻り値
/// (読めた項目, 読めなかった項目の説明)
pub fn parse_statuses(text: &str) -> (Vec<(PermissionKind, PermissionStatus)>, Vec<String>) {
    let mut parsed = Vec::new();
    let mut errors = Vec::new();
    for entry in entries(text) {
        let result = split_pair(entry)
            .ok_or_else(|| format!("「種類=状態」の形にしてください（{entry}）"))
            .and_then(|(kind, status)| {
                let kind = read_settable_kind(kind)?;
                let status = PermissionStatus::from_wire(status)
                    .ok_or_else(|| PermissionStatus::describe_unknown(names::KEY_STATUS, status))?;
                Ok((kind, status))
            });
        match result {
            Ok(pair) => parsed.push(pair),
            Err(error) => errors.push(error),
        }
    }
    (parsed, errors)
}

/// 答えの書き方を読む（項目は「種類=答え」「all=答え」か、種類を書かない「答え」＝すべて）。
///
/// # 戻り値
/// (読めた項目, 読めなかった項目の説明)
pub fn parse_answers(text: &str) -> (Vec<(SimAnswerTarget, SimAnswer)>, Vec<String>) {
    let mut parsed = Vec::new();
    let mut errors = Vec::new();
    for entry in entries(text) {
        let result = match split_pair(entry) {
            Some((target, answer)) => SimAnswerTarget::parse(target).and_then(|target| Ok((target, SimAnswer::parse(answer)?))),
            None => SimAnswer::parse(entry).map(|answer| (SimAnswerTarget::All, answer)),
        };
        match result {
            Ok(pair) => parsed.push(pair),
            Err(error) => errors.push(error),
        }
    }
    (parsed, errors)
}

/// 項目に分ける（前後の空白を落とし、空の項目は飛ばす）。
fn entries(text: &str) -> impl Iterator<Item = &str> {
    text.split(ENTRY_SEPARATOR).map(str::trim).filter(|entry| !entry.is_empty())
}

/// 「左=右」を分ける（どちらも前後の空白を落とす。= が無ければ None）。
fn split_pair(entry: &str) -> Option<(&str, &str)> {
    entry.split_once(KEY_VALUE_SEPARATOR).map(|(left, right)| (left.trim(), right.trim()))
}

// ============================================================
//  ユニットテスト（環境変数は使わず、文字列から読む）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 状態: 種類=状態 を ; で並べる。空白・空の項目は許す。
    #[test]
    fn statuses_are_parsed_in_order() {
        let (parsed, errors) = parse_statuses(" post_notifications = denied ;; exact_alarm=needs_settings;full_screen_intent=granted ");
        assert!(errors.is_empty(), "{errors:?}");
        assert_eq!(
            parsed,
            vec![
                (PermissionKind::PostNotifications, PermissionStatus::Denied),
                (PermissionKind::ExactAlarm, PermissionStatus::NeedsSettings),
                (PermissionKind::FullScreenIntent, PermissionStatus::Granted),
            ]
        );
        assert_eq!(parse_statuses(""), (Vec::new(), Vec::new()));
    }

    /// 状態: 読めない項目（= が無い・知らない種類・知らない状態・v2 の予約の種類）は飛ばし、読めた項目は残す。
    #[test]
    fn broken_status_entries_are_skipped() {
        let (parsed, errors) =
            parse_statuses("post_notifications;camera=denied;exact_alarm=maybe;send_sms=granted;full_screen_intent=needs_settings");
        assert_eq!(parsed, vec![(PermissionKind::FullScreenIntent, PermissionStatus::NeedsSettings)]);
        assert_eq!(errors.len(), 4, "{errors:?}");
        assert!(errors[3].contains(names::STATUS_NOT_APPLICABLE), "v2 の予約は常に not_applicable と説明する: {}", errors[3]);
    }

    /// 答え: 種類を書かない項目は all。none は答えない。all の後の種類の指定も並びどおりに残る。
    #[test]
    fn answers_accept_all_and_kind_entries() {
        let (parsed, errors) = parse_answers("none; exact_alarm=granted ;all=denied;post_notifications=denied_permanently");
        assert!(errors.is_empty(), "{errors:?}");
        assert_eq!(
            parsed,
            vec![
                (SimAnswerTarget::All, SimAnswer::NoAnswer),
                (SimAnswerTarget::Kind(PermissionKind::ExactAlarm), SimAnswer::Status(PermissionStatus::Granted)),
                (SimAnswerTarget::All, SimAnswer::Status(PermissionStatus::Denied)),
                (SimAnswerTarget::Kind(PermissionKind::PostNotifications), SimAnswer::Status(PermissionStatus::DeniedPermanently)),
            ]
        );
        let (_, errors) = parse_answers("yes;record_audio=granted;exact_alarm=later");
        assert_eq!(errors.len(), 3, "{errors:?}");
    }

    /// 答えの語: 状態の名前と none を行き来できる。
    #[test]
    fn answer_words_round_trip() {
        for status in PermissionStatus::ALL {
            assert_eq!(SimAnswer::parse(status.wire_name()), Ok(SimAnswer::Status(status)));
        }
        assert_eq!(SimAnswer::parse(names::ANSWER_NONE), Ok(SimAnswer::NoAnswer));
        assert_eq!(SimAnswer::NoAnswer.wire_name(), names::ANSWER_NONE);
        assert!(SimAnswer::parse("Granted").is_err());
        assert_eq!(SimAnswerTarget::parse(names::KIND_ALL), Ok(SimAnswerTarget::All));
        assert_eq!(SimAnswerTarget::parse("exact_alarm"), Ok(SimAnswerTarget::Kind(PermissionKind::ExactAlarm)));
    }

    /// 2 つの文字列から設定を作る（無い・空なら空の設定）。
    #[test]
    fn config_from_texts() {
        assert!(SimPermissionConfig::from_texts(None, None).is_empty());
        assert!(SimPermissionConfig::from_texts(Some(""), Some(" ; ")).is_empty());
        let config = SimPermissionConfig::from_texts(Some("post_notifications=denied"), Some("none"));
        assert_eq!(config.statuses, vec![(PermissionKind::PostNotifications, PermissionStatus::Denied)]);
        assert_eq!(config.answers, vec![(SimAnswerTarget::All, SimAnswer::NoAnswer)]);
        assert_eq!(config.describe(), "状態 [post_notifications=denied]・答え [all=none]");
    }
}
