// ============================================================
//  platform/bridge/desktop_sim/mod.rs — デスクトップの模擬（DesktopSimBridge。W1-P7。W1-3 で目覚ましを追加）
//
//  【役割】
//  PC の Play（エディタ埋め込み・単体起動）で SEED.Platform のスクリプトを動かすための、プロセスの中だけで完結する
//  PlatformBridge。Android の :seed_platform（PlatformProvider）と同じ命令に同じ形の JSON で答える。
//  画面づくりの大半はデスクトップで行う（docs/app_platform_roadmap.md X-5）ので、端末が無くても API の流れを試せるようにする。
//
//  【命令の足し方（データ駆動）】
//  命令は下の表 SIM_COMMANDS の 1 行（module・method・処理の関数）。W1-3 以降の模擬（目覚ましの予約をタイマーで鳴らす等）は、
//  表に行を足し、時間で起きるものは poll_events（エンジンがフレームの頭で呼ぶ）の中で「時刻が来たらイベントを積む」形にする。
//  その時刻は next_timed_event_delay_ms で知らせる（render_policy の on_demand で描画を止めている間も、その時刻に起きて
//  poll_events を回すため。W2-10a）。イベントは queue_event で積む（描く理由 platform_event も一緒に積む）。
//
//  【W1-1 の命令】（Java の CorePlatformModule と同じ意味）
//    platform.ping            … 受け取った JSON を echo に入れて返す＋pid（SEED.exe のもの）・模擬を作ってからの ms・simulated=true
//    platform.version         … プロトコルの版と使えるモジュール
//    platform.emit_test_event … 試験イベント platform.test_event を 1 つ積む（次のフレームでスクリプトへ届く）
//  【W1-3 の命令】（Java の AlarmModule と同じ意味。中身は alarm_commands.rs・予約表は alarm_book.rs）
//    alarm.schedule / cancel / cancel_all / list / can_schedule_exact … 予約表はプロセスの中だけ。壁時計（wall_clock.rs）が
//    予定時刻を過ぎたら poll_events で platform.alarm.fired を積む。エディタの Play の区切り（reset_session）で空にする。
//  【W1-4a の命令】（Java の RingService・AlarmModule・メインプロセスの MainProcessCommands と同じ意味）
//    alarm.get_ringing / stop_ringing … 発火した予約は鳴動の状態（ring_state.rs）へ渡り、鳴動中なら待ち行列（platform.alarm.queued）。
//        止める・安全弁（poll_events の中で max_ring_minutes を見る）で platform.alarm.ring_stopped。音は鳴らさない（ring_commands.rs）
//    platform.launch_reason … launcher（app_commands.rs。W1-6 から、単体起動の --deep-link=<URI> があれば deep_link）
//    window.set_show_when_locked … 受け付けて記録しログだけ（window_commands.rs）
//  【W1-5 の命令】（Java の :seed_platform の NotificationModule・メインプロセスの Permission*Command と同じ意味）
//    notification.ensure_channel / show / cancel / are_enabled … チャネルと出ている通知をプロセスの中に持ち、
//        show は [SEED PLATFORM] 通知: … のログ。are_enabled は常に true（notification_commands.rs・notification_state.rs）
//    permission.check / request / open_settings … v1 の種類は常に granted、v2 の予約の種類は not_applicable。
//        request はすぐ platform.permission_result を積む（permission_commands.rs）
//  【W1-6 の命令】（Java のメインプロセスの local/ の命令と同じ意味）
//    window.set_keep_screen_on / set_system_bars_visible … 受け付けて記録しログだけ（window_commands.rs・window_state.rs）
//    app.move_task_to_back / open_app_settings … 何もしない（ログだけ。app_commands.rs）
//    app.open_url … Android と同じ URL の規則（bridge::app）で判定し、http / https / mailto だけを PC の既定のアプリで開く
//        （url_opener.rs。シェルを通さない。単体テストと SEED_PLATFORM_SIM_NO_OPEN=1 は判定だけ）
//    haptics.tap / vibrate … 記録してログだけ（PC は振動しない。haptics_commands.rs・haptics_state.rs）
//  【W1-8 の命令】（Java のメインプロセスの local/Sensor*Command と同じ意味）
//    sensor.start / stop / read … 受け付けて種類ごとの状態を持つ（source は "simulated"）。PC にセンサーは無いので、値は
//        sensor.sim_inject（模擬だけの命令。Android では unknown_method）で入れた標本だけ。read は前回の read からの最大の大きさと
//        標本の数を返して 0 に戻す（sensor_commands.rs・sensor_state.rs）
//
//  【ファイル】mod.rs（表と共通）・alarm_book.rs（模擬の予約表）・alarm_commands.rs（目覚ましの命令と発火）・
//  ring_state.rs（鳴動の状態）・ring_commands.rs（鳴動の命令とイベント）・app_commands.rs（起動理由・アプリ）・
//  launch_uri.rs（起動引数のディープリンク）・url_opener.rs（URL を PC で開く係）・window_state.rs / window_commands.rs（画面）・
//  haptics_state.rs / haptics_commands.rs（触感）・notification_state.rs（通知の状態）・notification_commands.rs（通知の命令）・
//  permission_commands.rs（権限の命令）・sensor_state.rs / sensor_commands.rs（センサー）・wall_clock.rs（壁時計。テストで進める）
// ============================================================

/// 模擬の目覚ましの予約表。
mod alarm_book;
/// 模擬の目覚ましの命令と発火。
mod alarm_commands;
/// 模擬の起動理由とアプリの命令（W1-4a・W1-6）。
mod app_commands;
/// 模擬の触感の命令（W1-6）。
mod haptics_commands;
/// 模擬の触感の記録（W1-6）。
mod haptics_state;
/// 起動引数のディープリンク（W1-6）。
mod launch_uri;
/// 模擬の通知の命令（W1-5）。
mod notification_commands;
/// 模擬の通知の状態（W1-5）。
mod notification_state;
/// 模擬の権限の命令（W1-5）。
mod permission_commands;
/// 模擬の鳴動の命令とイベント（W1-4a）。
mod ring_commands;
/// 模擬の鳴動の状態（W1-4a）。
mod ring_state;
/// 模擬のセンサーの命令（W1-8）。
mod sensor_commands;
/// 模擬のセンサーの状態（W1-8）。
mod sensor_state;
/// URL を PC の既定のアプリで開く係（W1-6）。
mod url_opener;
/// 壁時計（テストで差し替える）。
mod wall_clock;
/// 模擬の画面の命令（W1-4a・W1-6）。
mod window_commands;
/// 模擬の画面の状態（W1-6）。
mod window_state;

use std::sync::atomic::{AtomicI64, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use serde_json::{json, Map, Value};

use super::event_queue::{PlatformEventQueue, DEFAULT_EVENT_QUEUE_CAPACITY};
use super::wire::{
    self, alarm as alarm_names, app as app_names, haptics as haptics_names, launch as launch_names,
    notification as notification_names, permission as permission_names, sensor as sensor_names, window as window_names,
    METHOD_EMIT_TEST_EVENT, METHOD_PING, METHOD_VERSION, MODULE_PLATFORM, TEST_EVENT_NAME,
};
use super::{PlatformBridge, PlatformBridgeKind};
use alarm_book::SimAlarmBook;
use haptics_state::SimHapticsLog;
use notification_state::SimNotificationBoard;
use ring_state::SimRingState;
use sensor_state::SimSensorBoard;
use window_state::SimWindowState;
pub use haptics_state::SimHapticsSnapshot;
pub use launch_uri::set_desktop_launch_uri;
pub use url_opener::{DryRunUrlOpener, SystemUrlOpener, UrlOpener, DESKTOP_OPENABLE_SCHEMES, NO_OPEN_ENV};
pub use wall_clock::{SystemWallClock, WallClock};
pub use window_state::SimWindowSnapshot;

/// 試験イベントの引数に message が無いときの文言。
const DEFAULT_TEST_MESSAGE: &str = "test";

/// 試験イベントの引数の名前（文言）。
const KEY_MESSAGE: &str = "message";

/// 最初に払い出す試験イベントの通し番号（Java の EventJournal と同じく 1 から）。
const FIRST_EVENT_SEQ: u64 = 1;

/// 命令 1 つの結果（成功なら返答の中身、失敗なら理由の名前と説明）。
type SimResult = Result<Map<String, Value>, SimFailure>;

/// 命令の失敗（返答の error と detail。Java の PlatformJson.errorReply と同じ形にする）。
#[derive(Debug, Clone, PartialEq, Eq)]
struct SimFailure {
    /// 理由の名前（wire::ERROR_* / wire::alarm::ERROR_*）。
    reason: &'static str,
    /// 説明（無ければ None）。
    detail: Option<String>,
}

impl SimFailure {
    /// 引数の値の誤り（invalid_argument。説明つき）。
    fn invalid_argument(detail: String) -> Self {
        Self { reason: alarm_names::ERROR_INVALID_ARGUMENT, detail: Some(detail) }
    }
}

impl From<&'static str> for SimFailure {
    fn from(reason: &'static str) -> Self {
        Self { reason, detail: None }
    }
}

/// 命令 1 つの処理。
type SimHandler = fn(&DesktopSimBridge, &Value) -> SimResult;

/// 模擬が受け付ける命令 1 つ（表の 1 行）。
struct SimCommand {
    /// モジュールの名前。
    module: &'static str,
    /// メソッドの名前。
    method: &'static str,
    /// 処理。
    handler: SimHandler,
}

/// 模擬が受け付ける命令の表（ここへ行を足せば命令が増える）。
const SIM_COMMANDS: &[SimCommand] = &[
    SimCommand { module: MODULE_PLATFORM, method: METHOD_PING, handler: DesktopSimBridge::handle_ping },
    SimCommand { module: MODULE_PLATFORM, method: METHOD_VERSION, handler: DesktopSimBridge::handle_version },
    SimCommand { module: MODULE_PLATFORM, method: METHOD_EMIT_TEST_EVENT, handler: DesktopSimBridge::handle_emit_test_event },
    SimCommand { module: alarm_names::MODULE, method: alarm_names::METHOD_SCHEDULE, handler: DesktopSimBridge::handle_alarm_schedule },
    SimCommand { module: alarm_names::MODULE, method: alarm_names::METHOD_CANCEL, handler: DesktopSimBridge::handle_alarm_cancel },
    SimCommand { module: alarm_names::MODULE, method: alarm_names::METHOD_CANCEL_ALL, handler: DesktopSimBridge::handle_alarm_cancel_all },
    SimCommand { module: alarm_names::MODULE, method: alarm_names::METHOD_LIST, handler: DesktopSimBridge::handle_alarm_list },
    SimCommand {
        module: alarm_names::MODULE,
        method: alarm_names::METHOD_CAN_SCHEDULE_EXACT,
        handler: DesktopSimBridge::handle_alarm_can_schedule_exact,
    },
    SimCommand { module: alarm_names::MODULE, method: alarm_names::METHOD_GET_RINGING, handler: DesktopSimBridge::handle_alarm_get_ringing },
    SimCommand { module: alarm_names::MODULE, method: alarm_names::METHOD_STOP_RINGING, handler: DesktopSimBridge::handle_alarm_stop_ringing },
    SimCommand {
        module: MODULE_PLATFORM,
        method: launch_names::METHOD_LAUNCH_REASON,
        handler: DesktopSimBridge::handle_platform_launch_reason,
    },
    SimCommand {
        module: window_names::MODULE,
        method: window_names::METHOD_SET_SHOW_WHEN_LOCKED,
        handler: DesktopSimBridge::handle_window_set_show_when_locked,
    },
    // W1-6: 画面の切り替え・アプリ・触感
    SimCommand {
        module: window_names::MODULE,
        method: window_names::METHOD_SET_KEEP_SCREEN_ON,
        handler: DesktopSimBridge::handle_window_set_keep_screen_on,
    },
    SimCommand {
        module: window_names::MODULE,
        method: window_names::METHOD_SET_SYSTEM_BARS_VISIBLE,
        handler: DesktopSimBridge::handle_window_set_system_bars_visible,
    },
    SimCommand { module: app_names::MODULE, method: app_names::METHOD_MOVE_TASK_TO_BACK, handler: DesktopSimBridge::handle_app_move_task_to_back },
    SimCommand { module: app_names::MODULE, method: app_names::METHOD_OPEN_URL, handler: DesktopSimBridge::handle_app_open_url },
    SimCommand { module: app_names::MODULE, method: app_names::METHOD_OPEN_APP_SETTINGS, handler: DesktopSimBridge::handle_app_open_app_settings },
    SimCommand { module: haptics_names::MODULE, method: haptics_names::METHOD_TAP, handler: DesktopSimBridge::handle_haptics_tap },
    SimCommand { module: haptics_names::MODULE, method: haptics_names::METHOD_VIBRATE, handler: DesktopSimBridge::handle_haptics_vibrate },
    // W1-5: 通知
    SimCommand {
        module: notification_names::MODULE,
        method: notification_names::METHOD_ENSURE_CHANNEL,
        handler: DesktopSimBridge::handle_notification_ensure_channel,
    },
    SimCommand { module: notification_names::MODULE, method: notification_names::METHOD_SHOW, handler: DesktopSimBridge::handle_notification_show },
    SimCommand {
        module: notification_names::MODULE,
        method: notification_names::METHOD_CANCEL,
        handler: DesktopSimBridge::handle_notification_cancel,
    },
    SimCommand {
        module: notification_names::MODULE,
        method: notification_names::METHOD_ARE_ENABLED,
        handler: DesktopSimBridge::handle_notification_are_enabled,
    },
    // W1-5: 権限
    SimCommand { module: permission_names::MODULE, method: permission_names::METHOD_CHECK, handler: DesktopSimBridge::handle_permission_check },
    SimCommand {
        module: permission_names::MODULE,
        method: permission_names::METHOD_REQUEST,
        handler: DesktopSimBridge::handle_permission_request,
    },
    SimCommand {
        module: permission_names::MODULE,
        method: permission_names::METHOD_OPEN_SETTINGS,
        handler: DesktopSimBridge::handle_permission_open_settings,
    },
    // W1-8: センサー（sim_inject は模擬だけ）
    SimCommand { module: sensor_names::MODULE, method: sensor_names::METHOD_START, handler: DesktopSimBridge::handle_sensor_start },
    SimCommand { module: sensor_names::MODULE, method: sensor_names::METHOD_STOP, handler: DesktopSimBridge::handle_sensor_stop },
    SimCommand { module: sensor_names::MODULE, method: sensor_names::METHOD_READ, handler: DesktopSimBridge::handle_sensor_read },
    SimCommand {
        module: sensor_names::MODULE,
        method: sensor_names::METHOD_SIM_INJECT,
        handler: DesktopSimBridge::handle_sensor_sim_inject,
    },
];

/// デスクトップの模擬の PlatformBridge。
#[derive(Debug)]
pub struct DesktopSimBridge {
    /// 模擬が積んだイベント（poll_events で取り出す）。
    events: PlatformEventQueue,
    /// 次に払い出す試験イベントの通し番号。
    next_seq: AtomicU64,
    /// 模擬を作った時刻（ping の uptime_ms の起点。Android の :seed_platform の「プロセスの起動から」に当たる）。
    started_at: Instant,
    /// 模擬の目覚ましの予約表（W1-3）。
    alarms: SimAlarmBook,
    /// 模擬の鳴動の状態（今鳴っている 1 つと待ち行列。W1-4a）。
    ringing: SimRingState,
    /// 模擬の通知（チャネルと出ている通知。W1-5）。
    notifications: SimNotificationBoard,
    /// 次に払い出す権限の要求の ID（W1-5。Play の区切りでも戻さない＝古い回のイベントと取り違えない）。
    next_permission_request_id: AtomicI64,
    /// 模擬の画面の状態（ロック画面の上・点けたまま・システムバー。W1-6）。
    window: SimWindowState,
    /// 模擬の触感の記録（W1-6）。
    haptics: SimHapticsLog,
    /// 模擬のセンサー（動いている種類と、sim_inject で入れた標本。W1-8）。
    sensors: SimSensorBoard,
    /// app.open_url の URL を PC の既定のアプリへ渡す係（W1-6。単体テストでは判定だけ・記録だけの係）。
    url_opener: Arc<dyn UrlOpener>,
    /// 単体起動の起動引数 --deep-link=<URI>（W1-6。無ければ None ＝ launcher）。Play の区切りでも消さない（プロセスの起動引数）。
    launch_uri: Option<String>,
    /// 予定時刻と比べる壁時計（テストでは手で進める時計）。
    clock: Arc<dyn WallClock>,
}

impl Default for DesktopSimBridge {
    fn default() -> Self {
        Self::new()
    }
}

impl DesktopSimBridge {
    /// 空の模擬を作る（本物の壁時計・URL を開く係は url_opener::default_url_opener・起動引数のディープリンク）。
    pub fn new() -> Self {
        let launch_uri = launch_uri::process_launch_uri();
        if let Some(uri) = &launch_uri {
            eprintln!("{} 起動引数のディープリンク: {uri}（platform.launch_reason は deep_link）", super::LOG_PREFIX);
        }
        Self::with_parts(Arc::new(SystemWallClock), url_opener::default_url_opener(), launch_uri)
    }

    /// 壁時計を指定して空の模擬を作る（単体テストで時刻を進めるため。URL を開く係は既定・起動引数のディープリンクは見ない）。
    pub fn with_clock(clock: Arc<dyn WallClock>) -> Self {
        Self::with_parts(clock, url_opener::default_url_opener(), None)
    }

    /// 部品をすべて指定して空の模擬を作る（W1-6。単体テストで URL を記録するだけの係・起動引数のディープリンクを差すため）。
    ///
    /// # 引数
    /// * `clock`      - 予定時刻と比べる壁時計
    /// * `url_opener` - app.open_url の URL を PC の既定のアプリへ渡す係
    /// * `launch_uri` - 起動引数のディープリンク（None なら launcher）
    pub fn with_parts(clock: Arc<dyn WallClock>, url_opener: Arc<dyn UrlOpener>, launch_uri: Option<String>) -> Self {
        Self {
            events: PlatformEventQueue::new(DEFAULT_EVENT_QUEUE_CAPACITY),
            next_seq: AtomicU64::new(FIRST_EVENT_SEQ),
            started_at: Instant::now(),
            alarms: SimAlarmBook::new(),
            ringing: SimRingState::new(),
            notifications: SimNotificationBoard::new(),
            next_permission_request_id: AtomicI64::new(permission_names::FIRST_REQUEST_ID),
            window: SimWindowState::new(),
            haptics: SimHapticsLog::new(),
            sensors: SimSensorBoard::new(),
            url_opener,
            launch_uri,
            clock,
        }
    }

    /// 模擬のイベントの通し番号を 1 つ払い出す（試験イベントと目覚ましで共通）。
    fn next_event_seq(&self) -> u64 {
        self.next_seq.fetch_add(1, Ordering::Relaxed)
    }

    /// 模擬のイベントを 1 つ積み、描く理由（PlatformEvent）を積む（模擬のイベントはすべてここを通す）。
    ///
    /// 描画を止めている間（render_policy の on_demand。W2-10a）でも、次のフレームでスクリプトへ届くようにする
    /// （Android の JNI の受け口 inbox.rs と同じ扱い。起きていれば理由を積むだけ）。
    fn queue_event(&self, event_json: String) {
        self.events.push(event_json);
        crate::engine::core::redraw::wake::raise(crate::engine::core::redraw::RedrawReason::PlatformEvent);
    }

    /// 時刻で起こる次の出来事（目覚ましの予定・鳴動の安全弁）の時刻（UTC の epoch ミリ秒）。無ければ None。
    fn next_timed_event_at_utc_ms(&self) -> Option<i64> {
        [self.alarms.next_trigger_utc_ms(), self.ringing.next_deadline_utc_ms()].into_iter().flatten().min()
    }

    /// platform.ping: 受け取った JSON をそのまま echo に入れ、pid と模擬を作ってからの ms を添える。
    fn handle_ping(&self, request: &Value) -> SimResult {
        let mut fields = Map::new();
        fields.insert("echo".into(), request.clone());
        fields.insert("pid".into(), Value::from(std::process::id()));
        fields.insert("uptime_ms".into(), Value::from(millis_u64(self.started_at.elapsed().as_millis())));
        fields.insert("simulated".into(), Value::Bool(true));
        Ok(fields)
    }

    /// platform.version: プロトコルの版と、模擬が知っているモジュールの一覧。
    fn handle_version(&self, _request: &Value) -> SimResult {
        let mut modules: Vec<&str> = SIM_COMMANDS.iter().map(|command| command.module).collect();
        modules.sort_unstable();
        modules.dedup();
        let mut fields = Map::new();
        fields.insert("protocol".into(), Value::from(wire::PROTOCOL_VERSION));
        fields.insert("modules".into(), Value::from(modules));
        fields.insert("simulated".into(), Value::Bool(true));
        Ok(fields)
    }

    /// platform.emit_test_event: 試験イベントを 1 つ積む（引数の message を data に入れる）。
    fn handle_emit_test_event(&self, request: &Value) -> SimResult {
        let message = request.get(KEY_MESSAGE).and_then(Value::as_str).unwrap_or(DEFAULT_TEST_MESSAGE);
        let seq = self.next_event_seq();
        let data = json!({ KEY_MESSAGE: message, "pid": std::process::id(), "simulated": true });
        self.queue_event(wire::event_json(TEST_EVENT_NAME, seq, now_utc_millis(), data));
        let mut fields = Map::new();
        fields.insert(wire::KEY_SEQ.into(), Value::from(seq));
        Ok(fields)
    }
}

impl PlatformBridge for DesktopSimBridge {
    fn kind(&self) -> PlatformBridgeKind {
        PlatformBridgeKind::Simulated
    }

    fn is_available(&self) -> bool {
        // プロセスの中だけで完結するので、いつでも呼べる
        true
    }

    fn invoke(&self, module: &str, method: &str, json: &str) -> Result<String, String> {
        // 返答はすべて Ok（届いた・届かなかったの Err は「基盤そのものが無い」ときだけ。失敗の理由は返答の JSON に入れる）
        let request = match wire::parse_request(json) {
            Ok(request) => request,
            Err(reason) => return Ok(wire::error_reply(reason)),
        };
        let Some(command) = SIM_COMMANDS.iter().find(|command| command.module == module && command.method == method) else {
            return Ok(wire::error_reply(wire::ERROR_UNKNOWN_METHOD));
        };
        Ok(match (command.handler)(self, &request) {
            Ok(fields) => wire::ok_reply(fields),
            Err(SimFailure { reason, detail: Some(detail) }) => {
                eprintln!("{} {module}.{method} を断りました: {reason}（{detail}）", super::LOG_PREFIX);
                wire::error_reply_with_detail(reason, &detail)
            }
            Err(SimFailure { reason, detail: None }) => wire::error_reply(reason),
        })
    }

    fn poll_events(&self) -> Vec<String> {
        // 時刻を過ぎた予約を先に積み（鳴動へ渡す）、鳴動の安全弁を見てから取り出す
        // （フレームの頭で呼ばれるので、次のスクリプトのフレームで届く）
        self.fire_due_alarms();
        self.check_ring_timeouts();
        self.events.drain()
    }

    fn next_timed_event_delay_ms(&self) -> Option<u64> {
        // 模擬の壁時計（単体テストでは手で進める時計）で残りを測る。過ぎていれば 0（すぐ起きて poll_events で積む）
        let due = self.next_timed_event_at_utc_ms()?;
        Some(u64::try_from(due.saturating_sub(self.clock.now_utc_ms())).unwrap_or(0))
    }

    fn reset_session(&self) {
        // Play の区切り: 予約・鳴動・通知（チャネルも）・画面の状態・触感の記録・センサー（W1-8）・積んだイベントを捨てる
        // （Play を止めれば模擬の予約も鳴動も通知も消え、画面は既定へ戻り、センサーは止まる。起動引数のディープリンクはプロセスのものなので残す）
        self.alarms.clear();
        self.ringing.clear();
        self.notifications.clear();
        self.window.clear();
        self.haptics.clear();
        self.sensors.clear();
        self.events.clear();
    }
}

/// 経過ミリ秒（u128）を JSON に入れられる u64 へ（あふれたら上限で止める）。
fn millis_u64(millis: u128) -> u64 {
    u64::try_from(millis).unwrap_or(u64::MAX)
}

/// 今の時刻（UTC の epoch ミリ秒）。時計が 1970 年より前なら 0。
fn now_utc_millis() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|elapsed| millis_u64(elapsed.as_millis()))
        .unwrap_or(0)
}

// ============================================================
//  ユニットテスト（ローカルの模擬だけを使う）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 返答の JSON を読む（テスト用）。
    fn parse(text: &str) -> Value {
        serde_json::from_str(text).expect("返答が JSON でない")
    }

    /// ping: 受け取った JSON がそのまま echo に入り、pid はこのプロセス、simulated=true。
    #[test]
    fn ping_echoes_request_and_reports_process() {
        let sim = DesktopSimBridge::new();
        let request = r#"{"nonce":"ping-7","nested":{"日本語":[1,2,3]}}"#;
        let reply = parse(&sim.invoke(MODULE_PLATFORM, METHOD_PING, request).unwrap());
        assert_eq!(reply[wire::KEY_OK], Value::Bool(true));
        assert_eq!(reply["echo"], parse(request));
        assert_eq!(reply["pid"], Value::from(std::process::id()));
        assert_eq!(reply["simulated"], Value::Bool(true));
        assert!(reply["uptime_ms"].is_u64());
    }

    /// ping: 引数が空なら echo は空のオブジェクト。
    #[test]
    fn ping_with_empty_request() {
        let sim = DesktopSimBridge::new();
        let reply = parse(&sim.invoke(MODULE_PLATFORM, METHOD_PING, "").unwrap());
        assert_eq!(reply["echo"], json!({}));
    }

    /// version: プロトコルの版と、模擬が知っているモジュール（名前の順。W1-3 で alarm、W1-4a で window、
    /// W1-5 で notification・permission、W1-6 で app・haptics、W1-8 で sensor が加わった）。
    #[test]
    fn version_reports_protocol() {
        let sim = DesktopSimBridge::new();
        let reply = parse(&sim.invoke(MODULE_PLATFORM, METHOD_VERSION, "{}").unwrap());
        assert_eq!(reply[wire::KEY_OK], Value::Bool(true));
        assert_eq!(reply["protocol"], Value::from(wire::PROTOCOL_VERSION));
        assert_eq!(
            reply["modules"],
            json!([
                alarm_names::MODULE,
                app_names::MODULE,
                haptics_names::MODULE,
                notification_names::MODULE,
                permission_names::MODULE,
                MODULE_PLATFORM,
                sensor_names::MODULE,
                window_names::MODULE
            ])
        );
    }

    /// emit_test_event: 積んだイベントが poll_events で順に出て、通し番号が 1 から増える。取り出した後は空。
    #[test]
    fn emit_test_event_is_polled_in_order() {
        let sim = DesktopSimBridge::new();
        assert!(sim.poll_events().is_empty());
        let first = parse(&sim.invoke(MODULE_PLATFORM, METHOD_EMIT_TEST_EVENT, r#"{"message":"一つ目"}"#).unwrap());
        let second = parse(&sim.invoke(MODULE_PLATFORM, METHOD_EMIT_TEST_EVENT, "").unwrap());
        assert_eq!(first[wire::KEY_SEQ], Value::from(FIRST_EVENT_SEQ));
        assert_eq!(second[wire::KEY_SEQ], Value::from(FIRST_EVENT_SEQ + 1));

        let events = sim.poll_events();
        assert_eq!(events.len(), 2);
        for text in &events {
            assert_eq!(wire::validate_event_json(text).unwrap(), TEST_EVENT_NAME);
        }
        let first_event = parse(&events[0]);
        assert_eq!(first_event[wire::KEY_DATA][KEY_MESSAGE], Value::from("一つ目"));
        assert_eq!(first_event[wire::KEY_SEQ], Value::from(FIRST_EVENT_SEQ));
        assert_eq!(parse(&events[1])[wire::KEY_DATA][KEY_MESSAGE], Value::from(DEFAULT_TEST_MESSAGE));
        assert!(sim.poll_events().is_empty(), "取り出した後に残っている");
    }

    /// 知らない命令・壊れた JSON は Ok の返答で ok=false と理由（届いたが失敗した、の形）。
    #[test]
    fn unknown_method_and_invalid_json_are_error_replies() {
        let sim = DesktopSimBridge::new();
        let unknown = parse(&sim.invoke(MODULE_PLATFORM, "no_such_method", "{}").unwrap());
        assert_eq!(unknown[wire::KEY_OK], Value::Bool(false));
        assert_eq!(unknown[wire::KEY_ERROR], Value::from(wire::ERROR_UNKNOWN_METHOD));
        let other_module = parse(&sim.invoke("alarm", METHOD_PING, "{}").unwrap());
        assert_eq!(other_module[wire::KEY_ERROR], Value::from(wire::ERROR_UNKNOWN_METHOD));
        let broken = parse(&sim.invoke(MODULE_PLATFORM, METHOD_PING, "{broken").unwrap());
        assert_eq!(broken[wire::KEY_ERROR], Value::from(wire::ERROR_INVALID_JSON));
    }

    /// 模擬の種類と可用性（スクリプトの IsSimulated / IsSupported の源）。
    #[test]
    fn kind_is_simulated_and_always_available() {
        let sim = DesktopSimBridge::new();
        assert_eq!(sim.kind(), PlatformBridgeKind::Simulated);
        assert!(sim.is_available());
    }

    /// 表の命令は module と method の組で重複しない（重複すると後の行が永遠に呼ばれない）。
    #[test]
    fn command_table_has_no_duplicates() {
        let mut keys: Vec<(&str, &str)> = SIM_COMMANDS.iter().map(|command| (command.module, command.method)).collect();
        let total = keys.len();
        keys.sort_unstable();
        keys.dedup();
        assert_eq!(keys.len(), total);
        for command in SIM_COMMANDS {
            assert!(wire::is_valid_name(command.module) && wire::is_valid_name(command.method));
        }
    }
}
