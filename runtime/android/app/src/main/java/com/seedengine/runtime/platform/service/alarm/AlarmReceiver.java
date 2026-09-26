// ============================================================
//  AlarmReceiver.java — AlarmManager.setAlarmClock の発火先（:seed_platform・exported=false・directBootAware。W1-3）
//
//  【W1-3 でしていること】発火で控えから該当の予約を読み、
//    1. 発火の記録 platform.alarm.fired { id, scheduled_at_utc_ms, fired_at_utc_ms, payload_json } を書く（端末保護ストレージ）
//    2. 控えから消す（一回限りの予約）
//    3. エンジン（メインプロセス）が居れば呼び鈴を鳴らす（記録と同時。EventRecorder）
//  まで（AlarmBook.fire）。音・前景サービス・フルスクリーン通知・鳴動画面は W1-4。
//
//  【マニフェスト】機能 alarm の断片（runtime/android/platform_features.json の application_elements）で宣言する:
//    exported=false（アプリ自身の PendingIntent だけが届く。他のアプリから鳴らせない・止められない）
//    android:process=":seed_platform"（エンジンが居なくても・落ちても動く Java だけのプロセス。E-01）
//    directBootAware=true（再起動の後、最初のロック解除の前に BootReceiver が張り直した予約も記録できる。控えと記録は
//    端末保護ストレージ。ロック解除の前に鳴らすかは W1-9 で決める）
// ============================================================

package com.seedengine.runtime.platform.service.alarm;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;

/**
 * 目覚ましの発火の受信機。
 */
public final class AlarmReceiver extends BroadcastReceiver {

    @Override
    public void onReceive(Context context, Intent intent) {
        // ────────────────────────────────────────────────────────────────────────────
        // 【W1-4 の差し込み位置】ここで**真っ先に** RingService を前景で起動する（context.startForegroundService）。
        // setAlarmClock の配信に付く背面からの前景サービス起動の一時許可は 10 秒（W1-0 の実機。使用中の重い端末では
        // 許可まで 1.79 s かかった）なので、控えの読み書き・fsync（下の AlarmBook.fire）より前に呼ぶこと。
        // 予約の鳴らし方（音源・音量・安全弁）は Intent の extras か控えから RingService が読む。
        // ────────────────────────────────────────────────────────────────────────────
        long receivedAt = System.currentTimeMillis();
        if (intent == null || !AlarmScheduler.ACTION_ALARM_FIRE.equals(intent.getAction())) {
            Log.w(PlatformContract.LOG_TAG, "知らない Intent を受けたので無視します: " + intent);
            return;
        }
        String id = AlarmScheduler.alarmIdOf(intent);
        long intentTriggerAt = intent.getLongExtra(AlarmScheduler.EXTRA_TRIGGER_AT_UTC_MS, AlarmScheduler.UNKNOWN_TRIGGER_AT);
        AlarmEntry fired = AlarmBook.fire(context, id, intentTriggerAt, receivedAt);
        if (fired != null) {
            Log.i(PlatformContract.LOG_TAG, "AlarmReceiver: " + id + " の発火を記録しました（処理 "
                    + (System.currentTimeMillis() - receivedAt) + " ms）");
        }
    }
}
