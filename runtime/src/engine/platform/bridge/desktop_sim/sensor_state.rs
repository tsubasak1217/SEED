// ============================================================
//  platform/bridge/desktop_sim/sensor_state.rs — 模擬のセンサーの状態（W1-8）
//
//  Android のメインプロセスの platform/sensor/（SensorFeed と SampleAccumulator）の代わり。PC にセンサーは無いので、
//  標本は sim_inject で入れたものだけ（入れなければ値は 0・標本の数も 0）。種類ごとに
//    ・動いているか（start 済みか。表に入っていれば動いている）と頻度
//    ・最新の標本（x, y, z）とその時刻（UTC の epoch ミリ秒）
//    ・前回の read からの最大の大きさ（peak）と標本の数（count）… read（drain）で 0 に戻す
//  を持つ。規則は Java の SampleAccumulator と同じ（大きさは bridge::sensor::magnitude・最大は「より大きいときだけ」更新・数は飽和）。
//  sensor_commands.rs が書き、返答と単体テストが読む。エディタの Play の区切りで空にする（動いていない状態へ戻す）。
// ============================================================

use std::collections::HashMap;
use std::sync::{Mutex, MutexGuard, PoisonError};

use crate::engine::platform::bridge::sensor::magnitude;

/// 読んだ値の写し（read の返答の中身）。
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct SimSensorReading {
    /// 最新の標本（x, y, z。m/s²。まだ無ければ 0）。
    pub latest: [f64; 3],
    /// 最新の標本の時刻（UTC の epoch ミリ秒。まだ無ければ 0）。
    pub timestamp_ms: i64,
    /// 前回の read からの標本の大きさの最大（m/s²。標本が無ければ 0）。
    pub peak_magnitude: f64,
    /// 前回の read からの標本の数。
    pub sample_count: u64,
}

impl SimSensorReading {
    /// 標本を 1 つ足す（最新を置き換え、最大はより大きいときだけ、数は飽和させて 1 増やす）。
    fn record(&mut self, sample: [f64; 3], timestamp_ms: i64) {
        self.latest = sample;
        self.timestamp_ms = timestamp_ms;
        let size = magnitude(sample);
        if size > self.peak_magnitude {
            self.peak_magnitude = size;
        }
        self.sample_count = self.sample_count.saturating_add(1);
    }

    /// 今の値を返し、最大と数を 0 に戻す（最新の標本と時刻は残す＝次の read でも「最新」として返る）。
    fn drain(&mut self) -> Self {
        let snapshot = *self;
        self.peak_magnitude = 0.0;
        self.sample_count = 0;
        snapshot
    }
}

/// 種類 1 つの状態（表に入っている間は「動いている」）。
#[derive(Debug, Clone, Copy)]
struct SimSensorFeed {
    /// start で受け付けた頻度（Hz。そろえた後）。模擬は標本を作らないので記録だけ。
    rate_hz: i64,
    /// 読んだ値の元。
    reading: SimSensorReading,
}

/// 模擬のセンサーの状態（種類 → 状態。Mutex 1 つで守る）。
#[derive(Debug, Default)]
pub struct SimSensorBoard {
    /// 動いている種類（wire::sensor::KINDS の文字列 → 状態）。
    feeds: Mutex<HashMap<&'static str, SimSensorFeed>>,
}

impl SimSensorBoard {
    /// 空の状態（どの種類も動いていない）。
    pub fn new() -> Self {
        Self::default()
    }

    /// ロックを取る（毒されていても中身は壊れない値だけなので使い続ける）。
    fn lock(&self) -> MutexGuard<'_, HashMap<&'static str, SimSensorFeed>> {
        self.feeds.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// 始める（動いていれば標本を捨てて始め直す。Android の SensorFeed.start と同じ）。
    pub fn start(&self, kind: &'static str, rate_hz: i64) {
        self.lock().insert(kind, SimSensorFeed { rate_hz, reading: SimSensorReading::default() });
    }

    /// 止める。
    ///
    /// # 戻り値
    /// 動いていたものを止めたら true（動いていなければ false）
    pub fn stop(&self, kind: &str) -> bool {
        self.lock().remove(kind).is_some()
    }

    /// 標本を 1 つ入れる（sim_inject）。
    ///
    /// # 戻り値
    /// 動いていれば Some(入れた後の、前回の read からの標本の数)。動いていなければ None（not_started）
    pub fn inject(&self, kind: &str, sample: [f64; 3], timestamp_ms: i64) -> Option<u64> {
        let mut feeds = self.lock();
        let feed = feeds.get_mut(kind)?;
        feed.reading.record(sample, timestamp_ms);
        Some(feed.reading.sample_count)
    }

    /// 読む（最大と数は 0 に戻る）。
    ///
    /// # 戻り値
    /// 動いていれば Some(読んだ値)。動いていなければ None（not_started）
    pub fn drain(&self, kind: &str) -> Option<SimSensorReading> {
        self.lock().get_mut(kind).map(|feed| feed.reading.drain())
    }

    /// 動いていればその頻度（単体テスト・診断用）。
    pub fn rate_hz(&self, kind: &str) -> Option<i64> {
        self.lock().get(kind).map(|feed| feed.rate_hz)
    }

    /// すべて止める（エディタの Play の区切り）。
    pub fn clear(&self) {
        self.lock().clear();
    }
}
