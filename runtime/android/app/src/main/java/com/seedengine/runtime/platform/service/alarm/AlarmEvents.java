// ============================================================
//  AlarmEvents.java — 目覚ましのイベントの中身を作って記録する（W1-3。W1-4a で鳴動の 2 つを追加）
//
//    platform.alarm.fired        { id, scheduled_at_utc_ms, fired_at_utc_ms, payload_json }
//    platform.alarm.missed       { id, scheduled_at_utc_ms, reason, payload_json }   reason = device_off / permission_revoked / start_failed
//    platform.alarms.rescheduled { reason, count, missed, failed }                   reason = boot / time_changed / package_replaced / permission_changed
//    platform.alarm.ring_stopped { id, reason, scheduled_at_utc_ms, payload_json }   reason = stopped / timeout / error（W1-4a）
//    platform.alarm.queued       { id, scheduled_at_utc_ms, waiting_for, payload_json }（W1-4a。別の予約の鳴動中に時刻が来た）
//  scheduled_at_utc_ms は「鳴るはずだった時刻」（予約の trigger_at_utc_ms）。記録は EventRecorder（端末保護ストレージの記録＋
//  エンジンへの呼び鈴）。エンジンが居なければ、次にアプリが SEED.Platform へつないだときに届く。
//  名前・欄は PlatformContract（Rust の wire::alarm・C# の AlarmFiredEvent 等と一致させる）。
//  鳴動（別パッケージの alarm/ring/）からも記録するので public。
// ============================================================

package com.seedengine.runtime.platform.service.alarm;

import android.content.Context;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.service.EventRecorder;

import org.json.JSONObject;

/**
 * 目覚ましのイベントの記録（static のみ）。
 */
public final class AlarmEvents {

    private AlarmEvents() {
    }

    /**
     * 鳴った（AlarmManager から配信された）ことを記録する。
     *
     * @param context   :seed_platform の Context
     * @param entry     鳴った予約
     * @param firedAtMs 配信を受けた時刻（UTC の epoch ミリ秒）
     * @return 記録の結果
     */
    static EventRecorder.Recorded recordFired(Context context, AlarmEntry entry, long firedAtMs) {
        JSONObject data = new JSONObject();
        PlatformJson.put(data, PlatformContract.KEY_ALARM_ID, entry.id);
        PlatformJson.put(data, PlatformContract.KEY_ALARM_SCHEDULED_AT_UTC_MS, entry.triggerAtUtcMs);
        PlatformJson.put(data, PlatformContract.KEY_ALARM_FIRED_AT_UTC_MS, firedAtMs);
        PlatformJson.put(data, PlatformContract.KEY_ALARM_PAYLOAD_JSON, entry.payloadJson);
        EventRecorder.Recorded recorded = EventRecorder.record(context, PlatformContract.EVENT_ALARM_FIRED, data);
        Log.i(PlatformContract.LOG_TAG, "目覚まし " + entry.id + " が鳴りました（予定から " + (firedAtMs - entry.triggerAtUtcMs)
                + " ms・seq " + recorded.seq + "・呼び鈴 " + (recorded.doorbellRang ? "鳴らした" : "相手なし") + "）");
        return recorded;
    }

    /**
     * 鳴らなかった（予定時刻を過ぎていた）ことを記録する。
     *
     * @param context :seed_platform の Context
     * @param entry   鳴らなかった予約
     * @param reason  理由（PlatformContract.MISSED_REASON_*）
     * @return 記録の結果
     */
    static EventRecorder.Recorded recordMissed(Context context, AlarmEntry entry, String reason) {
        JSONObject data = new JSONObject();
        PlatformJson.put(data, PlatformContract.KEY_ALARM_ID, entry.id);
        PlatformJson.put(data, PlatformContract.KEY_ALARM_SCHEDULED_AT_UTC_MS, entry.triggerAtUtcMs);
        PlatformJson.put(data, PlatformContract.KEY_REASON, reason);
        PlatformJson.put(data, PlatformContract.KEY_ALARM_PAYLOAD_JSON, entry.payloadJson);
        EventRecorder.Recorded recorded = EventRecorder.record(context, PlatformContract.EVENT_ALARM_MISSED, data);
        Log.w(PlatformContract.LOG_TAG, "目覚まし " + entry.id + " は鳴りませんでした（予定 " + entry.triggerAtUtcMs
                + "・理由 " + reason + "・seq " + recorded.seq + "）");
        return recorded;
    }

    /**
     * 予約を張り直したことを記録する。
     *
     * @param context :seed_platform の Context
     * @param reason  理由（PlatformContract.RESCHEDULE_REASON_*）
     * @param count   張り直した件数
     * @param missed  鳴らなかったと記録した件数
     * @param failed  張り直せなかった件数（控えには残した）
     * @return 記録の結果
     */
    static EventRecorder.Recorded recordRescheduled(Context context, String reason, int count, int missed, int failed) {
        JSONObject data = new JSONObject();
        PlatformJson.put(data, PlatformContract.KEY_REASON, reason);
        PlatformJson.put(data, PlatformContract.KEY_COUNT, count);
        PlatformJson.put(data, PlatformContract.KEY_MISSED, missed);
        PlatformJson.put(data, PlatformContract.KEY_FAILED, failed);
        return EventRecorder.record(context, PlatformContract.EVENT_ALARMS_RESCHEDULED, data);
    }

    /**
     * 鳴動が終わったことを記録する（W1-4a。アプリの停止・安全弁・続けられなかった）。
     *
     * @param context :seed_platform の Context
     * @param entry   鳴っていた（か待ち行列にいた）予約
     * @param reason  理由（PlatformContract.RING_STOP_REASON_*）
     * @return 記録の結果
     */
    public static EventRecorder.Recorded recordRingStopped(Context context, AlarmEntry entry, String reason) {
        JSONObject data = new JSONObject();
        PlatformJson.put(data, PlatformContract.KEY_ALARM_ID, entry.id);
        PlatformJson.put(data, PlatformContract.KEY_REASON, reason);
        PlatformJson.put(data, PlatformContract.KEY_ALARM_SCHEDULED_AT_UTC_MS, entry.triggerAtUtcMs);
        PlatformJson.put(data, PlatformContract.KEY_ALARM_PAYLOAD_JSON, entry.payloadJson);
        EventRecorder.Recorded recorded = EventRecorder.record(context, PlatformContract.EVENT_ALARM_RING_STOPPED, data);
        Log.i(PlatformContract.LOG_TAG, "目覚まし " + entry.id + " の鳴動が終わりました（理由 " + reason + "・seq " + recorded.seq
                + "・呼び鈴 " + (recorded.doorbellRang ? "鳴らした" : "相手なし") + "）");
        return recorded;
    }

    /**
     * 別の予約の鳴動中に時刻が来たので待たせたことを記録する（W1-4a。捨てずに、今の鳴動が止まったら続けて鳴らす）。
     *
     * @param context    :seed_platform の Context
     * @param entry      待たせた予約
     * @param waitingFor 今鳴っている予約の ID
     * @return 記録の結果
     */
    public static EventRecorder.Recorded recordQueued(Context context, AlarmEntry entry, String waitingFor) {
        JSONObject data = new JSONObject();
        PlatformJson.put(data, PlatformContract.KEY_ALARM_ID, entry.id);
        PlatformJson.put(data, PlatformContract.KEY_ALARM_SCHEDULED_AT_UTC_MS, entry.triggerAtUtcMs);
        PlatformJson.put(data, PlatformContract.KEY_ALARM_WAITING_FOR, waitingFor);
        PlatformJson.put(data, PlatformContract.KEY_ALARM_PAYLOAD_JSON, entry.payloadJson);
        EventRecorder.Recorded recorded = EventRecorder.record(context, PlatformContract.EVENT_ALARM_QUEUED, data);
        Log.i(PlatformContract.LOG_TAG, "目覚まし " + entry.id + " は " + waitingFor + " の鳴動が終わるまで待ちます（seq "
                + recorded.seq + "）");
        return recorded;
    }
}
