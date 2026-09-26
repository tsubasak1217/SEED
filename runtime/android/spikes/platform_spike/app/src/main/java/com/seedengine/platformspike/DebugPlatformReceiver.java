package com.seedengine.platformspike;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/**
 * スパイク専用（exported=true・:seed_platform）: 予約を経ずに、adb の放送から直接 RingService を前景で起こす。
 *
 * <p>正確なアラームの例外が無いときに、背面から前景サービスを起こせない（ForegroundServiceStartNotAllowedException）
 * ことを確かめ、AlarmReceiver からの起動が通るのは「正確なアラームの例外」のおかげだと切り分けるため。</p>
 */
public final class DebugPlatformReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        if (!SpikeContract.ACTION_RING_NOW.equals(intent.getAction())) {
            return;
        }
        String fgsType = intent.getStringExtra(SpikeContract.EXTRA_FGS_TYPE);
        SpikeLog.mark("debug_ring_now", "fgs_type=" + fgsType + " " + DeviceState.describe(context));
        RingService.startFromBackground(context, "debug_broadcast", "ring_now",
                System.currentTimeMillis(), fgsType,
                intent.getIntExtra(SpikeContract.EXTRA_MAX_RING_SECONDS, SpikeContract.DEFAULT_MAX_RING_SECONDS));
    }
}
