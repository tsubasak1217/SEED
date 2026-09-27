// ============================================================
//  platform/bridge/desktop_sim/permission_commands.rs — 模擬の権限の命令（W1-5）
//
//  Android のメインプロセスの Java（platform/local/Permission*Command・platform/permission/）の代わり
//  （同じ命令に同じ形の JSON で答える）。デスクトップに OS の権限は無いので、v1 の種類（通知・正確なアラーム・
//  フルスクリーン通知）は常に granted、v2 の予約の種類（録音・SMS）は not_applicable をそのまま返す:
//    permission.check         … 返答 { kind, status, simulated }
//    permission.request       … 返答 { kind, request_id, simulated }。結果は次のフレームでイベント
//                               platform.permission_result { request_id, kind, status, simulated }（確認の画面は出さない）
//    permission.open_settings … 受け付けてログだけ。返答 { kind, simulated }
//  platform.permission_changed（前面へ戻ったときの変化）は模擬では起きない。
// ============================================================

use std::sync::atomic::Ordering;

use serde_json::{json, Map, Value};

use super::{DesktopSimBridge, SimFailure, SimResult};
use crate::engine::platform::bridge::permission::{read_kind, PermissionKind};
use crate::engine::platform::bridge::wire::{alarm as alarm_names, permission as names};
use crate::engine::platform::bridge::LOG_PREFIX;

impl DesktopSimBridge {
    /// permission.check: 模擬の状態を返す。
    pub(super) fn handle_permission_check(&self, request: &Value) -> SimResult {
        let kind = read_kind(request).map_err(SimFailure::invalid_argument)?;
        let mut fields = Map::new();
        fields.insert(names::KEY_KIND.into(), Value::from(kind.wire_name()));
        fields.insert(names::KEY_STATUS.into(), Value::from(simulated_status(kind)));
        fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }

    /// permission.request: 要求の ID を払い出し、結果のイベントをすぐ積む（次のフレームでスクリプトへ届く）。
    pub(super) fn handle_permission_request(&self, request: &Value) -> SimResult {
        let kind = read_kind(request).map_err(SimFailure::invalid_argument)?;
        let request_id = self.next_permission_request_id.fetch_add(1, Ordering::Relaxed);
        let status = simulated_status(kind);
        let data = json!({
            names::KEY_REQUEST_ID: request_id,
            names::KEY_KIND: kind.wire_name(),
            names::KEY_STATUS: status,
            alarm_names::KEY_SIMULATED: true,
        });
        self.push_event(names::EVENT_RESULT, self.clock.now_utc_ms(), data);
        eprintln!("{LOG_PREFIX} 模擬: 権限 {} を求めました（要求 {request_id} → {status}。確認の画面は出しません）", kind.wire_name());
        let mut fields = Map::new();
        fields.insert(names::KEY_KIND.into(), Value::from(kind.wire_name()));
        fields.insert(names::KEY_REQUEST_ID.into(), Value::from(request_id));
        fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }

    /// permission.open_settings: 受け付けてログだけ（デスクトップに設定の画面は無い）。
    pub(super) fn handle_permission_open_settings(&self, request: &Value) -> SimResult {
        let kind = read_kind(request).map_err(SimFailure::invalid_argument)?;
        eprintln!("{LOG_PREFIX} 模擬: 権限 {} の設定の画面を開く命令（デスクトップでは何もしません）", kind.wire_name());
        let mut fields = Map::new();
        fields.insert(names::KEY_KIND.into(), Value::from(kind.wire_name()));
        fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }
}

/// 模擬の状態（v1 の種類は許可、v2 の予約の種類は not_applicable）。
fn simulated_status(kind: PermissionKind) -> &'static str {
    if kind.is_implemented() { names::STATUS_GRANTED } else { names::STATUS_NOT_APPLICABLE }
}

// ============================================================
//  ユニットテスト（check → request → permission_result の流れ）
// ============================================================

#[cfg(test)]
mod tests {
    use serde_json::{json, Value};

    use super::super::DesktopSimBridge;
    use crate::engine::platform::bridge::permission::PermissionKind;
    use crate::engine::platform::bridge::wire::{self, alarm as alarm_names, permission as names};
    use crate::engine::platform::bridge::PlatformBridge;

    /// 命令を送って返答の JSON を読む（テスト用）。
    fn call(sim: &DesktopSimBridge, method: &str, request: Value) -> Value {
        serde_json::from_str(&sim.invoke(names::MODULE, method, &request.to_string()).unwrap()).unwrap()
    }

    /// check: v1 の 3 種は granted、v2 の 2 種は not_applicable。知らない種類は invalid_argument。
    #[test]
    fn check_reports_simulated_statuses() {
        let sim = DesktopSimBridge::new();
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
        let sim = DesktopSimBridge::new();
        let first = call(&sim, names::METHOD_REQUEST, json!({ "kind": names::KIND_POST_NOTIFICATIONS }));
        let second = call(&sim, names::METHOD_REQUEST, json!({ "kind": names::KIND_SEND_SMS }));
        assert_eq!(first[names::KEY_REQUEST_ID], Value::from(names::FIRST_REQUEST_ID));
        assert_eq!(second[names::KEY_REQUEST_ID], Value::from(names::FIRST_REQUEST_ID + 1));

        let events: Vec<Value> = sim.poll_events().iter().map(|text| serde_json::from_str(text).unwrap()).collect();
        assert_eq!(events.len(), 2);
        let results: Vec<(Value, Value, Value)> = events
            .iter()
            .map(|event| {
                assert_eq!(event[wire::KEY_NAME], Value::from(names::EVENT_RESULT));
                let data = &event[wire::KEY_DATA];
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

    /// open_settings: 受け付けるだけ（イベントは積まない）。種類の誤りは invalid_argument。
    #[test]
    fn open_settings_is_accepted_without_events() {
        let sim = DesktopSimBridge::new();
        let reply = call(&sim, names::METHOD_OPEN_SETTINGS, json!({ "kind": names::KIND_EXACT_ALARM }));
        assert_eq!(reply[wire::KEY_OK], Value::Bool(true));
        assert!(sim.poll_events().is_empty());
        let bad = call(&sim, names::METHOD_OPEN_SETTINGS, json!({}));
        assert_eq!(bad[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT));
    }
}
