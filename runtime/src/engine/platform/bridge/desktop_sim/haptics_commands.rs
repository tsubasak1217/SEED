// ============================================================
//  platform/bridge/desktop_sim/haptics_commands.rs — 模擬の触感の命令（W1-6）
//
//  Android ではメインプロセスの Java（platform/local/HapticsTapCommand・HapticsVibrateCommand → platform/haptics/HapticFeedback）が
//  振動子を鳴らす命令の代わり。PC は振動しないので、受け付けて記録（haptics_state.rs）し、[SEED PLATFORM] のログを出すだけ:
//    haptics.tap      … 返答 { simulated }
//    haptics.vibrate  … 引数 { ms }（規則は bridge::haptics::read_vibrate_ms。Java と同じ）。返答 { ms（そろえた後）, simulated }
//  振動子が無い（no_vibrator）ことは模擬では起きない。
// ============================================================

use serde_json::{Map, Value};

use super::haptics_state::SimHapticsSnapshot;
use super::{DesktopSimBridge, SimFailure, SimResult};
use crate::engine::platform::bridge::haptics::read_vibrate_ms;
use crate::engine::platform::bridge::wire::{alarm as alarm_names, haptics as names};
use crate::engine::platform::bridge::LOG_PREFIX;

impl DesktopSimBridge {
    /// haptics.tap: 記録してログに残す。
    pub(super) fn handle_haptics_tap(&self, _request: &Value) -> SimResult {
        let count = self.haptics.record_tap();
        eprintln!("{LOG_PREFIX} 模擬: 触感 tap（{count} 回目。PC は振動しません）");
        Ok(simulated_reply())
    }

    /// haptics.vibrate: 長さを読んで（上限にそろえ）記録し、ログに残す。
    pub(super) fn handle_haptics_vibrate(&self, request: &Value) -> SimResult {
        let milliseconds = read_vibrate_ms(request).map_err(SimFailure::invalid_argument)?;
        let count = self.haptics.record_vibrate(milliseconds);
        eprintln!("{LOG_PREFIX} 模擬: 触感 vibrate {milliseconds} ms（{count} 回目。PC は振動しません）");
        let mut fields = simulated_reply();
        fields.insert(names::KEY_MS.into(), Value::from(milliseconds));
        Ok(fields)
    }

    /// 模擬の触感の記録の写し（単体テスト・診断用）。
    pub fn haptics_snapshot(&self) -> SimHapticsSnapshot {
        self.haptics.snapshot()
    }
}

/// 返答 `{ simulated }`。
fn simulated_reply() -> Map<String, Value> {
    let mut fields = Map::new();
    fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
    fields
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use serde_json::{json, Value};

    use super::super::haptics_state::SimHapticsSnapshot;
    use super::super::DesktopSimBridge;
    use crate::engine::platform::bridge::wire::{self, alarm as alarm_names, haptics as names};
    use crate::engine::platform::bridge::PlatformBridge;

    /// 命令を送って返答の JSON を読む（テスト用）。
    fn call(sim: &DesktopSimBridge, method: &str, request: Value) -> Value {
        serde_json::from_str(&sim.invoke(names::MODULE, method, &request.to_string()).unwrap()).unwrap()
    }

    /// tap と vibrate は受け付けて数える。vibrate は上限にそろえた長さを返して記録する。
    #[test]
    fn taps_and_vibrations_are_recorded() {
        let sim = DesktopSimBridge::new();
        assert_eq!(sim.haptics_snapshot(), SimHapticsSnapshot::default());
        let tap = call(&sim, names::METHOD_TAP, json!({}));
        assert_eq!((tap[wire::KEY_OK].clone(), tap[alarm_names::KEY_SIMULATED].clone()), (Value::Bool(true), Value::Bool(true)));
        call(&sim, names::METHOD_TAP, json!({}));
        let short = call(&sim, names::METHOD_VIBRATE, json!({ names::KEY_MS: 40 }));
        assert_eq!(short[names::KEY_MS], Value::from(40));
        let long = call(&sim, names::METHOD_VIBRATE, json!({ names::KEY_MS: 60_000 }));
        assert_eq!(long[names::KEY_MS], Value::from(names::MAX_VIBRATE_MS), "上限にそろえる");
        assert_eq!(
            sim.haptics_snapshot(),
            SimHapticsSnapshot { taps: 2, vibrations: 2, last_vibrate_ms: Some(names::MAX_VIBRATE_MS) }
        );
    }

    /// vibrate の誤り（下限未満・数でない）は invalid_argument で、記録しない。Play の区切りで空になる。
    #[test]
    fn invalid_vibrations_are_rejected_and_reset_clears() {
        let sim = DesktopSimBridge::new();
        for bad in [json!({}), json!({ names::KEY_MS: 0 }), json!({ names::KEY_MS: "40" })] {
            let reply = call(&sim, names::METHOD_VIBRATE, bad);
            assert_eq!(reply[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT));
        }
        assert_eq!(sim.haptics_snapshot().vibrations, 0);
        call(&sim, names::METHOD_TAP, json!({}));
        sim.reset_session();
        assert_eq!(sim.haptics_snapshot(), SimHapticsSnapshot::default());
    }
}
