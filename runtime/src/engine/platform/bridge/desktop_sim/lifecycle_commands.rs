// ============================================================
//  platform/bridge/desktop_sim/lifecycle_commands.rs — 模擬の前面・背面の命令とイベント（2026-10-01）
//
//  Android の MainActivity.onResume / onPause（platform/app/AppLifecycle が platform.resumed / paused を流す）の代わり:
//    窓のフォーカスの出入り（bridge::notify_host_focus_changed。render.rs の WindowEvent::Focused から、Play の間だけ）
//    app.sim_lifecycle { phase }（模擬だけ。Android では unknown_method）… 返答 { phase, changed }
//  どちらも状態（lifecycle_state.rs）が移ったときだけ、イベントを積む:
//    platform.resumed { count, background_ms, simulated } / platform.paused { count, simulated }
//  エディタに埋め込んだ Play では、エディタの別のパネルを押しただけでもフォーカスが外れて paused が出る（PC の近似。文書に明記）。
// ============================================================

use serde_json::{json, Map, Value};

use super::lifecycle_state::{SimLifecyclePhase, SimLifecycleTransition};
use super::{DesktopSimBridge, SimFailure, SimResult};
use crate::engine::platform::bridge::wire::{alarm as alarm_names, app};
use crate::engine::platform::bridge::LOG_PREFIX;

/// 前面・背面を動かしたものの名前（ログ用）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) enum LifecycleSource {
    /// 窓のフォーカスの出入り。
    WindowFocus,
    /// 模擬の命令（IPC・スクリプト）。
    Command,
}

impl LifecycleSource {
    /// ログ向けの言葉。
    fn label(self) -> &'static str {
        match self {
            LifecycleSource::WindowFocus => "窓のフォーカス",
            LifecycleSource::Command => "模擬の命令",
        }
    }
}

impl DesktopSimBridge {
    /// app.sim_lifecycle（模擬だけ）: 前面・背面へ移し、移ったらイベントを積む。
    pub(super) fn handle_app_sim_lifecycle(&self, request: &Value) -> SimResult {
        let word = request.get(app::KEY_PHASE).and_then(Value::as_str).unwrap_or_default();
        let phase = SimLifecyclePhase::parse(word).ok_or_else(|| {
            SimFailure::invalid_argument(format!("phase は {} / {} のどちらか（{word:?}）", app::LIFECYCLE_RESUMED, app::LIFECYCLE_PAUSED))
        })?;
        let changed = self.move_lifecycle(phase, LifecycleSource::Command);
        let mut fields = Map::new();
        fields.insert(app::KEY_PHASE.into(), Value::from(phase.wire()));
        fields.insert(app::KEY_CHANGED.into(), Value::Bool(changed));
        Ok(fields)
    }

    /// 窓のフォーカスの出入り（Play の間だけ届く）: 前面・背面へ移し、移ったらイベントを積む。
    pub(super) fn on_host_focus_changed(&self, focused: bool) {
        self.move_lifecycle(SimLifecyclePhase::from_focus(focused), LifecycleSource::WindowFocus);
    }

    /// 前面・背面へ移す（既にその状態なら何もしない）。
    ///
    /// # 戻り値
    /// 移った（イベントを積んだ）なら true
    pub(super) fn move_lifecycle(&self, phase: SimLifecyclePhase, source: LifecycleSource) -> bool {
        let Some(transition) = self.lifecycle.transition(phase, self.clock.now_utc_ms()) else {
            return false;
        };
        self.push_event(phase.event_name(), self.clock.now_utc_ms(), lifecycle_event_data(&transition));
        eprintln!(
            "{LOG_PREFIX} 模擬: {}（{}回目{}・{}）",
            phase.event_name(),
            transition.count,
            match transition.phase {
                SimLifecyclePhase::Resumed => format!("・背面 {} ms", transition.background_ms),
                SimLifecyclePhase::Paused => String::new(),
            },
            source.label()
        );
        true
    }
}

/// イベントの data（Android の AppLifecycle と同じ欄に模擬の印。background_ms は resumed だけ）。
fn lifecycle_event_data(transition: &SimLifecycleTransition) -> Value {
    match transition.phase {
        SimLifecyclePhase::Resumed => json!({
            app::KEY_LIFECYCLE_COUNT: transition.count,
            app::KEY_BACKGROUND_MS: transition.background_ms,
            alarm_names::KEY_SIMULATED: true,
        }),
        SimLifecyclePhase::Paused => json!({
            app::KEY_LIFECYCLE_COUNT: transition.count,
            alarm_names::KEY_SIMULATED: true,
        }),
    }
}

// ============================================================
//  ユニットテスト（命令・窓のフォーカス・イベントの形・Play の区切り）
// ============================================================

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use serde_json::{json, Value};

    use super::super::url_opener::DryRunUrlOpener;
    use super::super::wall_clock::ManualClock;
    use super::super::DesktopSimBridge;
    use crate::engine::platform::bridge::wire::{self, alarm as alarm_names, app};
    use crate::engine::platform::bridge::PlatformBridge;

    /// 手で進める時計の模擬を作る。
    fn sim_with_clock() -> (DesktopSimBridge, Arc<ManualClock>) {
        let clock = Arc::new(ManualClock::starting_at(1_700_000_000_000));
        (DesktopSimBridge::with_parts(clock.clone(), Arc::new(DryRunUrlOpener), None), clock)
    }

    /// 命令を送って返答の JSON を読む。
    fn call(sim: &DesktopSimBridge, request: Value) -> Value {
        serde_json::from_str(&sim.invoke(app::MODULE, app::METHOD_SIM_LIFECYCLE, &request.to_string()).unwrap()).unwrap()
    }

    /// 積まれたイベントを (名前, data) で取り出す。
    fn events(sim: &DesktopSimBridge) -> Vec<(String, Value)> {
        sim.poll_events()
            .iter()
            .map(|text| serde_json::from_str::<Value>(text).unwrap())
            .map(|event| (event[wire::KEY_NAME].as_str().unwrap().to_string(), event[wire::KEY_DATA].clone()))
            .collect()
    }

    /// 命令: paused → resumed の組で、data は count・background_ms・simulated。同じ状態は changed = false でイベントなし。
    #[test]
    fn sim_lifecycle_emits_pairs() {
        let (sim, clock) = sim_with_clock();
        let first = call(&sim, json!({ "phase": "resumed" }));
        assert_eq!(first[app::KEY_CHANGED], Value::Bool(false), "始まりは前面");
        assert!(events(&sim).is_empty());

        assert_eq!(call(&sim, json!({ "phase": "paused" }))[app::KEY_CHANGED], Value::Bool(true));
        clock.advance(1_500);
        assert_eq!(call(&sim, json!({ "phase": "resumed" }))[app::KEY_CHANGED], Value::Bool(true));
        let got = events(&sim);
        assert_eq!(got.len(), 2);
        assert_eq!(got[0].0, app::EVENT_PAUSED);
        assert_eq!(got[0].1, json!({ "count": 1, "simulated": true }));
        assert_eq!(got[1].0, app::EVENT_RESUMED);
        assert_eq!(got[1].1[app::KEY_LIFECYCLE_COUNT], Value::from(1));
        assert_eq!(got[1].1[app::KEY_BACKGROUND_MS], Value::from(1_500));
        assert_eq!(got[1].1[alarm_names::KEY_SIMULATED], Value::Bool(true));
    }

    /// 窓のフォーカス: 失う → paused、得る → resumed。重なった知らせは 1 つ。Play の区切りで前面へ戻る。
    #[test]
    fn host_focus_drives_lifecycle() {
        let (sim, _) = sim_with_clock();
        sim.on_host_focus_changed(true);
        assert!(events(&sim).is_empty(), "前面で前面へは知らせない");
        sim.on_host_focus_changed(false);
        sim.on_host_focus_changed(false);
        sim.on_host_focus_changed(true);
        let names: Vec<String> = events(&sim).into_iter().map(|(name, _)| name).collect();
        assert_eq!(names, vec![app::EVENT_PAUSED.to_string(), app::EVENT_RESUMED.to_string()]);
        sim.on_host_focus_changed(false);
        sim.reset_session();
        assert!(sim.lifecycle.is_foreground(), "Play の区切りで前面へ戻る");
        assert!(events(&sim).is_empty(), "積んだイベントも捨てる");
    }

    /// 知らない phase は invalid_argument。
    #[test]
    fn unknown_phase_is_rejected() {
        let (sim, _) = sim_with_clock();
        let reply = call(&sim, json!({ "phase": "stopped" }));
        assert_eq!(reply[wire::KEY_OK], Value::Bool(false));
        assert_eq!(reply[wire::KEY_ERROR], Value::from(alarm_names::ERROR_INVALID_ARGUMENT));
        assert!(sim.lifecycle.is_foreground());
    }
}
