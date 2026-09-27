// ============================================================
//  SensorStartResult.java — sensor.start の結果（SensorFeeds.start の戻り値。不変。W1-8）
//
//  成功なら出どころ（PlatformContract.SENSOR_SOURCE_*）とそろえた頻度、失敗なら理由（PlatformContract.ERROR_* ）と説明。
//  返答の JSON は local/SensorStartCommand が作る（ここは JSON を知らない）。
// ============================================================

package com.seedengine.runtime.platform.sensor;

/**
 * start の結果（不変）。
 */
public final class SensorStartResult {

    /** 失敗の理由（PlatformContract.ERROR_*。成功なら null）。 */
    public final String error;

    /** 失敗の説明（ログ・返答の detail。無ければ null）。 */
    public final String detail;

    /** 値の出どころ（PlatformContract.SENSOR_SOURCE_*。失敗なら null）。 */
    public final String source;

    /** 受け付けた頻度（Hz。失敗なら 0）。 */
    public final int rateHz;

    /** 今すぐ登録したか（false なら前面へ戻ったとき〈onResume〉に登録する。成功のときだけ意味がある）。 */
    public final boolean registeredNow;

    private SensorStartResult(String error, String detail, String source, int rateHz, boolean registeredNow) {
        this.error = error;
        this.detail = detail;
        this.source = source;
        this.rateHz = rateHz;
        this.registeredNow = registeredNow;
    }

    /**
     * 成功。
     *
     * @param source        値の出どころ
     * @param rateHz        受け付けた頻度
     * @param registeredNow 今すぐ登録したか
     * @return 結果
     */
    static SensorStartResult started(String source, int rateHz, boolean registeredNow) {
        return new SensorStartResult(null, null, source, rateHz, registeredNow);
    }

    /**
     * 失敗。
     *
     * @param error  理由（PlatformContract.ERROR_*）
     * @param detail 説明（null 可）
     * @return 結果
     */
    static SensorStartResult failed(String error, String detail) {
        return new SensorStartResult(error, detail, null, 0, false);
    }

    /**
     * 成功したか。
     *
     * @return 成功なら true
     */
    public boolean ok() {
        return error == null;
    }
}
