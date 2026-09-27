// ============================================================
//  RingWatchdog.java — 鳴動中の「見張りの予約」（:seed_platform が殺されたら鳴動を戻すための AlarmManager の予約。W1-7）
//
//  【仕組み】鳴動中、RingService は DELAY_MS 先に正確な予約（setExactAndAllowWhileIdle・ELAPSED_REALTIME_WAKEUP）を張り、
//  REARM_INTERVAL_MS ごとに張り直して先へ送り続ける（同じ PendingIntent なので置き換わる。配信されなければ回数の制限にも数えない）。
//  サービスが生きている間は発火しない。プロセスごと殺されると張り直しが止まり、最長 DELAY_MS 後に発火して
//  RingWatchdogReceiver → RingRecovery が ringing.json から鳴動を戻す。止めたら（finishRinging）取り消す。
//
//  【なぜ setExactAndAllowWhileIdle か】
//    ・正確な予約の権限（USE_EXACT_ALARM / SCHEDULE_EXACT_ALARM）を持つアプリの正確な予約の配信には、背面からの前景サービスの
//      起動の一時許可が付く（dumpsys alarm の idle-options の temporaryAppAllowlistType=0〈前景サービス可〉・Duration 10000 で
//      実機 W1-7 に確かめる）。発火で RingService を起こし直せる
//    ・setAlarmClock と違い、ステータスバーの目覚ましの印・「次のアラーム」（ロック画面・時計のアプリ）に出ない。
//      Doze の中でも配信される（allow-while-idle）
//    ・経過時間（ELAPSED_REALTIME）で張る: 端末の時刻が変わっても（TIME_SET）見張りの間隔は変わらない
//  AlarmManager は 5 秒より近い予約を 5 秒先へ延ばす（最小の先の時間）ので、DELAY_MS はそれより十分長くする。
//  【PendingIntent】要求コード 0・action ACTION_WATCHDOG・部品 RingWatchdogReceiver の 1 つだけ（予約ごとではない）。
//  発火の PendingIntent（AlarmScheduler。部品 AlarmReceiver・data の URI つき）とは Intent が違うので別物。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.os.SystemClock;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

/**
 * 見張りの予約（static のみ）。
 */
final class RingWatchdog {

    private RingWatchdog() {
    }

    /** 見張りの Intent の action（RingWatchdogReceiver がこれだけを受ける）。 */
    static final String ACTION_WATCHDOG = "com.seedengine.runtime.platform.action.RING_WATCHDOG";

    /**
     * 見張りの予約をどれだけ先に張るか（ミリ秒）。プロセスが殺されてから鳴動が戻るまでの最長の無音の時間でもある。
     * AlarmManager の最小の先の時間（5 秒）より十分長く、無音が長すぎない値。
     */
    static final long DELAY_MS = 20_000L;

    /** 鳴動中に見張りを張り直す間隔（ミリ秒。DELAY_MS より十分短く＝生きている間は発火しない）。 */
    static final long REARM_INTERVAL_MS = 5_000L;

    /** 見張りの PendingIntent の要求コード（1 つだけ）。 */
    private static final int REQUEST_CODE_WATCHDOG = 0;

    /**
     * 見張りを DELAY_MS 先に張る（張ってあれば先へ送る）。張れなければ警告だけ（鳴動は続ける。殺されたら戻せないだけ）。
     *
     * @param context :seed_platform の Context
     */
    static void arm(Context context) {
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        if (alarms == null) {
            Log.w(PlatformContract.LOG_TAG, "AlarmManager を取れないので、鳴動の見張りを張れません");
            return;
        }
        try {
            alarms.setExactAndAllowWhileIdle(AlarmManager.ELAPSED_REALTIME_WAKEUP, SystemClock.elapsedRealtime() + DELAY_MS,
                    pendingIntent(context, PendingIntent.FLAG_UPDATE_CURRENT));
        } catch (RuntimeException e) {
            // SecurityException（正確なアラームの権限が取り消された）など
            Log.w(PlatformContract.LOG_TAG, "鳴動の見張りを張れませんでした（殺されたときに戻せません）: " + PlatformJson.describe(e));
        }
    }

    /**
     * 見張りを取り消す（張っていなければ何もしない）。
     *
     * @param context :seed_platform の Context
     */
    static void disarm(Context context) {
        PendingIntent existing = pendingIntent(context, PendingIntent.FLAG_NO_CREATE);
        if (existing == null) {
            return;
        }
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        if (alarms != null) {
            alarms.cancel(existing);
        }
        existing.cancel();
    }

    /**
     * 見張りの PendingIntent がシステムに残っているか（張ってある・配信の途中の目安。再起動・強制停止で消える）。
     *
     * @param context :seed_platform の Context
     * @return 残っていれば true
     */
    static boolean isArmed(Context context) {
        return pendingIntent(context, PendingIntent.FLAG_NO_CREATE) != null;
    }

    /** 見張りの PendingIntent（flags に FLAG_NO_CREATE を渡すと、無ければ null）。 */
    private static PendingIntent pendingIntent(Context context, int flags) {
        Intent intent = new Intent(context, RingWatchdogReceiver.class).setAction(ACTION_WATCHDOG);
        return PendingIntent.getBroadcast(context, REQUEST_CODE_WATCHDOG, intent, flags | PendingIntent.FLAG_IMMUTABLE);
    }
}
