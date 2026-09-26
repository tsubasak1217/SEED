// ============================================================
//  BootReceiver.java — 予約の控えから setAlarmClock を張り直す（:seed_platform・exported=false・directBootAware。W1-3）
//
//  【受ける放送】（機能 alarm の断片の intent-filter。runtime/android/platform_features.json）
//    LOCKED_BOOT_COMPLETED / BOOT_COMPLETED … 再起動（電源断で予約はすべて消える）。Android 15+ は強制停止の後にアプリが
//        停止状態から出たときにも届く（W1-0 の実機で exported=false の受信機に両方が届き、MainActivity より前に張り直せた）
//    MY_PACKAGE_REPLACED                   … アプリの更新
//    TIME_SET / TIMEZONE_CHANGED           … 端末の時刻・タイムゾーンの変更（予約は UTC の絶対時刻のまま。アプリは
//        alarms.rescheduled(time_changed) を受けて次の時刻を計算し直す）
//    SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED … 正確なアラームの特別なアクセスが許可された（Android 12 系。
//        取り消されたときはアプリが止められ予約は全部消えるので、許可が戻ったここで張り直す。公式の「Schedule alarms」）
//  TIME_SET・TIMEZONE_CHANGED・LOCKED_BOOT_COMPLETED・BOOT_COMPLETED はマニフェストの受信機にも届く暗黙の放送の例外
//  （公式の「Implicit broadcast exceptions」）。MY_PACKAGE_REPLACED と権限の放送はこのアプリ宛て。
//
//  【すること】AlarmBook.rearm: まだ先の予約は張り直し、過ぎた予約は**鳴らさずに** alarm.missed を記録して控えから外す。
//  受信機から直接鳴らさない（Android 15+ は BOOT_COMPLETED から mediaPlayback の前景サービスを起こせず、Android 17 は
//  BOOT_COMPLETED から起こした前景サービスの音を抑える。E-10）。控えに予約があれば alarms.rescheduled を記録する
//  （控えが空なら記録しない＝目覚ましを使わない間は何も残さない）。
//  adb の am broadcast では届かない（exported=false のうえ、BOOT_COMPLETED などはシステムだけが送れる保護された放送）。
//  確かめ方は docs/android.md §25.11。
// ============================================================

package com.seedengine.runtime.platform.service.alarm;

import android.app.AlarmManager;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;

/**
 * 張り直しの受信機。
 */
public final class BootReceiver extends BroadcastReceiver {

    /** 放送ごとの理由（alarms.rescheduled の理由と、過ぎた予約の alarm.missed の理由）。 */
    private static final class Cause {
        /** alarms.rescheduled の理由。 */
        final String rescheduleReason;
        /** alarm.missed の理由。 */
        final String missedReason;

        Cause(String rescheduleReason, String missedReason) {
            this.rescheduleReason = rescheduleReason;
            this.missedReason = missedReason;
        }
    }

    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent != null ? intent.getAction() : null;
        Cause cause = causeOf(action);
        if (cause == null) {
            Log.w(PlatformContract.LOG_TAG, "BootReceiver: 知らない放送なので無視します: " + action);
            return;
        }
        AlarmBook.RearmResult result = AlarmBook.rearm(context, cause.missedReason, System.currentTimeMillis());
        if (result.stored > 0) {
            AlarmEvents.recordRescheduled(context, cause.rescheduleReason, result.armed, result.missed, result.failed);
        }
        Log.i(PlatformContract.LOG_TAG, "BootReceiver: " + action + " で張り直しました（" + result + "）");
    }

    /**
     * 放送の action から理由を決める（表に無ければ null）。
     *
     * @param action 放送の action
     * @return 理由
     */
    private static Cause causeOf(String action) {
        if (action == null) {
            return null;
        }
        switch (action) {
            case Intent.ACTION_LOCKED_BOOT_COMPLETED:
            case Intent.ACTION_BOOT_COMPLETED:
                return new Cause(PlatformContract.RESCHEDULE_REASON_BOOT, PlatformContract.MISSED_REASON_DEVICE_OFF);
            case Intent.ACTION_MY_PACKAGE_REPLACED:
                return new Cause(PlatformContract.RESCHEDULE_REASON_PACKAGE_REPLACED, PlatformContract.MISSED_REASON_DEVICE_OFF);
            case Intent.ACTION_TIME_CHANGED:
            case Intent.ACTION_TIMEZONE_CHANGED:
                return new Cause(PlatformContract.RESCHEDULE_REASON_TIME_CHANGED, PlatformContract.MISSED_REASON_DEVICE_OFF);
            case AlarmManager.ACTION_SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED:
                return new Cause(PlatformContract.RESCHEDULE_REASON_PERMISSION_CHANGED,
                        PlatformContract.MISSED_REASON_PERMISSION_REVOKED);
            default:
                return null;
        }
    }
}
