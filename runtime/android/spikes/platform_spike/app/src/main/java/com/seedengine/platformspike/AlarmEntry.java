package com.seedengine.platformspike;

import org.json.JSONException;
import org.json.JSONObject;

/** 予約の控えの 1 件（予約 ID・予定時刻・鳴らし方）。控えのファイルとの JSON の相互変換を持つ。 */
final class AlarmEntry {
    private static final String KEY_ID = "id";
    private static final String KEY_TRIGGER_AT = "trigger_at";
    private static final String KEY_FGS_TYPE = "fgs_type";
    private static final String KEY_MAX_RING_SECONDS = "max_ring_s";

    final String id;
    /** 予定時刻（UTC の epoch ミリ秒）。 */
    final long triggerAtMs;
    /** 鳴動の前景サービスの種類（SpikeContract.FGS_TYPE_*）。 */
    final String fgsType;
    /** 安全弁（秒）。 */
    final int maxRingSeconds;

    AlarmEntry(String id, long triggerAtMs, String fgsType, int maxRingSeconds) {
        this.id = id;
        this.triggerAtMs = triggerAtMs;
        this.fgsType = fgsType;
        this.maxRingSeconds = maxRingSeconds;
    }

    JSONObject toJson() throws JSONException {
        return new JSONObject()
                .put(KEY_ID, id)
                .put(KEY_TRIGGER_AT, triggerAtMs)
                .put(KEY_FGS_TYPE, fgsType)
                .put(KEY_MAX_RING_SECONDS, maxRingSeconds);
    }

    static AlarmEntry fromJson(JSONObject json) throws JSONException {
        return new AlarmEntry(
                json.getString(KEY_ID),
                json.getLong(KEY_TRIGGER_AT),
                json.optString(KEY_FGS_TYPE, SpikeContract.FGS_TYPE_MEDIA_PLAYBACK),
                json.optInt(KEY_MAX_RING_SECONDS, SpikeContract.DEFAULT_MAX_RING_SECONDS));
    }
}
