// ============================================================
//  TouchTimeline.java — MotionEvent の時刻と履歴を控えてネイティブへ送る（2026-09-29。docs/input_gestures.md §5）
//
//  【なぜ要るか】
//  winit 0.30.13（ネイティブのイベントループ）は GameActivity の glue から受け取った MotionEvent の時刻（eventTime）と
//  履歴（historical。1 回の vsync の間に溜まった途中の標本）を捨て、各ポインタを WindowEvent::Touch（ID・段階・位置だけ）にする。
//  エンジンは受け取った時刻でジェスチャーの速度を推定していたので、1 回の入力のまとまりに入った MotionEvent どうしが µs しか
//  違わない時刻になり、離す瞬間の小さな指の飛びで速いフリックが起きた。glue の入力のバッファは読むと消えるので、winit の外から
//  二重に読むことはできない。そこで MainActivity.processMotionEvent（glue へ渡す前）がここを呼び、同じ MotionEvent から
//  winit が Touch にするのと同じ指・同じ位置の控えを作って JNI で送る。エンジンが winit の Touch と（ID・段階・位置のビットで）
//  突き合わせ、一致すれば MotionEvent の時刻と履歴でジェスチャーを記録する（runtime/src/engine/core/input/touch/os_timing/）。
//
//  【winit・glue と同じにすること】（android-activity 0.6.1 の同梱の GameActivity 4.4.0 の C と winit の android/mod.rs を読んだ）
//    - 控える action: DOWN・POINTER_DOWN（Started）・MOVE（Moved）・UP・POINTER_UP（Ended）・CANCEL（Cancelled）。他は winit が捨てる
//    - glue の入力のフィルタ: (source & SOURCE_TOUCHSCREEN(0x1002)) != 0 のものだけ（android_native_app_glue.c の default_motion_filter）
//    - 指の数: glue の上限 8 まで（GameActivityEvents.h の GAMEACTIVITY_MAX_NUM_POINTERS_IN_MOTION_EVENT）
//    - 並び: Started / Ended は action の指 1 本、Moved / Cancelled は添字の順に全部の指（winit の handle_input_event）
//    - 位置: getAxisValue(AXIS_X / AXIS_Y, 添字)（GameActivity が glue へ渡すのと同じ float）。履歴は getHistoricalAxisValue
//  時刻は CLOCK_MONOTONIC の ns（API 34 以上は getEventTimeNanos / getHistoricalEventTimeNanos、未満は ms × 1,000,000）。
//
//  【重さ】JNI は MotionEvent 1 つにつき 1 回。配列は使い回す（UI スレッドで毎回確保しない。足りないときだけ広げる）。
//  【失敗】ネイティブの関数が無い（古い libSEED.so）なら以後は何もしない。例外は握りつぶしてタッチの本流（glue への受け渡し）を
//  止めない（控えが無いイベントは、エンジンが受け取った時刻に戻るだけ）。
//  【ログ】デバッグ版の APK だけ、DOWN・UP（POINTER_DOWN・POINTER_UP を含む）のときに 1 行（MOVE では出さない）。
// ============================================================

package com.seedengine.runtime.input;

import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.os.Build;
import android.util.Log;
import android.view.MotionEvent;

/**
 * MotionEvent の時刻と履歴の控えをネイティブへ送る係（UI スレッド専用。MainActivity が 1 つ持つ）。
 *
 * <p>段階の番号はネイティブの engine::core::input::touch::os_timing::stamp の OS_PHASE_* と一致させる。</p>
 */
public final class TouchTimeline {

    /** 段階の番号: 触れた（ACTION_DOWN・ACTION_POINTER_DOWN。winit の Started）。 */
    static final int PHASE_STARTED = 0;

    /** 段階の番号: 動いた（ACTION_MOVE。winit の Moved）。 */
    static final int PHASE_MOVED = 1;

    /** 段階の番号: 離れた（ACTION_UP・ACTION_POINTER_UP。winit の Ended）。 */
    static final int PHASE_ENDED = 2;

    /** 段階の番号: 取り消された（ACTION_CANCEL。winit の Cancelled）。 */
    static final int PHASE_CANCELLED = 3;

    /** 控えないイベントの印（winit が Touch にしない action）。 */
    private static final int PHASE_NONE = -1;

    /**
     * glue が受け取る入力元のビット（android_native_app_glue.c の default_motion_filter の SOURCE_TOUCHSCREEN = 0x1002。
     * InputDevice.SOURCE_TOUCHSCREEN と同じ値）。glue はこれとのビット積が 0 でないものだけを受け取る（マウス 0x2002 等も通る）。
     */
    private static final int GLUE_SOURCE_MASK = 0x00001002;

    /** glue が 1 つの MotionEvent で受け取る指の数の上限（GameActivityEvents.h の GAMEACTIVITY_MAX_NUM_POINTERS_IN_MOTION_EVENT）。 */
    private static final int GLUE_MAX_POINTERS = 8;

    /** 1 つの点の値の数（x, y）。 */
    private static final int COORDS_PER_POINT = 2;

    /** ms → ns（API 33 以下の getEventTime は ms）。 */
    private static final long NANOS_PER_MILLI = 1_000_000L;

    /** 最初に用意する標本の数（履歴 + 今。60 Hz の vsync に 120〜240 Hz の走査なら履歴は 1〜4 標本）。足りなければ広げる。 */
    private static final int INITIAL_SAMPLE_CAPACITY = 8;

    /** logcat のタグ（MainActivity・ネイティブと同じ。`adb logcat -s SEED` で一緒に見える）。 */
    private static final String LOG_TAG = "SEED";

    /** ログの頭（ネイティブの行は `[SEED TOUCH TIME] id=…`）。 */
    private static final String LOG_PREFIX = "[SEED TOUCH TIME] java ";

    /** デバッグ版の APK か（DOWN・UP のログを出すか）。 */
    private final boolean logEnabled;

    /** 指の ID（使い回す）。 */
    private final int[] ids = new int[GLUE_MAX_POINTERS];

    /** 位置（標本の順〈履歴の古い順 → 最後が今〉に、指の順の x, y。使い回し、足りなければ広げる）。 */
    private float[] coords = new float[INITIAL_SAMPLE_CAPACITY * GLUE_MAX_POINTERS * COORDS_PER_POINT];

    /** 時刻（CLOCK_MONOTONIC の ns。履歴の古い順 → 最後が今。使い回し、足りなければ広げる）。 */
    private long[] timesNs = new long[INITIAL_SAMPLE_CAPACITY];

    /** ネイティブの関数が無かった（古い libSEED.so）。以後は何もしない。 */
    private boolean nativeMissing;

    /** 例外を一度ログに出したか（UI スレッドから毎回出さない）。 */
    private boolean failureLogged;

    /**
     * 作る（MainActivity.onCreate の最初。super.onCreate が作る描画面が触れられるより前）。
     *
     * @param context アプリの情報（デバッグ版かどうか）を読む Context
     */
    public TouchTimeline(Context context) {
        logEnabled = (context.getApplicationInfo().flags & ApplicationInfo.FLAG_DEBUGGABLE) != 0;
    }

    /**
     * MotionEvent 1 つの控えを送る（winit が Touch にしないイベント・glue が受け取らないイベントは何もしない）。
     * 例外は握りつぶす（タッチの本流を止めない）。
     *
     * @param event GameActivity.processMotionEvent が受け取った MotionEvent
     */
    public void record(MotionEvent event) {
        if (nativeMissing) {
            return;
        }
        try {
            send(event);
        } catch (UnsatisfiedLinkError e) {
            nativeMissing = true;
            Log.w(LOG_TAG, LOG_PREFIX + "nativeOnMotionTimeline を呼べません（古い libSEED.so）。以後は控えを送りません: " + e);
        } catch (RuntimeException e) {
            if (!failureLogged) {
                failureLogged = true;
                Log.w(LOG_TAG, LOG_PREFIX + "控えを作れませんでした（以後の同じ失敗は出しません）: " + e);
            }
        }
    }

    /** 控えを作って送る。 */
    private void send(MotionEvent event) {
        int action = event.getActionMasked();
        int phase = phaseOf(action);
        if (phase == PHASE_NONE || (event.getSource() & GLUE_SOURCE_MASK) == 0) {
            return;
        }
        int pointers = Math.min(event.getPointerCount(), GLUE_MAX_POINTERS);
        int first;
        int count;
        if (phase == PHASE_STARTED || phase == PHASE_ENDED) {
            // winit は action の指 1 本だけを Touch にする（glue の上限の外の指は winit でも届かない）
            first = event.getActionIndex();
            count = 1;
            if (first >= pointers) {
                return;
            }
        } else {
            first = 0;
            count = pointers;
        }
        int history = phase == PHASE_MOVED ? event.getHistorySize() : 0;
        ensureCapacity(history + 1, count);
        for (int k = 0; k < count; k++) {
            ids[k] = event.getPointerId(first + k);
        }
        for (int h = 0; h < history; h++) {
            timesNs[h] = historicalTimeNanos(event, h);
            for (int k = 0; k < count; k++) {
                int base = (h * count + k) * COORDS_PER_POINT;
                coords[base] = event.getHistoricalAxisValue(MotionEvent.AXIS_X, first + k, h);
                coords[base + 1] = event.getHistoricalAxisValue(MotionEvent.AXIS_Y, first + k, h);
            }
        }
        long eventNs = eventTimeNanos(event);
        timesNs[history] = eventNs;
        for (int k = 0; k < count; k++) {
            int base = (history * count + k) * COORDS_PER_POINT;
            coords[base] = event.getAxisValue(MotionEvent.AXIS_X, first + k);
            coords[base + 1] = event.getAxisValue(MotionEvent.AXIS_Y, first + k);
        }
        nativeOnMotionTimeline(phase, count, history, ids, coords, timesNs);
        if (logEnabled && (phase == PHASE_STARTED || phase == PHASE_ENDED)) {
            Log.i(LOG_TAG, LOG_PREFIX + actionLabel(action) + " id=" + ids[0] + " ev_ns=" + eventNs
                    + " pos=(" + coords[history * count * COORDS_PER_POINT] + ", "
                    + coords[history * count * COORDS_PER_POINT + 1] + ")");
        }
    }

    /**
     * ログに出す action の名前（触れた・離れたの 4 つ。MotionEvent.actionToString は添字を付けるので使わない）。
     *
     * @param actionMasked MotionEvent.getActionMasked の値
     * @return 名前
     */
    private static String actionLabel(int actionMasked) {
        switch (actionMasked) {
            case MotionEvent.ACTION_DOWN:
                return "DOWN";
            case MotionEvent.ACTION_POINTER_DOWN:
                return "POINTER_DOWN";
            case MotionEvent.ACTION_UP:
                return "UP";
            case MotionEvent.ACTION_POINTER_UP:
                return "POINTER_UP";
            default:
                return String.valueOf(actionMasked);
        }
    }

    /**
     * winit が Touch にする action を段階の番号へ直す（それ以外は PHASE_NONE）。
     *
     * @param actionMasked MotionEvent.getActionMasked の値
     * @return 段階の番号
     */
    private static int phaseOf(int actionMasked) {
        switch (actionMasked) {
            case MotionEvent.ACTION_DOWN:
            case MotionEvent.ACTION_POINTER_DOWN:
                return PHASE_STARTED;
            case MotionEvent.ACTION_MOVE:
                return PHASE_MOVED;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_POINTER_UP:
                return PHASE_ENDED;
            case MotionEvent.ACTION_CANCEL:
                return PHASE_CANCELLED;
            default:
                return PHASE_NONE;
        }
    }

    /**
     * 使い回す配列を、標本 samples 個・指 count 本の分まで広げる（足りているときは何もしない）。
     *
     * @param samples 標本の数（履歴 + 今）
     * @param count   指の数
     */
    private void ensureCapacity(int samples, int count) {
        if (timesNs.length < samples) {
            timesNs = new long[samples];
        }
        int needed = samples * count * COORDS_PER_POINT;
        if (coords.length < needed) {
            coords = new float[needed];
        }
    }

    /**
     * MotionEvent の時刻（CLOCK_MONOTONIC の ns）。API 34 以上は ns のまま、未満は ms を ns へ。
     *
     * @param event MotionEvent
     * @return 時刻（ns）
     */
    private static long eventTimeNanos(MotionEvent event) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            return event.getEventTimeNanos();
        }
        return event.getEventTime() * NANOS_PER_MILLI;
    }

    /**
     * 履歴の標本の時刻（CLOCK_MONOTONIC の ns）。API 34 以上は ns のまま、未満は ms を ns へ。
     *
     * @param event MotionEvent
     * @param pos   履歴の添字（古い順）
     * @return 時刻（ns）
     */
    private static long historicalTimeNanos(MotionEvent event, int pos) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            return event.getHistoricalEventTimeNanos(pos);
        }
        return event.getHistoricalEventTime(pos) * NANOS_PER_MILLI;
    }

    /**
     * ネイティブの受け口（runtime/android/native/src/touch_timeline.rs）。MotionEvent 1 つにつき 1 回。
     *
     * @param phase        段階の番号（PHASE_*）
     * @param pointerCount 指の数（1〜8）
     * @param historySize  履歴の標本の数（MOVE だけ。他は 0）
     * @param ids          指の ID（先頭の pointerCount 個）
     * @param coords       位置（(historySize + 1) × pointerCount × 2 個）
     * @param timesNs      時刻（CLOCK_MONOTONIC の ns。historySize + 1 個）
     */
    private static native void nativeOnMotionTimeline(
            int phase, int pointerCount, int historySize, int[] ids, float[] coords, long[] timesNs);
}
