// ============================================================
//  SensorFeeds.java — センサーの受け取りの窓口（種類ごとの SensorFeed・センサーのスレッド・前面かどうか。メインプロセス。W1-8）
//
//  【呼ばれ方】
//    start / stop / read … local/SensorStartCommand ほか（エンジンのスレッド＝スクリプトのフレームの中。同期）
//    onHostResumed / onHostPaused … MainActivity.onResume / onPause（UI スレッド）
//    標本 … センサーのスレッド SEEDSensor（HandlerThread。最初の start で作り、プロセスの間ずっと使う）で FeedListener が受ける
//  【同期】登録の状態（どの種類が start され、今登録しているか・前面か）は LOCK 1 つで守る（start と onPause が同時に来ても、
//  前面から外れた後に登録が残らない）。標本は SampleAccumulator が自分の lock で守るので、センサーのスレッドは LOCK を取らない
//  （lock の順は LOCK → SampleAccumulator の lock の 1 通りだけ）。registerListener / unregisterListener は sensorservice への
//  Binder の呼び出しで、LOCK を持ったまま呼ぶ（前面の出入り・start / stop のときだけ）。
//  【前面】背面では電池を使わないよう、onPause で全部の登録を外し、onResume で start 中のものを登録し直す（公式の推奨）。
//  プロセスの最初の onResume より前の start は登録を onResume まで待つ。
//  権限は要らない（加速度・重力を除いた加速度は権限の無いセンサー）。マニフェストの uses-feature（required=false）は main に常設。
// ============================================================

package com.seedengine.runtime.platform.sensor;

import android.content.Context;
import android.hardware.SensorManager;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;

import java.util.Collections;
import java.util.EnumMap;
import java.util.Map;

/**
 * センサーの受け取りの窓口（static のみ。どのスレッドから呼んでもよい）。
 */
public final class SensorFeeds {

    private SensorFeeds() {
    }

    /** センサーのスレッドの名前（標本を受ける。logcat・ANR の記録で見分ける）。 */
    private static final String THREAD_NAME = "SEEDSensor";

    /** 登録の状態をまとめて守る。 */
    private static final Object LOCK = new Object();

    /** 種類 → 受け取り（全部の種類を最初に作る。作った後は表を変えない）。 */
    private static final Map<SensorKind, SensorFeed> FEEDS = buildFeeds();

    /** SensorManager（最初の start で取る。LOCK で守る）。 */
    private static SensorManager manager;

    /** センサーのスレッドの Handler（最初の start で作る。LOCK で守る）。 */
    private static Handler handler;

    /** 前面にいるか（MainActivity の onResume と onPause の間。LOCK で守る）。 */
    private static boolean hostResumed;

    /**
     * 表を作る。
     *
     * @return 種類 → 受け取り（変えられない表）
     */
    private static Map<SensorKind, SensorFeed> buildFeeds() {
        Map<SensorKind, SensorFeed> feeds = new EnumMap<>(SensorKind.class);
        for (SensorKind kind : SensorKind.values()) {
            feeds.put(kind, new SensorFeed(kind));
        }
        return Collections.unmodifiableMap(feeds);
    }

    /**
     * 始める（sensor.start。動いていれば標本を捨てて始め直す）。
     *
     * @param context アプリの Context（SensorManager を取る）
     * @param kind    種類
     * @param rateHz  頻度（Hz。そろえた後の値）
     * @return 結果
     */
    public static SensorStartResult start(Context context, SensorKind kind, int rateHz) {
        synchronized (LOCK) {
            SensorManager sensors = sensorManager(context);
            if (sensors == null) {
                return SensorStartResult.failed(PlatformContract.ERROR_NOT_SUPPORTED, "SensorManager がありません");
            }
            return FEEDS.get(kind).start(sensors, sensorHandler(), rateHz, hostResumed);
        }
    }

    /**
     * 止める（sensor.stop。動いていなくても成功）。
     *
     * @param kind 種類
     * @return 動いていたものを止めたら true
     */
    public static boolean stop(SensorKind kind) {
        synchronized (LOCK) {
            if (manager == null) {
                // 一度も start していない（SensorManager を取っていない）
                return false;
            }
            return FEEDS.get(kind).stop(manager);
        }
    }

    /**
     * 読む（sensor.read。最大と数は 0 に戻る）。
     *
     * @param kind 種類
     * @return 読んだ値（start していなければ null）
     */
    public static SensorReading read(SensorKind kind) {
        synchronized (LOCK) {
            return FEEDS.get(kind).read();
        }
    }

    /**
     * 前面へ戻った（MainActivity.onResume の super の後。UI スレッド）。start 中の種類を登録し直す。
     * 失敗は Activity の寿命の処理を止めないようログに残して飲み込む（権限の PermissionLifecycle と同じ方針）。
     */
    public static void onHostResumed() {
        try {
            synchronized (LOCK) {
                hostResumed = true;
                if (manager == null) {
                    return;
                }
                for (SensorFeed feed : FEEDS.values()) {
                    feed.resume(manager, sensorHandler());
                }
            }
        } catch (RuntimeException e) {
            Log.e(PlatformContract.LOG_TAG, "前面への復帰でのセンサーの登録に失敗しました（Activity は続けます）", e);
        }
    }

    /**
     * 前面から外れた（MainActivity.onPause の super の後。UI スレッド）。全部の登録を外す（start の状態とたまりは残す）。
     */
    public static void onHostPaused() {
        try {
            synchronized (LOCK) {
                hostResumed = false;
                if (manager == null) {
                    return;
                }
                for (SensorFeed feed : FEEDS.values()) {
                    feed.pause(manager);
                }
            }
        } catch (RuntimeException e) {
            Log.e(PlatformContract.LOG_TAG, "前面から外れるときのセンサーの登録の解除に失敗しました（Activity は続けます）", e);
        }
    }

    /**
     * SensorManager（無ければ取る。LOCK の中で呼ぶ）。
     *
     * @param context どの Context でもよい（アプリの Context を使う）
     * @return SensorManager（端末に無ければ null）
     */
    private static SensorManager sensorManager(Context context) {
        if (manager == null) {
            manager = context.getApplicationContext().getSystemService(SensorManager.class);
        }
        return manager;
    }

    /**
     * センサーのスレッドの Handler（無ければスレッドを立てる。LOCK の中で呼ぶ）。
     *
     * @return Handler
     */
    private static Handler sensorHandler() {
        if (handler == null) {
            HandlerThread thread = new HandlerThread(THREAD_NAME);
            thread.start();
            handler = new Handler(thread.getLooper());
        }
        return handler;
    }
}
