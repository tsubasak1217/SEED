// ============================================================
//  platform/bridge/desktop_sim/back_commands.rs — 模擬の予測型の戻るの命令（W2 の手直し P1-3。docs/android.md §25.18）
//
//  Android ではメインプロセスの Java（platform/local/BackCallbackCommand → MainActivity → back/BackCallbackController）が答える命令と、
//  Java の OnBackAnimationCallback が流すイベントの代わり:
//    app.set_back_callback   … 引数 { on }（アプリが戻るを受けるか）。状態（back_state.rs）に記録し、変わったときだけログ 1 行。
//                              デスクトップに予測型の戻るは無い（PC の戻るは Esc キー）ので何もせず、返答 { on, enabled: false, simulated: true }
//    app.sim_back_gesture    … 模擬だけ（Android では unknown_method）。引数 { phase, progress, edge }。Android の手ぶりと同じ形の
//                              イベント（platform.back_started / back_progressed / back_cancelled / back_invoked）を 1 つ積む。
//                              PC で SEED.UI の予測型の戻るのプレビュー（縮む見た目）を試すため。invoked は platform.back_invoked だけ
//                              （PC の確定は Esc キーで行うので Escape は注入しない）。返答 { gesture, simulated: true }
//  手ぶりの番号の決まりは back_state.rs（Java の BackGestureReporter と同じ）。毎フレーム積む back_progressed はログを出さない
//  （wire::is_high_frequency_event）。エディタの Play の区切りで状態と番号を捨てる（mod.rs の reset_session）。
// ============================================================

use serde_json::{json, Map, Value};

use super::back_state::{SimBackPhase, SimBackSnapshot};
use super::{now_utc_millis, DesktopSimBridge, SimFailure, SimResult};
use crate::engine::platform::bridge::wire::{self, alarm as alarm_names, app};
use crate::engine::platform::bridge::LOG_PREFIX;

/// 進み具合の下限（Android の BackEvent.getProgress の範囲の下端。引数が無いときの値）。
const MIN_PROGRESS: f64 = 0.0;

/// 進み具合の上限（BackEvent.getProgress の範囲の上端。超えた値はここへそろえる）。
const MAX_PROGRESS: f64 = 1.0;

/// 模擬の手ぶりの指の位置（PC の模擬に指は無い。Android でボタンの戻るのときと同じく 0 を入れる）。
const SIM_TOUCH_POSITION: f64 = 0.0;

/// set_back_callback の返答の enabled（デスクトップに予測型の戻るは無い）。
const SIM_PREDICTIVE_BACK_ENABLED: bool = false;

impl DesktopSimBridge {
    /// app.set_back_callback: アプリが戻るを受けるかを記録する（変わったときだけログ）。模擬なので enabled は常に false。
    pub(super) fn handle_app_set_back_callback(&self, request: &Value) -> SimResult {
        let on = request
            .get(app::KEY_ON)
            .and_then(Value::as_bool)
            .ok_or_else(|| SimFailure::invalid_argument(format!("{} は真偽にしてください", app::KEY_ON)))?;
        if self.back.set_app_handles_back(on) {
            eprintln!(
                "{LOG_PREFIX} 模擬: アプリが戻るを受ける = {on}（{}。デスクトップに予測型の戻るは無いので何もしません。PC の戻るは Esc キー）",
                if on { "受ける層がある" } else { "受ける層が無い＝根" }
            );
        }
        let mut fields = Map::new();
        fields.insert(app::KEY_ON.into(), Value::Bool(on));
        fields.insert(app::KEY_ENABLED.into(), Value::Bool(SIM_PREDICTIVE_BACK_ENABLED));
        fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }

    /// app.sim_back_gesture: 戻るの手ぶりのイベントを 1 つ積む（模擬だけ）。
    pub(super) fn handle_app_sim_back_gesture(&self, request: &Value) -> SimResult {
        // 引数を先にすべて確かめる（誤りなら番号を進めない）
        let phase = read_phase(request)?;
        let progress = read_progress(request)?;
        let edge = read_edge(request)?;
        let gesture = self.back.advance(phase);
        let data = if phase.carries_back_event() {
            // started・progressed は Android の BackEvent と同じ欄（指の位置は模擬に無いので 0）
            json!({
                app::KEY_GESTURE: gesture,
                app::KEY_PROGRESS: progress,
                app::KEY_EDGE: edge,
                app::KEY_TOUCH_X: SIM_TOUCH_POSITION,
                app::KEY_TOUCH_Y: SIM_TOUCH_POSITION,
                alarm_names::KEY_SIMULATED: true,
            })
        } else {
            json!({ app::KEY_GESTURE: gesture, alarm_names::KEY_SIMULATED: true })
        };
        let name = phase.event_name();
        let seq = self.next_event_seq();
        self.queue_event(wire::event_json(name, seq, now_utc_millis(), data));
        // 毎フレーム積む進み具合はログを出さない（Android の受け取りの箱と同じ扱い）
        if !wire::is_high_frequency_event(name) {
            eprintln!("{LOG_PREFIX} 模擬: {name}（gesture = {gesture}・progress = {progress}・edge = {edge}）");
        }
        let mut fields = Map::new();
        fields.insert(app::KEY_GESTURE.into(), Value::from(gesture));
        fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }

    /// 模擬の予測型の戻るの状態の写し（単体テスト・診断用）。
    pub fn back_snapshot(&self) -> SimBackSnapshot {
        self.back.snapshot()
    }
}

/// 引数 phase を読む（無い・知らない語は invalid_argument）。
fn read_phase(request: &Value) -> Result<SimBackPhase, SimFailure> {
    let word = request.get(app::KEY_PHASE).and_then(Value::as_str).unwrap_or_default();
    SimBackPhase::parse(word).ok_or_else(|| {
        SimFailure::invalid_argument(format!(
            "{} は {} / {} / {} / {} のどれか（{word:?}）",
            app::KEY_PHASE,
            app::PHASE_STARTED,
            app::PHASE_PROGRESSED,
            app::PHASE_CANCELLED,
            app::PHASE_INVOKED
        ))
    })
}

/// 引数 progress を読む（無ければ 0。数でなければ invalid_argument。範囲外は 0〜1 へそろえる）。
fn read_progress(request: &Value) -> Result<f64, SimFailure> {
    match request.get(app::KEY_PROGRESS) {
        None => Ok(MIN_PROGRESS),
        Some(value) => value
            .as_f64()
            .map(|progress| progress.clamp(MIN_PROGRESS, MAX_PROGRESS))
            .ok_or_else(|| SimFailure::invalid_argument(format!("{} は数（0〜1）にしてください", app::KEY_PROGRESS))),
    }
}

/// 引数 edge を読む（無ければ none。知らない語・文字列でなければ invalid_argument）。
fn read_edge(request: &Value) -> Result<&'static str, SimFailure> {
    let Some(value) = request.get(app::KEY_EDGE) else {
        return Ok(app::EDGE_NONE);
    };
    [app::EDGE_LEFT, app::EDGE_RIGHT, app::EDGE_NONE]
        .into_iter()
        .find(|edge| value.as_str() == Some(*edge))
        .ok_or_else(|| {
            SimFailure::invalid_argument(format!(
                "{} は {} / {} / {} のどれか（{value}）",
                app::KEY_EDGE,
                app::EDGE_LEFT,
                app::EDGE_RIGHT,
                app::EDGE_NONE
            ))
        })
}

// ============================================================
//  ユニットテスト（ローカルの模擬だけを使う）
// ============================================================

#[cfg(test)]
mod tests {
    use serde_json::{json, Value};

    use super::super::back_state::SimBackSnapshot;
    use super::super::DesktopSimBridge;
    use crate::engine::platform::bridge::wire::{self, alarm as alarm_names, app};
    use crate::engine::platform::bridge::PlatformBridge;

    /// 命令を送って返答の JSON を読む。
    fn call(sim: &DesktopSimBridge, method: &str, request: Value) -> Value {
        serde_json::from_str(&sim.invoke(app::MODULE, method, &request.to_string()).unwrap()).unwrap()
    }

    /// 手ぶりのイベントを 1 つ積む（返答を返す）。
    fn gesture(sim: &DesktopSimBridge, phase: &str, progress: f64, edge: &str) -> Value {
        call(sim, app::METHOD_SIM_BACK_GESTURE, json!({ app::KEY_PHASE: phase, app::KEY_PROGRESS: progress, app::KEY_EDGE: edge }))
    }

    /// 積まれたイベントを取り出して読む（名前と data の組）。
    fn events(sim: &DesktopSimBridge) -> Vec<(String, Value)> {
        sim.poll_events()
            .iter()
            .map(|text| serde_json::from_str::<Value>(text).unwrap())
            .map(|event| (event[wire::KEY_NAME].as_str().unwrap().to_string(), event[wire::KEY_DATA].clone()))
            .collect()
    }

    /// set_back_callback: 受け付けて記録し、模擬なので enabled は false（simulated の印つき）。
    #[test]
    fn set_back_callback_records_and_reports_disabled() {
        let sim = DesktopSimBridge::new();
        assert_eq!(sim.back_snapshot().app_handles_back, None, "知らせる前は None");
        for on in [false, true, true] {
            let reply = call(&sim, app::METHOD_SET_BACK_CALLBACK, json!({ app::KEY_ON: on }));
            assert_eq!(reply[wire::KEY_OK], Value::Bool(true));
            assert_eq!(reply[app::KEY_ON], Value::Bool(on));
            assert_eq!(reply[app::KEY_ENABLED], Value::Bool(false), "デスクトップに予測型の戻るは無い");
            assert_eq!(reply[alarm_names::KEY_SIMULATED], Value::Bool(true));
            assert_eq!(sim.back_snapshot().app_handles_back, Some(on));
        }
        assert!(sim.poll_events().is_empty(), "set_back_callback はイベントを積まない");
    }

    /// set_back_callback: 真偽でない引数は invalid_argument（状態は変わらない）。
    #[test]
    fn set_back_callback_accepts_bool_only() {
        let sim = DesktopSimBridge::new();
        for bad in [json!({}), json!({ app::KEY_ON: "true" }), json!({ app::KEY_ON: 1 })] {
            let reply = call(&sim, app::METHOD_SET_BACK_CALLBACK, bad);
            assert_eq!(reply[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT));
        }
        assert_eq!(sim.back_snapshot(), SimBackSnapshot::default());
    }

    /// 番号の決まり（Java の BackGestureReporter と同じ）: started で増え、progressed・cancelled・invoked は同じ番号。
    /// started の無い invoked（Android 13・ボタンだけの模擬）は invoked で増える。
    #[test]
    fn gesture_numbers_follow_the_java_rules() {
        let sim = DesktopSimBridge::new();
        let steps = [
            (app::PHASE_STARTED, 1),     // 1 つ目の手ぶり
            (app::PHASE_PROGRESSED, 1),
            (app::PHASE_PROGRESSED, 1),
            (app::PHASE_INVOKED, 1),     // 確定（手ぶりの途中なので増えない）
            (app::PHASE_INVOKED, 2),     // started の無い確定は新しい番号
            (app::PHASE_STARTED, 3),     // 3 つ目
            (app::PHASE_CANCELLED, 3),   // 取り消しは同じ番号
            (app::PHASE_INVOKED, 4),     // 取り消しの後の確定は新しい番号
        ];
        for (index, (phase, expected)) in steps.into_iter().enumerate() {
            let reply = gesture(&sim, phase, 0.5, app::EDGE_LEFT);
            assert_eq!(reply[app::KEY_GESTURE], Value::from(expected), "{index} 番目の {phase}");
        }
        let names: Vec<String> = events(&sim).into_iter().map(|(name, _)| name).collect();
        assert_eq!(
            names,
            vec![
                app::EVENT_BACK_STARTED,
                app::EVENT_BACK_PROGRESSED,
                app::EVENT_BACK_PROGRESSED,
                app::EVENT_BACK_INVOKED,
                app::EVENT_BACK_INVOKED,
                app::EVENT_BACK_STARTED,
                app::EVENT_BACK_CANCELLED,
                app::EVENT_BACK_INVOKED
            ]
        );
    }

    /// イベントの形: started・progressed は進み具合・端・指の位置（0）つき、cancelled・invoked は番号だけ。進み具合は 0〜1 にそろえる。
    #[test]
    fn gesture_event_shapes() {
        let sim = DesktopSimBridge::new();
        gesture(&sim, app::PHASE_STARTED, 0.0, app::EDGE_RIGHT);
        gesture(&sim, app::PHASE_PROGRESSED, 1.5, app::EDGE_RIGHT);
        call(&sim, app::METHOD_SIM_BACK_GESTURE, json!({ app::KEY_PHASE: app::PHASE_PROGRESSED }));
        gesture(&sim, app::PHASE_CANCELLED, 0.3, app::EDGE_RIGHT);
        let got = events(&sim);
        let (_, started) = &got[0];
        assert_eq!(started[app::KEY_GESTURE], Value::from(1));
        assert_eq!(started[app::KEY_PROGRESS], Value::from(0.0));
        assert_eq!(started[app::KEY_EDGE], Value::from(app::EDGE_RIGHT));
        assert_eq!(started[app::KEY_TOUCH_X], Value::from(0.0));
        assert_eq!(started[app::KEY_TOUCH_Y], Value::from(0.0));
        assert_eq!(started[alarm_names::KEY_SIMULATED], Value::Bool(true));
        assert_eq!(got[1].1[app::KEY_PROGRESS], Value::from(1.0), "1 を超えた値は 1 にそろえる");
        assert_eq!(got[2].1[app::KEY_PROGRESS], Value::from(0.0), "進み具合が無ければ 0");
        assert_eq!(got[2].1[app::KEY_EDGE], Value::from(app::EDGE_NONE), "端が無ければ none");
        let (name, cancelled) = &got[3];
        assert_eq!(name, app::EVENT_BACK_CANCELLED);
        assert_eq!(*cancelled, json!({ app::KEY_GESTURE: 1, alarm_names::KEY_SIMULATED: true }), "取り消しは番号だけ");
        for (name, _) in &got {
            assert!(name.starts_with(wire::EVENT_NAME_PREFIX));
        }
    }

    /// 誤った引数は invalid_argument で、番号を進めずイベントも積まない。
    #[test]
    fn gesture_rejects_bad_arguments() {
        let sim = DesktopSimBridge::new();
        for bad in [
            json!({}),
            json!({ app::KEY_PHASE: "done" }),
            json!({ app::KEY_PHASE: app::PHASE_STARTED, app::KEY_PROGRESS: "half" }),
            json!({ app::KEY_PHASE: app::PHASE_STARTED, app::KEY_EDGE: "top" }),
            json!({ app::KEY_PHASE: app::PHASE_STARTED, app::KEY_EDGE: 1 }),
        ] {
            let reply = call(&sim, app::METHOD_SIM_BACK_GESTURE, bad.clone());
            assert_eq!(reply[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT), "{bad}");
        }
        assert_eq!(sim.back_snapshot().gesture, 0, "番号は進まない");
        assert!(sim.poll_events().is_empty(), "イベントは積まない");
    }

    /// Play の区切りで、受けるかの記録と番号・途中の印を捨てる（次の Play の最初の手ぶりはまた 1）。
    #[test]
    fn reset_session_clears_back_state() {
        let sim = DesktopSimBridge::new();
        call(&sim, app::METHOD_SET_BACK_CALLBACK, json!({ app::KEY_ON: false }));
        gesture(&sim, app::PHASE_STARTED, 0.2, app::EDGE_LEFT);
        sim.reset_session();
        assert_eq!(sim.back_snapshot(), SimBackSnapshot::default());
        assert!(sim.poll_events().is_empty(), "積んだイベントも捨てる");
        assert_eq!(gesture(&sim, app::PHASE_INVOKED, 0.0, app::EDGE_NONE)[app::KEY_GESTURE], Value::from(1));
    }
}
