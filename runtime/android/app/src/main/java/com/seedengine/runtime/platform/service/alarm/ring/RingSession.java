// ============================================================
//  RingSession.java — 鳴動 1 回ぶん（どの予約を・いつから鳴らしているか。W1-4a）
//
//  RingRegistry が「今鳴っているもの」として持つ不変の値。serial は鳴動ごとの通し番号で、安全弁の時間切れや
//  サービスの同期が「別の鳴動に入れ替わった後の古い知らせ」を見分けるために使う（同じ予約 ID でも鳴動が違えば別の番号）。
//  get_ringing の返答の形（{id, scheduled_at_utc_ms, started_at_utc_ms, payload_json}）もここで作る
//  （Rust の模擬 desktop_sim/ring_state.rs の SimRinging と同じ欄）。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.service.alarm.AlarmEntry;

import org.json.JSONObject;

/**
 * 鳴動 1 回（不変）。
 */
final class RingSession {

    /** 鳴動の通し番号（プロセスの中で 1 から増える）。 */
    final long serial;
    /** 鳴らしている予約（鳴らし方の欄を含む）。 */
    final AlarmEntry entry;
    /** AlarmManager の配信を受けた時刻（UTC の epoch ミリ秒）。 */
    final long firedAtUtcMs;
    /** 鳴り始めた時刻（UTC の epoch ミリ秒。待ち行列から繰り上がったときはその時刻）。 */
    final long startedAtUtcMs;

    /**
     * @param serial         通し番号
     * @param entry          予約
     * @param firedAtUtcMs   配信を受けた時刻
     * @param startedAtUtcMs 鳴り始めた時刻
     */
    RingSession(long serial, AlarmEntry entry, long firedAtUtcMs, long startedAtUtcMs) {
        this.serial = serial;
        this.entry = entry;
        this.firedAtUtcMs = firedAtUtcMs;
        this.startedAtUtcMs = startedAtUtcMs;
    }

    /**
     * 安全弁で止める時刻（鳴り始め＋max_ring_minutes）。
     *
     * @return UTC の epoch ミリ秒
     */
    long deadlineUtcMs() {
        return startedAtUtcMs + entry.maxRingMinutes * PlatformContract.MILLIS_PER_MINUTE;
    }

    /**
     * get_ringing の返答の ringing（{id, scheduled_at_utc_ms, started_at_utc_ms, payload_json}）。
     *
     * @return JSON のオブジェクト
     */
    JSONObject toRingingJson() {
        JSONObject json = new JSONObject();
        PlatformJson.put(json, PlatformContract.KEY_ALARM_ID, entry.id);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_SCHEDULED_AT_UTC_MS, entry.triggerAtUtcMs);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_STARTED_AT_UTC_MS, startedAtUtcMs);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_PAYLOAD_JSON, entry.payloadJson);
        return json;
    }

    @Override
    public String toString() {
        return entry.id + "#" + serial;
    }
}
