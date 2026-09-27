// ============================================================
//  SensorSource.java — センサーの値の出どころ（どのハードウェアのセンサーから、どう作るか。W1-8）
//
//  SensorKind が「使える順」に並べた候補のうち、端末に最初に見つかった（SensorManager.getDefaultSensor が null でない）ものを使う。
//  start の返答の source（PlatformContract.SENSOR_SOURCE_*）でスクリプトへ知らせる。
//    LINEAR_ACCELERATION   … Sensor.TYPE_LINEAR_ACCELERATION（API 9。多くの端末は加速度とジャイロの融合で作る仮想のセンサー）。値をそのまま使う
//    ACCELEROMETER_LOWPASS … Sensor.TYPE_ACCELEROMETER（API 3）の値から、GravityFilter（一次の低域通過）で見積もった重力を引く。
//                            ジャイロの無い安い端末など、上が無いときの代わり（向きを素早く変えると一時的に重力の差が加速度に見える）
// ============================================================

package com.seedengine.runtime.platform.sensor;

import android.hardware.Sensor;

import com.seedengine.runtime.platform.PlatformContract;

/**
 * 値の出どころ。
 */
enum SensorSource {

    /** 端末の重力を除いた加速度のセンサー（値をそのまま使う）。 */
    LINEAR_ACCELERATION(Sensor.TYPE_LINEAR_ACCELERATION, PlatformContract.SENSOR_SOURCE_LINEAR_ACCELERATION, false),

    /** 加速度のセンサー（低域通過で見積もった重力を引く）。 */
    ACCELEROMETER_LOWPASS(Sensor.TYPE_ACCELEROMETER, PlatformContract.SENSOR_SOURCE_ACCELEROMETER_LOWPASS, true);

    /** SensorManager.getDefaultSensor に渡すセンサーの種類（Sensor.TYPE_*）。 */
    final int sensorType;

    /** start の返答の source に入れる名前（PlatformContract.SENSOR_SOURCE_*）。 */
    final String wireName;

    /** 受けた値から重力を引く必要があるか（加速度のセンサーは重力を含む）。 */
    final boolean removesGravity;

    SensorSource(int sensorType, String wireName, boolean removesGravity) {
        this.sensorType = sensorType;
        this.wireName = wireName;
        this.removesGravity = removesGravity;
    }
}
