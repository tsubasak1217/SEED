// ============================================================
//  SensorReading.java — sensor.read で返す値（SampleAccumulator.drain の結果。不変。W1-8）
//
//  最新の標本（x, y, z と時刻）と、前回の read からの最大の大きさ・標本の数。時刻は SensorEvent.timestamp（ナノ秒。
//  SystemClock.elapsedRealtimeNanos と同じ時計）のまま持ち、返答を作るときに UTC の epoch ミリ秒へ換算する（timestampEpochMillis）。
// ============================================================

package com.seedengine.runtime.platform.sensor;

import android.os.SystemClock;

import java.util.concurrent.TimeUnit;

/**
 * 読んだ値（不変）。
 */
public final class SensorReading {

    /** 最新の標本の x 成分（m/s²。まだ無ければ 0）。 */
    public final float x;

    /** 最新の標本の y 成分（m/s²）。 */
    public final float y;

    /** 最新の標本の z 成分（m/s²）。 */
    public final float z;

    /** 最新の標本の時刻（SensorEvent.timestamp。ナノ秒。まだ無ければ 0）。 */
    final long timestampNanos;

    /** 前回の read からの標本の大きさの最大（m/s²。標本が無ければ 0）。 */
    public final float peakMagnitude;

    /** 前回の read からの標本の数。 */
    public final int sampleCount;

    SensorReading(float x, float y, float z, long timestampNanos, float peakMagnitude, int sampleCount) {
        this.x = x;
        this.y = y;
        this.z = z;
        this.timestampNanos = timestampNanos;
        this.peakMagnitude = peakMagnitude;
        this.sampleCount = sampleCount;
    }

    /**
     * 最新の標本の時刻を UTC の epoch ミリ秒で返す。
     *
     * <p>SensorEvent.timestamp は「SystemClock.elapsedRealtimeNanos と同じ時計のナノ秒」（AOSP の SensorEvent.java の説明）なので、
     * 今の elapsedRealtimeNanos との差（標本の古さ）を今の壁時計から引く。壁時計が後で合わせ直されても、換算は読んだときの壁時計に揃う。
     * 標本の時刻が今より先に見える（時計の読みの前後）ときは古さを 0 とする。</p>
     *
     * @return UTC の epoch ミリ秒（標本がまだ無ければ 0）
     */
    public long timestampEpochMillis() {
        if (timestampNanos <= 0) {
            return 0L;
        }
        long ageNanos = Math.max(0L, SystemClock.elapsedRealtimeNanos() - timestampNanos);
        return System.currentTimeMillis() - TimeUnit.NANOSECONDS.toMillis(ageNanos);
    }
}
