// ============================================================
//  platform/bridge/desktop_sim/ring_state.rs — 模擬の鳴動の状態（今鳴っている 1 つと待ち行列。W1-4a）
//
//  Android の :seed_platform の RingRegistry（runtime/android/app/src/main/java/.../alarm/ring/RingRegistry.java）の代わり。
//  規則は同じ（変えるときは両方）:
//    ・配信された予約は、何も鳴っていなければ鳴り始め、鳴っていれば待ち行列へ（捨てない。同じ ID は置き換え）
//    ・止める（stop_ringing）: ID が空なら今鳴っているもの。待ち行列の予約も ID で外せる
//    ・今鳴っているものが止まったら（停止・安全弁）、待ち行列の先頭を繰り上げて鳴らす（鳴り始めはそのときの時刻）
//    ・安全弁: 鳴り始め＋max_ring_minutes を過ぎたら止める
//  音は鳴らさない（状態とイベントだけ。ログは呼び出し側の ring_commands.rs）。Play の区切りで空にする。
// ============================================================

use std::sync::{Mutex, MutexGuard, PoisonError};

use serde_json::{json, Value};

use super::alarm_book::SimAlarm;
use crate::engine::platform::bridge::wire::alarm as names;

/// 最初に払い出す鳴動の通し番号（Java の RingRegistry と同じく 1 から）。
const FIRST_SERIAL: u64 = 1;

/// 鳴動 1 回ぶん（どの予約を・いつから鳴らしているか）。
#[derive(Debug, Clone, PartialEq)]
pub struct SimRinging {
    /// 鳴動の通し番号。
    pub serial: u64,
    /// 鳴らしている予約。
    pub alarm: SimAlarm,
    /// 配信を受けた時刻（UTC の epoch ミリ秒）。
    pub fired_at_utc_ms: i64,
    /// 鳴り始めた時刻（UTC の epoch ミリ秒。待ち行列から繰り上がったときはその時刻）。
    pub started_at_utc_ms: i64,
}

impl SimRinging {
    /// 安全弁で止める時刻（鳴り始め＋max_ring_minutes）。
    pub fn deadline_utc_ms(&self) -> i64 {
        self.started_at_utc_ms.saturating_add(self.alarm.request.max_ring_minutes.saturating_mul(names::MILLIS_PER_MINUTE))
    }

    /// get_ringing の返答の ringing（実機の RingSession.toRingingJson と同じ欄＋simulated）。
    pub fn to_json(&self) -> Value {
        json!({
            names::KEY_ID: self.alarm.request.id,
            names::KEY_SCHEDULED_AT_UTC_MS: self.alarm.request.trigger_at_utc_ms,
            names::KEY_STARTED_AT_UTC_MS: self.started_at_utc_ms,
            names::KEY_PAYLOAD_JSON: self.alarm.request.payload_json,
            names::KEY_SIMULATED: true,
        })
    }
}

/// 配信された予約を渡した結果。
#[derive(Debug, Clone, PartialEq)]
pub enum RingOffer {
    /// 何も鳴っていなかったので鳴り始めた。
    Started(SimRinging),
    /// 鳴動中だったので待ち行列へ入れた（今鳴っている予約の ID）。
    Queued {
        /// 今鳴っている予約の ID。
        waiting_for: String,
    },
}

/// 止めた結果。
#[derive(Debug, Clone, PartialEq)]
pub struct RingStop {
    /// 止めた予約（鳴っていたか待ち行列にいたもの）。
    pub stopped: SimAlarm,
    /// 止めたのが鳴動中のものだったか（false なら待ち行列から外しただけ）。
    pub was_ringing: bool,
    /// 繰り上がって鳴り始めた鳴動。
    pub next: Option<SimRinging>,
}

/// 待ち行列の 1 件（配信を受けた時刻つき）。
#[derive(Debug, Clone, PartialEq)]
struct Waiting {
    /// 予約。
    alarm: SimAlarm,
    /// 配信を受けた時刻。
    fired_at_utc_ms: i64,
}

/// Mutex の中身。
#[derive(Debug)]
struct Inner {
    /// 今鳴っている鳴動。
    current: Option<SimRinging>,
    /// 待ち行列（配信の順）。
    queue: Vec<Waiting>,
    /// 次に払い出す通し番号。
    next_serial: u64,
}

impl Default for Inner {
    fn default() -> Self {
        Self { current: None, queue: Vec::new(), next_serial: FIRST_SERIAL }
    }
}

impl Inner {
    /// 新しい鳴動を作る（通し番号を払い出す）。
    fn new_session(&mut self, alarm: SimAlarm, fired_at_utc_ms: i64, now_utc_ms: i64) -> SimRinging {
        let serial = self.next_serial;
        self.next_serial += 1;
        SimRinging { serial, alarm, fired_at_utc_ms, started_at_utc_ms: now_utc_ms }
    }

    /// 今鳴っているものを止め、待ち行列の先頭を繰り上げる（鳴っていなければ None）。
    fn stop_current(&mut self, now_utc_ms: i64) -> Option<RingStop> {
        let stopped = self.current.take()?;
        if !self.queue.is_empty() {
            let head = self.queue.remove(0);
            let next = self.new_session(head.alarm, head.fired_at_utc_ms, now_utc_ms);
            self.current = Some(next);
        }
        Some(RingStop { stopped: stopped.alarm, was_ringing: true, next: self.current.clone() })
    }
}

/// 模擬の鳴動の状態（Mutex 1 つで守る。持つのは値の出し入れの間だけ）。
#[derive(Debug, Default)]
pub struct SimRingState {
    /// 中身。
    inner: Mutex<Inner>,
}

impl SimRingState {
    /// 空の状態。
    pub fn new() -> Self {
        Self::default()
    }

    /// ロックを取る（毒されていても中身は壊れない値だけなので使い続ける）。
    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// 配信された予約を渡す（何も鳴っていなければ鳴り始め、鳴動中なら待ち行列へ。同じ ID は置き換え）。
    pub fn offer(&self, alarm: SimAlarm, fired_at_utc_ms: i64, now_utc_ms: i64) -> RingOffer {
        let mut inner = self.lock();
        if let Some(current) = &inner.current {
            let waiting_for = current.alarm.request.id.clone();
            let waiting = Waiting { alarm, fired_at_utc_ms };
            match inner.queue.iter_mut().find(|queued| queued.alarm.request.id == waiting.alarm.request.id) {
                Some(existing) => *existing = waiting,
                None => inner.queue.push(waiting),
            }
            return RingOffer::Queued { waiting_for };
        }
        let session = inner.new_session(alarm, fired_at_utc_ms, now_utc_ms);
        inner.current = Some(session.clone());
        RingOffer::Started(session)
    }

    /// ID で止める（空なら今鳴っているもの。待ち行列の予約も外せる）。何も止めなければ None。
    pub fn stop(&self, id_or_empty: &str, now_utc_ms: i64) -> Option<RingStop> {
        let mut inner = self.lock();
        let current_matches =
            inner.current.as_ref().is_some_and(|current| id_or_empty.is_empty() || current.alarm.request.id == id_or_empty);
        if current_matches {
            return inner.stop_current(now_utc_ms);
        }
        if id_or_empty.is_empty() {
            return None;
        }
        let index = inner.queue.iter().position(|queued| queued.alarm.request.id == id_or_empty)?;
        let removed = inner.queue.remove(index);
        Some(RingStop { stopped: removed.alarm, was_ringing: false, next: None })
    }

    /// 安全弁: 今鳴っているものが時間切れなら止める（次を繰り上げる）。時間切れでなければ None。
    pub fn stop_if_timed_out(&self, now_utc_ms: i64) -> Option<RingStop> {
        let mut inner = self.lock();
        let timed_out = inner.current.as_ref().is_some_and(|current| current.deadline_utc_ms() <= now_utc_ms);
        if timed_out { inner.stop_current(now_utc_ms) } else { None }
    }

    /// 今鳴っている鳴動。
    pub fn current(&self) -> Option<SimRinging> {
        self.lock().current.clone()
    }

    /// 空にする（Play の区切り。通し番号は戻さない）。
    pub fn clear(&self) {
        let mut inner = self.lock();
        inner.current = None;
        inner.queue.clear();
    }
}

// ============================================================
//  ユニットテスト（状態の移り変わりだけ。イベントと時計つきの流れは ring_commands.rs）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::platform::bridge::alarm::read_schedule;

    /// 時刻の起点（UTC の epoch ミリ秒）。
    const T0: i64 = 1_790_000_000_000;
    /// 安全弁（分）。
    const MAX_MINUTES: i64 = 1;

    /// 予約（テスト用）。
    fn alarm(id: &str) -> SimAlarm {
        let request = read_schedule(&json!({ names::KEY_ID: id, names::KEY_TRIGGER_AT_UTC_MS: T0,
            names::KEY_MAX_RING_MINUTES: MAX_MINUTES }))
        .unwrap();
        SimAlarm { request, created_at_utc_ms: T0 }
    }

    /// 何も鳴っていなければ鳴り始め、鳴動中なら待ち行列。止めると次が繰り上がり、鳴り始めはそのときの時刻。
    #[test]
    fn queue_promotes_next_on_stop() {
        let state = SimRingState::new();
        assert!(matches!(state.offer(alarm("a"), T0, T0), RingOffer::Started(ref s) if s.serial == FIRST_SERIAL));
        assert_eq!(state.offer(alarm("b"), T0 + 1, T0 + 1), RingOffer::Queued { waiting_for: "a".into() });
        let stop = state.stop("", T0 + 10).expect("鳴動中のものが止まらない");
        assert_eq!(stop.stopped.request.id, "a");
        assert!(stop.was_ringing);
        let next = stop.next.expect("待ち行列が繰り上がらない");
        assert_eq!((next.alarm.request.id.as_str(), next.started_at_utc_ms, next.fired_at_utc_ms), ("b", T0 + 10, T0 + 1));
        assert_eq!(state.current().unwrap().serial, FIRST_SERIAL + 1);
        assert!(state.stop("b", T0 + 20).unwrap().next.is_none());
        assert!(state.current().is_none());
        assert!(state.stop("", T0 + 30).is_none(), "鳴っていないのに止めた");
    }

    /// 待ち行列の予約は ID で外せる（鳴動中のものはそのまま）。同じ ID の配信は置き換え。
    #[test]
    fn queued_alarm_can_be_removed_and_replaced() {
        let state = SimRingState::new();
        state.offer(alarm("a"), T0, T0);
        state.offer(alarm("b"), T0, T0);
        state.offer(alarm("b"), T0 + 5, T0 + 5);
        let removed = state.stop("b", T0 + 6).unwrap();
        assert!(!removed.was_ringing && removed.next.is_none());
        assert_eq!(state.current().unwrap().alarm.request.id, "a");
        assert!(state.stop("b", T0 + 7).is_none(), "置き換えたはずの 2 件目が残っている");
        assert!(state.stop("nope", T0 + 7).is_none());
    }

    /// 安全弁: 鳴り始め＋max_ring_minutes のちょうどで止まり、その前は止まらない。繰り上がった次は新しい鳴り始めから数える。
    #[test]
    fn safety_valve_uses_start_time() {
        let state = SimRingState::new();
        state.offer(alarm("a"), T0, T0);
        state.offer(alarm("b"), T0, T0);
        let deadline = T0 + MAX_MINUTES * names::MILLIS_PER_MINUTE;
        assert!(state.stop_if_timed_out(deadline - 1).is_none());
        let stop = state.stop_if_timed_out(deadline).unwrap();
        assert_eq!(stop.stopped.request.id, "a");
        assert_eq!(stop.next.unwrap().deadline_utc_ms(), deadline + MAX_MINUTES * names::MILLIS_PER_MINUTE);
        assert!(state.stop_if_timed_out(deadline + 1).is_none(), "繰り上がった直後に止まった");
    }

    /// Play の区切り: 鳴動も待ち行列も消える。
    #[test]
    fn clear_empties_everything() {
        let state = SimRingState::new();
        state.offer(alarm("a"), T0, T0);
        state.offer(alarm("b"), T0, T0);
        state.clear();
        assert!(state.current().is_none());
        assert!(matches!(state.offer(alarm("c"), T0, T0), RingOffer::Started(_)));
    }
}
