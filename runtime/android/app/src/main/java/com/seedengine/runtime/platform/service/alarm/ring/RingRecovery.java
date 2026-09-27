// ============================================================
//  RingRecovery.java — :seed_platform が鳴動中に殺された後の鳴動の復元と後始末（W1-7。W1-4b の T5・G-6 を塞ぐ）
//
//  【見張りの発火（onWatchdog。RingWatchdogReceiver・UI スレッド）】
//    1. このプロセスで鳴っていれば（張り直しが遅れただけ）見張りを張り直して終わる
//    2. ringing.json（RingStateStore）の前のプロセスの鳴動を取り出す（takeRegistryIfSame。1 か所だけが取り出せる）
//    3. 鳴らし直した回数が上限（MAX_RESTORES_PER_SESSION）なら、全部「終わった（error）」と記録して元の音量へ戻す
//       （起動の直後に落ち続ける不具合で、前景サービスの起動を繰り返さない）
//    4. 安全弁の時刻を過ぎていれば「終わった（timeout）」と記録し、待ち行列の先頭を繰り上げる（鳴り始めは今）
//    5. 鳴らすものがあれば RingRegistry.restore → 控えを書き直す → RingService を起こす（正確な予約の配信に付く
//       前景サービスの一時許可の間に）。起こせなければ全部「終わった（error）」と記録して元の音量へ戻す
//       鳴らすものが無ければ元の音量へ戻すだけ
//  force_volume は RingService → RingAudio → AlarmStreamVolume.force が控えの元の値を使ってかけ直し、止めたときに元へ戻す。
//
//  【プロセスの起動・再起動（onStartup。AlarmStartup の背面のスレッド・BootReceiver の起動の放送）】
//  前のプロセスの控えが残っていて、このプロセスでは鳴っておらず、見張りも張られていない（強制停止・再起動で消えた）なら、
//  鳴動は戻せないので全部「終わった（error）」と記録して元の音量へ戻す。見張りが張られていれば（配信の途中を含む）任せる。
//
//  【同時に起きること】見張りの配信でプロセスが起きると、PlatformProvider.onCreate の照合（onStartup・背面のスレッド）と
//  受信機（onWatchdog・UI スレッド）が並ぶ。どちらもこのクラスの lock を取り、取り出しは RingStateStore の「同じ物なら」で
//  1 回だけにする。配信の途中は AlarmManager が PendingIntent を持っているので isArmed は true（onStartup は任せる）。
//  lock の順は RingRecovery → RingRegistry / RingStateStore / 記録（EventJournal）だけ（逆向きは無い）。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import android.content.Context;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.service.alarm.AlarmEntry;
import com.seedengine.runtime.platform.service.alarm.AlarmEvents;

import java.util.ArrayList;
import java.util.List;

/**
 * 鳴動の復元と後始末（static のみ）。
 */
public final class RingRecovery {

    private RingRecovery() {
    }

    /**
     * 1 つの鳴動を見張りで鳴らし直す回数の上限（プロセスが殺され続ける〈起動の直後に落ちる不具合など〉ときに、
     * 前景サービスの起動と通知を繰り返し続けないため）。上限を超えたら「終わった（error）」と記録する。
     */
    static final int MAX_RESTORES_PER_SESSION = 5;

    /** 待ち行列から繰り上げた鳴動の「鳴らし直した回数」。 */
    private static final int PROMOTED_RESTORE_COUNT = 0;

    /** 鳴らし直すたびに増やす数。 */
    private static final int RESTORE_COUNT_STEP = 1;

    /**
     * 見張りの予約が発火した（RingWatchdogReceiver。UI スレッド）。
     *
     * @param context :seed_platform の Context
     */
    static synchronized void onWatchdog(Context context) {
        RingSession alive = RingRegistry.current();
        if (alive != null) {
            // サービスは生きている（張り直しが遅れた）。先へ送るだけ
            Log.i(PlatformContract.LOG_TAG, "鳴動の見張りが発火しましたが、鳴動 " + alive + " は続いています（張り直します）");
            RingWatchdog.arm(context);
            return;
        }
        RingStateStore.Saved saved = RingStateStore.load(context);
        Log.w(PlatformContract.LOG_TAG, "鳴動の見張りが発火しました（前のプロセスの控え: " + saved + "）");
        if (saved.registry == null) {
            // 鳴動は無い（止めた後に音量を戻す前に殺された等）。音量だけ戻す
            AlarmStreamVolume.restoreLeftover(context, saved.volume);
            return;
        }
        if (!RingStateStore.takeRegistryIfSame(context, saved.registry)) {
            Log.i(PlatformContract.LOG_TAG, "前のプロセスの鳴動は既に片付いています");
            return;
        }
        restore(context, saved);
    }

    /**
     * :seed_platform のプロセスが起きた・端末が再起動した（AlarmStartup の背面のスレッド・BootReceiver）。
     * 前のプロセスの鳴動が残っていて見張りも無ければ、「終わった（error）」と記録して元の音量へ戻す。
     *
     * @param context :seed_platform の Context
     */
    public static synchronized void onStartup(Context context) {
        if (RingRegistry.current() != null) {
            return;
        }
        RingStateStore.Saved saved = RingStateStore.load(context);
        if (saved.isEmpty()) {
            return;
        }
        if (RingWatchdog.isArmed(context)) {
            Log.i(PlatformContract.LOG_TAG, "前のプロセスの鳴動の控えがあります（" + saved + "）。見張りの予約があるので任せます");
            return;
        }
        Log.w(PlatformContract.LOG_TAG, "前のプロセスの鳴動の控えがありますが、見張りの予約が無い（強制停止・再起動）ので戻せません（"
                + saved + "）");
        List<AlarmEntry> ended = saved.registry != null && RingStateStore.takeRegistryIfSame(context, saved.registry)
                ? saved.registry.entries() : new ArrayList<>();
        recordEnded(context, ended, PlatformContract.RING_STOP_REASON_ERROR);
        AlarmStreamVolume.restoreLeftover(context, saved.volume);
    }

    /**
     * 戻せずに残した音量の控え（鳴動は無い）を、もう一度戻してみる（メインプロセスからの呼び出しのたび。PlatformProvider →
     * AlarmStartup.onMainProcessCall）。アプリが前面にいれば（同じ uid の画面が見えている）背面の制限に当たらずに戻る。
     * 鳴っている・前のプロセスの鳴動が見張りを待っているときは触らない。
     *
     * @param context :seed_platform の Context
     */
    public static synchronized void retryLeftoverVolume(Context context) {
        if (RingRegistry.current() != null) {
            return;
        }
        RingStateStore.Saved saved = RingStateStore.load(context);
        if (saved.registry != null || saved.volume == null) {
            return;
        }
        AlarmStreamVolume.restoreLeftover(context, saved.volume);
    }

    /**
     * 取り出した前のプロセスの鳴動を鳴らし直す（lock を持って呼ぶ）。
     *
     * @param context :seed_platform の Context
     * @param saved   取り出した控え（registry は takeRegistryIfSame 済み）
     */
    private static void restore(Context context, RingStateStore.Saved saved) {
        long now = System.currentTimeMillis();
        RingSnapshot leftover = saved.registry;
        RingSnapshot.Current head = leftover.current;
        List<RingSnapshot.Waiting> waiting = new ArrayList<>(leftover.queue);

        // 3. 鳴らし直しの回数の上限
        if (head != null && head.restoreCount >= MAX_RESTORES_PER_SESSION) {
            Log.e(PlatformContract.LOG_TAG, "目覚まし " + head.entry.id + " は " + head.restoreCount
                    + " 回鳴らし直しても続かないので、終わったと記録します");
            recordEnded(context, leftover.entries(), PlatformContract.RING_STOP_REASON_ERROR);
            AlarmStreamVolume.restoreLeftover(context, saved.volume);
            return;
        }

        // 4. 安全弁の時刻を過ぎていれば timeout で終わらせ、待ち行列の先頭を繰り上げる
        if (head != null && head.deadlineUtcMs() <= now) {
            Log.w(PlatformContract.LOG_TAG, "目覚まし " + head.entry.id + " は殺されている間に安全弁（" + head.entry.maxRingMinutes
                    + " 分）の時刻を過ぎていました");
            AlarmEvents.recordRingStopped(context, head.entry, PlatformContract.RING_STOP_REASON_TIMEOUT);
            head = null;
        }
        if (head == null && !waiting.isEmpty()) {
            RingSnapshot.Waiting promoted = waiting.remove(0);
            head = new RingSnapshot.Current(promoted.entry, promoted.firedAtUtcMs, now, PROMOTED_RESTORE_COUNT);
        } else if (head != null) {
            head = new RingSnapshot.Current(head.entry, head.firedAtUtcMs, head.startedAtUtcMs,
                    head.restoreCount + RESTORE_COUNT_STEP);
        }
        if (head == null) {
            AlarmStreamVolume.restoreLeftover(context, saved.volume);
            return;
        }

        // 5. 戻して控えを書き直し、鳴動の前景サービスを起こす
        RingSession session = RingRegistry.restore(head, waiting);
        if (session == null) {
            // 取り出した後に新しい目覚ましが鳴り始めた（そちらを優先する。戻せなかった分は終わったと記録）
            List<AlarmEntry> dropped = new ArrayList<>();
            dropped.add(head.entry);
            for (RingSnapshot.Waiting row : waiting) {
                dropped.add(row.entry);
            }
            Log.w(PlatformContract.LOG_TAG, "前のプロセスの鳴動を戻す前に別の鳴動が始まりました");
            recordEnded(context, dropped, PlatformContract.RING_STOP_REASON_ERROR);
            return;
        }
        RingControl.persist(context);
        if (!RingControl.startService(context, session.entry.id)) {
            List<AlarmEntry> aborted = RingRegistry.abortAll();
            RingControl.persist(context);
            recordEnded(context, aborted, PlatformContract.RING_STOP_REASON_ERROR);
            AlarmStreamVolume.restoreLeftover(context, saved.volume);
            return;
        }
        Log.w(PlatformContract.LOG_TAG, "目覚まし " + session.entry.id + " の鳴動を戻しました（鳴らし直し " + session.restoreCount
                + " 回目・鳴り始めから " + (now - session.startedAtUtcMs) + " ms・待ち " + waiting.size() + " 件）");
    }

    /**
     * 終わった鳴動を記録する（alarm.ring_stopped）。
     *
     * @param context :seed_platform の Context
     * @param entries 終わった予約
     * @param reason  理由（PlatformContract.RING_STOP_REASON_*）
     */
    private static void recordEnded(Context context, List<AlarmEntry> entries, String reason) {
        for (AlarmEntry entry : entries) {
            AlarmEvents.recordRingStopped(context, entry, reason);
        }
    }
}
