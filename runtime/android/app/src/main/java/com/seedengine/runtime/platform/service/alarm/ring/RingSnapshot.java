// ============================================================
//  RingSnapshot.java — 鳴動の状態（RingRegistry）の写し 1 つ（不変。ringing.json の registry の欄との相互変換。W1-7）
//
//  RingRegistry は :seed_platform のメモリにしかないので、プロセスごと殺されると鳴動が黙って消えた（W1-4b の T5）。
//  そこで状態が変わるたびに、その時点の写しをこの形で作って RingStateStore が端末保護ストレージへ書き、見張りの予約
//  （RingWatchdog）の発火で読み戻して鳴らし直す（RingRecovery）。
//
//  【欄】current … 鳴っている鳴動（予約・配信を受けた時刻・鳴り始めの時刻・戻した回数）。無ければ null
//        queue   … 待ち行列（予約・配信を受けた時刻。配信の順）
//        version … 写しを作った時点の RingRegistry の版（プロセスの中で増える）。別のスレッドが作った古い写しで
//                  新しい写しを上書きしないため（RingStateStore が比べる）。ファイルには書かない（プロセスをまたいで意味が無い）
//  【JSON】{ "current": { "entry": AlarmEntry.toJson(), "fired_at_utc_ms", "started_at_utc_ms", "restore_count" } | null,
//            "queue": [ { "entry": …, "fired_at_utc_ms" }, … ] }
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.service.alarm.AlarmEntry;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * 鳴動の状態の写し（不変）。
 */
final class RingSnapshot {

    /** ファイルから読んだ写しの版（このプロセスの RingRegistry のどの版よりも古い）。 */
    static final long LOADED_VERSION = -1L;

    /** 欄: 鳴っている鳴動。 */
    private static final String KEY_CURRENT = "current";

    /** 欄: 待ち行列。 */
    private static final String KEY_QUEUE = "queue";

    /** 欄: 予約（AlarmEntry.toJson の形）。 */
    private static final String KEY_ENTRY = "entry";

    /** 欄: 戻した回数（見張りで鳴らし直した回数。上限は RingRecovery）。 */
    private static final String KEY_RESTORE_COUNT = "restore_count";

    /** 戻した回数の欄が無いときの値（最初の鳴動）。 */
    private static final int NO_RESTORES = 0;

    /** 鳴っている鳴動の写し。 */
    static final class Current {
        /** 予約。 */
        final AlarmEntry entry;
        /** 配信を受けた時刻（UTC の epoch ミリ秒）。 */
        final long firedAtUtcMs;
        /** 鳴り始めた時刻（UTC の epoch ミリ秒。戻しても変えない＝安全弁の時刻は最初の鳴り始めから数える）。 */
        final long startedAtUtcMs;
        /** 見張りで戻した回数（最初の鳴動は 0）。 */
        final int restoreCount;

        Current(AlarmEntry entry, long firedAtUtcMs, long startedAtUtcMs, int restoreCount) {
            this.entry = entry;
            this.firedAtUtcMs = firedAtUtcMs;
            this.startedAtUtcMs = startedAtUtcMs;
            this.restoreCount = restoreCount;
        }

        /**
         * 安全弁で止める時刻（鳴り始め＋max_ring_minutes。RingSession.deadlineUtcMs と同じ）。
         *
         * @return UTC の epoch ミリ秒
         */
        long deadlineUtcMs() {
            return startedAtUtcMs + entry.maxRingMinutes * PlatformContract.MILLIS_PER_MINUTE;
        }
    }

    /** 待ち行列の 1 件の写し。 */
    static final class Waiting {
        /** 予約。 */
        final AlarmEntry entry;
        /** 配信を受けた時刻（UTC の epoch ミリ秒）。 */
        final long firedAtUtcMs;

        Waiting(AlarmEntry entry, long firedAtUtcMs) {
            this.entry = entry;
            this.firedAtUtcMs = firedAtUtcMs;
        }
    }

    /** 写しを作った時点の RingRegistry の版（ファイルから読んだものは LOADED_VERSION）。 */
    final long version;
    /** 鳴っている鳴動（無ければ null）。 */
    final Current current;
    /** 待ち行列（変えられないリスト）。 */
    final List<Waiting> queue;

    /**
     * @param version 版
     * @param current 鳴っている鳴動（null 可）
     * @param queue   待ち行列（写してから持つ）
     */
    RingSnapshot(long version, Current current, List<Waiting> queue) {
        this.version = version;
        this.current = current;
        this.queue = Collections.unmodifiableList(new ArrayList<>(queue));
    }

    /**
     * 何も鳴っておらず待ち行列も空か。
     *
     * @return 空なら true
     */
    boolean isEmpty() {
        return current == null && queue.isEmpty();
    }

    /**
     * 写しの中の予約をすべて（鳴っているもの → 待ち行列の順）。
     *
     * @return 予約の一覧（新しいリスト）
     */
    List<AlarmEntry> entries() {
        List<AlarmEntry> all = new ArrayList<>();
        if (current != null) {
            all.add(current.entry);
        }
        for (Waiting waiting : queue) {
            all.add(waiting.entry);
        }
        return all;
    }

    /**
     * ringing.json の registry の欄にする。
     *
     * @return JSON のオブジェクト
     */
    JSONObject toJson() {
        JSONObject json = new JSONObject();
        if (current != null) {
            JSONObject row = new JSONObject();
            PlatformJson.put(row, KEY_ENTRY, current.entry.toJson());
            PlatformJson.put(row, PlatformContract.KEY_ALARM_FIRED_AT_UTC_MS, current.firedAtUtcMs);
            PlatformJson.put(row, PlatformContract.KEY_ALARM_STARTED_AT_UTC_MS, current.startedAtUtcMs);
            PlatformJson.put(row, KEY_RESTORE_COUNT, current.restoreCount);
            PlatformJson.put(json, KEY_CURRENT, row);
        } else {
            PlatformJson.put(json, KEY_CURRENT, JSONObject.NULL);
        }
        JSONArray rows = new JSONArray();
        for (Waiting waiting : queue) {
            JSONObject row = new JSONObject();
            PlatformJson.put(row, KEY_ENTRY, waiting.entry.toJson());
            PlatformJson.put(row, PlatformContract.KEY_ALARM_FIRED_AT_UTC_MS, waiting.firedAtUtcMs);
            rows.put(row);
        }
        PlatformJson.put(json, KEY_QUEUE, rows);
        return json;
    }

    /**
     * ringing.json の registry の欄から作る（版は LOADED_VERSION）。
     *
     * @param json registry の欄
     * @return 写し
     * @throws JSONException 形が違う（予約の ID・予定時刻が無い等）
     */
    static RingSnapshot fromJson(JSONObject json) throws JSONException {
        Current current = null;
        JSONObject row = json.optJSONObject(KEY_CURRENT);
        if (row != null) {
            current = new Current(AlarmEntry.fromStoredJson(row.getJSONObject(KEY_ENTRY)),
                    row.getLong(PlatformContract.KEY_ALARM_FIRED_AT_UTC_MS),
                    row.getLong(PlatformContract.KEY_ALARM_STARTED_AT_UTC_MS),
                    row.optInt(KEY_RESTORE_COUNT, NO_RESTORES));
        }
        List<Waiting> queue = new ArrayList<>();
        JSONArray rows = json.optJSONArray(KEY_QUEUE);
        int count = rows != null ? rows.length() : 0;
        for (int i = 0; i < count; i++) {
            JSONObject waiting = rows.getJSONObject(i);
            queue.add(new Waiting(AlarmEntry.fromStoredJson(waiting.getJSONObject(KEY_ENTRY)),
                    waiting.getLong(PlatformContract.KEY_ALARM_FIRED_AT_UTC_MS)));
        }
        return new RingSnapshot(LOADED_VERSION, current, queue);
    }

    @Override
    public String toString() {
        return "鳴動 " + (current != null ? current.entry.id + "（戻した回数 " + current.restoreCount + "）" : "なし")
                + "・待ち " + queue.size() + " 件";
    }
}
