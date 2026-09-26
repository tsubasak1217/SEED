// ============================================================
//  platform/bridge/desktop_sim.rs — デスクトップの模擬（DesktopSimBridge。W1-P7）
//
//  【役割】
//  PC の Play（エディタ埋め込み・単体起動）で SEED.Platform のスクリプトを動かすための、プロセスの中だけで完結する
//  PlatformBridge。Android の :seed_platform（PlatformProvider）と同じ命令に同じ形の JSON で答える。
//  画面づくりの大半はデスクトップで行う（docs/app_platform_roadmap.md X-5）ので、端末が無くても API の流れを試せるようにする。
//
//  【命令の足し方（データ駆動）】
//  命令は下の表 SIM_COMMANDS の 1 行（module・method・処理の関数）。W1-3 以降の模擬（目覚ましの予約をタイマーで鳴らす等）は、
//  表に行を足し、時間で起きるものは poll_events（エンジンがフレームの頭で呼ぶ）の中で「時刻が来たらイベントを積む」形にする。
//
//  【W1-1 の命令】（Java の CorePlatformModule と同じ意味）
//    platform.ping            … 受け取った JSON を echo に入れて返す＋pid（SEED.exe のもの）・模擬を作ってからの ms・simulated=true
//    platform.version         … プロトコルの版と使えるモジュール
//    platform.emit_test_event … 試験イベント platform.test_event を 1 つ積む（次のフレームでスクリプトへ届く）
// ============================================================

use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use serde_json::{json, Map, Value};

use super::event_queue::{PlatformEventQueue, DEFAULT_EVENT_QUEUE_CAPACITY};
use super::wire::{self, METHOD_EMIT_TEST_EVENT, METHOD_PING, METHOD_VERSION, MODULE_PLATFORM, TEST_EVENT_NAME};
use super::{PlatformBridge, PlatformBridgeKind};

/// 試験イベントの引数に message が無いときの文言。
const DEFAULT_TEST_MESSAGE: &str = "test";

/// 試験イベントの引数の名前（文言）。
const KEY_MESSAGE: &str = "message";

/// 最初に払い出す試験イベントの通し番号（Java の EventJournal と同じく 1 から）。
const FIRST_EVENT_SEQ: u64 = 1;

/// 命令 1 つの結果（成功なら返答の中身、失敗なら理由の名前）。
type SimResult = Result<Map<String, Value>, &'static str>;

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
}

impl Default for DesktopSimBridge {
    fn default() -> Self {
        Self::new()
    }
}

impl DesktopSimBridge {
    /// 空の模擬を作る。
    pub fn new() -> Self {
        Self {
            events: PlatformEventQueue::new(DEFAULT_EVENT_QUEUE_CAPACITY),
            next_seq: AtomicU64::new(FIRST_EVENT_SEQ),
            started_at: Instant::now(),
        }
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
        let seq = self.next_seq.fetch_add(1, Ordering::Relaxed);
        let data = json!({ KEY_MESSAGE: message, "pid": std::process::id(), "simulated": true });
        self.events.push(wire::event_json(TEST_EVENT_NAME, seq, now_utc_millis(), data));
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
            Err(reason) => wire::error_reply(reason),
        })
    }

    fn poll_events(&self) -> Vec<String> {
        self.events.drain()
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

    /// version: プロトコルの版と platform モジュール。
    #[test]
    fn version_reports_protocol() {
        let sim = DesktopSimBridge::new();
        let reply = parse(&sim.invoke(MODULE_PLATFORM, METHOD_VERSION, "{}").unwrap());
        assert_eq!(reply[wire::KEY_OK], Value::Bool(true));
        assert_eq!(reply["protocol"], Value::from(wire::PROTOCOL_VERSION));
        assert_eq!(reply["modules"], json!([MODULE_PLATFORM]));
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
