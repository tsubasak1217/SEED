// ============================================================
//  platform/bridge/desktop_sim/window_commands.rs — 模擬の画面の命令（W1-4a の set_show_when_locked・W1-6 の 2 つ）
//
//  Android ではメインプロセスの Java（platform/local/WindowToggleCommand の 3 つ）が MainActivity の窓を UI スレッドで切り替える命令の代わり:
//    window.set_show_when_locked      … ロック画面の上に出す＋画面を点ける（デスクトップにロック画面は無い）
//    window.set_keep_screen_on        … 画面を点けたままにする（デスクトップの画面の消灯は触らない）
//    window.set_system_bars_visible   … システムバーを出す・隠す（デスクトップのウィンドウには何もしない。安全領域も全画面のまま）
//  どれも引数 { on: 真偽 }（真偽でなければ invalid_argument）で、受け付けて状態（window_state.rs）に記録し、[SEED PLATFORM] のログを
//  出して { on, simulated } を返す。
// ============================================================

use serde_json::{Map, Value};

use super::window_state::SimWindowSnapshot;
use super::{DesktopSimBridge, SimFailure, SimResult};
use crate::engine::platform::bridge::wire::{alarm as alarm_names, window};
use crate::engine::platform::bridge::LOG_PREFIX;

impl DesktopSimBridge {
    /// window.set_show_when_locked: 受け付けて記録し、ログに残す。
    pub(super) fn handle_window_set_show_when_locked(&self, request: &Value) -> SimResult {
        let on = read_on(request)?;
        self.window.set_show_when_locked(on);
        self.log_window("ロック画面の上に出す・画面を点ける", on);
        Ok(on_reply(on))
    }

    /// window.set_keep_screen_on: 受け付けて記録し、ログに残す。
    pub(super) fn handle_window_set_keep_screen_on(&self, request: &Value) -> SimResult {
        let on = read_on(request)?;
        self.window.set_keep_screen_on(on);
        self.log_window("画面を点けたままにする", on);
        Ok(on_reply(on))
    }

    /// window.set_system_bars_visible: 受け付けて記録し、ログに残す。
    pub(super) fn handle_window_set_system_bars_visible(&self, request: &Value) -> SimResult {
        let on = read_on(request)?;
        self.window.set_system_bars_visible(on);
        self.log_window("システムバーを出す", on);
        Ok(on_reply(on))
    }

    /// 模擬の画面の状態の写し（単体テスト・診断用）。
    pub fn window_snapshot(&self) -> SimWindowSnapshot {
        self.window.snapshot()
    }

    /// 切り替えのログ（デスクトップでは何もしない旨と、今の状態の全体）。
    fn log_window(&self, what: &str, on: bool) {
        let state = self.window.snapshot();
        let bars = state.system_bars_visible.map_or_else(|| "既定".to_string(), |visible| visible.to_string());
        eprintln!(
            "{LOG_PREFIX} 模擬: {what} = {on}（デスクトップでは何もしません。状態: ロック画面の上 {}・点けたまま {}・システムバー {bars}）",
            state.show_when_locked, state.keep_screen_on,
        );
    }
}

/// 引数 `{ on }` を読む（真偽でなければ invalid_argument。Java の WindowToggleCommand と同じ）。
fn read_on(request: &Value) -> Result<bool, SimFailure> {
    request
        .get(window::KEY_ON)
        .and_then(Value::as_bool)
        .ok_or_else(|| SimFailure::invalid_argument(format!("{} は真偽にしてください", window::KEY_ON)))
}

/// 返答 `{ on, simulated }`。
fn on_reply(on: bool) -> Map<String, Value> {
    let mut fields = Map::new();
    fields.insert(window::KEY_ON.into(), Value::Bool(on));
    fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
    fields
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use serde_json::{json, Value};

    use super::super::window_state::SimWindowSnapshot;
    use super::super::DesktopSimBridge;
    use crate::engine::platform::bridge::wire::{self, alarm as alarm_names, window};
    use crate::engine::platform::bridge::PlatformBridge;

    /// 命令を送って返答の JSON を読む（テスト用）。
    fn call(sim: &DesktopSimBridge, method: &str, request: Value) -> Value {
        serde_json::from_str(&sim.invoke(window::MODULE, method, &request.to_string()).unwrap()).unwrap()
    }

    /// 3 つの命令: 真偽なら受け付けて同じ値を返し、状態に記録する（システムバーは切り替えるまで既定＝None）。
    #[test]
    fn toggles_are_recorded() {
        let sim = DesktopSimBridge::new();
        assert_eq!(sim.window_snapshot(), SimWindowSnapshot::default());
        assert_eq!(sim.window_snapshot().system_bars_visible, None, "切り替えるまではプロジェクト設定の既定");
        for (method, on) in [
            (window::METHOD_SET_SHOW_WHEN_LOCKED, true),
            (window::METHOD_SET_KEEP_SCREEN_ON, true),
            (window::METHOD_SET_SYSTEM_BARS_VISIBLE, false),
        ] {
            let reply = call(&sim, method, json!({ window::KEY_ON: on }));
            assert_eq!(reply[wire::KEY_OK], Value::Bool(true), "{method}");
            assert_eq!(reply[window::KEY_ON], Value::Bool(on), "{method}");
            assert_eq!(reply[alarm_names::KEY_SIMULATED], Value::Bool(true), "{method}");
        }
        assert_eq!(
            sim.window_snapshot(),
            SimWindowSnapshot { show_when_locked: true, keep_screen_on: true, system_bars_visible: Some(false) }
        );
        // 戻す
        call(&sim, window::METHOD_SET_SYSTEM_BARS_VISIBLE, json!({ window::KEY_ON: true }));
        call(&sim, window::METHOD_SET_KEEP_SCREEN_ON, json!({ window::KEY_ON: false }));
        let state = sim.window_snapshot();
        assert_eq!((state.keep_screen_on, state.system_bars_visible), (false, Some(true)));
    }

    /// 真偽でない引数は invalid_argument（状態は変わらない）。
    #[test]
    fn toggles_accept_bool_only() {
        let sim = DesktopSimBridge::new();
        for method in [window::METHOD_SET_SHOW_WHEN_LOCKED, window::METHOD_SET_KEEP_SCREEN_ON, window::METHOD_SET_SYSTEM_BARS_VISIBLE] {
            for bad in [json!({}), json!({ window::KEY_ON: "yes" }), json!({ window::KEY_ON: 1 })] {
                let reply = call(&sim, method, bad);
                assert_eq!(reply[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT), "{method}");
            }
        }
        assert_eq!(sim.window_snapshot(), SimWindowSnapshot::default());
    }

    /// Play の区切りで既定へ戻る。
    #[test]
    fn reset_session_restores_defaults() {
        let sim = DesktopSimBridge::new();
        call(&sim, window::METHOD_SET_KEEP_SCREEN_ON, json!({ window::KEY_ON: true }));
        call(&sim, window::METHOD_SET_SYSTEM_BARS_VISIBLE, json!({ window::KEY_ON: true }));
        sim.reset_session();
        assert_eq!(sim.window_snapshot(), SimWindowSnapshot::default());
    }
}
