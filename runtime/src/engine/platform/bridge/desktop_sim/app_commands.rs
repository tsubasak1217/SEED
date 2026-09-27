// ============================================================
//  platform/bridge/desktop_sim/app_commands.rs — 模擬の起動理由と画面の命令（W1-4a）
//
//  Android ではメインプロセスの Java（platform/local/MainProcessCommands）が IPC なしで答える命令の代わり:
//    platform.launch_reason       … 常に launcher（デスクトップの Play は普通の起動。目覚ましで起きることは無い）。
//                                   返答 { launch: {kind, id, action_id, scheduled_at_utc_ms, fired_at_utc_ms, payload_json, simulated} }
//    window.set_show_when_locked  … 受け付けてログに残すだけ（デスクトップにロック画面は無い）。返答 { on, simulated }
//  platform.launch のイベント（起動後の Intent）は模擬では出ない。
// ============================================================

use serde_json::{json, Map, Value};

use super::{DesktopSimBridge, SimFailure, SimResult};
use crate::engine::platform::bridge::wire::{alarm as alarm_names, launch, window};
use crate::engine::platform::bridge::LOG_PREFIX;

/// 起動理由の数の欄が無いときの値。
const NO_TIME: i64 = 0;

impl DesktopSimBridge {
    /// platform.launch_reason: デスクトップは常にランチャー（普通の起動）。
    pub(super) fn handle_platform_launch_reason(&self, _request: &Value) -> SimResult {
        let mut fields = Map::new();
        fields.insert(launch::KEY_LAUNCH.into(), launcher_launch());
        Ok(fields)
    }

    /// window.set_show_when_locked: 受け付けてログに残すだけ（引数 on は真偽であること）。
    pub(super) fn handle_window_set_show_when_locked(&self, request: &Value) -> SimResult {
        let Some(on) = request.get(window::KEY_ON).and_then(Value::as_bool) else {
            return Err(SimFailure::invalid_argument(format!("{} は真偽にしてください", window::KEY_ON)));
        };
        eprintln!("{LOG_PREFIX} 模擬: ロック画面の上に出す・画面を点ける = {on}（デスクトップでは何もしません）");
        let mut fields = Map::new();
        fields.insert(window::KEY_ON.into(), Value::Bool(on));
        fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }
}

/// ランチャーの起動理由（実機の LaunchInfo.LAUNCHER と同じ欄＋simulated）。
fn launcher_launch() -> Value {
    json!({
        launch::KEY_KIND: launch::KIND_LAUNCHER,
        alarm_names::KEY_ID: "",
        launch::KEY_ACTION_ID: "",
        alarm_names::KEY_SCHEDULED_AT_UTC_MS: NO_TIME,
        alarm_names::KEY_FIRED_AT_UTC_MS: NO_TIME,
        alarm_names::KEY_PAYLOAD_JSON: "",
        alarm_names::KEY_SIMULATED: true,
    })
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use serde_json::{json, Value};

    use super::super::DesktopSimBridge;
    use crate::engine::platform::bridge::wire::{self, alarm as alarm_names, launch, window};
    use crate::engine::platform::bridge::PlatformBridge;

    /// 命令を送って返答の JSON を読む（テスト用）。
    fn call(sim: &DesktopSimBridge, module: &str, method: &str, request: Value) -> Value {
        serde_json::from_str(&sim.invoke(module, method, &request.to_string()).unwrap()).unwrap()
    }

    /// 起動理由はランチャー（欄はそろっていて、模擬の印つき）。
    #[test]
    fn launch_reason_is_launcher() {
        let sim = DesktopSimBridge::new();
        let reply = call(&sim, wire::MODULE_PLATFORM, launch::METHOD_LAUNCH_REASON, json!({}));
        assert_eq!(reply[wire::KEY_OK], Value::Bool(true));
        let info = &reply[launch::KEY_LAUNCH];
        assert_eq!(info[launch::KEY_KIND], Value::from(launch::KIND_LAUNCHER));
        assert_eq!(info[alarm_names::KEY_ID], Value::from(""));
        assert_eq!(info[launch::KEY_ACTION_ID], Value::from(""));
        assert_eq!(info[alarm_names::KEY_SCHEDULED_AT_UTC_MS], Value::from(0));
        assert_eq!(info[alarm_names::KEY_SIMULATED], Value::Bool(true));
    }

    /// ロック画面の上に出す: 真偽なら受け付けて同じ値を返し、真偽でなければ invalid_argument。
    #[test]
    fn set_show_when_locked_accepts_bool_only() {
        let sim = DesktopSimBridge::new();
        for on in [true, false] {
            let reply = call(&sim, window::MODULE, window::METHOD_SET_SHOW_WHEN_LOCKED, json!({ window::KEY_ON: on }));
            assert_eq!((reply[wire::KEY_OK].clone(), reply[window::KEY_ON].clone()), (Value::Bool(true), Value::Bool(on)));
        }
        for bad in [json!({}), json!({ window::KEY_ON: "yes" })] {
            let reply = call(&sim, window::MODULE, window::METHOD_SET_SHOW_WHEN_LOCKED, bad);
            assert_eq!(reply[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT));
        }
    }
}
