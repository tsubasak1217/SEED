package com.seedengine.platformspike;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/**
 * 再起動（LOCKED_BOOT_COMPLETED＝ロック解除前 / BOOT_COMPLETED）とアプリの更新（MY_PACKAGE_REPLACED）で、
 * 控えから予約を張り直す（:seed_platform・exported=false・directBootAware）。
 *
 * <p>exported=false でもシステム（system_server）が送るこれらの放送は届く、という前提を MY_PACKAGE_REPLACED
 * （adb install -r で起こせる）で確かめる。再起動の試験はユーザーの許可待ち。</p>
 */
public final class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent.getAction();
        SpikeLog.mark("boot_received", "action=" + action + " " + DeviceState.describe(context));
        String summary = AlarmScheduler.rearmAll(context, "boot:" + action);
        SpikeLog.i("BootReceiver: " + summary);
    }
}
