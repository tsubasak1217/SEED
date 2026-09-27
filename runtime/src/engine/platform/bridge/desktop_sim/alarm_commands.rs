// ============================================================
//  platform/bridge/desktop_sim/alarm_commands.rs — 模擬の目覚ましの命令と発火（W1-3）
//
//  Android の :seed_platform の AlarmModule・AlarmReceiver の代わり（同じ命令に同じ形の JSON で答える）:
//    alarm.schedule           … 引数を実機と同じ規則で検査し（bridge::alarm::request）、予約表へ（同じ ID は置き換え）
//    alarm.cancel / cancel_all / list … 予約表の操作
//    alarm.can_schedule_exact … 常に true（デスクトップには権限が無い）
//  発火: エンジンがフレームの頭で呼ぶ poll_events の中で、壁時計が予定時刻を過ぎた予約を取り出して
//  platform.alarm.fired { id, scheduled_at_utc_ms, fired_at_utc_ms, payload_json, simulated } を積み（一回限り）、
//  鳴動の状態へ渡す（W1-4a。ring_commands.rs の start_ringing。鳴動中なら待ち行列と platform.alarm.queued）。
//  エディタの Play の中だけで進む（Play していない・一時停止中はフレームが回らないので鳴らない。止めれば予約は消える）。
//  音は鳴らさない（[SEED PLATFORM] のログだけ）。
// ============================================================

use serde_json::{json, Map, Value};

use super::{DesktopSimBridge, SimFailure, SimResult};
use crate::engine::platform::bridge::alarm::{read_id, read_schedule};
use crate::engine::platform::bridge::wire::{self, alarm as names};
use crate::engine::platform::bridge::LOG_PREFIX;

impl DesktopSimBridge {
    /// alarm.schedule: 検査して予約表へ入れる。
    pub(super) fn handle_alarm_schedule(&self, request: &Value) -> SimResult {
        let alarm = read_schedule(request).map_err(SimFailure::invalid_argument)?;
        let now = self.clock.now_utc_ms();
        let (id, trigger_at) = (alarm.id.clone(), alarm.trigger_at_utc_ms);
        let replaced = self.alarms.schedule(alarm, now).map_err(SimFailure::from)?;
        eprintln!(
            "{LOG_PREFIX} 模擬の目覚まし {id} を予約しました（あと {} ms{}）",
            trigger_at - now,
            if replaced { "・置き換え" } else { "" }
        );
        let mut fields = Map::new();
        fields.insert(names::KEY_ID.into(), Value::from(id));
        fields.insert(names::KEY_TRIGGER_AT_UTC_MS.into(), Value::from(trigger_at));
        fields.insert(names::KEY_REPLACED.into(), Value::Bool(replaced));
        fields.insert(names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }

    /// alarm.cancel: 1 つ取り消す（無い ID でも成功）。
    pub(super) fn handle_alarm_cancel(&self, request: &Value) -> SimResult {
        let id = read_id(request).map_err(SimFailure::invalid_argument)?;
        let existed = self.alarms.cancel(&id);
        let mut fields = Map::new();
        fields.insert(names::KEY_ID.into(), Value::from(id));
        fields.insert(names::KEY_EXISTED.into(), Value::Bool(existed));
        Ok(fields)
    }

    /// alarm.cancel_all: 全部取り消す。
    pub(super) fn handle_alarm_cancel_all(&self, _request: &Value) -> SimResult {
        let mut fields = Map::new();
        fields.insert(names::KEY_COUNT.into(), Value::from(self.alarms.cancel_all()));
        Ok(fields)
    }

    /// alarm.list: 予約表の一覧（予定時刻の順）。
    pub(super) fn handle_alarm_list(&self, _request: &Value) -> SimResult {
        let alarms: Vec<Value> = self.alarms.list().iter().map(|alarm| alarm.to_json()).collect();
        let mut fields = Map::new();
        fields.insert(names::KEY_ALARMS.into(), Value::from(alarms));
        Ok(fields)
    }

    /// alarm.can_schedule_exact: デスクトップは常に張れる。
    pub(super) fn handle_alarm_can_schedule_exact(&self, _request: &Value) -> SimResult {
        let mut fields = Map::new();
        fields.insert(names::KEY_CAN_SCHEDULE_EXACT.into(), Value::Bool(true));
        fields.insert(names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }

    /// 壁時計が予定時刻を過ぎた予約を取り出し、platform.alarm.fired を積んで鳴動へ渡す（poll_events の頭で呼ぶ）。
    pub(super) fn fire_due_alarms(&self) {
        let now = self.clock.now_utc_ms();
        for alarm in self.alarms.take_due(now) {
            let request = &alarm.request;
            let seq = self.next_event_seq();
            let data = json!({
                names::KEY_ID: request.id,
                names::KEY_SCHEDULED_AT_UTC_MS: request.trigger_at_utc_ms,
                names::KEY_FIRED_AT_UTC_MS: now,
                names::KEY_PAYLOAD_JSON: request.payload_json,
                names::KEY_SIMULATED: true,
            });
            let time_ms = u64::try_from(now).unwrap_or_default();
            self.events.push(wire::event_json(names::EVENT_FIRED, seq, time_ms, data));
            eprintln!(
                "{LOG_PREFIX} 模擬の目覚まし {} が鳴りました（予定から {} ms・音は鳴らしません）",
                request.id,
                now - request.trigger_at_utc_ms
            );
            // 鳴動へ渡す（実機の AlarmReceiver → RingControl.handOver と同じ。鳴動中なら待ち行列）
            self.start_ringing(alarm, now, now);
        }
    }
}

// ============================================================
//  ユニットテスト（手で進める時計で「予約 → 時刻の経過 → 発火」を確かめる。模擬はテストごとに作る）
// ============================================================

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use serde_json::{json, Value};

    use super::super::wall_clock::ManualClock;
    use super::super::DesktopSimBridge;
    use crate::engine::platform::bridge::wire::{self, alarm as names};
    use crate::engine::platform::bridge::PlatformBridge;

    /// 時計の始まり（UTC の epoch ミリ秒。2026-09 ごろ）。
    const START_MS: i64 = 1_790_000_000_000;
    /// 予約までの時間（ミリ秒）。
    const DELAY_MS: i64 = 5_000;

    /// 手で進める時計つきの模擬（テスト用）。
    fn sim() -> (DesktopSimBridge, Arc<ManualClock>) {
        let clock = Arc::new(ManualClock::starting_at(START_MS));
        (DesktopSimBridge::with_clock(clock.clone()), clock)
    }

    /// 命令を送って返答の JSON を読む（テスト用）。
    fn call(sim: &DesktopSimBridge, method: &str, request: Value) -> Value {
        serde_json::from_str(&sim.invoke(names::MODULE, method, &request.to_string()).unwrap()).unwrap()
    }

    /// 予約の引数（テスト用）。
    fn request(id: &str, trigger_at: i64) -> Value {
        json!({ names::KEY_ID: id, names::KEY_TRIGGER_AT_UTC_MS: trigger_at, names::KEY_PAYLOAD_JSON: "{\"n\":1}" })
    }

    /// 一覧の ID（テスト用）。
    fn listed_ids(sim: &DesktopSimBridge) -> Vec<String> {
        let reply = call(sim, names::METHOD_LIST, json!({}));
        reply[names::KEY_ALARMS].as_array().unwrap().iter().map(|a| a[names::KEY_ID].as_str().unwrap().to_string()).collect()
    }

    /// 予約 → 時刻の前は鳴らない → 過ぎたら alarm.fired が 1 回だけ出る → 一覧から消える。
    #[test]
    fn scheduled_alarm_fires_once_after_trigger_time() {
        let (sim, clock) = sim();
        let reply = call(&sim, names::METHOD_SCHEDULE, request("morning", START_MS + DELAY_MS));
        assert_eq!(reply[wire::KEY_OK], Value::Bool(true));
        assert_eq!(reply[names::KEY_REPLACED], Value::Bool(false));
        assert_eq!(listed_ids(&sim), vec!["morning"]);

        clock.advance(DELAY_MS - 1);
        assert!(sim.poll_events().is_empty(), "予定時刻の前に鳴った");
        clock.advance(1);
        let events = sim.poll_events();
        assert_eq!(events.len(), 1);
        let event: Value = serde_json::from_str(&events[0]).unwrap();
        assert_eq!(wire::validate_event_json(&events[0]).unwrap(), names::EVENT_FIRED);
        let data = &event[wire::KEY_DATA];
        assert_eq!(data[names::KEY_ID], Value::from("morning"));
        assert_eq!(data[names::KEY_SCHEDULED_AT_UTC_MS], Value::from(START_MS + DELAY_MS));
        assert_eq!(data[names::KEY_FIRED_AT_UTC_MS], Value::from(START_MS + DELAY_MS));
        assert_eq!(data[names::KEY_PAYLOAD_JSON], Value::from("{\"n\":1}"));
        assert!(listed_ids(&sim).is_empty(), "鳴った予約が残っている（一回限り）");
        clock.advance(DELAY_MS);
        assert!(sim.poll_events().is_empty(), "2 回鳴った");
    }

    /// 同じ ID は置き換え（前の時刻では鳴らず、新しい時刻で 1 回）。
    #[test]
    fn same_id_replaces_previous_schedule() {
        let (sim, clock) = sim();
        call(&sim, names::METHOD_SCHEDULE, request("a", START_MS + DELAY_MS));
        let reply = call(&sim, names::METHOD_SCHEDULE, request("a", START_MS + 2 * DELAY_MS));
        assert_eq!(reply[names::KEY_REPLACED], Value::Bool(true));
        assert_eq!(listed_ids(&sim), vec!["a"]);
        clock.advance(DELAY_MS);
        assert!(sim.poll_events().is_empty(), "置き換える前の時刻で鳴った");
        clock.advance(DELAY_MS);
        assert_eq!(sim.poll_events().len(), 1);
    }

    /// 取り消した予約は鳴らない。無い ID の取り消しも成功（existed=false）。全部の取り消しは件数を返す。
    #[test]
    fn cancel_and_cancel_all() {
        let (sim, clock) = sim();
        call(&sim, names::METHOD_SCHEDULE, request("a", START_MS + DELAY_MS));
        call(&sim, names::METHOD_SCHEDULE, request("b", START_MS + DELAY_MS));
        call(&sim, names::METHOD_SCHEDULE, request("c", START_MS + DELAY_MS));
        let cancelled = call(&sim, names::METHOD_CANCEL, json!({ names::KEY_ID: "a" }));
        assert_eq!((cancelled[wire::KEY_OK].clone(), cancelled[names::KEY_EXISTED].clone()), (Value::Bool(true), Value::Bool(true)));
        let unknown = call(&sim, names::METHOD_CANCEL, json!({ names::KEY_ID: "nope" }));
        assert_eq!((unknown[wire::KEY_OK].clone(), unknown[names::KEY_EXISTED].clone()), (Value::Bool(true), Value::Bool(false)));
        assert_eq!(listed_ids(&sim), vec!["b", "c"]);
        let all = call(&sim, names::METHOD_CANCEL_ALL, json!({}));
        assert_eq!(all[names::KEY_COUNT], Value::from(2));
        clock.advance(DELAY_MS);
        assert!(sim.poll_events().is_empty(), "取り消した予約が鳴った");
    }

    /// 一覧は予定時刻の順。同時に過ぎた予約は予定時刻の順に、通し番号が増えながら鳴る
    /// （W1-4a から、2 件目以降は 1 件目の鳴動を待つので alarm.queued も続く。ここでは fired だけを見る）。
    #[test]
    fn list_and_fire_in_trigger_order() {
        let (sim, clock) = sim();
        call(&sim, names::METHOD_SCHEDULE, request("late", START_MS + 3 * DELAY_MS));
        call(&sim, names::METHOD_SCHEDULE, request("early", START_MS + DELAY_MS));
        call(&sim, names::METHOD_SCHEDULE, request("middle", START_MS + 2 * DELAY_MS));
        assert_eq!(listed_ids(&sim), vec!["early", "middle", "late"]);
        clock.advance(3 * DELAY_MS);
        let events: Vec<Value> = sim.poll_events().iter().map(|e| serde_json::from_str(e).unwrap()).collect();
        let seqs: Vec<u64> = events.iter().map(|e| e[wire::KEY_SEQ].as_u64().unwrap()).collect();
        assert!(seqs.windows(2).all(|pair| pair[0] < pair[1]), "通し番号が増えていない: {seqs:?}");
        let fired: Vec<&Value> = events.iter().filter(|e| e[wire::KEY_NAME] == Value::from(names::EVENT_FIRED)).collect();
        let ids: Vec<&str> = fired.iter().map(|e| e[wire::KEY_DATA][names::KEY_ID].as_str().unwrap()).collect();
        assert_eq!(ids, vec!["early", "middle", "late"]);
        let queued = events.iter().filter(|e| e[wire::KEY_NAME] == Value::from(names::EVENT_QUEUED)).count();
        assert_eq!(queued, 2, "鳴動中に届いた 2 件は待ち行列へ");
    }

    /// 引数の誤りは invalid_argument（detail つき）。上限を超える新しい ID は too_many_alarms（置き換えは通る）。
    #[test]
    fn invalid_arguments_and_limit() {
        let (sim, _clock) = sim();
        let bad = call(&sim, names::METHOD_SCHEDULE, json!({ names::KEY_ID: "", names::KEY_TRIGGER_AT_UTC_MS: START_MS }));
        assert_eq!(bad[wire::KEY_ERROR], Value::from(names::ERROR_INVALID_ARGUMENT));
        assert!(bad[wire::KEY_DETAIL].as_str().unwrap().contains(names::KEY_ID));
        let no_id = call(&sim, names::METHOD_CANCEL, json!({}));
        assert_eq!(no_id[wire::KEY_ERROR], Value::from(names::ERROR_INVALID_ARGUMENT));
        for index in 0..names::MAX_SCHEDULED_ALARMS {
            let reply = call(&sim, names::METHOD_SCHEDULE, request(&format!("id{index}"), START_MS + DELAY_MS));
            assert_eq!(reply[wire::KEY_OK], Value::Bool(true));
        }
        let over = call(&sim, names::METHOD_SCHEDULE, request("one_more", START_MS + DELAY_MS));
        assert_eq!(over[wire::KEY_ERROR], Value::from(names::ERROR_TOO_MANY_ALARMS));
        let replace = call(&sim, names::METHOD_SCHEDULE, request("id0", START_MS + 2 * DELAY_MS));
        assert_eq!(replace[names::KEY_REPLACED], Value::Bool(true));
    }

    /// 正確なアラームは常に張れる。Play の区切り（reset_session）で予約も積んだイベントも消える。
    #[test]
    fn can_schedule_exact_and_reset_session() {
        let (sim, clock) = sim();
        let exact = call(&sim, names::METHOD_CAN_SCHEDULE_EXACT, json!({}));
        assert_eq!(exact[names::KEY_CAN_SCHEDULE_EXACT], Value::Bool(true));
        call(&sim, names::METHOD_SCHEDULE, request("a", START_MS + DELAY_MS));
        sim.invoke(wire::MODULE_PLATFORM, wire::METHOD_EMIT_TEST_EVENT, "{}").unwrap();
        sim.reset_session();
        assert!(listed_ids(&sim).is_empty(), "Play の区切りで予約が消えていない");
        clock.advance(DELAY_MS);
        assert!(sim.poll_events().is_empty(), "Play の区切りの前のイベント・予約が残っている");
    }
}
