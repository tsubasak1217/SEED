// ============================================================
//  SensorStopCommand.java — sensor.stop（センサーの受け取りを止める。W1-8）
//
//  引数 { kind }。返答 { kind, stopped }（動いていたものを止めたか。動いていなくても ok=true＝冪等）。
//  登録を外し、たまった標本を捨てる（以後の read は not_started）。失敗は invalid_argument だけ。
// ============================================================

package com.seedengine.runtime.platform.local;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.sensor.SensorFeeds;

import org.json.JSONObject;

/**
 * sensor.stop。
 */
final class SensorStopCommand implements MainProcessCommand {

    @Override
    public byte[] handle(JSONObject arguments) {
        SensorArguments.Parsed parsed = SensorArguments.readKind(arguments);
        if (parsed.errorReply != null) {
            return parsed.errorReply;
        }
        boolean stopped = SensorFeeds.stop(parsed.kind);
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_SENSOR_KIND, parsed.kind.wireName);
        PlatformJson.put(fields, PlatformContract.KEY_SENSOR_STOPPED, stopped);
        return PlatformJson.okReply(fields);
    }
}
