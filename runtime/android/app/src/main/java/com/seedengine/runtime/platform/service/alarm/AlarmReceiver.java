// ============================================================
//  AlarmReceiver.java — AlarmManager.setAlarmClock の発火先（:seed_platform・exported=false・directBootAware。W1-3・W1-4a）
//
//  【発火の順序（W1-4a）】
//    1. **真っ先に**鳴動の前景サービス RingService を起こす（RingControl.startService → startForegroundService）。
//       setAlarmClock の配信に付く「背面からの前景サービス起動」の一時許可は 10 秒（W1-0 の実機。使用中の重い端末では許可まで
//       1.79 s かかった。F-7）なので、控えの読み書き・fsync より前に呼ぶ。起こせなければ（ForegroundServiceStartNotAllowedException
//       等）例外を捕まえ、2 で alarm.fired の代わりに alarm.missed(start_failed) を記録する
//    2. 控えから該当の予約を読み、鳴動へ渡し（RingControl.handOver → RingRegistry。鳴動中なら待ち行列）、発火の記録
//       （platform.alarm.fired。起こせなかったときは渡さずに alarm.missed(start_failed)）を書いてから控えから消す
//       （一回限りの予約。AlarmBook.fire。記録と同時にエンジンが居れば呼び鈴を鳴らす）。渡すのを記録より先にするので、
//       fired を受けたアプリの GetRinging は必ずその鳴動を返す
//    3. 待たせたなら alarm.queued を記録し（fired の後に並ぶ）、動いているサービスへ同期を投げる（RingControl.announce）
//  RingService の onStartCommand は、この onReceive が返った後に同じ UI スレッドで走るので、2 の後に鳴らすものを読める。
//  控えに無い・古い配信（取り消し・予約し直しと配信が重なった）のときは、起こされたサービスが仮の通知で前景に入ってすぐ止まる
//  （startForegroundService の後は startForeground を呼ぶ約束があるため）。
//
//  【マニフェスト】機能 alarm の断片（runtime/android/platform_features.json の application_elements）で宣言する:
//    exported=false（アプリ自身の PendingIntent だけが届く。他のアプリから鳴らせない・止められない）
//    android:process=":seed_platform"（エンジンが居なくても・落ちても動く Java だけのプロセス。E-01）
//    directBootAware=true（再起動の後、最初のロック解除の前に BootReceiver が張り直した予約も鳴らせる。控えと記録は
//    端末保護ストレージ。ロック解除の前の鳴動画面は W1-9）
// ============================================================

package com.seedengine.runtime.platform.service.alarm;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.service.alarm.ring.RingControl;

/**
 * 目覚ましの発火の受信機。
 */
public final class AlarmReceiver extends BroadcastReceiver {

    @Override
    public void onReceive(Context context, Intent intent) {
        long receivedAt = System.currentTimeMillis();
        if (intent == null || !AlarmScheduler.ACTION_ALARM_FIRE.equals(intent.getAction())) {
            Log.w(PlatformContract.LOG_TAG, "知らない Intent を受けたので無視します: " + intent);
            return;
        }
        String id = AlarmScheduler.alarmIdOf(intent);
        long intentTriggerAt = intent.getLongExtra(AlarmScheduler.EXTRA_TRIGGER_AT_UTC_MS, AlarmScheduler.UNKNOWN_TRIGGER_AT);

        // 1. 真っ先に前景サービス（一時許可の 10 秒の間に。下の控えの読み書き・fsync より前）
        boolean serviceStarted = id != null && RingControl.startService(context, id);
        long serviceRequestedAt = System.currentTimeMillis();

        // 2. 控え: 鳴動へ渡し、記録してから消す（起こせなかったなら渡さずに alarm.missed(start_failed)）
        final RingControl.HandOver[] handOver = new RingControl.HandOver[1];
        AlarmEntry fired = AlarmBook.fire(context, id, intentTriggerAt, receivedAt,
                serviceStarted ? entry -> handOver[0] = RingControl.handOver(entry, receivedAt) : null);

        // 3. 待たせたなら alarm.queued（fired の後）。RingService の onStartCommand はこの onReceive が返った後に走る
        if (fired != null && handOver[0] != null) {
            RingControl.announce(context, fired, handOver[0]);
        }
        if (fired != null) {
            Log.i(PlatformContract.LOG_TAG, "AlarmReceiver: " + id + " の発火を処理しました（予定から " + (receivedAt - fired.triggerAtUtcMs)
                    + " ms・前景サービスの要求まで " + (serviceRequestedAt - receivedAt) + " ms・全体 "
                    + (System.currentTimeMillis() - receivedAt) + " ms・前景サービス " + (serviceStarted ? "起こした" : "起こせなかった") + "）");
        }
    }
}
