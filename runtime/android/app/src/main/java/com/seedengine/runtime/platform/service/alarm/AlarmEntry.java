// ============================================================
//  AlarmEntry.java — 目覚ましの予約 1 件（控えの 1 行。W1-3）
//
//  スクリプトの AlarmRequest（C#）を :seed_platform が受け付けた形。値は AlarmRequestReader が検査・正規化してから作る
//  （ここは作った後に変えない値と、控えのファイル・list の返答の JSON との相互変換だけを持つ）。
//  JSON の欄の名前は PlatformContract.KEY_ALARM_*（Rust の wire::alarm・C# の AlarmJson と一致させる）。
//  鳴らし方の欄（sound_path・vibrate・force_volume・keep_volume・fade_in_seconds・max_ring_minutes・title・body）は
//  鳴動（alarm/ring/ の RingService。W1-4a）が使う。ring/ は別のパッケージなので、型と欄は public（書き換えられない final）。
//  JSON との相互変換（toJson・fromStoredJson）も、鳴動の状態の控え（ring/RingSnapshot の ringing.json。W1-7）が使うので public。
// ============================================================

package com.seedengine.runtime.platform.service.alarm;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

import org.json.JSONException;
import org.json.JSONObject;

/**
 * 予約 1 件（不変。作るのはこのパッケージの AlarmRequestReader と控えの読み込みだけ）。
 */
public final class AlarmEntry {

    /** 予約の ID（アプリが決める。同じ ID は置き換え）。 */
    public final String id;
    /** 鳴らす時刻（UTC の epoch ミリ秒）。 */
    public final long triggerAtUtcMs;
    /** 音源の端末のファイルの絶対パス（空なら既定の音）。 */
    public final String soundPath;
    /** バイブするか。 */
    public final boolean vibrate;
    /** 鳴っている間のアラームの音量（0..1。PlatformContract.ALARM_VOLUME_UNCHANGED なら触らない）。 */
    public final double forceVolume;
    /** 利用者が音量を下げても戻すか。 */
    public final boolean keepVolume;
    /** 音量の漸増の秒（0 以上）。 */
    public final double fadeInSeconds;
    /** 鳴り続ける上限の分（1 以上）。 */
    public final int maxRingMinutes;
    /** 鳴動の通知の題。 */
    public final String title;
    /** 鳴動の通知の本文。 */
    public final String body;
    /** アプリの任意の JSON（文字列のまま返す）。 */
    public final String payloadJson;
    /** 予約を受け付けた時刻（UTC の epoch ミリ秒。診断と、同じ ID の予約の見分けに使う）。 */
    public final long createdAtUtcMs;

    /** 控えの欄をそのまま並べて作る（値の検査・正規化は AlarmRequestReader が済ませてから呼ぶ）。 */
    AlarmEntry(String id, long triggerAtUtcMs, String soundPath, boolean vibrate, double forceVolume, boolean keepVolume,
               double fadeInSeconds, int maxRingMinutes, String title, String body, String payloadJson, long createdAtUtcMs) {
        this.id = id;
        this.triggerAtUtcMs = triggerAtUtcMs;
        this.soundPath = soundPath;
        this.vibrate = vibrate;
        this.forceVolume = forceVolume;
        this.keepVolume = keepVolume;
        this.fadeInSeconds = fadeInSeconds;
        this.maxRingMinutes = maxRingMinutes;
        this.title = title;
        this.body = body;
        this.payloadJson = payloadJson;
        this.createdAtUtcMs = createdAtUtcMs;
    }

    /**
     * 同じ予約か（ID・予定時刻・受け付けた時刻が同じ）。発火を処理する間に同じ ID で予約し直されていないかを見分ける。
     *
     * @param other 比べる相手
     * @return 同じ予約なら true
     */
    public boolean isSameReservation(AlarmEntry other) {
        return other != null && id.equals(other.id) && triggerAtUtcMs == other.triggerAtUtcMs
                && createdAtUtcMs == other.createdAtUtcMs;
    }

    /**
     * 控えのファイル・list の返答の 1 行（全部の欄）にする。
     *
     * @return JSON のオブジェクト
     */
    public JSONObject toJson() {
        JSONObject json = new JSONObject();
        PlatformJson.put(json, PlatformContract.KEY_ALARM_ID, id);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_TRIGGER_AT_UTC_MS, triggerAtUtcMs);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_SOUND_PATH, soundPath);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_VIBRATE, vibrate);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_FORCE_VOLUME, forceVolume);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_KEEP_VOLUME, keepVolume);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_FADE_IN_SECONDS, fadeInSeconds);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_MAX_RING_MINUTES, maxRingMinutes);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_TITLE, title);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_BODY, body);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_PAYLOAD_JSON, payloadJson);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_CREATED_AT_UTC_MS, createdAtUtcMs);
        return json;
    }

    /**
     * 控えのファイルの 1 行から作る（自分で書いたものなので、ID と予定時刻が無ければ壊れているとみなす）。
     *
     * @param json 控えの 1 行
     * @return 予約
     * @throws JSONException ID・予定時刻が無い
     */
    public static AlarmEntry fromStoredJson(JSONObject json) throws JSONException {
        return new AlarmEntry(
                json.getString(PlatformContract.KEY_ALARM_ID),
                json.getLong(PlatformContract.KEY_ALARM_TRIGGER_AT_UTC_MS),
                json.optString(PlatformContract.KEY_ALARM_SOUND_PATH, ""),
                json.optBoolean(PlatformContract.KEY_ALARM_VIBRATE, PlatformContract.DEFAULT_ALARM_VIBRATE),
                json.optDouble(PlatformContract.KEY_ALARM_FORCE_VOLUME, PlatformContract.ALARM_VOLUME_UNCHANGED),
                json.optBoolean(PlatformContract.KEY_ALARM_KEEP_VOLUME, PlatformContract.DEFAULT_ALARM_KEEP_VOLUME),
                json.optDouble(PlatformContract.KEY_ALARM_FADE_IN_SECONDS, PlatformContract.DEFAULT_ALARM_FADE_IN_SECONDS),
                json.optInt(PlatformContract.KEY_ALARM_MAX_RING_MINUTES, PlatformContract.DEFAULT_ALARM_MAX_RING_MINUTES),
                json.optString(PlatformContract.KEY_ALARM_TITLE, ""),
                json.optString(PlatformContract.KEY_ALARM_BODY, ""),
                json.optString(PlatformContract.KEY_ALARM_PAYLOAD_JSON, ""),
                json.optLong(PlatformContract.KEY_ALARM_CREATED_AT_UTC_MS, 0));
    }
}
