// ============================================================
//  platform/bridge/desktop_sim/ui_mode_commands.rs — 模擬の端末の明暗の命令とイベント（W2-9）
//
//  Android ではメインプロセスの Java（platform/local/UiModeCommand・MainActivity.onConfigurationChanged）が答える命令の代わり:
//    app.ui_mode          … 今の値（差し替え → OS の設定）。返答 { night, simulated }
//    app.sim_set_ui_mode  … 模擬だけ（Android では unknown_method）。引数 { night }（yes / no / unknown、system で差し替えをやめる）。
//                           値が変わったら platform.ui_mode_changed を積む。返答 { night }
//  ウィンドウの ThemeChanged（OS の設定が変わった。render.rs → bridge::notify_host_ui_mode_changed）でも、差し替えていなければ
//  OS の設定を読み直し、変わっていれば platform.ui_mode_changed を積む。
// ============================================================

use serde_json::{json, Map, Value};

use super::ui_mode_state::SimNight;
use super::{now_utc_millis, DesktopSimBridge, SimFailure, SimResult};
use crate::engine::platform::bridge::wire::{self, alarm as alarm_names, app};
use crate::engine::platform::bridge::LOG_PREFIX;

impl DesktopSimBridge {
    /// app.ui_mode: 今の値を返す（知らせた値として覚える＝同じ値のイベントを後で出さない）。
    pub(super) fn handle_app_ui_mode(&self, _request: &Value) -> SimResult {
        let night = self.ui_mode.report();
        let mut fields = Map::new();
        fields.insert(app::KEY_NIGHT.into(), Value::from(night.wire()));
        fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
        Ok(fields)
    }

    /// app.sim_set_ui_mode: 差し替えを置く・やめる。値が変わったらイベントを積む。
    pub(super) fn handle_app_sim_set_ui_mode(&self, request: &Value) -> SimResult {
        let word = request.get(app::KEY_NIGHT).and_then(Value::as_str).unwrap_or_default();
        let overridden = if word == app::NIGHT_SYSTEM {
            None
        } else {
            Some(SimNight::parse(word).ok_or_else(|| {
                SimFailure::invalid_argument(format!(
                    "night は {} / {} / {} / {} のどれか（{word:?}）",
                    app::NIGHT_YES,
                    app::NIGHT_NO,
                    app::NIGHT_UNKNOWN,
                    app::NIGHT_SYSTEM
                ))
            })?)
        };
        self.ui_mode.set_override(overridden);
        eprintln!("{LOG_PREFIX} 模擬: 端末の明暗を {}（{}）", word, if overridden.is_some() { "差し替え" } else { "OS の設定へ戻す" });
        self.queue_ui_mode_change_if_any();
        let mut fields = Map::new();
        fields.insert(app::KEY_NIGHT.into(), Value::from(self.ui_mode.current().wire()));
        Ok(fields)
    }

    /// ウィンドウの ThemeChanged（OS の明暗の設定が変わった）: 差し替えていなければ、変わっていればイベントを積む。
    pub(super) fn on_host_ui_mode_changed(&self) {
        if self.ui_mode.is_overridden() {
            return;
        }
        self.queue_ui_mode_change_if_any();
    }

    /// 今の値が最後に知らせた値と違えば platform.ui_mode_changed を積む。
    fn queue_ui_mode_change_if_any(&self) {
        if let Some(night) = self.ui_mode.take_change() {
            let seq = self.next_event_seq();
            let data = json!({ app::KEY_NIGHT: night.wire(), alarm_names::KEY_SIMULATED: true });
            self.queue_event(wire::event_json(app::EVENT_UI_MODE_CHANGED, seq, now_utc_millis(), data));
            eprintln!("{LOG_PREFIX} 模擬: {}（night = {}）", app::EVENT_UI_MODE_CHANGED, night.wire());
        }
    }
}

// ============================================================
//  ユニットテスト（OS の設定を読む係を差し替える）
// ============================================================

#[cfg(test)]
mod tests {
    use serde_json::{json, Value};

    use super::super::ui_mode_state::SimNight;
    use super::super::DesktopSimBridge;
    use crate::engine::platform::bridge::wire::{self, app};
    use crate::engine::platform::bridge::PlatformBridge;

    /// 命令を送って返答の JSON を読む。
    fn call(sim: &DesktopSimBridge, method: &str, request: Value) -> Value {
        serde_json::from_str(&sim.invoke(app::MODULE, method, &request.to_string()).unwrap()).unwrap()
    }

    /// 積まれたイベントの night の並び。
    fn events(sim: &DesktopSimBridge) -> Vec<String> {
        sim.poll_events()
            .iter()
            .map(|e| serde_json::from_str::<Value>(e).unwrap())
            .filter(|e| e[wire::KEY_NAME] == Value::from(app::EVENT_UI_MODE_CHANGED))
            .map(|e| e[wire::KEY_DATA][app::KEY_NIGHT].as_str().unwrap().to_string())
            .collect()
    }

    /// OS の設定: ダーク。
    fn os_dark() -> SimNight {
        SimNight::Yes
    }

    /// OS の設定: ライト。
    fn os_light() -> SimNight {
        SimNight::No
    }

    /// 問い合わせは OS の設定を返す（模擬の印つき）。
    #[test]
    fn ui_mode_reports_os_setting() {
        let sim = DesktopSimBridge::new();
        sim.ui_mode.set_reader(os_dark);
        let reply = call(&sim, app::METHOD_UI_MODE, json!({}));
        assert_eq!(reply[wire::KEY_OK], Value::Bool(true));
        assert_eq!(reply[app::KEY_NIGHT], Value::from(app::NIGHT_YES));
        assert_eq!(reply["simulated"], Value::Bool(true));
        sim.ui_mode.set_reader(os_light);
        assert_eq!(call(&sim, app::METHOD_UI_MODE, json!({}))[app::KEY_NIGHT], Value::from(app::NIGHT_NO));
    }

    /// 差し替え: 変わったらイベント 1 つ・同じ値ではイベントなし・system で OS の設定へ戻る。
    #[test]
    fn sim_override_emits_event_only_on_change() {
        let sim = DesktopSimBridge::new();
        sim.ui_mode.set_reader(os_dark);
        call(&sim, app::METHOD_UI_MODE, json!({}));
        let reply = call(&sim, app::METHOD_SIM_SET_UI_MODE, json!({ "night": "no" }));
        assert_eq!(reply[app::KEY_NIGHT], Value::from(app::NIGHT_NO));
        assert_eq!(events(&sim), vec![app::NIGHT_NO.to_string()]);
        call(&sim, app::METHOD_SIM_SET_UI_MODE, json!({ "night": "no" }));
        assert!(events(&sim).is_empty(), "同じ値ではイベントを出さない");
        assert_eq!(call(&sim, app::METHOD_UI_MODE, json!({}))[app::KEY_NIGHT], Value::from(app::NIGHT_NO));
        call(&sim, app::METHOD_SIM_SET_UI_MODE, json!({ "night": "system" }));
        assert_eq!(events(&sim), vec![app::NIGHT_YES.to_string()], "OS の設定（ダーク）へ戻る");
    }

    /// 知らない語は invalid_argument（差し替えは変わらない）。
    #[test]
    fn sim_override_rejects_unknown_word() {
        let sim = DesktopSimBridge::new();
        sim.ui_mode.set_reader(os_dark);
        let reply = call(&sim, app::METHOD_SIM_SET_UI_MODE, json!({ "night": "dim" }));
        assert_eq!(reply[wire::KEY_OK], Value::Bool(false));
        assert_eq!(reply[wire::KEY_ERROR], Value::from("invalid_argument"));
        assert!(!sim.ui_mode.is_overridden());
    }

    /// ウィンドウの ThemeChanged: OS の設定が変わっていればイベント・差し替え中は出さない・Play の区切りで差し替えを捨てる。
    #[test]
    fn host_theme_change_follows_os_unless_overridden() {
        let sim = DesktopSimBridge::new();
        sim.ui_mode.set_reader(os_dark);
        call(&sim, app::METHOD_UI_MODE, json!({}));
        sim.on_host_ui_mode_changed();
        assert!(events(&sim).is_empty(), "変わっていなければ出さない");
        sim.ui_mode.set_reader(os_light);
        sim.on_host_ui_mode_changed();
        assert_eq!(events(&sim), vec![app::NIGHT_NO.to_string()]);
        call(&sim, app::METHOD_SIM_SET_UI_MODE, json!({ "night": "yes" }));
        events(&sim);
        sim.ui_mode.set_reader(os_dark);
        sim.ui_mode.set_reader(os_light);
        sim.on_host_ui_mode_changed();
        assert!(events(&sim).is_empty(), "差し替え中は OS の変化を知らせない");
        sim.reset_session();
        assert!(!sim.ui_mode.is_overridden(), "Play の区切りで差し替えを捨てる");
        assert_eq!(call(&sim, app::METHOD_UI_MODE, json!({}))[app::KEY_NIGHT], Value::from(app::NIGHT_NO));
    }
}
