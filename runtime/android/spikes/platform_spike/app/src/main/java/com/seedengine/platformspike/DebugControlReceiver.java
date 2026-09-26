package com.seedengine.platformspike;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.os.Bundle;

/**
 * スパイク専用（exported=true・メインプロセス）: エンジンの代役。adb の am broadcast で受けた命令を、
 * 本番と同じ経路（メインプロセス → ContentResolver.call → :seed_platform の PlatformProvider）で実行する。
 *
 * <pre>
 * am broadcast -n com.seedengine.platformspike/.DebugControlReceiver -a com.seedengine.platformspike.SCHEDULE --ei seconds 90 [--es id x] [--es fgs_type systemExempted] [--ei max_ring_s 60]
 * ... -a ...CANCEL [--es id x] / ...CANCEL_ALL / ...STOP / ...STATUS / ...REARM / ...IPC_BENCH
 * </pre>
 */
public final class DebugControlReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent.getAction();
        SpikeLog.mark("debug_control", "action=" + action);
        PendingResult pending = goAsync();
        Context app = context.getApplicationContext();
        // Binder の往復と計測は UI スレッドを塞がないよう別スレッドで（bindService のコールバックを UI スレッドで受けるため）。
        new Thread(() -> {
            try {
                handle(app, intent);
            } finally {
                pending.finish();
            }
        }, "spike-debug-control").start();
    }

    private static void handle(Context app, Intent intent) {
        String action = intent.getAction();
        if (action == null) {
            return;
        }
        switch (action) {
            case SpikeContract.ACTION_SCHEDULE: {
                Bundle extras = new Bundle();
                extras.putString(SpikeContract.EXTRA_ID, orDefault(intent.getStringExtra(SpikeContract.EXTRA_ID),
                        SpikeContract.DEFAULT_ALARM_ID));
                extras.putInt(SpikeContract.EXTRA_SECONDS,
                        intent.getIntExtra(SpikeContract.EXTRA_SECONDS, SpikeContract.DEFAULT_SECONDS));
                extras.putString(SpikeContract.EXTRA_FGS_TYPE, orDefault(intent.getStringExtra(SpikeContract.EXTRA_FGS_TYPE),
                        SpikeContract.FGS_TYPE_MEDIA_PLAYBACK));
                extras.putInt(SpikeContract.EXTRA_MAX_RING_SECONDS, intent.getIntExtra(
                        SpikeContract.EXTRA_MAX_RING_SECONDS, SpikeContract.DEFAULT_MAX_RING_SECONDS));
                Bundle result = PlatformClient.call(app, SpikeContract.M_ALARM_SCHEDULE, extras);
                SpikeLog.mark("schedule_result", "ok=" + result.getBoolean(SpikeContract.R_OK)
                        + " trigger_at=" + result.getLong(SpikeContract.EXTRA_TRIGGER_AT, -1)
                        + " error=" + result.getString(SpikeContract.R_ERROR));
                break;
            }
            case SpikeContract.ACTION_CANCEL: {
                Bundle extras = new Bundle();
                extras.putString(SpikeContract.EXTRA_ID, orDefault(intent.getStringExtra(SpikeContract.EXTRA_ID),
                        SpikeContract.DEFAULT_ALARM_ID));
                logResult("cancel_result", PlatformClient.call(app, SpikeContract.M_ALARM_CANCEL, extras));
                break;
            }
            case SpikeContract.ACTION_CANCEL_ALL:
                logResult("cancel_all_result", PlatformClient.call(app, SpikeContract.M_ALARM_CANCEL_ALL, null));
                break;
            case SpikeContract.ACTION_STOP:
                logResult("stop_result", PlatformClient.call(app, SpikeContract.M_RING_STOP, "adb", null));
                break;
            case SpikeContract.ACTION_REARM:
                logResult("rearm_result", PlatformClient.call(app, SpikeContract.M_ALARM_REARM_ALL, "debug", null));
                break;
            case SpikeContract.ACTION_STATUS:
                logResult("status_perm", PlatformClient.call(app, SpikeContract.M_PERM_STATUS, null));
                logResult("status_alarms", PlatformClient.call(app, SpikeContract.M_ALARM_LIST, null));
                logResult("status_ring", PlatformClient.call(app, SpikeContract.M_RING_STATUS, null));
                SpikeLog.mark("status_main_process", DeviceState.permissions(app));
                break;
            case SpikeContract.ACTION_IPC_BENCH:
                IpcBench.run(app);
                break;
            default:
                SpikeLog.mark("debug_control_unknown", "action=" + action);
                break;
        }
    }

    private static void logResult(String name, Bundle result) {
        SpikeLog.mark(name, "ok=" + result.getBoolean(SpikeContract.R_OK)
                + " platform_pid=" + result.getInt(SpikeContract.R_PID, -1)
                + " json=" + result.getString(SpikeContract.R_JSON)
                + " error=" + result.getString(SpikeContract.R_ERROR));
    }

    private static String orDefault(String value, String fallback) {
        return value != null ? value : fallback;
    }
}
