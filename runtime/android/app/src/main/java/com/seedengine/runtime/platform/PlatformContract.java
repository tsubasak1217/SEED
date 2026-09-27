// ============================================================
//  PlatformContract.java — アプリのプラットフォーム機能（SEED.Platform）の約束の置き場（W1-1・W1-3 で目覚まし・
//  W1-4a で鳴動・起動理由・画面の命令を追加）
//
//  メインプロセス（SeedPlatform・PlatformConnection）と :seed_platform プロセス（service/ の PlatformProvider 等）の
//  両方が使う名前・キー・理由の名前を 1 か所に集める（マジックナンバー・文字列を散らさない）。
//  エンジン側（Rust）の対になる正典は runtime/src/engine/platform/bridge/wire.rs（目覚ましは wire::alarm、起動理由は
//  wire::launch、画面は wire::window）、C# 側は scripting/src/Api/Platform/（目覚ましは Alarms/AlarmJson.cs、起動理由は
//  App/LaunchJson.cs）。値を変えるときは 3 か所を必ず揃える（Rust の単体テスト wire::tests::java_contract_matches_wire が、
//  このファイルの文字列の定数と wire.rs を突き合わせる）。
//  全体像は docs/android.md §25（目覚ましは §25.11、鳴動は §25.12）。
// ============================================================

package com.seedengine.runtime.platform;

import android.content.Context;
import android.os.IBinder;

/**
 * プラットフォーム機能の約束（名前・キー・理由）の定数と、名前の規則の判定。
 *
 * <p>インスタンスは作らない。両方のプロセスから参照するので、ネイティブライブラリや Activity に依存するものを置かない
 * （:seed_platform は Java だけのプロセスで、libSEED.so を読み込まない）。</p>
 */
public final class PlatformContract {

    private PlatformContract() {
    }

    /** プロトコルの版（platform.version の返答）。Rust の wire::PROTOCOL_VERSION と一致させる。 */
    public static final int PROTOCOL_VERSION = 1;

    /** logcat のタグ（両プロセス共通。`adb logcat -s SEED SEEDPlatform` で一緒に見る）。 */
    public static final String LOG_TAG = "SEEDPlatform";

    // ── ContentProvider（:seed_platform の PlatformProvider）──

    /**
     * プロバイダの authority の接尾辞。authority は「applicationId＋この接尾辞」（マニフェストの
     * {@code ${applicationId}.seed_platform} と一致させる）。applicationId はプロジェクトごとに変わるので、実行時に作る。
     */
    public static final String AUTHORITY_SUFFIX = ".seed_platform";

    /** ContentProvider の call の method を「module＋区切り＋method」にするときの区切り。 */
    public static final String METHOD_SEPARATOR = ".";

    /** call の extras / 返答の Bundle: JSON（UTF-8 の byte[]）のキー。 */
    public static final String BUNDLE_JSON = "json";

    /** call の extras: イベントの知らせを受ける Binder（platform.register_callback）のキー。 */
    public static final String BUNDLE_CALLBACK = "callback";

    /** :seed_platform → メインプロセスの知らせ（Binder の oneway の呼び出し）: 未読の記録がある。Parcel は long（最新の seq）1 つ。 */
    public static final int TRANSACTION_EVENTS_AVAILABLE = IBinder.FIRST_CALL_TRANSACTION;

    // ── モジュールとメソッド（W1-1）──

    /** 基盤そのもののモジュール。 */
    public static final String MODULE_PLATFORM = "platform";
    /** 往復の計測（受け取った JSON をそのまま echo に入れて返す＋pid・起動からの ms）。 */
    public static final String METHOD_PING = "ping";
    /** プロトコルの版と使えるモジュール。 */
    public static final String METHOD_VERSION = "version";
    /** イベントの知らせを受ける Binder を登録する（メインプロセスの PlatformConnection が接続のたびに呼ぶ）。 */
    public static final String METHOD_REGISTER_CALLBACK = "register_callback";
    /** 試験イベントを 1 つ記録し、知らせる（デバッグ用）。 */
    public static final String METHOD_EMIT_TEST_EVENT = "emit_test_event";
    /** 未読の記録を取り出す（知らせを受けたメインプロセスが呼ぶ）。 */
    public static final String METHOD_POLL_EVENTS = "poll_events";
    /**
     * :seed_platform の置き場（端末保護ストレージ）の絶対パスを返す（W1-3）。メインプロセス（エンジンの Android の糊）が
     * 最初に要るときに 1 回呼び、目覚ましの音源（assets:// の中身）を書き出す先（sounds_dir）を知る。
     */
    public static final String METHOD_PATHS = "paths";

    // ── JSON のキー ──

    /** 返答: 成功したか。 */
    public static final String KEY_OK = "ok";
    /** 返答: 失敗の理由の名前（下の ERROR_*）。 */
    public static final String KEY_ERROR = "error";
    /** 返答: 失敗の詳しい説明（例外の型と文言。スクリプトの LastError には入らない。ログ用）。 */
    public static final String KEY_DETAIL = "detail";
    /** イベント: 名前。 */
    public static final String KEY_NAME = "name";
    /** イベント: 通し番号（記録の番号。メインプロセスで作る知らせは LOCAL_EVENT_SEQ）。 */
    public static final String KEY_SEQ = "seq";
    /** イベント: 起きた時刻（UTC の epoch ミリ秒）。 */
    public static final String KEY_TIME_MS = "time_ms";
    /** イベント: 中身。 */
    public static final String KEY_DATA = "data";
    /** poll_events の返答: イベントの配列。 */
    public static final String KEY_EVENTS = "events";
    /** poll_events の返答: まだ未読が残っているか。 */
    public static final String KEY_HAS_MORE = "has_more";
    /** 記録の最新の通し番号。 */
    public static final String KEY_LATEST_SEQ = "latest_seq";
    /** poll_events の引数: 1 回に取り出す最大の件数。 */
    public static final String KEY_MAX = "max";
    /** ping の返答: 受け取った JSON。 */
    public static final String KEY_ECHO = "echo";
    /** プロセスの pid。 */
    public static final String KEY_PID = "pid";
    /** ping の返答: プロセスの起動からの経過ミリ秒。 */
    public static final String KEY_UPTIME_MS = "uptime_ms";
    /** プロセスの名前（例 com.example:seed_platform）。 */
    public static final String KEY_PROCESS = "process";
    /** version の返答: プロトコルの版。 */
    public static final String KEY_PROTOCOL = "protocol";
    /** version の返答: 使えるモジュールの名前の配列。 */
    public static final String KEY_MODULES = "modules";
    /** version の返答: 端末の API レベル。 */
    public static final String KEY_SDK_INT = "sdk_int";
    /** 試験イベントの文言。 */
    public static final String KEY_MESSAGE = "message";
    /** 接続の知らせ: 接続にかかったミリ秒。 */
    public static final String KEY_CONNECT_MS = "connect_ms";
    /** 知らせ・返答: 記録の知らせ（Binder）を送れたか。 */
    public static final String KEY_DOORBELL = "doorbell";
    /** paths の返答: 端末保護ストレージの files の絶対パス（createDeviceProtectedStorageContext().getFilesDir()）。 */
    public static final String KEY_DEVICE_PROTECTED_FILES_DIR = "device_protected_files_dir";
    /** paths の返答: 目覚ましの音源の書き出し先の絶対パス（…/files/seed_platform/sounds。返す前にフォルダを作る）。 */
    public static final String KEY_SOUNDS_DIR = "sounds_dir";

    // ── :seed_platform の置き場（端末保護ストレージの files の下。再起動の後・最初のロック解除の前でも読み書きできる）──

    /** :seed_platform が使うフォルダの名前（files/seed_platform）。予約の控え・記録・音源を置く。 */
    public static final String STORAGE_DIR_NAME = "seed_platform";
    /** 目覚ましの音源のフォルダの名前（files/seed_platform/sounds。メインプロセスが「内容のハッシュ.拡張子」で書き出す）。 */
    public static final String SOUNDS_DIR_NAME = "sounds";

    // ── 目覚まし（モジュール "alarm"。W1-3。機能 alarm を入れた APK だけで使える）──

    /** 目覚ましのモジュール。 */
    public static final String MODULE_ALARM = "alarm";
    /** 予約する（同じ ID は置き換え。控えに書き、AlarmManager.setAlarmClock で張る）。 */
    public static final String METHOD_ALARM_SCHEDULE = "schedule";
    /** 予約を 1 つ取り消す（無い ID でも成功。冪等）。 */
    public static final String METHOD_ALARM_CANCEL = "cancel";
    /** 予約を全部取り消す。 */
    public static final String METHOD_ALARM_CANCEL_ALL = "cancel_all";
    /** 控えの一覧（予定時刻の順）。 */
    public static final String METHOD_ALARM_LIST = "list";
    /** 正確なアラームを張れるか（Android 12 系の特別なアクセス。11 以前は常に true）。 */
    public static final String METHOD_ALARM_CAN_SCHEDULE_EXACT = "can_schedule_exact";

    /** 予約・控え: 予約の ID（アプリが決める。同じ ID は置き換え）。 */
    public static final String KEY_ALARM_ID = "id";
    /** 予約・控え: 鳴らす時刻（UTC の epoch ミリ秒。壁時計の計算はアプリがする）。 */
    public static final String KEY_ALARM_TRIGGER_AT_UTC_MS = "trigger_at_utc_ms";
    /** 予約の引数: スクリプトが渡した音源（"assets://…" か端末のファイルの絶対パス）。メインプロセスが sound_path に置き換えて送る。 */
    public static final String KEY_ALARM_SOUND_ASSET = "sound_asset";
    /** 予約・控え: 音源の端末のファイルの絶対パス（空なら既定の音）。 */
    public static final String KEY_ALARM_SOUND_PATH = "sound_path";
    /** 予約・控え: バイブするか。 */
    public static final String KEY_ALARM_VIBRATE = "vibrate";
    /** 予約・控え: 鳴っている間のアラームの音量（0..1。負 = 触らない）。 */
    public static final String KEY_ALARM_FORCE_VOLUME = "force_volume";
    /** 予約・控え: 利用者が音量を下げても戻すか。 */
    public static final String KEY_ALARM_KEEP_VOLUME = "keep_volume";
    /** 予約・控え: 音量の漸増の秒（0 = 最初から）。 */
    public static final String KEY_ALARM_FADE_IN_SECONDS = "fade_in_seconds";
    /** 予約・控え: 鳴り続ける上限の分（安全弁）。 */
    public static final String KEY_ALARM_MAX_RING_MINUTES = "max_ring_minutes";
    /** 予約・控え: 鳴動の通知の題。 */
    public static final String KEY_ALARM_TITLE = "title";
    /** 予約・控え: 鳴動の通知の本文。 */
    public static final String KEY_ALARM_BODY = "body";
    /** 予約・控え・イベント: アプリの任意の JSON（文字列のまま返す）。 */
    public static final String KEY_ALARM_PAYLOAD_JSON = "payload_json";
    /** 控え: 予約を受け付けた時刻（UTC の epoch ミリ秒。診断用。イベントの scheduled_at_utc_ms〈予定時刻〉とは別物）。 */
    public static final String KEY_ALARM_CREATED_AT_UTC_MS = "created_at_utc_ms";
    /** list の返答: 控えの配列。 */
    public static final String KEY_ALARMS = "alarms";
    /** schedule の返答: 同じ ID の予約を置き換えたか。 */
    public static final String KEY_ALARM_REPLACED = "replaced";
    /** cancel の返答: その ID の予約があったか。 */
    public static final String KEY_ALARM_EXISTED = "existed";
    /** cancel_all の返答・alarms.rescheduled: 件数。 */
    public static final String KEY_COUNT = "count";
    /** can_schedule_exact の返答: 正確なアラームを張れるか。 */
    public static final String KEY_CAN_SCHEDULE_EXACT = "can_schedule_exact";
    /** イベント: 鳴るはずだった時刻（予約の trigger_at_utc_ms。UTC の epoch ミリ秒）。 */
    public static final String KEY_ALARM_SCHEDULED_AT_UTC_MS = "scheduled_at_utc_ms";
    /** イベント alarm.fired: 発火を受けた時刻（UTC の epoch ミリ秒）。 */
    public static final String KEY_ALARM_FIRED_AT_UTC_MS = "fired_at_utc_ms";
    /** イベント: 理由（下の MISSED_REASON_* / RESCHEDULE_REASON_*）。 */
    public static final String KEY_REASON = "reason";
    /** イベント alarms.rescheduled: 鳴らさずに「鳴らなかった」と記録した件数。 */
    public static final String KEY_MISSED = "missed";
    /** イベント alarms.rescheduled: 張り直せなかった件数（正確なアラームの許可が無い等。控えには残す）。 */
    public static final String KEY_FAILED = "failed";

    /** 鳴動中の目覚まし（W1-4a）: 今鳴っている予約を返す（無ければ ringing が null）。 */
    public static final String METHOD_ALARM_GET_RINGING = "get_ringing";
    /** 鳴動を止める（W1-4a）: 引数の id が空・無しなら今鳴っているもの。待ち行列の予約も id で止められる。 */
    public static final String METHOD_ALARM_STOP_RINGING = "stop_ringing";
    /** get_ringing の返答: 鳴動中の予約（{id, scheduled_at_utc_ms, started_at_utc_ms, payload_json}。無ければ null）。 */
    public static final String KEY_ALARM_RINGING = "ringing";
    /** get_ringing の返答: 鳴り始めた時刻（UTC の epoch ミリ秒。待ち行列から繰り上がったときはその時刻）。 */
    public static final String KEY_ALARM_STARTED_AT_UTC_MS = "started_at_utc_ms";
    /** イベント alarm.queued: 今鳴っていて、止まるのを待っている予約の ID。 */
    public static final String KEY_ALARM_WAITING_FOR = "waiting_for";
    /** stop_ringing の返答: 何かを止めたか（鳴っていなければ false。それでも ok=true＝冪等）。 */
    public static final String KEY_ALARM_STOPPED = "stopped";

    /** 目覚ましが鳴った（予定時刻に AlarmManager から配信され、鳴動のサービスへ渡した。鳴り始めたか待ち行列に入った）。 */
    public static final String EVENT_ALARM_FIRED = "platform.alarm.fired";
    /** 鳴動が終わった（W1-4a。reason = stopped〈StopRinging〉/ timeout〈安全弁〉/ error〈前景にできない等〉）。 */
    public static final String EVENT_ALARM_RING_STOPPED = "platform.alarm.ring_stopped";
    /** 別の予約の鳴動中に時刻が来たので待たせた（W1-4a。捨てずに、今の鳴動が止まったら続けて鳴らす）。 */
    public static final String EVENT_ALARM_QUEUED = "platform.alarm.queued";
    /** 目覚ましが鳴らなかった（電源断・強制停止・許可の取り消しの間に予定時刻を過ぎた。張り直すときに見つける）。 */
    public static final String EVENT_ALARM_MISSED = "platform.alarm.missed";
    /** 予約を張り直した（再起動・時刻／タイムゾーンの変更・アプリの更新・正確なアラームの許可）。控えが空なら記録しない。 */
    public static final String EVENT_ALARMS_RESCHEDULED = "platform.alarms.rescheduled";

    /** alarm.missed の理由: 端末の電源断・強制停止・更新などで、予約が OS から消えていた間に予定時刻を過ぎた。 */
    public static final String MISSED_REASON_DEVICE_OFF = "device_off";
    /** alarm.missed の理由: 正確なアラームの許可が取り消されていた間に予定時刻を過ぎた。 */
    public static final String MISSED_REASON_PERMISSION_REVOKED = "permission_revoked";
    /**
     * alarm.missed の理由: 配信は届いたが、鳴動の前景サービスを起こせなかった（W1-4a。ForegroundServiceStartNotAllowedException など。
     * このときは alarm.fired の代わりにこれを記録する）。
     */
    public static final String MISSED_REASON_START_FAILED = "start_failed";
    /** alarm.ring_stopped の理由: アプリが止めた（alarm.stop_ringing）。 */
    public static final String RING_STOP_REASON_STOPPED = "stopped";
    /** alarm.ring_stopped の理由: 安全弁（max_ring_minutes）で自動で止めた。 */
    public static final String RING_STOP_REASON_TIMEOUT = "timeout";
    /** alarm.ring_stopped の理由: 鳴らし続けられなかった（前景にできない・サービスが作り直された等）。 */
    public static final String RING_STOP_REASON_ERROR = "error";
    /** alarms.rescheduled の理由: 再起動（LOCKED_BOOT_COMPLETED / BOOT_COMPLETED。強制停止からの復帰でも届く）。 */
    public static final String RESCHEDULE_REASON_BOOT = "boot";
    /** alarms.rescheduled の理由: 端末の時刻・タイムゾーンが変わった（アプリは次の時刻を計算し直す）。 */
    public static final String RESCHEDULE_REASON_TIME_CHANGED = "time_changed";
    /** alarms.rescheduled の理由: アプリが更新された（MY_PACKAGE_REPLACED）。 */
    public static final String RESCHEDULE_REASON_PACKAGE_REPLACED = "package_replaced";
    /** alarms.rescheduled の理由: 正確なアラームの特別なアクセスが許可された。 */
    public static final String RESCHEDULE_REASON_PERMISSION_CHANGED = "permission_changed";

    /** 予約の ID の最大の長さ（Unicode の符号位置の数）。 */
    public static final int MAX_ALARM_ID_LENGTH = 128;
    /** title・body の最大の長さ（Unicode の符号位置の数）。 */
    public static final int MAX_ALARM_TEXT_LENGTH = 4096;
    /** payload_json の最大の長さ（Unicode の符号位置の数。控えと Binder の返答を膨らませないため）。 */
    public static final int MAX_ALARM_PAYLOAD_LENGTH = 16384;
    /** 控えに持てる予約の数の上限（新しい ID の予約だけを断る。置き換えは通す）。 */
    public static final int MAX_SCHEDULED_ALARMS = 64;
    /** vibrate の既定値。 */
    public static final boolean DEFAULT_ALARM_VIBRATE = true;
    /** force_volume の「音量に触らない」の値（負の値はすべてこれにそろえる）。 */
    public static final double ALARM_VOLUME_UNCHANGED = -1.0;
    /** force_volume の上限。 */
    public static final double MAX_ALARM_FORCE_VOLUME = 1.0;
    /** keep_volume の既定値。 */
    public static final boolean DEFAULT_ALARM_KEEP_VOLUME = false;
    /** fade_in_seconds の既定値。 */
    public static final double DEFAULT_ALARM_FADE_IN_SECONDS = 5.0;
    /** max_ring_minutes の既定値（安全弁）。 */
    public static final int DEFAULT_ALARM_MAX_RING_MINUTES = 60;
    /** max_ring_minutes の下限（これより小さい値はこれにそろえる）。 */
    public static final int MIN_ALARM_MAX_RING_MINUTES = 1;
    /** 1 分のミリ秒（安全弁の max_ring_minutes をミリ秒にする）。 */
    public static final long MILLIS_PER_MINUTE = 60_000L;

    // ── 起動理由（W1-4a。メインプロセスで答える命令 platform.launch_reason とイベント platform.launch）──

    /**
     * 信頼できる起動の入口（exported=false の activity-alias。main の AndroidManifest.xml に常設。targetActivity は MainActivity）。
     * :seed_platform が作る Activity 行きの PendingIntent はすべてこの別名を通す（W1-P6）。他のアプリはこの部品を起動できないので、
     * 起動の Intent の部品名がこれなら、中の起動理由（EXTRA_LAUNCH）はプラットフォーム層が自分で作ったものと信用できる。
     */
    public static final String PLATFORM_ENTRY_ALIAS = "com.seedengine.runtime.platform.PlatformEntry";
    /**
     * PlatformEntry 行きの Intent の extra: 起動理由の JSON（文字列。{kind, id, action_id, scheduled_at_utc_ms, fired_at_utc_ms,
     * payload_json}）。名前を "seed." で始めないのは、デバッグ版の MainActivity が "seed." の extra を起動オプションとして
     * ネイティブへ丸ごと渡す（forwardLaunchOptions）ため。
     */
    public static final String EXTRA_LAUNCH = "com.seedengine.runtime.platform.extra.LAUNCH";
    /** この起動の理由を返す（メインプロセスで答える。IPC なし）。返答 { launch: {kind, …} }。 */
    public static final String METHOD_LAUNCH_REASON = "launch_reason";
    /** 起動した後に届いた Intent（singleTask の onNewIntent）の理由。data は起動理由の JSON と同じ形。 */
    public static final String EVENT_LAUNCH = "platform.launch";
    /** launch_reason の返答: 起動理由のオブジェクト。 */
    public static final String KEY_LAUNCH = "launch";
    /** 起動理由: 種類（下の LAUNCH_KIND_*）。 */
    public static final String KEY_LAUNCH_KIND = "kind";
    /** 起動理由: 通知の操作の ID（notification_action のとき。例 open）。 */
    public static final String KEY_LAUNCH_ACTION_ID = "action_id";
    /** 起動の種類: ランチャー・普通の起動（プラットフォーム層を通らない起動はすべてこれか other）。 */
    public static final String LAUNCH_KIND_LAUNCHER = "launcher";
    /** 起動の種類: 目覚ましの鳴動（フルスクリーン通知・鳴動の通知の本文のタップ）。ロック画面の上に出し画面を点ける。 */
    public static final String LAUNCH_KIND_ALARM = "alarm";
    /** 起動の種類: 通知の本文のタップ（W1-5 の通知。W1-4a では作らない）。 */
    public static final String LAUNCH_KIND_NOTIFICATION_TAP = "notification_tap";
    /** 起動の種類: 通知の操作（ボタン）。action_id に操作の ID。 */
    public static final String LAUNCH_KIND_NOTIFICATION_ACTION = "notification_action";
    /** 起動の種類: ステータスバー・ロック画面の「次の目覚まし」の表示を押した（AlarmClockInfo の showIntent）。 */
    public static final String LAUNCH_KIND_ALARM_CLOCK_INFO = "alarm_clock_info";
    /** 起動の種類: それ以外（ランチャー以外の action で起動された・プラットフォーム層の Intent が読めなかった）。 */
    public static final String LAUNCH_KIND_OTHER = "other";
    /** 通知の操作の ID: 鳴動の通知の「開く」。 */
    public static final String LAUNCH_ACTION_OPEN = "open";

    // ── 画面（W1-4a。モジュール "window"。メインプロセスで答える。IPC なし）──

    /** 画面のモジュール（メインプロセスの Activity を操作する）。 */
    public static final String MODULE_WINDOW = "window";
    /** ロック画面の上に出す＋画面を点ける（setShowWhenLocked・setTurnScreenOn）の切り替え。引数 { on }。 */
    public static final String METHOD_WINDOW_SET_SHOW_WHEN_LOCKED = "set_show_when_locked";
    /** set_show_when_locked の引数・返答: 上げるか。 */
    public static final String KEY_WINDOW_ON = "on";

    // ── 鳴動の通知（W1-4a。:seed_platform の RingService）──

    /** 鳴動の通知チャネルの ID（重要度 HIGH・チャネルの音なし＝音は RingService が USAGE_ALARM で鳴らす）。 */
    public static final String NOTIFICATION_CHANNEL_ALARM = "seed_platform_alarm";

    // ── イベントの名前（スクリプトの SEED.Events にもこの名前で流れる）──

    /** 名前の接頭辞。 */
    public static final String EVENT_PREFIX = "platform.";
    /** 試験イベント（platform.emit_test_event）。 */
    public static final String EVENT_TEST = "platform.test_event";
    /** :seed_platform へつながった（最初の呼び出しが背面で始めた接続が済んだ。以降の呼び出しは同期で通る）。 */
    public static final String EVENT_CONNECTED = "platform.connected";
    /** :seed_platform へつなげなかった（data.error に理由）。 */
    public static final String EVENT_CONNECT_FAILED = "platform.connect_failed";
    /** :seed_platform のプロセスが居なくなった（次の呼び出しでつなぎ直す）。 */
    public static final String EVENT_DISCONNECTED = "platform.disconnected";
    /** メインプロセスの中で作った知らせの seq（記録を通らないので番号を持たない）。Rust の wire::LOCAL_EVENT_SEQ と一致させる。 */
    public static final long LOCAL_EVENT_SEQ = 0;

    // ── 失敗の理由の名前（Rust の wire::ERROR_* と重なるものは一致させる。C# の Platform.LastError に入る）──

    /** 知らないモジュール・メソッド。 */
    public static final String ERROR_UNKNOWN_METHOD = "unknown_method";
    /** module / method の名前が規則（isValidName）に合わない。 */
    public static final String ERROR_INVALID_NAME = "invalid_name";
    /** 引数の JSON が読めない。 */
    public static final String ERROR_INVALID_JSON = "invalid_json";
    /** :seed_platform へつなぎ始めた・つないでいる途中（プロセスの起動を描画のスレッドで待たないため。platform.connected を待って呼び直す）。 */
    public static final String ERROR_CONNECTING = "connecting";
    /** プロバイダが見つからない（マニフェストに無い等）・つなげなかった。 */
    public static final String ERROR_PROVIDER_UNAVAILABLE = "provider_unavailable";
    /** :seed_platform のプロセスが居なくなった。 */
    public static final String ERROR_PROVIDER_DIED = "provider_died";
    /** Binder の呼び出しの失敗（プロセスの死以外）。 */
    public static final String ERROR_REMOTE = "remote_exception";
    /** 同期でつなぐのを待ちきれなかった（デバッグの受信機など、待ってよい呼び出し元だけ）。 */
    public static final String ERROR_CONNECT_TIMEOUT = "connect_timeout";
    /** 返答が空だった。 */
    public static final String ERROR_NO_REPLY = "no_reply";
    /** Java の中の想定外の例外。 */
    public static final String ERROR_INTERNAL = "internal_error";
    /** SeedPlatform.init が呼ばれていない。 */
    public static final String ERROR_NOT_INITIALIZED = "not_initialized";
    /** 同じアプリ以外からの呼び出し。 */
    public static final String ERROR_FORBIDDEN = "forbidden";
    /** register_callback に Binder が無い。 */
    public static final String ERROR_MISSING_CALLBACK = "missing_callback";
    /** 正確なアラームを張れない（Android 12 系で SCHEDULE_EXACT_ALARM が許可されていない）。黙って不正確な予約に落とさない。 */
    public static final String ERROR_EXACT_ALARM_NOT_ALLOWED = "exact_alarm_not_allowed";
    /** 引数の値が約束に合わない（detail にどの欄か）。 */
    public static final String ERROR_INVALID_ARGUMENT = "invalid_argument";
    /** 予約の数が上限（MAX_SCHEDULED_ALARMS）に達している。 */
    public static final String ERROR_TOO_MANY_ALARMS = "too_many_alarms";
    /** 予約の控えを書けなかった（空き容量など。予約は張らない）。 */
    public static final String ERROR_STORE_WRITE_FAILED = "store_write_failed";
    /** AlarmManager が予約を受け付けなかった（SecurityException・上限など。detail に例外）。 */
    public static final String ERROR_SCHEDULE_FAILED = "schedule_failed";
    /** APK に機能 alarm が入っていない（project_settings.json の android.features に "alarm" が無い）。 */
    public static final String ERROR_FEATURE_NOT_ENABLED = "feature_not_enabled";
    /** 画面の命令: 操作する Activity が無い（破棄された・まだ作られていない）。 */
    public static final String ERROR_NO_ACTIVITY = "no_activity";

    /** module / method の名前の最大の長さ（文字）。Rust の wire::MAX_NAME_LEN と一致させる。 */
    public static final int MAX_NAME_LENGTH = 64;

    /**
     * プロバイダの authority（applicationId＋接尾辞）を作る。
     *
     * @param context どのプロセスの Context でもよい（パッケージ名 = applicationId）
     * @return authority
     */
    public static String authority(Context context) {
        return context.getPackageName() + AUTHORITY_SUFFIX;
    }

    /**
     * ContentProvider の call に渡す method（module＋区切り＋method）を作る。
     *
     * @param module モジュールの名前（isValidName を満たすこと）
     * @param method メソッドの名前（isValidName を満たすこと）
     * @return 例 "platform.ping"
     */
    public static String providerMethod(String module, String method) {
        return module + METHOD_SEPARATOR + method;
    }

    /**
     * module / method の名前が規則どおりか（1〜MAX_NAME_LENGTH 文字の小文字英数字と _）。
     * 区切りの . を含む名前を通すと module と method の境目が分からなくなるので、呼び出しの両端で確かめる。
     *
     * @param name 調べる名前（null 可）
     * @return 規則どおりなら true
     */
    public static boolean isValidName(String name) {
        if (name == null || name.isEmpty() || name.length() > MAX_NAME_LENGTH) {
            return false;
        }
        for (int i = 0; i < name.length(); i++) {
            char c = name.charAt(i);
            boolean allowed = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
            if (!allowed) {
                return false;
            }
        }
        return true;
    }
}
