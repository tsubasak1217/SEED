// ============================================================
//  SensorKind.java — スクリプトが求めるセンサーの種類（sensor.* の引数 kind。W1-8）
//
//  種類ごとに「値の出どころの候補（使える順）」を持つ（データの表。種類を足すときは値を 1 つ足す）:
//    LINEAR_ACCELERATION（"linear_acceleration"）… 重力を除いた加速度（m/s²）。TYPE_LINEAR_ACCELERATION が無ければ、加速度から低域通過で
//                                                  重力を引いたもの（SensorSource.ACCELEROMETER_LOWPASS）。どちらも無ければ not_supported
//  名前は PlatformContract.SENSOR_KIND_*（Rust の wire::sensor::KINDS・C# の SensorJson と一致させる）。
// ============================================================

package com.seedengine.runtime.platform.sensor;

import com.seedengine.runtime.platform.PlatformContract;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;

/**
 * センサーの種類。
 */
public enum SensorKind {

    /** 重力を除いた加速度（振る・揺れの判定に使う）。 */
    LINEAR_ACCELERATION(PlatformContract.SENSOR_KIND_LINEAR_ACCELERATION,
            SensorSource.LINEAR_ACCELERATION, SensorSource.ACCELEROMETER_LOWPASS);

    /** 命令の引数 kind の名前。 */
    public final String wireName;

    /** 値の出どころの候補（使える順。変えられない一覧）。 */
    final List<SensorSource> sources;

    SensorKind(String wireName, SensorSource... sources) {
        this.wireName = wireName;
        this.sources = Collections.unmodifiableList(Arrays.asList(sources));
    }

    /**
     * 命令の引数の名前から種類を引く。
     *
     * @param name 名前（null 可）
     * @return 種類（知らない名前なら null）
     */
    public static SensorKind fromWire(String name) {
        for (SensorKind kind : values()) {
            if (kind.wireName.equals(name)) {
                return kind;
            }
        }
        return null;
    }
}
