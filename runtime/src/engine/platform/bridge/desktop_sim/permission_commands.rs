// ============================================================
//  platform/bridge/desktop_sim/permission_commands.rs — 模擬の権限の命令（W1-5。2026-10-01 に状態の操作を追加）
//
//  Android のメインプロセスの Java（platform/local/Permission*Command・platform/permission/）の代わり
//  （同じ命令に同じ形の JSON で答える）。デスクトップに OS の権限は無いので、状態は模擬の表（permission_state.rs）が持つ。
//  既定は v1 の種類（通知・正確なアラーム・フルスクリーン通知）が granted、v2 の予約の種類（録音・SMS）が not_applicable
//  （2026-09 までの模擬と同じ）。起動時の状態と答えは環境変数（permission_config.rs）で与えられる:
//    permission.check         … 表の状態。返答 { kind, status, simulated }
//    permission.request       … 返答 { kind, request_id, simulated }。表の状態と模擬の利用者の答えから新しい状態を決め
//                               （permission_state::decide_request）、次のフレームでイベント
//                               platform.permission_result { request_id, kind, status, simulated } を届ける（確認の画面は出さない）。
//                               状態が変わったら続けて platform.permission_changed（Android でも結果の後に届く順）
//    permission.open_settings … 受け付けてログだけ（設定の画面は無い）。返答 { kind, simulated }
//  模擬だけの命令（Android には無く unknown_method。IPC の PLATFORM_SIM とスクリプトの PlatformDiagnostics が使う）:
//    permission.sim_set    { kind, status } … 状態を変える。変わったらすぐ platform.permission_changed を積む
//                                             （Android は前面へ戻ったときに気づくが、模擬は変えたときに知らせる）。返答 { kind, status, changed }
//    permission.sim_answer { kind, answer } … 求めたときの答えを決める（kind は all も可・answer は状態の名前か none）。返答 { kind, answer }
// ============================================================

use std::sync::atomic::Ordering;

use serde_json::{json, Map, Value};

use super::permission_config::{read_settable_kind, SimAnswer, SimAnswerTarget};
use super::{DesktopSimBridge, SimFailure, SimResult};
use crate::engine::platform::bridge::permission::{read_kind, PermissionKind, PermissionStatus};
use crate::engine::platform::bridge::wire::{alarm as alarm_names, permission as names};
use crate::engine::platform::bridge::LOG_PREFIX;

impl DesktopSimBridge {
    /// permission.check: 模擬の表の状態を返す。
    pub(super) fn handle_permission_check(&self, request: &Value) -> SimResult {
        let kind = read_kind(request).map_err(SimFailure::invalid_argument)?;
        let mut fields = Map::new();
        fields.insert(names::KEY_KIND.into(), Value::from(kind.wire_name()));
        fields.insert(names::KEY_STATUS.into(), Value::from(self.permissions.status(kind).wire_name()));
        fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }

    /// permission.request: 要求の ID を払い出し、答えを当てた結果のイベントをすぐ積む（次のフレームでスクリプトへ届く）。
    pub(super) fn handle_permission_request(&self, request: &Value) -> SimResult {
        let kind = read_kind(request).map_err(SimFailure::invalid_argument)?;
        let request_id = self.next_permission_request_id.fetch_add(1, Ordering::Relaxed);
        let before = self.permissions.status(kind);
        let answer = self.permissions.answer(kind);
        let (status, changed) = self.permissions.apply_request(kind);
        let data = json!({
            names::KEY_REQUEST_ID: request_id,
            names::KEY_KIND: kind.wire_name(),
            names::KEY_STATUS: status.wire_name(),
            alarm_names::KEY_SIMULATED: true,
        });
        self.push_event(names::EVENT_RESULT, self.clock.now_utc_ms(), data);
        eprintln!(
            "{LOG_PREFIX} 模擬: 権限 {} を求めました（要求 {request_id}: {} → {}・模擬の利用者の答え {}。確認の画面は出しません）",
            kind.wire_name(),
            before.wire_name(),
            status.wire_name(),
            answer.wire_name()
        );
        if changed {
            // Android でも確認の画面で許可したときは permission_result → permission_changed の順に届く
            self.queue_permission_changed(kind, before, status);
        }
        let mut fields = Map::new();
        fields.insert(names::KEY_KIND.into(), Value::from(kind.wire_name()));
        fields.insert(names::KEY_REQUEST_ID.into(), Value::from(request_id));
        fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }

    /// permission.open_settings: 受け付けてログだけ（デスクトップに設定の画面は無い）。
    pub(super) fn handle_permission_open_settings(&self, request: &Value) -> SimResult {
        let kind = read_kind(request).map_err(SimFailure::invalid_argument)?;
        eprintln!(
            "{LOG_PREFIX} 模擬: 権限 {} の設定の画面を開く命令（デスクトップでは開きません。変えるなら PLATFORM_SIM:permission,{},<状態>）",
            kind.wire_name(),
            kind.wire_name()
        );
        let mut fields = Map::new();
        fields.insert(names::KEY_KIND.into(), Value::from(kind.wire_name()));
        fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }

    /// permission.sim_set（模擬だけ）: 状態を変え、変わったらすぐ platform.permission_changed を積む。
    pub(super) fn handle_permission_sim_set(&self, request: &Value) -> SimResult {
        let kind = read_settable_kind(read_word(request, names::KEY_KIND)).map_err(SimFailure::invalid_argument)?;
        let word = read_word(request, names::KEY_STATUS);
        let status = PermissionStatus::from_wire(word)
            .ok_or_else(|| SimFailure::invalid_argument(PermissionStatus::describe_unknown(names::KEY_STATUS, word)))?;
        let before = self.permissions.status(kind);
        let changed = self.permissions.set_status(kind, status);
        if changed {
            self.queue_permission_changed(kind, before, status);
        } else {
            eprintln!("{LOG_PREFIX} 模擬: 権限 {} は既に {}（イベントは出しません）", kind.wire_name(), status.wire_name());
        }
        let mut fields = Map::new();
        fields.insert(names::KEY_KIND.into(), Value::from(kind.wire_name()));
        fields.insert(names::KEY_STATUS.into(), Value::from(status.wire_name()));
        fields.insert(names::KEY_CHANGED.into(), Value::Bool(changed));
        Ok(fields)
    }

    /// permission.sim_answer（模擬だけ）: 求めたときの模擬の利用者の答えを決める。
    pub(super) fn handle_permission_sim_answer(&self, request: &Value) -> SimResult {
        let target = SimAnswerTarget::parse(read_word(request, names::KEY_KIND)).map_err(SimFailure::invalid_argument)?;
        let answer = SimAnswer::parse(read_word(request, names::KEY_ANSWER)).map_err(SimFailure::invalid_argument)?;
        self.permissions.set_answer(target, answer);
        eprintln!("{LOG_PREFIX} 模擬: 権限 {} を求めたときの答えを {} にしました", target.wire_name(), answer.wire_name());
        let mut fields = Map::new();
        fields.insert(names::KEY_KIND.into(), Value::from(target.wire_name()));
        fields.insert(names::KEY_ANSWER.into(), Value::from(answer.wire_name()));
        Ok(fields)
    }

    /// platform.permission_changed を積む（data は Android と同じ { kind, status } に模擬の印）。
    fn queue_permission_changed(&self, kind: PermissionKind, before: PermissionStatus, status: PermissionStatus) {
        let data = json!({
            names::KEY_KIND: kind.wire_name(),
            names::KEY_STATUS: status.wire_name(),
            alarm_names::KEY_SIMULATED: true,
        });
        self.push_event(names::EVENT_CHANGED, self.clock.now_utc_ms(), data);
        eprintln!("{LOG_PREFIX} 模擬: 権限 {} の状態が変わりました: {} → {}", kind.wire_name(), before.wire_name(), status.wire_name());
    }
}

/// 引数の文字列の欄を読む（無い・文字列でなければ空文字＝読み手の検査で invalid_argument になる）。
fn read_word<'a>(request: &'a Value, key: &str) -> &'a str {
    request.get(key).and_then(Value::as_str).unwrap_or_default()
}

// ============================================================
//  ユニットテスト（check → request → permission_result の流れと、模擬だけの状態の操作）
// ============================================================

#[cfg(test)]
mod tests {
    use serde_json::{json, Value};

    use super::super::permission_config::SimPermissionConfig;
    use super::super::DesktopSimBridge;
    use crate::engine::platform::bridge::permission::PermissionKind;
    use crate::engine::platform::bridge::wire::{self, alarm as alarm_names, permission as names};
    use crate::engine::platform::bridge::PlatformBridge;

    /// 命令を送って返答の JSON を読む（テスト用）。
    fn call(sim: &DesktopSimBridge, method: &str, request: Value) -> Value {
        serde_json::from_str(&sim.invoke(names::MODULE, method, &request.to_string()).unwrap()).unwrap()
    }

    /// 積まれたイベントを (名前, data) の並びで取り出す（テスト用）。
    fn events(sim: &DesktopSimBridge) -> Vec<(String, Value)> {
        sim.poll_events()
            .iter()
            .map(|text| serde_json::from_str::<Value>(text).unwrap())
            .map(|event| (event[wire::KEY_NAME].as_str().unwrap().to_string(), event[wire::KEY_DATA].clone()))
            .collect()
    }

    /// 起動時の設定を指定して模擬を作る（環境変数は使わない）。
    fn sim_with(statuses: &str, answers: &str) -> DesktopSimBridge {
        let sim = DesktopSimBridge::new();
        sim.replace_permission_config(SimPermissionConfig::from_texts(Some(statuses), Some(answers)));
        sim
    }

    /// check: v1 の 3 種は granted、v2 の 2 種は not_applicable。知らない種類は invalid_argument。
    #[test]
    fn check_reports_simulated_statuses() {
        let sim = sim_with("", "");
        for kind in PermissionKind::ALL {
            let reply = call(&sim, names::METHOD_CHECK, json!({ "kind": kind.wire_name() }));
            let expected = if kind.is_implemented() { names::STATUS_GRANTED } else { names::STATUS_NOT_APPLICABLE };
            assert_eq!(reply[names::KEY_STATUS], Value::from(expected), "{}", kind.wire_name());
            assert_eq!(reply[names::KEY_KIND], Value::from(kind.wire_name()));
        }
        let unknown = call(&sim, names::METHOD_CHECK, json!({ "kind": "camera" }));
        assert_eq!(unknown[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT));
    }

    /// request: 要求の ID は 1 から増え、次の poll_events で permission_result（同じ ID・種類・状態）が届く。
    #[test]
    fn request_emits_result_event() {
        let sim = sim_with("", "");
        let first = call(&sim, names::METHOD_REQUEST, json!({ "kind": names::KIND_POST_NOTIFICATIONS }));
        let second = call(&sim, names::METHOD_REQUEST, json!({ "kind": names::KIND_SEND_SMS }));
        assert_eq!(first[names::KEY_REQUEST_ID], Value::from(names::FIRST_REQUEST_ID));
        assert_eq!(second[names::KEY_REQUEST_ID], Value::from(names::FIRST_REQUEST_ID + 1));

        let results: Vec<(Value, Value, Value)> = events(&sim)
            .into_iter()
            .map(|(name, data)| {
                assert_eq!(name, names::EVENT_RESULT);
                (data[names::KEY_REQUEST_ID].clone(), data[names::KEY_KIND].clone(), data[names::KEY_STATUS].clone())
            })
            .collect();
        assert_eq!(
            results,
            vec![
                (Value::from(names::FIRST_REQUEST_ID), Value::from(names::KIND_POST_NOTIFICATIONS), Value::from(names::STATUS_GRANTED)),
                (Value::from(names::FIRST_REQUEST_ID + 1), Value::from(names::KIND_SEND_SMS), Value::from(names::STATUS_NOT_APPLICABLE)),
            ]
        );
        assert!(sim.poll_events().is_empty());
    }

    /// request: 拒否の状態は模擬の利用者の答えを当て、変わったら result → changed の順に届く。答えないなら result だけ。
    #[test]
    fn request_applies_answer_and_reports_change() {
        let sim = sim_with("post_notifications=denied;exact_alarm=needs_settings", "exact_alarm=none");
        call(&sim, names::METHOD_REQUEST, json!({ "kind": names::KIND_POST_NOTIFICATIONS }));
        let got = events(&sim);
        assert_eq!(got.len(), 2, "{got:?}");
        assert_eq!((got[0].0.as_str(), &got[0].1[names::KEY_STATUS]), (names::EVENT_RESULT, &Value::from(names::STATUS_GRANTED)));
        assert_eq!((got[1].0.as_str(), &got[1].1[names::KEY_STATUS]), (names::EVENT_CHANGED, &Value::from(names::STATUS_GRANTED)));
        assert_eq!(got[1].1[alarm_names::KEY_SIMULATED], Value::Bool(true));
        let check = call(&sim, names::METHOD_CHECK, json!({ "kind": names::KIND_POST_NOTIFICATIONS }));
        assert_eq!(check[names::KEY_STATUS], Value::from(names::STATUS_GRANTED));

        call(&sim, names::METHOD_REQUEST, json!({ "kind": names::KIND_EXACT_ALARM }));
        let got = events(&sim);
        assert_eq!(got.len(), 1, "答えないなら changed は出ない: {got:?}");
        assert_eq!(got[0].1[names::KEY_STATUS], Value::from(names::STATUS_NEEDS_SETTINGS));
    }

    /// open_settings: 受け付けるだけ（イベントは積まない・状態も変えない）。種類の誤りは invalid_argument。
    #[test]
    fn open_settings_is_accepted_without_events() {
        let sim = sim_with("exact_alarm=needs_settings", "");
        let reply = call(&sim, names::METHOD_OPEN_SETTINGS, json!({ "kind": names::KIND_EXACT_ALARM }));
        assert_eq!(reply[wire::KEY_OK], Value::Bool(true));
        assert!(sim.poll_events().is_empty());
        let check = call(&sim, names::METHOD_CHECK, json!({ "kind": names::KIND_EXACT_ALARM }));
        assert_eq!(check[names::KEY_STATUS], Value::from(names::STATUS_NEEDS_SETTINGS));
        let bad = call(&sim, names::METHOD_OPEN_SETTINGS, json!({}));
        assert_eq!(bad[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT));
    }

    /// sim_set: 変わったらすぐ changed、同じ値ならイベントなし。v2 の予約・知らない状態は invalid_argument。
    #[test]
    fn sim_set_changes_status_and_emits_changed() {
        let sim = sim_with("", "");
        let reply = call(&sim, names::METHOD_SIM_SET, json!({ "kind": names::KIND_FULL_SCREEN_INTENT, "status": names::STATUS_NEEDS_SETTINGS }));
        assert_eq!(reply[names::KEY_CHANGED], Value::Bool(true));
        let got = events(&sim);
        assert_eq!(got.len(), 1);
        assert_eq!(got[0].0, names::EVENT_CHANGED);
        assert_eq!(got[0].1[names::KEY_KIND], Value::from(names::KIND_FULL_SCREEN_INTENT));
        assert_eq!(got[0].1[names::KEY_STATUS], Value::from(names::STATUS_NEEDS_SETTINGS));

        let same = call(&sim, names::METHOD_SIM_SET, json!({ "kind": names::KIND_FULL_SCREEN_INTENT, "status": names::STATUS_NEEDS_SETTINGS }));
        assert_eq!(same[names::KEY_CHANGED], Value::Bool(false));
        assert!(sim.poll_events().is_empty());

        for bad in [
            json!({ "kind": names::KIND_SEND_SMS, "status": names::STATUS_GRANTED }),
            json!({ "kind": names::KIND_EXACT_ALARM, "status": "maybe" }),
            json!({ "status": names::STATUS_GRANTED }),
        ] {
            let reply = call(&sim, names::METHOD_SIM_SET, bad.clone());
            assert_eq!(reply[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT), "{bad}");
        }
    }

    /// sim_answer: all とそれぞれの種類。以後の request に当たる。Play の区切りで起動時の設定へ戻る。
    #[test]
    fn sim_answer_is_used_by_request_and_reset_by_session() {
        let sim = sim_with("post_notifications=denied", "");
        let reply = call(&sim, names::METHOD_SIM_ANSWER, json!({ "kind": names::KIND_ALL, "answer": names::STATUS_DENIED_PERMANENTLY }));
        assert_eq!(reply[names::KEY_ANSWER], Value::from(names::STATUS_DENIED_PERMANENTLY));
        call(&sim, names::METHOD_REQUEST, json!({ "kind": names::KIND_POST_NOTIFICATIONS }));
        let got = events(&sim);
        assert_eq!(got[0].1[names::KEY_STATUS], Value::from(names::STATUS_DENIED_PERMANENTLY));
        assert_eq!(got[1].0, names::EVENT_CHANGED);

        let bad = call(&sim, names::METHOD_SIM_ANSWER, json!({ "kind": names::KIND_ALL, "answer": "yes" }));
        assert_eq!(bad[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT));

        sim.reset_session();
        let check = call(&sim, names::METHOD_CHECK, json!({ "kind": names::KIND_POST_NOTIFICATIONS }));
        assert_eq!(check[names::KEY_STATUS], Value::from(names::STATUS_DENIED), "Play の区切りで起動時の状態へ戻る");
    }
}
