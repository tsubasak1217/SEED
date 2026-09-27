// ============================================================
//  SensorFeed.java — センサーの種類 1 つの受け取り（出どころの選択・登録・止める・読む・前面から外れたときの一時停止。W1-8）
//
//  【状態】
//    wanted   … スクリプトが start している（stop まで。前面から外れても true のまま）
//    listener … 今の登録（SensorManager に登録中の FeedListener。前面にいて wanted のときだけ。無ければ null）
//    samples  … 標本のたまり（SampleAccumulator。read で最大と数を 0 に戻す）
//  【登録】SensorManager.registerListener(listener, sensor, 間隔〈µs〉, センサーのスレッドの Handler)。間隔は 1,000,000 / rate_hz µs
//  （1〜200 Hz → 1,000,000〜5,000 µs。0〜3 は SENSOR_DELAY_* の定数とみなされるが、この範囲には入らない）。間隔は OS への「希望」で、
//  実際の頻度は端末と他のアプリの登録で変わる（公式の Sensors overview。標本の数は read の sample_count で分かる）。
//  【前面から外れたとき】pause で登録を外し（背面で電池を使わない。公式の推奨は onPause で外す）、resume で同じ頻度・同じ世代で
//  登録し直す（たまりは捨てない。外れている間の標本は無いだけ）。重力を引く係は登録ごとに作り直す（向きが変わっているため）。
//  【スレッド】どのメソッドも SensorFeeds の lock を持って呼ぶ（登録の状態は 1 つの lock で守る。標本は SampleAccumulator の lock）。
// ============================================================

package com.seedengine.runtime.platform.sensor;

import android.hardware.Sensor;
import android.hardware.SensorManager;
import android.os.Handler;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;

/**
 * 種類 1 つの受け取り（SensorFeeds の lock の中で使う）。
 */
final class SensorFeed {

    /** 1 秒のマイクロ秒（頻度 Hz を registerListener の間隔 µs にする）。 */
    private static final int MICROS_PER_SECOND = 1_000_000;

    /** 種類。 */
    private final SensorKind kind;

    /** 標本のたまり。 */
    private final SampleAccumulator samples = new SampleAccumulator();

    /** 選んだ出どころ（最初の start で決める。端末のセンサーは変わらないので覚える）。 */
    private SensorSource source;

    /** 選んだセンサー。 */
    private Sensor sensor;

    /** スクリプトが start しているか。 */
    private boolean wanted;

    /** 受け付けた頻度（Hz）。 */
    private int rateHz;

    /** 今の登録（無ければ null）。 */
    private FeedListener listener;

    SensorFeed(SensorKind kind) {
        this.kind = kind;
    }

    /**
     * 始める（動いていれば標本を捨てて始め直す）。
     *
     * @param manager     SensorManager
     * @param handler     センサーのスレッドの Handler（標本はこのスレッドで受ける）
     * @param rateHz      頻度（Hz。SensorArguments でそろえた後の値）
     * @param hostResumed 前面にいるか（いなければ登録は onResume まで待つ）
     * @return 結果
     */
    SensorStartResult start(SensorManager manager, Handler handler, int rateHz, boolean hostResumed) {
        if (sensor == null && !chooseSource(manager)) {
            Log.i(PlatformContract.LOG_TAG, "センサー " + kind.wireName + ": 端末に使えるセンサーがありません（not_supported）");
            return SensorStartResult.failed(PlatformContract.ERROR_NOT_SUPPORTED,
                    kind.wireName + " を出せるセンサー（重力を除いた加速度・加速度）がありません");
        }
        // 始め直し: 今の登録を外し、たまりを新しい世代にする（古い登録から遅れて届く標本は数えない）
        unregister(manager);
        int generation = samples.reset();
        this.rateHz = rateHz;
        wanted = true;
        if (hostResumed && !register(manager, handler, generation)) {
            wanted = false;
            samples.reset();
            return SensorStartResult.failed(PlatformContract.ERROR_REGISTER_FAILED,
                    "SensorManager.registerListener が false を返しました（" + sensor.getName() + "）");
        }
        Log.i(PlatformContract.LOG_TAG, "センサー " + kind.wireName + " を始めました: 出どころ " + source.wireName + "（" + sensor.getName()
                + "）・" + rateHz + " Hz（間隔 " + periodMicros(rateHz) + " µs）・" + (listener != null ? "登録しました" : "前面へ戻ったら登録します"));
        return SensorStartResult.started(source.wireName, rateHz, listener != null);
    }

    /**
     * 止める（動いていなくても何もしないで返る）。
     *
     * @param manager SensorManager
     * @return 動いていたものを止めたら true
     */
    boolean stop(SensorManager manager) {
        boolean wasWanted = wanted;
        unregister(manager);
        wanted = false;
        samples.reset();
        if (wasWanted) {
            Log.i(PlatformContract.LOG_TAG, "センサー " + kind.wireName + " を止めました");
        }
        return wasWanted;
    }

    /**
     * 読む（最大と数は 0 に戻る）。
     *
     * @return 読んだ値（start していなければ null）
     */
    SensorReading read() {
        return wanted ? samples.drain() : null;
    }

    /**
     * 前面から外れた（MainActivity.onPause）。登録を外す（wanted とたまりは残す）。
     *
     * @param manager SensorManager
     */
    void pause(SensorManager manager) {
        if (listener == null) {
            return;
        }
        unregister(manager);
        Log.i(PlatformContract.LOG_TAG, "センサー " + kind.wireName + ": 前面から外れたので登録を外しました（前面へ戻ったら登録し直します）");
    }

    /**
     * 前面へ戻った（MainActivity.onResume）。start されていて登録が無ければ、同じ頻度・同じ世代で登録し直す。
     *
     * @param manager SensorManager
     * @param handler センサーのスレッドの Handler
     */
    void resume(SensorManager manager, Handler handler) {
        if (!wanted || listener != null) {
            return;
        }
        if (register(manager, handler, samples.generation())) {
            Log.i(PlatformContract.LOG_TAG, "センサー " + kind.wireName + ": 前面へ戻ったので登録し直しました（" + rateHz + " Hz）");
        } else {
            // 次の onResume でまたやり直す。その間の read は標本の数 0 で返る
            Log.w(PlatformContract.LOG_TAG, "センサー " + kind.wireName + ": 前面へ戻ったときの登録に失敗しました（registerListener が false）");
        }
    }

    /**
     * 出どころを選ぶ（種類の候補のうち、端末に最初に見つかったもの）。
     *
     * @param manager SensorManager
     * @return 見つかったら true
     */
    private boolean chooseSource(SensorManager manager) {
        for (SensorSource candidate : kind.sources) {
            Sensor found = manager.getDefaultSensor(candidate.sensorType);
            if (found != null) {
                source = candidate;
                sensor = found;
                return true;
            }
        }
        return false;
    }

    /**
     * 登録する（新しい FeedListener。加速度なら新しい重力を引く係つき）。
     *
     * @param manager    SensorManager
     * @param handler    センサーのスレッドの Handler
     * @param generation この登録の世代
     * @return 登録できたら true
     */
    private boolean register(SensorManager manager, Handler handler, int generation) {
        FeedListener created = new FeedListener(samples, generation, source.removesGravity ? new GravityFilter() : null);
        if (!manager.registerListener(created, sensor, periodMicros(rateHz), handler)) {
            return false;
        }
        listener = created;
        return true;
    }

    /**
     * 登録を外す（無ければ何もしない）。
     *
     * @param manager SensorManager
     */
    private void unregister(SensorManager manager) {
        if (listener == null) {
            return;
        }
        manager.unregisterListener(listener);
        listener = null;
    }

    /**
     * 頻度（Hz）を registerListener の間隔（µs）にする。
     *
     * @param rateHz 頻度（1 以上）
     * @return 間隔（µs）
     */
    static int periodMicros(int rateHz) {
        return MICROS_PER_SECOND / rateHz;
    }
}
