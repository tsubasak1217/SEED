// ============================================================
//  SensorReadCommand.java — sensor.read（最新の標本と、前回の read からの最大の大きさ・標本の数。W1-8）
//
//  引数 { kind }。返答 { kind, x, y, z, timestamp_ms, peak_magnitude, sample_count }。
//    x / y / z     … 最新の標本（m/s²。重力を除いた加速度。まだ無ければ 0）
//    timestamp_ms  … 最新の標本の時刻（UTC の epoch ミリ秒。SensorEvent.timestamp から換算〈sensor/SensorReading〉。まだ無ければ 0）
//    peak_magnitude・sample_count … 前回の read からの最大の大きさ √(x²+y²+z²) と標本の数。読むと 0 に戻る
//  毎フレーム呼ばれる前提なのでログを出さない。標本ごとのイベントは流さない（ここで取る）。
//  失敗: invalid_argument・not_started（start していない・stop の後）。前面から外れている間は ok で標本の数 0。
// ============================================================

package com.seedengine.runtime.platform.local;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.sensor.SensorFeeds;
import com.seedengine.runtime.platform.sensor.SensorReading;

import org.json.JSONObject;

/**
 * sensor.read。
 */
final class SensorReadCommand implements MainProcessCommand {

    @Override
    public byte[] handle(JSONObject arguments) {
        SensorArguments.Parsed parsed = SensorArguments.readKind(arguments);
        if (parsed.errorReply != null) {
            return parsed.errorReply;
        }
        SensorReading reading = SensorFeeds.read(parsed.kind);
        if (reading == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_NOT_STARTED);
        }
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_SENSOR_KIND, parsed.kind.wireName);
        PlatformJson.put(fields, PlatformContract.KEY_SENSOR_X, reading.x);
        PlatformJson.put(fields, PlatformContract.KEY_SENSOR_Y, reading.y);
        PlatformJson.put(fields, PlatformContract.KEY_SENSOR_Z, reading.z);
        PlatformJson.put(fields, PlatformContract.KEY_SENSOR_TIMESTAMP_MS, reading.timestampEpochMillis());
        PlatformJson.put(fields, PlatformContract.KEY_SENSOR_PEAK_MAGNITUDE, reading.peakMagnitude);
        PlatformJson.put(fields, PlatformContract.KEY_SENSOR_SAMPLE_COUNT, reading.sampleCount);
        return PlatformJson.okReply(fields);
    }
}
