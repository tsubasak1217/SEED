// ============================================================
//  RingControl.java — 鳴動の外からの入口（受信機・命令のモジュールが使う。:seed_platform。W1-4a）
//
//  alarm/ パッケージ（AlarmReceiver・AlarmModule）から ring/ の中身（RingRegistry・RingService）を触るときは、ここだけを通す。
//    startService … AlarmReceiver の先頭で**真っ先に** startForegroundService（一時許可は 10 秒。控えの fsync より前）。
//                   起こせなければ false（呼び出し側が alarm.missed(start_failed) を記録する）
//    handOver     … 控えの予約を RingRegistry へ渡す（鳴らし始めるか、鳴動中なら待ち行列へ）。AlarmBook.fire が
//                   alarm.fired を記録する**前**に呼ぶ（fired を受けたアプリの GetRinging が必ずその鳴動を返すように）。記録はしない
//    announce     … handOver の後始末（待たせたなら alarm.queued を記録＝fired の後に並ぶ。動いているサービスへ同期を投げる）
//    ringingJson  … alarm.get_ringing の中身
//    stop         … alarm.stop_ringing（止めて alarm.ring_stopped(stopped) を記録し、サービスを合わせ直す）
//    onSafetyValve… 安全弁の時間切れ（RingService から。alarm.ring_stopped(timeout)）
//    persist      … RingRegistry の写しを ringing.json へ書く（W1-7。状態を変えた直後・記録の前に。殺されても見張りが戻せるように）
//  記録（EventRecorder の端末保護ストレージへの書き込み）と写しの書き込みは RingRegistry の lock の外で行う。
//  写しは「変えた → 書く → 記録する」の順（書く前に殺されたら記録も無いので、見張りが前の状態から鳴らし直しても食い違わない）。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import android.content.Context;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.service.alarm.AlarmEntry;
import com.seedengine.runtime.platform.service.alarm.AlarmEvents;

import org.json.JSONObject;

import java.util.List;

/**
 * 鳴動の入口（static のみ）。
 */
public final class RingControl {

    private RingControl() {
    }

    /** handOver の結果（announce へ渡す）。 */
    public static final class HandOver {
        /** 待たせたときの、今鳴っている予約の ID（鳴り始めたなら null）。 */
        final String waitingFor;

        HandOver(String waitingFor) {
            this.waitingFor = waitingFor;
        }
    }

    /** stop の結果。 */
    public static final class StopResult {
        /** 何かを止めたか（鳴っていなければ false。それでも命令は成功＝冪等）。 */
        public final boolean stopped;
        /** 止めた予約の ID（止めなければ要求された ID）。 */
        public final String id;

        StopResult(boolean stopped, String id) {
            this.stopped = stopped;
            this.id = id;
        }
    }

    /**
     * 鳴動の前景サービスを起こす（AlarmReceiver の先頭。setAlarmClock の配信に付く 10 秒の一時許可の間に）。
     * 続けて引き継ぎのウェイクロックを取る（受信機が返ってからサービスが動くまでに端末が眠らないように）。
     *
     * @param context :seed_platform の Context
     * @param alarmId 配信された予約の ID（ログ用）
     * @return 起こせたら true（例外は捕まえて false）
     */
    public static boolean startService(Context context, String alarmId) {
        try {
            context.startForegroundService(RingService.startIntent(context, alarmId));
        } catch (RuntimeException e) {
            // ForegroundServiceStartNotAllowedException（API 31。IllegalStateException の子）・SecurityException など。
            // 例外の型を名前で参照しない（minSdk 29 の端末にはクラスが無い）
            Log.e(PlatformContract.LOG_TAG, "鳴動の前景サービスを起こせませんでした（目覚まし " + alarmId + "）: "
                    + PlatformJson.describe(e));
            return false;
        }
        RingWakeLock.acquire(context, RingWakeLock.HANDOVER_TIMEOUT_MS);
        return true;
    }

    /**
     * 控えの予約を鳴動へ渡す（AlarmBook.fire の中から。alarm.fired を記録する前）。鳴っていなければ鳴らし始め、
     * 鳴動中なら待ち行列へ入れる。値の出し入れだけで記録（ファイル）には触らない（AlarmBook の lock の中で呼ばれるため）。
     * サービスの onStartCommand（AlarmReceiver の onReceive の後に同じスレッドで走る）が音を出す。
     *
     * @param entry        配信された予約
     * @param firedAtUtcMs 配信を受けた時刻
     * @return 結果（announce へ渡す）
     */
    public static HandOver handOver(AlarmEntry entry, long firedAtUtcMs) {
        RingRegistry.Offer offer = RingRegistry.offer(entry, firedAtUtcMs, System.currentTimeMillis());
        return new HandOver(offer.queued() ? offer.waitingFor : null);
    }

    /**
     * handOver の後始末（AlarmReceiver から。alarm.fired の記録の後）: 待たせたなら alarm.queued を記録し、
     * 既に動いているサービスへ同期を投げる（待ち行列に入っただけなら何も変わらない）。
     *
     * @param context  :seed_platform の Context
     * @param entry    渡した予約
     * @param handOver handOver の結果
     */
    public static void announce(Context context, AlarmEntry entry, HandOver handOver) {
        // 渡した状態を控える（W1-7。ここから先に殺されても見張りが鳴らし直せる。handOver は AlarmBook の lock の中なので書けない）
        persist(context);
        if (handOver.waitingFor != null) {
            AlarmEvents.recordQueued(context, entry, handOver.waitingFor);
        }
        RingService.requestSync();
    }

    /**
     * 今の RingRegistry の写しを ringing.json へ書く（W1-7。状態を変えた直後に、lock の外で呼ぶ。版の古い写しは書かれない）。
     * 前のプロセスの鳴動（見張りで戻す前）を新しい鳴動で置き換えたときは、その予約を「終わった（error）」と記録する。
     *
     * @param context :seed_platform の Context
     */
    static void persist(Context context) {
        List<AlarmEntry> superseded = RingStateStore.saveRegistry(context, RingRegistry.snapshot());
        for (AlarmEntry entry : superseded) {
            Log.w(PlatformContract.LOG_TAG, "前のプロセスの鳴動 " + entry.id + " は戻す前に新しい鳴動に置き換わったので、終わったと記録します");
            AlarmEvents.recordRingStopped(context, entry, PlatformContract.RING_STOP_REASON_ERROR);
        }
    }

    /**
     * alarm.get_ringing の中身（{id, scheduled_at_utc_ms, started_at_utc_ms, payload_json}）。
     *
     * @return 鳴動中の予約（鳴っていなければ null）
     */
    public static JSONObject ringingJson() {
        RingSession current = RingRegistry.current();
        return current != null ? current.toRingingJson() : null;
    }

    /**
     * 鳴動を止める（alarm.stop_ringing。Binder のスレッドから）。止めたら alarm.ring_stopped(stopped) を記録し、
     * 待ち行列があれば次を鳴らす（サービスが合わせ直す）。
     *
     * @param context   :seed_platform の Context
     * @param idOrEmpty 予約の ID（空文字 = 今鳴っているもの）
     * @return 結果
     */
    public static StopResult stop(Context context, String idOrEmpty) {
        RingRegistry.Stop stop = RingRegistry.stop(idOrEmpty, System.currentTimeMillis());
        if (!stop.any()) {
            Log.i(PlatformContract.LOG_TAG, "鳴動を止める命令: 止めるものがありません（" + (idOrEmpty.isEmpty() ? "鳴動中のもの" : idOrEmpty) + "）");
            return new StopResult(false, idOrEmpty);
        }
        // 変えた → 控える → 記録する（控える前に殺されたら記録も無いので、見張りが鳴らし直してもアプリの知る状態と食い違わない）
        persist(context);
        AlarmEvents.recordRingStopped(context, stop.stopped, PlatformContract.RING_STOP_REASON_STOPPED);
        if (stop.wasRinging) {
            RingService.requestSync();
        }
        return new StopResult(true, stop.stopped.id);
    }

    /**
     * 安全弁の時間切れ（RingService の UI スレッドのタイマー）。その鳴動がまだ鳴っていれば止め、alarm.ring_stopped(timeout) を記録する。
     *
     * @param context :seed_platform の Context
     * @param serial  時間切れになった鳴動の通し番号
     */
    static void onSafetyValve(Context context, long serial) {
        RingRegistry.Stop stop = RingRegistry.stopSession(serial, System.currentTimeMillis());
        if (!stop.any()) {
            return;
        }
        Log.w(PlatformContract.LOG_TAG, "目覚まし " + stop.stopped.id + " は安全弁（" + stop.stopped.maxRingMinutes + " 分）で止めました");
        persist(context);
        AlarmEvents.recordRingStopped(context, stop.stopped, PlatformContract.RING_STOP_REASON_TIMEOUT);
        RingService.requestSync();
    }
}
