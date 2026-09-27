// ============================================================
//  RingService.java — 目覚ましの鳴動の前景サービス（:seed_platform・exported=false・directBootAware・mediaPlayback。W1-4a）
//
//  【起こされ方】AlarmReceiver が発火を受けて**真っ先に** startForegroundService する（setAlarmClock の配信に付く
//  前景サービス起動の一時許可は 10 秒。W1-0 の F-7）。続けて受信機が控えから予約を取り出して RingRegistry へ渡すので、
//  このサービスの onStartCommand（受信機の onReceive が返った後に同じ UI スレッドで走る）では、鳴らすものが registry にある。
//
//  【すること】RingRegistry（鳴動の状態の正本）に合わせて、音（RingAudio）・振動（RingVibration）・通知（RingNotification）・
//  部分ウェイクロック（RingWakeLock）を出し入れする（syncWithRegistry）。状態を変えるのは:
//    ・アプリの alarm.stop_ringing（AlarmModule → RingControl。Binder のスレッド）→ ここへ同期を投げる
//    ・安全弁（max_ring_minutes。このサービスの UI スレッドのタイマー）→ alarm.ring_stopped(timeout)
//    ・配信（AlarmReceiver → RingControl.handOver）→ 鳴っていなければ鳴らし、鳴っていれば待ち行列（alarm.queued）
//  止まった鳴動の後に待ち行列があれば続けて鳴らし、何も無くなったら前景をやめて自分を止める。
//
//  【止めないもの】音声フォーカスの喪失（そもそも要求しない。RingAudio）・最近のタスクから消されたこと（onTaskRemoved は記録だけ）・
//  通知のスワイプ（前景サービスは続く）・エンジン（メインプロセス）の死。E-01・W1-0 の実機で、鳴動画面のタスクを消しても
//  別プロセスの音は続いた。
//
//  【種類】foregroundServiceType=mediaPlayback（E-03）。startForeground にも同じ種類を渡す（targetSdk 34+ の必須）。
//  START_NOT_STICKY: プロセスごと殺されてもシステムには作り直させない（W1-4b の T5 で、殺されると黙って止まった）。
//  代わりに W1-7 から、鳴動中は見張りの予約（RingWatchdog。RingWatchdog.DELAY_MS 先）を張り、RingWatchdog.REARM_INTERVAL_MS
//  ごとに先へ送り続ける。殺されると見張りが発火し、RingRecovery が ringing.json（RingStateStore）から鳴動を戻す
//  （正確な予約の配信に付く前景サービスの一時許可で起こし直す）。止めたら見張りを取り消す。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import android.app.Notification;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.service.alarm.AlarmEntry;
import com.seedengine.runtime.platform.service.alarm.AlarmEvents;

import java.util.List;

/**
 * 鳴動の前景サービス（状態は RingRegistry。ここは音・振動・通知を合わせるだけ）。
 */
public final class RingService extends Service {

    /** 起こす Intent の action（受信機が startForegroundService に渡す）。 */
    static final String ACTION_RING = "com.seedengine.runtime.platform.action.RING";

    /** 起こす Intent の extra: 配信された予約の ID（ログ用。鳴らす中身は RingRegistry から読む）。 */
    static final String EXTRA_ALARM_ID = "com.seedengine.runtime.platform.extra.RING_ALARM_ID";

    /** 何も鳴らしていないときの鳴動の通し番号。 */
    private static final long NO_SESSION = 0L;

    /** 動いているサービス（UI スレッドで書き、どのスレッドからも読む。無ければ null）。 */
    private static volatile RingService instance;

    /** UI スレッドの Handler（安全弁のタイマーと、他のスレッドからの同期）。 */
    private final Handler mainHandler = new Handler(Looper.getMainLooper());

    /** 音（専用のスレッド）。 */
    private RingAudio audio;

    /** 振動。 */
    private RingVibration vibration;

    /** 今鳴らしている鳴動の通し番号（NO_SESSION なら鳴らしていない）。 */
    private long activeSerial = NO_SESSION;

    /** 今使っている通知の ID の番号（RingNotification.notificationId）。 */
    private int notificationSlot;

    /** 最後に受けた onStartCommand の startId（stopSelfResult で「後から来た起動」を消さないため）。 */
    private int lastStartId;

    /** 今の鳴動の安全弁（無ければ null）。 */
    private Runnable safetyValve;

    /** 鳴動中に見張りの予約を先へ送り続ける処理（W1-7。UI スレッドのタイマー）。 */
    private final Runnable watchdogRearm = this::rearmWatchdog;

    /**
     * 破棄されたか（UI スレッドだけが触る）。止まる途中のこのサービスへ投げられた同期（requestSync）が、onDestroy の後に
     * 走っても何もしないため（次の配信は新しく作られたサービスの onStartCommand が鳴らす）。
     */
    private boolean destroyed;

    /**
     * 受信機から起こす Intent を作る。
     *
     * @param context :seed_platform の Context
     * @param alarmId 配信された予約の ID（ログ用）
     * @return Intent
     */
    static Intent startIntent(Context context, String alarmId) {
        return new Intent(context, RingService.class).setAction(ACTION_RING).putExtra(EXTRA_ALARM_ID, alarmId);
    }

    /**
     * RingRegistry が変わったことを知らせる（どのスレッドからでもよい。UI スレッドで合わせ直す）。
     * サービスが動いていなければ何もしない（次に起こされたときの onStartCommand で合わせる）。
     */
    static void requestSync() {
        RingService service = instance;
        if (service != null) {
            service.mainHandler.post(service::syncWithRegistry);
        }
    }

    @Override
    public void onCreate() {
        super.onCreate();
        instance = this;
        audio = new RingAudio(this);
        vibration = new RingVibration(this);
        RingNotification.ensureChannel(this);
        RingNotification.logVisibility(this);
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        lastStartId = startId;
        String alarmId = intent != null ? intent.getStringExtra(EXTRA_ALARM_ID) : null;
        RingSession current = RingRegistry.current();
        Log.i(PlatformContract.LOG_TAG, "RingService: 起こされました（配信 " + alarmId + "・鳴動 " + current
                + "・待ち " + RingRegistry.queuedCount() + " 件）");
        // startForegroundService で起こされたら、どの道でも必ず startForeground する（しないとシステムがアプリを落とす）
        boolean foreground = current != null && current.serial != activeSerial
                ? beginSession(current)      // 新しい鳴動: 音を真っ先に始め、同じ流れで前景に入る
                : enterForeground(current);  // 鳴らしている鳴動のまま（待ち行列に入っただけ）か、鳴らすものが無い（古い配信）
        if (!foreground) {
            abortAll();
            stopSelfResult(startId);
            return START_NOT_STICKY;
        }
        syncWithRegistry();
        return START_NOT_STICKY;
    }

    /**
     * 今の鳴動の通知で前景に入り直す（鳴らすものが無ければ仮の通知。直後の syncWithRegistry が片付ける）。
     *
     * @param current 今の鳴動（null 可）
     * @return 入れたら true
     */
    private boolean enterForeground(RingSession current) {
        Notification notification = current != null ? RingNotification.build(this, current) : RingNotification.placeholder(this);
        return startForegroundWith(notification);
    }

    /**
     * 今の番号の通知 ID で前景に入る（入っていれば通知を差し替える）。
     *
     * @param notification 通知
     * @return 入れたら true
     */
    private boolean startForegroundWith(Notification notification) {
        try {
            startForeground(RingNotification.notificationId(notificationSlot), notification,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK);
            return true;
        } catch (RuntimeException e) {
            // ForegroundServiceStartNotAllowedException（一時許可の切れ）・SecurityException（種類の権限が無い）など
            Log.e(PlatformContract.LOG_TAG, "RingService: 前景にできませんでした（鳴らせません）: " + PlatformJson.describe(e));
            return false;
        }
    }

    /**
     * RingRegistry に合わせる（UI スレッド）。鳴らすものが無ければ片付けて止まり、別の鳴動に入れ替わっていれば鳴らし直す。
     */
    private void syncWithRegistry() {
        if (destroyed) {
            return;
        }
        RingSession current = RingRegistry.current();
        if (current == null) {
            finishRinging();
            return;
        }
        if (current.serial != activeSerial) {
            beginSession(current);
        }
    }

    /**
     * 鳴動を始める・入れ替える（UI スレッド）。
     *
     * @param session 鳴らす鳴動
     * @return 前景に入れた（通知を出せた）なら true
     */
    private boolean beginSession(RingSession session) {
        AlarmEntry entry = session.entry;
        // 音を真っ先に（MediaPlayer の準備は専用のスレッドで、下の通知の組み立て・startForeground と並べて進む）
        audio.start(session);
        if (entry.vibrate) {
            vibration.start();
        } else {
            vibration.stop();
        }
        // 入れ替えなら別の ID で出し直す（新しい通知として知らせ直し、フルスクリーン通知ももう一度起きる）
        boolean replacing = activeSerial != NO_SESSION;
        int previousId = RingNotification.notificationId(notificationSlot);
        if (replacing) {
            notificationSlot = RingNotification.nextSlot(notificationSlot);
        }
        boolean foreground = startForegroundWith(RingNotification.build(this, session));
        if (replacing) {
            NotificationManager manager = getSystemService(NotificationManager.class);
            if (manager != null) {
                manager.cancel(previousId);
            }
        }
        long now = System.currentTimeMillis();
        long untilDeadline = Math.max(0L, session.deadlineUtcMs() - now);
        RingWakeLock.acquire(this, untilDeadline + RingWakeLock.DEADLINE_MARGIN_MS);
        scheduleSafetyValve(session, untilDeadline);
        activeSerial = session.serial;
        // 殺されたら鳴動を戻す見張り（W1-7）。張って、鳴動の間ずっと先へ送り続ける
        rearmWatchdog();
        Log.i(PlatformContract.LOG_TAG, "RingService: 目覚まし " + entry.id + " を鳴らしています（予定から "
                + (session.startedAtUtcMs - entry.triggerAtUtcMs) + " ms・安全弁 " + entry.maxRingMinutes + " 分・振動 "
                + entry.vibrate + (replacing ? "・待ち行列から繰り上げ" : "")
                + (session.restoreCount > 0 ? "・見張りで鳴らし直し " + session.restoreCount + " 回目" : "") + "）");
        return foreground;
    }

    /**
     * 見張りの予約を先へ送り、次の張り直しを仕掛ける（UI スレッド。鳴らしていなければ何もしない）。
     */
    private void rearmWatchdog() {
        mainHandler.removeCallbacks(watchdogRearm);
        if (activeSerial == NO_SESSION || destroyed) {
            return;
        }
        RingWatchdog.arm(this);
        mainHandler.postDelayed(watchdogRearm, RingWatchdog.REARM_INTERVAL_MS);
    }

    /** 見張りの予約をやめる（UI スレッド。止めた・続けられないとき）。 */
    private void stopWatchdog() {
        mainHandler.removeCallbacks(watchdogRearm);
        RingWatchdog.disarm(this);
    }

    /**
     * 安全弁を仕掛け直す（UI スレッド）。
     *
     * @param session       鳴動
     * @param untilDeadline 時間切れまでのミリ秒
     */
    private void scheduleSafetyValve(RingSession session, long untilDeadline) {
        cancelSafetyValve();
        long serial = session.serial;
        safetyValve = () -> RingControl.onSafetyValve(this, serial);
        mainHandler.postDelayed(safetyValve, untilDeadline);
    }

    /** 安全弁を外す（UI スレッド）。 */
    private void cancelSafetyValve() {
        if (safetyValve != null) {
            mainHandler.removeCallbacks(safetyValve);
            safetyValve = null;
        }
    }

    /**
     * 鳴らすものが無くなった: 音・振動・通知・ウェイクロックを片付けて止まる（UI スレッド）。
     */
    private void finishRinging() {
        cancelSafetyValve();
        stopWatchdog();
        audio.stop();
        vibration.stop();
        stopForeground(STOP_FOREGROUND_REMOVE);
        RingWakeLock.release();
        if (activeSerial != NO_SESSION) {
            Log.i(PlatformContract.LOG_TAG, "RingService: 鳴動を片付けました");
        }
        activeSerial = NO_SESSION;
        // 後から届いた起動（onStartCommand がまだ）があれば止まらない（その onStartCommand が合わせ直す）
        stopSelfResult(lastStartId);
    }

    /**
     * 鳴らし続けられない: 全部やめて alarm.ring_stopped(error) を記録する（UI スレッド）。
     */
    private void abortAll() {
        List<AlarmEntry> aborted = RingRegistry.abortAll();
        // 変えた → 控える（空にする）→ 記録する（RingControl と同じ順）
        RingControl.persist(this);
        for (AlarmEntry entry : aborted) {
            AlarmEvents.recordRingStopped(this, entry, PlatformContract.RING_STOP_REASON_ERROR);
        }
        cancelSafetyValve();
        stopWatchdog();
        audio.stop();
        vibration.stop();
        RingWakeLock.release();
        activeSerial = NO_SESSION;
    }

    @Override
    public void onTaskRemoved(Intent rootIntent) {
        // 最近のタスクから消された（鳴動画面のタスクなど）。止めない（AC-2）。記録だけ
        Log.i(PlatformContract.LOG_TAG, "RingService: タスクが消されましたが鳴らし続けます（"
                + (rootIntent != null ? rootIntent.getComponent() : null) + "・鳴動 " + RingRegistry.current() + "）");
        super.onTaskRemoved(rootIntent);
    }

    @Override
    public void onDestroy() {
        // 自分で片付けて止まったとき（finishRinging）は activeSerial が NO_SESSION。鳴らしている途中で壊されたときだけ、
        // 鳴動を続けられないので全部やめて記録する（止まる途中に次の配信が registry へ入った場合は、次に起こされた
        // サービスがそれを鳴らすので、ここでは触らない）
        if (activeSerial != NO_SESSION) {
            Log.w(PlatformContract.LOG_TAG, "RingService: 鳴らしている途中で破棄されました");
            abortAll();
        }
        destroyed = true;
        cancelSafetyValve();
        mainHandler.removeCallbacks(watchdogRearm);
        audio.release();
        vibration.stop();
        if (instance == this) {
            instance = null;
        }
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        // 結び付けは使わない（状態は同じプロセスの RingRegistry で受け渡す）
        return null;
    }
}
