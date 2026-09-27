// ============================================================
//  platform/bridge/desktop_sim/notification_commands.rs — 模擬の通知の命令（W1-5）
//
//  Android の :seed_platform の NotificationModule の代わり（同じ命令に同じ形の JSON で答える）:
//    notification.ensure_channel … 引数を実機と同じ規則で検査し（bridge::notification::request）、チャネルを作る。返答 { channel_id, simulated }
//    notification.show           … 検査して「出ている通知」へ入れ、[SEED PLATFORM] 通知: … のログを出す。返答 { id, simulated }。
//                                  チャネルが無ければ channel_not_found（実機と同じ。ensure_channel を先に呼ぶ）
//    notification.cancel         … 出ている通知から外す（無い ID でも成功）。返答 { id, simulated }
//    notification.are_enabled    … 常に true（デスクトップに通知の許可は無い）。返答 { enabled: true, simulated }
//  画面には何も出さない。通知の操作（ボタン）を押す手段は無い（起動理由は常に launcher。app_commands.rs）。
//  状態は notification_state.rs。エディタの Play の区切りで空にする。
// ============================================================

use serde_json::{Map, Value};

use super::{DesktopSimBridge, SimFailure, SimResult};
use crate::engine::platform::bridge::notification::{is_known_category, read_channel, read_id, read_show, NotificationRequest};
use crate::engine::platform::bridge::wire::{alarm as alarm_names, notification as names};
use crate::engine::platform::bridge::LOG_PREFIX;

impl DesktopSimBridge {
    /// notification.ensure_channel: 検査してチャネルを作る（あれば名前と説明を変える）。
    pub(super) fn handle_notification_ensure_channel(&self, request: &Value) -> SimResult {
        let channel = read_channel(request).map_err(SimFailure::invalid_argument)?;
        let channel_id = channel.channel_id.clone();
        let summary = format!("{}・重要度 {}", channel.name, channel.importance);
        let created = self.notifications.ensure_channel(channel);
        eprintln!("{LOG_PREFIX} 模擬: 通知チャネル {channel_id}（{summary}）を{}", if created { "作りました" } else { "更新しました" });
        let mut fields = Map::new();
        fields.insert(names::KEY_CHANNEL_ID.into(), Value::from(channel_id));
        fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }

    /// notification.show: 検査して「出ている通知」へ入れる（同じ ID は置き換え）。
    pub(super) fn handle_notification_show(&self, request: &Value) -> SimResult {
        let notification = read_show(request).map_err(SimFailure::invalid_argument)?;
        if !self.notifications.has_channel(&notification.channel_id) {
            return Err(SimFailure {
                reason: names::ERROR_CHANNEL_NOT_FOUND,
                detail: Some(format!("チャネル {} がありません（先に ensure_channel）", notification.channel_id)),
            });
        }
        let id = notification.id.clone();
        let line = describe(&notification);
        let replaced = self.notifications.show(notification, self.clock.now_utc_ms());
        eprintln!("{LOG_PREFIX} 通知: {line}{}", if replaced { "・置き換え" } else { "" });
        let mut fields = Map::new();
        fields.insert(names::KEY_ID.into(), Value::from(id));
        fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }

    /// notification.cancel: 出ている通知から外す（無い ID でも成功＝冪等）。
    pub(super) fn handle_notification_cancel(&self, request: &Value) -> SimResult {
        let id = read_id(request).map_err(SimFailure::invalid_argument)?;
        let existed = self.notifications.cancel(&id);
        let remaining = self.notifications.shown().len();
        if existed {
            eprintln!("{LOG_PREFIX} 通知を消しました: {id}（出ている通知 {remaining} 件）");
        } else {
            eprintln!("{LOG_PREFIX} 通知を消す命令: {id} は出ていません（出ている通知 {remaining} 件）");
        }
        let mut fields = Map::new();
        fields.insert(names::KEY_ID.into(), Value::from(id));
        fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }

    /// notification.are_enabled: デスクトップは常に有効。
    pub(super) fn handle_notification_are_enabled(&self, _request: &Value) -> SimResult {
        let mut fields = Map::new();
        fields.insert(names::KEY_ENABLED.into(), Value::Bool(true));
        fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }
}

/// ログの 1 行（題 — 本文（ID・チャネル・常駐・種類・操作・payload））。
fn describe(notification: &NotificationRequest) -> String {
    let actions: Vec<String> = notification.actions.iter().map(|action| format!("{}:{}", action.id, action.label)).collect();
    let category = match notification.category.as_str() {
        "" => "なし".to_string(),
        known if is_known_category(known) => known.to_string(),
        unknown => format!("{unknown}（知らない種類なので実機では付けない）"),
    };
    format!(
        "{} — {}（id {}・チャネル {}・{}・種類 {category}・操作 [{}]・payload {}）",
        notification.title,
        notification.body,
        notification.id,
        notification.channel_id,
        if notification.ongoing { "常駐" } else { "普通" },
        actions.join(", "),
        notification.payload_json
    )
}

// ============================================================
//  ユニットテスト（チャネル → 出す → 一覧 → 消す、と誤り・Play の区切り）
// ============================================================

#[cfg(test)]
mod tests {
    use serde_json::{json, Value};

    use super::super::DesktopSimBridge;
    use crate::engine::platform::bridge::wire::{self, alarm as alarm_names, notification as names};
    use crate::engine::platform::bridge::PlatformBridge;

    /// 命令を送って返答の JSON を読む（テスト用）。
    fn call(sim: &DesktopSimBridge, method: &str, request: Value) -> Value {
        serde_json::from_str(&sim.invoke(names::MODULE, method, &request.to_string()).unwrap()).unwrap()
    }

    /// 確かめ用のチャネルを作る。
    fn ensure_channel(sim: &DesktopSimBridge) {
        let reply = call(sim, names::METHOD_ENSURE_CHANNEL, json!({ "channel_id": "smoke", "name": "確かめ", "importance": "high" }));
        assert_eq!(reply[wire::KEY_OK], Value::Bool(true), "{reply}");
        assert_eq!(reply[names::KEY_CHANNEL_ID], Value::from("smoke"));
    }

    /// チャネル → 出す（操作 2 つ・常駐）→ 一覧に 1 件 → 同じ ID は置き換え → 消す → 空。are_enabled は true。
    #[test]
    fn show_list_and_cancel() {
        let sim = DesktopSimBridge::new();
        ensure_channel(&sim);
        let show = json!({
            "id": "note", "channel_id": "smoke", "title": "題", "body": "本文", "ongoing": true, "category": "status",
            "actions": [ { "id": "stop", "label": "止める" }, { "id": "later", "label": "あとで" } ], "payload_json": "{}",
        });
        let reply = call(&sim, names::METHOD_SHOW, show.clone());
        assert_eq!((reply[wire::KEY_OK].clone(), reply[names::KEY_ID].clone()), (Value::Bool(true), Value::from("note")));
        assert_eq!(reply[alarm_names::KEY_SIMULATED], Value::Bool(true));
        let shown = sim.notifications.shown();
        assert_eq!(shown.len(), 1);
        assert!(shown[0].request.ongoing);
        assert_eq!(shown[0].request.actions.len(), 2);

        let mut replacement = show;
        replacement["title"] = json!("題（更新）");
        call(&sim, names::METHOD_SHOW, replacement);
        let shown = sim.notifications.shown();
        assert_eq!((shown.len(), shown[0].request.title.as_str()), (1, "題（更新）"), "同じ ID は置き換え");

        let cancel = call(&sim, names::METHOD_CANCEL, json!({ "id": "note" }));
        assert_eq!(cancel[wire::KEY_OK], Value::Bool(true));
        assert!(sim.notifications.shown().is_empty());
        let again = call(&sim, names::METHOD_CANCEL, json!({ "id": "note" }));
        assert_eq!(again[wire::KEY_OK], Value::Bool(true), "無い ID の取り消しも成功（冪等）");

        let enabled = call(&sim, names::METHOD_ARE_ENABLED, json!({}));
        assert_eq!(enabled[names::KEY_ENABLED], Value::Bool(true));
        assert!(sim.poll_events().is_empty(), "通知の命令はイベントを積まない");
    }

    /// チャネルが無い → channel_not_found。引数の誤り → invalid_argument（通知は増えない）。
    #[test]
    fn show_errors() {
        let sim = DesktopSimBridge::new();
        let missing = call(&sim, names::METHOD_SHOW, json!({ "id": "a", "channel_id": "nope" }));
        assert_eq!(missing[wire::KEY_ERROR], Value::from(names::ERROR_CHANNEL_NOT_FOUND));
        ensure_channel(&sim);
        let four_actions: Vec<Value> = (0..4).map(|i| json!({ "id": format!("a{i}"), "label": "L" })).collect();
        for bad in [
            json!({ "channel_id": "smoke" }),
            json!({ "id": "a", "channel_id": "smoke", "actions": four_actions }),
            json!({ "id": "a", "channel_id": "smoke", "ongoing": "yes" }),
        ] {
            let reply = call(&sim, names::METHOD_SHOW, bad.clone());
            assert_eq!(reply[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT), "{bad}");
        }
        let reserved = call(&sim, names::METHOD_ENSURE_CHANNEL, json!({ "channel_id": "seed_platform_alarm", "name": "x" }));
        assert_eq!(reserved[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT));
        assert!(sim.notifications.shown().is_empty());
    }

    /// Play の区切りでチャネルも通知も消える（次の Play では ensure_channel からやり直す）。
    #[test]
    fn reset_session_clears_notifications_and_channels() {
        let sim = DesktopSimBridge::new();
        ensure_channel(&sim);
        call(&sim, names::METHOD_SHOW, json!({ "id": "a", "channel_id": "smoke" }));
        assert_eq!(sim.notifications.shown().len(), 1);
        sim.reset_session();
        assert!(sim.notifications.shown().is_empty());
        let after = call(&sim, names::METHOD_SHOW, json!({ "id": "a", "channel_id": "smoke" }));
        assert_eq!(after[wire::KEY_ERROR], Value::from(names::ERROR_CHANNEL_NOT_FOUND));
    }
}
