// ============================================================
//  RingWatchdogReceiver.java — 鳴動の見張りの予約の発火先（:seed_platform・exported=false・directBootAware。W1-7）
//
//  鳴動中に :seed_platform が殺されると、RingService が張り直していた見張りの予約（RingWatchdog）が発火してここへ届く
//  （新しい :seed_platform のプロセスが起きる）。処理は RingRecovery.onWatchdog（ringing.json から鳴動を戻す・戻せなければ
//  「終わった（error）」と記録して元の音量へ戻す）。サービスが生きている間に届いた（張り直しが遅れた）ときは張り直すだけ。
//
//  【マニフェスト】機能 alarm の断片（runtime/android/platform_features.json の application_elements）で宣言する:
//    exported=false（アプリ自身の PendingIntent だけが届く）・android:process=":seed_platform"（鳴動と同じプロセス）・
//    directBootAware=true（再起動の後・最初のロック解除の前の鳴動も見張る。控えは端末保護ストレージ）。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;

/**
 * 見張りの受信機。
 */
public final class RingWatchdogReceiver extends BroadcastReceiver {

    @Override
    public void onReceive(Context context, Intent intent) {
        if (intent == null || !RingWatchdog.ACTION_WATCHDOG.equals(intent.getAction())) {
            Log.w(PlatformContract.LOG_TAG, "RingWatchdogReceiver: 知らない Intent を受けたので無視します: " + intent);
            return;
        }
        // 背面からの前景サービスの起動の一時許可（正確な予約の配信に付く 10 秒）の間に、真っ先に処理する
        RingRecovery.onWatchdog(context);
    }
}
