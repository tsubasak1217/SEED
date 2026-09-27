// ============================================================
//  platform/bridge/desktop_sim/app_commands.rs — 模擬の起動理由とアプリの命令（W1-4a の起動理由・W1-6 のディープリンクとアプリ）
//
//  Android ではメインプロセスの Java（platform/local/MainProcessCommands）が IPC なしで答える命令の代わり:
//    platform.launch_reason   … 普通は launcher（デスクトップの Play は普通の起動。目覚ましで起きることは無い）。単体起動の
//                               --deep-link=<URI>（launch_uri.rs）があれば deep_link・uri = <URI>（W1-6）。
//                               返答 { launch: {kind, id, action_id, scheduled_at_utc_ms, fired_at_utc_ms, payload_json, uri, simulated} }
//    app.move_task_to_back    … 何もしない（PC のウィンドウは背面へ回さない。ログだけ）。返答 { simulated }（W1-6）
//    app.open_url             … URL の規則（bridge::app。Android と同じ）で判定し、通ったもののうち http / https / mailto だけを
//                               PC の既定のアプリで開く（url_opener.rs。ほかの scheme は判定だけ）。返答 { scheme, opened, simulated }（W1-6）
//    app.open_app_settings    … 何もしない（ログだけ）。返答 { simulated }（W1-6）
//  platform.launch のイベント（起動後の Intent）は模擬では出ない。画面の命令は window_commands.rs。
// ============================================================

use serde_json::{json, Map, Value};

use super::url_opener::DESKTOP_OPENABLE_SCHEMES;
use super::{DesktopSimBridge, SimFailure, SimResult};
use crate::engine::platform::bridge::app::read_open_url;
use crate::engine::platform::bridge::wire::{alarm as alarm_names, app, launch};
use crate::engine::platform::bridge::LOG_PREFIX;

/// 起動理由の数の欄が無いときの値。
const NO_TIME: i64 = 0;

impl DesktopSimBridge {
    /// platform.launch_reason: 起動引数のディープリンクがあれば deep_link、無ければランチャー（普通の起動）。
    pub(super) fn handle_platform_launch_reason(&self, _request: &Value) -> SimResult {
        let mut fields = Map::new();
        let info = match &self.launch_uri {
            Some(uri) => launch_info(launch::KIND_DEEP_LINK, uri),
            None => launch_info(launch::KIND_LAUNCHER, ""),
        };
        fields.insert(launch::KEY_LAUNCH.into(), info);
        Ok(fields)
    }

    /// app.move_task_to_back: デスクトップでは何もしない（ログだけ）。
    pub(super) fn handle_app_move_task_to_back(&self, _request: &Value) -> SimResult {
        eprintln!("{LOG_PREFIX} 模擬: 閉じずに背面へ（デスクトップでは何もしません。Android では moveTaskToBack）");
        Ok(simulated_reply())
    }

    /// app.open_url: 規則で判定し、http / https / mailto だけを PC の既定のアプリへ渡す。
    pub(super) fn handle_app_open_url(&self, request: &Value) -> SimResult {
        let (url, scheme) = read_open_url(request).map_err(|rejection| SimFailure {
            reason: rejection.reason(),
            detail: Some(rejection.detail().to_string()),
        })?;
        let opened = if DESKTOP_OPENABLE_SCHEMES.contains(&scheme.as_str()) {
            self.url_opener.open(&url)
        } else {
            eprintln!("{LOG_PREFIX} 模擬: {scheme}: の URL は PC では開きません（判定だけ。Android では ACTION_VIEW で開く）: {url}");
            false
        };
        let mut fields = simulated_reply();
        fields.insert(app::KEY_SCHEME.into(), Value::from(scheme));
        fields.insert(app::KEY_OPENED.into(), Value::Bool(opened));
        Ok(fields)
    }

    /// app.open_app_settings: デスクトップでは何もしない（ログだけ）。
    pub(super) fn handle_app_open_app_settings(&self, _request: &Value) -> SimResult {
        eprintln!("{LOG_PREFIX} 模擬: アプリ情報の画面（デスクトップでは何もしません。Android では端末の設定のアプリ情報）");
        Ok(simulated_reply())
    }
}

/// 起動理由（実機の LaunchInfo と同じ欄＋simulated）。
fn launch_info(kind: &str, uri: &str) -> Value {
    json!({
        launch::KEY_KIND: kind,
        alarm_names::KEY_ID: "",
        launch::KEY_ACTION_ID: "",
        alarm_names::KEY_SCHEDULED_AT_UTC_MS: NO_TIME,
        alarm_names::KEY_FIRED_AT_UTC_MS: NO_TIME,
        alarm_names::KEY_PAYLOAD_JSON: "",
        launch::KEY_URI: uri,
        alarm_names::KEY_SIMULATED: true,
    })
}

/// 返答 `{ simulated }`。
fn simulated_reply() -> Map<String, Value> {
    let mut fields = Map::new();
    fields.insert(alarm_names::KEY_SIMULATED.into(), Value::Bool(true));
    fields
}

// ============================================================
//  ユニットテスト（URL は記録するだけの係に渡し、PC では何も開かない）
// ============================================================

#[cfg(test)]
mod tests {
    use std::sync::{Arc, Mutex};

    use serde_json::{json, Value};

    use super::super::url_opener::UrlOpener;
    use super::super::{DesktopSimBridge, SystemWallClock};
    use crate::engine::platform::bridge::wire::{self, alarm as alarm_names, app, launch};
    use crate::engine::platform::bridge::PlatformBridge;

    /// 渡された URL を記録するだけの係（テスト用）。
    #[derive(Debug, Default)]
    struct RecordingOpener {
        /// 渡された URL（渡された順）。
        opened: Mutex<Vec<String>>,
    }

    impl UrlOpener for RecordingOpener {
        fn open(&self, url: &str) -> bool {
            self.opened.lock().unwrap().push(url.to_string());
            true
        }
    }

    /// 記録する係と起動引数のディープリンクを差した模擬（テスト用）。
    fn sim_with(launch_uri: Option<&str>) -> (DesktopSimBridge, Arc<RecordingOpener>) {
        let opener = Arc::new(RecordingOpener::default());
        let sim = DesktopSimBridge::with_parts(Arc::new(SystemWallClock), opener.clone(), launch_uri.map(str::to_string));
        (sim, opener)
    }

    /// 命令を送って返答の JSON を読む（テスト用）。
    fn call(sim: &DesktopSimBridge, module: &str, method: &str, request: Value) -> Value {
        serde_json::from_str(&sim.invoke(module, method, &request.to_string()).unwrap()).unwrap()
    }

    /// 起動理由: 起動引数が無ければランチャー（uri は空）。欄はそろっていて、模擬の印つき。
    #[test]
    fn launch_reason_is_launcher_by_default() {
        let (sim, _) = sim_with(None);
        let reply = call(&sim, wire::MODULE_PLATFORM, launch::METHOD_LAUNCH_REASON, json!({}));
        assert_eq!(reply[wire::KEY_OK], Value::Bool(true));
        let info = &reply[launch::KEY_LAUNCH];
        assert_eq!(info[launch::KEY_KIND], Value::from(launch::KIND_LAUNCHER));
        assert_eq!(info[alarm_names::KEY_ID], Value::from(""));
        assert_eq!(info[launch::KEY_ACTION_ID], Value::from(""));
        assert_eq!(info[alarm_names::KEY_SCHEDULED_AT_UTC_MS], Value::from(0));
        assert_eq!(info[launch::KEY_URI], Value::from(""));
        assert_eq!(info[alarm_names::KEY_SIMULATED], Value::Bool(true));
    }

    /// 起動理由: 起動引数のディープリンクがあれば deep_link と uri。
    #[test]
    fn launch_reason_reports_deep_link_from_launch_argument() {
        let (sim, _) = sim_with(Some("wakeorpay://alarm/x?y=1"));
        let reply = call(&sim, wire::MODULE_PLATFORM, launch::METHOD_LAUNCH_REASON, json!({}));
        let info = &reply[launch::KEY_LAUNCH];
        assert_eq!(info[launch::KEY_KIND], Value::from(launch::KIND_DEEP_LINK));
        assert_eq!(info[launch::KEY_URI], Value::from("wakeorpay://alarm/x?y=1"));
    }

    /// open_url: 規則を通った URL のうち http / https / mailto だけを PC の係へ渡す（ほかは判定だけで opened=false）。
    #[test]
    fn open_url_hands_only_web_and_mail_to_the_desktop() {
        let (sim, opener) = sim_with(None);
        let cases = [
            ("https://example.com/?a=1&b=%20", "https", true),
            ("HTTP://EXAMPLE.COM", "http", true),
            ("mailto:someone@example.com", "mailto", true),
            ("tel:+81-90-0000-0000", "tel", false),
            ("wakeorpay://alarm/x", "wakeorpay", false),
            (r"C:\Windows\notepad.exe", "c", false),
        ];
        for (url, scheme, opened) in cases {
            let reply = call(&sim, app::MODULE, app::METHOD_OPEN_URL, json!({ app::KEY_URL: url }));
            assert_eq!(reply[wire::KEY_OK], Value::Bool(true), "{url}");
            assert_eq!(reply[app::KEY_SCHEME], Value::from(scheme), "{url}");
            assert_eq!(reply[app::KEY_OPENED], Value::Bool(opened), "{url}");
            assert_eq!(reply[alarm_names::KEY_SIMULATED], Value::Bool(true), "{url}");
        }
        assert_eq!(
            *opener.opened.lock().unwrap(),
            vec!["https://example.com/?a=1&b=%20".to_string(), "HTTP://EXAMPLE.COM".to_string(), "mailto:someone@example.com".to_string()]
        );
    }

    /// open_url: 断る scheme は scheme_not_allowed、形の誤りは invalid_argument（どちらも PC の係へ渡さない）。
    #[test]
    fn open_url_rejections_never_reach_the_opener() {
        let (sim, opener) = sim_with(None);
        for (request, reason) in [
            (json!({ app::KEY_URL: "file:///C:/Windows/notepad.exe" }), app::ERROR_SCHEME_NOT_ALLOWED),
            (json!({ app::KEY_URL: "javascript:alert(1)" }), app::ERROR_SCHEME_NOT_ALLOWED),
            (json!({ app::KEY_URL: "content://x/y" }), app::ERROR_SCHEME_NOT_ALLOWED),
            (json!({ app::KEY_URL: "example.com" }), alarm_names::ERROR_INVALID_ARGUMENT),
            (json!({ app::KEY_URL: 7 }), alarm_names::ERROR_INVALID_ARGUMENT),
            (json!({}), alarm_names::ERROR_INVALID_ARGUMENT),
        ] {
            let reply = call(&sim, app::MODULE, app::METHOD_OPEN_URL, request.clone());
            assert_eq!(reply[wire::KEY_OK], Value::Bool(false), "{request}");
            assert_eq!(reply[wire::KEY_ERROR], Value::from(reason), "{request}");
        }
        assert!(opener.opened.lock().unwrap().is_empty(), "断った URL が PC の係へ渡った");
    }

    /// 背面へ・アプリ情報は受け付けるだけ（模擬の印つき）。
    #[test]
    fn move_task_to_back_and_open_app_settings_are_accepted() {
        let (sim, opener) = sim_with(None);
        for method in [app::METHOD_MOVE_TASK_TO_BACK, app::METHOD_OPEN_APP_SETTINGS] {
            let reply = call(&sim, app::MODULE, method, json!({}));
            assert_eq!(reply[wire::KEY_OK], Value::Bool(true), "{method}");
            assert_eq!(reply[alarm_names::KEY_SIMULATED], Value::Bool(true), "{method}");
        }
        assert!(opener.opened.lock().unwrap().is_empty());
    }
}
