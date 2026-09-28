// ============================================================
//  platform/bridge/wire.rs — プラットフォーム機能（SEED.Platform）の JSON の約束（W1-1・W1-3 で目覚まし wire::alarm・
//  W1-4a で鳴動〈wire::alarm の get_ringing / stop_ringing 等〉・起動理由 wire::launch・画面 wire::window を追加・
//  W1-5 で通知 wire::notification・権限 wire::permission を追加・W1-6 で画面の切り替え・ディープリンク〈wire::launch の uri〉・
//  アプリ wire::app・触感 wire::haptics を追加・W1-8 でセンサー wire::sensor を追加）
//
//  【役割】
//  エンジン・Java（メインプロセスの SeedPlatform と :seed_platform の PlatformProvider）・C#（SEED.Platform）の
//  3 者が同じ形で読み書きする JSON の「名前・形・エラーの理由」をこのファイル 1 か所に集める（Rust 側の正典）。
//  Java 側の対になる定数は runtime/android/app/src/main/java/com/seedengine/runtime/platform/PlatformContract.java、
//  C# 側は scripting/src/Api/Platform/。値を変えるときは 3 か所を必ず揃える（Java とはこのファイルの単体テスト
//  java_contract_matches_wire が突き合わせる。C# の目覚ましの名前は scripting/src/Api/Platform/Alarms/AlarmJson.cs）。
//
//  【形】
//    命令     : invoke(module, method, json)。module・method は小文字英数字と _ の名前（is_valid_name）
//    返答     : {"ok": true, ...}  /  {"ok": false, "error": "<理由の名前>"}
//    イベント : {"name": "platform.<名前>", "seq": <番号>, "time_ms": <UTC の epoch ミリ秒>, "data": {...}}
//               seq は :seed_platform の記録（EventJournal）の通し番号（1 から）。メインプロセスの中で作る
//               接続の知らせ（platform.connected など）とデスクトップの模擬の試験イベントは LOCAL_EVENT_SEQ（0）か
//               模擬の中の通し番号
//
//  全体像は docs/android.md §25・docs/app_platform_roadmap.md §2.2。
// ============================================================

use serde_json::{json, Map, Value};

/// プロトコルの版（platform.version の返答の protocol）。Java の PlatformContract.PROTOCOL_VERSION と一致させる。
///
/// JSON の形・メソッドの意味を互換のない形で変えたら 1 つ上げる（APK の Java と C# の組み合わせの食い違いを見分けるため）。
pub const PROTOCOL_VERSION: u32 = 1;

// ── キーの名前（Java の PlatformContract.KEY_* と一致させる）──

/// 返答: 成功したか（bool）。
pub const KEY_OK: &str = "ok";
/// 返答: 失敗の理由の名前（文字列。ERROR_* のどれか、または Java 側の理由）。
pub const KEY_ERROR: &str = "error";
/// 返答: 失敗の詳しい説明（ログ用。スクリプトの LastError には入らない）。
pub const KEY_DETAIL: &str = "detail";
/// イベント: 名前（`platform.` で始まる）。
pub const KEY_NAME: &str = "name";
/// イベント: 通し番号。
pub const KEY_SEQ: &str = "seq";
/// イベント: 起きた時刻（UTC の epoch ミリ秒）。
pub const KEY_TIME_MS: &str = "time_ms";
/// イベント: 中身（オブジェクト）。
pub const KEY_DATA: &str = "data";

/// イベントの名前の接頭辞（スクリプトの SEED.Events にもこの名前で流れる）。
pub const EVENT_NAME_PREFIX: &str = "platform.";

/// メインプロセスの中で作ったイベント（接続の知らせ）の seq（記録を通らないので番号を持たない）。
pub const LOCAL_EVENT_SEQ: u64 = 0;

// ── モジュールとメソッドの名前（W1-1 で実装するもの）──

/// 基盤そのもののモジュール。
pub const MODULE_PLATFORM: &str = "platform";
/// 往復の計測（受け取った JSON をそのまま echo に入れて返す＋pid・起動からの ms）。
pub const METHOD_PING: &str = "ping";
/// プロトコルの版と使えるモジュール。
pub const METHOD_VERSION: &str = "version";
/// 試験イベントを 1 つ流す（デバッグ用）。
pub const METHOD_EMIT_TEST_EVENT: &str = "emit_test_event";
/// 試験イベントの名前。
pub const TEST_EVENT_NAME: &str = "platform.test_event";
/// 端末保護ストレージの置き場の絶対パス（W1-3。Android の糊が目覚ましの音源の書き出し先を知るために 1 回呼ぶ）。
pub const METHOD_PATHS: &str = "paths";
/// paths の返答: 端末保護ストレージの files の絶対パス。
pub const KEY_DEVICE_PROTECTED_FILES_DIR: &str = "device_protected_files_dir";
/// paths の返答: 目覚ましの音源の書き出し先の絶対パス（…/files/seed_platform/sounds）。
pub const KEY_SOUNDS_DIR: &str = "sounds_dir";
/// :seed_platform へつないでいる途中（Android の最初の呼び出し。Java の ERROR_CONNECTING）。
pub const ERROR_CONNECTING: &str = "connecting";

// ── エラーの理由（Java の PlatformContract.ERROR_* と一致させる。C# の Platform.LastError に入る）──

/// 基盤が無い（Android で Java 側がまだ登録していない・デスクトップ以外で模擬も無い）。
pub const ERROR_UNAVAILABLE: &str = "platform_unavailable";
/// module / method の名前が約束（is_valid_name）に合わない。
pub const ERROR_INVALID_NAME: &str = "invalid_name";
/// 引数の JSON が読めない。
pub const ERROR_INVALID_JSON: &str = "invalid_json";
/// 知らないメソッド（模擬・PlatformProvider とも同じ理由）。
pub const ERROR_UNKNOWN_METHOD: &str = "unknown_method";
/// エンジンの中で panic した（呼び出しは続けられる）。
pub const ERROR_INTERNAL_PANIC: &str = "internal_panic";

/// module / method の名前の最大の長さ（バイト）。
pub const MAX_NAME_LEN: usize = 64;

/// URL（app.open_url の url・ディープリンクの起動理由の uri）の最大の長さ（Unicode の符号位置の数。W1-6）。
/// Java の PlatformContract.MAX_URL_LENGTH と一致させる。
pub const MAX_URL_LENGTH: usize = 8192;

/// module / method の名前が約束どおりか（1〜MAX_NAME_LEN 文字の小文字英数字と `_`）。
///
/// Java 側は `<module>.<method>` を ContentProvider の call の method に使うので、`.` や空白を含む名前は通さない。
pub fn is_valid_name(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= MAX_NAME_LEN
        && name.bytes().all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'_')
}

/// 失敗の返答 `{"ok": false, "error": <理由>}` を作る。
pub fn error_reply(reason: &str) -> String {
    json!({ KEY_OK: false, KEY_ERROR: reason }).to_string()
}

/// 失敗の返答 `{"ok": false, "error": <理由>, "detail": <説明>}` を作る（Java の PlatformJson.errorReply(reason, detail) と同じ形）。
pub fn error_reply_with_detail(reason: &str, detail: &str) -> String {
    json!({ KEY_OK: false, KEY_ERROR: reason, KEY_DETAIL: detail }).to_string()
}

/// 成功の返答を作る（`fields` の先頭に `"ok": true` を足す）。
///
/// # 引数
/// * `fields` - 返答の中身（オブジェクト）。`ok` のキーがあれば true で上書きする
pub fn ok_reply(fields: Map<String, Value>) -> String {
    let mut reply = Map::new();
    reply.insert(KEY_OK.to_string(), Value::Bool(true));
    for (key, value) in fields {
        if key != KEY_OK {
            reply.insert(key, value);
        }
    }
    Value::Object(reply).to_string()
}

/// イベントの JSON を作る（`{"name", "seq", "time_ms", "data"}`）。
///
/// # 引数
/// * `name`    - イベントの名前（`platform.` で始めること）
/// * `seq`     - 通し番号
/// * `time_ms` - 起きた時刻（UTC の epoch ミリ秒）
/// * `data`    - 中身（オブジェクト）
pub fn event_json(name: &str, seq: u64, time_ms: u64, data: Value) -> String {
    json!({ KEY_NAME: name, KEY_SEQ: seq, KEY_TIME_MS: time_ms, KEY_DATA: data }).to_string()
}

/// 外（Java）から届いたイベントの JSON が約束の形か確かめる（オブジェクトで、`name` が `platform.` で始まる文字列）。
///
/// # 戻り値
/// 形が正しければ Ok(名前)。正しくなければ Err(理由の説明。ログ用)
pub fn validate_event_json(text: &str) -> Result<String, String> {
    let value: Value = serde_json::from_str(text).map_err(|err| format!("JSON として読めません: {err}"))?;
    let Some(name) = value.get(KEY_NAME).and_then(Value::as_str) else {
        return Err(format!("\"{KEY_NAME}\"（文字列）がありません"));
    };
    if !name.starts_with(EVENT_NAME_PREFIX) {
        return Err(format!("名前が \"{EVENT_NAME_PREFIX}\" で始まっていません: {name}"));
    }
    Ok(name.to_string())
}

/// 引数の JSON を読む（空なら空のオブジェクト）。
///
/// # 戻り値
/// 読めた値。読めなければ Err(ERROR_INVALID_JSON)
pub fn parse_request(json_text: &str) -> Result<Value, &'static str> {
    if json_text.trim().is_empty() {
        return Ok(Value::Object(Map::new()));
    }
    serde_json::from_str(json_text).map_err(|_| ERROR_INVALID_JSON)
}

/// 目覚まし（モジュール "alarm"。W1-3）の名前・欄・理由・上限（Java の PlatformContract の *ALARM*・C# の AlarmJson と一致させる）。
///
/// 予約の形: `{ id, trigger_at_utc_ms, sound_asset | sound_path, vibrate, force_volume, keep_volume, fade_in_seconds,
/// max_ring_minutes, title, body, payload_json }`。イベントの `scheduled_at_utc_ms` は「鳴るはずだった時刻」
/// （予約の trigger_at_utc_ms）で、控えの `created_at_utc_ms`（予約を受け付けた時刻）とは別物。
pub mod alarm {
    /// 目覚ましのモジュール。
    pub const MODULE: &str = "alarm";
    /// 予約する（同じ ID は置き換え）。
    pub const METHOD_SCHEDULE: &str = "schedule";
    /// 1 つ取り消す（無い ID でも成功）。
    pub const METHOD_CANCEL: &str = "cancel";
    /// 全部取り消す。
    pub const METHOD_CANCEL_ALL: &str = "cancel_all";
    /// 控えの一覧（予定時刻の順）。
    pub const METHOD_LIST: &str = "list";
    /// 正確なアラームを張れるか。
    pub const METHOD_CAN_SCHEDULE_EXACT: &str = "can_schedule_exact";
    /// 鳴動中の予約を返す（W1-4a。無ければ ringing が null）。
    pub const METHOD_GET_RINGING: &str = "get_ringing";
    /// 鳴動を止める（W1-4a。引数の id が空・無しなら今鳴っているもの。待ち行列の予約も id で外せる）。
    pub const METHOD_STOP_RINGING: &str = "stop_ringing";

    /// 予約の ID。
    pub const KEY_ID: &str = "id";
    /// 鳴らす時刻（UTC の epoch ミリ秒）。
    pub const KEY_TRIGGER_AT_UTC_MS: &str = "trigger_at_utc_ms";
    /// スクリプトが渡した音源（"assets://…" か端末のファイルの絶対パス）。Android の糊が sound_path に置き換えて送る。
    pub const KEY_SOUND_ASSET: &str = "sound_asset";
    /// 音源の端末のファイルの絶対パス（空なら既定の音）。
    pub const KEY_SOUND_PATH: &str = "sound_path";
    /// バイブするか。
    pub const KEY_VIBRATE: &str = "vibrate";
    /// 鳴っている間のアラームの音量（0..1。負 = 触らない）。
    pub const KEY_FORCE_VOLUME: &str = "force_volume";
    /// 利用者が音量を下げても戻すか。
    pub const KEY_KEEP_VOLUME: &str = "keep_volume";
    /// 音量の漸増の秒。
    pub const KEY_FADE_IN_SECONDS: &str = "fade_in_seconds";
    /// 鳴り続ける上限の分（安全弁）。
    pub const KEY_MAX_RING_MINUTES: &str = "max_ring_minutes";
    /// 鳴動の通知の題。
    pub const KEY_TITLE: &str = "title";
    /// 鳴動の通知の本文。
    pub const KEY_BODY: &str = "body";
    /// アプリの任意の JSON（文字列のまま返す）。
    pub const KEY_PAYLOAD_JSON: &str = "payload_json";
    /// 控え: 予約を受け付けた時刻。
    pub const KEY_CREATED_AT_UTC_MS: &str = "created_at_utc_ms";
    /// list の返答: 控えの配列。
    pub const KEY_ALARMS: &str = "alarms";
    /// schedule の返答: 置き換えたか。
    pub const KEY_REPLACED: &str = "replaced";
    /// cancel の返答: その ID の予約があったか。
    pub const KEY_EXISTED: &str = "existed";
    /// cancel_all の返答・alarms.rescheduled: 件数。
    pub const KEY_COUNT: &str = "count";
    /// can_schedule_exact の返答。
    pub const KEY_CAN_SCHEDULE_EXACT: &str = "can_schedule_exact";
    /// イベント: 鳴るはずだった時刻（予約の trigger_at_utc_ms）。
    pub const KEY_SCHEDULED_AT_UTC_MS: &str = "scheduled_at_utc_ms";
    /// イベント alarm.fired: 発火を受けた時刻。
    pub const KEY_FIRED_AT_UTC_MS: &str = "fired_at_utc_ms";
    /// イベント: 理由。
    pub const KEY_REASON: &str = "reason";
    /// イベント alarms.rescheduled: 鳴らなかったと記録した件数。
    pub const KEY_MISSED: &str = "missed";
    /// イベント alarms.rescheduled: 張り直せなかった件数。
    pub const KEY_FAILED: &str = "failed";
    /// 模擬の返答・イベント: 模擬が作ったか。
    pub const KEY_SIMULATED: &str = "simulated";
    /// get_ringing の返答: 鳴動中の予約（{id, scheduled_at_utc_ms, started_at_utc_ms, payload_json}。無ければ null）。
    pub const KEY_RINGING: &str = "ringing";
    /// get_ringing の返答: 鳴り始めた時刻（待ち行列から繰り上がったときはその時刻）。
    pub const KEY_STARTED_AT_UTC_MS: &str = "started_at_utc_ms";
    /// イベント alarm.queued: 今鳴っていて、止まるのを待っている予約の ID。
    pub const KEY_WAITING_FOR: &str = "waiting_for";
    /// stop_ringing の返答: 何かを止めたか（鳴っていなければ false。それでも ok=true）。
    pub const KEY_STOPPED: &str = "stopped";

    /// 目覚ましが鳴った（配信され、鳴動へ渡した。鳴り始めたか待ち行列に入った）。
    pub const EVENT_FIRED: &str = "platform.alarm.fired";
    /// 目覚ましが鳴らなかった（電源断・強制停止・許可の取り消しの間に予定時刻を過ぎた・鳴動を始められなかった）。
    pub const EVENT_MISSED: &str = "platform.alarm.missed";
    /// 予約を張り直した。
    pub const EVENT_RESCHEDULED: &str = "platform.alarms.rescheduled";
    /// 鳴動が終わった（W1-4a。reason = stopped / timeout / error）。
    pub const EVENT_RING_STOPPED: &str = "platform.alarm.ring_stopped";
    /// 別の予約の鳴動中に時刻が来たので待たせた（W1-4a。今の鳴動が止まったら続けて鳴らす）。
    pub const EVENT_QUEUED: &str = "platform.alarm.queued";

    /// alarm.missed の理由: 電源断・強制停止・更新などで予約が OS から消えていた。
    pub const MISSED_REASON_DEVICE_OFF: &str = "device_off";
    /// alarm.missed の理由: 正確なアラームの許可が取り消されていた。
    pub const MISSED_REASON_PERMISSION_REVOKED: &str = "permission_revoked";
    /// alarm.missed の理由: 配信は届いたが鳴動の前景サービスを起こせなかった（W1-4a。alarm.fired の代わりに記録する）。
    pub const MISSED_REASON_START_FAILED: &str = "start_failed";
    /// alarm.ring_stopped の理由: アプリが止めた（stop_ringing）。
    pub const RING_STOP_REASON_STOPPED: &str = "stopped";
    /// alarm.ring_stopped の理由: 安全弁（max_ring_minutes）で止めた。
    pub const RING_STOP_REASON_TIMEOUT: &str = "timeout";
    /// alarm.ring_stopped の理由: 鳴らし続けられなかった。
    pub const RING_STOP_REASON_ERROR: &str = "error";
    /// alarms.rescheduled の理由: 再起動（強制停止からの復帰を含む）。
    pub const RESCHEDULE_REASON_BOOT: &str = "boot";
    /// alarms.rescheduled の理由: 端末の時刻・タイムゾーンの変更。
    pub const RESCHEDULE_REASON_TIME_CHANGED: &str = "time_changed";
    /// alarms.rescheduled の理由: アプリの更新。
    pub const RESCHEDULE_REASON_PACKAGE_REPLACED: &str = "package_replaced";
    /// alarms.rescheduled の理由: 正確なアラームの許可。
    pub const RESCHEDULE_REASON_PERMISSION_CHANGED: &str = "permission_changed";

    /// 正確なアラームを張れない（黙って不正確な予約に落とさない）。
    pub const ERROR_EXACT_ALARM_NOT_ALLOWED: &str = "exact_alarm_not_allowed";
    /// 引数の値が約束に合わない。
    pub const ERROR_INVALID_ARGUMENT: &str = "invalid_argument";
    /// 予約の数が上限に達している。
    pub const ERROR_TOO_MANY_ALARMS: &str = "too_many_alarms";
    /// 予約の控えを書けなかった。
    pub const ERROR_STORE_WRITE_FAILED: &str = "store_write_failed";
    /// AlarmManager が予約を受け付けなかった。
    pub const ERROR_SCHEDULE_FAILED: &str = "schedule_failed";
    /// APK に機能 alarm が入っていない。
    pub const ERROR_FEATURE_NOT_ENABLED: &str = "feature_not_enabled";

    /// 予約の ID の最大の長さ（Unicode の符号位置の数）。
    pub const MAX_ID_LENGTH: usize = 128;
    /// title・body の最大の長さ（Unicode の符号位置の数）。
    pub const MAX_TEXT_LENGTH: usize = 4096;
    /// payload_json の最大の長さ（Unicode の符号位置の数）。
    pub const MAX_PAYLOAD_LENGTH: usize = 16384;
    /// 控えに持てる予約の数の上限。
    pub const MAX_SCHEDULED_ALARMS: usize = 64;
    /// vibrate の既定値。
    pub const DEFAULT_VIBRATE: bool = true;
    /// force_volume の「触らない」の値。
    pub const VOLUME_UNCHANGED: f64 = -1.0;
    /// force_volume の上限。
    pub const MAX_FORCE_VOLUME: f64 = 1.0;
    /// keep_volume の既定値。
    pub const DEFAULT_KEEP_VOLUME: bool = false;
    /// fade_in_seconds の既定値。
    pub const DEFAULT_FADE_IN_SECONDS: f64 = 5.0;
    /// max_ring_minutes の既定値。
    pub const DEFAULT_MAX_RING_MINUTES: i64 = 60;
    /// max_ring_minutes の下限。
    pub const MIN_MAX_RING_MINUTES: i64 = 1;
    /// 1 分のミリ秒（安全弁の max_ring_minutes をミリ秒にする）。
    pub const MILLIS_PER_MINUTE: i64 = 60_000;
}

/// 起動理由（W1-4a。モジュール "platform" の launch_reason とイベント platform.launch。Android はメインプロセスが答える）の名前
/// （Java の PlatformContract の *LAUNCH*・C# の LaunchJson と一致させる）。
///
/// 起動理由の形: `{ kind, id, action_id, scheduled_at_utc_ms, fired_at_utc_ms, payload_json }`。id・scheduled_at_utc_ms・
/// fired_at_utc_ms・payload_json は目覚ましと同じ名前の欄（wire::alarm の KEY_*）。
pub mod launch {
    /// この起動の理由を返す（モジュールは wire::MODULE_PLATFORM）。返答 `{ launch: {…} }`。
    pub const METHOD_LAUNCH_REASON: &str = "launch_reason";
    /// 起動した後に届いた Intent（singleTask の onNewIntent）の理由。data は起動理由と同じ形。
    pub const EVENT_LAUNCH: &str = "platform.launch";
    /// launch_reason の返答: 起動理由のオブジェクト。
    pub const KEY_LAUNCH: &str = "launch";
    /// 起動理由: 種類（KIND_*）。
    pub const KEY_KIND: &str = "kind";
    /// 起動理由: 通知の操作の ID。
    pub const KEY_ACTION_ID: &str = "action_id";
    /// 種類: ランチャー・普通の起動。
    pub const KIND_LAUNCHER: &str = "launcher";
    /// 種類: 目覚ましの鳴動（フルスクリーン通知・鳴動の通知の本文のタップ）。
    pub const KIND_ALARM: &str = "alarm";
    /// 種類: 通知の本文のタップ（W1-5）。
    pub const KIND_NOTIFICATION_TAP: &str = "notification_tap";
    /// 種類: 通知の操作（ボタン）。
    pub const KIND_NOTIFICATION_ACTION: &str = "notification_action";
    /// 種類: ステータスバー・ロック画面の「次の目覚まし」の表示を押した。
    pub const KIND_ALARM_CLOCK_INFO: &str = "alarm_clock_info";
    /// 種類: それ以外。
    pub const KIND_OTHER: &str = "other";
    /// 種類: ディープリンク（W1-6。PlatformEntry を通らない VIEW＋data の Intent。どのアプリからでも送れるので中身はアプリが検査する）。
    pub const KIND_DEEP_LINK: &str = "deep_link";
    /// 起動理由: ディープリンクの URI（deep_link のとき。ほかは空。W1-6）。
    pub const KEY_URI: &str = "uri";
    /// 通知の操作の ID: 鳴動の通知の「開く」。
    pub const ACTION_OPEN: &str = "open";
}

/// 画面（W1-4a。モジュール "window"。Android はメインプロセスが Activity を操作して答える）の名前
/// （Java の PlatformContract の *WINDOW*・C# の Window と一致させる）。
pub mod window {
    /// 画面のモジュール。
    pub const MODULE: &str = "window";
    /// ロック画面の上に出す＋画面を点ける の切り替え。引数 `{ on }`。
    pub const METHOD_SET_SHOW_WHEN_LOCKED: &str = "set_show_when_locked";
    /// 画面を点けたままにする（Android の FLAG_KEEP_SCREEN_ON）の切り替え。引数 `{ on }`（W1-6）。
    pub const METHOD_SET_KEEP_SCREEN_ON: &str = "set_keep_screen_on";
    /// システムバー（ステータスバー・ナビゲーションバー）を出す・隠すの切り替え。引数 `{ on }`（W1-6。起動時の既定は android.system_bars）。
    pub const METHOD_SET_SYSTEM_BARS_VISIBLE: &str = "set_system_bars_visible";
    /// 画面の切り替えの命令の引数・返答: 入れるか。
    pub const KEY_ON: &str = "on";
    /// 操作する Activity が無い（Android）。
    pub const ERROR_NO_ACTIVITY: &str = "no_activity";
}

/// アプリ（W1-6。モジュール "app"。Android はメインプロセスが答える）の名前・欄・理由
/// （Java の PlatformContract の *APP*・URL_SCHEME_*・C# の AppJson と一致させる）。
///
/// 命令: `move_task_to_back {}` → `{}`・`open_url { url }` → `{ scheme }`・`open_app_settings {}` → `{}`・
/// `ui_mode {}` → `{ night }`（W2-9。端末の明暗の設定）・模擬だけ `sim_set_ui_mode { night }` → `{ night }`（Android には無く unknown_method）。
/// イベント `platform.ui_mode_changed { night }`（W2-9。Android は MainActivity.onConfigurationChanged・PC はウィンドウの ThemeChanged と模擬の命令）。
/// open_url の URL の規則は bridge::app（Java の app/UrlPolicy と同じ）。失敗の理由のうち `invalid_argument` は目覚ましと同じ文字列。
pub mod app {
    /// アプリのモジュール。
    pub const MODULE: &str = "app";
    /// 閉じずに背面へ（戻るの最上位で使う）。
    pub const METHOD_MOVE_TASK_TO_BACK: &str = "move_task_to_back";
    /// URL を開く（Android は ACTION_VIEW）。
    pub const METHOD_OPEN_URL: &str = "open_url";
    /// 端末の「アプリ情報」の画面を開く。
    pub const METHOD_OPEN_APP_SETTINGS: &str = "open_app_settings";
    /// open_url の引数: 開く URL。
    pub const KEY_URL: &str = "url";
    /// open_url の返答: 小文字にそろえた scheme。
    pub const KEY_SCHEME: &str = "scheme";
    /// open_url の返答（デスクトップの模擬だけ）: PC の既定のアプリへ実際に渡したか。
    pub const KEY_OPENED: &str = "opened";
    /// 断る scheme: 端末のファイル。
    pub const SCHEME_FILE: &str = "file";
    /// 断る scheme: ContentProvider（アプリの中身を他のアプリへ渡しうる）。
    pub const SCHEME_CONTENT: &str = "content";
    /// 断る scheme: スクリプトの実行。
    pub const SCHEME_JAVASCRIPT: &str = "javascript";
    /// open_url で断る scheme の一覧（小文字）。
    pub const DENIED_SCHEMES: [&str; 3] = [SCHEME_FILE, SCHEME_CONTENT, SCHEME_JAVASCRIPT];
    /// open_url: その URL を開けるアプリが無い（Android の ActivityNotFoundException）。
    pub const ERROR_NO_HANDLER: &str = "no_handler";
    /// open_url: 断る scheme（DENIED_SCHEMES）。
    pub const ERROR_SCHEME_NOT_ALLOWED: &str = "scheme_not_allowed";
    /// 端末の明暗の設定（W2-9。Android は Configuration.uiMode の夜の bit・デスクトップの模擬は OS の「アプリのモード」）。
    pub const METHOD_UI_MODE: &str = "ui_mode";
    /// 模擬だけ（W2-9）: 端末の明暗を差し替える。引数 `{ night }`（yes / no / unknown、system で差し替えをやめる）。
    pub const METHOD_SIM_SET_UI_MODE: &str = "sim_set_ui_mode";
    /// ui_mode の返答・ui_mode_changed の data・sim_set_ui_mode の引数: 夜の表示か。
    pub const KEY_NIGHT: &str = "night";
    /// night: 夜の表示（暗い。Android の UI_MODE_NIGHT_YES）。
    pub const NIGHT_YES: &str = "yes";
    /// night: 夜の表示でない（明るい。UI_MODE_NIGHT_NO）。
    pub const NIGHT_NO: &str = "no";
    /// night: 取れない（UI_MODE_NIGHT_UNDEFINED・PC で設定が無い）。
    pub const NIGHT_UNKNOWN: &str = "unknown";
    /// sim_set_ui_mode の night: 差し替えをやめて OS の設定へ戻す（模擬だけ）。
    pub const NIGHT_SYSTEM: &str = "system";
    /// 端末の明暗の設定が変わった（W2-9。data `{ night }`）。
    pub const EVENT_UI_MODE_CHANGED: &str = "platform.ui_mode_changed";
}

/// 触感（W1-6。モジュール "haptics"。Android はメインプロセスが振動子を鳴らして答える）の名前・欄・上限・理由
/// （Java の PlatformContract の *HAPTICS*・*VIBRATE*・C# の Haptics と一致させる）。
///
/// 命令: `tap {}` → `{}`・`vibrate { ms }` → `{ ms }`（ms の規則は bridge::haptics。Java の HapticsVibrateCommand と同じ）。
pub mod haptics {
    /// 触感のモジュール。
    pub const MODULE: &str = "haptics";
    /// 軽いクリックの触感（Android は VibrationEffect.EFFECT_CLICK）。
    pub const METHOD_TAP: &str = "tap";
    /// 決まった長さの振動（Android は VibrationEffect.createOneShot）。
    pub const METHOD_VIBRATE: &str = "vibrate";
    /// vibrate の引数・返答: 長さ（ミリ秒）。
    pub const KEY_MS: &str = "ms";
    /// vibrate の ms の下限（これより小さい値は invalid_argument）。
    pub const MIN_VIBRATE_MS: i64 = 1;
    /// vibrate の ms の上限（これより大きい値はこれにそろえる）。
    pub const MAX_VIBRATE_MS: i64 = 5000;
    /// 端末に振動子が無い（Android）。
    pub const ERROR_NO_VIBRATOR: &str = "no_vibrator";
}

/// 通知（W1-5。モジュール "notification"。Android は :seed_platform の NotificationModule が答える。機能 `notifications`）の名前・欄・
/// 理由・上限（Java の PlatformContract の *NOTIFICATION*・C# の NotificationJson と一致させる）。
///
/// 通知の形: `{ id, channel_id, title, body, ongoing, category, actions: [ { id, label }, … ], payload_json }`。
/// チャネルの形: `{ channel_id, name, importance, description }`。`id` は文字列のまま通知の tag にする（int の ID の表を持たない）。
/// 失敗の理由のうち `invalid_argument`・`feature_not_enabled` は目覚ましと同じ文字列（wire::alarm の ERROR_*）。
pub mod notification {
    /// 通知のモジュール。
    pub const MODULE: &str = "notification";
    /// チャネルを作る（あれば名前・説明だけ変わる）。
    pub const METHOD_ENSURE_CHANNEL: &str = "ensure_channel";
    /// 通知を出す（同じ id は置き換え）。
    pub const METHOD_SHOW: &str = "show";
    /// 通知を消す（無い id でも成功）。
    pub const METHOD_CANCEL: &str = "cancel";
    /// アプリの通知が端末で有効か。
    pub const METHOD_ARE_ENABLED: &str = "are_enabled";

    /// 通知の ID（アプリが決める文字列）。
    pub const KEY_ID: &str = "id";
    /// チャネルの ID。
    pub const KEY_CHANNEL_ID: &str = "channel_id";
    /// チャネルの表示名（端末の設定の「通知」に出る）。
    pub const KEY_CHANNEL_NAME: &str = "name";
    /// チャネルの重要度（IMPORTANCE_*）。
    pub const KEY_IMPORTANCE: &str = "importance";
    /// チャネルの説明。
    pub const KEY_DESCRIPTION: &str = "description";
    /// 通知の題。
    pub const KEY_TITLE: &str = "title";
    /// 通知の本文（長文は折りたたみを開くと全部見える）。
    pub const KEY_BODY: &str = "body";
    /// 常駐（スワイプで消えにくい）か。
    pub const KEY_ONGOING: &str = "ongoing";
    /// 種類（CATEGORY_*。知らない値は付けない）。
    pub const KEY_CATEGORY: &str = "category";
    /// 操作（ボタン）の配列（最大 MAX_ACTIONS）。
    pub const KEY_ACTIONS: &str = "actions";
    /// 操作の ID（押すと起動理由 notification_action の action_id になる）。
    pub const KEY_ACTION_ID: &str = "id";
    /// 操作の表示の文字。
    pub const KEY_ACTION_LABEL: &str = "label";
    /// 起動理由にそのまま返す任意の JSON。
    pub const KEY_PAYLOAD_JSON: &str = "payload_json";
    /// are_enabled の返答: 通知が有効か。
    pub const KEY_ENABLED: &str = "enabled";

    /// 重要度: 低い（音なし・ステータスバーに出ない）。
    pub const IMPORTANCE_LOW: &str = "low";
    /// 重要度: 普通（音あり）。
    pub const IMPORTANCE_DEFAULT: &str = "default";
    /// 重要度: 高い（ヘッドアップ通知）。
    pub const IMPORTANCE_HIGH: &str = "high";
    /// 種類: 目覚まし。
    pub const CATEGORY_ALARM: &str = "alarm";
    /// 種類: 利用者が決めた予定の知らせ。
    pub const CATEGORY_REMINDER: &str = "reminder";
    /// 種類: 状態の表示。
    pub const CATEGORY_STATUS: &str = "status";
    /// 種類: 予定表の出来事。
    pub const CATEGORY_EVENT: &str = "event";
    /// 種類: 長い処理の進み具合。
    pub const CATEGORY_PROGRESS: &str = "progress";

    /// 通知が端末で無効（Android 13+ の POST_NOTIFICATIONS が無い・利用者が切った・チャネルが止められた）。
    pub const ERROR_NOTIFICATIONS_DISABLED: &str = "notifications_disabled";
    /// チャネルが無い（ensure_channel を先に呼ぶ）。
    pub const ERROR_CHANNEL_NOT_FOUND: &str = "channel_not_found";

    /// 通知・チャネル・操作の ID の最大の長さ（Unicode の符号位置の数）。
    pub const MAX_ID_LENGTH: usize = 128;
    /// 題・本文・操作の文字・チャネルの名前と説明の最大の長さ（Unicode の符号位置の数）。
    pub const MAX_TEXT_LENGTH: usize = 4096;
    /// payload_json の最大の長さ（Unicode の符号位置の数）。
    pub const MAX_PAYLOAD_LENGTH: usize = 16384;
    /// 操作（ボタン）の最大の数（Android の通知の標準の見た目が並べられる数）。
    pub const MAX_ACTIONS: usize = 3;
    /// アプリが使えないチャネルの ID の接頭辞（鳴動の通知チャネル seed_platform_alarm などプラットフォーム層のもの）。
    pub const RESERVED_CHANNEL_PREFIX: &str = "seed_platform";
}

/// 権限（W1-5。モジュール "permission"。Android はメインプロセスが Activity を使って答える）の名前・欄・種類・状態
/// （Java の PlatformContract の *PERMISSION*・C# の PermissionJson と一致させる）。
///
/// 命令の引数は `{ kind }`。check の返答 `{ kind, status }`・request の返答 `{ kind, request_id }`（結果は
/// イベント `platform.permission_result { request_id, kind, status }`）・open_settings の返答 `{ kind }`。
/// 前面へ戻ったとき（Android の onResume）に状態が前回と違えば `platform.permission_changed { kind, status }`。
pub mod permission {
    /// 権限のモジュール。
    pub const MODULE: &str = "permission";
    /// 今の状態を返す。
    pub const METHOD_CHECK: &str = "check";
    /// 求める（実行時の確認の画面か設定の画面）。結果は platform.permission_result。
    pub const METHOD_REQUEST: &str = "request";
    /// 設定の画面を開く（結果のイベントは無い。戻ったときに変わっていれば platform.permission_changed）。
    pub const METHOD_OPEN_SETTINGS: &str = "open_settings";

    /// 引数・返答・イベント: 種類（KIND_*）。
    pub const KEY_KIND: &str = "kind";
    /// 返答・イベント: 状態（STATUS_*）。
    pub const KEY_STATUS: &str = "status";
    /// request の返答・permission_result: 要求の ID（FIRST_REQUEST_ID から増える）。
    pub const KEY_REQUEST_ID: &str = "request_id";

    /// 種類: 通知（Android 13+ の POST_NOTIFICATIONS。12 以前は通知の設定）。
    pub const KIND_POST_NOTIFICATIONS: &str = "post_notifications";
    /// 種類: 正確なアラーム（Android 12 系の特別なアクセス。13+ は USE_EXACT_ALARM）。
    pub const KIND_EXACT_ALARM: &str = "exact_alarm";
    /// 種類: フルスクリーン通知（Android 14+ の特別なアクセス）。
    pub const KIND_FULL_SCREEN_INTENT: &str = "full_screen_intent";
    /// 種類: 録音（v2 の予約。今は常に not_applicable）。
    pub const KIND_RECORD_AUDIO: &str = "record_audio";
    /// 種類: SMS の送信（v2 の予約。今は常に not_applicable）。
    pub const KIND_SEND_SMS: &str = "send_sms";

    /// 状態: 許可されている。
    pub const STATUS_GRANTED: &str = "granted";
    /// 状態: 許可されていない（もう一度求めれば確認の画面が出る見込み）。
    pub const STATUS_DENIED: &str = "denied";
    /// 状態: 許可されず、求めても確認の画面が出ない（設定の画面へ案内する）。
    pub const STATUS_DENIED_PERMANENTLY: &str = "denied_permanently";
    /// 状態: 設定の画面で利用者が切り替える種類で、今は切られている。
    pub const STATUS_NEEDS_SETTINGS: &str = "needs_settings";
    /// 状態: この OS の版・この段階では要らない（扱わない）。
    pub const STATUS_NOT_APPLICABLE: &str = "not_applicable";

    /// 要求の結果（request の結果）。
    pub const EVENT_RESULT: &str = "platform.permission_result";
    /// 前面へ戻ったときに状態が変わっていた。
    pub const EVENT_CHANGED: &str = "platform.permission_changed";

    /// 最初に払い出す要求の ID（0 は「要求できなかった」の印に使う）。
    pub const FIRST_REQUEST_ID: i64 = 1;
}

/// センサー（W1-8。モジュール "sensor"。Android はメインプロセスの Java〈platform/sensor/〉が SensorManager から受けて答える。IPC なし）の
/// 名前・欄・上限・理由（Java の PlatformContract の *SENSOR*・C# の SensorJson と一致させる）。
///
/// 命令: `start { kind, rate_hz }` → `{ kind, supported, source, rate_hz }`・`stop { kind }` → `{ kind, stopped }`・
/// `read { kind }` → `{ kind, x, y, z, timestamp_ms, peak_magnitude, sample_count }`。x・y・z は最新の標本（m/s²）、timestamp_ms は
/// その時刻（UTC の epoch ミリ秒。まだ無ければ 0）、peak_magnitude と sample_count は**前回の read からの**最大の大きさと標本の数で、
/// read のたびに 0 へ戻る（フレームの間に来た振りを見落とさないため。標本ごとのイベントは流さない）。
/// 模擬だけ `sim_inject { kind, x, y, z }` → `{ kind, sample_count }`（標本を 1 つ入れる。Android には無く unknown_method）。
/// 失敗の理由のうち `invalid_argument` は目覚ましと同じ文字列（wire::alarm の ERROR_INVALID_ARGUMENT）。規則は bridge::sensor。
pub mod sensor {
    /// センサーのモジュール。
    pub const MODULE: &str = "sensor";
    /// 受け取りを始める（動いていれば標本を捨てて始め直す）。
    pub const METHOD_START: &str = "start";
    /// 受け取りを止める（動いていなくても成功。冪等）。
    pub const METHOD_STOP: &str = "stop";
    /// 最新の標本と、前回の read からの最大の大きさ・標本の数を読む（読むと最大と数は 0 に戻る）。
    pub const METHOD_READ: &str = "read";
    /// 標本を 1 つ入れる（デスクトップの模擬だけ。単体テストと PC の確かめで振りを作る）。
    pub const METHOD_SIM_INJECT: &str = "sim_inject";

    /// 引数・返答: センサーの種類（KIND_*）。
    pub const KEY_KIND: &str = "kind";
    /// start の引数・返答: 受け取りの頻度（Hz。返答はそろえた後の値）。
    pub const KEY_RATE_HZ: &str = "rate_hz";
    /// start の返答: 使えるか（成功の返答では常に true。使えなければ ERROR_NOT_SUPPORTED の失敗の返答）。
    pub const KEY_SUPPORTED: &str = "supported";
    /// start の返答: 値の出どころ（SOURCE_*）。
    pub const KEY_SOURCE: &str = "source";
    /// stop の返答: 動いていたものを止めたか（動いていなければ false。それでも ok=true）。
    pub const KEY_STOPPED: &str = "stopped";
    /// read の返答・sim_inject の引数: 最新の標本の x 成分（m/s²。端末の座標系）。
    pub const KEY_X: &str = "x";
    /// read の返答・sim_inject の引数: 最新の標本の y 成分（m/s²）。
    pub const KEY_Y: &str = "y";
    /// read の返答・sim_inject の引数: 最新の標本の z 成分（m/s²）。
    pub const KEY_Z: &str = "z";
    /// read の返答: 最新の標本の時刻（UTC の epoch ミリ秒。まだ標本が無ければ 0）。
    pub const KEY_TIMESTAMP_MS: &str = "timestamp_ms";
    /// read の返答: 前回の read からの標本の大きさ √(x²+y²+z²) の最大（m/s²。標本が無ければ 0）。
    pub const KEY_PEAK_MAGNITUDE: &str = "peak_magnitude";
    /// read の返答: 前回の read からの標本の数（sim_inject の返答は入れた後の数）。
    pub const KEY_SAMPLE_COUNT: &str = "sample_count";

    /// 種類: 重力を除いた加速度（Android の TYPE_LINEAR_ACCELERATION に当たる。m/s²）。
    pub const KIND_LINEAR_ACCELERATION: &str = "linear_acceleration";
    /// 約束の種類の一覧（これ以外は invalid_argument）。
    pub const KINDS: [&str; 1] = [KIND_LINEAR_ACCELERATION];

    /// 出どころ: 端末の重力を除いた加速度のセンサー（Android の TYPE_LINEAR_ACCELERATION）。
    pub const SOURCE_LINEAR_ACCELERATION: &str = "linear_acceleration";
    /// 出どころ: 加速度のセンサー（TYPE_ACCELEROMETER）から低域通過で重力を見積もって引いた値（上が無い端末の代わり）。
    pub const SOURCE_ACCELEROMETER_LOWPASS: &str = "accelerometer_lowpass";
    /// 出どころ: デスクトップの模擬（値は sim_inject で入れた標本だけ。入れなければ 0）。
    pub const SOURCE_SIMULATED: &str = "simulated";

    /// rate_hz の既定値（Android の SENSOR_DELAY_GAME＝20,000 µs と同じ 50 Hz）。
    pub const DEFAULT_RATE_HZ: i64 = 50;
    /// rate_hz の下限（これより小さい値は invalid_argument）。
    pub const MIN_RATE_HZ: i64 = 1;
    /// rate_hz の上限（これより大きい値はこれにそろえる。Android 12 以降の registerListener の上限 200 Hz）。
    pub const MAX_RATE_HZ: i64 = 200;

    /// 端末にその種類を出せるセンサーが無い（重力を除いた加速度も加速度も無い）。
    pub const ERROR_NOT_SUPPORTED: &str = "not_supported";
    /// start していない種類を read した・標本を入れようとした。
    pub const ERROR_NOT_STARTED: &str = "not_started";
    /// センサーの登録を OS が受け付けなかった（Android の registerListener が false）。
    pub const ERROR_REGISTER_FAILED: &str = "register_failed";
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 名前の約束: 小文字英数字と _ だけ・空と長すぎは不可・`.` を含む名前は不可（Java で `<module>.<method>` に使うため）。
    #[test]
    fn name_rules() {
        assert!(is_valid_name("platform"));
        assert!(is_valid_name("emit_test_event"));
        assert!(is_valid_name("alarm2"));
        assert!(!is_valid_name(""));
        assert!(!is_valid_name("Platform"), "大文字は不可");
        assert!(!is_valid_name("platform.ping"), "区切りの . を含む名前は不可");
        assert!(!is_valid_name("ping pong"));
        assert!(!is_valid_name(&"a".repeat(MAX_NAME_LEN + 1)));
        assert!(is_valid_name(&"a".repeat(MAX_NAME_LEN)));
    }

    /// 失敗の返答は ok=false と理由を持つ。
    #[test]
    fn error_reply_shape() {
        let value: Value = serde_json::from_str(&error_reply(ERROR_UNKNOWN_METHOD)).unwrap();
        assert_eq!(value[KEY_OK], Value::Bool(false));
        assert_eq!(value[KEY_ERROR], Value::from(ERROR_UNKNOWN_METHOD));
    }

    /// 成功の返答は ok=true が先頭に入り、呼び出し側の ok は上書きされる。
    #[test]
    fn ok_reply_forces_ok_true() {
        let mut fields = Map::new();
        fields.insert(KEY_OK.to_string(), Value::Bool(false));
        fields.insert("pid".to_string(), Value::from(42));
        let text = ok_reply(fields);
        let value: Value = serde_json::from_str(&text).unwrap();
        assert_eq!(value[KEY_OK], Value::Bool(true));
        assert_eq!(value["pid"], Value::from(42));
        assert!(text.starts_with("{\"ok\":true"), "ok が先頭にない: {text}");
    }

    /// イベントの JSON は検査を通り、名前を返す。接頭辞の無い名前・名前の無いもの・壊れた JSON は通さない。
    #[test]
    fn event_json_round_trips_through_validation() {
        let text = event_json(TEST_EVENT_NAME, 3, 1_700_000_000_000, json!({ "message": "こんにちは" }));
        assert_eq!(validate_event_json(&text).unwrap(), TEST_EVENT_NAME);
        assert!(validate_event_json(r#"{"name":"alarm.started"}"#).is_err());
        assert!(validate_event_json(r#"{"seq":1}"#).is_err());
        assert!(validate_event_json("{not json").is_err());
    }

    /// 引数の JSON: 空は空のオブジェクト、壊れたものは ERROR_INVALID_JSON。
    #[test]
    fn parse_request_rules() {
        assert_eq!(parse_request("").unwrap(), json!({}));
        assert_eq!(parse_request("  ").unwrap(), json!({}));
        assert_eq!(parse_request(r#"{"a":1}"#).unwrap(), json!({ "a": 1 }));
        assert_eq!(parse_request("{").unwrap_err(), ERROR_INVALID_JSON);
    }

    /// Java の PlatformContract.java の定数（`public static final <型> 名前 = 値;`）の値を取り出す（テスト用）。
    fn java_constant(source: &str, name: &str) -> String {
        let marker = format!(" {name} = ");
        let start = source.find(&marker).unwrap_or_else(|| panic!("PlatformContract.java に {name} がありません")) + marker.len();
        let end = source[start..].find(';').expect("定数の終わりの ; が無い") + start;
        source[start..end].trim().trim_matches('"').to_string()
    }

    /// Java の PlatformContract.java と wire.rs の名前・上限・既定値が一致する（食い違うと実機で黙って動かない）。
    #[test]
    fn java_contract_matches_wire() {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/android/app/src/main/java/com/seedengine/runtime/platform/PlatformContract.java"
        );
        let source = std::fs::read_to_string(path).expect("PlatformContract.java が読めない");
        let strings: &[(&str, &str)] = &[
            ("MODULE_PLATFORM", MODULE_PLATFORM), ("METHOD_PING", METHOD_PING), ("METHOD_VERSION", METHOD_VERSION),
            ("METHOD_EMIT_TEST_EVENT", METHOD_EMIT_TEST_EVENT), ("EVENT_TEST", TEST_EVENT_NAME), ("METHOD_PATHS", METHOD_PATHS),
            ("KEY_DEVICE_PROTECTED_FILES_DIR", KEY_DEVICE_PROTECTED_FILES_DIR), ("KEY_SOUNDS_DIR", KEY_SOUNDS_DIR),
            ("KEY_OK", KEY_OK), ("KEY_ERROR", KEY_ERROR), ("KEY_DETAIL", KEY_DETAIL), ("KEY_NAME", KEY_NAME),
            ("KEY_SEQ", KEY_SEQ), ("KEY_TIME_MS", KEY_TIME_MS), ("KEY_DATA", KEY_DATA), ("EVENT_PREFIX", EVENT_NAME_PREFIX),
            ("ERROR_UNKNOWN_METHOD", ERROR_UNKNOWN_METHOD), ("ERROR_INVALID_NAME", ERROR_INVALID_NAME),
            ("ERROR_INVALID_JSON", ERROR_INVALID_JSON), ("ERROR_CONNECTING", ERROR_CONNECTING),
            ("MODULE_ALARM", alarm::MODULE), ("METHOD_ALARM_SCHEDULE", alarm::METHOD_SCHEDULE),
            ("METHOD_ALARM_CANCEL", alarm::METHOD_CANCEL), ("METHOD_ALARM_CANCEL_ALL", alarm::METHOD_CANCEL_ALL),
            ("METHOD_ALARM_LIST", alarm::METHOD_LIST), ("METHOD_ALARM_CAN_SCHEDULE_EXACT", alarm::METHOD_CAN_SCHEDULE_EXACT),
            ("KEY_ALARM_ID", alarm::KEY_ID), ("KEY_ALARM_TRIGGER_AT_UTC_MS", alarm::KEY_TRIGGER_AT_UTC_MS),
            ("KEY_ALARM_SOUND_ASSET", alarm::KEY_SOUND_ASSET), ("KEY_ALARM_SOUND_PATH", alarm::KEY_SOUND_PATH),
            ("KEY_ALARM_VIBRATE", alarm::KEY_VIBRATE), ("KEY_ALARM_FORCE_VOLUME", alarm::KEY_FORCE_VOLUME),
            ("KEY_ALARM_KEEP_VOLUME", alarm::KEY_KEEP_VOLUME), ("KEY_ALARM_FADE_IN_SECONDS", alarm::KEY_FADE_IN_SECONDS),
            ("KEY_ALARM_MAX_RING_MINUTES", alarm::KEY_MAX_RING_MINUTES), ("KEY_ALARM_TITLE", alarm::KEY_TITLE),
            ("KEY_ALARM_BODY", alarm::KEY_BODY), ("KEY_ALARM_PAYLOAD_JSON", alarm::KEY_PAYLOAD_JSON),
            ("KEY_ALARM_CREATED_AT_UTC_MS", alarm::KEY_CREATED_AT_UTC_MS), ("KEY_ALARMS", alarm::KEY_ALARMS),
            ("KEY_ALARM_REPLACED", alarm::KEY_REPLACED), ("KEY_ALARM_EXISTED", alarm::KEY_EXISTED), ("KEY_COUNT", alarm::KEY_COUNT),
            ("KEY_CAN_SCHEDULE_EXACT", alarm::KEY_CAN_SCHEDULE_EXACT),
            ("KEY_ALARM_SCHEDULED_AT_UTC_MS", alarm::KEY_SCHEDULED_AT_UTC_MS), ("KEY_ALARM_FIRED_AT_UTC_MS", alarm::KEY_FIRED_AT_UTC_MS),
            ("KEY_REASON", alarm::KEY_REASON), ("KEY_MISSED", alarm::KEY_MISSED), ("KEY_FAILED", alarm::KEY_FAILED),
            ("EVENT_ALARM_FIRED", alarm::EVENT_FIRED), ("EVENT_ALARM_MISSED", alarm::EVENT_MISSED),
            ("EVENT_ALARMS_RESCHEDULED", alarm::EVENT_RESCHEDULED),
            ("MISSED_REASON_DEVICE_OFF", alarm::MISSED_REASON_DEVICE_OFF),
            ("MISSED_REASON_PERMISSION_REVOKED", alarm::MISSED_REASON_PERMISSION_REVOKED),
            ("RESCHEDULE_REASON_BOOT", alarm::RESCHEDULE_REASON_BOOT),
            ("RESCHEDULE_REASON_TIME_CHANGED", alarm::RESCHEDULE_REASON_TIME_CHANGED),
            ("RESCHEDULE_REASON_PACKAGE_REPLACED", alarm::RESCHEDULE_REASON_PACKAGE_REPLACED),
            ("RESCHEDULE_REASON_PERMISSION_CHANGED", alarm::RESCHEDULE_REASON_PERMISSION_CHANGED),
            ("ERROR_EXACT_ALARM_NOT_ALLOWED", alarm::ERROR_EXACT_ALARM_NOT_ALLOWED),
            ("ERROR_INVALID_ARGUMENT", alarm::ERROR_INVALID_ARGUMENT), ("ERROR_TOO_MANY_ALARMS", alarm::ERROR_TOO_MANY_ALARMS),
            ("ERROR_STORE_WRITE_FAILED", alarm::ERROR_STORE_WRITE_FAILED), ("ERROR_SCHEDULE_FAILED", alarm::ERROR_SCHEDULE_FAILED),
            ("ERROR_FEATURE_NOT_ENABLED", alarm::ERROR_FEATURE_NOT_ENABLED),
            // W1-4a: 鳴動
            ("METHOD_ALARM_GET_RINGING", alarm::METHOD_GET_RINGING), ("METHOD_ALARM_STOP_RINGING", alarm::METHOD_STOP_RINGING),
            ("KEY_ALARM_RINGING", alarm::KEY_RINGING), ("KEY_ALARM_STARTED_AT_UTC_MS", alarm::KEY_STARTED_AT_UTC_MS),
            ("KEY_ALARM_WAITING_FOR", alarm::KEY_WAITING_FOR), ("KEY_ALARM_STOPPED", alarm::KEY_STOPPED),
            ("EVENT_ALARM_RING_STOPPED", alarm::EVENT_RING_STOPPED), ("EVENT_ALARM_QUEUED", alarm::EVENT_QUEUED),
            ("MISSED_REASON_START_FAILED", alarm::MISSED_REASON_START_FAILED),
            ("RING_STOP_REASON_STOPPED", alarm::RING_STOP_REASON_STOPPED), ("RING_STOP_REASON_TIMEOUT", alarm::RING_STOP_REASON_TIMEOUT),
            ("RING_STOP_REASON_ERROR", alarm::RING_STOP_REASON_ERROR),
            // W1-4a: 起動理由
            ("METHOD_LAUNCH_REASON", launch::METHOD_LAUNCH_REASON), ("EVENT_LAUNCH", launch::EVENT_LAUNCH),
            ("KEY_LAUNCH", launch::KEY_LAUNCH), ("KEY_LAUNCH_KIND", launch::KEY_KIND), ("KEY_LAUNCH_ACTION_ID", launch::KEY_ACTION_ID),
            ("LAUNCH_KIND_LAUNCHER", launch::KIND_LAUNCHER), ("LAUNCH_KIND_ALARM", launch::KIND_ALARM),
            ("LAUNCH_KIND_NOTIFICATION_TAP", launch::KIND_NOTIFICATION_TAP),
            ("LAUNCH_KIND_NOTIFICATION_ACTION", launch::KIND_NOTIFICATION_ACTION),
            ("LAUNCH_KIND_ALARM_CLOCK_INFO", launch::KIND_ALARM_CLOCK_INFO), ("LAUNCH_KIND_OTHER", launch::KIND_OTHER),
            ("LAUNCH_ACTION_OPEN", launch::ACTION_OPEN),
            // W1-6: ディープリンク
            ("LAUNCH_KIND_DEEP_LINK", launch::KIND_DEEP_LINK), ("KEY_LAUNCH_URI", launch::KEY_URI),
            // W1-4a: 画面（W1-6 で画面を点けたまま・システムバー）
            ("MODULE_WINDOW", window::MODULE), ("METHOD_WINDOW_SET_SHOW_WHEN_LOCKED", window::METHOD_SET_SHOW_WHEN_LOCKED),
            ("METHOD_WINDOW_SET_KEEP_SCREEN_ON", window::METHOD_SET_KEEP_SCREEN_ON),
            ("METHOD_WINDOW_SET_SYSTEM_BARS_VISIBLE", window::METHOD_SET_SYSTEM_BARS_VISIBLE),
            ("KEY_WINDOW_ON", window::KEY_ON), ("ERROR_NO_ACTIVITY", window::ERROR_NO_ACTIVITY),
            // W1-6: アプリ
            ("MODULE_APP", app::MODULE), ("METHOD_APP_MOVE_TASK_TO_BACK", app::METHOD_MOVE_TASK_TO_BACK),
            ("METHOD_APP_OPEN_URL", app::METHOD_OPEN_URL), ("METHOD_APP_OPEN_APP_SETTINGS", app::METHOD_OPEN_APP_SETTINGS),
            ("KEY_APP_URL", app::KEY_URL), ("KEY_APP_SCHEME", app::KEY_SCHEME),
            ("URL_SCHEME_FILE", app::SCHEME_FILE), ("URL_SCHEME_CONTENT", app::SCHEME_CONTENT),
            ("URL_SCHEME_JAVASCRIPT", app::SCHEME_JAVASCRIPT),
            ("ERROR_NO_HANDLER", app::ERROR_NO_HANDLER), ("ERROR_SCHEME_NOT_ALLOWED", app::ERROR_SCHEME_NOT_ALLOWED),
            // W2-9: 端末の明暗（sim_set_ui_mode と NIGHT_SYSTEM は模擬だけなので Java には無い）
            ("METHOD_APP_UI_MODE", app::METHOD_UI_MODE), ("KEY_APP_NIGHT", app::KEY_NIGHT),
            ("APP_NIGHT_YES", app::NIGHT_YES), ("APP_NIGHT_NO", app::NIGHT_NO), ("APP_NIGHT_UNKNOWN", app::NIGHT_UNKNOWN),
            ("EVENT_UI_MODE_CHANGED", app::EVENT_UI_MODE_CHANGED),
            // W1-6: 触感
            ("MODULE_HAPTICS", haptics::MODULE), ("METHOD_HAPTICS_TAP", haptics::METHOD_TAP),
            ("METHOD_HAPTICS_VIBRATE", haptics::METHOD_VIBRATE), ("KEY_HAPTICS_MS", haptics::KEY_MS),
            ("ERROR_NO_VIBRATOR", haptics::ERROR_NO_VIBRATOR),
            // W1-5: 通知
            ("MODULE_NOTIFICATION", notification::MODULE),
            ("METHOD_NOTIFICATION_ENSURE_CHANNEL", notification::METHOD_ENSURE_CHANNEL),
            ("METHOD_NOTIFICATION_SHOW", notification::METHOD_SHOW), ("METHOD_NOTIFICATION_CANCEL", notification::METHOD_CANCEL),
            ("METHOD_NOTIFICATION_ARE_ENABLED", notification::METHOD_ARE_ENABLED),
            ("KEY_NOTIFICATION_ID", notification::KEY_ID), ("KEY_NOTIFICATION_CHANNEL_ID", notification::KEY_CHANNEL_ID),
            ("KEY_NOTIFICATION_CHANNEL_NAME", notification::KEY_CHANNEL_NAME),
            ("KEY_NOTIFICATION_IMPORTANCE", notification::KEY_IMPORTANCE),
            ("KEY_NOTIFICATION_DESCRIPTION", notification::KEY_DESCRIPTION), ("KEY_NOTIFICATION_TITLE", notification::KEY_TITLE),
            ("KEY_NOTIFICATION_BODY", notification::KEY_BODY), ("KEY_NOTIFICATION_ONGOING", notification::KEY_ONGOING),
            ("KEY_NOTIFICATION_CATEGORY", notification::KEY_CATEGORY), ("KEY_NOTIFICATION_ACTIONS", notification::KEY_ACTIONS),
            ("KEY_NOTIFICATION_ACTION_ID", notification::KEY_ACTION_ID),
            ("KEY_NOTIFICATION_ACTION_LABEL", notification::KEY_ACTION_LABEL),
            ("KEY_NOTIFICATION_PAYLOAD_JSON", notification::KEY_PAYLOAD_JSON),
            ("KEY_NOTIFICATIONS_ENABLED", notification::KEY_ENABLED),
            ("NOTIFICATION_IMPORTANCE_LOW", notification::IMPORTANCE_LOW),
            ("NOTIFICATION_IMPORTANCE_DEFAULT", notification::IMPORTANCE_DEFAULT),
            ("NOTIFICATION_IMPORTANCE_HIGH", notification::IMPORTANCE_HIGH),
            ("NOTIFICATION_CATEGORY_ALARM", notification::CATEGORY_ALARM),
            ("NOTIFICATION_CATEGORY_REMINDER", notification::CATEGORY_REMINDER),
            ("NOTIFICATION_CATEGORY_STATUS", notification::CATEGORY_STATUS),
            ("NOTIFICATION_CATEGORY_EVENT", notification::CATEGORY_EVENT),
            ("NOTIFICATION_CATEGORY_PROGRESS", notification::CATEGORY_PROGRESS),
            ("ERROR_NOTIFICATIONS_DISABLED", notification::ERROR_NOTIFICATIONS_DISABLED),
            ("ERROR_CHANNEL_NOT_FOUND", notification::ERROR_CHANNEL_NOT_FOUND),
            ("RESERVED_NOTIFICATION_CHANNEL_PREFIX", notification::RESERVED_CHANNEL_PREFIX),
            // W1-5: 権限
            ("MODULE_PERMISSION", permission::MODULE), ("METHOD_PERMISSION_CHECK", permission::METHOD_CHECK),
            ("METHOD_PERMISSION_REQUEST", permission::METHOD_REQUEST),
            ("METHOD_PERMISSION_OPEN_SETTINGS", permission::METHOD_OPEN_SETTINGS),
            ("KEY_PERMISSION_KIND", permission::KEY_KIND), ("KEY_PERMISSION_STATUS", permission::KEY_STATUS),
            ("KEY_PERMISSION_REQUEST_ID", permission::KEY_REQUEST_ID),
            ("PERMISSION_KIND_POST_NOTIFICATIONS", permission::KIND_POST_NOTIFICATIONS),
            ("PERMISSION_KIND_EXACT_ALARM", permission::KIND_EXACT_ALARM),
            ("PERMISSION_KIND_FULL_SCREEN_INTENT", permission::KIND_FULL_SCREEN_INTENT),
            ("PERMISSION_KIND_RECORD_AUDIO", permission::KIND_RECORD_AUDIO),
            ("PERMISSION_KIND_SEND_SMS", permission::KIND_SEND_SMS),
            ("PERMISSION_STATUS_GRANTED", permission::STATUS_GRANTED), ("PERMISSION_STATUS_DENIED", permission::STATUS_DENIED),
            ("PERMISSION_STATUS_DENIED_PERMANENTLY", permission::STATUS_DENIED_PERMANENTLY),
            ("PERMISSION_STATUS_NEEDS_SETTINGS", permission::STATUS_NEEDS_SETTINGS),
            ("PERMISSION_STATUS_NOT_APPLICABLE", permission::STATUS_NOT_APPLICABLE),
            ("EVENT_PERMISSION_RESULT", permission::EVENT_RESULT), ("EVENT_PERMISSION_CHANGED", permission::EVENT_CHANGED),
            // W1-8: センサー（sim_inject と SOURCE_SIMULATED は模擬だけなので Java には無い）
            ("MODULE_SENSOR", sensor::MODULE), ("METHOD_SENSOR_START", sensor::METHOD_START),
            ("METHOD_SENSOR_STOP", sensor::METHOD_STOP), ("METHOD_SENSOR_READ", sensor::METHOD_READ),
            ("KEY_SENSOR_KIND", sensor::KEY_KIND), ("KEY_SENSOR_RATE_HZ", sensor::KEY_RATE_HZ),
            ("KEY_SENSOR_SUPPORTED", sensor::KEY_SUPPORTED), ("KEY_SENSOR_SOURCE", sensor::KEY_SOURCE),
            ("KEY_SENSOR_STOPPED", sensor::KEY_STOPPED), ("KEY_SENSOR_X", sensor::KEY_X), ("KEY_SENSOR_Y", sensor::KEY_Y),
            ("KEY_SENSOR_Z", sensor::KEY_Z), ("KEY_SENSOR_TIMESTAMP_MS", sensor::KEY_TIMESTAMP_MS),
            ("KEY_SENSOR_PEAK_MAGNITUDE", sensor::KEY_PEAK_MAGNITUDE), ("KEY_SENSOR_SAMPLE_COUNT", sensor::KEY_SAMPLE_COUNT),
            ("SENSOR_KIND_LINEAR_ACCELERATION", sensor::KIND_LINEAR_ACCELERATION),
            ("SENSOR_SOURCE_LINEAR_ACCELERATION", sensor::SOURCE_LINEAR_ACCELERATION),
            ("SENSOR_SOURCE_ACCELEROMETER_LOWPASS", sensor::SOURCE_ACCELEROMETER_LOWPASS),
            ("ERROR_NOT_SUPPORTED", sensor::ERROR_NOT_SUPPORTED), ("ERROR_NOT_STARTED", sensor::ERROR_NOT_STARTED),
            ("ERROR_REGISTER_FAILED", sensor::ERROR_REGISTER_FAILED),
        ];
        for (java, rust) in strings {
            assert_eq!(java_constant(&source, java), *rust, "PlatformContract.{java} と wire.rs が食い違う");
        }
        let numbers: &[(&str, f64)] = &[
            ("PROTOCOL_VERSION", f64::from(PROTOCOL_VERSION)), ("MAX_NAME_LENGTH", MAX_NAME_LEN as f64),
            ("LOCAL_EVENT_SEQ", LOCAL_EVENT_SEQ as f64), ("MAX_ALARM_ID_LENGTH", alarm::MAX_ID_LENGTH as f64),
            ("MAX_ALARM_TEXT_LENGTH", alarm::MAX_TEXT_LENGTH as f64), ("MAX_ALARM_PAYLOAD_LENGTH", alarm::MAX_PAYLOAD_LENGTH as f64),
            ("MAX_SCHEDULED_ALARMS", alarm::MAX_SCHEDULED_ALARMS as f64), ("ALARM_VOLUME_UNCHANGED", alarm::VOLUME_UNCHANGED),
            ("MAX_ALARM_FORCE_VOLUME", alarm::MAX_FORCE_VOLUME), ("DEFAULT_ALARM_FADE_IN_SECONDS", alarm::DEFAULT_FADE_IN_SECONDS),
            ("DEFAULT_ALARM_MAX_RING_MINUTES", alarm::DEFAULT_MAX_RING_MINUTES as f64),
            ("MIN_ALARM_MAX_RING_MINUTES", alarm::MIN_MAX_RING_MINUTES as f64),
            ("MILLIS_PER_MINUTE", alarm::MILLIS_PER_MINUTE as f64),
            // W1-5: 通知の上限・権限の要求の ID
            ("MAX_NOTIFICATION_ID_LENGTH", notification::MAX_ID_LENGTH as f64),
            ("MAX_NOTIFICATION_TEXT_LENGTH", notification::MAX_TEXT_LENGTH as f64),
            ("MAX_NOTIFICATION_PAYLOAD_LENGTH", notification::MAX_PAYLOAD_LENGTH as f64),
            ("MAX_NOTIFICATION_ACTIONS", notification::MAX_ACTIONS as f64),
            ("FIRST_PERMISSION_REQUEST_ID", permission::FIRST_REQUEST_ID as f64),
            // W1-6: URL の長さ・振動の長さ
            ("MAX_URL_LENGTH", MAX_URL_LENGTH as f64),
            ("MIN_VIBRATE_MS", haptics::MIN_VIBRATE_MS as f64), ("MAX_VIBRATE_MS", haptics::MAX_VIBRATE_MS as f64),
            // W1-8: センサーの頻度
            ("DEFAULT_SENSOR_RATE_HZ", sensor::DEFAULT_RATE_HZ as f64), ("MIN_SENSOR_RATE_HZ", sensor::MIN_RATE_HZ as f64),
            ("MAX_SENSOR_RATE_HZ", sensor::MAX_RATE_HZ as f64),
        ];
        for (java, rust) in numbers {
            // Java の数の書き方（桁区切りの _・long の L）を落としてから読む
            let literal = java_constant(&source, java).replace('_', "");
            let value: f64 =
                literal.trim_end_matches(['L', 'l']).parse().unwrap_or_else(|_| panic!("{java} が数でない: {literal}"));
            assert_eq!(value, *rust, "PlatformContract.{java} と wire.rs が食い違う");
        }
        let flags: &[(&str, bool)] =
            &[("DEFAULT_ALARM_VIBRATE", alarm::DEFAULT_VIBRATE), ("DEFAULT_ALARM_KEEP_VOLUME", alarm::DEFAULT_KEEP_VOLUME)];
        for (java, rust) in flags {
            assert_eq!(java_constant(&source, java), rust.to_string(), "PlatformContract.{java} と wire.rs が食い違う");
        }
    }
}
