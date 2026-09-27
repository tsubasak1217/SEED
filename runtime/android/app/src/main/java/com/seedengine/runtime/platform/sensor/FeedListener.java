// ============================================================
//  FeedListener.java — 1 回の登録で標本を受ける SensorEventListener（センサーのスレッド SEEDSensor で呼ばれる。W1-8）
//
//  登録（SensorManager.registerListener）のたびに新しく作る。受けた標本を（加速度なら GravityFilter で重力を引いてから）
//  SampleAccumulator へ足すだけで、イベントは流さない（スクリプトは sensor.read で取る）。SensorEvent はフレームワークが
//  使い回すので、onSensorChanged の中で値を写し取る。
// ============================================================

package com.seedengine.runtime.platform.sensor;

import android.hardware.Sensor;
import android.hardware.SensorEvent;
import android.hardware.SensorEventListener;

/**
 * 標本の受け口（1 回の登録に 1 つ）。
 */
final class FeedListener implements SensorEventListener {

    /** 標本の成分の数（x, y, z）。これより短い values は捨てる。 */
    private static final int AXES = 3;

    /** 足し先。 */
    private final SampleAccumulator samples;

    /** この登録の世代（SampleAccumulator の世代と違えば、足しても捨てられる）。 */
    private final int generation;

    /** 重力を引く係（加速度のセンサーのときだけ。重力を除いた加速度のセンサーなら null）。 */
    private final GravityFilter gravityFilter;

    /** 重力を引いた値の書き先（センサーのスレッドだけが使う）。 */
    private final float[] linear = new float[AXES];

    /**
     * @param samples       足し先
     * @param generation    この登録の世代
     * @param gravityFilter 重力を引く係（要らなければ null）
     */
    FeedListener(SampleAccumulator samples, int generation, GravityFilter gravityFilter) {
        this.samples = samples;
        this.generation = generation;
        this.gravityFilter = gravityFilter;
    }

    @Override
    public void onSensorChanged(SensorEvent event) {
        float[] values = event.values;
        if (values == null || values.length < AXES) {
            return;
        }
        if (gravityFilter == null) {
            samples.record(generation, values[0], values[1], values[2], event.timestamp);
            return;
        }
        gravityFilter.removeGravity(values, event.timestamp, linear);
        samples.record(generation, linear[0], linear[1], linear[2], event.timestamp);
    }

    @Override
    public void onAccuracyChanged(Sensor sensor, int accuracy) {
        // 精度の変化は使わない（値はそのまま数える。精度はスクリプトへ出していない）
    }
}
