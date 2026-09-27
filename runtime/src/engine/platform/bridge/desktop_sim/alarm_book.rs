// ============================================================
//  platform/bridge/desktop_sim/alarm_book.rs — 模擬の目覚ましの予約表（プロセスの中だけ。W1-3）
//
//  Android の :seed_platform の控え（AlarmStore）＋AlarmManager の代わり。予約・置き換え・取り消し・一覧と、
//  「時刻を過ぎた予約を取り出す」（エンジンがフレームの頭で呼ぶ poll_events の中で使う）だけを持つ。
//  ファイルには書かない（エディタの Play の開始・停止で空にする＝Play を止めれば予約は消える。bridge::reset_session）。
//  件数の上限（wire::alarm::MAX_SCHEDULED_ALARMS）と「同じ ID は置き換え」は実機と同じ。
// ============================================================

use std::sync::{Mutex, MutexGuard, PoisonError};

use serde_json::{json, Value};

use crate::engine::platform::bridge::alarm::AlarmRequest;
use crate::engine::platform::bridge::wire::alarm as names;

/// 予約 1 件（受け付けた時刻つき）。
#[derive(Debug, Clone, PartialEq)]
pub struct SimAlarm {
    /// 検査済みの引数。
    pub request: AlarmRequest,
    /// 予約を受け付けた時刻（UTC の epoch ミリ秒）。
    pub created_at_utc_ms: i64,
}

impl SimAlarm {
    /// list の返答の 1 行（実機の控えの 1 行と同じ欄。音源は模擬では書き出さないので渡されたままを sound_asset に入れる）。
    pub fn to_json(&self) -> Value {
        let request = &self.request;
        json!({
            names::KEY_ID: request.id,
            names::KEY_TRIGGER_AT_UTC_MS: request.trigger_at_utc_ms,
            names::KEY_SOUND_ASSET: request.sound,
            names::KEY_VIBRATE: request.vibrate,
            names::KEY_FORCE_VOLUME: request.force_volume,
            names::KEY_KEEP_VOLUME: request.keep_volume,
            names::KEY_FADE_IN_SECONDS: request.fade_in_seconds,
            names::KEY_MAX_RING_MINUTES: request.max_ring_minutes,
            names::KEY_TITLE: request.title,
            names::KEY_BODY: request.body,
            names::KEY_PAYLOAD_JSON: request.payload_json,
            names::KEY_CREATED_AT_UTC_MS: self.created_at_utc_ms,
            names::KEY_SIMULATED: true,
        })
    }
}

/// 模擬の予約表（Mutex 1 つで守る。持つのは表の出し入れの間だけ）。
#[derive(Debug, Default)]
pub struct SimAlarmBook {
    /// 予約（順不同。一覧・取り出しのときに並べる）。
    alarms: Mutex<Vec<SimAlarm>>,
}

impl SimAlarmBook {
    /// 空の予約表。
    pub fn new() -> Self {
        Self::default()
    }

    /// ロックを取る（毒されていても中身は壊れない値だけなので使い続ける）。
    fn lock(&self) -> MutexGuard<'_, Vec<SimAlarm>> {
        self.alarms.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// 予約する（同じ ID は置き換え）。
    ///
    /// # 戻り値
    /// Ok(置き換えたか)。新しい ID で上限に達していれば Err(too_many_alarms)
    pub fn schedule(&self, request: AlarmRequest, created_at_utc_ms: i64) -> Result<bool, &'static str> {
        let mut alarms = self.lock();
        let alarm = SimAlarm { request, created_at_utc_ms };
        if let Some(existing) = alarms.iter_mut().find(|existing| existing.request.id == alarm.request.id) {
            *existing = alarm;
            return Ok(true);
        }
        if alarms.len() >= names::MAX_SCHEDULED_ALARMS {
            return Err(names::ERROR_TOO_MANY_ALARMS);
        }
        alarms.push(alarm);
        Ok(false)
    }

    /// 1 つ取り消す。
    ///
    /// # 戻り値
    /// その ID の予約があったか（無くても成功）
    pub fn cancel(&self, id: &str) -> bool {
        let mut alarms = self.lock();
        let before = alarms.len();
        alarms.retain(|alarm| alarm.request.id != id);
        alarms.len() != before
    }

    /// 全部取り消す。
    ///
    /// # 戻り値
    /// 取り消した件数
    pub fn cancel_all(&self) -> usize {
        let mut alarms = self.lock();
        let count = alarms.len();
        alarms.clear();
        count
    }

    /// 一覧（予定時刻の順。同じ時刻は ID の順）。
    pub fn list(&self) -> Vec<SimAlarm> {
        let mut alarms = self.lock().clone();
        sort_by_trigger(&mut alarms);
        alarms
    }

    /// 予定時刻を過ぎた（`now_utc_ms` 以前の）予約を取り出す（一回限り。予定時刻の順）。
    pub fn take_due(&self, now_utc_ms: i64) -> Vec<SimAlarm> {
        let mut alarms = self.lock();
        let (mut due, pending): (Vec<SimAlarm>, Vec<SimAlarm>) =
            alarms.drain(..).partition(|alarm| alarm.request.trigger_at_utc_ms <= now_utc_ms);
        *alarms = pending;
        sort_by_trigger(&mut due);
        due
    }

    /// いちばん早い予定時刻（UTC の epoch ミリ秒）。予約が無ければ None。
    ///
    /// 描画を止めている間（render_policy の on_demand。W2-10a）も、この時刻に起きて発火させる（WaitUntil）ために使う。
    pub fn next_trigger_utc_ms(&self) -> Option<i64> {
        self.lock().iter().map(|alarm| alarm.request.trigger_at_utc_ms).min()
    }

    /// 空にする（Play の区切り）。
    pub fn clear(&self) {
        self.lock().clear();
    }
}

/// 予定時刻の順（同じ時刻は ID の順）に並べる。
fn sort_by_trigger(alarms: &mut [SimAlarm]) {
    alarms.sort_by(|a, b| {
        a.request.trigger_at_utc_ms.cmp(&b.request.trigger_at_utc_ms).then_with(|| a.request.id.cmp(&b.request.id))
    });
}
