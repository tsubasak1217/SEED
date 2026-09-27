// ============================================================
//  DebugPlatformReceiver.java — デバッグ版だけの adb の入口（プラットフォーム機能の確かめ。W1-1・W1-4b で目覚ましの命令を追加）
//
//  デバッグ版の APK（app/src/debug/）にだけ入る、exported の受信機（メインプロセス）。adb から命じて、
//  メインプロセス → :seed_platform の往復と、:seed_platform → エンジンのイベントの経路を確かめる。
//  送り手に android.permission.DUMP を求める（W1-7。app/src/debug/AndroidManifest.xml。adb の shell は持つが普通のアプリは持てないので、
//  端末に入った他のアプリからは予約・停止を送れない）。
//  暗黙の放送はマニフェストの受信機に届かないので、必ず -n で部品を指定する（docs/app_platform_roadmap.md §2.6）:
//
//    adb shell am broadcast -n <applicationId>/com.seedengine.runtime.platform.DebugPlatformReceiver \
//        -a com.seedengine.runtime.platform.PING [--ei count 5]
//    adb shell am broadcast -n <applicationId>/com.seedengine.runtime.platform.DebugPlatformReceiver \
//        -a com.seedengine.runtime.platform.EMIT_TEST_EVENT [--es message hello]
//
//  目覚ましの命令（W1-4b の実機の計測。スクリプト無しで予約・停止するため。:seed_platform の alarm モジュールをそのまま呼ぶ）:
//
//    -a com.seedengine.runtime.platform.SCHEDULE [--ei seconds 90] [--es id t1] [--ei max_ring_minutes 1]
//                                                [--es title 題] [--es body 本文] [--ef fade_in_seconds 1.0] [--ez vibrate true]
//                                                [--ef force_volume 0.0] [--ez keep_volume false]
//        … 今から seconds 秒後（既定 60 秒）に予約する（alarm.schedule）。安全弁の既定はこの受信機では 1 分（本番の既定 60 分ではない。
//          試験で鳴らしっぱなしにしないため）。題・本文・漸増・振動・音量は渡したときだけ入れる（無ければ本番の既定値）。
//          音源は渡さない（既定の音。音源の書き出しはエンジンの仕事で、この受信機はエンジンを通らない）。
//          私物の端末で音を小さくするなら --ef force_volume 0.0 --ez vibrate false --ef fade_in_seconds 60（最小の段階・漸増 60 秒。
//          止めれば音量は元へ戻る。W1-7 から :seed_platform ごと殺されても見張りで戻した鳴動が止めたときに戻す。強制停止では
//          背面から戻せず〈Android 17 の AudioHardening〉、アプリを開くか次の鳴動で戻る）。
//          title・body に空白を入れない（adb shell が 1 行の命令にして端末のシェルが空白で分けるため、後ろが切れる）
//    -a com.seedengine.runtime.platform.CANCEL_ALL                    … 予約を全部取り消す（alarm.cancel_all）
//    -a com.seedengine.runtime.platform.STOP_RINGING [--es id t1]     … 鳴動を止める（alarm.stop_ringing。id が無ければ今鳴っているもの）
//    -a com.seedengine.runtime.platform.GET_RINGING                   … 鳴動中の予約（alarm.get_ringing）
//    -a com.seedengine.runtime.platform.LIST                          … 控えの一覧（alarm.list）
//
//  結果は logcat のタグ SEEDPlatform（`adb logcat -s SEED SEEDPlatform`）。目覚ましの命令は 1 行
//  「[debug] alarm.<命令>（温|冷: 接続込み） rtt_us=… request=<JSON> reply=<JSON>」で出す（計測のスクリプトが reply の
//  trigger_at_utc_ms などを読む）。試験イベントはエンジンが動いていればスクリプトへ platform.test_event として届く（エンジンが居なければ捨てる）。
//  受信は UI スレッドなので goAsync で専用のスレッドへ移し、そこで接続を待ってよい（描画のスレッドではない）。
//
//  【注意】この受信機は directBootAware ではない（メインプロセス）。再起動の後、最初のロック解除の前は届かない
//  （そのときに鳴っている鳴動は安全弁〈max_ring_minutes〉で止まる。W1-7 の T4 では解除まで adb も unauthorized だった）。
//  エンジンが居ないメインプロセス（この受信機の放送だけで起きたプロセス）でも接続は呼び鈴を登録するので、その間に
//  取り出した :seed_platform の記録（alarm.fired・alarm.ring_stopped など）は既読になって捨てられる（SeedPlatform.deliverEvent。
//  受け取りの確認〈ack〉が無い間の割り切り。docs/backlog.md）。スクリプトで記録を受ける確かめと混ぜないこと。
// ============================================================

package com.seedengine.runtime.platform;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.os.SystemClock;
import android.util.Log;

import org.json.JSONException;
import org.json.JSONObject;

/**
 * デバッグ版だけの adb の入口（本番のマニフェストには入らない）。
 */
public final class DebugPlatformReceiver extends BroadcastReceiver {

    /** ping を count 回（既定 3 回）送り、往復の時間を出す。続けて version を 1 回。 */
    public static final String ACTION_PING = "com.seedengine.runtime.platform.PING";

    /** 試験イベントを 1 つ流す（message の文言で）。 */
    public static final String ACTION_EMIT_TEST_EVENT = "com.seedengine.runtime.platform.EMIT_TEST_EVENT";

    /** 目覚ましを seconds 秒後に予約する（alarm.schedule。W1-4b）。 */
    public static final String ACTION_SCHEDULE = "com.seedengine.runtime.platform.SCHEDULE";

    /** 予約を全部取り消す（alarm.cancel_all。W1-4b）。 */
    public static final String ACTION_CANCEL_ALL = "com.seedengine.runtime.platform.CANCEL_ALL";

    /** 鳴動を止める（alarm.stop_ringing。W1-4b）。 */
    public static final String ACTION_STOP_RINGING = "com.seedengine.runtime.platform.STOP_RINGING";

    /** 鳴動中の予約を出す（alarm.get_ringing。W1-4b）。 */
    public static final String ACTION_GET_RINGING = "com.seedengine.runtime.platform.GET_RINGING";

    /** 控えの一覧を出す（alarm.list。W1-4b）。 */
    public static final String ACTION_LIST = "com.seedengine.runtime.platform.LIST";

    /** extra: ping の回数。 */
    private static final String EXTRA_COUNT = "count";

    /** extra: 試験イベントの文言。 */
    private static final String EXTRA_MESSAGE = "message";

    /** extra: 予約までの秒（--ei。今からの相対。予定時刻はこの受信機が受けた時刻＋これ）。 */
    private static final String EXTRA_SECONDS = "seconds";

    /** ping の既定の回数。 */
    private static final int DEFAULT_PING_COUNT = 3;

    /** ping の回数の上限（誤って大きな数を渡したときの蓋）。 */
    private static final int MAX_PING_COUNT = 100;

    /** 試験イベントの既定の文言。 */
    private static final String DEFAULT_MESSAGE = "adb";

    /** 予約までの既定の秒。 */
    private static final int DEFAULT_SCHEDULE_SECONDS = 60;

    /** 予約までの秒の下限（0 以下は過去の時刻＝すぐ鳴るので、少なくともこれだけ先にする）。 */
    private static final int MIN_SCHEDULE_SECONDS = 1;

    /** 予約までの秒の上限（1 日。誤って大きな数を渡したときの蓋）。 */
    private static final int MAX_SCHEDULE_SECONDS = 86_400;

    /** 予約の既定の ID。 */
    private static final String DEFAULT_ALARM_ID = "debug";

    /** この受信機で予約するときの安全弁の既定（分）。試験で鳴らしっぱなしにしないため、本番の既定（60 分）より短い最小の値。 */
    private static final int DEFAULT_DEBUG_MAX_RING_MINUTES = PlatformContract.MIN_ALARM_MAX_RING_MINUTES;

    /** 1 秒のミリ秒。 */
    private static final long MILLIS_PER_SECOND = 1_000L;

    /** ping の nonce の接頭辞（返ってきた echo と突き合わせる）。 */
    private static final String NONCE_PREFIX = "adb-";

    /** ping の引数の nonce のキー（C# の PlatformDiagnostics.Ping と同じ）。 */
    private static final String KEY_NONCE = "nonce";

    /** 専用のスレッドの名前。 */
    private static final String THREAD_NAME = "SEEDPlatformDebug";

    /** ナノ秒 → マイクロ秒。 */
    private static final long NANOS_PER_MICRO = 1_000L;

    /** 1 つの命令（専用のスレッドで、接続を受け取って行う）。 */
    private interface Command {
        /**
         * 命令を行い、結果を logcat へ出す。
         *
         * @param connection :seed_platform への接続（待ってよい）
         */
        void run(PlatformConnection connection);
    }

    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent.getAction();
        // extras は受けたこの場で読む（予定時刻の起点を「受けた時刻」にするため。専用のスレッドの起動の遅れを入れない）
        Command command = commandFor(action, intent);
        if (command == null) {
            Log.w(PlatformContract.LOG_TAG, "知らない命令です: " + action);
            return;
        }
        PendingResult pending = goAsync();
        PlatformConnection connection = SeedPlatform.connection(context);
        new Thread(() -> {
            try {
                command.run(connection);
            } catch (RuntimeException e) {
                Log.e(PlatformContract.LOG_TAG, "デバッグの命令の途中で例外", e);
            } finally {
                pending.finish();
            }
        }, THREAD_NAME).start();
    }

    /**
     * action に当たる命令を作る（引数は Intent の extras からここで読む）。
     *
     * @param action 放送の action
     * @param intent 放送の Intent
     * @return 命令。知らない action なら null
     */
    private static Command commandFor(String action, Intent intent) {
        if (action == null) {
            return null;
        }
        switch (action) {
            case ACTION_PING: {
                int count = Math.max(1, Math.min(MAX_PING_COUNT, intent.getIntExtra(EXTRA_COUNT, DEFAULT_PING_COUNT)));
                return connection -> runPings(connection, count);
            }
            case ACTION_EMIT_TEST_EVENT: {
                String message = intent.hasExtra(EXTRA_MESSAGE) ? intent.getStringExtra(EXTRA_MESSAGE) : DEFAULT_MESSAGE;
                return connection -> emitTestEvent(connection, message);
            }
            case ACTION_SCHEDULE: {
                JSONObject request = scheduleRequest(intent, System.currentTimeMillis());
                return connection -> invokeAlarm(connection, PlatformContract.METHOD_ALARM_SCHEDULE, request);
            }
            case ACTION_STOP_RINGING: {
                // id が無ければ空の引数（＝今鳴っているもの）
                JSONObject request = new JSONObject();
                if (intent.hasExtra(PlatformContract.KEY_ALARM_ID)) {
                    PlatformJson.put(request, PlatformContract.KEY_ALARM_ID, intent.getStringExtra(PlatformContract.KEY_ALARM_ID));
                }
                return connection -> invokeAlarm(connection, PlatformContract.METHOD_ALARM_STOP_RINGING, request);
            }
            case ACTION_CANCEL_ALL:
                return connection -> invokeAlarm(connection, PlatformContract.METHOD_ALARM_CANCEL_ALL, new JSONObject());
            case ACTION_GET_RINGING:
                return connection -> invokeAlarm(connection, PlatformContract.METHOD_ALARM_GET_RINGING, new JSONObject());
            case ACTION_LIST:
                return connection -> invokeAlarm(connection, PlatformContract.METHOD_ALARM_LIST, new JSONObject());
            default:
                return null;
        }
    }

    /**
     * SCHEDULE の extras から alarm.schedule の引数を作る。
     * 型は am broadcast の指定に合わせる（seconds・max_ring_minutes は --ei、fade_in_seconds は --ef、vibrate は --ez、
     * id・title・body は --es）。型が違う extra は Android が警告を出して既定値になる。
     *
     * @param intent       放送の Intent
     * @param receivedAtMs 受けた時刻（UTC の epoch ミリ秒。予定時刻の起点）
     * @return 引数
     */
    private static JSONObject scheduleRequest(Intent intent, long receivedAtMs) {
        int seconds = Math.max(MIN_SCHEDULE_SECONDS,
                Math.min(MAX_SCHEDULE_SECONDS, intent.getIntExtra(EXTRA_SECONDS, DEFAULT_SCHEDULE_SECONDS)));
        String id = intent.hasExtra(PlatformContract.KEY_ALARM_ID)
                ? intent.getStringExtra(PlatformContract.KEY_ALARM_ID) : DEFAULT_ALARM_ID;
        JSONObject request = new JSONObject();
        PlatformJson.put(request, PlatformContract.KEY_ALARM_ID, id != null ? id : DEFAULT_ALARM_ID);
        PlatformJson.put(request, PlatformContract.KEY_ALARM_TRIGGER_AT_UTC_MS, receivedAtMs + seconds * MILLIS_PER_SECOND);
        PlatformJson.put(request, PlatformContract.KEY_ALARM_MAX_RING_MINUTES,
                intent.getIntExtra(PlatformContract.KEY_ALARM_MAX_RING_MINUTES, DEFAULT_DEBUG_MAX_RING_MINUTES));
        // 渡されたときだけ入れる（無ければ :seed_platform の AlarmRequestReader が本番の既定値にする）
        putStringExtra(request, intent, PlatformContract.KEY_ALARM_TITLE);
        putStringExtra(request, intent, PlatformContract.KEY_ALARM_BODY);
        if (intent.hasExtra(PlatformContract.KEY_ALARM_FADE_IN_SECONDS)) {
            PlatformJson.put(request, PlatformContract.KEY_ALARM_FADE_IN_SECONDS,
                    (double) intent.getFloatExtra(PlatformContract.KEY_ALARM_FADE_IN_SECONDS,
                            (float) PlatformContract.DEFAULT_ALARM_FADE_IN_SECONDS));
        }
        if (intent.hasExtra(PlatformContract.KEY_ALARM_VIBRATE)) {
            PlatformJson.put(request, PlatformContract.KEY_ALARM_VIBRATE,
                    intent.getBooleanExtra(PlatformContract.KEY_ALARM_VIBRATE, PlatformContract.DEFAULT_ALARM_VIBRATE));
        }
        // 鳴動の間のアラームの音量（--ef。0.0 で端末の最小の段階〈AlarmStreamVolume が最小〜最大に丸める〉。止めたら元へ戻る）。
        // --ei で渡すと型が合わず「触らない」（-1）になり、利用者の音量のまま鳴るので、呼ぶ側は request の行で値を確かめること
        if (intent.hasExtra(PlatformContract.KEY_ALARM_FORCE_VOLUME)) {
            PlatformJson.put(request, PlatformContract.KEY_ALARM_FORCE_VOLUME,
                    (double) intent.getFloatExtra(PlatformContract.KEY_ALARM_FORCE_VOLUME,
                            (float) PlatformContract.ALARM_VOLUME_UNCHANGED));
        }
        if (intent.hasExtra(PlatformContract.KEY_ALARM_KEEP_VOLUME)) {
            PlatformJson.put(request, PlatformContract.KEY_ALARM_KEEP_VOLUME,
                    intent.getBooleanExtra(PlatformContract.KEY_ALARM_KEEP_VOLUME, PlatformContract.DEFAULT_ALARM_KEEP_VOLUME));
        }
        return request;
    }

    /**
     * 文字列の extra を、渡されていれば同じ名前のキーで引数へ入れる。
     *
     * @param request 引数
     * @param intent  放送の Intent
     * @param key     extra とキーの名前
     */
    private static void putStringExtra(JSONObject request, Intent intent, String key) {
        String value = intent.getStringExtra(key);
        if (value != null) {
            PlatformJson.put(request, key, value);
        }
    }

    /**
     * alarm モジュールの命令を 1 つ送り、往復の時間・引数・返答を 1 行で出す（計測のスクリプトが読む形）。
     *
     * @param connection 接続
     * @param method     alarm モジュールのメソッド（PlatformContract.METHOD_ALARM_*）
     * @param request    引数
     */
    private static void invokeAlarm(PlatformConnection connection, String method, JSONObject request) {
        boolean wasConnected = connection.isConnected();
        String requestText = request.toString();
        long startNs = SystemClock.elapsedRealtimeNanos();
        byte[] reply = connection.invoke(PlatformContract.MODULE_ALARM, method, PlatformJson.utf8(requestText), true);
        long elapsedUs = (SystemClock.elapsedRealtimeNanos() - startNs) / NANOS_PER_MICRO;
        Log.i(PlatformContract.LOG_TAG, "[debug] " + PlatformContract.MODULE_ALARM + "." + method
                + (wasConnected ? "（温）" : "（冷: 接続込み）") + " rtt_us=" + elapsedUs
                + " request=" + requestText + " reply=" + PlatformJson.text(reply));
    }

    /**
     * ping を count 回送り、1 回ごとに往復の時間・pid・echo の一致を出す。最後に version。
     *
     * @param connection 接続
     * @param count      回数
     */
    private static void runPings(PlatformConnection connection, int count) {
        for (int i = 1; i <= count; i++) {
            boolean wasConnected = connection.isConnected();
            String nonce = NONCE_PREFIX + i;
            JSONObject request = new JSONObject();
            PlatformJson.put(request, KEY_NONCE, nonce);
            long startNs = SystemClock.elapsedRealtimeNanos();
            byte[] reply = connection.invoke(PlatformContract.MODULE_PLATFORM, PlatformContract.METHOD_PING,
                    PlatformJson.utf8(request.toString()), true);
            long elapsedUs = (SystemClock.elapsedRealtimeNanos() - startNs) / NANOS_PER_MICRO;
            JSONObject parsed = parse(reply);
            boolean ok = parsed != null && parsed.optBoolean(PlatformContract.KEY_OK);
            boolean echoMatches = ok && nonce.equals(echoedNonce(parsed));
            Log.i(PlatformContract.LOG_TAG, "[debug] ping " + i + "/" + count
                    + (wasConnected ? "（温）" : "（冷: 接続込み）")
                    + " ok=" + ok + " rtt_us=" + elapsedUs
                    + (ok ? " platform_pid=" + parsed.optInt(PlatformContract.KEY_PID)
                        + " uptime_ms=" + parsed.optLong(PlatformContract.KEY_UPTIME_MS)
                        + " echo=" + (echoMatches ? "一致" : "不一致")
                        : " reply=" + PlatformJson.text(reply)));
        }
        byte[] version = connection.invoke(PlatformContract.MODULE_PLATFORM, PlatformContract.METHOD_VERSION,
                PlatformJson.emptyObject(), true);
        Log.i(PlatformContract.LOG_TAG, "[debug] version " + PlatformJson.text(version));
    }

    /**
     * 試験イベントを 1 つ流す（:seed_platform が記録し、呼び鈴 → 取り出し → エンジンへ）。
     *
     * @param connection 接続
     * @param message    文言
     */
    private static void emitTestEvent(PlatformConnection connection, String message) {
        JSONObject request = new JSONObject();
        PlatformJson.put(request, PlatformContract.KEY_MESSAGE, message != null ? message : DEFAULT_MESSAGE);
        byte[] reply = connection.invoke(PlatformContract.MODULE_PLATFORM, PlatformContract.METHOD_EMIT_TEST_EVENT,
                PlatformJson.utf8(request.toString()), true);
        Log.i(PlatformContract.LOG_TAG, "[debug] emit_test_event " + PlatformJson.text(reply));
    }

    /**
     * ping の返答の echo に入っている nonce を取り出す。
     *
     * @param reply 読んだ返答
     * @return nonce。無ければ null
     */
    private static String echoedNonce(JSONObject reply) {
        JSONObject echo = reply.optJSONObject(PlatformContract.KEY_ECHO);
        return echo != null ? echo.optString(KEY_NONCE, null) : null;
    }

    /**
     * 返答の JSON を読む。
     *
     * @param reply 返答（UTF-8）
     * @return オブジェクト。読めなければ null
     */
    private static JSONObject parse(byte[] reply) {
        try {
            return new JSONObject(PlatformJson.text(reply));
        } catch (JSONException e) {
            return null;
        }
    }
}
