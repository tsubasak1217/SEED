// ============================================================
//  platform/bridge/desktop_sim/sensor_commands.rs — 模擬のセンサーの命令（W1-8）
//
//  Android ではメインプロセスの Java（platform/local/SensorStartCommand ほか → platform/sensor/SensorFeeds）が SensorManager の
//  重力を除いた加速度を受けて答える命令の代わり。PC にセンサーは無いので、値は sim_inject で入れた標本だけ（入れなければ 0）:
//    sensor.start      … 引数 { kind, rate_hz }（規則は bridge::sensor）。返答 { kind, supported: true, source: "simulated", rate_hz, simulated }。
//                         動いていれば標本を捨てて始め直す
//    sensor.stop       … 引数 { kind }。返答 { kind, stopped（動いていたか）, simulated }。動いていなくても成功（冪等）
//    sensor.read       … 引数 { kind }。返答 { kind, x, y, z, timestamp_ms, peak_magnitude, sample_count, simulated }。
//                         peak_magnitude と sample_count は前回の read からの分で、読むと 0 に戻る。start 前・stop 後は not_started
//    sensor.sim_inject … 模擬だけ（Android では unknown_method）。引数 { kind, x, y, z }（有限の数・m/s²）。時刻は模擬の壁時計の今。
//                         返答 { kind, sample_count（入れた後の数）, simulated }。動いていなければ not_started
//  標本ごとのイベントは流さない（Android と同じく read で取る）。read は毎フレーム呼ばれるのでログを出さない。
// ============================================================

use serde_json::{Map, Value};

use super::sensor_state::SimSensorReading;
use super::{DesktopSimBridge, SimFailure, SimResult};
use crate::engine::platform::bridge::sensor::{read_kind, read_rate_hz, read_sample};
use crate::engine::platform::bridge::wire::{alarm as alarm_names, sensor as names};
use crate::engine::platform::bridge::LOG_PREFIX;

impl DesktopSimBridge {
    /// sensor.start: 種類と頻度を読み、（始め直しを含めて）動かす。
    pub(super) fn handle_sensor_start(&self, request: &Value) -> SimResult {
        let kind = read_kind(request).map_err(SimFailure::invalid_argument)?;
        let rate_hz = read_rate_hz(request).map_err(SimFailure::invalid_argument)?;
        self.sensors.start(kind, rate_hz);
        eprintln!("{LOG_PREFIX} 模擬: センサー {kind} を {rate_hz} Hz で始めました（PC にセンサーは無いので、値は sim_inject で入れた標本だけです）");
        let mut fields = kind_reply(kind);
        fields.insert(names::KEY_SUPPORTED.into(), Value::Bool(true));
        fields.insert(names::KEY_SOURCE.into(), Value::from(names::SOURCE_SIMULATED));
        fields.insert(names::KEY_RATE_HZ.into(), Value::from(rate_hz));
        Ok(fields)
    }

    /// sensor.stop: 止める（動いていなくても成功）。
    pub(super) fn handle_sensor_stop(&self, request: &Value) -> SimResult {
        let kind = read_kind(request).map_err(SimFailure::invalid_argument)?;
        let stopped = self.sensors.stop(kind);
        eprintln!("{LOG_PREFIX} 模擬: センサー {kind} を止めました（{}）", if stopped { "動いていた" } else { "動いていなかった" });
        let mut fields = kind_reply(kind);
        fields.insert(names::KEY_STOPPED.into(), Value::Bool(stopped));
        Ok(fields)
    }

    /// sensor.read: 最新の標本と、前回の read からの最大の大きさ・標本の数（読むと 0 に戻る）。
    pub(super) fn handle_sensor_read(&self, request: &Value) -> SimResult {
        let kind = read_kind(request).map_err(SimFailure::invalid_argument)?;
        let reading = self.sensors.drain(kind).ok_or(SimFailure::from(names::ERROR_NOT_STARTED))?;
        Ok(reading_reply(kind, &reading))
    }

    /// sensor.sim_inject: 標本を 1 つ入れる（模擬だけ。時刻は模擬の壁時計の今）。
    pub(super) fn handle_sensor_sim_inject(&self, request: &Value) -> SimResult {
        let kind = read_kind(request).map_err(SimFailure::invalid_argument)?;
        let sample = read_sample(request).map_err(SimFailure::invalid_argument)?;
        let count = self.sensors.inject(kind, sample, self.clock.now_utc_ms()).ok_or(SimFailure::from(names::ERROR_NOT_STARTED))?;
        let [x, y, z] = sample;
        eprintln!("{LOG_PREFIX} 模擬: センサー {kind} に標本 ({x}, {y}, {z}) を入れました（前回の read から {count} 個目）");
        let mut fields = kind_reply(kind);
        fields.insert(names::KEY_SAMPLE_COUNT.into(), Value::from(count));
        Ok(fields)
    }

    /// 模擬のセンサーが動いていればその頻度（単体テスト・診断用）。
    pub fn sensor_rate_hz(&self, kind: &str) -> Option<i64> {
        self.sensors.rate_hz(kind)
    }
}

/// 返答 `{ kind, simulated }`（どの命令の返答もここから始める）。
fn kind_reply(kind: &str) -> Map<String, Value> {
    let mut fields = Map::new();
    fields.insert(names::KEY_KIND.into(), Value::from(kind));
    fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
    fields
}

/// read の返答 `{ kind, x, y, z, timestamp_ms, peak_magnitude, sample_count, simulated }`。
fn reading_reply(kind: &str, reading: &SimSensorReading) -> Map<String, Value> {
    let mut fields = kind_reply(kind);
    let [x, y, z] = reading.latest;
    fields.insert(names::KEY_X.into(), Value::from(x));
    fields.insert(names::KEY_Y.into(), Value::from(y));
    fields.insert(names::KEY_Z.into(), Value::from(z));
    fields.insert(names::KEY_TIMESTAMP_MS.into(), Value::from(reading.timestamp_ms));
    fields.insert(names::KEY_PEAK_MAGNITUDE.into(), Value::from(reading.peak_magnitude));
    fields.insert(names::KEY_SAMPLE_COUNT.into(), Value::from(reading.sample_count));
    fields
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use serde_json::{json, Value};

    use super::super::wall_clock::ManualClock;
    use super::super::DesktopSimBridge;
    use crate::engine::platform::bridge::wire::{self, alarm as alarm_names, sensor as names};
    use crate::engine::platform::bridge::PlatformBridge;

    /// 模擬の壁時計の最初の時刻（2026-09-27 ごろの UTC の epoch ミリ秒）。
    const START_MS: i64 = 1_790_000_000_000;

    /// 手で進める時計の模擬を作る（テスト用）。
    fn sim() -> (DesktopSimBridge, Arc<ManualClock>) {
        let clock = Arc::new(ManualClock::starting_at(START_MS));
        (DesktopSimBridge::with_clock(clock.clone()), clock)
    }

    /// 命令を送って返答の JSON を読む（テスト用）。
    fn call(sim: &DesktopSimBridge, method: &str, request: Value) -> Value {
        serde_json::from_str(&sim.invoke(names::MODULE, method, &request.to_string()).unwrap()).unwrap()
    }

    /// 重力を除いた加速度の引数（kind だけ）。
    fn linear() -> Value {
        json!({ names::KEY_KIND: names::KIND_LINEAR_ACCELERATION })
    }

    /// 標本を入れる引数。
    fn sample(x: f64, y: f64, z: f64) -> Value {
        json!({ names::KEY_KIND: names::KIND_LINEAR_ACCELERATION, names::KEY_X: x, names::KEY_Y: y, names::KEY_Z: z })
    }

    /// start → read（値 0・数 0）→ stop（止めた）→ もう一度 stop（止めていない・成功）→ read は not_started。
    #[test]
    fn start_read_stop_round_trip() {
        let (sim, _) = sim();
        let started = call(&sim, names::METHOD_START, linear());
        assert_eq!(started[wire::KEY_OK], Value::Bool(true));
        assert_eq!(started[names::KEY_KIND], Value::from(names::KIND_LINEAR_ACCELERATION));
        assert_eq!(started[names::KEY_SUPPORTED], Value::Bool(true));
        assert_eq!(started[names::KEY_SOURCE], Value::from(names::SOURCE_SIMULATED));
        assert_eq!(started[names::KEY_RATE_HZ], Value::from(names::DEFAULT_RATE_HZ), "rate_hz の省略は既定の 50 Hz");
        assert_eq!(started[alarm_names::KEY_SIMULATED], Value::Bool(true));

        let read = call(&sim, names::METHOD_READ, linear());
        assert_eq!(read[wire::KEY_OK], Value::Bool(true));
        for key in [names::KEY_X, names::KEY_Y, names::KEY_Z, names::KEY_PEAK_MAGNITUDE] {
            assert_eq!(read[key].as_f64(), Some(0.0), "{key} は 0");
        }
        assert_eq!(read[names::KEY_TIMESTAMP_MS], Value::from(0), "標本がまだ無ければ時刻は 0");
        assert_eq!(read[names::KEY_SAMPLE_COUNT], Value::from(0));

        assert_eq!(call(&sim, names::METHOD_STOP, linear())[names::KEY_STOPPED], Value::Bool(true));
        let again = call(&sim, names::METHOD_STOP, linear());
        assert_eq!((again[wire::KEY_OK].clone(), again[names::KEY_STOPPED].clone()), (Value::Bool(true), Value::Bool(false)), "冪等");
        assert_eq!(call(&sim, names::METHOD_READ, linear())[wire::KEY_ERROR], Value::from(names::ERROR_NOT_STARTED));
    }

    /// 標本を入れると、read は最新の標本・その時刻・前回の read からの最大の大きさと数を返し、読むと最大と数は 0 に戻る（最新は残る）。
    #[test]
    fn injected_samples_give_peak_and_count_and_read_resets() {
        let (sim, clock) = sim();
        call(&sim, names::METHOD_START, linear());
        // 大きさ 5 → 13 → √3。最大は 13、数は 3、最新は最後の (1, 1, 1)
        assert_eq!(call(&sim, names::METHOD_SIM_INJECT, sample(3.0, 4.0, 0.0))[names::KEY_SAMPLE_COUNT], Value::from(1));
        clock.advance(20);
        call(&sim, names::METHOD_SIM_INJECT, sample(0.0, 0.0, -13.0));
        clock.advance(20);
        assert_eq!(call(&sim, names::METHOD_SIM_INJECT, sample(1.0, 1.0, 1.0))[names::KEY_SAMPLE_COUNT], Value::from(3));

        let first = call(&sim, names::METHOD_READ, linear());
        assert_eq!(first[names::KEY_PEAK_MAGNITUDE].as_f64(), Some(13.0));
        assert_eq!(first[names::KEY_SAMPLE_COUNT], Value::from(3));
        assert_eq!((first[names::KEY_X].as_f64(), first[names::KEY_Y].as_f64(), first[names::KEY_Z].as_f64()), (Some(1.0), Some(1.0), Some(1.0)));
        assert_eq!(first[names::KEY_TIMESTAMP_MS], Value::from(START_MS + 40), "最新の標本を入れた時刻");

        // 読んだ後は最大と数が 0。最新の標本と時刻は残る
        let second = call(&sim, names::METHOD_READ, linear());
        assert_eq!(second[names::KEY_PEAK_MAGNITUDE].as_f64(), Some(0.0));
        assert_eq!(second[names::KEY_SAMPLE_COUNT], Value::from(0));
        assert_eq!(second[names::KEY_X].as_f64(), Some(1.0));
        assert_eq!(second[names::KEY_TIMESTAMP_MS], Value::from(START_MS + 40));

        // 次の区間は、その区間の標本だけで最大を取る（前の区間の 13 を引きずらない）
        call(&sim, names::METHOD_SIM_INJECT, sample(0.0, 2.0, 0.0));
        let third = call(&sim, names::METHOD_READ, linear());
        assert_eq!((third[names::KEY_PEAK_MAGNITUDE].as_f64(), third[names::KEY_SAMPLE_COUNT].clone()), (Some(2.0), Value::from(1)));
    }

    /// 始め直し（動いている間の start）は標本を捨てる。頻度は上限にそろえて返り、記録される。
    #[test]
    fn restart_discards_samples_and_rate_is_clamped() {
        let (sim, _) = sim();
        call(&sim, names::METHOD_START, linear());
        call(&sim, names::METHOD_SIM_INJECT, sample(0.0, 0.0, 20.0));
        let restarted = call(&sim, names::METHOD_START, json!({ names::KEY_KIND: names::KIND_LINEAR_ACCELERATION, names::KEY_RATE_HZ: 1000 }));
        assert_eq!(restarted[names::KEY_RATE_HZ], Value::from(names::MAX_RATE_HZ));
        assert_eq!(sim.sensor_rate_hz(names::KIND_LINEAR_ACCELERATION), Some(names::MAX_RATE_HZ));
        let read = call(&sim, names::METHOD_READ, linear());
        assert_eq!((read[names::KEY_SAMPLE_COUNT].clone(), read[names::KEY_PEAK_MAGNITUDE].as_f64()), (Value::from(0), Some(0.0)));
    }

    /// 誤り: 知らない種類・頻度の誤り・数でない標本は invalid_argument、動いていない種類への標本は not_started。どれも状態を変えない。
    #[test]
    fn invalid_requests_are_rejected() {
        let (sim, _) = sim();
        assert_eq!(call(&sim, names::METHOD_SIM_INJECT, sample(1.0, 2.0, 3.0))[wire::KEY_ERROR], Value::from(names::ERROR_NOT_STARTED));
        for (method, request) in [
            (names::METHOD_START, json!({ names::KEY_KIND: "gyroscope" })),
            (names::METHOD_START, json!({})),
            (names::METHOD_START, json!({ names::KEY_KIND: names::KIND_LINEAR_ACCELERATION, names::KEY_RATE_HZ: 0 })),
            (names::METHOD_READ, json!({ names::KEY_KIND: 3 })),
            (names::METHOD_STOP, json!({ names::KEY_KIND: "LINEAR_ACCELERATION" })),
        ] {
            assert_eq!(call(&sim, method, request.clone())[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT), "{method} {request}");
        }
        assert_eq!(sim.sensor_rate_hz(names::KIND_LINEAR_ACCELERATION), None, "断った start で動き出さない");
        call(&sim, names::METHOD_START, linear());
        let bad = json!({ names::KEY_KIND: names::KIND_LINEAR_ACCELERATION, names::KEY_X: 1, names::KEY_Y: "2", names::KEY_Z: 3 });
        assert_eq!(call(&sim, names::METHOD_SIM_INJECT, bad)[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT));
        assert_eq!(call(&sim, names::METHOD_READ, linear())[names::KEY_SAMPLE_COUNT], Value::from(0), "断った標本は数えない");
    }

    /// エディタの Play の区切りで、動いていたセンサーと標本は消える（次の回の read は not_started）。
    #[test]
    fn play_boundary_stops_sensors() {
        let (sim, _) = sim();
        call(&sim, names::METHOD_START, linear());
        call(&sim, names::METHOD_SIM_INJECT, sample(0.0, 9.0, 0.0));
        sim.reset_session();
        assert_eq!(sim.sensor_rate_hz(names::KIND_LINEAR_ACCELERATION), None);
        assert_eq!(call(&sim, names::METHOD_READ, linear())[wire::KEY_ERROR], Value::from(names::ERROR_NOT_STARTED));
        // 始め直せば 0 から
        call(&sim, names::METHOD_START, linear());
        assert_eq!(call(&sim, names::METHOD_READ, linear())[names::KEY_SAMPLE_COUNT], Value::from(0));
    }
}
