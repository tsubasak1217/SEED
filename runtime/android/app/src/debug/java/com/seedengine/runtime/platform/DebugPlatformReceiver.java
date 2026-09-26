// ============================================================
//  DebugPlatformReceiver.java — デバッグ版だけの adb の入口（プラットフォーム機能の確かめ。W1-1）
//
//  デバッグ版の APK（app/src/debug/）にだけ入る、exported の受信機（メインプロセス）。adb から命じて、
//  メインプロセス → :seed_platform の往復と、:seed_platform → エンジンのイベントの経路を確かめる。
//  暗黙の放送はマニフェストの受信機に届かないので、必ず -n で部品を指定する（docs/app_platform_roadmap.md §2.6）:
//
//    adb shell am broadcast -n <applicationId>/com.seedengine.runtime.platform.DebugPlatformReceiver \
//        -a com.seedengine.runtime.platform.PING [--ei count 5]
//    adb shell am broadcast -n <applicationId>/com.seedengine.runtime.platform.DebugPlatformReceiver \
//        -a com.seedengine.runtime.platform.EMIT_TEST_EVENT [--es message hello]
//
//  結果は logcat のタグ SEEDPlatform（`adb logcat -s SEED SEEDPlatform`）。試験イベントはエンジンが動いていれば
//  スクリプトへ platform.test_event として届く（エンジンが居なければ捨てる）。
//  受信は UI スレッドなので goAsync で専用のスレッドへ移し、そこで接続を待ってよい（描画のスレッドではない）。
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

    /** extra: ping の回数。 */
    private static final String EXTRA_COUNT = "count";

    /** extra: 試験イベントの文言。 */
    private static final String EXTRA_MESSAGE = "message";

    /** ping の既定の回数。 */
    private static final int DEFAULT_PING_COUNT = 3;

    /** ping の回数の上限（誤って大きな数を渡したときの蓋）。 */
    private static final int MAX_PING_COUNT = 100;

    /** 試験イベントの既定の文言。 */
    private static final String DEFAULT_MESSAGE = "adb";

    /** ping の nonce の接頭辞（返ってきた echo と突き合わせる）。 */
    private static final String NONCE_PREFIX = "adb-";

    /** ping の引数の nonce のキー（C# の PlatformDiagnostics.Ping と同じ）。 */
    private static final String KEY_NONCE = "nonce";

    /** 専用のスレッドの名前。 */
    private static final String THREAD_NAME = "SEEDPlatformDebug";

    /** ナノ秒 → マイクロ秒。 */
    private static final long NANOS_PER_MICRO = 1_000L;

    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent.getAction();
        if (!ACTION_PING.equals(action) && !ACTION_EMIT_TEST_EVENT.equals(action)) {
            Log.w(PlatformContract.LOG_TAG, "知らない命令です: " + action);
            return;
        }
        PendingResult pending = goAsync();
        PlatformConnection connection = SeedPlatform.connection(context);
        int count = Math.max(1, Math.min(MAX_PING_COUNT, intent.getIntExtra(EXTRA_COUNT, DEFAULT_PING_COUNT)));
        String message = intent.hasExtra(EXTRA_MESSAGE) ? intent.getStringExtra(EXTRA_MESSAGE) : DEFAULT_MESSAGE;
        new Thread(() -> {
            try {
                if (ACTION_PING.equals(action)) {
                    runPings(connection, count);
                } else {
                    emitTestEvent(connection, message);
                }
            } catch (RuntimeException e) {
                Log.e(PlatformContract.LOG_TAG, "デバッグの命令の途中で例外", e);
            } finally {
                pending.finish();
            }
        }, THREAD_NAME).start();
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
