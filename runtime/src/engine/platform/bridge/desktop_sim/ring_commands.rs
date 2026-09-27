// ============================================================
//  platform/bridge/desktop_sim/ring_commands.rs — 模擬の鳴動の命令とイベント（W1-4a）
//
//  Android の :seed_platform の RingService・RingControl・AlarmModule の鳴動の命令の代わり（同じ命令に同じ形の JSON で答える）:
//    alarm.get_ringing  … 鳴動中の予約 { ringing: {id, scheduled_at_utc_ms, started_at_utc_ms, payload_json, simulated} | null }
//    alarm.stop_ringing … 引数 { id }（空・無しなら今鳴っているもの）。返答 { id, stopped }。止めたら
//                         platform.alarm.ring_stopped { id, reason: "stopped", scheduled_at_utc_ms, payload_json, simulated }
//  発火（alarm_commands.rs の fire_due_alarms）の後に start_ringing で鳴動へ渡し、鳴動中なら
//  platform.alarm.queued { id, scheduled_at_utc_ms, waiting_for, payload_json, simulated } を積む。
//  安全弁（max_ring_minutes）はフレームの頭の poll_events の中で check_ring_timeouts が見て、
//  platform.alarm.ring_stopped（reason: "timeout"）を積み、待ち行列の次を鳴らす。
//  **音は鳴らさない**（[SEED PLATFORM] のログだけ）。状態は ring_state.rs。Play の区切りで空にする。
// ============================================================

use serde_json::{json, Map, Value};

use super::alarm_book::SimAlarm;
use super::ring_state::{RingOffer, RingStop, SimRinging};
use super::{DesktopSimBridge, SimFailure, SimResult};
use crate::engine::platform::bridge::alarm::read_optional_id;
use crate::engine::platform::bridge::wire::{self, alarm as names};
use crate::engine::platform::bridge::LOG_PREFIX;

impl DesktopSimBridge {
    /// alarm.get_ringing: 鳴動中の予約（無ければ null）。
    pub(super) fn handle_alarm_get_ringing(&self, _request: &Value) -> SimResult {
        let ringing = self.ringing.current().map_or(Value::Null, |session| session.to_json());
        let mut fields = Map::new();
        fields.insert(names::KEY_RINGING.into(), ringing);
        Ok(fields)
    }

    /// alarm.stop_ringing: 止める（鳴っていなければ stopped=false でも成功＝冪等）。
    pub(super) fn handle_alarm_stop_ringing(&self, request: &Value) -> SimResult {
        let id = read_optional_id(request).map_err(SimFailure::invalid_argument)?;
        let now = self.clock.now_utc_ms();
        let mut fields = Map::new();
        match self.ringing.stop(&id, now) {
            Some(stop) => {
                fields.insert(names::KEY_ID.into(), Value::from(stop.stopped.request.id.clone()));
                fields.insert(names::KEY_STOPPED.into(), Value::Bool(true));
                self.finish_ring_stop(stop, names::RING_STOP_REASON_STOPPED, now);
            }
            None => {
                eprintln!("{LOG_PREFIX} 模擬の鳴動を止める命令: 止めるものがありません（{}）", if id.is_empty() { "鳴動中のもの" } else { &id });
                fields.insert(names::KEY_ID.into(), Value::from(id));
                fields.insert(names::KEY_STOPPED.into(), Value::Bool(false));
            }
        }
        Ok(fields)
    }

    /// 発火した予約を鳴動へ渡す（fire_due_alarms から）。鳴動中なら待ち行列へ入れて platform.alarm.queued を積む。
    pub(super) fn start_ringing(&self, alarm: SimAlarm, fired_at_utc_ms: i64, now_utc_ms: i64) {
        let id = alarm.request.id.clone();
        let scheduled_at = alarm.request.trigger_at_utc_ms;
        let payload_json = alarm.request.payload_json.clone();
        match self.ringing.offer(alarm, fired_at_utc_ms, now_utc_ms) {
            RingOffer::Started(session) => log_ring_started(&session, false),
            RingOffer::Queued { waiting_for } => {
                let data = json!({
                    names::KEY_ID: id,
                    names::KEY_SCHEDULED_AT_UTC_MS: scheduled_at,
                    names::KEY_WAITING_FOR: waiting_for,
                    names::KEY_PAYLOAD_JSON: payload_json,
                    names::KEY_SIMULATED: true,
                });
                self.push_event(names::EVENT_QUEUED, now_utc_ms, data);
                eprintln!("{LOG_PREFIX} 模擬の目覚まし {id} は {waiting_for} の鳴動が終わるまで待ちます");
            }
        }
    }

    /// 安全弁: 今鳴っているものが時間切れなら止め、platform.alarm.ring_stopped（timeout）を積む（poll_events から）。
    pub(super) fn check_ring_timeouts(&self) {
        let now = self.clock.now_utc_ms();
        if let Some(stop) = self.ringing.stop_if_timed_out(now) {
            eprintln!(
                "{LOG_PREFIX} 模擬の目覚まし {} は安全弁（{} 分）で止めました",
                stop.stopped.request.id, stop.stopped.request.max_ring_minutes
            );
            self.finish_ring_stop(stop, names::RING_STOP_REASON_TIMEOUT, now);
        }
    }

    /// 止めた後始末: platform.alarm.ring_stopped を積み、繰り上がった鳴動があればログへ出す。
    fn finish_ring_stop(&self, stop: RingStop, reason: &str, now_utc_ms: i64) {
        let request = &stop.stopped.request;
        let data = json!({
            names::KEY_ID: request.id,
            names::KEY_REASON: reason,
            names::KEY_SCHEDULED_AT_UTC_MS: request.trigger_at_utc_ms,
            names::KEY_PAYLOAD_JSON: request.payload_json,
            names::KEY_SIMULATED: true,
        });
        self.push_event(names::EVENT_RING_STOPPED, now_utc_ms, data);
        eprintln!(
            "{LOG_PREFIX} 模擬の目覚まし {} の鳴動が終わりました（理由 {reason}{}）",
            request.id,
            if stop.was_ringing { "" } else { "・待ち行列から外した" }
        );
        if let Some(next) = &stop.next {
            log_ring_started(next, true);
        }
    }

    /// 模擬のイベントを 1 つ積む（通し番号は試験イベント・発火と共通。W1-5 で権限の結果〈permission_commands.rs〉も使う）。
    pub(super) fn push_event(&self, name: &str, now_utc_ms: i64, data: Value) {
        let seq = self.next_event_seq();
        let time_ms = u64::try_from(now_utc_ms).unwrap_or_default();
        self.queue_event(wire::event_json(name, seq, time_ms, data));
    }
}

/// 鳴り始めたことをログへ出す（模擬は音を鳴らさない）。
fn log_ring_started(session: &SimRinging, promoted: bool) {
    let request = &session.alarm.request;
    eprintln!(
        "{LOG_PREFIX} 模擬の目覚まし {} が鳴り始めました（予定から {} ms・安全弁 {} 分・音は鳴らしません{}）",
        request.id,
        session.started_at_utc_ms - request.trigger_at_utc_ms,
        request.max_ring_minutes,
        if promoted { "・待ち行列から繰り上げ" } else { "" }
    );
}

// ============================================================
//  ユニットテスト（手で進める時計で「予約 → 発火 → 鳴動 → 停止／安全弁／待ち行列 → Play の区切り」を確かめる）
// ============================================================

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use serde_json::{json, Value};

    use super::super::wall_clock::ManualClock;
    use super::super::DesktopSimBridge;
    use crate::engine::platform::bridge::wire::{self, alarm as names};
    use crate::engine::platform::bridge::PlatformBridge;

    /// 時計の始まり（UTC の epoch ミリ秒）。
    const START_MS: i64 = 1_790_000_000_000;
    /// 予約までの時間（ミリ秒）。
    const DELAY_MS: i64 = 5_000;
    /// 2 件目を 1 件目から遅らせる時間（ミリ秒）。
    const SECOND_OFFSET_MS: i64 = 1_000;
    /// 安全弁（分）。
    const MAX_MINUTES: i64 = 1;

    /// 手で進める時計つきの模擬（テスト用）。
    fn sim() -> (DesktopSimBridge, Arc<ManualClock>) {
        let clock = Arc::new(ManualClock::starting_at(START_MS));
        (DesktopSimBridge::with_clock(clock.clone()), clock)
    }

    /// 目覚ましの命令を送って返答の JSON を読む（テスト用）。
    fn call(sim: &DesktopSimBridge, method: &str, request: Value) -> Value {
        serde_json::from_str(&sim.invoke(names::MODULE, method, &request.to_string()).unwrap()).unwrap()
    }

    /// 予約する（テスト用）。
    fn schedule(sim: &DesktopSimBridge, id: &str, trigger_at: i64) {
        let reply = call(sim, names::METHOD_SCHEDULE, json!({ names::KEY_ID: id, names::KEY_TRIGGER_AT_UTC_MS: trigger_at,
            names::KEY_MAX_RING_MINUTES: MAX_MINUTES, names::KEY_PAYLOAD_JSON: format!("{{\"id\":\"{id}\"}}") }));
        assert_eq!(reply[wire::KEY_OK], Value::Bool(true), "{reply}");
    }

    /// 取り出したイベントの（名前, data）の一覧（テスト用）。
    fn poll(sim: &DesktopSimBridge) -> Vec<(String, Value)> {
        sim.poll_events()
            .iter()
            .map(|text| {
                let event: Value = serde_json::from_str(text).unwrap();
                (event[wire::KEY_NAME].as_str().unwrap().to_string(), event[wire::KEY_DATA].clone())
            })
            .collect()
    }

    /// 鳴動中の予約の ID（無ければ None。テスト用）。
    fn ringing_id(sim: &DesktopSimBridge) -> Option<String> {
        let reply = call(sim, names::METHOD_GET_RINGING, json!({}));
        assert_eq!(reply[wire::KEY_OK], Value::Bool(true));
        reply[names::KEY_RINGING][names::KEY_ID].as_str().map(str::to_string)
    }

    /// 発火 → 鳴動（get_ringing が返す）→ stop_ringing → ring_stopped(stopped) → 鳴っていない。
    #[test]
    fn fired_alarm_rings_until_stopped() {
        let (sim, clock) = sim();
        assert_eq!(ringing_id(&sim), None);
        schedule(&sim, "morning", START_MS + DELAY_MS);
        clock.advance(DELAY_MS);
        let events = poll(&sim);
        assert_eq!(events.iter().map(|(n, _)| n.as_str()).collect::<Vec<_>>(), vec![names::EVENT_FIRED]);
        let ringing = call(&sim, names::METHOD_GET_RINGING, json!({}))[names::KEY_RINGING].clone();
        assert_eq!(ringing[names::KEY_ID], Value::from("morning"));
        assert_eq!(ringing[names::KEY_SCHEDULED_AT_UTC_MS], Value::from(START_MS + DELAY_MS));
        assert_eq!(ringing[names::KEY_STARTED_AT_UTC_MS], Value::from(START_MS + DELAY_MS));
        assert_eq!(ringing[names::KEY_PAYLOAD_JSON], Value::from("{\"id\":\"morning\"}"));

        clock.advance(3_000);
        let stopped = call(&sim, names::METHOD_STOP_RINGING, json!({}));
        assert_eq!((stopped[names::KEY_ID].clone(), stopped[names::KEY_STOPPED].clone()), (Value::from("morning"), Value::Bool(true)));
        let events = poll(&sim);
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].0, names::EVENT_RING_STOPPED);
        assert_eq!(events[0].1[names::KEY_ID], Value::from("morning"));
        assert_eq!(events[0].1[names::KEY_REASON], Value::from(names::RING_STOP_REASON_STOPPED));
        assert_eq!(ringing_id(&sim), None);

        // 鳴っていないときの停止も成功（stopped=false）。イベントは出ない
        let again = call(&sim, names::METHOD_STOP_RINGING, json!({ names::KEY_ID: "morning" }));
        assert_eq!((again[wire::KEY_OK].clone(), again[names::KEY_STOPPED].clone()), (Value::Bool(true), Value::Bool(false)));
        assert!(poll(&sim).is_empty());
    }

    /// 安全弁: max_ring_minutes のちょうどで ring_stopped(timeout)。その 1 ms 前は鳴ったまま。
    #[test]
    fn safety_valve_stops_with_timeout() {
        let (sim, clock) = sim();
        schedule(&sim, "a", START_MS + DELAY_MS);
        clock.advance(DELAY_MS);
        poll(&sim);
        clock.advance(MAX_MINUTES * names::MILLIS_PER_MINUTE - 1);
        assert!(poll(&sim).is_empty(), "安全弁の前に止まった");
        assert_eq!(ringing_id(&sim).as_deref(), Some("a"));
        clock.advance(1);
        let events = poll(&sim);
        assert_eq!(events.len(), 1);
        assert_eq!((events[0].0.as_str(), events[0].1[names::KEY_REASON].as_str()), (names::EVENT_RING_STOPPED, Some(names::RING_STOP_REASON_TIMEOUT)));
        assert_eq!(ringing_id(&sim), None);
    }

    /// 待ち行列: 1 秒差の 2 件 → 2 件目は queued（waiting_for = 1 件目）→ 1 件目を止めると 2 件目が鳴る → 止めると空。
    #[test]
    fn second_alarm_waits_then_rings() {
        let (sim, clock) = sim();
        schedule(&sim, "first", START_MS + DELAY_MS);
        schedule(&sim, "second", START_MS + DELAY_MS + SECOND_OFFSET_MS);
        clock.advance(DELAY_MS);
        assert_eq!(poll(&sim).len(), 1, "1 件目の fired");
        clock.advance(SECOND_OFFSET_MS);
        let events = poll(&sim);
        assert_eq!(events.iter().map(|(n, _)| n.as_str()).collect::<Vec<_>>(), vec![names::EVENT_FIRED, names::EVENT_QUEUED]);
        assert_eq!(events[1].1[names::KEY_ID], Value::from("second"));
        assert_eq!(events[1].1[names::KEY_WAITING_FOR], Value::from("first"));
        assert_eq!(ringing_id(&sim).as_deref(), Some("first"));

        let stopped = call(&sim, names::METHOD_STOP_RINGING, json!({ names::KEY_ID: "first" }));
        assert_eq!(stopped[names::KEY_STOPPED], Value::Bool(true));
        assert_eq!(ringing_id(&sim).as_deref(), Some("second"), "待ち行列が繰り上がらない");
        let ringing = call(&sim, names::METHOD_GET_RINGING, json!({}))[names::KEY_RINGING].clone();
        assert_eq!(ringing[names::KEY_STARTED_AT_UTC_MS], Value::from(START_MS + DELAY_MS + SECOND_OFFSET_MS), "鳴り始めは繰り上がった時刻");
        call(&sim, names::METHOD_STOP_RINGING, json!({}));
        let events = poll(&sim);
        let stopped_ids: Vec<&str> = events.iter().map(|(_, data)| data[names::KEY_ID].as_str().unwrap()).collect();
        assert_eq!(stopped_ids, vec!["first", "second"]);
        assert_eq!(ringing_id(&sim), None);
    }

    /// Play の区切り（reset_session）で鳴動も待ち行列も消え、前の回の鳴動が次の回へ持ち越されない。
    #[test]
    fn reset_session_clears_ringing() {
        let (sim, clock) = sim();
        schedule(&sim, "a", START_MS + DELAY_MS);
        schedule(&sim, "b", START_MS + DELAY_MS);
        clock.advance(DELAY_MS);
        poll(&sim);
        assert_eq!(ringing_id(&sim).as_deref(), Some("a"));
        sim.reset_session();
        assert_eq!(ringing_id(&sim), None);
        clock.advance(MAX_MINUTES * names::MILLIS_PER_MINUTE);
        assert!(poll(&sim).is_empty(), "Play の区切りの後に前の回の鳴動のイベントが出た");
    }

    /// stop_ringing の引数の誤り（id が文字列でない）は invalid_argument。
    #[test]
    fn stop_ringing_rejects_bad_id() {
        let (sim, _clock) = sim();
        let reply = call(&sim, names::METHOD_STOP_RINGING, json!({ names::KEY_ID: 3 }));
        assert_eq!(reply[wire::KEY_ERROR], Value::from(names::ERROR_INVALID_ARGUMENT));
    }
}
