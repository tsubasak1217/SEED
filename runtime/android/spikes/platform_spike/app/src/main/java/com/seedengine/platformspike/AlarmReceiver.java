package com.seedengine.platformspike;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/**
 * setAlarmClock の発火先（:seed_platform・exported=false）。鳴動の前景サービスを起こすだけ。
 *
 * <p>正確なアラームの配信は「背面からの前景サービスの起動の制限」の例外になる（公式の例外の一覧）。
 * それが API 36 の mediaPlayback で本当に通るかを、fgs_start_requested / fgs_started / fgs_start_denied の目印で確かめる。</p>
 */
public final class AlarmReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        if (!SpikeContract.ACTION_ALARM_FIRE.equals(intent.getAction())) {
            return;
        }
        long now = System.currentTimeMillis();
        String id = intent.getStringExtra(SpikeContract.EXTRA_ID);
        long scheduled = intent.getLongExtra(SpikeContract.EXTRA_TRIGGER_AT, -1);
        String fgsType = intent.getStringExtra(SpikeContract.EXTRA_FGS_TYPE);
        int maxRing = intent.getIntExtra(SpikeContract.EXTRA_MAX_RING_SECONDS,
                SpikeContract.DEFAULT_MAX_RING_SECONDS);
        SpikeLog.mark("alarm_received", "id=" + id + " sched=" + scheduled
                + " late_ms=" + (now - scheduled) + " " + DeviceState.describe(context));
        // 一回限りの予約として控えから外す（鳴らした記録は RingService の目印）。
        AlarmStore.remove(context, id);
        RingService.startFromBackground(context, "alarm", id, scheduled, fgsType, maxRing);
    }
}
