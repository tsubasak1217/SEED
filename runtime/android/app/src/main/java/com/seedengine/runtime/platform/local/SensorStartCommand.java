// ============================================================
//  SensorStartCommand.java — sensor.start（センサーの受け取りを始める。W1-8）
//
//  引数 { kind, rate_hz }（規則は SensorArguments）。返答 { kind, supported: true, source, rate_hz }（source は sensor/SensorSource の
//  出どころの名前。rate_hz はそろえた後の値）。動いていれば標本を捨てて始め直す。前面にいなければ登録は onResume まで待つ（成功で返る）。
//  失敗: invalid_argument・not_initialized（アプリの Context が無い）・not_supported（使えるセンサーが無い）・register_failed。
//  中身は sensor/SensorFeeds.start（registerListener は sensorservice への Binder の呼び出し。エンジンのスレッドのまま同期に行う）。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.content.Context;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.sensor.SensorFeeds;
import com.seedengine.runtime.platform.sensor.SensorStartResult;

import org.json.JSONObject;

/**
 * sensor.start。
 */
final class SensorStartCommand implements MainProcessCommand {

    @Override
    public byte[] handle(JSONObject arguments) {
        SensorArguments.Parsed parsed = SensorArguments.readKindAndRate(arguments);
        if (parsed.errorReply != null) {
            return parsed.errorReply;
        }
        Context context = HostActivity.applicationContext();
        if (context == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_NOT_INITIALIZED);
        }
        SensorStartResult result = SensorFeeds.start(context, parsed.kind, parsed.rateHz);
        if (!result.ok()) {
            return PlatformJson.errorReply(result.error, result.detail);
        }
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_SENSOR_KIND, parsed.kind.wireName);
        PlatformJson.put(fields, PlatformContract.KEY_SENSOR_SUPPORTED, true);
        PlatformJson.put(fields, PlatformContract.KEY_SENSOR_SOURCE, result.source);
        PlatformJson.put(fields, PlatformContract.KEY_SENSOR_RATE_HZ, result.rateHz);
        return PlatformJson.okReply(fields);
    }
}
