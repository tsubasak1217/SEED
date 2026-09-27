// ============================================================
//  SampleAccumulator.java — 標本のたまり（最新の標本・前回の read からの最大の大きさと数。W1-8）
//
//  【スレッド】書くのはセンサーのスレッド（FeedListener.onSensorChanged → record。50〜200 Hz）、読むのはエンジンのスレッド
//  （sensor.read → drain。フレームに 1 回程度）、始め直すのは登録の側（SensorFeed の start / stop → reset）。短い区間を 1 つの
//  synchronized で守る（大きさの平方根は lock の外で計算する）。
//  【read の意味】drain は「前回の drain からの」最大の大きさと数を返して 0 に戻す。フレームの間（例 60 fps なら約 17 ms）に来た
//  標本の山を 1 つも落とさないため（最新の値だけを読むと、フレームの境目の間の振りの頂点を見落とす）。最新の標本と時刻は残す。
//  【世代】start・stop のたびに reset で世代を上げる。登録を外した後に配送の待ち行列に残っていた古い登録の標本（FeedListener が
//  覚えている世代が古い）は数えない。
//  【規則】（Rust のデスクトップの模擬 desktop_sim/sensor_state.rs と同じ）大きさは √(x²+y²+z²)、最大は「より大きいときだけ」更新、
//  数は int の上限で止める。有限でない成分を含む標本は標本として数えない（返答の JSON に NaN を入れられないため）。
// ============================================================

package com.seedengine.runtime.platform.sensor;

/**
 * 標本のたまり（どのスレッドから呼んでもよい）。
 */
final class SampleAccumulator {

    /** 下の値をすべて守る。 */
    private final Object lock = new Object();

    /** 今の世代（reset で 1 つ上がる）。 */
    private int generation;

    /** 最新の標本（m/s²）。 */
    private float latestX;
    private float latestY;
    private float latestZ;

    /** 最新の標本の時刻（SensorEvent.timestamp。ナノ秒。まだ無ければ 0）。 */
    private long latestTimestampNanos;

    /** 前回の drain からの大きさの最大（m/s²）。 */
    private float peakMagnitude;

    /** 前回の drain からの標本の数。 */
    private int sampleCount;

    /**
     * すべてを捨てて新しい世代にする（start・stop から）。
     *
     * @return 新しい世代（新しい登録の FeedListener に渡す）
     */
    int reset() {
        synchronized (lock) {
            generation++;
            latestX = 0f;
            latestY = 0f;
            latestZ = 0f;
            latestTimestampNanos = 0L;
            peakMagnitude = 0f;
            sampleCount = 0;
            return generation;
        }
    }

    /**
     * 今の世代（登録し直すときに、たまりを捨てずに同じ世代で続けるため）。
     *
     * @return 世代
     */
    int generation() {
        synchronized (lock) {
            return generation;
        }
    }

    /**
     * 標本を 1 つ足す（センサーのスレッドから）。
     *
     * @param sourceGeneration 標本を受けた登録の世代（今の世代と違えば捨てる）
     * @param x                x 成分（m/s²）
     * @param y                y 成分（m/s²）
     * @param z                z 成分（m/s²）
     * @param timestampNanos   時刻（SensorEvent.timestamp）
     */
    void record(int sourceGeneration, float x, float y, float z, long timestampNanos) {
        if (!Float.isFinite(x) || !Float.isFinite(y) || !Float.isFinite(z)) {
            return;
        }
        // 大きさは lock の外で（double で計算して float へ。Rust の模擬の magnitude と同じ式）
        float magnitude = (float) Math.sqrt((double) x * x + (double) y * y + (double) z * z);
        synchronized (lock) {
            if (sourceGeneration != generation) {
                return;
            }
            latestX = x;
            latestY = y;
            latestZ = z;
            latestTimestampNanos = timestampNanos;
            if (magnitude > peakMagnitude) {
                peakMagnitude = magnitude;
            }
            if (sampleCount < Integer.MAX_VALUE) {
                sampleCount++;
            }
        }
    }

    /**
     * 今の値を返し、最大と数を 0 に戻す（sensor.read から。最新の標本と時刻は残す）。
     *
     * @return 読んだ値
     */
    SensorReading drain() {
        synchronized (lock) {
            SensorReading reading = new SensorReading(latestX, latestY, latestZ, latestTimestampNanos, peakMagnitude, sampleCount);
            peakMagnitude = 0f;
            sampleCount = 0;
            return reading;
        }
    }
}
