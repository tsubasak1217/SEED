package com.seedengine.platformspike;

import android.net.Uri;

/**
 * スパイク全体で共有する名前と既定値の置き場（マジックナンバーを散らさない）。
 *
 * <p>adb の操作（scripts/*.sh）はここのアクション名・extra 名と一致させる。</p>
 */
final class SpikeContract {
    private SpikeContract() {}

    /** applicationId（= Java のパッケージ名）。 */
    static final String PACKAGE = "com.seedengine.platformspike";

    // ---- adb から叩くデバッグ用の操作（DebugControlReceiver。メインプロセス＝エンジンの代役） ----
    static final String ACTION_SCHEDULE = PACKAGE + ".SCHEDULE";
    static final String ACTION_CANCEL = PACKAGE + ".CANCEL";
    static final String ACTION_CANCEL_ALL = PACKAGE + ".CANCEL_ALL";
    static final String ACTION_STOP = PACKAGE + ".STOP";
    static final String ACTION_STATUS = PACKAGE + ".STATUS";
    static final String ACTION_REARM = PACKAGE + ".REARM";
    static final String ACTION_IPC_BENCH = PACKAGE + ".IPC_BENCH";
    /** DebugPlatformReceiver（:seed_platform）。予約を経ずに前景サービスを起こす（背面からの起動制限の確認）。 */
    static final String ACTION_RING_NOW = PACKAGE + ".RING_NOW";

    // ---- 内部の Intent ----
    static final String ACTION_ALARM_FIRE = PACKAGE + ".ALARM_FIRE";
    static final String ACTION_RING_START = PACKAGE + ".RING_START";
    static final String ACTION_RING_STOP = PACKAGE + ".RING_STOP";
    /** :seed_platform → メインプロセスへの知らせ（自パッケージ宛てのブロードキャスト）。鳴動が止まった。 */
    static final String ACTION_RING_STOPPED = PACKAGE + ".RING_STOPPED";
    /** :seed_platform → メインプロセスへの知らせの遅延の計測用。 */
    static final String ACTION_BENCH_PONG = PACKAGE + ".BENCH_PONG";

    // ---- extra / Bundle のキー ----
    static final String EXTRA_SECONDS = "seconds";
    static final String EXTRA_ID = "id";
    /** 予定時刻（UTC の epoch ミリ秒）。 */
    static final String EXTRA_TRIGGER_AT = "trigger_at";
    static final String EXTRA_FGS_TYPE = "fgs_type";
    static final String EXTRA_MAX_RING_SECONDS = "max_ring_s";
    /** 鳴動を始めた時刻（UTC の epoch ミリ秒）。鳴動画面の経過秒数の起点。 */
    static final String EXTRA_RING_START_WALL = "ring_start_wall";
    static final String EXTRA_REASON = "reason";
    /** 送信側の SystemClock.elapsedRealtimeNanos()（端末全体で共通の時計なのでプロセス間で比べられる）。 */
    static final String EXTRA_T_SEND_NS = "t_send_ns";
    static final String EXTRA_JSON = "json";

    // ---- 既定値 ----
    static final String DEFAULT_ALARM_ID = "spike";
    static final int DEFAULT_SECONDS = 90;
    /** 安全弁（スパイクは音を短くするため 60 秒。本番の既定は 60 分）。 */
    static final int DEFAULT_MAX_RING_SECONDS = 60;
    static final String FGS_TYPE_MEDIA_PLAYBACK = "mediaPlayback";
    static final String FGS_TYPE_SYSTEM_EXEMPTED = "systemExempted";
    static final String FGS_TYPE_SPECIAL_USE = "specialUse";

    // ---- ContentProvider（:seed_platform）の命令 ----
    static final String AUTHORITY = PACKAGE + ".platform";
    static final Uri PROVIDER_URI = Uri.parse("content://" + AUTHORITY);
    static final String M_PING = "ping";
    static final String M_INVOKE = "invoke";
    static final String M_ALARM_SCHEDULE = "alarm.schedule";
    static final String M_ALARM_CANCEL = "alarm.cancel";
    static final String M_ALARM_CANCEL_ALL = "alarm.cancel_all";
    static final String M_ALARM_REARM_ALL = "alarm.rearm_all";
    static final String M_ALARM_LIST = "alarm.list";
    static final String M_RING_STOP = "ring.stop";
    static final String M_RING_STATUS = "ring.status";
    static final String M_PERM_STATUS = "perm.status";
    static final String M_BENCH_BROADCAST_BACK = "bench.broadcast_back";
    /** 戻り値の Bundle のキー。 */
    static final String R_OK = "ok";
    static final String R_JSON = "json";
    static final String R_ERROR = "error";
    static final String R_PID = "pid";

    // ---- 通知 ----
    static final String CHANNEL_RING = "ring";
    static final int NOTIFICATION_ID_RING = 4101;

    /** 鳴動画面の信頼できる入口（activity-alias。exported=false）。 */
    static final String RING_ENTRY_ALIAS = PACKAGE + ".RingEntry";

    // ---- 計測 ----
    static final int BENCH_ITERATIONS = 10;
    static final int BENCH_PAYLOAD_BYTES = 256;
    static final long BENCH_TIMEOUT_MS = 5000;
    /** 鳴動中の生存ログの間隔（最近のタスクから消した後も鳴っているかの証拠）。 */
    static final long RING_HEARTBEAT_MS = 5000;
    /** WakeLock は安全弁より少し長く持つ（取り忘れの保険として時間切れ付きで取る）。 */
    static final long WAKE_LOCK_MARGIN_MS = 10_000;
    static final long MS_PER_SECOND = 1000;
    static final long NS_PER_US = 1000;
    static final long UI_TICK_MS = 1000;
}
