// ============================================================
//  SensorArguments.java — センサーの命令（sensor.start / stop / read）の引数 { kind, rate_hz } を読む（W1-8）
//
//  【規則】（Rust の runtime/src/engine/platform/bridge/sensor/mod.rs の read_kind・read_rate_hz と同じ。変えるときは両方）
//    kind    … 必須の文字列で、約束の種類（sensor/SensorKind。今は linear_acceleration だけ）。それ以外は invalid_argument
//    rate_hz … start だけ。無い・null なら DEFAULT_SENSOR_RATE_HZ（50）。数でない・有限でない・MIN_SENSOR_RATE_HZ（1）未満は
//               invalid_argument。小数は切り捨て、MAX_SENSOR_RATE_HZ（200）を超えたらそろえる
// ============================================================

package com.seedengine.runtime.platform.local;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.sensor.SensorKind;

import org.json.JSONObject;

/**
 * 引数の読み取り（static のみ）。
 */
final class SensorArguments {

    private SensorArguments() {
    }

    /** 読み取りの結果（種類と頻度か、失敗の返答のどちらか一方）。 */
    static final class Parsed {
        /** 読めた種類（失敗なら null）。 */
        final SensorKind kind;
        /** 読めた頻度（Hz。start 以外・失敗なら 0）。 */
        final int rateHz;
        /** 失敗の返答（成功なら null）。 */
        final byte[] errorReply;

        private Parsed(SensorKind kind, int rateHz, byte[] errorReply) {
            this.kind = kind;
            this.rateHz = rateHz;
            this.errorReply = errorReply;
        }
    }

    /**
     * 種類だけを読む（stop・read）。
     *
     * @param arguments 引数
     * @return 結果
     */
    static Parsed readKind(JSONObject arguments) {
        Object value = arguments.opt(PlatformContract.KEY_SENSOR_KIND);
        if (!(value instanceof String)) {
            return failed(PlatformContract.KEY_SENSOR_KIND + " は文字列にしてください");
        }
        SensorKind kind = SensorKind.fromWire((String) value);
        if (kind == null) {
            return failed(PlatformContract.KEY_SENSOR_KIND + " が約束の種類ではありません（" + value + "）");
        }
        return new Parsed(kind, 0, null);
    }

    /**
     * 種類と頻度を読む（start）。
     *
     * @param arguments 引数
     * @return 結果
     */
    static Parsed readKindAndRate(JSONObject arguments) {
        Parsed kind = readKind(arguments);
        if (kind.errorReply != null) {
            return kind;
        }
        Object value = arguments.opt(PlatformContract.KEY_SENSOR_RATE_HZ);
        if (value == null || value == JSONObject.NULL) {
            // 省略（と null）は既定の頻度
            return new Parsed(kind.kind, PlatformContract.DEFAULT_SENSOR_RATE_HZ, null);
        }
        double requested = value instanceof Number ? ((Number) value).doubleValue() : Double.NaN;
        // 下限の比較は double のまま（1 未満の小数を切り捨てて 0 にしてから比べない）
        if (Double.isNaN(requested) || Double.isInfinite(requested) || requested < PlatformContract.MIN_SENSOR_RATE_HZ) {
            return failed(PlatformContract.KEY_SENSOR_RATE_HZ + " は " + PlatformContract.MIN_SENSOR_RATE_HZ + " 以上の数にしてください");
        }
        // 小数は切り捨て（1 以上なので long への変換の切り捨てと同じ。大きすぎる値は long の上限に飽和する）、上限にそろえる
        int rateHz = (int) Math.min((long) requested, PlatformContract.MAX_SENSOR_RATE_HZ);
        return new Parsed(kind.kind, rateHz, null);
    }

    /** 失敗の結果（invalid_argument）。 */
    private static Parsed failed(String detail) {
        return new Parsed(null, 0, PlatformJson.errorReply(PlatformContract.ERROR_INVALID_ARGUMENT, detail));
    }
}
